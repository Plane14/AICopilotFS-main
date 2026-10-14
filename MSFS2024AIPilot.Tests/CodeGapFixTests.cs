using System;
using System.IO;
using System.Text;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Regression tests for the defects fixed during the code-gap and bug review. Each test
/// names the behaviour that was wrong and pins the corrected behaviour, so a regression
/// reads as a failing test rather than silent garbage.
/// </summary>
public class CodeGapFixTests
{
    // ---------------- Stall-speed mapping ----------------

    [Fact]
    public void CleanAndLandingStallSpeedsMapToVs1AndVs0()
    {
        // flaps_up_stall_speed is the CLEAN stall speed (Vs1); full_flaps_stall_speed is
        // the LANDING stall speed (Vs0). They were assigned the other way round.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "stalls-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "flight_model.cfg"), """
                [FLIGHT_TUNING]
                flaps_up_stall_speed = 70
                full_flaps_stall_speed = 55
                """);

            var parsed = AircraftConfigParser.ParseAircraftConfig(dir);

            Assert.Equal(70, parsed.Vs1);
            Assert.Equal(55, parsed.Vso);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- Landing reference speed ----------------

    [Fact]
    public void CalculateVRefNeverReturnsNaNWhenNoLandingWeightIsPublished()
    {
        // MaxLandingWeight of 0 divided into the weight is NaN, which every "< 50"
        // guard downstream treats as a real number because comparisons with NaN are false.
        var perf = new AircraftPerformance { Vref = 135 };
        perf.MaxLandingWeight = 0; // an aircraft with no flight_performance.cfg

        var vref = PerformanceCalculator.CalculateVRef(perf, 4000, 2);

        Assert.False(double.IsNaN(vref));
        Assert.Equal(135, vref);
    }

    // ---------------- "Cleared for the approach" ----------------

    [Theory]
    [InlineData("CLEARED FOR APPROACH RUNWAY 25L")]
    [InlineData("CLEARED FOR THE APPROACH")]
    [InlineData("CLEARED APPROACH")]
    public void EveryApproachClearanceSpellingIsRecognised(string text)
    {
        var instruction = BeyondAtcBridge.ParseAtcText(text, "TWR");
        Assert.True(instruction.ClearedForApproach);
    }

    [Fact]
    public void DirectToSkipsTheArticleAndNamesTheRealFix()
    {
        // "DIRECT TO THE VOR" used to capture "THE" and look it up as a fix.
        var instruction = BeyondAtcBridge.ParseAtcText("DIRECT TO THE GLASG", "CTR");
        Assert.Equal("GLASG", instruction.DirectToFix);
    }

    [Fact]
    public void DirectToRunwayIsNotAFix()
    {
        var instruction = BeyondAtcBridge.ParseAtcText("DIRECT TO RUNWAY 25", "CTR");
        Assert.Null(instruction.DirectToFix);
    }

    // ---------------- Landing gear retraction ----------------

    [Fact]
    public void GearUpRetractsRatherThanDeploys()
    {
        // "LANDING GEAR UP" used to fire GEAR_DOWN because "UP" did not count as a
        // retraction, extending the gear it was asked to raise.
        var up = ChecklistTextResolver.Resolve("LANDING GEAR", "UP", new ChecklistEnvironment());
        Assert.Contains(("GEAR_UP", 1.0), up.Events);
        Assert.DoesNotContain("GEAR_DOWN", up.Events.Select(e => e.Item1));
        Assert.False(up.ExpectOn);
        Assert.Equal("GEAR HANDLE POSITION", up.VerifySimVar);
    }

    [Fact]
    public void GearRetractAndStowedAlsoRetract()
    {
        Assert.Contains(("GEAR_UP", 1.0),
            ChecklistTextResolver.Resolve("GEAR", "RETRACT", new ChecklistEnvironment()).Events);
        Assert.Contains(("GEAR_UP", 1.0),
            ChecklistTextResolver.Resolve("GEAR", "STOWED", new ChecklistEnvironment()).Events);
    }

    [Fact]
    public void GearDownStillDeploys()
    {
        var down = ChecklistTextResolver.Resolve("GEAR", "DOWN", new ChecklistEnvironment());
        Assert.Contains(("GEAR_DOWN", 1.0), down.Events);
        Assert.True(down.ExpectOn);
    }

    // ---------------- Spoilers ----------------

    [Fact]
    public void SpoilersRetractKeywordIsARetraction()
    {
        var retract = ChecklistTextResolver.Resolve("SPOILERS", "RETRACT", new ChecklistEnvironment());
        Assert.Contains(("SPOILERS_OFF", 1.0), retract.Events);
        Assert.False(retract.ExpectOn);
    }

    [Fact]
    public void SpoilersUpIsStillDeployedBecauseSpeedbrakeUpIsAmbiguous()
    {
        // Unlike the gear, a spoiler/speedbrake "up" can mean deployed, so "UP" is
        // deliberately not a spoiler retraction word.
        var up = ChecklistTextResolver.Resolve("SPOILERS", "UP", new ChecklistEnvironment());
        Assert.Contains(("SPOILERS_ON", 1.0), up.Events);
        Assert.True(up.ExpectOn);
    }

    // ---------------- Flaps ----------------

    [Fact]
    public void FlapsZeroRetracts()
    {
        // "FLAPS 0" used to fire FLAPS_DOWN and then confirm a handle that was already up.
        var zero = ChecklistTextResolver.Resolve("FLAPS", "0", new ChecklistEnvironment());
        Assert.Contains(("FLAPS_UP", 1.0), zero.Events);
        Assert.False(zero.ExpectOn);
        Assert.Equal("FLAPS HANDLE INDEX", zero.VerifySimVar);
    }

    [Fact]
    public void FlapsDetentAboveFourStillCommandsAndVerifies()
    {
        // A detent with no indexed event used to fire FLAPS_DOWN with no verification.
        var five = ChecklistTextResolver.Resolve("FLAPS", "5", new ChecklistEnvironment());
        Assert.Contains(("FLAPS_DOWN", 1.0), five.Events);
        Assert.Equal("FLAPS HANDLE INDEX", five.VerifySimVar);
    }

    // ---------------- Fix position ----------------

    [Fact]
    public void ApproachLegHasPositionRejectsNullIslandAndNaN()
    {
        Assert.True(new ApproachLeg { Latitude = 40.5, Longitude = -73.9 }.HasPosition);
        Assert.False(new ApproachLeg { Latitude = 0, Longitude = 0 }.HasPosition);
        Assert.False(new ApproachLeg { Latitude = double.NaN, Longitude = 1 }.HasPosition);
    }

    [Fact]
    public void NavFixHasPositionRejectsNullIslandAndNaN()
    {
        Assert.True(new NavFix { Latitude = 40.5, Longitude = -73.9 }.HasPosition);
        Assert.False(new NavFix { Latitude = 0, Longitude = 0 }.HasPosition);
        Assert.False(new NavFix { Latitude = double.NaN, Longitude = 1 }.HasPosition);
    }

    // ---------------- DME arc radius ----------------

    [Fact]
    public void ArcToFixUsesThePublishedRhoDistanceAsRadius()
    {
        // An arc to a fix is a DME arc: every point is RhoMeters from the reference
        // navaid. The leg's published length (RouteDistance) was used as the radius, so
        // the arc was swept at the wrong distance and cut the corner it prevents.
        const double centerLat = 40.0, centerLon = -73.0;
        const double rho = 5000;

        // Two points on the arc, at bearings 0 and 90 from the reference navaid.
        var from = Geo.OffsetByMeters(centerLat, centerLon, 0, rho);
        var terminator = Geo.OffsetByMeters(centerLat, centerLon, rho, 0);

        var leg = new ApproachLeg
        {
            PathType = LegPathType.ArcToFix,
            OriginLatitude = centerLat,
            OriginLongitude = centerLon,
            RhoMeters = rho,
            RouteDistance = 9000, // Wrong on purpose: must not be used as the radius.
            Latitude = terminator.lat,
            Longitude = terminator.lon,
            Turn = ProcedureTurn.Right
        };

        var points = ArcGeometry.Points(leg, from.lat, from.lon);

        Assert.NotEmpty(points);
        foreach (var p in points)
        {
            Assert.True(Math.Abs(Geo.DistanceMeters(centerLat, centerLon, p.lat, p.lon) - rho) < 5,
                "an arc-to-fix point was not at the published DME distance");
        }
    }
}

