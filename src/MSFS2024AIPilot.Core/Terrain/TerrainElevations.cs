using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core.Terrain;

/// <summary>
/// Samples terrain elevation (meters MSL) for route planning. Primary source is
/// the free Open-Meteo elevation API (no key, batch requests); results are
/// cached on disk so repeat legs are instant. All lookups degrade gracefully —
/// callers get <c>null</c> when data is unavailable and must apply the
/// airport-based fallback profile rather than assume terrain is flat.
/// </summary>
public sealed class TerrainElevations
{
    private const string ApiUrl = "https://api.open-meteo.com/v1/elevation";
    private const int BatchSize = 100;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly Dictionary<string, double> _cache = new(StringComparer.Ordinal);
    private bool _cacheLoaded;
    private readonly object _gate = new();

    public TerrainElevations() : this(new HttpClient { Timeout = RequestTimeout }) { }

    public TerrainElevations(HttpClient http)
    {
        _http = http;
        _cachePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "AICopilotFS", "terrain-cache.json");
    }

    /// <summary>Test hook: inject a pre-populated cache path.</summary>
    public TerrainElevations(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    /// <summary>
    /// Returns elevations in meters for each requested (lat, lon), or
    /// <c>null</c> when the API/cache could not satisfy the request. Cached
    /// points are always returned even if a batch fails.
    /// </summary>
    public async Task<IReadOnlyList<double>?> GetElevationsAsync(
        IReadOnlyList<(double lat, double lon)> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return Array.Empty<double>();
        EnsureCacheLoaded();

        var result = new double?[points.Count];
        var missing = new List<int>();
        for (var i = 0; i < points.Count; i++)
        {
            if (_cache.TryGetValue(CacheKey(points[i].lat, points[i].lon), out var cached))
                result[i] = cached;
            else
                missing.Add(i);
        }
        if (missing.Count == 0) return result.Select(v => v!.Value).ToArray();

        // Fetch in batches; on any failure keep what the cache already has.
        var fetched = new Dictionary<int, double>();
        foreach (var batch in missing.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var elevations = await FetchBatchAsync(
                batch.Select(i => points[i]).ToArray(), ct).ConfigureAwait(false);
            if (elevations is null) continue;
            for (var j = 0; j < batch.Length; j++)
            {
                var idx = batch[j];
                var elev = elevations[j];
                fetched[idx] = elev;
                var p = points[idx];
                lock (_gate) _cache[CacheKey(p.lat, p.lon)] = elev;
            }
        }
        SaveCache();

        // A point counts as known only if cached or freshly fetched.
        for (var i = 0; i < points.Count; i++)
            if (result[i] is null && fetched.TryGetValue(i, out var e)) result[i] = e;
        if (result.Any(v => v is null)) return null;
        return result.Select(v => v!.Value).ToArray();
    }

    private async Task<double[]?> FetchBatchAsync((double lat, double lon)[] points, CancellationToken ct)
    {
        try
        {
            var lats = string.Join(',', points.Select(p => p.lat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            var lons = string.Join(',', points.Select(p => p.lon.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            var url = $"{ApiUrl}?latitude={Uri.EscapeDataString(lats)}&longitude={Uri.EscapeDataString(lons)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elevation", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var values = new double[arr.GetArrayLength()];
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (i >= values.Length) break;
                if (el.ValueKind == JsonValueKind.Number) values[i] = el.GetDouble();
                i++;
            }
            return i == values.Length ? values : null;
        }
        catch
        {
            return null;
        }
    }

    private static string CacheKey(double lat, double lon)
        => $"{Round(lat):0.00},{Round(lon):0.00}";

    private static double Round(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private void EnsureCacheLoaded()
    {
        lock (_gate)
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(_cachePath)) return;
                var json = File.ReadAllText(_cachePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                if (dict is null) return;
                foreach (var kv in dict) _cache[kv.Key] = kv.Value;
            }
            catch { /* corrupt cache — start fresh */ }
        }
    }

    private void SaveCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch { /* cache write failure is non-fatal */ }
    }
}
