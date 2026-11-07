using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.SimConnect;

namespace MSFS2024AIPilot.Core.Planning;

/// <summary>
/// Picks the next destination for an automated continuous-flight leg: a random
/// airport inside the aircraft's range that has runways long enough for it.
/// </summary>
public sealed class ContinuousFlightPlanner
{
    private readonly AirportFacilityLoader _loader;
    private readonly Dictionary<string, AirportData> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double lat, double lon, double altM)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _airportIcaos = new();
    // O(1) dedup for the list feed — a List.Contains scan per item is O(n²)
    // over ~15k airports and stalls the SimConnect dispatch pump for tens of
    // seconds while the list arrives in chunks.
    private readonly HashSet<string> _airportIcaoSet = new(StringComparer.OrdinalIgnoreCase);
    private uint _listRequestId;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<bool> _listReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Maximum leg distance in nautical miles (0 = unlimited).</summary>
    public double MaxRangeNm { get; set; } = 400;
    /// <summary>Minimum leg distance in nautical miles.</summary>
    public double MinRangeNm { get; set; } = 30;
    /// <summary>Runway length required for the aircraft, meters.</summary>
    public double RequiredRunwayMeters { get; set; } = 800;
    /// <summary>Exclude the current airport from selection.</summary>
    public string? ExcludeIcao { get; set; }
    /// <summary>Operational class of the aircraft — filters destination suitability.</summary>
    public AircraftKind Kind { get; set; } = AircraftKind.Airplane;
    /// <summary>Optional diagnostic sink for pick progress.</summary>
    public Action<string>? Logger { get; set; }

    public IReadOnlyList<string> KnownAirports { get { lock (_lock) return _airportIcaos.ToList(); } }

    public ContinuousFlightPlanner(AirportFacilityLoader loader)
    {
        _loader = loader;
        loader.OnAirportLoaded += a =>
        {
            lock (_lock) _cache[a.Icao] = a;
        };
    }

    /// <summary>Requests the full airport list from the sim (async; call once after connect).</summary>
    public void RequestAirportList()
    {
        // The loader owns facility requests; airport list comes through the same client.
        // Wire the SimConnect events once.
        _loader.SimConnect.OnAirportListItem += OnListItem;
        _loader.SimConnect.OnAirportListEnd += OnListEnd;
        _listRequestId = _loader.RequestAirportList();
    }

    private void OnListItem(uint requestId, string icao, double lat, double lon, double altM)
    {
        if (requestId != _listRequestId) return;
        lock (_lock)
        {
            if (_airportIcaoSet.Add(icao))
                _airportIcaos.Add(icao);
            _positions[icao] = (lat, lon, altM);
        }
    }

    private void OnListEnd(uint requestId)
    {
        if (requestId != _listRequestId) return;
        _listReady.TrySetResult(true);
    }

    /// <summary>Waits for the airport list to arrive (with timeout).</summary>
    public async Task<bool> WaitForAirportListAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var delay = Task.Delay(timeout, ct);
        var done = await Task.WhenAny(_listReady.Task, delay);
        return done == _listReady.Task;
    }

    public AirportData? GetCachedAirport(string icao)
    {
        lock (_lock) return _cache.TryGetValue(icao, out var a) ? a : null;
    }

    /// <summary>Loads an airport's facility data and waits for it (with timeout).</summary>
    public async Task<AirportData?> LoadAirportAsync(string icao, TimeSpan timeout, CancellationToken ct = default)
    {
        AirportData? cached;
        lock (_lock) _cache.TryGetValue(icao, out cached);
        if (cached is not null) return cached;
        var tcs = new TaskCompletionSource<AirportData>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = 0;
        // Match on the facility request id, not the ICAO string — the payload
        // ident can differ from (or be missing relative to) the list ident.
        void Handler(uint id, AirportData a)
        {
            if (id == requestId) tcs.TrySetResult(a);
        }
        _loader.OnAirportLoadedForRequest += Handler;
        try
        {
            // Start the timeout BEFORE sending: RequestAirport can block on the
            // SimConnect send lock, and a stalled send must still time out.
            var delay = Task.Delay(timeout, ct);
            requestId = _loader.RequestAirport(icao);
            var done = await Task.WhenAny(tcs.Task, delay);
            if (done != tcs.Task) return null;
            var airport = await tcs.Task;
            // Cache under both the requested ident and the payload ident —
            // they can differ for small strips.
            lock (_lock) { _cache[icao] = airport; if (!string.IsNullOrEmpty(airport.Icao)) _cache[airport.Icao] = airport; }
            return airport;
        }
        finally
        {
            _loader.OnAirportLoadedForRequest -= Handler;
        }
    }

    /// <summary>
    /// Picks a random suitable destination within range of the current position.
    /// Loads candidate airports until one qualifies (runway long enough, not closed).
    /// </summary>
    public async Task<AirportData?> PickDestinationAsync(
        double lat, double lon, double aircraftRangeNm,
        TimeSpan timeout, CancellationToken ct = default,
        AircraftKind? kind = null)
    {
        var range = MaxRangeNm > 0 ? Math.Min(MaxRangeNm, aircraftRangeNm) : aircraftRangeNm;
        if (range <= 0) range = 400;

        List<string> candidates;
        Dictionary<string, (double lat, double lon, double altM)> positions;
        lock (_lock)
        {
            candidates = _airportIcaos.ToList();
            positions = new Dictionary<string, (double, double, double)>(_positions, StringComparer.OrdinalIgnoreCase);
        }
        if (candidates.Count == 0) return null;

        // Position-aware prefilter: the airport list carries coordinates, so we
        // only load facility data for airports actually inside the range band.
        var inRange = new List<(string icao, double dist)>();
        foreach (var icao in candidates)
        {
            if (!positions.TryGetValue(icao, out var p)) continue;
            var d = GeoMath.DistanceNm(lat, lon, p.lat, p.lon);
            if (d >= MinRangeNm && d <= range)
                inRange.Add((icao, d));
        }
        if (inRange.Count == 0)
        {
            // No coordinates received (older sim): fall back to blind sampling.
            inRange.AddRange(candidates.Select(c => (c, -1.0)));
        }
        Logger?.Invoke($"[Planner] {candidates.Count} airports listed, {inRange.Count} in {MinRangeNm:0}-{range:0}nm band, range cap {range:0}nm.");

        var effectiveKind = kind ?? Kind;
        var rng = new Random();
        var shuffled = inRange.OrderBy(_ => rng.Next()).ToList();
        var tried = 0;
        var loaded = 0;
        var unsuitable = 0;
        var outOfBand = 0;
        var deadline = DateTime.UtcNow + timeout;

        foreach (var (icao, knownDist) in shuffled)
        {
            if (ct.IsCancellationRequested) return null;
            if (DateTime.UtcNow > deadline) break;
            if (!string.IsNullOrEmpty(ExcludeIcao) && icao.Equals(ExcludeIcao, StringComparison.OrdinalIgnoreCase)) continue;
            if (_cache.TryGetValue(icao, out var cached))
            {
                var cd = GeoMath.DistanceNm(lat, lon, cached.ReferenceLatitude, cached.ReferenceLongitude);
                if (cd < MinRangeNm || cd > range) { outOfBand++; continue; }
                if (IsSuitable(cached, effectiveKind)) return cached;
                unsuitable++;
                continue;
            }

            tried++;
            // Cap facility loads per pick to stay responsive.
            if (tried > 60) break;
            var airport = await LoadAirportAsync(icao, TimeSpan.FromSeconds(15), ct);
            if (airport is null) continue;
            loaded++;
            var dist = knownDist >= 0 ? knownDist
                : GeoMath.DistanceNm(lat, lon, airport.ReferenceLatitude, airport.ReferenceLongitude);
            if (dist < MinRangeNm || dist > range) { outOfBand++; continue; }
            if (IsSuitable(airport, effectiveKind)) return airport;
            unsuitable++;
            if (unsuitable == 1 || unsuitable % 10 == 0)
                Logger?.Invoke($"[Planner] {icao}: loaded but unsuitable (runways={airport.Runways.Count}, closed={airport.IsClosed}, kind={effectiveKind}).");
        }
        Logger?.Invoke($"[Planner] No destination: tried {tried} loads ({loaded} ok), {unsuitable} unsuitable, {outOfBand} out-of-band.");
        return null;
    }

    /// <summary>ICAO + position of the airport nearest to a point, from the list (no facility load).</summary>
    public (string icao, double lat, double lon, double distNm)? NearestAirport(double lat, double lon)
    {
        var list = NearestAirports(lat, lon, 1);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>The <paramref name="count"/> nearest airports to a point, nearest first.</summary>
    public List<(string icao, double lat, double lon, double distNm)> NearestAirports(double lat, double lon, int count)
    {
        lock (_lock)
        {
            return _positions
                .Select(kv => (kv.Key, kv.Value.lat, kv.Value.lon,
                    GeoMath.DistanceNm(lat, lon, kv.Value.lat, kv.Value.lon)))
                .OrderBy(t => t.Item4)
                .Take(count)
                .Select(t => (t.Item1, t.Item2, t.Item3, t.Item4))
                .ToList();
        }
    }

    /// <summary>
    /// True when the airport can take the aircraft: rotorcraft need any open
    /// airport/helipad (no runway-length requirement); floatplanes need a water
    /// runway; amphibians and landplanes need a runway long enough.
    /// </summary>
    public bool IsSuitable(AirportData airport, AircraftKind? kind = null)
    {
        var k = kind ?? Kind;
        if (airport.IsClosed) return false;
        if (k == AircraftKind.Rotorcraft) return true; // airports/helipads, no runway needed
        if (k == AircraftKind.Seaplane) return airport.HasWaterRunway;
        if (!airport.HasRunways) return false;

        // Prefer airports with at least one runway meeting the required length.
        foreach (var rw in airport.Runways)
        {
            if (rw.PrimaryClosed && rw.SecondaryClosed) continue;
            // A runway record's LengthMeters is the full strip length.
            if (rw.LengthMeters >= RequiredRunwayMeters) return true;
        }
        // Accept if the best runway is at least 60% of required (short-field capable aircraft).
        var best = airport.Runways.Select(r => r.LengthMeters).DefaultIfEmpty(0).Max();
        return best >= RequiredRunwayMeters * 0.6;
    }

    /// <summary>
    /// Picks a runway at an airport: longest suitable one, favouring into-wind.
    /// <paramref name="waterOnly"/> restricts candidates to water runways
    /// (pure floatplanes); <paramref name="preferWater"/> scores water runways
    /// higher but still allows land runways (amphibians).
    /// </summary>
    public static (RunwayInfo runway, string name)? PickRunway(
        AirportData airport, double windDirectionDeg, double requiredMeters,
        bool waterOnly = false, bool preferWater = false)
    {
        RunwayInfo? best = null;
        string? bestName = null;
        double bestScore = double.MinValue;

        foreach (var rw in airport.Runways)
        {
            if (waterOnly && !rw.IsWater) continue;
            var waterBonus = preferWater && rw.IsWater ? requiredMeters * 0.5 : 0;
            foreach (var (name, closed) in new[] { (rw.PrimaryName, rw.PrimaryClosed), (rw.SecondaryName, rw.SecondaryClosed) })
            {
                if (string.IsNullOrEmpty(name) || closed) continue;
                if (rw.LengthMeters < requiredMeters * 0.8) continue;
                var threshold = rw.ThresholdFor(name);
                if (threshold is null) continue;
                // Prefer runways whose landing/takeoff heading has a headwind component.
                var windAngle = GeoMath.AngleDelta(windDirectionDeg, rw.TravelHeadingDeg(name));
                var score = rw.LengthMeters + waterBonus - Math.Abs(GeoMath.AngleDelta(windAngle, 0)) * 5;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rw;
                    bestName = name;
                }
            }
        }
        return best is not null && bestName is not null ? (best, bestName) : null;
    }
}
