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
            // A directory override that already is (or contains) an aircraft
            // config dir is used directly - the AircraftLoaded system state
            // hands us the exact aircraft.cfg path, so no title matching or
            // package scan is needed at all.
            if (Directory.Exists(rootOverride) &&
                (File.Exists(Path.Combine(rootOverride, "aircraft.cfg")) ||
                 File.Exists(Path.Combine(rootOverride, "flight_model.cfg"))))
                return rootOverride;
            if (Directory.Exists(rootOverride))
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

    /// <summary>
    /// Candidate aircraft.cfg locations, found by walking directories rather
    /// than enumerating every file: the VFS projection holds ~4000 packages
    /// with deep texture/sound trees, so a recursive file scan takes minutes.
    /// Config files live at the package root, in common/, or common/config/ —
    /// depth 5 covers all observed layouts.
    /// </summary>
    private static IEnumerable<string> EnumerateAircraftConfigs(string root)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < 20000)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            var cfg = Path.Combine(dir, "aircraft.cfg");
            var hasCfg = false;
            try { hasCfg = File.Exists(cfg); } catch { }
            if (hasCfg) { yield return cfg; continue; }
            if (depth >= 5) continue;
            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch { continue; }
            foreach (var s in subs)
            {
                // Prune content trees that never hold a package root: a nested
                // aircraft.cfg inside sound/texture/model/panel data is not a
                // package, and these subtrees hold most of the files.
                if (s.Name.Equals("sound", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals("sounds", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.StartsWith("texture", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals("panel", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals("liveries", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals("attachments", StringComparison.OrdinalIgnoreCase))
                    continue;
                queue.Enqueue((s.FullName, depth + 1));
            }
        }
    }

    internal static bool MatchesTitle(string cfgPath, string title)
    {
        try
        {
            var normalizedTitle = Normalize(title);
            // An empty title means the caller already pointed at the package
            // (the AircraftLoaded path hint): the first config found wins.
            if (normalizedTitle.Length == 0) return true;
            // aircraft.cfg has no single guaranteed identity key: packages variously
            // publish "title", atc_type/atc_model, or only the ICAO designator, so
            // every candidate string is collected and matched against the sim title.
            var candidates = new List<string>();
            var atcType = "";
            var atcModel = "";
            foreach (var line in File.ReadLines(cfgPath))
            {
                var t = line.Trim();
                var eq = t.IndexOf('=');
                if (eq < 0) continue;
                var key = t[..eq].Trim();
                var value = t[(eq + 1)..].Trim().Trim('"');
                if (key.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("icao_type_designator", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(value);
                if (key.Equals("atc_type", StringComparison.OrdinalIgnoreCase)) atcType = value;
                if (key.Equals("atc_model", StringComparison.OrdinalIgnoreCase)) atcModel = value;
            }
            if (atcType.Length > 0 && atcModel.Length > 0)
                candidates.Add(atcType + " " + atcModel);
            // The package folder name is a slug of the title in many packages.
            var dirName = Path.GetFileName(Path.GetDirectoryName(cfgPath));
            if (!string.IsNullOrEmpty(dirName)) candidates.Add(dirName.Replace('_', ' ').Replace('-', ' '));
            foreach (var c in candidates)
            {
                var n = Normalize(c);
                if (n.Length > 0 && (n.Contains(normalizedTitle) || normalizedTitle.Contains(n))) return true;
            }
        }
        catch { }
        return false;
    }

    internal static string Normalize(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// MSFS ships unresolved localization tokens in cfg titles and the sim's
    /// TITLE variable ("$$:Cessna $$:172"). Strip the token markers so the GUI
    /// and log show "Cessna 172"; matching never depended on them anyway.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(title, @"\$\$:\s*", "");
        t = t.Replace("$$", "").Trim();
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ");
    }

    /// <summary>True when the directory looks like an aircraft package root.</summary>
    public static bool HasProfileContent(string dir)
        => File.Exists(Path.Combine(dir, "aircraft.cfg")) ||
           File.Exists(Path.Combine(dir, "flight_model.cfg"));
}
