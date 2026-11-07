using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MSFS2024AIPilot.Core.Aircraft;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Fetches aircraft checklists from flightchecklist.app when the simobject
/// package ships no readable checklist XML (encrypted VFS packages, third-party
/// aircraft). The site renders every checklist server-side: the aircraft index
/// at /msfs and each aircraft page at /msfs/{slug} embed the full phase/step
/// tree inside the Next.js RSC payload, which is extracted here without any
/// API key. Results are cached on disk per aircraft slug.
/// </summary>
public static class FlightChecklistProvider
{
    private const string BaseUrl = "https://flightchecklist.app";
    private const string IndexPath = "/msfs";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim IndexLock = new(1, 1);
    private static List<(string Name, string Slug)>? _index;
    private static DateTime _indexFetchedUtc = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AICopilotFS/1.0 (+https://github.com/Plane14/AICopilotFS-main)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return http;
    }

    /// <summary>Everything one aircraft page can provide: checklist set + spec sheet.</summary>
    public sealed class FlightChecklistBundle
    {
        public AircraftChecklistSet? Checklists;
        public AircraftPerformance? Performance;
        /// <summary>Raw spec strings, kept for the disk cache.</summary>
        public CachedSpecs? Specs;
    }

    /// <summary>
    /// Attempts to load checklist + performance data for <paramref name="aircraftTitle"/>.
    /// Returns null when the aircraft is not in the site's library or the fetch fails.
    /// </summary>
    public static async Task<FlightChecklistBundle?> TryLoadBundleAsync(string aircraftTitle, string? icaoDesignator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(aircraftTitle)) return null;

        var slug = await MatchSlugAsync(aircraftTitle, icaoDesignator, ct);
        if (slug is null) return null;

        var cached = await LoadCachedAsync(slug, ct);
        if (cached is not null)
        {
            // Cached files store the site slug as the title; restore the real one.
            if (cached.Checklists is not null) cached.Checklists.AircraftTitle = aircraftTitle;
            return cached;
        }

