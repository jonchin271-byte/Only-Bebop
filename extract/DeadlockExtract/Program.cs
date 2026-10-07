// deadlock-extract: reads the player's own Deadlock install and writes what Only Bebop needs into a private
// cache on their PC. Nothing read here is ever uploaded or shipped. What to read comes from plan.json, which
// tools/gen.py derives from design/sheets.
//
// usage: deadlock-extract --game <Deadlock folder> --plan <plan.json> --out <cache dir>
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

static class Program
{
    static readonly List<string> Errors = new();

    static int Main(string[] args)
    {
        string? game = Arg(args, "--game"), planPath = Arg(args, "--plan"), outDir = Arg(args, "--out");
        if (game is null || planPath is null || outDir is null)
        {
            Console.Error.WriteLine("usage: deadlock-extract --game <Deadlock folder> --plan <plan.json> --out <cache dir>");
            return 2;
        }
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!["deadlock"]!;
        var vpkPath = Path.Combine(game, plan["archive"]!.GetValue<string>());
        if (!File.Exists(vpkPath))
        {
            Console.Error.WriteLine($"MISSING {vpkPath}");
            return 3;
        }
        Directory.CreateDirectory(outDir);
        Progress("Opening Deadlock's files", 0);

        using var package = new Package();
        package.Read(vpkPath);
        using var loader = new GameFileLoader(package, vpkPath);

        var manifest = new JsonObject
        {
            ["source"] = SourceStamp(vpkPath),
            ["extractor"] = "deadlock-extract 0.1.0",
        };

        // 1. Data: heroes + abilities vdata
        var heroes = LoadKv3(loader, "scripts/heroes.vdata_c");
        var abilities = LoadKv3(loader, "scripts/abilities.vdata_c");
        Progress("Reading hero and ability data", 10);

        var stats = new JsonObject();
        foreach (var (id, pathNode) in plan["stats"]!.AsObject())
        {
            var path = pathNode!.GetValue<string>();
            var raw = ResolveStat(path, heroes, abilities);
            if (raw is null) { if (!path.Contains(' ')) Errors.Add($"stat {id}: {path} not found"); continue; }
            stats[id] = new JsonObject { ["raw"] = raw, ["value"] = ParseNumber(raw), ["meters"] = raw.TrimEnd().EndsWith('m') };
        }
        manifest["stats"] = stats;

        // 2. Roster + hero cards
        var names = LoadHeroNames(game);
        var roster = new JsonArray();
        Directory.CreateDirectory(Path.Combine(outDir, "cards"));
        var playable = plan["heroes"]!.AsArray().Select(h => h!["vdata_key"]!.GetValue<string>()).ToHashSet();
        if (heroes is not null)
        {
            foreach (var key in heroes.Keys.Where(k => k.StartsWith("hero_")))
            {
                var h = heroes[key];
                if (Bool(h, "m_bDisabled") || Bool(h, "m_bInDevelopment")) continue;
                if (Regex.IsMatch(key, "test|dummy|base|genericperson")) continue;
                var card = Str(h, "m_strIconHeroCard");
                if (card is null) continue;
                string? cardFile = null;
                var vtex = PanoramaToVtex(card);
                var png = ExtractToFile(loader, vtex, Path.Combine(outDir, "cards", key));
                if (png is not null) cardFile = Path.GetRelativePath(outDir, png).Replace('\\', '/');
                roster.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = names.GetValueOrDefault(key) ?? TitleCase(key[5..]),
                    ["card"] = cardFile,
                    ["playable"] = playable.Contains(key),
                    ["hero_id"] = Int(h, "m_HeroID"),
                });
            }
        }
        manifest["roster"] = roster;
        Progress("Reading the hero roster", 30);

        // 3. Ability icons
        var icons = new JsonObject();
        Directory.CreateDirectory(Path.Combine(outDir, "icons"));
        foreach (var a in plan["abilities"]!.AsArray())
        {
            var id = a!["id"]!.GetValue<string>();
            var abilityImage = abilities is not null && abilities.TryGetValue(a["vdata_key"]!.GetValue<string>(), out var ab) ? Str(ab, "m_strAbilityImage") : null;
            var vtex = abilityImage is not null ? PanoramaToVtex(abilityImage) : a["icon"]!.GetValue<string>();
            var png = ExtractToFile(loader, vtex, Path.Combine(outDir, "icons", id));
            if (png is not null) icons[id] = Path.GetRelativePath(outDir, png).Replace('\\', '/');
        }
        manifest["icons"] = icons;

        // 4. Sounds: event -> script's vsnd_files -> first file
        var sounds = new JsonObject();
        Directory.CreateDirectory(Path.Combine(outDir, "sounds"));
        var scripts = new Dictionary<string, KVObject?>();
        foreach (var s in plan["sounds"]!.AsArray())
        {
            string id = s!["id"]!.GetValue<string>(), ev = s["event"]!.GetValue<string>(), script = s["script"]!.GetValue<string>();
            if (!scripts.TryGetValue(script, out var events)) scripts[script] = events = LoadKv3(loader, script);
            if (events is null || !events.TryGetValue(ev, out var e)) { Errors.Add($"sound {id}: event {ev} not in {script}"); continue; }
            var files = new List<string>();
            foreach (var k in e.Keys.Where(k => k.StartsWith("vsnd_files")))
                CollectStrings(e[k], files);
            var first = files.FirstOrDefault(f => f.EndsWith(".vsnd"));
            if (first is null) { Errors.Add($"sound {id}: no vsnd_files"); continue; }
            var outFile = ExtractToFile(loader, first + "_c", Path.Combine(outDir, "sounds", id));
            if (outFile is not null) sounds[id] = Path.GetRelativePath(outDir, outFile).Replace('\\', '/');
        }
        manifest["sounds"] = sounds;
        Progress("Reading Bebop's voice and sounds", 45);

        // 5. Hero models (with animations)
        var models = new JsonObject();
        Directory.CreateDirectory(Path.Combine(outDir, "models"));
        foreach (var h in plan["heroes"]!.AsArray())
        {
            string id = h!["id"]!.GetValue<string>(), model = h["model"]!.GetValue<string>();
            try
            {
                var res = loader.LoadFileCompiled(model);
                if (res is null) { Errors.Add($"model {id}: {model} not found"); continue; }
                var exporter = new GltfModelExporter(loader)
                {
                    ExportMaterials = true,
                    ExportAnimations = true,
                    AdaptTextures = true,
                    SatelliteImages = false,
                    ProgressReporter = new Progress<string>(m => Console.WriteLine($"LOG {m}")),
                };
                var target = Path.Combine(outDir, "models", id + ".glb");
                exporter.Export(res, target, CancellationToken.None);
                models[id] = $"models/{id}.glb";
            }
            catch (Exception ex) { Errors.Add($"model {id}: {ex.GetType().Name}: {ex.Message}"); }
        }
        manifest["models"] = models;
        Progress("Reading Bebop's model and animations", 95);

        manifest["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e).ToArray());
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var e in Errors) Console.WriteLine($"WARN {e}");
        Progress("Done", 100);
        return models.Count > 0 ? 0 : 4;
    }

    static string? Arg(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static void Progress(string what, int pct) => Console.WriteLine($"PROGRESS {pct} {what}");

    static JsonObject SourceStamp(string vpk)
    {
        var fi = new FileInfo(vpk);
        return new JsonObject { ["archive"] = vpk, ["size"] = fi.Length, ["mtime"] = fi.LastWriteTimeUtc.ToString("o") };
    }

    static KVObject? LoadKv3(GameFileLoader loader, string path)
    {
        try
        {
            var res = loader.LoadFileCompiled(path);
            if (res?.DataBlock is BinaryKV3 kv) return kv.Data;
            Errors.Add($"{path}: not found or not KV3");
        }
        catch (Exception ex) { Errors.Add($"{path}: {ex.Message}"); }
        return null;
    }

    // "heroes.vdata/hero_bebop/m_mapStartingStats/EMaxMoveSpeed" or "abilities.vdata/<ability>/<Property>"
    static string? ResolveStat(string path, KVObject? heroes, KVObject? abilities)
    {
        var parts = path.Split('/');
        KVObject? node = parts[0] switch { "heroes.vdata" => heroes, "abilities.vdata" => abilities, _ => null };
        if (node is null) return null;
        for (int i = 1; i < parts.Length; i++)
        {
            if (!node.TryGetValue(parts[i], out var next))
            {
                if (parts[0] == "abilities.vdata" && i == parts.Length - 1 && node.TryGetValue("m_mapAbilityProperties", out var props)
                    && props.TryGetValue(parts[i], out var prop) && prop.TryGetValue("m_strValue", out var v))
                    return v.ToString();
                return null;
            }
            node = next;
        }
        return node.ToString();
    }

    static double? ParseNumber(string raw)
    {
        var m = Regex.Match(raw, @"-?\d+(\.\d+)?");
        return m.Success ? double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    static bool Bool(KVObject o, string k) => o.TryGetValue(k, out var v) && v.ToString() is "1" or "true" or "True";
    static string? Str(KVObject o, string k) => o.TryGetValue(k, out var v) ? v.ToString() : null;
    static int? Int(KVObject o, string k) => o.TryGetValue(k, out var v) && int.TryParse(v.ToString(), out var i) ? i : null;

    static void CollectStrings(KVObject o, List<string> into)
    {
        if (o.IsArray || o.IsCollection) { foreach (var c in o.Values) CollectStrings(c, into); }
        else into.Add(o.ToString() ?? "");
    }

    // panorama:"file://{images}/heroes/bebop_card.psd" -> panorama/images/heroes/bebop_card_psd.vtex_c
    static string PanoramaToVtex(string s)
    {
        s = s.Replace("panorama:", "").Trim('"').Replace("file://{images}/", "panorama/images/");
        var ext = Path.GetExtension(s);
        return s[..^ext.Length] + "_" + ext.TrimStart('.') + ".vtex_c";
    }

    static string? ExtractToFile(GameFileLoader loader, string path, string outBase)
    {
        try
        {
            var res = loader.LoadFileCompiled(path);
            if (res is null) { Errors.Add($"{path}: not found"); return null; }
            using var content = FileExtract.Extract(res, loader, null);
            if (content.Data is null) { Errors.Add($"{path}: nothing extracted"); return null; }
            var ext = Path.GetExtension(content.FileName ?? "");
            if (string.IsNullOrEmpty(ext)) ext = FileExtract.GetExtension(res) is { } e ? "." + e : ".bin";
            var target = outBase + ext;
            File.WriteAllBytes(target, content.Data);
            return target;
        }
        catch (Exception ex) { Errors.Add($"{path}: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    // Hero display names from Deadlock's own English localization (plain KeyValues text on disk).
    static Dictionary<string, string> LoadHeroNames(string game)
    {
        var names = new Dictionary<string, string>();
        var dir = Path.Combine(game, "game", "citadel", "resource", "localization");
        if (!Directory.Exists(dir)) return names;
        var rx = new Regex("^\\s*\"(hero_[a-z0-9_]+)\"\\s+\"([^\"]+)\"", RegexOptions.Multiline);
        foreach (var f in Directory.EnumerateFiles(dir, "*english*.txt", SearchOption.AllDirectories))
            foreach (Match m in rx.Matches(File.ReadAllText(f)))
                names.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return names;
    }

    static string TitleCase(string s) => string.Join(' ', s.Split('_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
}
