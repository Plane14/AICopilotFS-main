using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Planning;

/// <summary>Everything the route builder needs to know about the legs.</summary>
public sealed class RouteRequest
{
    public required AirportData Departure { get; init; }
    public required AirportData Destination { get; init; }
    public string? DepartureRunway { get; set; }
    public string? DestinationRunway { get; set; }
    public double CruiseAltitudeFt { get; set; } = 6000;
    public double ClimbRateFpm { get; set; } = 1500;
    public double DescentRateFpm { get; set; } = 1500;
    public double ApproachSpeedKt { get; set; } = 90;
    public double CruiseSpeedKt { get; set; } = 160;
    /// <summary>Whether to build an ILS approach (needs the destination runway's ILS frequency).</summary>
    public bool PreferIls { get; set; } = true;
    /// <summary>ILS frequency in MHz for the destination runway, when known.</summary>
    public double? IlsFrequencyMhz { get; set; }
}

/// <summary>The generated flight plan plus the waypoints that carry approach guidance.</summary>
public sealed class BuiltRoute
{
    public List<FlightPlanWaypoint> Waypoints { get; } = new();
    public string DepartureIcao => Departure?.Icao ?? "";
    public string DestinationIcao => Destination?.Icao ?? "";
    public AirportData? Departure { get; set; }
    public AirportData? Destination { get; set; }
    public string? DepartureRunway { get; set; }
    public string? DestinationRunway { get; set; }
    public double CruiseAltitudeFt { get; set; }

    /// <summary>The final approach fix (start of the descent to the runway).</summary>
    public FlightPlanWaypoint? FinalApproachFix { get; set; }
    /// <summary>The runway threshold waypoint.</summary>
    public FlightPlanWaypoint? RunwayWaypoint { get; set; }
    /// <summary>Missed approach point / climb-out fix.</summary>
    public FlightPlanWaypoint? MissedApproachFix { get; set; }

    public double TotalDistanceNm { get; set; }
}

/// <summary>
/// Builds a flyable route between two airports: departure runway in departure
/// order, great-circle enroute waypoints at a safe interval, then a
/// procedure-style arrival ending at the destination runway threshold.
/// </summary>
public static class RouteBuilder
{
    /// <summary>Spacing between enroute waypoints, nm.</summary>
    public const double EnrouteSpacingNm = 1.0;
    /// <summary>Final approach fix distance from threshold, nm (~5nm final).</summary>
    public const double FinalLegNm = 5.0;

    public static BuiltRoute Build(RouteRequest req)
    {
        var route = new BuiltRoute
        {
            Departure = req.Departure,
            Destination = req.Destination,
            DepartureRunway = req.DepartureRunway,
            DestinationRunway = req.DestinationRunway,
            CruiseAltitudeFt = req.CruiseAltitudeFt,
        };

        // --- Departure end ---
        var depThreshold = DepartureThreshold(req.Departure, req.DepartureRunway);
        var depEnd = DepartureEnd(req.Departure, req.DepartureRunway);
        if (depThreshold is not null)
            route.Waypoints.Add(new FlightPlanWaypoint
            {
                Ident = req.Departure.Icao,
                Type = WaypointType.Airport,
                Latitude = depThreshold.Value.lat,
                Longitude = depThreshold.Value.lon,
                Altitude = req.Departure.ReferenceAltitudeMeters * GeoMath.FeetPerMeter,
            });

        // --- Destination runway ---
        var dstRunway = req.DestinationRunway is not null ? req.Destination.FindRunway(req.DestinationRunway) : null;
        var threshold = dstRunway?.ThresholdFor(req.DestinationRunway ?? "") ?? DestinationThreshold(req.Destination, req.DestinationRunway);
        var thresholdAlt = (dstRunway?.AltitudeMeters ?? req.Destination.ReferenceAltitudeMeters) * GeoMath.FeetPerMeter;

        // --- Enroute: great-circle waypoints from departure to destination ---
        var startLat = depThreshold?.lat ?? req.Departure.ReferenceLatitude;
        var startLon = depThreshold?.lon ?? req.Departure.ReferenceLongitude;
        var endLat = threshold?.lat ?? req.Destination.ReferenceLatitude;
        var endLat2 = endLat;
        var endLon = threshold?.lon ?? req.Destination.ReferenceLongitude;

        var totalNm = GeoMath.DistanceNm(startLat, startLon, endLat2, endLon);
        route.TotalDistanceNm = totalNm;

        // Enroute waypoint count: enough to keep GPS happy but not absurd.
        var count = Math.Clamp((int)Math.Ceiling(totalNm / 60), 1, 12);
        var spacingNm = totalNm / (count + 1);

        for (int i = 1; i <= count; i++)
        {
            var frac = (double)i / (count + 1);
            var (lat, lon) = GreatCirclePoint(startLat, startLon, endLat2, endLon, frac);
            route.Waypoints.Add(new FlightPlanWaypoint
            {
                Ident = $"RT{i}",
                Type = WaypointType.Intersection,
                Latitude = lat,
                Longitude = lon,
                Altitude = req.CruiseAltitudeFt,
            });
        }

        // --- Arrival: final approach fix ~5nm out on extended centreline ---
        if (threshold is not null && dstRunway is not null)
        {
            var heading = dstRunway.HeadingDegTrue;
            // FAF is outbound from the threshold along the reciprocal of the landing heading.
            var fafBearing = GeoMath.NormalizeDeg(heading + 180);
            var (fafLat, fafLon) = GeoMath.DestinationNm(threshold.Value.lat, threshold.Value.lon, fafBearing, FinalLegNm);
            var faf = new FlightPlanWaypoint
            {
                Ident = "FAF",
                Type = WaypointType.Intersection,
                Latitude = fafLat,
                Longitude = fafLon,
                Altitude = req.CruiseAltitudeFt,
            };
            route.Waypoints.Add(faf);
            route.FinalApproachFix = faf;

            // Threshold waypoint (the runway end we fly to).
            var rw = new FlightPlanWaypoint
            {
                Ident = req.Destination.Icao,
                Type = WaypointType.Airport,
                Latitude = threshold.Value.lat,
                Longitude = threshold.Value.lon,
                Altitude = thresholdAlt,
                Runway = req.DestinationRunway,
            };
            route.Waypoints.Add(rw);
            route.RunwayWaypoint = rw;

            // Missed approach fix: continue past the threshold on the runway heading.
            var (mapLat, mapLon) = GeoMath.DestinationNm(threshold.Value.lat, threshold.Value.lon, heading, 3);
            var map = new FlightPlanWaypoint
            {
                Ident = "MAP",
                Type = WaypointType.MissedApproach,
                Latitude = mapLat,
                Longitude = mapLon,
                Altitude = req.CruiseAltitudeFt,
            };
            route.MissedApproachFix = map;
        }
        else
        {
            // No runway data: end at the airport reference point.
            route.Waypoints.Add(new FlightPlanWaypoint
            {
                Ident = req.Destination.Icao,
                Type = WaypointType.Airport,
                Latitude = endLat2,
                Longitude = endLon,
                Altitude = thresholdAlt,
            });
        }

        return route;
    }

