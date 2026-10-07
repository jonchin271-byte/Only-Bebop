// onlyup-extract: reads the player's own Only Up! install (Unreal Engine 5) and writes the climb into a private
// cache on their PC: the level's static meshes as .glb, where each one sits, the spawn point and the music.
// Nothing read here is ever uploaded or shipped.
//
// usage: onlyup-extract --game <Only Up! folder> --plan <plan.json> --out <cache dir> [--ue GAME_UE5_1] [--aes 0x...]
// exit codes: 0 ok, 2 usage, 3 no paks, 5 archives encrypted (no key), 6 needs Oodle, 7 no level found
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

static class Program
{
    static readonly List<string> Errors = new();
    static readonly Dictionary<string, byte[]?> TextureCache = new();
    const float Cm = 0.01f; // Only Up! is in centimetres (games.onlyup.units_per_meter = 100)

    static int Main(string[] args)
    {
        string? game = Arg(args, "--game"), planPath = Arg(args, "--plan"), outDir = Arg(args, "--out");
        if (game is null || planPath is null || outDir is null)
        {
            Console.Error.WriteLine("usage: onlyup-extract --game <Only Up! folder> --plan <plan.json> --out <cache dir> [--ue GAME_UE5_1] [--aes 0x...]");
            return 2;
        }
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!["onlyup"]!;
        var paks = Directory.Exists(game)
            ? Directory.EnumerateDirectories(game, "Paks", SearchOption.AllDirectories).FirstOrDefault(d => d.Replace('\\', '/').EndsWith("Content/Paks"))
            : null;
        if (paks is null) { Console.Error.WriteLine($"MISSING Content/Paks under {game}"); return 3; }
        Directory.CreateDirectory(outDir);
        Progress("Opening Only Up!'s files", 0);

        var ueVersions = Arg(args, "--ue") is { } forced
            ? new[] { Enum.Parse<EGame>(forced) }
            : new[] { EGame.GAME_UE5_1, EGame.GAME_UE5_2, EGame.GAME_UE5_0, EGame.GAME_UE5_3 };

        // zlib-ng (zlib license, bundled next to this exe) for zlib-compressed archives
        var zlib = Path.Combine(AppContext.BaseDirectory, ZlibHelper.DLL_NAME);
        if (File.Exists(zlib)) ZlibHelper.Initialize(zlib);

        foreach (var ue in ueVersions)
        {
            using var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, true, new VersionContainer(ue));
            provider.Initialize();
            if (Arg(args, "--aes") is { } key) provider.SubmitKey(new FGuid(), new FAesKey(key));
            provider.Mount();
            if (provider.RequiredKeys.Count > 0 && provider.Files.Count == 0)
            {
                Console.WriteLine($"ENCRYPTED {provider.RequiredKeys.Count} archive key(s) required");
                return 5;
            }
            provider.PostMount();
            Console.WriteLine($"LOG mounted {provider.MountedVfs.Count} archives, {provider.Files.Count} files as {ue}");

            if (provider.Files.Values.Any(f => f.CompressionMethod == CompressionMethod.Oodle) && OodleHelper.Instance is null)
            {
                var dll = Directory.EnumerateFiles(game, "oo2core*win64.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (dll is null) { Console.WriteLine("NEEDS_OODLE archives use Oodle compression and no oo2core dll was found in the game folder"); return 6; }
                OodleHelper.Initialize(dll);
            }

            try
            {
                var result = ExtractLevel(provider, outDir, ue);
                if (result is null) continue; // try the next engine version
                ExtractMusic(provider, outDir, plan, result);
                result["source"] = new JsonObject { ["paks"] = paks, ["stamp"] = Stamp(paks) };
                result["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e).ToArray());
                File.WriteAllText(Path.Combine(outDir, "manifest.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                foreach (var e in Errors.Take(50)) Console.WriteLine($"WARN {e}");
                Progress("Done", 100);
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"LOG {ue} failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine("NOLEVEL no map with static meshes could be read");
        return 7;
    }

    // ---- Level -----------------------------------------------------------------------------------------

    static JsonObject? ExtractLevel(DefaultFileProvider provider, string outDir, EGame ue)
    {
        var maps = provider.Files.Values.Where(f => f.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"LOG {maps.Count} maps: " + string.Join(", ", maps.Take(40).Select(m => m.Path)));
        if (maps.Count == 0) return null;

        // Pick the map whose own package + external actors hold the most static mesh components.
        (string path, List<IPackage> pkgs, int count) best = ("", new(), 0);
        foreach (var map in maps)
        {
            var pkgs = new List<IPackage>();
            if (provider.TryLoadPackage(map, out var p)) pkgs.Add(p);
            var rel = map.PathWithoutExtension;
            var ext = rel.Replace("/Content/", "/Content/__ExternalActors__/", StringComparison.OrdinalIgnoreCase);
            foreach (var f in provider.Files.Values.Where(f => f.Path.StartsWith(ext + "/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".uasset")))
                if (provider.TryLoadPackage(f, out var ep)) pkgs.Add(ep);
            var n = pkgs.Sum(pk => SafeExports(pk).Count(e => e is UStaticMeshComponent));
            Console.WriteLine($"LOG map {map.Path}: {pkgs.Count} packages, {n} static mesh components");
            if (n > best.count) best = (map.Path, pkgs, n);
        }
        if (best.count == 0) return null;
        Progress($"Reading the tower ({best.count} pieces)", 10);

        // Streamed sublevels of the chosen world.
        foreach (var w in best.pkgs.ToList().SelectMany(SafeExports).OfType<UWorld>())
            foreach (var sl in w.StreamingLevels ?? Array.Empty<FPackageIndex>())
                try
                {
                    var streaming = sl.Load();
                    var world = streaming?.GetOrDefault<FSoftObjectPath>("WorldAsset");
                    var pkgPath = world?.AssetPathName.Text.Split('.')[0];
                    if (pkgPath is not null && provider.TryLoadPackage(pkgPath, out var sp)) best.pkgs.Add(sp);
                }
                catch (Exception ex) { Errors.Add($"streaming level: {ex.Message}"); }

        Directory.CreateDirectory(Path.Combine(outDir, "meshes"));
        var meshIds = new Dictionary<string, string?>();
        var instances = new JsonArray();
        JsonObject? spawn = null;
        int done = 0, total = best.pkgs.Sum(p => SafeExports(p).Count(e => e is UStaticMeshComponent));

        foreach (var pkg in best.pkgs)
        foreach (var export in SafeExports(pkg))
        {
            if (export.ExportType == "PlayerStart" && spawn is null)
            {
                try
                {
                    var root = export.GetOrDefault<FPackageIndex>("RootComponent")?.Load<USceneComponent>();
                    if (root is not null) spawn = TransformJson(root.GetAbsoluteTransform());
                }
                catch (Exception ex) { Errors.Add($"PlayerStart: {ex.Message}"); }
            }
            if (export is not UStaticMeshComponent smc) continue;
            done++;
            if (done % 50 == 0) Progress($"Reading the tower ({done}/{total})", 10 + 80 * done / Math.Max(1, total));
            try
            {
                if (smc.GetOrDefault("bHiddenInGame", false) || !smc.GetOrDefault("bVisible", true)) continue;
                var meshRef = smc.GetOrDefault<FPackageIndex>("StaticMesh");
                if (meshRef is null || meshRef.IsNull) continue;
                var mesh = meshRef.Load<UStaticMesh>();
                if (mesh is null) continue;
                var key = mesh.GetPathName();
                if (!meshIds.TryGetValue(key, out var meshId))
                    meshIds[key] = meshId = ExportMesh(mesh, outDir, meshIds.Count);
                if (meshId is null) continue;

                var world = smc.GetAbsoluteTransform();
                if (smc is UInstancedStaticMeshComponent ism && ism.GetInstances() is { Length: > 0 } insts)
                {
                    var wm = ToMatrix(world);
                    foreach (var inst in insts)
                        instances.Add(new JsonObject { ["mesh"] = meshId, ["m"] = MatrixJson(ToMatrix(inst.TransformData) * wm) });
                }
                else instances.Add(new JsonObject { ["mesh"] = meshId, ["m"] = MatrixJson(ToMatrix(world)) });
            }
            catch (Exception ex) { Errors.Add($"{export.Name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        return new JsonObject
        {
            ["extractor"] = "onlyup-extract 0.1.0",
            ["engine"] = ue.ToString(),
            ["map"] = best.path,
            ["meshes"] = new JsonObject(meshIds.Where(kv => kv.Value is not null).Select(kv => KeyValuePair.Create(kv.Value!, (JsonNode?)$"meshes/{kv.Value}.glb"))),
            ["instances"] = instances,
            ["spawn"] = spawn,
        };
    }

    static IEnumerable<UObject> SafeExports(IPackage p)
    {
        IEnumerable<UObject> all;
        try { all = p.GetExports().ToList(); } catch (Exception ex) { Errors.Add($"{p.Name}: {ex.Message}"); yield break; }
        foreach (var e in all) yield return e;
    }

    // UE: X forward, Y right, Z up, left-handed, cm.  glTF/Godot: right-handed, Y up, m.  Swap Y<->Z (a reflection)
    // converts handedness; triangle winding is reversed to match.
    static Vector3 P(FVector v) => new(v.X * Cm, v.Z * Cm, v.Y * Cm);
    static Vector3 Pn(FVector4 v) => Vector3.Normalize(new Vector3(v.X, v.Z, v.Y));

    static Matrix4x4 ToMatrix(FTransform t)
    {
        var q = new Quaternion(-t.Rotation.X, -t.Rotation.Z, -t.Rotation.Y, t.Rotation.W);
        var s = new Vector3(t.Scale3D.X, t.Scale3D.Z, t.Scale3D.Y);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(P(t.Translation));
    }

    static JsonArray MatrixJson(Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43);

    static JsonObject TransformJson(FTransform t)
    {
        var p = P(t.Translation);
        return new JsonObject { ["pos"] = new JsonArray(p.X, p.Y, p.Z), ["m"] = MatrixJson(ToMatrix(t)) };
    }

    static string? ExportMesh(UStaticMesh mesh, string outDir, int index)
    {
        try
        {
            if (!MeshConverter.TryConvert(mesh, out StaticMeshDto dto, EMeshQuality.Highest, ENaniteMeshFormat.NoNanite, null) || dto.LODs.Count == 0)
            { Errors.Add($"mesh {mesh.Name}: could not convert"); return null; }
            var lod = dto.LODs[0];
            var id = $"{index:D5}_{Regex.Replace(mesh.Name, "[^A-Za-z0-9_]", "_")}";
            var scene = new SceneBuilder();
            var meshB = new MeshBuilder<VertexPositionNormal, VertexTexture1>(id);
            foreach (var section in lod.Sections)
            {
                var mat = MaterialFor(dto, section, mesh);
                var prim = meshB.UsePrimitive(mat);
                for (int i = 0; i < section.NumFaces; i++)
                {
                    int b = section.FirstIndex + i * 3;
                    var a = V(lod.Vertices[lod.Indices[b]]);
                    var c = V(lod.Vertices[lod.Indices[b + 1]]);
                    var d = V(lod.Vertices[lod.Indices[b + 2]]);
                    prim.AddTriangle(a, d, c); // reversed winding after the handedness swap
                }
            }
            scene.AddRigidMesh(meshB, Matrix4x4.Identity);
            scene.ToGltf2().SaveGLB(Path.Combine(outDir, "meshes", id + ".glb"));
            return id;
        }
        catch (Exception ex) { Errors.Add($"mesh {mesh.Name}: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    static (VertexPositionNormal, VertexTexture1) V(MeshVertex v) =>
        (new VertexPositionNormal(P(v.Position), Pn(v.Normal)), new VertexTexture1(new Vector2(v.Uv.U, v.Uv.V)));

    static readonly Dictionary<string, MaterialBuilder> Materials = new();
    static MaterialBuilder MaterialFor(StaticMeshDto dto, MeshSectionDto section, UStaticMesh mesh)
    {
        var matRef = section.MaterialIndex >= 0 && section.MaterialIndex < dto.Materials.Length ? dto.Materials[section.MaterialIndex].Material : null;
        var key = matRef?.ToString() ?? "default";
        if (Materials.TryGetValue(key, out var mb)) return mb;
        mb = new MaterialBuilder(key).WithDoubleSide(false).WithMetallicRoughnessShader();
        try
        {
            if (matRef?.Load() is UMaterialInterface mi)
            {
                var p = new CUE4Parse.UE4.Assets.Exports.Material.CMaterialParams();
                mi.GetParams(p);
                if (p.Diffuse?.Load() is UTexture tex && Png(tex) is { } png)
                    mb.WithBaseColor(new SharpGLTF.Memory.MemoryImage(png));
                else
                    mb.WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1));
            }
        }
        catch (Exception ex) { Errors.Add($"material {key}: {ex.Message}"); }
        Materials[key] = mb;
        return mb;
    }

    static byte[]? Png(UTexture tex)
    {
        var key = tex.GetPathName();
        if (TextureCache.TryGetValue(key, out var hit)) return hit;
        byte[]? png = null;
        try
        {
            var decoded = TextureDecoder.Decode(tex, 1024, ETexturePlatform.DesktopMobile);
            if (decoded is not null) png = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _, 100);
        }
        catch (Exception ex) { Errors.Add($"texture {tex.Name}: {ex.Message}"); }
        return TextureCache[key] = png;
    }

    // ---- Music -----------------------------------------------------------------------------------------

    static void ExtractMusic(DefaultFileProvider provider, string outDir, JsonNode plan, JsonObject result)
    {
        Progress("Reading Only Up!'s music", 92);
        Directory.CreateDirectory(Path.Combine(outDir, "music"));
        var tracks = new JsonArray();
        var candidates = provider.Files.Values
            .Where(f => f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(f.Path, "music|ost|soundtrack", RegexOptions.IgnoreCase))
            .OrderBy(f => f.Path).ToList();
        Console.WriteLine($"LOG {candidates.Count} music candidates");
        foreach (var f in candidates)
        {
            if (tracks.Count >= 3) break;
            try
            {
                if (!provider.TryLoadPackage(f, out var pkg)) continue;
                foreach (var sw in SafeExports(pkg).OfType<USoundWave>())
                {
                    SoundDecoder.Decode(sw, true, out var fmt, out var data);
                    if (data is null || fmt is null) continue;
                    var ext = fmt.ToLowerInvariant();
                    if (ext is not ("ogg" or "wav" or "mp3")) { Errors.Add($"music {sw.Name}: format {fmt} not playable"); continue; }
                    var name = $"music/{tracks.Count:D2}_{Regex.Replace(sw.Name, "[^A-Za-z0-9_]", "_")}.{ext}";
                    File.WriteAllBytes(Path.Combine(outDir, name), data);
                    tracks.Add(new JsonObject { ["file"] = name, ["asset"] = f.Path });
                }
            }
            catch (Exception ex) { Errors.Add($"music {f.Path}: {ex.Message}"); }
        }
        result["music"] = tracks;
    }

    static string? Arg(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static void Progress(string what, int pct) => Console.WriteLine($"PROGRESS {pct} {what}");
    static string Stamp(string dir) => string.Join(";", Directory.EnumerateFiles(dir).OrderBy(f => f)
        .Select(f => new FileInfo(f)).Select(fi => $"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"));
}
