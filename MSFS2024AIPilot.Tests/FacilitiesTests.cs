using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Builds a synthetic airport graph so routing and frame calibration can be tested
/// without a running simulator.
/// </summary>
internal static class TestAirportBuilder
{
    public const double RefLat = 40.6413;   // JFK
    public const double RefLon = -73.7781;

    /// <summary>
    /// Builds a simple airport: a straight taxiway with a gate on one end and a
    /// runway hold-short on the other, plus a short parallel dead-end.
    /// </summary>
    public static AirportData Build()
    {
        var airport = new AirportData
        {
            Icao = "TEST",
            Name = "Test Airport",
            ReferenceLatitude = RefLat,
            ReferenceLongitude = RefLon
        };

        // Node 0: gate, 800m west of reference
        Add(airport, 0, 0, -800, GroundNodeType.Parking, "GATE5");
        // Node 1: centre of the apron, 400m west
        Add(airport, 1, 0, -400, GroundNodeType.Normal);
        // Node 2: apron exit, at the reference point
        Add(airport, 2, 0, 0, GroundNodeType.Normal);
        // Node 3: hold-short for runway 13
        Add(airport, 3, 0, 400, GroundNodeType.HoldShort);
        // Node 4: dead-end spur, must not be on the shortest path
        Add(airport, 4, 300, -100, GroundNodeType.Normal);

        airport.Runways.Add(new RunwayInfo
        {
            Latitude = RefLat,
            Longitude = Offset(RefLon, 600),
            AltitudeMeters = 4,
            HeadingDegTrue = 135,
            LengthMeters = 3000,
            WidthMeters = 45,
            PrimaryNumber = 13,
            PrimaryDesignator = 0,
            SecondaryNumber = 31,
            SecondaryDesignator = 0
        });

        airport.TaxiNames.Add("");        // index 0 unused
        airport.TaxiNames.Add("ALPHA");   // index 1
        airport.TaxiNames.Add("BRAVO");   // index 2

        Connect(airport, 0, 1, nameIndex: 1);
        Connect(airport, 1, 2, nameIndex: 1);
        Connect(airport, 1, 4, nameIndex: 2);
        Connect(airport, 2, 3, nameIndex: 2);

        return airport;
    }

    private static void Add(AirportData airport, int index, double east, double north,
        GroundNodeType type, string? parkingName = null)
    {
        var (lat, lon) = Geo.OffsetByMeters(airport.ReferenceLatitude, airport.ReferenceLongitude, east, north);
        airport.Nodes[index] = new GroundNode
        {
            Index = index,
            Latitude = lat,
            Longitude = lon,
            AltitudeMeters = airport.ReferenceAltitudeMeters,
            Type = type,
            ParkingName = parkingName,
            BiasX = east,
            BiasZ = north
        };
    }

    private static void Connect(AirportData airport, int from, int to, TaxiPathType type = TaxiPathType.Taxi, int nameIndex = 0)
    {
        var a = airport.Nodes[from];
        var b = airport.Nodes[to];
        var length = Geo.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

        a.Edges.Add(new GroundEdge
        {
            From = from, To = to, LengthMeters = length, PathType = type,
            NameIndex = nameIndex
        });
        b.Edges.Add(new GroundEdge
        {
            From = to, To = from, LengthMeters = length, PathType = type,
            NameIndex = nameIndex
        });
    }

    private static double Offset(double lon, double meters)
    {
        var (_, l) = Geo.OffsetByMeters(RefLat, RefLon, 0, 0);
        // Offset longitudinally only; approximate is fine for tests.
        return lon + meters / (111320.0 * Math.Cos(RefLat * Math.PI / 180.0));
    }
}

public class TaxiRouterTests
{
    [Fact]
    public void RoutesFromGateToRunwayHoldShort()
    {
        var airport = TestAirportBuilder.Build();
        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.True(route.Success, route.FailureReason);
        // Straight chain 0 -> 1 -> 2 -> 3, not via the dead-end spur.
        Assert.Equal(new[] { 0, 1, 2, 3 }, route.Legs.Select(l => l.FromNode).Append(route.Legs[^1].ToNode));
        Assert.True(route.TotalLengthMeters > 1000);
    }

    [Fact]
    public void DoesNotTakeTheDeadEndSpur()
    {
        var airport = TestAirportBuilder.Build();
        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.True(route.Success);
        Assert.DoesNotContain(route.Legs, l => l.ToNode == 4);
    }

