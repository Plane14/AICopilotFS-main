using System;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The envelope arithmetic is pure functions over the aircraft's published limits and the
/// measured state, so it is pinned here without a simulator. These are the rules that keep
/// the pilot inside the airframe: an overspeed, a load factor, a vertical rate, an engine
/// limit and a configuration limit, each with the sign convention asserted rather than
/// assumed.
/// </summary>
public class FlightEnvelopeTests
{
    // A mid-weight jet: no config V-speeds, but the simulator publishes its barber pole.
    private static OperatingLimits JetLimits() => new()
    {
        MaxAirspeedKt = 350,
        MaxFlapSpeedKt = 0,
        MaxGearSpeedKt = 0,
        MinAirspeedKt = 130,
        MaxClimbRateFpm = 3000,
        MaxDescentRateFpm = 3000
    };

    private static OperatingLimits LightAircraftLimits() => new()
    {
        MaxAirspeedKt = 163,
        MaxFlapSpeedKt = 110,
        MaxGearSpeedKt = 140,
        MinAirspeedKt = 48,
        MaxClimbRateFpm = 700,
        MaxDescentRateFpm = 1000
    };

    #region Speed limit resolution

    [Fact]
    public void BarberPoleWinsOverAConfigVSpeed()
    {
        // The simulator's needle already accounts for Mach, configuration and altitude, so it
        // is the tighter and the more correct of the two.
        Assert.Equal(340, FlightEnvelope.MaxAirspeedKt(340, 350), 6);
        Assert.Equal(350, FlightEnvelope.MaxAirspeedKt(355, 350), 6);
    }

    [Fact]
    public void NoBarberPoleFallsBackToTheConfigVSpeed()
    {
        Assert.Equal(280, FlightEnvelope.MaxAirspeedKt(0, 280), 6);
    }

    [Fact]
    public void NoBarberPoleAndNoVneFallsBackToVno()
    {
        // An aircraft above Vno is outside its structural envelope even below Vne.
        Assert.Equal(220, FlightEnvelope.MaxAirspeedKt(0, 0, 220), 6);
    }

    [Fact]
    public void APlaceholderReadingIsNotALimit()
    {
        // Zero and 5 knots mean "the sim has not filled it in yet", not a low limit.
        Assert.Equal(0.0, FlightEnvelope.MaxAirspeedKt(0, 0), 6);
        Assert.Equal(0.0, FlightEnvelope.MaxAirspeedKt(5, 5, 5), 6);
    }

    #endregion

    #region Configuration limits

    [Fact]
    public void FlapLimitAppliesOnlyWithFlapsSelected()
    {
        var limits = LightAircraftLimits();

        Assert.Equal(163, limits.ConfigurationSpeedLimitKt(0, false), 6);   // clean
        Assert.Equal(110, limits.ConfigurationSpeedLimitKt(2, false), 6);   // flaps out
        Assert.Equal(140, limits.ConfigurationSpeedLimitKt(0, true), 6);    // gear down
        Assert.Equal(110, limits.ConfigurationSpeedLimitKt(3, true), 6);    // both, tightest wins
    }

    [Fact]
    public void NoConfigurationLimitLeavesTheAirframeLimitAlone()
    {
        var limits = JetLimits();

        Assert.Equal(350, limits.ConfigurationSpeedLimitKt(3, true), 6);
    }

    [Fact]
    public void ClampAirspeedReducesAnOverspeedCommand()
    {
        var limits = LightAircraftLimits();

        // Flaps out at landing flaps: Vfe is the limit.
        Assert.Equal(110, FlightEnvelope.ClampAirspeedKt(140, limits, 3, true), 6);
        // Clean: nothing applies.
        Assert.Equal(130, FlightEnvelope.ClampAirspeedKt(130, limits, 0, false), 6);
        // A command above the gear limit is reduced, not passed through.
        Assert.Equal(140, FlightEnvelope.ClampAirspeedKt(160, limits, 0, true), 6);
    }

