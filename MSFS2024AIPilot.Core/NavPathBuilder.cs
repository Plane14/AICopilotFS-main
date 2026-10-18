using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// A route the pilot can fly: a chain of legs from where the aircraft is to the runway
/// threshold, plus the published missed approach for arriving.
/// </summary>
public sealed class NavRoutePlan
{
    public List<NavLeg> Legs { get; } = new();
    public List<NavLeg> MissedLegs { get; } = new();

    /// <summary>The approach the route ends in, when one was loaded from the navdata.</summary>
    public ApproachInfo? Approach { get; set; }

    /// <summary>
    /// Fixes the plan names that the navigation database does not know. The route is
    /// still flown, with those legs missing, because a route with a hole in it is better
    /// than no route at all - but it is reported so the gap is visible.
    /// </summary>
    public List<string> UnresolvedFixes { get; } = new();

    public string? DepartureIcao { get; set; }
    public string? ArrivalIcao { get; set; }
    public string? DepartureRunway { get; set; }
    public string? ArrivalRunway { get; set; }
    public double MagneticVariationDeg { get; set; }

    public bool HasRoute => Legs.Count(l => l.HasPosition) > 0;
    public bool HasApproach => Approach?.HasFinalApproach == true;
    public bool HasMissedApproach => MissedLegs.Count(l => l.HasPosition) > 0;
    public bool HasUnresolvedFixes => UnresolvedFixes.Count > 0;

    public NavPathFollower CreateFollower() => new(Legs, MagneticVariationDeg);

    public NavPathFollower CreateMissedApproachFollower() => new(MissedLegs, MagneticVariationDeg);

    public string Describe()
    {
        var route = string.Join(" > ", Legs.Select(l => l.Ident));
        var missed = HasMissedApproach ? string.Join(" > ", MissedLegs.Select(l => l.Ident)) : "none";
        return $"route ({Legs.Count} legs): {route}{Environment.NewLine}approach: " +
               $"{(Approach is null ? "none" : Approach.Describe())}{Environment.NewLine}" +
               $"missed approach: {missed}" +
               (HasUnresolvedFixes ? $"{Environment.NewLine}unresolved fixes: {string.Join(", ", UnresolvedFixes)}" : "");
    }
}

/// <summary>
/// Assembles a route out of a flight plan and the navigation database.
/// <para>
/// A MSFS 2024 plan is a list of names: the built-in planner writes waypoint identifiers
/// and no positions at all. The geometry comes from the navdata - the approaches with
/// their published legs, and the position of every fix the plan names. What cannot be
/// resolved is reported rather than guessed at, because a leg in the wrong place is worse
/// than a leg that is missing.
/// </para>
/// </summary>
public static class NavPathBuilder
{
    /// <summary>
    /// Builds the route for a plan. Call again whenever more fixes resolve: the plan
    /// keeps its unresolved names and the route fills in as the answers arrive.
    /// </summary>
    public static NavRoutePlan Build(
        NavigationState nav,
        NavDataLoader navData,
        string? departureRunway,
        string? arrivalRunway,
        AirportData? arrivalAirport)
    {
        // A MSFS 2024 plan names its fixes and publishes no positions, so each one is
        // asked for here. The lookups answer asynchronously and the route is rebuilt as
        // they do, which is why this is called again whenever navdata arrives.
        foreach (var waypoint in nav.Waypoints.Where(ShouldRequestFixLookup))
            navData.RequestFix(waypoint.Ident);

        return Build(
            nav,
            navData.GetFix,
            navData.GetApproaches(nav.ArrivalICAO ?? ""),
            departureRunway,
            arrivalRunway,
            arrivalAirport,
            fix => navData.IsFixUnknown(fix));
    }

    /// <summary>
    /// Whether this waypoint should be resolved through the fix database.
    /// Airport/runway waypoints are not nav fixes and should not be requested as
    /// WAYPOINT/NDB/VOR facilities.
    /// </summary>
    internal static bool ShouldRequestFixLookup(FlightPlanWaypoint waypoint)
    {
        if (string.IsNullOrWhiteSpace(waypoint.Ident)) return false;
        if (waypoint.HasPosition) return false;

        return waypoint.Type is not (WaypointType.Airport or WaypointType.Runway);
    }

