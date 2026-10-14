using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The back-taxi decision. A runway is usable from where the taxiway joins it, so the
/// decisive question is how much strip is left ahead of that joining point - which is a
/// question about geometry, because neither the simulator's ATC nor BeyondATC tells an
/// add-on when a runway must be entered and then back-taxed.
/// </summary>
public class BacktrackPlannerTests
{
    private const double Threshold09Lat = 40.0;
    private const double Threshold09Lon = -70.0;
    private const double RunwayLengthMeters = 3000.0;
    private const double RunwayWidthMeters = 45.0;

    // OffsetByMeters works in the ellipsoid's local metres and DistanceMeters in the
    // sphere's, which disagree by about 0.15%. The decision is only right to metres, so the
    // tests allow that model difference rather than asserting an impossible precision.
    private const double ToleranceMeters = 3.0;

    /// <summary>
    /// A 3000 m runway with the 09 end at the threshold above and the 09 roll direction
    /// pointing north, so a departure on 09 rolls from the threshold towards its centre.
    /// The facility payload only carries the runway centre, so the centre is half a length
    /// beyond the threshold.
    /// </summary>
    private static RunwayInfo BuildRunway() => new()
    {
        PrimaryNumber = 9,
        PrimaryDesignator = 0,
        SecondaryNumber = 27,
        SecondaryDesignator = 0,
        Latitude = Threshold09Lat + 1500.0 / 111194.9,
        Longitude = Threshold09Lon,
        HeadingDegTrue = 0,
        LengthMeters = RunwayLengthMeters,
        WidthMeters = RunwayWidthMeters
    };

    /// <summary>A point on the 09 roll axis, in metres beyond the 09 threshold.</summary>
    private static (double lat, double lon) AlongRunway(double meters) =>
        Geo.OffsetByMeters(Threshold09Lat, Threshold09Lon, 0, meters);

    private static void AssertNear(double expected, double actual, double tolerance = ToleranceMeters) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    /// <summary>
    /// The two geodetic helpers this code uses disagree by about 0.15%, so a whole-runway
    /// length is only right to five metres in three thousand.
    /// </summary>
    private static void AssertRunwayLengthNear(double expected, double actual) =>
        Assert.InRange(actual, expected * 0.995, expected * 1.005);

    [Fact]
    public void AThresholdJoinLeavesTheWholeRunway()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(0);

