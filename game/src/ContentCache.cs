using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OnlyBebop;

/// systems.content_cache: %LOCALAPPDATA%/OnlyBebop/cache/<game>. Filled by the extractors from the player's own
/// installs; re-filled when a game updates. Never leaves the player's PC.
public static class ContentCache
{
    public static string Root => Environment.GetEnvironmentVariable("ONLYBEBOP_HOME") is { Length: > 0 } h ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyBebop");
    public static string Dir(string game) => Path.Combine(Root, "cache", game);
    public static string ToolsDir => Path.Combine(Path.GetDirectoryName(Godot.OS.GetExecutablePath()) ?? ".", "tools");
    public static string PlanPath => Path.Combine(ToolsDir, "plan.json");

    public static JsonObject? Manifest(string game)
    {
        var p = Path.Combine(Dir(game), "manifest.json");
        try { return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) as JsonObject : null; } catch { return null; }
    }

    public static string Abs(string game, string? rel) => rel is null ? "" : Path.Combine(Dir(game), rel);

    public static bool DeadlockFresh(string deadlockDir)
    {
        var m = Manifest("deadlock");
        var vpk = Path.Combine(deadlockDir, Sheets.Games.Deadlock.Archive);
        if (m?["source"] is not JsonObject s || !File.Exists(vpk)) return false;
        var fi = new FileInfo(vpk);
        return s["size"]?.GetValue<long>() == fi.Length && s["mtime"]?.GetValue<string>() == fi.LastWriteTimeUtc.ToString("o");
    }

    public static bool OnlyUpFresh(string onlyUpDir)
    {
        var m = Manifest("onlyup");
        if (m?["source"] is not JsonObject s) return false;
        var paks = s["paks"]?.GetValue<string>();
        if (paks is null || !Directory.Exists(paks) || !paks.StartsWith(onlyUpDir, StringComparison.OrdinalIgnoreCase)) return false;
        return s["stamp"]?.GetValue<string>() == OnlyUpExtractor.Stamp(paks);
    }
}

public sealed record ExtractResult(int ExitCode, string LastLine);

/// Runs one of the bundled extractor programs and streams its PROGRESS lines.
public static class ExtractorRunner
{
    public static async Task<ExtractResult> Run(string exe, string args, Action<int, string> progress, Action<string> log)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = new Process { StartInfo = psi };
        string last = "";
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            log(e.Data);
            if (e.Data.StartsWith("PROGRESS "))
            {
                var parts = e.Data.Split(' ', 3);
                if (int.TryParse(parts[1], out var pct)) progress(pct, parts.Length > 2 ? parts[2] : "");
            }
            else if (!e.Data.StartsWith("LOG ")) last = e.Data;
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { log("ERR " + e.Data); last = e.Data; } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return new ExtractResult(p.ExitCode, last);
    }
}

/// systems.deadlock_extract
public static class DeadlockExtractor
{
    public static string Exe => Path.Combine(ContentCache.ToolsDir, "deadlock-extract", OperatingSystem.IsWindows() ? "deadlock-extract.exe" : "deadlock-extract");
    public static Task<ExtractResult> Run(string deadlockDir, Action<int, string> progress, Action<string> log) =>
        ExtractorRunner.Run(Exe, $"--game \"{deadlockDir}\" --plan \"{ContentCache.PlanPath}\" --out \"{ContentCache.Dir("deadlock")}\"", progress, log);
}

/// systems.onlyup_extract
public static class OnlyUpExtractor
{
    public static string Exe => Path.Combine(ContentCache.ToolsDir, "onlyup-extract", OperatingSystem.IsWindows() ? "onlyup-extract.exe" : "onlyup-extract");
    public static Task<ExtractResult> Run(string onlyUpDir, Action<int, string> progress, Action<string> log) =>
        ExtractorRunner.Run(Exe, $"--game \"{onlyUpDir}\" --plan \"{ContentCache.PlanPath}\" --out \"{ContentCache.Dir("onlyup")}\"", progress, log);

    // Must match onlyup-extract's Stamp().
    public static string Stamp(string dir) => string.Join(";", Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => new FileInfo(f)).Select(fi => $"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"));
}
