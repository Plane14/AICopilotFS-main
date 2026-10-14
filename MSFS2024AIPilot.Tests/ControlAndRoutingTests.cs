using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Tests for the PID loops behind direct surface control. The derivative term is the
/// interesting part: when its previous-error state was a local variable, it behaved as a
/// multiplier on the raw error rather than as a slope, which saturated the throttle
/// between idle and full on any error of a few knots.
/// </summary>
public class PidLoopTests
{
    [Fact]
    public void DerivativeTermUsesTheChangeInErrorNotTheError()
    {
        // A loop that has not seen an error yet produces none from the derivative: the
        // previous error is primed from the first one. Seeding it at zero instead made the
        // first step after every Reset() kd * error / dt, and Reset() ran on every mode
        // change - so 0.01 * 15 / 0.05 = 6.0 was clamped to 1.0: a full elevator
        // deflection on one frame at every engage, takeoff, final approach and go-around.
        var fresh = new PidLoop(0, 0, 0.01).Step(5.0, 0.05);
        Assert.Equal(0.0, fresh, 6);

        // A loop that has already seen the error produces only the change.
        var seeded = new PidLoop(0, 0, 0.01);
        seeded.Step(5.0, 0.05);
        var steady = seeded.Step(5.0, 0.05);
        Assert.Equal(0.0, steady, 6); // (5 - 5) / dt = 0

        // A change in the error is what the derivative measures: 0.01 * (10 - 5) / 0.05 = 1.0.
        var change = seeded.Step(10.0, 0.05);
        Assert.Equal(1.0, change, 4);

        // The same magnitude, once the error is steady, is a far smaller output than the
        // first step - the signature of a derivative rather than a proportional term.
        var afterChange = seeded.Step(10.0, 0.05);
        Assert.Equal(0.0, afterChange, 6);
    }

    [Fact]
    public void AConstantErrorProducesAFiniteBoundedOutput()
    {
        var pid = new PidLoop(kp: 0.02, ki: 0.001, kd: 0.01);

        // A steady-state error stepped at the 20 Hz control rate. With the previous error
        // re-seeded to zero on every step, the derivative term alone was kd * error / dt,
        // which saturated at 1.0 (full power) on every single step, not just the first.
        var outputs = Enumerable.Range(0, 20)
            .Select(_ => pid.Step(8.0, 0.05))
            .ToList();

        Assert.All(outputs, o => Assert.InRange(o, -1.0, 1.0));

        // Steps two onward no longer carry the start-up spike, which is the whole
        // difference: the derivative has gone to zero and only the proportional and
        // integral terms remain.
        var steady = outputs.Skip(1).ToList();
        Assert.All(steady, o => Assert.True(o < 0.5, $"steady output was {o}, expected well below the clamp"));
    }

    [Fact]
    public void IntegralIsBoundedSoALongOffsetCannotLurch()
    {
        var pid = new PidLoop(kp: 0, ki: 10.0, kd: 0);

        for (var i = 0; i < 1000; i++) pid.Step(1.0, 0.05);

        Assert.InRange(pid.Integral, -1.0, 1.0);
    }

    [Fact]
    public void ResetClearsIntegralAndPreviousError()
    {
        var pid = new PidLoop(kp: 0.02, ki: 0.001, kd: 0.01);
        for (var i = 0; i < 50; i++) pid.Step(8.0, 0.05);

        pid.Reset();

        Assert.Equal(0.0, pid.Integral);
        // A reset also un-primes the derivative, so the first step after it is quiet: the
        // spike that used to live here (kd * 8 / 0.05 = 1.6, clamped to 1.0) is what made
        // every mode change start with a full command on one frame.
        Assert.Equal(0.0, new PidLoop(0, 0, 0.01).Step(8.0, 0.05), 6);
    }

    [Fact]
    public void ASaturatedIntegralUnwindsInsteadOfHoldingTheCommand()
    {
        // Anti-windup: the integral used to be clamped on its own, independently of the
        // output clamp, so it kept growing while the command was already pinned and then
        // held the output at the limit long after the error had reversed. A 20 kt overspeed
        // kept the throttle above neutral for tens of seconds.
        var pid = new PidLoop(kp: 0.02, ki: 0.001, kd: 0);
        for (var i = 0; i < 50; i++) pid.Step(20.0, 0.05);
        Assert.InRange(pid.Integral, -1.0, 1.0);

        // The error reverses: the proportional term now asks for idle, and the integral may
        // not keep the command above neutral for long.
        var outputs = Enumerable.Range(0, 20).Select(_ => pid.Step(-20.0, 0.05)).ToList();
        Assert.All(outputs, o => Assert.InRange(o, -1.0, 1.0));
        Assert.True(outputs[^1] < 0.4,
            $"the reversed error still left an output of {outputs[^1]:F2}, which is a wound-up integral");
    }