    private static (double lat, double lon)? DepartureThreshold(AirportData airport, string? runwayName)
    {
        if (!string.IsNullOrEmpty(runwayName))
        {
            var rw = airport.FindRunway(runwayName);
            var t = rw?.ThresholdFor(runwayName);
            if (t is not null) return t;
        }
        // Default: first runway's primary threshold, else airport reference.
        var any = airport.Runways.FirstOrDefault();
        if (any is not null)
        {
            var name = any.PrimaryName;
            if (!string.IsNullOrEmpty(name)) return any.ThresholdFor(name);
        }
        return (airport.ReferenceLatitude, airport.ReferenceLongitude);
    }

    private static (double lat, double lon)? DepartureEnd(AirportData airport, string? runwayName)
    {
        // The end opposite the threshold: where the aircraft actually ends up after takeoff.
        if (!string.IsNullOrEmpty(runwayName))
        {
            var rw = airport.FindRunway(runwayName);
            var t = rw?.ThresholdFor(runwayName);
            if (t is not null && rw is not null)
            {
                // Opposite end is the other threshold.
                var other = rw.SecondaryName.Equals(runwayName, StringComparison.OrdinalIgnoreCase)
                    ? rw.ThresholdFor(rw.PrimaryName)
                    : rw.ThresholdFor(rw.SecondaryName);
                if (other is not null) return other;
            }
        }
        var any = airport.Runways.FirstOrDefault();
        if (any is not null && !string.IsNullOrEmpty(any.SecondaryName))
            return any.ThresholdFor(any.SecondaryName);
        return null;
    }

    private static (double lat, double lon)? DestinationThreshold(AirportData airport, string? runwayName)
    {
        if (!string.IsNullOrEmpty(runwayName))
        {
            var rw = airport.FindRunway(runwayName);
            var t = rw?.ThresholdFor(runwayName);
            if (t is not null) return t;
        }
        var any = airport.Runways.FirstOrDefault();
        if (any is not null && !string.IsNullOrEmpty(any.PrimaryName))
            return any.ThresholdFor(any.PrimaryName);
        return null;
    }

    /// <summary>Point at fraction t along the great-circle path from p1 to p2.</summary>
    public static (double lat, double lon) GreatCirclePoint(double lat1, double lon1, double lat2, double lon2, double t)
    {
        // Slerp on the sphere.
        var d = GeoMath.DistanceNm(lat1, lon1, lat2, lon2) * GeoMath.MetersPerNm / GeoMath.EarthRadiusMeters;
        if (d < 1e-9) return (lat1, lon1);
        var br = GeoMath.ToRad(GeoMath.BearingDeg(lat1, lon1, lat2, lon2));
        var lat1r = GeoMath.ToRad(lat1); var lon1r = GeoMath.ToRad(lon1);
        var angular = d * t;
        var lat2r = Math.Asin(Math.Sin(lat1r) * Math.Cos(angular) + Math.Cos(lat1r) * Math.Sin(angular) * Math.Cos(br));
        var lon2r = lon1r + Math.Atan2(Math.Sin(br) * Math.Sin(angular) * Math.Cos(lat1r), Math.Cos(angular) - Math.Sin(lat1r) * Math.Sin(lat2r));
        return (GeoMath.ToDeg(lat2r), GeoMath.NormalizeDeg(GeoMath.ToDeg(lon2r)));
    }
}
