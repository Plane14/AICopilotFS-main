using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>One AI or multi-player aircraft observed around the user aircraft.</summary>
public sealed class TrafficContact
{
    /// <summary>SimConnect object ID of the contact; unique for the session.</summary>
    public uint ObjectId { get; init; }

    public double Latitude { get; init; }
    public double Longitude { get; init; }
    /// <summary>Feet MSL. The facilities API carries no elevation for other objects.</summary>
    public double AltitudeMslFt { get; init; }
    public double HeadingDegTrue { get; init; }
    public double GroundSpeedKt { get; init; }
    public double IndicatedAirspeedKt { get; init; }
    public bool OnGround { get; init; }
    /// <summary>True when this record is the user's own aircraft, which is never traffic.</summary>
    public bool IsUserSim { get; init; }

    public DateTime LastSeenUtc { get; init; }

    // Derived against the user aircraft by the assessment that produced this contact.
    public double RangeMeters { get; internal set; }
    public double RangeNauticalMiles { get; internal set; }
    public double BearingDegTrue { get; internal set; }
    /// <summary>Contact altitude minus own altitude, feet. Positive is above.</summary>
    public double RelativeAltitudeFt { get; internal set; }
    /// <summary>Ground speed along the line of sight, knots. Negative is closing.</summary>
    public double ClosingSpeedKt { get; internal set; }
}

/// <summary>
/// Result of one traffic assessment: everything the flight logic needs to decide
/// whether the runway or the airspace around it is usable.
/// </summary>
public sealed class TrafficAssessment
{
    public IReadOnlyList<TrafficContact> Contacts { get; init; } = Array.Empty<TrafficContact>();

    /// <summary>Airborne traffic close enough and low enough to be a collision risk.</summary>
    public IReadOnlyList<TrafficContact> AirborneConflicts { get; init; } = Array.Empty<TrafficContact>();

    /// <summary>Traffic on the ground on the runway this aircraft is about to use.</summary>
    public TrafficContact? DepartureRunwayOccupant { get; init; }

    /// <summary>Traffic established on final for the runway this aircraft is using.</summary>
    public TrafficContact? ArrivalOnDepartureRunway { get; init; }

    /// <summary>True when entering or rolling on the runway would be unsafe.</summary>
    public bool RunwayEntryUnsafe => DepartureRunwayOccupant != null || ArrivalOnDepartureRunway != null || AirborneConflicts.Count > 0;

    public string Describe()
    {
        if (Contacts.Count == 0) return "no traffic in range";
        var parts = new List<string> { $"{Contacts.Count} aircraft in range" };
        if (DepartureRunwayOccupant is { } occupant)
            parts.Add($"runway occupied by an aircraft {occupant.RangeMeters:F0} m away");
        if (ArrivalOnDepartureRunway is { } arrival)
            parts.Add($"aircraft on final {arrival.RangeNauticalMiles:F1} NM out");
        if (AirborneConflicts.Count > 0)
            parts.Add($"{AirborneConflicts.Count} airborne conflict(s)");
        return string.Join(", ", parts);
    }
}

/// <summary>Tunables for <see cref="TrafficMonitor"/>.</summary>
public sealed class TrafficOptions
{
    /// <summary>Radius, in metres, around the user object that objects are reported for.</summary>
    public uint RequestRadiusMeters { get; init; } = 40000;

    /// <summary>How often the object-type request is re-issued.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// A contact not re-reported for this long is dropped. Deliberately longer than the
    /// refresh interval so a single missed cycle never makes traffic disappear, and a
    /// stale contact can only ever hold the aircraft rather than release it.
    /// </summary>
    public TimeSpan ContactTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Horizontal separation at which airborne traffic counts as a conflict.</summary>
    public double AirborneConflictRangeNauticalMiles { get; init; } = 5;

    /// <summary>Vertical separation at which airborne traffic counts as a conflict.</summary>
    public double AirborneConflictVerticalFt { get; init; } = 900;

    /// <summary>How close on the ground, in metres, counts as occupying the runway.</summary>
    public double RunwayOccupancyMeters { get; init; } = 250;

    /// <summary>How far out, in NM, an aircraft can be and still be treated as on final.</summary>
    public double FinalApproachRangeNauticalMiles { get; init; } = 8;

    /// <summary>Highest height above the runway, feet, that still counts as being on final.</summary>
    public double FinalApproachMaxHeightFt { get; init; } = 1800;
}