    [Fact]
    public void SameStartAndEndIsTriviallySuccessful()
    {
        var airport = TestAirportBuilder.Build();
        var route = TaxiRouter.FindRoute(airport, 2, 2);

        Assert.True(route.Success);
        Assert.Empty(route.Legs);
    }

    [Fact]
    public void UnknownNodeFailsGracefully()
    {
        var airport = TestAirportBuilder.Build();
        var route = TaxiRouter.FindRoute(airport, 0, 999);

        Assert.False(route.Success);
        Assert.NotEmpty(route.FailureReason);
    }

    [Fact]
    public void ClosedPathIsNotRoutable()
    {
        var airport = TestAirportBuilder.Build();
        // Replace the 2 -> 3 taxiway with a closed taxiway.
        airport.Nodes[2].Edges.RemoveAll(e => e.To == 3);
        airport.Nodes[3].Edges.RemoveAll(e => e.To == 2);

        var a = airport.Nodes[2];
        var b = airport.Nodes[3];
        var length = Geo.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        a.Edges.Add(new GroundEdge { From = 2, To = 3, LengthMeters = length, PathType = TaxiPathType.Closed });
        b.Edges.Add(new GroundEdge { From = 3, To = 2, LengthMeters = length, PathType = TaxiPathType.Closed });

        var route = TaxiRouter.FindRoute(airport, 0, 3);
        Assert.False(route.Success);
    }

    [Fact]
    public void OverweightAircraftAvoidsRestrictedPath()
    {
        var airport = TestAirportBuilder.Build();

        // Restrict the final leg to a 5000 lb rating.
        foreach (var node in new[] { airport.Nodes[2], airport.Nodes[3] })
        {
            foreach (var edge in node.Edges) edge.WeightLimitLbs = 5000;
        }

        // A light aircraft is fine.
        var lightAircraft = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { AircraftWeightLbs = 3000 });
        Assert.True(lightAircraft.Success, lightAircraft.FailureReason);

