using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

namespace OnlyBebop;

/// systems.level_loader: Only Up!'s tower from the cache (level sheet). One MultiMesh per unique mesh for
/// drawing, one trimesh collider per placed piece.
public partial class LevelLoader : Node3D
{
    public Vector3 Spawn;
    public float LowestY = float.MaxValue;
    public int Pieces;

    public static Transform3D FromRow(JsonArray m) => new(
        new Basis(new Vector3(F(m[0]), F(m[1]), F(m[2])), new Vector3(F(m[3]), F(m[4]), F(m[5])), new Vector3(F(m[6]), F(m[7]), F(m[8]))),
        new Vector3(F(m[9]), F(m[10]), F(m[11])));
    static float F(JsonNode? n) => float.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public void Load(JsonObject manifest, Action<string>? log = null)
    {
        var meshes = new Dictionary<string, (Mesh mesh, Shape3D shape)>();
        foreach (var (id, rel) in manifest["meshes"]!.AsObject())
        {
            var path = ContentCache.Abs("onlyup", rel!.GetValue<string>());
            var mesh = LoadFirstMesh(path);
            if (mesh is null) { log?.Invoke($"mesh {id} failed"); continue; }
            meshes[id] = (mesh, mesh.CreateTrimeshShape());
        }

        var byMesh = manifest["instances"]!.AsArray().GroupBy(i => i!["mesh"]!.GetValue<string>());
        var body = new StaticBody3D { Name = "Tower" };
        AddChild(body);
        foreach (var g in byMesh)
        {
            if (!meshes.TryGetValue(g.Key, out var mm)) continue;
            var xforms = g.Select(i => FromRow(i!["m"]!.AsArray())).ToList();
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mm.mesh, InstanceCount = xforms.Count };
            for (int i = 0; i < xforms.Count; i++) multi.SetInstanceTransform(i, xforms[i]);
            AddChild(new MultiMeshInstance3D { Multimesh = multi });
            foreach (var x in xforms)
            {
                body.AddChild(new CollisionShape3D { Shape = mm.shape, Transform = x });
                var aabb = x * mm.mesh.GetAabb();
                LowestY = Math.Min(LowestY, aabb.Position.Y);
                Pieces++;
            }
        }

        if (manifest["spawn"]?["pos"] is JsonArray sp) Spawn = new Vector3(F(sp[0]), F(sp[1]), F(sp[2]));
        else Spawn = FallbackSpawn(body);
        if (LowestY == float.MaxValue) LowestY = Spawn.Y - 10;
    }

    /// level.spawn fallback: top of the lowest walkable surface near the world origin.
    Vector3 FallbackSpawn(StaticBody3D body)
    {
        float best = float.MaxValue; Vector3 at = Vector3.Zero;
        foreach (var c in body.GetChildren().OfType<CollisionShape3D>())
        {
            var p = c.Transform.Origin;
            var d = new Vector2(p.X, p.Z).Length() + p.Y * 0.5f;
            if (d < best) { best = d; at = p + Vector3.Up * 3; }
        }
        return at;
    }

    public static Mesh? LoadFirstMesh(string glb)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(glb, state) != Error.Ok) return null;
        var root = doc.GenerateScene(state);
        var mi = Find<MeshInstance3D>(root);
        var mesh = mi?.Mesh;
        root.QueueFree();
        return mesh;
    }

    public static T? Find<T>(Node n) where T : Node
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren()) if (Find<T>(c) is { } hit) return hit;
        return null;
    }
}
