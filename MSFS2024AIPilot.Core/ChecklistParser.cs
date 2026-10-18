using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Finds the installed aircraft package so checklists can be read from it.
/// Uses the same MSFS install/VFS discovery as the aircraft profile loader.
/// </summary>
public static class AircraftPackageLocator
{
    internal static List<string> SearchRoots() => new(MsfsInstallLayout.Discover().AircraftSearchRoots);

    /// <summary>
    /// Resolves the aircraft package directory. When <paramref name="rootOverride"/>
    /// is provided (e.g. from the .air file path reported by the AircraftLoaded
    /// system state), the filesystem scan is skipped entirely.
    /// </summary>
    public static string? LocateAircraftDirectory(string title, string? rootOverride = null)
        => LocateAircraftDirectoryCore(title, rootOverride, allowProfileFallback: false);

    internal static string? LocateAircraftDirectoryForPerformance(string title, string? rootOverride = null)
        => LocateAircraftDirectoryCore(title, rootOverride, allowProfileFallback: true);

    private static string? LocateAircraftDirectoryCore(string title, string? rootOverride, bool allowProfileFallback)
    {
        var overrideDir = ResolveOverrideDirectory(rootOverride);
        if (!string.IsNullOrEmpty(overrideDir))
            return overrideDir;

        // A supplied override is authoritative. If it does not resolve to a real
        // aircraft package root, do not fall through to a broad Community scan: a
        // helper or traffic folder can be a large tree and scanning it is exactly the
        // false-positive path that caused the empty-pilot / generic-checklist state.
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            if (string.IsNullOrWhiteSpace(title) || !Directory.Exists(rootOverride))
                return null;

            return LocateWithinOverrideRoot(title, rootOverride, allowProfileFallback);
        }

        if (string.IsNullOrWhiteSpace(title))
            return null;