/// <summary>
/// Observes other aircraft in the simulator and decides what that means for the runway
/// and the approach. Traffic is read with
/// <c>SimConnect_RequestDataOnSimObjectType</c>, which delivers one record per object
/// with that object's own ID - there is no traffic list SimVar, and without this the
/// pilot is blind to everything else in the sky.
/// <para>
/// The monitor is deliberately fail-safe: it can only ever conclude "unsafe". If the
/// simulator rejects the request (a simulator build without traffic support, or a datum
/// name that does not exist), the monitor disables itself and reports no traffic rather
/// than guessing, so an unsupported simulator can never be told the runway is occupied
/// when it is empty. Contacts that stop being reported age out, and a stale contact can
/// only hold the aircraft, never release it.
/// </para>
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    /// <summary>
    /// Numeric, object-scoped datums that exist for AI and multi-player objects. No
    /// strings: a string datum forces every other field of the definition into a
    /// fixed-width text layout, and identity is not needed for a conflict decision.
    /// </summary>
    internal static readonly (string Name, string Unit)[] Datums =
    {
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("GROUND VELOCITY", "knots"),
        ("AIRSPEED INDICATED", "knots"),
        ("SIM ON GROUND", "bool"),
        ("IS USER SIM", "bool"),
    };

    /// <summary>
    /// The decision engine. Split out from the SimConnect plumbing so the geometry and
    /// thresholds can be exercised without a simulator, and so a monitor that has been
    /// disabled still has a coherent empty state.
    /// </summary>
    private readonly ContactTracker _contacts = new();
    private readonly TrafficOptions _options;
    private readonly Action<string> _log;
    private readonly SimConnectManager _sc;


    private uint _defineId;
    private uint _requestId;
    private DateTime _lastRefreshUtc;
    private bool _enabled = true;
    private bool _reportedDisabled;

    public TrafficMonitor(SimConnectManager sc, TrafficOptions? options = null, Action<string>? log = null)
    {
        _sc = sc;
        _options = options ?? new TrafficOptions();
        _log = log ?? (_ => { });
        _sc.OnError += OnSimConnectError;
    }

    /// <summary>False once the simulator has rejected the traffic request.</summary>
    /// <summary>
    /// Whether the simulator has rejected the traffic request. Once it has, no further
    /// requests are made for the session: retrying every interval would only fill the log.
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>Number of live contacts, the user's own aircraft excluded.</summary>
    public int ContactCount => _contacts.Count;

    /// <summary>
    /// Re-issues the request on a timer. Called once per control loop: a request that
    /// the simulator never answers (no traffic, or a rejected datum) costs one call per
    /// interval and is then abandoned by <see cref="OnSimConnectError"/>.
    /// <para>
    /// The request ID is kept and re-issued rather than allocated fresh each time. An
    /// object-type request cannot be cancelled, and the simulator reads a re-issued ID as
    /// a replacement, so a new ID per refresh accumulated one live request per cycle for
    /// the whole session - each one delivering a record per aircraft.
    /// </para>
    /// </summary>
    public void Refresh()
    {
        if (!_enabled) return;

        if (!_sc.IsConnected)
        {
            // The handle is gone, so the definition and the request with it.
            _defineId = 0;
            _requestId = 0;
            _contacts.Clear();
            return;
        }

        if (_requestId != 0 && DateTime.UtcNow - _lastRefreshUtc < _options.RefreshInterval) return;

        _lastRefreshUtc = DateTime.UtcNow;

        try
        {
            if (_defineId == 0)
            {
                _defineId = _sc.CreateDataDefinition();
                foreach (var (name, unit) in Datums)
                {
                    _sc.AddToDataDefinition(_defineId, name, unit);
                }
            }

            _requestId = _sc.RequestDataOnSimObjectType(
                _defineId, _options.RequestRadiusMeters, SimObjectType.Aircraft, _requestId);
        }
        catch (Exception ex)
        {
            Disable($"traffic request failed: {ex.Message}");
        }
    }

    /// <summary>Records one object's data. Called from the SimConnect dispatch path.</summary>
    public void OnData(SimObjectData data)
    {
        if (!_enabled) return;

        // Records arrive for every definition the client subscribes to, so the definition
        // has to match; without this the aircraft state block would be decoded as traffic.
        if (data.DefineId != 0 && data.DefineId != _defineId) return;

        var v = data.Values;
        double D(int i) => i < v.Length && v[i] is double d ? d.EnsureFinite() : 0;

        var contact = new TrafficContact
        {
            ObjectId = data.ObjectId,
            Latitude = D(0),
            Longitude = D(1),
            AltitudeMslFt = D(2),
            HeadingDegTrue = D(3),
            GroundSpeedKt = D(4),
            IndicatedAirspeedKt = D(5),
            OnGround = D(6) > 0.5,
            IsUserSim = D(7) > 0.5,
            LastSeenUtc = DateTime.UtcNow
        };

        // A record at 0/0 is not an aircraft: it is an object the sim has not placed.
        if (Math.Abs(contact.Latitude) < 0.000001 && Math.Abs(contact.Longitude) < 0.000001) return;

        _contacts.Add(contact);
    }

    /// <summary>
    /// Drops contacts that stopped being reported, then evaluates what the live ones mean
    /// for <paramref name="runway"/>. Own position comes from <paramref name="own"/>.
    /// </summary>
    public TrafficAssessment Assess(AircraftState own, RunwayInfo? runway = null)
    {
        var live = _contacts.RemoveStale(_options.ContactTimeout);
        return TrafficEvaluator.Evaluate(live, own, runway, _options);
    }

    /// <summary>
    /// An aircraft is on final when it is airborne, low, close to the runway threshold it
    /// is approaching, and pointing down that runway rather than across it.
    /// </summary>
    private void OnSimConnectError(string message)
    {
        // Only the traffic request's own rejections disable it: an unrelated exception
        // (a bad checklist event, say) must not silence traffic for the session.
        if (!_enabled) return;
        var isTraffic = message.Contains("sim object type", StringComparison.OrdinalIgnoreCase)
            || Datums.Any(d => message.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
        if (!isTraffic) return;
        Disable($"traffic request was rejected by the simulator: {message}");
    }

    private void Disable(string reason)
    {
        _enabled = false;
        _contacts.Clear();
        if (_reportedDisabled) return;
        _reportedDisabled = true;
        _log($"[Traffic] {reason}; traffic awareness is off for this session");
    }

    public void Dispose()
    {
        _sc.OnError -= OnSimConnectError;
        _contacts.Clear();
    }
}