    /// <summary>
    /// The route from the plan's fix names and the geometry the navigation database has
    /// for them. <paramref name="resolveFix"/> answers with a fix or null, and
    /// <paramref name="publishedApproaches"/> is what the arrival airport publishes - all
    /// plain data rather than a simulator, which is what makes the route testable without
    /// a running sim. <paramref name="isFixUnknown"/> answers whether a name the database
    /// could not place is genuinely not in it, which keeps an unresolved fix that has
    /// merely not been looked up yet out of the reported set.
    /// </summary>
    public static NavRoutePlan Build(
        NavigationState nav,
        Func<string, NavFix?> resolveFix,
        IReadOnlyList<ApproachInfo> publishedApproaches,
        string? departureRunway,
        string? arrivalRunway,
        AirportData? arrivalAirport,
        Func<string, bool>? isFixUnknown = null)
    {
        var plan = new NavRoutePlan
        {
            DepartureIcao = nav.DepartureICAO,
            ArrivalIcao = nav.ArrivalICAO,
            DepartureRunway = departureRunway,
            ArrivalRunway = arrivalRunway,
            MagneticVariationDeg = arrivalAirport?.MagneticVariationDeg ?? 0
        };

        AppendRouteFixes(nav, resolveFix, plan, isFixUnknown);

        var approach = SelectApproach(publishedApproaches, arrivalRunway);
        if (approach != null)
        {
            AppendApproach(nav, resolveFix, plan, approach, arrivalAirport);
        }

        Chain(plan.Legs);
        return plan;
    }

    /// <summary>
    /// The plan's fixes, with the ones the database knows about placed. A fix that
    /// cannot be placed is left out of the route and remembered by name, but only once it
    /// is known not to be in the database at all: one that has simply not been looked up
    /// yet is still answered for on the next rebuild.
    /// </summary>
    private static void AppendRouteFixes(
        NavigationState nav, Func<string, NavFix?> resolveFix, NavRoutePlan plan,
        Func<string, bool>? isFixUnknown)
    {
        foreach (var waypoint in nav.Waypoints)
        {
            if (string.IsNullOrEmpty(waypoint.Ident)) continue;

            var kind = waypoint.Type switch
            {
                WaypointType.SID => NavLegKind.Sid,
                WaypointType.STAR => NavLegKind.Star,
                WaypointType.Approach or WaypointType.Runway => NavLegKind.Approach,
                _ => NavLegKind.Enroute
            };

            if (waypoint.HasPosition)
            {
                plan.Legs.Add(new NavLeg
                {
                    Ident = waypoint.Ident,
                    Latitude = waypoint.Latitude,
                    Longitude = waypoint.Longitude,
                    AltitudeFt = waypoint.Altitude,
                    Kind = kind
                });
                continue;
            }

            var fix = resolveFix(waypoint.Ident);
            if (fix?.HasPosition == true)
            {
                plan.Legs.Add(new NavLeg
                {
                    Ident = waypoint.Ident,
                    Latitude = fix.Latitude,
                    Longitude = fix.Longitude,
                    AltitudeFt = Geo.MetersToFeet(fix.AltitudeMeters),
                    Kind = kind
                });
            }
            else if (isFixUnknown?.Invoke(waypoint.Ident) ?? true)
            {
                plan.UnresolvedFixes.Add(waypoint.Ident);
            }
        }

        // The plan's two airport waypoints carry positions even in the native format, and
        // they are what the route is anchored to at both ends.
        AppendDepartureStart(nav, plan);
    }