    [Fact]
    public void ClampAirspeedRaisesACommandBelowTheStallFloor()
    {
        var limits = JetLimits();

        // A fixed flare speed of 60 on a jet would be below its own stall; the floor raises it.
        Assert.Equal(130, FlightEnvelope.ClampAirspeedKt(60, limits, 3, true), 6);
        // A command inside the floor is untouched.
        Assert.Equal(140, FlightEnvelope.ClampAirspeedKt(140, limits, 3, true), 6);
    }

    [Fact]
    public void SpeedFloorNeverContradictsTheConfigurationLimit()
    {
        // A stall floor above Vfe is a contradiction; the airframe limit must win, because
        // exceeding Vfe damages the flaps and stalling only loses control.
        var limits = new OperatingLimits
        {
            MaxAirspeedKt = 163, MaxFlapSpeedKt = 110, MinAirspeedKt = 130
        };

        Assert.Equal(110, FlightEnvelope.ClampAirspeedKt(60, limits, 3, false), 6);
    }

    [Fact]
    public void ClampAirspeedAcceptsNaNWithoutPropagatingIt()
    {
        var limits = JetLimits();

        // NaN fails every comparison, so "< 50" alone lets it through and every downstream
        // comparison then passes too.
        var result = FlightEnvelope.ClampAirspeedKt(double.NaN, limits, 0, false);
        Assert.True(double.IsNaN(result));
    }

    #endregion