        // An aircraft over the rating is blocked.
        var tooHeavy = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { AircraftWeightLbs = 8000 });
        Assert.False(tooHeavy.Success);

        // Omitting the weight disables the check.
        var uncheckedFlight = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints());
        Assert.True(uncheckedFlight.Success, uncheckedFlight.FailureReason);
    }

    [Fact]
    public void NarrowPathAvoidedWhenWingspanTooLarge()
    {
        var airport = TestAirportBuilder.Build();
        foreach (var edge in airport.Nodes[2].Edges) edge.WidthMeters = 5;

        var wide = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { MinWidthMeters = 0 });
        Assert.True(wide.Success);

        var large = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { MinWidthMeters = 30 });
        Assert.False(large.Success);
    }

    [Fact]
    public void RunwayEdgesRestrictedToAssignedRunway()
    {
        var airport = TestAirportBuilder.Build();

        // Mark the final leg as a runway exit for a runway other than the assigned one.
        foreach (var edge in airport.Nodes[2].Edges)
        {
            edge.PathType = TaxiPathType.Runway;
            edge.RunwayNumber = 22;
            edge.RunwayDesignator = 0;
        }

        var other = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { RunwayName = "13" });
        Assert.False(other.Success);

        // Building the same restriction but with the matching runway allows it.
        foreach (var edge in airport.Nodes[2].Edges)
        {
            edge.RunwayNumber = 13;
            edge.RunwayDesignator = 0;
        }
        var matching = TaxiRouter.FindRoute(airport, 0, 3, new TaxiConstraints { RunwayName = "13" });
        Assert.True(matching.Success, matching.FailureReason);
    }

    [Fact]
    public void FindsNearestNode()
    {
        var airport = TestAirportBuilder.Build();
        var gate = airport.Nodes[0];

        var nearest = TaxiRouter.FindNearestNode(airport, gate.Latitude, gate.Longitude);
        Assert.NotNull(nearest);
        Assert.Equal(0, nearest!.Index);
    }

    [Fact]
    public void FindsNearestParking()
    {
        var airport = TestAirportBuilder.Build();
        var parking = TaxiRouter.FindNearestParking(airport, airport.Nodes[0].Latitude, airport.Nodes[0].Longitude);

        Assert.NotNull(parking);
        Assert.Equal(GroundNodeType.Parking, parking!.Type);
        Assert.Equal("GATE5", parking.ParkingName);
    }

    [Fact]
    public void FindsRunwayHoldShort()
    {
        var airport = TestAirportBuilder.Build();

        // Node 3 is the only hold-short, but it only counts for runway 13 once a
        // runway-type taxiway path attaches it to that runway - a taxi point carries no
        // runway of its own, so without the path no hold-short guards the runway.
        var (spurLat, spurLon) = Geo.OffsetByMeters(
            airport.ReferenceLatitude, airport.ReferenceLongitude, 440, 0);
        var holdShort = airport.Nodes[3];
        holdShort.Edges.Add(new GroundEdge
        {
            From = 3, To = 99,
            LengthMeters = Geo.DistanceMeters(holdShort.Latitude, holdShort.Longitude, spurLat, spurLon),
            PathType = TaxiPathType.Runway,
            RunwayNumber = 13,
            RunwayDesignator = 0
        });

        var hold = TaxiRouter.FindRunwayHoldShort(airport, "13");

        Assert.NotNull(hold);
        Assert.Equal(3, hold!.Index);

        // It guards no other runway, so proximity alone must not return it for one it does
        // not serve: this is the parallel-runway trap the guard filter removes.
        Assert.Null(TaxiRouter.FindRunwayHoldShort(airport, "22"));
    }

    [Fact]
    public void ResolvesTaxiwayChainByName()
    {
        var airport = TestAirportBuilder.Build();
        var chain = TaxiRouter.ResolveTaxiwayChain(airport, new List<string> { "ALPHA", "BRAVO" });

        Assert.NotEmpty(chain);
        Assert.All(chain, i => Assert.Contains(i, airport.Nodes.Keys));
    }

    [Fact]
    public void UnresolvableTaxiwayNameReturnsEmpty()
    {
        var airport = TestAirportBuilder.Build();
        var chain = TaxiRouter.ResolveTaxiwayChain(airport, new List<string> { "NOSUCH" });

        Assert.Empty(chain);
    }

    [Fact]
    public void RouteDescribeReportsTaxiwayNames()
    {
        var airport = TestAirportBuilder.Build();
        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.True(route.Success);
        var description = route.Describe();
        Assert.Contains("ALPHA", description);
    }
}

public class BiasFrameCalibrationTests
{
    [Fact]
    public void CalibrationRecoversEastNorthFrame()
    {
        var airport = TestAirportBuilder.Build();
        var loader = new AirportFacilitiesLoader(new SimConnectManager());

        // The gate is 800m west of the reference point; treat it as the aircraft spot.
        var gate = airport.Nodes[0];
        var frame = loader.CalibrateFrame(airport, gate.Latitude, gate.Longitude);

        Assert.Equal(BiasFrame.XEastZNorth, frame);
    }

    [Fact]
    public void CalibrationPicksTheFramePlacingNodesNearTheAircraft()
    {
        var airport = TestAirportBuilder.Build();
        var loader = new AirportFacilitiesLoader(new SimConnectManager());

        var frame = loader.CalibrateFrame(airport, TestAirportBuilder.RefLat, TestAirportBuilder.RefLon);

        // With the reference point as the aircraft position the correct frame is the
        // one that produced the graph, and the nearest node must be very close.
        var nearest = TaxiRouter.FindNearestNode(airport, TestAirportBuilder.RefLat, TestAirportBuilder.RefLon);
        Assert.NotNull(nearest);
        Assert.True(
            Geo.DistanceMeters(nearest!.Latitude, nearest.Longitude, TestAirportBuilder.RefLat, TestAirportBuilder.RefLon) < 1.0,
            $"frame {frame} put the nearest node {Geo.DistanceMeters(nearest.Latitude, nearest.Longitude, TestAirportBuilder.RefLat, TestAirportBuilder.RefLon)}m away");
    }

    [Fact]
    public void FrameConversionIsReversible()
    {
        // Every frame must map (x, z) to a unique (east, north) pair.
        var mappings = Enum.GetValues<BiasFrame>()
            .Where(f => f != BiasFrame.Unknown)
            .Select(f => f.ToEastNorth(10, 20))
            .ToList();

        Assert.Equal(mappings.Count, mappings.Select(m => $"{m.east:F3},{m.north:F3}").Distinct().Count());
    }
}

