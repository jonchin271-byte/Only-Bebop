using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyBebop;

/// systems.steam_locator: finds a Steam game's folder from Steam's own library list.
public static class SteamLocator
{
    public static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p) roots.Add(p);
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string q) roots.Add(q);
            }
            catch { /* registry unavailable */ }
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".steam", "steam"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
        }
        var extra = Environment.GetEnvironmentVariable("ONLYBEBOP_STEAM_ROOT");
        if (!string.IsNullOrEmpty(extra)) roots.Insert(0, extra);
        return roots.Select(r => r.Replace('/', Path.DirectorySeparatorChar)).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> Libraries()
    {
        var libs = new List<string>();
        foreach (var root in SteamRoots())
        {
            libs.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// The install folder of a Steam app, from appmanifest_<appid>.acf; null when not installed.
    public static string? FindApp(int appId, string? folderNamePattern = null)
    {
        foreach (var lib in Libraries())
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(acf))
            {
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                if (m.Success)
                {
                    var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                    if (Directory.Exists(dir)) return dir;
                }
            }
        }
        if (folderNamePattern is null) return null;
        foreach (var lib in Libraries())
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            var hit = Directory.EnumerateDirectories(common).FirstOrDefault(d => Regex.IsMatch(Path.GetFileName(d), folderNamePattern, RegexOptions.IgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}
