using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class AtcParserTests
{
    [Fact]
    public void ParsesRunwayAssignment()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Runway 25L cleared for takeoff", "TWR");
        Assert.Equal("25L", inst.Runway);
        Assert.True(inst.ClearedForTakeoff);
    }

    [Fact]
    public void ParsesRunwayWithoutDesignator()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Cleared to land runway 09", "TWR");
        Assert.Equal("09", inst.Runway);
        Assert.True(inst.ClearedToLand);
    }

    [Fact]
    public void ParsesFlightLevelAltitude()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Climb and maintain FL350", "DEP");
        Assert.Equal(35000, inst.AltitudeFt);
    }

    [Fact]
    public void ParsesAltitudeInFeet()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Descend and maintain 5000 feet", "APP");
        Assert.Equal(5000, inst.AltitudeFt);
    }

    [Fact]
    public void ParsesHeading()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Turn right heading 270", "APP");
        Assert.Equal(270, inst.HeadingDeg);
    }

    [Fact]
    public void ParsesSpeed()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Reduce speed to 210 knots", "APP");
        Assert.Equal(210, inst.SpeedKnots);
    }

    [Fact]
    public void ParsesMach()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Reduce Mach to 0.78", "CTR");
        Assert.Equal(0.78, inst.Mach);
    }

    [Fact]
    public void ParsesSquawk()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Squawk 4123", "GND");
        Assert.Equal("4123", inst.Squawk);
    }

    [Fact]
    public void ParsesTaxiRoute()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Taxi to holding point runway 25 via Alpha, Bravo, Charlie", "GND");
        Assert.True(inst.IsTaxiInstruction);
        Assert.Contains("ALPHA", inst.TaxiWaypoints);
        Assert.Contains("BRAVO", inst.TaxiWaypoints);
        Assert.Contains("CHARLIE", inst.TaxiWaypoints);
    }

    [Fact]
    public void ParsesHoldShort()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Taxi via Alpha and hold short of Bravo", "GND");
        Assert.True(inst.IsTaxiInstruction);
        Assert.True(inst.HoldShort);
    }

    [Fact]
    public void ParsesCrossRunway()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Cross runway 25 at Alpha", "GND");
        Assert.True(inst.CrossRunway);
    }

    [Fact]
    public void ParsesHandoff()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Contact Approach on @119.500", "DEP");
        Assert.True(inst.IsHandoff);
        Assert.Equal("119.500", inst.HandoffFrequency);
    }

    [Fact]
    public void ParsesLineUpAndWait()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Runway 25L, line up and wait", "TWR");
        Assert.True(inst.LineUpAndWait);
    }

    [Fact]
    public void ParsesGoAround()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Go around, fly runway heading", "TWR");
        Assert.True(inst.GoAround);
    }

    [Fact]
    public void ParsesHoldPosition()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Hold position, traffic on runway", "TWR");
        Assert.True(inst.HoldPosition);
    }

    [Fact]
    public void ParsesClearedForApproach()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Cleared for the approach runway 25L", "APP");
        Assert.True(inst.ClearedForApproach);
    }

    [Fact]
    public void ParsesResumeOwnNavigation()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Resume own navigation, direct to waypoint", "CTR");
        Assert.True(inst.ResumeOwnNavigation);
    }

    [Fact]
    public void ParsesBacktrack()
    {
        // The phrase a controller uses for a runway the aircraft has to enter and then taxi
        // up, which is how an insufficient takeoff run is dealt with.
        var inst = BeyondAtcBridge.ParseAtcText("Enter runway 08 and backtrack, line up and wait", "TWR");
        Assert.True(inst.Backtrack);
        Assert.True(inst.IsTaxiInstruction);
        Assert.Equal("08", inst.Runway);
    }

    [Fact]
    public void ParsesBackTaxiAsABacktrack()
    {
        // "Back taxi" is the same clearance with the words the other way round.
        var inst = BeyondAtcBridge.ParseAtcText("Back taxi runway 25R, report ready", "TWR");
        Assert.True(inst.Backtrack);
    }

    [Fact]
    public void AnOrdinaryTaxiIsNotABacktrack()
    {
        var inst = BeyondAtcBridge.ParseAtcText("Taxi to holding point runway 27 via A B C", "GND");
        Assert.False(inst.Backtrack);
    }

    // ------------------------------------------------------------------ altitude

    /// <summary>
    /// Every altitude spelling a controller uses has to resolve to the same feet value. The
    /// old parser read "FLIGHT LEVEL 100" as nothing at all and "CLIMB TO FL350" as 350,
    /// which was then corrected by a second pass that only ran because the first had failed.
    /// </summary>
    public class AltitudeParsingTests
    {
        [Theory]
        [InlineData("Climb and maintain FL350")]
        [InlineData("Climb to FL 350")]
        [InlineData("Climb to FL350")]
        [InlineData("Climb to flight level 350")]
        [InlineData("Climb and maintain flight level 350")]
        public void EveryFlightLevelSpellingReadsHundredsOfFeet(string text)
            => Assert.Equal(35000, BeyondAtcBridge.ParseAtcText(text, "CTR").AltitudeFt);

    [Theory]
    [InlineData("Descend to 3000 feet")]
    [InlineData("Descend to 3000 ft")]
    [InlineData("Descend to 3,000")]
    [InlineData("Maintain 3000 feet")]
    public void EveryFeetSpellingReadsFeet(string text)
        => Assert.Equal(3000, BeyondAtcBridge.ParseAtcText(text, "CTR").AltitudeFt);

        [Theory]
        [InlineData("Climb to 10000 feet")]
        [InlineData("Climb to 10,000 feet")]
        public void ThousandsSeparatorsAreIgnored(string text)
            => Assert.Equal(10000, BeyondAtcBridge.ParseAtcText(text, "CTR").AltitudeFt);

    [Fact]
    public void ADoubleQuantityOnTheSameVerbStillReadsAsAnAltitude()
    {
        // "Descend and maintain 3000" is a feet spoken with no unit at all, which the
        // pattern previously missed because it only accepted "AND MAINTAIN" once.
        var inst = BeyondAtcBridge.ParseAtcText("Descend and maintain 3000", "CTR");
        Assert.Equal(3000, inst.AltitudeFt);
    }

    [Fact]
    public void ASpeedInstructionIsNotAnAltitude()
        {
            // "MAINTAIN 250 KNOTS" used to be read as an altitude of 250 ft because the
            // altitude pattern matched any number after the verb.
            var inst = BeyondAtcBridge.ParseAtcText("Maintain 250 knots", "CTR");
            Assert.Null(inst.AltitudeFt);
            Assert.Equal(250, inst.SpeedKnots);
        }

        [Fact]
        public void ASpeedReductionIsNotAnAltitude()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Reduce speed to 210 knots", "APP");
            Assert.Null(inst.AltitudeFt);
        }

        [Fact]
        public void NoAltitudeIsOnlyNull()
        {
            Assert.Null(BeyondAtcBridge.ParseAtcText("Cleared for the approach runway 25L", "APP").AltitudeFt);
        }
    }

    // -------------------------------------------------------------------- speed

    public class SpeedParsingTests
    {
        [Fact]
        public void AMachNumberWithADecimalIsAMachNumber()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Reduce Mach to 0.78", "CTR");
            Assert.Equal(0.78, inst.Mach);
            Assert.Null(inst.SpeedKnots);
        }

        [Fact]
        public void TheSpokenMachShorthandIsAMachNumber()
        {
            // "MACH 78" is how a controller says 0.78, and the old parser read the 78 as an
            // indicated airspeed.
            var inst = BeyondAtcBridge.ParseAtcText("Maintain Mach 78", "CTR");
            Assert.Equal(0.78, inst.Mach);
            Assert.Null(inst.SpeedKnots);
        }

        [Fact]
        public void AnAirspeedIsStillAnAirspeed()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Reduce speed to 210 knots", "APP");
            Assert.Equal(210, inst.SpeedKnots);
            Assert.Null(inst.Mach);
        }
    }

    // ------------------------------------------------------------------ heading

    public class HeadingParsingTests
    {
        [Theory]
        [InlineData("Turn right heading 270", 270)]
        [InlineData("Turn left to heading 030", 30)]
        [InlineData("Fly heading 250", 250)]
        [InlineData("Heading 250", 250)]
        [InlineData("HDG 250", 250)]
        [InlineData("Turn to 90 degrees", 90)]
        public void EveryHeadingSpellingIsRead(string text, int expected)
            => Assert.Equal(expected, BeyondAtcBridge.ParseAtcText(text, "APP").HeadingDeg);

        [Fact]
        public void ARelativeTurnIsNotAnAbsoluteHeading()
        {
            // "Turn left 10 degrees" is measured from the heading the aircraft is already
            // on, which the parser does not know: the number is not a heading assignment.
            // Reading it as one steered the bug at 010 - a ten-degree correction flown as
            // a heading ninety degrees off course.
            var inst = BeyondAtcBridge.ParseAtcText("Turn left 10 degrees", "APP");
            Assert.Null(inst.HeadingDeg);
        }

        [Fact]
        public void AHeadingOf360NormalisesToZero()
        {
            // 360 is the same direction as 0, not a direction a pilot turns to.
            var inst = BeyondAtcBridge.ParseAtcText("Heading 360", "APP");
            Assert.Equal(0, inst.HeadingDeg);
        }

        [Fact]
        public void FlyRunwayHeadingIsNotANumericHeading()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Go around, fly runway heading", "TWR");
            Assert.Null(inst.HeadingDeg);
            Assert.True(inst.GoAround);
        }
    }

    // ------------------------------------------------------------------ handoff

    public class HandoffParsingTests
    {
        [Fact]
        public void AHandoffWithAFrequencySetsIt()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Contact Approach on 119.500", "DEP");
            Assert.True(inst.IsHandoff);
            Assert.Equal("119.500", inst.HandoffFrequency);
        }

        [Fact]
        public void AHandoffNamedWithAnAtSignIsUnchanged()
        {
            var inst = BeyondAtcBridge.ParseAtcText("Contact Approach on @119.500", "DEP");
            Assert.True(inst.IsHandoff);
            Assert.Equal("119.500", inst.HandoffFrequency);
        }

        [Fact]
        public void AHandoffWithNoFrequencyTunesNothing()
        {
            // The old test matched any digit or even a sentence-ending period, so it
            // reported a handoff with no frequency and the handler then had nothing to tune
            // to. A frequency is what makes it a handoff.
            var inst = BeyondAtcBridge.ParseAtcText("Contact tower", "DEP");
            Assert.False(inst.IsHandoff);
            Assert.Null(inst.HandoffFrequency);
        }
    }
}

