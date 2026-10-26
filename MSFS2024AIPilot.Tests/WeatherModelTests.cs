using System;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The weather reactions are pure arithmetic over a heading and a wind/ice/turbulence
/// reading, so they are pinned here without a simulator. A wind calculation is only correct
/// if its sign conventions agree with the way it is used, which is what these assert.
/// </summary>
public class WeatherModelTests
{
    private const double Course = 360;

    [Fact]
    public void DirectHeadwindIsPositiveAndHasNoCrosswind()
    {
        var (headwind, crosswind) = WeatherModel.WindComponents(Course, 20, Course);

        Assert.Equal(20, headwind, 6);
        Assert.Equal(0, crosswind, 6);
    }

    [Fact]
    public void DirectTailwindIsNegativeHeadwind()
    {
        var (headwind, _) = WeatherModel.WindComponents(Course + 180, 15, Course);

        Assert.Equal(-15, headwind, 6);
    }

    [Fact]
    public void WindFromTheRightIsPositiveCrosswind()
    {
        var (headwind, crosswind) = WeatherModel.WindComponents(Course + 90, 10, Course);

        Assert.Equal(0, headwind, 6);
        Assert.Equal(10, crosswind, 6); // from the right (starboard) is positive
    }

    [Fact]
    public void WindFromTheLeftIsNegativeCrosswind()
    {
        var (_, crosswind) = WeatherModel.WindComponents(Course + 270, 10, Course);

        Assert.Equal(-10, crosswind, 6);
    }

    [Fact]
    public void AngledWindResolvesIntoBothComponents()
    {
        var (headwind, crosswind) = WeatherModel.WindComponents(Course + 30, 20, Course);

        Assert.Equal(20 * Math.Cos(Geo.ToRadians(30)), headwind, 6);
        Assert.Equal(20 * Math.Sin(Geo.ToRadians(30)), crosswind, 6);
    }

    [Fact]
    public void CrosswindMagnitudeIgnoresTheSign()
    {
        Assert.Equal(10, WeatherModel.CrosswindMagnitudeKt(Course + 90, 10, Course), 6);
        Assert.Equal(10, WeatherModel.CrosswindMagnitudeKt(Course + 270, 10, Course), 6);
    }

    [Fact]
    public void WindCorrectionAngleCancelsTheCrosswind()
    {
        // A right crosswind needs a right (positive) heading offset; the offset is what
        // cancels the crosswind, so the airspeed's sideways component equals the crosswind.
        const double tas = 120;
        const double crosswind = 18;
        var wca = WeatherModel.WindCorrectionAngleDeg(crosswind, tas);

        Assert.True(wca > 0);
        Assert.Equal(crosswind, tas * Math.Sin(Geo.ToRadians(wca)), 6);
    }

    [Fact]
    public void WindCorrectionAngleSignFollowsTheCrosswind()
    {
        Assert.True(WeatherModel.WindCorrectionAngleDeg(-10, 100) < 0);
        Assert.Equal(
            WeatherModel.WindCorrectionAngleDeg(10, 100),
            -WeatherModel.WindCorrectionAngleDeg(-10, 100), 6);
    }

    [Fact]
    public void WindCorrectionAngleClampsWhenCrosswindExceedsAirspeed()
    {
        Assert.Equal(90, WeatherModel.WindCorrectionAngleDeg(200, 100), 6);
        Assert.Equal(-90, WeatherModel.WindCorrectionAngleDeg(-200, 100), 6);
    }

    [Fact]
    public void WindCorrectionAngleIsZeroAtLowAirspeedOrNoWind()
    {
        Assert.Equal(0, WeatherModel.WindCorrectionAngleDeg(10, 20), 6); // too slow to crab
        Assert.Equal(0, WeatherModel.WindCorrectionAngleDeg(0, 120), 6);
    }

    [Theory]
    [InlineData(20, 10)]   // half the headwind, well under the cap
    [InlineData(16, 8)]
    [InlineData(24, 12)]   // half the headwind, exactly at the cap
    public void ApproachWindIncrementIsHalfTheHeadwind(double headwind, double expected)
    {
        Assert.Equal(expected, WeatherModel.ApproachWindIncrementKt(headwind), 6);
    }

    [Fact]
    public void ApproachWindIncrementUsesMagnitudeAndIsCapped()
    {
        Assert.Equal(10, WeatherModel.ApproachWindIncrementKt(-20), 6); // tailwind counts too
        Assert.Equal(WeatherModel.MaxApproachWindIncrementKt,
            WeatherModel.ApproachWindIncrementKt(100), 6);
    }