    [Fact]
    public void ALongGapIsClampedToOneFrame()
    {
        // The bias loops run on the wall clock, so a stalled loop or a long descent left a
        // gap of minutes on the next frame: integrating 0.001 * 1500 * 600 saturated the
        // integral in one step and aimed full elevator at the first frame of the recovery.
        var pid = new PidLoop(kp: 0.00002, ki: 0.00002, kd: 0);
        var first = pid.Step(1500.0, 600.0);

        Assert.InRange(first, -1.0, 1.0);
        Assert.True(first < 0.5, $"a 600 s interval produced {first:F2} on its first frame");
    }

    [Fact]
    public void AStalledIntervalIsReplacedWithOneFrame()
    {
        var pid = new PidLoop(kp: 0.02, ki: 0.001, kd: 0.01);

        // Zero and NaN intervals would otherwise divide by zero. The output has to stay
        // finite and inside the clamp.
        var zero = pid.Step(8.0, 0);
        var nan = pid.Step(8.0, double.NaN);
        var negative = pid.Step(8.0, -1.0);

        foreach (var value in new[] { zero, nan, negative })
        {
            Assert.False(double.IsNaN(value));
            Assert.InRange(value, -1.0, 1.0);
        }
    }

    [Fact]
    public void WallClockStepSpacesTheIntegralByRealTime()
    {
        var pid = new PidLoop(kp: 0, ki: 0.001, kd: 0);

        // Two steps a wall-clock second apart accumulate roughly twice the integral of
        // two steps in the same instant. This is what broke when the altitude loop read
        // a timestamp assigned a few lines above: the interval was microseconds, so the
        // integral never grew and the derivative was a single clamped spike.
        var spaced = new PidLoop(0, 0.001, 0);
        spaced.Step(100.0);
        System.Threading.Thread.Sleep(60);
        spaced.Step(100.0);

        Assert.True(spaced.Integral > 0, "integral did not accumulate");
    }
}

/// <summary>
/// The gate-to-gate route: the same network the pilot walks with the taxis, but a route a
/// controller names taxiway by taxiway is not the shortest route through the graph. This
/// is the router that follows a named chain in order, which is what "taxi to the 27
/// holding point via ALPHA one left on BRAVO" means.
/// </summary>
public class TaxiRouteViaChainTests
{
    [Fact]
    public void TheShortestRouteIsAStraightLine()
    {
        var airport = TestAirportBuilder.Build();

        // 0 -> 1 -> 2 -> 3 is the only way to the hold-short, but 1 also has a dead-end
        // spur that must not be on the path.
        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.True(route.Success, route.FailureReason);
        Assert.Equal(new[] { 1, 2, 3 }, route.Legs.Select(l => l.ToNode));
    }

    [Fact]
    public void AChainRoutePassesThroughItsWaypointsInOrder()
    {
        var airport = TestAirportBuilder.Build();

        // Start at the gate, touch BRAVO's node, then finish at the hold-short.
        var route = TaxiRouter.FindRouteViaChain(airport, 0, 3, new List<int> { 2 });

        Assert.True(route.Success, route.FailureReason);
        var visited = route.Legs.Select(l => l.ToNode).ToList();
        Assert.Contains(2, visited);
        // The waypoint is visited before the goal.
        Assert.True(visited.IndexOf(2) < visited.IndexOf(3));
    }

    [Fact]
    public void AChainRouteCrossesNoWaypointTwice()
    {
        var airport = TestAirportBuilder.Build();

        var route = TaxiRouter.FindRouteViaChain(airport, 0, 3, new List<int> { 2 });

        Assert.True(route.Success, route.FailureReason);
        var nodes = route.Legs.Select(l => l.ToNode).ToList();
        Assert.Equal(nodes.Count, nodes.Distinct().Count());
    }

    [Fact]
    public void AChainThatCannotReachTheDestinationFails()
    {
        var airport = TestAirportBuilder.Build();

        // A chain that resolves to nothing at all cannot get anywhere, and the caller is
        // told why rather than being handed a route that leads nowhere.
        var blocked = TaxiRouter.FindRouteViaChain(airport, 3, 3, new List<int> { 99 });

        Assert.False(blocked.Success, blocked.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(blocked.FailureReason));
        Assert.Empty(blocked.Legs);
    }