public class GeoProjectionTests
{
    private const double ThresholdLat = 40.0;
    private const double ThresholdLon = -70.0;

    // OffsetByMeters scales degrees with the ellipsoid's local metres-per-degree and
    // DistanceMeters uses the sphere's, and the two disagree by about 0.15% - 1.4 m in a
    // thousand. The projection's direction and the ratio of its parts are right either way,
    // so the tests allow that model difference rather than asserting an impossible precision.
    private const double ToleranceMeters = 3.0;

    // A 3000 m runway pointing north, long enough to measure along.
    private static (double lat, double lon) OffsetAlong(double bearingDeg, double meters) =>
        Geo.OffsetByMeters(
            ThresholdLat, ThresholdLon,
            Math.Sin(Geo.ToRadians(bearingDeg)) * meters,
            Math.Cos(Geo.ToRadians(bearingDeg)) * meters);

    [Fact]
    public void TheThresholdItselfProjectsToZero()
    {
        var (along, offset) = Geo.ProjectOntoSegment(
            ThresholdLat, ThresholdLon, ThresholdLat, ThresholdLon, 40.01, -70.0);

        Assert.Equal(0, along, 3);
        Assert.Equal(0, offset, 3);
    }

    [Fact]
    public void APointAlongTheAxisHasNoOffset()
    {
        var (lat, lon) = OffsetAlong(0, 1000);
        var (along, offset) = Geo.ProjectOntoSegment(
            lat, lon, ThresholdLat, ThresholdLon, ThresholdLat + 0.01, ThresholdLon);

        Assert.InRange(along, 1000 - ToleranceMeters, 1000 + ToleranceMeters);
        Assert.Equal(0, offset, 3);
    }