    [Fact]
    public void TraceIceBarelyBumpsTheReferenceSpeedAndSevereIceCaps()
    {
        Assert.Equal(0, WeatherModel.ApproachIceIncrementKt(0), 6);
        Assert.Equal(0, WeatherModel.ApproachIceIncrementKt(WeatherModel.IcePerceptionThreshold / 2), 6);

        var trace = WeatherModel.ApproachIceIncrementKt(WeatherModel.IcePerceptionThreshold);
        Assert.True(trace > 0 && trace < WeatherModel.MaxApproachIceIncrementKt);

        Assert.Equal(WeatherModel.MaxApproachIceIncrementKt,
            WeatherModel.ApproachIceIncrementKt(WeatherModel.ModerateIcePercent), 6);
        Assert.Equal(WeatherModel.MaxApproachIceIncrementKt,
            WeatherModel.ApproachIceIncrementKt(WeatherModel.SevereIcePercent), 6);
    }

    [Fact]
    public void CombinedApproachIncrementIsCapped()
    {
        var combined = WeatherModel.ApproachWeatherIncrementKt(
            60, WeatherModel.SevereIcePercent, TurbulenceLevel.Severe);

        Assert.Equal(WeatherModel.MaxApproachWeatherIncrementKt, combined, 6);
    }

    [Fact]
    public void CombinedApproachIncrementIsZeroInCalmDryAir()
    {
        Assert.Equal(0, WeatherModel.ApproachWeatherIncrementKt(0, 0, TurbulenceLevel.Calm), 6);
    }

    [Fact]
    public void IcingNeedsMoistureInTheIcingBand()
    {
        Assert.False(WeatherModel.IcingConditions(0, inCloud: false, precipState: 0, structuralIcePercent: 0));
        Assert.True(WeatherModel.IcingConditions(0, inCloud: true, precipState: 0, structuralIcePercent: 0));
        // AMBIENT PRECIP STATE is a bitmask: 2=None, 4=Rain, 8=Snow.
        // None (2) must not count as moisture; rain (4) and snow (8) must.
        Assert.True(WeatherModel.IcingConditions(0, inCloud: false, precipState: 4, structuralIcePercent: 0));
        Assert.True(WeatherModel.IcingConditions(0, inCloud: false, precipState: 8, structuralIcePercent: 0));
        Assert.False(WeatherModel.IcingConditions(0, inCloud: false, precipState: 2, structuralIcePercent: 0));
    }

    [Fact]
    public void IcingIsOnlyInTheTemperatureBand()
    {
        Assert.False(WeatherModel.IcingConditions(15, inCloud: true, precipState: 0, structuralIcePercent: 0));
        Assert.False(WeatherModel.IcingConditions(-45, inCloud: true, precipState: 0, structuralIcePercent: 0));
        Assert.True(WeatherModel.IcingConditions(-5, inCloud: true, precipState: 0, structuralIcePercent: 0));
    }

    [Fact]
    public void IcingConditionsIsTrueOnceIceIsAlreadyOnTheStructure()
    {
        // Even a warm day reports icing if the airframe shows ice - the measurement wins.
        Assert.True(WeatherModel.IcingConditions(15, inCloud: false, precipState: 0,
            WeatherModel.IcePerceptionThreshold));
    }

    [Fact]
    public void HeadwindShortensAndTailwindLengthensTheRunwayNeeded()
    {
        var calm = WeatherModel.WindRunFactor(0, 140);
        var head = WeatherModel.WindRunFactor(20, 140);
        var tail = WeatherModel.WindRunFactor(-20, 140);

        Assert.Equal(1.0, calm, 6);
        Assert.True(head < 1.0);
        Assert.True(tail > 1.0);
    }

    [Fact]
    public void WindRunFactorStaysWithinItsClamp()
    {
        Assert.True(WeatherModel.WindRunFactor(400, 140) >= 0.6);
        Assert.True(WeatherModel.WindRunFactor(-400, 140) <= 1.8);
        Assert.Equal(1.0, WeatherModel.WindRunFactor(20, 30), 6); // too slow to matter
    }

    [Fact]
    public void ContaminatedSurfaceMultipliesTheRunwayNeeded()
    {
        // Icy doubles the roll; the wind factor and the surface factor are independent.
        var dry = WeatherModel.TakeoffRunFactor(0, 140, surfaceCondition: 0, surfaceInfoValid: true);
        var icyHeadwind = WeatherModel.TakeoffRunFactor(20, 140, surfaceCondition: 2, surfaceInfoValid: true);
        var headwindOnly = WeatherModel.TakeoffRunFactor(20, 140, surfaceCondition: 0, surfaceInfoValid: true);

        Assert.Equal(1.0, dry, 6);
        Assert.Equal(headwindOnly * WeatherModel.SurfaceRollFactor(2), icyHeadwind, 6);
        Assert.True(icyHeadwind < 1.8); // the headwind refunds most of the doubling here
    }

    [Fact]
    public void SurfaceInfoInvalidIsIgnored()
    {
        Assert.Equal(1.0, WeatherModel.TakeoffRunFactor(0, 140, surfaceCondition: 2, surfaceInfoValid: false), 6);
        Assert.False(WeatherModel.SurfaceIsContaminated(2, surfaceInfoValid: false));
        Assert.Equal("unknown", WeatherModel.SurfaceText(2, surfaceInfoValid: false));
    }