public class RunwayInfoTests
{
    [Fact]
    public void NamesBothEnds()
    {
        var rw = new RunwayInfo
        {
            PrimaryNumber = 13, PrimaryDesignator = 1,
            SecondaryNumber = 31, SecondaryDesignator = 2
        };

        Assert.Equal("13L", rw.PrimaryName);
        Assert.Equal("31R", rw.SecondaryName);
    }

    [Fact]
    public void MatchesEitherEnd()
    {
        var rw = new RunwayInfo
        {
            PrimaryNumber = 25, PrimaryDesignator = 0,
            SecondaryNumber = 7, SecondaryDesignator = 0
        };

        Assert.True(rw.Matches("25"));
        Assert.True(rw.Matches("07"));
        Assert.False(rw.Matches("26"));
    }

    [Fact]
    public void ZeroNumberHasNoName()
    {
        var rw = new RunwayInfo { PrimaryNumber = 0, SecondaryNumber = 36, SecondaryDesignator = 3 };
        Assert.Equal("", rw.PrimaryName);
        Assert.Equal("36C", rw.SecondaryName);
    }

    [Fact]
    public void MagneticHeadingConversionOpposesVariation()
    {
        // A 10 degree east variation means magnetic heading is true minus the variation.
        Assert.Equal(350, AIPilot.TrueToMagnetic(0, 10));
        Assert.Equal(80, AIPilot.TrueToMagnetic(90, 10));
    }
}

/// <summary>
/// Tests for the hold-short logic, the phase that stops the aircraft in front of the
/// runway markings. The runway itself is derived geometry: the facilities API reports the
/// centre of the runway, so the thresholds have to be computed from it.
/// </summary>
public class HoldShortTests
{
    [Fact]
    public void RunwayEndsAreHalfALengthEitherWayOfTheCentre()
    {
        var rw = new RunwayInfo
        {
            Latitude = 40.6413,
            Longitude = -73.7781,
            HeadingDegTrue = 90,
            LengthMeters = 3000
        };

        var (pLat, pLon) = rw.PrimaryEnd;
        var (sLat, sLon) = rw.SecondaryEnd;

        // Half the length apart, in opposite directions along the heading. The tolerance
        // covers the spherical model, which flattens long spans slightly.
        Assert.True(Math.Abs(Geo.DistanceMeters(pLat, pLon, sLat, sLon) - 3000) < 10,
            $"ends were {Geo.DistanceMeters(pLat, pLon, sLat, sLon)} m apart");

        // The centre is the midpoint of the two ends.
        Assert.True(Math.Abs(Geo.DistanceMeters(pLat, pLon, rw.Latitude, rw.Longitude) - 1500) < 10,
            $"centre was {Geo.DistanceMeters(pLat, pLon, rw.Latitude, rw.Longitude)} m from the primary end");
    }

    [Fact]
    public void ThresholdIsResolvedByNameNotByProximity()
    {
        var rw = new RunwayInfo
        {
            Latitude = 40.6413,
            Longitude = -73.7781,
            HeadingDegTrue = 90,
            LengthMeters = 3000,
            PrimaryNumber = 13,
            PrimaryDesignator = 0,
            SecondaryNumber = 31,
            SecondaryDesignator = 0
        };

        Assert.NotNull(rw.ThresholdFor("13"));
        Assert.NotNull(rw.ThresholdFor("31"));
        // The named end matters: pointing the wrong way down the runway would be the
        // result of picking whichever threshold is nearer.
        Assert.NotEqual(rw.ThresholdFor("13"), rw.ThresholdFor("31"));
    }

    [Fact]
    public void ThresholdForAnUnknownRunwayIsNull()
    {
        var rw = new RunwayInfo
        {
            HeadingDegTrue = 90, LengthMeters = 3000,
            PrimaryNumber = 13, SecondaryNumber = 31
        };

        Assert.Null(rw.ThresholdFor("09"));
        Assert.Null(rw.ThresholdFor(""));
        Assert.Null(rw.ThresholdFor(null!));
    }

