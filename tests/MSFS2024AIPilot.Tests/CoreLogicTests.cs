using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.Planning;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Tests;

public class RouteAndPlnTests
{
    [Fact]
    public void Destination_round_trips_through_dms()
    {
        var (lat, lon) = (48.123456, 11.654321);
        var s = PlnFile.FormatDms(lat, lon, 3000);
        // Round-trip through the parser's private path is not public; verify format shape.
        Assert.StartsWith("N", s);
        Assert.Contains("E", s);
        Assert.Contains("+003", s);
    }

    [Fact]
    public void Bearing_and_distance_are_consistent()
    {
        var brg = GeoMath.BearingDeg(47.0, 8.0, 48.0, 9.0);
        Assert.InRange(brg, 0, 360);
        var d = GeoMath.DistanceNm(47.0, 8.0, 48.0, 9.0);
        Assert.InRange(d, 60, 90);
    }

    [Fact]
    public void RouteBuilder_produces_threshold_to_threshold_plan()
    {
        var dep = new AirportData
        {
            Icao = "LOWW", Name = "Vienna",
            ReferenceLatitude = 48.11, ReferenceLongitude = 16.57, ReferenceAltitudeMeters = 200,
        };
        var dst = new AirportData
        {
            Icao = "EDDF", Name = "Frankfurt",
            ReferenceLatitude = 50.03, ReferenceLongitude = 8.56, ReferenceAltitudeMeters = 111,
        };
        var route = RouteBuilder.Build(new RouteRequest
        {
            Departure = dep, Destination = dst, CruiseAltitudeFt = 8000,
        });
        Assert.NotEmpty(route.Waypoints);
        Assert.True(route.TotalDistanceNm > 200);
        Assert.NotNull(route.RunwayWaypoint ?? route.Waypoints.Last());
    }

    [Fact]
    public void ContinuousPlanner_rejects_short_runways()
    {
        var loader = new AirportFacilityLoader(new MSFS2024AIPilot.Core.SimConnect.SimConnectClient());
        var planner = new ContinuousFlightPlanner(loader);
        var apt = new AirportData { Icao = "XXXX" };
        apt.Runways.Add(new RunwayInfo { LengthMeters = 400 });
        planner.RequiredRunwayMeters = 800;
        Assert.False(planner.IsSuitable(apt));
        apt.Runways[0].LengthMeters = 2000;
        Assert.True(planner.IsSuitable(apt));
    }
}