        try
        {
            var html = await Http.GetStringAsync($"{BaseUrl}/msfs/{slug}", ct);
            var stream = ExtractRscStream(html);
            var set = ParsePageStream(stream, slug);
            var (perf, rawSpecs) = ParseSpecs(stream);
            var bundle = new FlightChecklistBundle { Specs = rawSpecs };
            if (set is not null && set.IsLoaded)
            {
                set.AircraftTitle = aircraftTitle;
                set.SourcePath = $"{BaseUrl}/msfs/{slug}";
                set.Format = ChecklistSetFormat.WebJson;
                bundle.Checklists = set;
            }
            if (perf is not null) bundle.Performance = perf;
            if (bundle.Checklists is null && bundle.Performance is null) return null;
            SaveCached(slug, bundle);
            return bundle;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline or site changed: fall through to the generic checklists.
        }
        return null;
    }

    /// <summary>
    /// Attempts to load a checklist set for <paramref name="aircraftTitle"/>.
    /// Returns null when the aircraft is not in the site's library or the fetch fails.
    /// </summary>
    public static async Task<AircraftChecklistSet?> TryLoadAsync(string aircraftTitle, string? icaoDesignator, CancellationToken ct = default)
        => (await TryLoadBundleAsync(aircraftTitle, icaoDesignator, ct))?.Checklists;

    // ---------------- aircraft index ----------------

    private static async Task<string?> MatchSlugAsync(string title, string? icao, CancellationToken ct)
    {
        var index = await GetIndexAsync(ct);
        if (index.Count == 0) return null;

        var best = MatchEntry(index, title);
        if (best is null && !string.IsNullOrWhiteSpace(icao))
        {
            // Second pass: match on tokens shared with the ICAO designator
            // (e.g. title "Boeing C-17" → designator "C17" won't appear verbatim,
            // but numeric model tokens still help).
            best = index
                .Select(e => (Entry: e, Score: Score(Normalize(e.Name), Normalize(icao!))))
                .Where(x => x.Score >= 0.5)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Entry)
                .FirstOrDefault();
        }
        return best?.Slug;
    }

    private static (string Name, string Slug)? MatchEntry(List<(string Name, string Slug)> index, string title)
    {
        var t = Normalize(title);
        if (t.Length == 0) return null;
        return index
            .Select(e => (Entry: e, Score: Score(Normalize(e.Name), t)))
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .Select(x => x.Entry)
            .FirstOrDefault();
    }

    /// Token-overlap score in [0,1]; numeric tokens count double because they
    /// carry the model identity ("172", "737", "A320").
    private static double Score(string siteName, string query)
    {
        if (siteName.Length == 0 || query.Length == 0) return 0;
        if (siteName.Contains(query, StringComparison.Ordinal) || query.Contains(siteName, StringComparison.Ordinal))
            return 0.95;

        var a = Tokens(siteName);
        var b = Tokens(query);
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Intersect(b).ToList();
        var union = a.Union(b).ToList();
        if (union.Count == 0) return 0;
        var jaccard = (double)inter.Count / union.Count;
        var containment = (double)inter.Count / Math.Min(a.Count, b.Count);
        return Math.Max(jaccard, containment * 0.9);
    }

    private static HashSet<string> Tokens(string s)
        => new(s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('-', '(', ')', ',')), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static async Task<List<(string Name, string Slug)>> GetIndexAsync(CancellationToken ct)
    {
        if (_index is not null && DateTime.UtcNow - _indexFetchedUtc < TimeSpan.FromDays(7))
            return _index;

        await IndexLock.WaitAsync(ct);
        try
        {
            if (_index is not null && DateTime.UtcNow - _indexFetchedUtc < TimeSpan.FromDays(7))
                return _index;

            var cached = LoadCachedIndex();
            if (cached is not null)
            {
                _index = cached;
                _indexFetchedUtc = DateTime.UtcNow;
                return _index;
            }

            var index = new List<(string Name, string Slug)>();
            try
            {
                var html = await Http.GetStringAsync($"{BaseUrl}{IndexPath}", ct);
                // ItemList JSON-LD: {"name":"X Startup Checklist","url":".../msfs/slug"}
                foreach (Match m in Regex.Matches(html,
                    @"""name"":""([^""]+?) Startup Checklist"",""url"":""https://flightchecklist\.app/msfs/([a-z0-9\-]+)"""))
                {
                    index.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value));
                }
                if (index.Count == 0)
                {
                    // Fallback: card anchors followed by their <h2> name.
                    foreach (Match m in Regex.Matches(html,
                        @"href=""/msfs/([a-z0-9\-]+)""[\s\S]{0,4000}?<h2[^>]*>([^<]+)</h2>"))
                    {
                        index.Add((m.Groups[2].Value.Trim(), m.Groups[1].Value));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Offline: keep whatever we had.
            }

            if (index.Count > 0)
            {
                _index = index;
                _indexFetchedUtc = DateTime.UtcNow;
                SaveCachedIndex(index);
            }
            return _index ?? new List<(string Name, string Slug)>();
        }
        finally { IndexLock.Release(); }
    }

    // ---------------- page parsing ----------------

    /// <summary>
    /// Extracts the phases/steps tree embedded in the page's Next.js RSC
    /// payload: self.__next_f.push([n,"..."]) chunks, concatenated and
    /// unescaped, contain "phases":[{name,steps[{id,action,expected,note}]}].
    /// </summary>
    public static AircraftChecklistSet? ParsePage(string html, string slug)
        => ParsePageStream(ExtractRscStream(html), slug);

    private static AircraftChecklistSet? ParsePageStream(string stream, string slug)
    {
        var phasesJson = ExtractPhasesArray(stream);
        if (phasesJson is null) return null;

        using var doc = JsonDocument.Parse(phasesJson);
        var set = new AircraftChecklistSet { AircraftTitle = slug, Format = ChecklistSetFormat.WebJson };
        foreach (var phase in doc.RootElement.EnumerateArray())
        {
            var phaseName = phase.GetProperty("name").GetString() ?? "";
            if (!phase.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                continue;

            var step = ChecklistParser.MapLegacyStep(phaseName);
            var list = set.Find(step) ?? new ChecklistList(step, phaseName);
            if (set.Find(step) is null) set.Lists.Add(list);

            foreach (var st in steps.EnumerateArray())
            {
                var action = st.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                var expected = st.TryGetProperty("expected", out var e) ? e.GetString() ?? "" : "";
                if (action.Length == 0) continue;
                list.Items.Add(ChecklistParser.BuildLegacyItem(action, expected, phaseName));
            }
        }
        return set.IsLoaded ? set : null;
    }

    internal static string ExtractRscStream(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        foreach (Match m in Regex.Matches(html, @"self\.__next_f\.push\(\[\d+,""((?:[^""\\]|\\.)*)""\]\)"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"");
                if (s is not null) sb.Append(s);
            }
            catch (JsonException) { /* skip malformed chunk */ }
        }
        return sb.ToString();
    }

    /// Locates "phases":[ ... ] in the unescaped RSC stream and returns the
    /// balanced JSON array text.
    private static string? ExtractPhasesArray(string stream)
    {
        var key = stream.IndexOf("\"phases\":[", StringComparison.Ordinal);
        if (key < 0) return null;
        var start = stream.IndexOf('[', key);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        for (var i = start; i < stream.Length; i++)
        {
            var c = stream[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
                return stream.Substring(start, i - start + 1);
        }
        return null;
    }

    // ---------------- spec sheet ("Quick Reference" tab data) ----------------

    /// <summary>
    /// Parses the aircraft's spec block embedded in the RSC stream:
    /// {"specs":{"engines":"1x Lycoming IO-360-L2A (180 hp)","maxSpeed":"233 km/h
    /// (126 kts)","range":"1,289 km (696 nmi)","ceiling":"13,500 ft",
    /// "weight":"1,157 kg (MTOW)"}, ... "type":"Single Engine Piston"}.
    /// Only fields the simobject cfg cannot provide (encrypted packages) are
    /// filled; the aircraft title is never taken from here.
    /// </summary>
    internal static (AircraftPerformance? Perf, CachedSpecs? Raw) ParseSpecs(string stream)
    {
        string? engines = null, maxSpeed = null, range = null, ceiling = null, weight = null;
        var specsJson = ExtractJsonObject(stream, "\"specs\":");
        if (specsJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(specsJson);
                var root = doc.RootElement;
                engines = GetStr(root, "engines");
                maxSpeed = GetStr(root, "maxSpeed");
                range = GetStr(root, "range");
                ceiling = GetStr(root, "ceiling");
                weight = GetStr(root, "weight");
            }
            catch (JsonException) { }
        }
        var type = ExtractType(stream);
        var raw = new CachedSpecs(engines, maxSpeed, range, ceiling, weight, type);
        var hasAny = new[] { engines, maxSpeed, range, ceiling, weight, type }.Any(s => !string.IsNullOrEmpty(s));
        return (hasAny ? BuildPerformance(engines, maxSpeed, range, ceiling, weight, type) : null, raw);
    }

    /// <summary>Builds performance fields from the raw spec strings (shared by live parse and disk cache).</summary>
    internal static AircraftPerformance? BuildPerformance(string? engines, string? maxSpeed, string? range, string? ceiling, string? weight, string? type)
    {
        var perf = new AircraftPerformance();
        var found = false;

        if (!string.IsNullOrEmpty(type))
        {
            perf.EngineType = EngineTypeFrom(type);
            found = true;
        }

        if (!string.IsNullOrEmpty(engines))
        {
            var m = Regex.Match(engines, @"^(\d+)\s*[x×]", RegexOptions.IgnoreCase);
            if (m.Success) { perf.EngineCount = int.Parse(m.Groups[1].Value); found = true; }
            var rating = Regex.Match(engines, @"\(([^)]*(?:hp|lbf|kn)[^)]*)\)", RegexOptions.IgnoreCase);
            if (rating.Success)
            {
                var t = ParsePowerToLbs(rating.Groups[1].Value);
                if (t > 0) { perf.MaxThrustPerEngine = t; found = true; }
            }
        }

        if (!string.IsNullOrEmpty(maxSpeed))
        {
            var kts = ParenthesizedNumber(maxSpeed, "kts", "km/h", "mph");
            if (kts > 0)
            {
                perf.Vno = kts;                       // level max speed ≈ never-exceed-ish
                perf.CruisePerf.DefaultCruiseSpeed = kts * 0.85;
                found = true;
            }
        }

        if (!string.IsNullOrEmpty(range))
        {
            // "1,289 km (696 nmi)" → prefer the parenthesized nmi figure.
            var nm = ParseRangeNm(range);
            if (nm > 0) { perf.MaxRangeNm = nm; found = true; }
        }

        if (!string.IsNullOrEmpty(ceiling))
        {
            var ft = FirstNumber(ceiling, "ft", "m");
            if (ft > 0) { perf.CruisePerf.MaxCruiseAltitude = ft; found = true; }
        }

        if (!string.IsNullOrEmpty(weight))
        {
            var lbs = ParseWeightToLbs(weight);
            if (lbs > 0) { perf.MaxGrossWeight = lbs; perf.MaxTakeoffWeight = lbs; found = true; }
        }

        return found ? perf : null;
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Brace-matches the JSON object following a "key": marker.</summary>
    private static string? ExtractJsonObject(string stream, string marker)
    {
        var key = stream.IndexOf(marker, StringComparison.Ordinal);
        if (key < 0) return null;
        var start = stream.IndexOf('{', key + marker.Length);
        if (start < 0) return null;
        var depth = 0; var inString = false;
        for (var i = start; i < stream.Length; i++)
        {
            var c = stream[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return stream.Substring(start, i - start + 1);
        }
        return null;
    }

    /// <summary>The page carries one aircraft "type" like "Single Engine Piston".</summary>
    private static string? ExtractType(string stream)
    {
        foreach (Match m in Regex.Matches(stream, @"""type"":""([^""]+)"""))
        {
            var v = m.Groups[1].Value;
            if (v.Contains("Piston") || v.Contains("Turboprop") || v.Contains("Jet") ||
                v.Contains("Helicopter") || v.Contains("Rotary") || v.Contains("Glider"))
                return v;
        }
        return null;
    }

    private static string EngineTypeFrom(string type)
    {
        var t = type.ToUpperInvariant();
        if (t.Contains("PISTON")) return "PISTON";
        if (t.Contains("TURBOPROP")) return "TURBOPROP";
        if (t.Contains("JET")) return "JET";
        if (t.Contains("HELICOPTER") || t.Contains("ROTARY")) return "ROTOR";
        return "";
    }

    /// <summary>"(180 hp)" → ~lb thrust; "(27,000 lbf)" → lbf directly.</summary>
    private static double ParsePowerToLbs(string rating)
    {
        var m = Regex.Match(rating, @"([\d,\.]+)\s*(lbf|hp|kn)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("hp", StringComparison.OrdinalIgnoreCase) ? v * 0.9 : v;
    }

    /// <summary>First parenthesized number, converted to the requested unit.</summary>
    private static double ParenthesizedNumber(string s, params string[] units)
    {
        var m = Regex.Match(s, @"\(([^)]*)\)");
        if (!m.Success) return 0;
        return FirstNumber(m.Groups[1].Value, units);
    }

    /// <summary>"1,289 km (696 nmi)" style: first "N unit" occurrence → base unit.</summary>
    private static double FirstNumber(string s, params string[] units)
    {
        foreach (var unit in units)
        {
            var m = Regex.Match(s, @"([\d,\.]+)\s*" + Regex.Escape(unit), RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
                // Convert to the canonical unit of the field being filled.
                if (unit.Equals("km/h", StringComparison.OrdinalIgnoreCase)) return v * 0.539957;   // → kts
                if (unit.Equals("km", StringComparison.OrdinalIgnoreCase)) return v * 0.539957;    // → nmi
                if (unit.Equals("mph", StringComparison.OrdinalIgnoreCase)) return v * 0.868976;   // → kts
                if (unit.Equals("m", StringComparison.OrdinalIgnoreCase)) return v * 3.28084;      // → ft
                if (unit.Equals("kg", StringComparison.OrdinalIgnoreCase)) return v * 2.20462;     // → lb
                if (unit.Equals("t", StringComparison.OrdinalIgnoreCase)) return v * 2204.62;      // → lb
                return v; // already canonical (kts, nmi, ft, lb)
            }
        }
        return 0;
    }

    /// <summary>"1,289 km (696 nmi)" / "620 nm" → nautical miles.</summary>
    private static double ParseRangeNm(string s)
    {
        var nmi = Regex.Match(s, @"([\d,\.]+)\s*(?:nmi|nm|knots?\s+range)\b", RegexOptions.IgnoreCase);
        if (nmi.Success && !s.Contains("km", StringComparison.OrdinalIgnoreCase))
            return double.Parse(nmi.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        var paren = Regex.Match(s, @"\(([^)]*nmi[^)]*)\)", RegexOptions.IgnoreCase);
        if (paren.Success)
        {
            var v = Regex.Match(paren.Groups[1].Value, @"([\d,\.]+)");
            if (v.Success)
                return double.Parse(v.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        }
        var km = Regex.Match(s, @"([\d,\.]+)\s*km\b", RegexOptions.IgnoreCase);
        if (km.Success)
            return double.Parse(km.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture) * 0.539957;
        return 0;
    }

    /// <summary>"1,157 kg (MTOW)" / "2,550 lb" → pounds.</summary>
    private static double ParseWeightToLbs(string s)
    {
        var kg = Regex.Match(s, @"([\d,\.]+)\s*kg\b", RegexOptions.IgnoreCase);
        if (kg.Success)
            return double.Parse(kg.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture) * 2.20462;
        var lb = Regex.Match(s, @"([\d,\.]+)\s*(?:lb|lbs)\b", RegexOptions.IgnoreCase);
        if (lb.Success)
            return double.Parse(lb.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        return 0;
    }

    // ---------------- disk cache ----------------

    private static string CacheDir
    {
        get
        {
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "AICopilotFS", "flightchecklists");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SlugCachePath(string slug) => Path.Combine(CacheDir, $"{slug}.json");
    private static string IndexCachePath => Path.Combine(CacheDir, "_index.json");

    /// <summary>flightchecklists/ folder shipped next to the exe; populated at build time.</summary>
    private static string BundledDir => Path.Combine(AppContext.BaseDirectory, "flightchecklists");

    /// <summary>Public aircraft index (name + slug) for tooling and matching.</summary>
    public static async Task<IReadOnlyList<(string Name, string Slug)>> GetAircraftIndexAsync(CancellationToken ct = default)
        => await GetIndexAsync(ct);

    /// <summary>Fetches and parses one aircraft page by slug; used by the bundle generator.</summary>
    public static async Task<FlightChecklistBundle?> FetchBySlugAsync(string slug, CancellationToken ct = default)
    {
        var cached = await LoadCachedAsync(slug, ct);
        if (cached is not null) return cached;
        try
        {
            var html = await Http.GetStringAsync($"{BaseUrl}/msfs/{slug}", ct);
            var stream = ExtractRscStream(html);
            var set = ParsePageStream(stream, slug);
            var (perf, rawSpecs) = ParseSpecs(stream);
            var bundle = new FlightChecklistBundle { Specs = rawSpecs };
            if (set is not null && set.IsLoaded)
            {
                set.Format = ChecklistSetFormat.WebJson;
                bundle.Checklists = set;
            }
            if (perf is not null) bundle.Performance = perf;
            if (bundle.Checklists is null && bundle.Performance is null) return null;
            SaveCached(slug, bundle);
            return bundle;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<FlightChecklistBundle?> LoadCachedAsync(string slug, CancellationToken ct)
    {
        // Bundled copy ships inside the app directory and wins over the
        // per-user cache so the app works fully offline.
        var bundled = Path.Combine(BundledDir, $"{slug}.json");
        if (File.Exists(bundled))
        {
            var b = await ReadBundleFileAsync(bundled, ct);
            if (b is not null) return b;
        }

        var path = SlugCachePath(slug);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var cached = JsonSerializer.Deserialize<CachedSet>(json, CacheOptions);
            if (cached is null || cached.V != 2) return null;
            return RebuildBundle(cached);
        }
        catch { return null; }
    }

    private static async Task<FlightChecklistBundle?> ReadBundleFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var cached = JsonSerializer.Deserialize<CachedSet>(json, CacheOptions);
            if (cached is null || cached.V != 2) return null;
            return RebuildBundle(cached);
        }
        catch { return null; }
    }

    private static FlightChecklistBundle? RebuildBundle(CachedSet cached)
    {
        var bundle = new FlightChecklistBundle { Specs = cached.Specs };
        var set = Rebuild(cached);
        if (set is not null) bundle.Checklists = set;
        if (cached.Specs is not null)
            bundle.Performance = BuildPerformance(
                cached.Specs.Engines, cached.Specs.MaxSpeed, cached.Specs.Range,
                cached.Specs.Ceiling, cached.Specs.Weight, cached.Specs.Type);
        return bundle.Checklists is null && bundle.Performance is null ? null : bundle;
    }

    private static void SaveCached(string slug, FlightChecklistBundle bundle)
    {
        try
        {
            List<CachedList>? steps = null;
            if (bundle.Checklists is not null)
            {
                steps = bundle.Checklists.Lists.Select(l => new CachedList(
                    l.Step.ToString(), l.Title,
                    l.Items.Where(i => i.Kind != ChecklistItemKind.Header)
                        .Select(i => new CachedItem(i.Subject, i.Expectation, l.Title)).ToList())).ToList();
            }

            var payload = new CachedSet(2,
                bundle.Checklists?.AircraftTitle ?? "",
                bundle.Checklists?.SourcePath ?? "",
                steps ?? new List<CachedList>(),
                bundle.Specs);
            File.WriteAllText(SlugCachePath(slug), JsonSerializer.Serialize(payload, CacheOptions));
        }
        catch { /* cache is best-effort */ }
    }

    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = false };

    private sealed record CachedSet(int V, string AircraftTitle, string SourcePath, List<CachedList> Lists, CachedSpecs? Specs);
    private sealed record CachedList(string Step, string Title, List<CachedItem> Items);
    private sealed record CachedItem(string Subject, string Expectation, string SourceList);
    /// <summary>Raw spec strings from the site, kept for the disk cache.</summary>
    public sealed record CachedSpecs(string? Engines, string? MaxSpeed, string? Range, string? Ceiling, string? Weight, string? Type);

    private static AircraftChecklistSet? Rebuild(CachedSet cached)
    {
        var set = new AircraftChecklistSet
        {
            AircraftTitle = cached.AircraftTitle,
            SourcePath = cached.SourcePath,
            Format = ChecklistSetFormat.WebJson,
        };
        foreach (var l in cached.Lists)
        {
            if (!Enum.TryParse<ChecklistStep>(l.Step, out var step)) continue;
            var list = new ChecklistList(step, l.Title);
            foreach (var item in l.Items)
                list.Items.Add(ChecklistParser.BuildLegacyItem(item.Subject, item.Expectation, l.Title));
            set.Lists.Add(list);
        }
        return set.IsLoaded ? set : null;
    }

    private static List<(string Name, string Slug)>? LoadCachedIndex()
    {
        // Bundled index (shipped with the app) is checked before the user cache
        // so slug matching works fully offline.
        var bundled = Path.Combine(BundledDir, "_index.json");
        foreach (var path in new[] { bundled, IndexCachePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<IndexEntry>>(json, CacheOptions)
                    ?.Select(e => (e.Name, e.Slug)).ToList();
                if (list is { Count: > 0 }) return list;
            }
            catch { }
        }
        return null;
    }

    private static void SaveCachedIndex(List<(string Name, string Slug)> index)
    {
        try
        {
            var entries = index.Select(e => new IndexEntry(e.Name, e.Slug)).ToList();
            File.WriteAllText(IndexCachePath, JsonSerializer.Serialize(entries, CacheOptions));
        }
        catch { }
    }

    private sealed record IndexEntry(string Name, string Slug);
}