    [Theory]
    [InlineData(0, "dry", false)]
    [InlineData(1, "wet", true)]
    [InlineData(2, "icy", true)]
    [InlineData(3, "snow", true)]
    public void SurfaceStateReadsBack(double condition, string text, bool contaminated)
    {
        Assert.Equal(text, WeatherModel.SurfaceText(condition, surfaceInfoValid: true));
        Assert.Equal(contaminated, WeatherModel.SurfaceIsContaminated(condition, surfaceInfoValid: true));
    }

    [Fact]
    public void TurbulenceSeverityIsBoundedAndMonotonicInDeviation()
    {
        Assert.Equal(0, WeatherModel.SeverityFromRmsG(0), 6);
        Assert.Equal(0, WeatherModel.SeverityFromRmsG(0.03), 6);
        Assert.Equal(1, WeatherModel.SeverityFromRmsG(1.0), 6);
        var low = WeatherModel.SeverityFromRmsG(0.08);
        var mid = WeatherModel.SeverityFromRmsG(0.14);
        var high = WeatherModel.SeverityFromRmsG(0.22);
        Assert.True(low < mid && mid < high);
    }

    [Fact]
    public void TurbulenceLevelThresholds()
    {
        Assert.Equal(TurbulenceLevel.Calm, WeatherModel.Classify(0.0));
        Assert.Equal(TurbulenceLevel.Light, WeatherModel.Classify(0.2));
        Assert.Equal(TurbulenceLevel.Moderate, WeatherModel.Classify(0.5));
        Assert.Equal(TurbulenceLevel.Severe, WeatherModel.Classify(0.9));
    }

    [Fact]
    public void BankLimitFallsAsTurbulenceRises()
    {
        var limits = Enum.GetValues<TurbulenceLevel>()
            .Select(WeatherModel.ControlBankDeg)
            .ToArray();

        // Strictly decreasing bank authority from calm to severe.
        for (var i = 1; i < limits.Length; i++)
        {
            Assert.True(limits[i] <= limits[i - 1], "bank limit must not rise with turbulence");
        }
    }

    [Fact]
    public void EnrouteSpeedIsOnlyReducedInTurbulenceAndFloors()
    {
        Assert.Equal(280, WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Calm, 280), 6);
        Assert.Equal(280, WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Light, 280), 6);

        var moderate = WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Moderate, 280);
        var severe = WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Severe, 280);
        Assert.True(moderate < 280);
        Assert.True(severe < moderate);

        // The floor keeps a slow aircraft from being slowed below a safe speed.
        Assert.True(WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Severe, 100) >= 100 * 0.5);

        // ...but it must never speed a slow aircraft up above its own normal target.
        Assert.Equal(120, WeatherModel.EnrouteSpeedReductionKt(TurbulenceLevel.Severe, 120), 6);
    }

    [Fact]
    public void TurbulenceTrackerReportsCalmForSteadyLevelFlight()
    {
        var tracker = new TurbulenceTracker();
        for (var i = 0; i < 200; i++) tracker.Update(1.0, 0.05);

        Assert.Equal(TurbulenceLevel.Calm, tracker.Level);
        Assert.True(tracker.Severity < 0.12);
    }

    [Fact]
    public void TurbulenceTrackerIgnoresGroundChop()
    {
        var tracker = new TurbulenceTracker();
        // Alternating hard, but marked quiet (on the ground): must read calm.
        for (var i = 0; i < 300; i++) tracker.Update(i % 2 == 0 ? 1.6 : 0.4, 0.05, quiet: true);

        Assert.Equal(TurbulenceLevel.Calm, tracker.Level);
    }

    [Fact]
    public void TurbulenceTrackerEscalatesWithGustAmplitude()
    {
        var calm = BuildSeverity(amplitude: 0.03);
        var light = BuildSeverity(amplitude: 0.10);
        var severe = BuildSeverity(amplitude: 0.24);

        Assert.True(calm < light, "more gust amplitude must read rougher");
        Assert.True(light < severe);
        Assert.Equal(TurbulenceLevel.Severe, WeatherModel.Classify(severe));
    }

    [Fact]
    public void TurbulenceTrackerTreatsGarbageAsSteadyAndResets()
    {
        var tracker = new TurbulenceTracker();
        for (var i = 0; i < 100; i++) tracker.Update(double.NaN, 0.05);
        Assert.Equal(TurbulenceLevel.Calm, tracker.Level);

        tracker.Reset();
        Assert.Equal(0, tracker.Severity, 6);
    }

    // Feeds an alternating gust of +/- amplitude around 1 g and returns the converged severity.
    private static double BuildSeverity(double amplitude)
    {
        var tracker = new TurbulenceTracker();
        for (var i = 0; i < 400; i++)
        {
            tracker.Update(1.0 + (i % 2 == 0 ? amplitude : -amplitude), 0.05);
        }
        return tracker.Severity;
    }
}

