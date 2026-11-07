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
        // Collect every title match and keep the best-scoring package instead
        // of the first one found: AI-traffic repaint packages (aigaim_*) carry
        // the original aircraft title in their cfg, so a first-match scan
        // resolves the user aircraft to a repaint that has no checklists.
        string? best = null;
        string? bestFallback = null;
        var bestScore = int.MinValue;
        var fallbackScore = int.MinValue;
        foreach (var cfg in EnumerateAircraftConfigs(root))
        {
            var dir = Path.GetDirectoryName(cfg);
            if (dir is null) continue;
            var score = MatchScore(cfg, title);
            if (score == int.MinValue) continue;
            if (IsAiRepaintPackage(cfg))
            {
                if (score > fallbackScore) { fallbackScore = score; bestFallback = dir; }
            }
            else if (score > bestScore) { bestScore = score; best = dir; }
        }
        return best ?? bestFallback;
    }

    /// <summary>
    /// aigaim_* folders are Asobo's AI-traffic repaints of the real aircraft;
    /// they duplicate the title but are never the user aircraft.
    /// </summary>
    private static bool IsAiRepaintPackage(string cfgPath)
    {
        var dir = Path.GetDirectoryName(cfgPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("aigaim", StringComparison.OrdinalIgnoreCase)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// Scores how well a candidate aircraft.cfg matches the sim title.
    /// Exact normalized equality beats substring containment; a "title=" key
    /// beats derived atc_type/atc_model or folder-slug candidates. Returns
    /// <see cref="int.MinValue"/> when nothing matches.
    /// </summary>
    private static int MatchScore(string cfgPath, string title)
    {
        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Length == 0) return 0;
        // The package folder name is a slug of the title in many packages —
        // and for encrypted packages (VFS stubs) it is the ONLY identity
        // signal, since the cfg itself cannot be read.
        var dirName = Path.GetFileName(Path.GetDirectoryName(cfgPath));
        var folderScore = int.MinValue;
        if (!string.IsNullOrEmpty(dirName))
        {
            folderScore = ScoreCandidate(dirName.Replace('_', ' ').Replace('-', ' '), normalizedTitle, 40);
            folderScore = Math.Max(folderScore, ScoreFolderTokens(dirName, title, normalizedTitle));
        }
        try
        {
            var best = folderScore;
            var atcType = "";
            var atcModel = "";
            foreach (var line in File.ReadLines(cfgPath))
            {
                var t = line.Trim();
                var eq = t.IndexOf('=');
                if (eq < 0) continue;
                var key = t[..eq].Trim();
                var value = t[(eq + 1)..].Trim().Trim('"');
                if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, ScoreCandidate(value, normalizedTitle, 100));
                else if (key.Equals("icao_type_designator", StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, ScoreCandidate(value, normalizedTitle, 60));
                if (key.Equals("atc_type", StringComparison.OrdinalIgnoreCase)) atcType = value;
                if (key.Equals("atc_model", StringComparison.OrdinalIgnoreCase)) atcModel = value;
            }
            if (atcType.Length > 0 && atcModel.Length > 0)
                best = Math.Max(best, ScoreCandidate(atcType + " " + atcModel, normalizedTitle, 80));
            return best;
        }
        catch { }
        return folderScore;
    }

    private static int ScoreCandidate(string candidate, string normalizedTitle, int weight)
    {
        var n = Normalize(candidate);
        if (n.Length == 0) return int.MinValue;
        if (n == normalizedTitle) return weight + 1000;
        if (n.Contains(normalizedTitle) || normalizedTitle.Contains(n)) return weight;
        return int.MinValue;
    }

    /// <summary>
    /// Weak folder-slug evidence for packages whose cfg cannot be read (VFS
    /// encrypted stubs): scores title words that appear inside slug tokens.
    /// "Cessna 172" against "asobo_c172sp" earns partial credit via "172".
    /// </summary>
    private static int ScoreFolderTokens(string dirName, string title, string normalizedTitle)
    {
        var slug = Normalize(dirName.Replace('_', ' ').Replace('-', ' '));
        if (slug.Length == 0) return int.MinValue;
        var words = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Normalize(w)).Where(w => w.Length >= 2).ToList();
        if (words.Count == 0) return int.MinValue;
        var hits = words.Count(w => slug.Contains(w));
        // Require at least one hit and scale by coverage; model numbers are
        // the strongest signal, so a single hit still scores.
        return hits == 0 ? int.MinValue : 20 + 20 * hits / words.Count;
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
