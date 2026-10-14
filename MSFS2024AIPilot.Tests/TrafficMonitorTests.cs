using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Tests for the traffic awareness: how contacts are decayed and turned into a decision
/// about the runway. Nothing here needs a simulator - the evaluator is fed positions and
/// a runway directly, which is the point of keeping the geometry out of the SimConnect
/// plumbing.
/// </summary>
public class TrafficMonitorTests
{
    private static readonly TrafficOptions Options = new();

    [Fact]
    public void TrafficDatumsMatchTheDecodeIndices()
    {
        // A change here silently shifts every field of every contact, exactly as it does
        // for the aircraft state block.
        Assert.Equal(new[]
        {
            ("PLANE LATITUDE", "degrees"),
            ("PLANE LONGITUDE", "degrees"),
            ("PLANE ALTITUDE", "feet"),
            ("PLANE HEADING DEGREES TRUE", "degrees"),
            ("GROUND VELOCITY", "knots"),
            ("AIRSPEED INDICATED", "knots"),
            ("SIM ON GROUND", "bool"),
            ("IS USER SIM", "bool"),
        }, TrafficMonitor.Datums);
    }

    [Fact]
    public void TrafficIsReadThroughAnObjectTypeRequest()
    {
        // There is no traffic-list simulator variable, so an object-type request is the
        // only way to observe other aircraft. If this disappears, traffic blindness
        // returns silently.
        //
        // The request ID an existing request was issued with is passed back in, so a
        // refresh re-issues rather than allocating another persistent request: an
        // object-type request cannot be cancelled, so a fresh one per refresh
        // accumulated for the whole session.
        var request = typeof(SimConnectManager).GetMethod("RequestDataOnSimObjectType",
            new[] { typeof(uint), typeof(uint), typeof(SimObjectType), typeof(uint) });
        Assert.NotNull(request);
        Assert.True(request!.GetParameters()[3].HasDefaultValue,
            "the existing request ID must stay optional so a first request omits it");
    }