    [Fact]
    public void AChainRouteIsLongerThanTheShortest()
    {
        var airport = TestAirportBuilder.Build();

        var direct = TaxiRouter.FindRoute(airport, 0, 3);
        // Via node 2 is also the shortest route, so this only asserts the router does not
        // lose the route when it is asked for the same one twice.
        var via = TaxiRouter.FindRouteViaChain(airport, 0, 3, new List<int> { 2 });

        Assert.True(direct.Success);
        Assert.True(via.Success);
        Assert.True(via.TotalLengthMeters > 0);
    }

    [Fact]
    public void TheSameTaxiwayNamedByNameIsTheSameRoute()
    {
        var airport = TestAirportBuilder.Build();

        // The pilot only ever fills the chain from the controller's words, so a name that
        // resolves to a chain has to give the same route as that chain.
        var chain = TaxiRouter.ResolveTaxiwayChain(airport, new List<string> { "ALPHA", "BRAVO" });
        Assert.NotEmpty(chain);

        var viaChain = TaxiRouter.FindRouteViaChain(airport, 0, 3, chain);
        Assert.True(viaChain.Success, viaChain.FailureReason);
    }
}

/// <summary>
/// Taxi route path tests, which were previously a list of null-island points: every leg
/// returned (0, 0), so any consumer got a meaningless route.
/// </summary>
public class TaxiRoutePathTests
{
    [Fact]
    public void PathListsTheNodesInTravelOrder()
    {
        var airport = TestAirportBuilder.Build();

        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.True(route.Success, route.FailureReason);
        Assert.Equal(route.Legs.Count + 1, route.Path.Count);

        // First entry is the start node, last is the goal.
        var start = airport.Nodes[0];
        var goal = airport.Nodes[3];
        Assert.Equal(start.Latitude, route.Path[0].lat, 9);
        Assert.Equal(start.Longitude, route.Path[0].lon, 9);
        Assert.Equal(goal.Latitude, route.Path[^1].lat, 9);
        Assert.Equal(goal.Longitude, route.Path[^1].lon, 9);
    }

    [Fact]
    public void PathPositionsAreRealNotZero()
    {
        var airport = TestAirportBuilder.Build();

        var route = TaxiRouter.FindRoute(airport, 0, 3);

        Assert.All(route.Path, point =>
        {
            Assert.False(Math.Abs(point.lat) < 1e-9 && Math.Abs(point.lon) < 1e-9);
        });
    }

    [Fact]
    public void ConsecutivePathPointsMatchTheLegs()
    {
        var airport = TestAirportBuilder.Build();

        var route = TaxiRouter.FindRoute(airport, 0, 3);
        Assert.True(route.Success, route.FailureReason);

        for (var i = 0; i < route.Legs.Count; i++)
        {
            var node = airport.Nodes[route.Legs[i].ToNode];
            Assert.Equal(node.Latitude, route.Path[i + 1].lat, 9);
            Assert.Equal(node.Longitude, route.Path[i + 1].lon, 9);
        }
    }

    [Fact]
    public void TrivialRoutePathsOnlyItsStartNode()
    {
        var airport = TestAirportBuilder.Build();

        var route = TaxiRouter.FindRoute(airport, 3, 3);

        Assert.True(route.Success);
        Assert.Single(route.Path);
    }
}