        return LocateAcrossRoots(title, SearchRoots(), allowProfileFallback);
    }

    private static string? LocateWithinOverrideRoot(string title, string rootOverride, bool allowProfileFallback)
    {
        var normalizedTitle = Normalize(title);
        var titleMatches = new List<string>();
        var fallbackMatches = allowProfileFallback
            ? new List<string>()
            : null;

        foreach (var cfg in SafeEnumerateFiles(rootOverride, "aircraft.cfg"))
        {
            var dir = Directory.GetParent(cfg);
            if (dir is null || !MatchesAircraftTitle(cfg, title, normalizedTitle))
                continue;

            if (HasProfileContent(dir.FullName))
                return dir.FullName;

            titleMatches.Add(dir.FullName);
        }

        if (allowProfileFallback)
        {
            foreach (var candidate in EnumerateProfileCandidates(rootOverride))
                fallbackMatches!.Add(candidate);
        }

        if (titleMatches.Count > 0)
            return titleMatches[0];

        if (fallbackMatches is { Count: > 0 })
            return fallbackMatches[0];

        return null;
    }

    private static string? LocateAcrossRoots(string title, IEnumerable<string> roots, bool allowProfileFallback)
    {
        var normalizedTitle = Normalize(title);
        var titleMatches = new List<string>();
        var fallbackMatches = allowProfileFallback
            ? new List<string>()
            : null;

        foreach (var root in SearchRoots())
        {
            try
            {
                // Walk every aircraft directory and inspect its aircraft.cfg: the folder
                // name is a package slug (e.g. "asobo_b737max") while the title the sim
                // publishes is the variation's title (e.g. "737 Max 8 Passengers"), so
                // matching on the folder name alone never finds the package.
                foreach (var cfg in EnumerateAircraftConfigCandidates(root))
                {
                    var dir = Directory.GetParent(cfg);
                    if (dir is null) continue;
                    if (!MatchesAircraftTitle(cfg, title, normalizedTitle))
                        continue;

                    if (HasProfileContent(dir.FullName))
                        return dir.FullName;

                    titleMatches.Add(dir.FullName);
                }

                if (allowProfileFallback)
                {
                    foreach (var candidate in EnumerateProfileCandidates(root))
                        fallbackMatches!.Add(candidate);
                }
            }
            catch { }
        }

        if (titleMatches.Count > 0)
            return titleMatches[0];

        if (fallbackMatches is { Count: > 0 })
            return fallbackMatches[0];

        return null;
    }

    private static bool MatchesAircraftTitle(string cfgPath, string title, string normalizedTitle)
    {
        try
        {
            var content = File.ReadAllText(cfgPath);
            return content.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ContainsTitle(content, normalizedTitle);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasProfileContent(string dir)
        => File.Exists(Path.Combine(dir, "flight_model.cfg")) ||
           File.Exists(Path.Combine(dir, "flight_performance.cfg")) ||
           SafeEnumerateFiles(dir, "flight_model.cfg").Count > 0 ||
           SafeEnumerateFiles(dir, "flight_performance.cfg").Count > 0;

    private static IEnumerable<string> EnumerateProfileCandidates(string root)
    {
        if (IsProjectedSimObjectCollection(root))
        {
            foreach (var candidate in EnumerateProjectedProfileCandidates(root))
                yield return candidate;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in new[] { "flight_performance.cfg", "flight_model.cfg" })
        {
            foreach (var file in SafeEnumerateFiles(root, pattern))
            {
                var dir = Directory.GetParent(file);
                if (dir is not null && seen.Add(dir.FullName))
                    yield return dir.FullName;
            }
        }
    }

    private static IEnumerable<string> EnumerateAircraftConfigCandidates(string root)
    {
        if (IsProjectedSimObjectCollection(root))
        {
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var cfg = Path.Combine(dir, "aircraft.cfg");
                if (File.Exists(cfg))
                    yield return cfg;
            }
            yield break;
        }

        foreach (var cfg in SafeEnumerateFiles(root, "aircraft.cfg"))
            yield return cfg;
    }

    private static IEnumerable<string> EnumerateProjectedProfileCandidates(string root)
    {
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            if (File.Exists(Path.Combine(dir, "flight_performance.cfg")) ||
                File.Exists(Path.Combine(dir, "flight_model.cfg")))
            {
                yield return dir;
            }
        }
    }

    private static bool IsProjectedSimObjectCollection(string root)
    {
        var leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return leaf.Equals("airplanes", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("groundvehicles", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("vehicles", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveOverrideDirectory(string? rootOverride)
    {
        if (string.IsNullOrWhiteSpace(rootOverride)) return null;

        var candidate = rootOverride;
        if (File.Exists(candidate))
        {
            var dir = Directory.GetParent(candidate);
            if (dir is not null) candidate = dir.FullName;
        }

        if (!Directory.Exists(candidate)) return null;

        // A raw .air or package hint is only valid when it resolves to a real aircraft
        // root. Traffic / placeholder packages can also live under SimObjects\Airplanes,
        // but a real aircraft package is recognised by its own aircraft.cfg directly in
        // the candidate root. A recursive search is intentionally avoided here: a helper
        // folder can be very large, and scanning every descendant to validate a directory
        // is expensive and can hang when the override is not the current aircraft root.
        if (File.Exists(Path.Combine(candidate, "aircraft.cfg")))
            return candidate;

        // Trim the candidate one level up when a path points to a descendant file or folder
        // that is not itself the root but still belongs to the aircraft package.
        for (int i = 0; i < 3; i++)
        {
            var parent = Directory.GetParent(candidate);
            if (parent is null) break;
            candidate = parent.FullName;
            if (File.Exists(Path.Combine(candidate, "aircraft.cfg")))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// True when a normalized "title =" line in the cfg contains the normalized title.
    /// The plain IndexOf above catches quoted titles like Title="Asobo 737 Max Common";
    /// this catches the unquoted variation form title = "737 Max 8 Passengers".
    /// </summary>
    private static bool ContainsTitle(string content, string normalizedTitle)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("title", StringComparison.OrdinalIgnoreCase)) continue;
            if (Normalize(line).Contains(normalizedTitle)) return true;
        }
        return false;
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private static List<string> SafeEnumerateFiles(string root, string pattern, int maxDepth = 6)
    {
        var results = new List<string>();
        try
        {
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                if (depth > maxDepth) continue;

                string[]? files = null;
                string[]? dirs = null;
                try
                {
                    files = Directory.GetFiles(dir, pattern);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { continue; }

                if (files != null) results.AddRange(files);
                if (dirs != null)
                {
                    foreach (var d in dirs)
                    {
                        if (depth < maxDepth) stack.Push((d, depth + 1));
                    }
                }
            }
        }
        catch { }
        return results;
    }
}

/// <summary>
/// Reads aircraft checklists from disk, both the documented MSFS 2024
/// SimBase.Document XML (steps/pages/blocks/checkpoints with copilot actions
/// and checkpoint libraries) and the legacy text-only format shipped by
/// many stock and Community aircraft. Legacy items are mapped onto the
/// event/simvar namespace through <see cref="ChecklistTextResolver"/>.
/// </summary>
public static class ChecklistParser
{
    public sealed class Options
    {
        public bool IncludeEmergencyGroups;
        public bool IncludeAllLevels;
        public bool RestartCompletedSteps;
    }

    public static AircraftChecklistSet ParseFromAircraft(string title, Options? options = null, string? rootOverride = null)
    {
        options ??= new Options();
        var set = new AircraftChecklistSet { AircraftTitle = title };
        var dir = AircraftPackageLocator.LocateAircraftDirectory(title, rootOverride);
        if (dir == null)
        {
            set.Notes.Add("aircraft package folder not found in the installed MSFS packages or VFS projection");
            return set;
        }

        var checklistDirs = FindChecklistFolders(dir);
        if (checklistDirs.Count == 0)
        {
            set.Notes.Add($"no Checklist folder under {dir}");
            return set;
        }

        var loc = LoadLocalization(dir);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool saw2024 = false, sawLegacy = false;

        foreach (var cdir in checklistDirs)
        {
            foreach (var file in SafeXmlFiles(cdir))
            {
                // Every file parsed here is already in the visited set, so an include that
                // points back at a checklist file is recognised as the cycle it is.
                visited.Add(file);
                try
                {
                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root == null) continue;
                    // The type attribute is compared case-insensitively: a package that
                    // writes "Checklist" as "checklist" was silently not recognised, and
                    // the aircraft then fell back to the generic flow with no indication
                    // that its own lists had been skipped.
                    var type = (string?)root.Attribute("Type") ?? "";

                    if (root.Name.LocalName == "SimBase.Document" &&
                        type.Equals("Checklist", StringComparison.OrdinalIgnoreCase))
                    {
                        Parse2024File(doc, file, loc, set, options, visited);
                        saw2024 = true;
                    }
                    else if (root.Name.LocalName == "Checklist")
                    {
                        ParseLegacyFile(doc, file, set, options);
                        sawLegacy = true;
                    }
                }
                catch (Exception ex)
                {
                    set.Notes.Add($"unreadable {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        // A Checklist folder whose files are neither format is worth a note: the package
        // ships a checklist that cannot be read, and the generic flow then silently
        // replaces it.
        if (!saw2024 && !sawLegacy && set.Notes.Count == 0)
            set.Notes.Add($"no readable checklist in {string.Join(", ", checklistDirs)} - the generic flow is used");

        set.Format = saw2024 ? ChecklistSetFormat.Msfs2024Xml : sawLegacy ? ChecklistSetFormat.LegacyXml : ChecklistSetFormat.None;
        // The root every include resolves against: set before the parse loop, because the
        // includes are resolved while it runs and an empty path made the aircraft-wide
        // branch of ResolveIncludePath dead code.
        if (set.IsLoaded && string.IsNullOrEmpty(set.SourcePath)) set.SourcePath = dir;
        foreach (var list in set.Lists)
        {
            list.Items.RemoveAll(i => i.Kind == ChecklistItemKind.Header && string.IsNullOrWhiteSpace(i.Subject));
            // Keep the library reference / descriptive ID when there is one; only
            // synthesise a positional ID for items that arrive without any.
            for (int i = 0; i < list.Items.Count; i++)
                if (string.IsNullOrEmpty(list.Items[i].Id))
                    list.Items[i].Id = $"{list.Step}[{i}]";
        }
        return set;
    }

    private static List<string> FindChecklistFolders(string aircraftDir)
    {
        var dirs = new List<string>();
        try
        {
            var stack = new Stack<string>();
            stack.Push(aircraftDir);
            while (stack.Count > 0 && dirs.Count < 8)
            {
                var d = stack.Pop();
                DirectoryInfo[] subs;
                try { subs = new DirectoryInfo(d).GetDirectories(); }
                catch { continue; }
                foreach (var s in subs)
                {
                    if (s.Name.Equals("checklist", StringComparison.OrdinalIgnoreCase)) dirs.Add(s.FullName);
                    else if (s.Name.Length > 0 && !s.Name.StartsWith("texture", StringComparison.OrdinalIgnoreCase))
                        stack.Push(s.FullName);
                }
            }
        }
        catch { }
        return dirs;
    }

    private static List<string> SafeXmlFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.xml").ToList(); }
        catch { return new List<string>(); }
    }

    private static Dictionary<string, string> LoadLocalization(string aircraftDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Ordered by preference: a package that ships de-DE.loc and en-US.loc resolves
            // in whatever order the directory listing returns, so a subject and its
            // expectation could come from different languages. English first, then the
            // languages the rest of the UI is published in, and anything else only as a
            // fallback.
            var files = Directory.GetFiles(aircraftDir, "*.loc", SearchOption.AllDirectories)
                .OrderBy(f => LanguageRank(f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                try
                {
                    foreach (var el in XDocument.Load(f).Descendants())
                    {
                        if (el.Name.LocalName != "String") continue;
                        var id = (string?)el.Attribute("ID");
                        var str = (string?)el.Attribute("String");
                        if (!string.IsNullOrEmpty(id) && str != null && !map.ContainsKey(id))
                            map[id] = str;
                    }
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Where a localisation file sits in the preference order. Lower is preferred: the
    /// file's directory name carries the language, and a neutral (language-less) file is
    /// the fallback of the fallback.
    /// </summary>
    private static int LanguageRank(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var rank = name switch
        {
            _ when name.Contains("en-US", StringComparison.OrdinalIgnoreCase) => 0,
            _ when name.Contains("en-GB", StringComparison.OrdinalIgnoreCase) => 1,
            _ when name.StartsWith("en", StringComparison.OrdinalIgnoreCase) => 2,
            _ => 3
        };
        // A language-agnostic file (e.g. "Aircraft.loc") is the least informative of all.
        return name.Any(char.IsDigit) || name.Contains('-') ? rank : rank + 10;
    }

    // ----------------------------- 2024 format -----------------------------

    private static void Parse2024File(XDocument doc, string file, Dictionary<string, string> loc,
        AircraftChecklistSet set, Options options, HashSet<string> visited)
    {
        var checklistRoot = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Checklist.Checklist");
        if (checklistRoot == null) return;
        var baseDir = Path.GetDirectoryName(file)!;
        var libraries = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var inc in checklistRoot.Elements().Where(e => e.Name.LocalName == "IncludeCheckpointLibrary"))
        {
            var name = (string?)inc.Attribute("FileName");
            if (name == null) continue;
            var libFile = ResolveIncludePath(baseDir, set.SourcePath, name);
            if (libFile == null) continue;
            try
            {
                var libDoc = XDocument.Load(libFile);
                var libRoot = libDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Checklist.CheckpointLibrary");
                if (libRoot == null)
                {
                    set.Notes.Add($"library {name}: no checkpoint library in it, so its items cannot be referenced");
                }
                foreach (var cp in libRoot?.Elements().Where(e => e.Name.LocalName == "Checkpoint") ?? Enumerable.Empty<XElement>())
                {
                    var id = (string?)cp.Attribute("Id");
                    if (id != null && !libraries.ContainsKey(id)) libraries[id] = cp;
                }
            }
            catch (Exception ex) { set.Notes.Add($"library {name}: {ex.Message}"); }
        }

        // IncludeChecklist recursion carries the files already being parsed, because a
        // self-include or a mutual include used to recurse until the stack ran out and
        // StackOverflowException, which no catch in the call chain can handle.
        foreach (var inc in checklistRoot.Elements().Where(e => e.Name.LocalName == "IncludeChecklist"))
        {
            var name = (string?)inc.Attribute("FileName");
            var incFile = ResolveIncludePath(baseDir, set.SourcePath, name);
            if (incFile == null)
            {
                if (name != null) set.Notes.Add($"include {name}: not found, so it is skipped");
                continue;
            }
            if (!visited.Add(incFile))
            {
                set.Notes.Add($"include {Path.GetFileName(incFile)}: already parsed, so the cycle is broken");
                continue;
            }
            try
            {
                Parse2024File(XDocument.Load(incFile), incFile, loc, set, options, visited);
            }
            catch (Exception ex) { set.Notes.Add($"include {name}: {ex.Message}"); }
        }

        foreach (var step in checklistRoot.Elements().Where(e => e.Name.LocalName == "Step"))
        {
            if (!Enum.TryParse<ChecklistStep>((string?)step.Attribute("ChecklistStepId"), true, out var stepId)) continue;
            foreach (var page in step.Elements().Where(e => e.Name.LocalName == "Page"))
            {
                // The default title is the step's own name: a hardcoded "PREFLIGHT GATE"
                // titled every step, so the landing step reported the gate step's name.
                var list = set.Find(stepId) ?? new ChecklistList(stepId, StepTitle(stepId));
                if (set.Find(stepId) == null) set.Lists.Add(list);
                list.Title = ResolveText(loc, (string?)page.Attribute("SubjectTT")) ?? list.Title;

                foreach (var el in page.Elements())
                {
                    if (el.Name.LocalName == "Block")
                        AddBlock(set, list, el, loc, libraries, options);
                    else if (el.Name.LocalName == "Checkpoint")
                        AddCheckpointItem(list, el, loc, libraries, options);
                }
            }
        }
    }

    /// <summary>A readable title for a step that publishes none, from the step's own name.</summary>
    private static string StepTitle(ChecklistStep step)
    {
        var name = step.ToString().Replace('_', ' ').ToLowerInvariant();
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string? ResolveIncludePath(string baseDir, string aircraftRoot, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var candidate = Path.GetFullPath(Path.Combine(baseDir, name));
        if (File.Exists(candidate)) return candidate;

        if (!string.IsNullOrEmpty(aircraftRoot))
        {
            // Aircraft-wide library paths, e.g. "html_ui/airplanes/x/Checklist/Library.xml".
            var fromRoot = Path.GetFullPath(Path.Combine(aircraftRoot, name));
            if (File.Exists(fromRoot)) return fromRoot;
        }

        var fileName = Path.GetFileName(name);
        if (!string.IsNullOrEmpty(aircraftRoot))
        {
            foreach (var f in SafeEnumerateFiles(aircraftRoot, fileName))
                return f;
        }
        foreach (var f in SafeEnumerateFiles(baseDir, fileName))
            return f;
        return null;
    }

    private static void AddBlock(AircraftChecklistSet set, ChecklistList list, XElement block,
        Dictionary<string, string> loc, Dictionary<string, XElement> libraries, Options options)
    {
        var facultative = ((string?)block.Attribute("Facultative") ?? "FALSE")
            .Equals("TRUE", StringComparison.OrdinalIgnoreCase);

        var guard = block.Elements().FirstOrDefault(e => e.Name.LocalName == "BlockCondition") is { } bc
            ? ParseCondition(bc)
            : null;

        foreach (var el in block.Elements())
        {
            if (el.Name.LocalName == "BlockCondition") continue;
            if (el.Name.LocalName == "Block")
                AddBlock(set, list, el, loc, libraries, options);
            else if (el.Name.LocalName == "Checkpoint")
            {
                var item = BuildCheckpointItem(el, loc, libraries, options);
                if (item == null) continue;
                item.Facultative = facultative;
                item.StartGuard = guard;
                list.Items.Add(item);
            }
        }
    }

    private static void AddCheckpointItem(ChecklistList list, XElement el,
        Dictionary<string, string> loc, Dictionary<string, XElement> libraries, Options options)
    {
        var item = BuildCheckpointItem(el, loc, libraries, options);
        if (item != null) list.Items.Add(item);
    }

    private static ChecklistItem? BuildCheckpointItem(XElement call,
        Dictionary<string, string> loc, Dictionary<string, XElement> libraries, Options options)
    {
        var referenceId = (string?)call.Attribute("ReferenceId");
        var source = call;
        if (referenceId != null)
        {
            if (!libraries.TryGetValue(referenceId, out var def))
            {
                return HeaderItem(referenceId, referenceId);
            }
            source = def;
        }

        var id = (string?)source.Attribute("Id") ?? referenceId ?? "";
        var lod = (string?)call.Attribute("LevelOfDetail") ?? (string?)source.Attribute("LevelOfDetail") ?? "STANDARD";
        if (!options.IncludeAllLevels && !lod.Equals("STANDARD", StringComparison.OrdinalIgnoreCase))
            return null;

        var desc = source.Elements().FirstOrDefault(e => e.Name.LocalName == "CheckpointDesc");
        var subject = ResolveText(loc, (string?)desc?.Attribute("SubjectTT")) ?? id;
        var expectation = ResolveText(loc, (string?)desc?.Attribute("ExpectationTT")) ?? "";
        var clue = source.Elements().FirstOrDefault(e => e.Name.LocalName == "Clue") is { } c
            ? ResolveText(loc, (string?)c.Attribute("Name"))
            : null;

        var item = new ChecklistItem
        {
            Id = id,
            Subject = subject,
            Expectation = expectation,
            Clue = clue,
            SourceList = referenceId != null ? $"ref:{referenceId}" : "inline",
        };

        // Every test that must hold: direct <Test> children plus the sub-tests of
        // a <Sequence>, evaluated recursively so nested sequences flatten too.
        var tests = new List<ConditionNode>();
        foreach (var t in TestElements(source))
        {
            var tv = t.Element(_name("TestValue"));
            if (tv != null)
            {
                var node = ParseCondition(tv);
                if (node != null) tests.Add(node);
            }
            var dur = t.Element(_name("Duration"));
            if (dur != null && Parsing.TryDouble((string?)dur.Attribute("Value"), out var secs))
                item.DurationSeconds = secs;
            var once = t.Element(_name("Once"));
            if (once != null && string.Equals((string?)once, "True", StringComparison.OrdinalIgnoreCase))
                item.Once = true;
        }

        item.Tests.AddRange(tests);

        // Copilot actions: declared on the checkpoint itself, or on any test
        // inside it (a sequence's actions would otherwise be dropped and the
        // checkpoint would only ever wait, never command).
        foreach (var a in source.Elements().Where(e => e.Name.LocalName == "Action"))
        {
            var action = ParseAction(a);
            if (action != null) item.Actions.Add(action);
        }
        foreach (var container in source.Elements().Where(e => e.Name.LocalName is "Test" or "Sequence"))
            foreach (var a in container.Descendants().Where(e => e.Name.LocalName == "Action"))
            {
                var action = ParseAction(a);
                if (action != null) item.Actions.Add(action);
            }

        item.Kind = item.Actions.Count > 0 ? ChecklistItemKind.Set : item.Tests.Count > 0 ? ChecklistItemKind.Check : ChecklistItemKind.Manual;
        return item;
    }

    /// <summary>
    /// All <![CDATA[<Test>]]> elements a checkpoint evaluates: its direct children
    /// plus those nested in (possibly nested) <![CDATA[<Sequence>]]> containers.
    /// </summary>
    private static IEnumerable<XElement> TestElements(XElement container)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name.LocalName == "Test") yield return child;
            else if (child.Name.LocalName == "Sequence")
                foreach (var t in TestElements(child)) yield return t;
        }
    }

    private static ChecklistAction? ParseAction(XElement a)
    {
        var action = new ChecklistAction
        {
            EventId = (string?)a.Attribute("EventID"),
            FlowEvent = (string?)a.Attribute("FlowEvent"),
            Once = string.Equals((string?)a.Attribute("Once"), "TRUE", StringComparison.OrdinalIgnoreCase),
            Copilot = string.Equals((string?)a.Attribute("Copilot"), "TRUE", StringComparison.OrdinalIgnoreCase),
        };
        if (double.TryParse((string?)a.Attribute("EventParam"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var p))
            action.EventParam = p;
        if (Enum.TryParse((string?)a.Attribute("SpecialAction"), true, out SpecialActionType special))
            action.Special = special;
        if (double.TryParse((string?)a.Attribute("Value"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            action.Value = v;

        if (!string.IsNullOrEmpty(action.EventId))
            action.Condition = Enum.TryParse((string?)a.Attribute("Condition"), true, out ActionCondition c) ? c : ActionCondition.TestValueFalse;

        if (action.EventId == null && action.Special == SpecialActionType.None)
            return null;
        return action;
    }

    private static ConditionNode? ParseCondition(XElement container)
    {
        var op = container.Element(_name("Operator"));
        if (op != null) return ParseOperator(op);
        var val = container.Element(_name("Val"));
        if (val != null)
        {
            var node = ValToNode(val);
            return new ConditionNode { Type = CheckOpType.GreaterOrEqual, Operands = { node, new SimVarNode { Constant = 0.5 } } };
        }
        return null;
    }

    private static ConditionNode ParseOperator(XElement op)
    {
        var node = new ConditionNode();
        node.Type = Enum.TryParse((string?)op.Attribute("OpType"), true, out CheckOpType t) ? t : CheckOpType.Equal;

        if (double.TryParse((string?)op.Attribute("Tolerance"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var tol))
            node.Tolerance = tol;

        foreach (var child in op.Elements())
        {
            if (child.Name.LocalName == "Val") node.Operands.Add(ValToNode(child));
            else if (child.Name.LocalName == "Operator") node.Children.Add(ParseOperator(child));
        }
        return node;
    }

    private static SimVarNode ValToNode(XElement val)
    {
        var node = new SimVarNode();
        if (double.TryParse((string?)val.Attribute("Value"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var constant))
            node.Constant = constant;
        var simVar = (string?)val.Attribute("SimVarName");
        if (simVar != null)
        {
            node.Name = simVar;
            node.Units = (string?)val.Attribute("Units") ?? "bool";
        }
        return node;
    }

    private static ChecklistItem HeaderItem(string id, string text) => new()
    {
        Id = id,
        Subject = text,
        Kind = ChecklistItemKind.Header
    };

    private static string? ResolveText(Dictionary<string, string> loc, string? tt)
    {
        if (string.IsNullOrEmpty(tt)) return null;
        var key = tt!;
        if (loc.TryGetValue(key, out var text)) return text;
        // "TT:GAME.CHECKLIST_PARKING_BRAKE" -> "PARKING BRAKE" as a last resort.
        var last = key.Split('.', StringSplitOptions.RemoveEmptyEntries).Last();
        last = last.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(last) ? key : last;
    }

    // ----------------------------- legacy format -----------------------------

    private static void ParseLegacyFile(XDocument doc, string file, AircraftChecklistSet set, Options options)
    {
        var root = doc.Root!;
        foreach (var group in root.Elements().Where(e => e.Name.LocalName == "Group"))
        {
            var groupName = (string?)group.Attribute("name") ?? "";
            if (!options.IncludeEmergencyGroups &&
                (groupName.Contains("EMERGENCY", StringComparison.OrdinalIgnoreCase) ||
                 groupName.Contains("ABNORMAL", StringComparison.OrdinalIgnoreCase)))
            {
                set.Notes.Add($"skipped group '{groupName}' (emergency/abnormal procedures are not automated)");
                continue;
            }

            foreach (var list in group.Elements().Where(e => e.Name.LocalName == "List"))
            {
                var listName = (string?)list.Attribute("name") ?? "";
                var step = MapLegacyStep(listName);
                var title = listName;
                var target = set.Find(step);
                if (target == null)
                {
                    target = new ChecklistList(step, title);
                    set.Lists.Add(target);
                }

                foreach (var item in list.Elements().Where(e => e.Name.LocalName == "Item"))
                {
                    var type = ((string?)item.Attribute("type") ?? "actionable").ToLowerInvariant();
                    var text = string.Join(" ", item.Elements()
                        .Where(e => e.Name.LocalName is "LabelText" or "ActionText" or "QuestionText" or "AnswerText" or "Text")
                        .Select(e => (e.Value ?? "").Trim().Replace("\n", " "))
                        .Where(v => v.Length > 0));

                    if (type == "title" || type == "note")
                    {
                        target.Items.Add(new ChecklistItem
                        {
                            Subject = text,
                            Expectation = "",
                            Kind = ChecklistItemKind.Header,
                            SourceList = listName
                        });
                        continue;
                    }

                    // challenge_response has two text nodes; the first is the state, second the answer.
                    var subject = text;
                    var expectation = "";
                    if (type == "challenge_response")
                    {
                        var parts = text.Split(' ', 2);
                        if (parts.Length == 2) { subject = parts[0]; expectation = parts[1]; }
                    }
                    else
                    {
                        var label = (string?)item.Element(_name("LabelText"));
                        var action = (string?)item.Element(_name("ActionText"));
                        if (label != null) subject = label.Trim();
                        if (action != null) expectation = action.Trim();
                    }

                    target.Items.Add(BuildLegacyItem(subject, expectation, listName));
                }
            }
        }
    }

    public static ChecklistItem BuildLegacyItem(string subject, string expectation, string listName)
    {
        var env = EnvironmentForText();
        var cmd = ChecklistTextResolver.Resolve(subject, expectation, env);

        var item = new ChecklistItem
        {
            Id = $"legacy:{subject}",
            Subject = subject,
            Expectation = expectation,
            SourceList = listName,
            Clue = cmd.IsManual ? cmd.ManualReason : null
        };
        item.TextBinding = cmd;

        if (cmd.VerifySimVar != null)
            item.Tests.Add(ConditionNode.Verify(cmd.VerifySimVar, cmd.VerifyUnits, cmd.ExpectOn));

        foreach (var (name, data) in cmd.Events)
        {
            item.Actions.Add(new ChecklistAction
            {
                EventId = name,
                EventParam = Math.Abs(data - 1d) < 0.001 ? null : data,
                Condition = ActionCondition.TestValueFalse
            });
        }

        if (item.Actions.Count > 0) item.Kind = ChecklistItemKind.Set;
        else if (item.Tests.Count > 0) item.Kind = ChecklistItemKind.Check;
        else item.Kind = ChecklistItemKind.Manual;

        return item;
    }

    private static ChecklistEnvironment EnvironmentForText()
    {
        // Without sim state in the parser we settle AS REQUIRED at execution
        // time in the executor; the parser just wires the default binder.
        return new ChecklistEnvironment();
    }

    public static ChecklistStep MapLegacyStep(string listName)
    {
        var n = listName.ToUpperInvariant();
        if (n.Contains("BEFORE START") || n.Contains("BEFORE STARTING ENGINES") || n.Contains("COCKPIT PREPARATION")
            || n.Contains("BEFORE ENGINE START") || n.Contains("PREFLIGHT INSPECTION") || n.Contains("STARTING ENGINES")
            || n.Contains("ENGINE START") || n.Contains("BEFORE STARTING") || n.Contains("COCKPIT SETUP")
            || n.Contains("PREPARATION") || n.Contains("PRE-START") || n.Contains("BEFORE PUSHBACK"))
            return ChecklistStep.PREFLIGHT_GATE;
        if (n.Contains("BEFORE TAXI") || n.Contains("TAXI CHECK") || n.Contains("TAXI CHECKS") || n.Contains("BEFORE TAXIING"))
            return ChecklistStep.PREFLIGHT_TAXI_OUT;
        if (n.Contains("BEFORE TAKEOFF") || n.Contains("LINEUP") || n.Contains("LINE UP") || n.Contains("RUN UP"))
            return ChecklistStep.FLIGHT_RUNWAY;
        if (n.Contains("TAKEOFF") && !n.Contains("BEFORE")) return ChecklistStep.FLIGHT_TAKEOFF;
        if (n.Contains("CLIMB")) return ChecklistStep.FLIGHT_CLIMB;
        if (n.Contains("CRUISE")) return ChecklistStep.FLIGHT_CRUISE;
        if (n.Contains("DESCENT") || n.Contains("APPROACH PREPARATION")) return ChecklistStep.FLIGHT_DESCENT;
        if (n.Contains("BEFORE LANDING") || n.Contains("FINAL") || n.Contains("BEFORE APPROACH")) return ChecklistStep.LANDING_FINAL;
        if (n.Contains("APPROACH")) return ChecklistStep.LANDING_APPROACH;
        if (n.Contains("AFTER LANDING") || n.Contains("GROUND") || n.Contains("EXIT RUNWAY") || n.Contains("LANDING ROLL"))
            return ChecklistStep.LANDING_GROUNDROLL;
        if (n.Contains("TAXI IN") || n.Contains("PARKING") || n.Contains("BEFORE PARKING") || n.Contains("ARRIVAL"))
            return ChecklistStep.LANDING_TAXI_IN;
        if (n.Contains("SHUTDOWN") || n.Contains("SHUT DOWN") || n.Contains("ENGINE SHUTDOWN") || n.Contains("PARKING CHECKS")
            || n.Contains("SECURING") || n.Contains("AFTER PARKING") || n.Contains("TERMINATION"))
            return ChecklistStep.LANDING_GATE;

        // A bare "LANDING" / "LANDING CHECKLIST" list is the rollout one: falling through
        // to the gate step made the aircraft run the stand checklist on the runway.
        if (n.Contains("LANDING")) return ChecklistStep.LANDING_GROUNDROLL;
        return ChecklistStep.PREFLIGHT_GATE;
    }

    private static List<string> SafeEnumerateFiles(string root, string name)
    {
        var results = new List<string>();
        try
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                FileInfo[]? files;
                DirectoryInfo[]? dirs;
                try
                {
                    var di = new DirectoryInfo(dir);
                    files = di.GetFiles(name);
                    dirs = di.GetDirectories();
                }
                catch { continue; }
                foreach (var f in files) results.Add(f.FullName);
                foreach (var d in dirs) stack.Push(d.FullName);
            }
        }
        catch { }
        return results;
    }

    private static XName _name(string local) => local;
}