    [Fact]
    public void NoTrafficMeansRunwayEntryIsSafe()
    {
        var own = OwnState();

        var assessment = TrafficEvaluatorFor(own);

        Assert.Empty(assessment.Contacts);
        Assert.False(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void AContactBeyondTheConflictRangeIsNotAConflict()
    {
        var own = OwnState();
        // 12 NM north, well outside the 5 NM conflict range.
        var far = Contact(
            lat: own.Latitude + 12.0 / 60.0,
            lon: own.Longitude,
            altitudeFt: own.Altitude + 500);

        var assessment = TrafficEvaluatorFor(own, far);

        Assert.Single(assessment.Contacts);
        Assert.Empty(assessment.AirborneConflicts);
        Assert.False(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void AnAirborneContactInsideRangeAndVerticalGapIsAConflict()
    {
        var own = OwnState();
        var near = Contact(
            lat: own.Latitude + 2.0 / 60.0,
            lon: own.Longitude,
            altitudeFt: own.Altitude + 400);

        var assessment = TrafficEvaluatorFor(own, near);

        Assert.Single(assessment.AirborneConflicts);
        Assert.True(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void VerticalSeparationAloneDoesNotMakeAConflict()
    {
        var own = OwnState();
        // Two miles away but 3000 ft above: no risk.
        var high = Contact(
            lat: own.Latitude + 2.0 / 60.0,
            lon: own.Longitude,
            altitudeFt: own.Altitude + 3000);

        var assessment = TrafficEvaluatorFor(own, high);

        Assert.Empty(assessment.AirborneConflicts);
        Assert.False(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void GroundTrafficOnTheRunwayOccupiesIt()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        var onRunway = Contact(
            lat: runway.Latitude,
            lon: runway.Longitude,
            altitudeFt: Geo.MetersToFeet(runway.AltitudeMeters),
            onGround: true);

        var assessment = TrafficEvaluatorFor(own, runway, onRunway);

        Assert.NotNull(assessment.DepartureRunwayOccupant);
        Assert.True(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void GroundTrafficOffTheRunwayDoesNotOccupyIt()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        // Three nautical miles from the runway, on the ground.
        var onTaxiway = Contact(
            lat: runway.Latitude + 3.0 / 60.0,
            lon: runway.Longitude,
            altitudeFt: Geo.MetersToFeet(runway.AltitudeMeters),
            onGround: true);

        var assessment = TrafficEvaluatorFor(own, runway, onTaxiway);

        Assert.Null(assessment.DepartureRunwayOccupant);
        Assert.False(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void GroundTrafficAtTheEndOfTheRunwayOccupiesIt()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        // On the centreline at the far end: point-to-segment distance, not distance to
        // the midpoint, is what occupancy has to measure.
        var (endLat, endLon) = runway.SecondaryEnd;
        var atEnd = Contact(endLat, endLon, Geo.MetersToFeet(runway.AltitudeMeters), onGround: true);

        var assessment = TrafficEvaluatorFor(own, runway, atEnd);

        Assert.NotNull(assessment.DepartureRunwayOccupant);
        Assert.True(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void AnAircraftOnFinalBlocksTheRunway()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        // On the 3 degree path three NM out, pointing at the threshold.
        var (thresholdLat, thresholdLon) = runway.PrimaryEnd;
        var outbound = Geo.OffsetByMeters(
            thresholdLat, thresholdLon, -3.0 * 1852 * Math.Sin(Geo.ToRadians(runway.HeadingDegTrue)),
            -3.0 * 1852 * Math.Cos(Geo.ToRadians(runway.HeadingDegTrue)));

        var onFinal = Contact(
            lat: outbound.lat,
            lon: outbound.lon,
            altitudeFt: Geo.MetersToFeet(runway.AltitudeMeters) + 3.0 * 318,
            headingTrue: runway.HeadingDegTrue,
            groundSpeedKt: 140);

        var assessment = TrafficEvaluatorFor(own, runway, onFinal);

        Assert.NotNull(assessment.ArrivalOnDepartureRunway);
        Assert.True(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void AnAircraftTooHighToBeOnFinalDoesNotBlock()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        var (thresholdLat, thresholdLon) = runway.PrimaryEnd;
        var outbound = Geo.OffsetByMeters(
            thresholdLat, thresholdLon, -3.0 * 1852 * Math.Sin(Geo.ToRadians(runway.HeadingDegTrue)),
            -3.0 * 1852 * Math.Cos(Geo.ToRadians(runway.HeadingDegTrue)));

        // 3000 ft above the runway at three miles out is not on a 3 degree path.
        var circling = Contact(
            lat: outbound.lat,
            lon: outbound.lon,
            altitudeFt: Geo.MetersToFeet(runway.AltitudeMeters) + 3000,
            headingTrue: runway.HeadingDegTrue);

        var assessment = TrafficEvaluatorFor(own, runway, circling);

        Assert.Null(assessment.ArrivalOnDepartureRunway);
        Assert.False(assessment.RunwayEntryUnsafe);
    }

    [Fact]
    public void AStaleContactIsDropped()
    {
        var tracker = new ContactTracker();
        tracker.Add(Contact(lat: 41.3, lon: 2.05, altitudeFt: 5000));

        // A live contact survives any positive timeout.
        Assert.Single(tracker.RemoveStale(TimeSpan.FromSeconds(60)));

        // Aged beyond the timeout it is gone, so the aircraft stops waiting on traffic
        // the simulator is no longer reporting.
        Assert.Empty(tracker.RemoveStale(TimeSpan.Zero));
    }

    [Fact]
    public void AContactReplacesAnEarlierReportOfTheSameObject()
    {
        var tracker = new ContactTracker();
        tracker.Add(Contact(lat: 41.3, lon: 2.05, altitudeFt: 5000));
        tracker.Add(Contact(lat: 41.4, lon: 2.05, altitudeFt: 5000));

        Assert.Single(tracker.RemoveStale(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void TheUsersOwnAircraftIsNotCounted()
    {
        var tracker = new ContactTracker();
        tracker.Add(Contact(lat: 41.3, lon: 2.05, altitudeFt: 5000, isUser: true));

        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.RemoveStale(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void AssessmentDescriptionReportsWhatIsBlockingEntry()
    {
        var own = OwnState();
        var runway = RunwayFor(own);

        var onRunway = Contact(
            lat: runway.Latitude,
            lon: runway.Longitude,
            altitudeFt: Geo.MetersToFeet(runway.AltitudeMeters),
            onGround: true);

        var assessment = TrafficEvaluatorFor(own, runway, onRunway);

        Assert.Contains("runway occupied", assessment.Describe());
    }

    [Fact]
    public void EmptyAssessmentDescribesNoTraffic()
    {
        Assert.Contains("no traffic", new TrafficAssessment().Describe());
    }

    [Fact]
    public void ClosingSpeedUsesTheLineOfSightComponent()
    {
        var own = OwnState();
        // Contact two NM north flying due north, away from the aircraft.
        var departing = Contact(
            lat: own.Latitude + 2.0 / 60.0,
            lon: own.Longitude,
            altitudeFt: own.Altitude,
            headingTrue: 0,
            groundSpeedKt: 150);

        var assessment = TrafficEvaluatorFor(own, departing);

        var original = Assert.Single(assessment.Contacts);
        // Flying away up the line means negative closing speed, not a head-on closure.
        Assert.True(original.ClosingSpeedKt < -100, $"expected a negative closing speed, was {original.ClosingSpeedKt}");
    }

    /// <summary>
    /// Runs the evaluator without a runway, for the airborne-conflict cases.
    /// </summary>
    private static TrafficAssessment TrafficEvaluatorFor(
        AircraftState own, TrafficContact single) =>
        TrafficEvaluator.Evaluate(new List<TrafficContact> { single }, own, null, Options);

    /// <summary>Runs the evaluator against a runway and one contact.</summary>
    private static TrafficAssessment TrafficEvaluatorFor(
        AircraftState own, RunwayInfo runway, TrafficContact contact) =>
        TrafficEvaluator.Evaluate(new List<TrafficContact> { contact }, own, runway, Options);

    /// <summary>Runs the evaluator with no traffic at all.</summary>
    private static TrafficAssessment TrafficEvaluatorFor(AircraftState own) =>
        TrafficEvaluator.Evaluate(new List<TrafficContact>(), own, null, Options);

    private static TrafficContact Contact(double lat, double lon, double altitudeFt,
        double headingTrue = 0, double groundSpeedKt = 0, double iasKt = 0,
        bool onGround = false, bool isUser = false) =>
        new()
        {
            ObjectId = 1,
            Latitude = lat,
            Longitude = lon,
            AltitudeMslFt = altitudeFt,
            HeadingDegTrue = headingTrue,
            GroundSpeedKt = groundSpeedKt,
            IndicatedAirspeedKt = iasKt,
            OnGround = onGround,
            IsUserSim = isUser,
            LastSeenUtc = DateTime.UtcNow
        };

    private static AircraftState OwnState() => new()
    {
        Latitude = 41.3,
        Longitude = 2.05,
        Altitude = 5000,
        IndicatedAirspeed = 120
    };

    private static RunwayInfo RunwayFor(AircraftState own) => new()
    {
        Latitude = own.Latitude,
        Longitude = own.Longitude,
        AltitudeMeters = 100,
        HeadingDegTrue = 90,
        LengthMeters = 2000,
        WidthMeters = 45,
        PrimaryNumber = 9,
        PrimaryDesignator = 'L',
        SecondaryNumber = 27,
        SecondaryDesignator = 'R'
    };
}