    [Fact]
    public void APointBesideTheAxisHasAFullOffsetAndNoLength()
    {
        var (lat, lon) = Geo.OffsetByMeters(ThresholdLat, ThresholdLon, 50, 0);
        var (along, offset) = Geo.ProjectOntoSegment(
            lat, lon, ThresholdLat, ThresholdLon, ThresholdLat + 0.01, ThresholdLon);

        Assert.InRange(offset, 50 - ToleranceMeters, 50 + ToleranceMeters);
        Assert.Equal(0, along, 3);
    }

    [Fact]
    public void APointBeforeTheStartProjectsNegative()
    {
        var (lat, lon) = OffsetAlong(180, 200);
        var (along, _) = Geo.ProjectOntoSegment(
            lat, lon, ThresholdLat, ThresholdLon, ThresholdLat + 0.01, ThresholdLon);

        Assert.InRange(along, -200 - ToleranceMeters, -200 + ToleranceMeters);
    }

    [Fact]
    public void APointAlongAndAsideSplitsIntoBothComponents()
    {
        var (lat, lon) = Geo.OffsetByMeters(ThresholdLat, ThresholdLon, 30, 1000);
        var (along, offset) = Geo.ProjectOntoSegment(
            lat, lon, ThresholdLat, ThresholdLon, ThresholdLat + 0.01, ThresholdLon);

        Assert.InRange(along, 1000 - ToleranceMeters, 1000 + ToleranceMeters);
        Assert.InRange(offset, 30 - ToleranceMeters, 30 + ToleranceMeters);
    }
}