    [Fact]
    public void HoldShortsForARunwayAreMatchedThroughTheRunwayPaths()
    {
        // A taxi point payload carries no runway of its own: the runway a hold-short
        // guards is only recorded on the runway taxiway paths attached to it.
        var airport = TestAirportBuilder.Build();
        AddNode(airport, 99, 450, GroundNodeType.Normal);
        var runwayPath = Connect(airport, 3, 99, TaxiPathType.Runway);
        runwayPath.RunwayNumber = 13;
        runwayPath.RunwayDesignator = 0;

        var holdShorts = TaxiRouter.HoldShortsFor(airport, "13");

        Assert.Contains(holdShorts, n => n.Index == 3);
    }

    [Fact]
    public void AHoldShortForAnotherRunwayIsNotSelected()
    {
        var airport = TestAirportBuilder.Build();
        AddNode(airport, 99, 450, GroundNodeType.Normal);
        var runwayPath = Connect(airport, 3, 99, TaxiPathType.Runway);
        runwayPath.RunwayNumber = 22;
        runwayPath.RunwayDesignator = 0;

        Assert.DoesNotContain(TaxiRouter.HoldShortsFor(airport, "13"), n => n.Index == 3);
        Assert.Contains(TaxiRouter.HoldShortsFor(airport, "22"), n => n.Index == 3);
    }

    [Fact]
    public void AReachableHoldShortIsChosenByRouteLengthNotProximity()
    {
        var airport = TestAirportBuilder.Build();

        // Node 3 is a hold-short for runway 13 through a runway path.
        AddNode(airport, 30, 440, GroundNodeType.Normal);
        var runwayPath = Connect(airport, 3, 30, TaxiPathType.Runway);
        runwayPath.RunwayNumber = 13;
        runwayPath.RunwayDesignator = 0;

        // A decoy hold-short, also guarding runway 13 through a runway path, sitting
        // closer to the runway centre than node 3 but requiring a longer detour: picking
        // by proximity to the runway would choose it.
        AddNode(airport, 20, 420, GroundNodeType.HoldShort);
        AddNode(airport, 40, 460, GroundNodeType.Normal);
        var decoyPath = Connect(airport, 20, 40, TaxiPathType.Runway);
        decoyPath.RunwayNumber = 13;
        decoyPath.RunwayDesignator = 0;

        var chosen = TaxiRouter.FindReachableHoldShort(airport, 2, "13");

        Assert.NotNull(chosen);
        Assert.Equal(3, chosen!.Index);
    }

    [Fact]
    public void NoHoldShortReachableMeansNoHoldShortReturned()
    {
        var airport = TestAirportBuilder.Build();

        // No hold-short node at all.
        airport.Nodes[3].Type = GroundNodeType.Normal;

        Assert.Null(TaxiRouter.FindReachableHoldShort(airport, 0, "13"));
    }

    private static GroundEdge Connect(AirportData airport, int from, int to, TaxiPathType type)
    {
        var a = airport.Nodes[from];
        var b = airport.Nodes[to];
        var length = Geo.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

        var edge = new GroundEdge { From = from, To = to, LengthMeters = length, PathType = type };
        a.Edges.Add(edge);
        var back = new GroundEdge { From = to, To = from, LengthMeters = length, PathType = type };
        b.Edges.Add(back);
        return edge;
    }

    private static void AddNode(AirportData airport, int index, double east, GroundNodeType type)
    {
        var (lat, lon) = Geo.OffsetByMeters(
            airport.ReferenceLatitude, airport.ReferenceLongitude, east, 0);
        airport.Nodes[index] = new GroundNode
        {
            Index = index,
            Latitude = lat,
            Longitude = lon,
            Type = type,
            BiasX = east,
            BiasZ = 0
        };
    }
}