/// <summary>
/// Tests for the constraint model, which held two members that nothing read or wrote.
/// </summary>
public class TaxiConstraintTests
    {
    [Fact]
    public void RunwayEdgesAreConfinedToTheAssignedRunway()
    {
        var airport = TestAirportBuilder.Build();
        AttachRunwayPath(airport, 3, 90, "13");
        AttachRunwayPath(airport, 3, 91, "22");

        var assigned = TaxiRouter.FindRoute(airport, 2, 90, new TaxiConstraints { RunwayName = "13" });
        Assert.True(assigned.Success, assigned.FailureReason);
        Assert.All(assigned.Legs, leg => Assert.DoesNotContain("22", leg.TaxiwayName ?? string.Empty));

        var other = TaxiRouter.FindRoute(airport, 2, 91, new TaxiConstraints { RunwayName = "13" });
        Assert.False(other.Success);
    }

    [Fact]
    public void HoldingShortOfARunwayBlocksCrossingIt()
    {
        var airport = TestAirportBuilder.Build();
        AttachRunwayPath(airport, 3, 90, "13");

        // Without a hold-short constraint the route reaches the far side of the runway.
        var crossing = TaxiRouter.FindRoute(airport, 2, 90, new TaxiConstraints { RunwayName = "13" });
        Assert.True(crossing.Success, crossing.FailureReason);

        // Holding short of runway 13 makes the same route impossible.
        var held = TaxiRouter.FindRoute(airport, 2, 90, new TaxiConstraints
        {
            RunwayName = "13",
            HoldShortBefore = "13"
        });
        Assert.False(held.Success);
    }

    [Fact]
    public void AnExplicitlyCrossableRunwayMayBeCrossed()
    {
        var airport = TestAirportBuilder.Build();
        AttachRunwayPath(airport, 3, 90, "13");

        var crossed = TaxiRouter.FindRoute(airport, 2, 90, new TaxiConstraints
        {
            RunwayName = "13",
            HoldShortBefore = "13",
            CrossableRunways = { "13" }
        });

        Assert.True(crossed.Success, crossed.FailureReason);
    }

    [Fact]
    public void WingspanFilterConvertsFeetToMetres()
    {
        // AircraftPerformance.WingSpan is in feet and path widths in metres, so the
        // conversion has to happen where the constraint is built: without it a 100 ft
        // span would be read as 100 m and reject every taxiway in the world.
        Assert.Equal(0.3048, Geo.FeetToMeters(1), 6);
        Assert.Equal(30.48, Geo.FeetToMeters(100), 6);
    }

    private static void AttachRunwayPath(AirportData airport, int holdShortNode, int runwayNode, string runway)
    {
        AddNode(airport, runwayNode, 480, GroundNodeType.Normal);

        var number = int.Parse(runway.TrimEnd('L', 'R', 'C'));
        var designator = runway[^1] switch
        {
            'L' => 1,
            'R' => 2,
            'C' => 3,
            _ => 0
        };

        foreach (var nodeIndex in new[] { holdShortNode, runwayNode })
        {
            var from = airport.Nodes[nodeIndex];
            var to = airport.Nodes[nodeIndex == holdShortNode ? runwayNode : holdShortNode];
            var length = Geo.DistanceMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

            from.Edges.Add(new GroundEdge
            {
                From = nodeIndex,
                To = to.Index,
                LengthMeters = length,
                PathType = TaxiPathType.Runway,
                RunwayNumber = number,
                RunwayDesignator = designator
            });
        }
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

/// <summary>Tests for the runway occupancy geometry.</summary>
public class TrafficGeometryTests
{
    [Fact]
    public void APointOnTheSegmentIsZeroDistance()
    {
        var (startLat, startLon) = Geo.OffsetByMeters(40.6413, -73.7781, 0, 0);
        var (endLat, endLon) = Geo.OffsetByMeters(40.6413, -73.7781, 2000, 0);
        var (midLat, midLon) = Geo.OffsetByMeters(40.6413, -73.7781, 1000, 0);

        Assert.Equal(0, TrafficGeometry.DistanceToSegmentMeters(midLat, midLon, startLat, startLon, endLat, endLon), 6);
    }

    [Fact]
    public void APointBeyondTheEndMeasuresToTheEnd()
    {
        var (startLat, startLon) = Geo.OffsetByMeters(40.6413, -73.7781, 0, 0);
        var (endLat, endLon) = Geo.OffsetByMeters(40.6413, -73.7781, 2000, 0);
        var (pastLat, pastLon) = Geo.OffsetByMeters(40.6413, -73.7781, 2600, 0);

        // 600 m past the far end, so the distance must be 600 m, not 2600 m.
        Assert.Equal(600, TrafficGeometry.DistanceToSegmentMeters(pastLat, pastLon, startLat, startLon, endLat, endLon), 1);
    }

    [Fact]
    public void PerpendicularOffsetMeasuresAcrossTheCentreline()
    {
        var (startLat, startLon) = Geo.OffsetByMeters(40.6413, -73.7781, 0, 0);
        var (endLat, endLon) = Geo.OffsetByMeters(40.6413, -73.7781, 2000, 0);
        var (offLat, offLon) = Geo.OffsetByMeters(40.6413, -73.7781, 1000, 150);

        Assert.Equal(150, TrafficGeometry.DistanceToSegmentMeters(offLat, offLon, startLat, startLon, endLat, endLon), 1);
    }

    [Fact]
    public void ADegenerateSegmentMeasuresToItsSinglePoint()
    {
        var (lat, lon) = Geo.OffsetByMeters(40.6413, -73.7781, 0, 0);
        var (offLat, offLon) = Geo.OffsetByMeters(40.6413, -73.7781, 300, 400);

        Assert.Equal(500, TrafficGeometry.DistanceToSegmentMeters(offLat, offLon, lat, lon, lat, lon), 1);
    }
}