    #region Load factor

    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(0, 45, 1.414)]
    [InlineData(0, 60, 2.0)]
    [InlineData(0, 89, 20.0)]  // clamped, not a division by a near-zero cosine
    public void SteadyLoadFactorRisesWithBankAndPitch(double pitchDeg, double bankDeg, double expected)
    {
        Assert.Equal(expected, FlightEnvelope.SteadyLoadFactorG(pitchDeg, bankDeg), 2);
    }

    [Fact]
    public void CleanLimitIsHigherThanConfigured()
    {
        Assert.Equal(FlightEnvelope.MaxLoadFactorCleanG, FlightEnvelope.MaxLoadFactorG(0, false), 6);
        Assert.Equal(FlightEnvelope.MaxLoadFactorConfiguredG, FlightEnvelope.MaxLoadFactorG(3, false), 6);
        Assert.Equal(FlightEnvelope.MaxLoadFactorConfiguredG, FlightEnvelope.MaxLoadFactorG(0, true), 6);
        Assert.True(FlightEnvelope.MaxLoadFactorCleanG > FlightEnvelope.MaxLoadFactorConfiguredG);
    }

    [Fact]
    public void LoadFactorHeadroomIsPositiveBelowTheLimitAndNegativePastIt()
    {
        Assert.Equal(1.5, FlightEnvelope.LoadFactorHeadroomG(1.0, 2.5), 6);
        Assert.Equal(0.0, FlightEnvelope.LoadFactorHeadroomG(2.5, 2.5), 6);
        Assert.Equal(-0.7, FlightEnvelope.LoadFactorHeadroomG(3.2, 2.5), 6);
    }

    [Fact]
    public void GarbageLoadFactorIsTreatedAsOneGNotAsTheLimit()
    {
        // A NaN or wildly out-of-range reading must not be read as "over the limit": every
        // comparison with NaN is false, so it would silently disable the protection.
        Assert.Equal(1.0, FlightEnvelope.SanitizeLoadFactor(double.NaN), 6);
        Assert.Equal(1.0, FlightEnvelope.SanitizeLoadFactor(999), 6);
        Assert.Equal(1.0, FlightEnvelope.SanitizeLoadFactor(-999), 6);
        Assert.Equal(1.8, FlightEnvelope.SanitizeLoadFactor(1.8), 6);
    }

    [Fact]
    public void ElevatorPassesThroughWithAmpleHeadroom()
    {
        var limits = new OperatingLimits { MaxLoadFactorG = 2.5 };

        Assert.Equal(0.4, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 1.0, limits), 6);
        Assert.Equal(0.0, FlightEnvelope.LimitElevatorForLoadFactor(0.0, 1.0, limits), 6);
    }

    [Fact]
    public void ElevatorIsScaledBackAsTheLoadFactorApproachesTheLimit()
    {
        var limits = new OperatingLimits { MaxLoadFactorG = 2.5 };

        // Half the authority window left: the command is halved rather than zeroed, so the
        // aircraft is still being flown.
        Assert.Equal(0.2, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.25, limits), 3);
    }

    [Fact]
    public void ElevatorUnloadsTheWingPastTheLoadFactorLimit()
    {
        var limits = new OperatingLimits { MaxLoadFactorG = 2.5 };

        var result = FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.6, limits);
        Assert.Equal(FlightEnvelope.UnloadElevatorCommand, result, 6);
        Assert.True(result < 0, "a pull past the limit must be answered with a push");
    }

    [Fact]
    public void ElevatorReloadsTheWingPastTheNegativeLoadFactorLimit()
    {
        var limits = new OperatingLimits { MinLoadFactorG = 1.0 };

        // -1.2 g on an aircraft rated to -1 g: the elevator is moved to pull rather than held
        // at the command.
        var result = FlightEnvelope.LimitElevatorForLoadFactor(-0.4, -1.2, limits);
        Assert.Equal(FlightEnvelope.ReloadElevatorCommand, result, 6);
    }

    [Fact]
    public void ConfiguredLimitIsTighterThanCleanForTheSameCommand()
    {
        var clean = new OperatingLimits { MaxLoadFactorG = FlightEnvelope.MaxLoadFactorCleanG };
        var configured = new OperatingLimits { MaxLoadFactorG = FlightEnvelope.MaxLoadFactorConfiguredG };

        // 1.8 g is inside the clean envelope but leaves only 0.2 g on a configured aircraft,
        // which is inside the authority window, so the command is scaled rather than passed
        // through in full.
        Assert.Equal(0.4, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 1.8, clean), 6);
        Assert.Equal(0.16, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 1.8, configured), 6);
    }

    [Fact]
    public void ConfiguredLimitUnloadsTheWingSoonerThanClean()
    {
        var clean = new OperatingLimits { MaxLoadFactorG = FlightEnvelope.MaxLoadFactorCleanG };
        var configured = new OperatingLimits { MaxLoadFactorG = FlightEnvelope.MaxLoadFactorConfiguredG };

        // 2.3 g is past the configured limit, so that aircraft's elevator is cut to an unload
        // command. The clean aircraft is inside its envelope but has only 0.2 g left, so its
        // command is scaled back rather than passed through in full - the same rule, arriving
        // at a different place because the limits differ.
        Assert.Equal(FlightEnvelope.UnloadElevatorCommand, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.3, configured), 6);
        Assert.Equal(0.16, FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.3, clean), 3);
        Assert.True(FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.3, configured) <
                    FlightEnvelope.LimitElevatorForLoadFactor(0.4, 2.3, clean));
    }

    [Fact]
    public void ASteadyAttitudeIsNotWhatOverstressesAnAirframe()
    {
        // The reason there is no "maximum pitch attitude" function: a steady 15-degree climb
        // at any bank a pilot would hold produces cos(15)/cos(bank) g, which is at or below
        // the limit. Overstress comes from the *rate* of rotation, which is what the elevator
        // limiter and the vertical-rate ramp bound - not from a static attitude.
        var limits = new OperatingLimits { MaxLoadFactorG = FlightEnvelope.MaxLoadFactorCleanG };

        Assert.True(FlightEnvelope.SteadyLoadFactorG(15, 0) <= limits.MaxLoadFactorG);
        Assert.True(FlightEnvelope.SteadyLoadFactorG(15, 25) <= limits.MaxLoadFactorG);
        // A hard bank is where the steady load does approach the limit, and 1/cos(bank) is
        // what the bank limiter in the direct-control path is there to keep off.
        Assert.True(FlightEnvelope.SteadyLoadFactorG(0, 60) < FlightEnvelope.MaxLoadFactorConfiguredG);
    }

    #endregion

    #region Vertical rate

    [Fact]
    public void VerticalRateIsClampedToTheAircraftsOwnEnvelope()
    {
        var jet = JetLimits();
        var light = LightAircraftLimits();

        // The old fixed -1800 fpm was a rate a light aircraft is not built for and a jet
        // barely reaches below the tropopause; each is clamped to its own envelope.
        Assert.Equal(-1800, FlightEnvelope.ClampVerticalRateFpm(-1800, jet), 6);
        Assert.Equal(-1000, FlightEnvelope.ClampVerticalRateFpm(-1800, light), 6);
        Assert.Equal(-3000, FlightEnvelope.ClampVerticalRateFpm(-4000, jet), 6);
        Assert.Equal(700, FlightEnvelope.ClampVerticalRateFpm(5000, light), 6);
    }

    [Fact]
    public void ACommandInsideTheEnvelopeIsLeftAlone()
    {
        var limits = JetLimits();

        Assert.Equal(-1500, FlightEnvelope.ClampVerticalRateFpm(-1500, limits), 6);
        Assert.Equal(2000, FlightEnvelope.ClampVerticalRateFpm(2000, limits), 6);
    }

    [Fact]
    public void VerticalAccelerationIsZeroWithNoLoadFactorHeadroom()
    {
        // No headroom means the aircraft may not be pulled any harder: the rate may not
        // change at all, which is what stops a step command overstressing the airframe.
        Assert.Equal(0.0, FlightEnvelope.MaxVerticalAccelerationFpmPerSecond(-1.0), 6);
        Assert.Equal(0.0, FlightEnvelope.MaxVerticalAccelerationFpmPerSecond(0.0), 6);
    }

    [Fact]
    public void VerticalAccelerationRisesWithHeadroom()
    {
        var small = FlightEnvelope.MaxVerticalAccelerationFpmPerSecond(0.2);
        var large = FlightEnvelope.MaxVerticalAccelerationFpmPerSecond(1.5);

        Assert.True(small > 0);
        Assert.True(large > small);
    }

    [Fact]
    public void RampMovesTowardTheTargetWithoutOvershooting()
    {
        Assert.Equal(-100, FlightEnvelope.RampTo(0, -1800, 100), 6);
        Assert.Equal(1990, FlightEnvelope.RampTo(-10, 2000, 2000), 6);
        // Already within one step: the target itself, not a partial move.
        Assert.Equal(-1800, FlightEnvelope.RampTo(-1700, -1800, 100), 6);
    }

    [Fact]
    public void RampIsAPureFunctionOfItsInputs()
    {
        // Starting from the aircraft's own rate, not from the last command: an aircraft
        // already descending is held there rather than yanked back to the previous value.
        Assert.Equal(-1700, FlightEnvelope.RampTo(-1600, -1800, 100), 6);
        Assert.Equal(-1700, FlightEnvelope.RampTo(-1600, -1800, 100), 6);
    }

    [Fact]
    public void ZeroStepMeansTheRateIsHeld()
    {
        // With no load-factor headroom the aircraft may not be pulled any harder, so the
        // current rate is carried rather than the commanded one applied uncorrected.
        Assert.Equal(-2000, FlightEnvelope.RampTo(-2000, -3000, 0), 6);
        Assert.Equal(-2000, FlightEnvelope.RampTo(-2000, -3000, -500), 6);
        // A garbage step is the same case, not a licence to jump.
        Assert.Equal(-2000, FlightEnvelope.RampTo(-2000, -3000, double.NaN), 6);
    }

    #endregion

    #region Engine limits

    [Fact]
    public void JetAndTurbopropCarryAnEgtRedlineAndAPistonDoesNot()
    {
        Assert.Equal(FlightEnvelope.JetEgtRedlineC, FlightEnvelope.EgtRedlineC("JET"), 6);
        Assert.Equal(FlightEnvelope.TurbopropEgtRedlineC, FlightEnvelope.EgtRedlineC("TURBOPROP"), 6);
        // EGT is a per-cylinder measurement on a recip with no single redline; the near-
        // ambient value the variable reports for one is not one either.
        Assert.Equal(0.0, FlightEnvelope.EgtRedlineC("PISTON"), 6);
        Assert.Equal(0.0, FlightEnvelope.EgtRedlineC(""), 6);
        Assert.Equal(0.0, FlightEnvelope.EgtRedlineC(null), 6);
    }

    [Fact]
    public void ThrottleIsCappedAsEgtApproachesTheRedline()
    {
        const double redline = FlightEnvelope.JetEgtRedlineC;

        // 850 C is more than 10% below the redline: full authority.
        Assert.Equal(1.0, FlightEnvelope.MaxThrottleFraction(1.0, 850, redline), 6);
        // Inside the last 10%: authority is scaled back, and a command already at full
        // throttle is exactly the one that needs scaling.
        var partial = FlightEnvelope.MaxThrottleFraction(1.0, 920, redline);
        Assert.True(partial > 0 && partial < 1.0, $"expected partial authority, got {partial}");
        // At the redline: idle, not held there.
        Assert.Equal(0.0, FlightEnvelope.MaxThrottleFraction(1.0, redline, redline), 6);
        Assert.Equal(0.0, FlightEnvelope.MaxThrottleFraction(1.0, 1200, redline), 6);
    }

    [Fact]
    public void NoEgtLimitLeavesTheThrottleAlone()
    {
        Assert.Equal(1.0, FlightEnvelope.MaxThrottleFraction(1.0, 200, 0), 6);
        Assert.Equal(0.5, FlightEnvelope.MaxThrottleFraction(0.5, 0, 0), 6);
    }

    [Fact]
    public void EngineLimitFractionReportsTheHarsherOfTheTwoLimits()
    {
        const double redline = FlightEnvelope.JetEgtRedlineC;

        Assert.Equal(0.5, FlightEnvelope.EngineLimitFraction(redline / 2, redline, 50), 6);
        Assert.Equal(1.0, FlightEnvelope.EngineLimitFraction(redline, redline, 50), 6);
        Assert.Equal(0.0, FlightEnvelope.EngineLimitFraction(0, 0, 0), 6);
    }

    #endregion

    #region Regulatory speed

    [Fact]
    public void RegulatorySpeedAppliesOnlyBelowTenThousand()
    {
        Assert.Equal(0, FlightEnvelope.RegulatorySpeedKt(0), 6);
        Assert.Equal(250, FlightEnvelope.RegulatorySpeedKt(5000), 6);
        Assert.Equal(250, FlightEnvelope.RegulatorySpeedKt(9999), 6);
        Assert.Equal(0, FlightEnvelope.RegulatorySpeedKt(10000), 6);
        Assert.Equal(0, FlightEnvelope.RegulatorySpeedKt(30000), 6);
    }

    #endregion

    #region The guard as a whole

    /// <summary>A jet with nothing the simulator publishes and no config V-speeds but Vne.</summary>
    private static AircraftPerformance JetPerf() => new()
    {
        Vne = 340,
        Vso = 140,
        Vref = 150,
        EngineType = "JET",
        EngineCount = 2,
        CruisePerf = new CruisePerformance { DefaultCruiseSpeed = 280 }
    };

    /// <summary>A light single with the config V-speeds published.</summary>
    private static AircraftPerformance LightPerf() => new()
    {
        Vne = 163,
        Vfe = 110,
        Vle = 130,
        Vso = 48,
        Vref = 60,
        EngineType = "PISTON",
        EngineCount = 1
    };

    private static AircraftState State(double ias = 120, double vs = 0, double g = 1.0,
        int flaps = 0, bool gear = false, double barber = 0) => new()
        {
            IndicatedAirspeed = ias,
            GroundSpeed = ias,
            VerticalSpeed = vs,
            GLoad = g,
            FlapsHandleIndex = flaps,
            GearDown = gear,
            BarberPoleKt = barber,
            EngineN1 = [30],
            EngineN2 = [60],
            EngineThrottle = [0.8],
            EngineEgtC = [400],
            EngineMaxRpmPct = [80]
        };

    [Fact]
    public void BarberPoleResolvesTheSpeedLimitOverTheConfigVSpeed()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(barber: 330), JetPerf(), 0, false);

        // The simulator's own needle beats the config's 340.
        Assert.Equal(330, guard.Limits.MaxAirspeedKt, 6);
    }

    [Fact]
    public void NoBarberPoleResolvesTheSpeedLimitFromConfig()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(), JetPerf(), 0, false);

        Assert.Equal(340, guard.Limits.MaxAirspeedKt, 6);
    }

    [Fact]
    public void ConfiguredLimitFollowsTheFlapsAndGearSelected()
    {
        var guard = new EnvelopeGuard();

        guard.Update(State(flaps: 0, gear: false), LightPerf(), 0, false);
        Assert.Equal(163, guard.CappedAirspeedKt(163, 0, false), 6);

        guard.Update(State(flaps: 2, gear: false), LightPerf(), 2, false);
        Assert.Equal(110, guard.CappedAirspeedKt(163, 2, false), 6);

        guard.Update(State(flaps: 0, gear: true), LightPerf(), 0, true);
        Assert.Equal(130, guard.CappedAirspeedKt(163, 0, true), 6);
    }

    [Fact]
    public void APistonGetsNoEngineLimitButAJetDoes()
    {
        var piston = new EnvelopeGuard();
        piston.Update(State(), LightPerf(), 0, false);
        Assert.False(piston.Limits.HasEgtLimit);
        Assert.Equal(1.0, piston.MaxThrottle, 6);

        var jet = new EnvelopeGuard();
        jet.Update(State(), JetPerf(), 0, false);
        Assert.True(jet.Limits.HasEgtLimit);
    }

    [Fact]
    public void FlapsAboveVfeAreReportedAsABreach()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(ias: 125, flaps: 3), LightPerf(), 3, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.FlapSpeed, guard.CurrentBreach);
        Assert.Contains("Vfe", guard.BreachDescription());
    }

    [Fact]
    public void GearAboveVleIsReportedAsABreach()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(ias: 140, gear: true), LightPerf(), 0, true);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.GearSpeed, guard.CurrentBreach);
    }

    [Fact]
    public void ACleanOverspeedIsNotReportedAsAGearBreach()
    {
        // Flaps and gear retracted, 20 knots above Vne. The breach used to fall through to
        // GearSpeed - "gear extended above Vle" - for every overspeed with the gear up,
        // because the classification only asked whether the flaps were selected.
        var guard = new EnvelopeGuard();
        guard.Update(State(ias: 185), LightPerf(), 0, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.Airspeed, guard.CurrentBreach);
    }

    [Fact]
    public void TheSimulatorsOwnOverspeedWarningIsReportedBeforeAnyArithmetic()
    {
        var guard = new EnvelopeGuard();
        var state = State(ias: 200);
        state.OverspeedWarning = true;

        guard.Update(state, JetPerf(), 0, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.Airspeed, guard.CurrentBreach);
    }

    [Fact]
    public void LoadFactorOverTheLimitIsReported()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(g: 2.9), JetPerf(), 0, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.LoadFactor, guard.CurrentBreach);
        Assert.Contains("2.90", guard.BreachDescription());
    }

    [Fact]
    public void AConfiguredAircraftHasTheTighterLoadFactorLimit()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(), JetPerf(), 0, false);
        Assert.Equal(FlightEnvelope.MaxLoadFactorCleanG, guard.Limits.MaxLoadFactorG, 6);

        guard.Update(State(), JetPerf(), 3, false);
        Assert.Equal(FlightEnvelope.MaxLoadFactorConfiguredG, guard.Limits.MaxLoadFactorG, 6);
    }

    [Fact]
    public void VerticalRateOverTheLimitIsReported()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(vs: -4500), JetPerf(), 0, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.VerticalRate, guard.CurrentBreach);
    }

    [Fact]
    public void EngineAtTheEgtRedlineIsReportedAndCutsTheThrottle()
    {
        var guard = new EnvelopeGuard();
        var state = State();
        state.EngineEgtC = [FlightEnvelope.JetEgtRedlineC + 10];

        guard.Update(state, JetPerf(), 0, false);

        Assert.True(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.EngineEgt, guard.CurrentBreach);
        // Holding full power at the redline is what damages an engine.
        Assert.Equal(0.0, guard.MaxThrottle, 6);
    }

    [Fact]
    public void NoBreachInsideTheEnvelope()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(ias: 250, vs: -1500, g: 1.05), JetPerf(), 0, false);

        Assert.False(guard.AnyExceeded);
        Assert.Equal(OperatingLimit.None, guard.CurrentBreach);
        Assert.Null(guard.BreachDescription());
        Assert.Equal(1.0, guard.MaxThrottle, 6);
    }

    [Fact]
    public void VerticalRateIsRampedRatherThanStepped()
    {
        var guard = new EnvelopeGuard();
        // Level flight, so the full load-factor headroom is available.
        guard.Update(State(vs: 0, g: 1.0), JetPerf(), 0, false);

        var oneFrame = guard.CappedVerticalRateFpm(-2500, 0.05);
        Assert.True(oneFrame > -2500 && oneFrame <= 0,
            "a commanded -2500 fpm must be ramped in, not applied in one frame");

        // Repeated application converges on the commanded rate.
        guard.Update(State(vs: oneFrame, g: 1.0), JetPerf(), 0, false);
        var second = guard.CappedVerticalRateFpm(-2500, 0.05);
        Assert.True(second < oneFrame);
    }

    [Fact]
    public void NoLoadFactorHeadroomHoldsTheCurrentVerticalRate()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(vs: -2000, g: 2.5), JetPerf(), 0, false);

        // Over the limit: the rate may not be changed at all.
        Assert.Equal(-2000, guard.CappedVerticalRateFpm(-3000, 0.05), 6);
    }

    [Fact]
    public void GarbageLoadFactorDoesNotTriggerOrSuppressTheGuard()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(g: double.NaN), JetPerf(), 0, false);

        // NaN fails every comparison, so it must be read as one g rather than as a breach -
        // and the protection must still be live.
        Assert.False(guard.AnyExceeded);
        Assert.Equal(1.0, guard.MeasuredLoadFactor, 6);
    }

    [Fact]
    public void TheElevatorIsLimitedThroughTheGuard()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(g: 1.0), JetPerf(), 0, false);

        // Ample headroom: the command passes through.
        Assert.Equal(0.4, guard.CappedElevator(0.4), 6);

        // At the limit: the airframe is unloaded.
        guard.Update(State(g: 2.6), JetPerf(), 0, false);
        Assert.Equal(FlightEnvelope.UnloadElevatorCommand, guard.CappedElevator(0.4), 6);
    }

    [Fact]
    public void LimitsAreResolvedPerAircraftNotGlobally()
    {
        var jet = new EnvelopeGuard();
        jet.Update(State(), JetPerf(), 0, false);
        var light = new EnvelopeGuard();
        light.Update(State(), LightPerf(), 0, false);

        // The same code resolves different envelopes per aircraft: the jet is faster and has
        // engine limits to enforce; the light aircraft is slower and has configuration limits.
        Assert.True(jet.Limits.MaxAirspeedKt > light.Limits.MaxAirspeedKt);
        Assert.True(light.Limits.MaxFlapSpeedKt > 0 && jet.Limits.MaxFlapSpeedKt == 0);
        Assert.True(jet.Limits.HasEgtLimit && !light.Limits.HasEgtLimit);
        Assert.True(light.Limits.MaxDescentRateFpm == jet.Limits.MaxDescentRateFpm);
    }

    [Fact]
    public void APublishedDescentRateIsUsedForThatAircraft()
    {
        var perf = JetPerf();
        perf.DescentPerf = new DescentPerformance
        {
            Profiles = { new DescentProfile { IAS = 250, Mach = 0.70, RateOfDescent = 2000, Gradient = 3.0 } }
        };

        var guard = new EnvelopeGuard();
        guard.Update(State(), perf, 0, false);

        // The aircraft's own published rate, not the generic default.
        Assert.Equal(2000, guard.Limits.MaxDescentRateFpm, 6);
        Assert.Equal(-2000, FlightEnvelope.ClampVerticalRateFpm(-4000, guard.Limits), 6);
    }

    [Fact]
    public void DescribeReportsTheResolvedEnvelope()
    {
        var guard = new EnvelopeGuard();
        guard.Update(State(), LightPerf(), 0, false);
        var text = guard.Describe();

        Assert.Contains("163 kt", text);
        Assert.Contains("Vfe 110", text);
        Assert.Contains("Vle 130", text);
        Assert.Contains("stall floor", text);
        // A piston has no EGT redline, so none is claimed.
        Assert.DoesNotContain("EGT", text);
    }

    #endregion
}
