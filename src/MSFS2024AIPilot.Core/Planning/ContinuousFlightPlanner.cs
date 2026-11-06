using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            if (!_airportIcaos.Contains(icao, StringComparer.OrdinalIgnoreCase))
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
        if (_cache.TryGetValue(icao, out var cached)) return cached;
        var tcs = new TaskCompletionSource<AirportData>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(AirportData a)
        {
            if (a.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult(a);
        }
        _loader.OnAirportLoaded += Handler;
        try
        {
            _loader.RequestAirport(icao);
            var delay = Task.Delay(timeout, ct);
            var done = await Task.WhenAny(tcs.Task, delay);
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            _loader.OnAirportLoaded -= Handler;
        }
    }

    /// <summary>
    /// Picks a random suitable destination within range of the current position.
    /// Loads candidate airports until one qualifies (runway long enough, not closed).
    /// </summary>
    public async Task<AirportData?> PickDestinationAsync(
        double lat, double lon, double aircraftRangeNm,
        TimeSpan timeout, CancellationToken ct = default)
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

        var rng = new Random();
        var shuffled = inRange.OrderBy(_ => rng.Next()).ToList();
        var tried = 0;
        var deadline = DateTime.UtcNow + timeout;

        foreach (var (icao, knownDist) in shuffled)
        {
            if (ct.IsCancellationRequested) return null;
            if (DateTime.UtcNow > deadline) break;
            if (!string.IsNullOrEmpty(ExcludeIcao) && icao.Equals(ExcludeIcao, StringComparison.OrdinalIgnoreCase)) continue;
            if (_cache.TryGetValue(icao, out var cached))
            {
                var cd = GeoMath.DistanceNm(lat, lon, cached.ReferenceLatitude, cached.ReferenceLongitude);
                if (cd < MinRangeNm || cd > range) continue;
                if (IsSuitable(cached)) return cached;
                continue;
            }

            tried++;
            // Cap facility loads per pick to stay responsive.
            if (tried > 60) break;
            var airport = await LoadAirportAsync(icao, TimeSpan.FromSeconds(15), ct);
            if (airport is null) continue;
            var dist = knownDist >= 0 ? knownDist
                : GeoMath.DistanceNm(lat, lon, airport.ReferenceLatitude, airport.ReferenceLongitude);
            if (dist < MinRangeNm || dist > range) continue;
            if (IsSuitable(airport)) return airport;
        }
        return null;
    }

    /// <summary>ICAO + position of the airport nearest to a point, from the list (no facility load).</summary>
    public (string icao, double lat, double lon, double distNm)? NearestAirport(double lat, double lon)
    {
        lock (_lock)
        {
            string? best = null;
            double bestDist = double.MaxValue;
            foreach (var (icao, p) in _positions)
            {
                var d = GeoMath.DistanceNm(lat, lon, p.lat, p.lon);
                if (d < bestDist) { bestDist = d; best = icao; }
            }
            if (best is null) return null;
            var pos = _positions[best];
            return (best, pos.lat, pos.lon, bestDist);
        }
    }

    /// <summary>True when the airport can take the aircraft: open, with a runway long enough.</summary>
    public bool IsSuitable(AirportData airport)
    {
        if (airport.IsClosed || airport.Runways.Count == 0) return false;
        // Prefer airports with at least one runway meeting the required length.
        foreach (var rw in airport.Runways)
        {
            if (rw.PrimaryClosed && rw.SecondaryClosed) continue;
            var longest = Math.Max(rw.LengthMeters, 0);
            // A runway record's LengthMeters is the full strip length.
            if (longest >= RequiredRunwayMeters) return true;
        }
        // Accept if the best runway is at least 60% of required (short-field capable aircraft).
        var best = airport.Runways.Select(r => r.LengthMeters).DefaultIfEmpty(0).Max();
        return best >= RequiredRunwayMeters * 0.6;
    }

    /// <summary>Picks a runway at an airport: longest suitable one, favouring into-wind.</summary>
    public static (RunwayInfo runway, string name)? PickRunway(AirportData airport, double windDirectionDeg, double requiredMeters)
    {
        RunwayInfo? best = null;
        string? bestName = null;
        double bestScore = double.MinValue;

        foreach (var rw in airport.Runways)
        {
            foreach (var (name, closed) in new[] { (rw.PrimaryName, rw.PrimaryClosed), (rw.SecondaryName, rw.SecondaryClosed) })
            {
                if (string.IsNullOrEmpty(name) || closed) continue;
                if (rw.LengthMeters < requiredMeters * 0.8) continue;
                var threshold = rw.ThresholdFor(name);
                if (threshold is null) continue;
                // Prefer runways whose landing/takeoff heading has a headwind component.
                var windAngle = GeoMath.AngleDelta(windDirectionDeg, rw.HeadingDegTrue);
                var score = rw.LengthMeters - Math.Abs(GeoMath.AngleDelta(windAngle, 0)) * 5;
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