        // 1500 m of roll in a 3000 m runway: there is nothing to gain by turning round.
        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 1500));
        AssertNear(RunwayLengthMeters, BacktrackPlanner.RunwayAheadOf(runway, 0));
    }

    [Fact]
    public void AJoinShortOfTheThresholdStillLeavesTheWholeRunway()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(-100);

        // A taxiway joining before the threshold is still at the start of the roll.
        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 2000));
        AssertNear(RunwayLengthMeters, BacktrackPlanner.RunwayAheadOf(runway, -100));
    }

    [Fact]
    public void AJoinHalfwayAlongNeedsABacktrackForALongRun()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(RunwayLengthMeters / 2);

        // Half the strip is 1500 m and the roll needs 2000 m.
        AssertNear(1500, BacktrackPlanner.RunwayAheadOf(runway, RunwayLengthMeters / 2));
        Assert.True(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 2000));
    }

    [Fact]
    public void TheSameJoinNeedsNoBacktrackForAShortRun()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(RunwayLengthMeters / 2);

        // 1500 m of strip is plenty for a 1000 m roll.
        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 1000));
    }

    [Fact]
    public void AJoinAtTheFarEndAlwaysNeedsABacktrack()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(RunwayLengthMeters - 10);

        AssertNear(10, BacktrackPlanner.RunwayAheadOf(runway, RunwayLengthMeters - 10));
        Assert.True(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 500));
    }

    [Fact]
    public void AJoinPastTheFarEndLeavesNoRunwayAtAll()
    {
        var runway = BuildRunway();

        // Beyond the end there is no strip, and no back-taxi can give it back. That is a
        // routing error rather than a back-taxi, and the pilot is what has to catch it:
        // all this reports is that there is no runway left.
        AssertNear(0, BacktrackPlanner.RunwayAheadOf(runway, RunwayLengthMeters + 50));
    }

    [Fact]
    public void TheRunwayAheadIsMeasuredFromTheAssignedEnd()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(500);

        // 500 m beyond the 09 end leaves 2500 m of 09; the same joining point is 2500 m
        // beyond the 27 end and leaves 500 m of 27, so which end is assigned decides how
        // much strip there is.
        AssertNear(2500, BacktrackPlanner.RunwayAheadOf(runway, 500));
        AssertNear(500, BacktrackPlanner.RunwayAheadOf(runway, 2500));

        // 2500 m of 09 ahead of a 2000 m roll is enough. From the same joining point, 27
        // has 500 m ahead of it, which is not.
        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 2000));
        Assert.True(BacktrackPlanner.RequiresBacktrack(runway, "27", lat, lon, 2000));
    }

    [Fact]
    public void AThresholdIsTheStartOfTheRollNotTheCentre()
    {
        var runway = BuildRunway();
        var threshold = runway.ThresholdFor("09");

        Assert.NotNull(threshold);
        AssertNear(0, Geo.DistanceMeters(
            threshold!.Value.lat, threshold.Value.lon, Threshold09Lat, Threshold09Lon));

        var otherEnd = runway.ThresholdFor("27");
        Assert.NotNull(otherEnd);
        AssertRunwayLengthNear(RunwayLengthMeters, Geo.DistanceMeters(
            threshold.Value.lat, threshold.Value.lon, otherEnd!.Value.lat, otherEnd.Value.lon));
    }

    [Fact]
    public void ATurnPointIsShortOfTheThresholdNotPastIt()
    {
        var runway = BuildRunway();
        var turn = BacktrackPlanner.TurnPointFor(runway, "09", 60);
        Assert.NotNull(turn);

        var threshold = runway.ThresholdFor("09")!;
        AssertNear(60, Geo.DistanceMeters(
            turn!.Value.lat, turn.Value.lon, threshold.Value.lat, threshold.Value.lon));

        // The turn point has to be before the threshold in the roll direction, so the
        // aircraft turns round on the runway rather than off the end of it.
        var (along, offset) = Geo.ProjectOntoSegment(
            turn.Value.lat, turn.Value.lon,
            threshold.Value.lat, threshold.Value.lon,
            runway.PrimaryEnd.lat, runway.PrimaryEnd.lon);

        Assert.True(along < 0, "the turn point is before the threshold");
        AssertNear(0, offset);
    }

    [Fact]
    public void ARunwayWithNoNamedEndHasNoTurnPoint()
    {
        var runway = BuildRunway();
        Assert.Null(BacktrackPlanner.TurnPointFor(runway, "13", 60));
        Assert.Null(BacktrackPlanner.TurnPointFor(runway, "", 60));
    }

    [Fact]
    public void AnUnknownRunwayNeedsNoBacktrack()
    {
        var runway = BuildRunway();
        var (lat, lon) = AlongRunway(2500);

        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "13", lat, lon, 5000));
    }

    [Fact]
    public void AJoinBesideTheRunwayIsReadAsAJoinPointOnIt()
    {
        var runway = BuildRunway();

        // A hold-short line sits beside the paved surface. Where the aircraft joins is that
        // line's projection onto the centreline, which is the whole reason the projection is
        // needed rather than the raw distance.
        var (lat, lon) = Geo.OffsetByMeters(Threshold09Lat, Threshold09Lon, 30, 1000);
        Assert.True(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 2200));
        Assert.False(BacktrackPlanner.RequiresBacktrack(runway, "09", lat, lon, 1800));
    }
}
