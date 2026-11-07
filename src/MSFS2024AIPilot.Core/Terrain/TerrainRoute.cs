using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Geo;

namespace MSFS2024AIPilot.Core.Terrain;

/// <summary>One terrain sample ordered along the route.</summary>
public readonly record struct TerrainSample(double Latitude, double Longitude, double ElevationMeters);

/// <summary>
/// Terrain profile along a route: per-sample terrain elevation and the
/// minimum safe altitude above it (MSFS feet MSL). Mirrors the behaviour
/// described in aerotto.com's guide — 1,000 ft above terrain normally,
/// 2,000 ft over high/mountainous terrain — with an airport-based fallback
/// (3,000 ft above the highest airport) when elevation data is unavailable.
/// </summary>
public sealed class TerrainProfile
{
    /// <summary>Terrain above this MSL altitude is treated as mountainous.</summary>
    public const double MountainousThresholdFt = 4500;

    public const double NormalMarginFt = 1000;
    public const double MountainMarginFt = 2000;
    public const double FallbackMarginFt = 3000;

    /// <summary>Spacing between terrain samples along each leg, nautical miles.</summary>
    public const double SampleSpacingNm = 8.0;

    private readonly TerrainSample[] _samples;
    private readonly double[] _requiredFt;
    private readonly double[] _cumDistNm;

    public IReadOnlyList<TerrainSample> Samples => _samples;
    public bool Mountainous { get; }
    public bool IsFallback { get; }

    /// <summary>Highest required (terrain + margin) altitude on the route.</summary>
    public double MaxRequiredAltitudeFt { get; }

    private TerrainProfile(TerrainSample[] samples, double[] requiredFt, double[] cumDistNm, bool mountainous, bool isFallback)
    {
        _samples = samples;
        _requiredFt = requiredFt;
        _cumDistNm = cumDistNm;
        Mountainous = mountainous;
        IsFallback = isFallback;
        MaxRequiredAltitudeFt = requiredFt.Length == 0 ? 0 : requiredFt.Max();
    }

    public static double MarginForElevationFt(double elevationFt) =>
        elevationFt >= MountainousThresholdFt ? MountainMarginFt : NormalMarginFt;

    /// <summary>Required altitude for one terrain sample.</summary>
    public static double RequiredFtForElevationMeters(double elevM)
    {
        var elevFt = elevM * GeoMath.FeetPerMeter;
        return elevFt + MarginForElevationFt(elevFt);
    }

    /// <summary>
    /// Points to query for terrain elevation along the route — spaced
    /// <paramref name="spacingNm"/> apart on each leg, endpoints included,
    /// consecutive near-duplicates removed.
    /// </summary>
    public static List<(double lat, double lon)> SampleRoutePoints(
        IReadOnlyList<(double lat, double lon)> route, double spacingNm = SampleSpacingNm)
    {
        var pts = new List<(double lat, double lon)>();
        if (route.Count < 2) return pts;
        for (var i = 0; i < route.Count - 1; i++)
        {
            var a = route[i];
            var b = route[i + 1];
            var legNm = GeoMath.DistanceNm(a.lat, a.lon, b.lat, b.lon);
            if (legNm < 0.01) continue;
            var steps = Math.Max(1, (int)Math.Ceiling(legNm / spacingNm));
            var bearing = GeoMath.BearingDeg(a.lat, a.lon, b.lat, b.lon);
            for (var k = 0; k <= steps; k++)
            {
                var d = legNm * k / steps;
                pts.Add(GeoMath.DestinationNm(a.lat, a.lon, bearing, d));
            }
        }
        // Dedup points closer than ~0.5 NM (leg joins).
        var dedup = new List<(double lat, double lon)> { pts[0] };
        foreach (var p in pts.Skip(1))
            if (GeoMath.DistanceNm(dedup[^1].lat, dedup[^1].lon, p.lat, p.lon) > 0.5)
                dedup.Add(p);
        return dedup;
    }