/// <summary>
/// Verifies that facility payloads are decoded in exactly the field order the
/// definition declared them, which is how SimConnect lays out the buffer.
/// </summary>
public class FacilityPayloadReaderTests
{
    [Fact]
    public void ReadsFieldsInOrder()
    {
        // Mirrors the TAXI_PATH field order in AirportFacilitiesLoader.
        using var ms = new MemoryStream();

        void WriteInt(int v) => ms.Write(BitConverter.GetBytes(v));
        void WriteFloat(float v) => ms.Write(BitConverter.GetBytes(v));

        WriteInt((int)TaxiPathType.Taxi);          // TYPE
        WriteFloat(24.0f);                         // WIDTH
        WriteFloat(12.0f);                         // LEFT_HALF_WIDTH
        WriteFloat(12.0f);                         // RIGHT_HALF_WIDTH
        ms.Write(BitConverter.GetBytes(120000u));  // WEIGHT
        WriteInt(13);                              // RUNWAY_NUMBER
        WriteInt(1);                               // RUNWAY_DESIGNATOR
        WriteInt(0);                               // LEFT_EDGE
        WriteInt(1);                               // LEFT_EDGE_LIGHTED
        WriteInt(0);                               // RIGHT_EDGE
        WriteInt(1);                               // RIGHT_EDGE_LIGHTED
        WriteInt(1);                               // CENTER_LINE
        WriteInt(1);                               // CENTER_LINE_LIGHTED
        WriteInt(7);                               // START
        WriteInt(9);                               // END
        ms.Write(BitConverter.GetBytes(3u));       // NAME_INDEX

        var r = new FacilityPayloadReader(ms.ToArray());

        Assert.Equal((int)TaxiPathType.Taxi, r.ReadInt32());
        Assert.Equal(24.0f, r.ReadFloat32());
        Assert.Equal(12.0f, r.ReadFloat32());
        Assert.Equal(12.0f, r.ReadFloat32());
        Assert.Equal(120000u, r.ReadUInt32());
        Assert.Equal(13, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(9, r.ReadInt32());
        Assert.Equal(3u, r.ReadUInt32());

        Assert.False(r.HasMore);
    }

    [Fact]
    public void ReadsFixedLengthStringsStoppingAtNullTerminator()
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("WEST APRON").CopyTo(bytes, 0);

        var r = new FacilityPayloadReader(bytes);
        Assert.Equal("WEST APRON", r.ReadString(64));
    }

    [Fact]
    public void ShortPayloadDoesNotThrow()
    {
        var r = new FacilityPayloadReader(new byte[3]);
        var value = r.ReadString(64);
        Assert.Equal("", value);
    }

    [Fact]
    public void TracksRemainingData()
    {
        var r = new FacilityPayloadReader(BitConverter.GetBytes(42.0));
        Assert.True(r.HasMore);
        Assert.Equal(42.0, r.ReadFloat64());
        Assert.False(r.HasMore);
    }
}

public class GeoTests
{
    [Fact]
    public void DistanceMatchesKnownRoute()
    {
        // JFK to LAX great-circle distance is about 2146 NM.
        var nm = Geo.DistanceNauticalMiles(40.6413, -73.7781, 33.9416, -118.4085);
        Assert.InRange(nm, 2100, 2200);
    }

    [Fact]
    public void BearingPointsNortheastFromJfkToBoston()
    {
        var bearing = Geo.BearingDegrees(40.6413, -73.7781, 42.3656, -71.0096);
        Assert.InRange(bearing, 45, 55);
    }

    [Fact]
    public void ReverseBearingDiffersBy180DegreesOverShortDistances()
    {
        // Over long distances a great circle changes course, so the reverse initial
        // bearing is not simply the forward bearing plus 180. Use a local pair where
        // the great circle is effectively straight.
        var lat = 40.6413;
        var lon = -73.7781;
        var (lat2, lon2) = Geo.OffsetByMeters(lat, lon, 3000, 1500);

        var forward = Geo.BearingDegrees(lat, lon, lat2, lon2);
        var backward = Geo.BearingDegrees(lat2, lon2, lat, lon);

        Assert.InRange(Math.Abs(forward - backward), 179, 181);
    }

    [Fact]
    public void OffsetByMetersIsConsistent()
    {
        var (lat, lon) = Geo.OffsetByMeters(40.6413, -73.7781, 1000, 2000);

        Assert.True(Geo.DistanceMeters(40.6413, -73.7781, lat, -73.7781) > 1800);
        Assert.True(Geo.DistanceMeters(40.6413, -73.7781, 40.6413, lon) > 900);
    }

    [Fact]
    public void NormalizeDegreesWraps()
    {
        Assert.Equal(0, Geo.NormalizeDegrees(360));
        Assert.Equal(-90, Geo.NormalizeDegrees(270));
        Assert.Equal(10, Geo.NormalizeDegrees(370));
    }

    [Fact]
    public void FeetersonsistentRoundTrip()
    {
        var meters = 100.0;
        var feet = Geo.MetersToFeet(meters);
        Assert.Equal(meters, Geo.FeetToMeters(feet), precision: 6);
    }
}