/// <summary>
/// Holds the contacts the simulator is still reporting, keyed by object ID. Replacing an
/// entry on each report keeps the set the same size as the traffic feed, so the only thing
/// that has to expire is contact that stopped being reported.
/// </summary>
internal sealed class ContactTracker
{
    private readonly Dictionary<uint, TrafficContact> _byObject = new();

    /// <summary>Live contacts, the user's own aircraft excluded.</summary>
    public int Count => _byObject.Values.Count(c => !c.IsUserSim);

    public void Clear() => _byObject.Clear();

    /// <summary>Records a contact, replacing any earlier report for the same object.</summary>
    public void Add(TrafficContact contact) => _byObject[contact.ObjectId] = contact;

    /// <summary>
    /// Returns the contacts reported within <paramref name="timeout"/>, dropping the rest.
    /// A contact that stopped being reported has gone out of range or been removed, and
    /// keeping it would make the aircraft wait for traffic that is no longer there.
    /// </summary>
    public List<TrafficContact> RemoveStale(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;

        foreach (var id in _byObject.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList())
        {
            _byObject.Remove(id);
        }

        return _byObject.Values.Where(c => !c.IsUserSim).ToList();
    }
}

/// <summary>
/// Turns a set of contacts into a decision. Separated from <see cref="TrafficMonitor"/> so
/// the geometry and thresholds can be exercised without a simulator: the monitor's job is
/// the SimConnect plumbing, and "is the runway usable" is answerable from positions and a
/// runway alone.
/// </summary>
internal static class TrafficEvaluator
{
    public static TrafficAssessment Evaluate(
        List<TrafficContact> live, AircraftState own, RunwayInfo? runway, TrafficOptions options)
    {
        var airborne = new List<TrafficContact>();
        var occupant = default(TrafficContact);
        var arrival = default(TrafficContact);

        foreach (var contact in live)
        {
            contact.RangeMeters = Geo.DistanceMeters(own.Latitude, own.Longitude, contact.Latitude, contact.Longitude);
            contact.RangeNauticalMiles = contact.RangeMeters / 1852.0;
            contact.BearingDegTrue = Geo.BearingDegrees(own.Latitude, own.Longitude, contact.Latitude, contact.Longitude);
            contact.RelativeAltitudeFt = contact.AltitudeMslFt - own.Altitude;

            // Component of the contact's ground velocity along the line of sight. A
            // contact flying away down the line is much less of a risk than one
            // converging head-on, so range alone would over-report.
            var along = contact.GroundSpeedKt * Math.Cos(Geo.ToRadians(contact.HeadingDegTrue - contact.BearingDegTrue));
            contact.ClosingSpeedKt = -along;

            if (!contact.OnGround &&
                contact.RangeNauticalMiles <= options.AirborneConflictRangeNauticalMiles &&
                Math.Abs(contact.RelativeAltitudeFt) <= options.AirborneConflictVerticalFt)
            {
                airborne.Add(contact);
            }
        }

        if (runway is { LengthMeters: > 0 } strip)
        {
            var (a, b) = strip.PrimaryEnd;
            var (c, d) = strip.SecondaryEnd;

            foreach (var contact in live)
            {
                if (contact.OnGround)
                {
                    var toCentreline = TrafficGeometry.DistanceToSegmentMeters(
                        contact.Latitude, contact.Longitude, a, b, c, d);
                    if (toCentreline <= options.RunwayOccupancyMeters && occupant == null)
                    {
                        occupant = contact;
                    }
                }
                else if (IsOnFinal(contact, strip, options))
                {
                    arrival = contact;
                }
            }
        }

        return new TrafficAssessment
        {
            Contacts = live,
            AirborneConflicts = airborne,
            DepartureRunwayOccupant = occupant,
            ArrivalOnDepartureRunway = arrival
        };
    }