    /// <summary>
    /// Builds the profile from ordered samples whose elevations were
    /// resolved for exactly these points (index-aligned).
    /// </summary>
    public static TerrainProfile? Build(IReadOnlyList<TerrainSample> samples, bool isFallback = false)
    {
        if (samples.Count < 2) return null;
        var arr = samples.ToArray();
        var req = arr.Select(s => RequiredFtForElevationMeters(s.ElevationMeters)).ToArray();
        var cum = new double[arr.Length];
        for (var i = 1; i < arr.Length; i++)
            cum[i] = cum[i - 1] + GeoMath.DistanceNm(arr[i - 1].Latitude, arr[i - 1].Longitude, arr[i].Latitude, arr[i].Longitude);
        var mountainous = arr.Any(s => s.ElevationMeters * GeoMath.FeetPerMeter >= MountainousThresholdFt);
        return new TerrainProfile(arr, req, cum, mountainous, isFallback);
    }

    /// <summary>
    /// Offline fallback (Aerotto-style): required altitude is the highest
    /// airport elevation + 3,000 ft everywhere along the route.
    /// </summary>
    public static TerrainProfile Fallback(double highestAirportElevM, IReadOnlyList<(double lat, double lon)> route)
    {
        var floorFt = highestAirportElevM * GeoMath.FeetPerMeter + FallbackMarginFt;
        var pts = route.Count >= 2
            ? route
            : new List<(double lat, double lon)> { (0.0, 0.0), (0.0, 0.001) };
        var arr = pts.Select(p => new TerrainSample(p.lat, p.lon, highestAirportElevM)).ToArray();
        var req = arr.Select(_ => floorFt).ToArray();
        var cum = new double[arr.Length];
        for (var i = 1; i < arr.Length; i++)
            cum[i] = cum[i - 1] + GeoMath.DistanceNm(arr[i - 1].Latitude, arr[i - 1].Longitude, arr[i].Latitude, arr[i].Longitude);
        return new TerrainProfile(arr, req, cum, false, true);
    }

    /// <summary>
    /// Highest required altitude (MSL ft) among samples lying within
    /// <paramref name="aheadNm"/> nautical miles ahead of the aircraft's
    /// current position along the route. Returns 0 when the position is
    /// far off route.
    /// </summary>
    public double RequiredAheadFt(double lat, double lon, double aheadNm)
    {
        if (_samples.Length == 0) return 0;

        // Find the sample index nearest to the aircraft position.
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _samples.Length; i++)
        {
            var d = GeoMath.DistanceNm(lat, lon, _samples[i].Latitude, _samples[i].Longitude);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (bestDist > 25) return 0; // far off route — no confident ahead data

        var max = 0.0;
        for (var i = best; i < _samples.Length; i++)
        {
            if (_cumDistNm[i] - _cumDistNm[best] > aheadNm) break;
            if (_requiredFt[i] > max) max = _requiredFt[i];
        }
        return max;
    }

    /// <summary>
    /// Required altitude at samples within <paramref name="radiusNm"/> of the
    /// position — used for near-field checks during climb.
    /// </summary>
    public double RequiredNearFt(double lat, double lon, double radiusNm = 15)
    {
        var max = 0.0;
        for (var i = 0; i < _samples.Length; i++)
        {
            var d = GeoMath.DistanceNm(lat, lon, _samples[i].Latitude, _samples[i].Longitude);
            if (d <= radiusNm && _requiredFt[i] > max) max = _requiredFt[i];
        }
        return max;
    }
}

/// <summary>
/// Simple racetrack/hold pattern over a point: outbound and inbound legs of
/// ~2 NM each on a fixed track heading. Used for valley departures (climb
/// before setting course) and mountain arrivals (stay high until terrain is
/// behind). Pure state machine — the caller supplies position each tick.
/// </summary>
public sealed class Racetrack
{
    public const double LegLengthNm = 2.0;

    private bool _outbound = true;
    private (double lat, double lon) _legStart;
    private bool _initialized;

    public bool Outbound => _outbound;

    /// <summary>Resets the pattern to outbound, starting at the current position.</summary>
    public void Reset(double lat, double lon)
    {
        _outbound = true;
        _legStart = (lat, lon);
        _initialized = true;
    }

    /// <summary>
    /// Call each tick with the aircraft position. Returns the heading to fly
    /// this leg, or null before the first <see cref="Reset"/>.
    /// </summary>
    public double? Update(double lat, double lon, double trackHeadingDeg)
    {
        if (!_initialized) return null;
        var flown = GeoMath.DistanceNm(_legStart.lat, _legStart.lon, lat, lon);
        if (flown >= LegLengthNm)
        {
            _outbound = !_outbound;
            _legStart = (lat, lon);
        }
        return GeoMath.NormalizeDeg(trackHeadingDeg + (_outbound ? 0 : 180));
    }
}
