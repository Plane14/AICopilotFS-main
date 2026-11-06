using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MSFS2024AIPilot.Core.Paths;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>
/// Finds the installed aircraft package directory so performance and checklist
/// data can be read from it. Matches on the title inside aircraft.cfg, since the
/// package folder name is a slug that rarely equals the sim's published title.
/// </summary>
public static class AircraftPackageLocator
{
    public static string? LocateAircraftDirectory(string title, string? rootOverride = null, SimulatorPathsConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            var dir = ResolveOverrideDirectory(rootOverride);
            if (!string.IsNullOrEmpty(dir)) return dir;
            if (!string.IsNullOrWhiteSpace(title) && Directory.Exists(rootOverride))
                return LocateWithin(title, rootOverride);
            return null;
        }
        if (string.IsNullOrWhiteSpace(title)) return null;
        return LocateAcrossRoots(title, config);
    }

    private static string? ResolveOverrideDirectory(string? rootOverride)
    {
        if (string.IsNullOrWhiteSpace(rootOverride) || !File.Exists(rootOverride)) return null;
        // The override may be the .air file itself or a directory.
        var dir = Directory.Exists(rootOverride) ? rootOverride : Path.GetDirectoryName(rootOverride);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    private static string? LocateWithin(string title, string root)
    {
        foreach (var cfg in EnumerateAircraftConfigs(root))
        {
            var dir = Path.GetDirectoryName(cfg);
            if (dir is not null && MatchesTitle(cfg, title)) return dir;
        }
        return null;
    }

    private static string? LocateAcrossRoots(string title, SimulatorPathsConfig? config)
    {
        var layout = config is null ? MsfsInstallLayout.Discover() : MsfsInstallLayout.Discover(config);
        foreach (var root in layout.AircraftSearchRoots)
        {
            var found = LocateWithin(title, root);
            if (found is not null) return found;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateAircraftConfigs(string root)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "aircraft.cfg", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
        {
            // Skip deep unrelated trees (sound, textures) — aircraft.cfg lives at the package root.
            if (f.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("panel", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    internal static bool MatchesTitle(string cfgPath, string title)
    {
        try
        {
            var normalizedTitle = Normalize(title);
            foreach (var line in File.ReadLines(cfgPath))
            {
                var t = line.Trim();
                var eq = t.IndexOf('=');
                if (eq < 0 || !t[..eq].Trim().Equals("title", StringComparison.OrdinalIgnoreCase)) continue;
                var value = t[(eq + 1)..].Trim().Trim('"');
                return Normalize(value).Contains(normalizedTitle) || normalizedTitle.Contains(Normalize(value));
            }
        }
        catch { }
        return false;
    }

    internal static string Normalize(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>True when the directory looks like an aircraft package root.</summary>
    public static bool HasProfileContent(string dir)
        => File.Exists(Path.Combine(dir, "aircraft.cfg")) ||
           File.Exists(Path.Combine(dir, "flight_model.cfg"));
}