    /// <summary>
    /// An aircraft is on final when it is airborne, low, close to the runway threshold it
    /// is approaching, and pointing down that runway rather than across it.
    /// </summary>
    private static bool IsOnFinal(TrafficContact contact, RunwayInfo runway, TrafficOptions options)
    {
        var threshold = runway.PrimaryEnd;
        var distanceToThreshold = Geo.DistanceMeters(
            contact.Latitude, contact.Longitude, threshold.lat, threshold.lon) / 1852.0;
        if (distanceToThreshold > options.FinalApproachRangeNauticalMiles) return false;

        // Relative to the runway elevation, because a thousand feet is nothing at
        // Denver and a lot at sea level.
        var heightAboveRunwayFt = contact.AltitudeMslFt - Geo.MetersToFeet(runway.AltitudeMeters);
        if (heightAboveRunwayFt > options.FinalApproachMaxHeightFt) return false;

        // 3 degrees needs roughly 318 ft per NM; being higher than that means the
        // aircraft is not on this runway's path.
        var expectedHeightFt = distanceToThreshold * 318.0;
        if (heightAboveRunwayFt > expectedHeightFt + 800) return false;

        var bearingToRunway = Geo.BearingDegrees(contact.Latitude, contact.Longitude, threshold.lat, threshold.lon);
        return Math.Abs(Geo.NormalizeDegrees(contact.HeadingDegTrue - bearingToRunway)) < 30;
    }
}

/// <summary>
/// Geometry helpers for traffic, kept separate from the monitor so they can be tested
/// without a simulator.
/// </summary>
public static class TrafficGeometry
{
    /// <summary>
    /// Distance in metres from a point to the line segment joining two positions, using
    /// the same local plane approximation as <see cref="Geo.OffsetByMeters"/>. Accurate
    /// to well under a metre at airport scale, which is what runway occupancy needs.
    /// </summary>
    public static double DistanceToSegmentMeters(
        double lat, double lon, double aLat, double aLon, double bLat, double bLon)
    {
        var (metersPerDegLat, metersPerDegLon) = MetersPerDegree(aLat);
        var ax = 0.0;
        var ay = 0.0;
        var bx = (bLon - aLon) * metersPerDegLon;
        var by = (bLat - aLat) * metersPerDegLat;
        var px = (lon - aLon) * metersPerDegLon;
        var py = (lat - aLat) * metersPerDegLat;

        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9) return Math.Sqrt(px * px + py * py);

        var t = ((px - ax) * dx + (py - ay) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);

        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    internal static (double lat, double lon) MetersPerDegree(double latitudeDeg)
    {
        var lat = Geo.ToRadians(latitudeDeg);
        return (
            111132.92 - 559.82 * Math.Cos(2 * lat) + 1.175 * Math.Cos(4 * lat),
            111412.84 * Math.Cos(lat) - 93.5 * Math.Cos(3 * lat));
    }
}

internal static class TrafficDoubleExtensions{
    /// <summary>Replaces NaN and infinity with zero so one bad record cannot poison the assessment.</summary>
    public static double EnsureFinite(this double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
}

/// <summary>Formats a traffic summary without depending on the current culture.</summary>
public static class TrafficFormat
{
    public static string Text(TrafficContact contact) => string.Create(CultureInfo.InvariantCulture,
        $"#{contact.ObjectId} {contact.RangeNauticalMiles:F1}NM {contact.BearingDegTrue:F0}T " +
        $"{contact.RelativeAltitudeFt:+0;-0;0}ft {(contact.OnGround ? "ground" : "air")}");
}