public class PerformanceCalculatorTests
{
    private static AircraftPerformance CreateTestAircraft() => new()
    {
        Vso = 110,
        Vs1 = 140,
        MaxLandingWeight = 120000,
        TakeoffVSpeeds =
        {
            [0] = new VSpeedTable
            {
                FlapsIndex = 0,
                Entries =
                {
                    new VSpeedEntry { Weight = 100000, V1 = 140, Vr = 145, V2 = 150 },
                    new VSpeedEntry { Weight = 150000, V1 = 160, Vr = 165, V2 = 170 },
                }
            }
        }
    };

    [Fact]
    public void InterpolatesVSpeedBelowRange()
    {
        var perf = CreateTestAircraft();
        var v = PerformanceCalculator.InterpolateVSpeed(perf.TakeoffVSpeeds[0].Entries, 80000, "Vr");
        Assert.Equal(145, v);
    }

    [Fact]
    public void InterpolatesVSpeedAboveRange()
    {
        var perf = CreateTestAircraft();
        var v = PerformanceCalculator.InterpolateVSpeed(perf.TakeoffVSpeeds[0].Entries, 200000, "Vr");
        Assert.Equal(165, v);
    }

    [Fact]
    public void InterpolatesVSpeedInsideRange()
    {
        var perf = CreateTestAircraft();
        var v = PerformanceCalculator.InterpolateVSpeed(perf.TakeoffVSpeeds[0].Entries, 125000, "Vr");
        Assert.Equal(155, v, precision: 1);
    }

    [Fact]
    public void InterpolatesV1Correctly()
    {
        var perf = CreateTestAircraft();
        var v = PerformanceCalculator.InterpolateVSpeed(perf.TakeoffVSpeeds[0].Entries, 100000, "V1");
        Assert.Equal(140, v, precision: 1);
    }

    [Fact]
    public void InterpolatesV2Correctly()
    {
        var perf = CreateTestAircraft();
        var v = PerformanceCalculator.InterpolateVSpeed(perf.TakeoffVSpeeds[0].Entries, 100000, "V2");
        Assert.Equal(150, v, precision: 1);
    }
}