    /// <summary>
    /// Where the route begins: the departure airport, which is where the aircraft is
    /// standing when the flight starts. A plan whose first fix already is that airport (a
    /// plan that lists its departure by ICAO) is left alone.
    /// </summary>
    private static void AppendDepartureStart(NavigationState nav, NavRoutePlan plan)
    {
        if (nav.DepartureLatitude == 0 && nav.DepartureLongitude == 0) return;

        var first = plan.Legs.FirstOrDefault(l => l.HasPosition);
        if (first != null &&
            Geo.DistanceMeters(first.Latitude, first.Longitude, nav.DepartureLatitude, nav.DepartureLongitude) < 2000)
        {
            return;
        }

        plan.Legs.Insert(0, new NavLeg
        {
            Ident = $"{nav.DepartureICAO} {nav.DepartureRunway}".Trim(),
            Latitude = nav.DepartureLatitude,
            Longitude = nav.DepartureLongitude,
            AltitudeFt = nav.DepartureAltitude,
            Kind = NavLegKind.RunwayStart
        });
    }

    /// <summary>
    /// The approach to fly: the one published to the runway the controller assigned, and
    /// failing that the best one at the airport. An ILS beats an RNAV beats a VOR, which
    /// is the order a crew would choose in.
    /// </summary>
    public static ApproachInfo? SelectApproach(
        IReadOnlyList<ApproachInfo> publishedApproaches, string? runway)
    {
        if (publishedApproaches.Count == 0) return null;

        var flyable = publishedApproaches.Where(a => a.HasFinalApproach).ToList();
        if (flyable.Count == 0) return null;

        var forRunway = string.IsNullOrEmpty(runway)
            ? flyable
            : flyable.Where(a => string.Equals(a.Runway, runway, StringComparison.OrdinalIgnoreCase)).ToList();

        var candidates = forRunway.Count > 0 ? forRunway : flyable;

        return candidates
            .OrderBy(a => Preference(a))
            .ThenBy(a => a.Runway, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>Lower is better: the order a crew would pick an approach in.</summary>
    private static int Preference(ApproachInfo approach)
    {
        var rank = approach.Kind switch
        {
            ApproachKind.Ils => 0,
            ApproachKind.Localizer or ApproachKind.Lda => 1,
            ApproachKind.Rnav => 2,
            ApproachKind.Gps => 3,
            ApproachKind.Vor or ApproachKind.VorDme => 4,
            ApproachKind.Ndb or ApproachKind.NdbDme => 5,
            _ => 6
        };

        if (approach.HasLpv) rank -= 1;
        return rank;
    }

    private static void AppendApproach(
        NavigationState nav,
        Func<string, NavFix?> resolveFix,
        NavRoutePlan plan,
        ApproachInfo approach,
        AirportData? arrivalAirport)
    {
        plan.Approach = approach;

        // The way in to the approach: the transition whose initial fix is the fix the
        // route arrives from. Without a matching transition the approach's own legs are
        // flown, which is what a "straight-in, no transition" clearance means.
        var from = plan.Legs.LastOrDefault(l => l.HasPosition)?.Ident;
        var transition = approach.TransitionFor(from);
        if (transition != null)
        {
            AppendProcedureLegs(plan.Legs, transition.Legs, NavLegKind.Approach);
        }

        AppendProcedureLegs(plan.Legs, approach.FinalLegs, NavLegKind.Approach);
        AppendArrivalThreshold(plan, nav, arrivalAirport);

        // The published missed approach is the go-around: a chain of legs starting at the
        // missed approach point, exactly like the rest of the route.
        AppendProcedureLegs(plan.MissedLegs, approach.MissedLegs, NavLegKind.Missed);
        Chain(plan.MissedLegs);
    }

    /// <summary>
    /// Appends a published procedure's legs to a route, turning the ones that fly an arc
    /// into a chain of short legs along it: a constant radius arc has no place in a route
    /// of straight legs, and flying the straight line between its ends cuts the corner.
    /// </summary>
    private static void AppendProcedureLegs(
        List<NavLeg> into, IEnumerable<ApproachLeg> legs, NavLegKind kind)
    {
        foreach (var leg in legs)
        {
            if (!leg.HasPosition) continue;

            var previous = into.Count > 0 ? into[^1] : null;
            AddProcedureLeg(into, leg, kind);

            if (leg.IsArc && previous is not null)
            {
                var points = ArcGeometry.Points(leg, previous.Latitude, previous.Longitude);
                if (points.Count == 0) continue;

                // The terminator was just appended: the arc points go in front of it.
                // The sweep's last point is that same terminator, so drop it here to avoid
                // emitting the fix twice (a duplicate, zero-length leg).
                into.InsertRange(into.Count - 1, points.Take(points.Count - 1).Select(p => new NavLeg
                {
                    Ident = "",
                    Latitude = p.lat,
                    Longitude = p.lon,
                    AltitudeFt = Geo.MetersToFeet(leg.AltitudeMeters),
                    MinimumAltitudeFt = leg.MinimumAltitudeFt,
                    Kind = kind
                }));
            }
        }
    }

    private static void AddProcedureLeg(List<NavLeg> into, ApproachLeg leg, NavLegKind kind)
    {
        var navLeg = new NavLeg
        {
            Ident = leg.Ident,
            Latitude = leg.Latitude,
            Longitude = leg.Longitude,
            AltitudeFt = Geo.MetersToFeet(leg.AltitudeMeters),
            MinimumAltitudeFt = leg.MinimumAltitudeFt,
            MaximumAltitudeFt = leg.MaximumAltitudeFt,
            SpeedLimitKt = leg.SpeedLimitKt,
            FlyOver = leg.FlyOver,
            Kind = kind
        };

        // The route is a chain of fixes: a fix that is already there is the join between
        // the route and the procedure, not a second fix at the same place.
        if (into.Count > 0)
        {
            var last = into[^1];
            if (last.HasPosition &&
                Geo.DistanceMeters(last.Latitude, last.Longitude, navLeg.Latitude, navLeg.Longitude) < 100)
            {
                into[^1] = navLeg.WithKind(kind);
                return;
            }
        }

        into.Add(navLeg);
    }

    /// <summary>
    /// Ends the route at the runway threshold when the approach's own last fix is not on
    /// the strip, which is the normal case: the missed approach point sits before it.
    /// </summary>
    private static void AppendArrivalThreshold(NavRoutePlan plan, NavigationState nav, AirportData? arrivalAirport)
    {
        var runwayName = !string.IsNullOrEmpty(plan.ArrivalRunway)
            ? plan.ArrivalRunway
            : plan.Approach?.Runway;
        if (string.IsNullOrEmpty(runwayName) || arrivalAirport is null) return;

        var runway = arrivalAirport.FindRunway(runwayName);
        if (runway is null || double.IsNaN(runway.Latitude)) return;

        // The threshold the aircraft lands on is the end named by the approach runway.
        var threshold = runway.ThresholdFor(runwayName);
        if (!threshold.HasValue) return;

        var last = plan.Legs.LastOrDefault(l => l.HasPosition);
        if (last != null &&
            Geo.DistanceMeters(last.Latitude, last.Longitude, threshold.Value.lat, threshold.Value.lon) < 100)
        {
            return;
        }

        plan.Legs.Add(new NavLeg
        {
            Ident = $"{runwayName} threshold",
            Latitude = threshold.Value.lat,
            Longitude = threshold.Value.lon,
            AltitudeFt = Geo.MetersToFeet(runway.AltitudeMeters),
            Kind = NavLegKind.Approach
        });
    }

    /// <summary>Links each leg to its predecessor, which is what the follower steers from.</summary>
    public static void Chain(List<NavLeg> legs)
    {
        for (var i = 1; i < legs.Count; i++)
        {
            if (legs[i].Previous is null) legs[i].Previous = legs[i - 1];
        }
    }
}

/// <summary>
/// Point geometry for the arc terms of a published procedure. An RF leg is a constant
/// radius turn about a centre; an AF or CR leg is an arc about a reference navaid at a
/// published distance. Both are turned into points so the rest of the pilot only ever
/// has to follow a chain of fixes.
/// </summary>
public static class ArcGeometry
{
    /// <summary>Angular step of the generated arc points, in degrees.</summary>
    public const double StepDegrees = 4;

    /// <summary>
    /// Generates the points of the arc a leg flies, from just after it starts to its
    /// terminator. The centre is the leg's own, and the direction comes from the turn
    /// direction the procedure publishes.
    /// </summary>
    public static List<(double lat, double lon)> Points(ApproachLeg leg, double fromLatitude, double fromLongitude)
    {
        var centerLatitude = leg.PathType == LegPathType.ConstantRadiusArc
            ? leg.ArcCenterLatitude
            : leg.OriginLatitude;
        var centerLongitude = leg.PathType == LegPathType.ConstantRadiusArc
            ? leg.ArcCenterLongitude
            : leg.OriginLongitude;

        // An arc has to start somewhere: the previous fix on the route. Without one the
        // sweep has no defined beginning and the arc cannot be built.
        if (fromLatitude == 0 && fromLongitude == 0) return new List<(double, double)>();
        if (centerLatitude == 0 && centerLongitude == 0) return new List<(double, double)>();

        var radius = leg.PathType == LegPathType.ConstantRadiusArc
            ? Geo.DistanceMeters(centerLatitude, centerLongitude, fromLatitude, fromLongitude)
            // An arc to a fix is a DME arc: every point on it is the published distance
            // (RhoMeters) from the reference navaid, which is the arc's radius. The leg's
            // published length (RouteDistance) is not a radius, and is sometimes minutes,
            // so using it produced points at the wrong distance and cut the corner it
            // exists to prevent.
            : leg.RhoMeters;

        if (radius <= 1) return new List<(double, double)>();

        var start = Geo.BearingDegrees(centerLatitude, centerLongitude, fromLatitude, fromLongitude);
        var end = Geo.BearingDegrees(centerLatitude, centerLongitude, leg.Latitude, leg.Longitude);

        var clockwise = leg.Turn switch
        {
            ProcedureTurn.Right => true,
            ProcedureTurn.Left => false,
            // No published turn direction: take the short way round.
            _ => Geo.NormalizeDegrees(end - start) < 0
        };

        return Sweep(centerLatitude, centerLongitude, radius, start, end, clockwise);
    }

    /// <summary>
    /// Walks a circle of <paramref name="radius"/> metres from <paramref name="start"/>
    /// to <paramref name="end"/>, in the direction asked for, stepping by
    /// <see cref="StepDegrees"/> and finishing exactly on the end bearing.
    /// </summary>
    public static List<(double lat, double lon)> Sweep(
        double centerLatitude, double centerLongitude, double radius,
        double startDeg, double endDeg, bool clockwise)
    {
        var delta = clockwise ? startDeg - endDeg : endDeg - startDeg;
        while (delta <= 0) delta += 360;
        while (delta > 360) delta -= 360;
        if (delta < 0.5) return new List<(double, double)>();

        var steps = Math.Max(1, (int)Math.Ceiling(delta / StepDegrees));
        var points = new List<(double, double)>(steps);

        for (var step = 1; step <= steps; step++)
        {
            var bearing = clockwise
                ? startDeg - delta * step / steps
                : startDeg + delta * step / steps;
            points.Add(Offset(centerLatitude, centerLongitude, bearing, radius));
        }

        return points;
    }

    private static (double lat, double lon) Offset(double latitude, double longitude, double bearingDeg, double meters)
    {
        var latRadians = Geo.ToRadians(latitude);
        var bearingRadians = Geo.ToRadians(bearingDeg);

        var lat2 = Math.Asin(Math.Sin(latRadians) * Math.Cos(meters / 6371008.8) +
                             Math.Cos(latRadians) * Math.Sin(meters / 6371008.8) * Math.Cos(bearingRadians));
        var lon2 = Geo.ToRadians(longitude) + Math.Atan2(
            Math.Sin(bearingRadians) * Math.Sin(meters / 6371008.8) * Math.Cos(latRadians),
            Math.Cos(meters / 6371008.8) - Math.Sin(latRadians) * Math.Sin(lat2));

        return (Geo.ToDegrees(lat2), Geo.ToDegrees(lon2));
    }
}

file static class NavLegExtensions
{
    public static NavLeg WithKind(this NavLeg leg, NavLegKind kind)
    {
        leg.Kind = kind;
        return leg;
    }
}
