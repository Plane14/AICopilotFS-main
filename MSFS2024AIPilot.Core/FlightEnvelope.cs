using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Which of an aircraft's structural, aerodynamic or powerplant limits is being tested.
/// </summary>
public enum OperatingLimit
{
    None,
    /// <summary>Vmo/Mmo overspeed: the barber pole, Vne or Vno.</summary>
    Airspeed,
    /// <summary>Airframe normal-load-factor limit, positive or negative.</summary>
    LoadFactor,
    /// <summary>Climb or descent rate beyond what the airframe is flown at.</summary>
    VerticalRate,
    /// <summary>Flaps extended above Vfe.</summary>
    FlapSpeed,
    /// <summary>Gear extended above Vle/Vlo.</summary>
    GearSpeed,
    /// <summary>An engine at or above its maximum RPM.</summary>
    EngineRpm,
    /// <summary>Exhaust gas temperature above the redline.</summary>
    EngineEgt
}

/// <summary>
/// The resolved operating envelope for one aircraft at this moment. A field left at zero
/// is a limit this aircraft does not publish and the simulator does not report, which is
/// different from a limit of zero: <see cref="EnvelopeGuard"/> never invents one.
/// </summary>
public sealed class OperatingLimits
{
    /// <summary>Maximum indicated airspeed in knots (Vmo/Vne). Zero = unknown.</summary>
    public double MaxAirspeedKt { get; set; }
    /// <summary>Maximum positive load factor in g.</summary>
    public double MaxLoadFactorG { get; set; } = FlightEnvelope.MaxLoadFactorCleanG;
    /// <summary>Maximum negative load factor in g, as a magnitude so it reads positive.</summary>
    public double MinLoadFactorG { get; set; } = FlightEnvelope.MinLoadFactorGMagnitude;
    /// <summary>Maximum flap-extended speed in knots (Vfe). Zero = unknown.</summary>
    public double MaxFlapSpeedKt { get; set; }
    /// <summary>Maximum gear-extended speed in knots (Vle/Vlo). Zero = unknown.</summary>
    public double MaxGearSpeedKt { get; set; }
    /// <summary>Maximum climb rate in feet per minute.</summary>
    public double MaxClimbRateFpm { get; set; } = FlightEnvelope.DefaultMaxClimbRateFpm;
    /// <summary>Maximum descent rate in feet per minute, as a magnitude so it reads positive.</summary>
    public double MaxDescentRateFpm { get; set; } = FlightEnvelope.DefaultMaxDescentRateFpm;
    /// <summary>Slowest speed the aircraft is flown at, derived from its stall speed.</summary>
    public double MinAirspeedKt { get; set; }
    /// <summary>Maximum exhaust gas temperature, Celsius. Zero = not applicable to this engine.</summary>
    public double MaxEgtC { get; set; }
    /// <summary>Maximum engine RPM as a percentage. Zero = not applicable.</summary>
    public double MaxEngineRpmPercent { get; set; }

    public bool HasAirspeedLimit => MaxAirspeedKt > 0;
    public bool HasFlapSpeedLimit => MaxFlapSpeedKt > 0;
    public bool HasGearSpeedLimit => MaxGearSpeedKt > 0;
    public bool HasSpeedFloor => MinAirspeedKt > 0;
    public bool HasEgtLimit => MaxEgtC > 0;

    /// <summary>
    /// The single speed that must not be exceeded given the configuration actually flown:
    /// the tightest of the airframe limit and whichever of the flap or gear limit applies.
    /// </summary>
    public double ConfigurationSpeedLimitKt(int flapHandleIndex, bool gearDown)
    {
        double limit = MaxAirspeedKt;
        if (flapHandleIndex >= 1 && HasFlapSpeedLimit) limit = Combine(limit, MaxFlapSpeedKt);
        if (gearDown && HasGearSpeedLimit) limit = Combine(limit, MaxGearSpeedKt);
        return limit;
    }

    /// <summary>The tighter of two limits, treating a non-positive one as "unknown".</summary>
    private static double Combine(double a, double b)
    {
        if (a <= 0) return b;
        if (b <= 0) return a;
        return Math.Min(a, b);
    }
}

/// <summary>
/// Structural, aerodynamic and powerplant envelope arithmetic, written as pure functions so
/// the reactions the pilot takes can be exercised without a running simulator.
/// <para>
/// The limits come from two sources and the simulator's own beats the config's, because the
/// simulator already folds in what a config file cannot: the configuration actually selected,
/// the altitude at which a Mach limit converts to an indicated speed, and the barber-pole
/// needle the aircraft itself displays. <c>AIRSPEED BARBER POLE</c> and
/// <c>OVERSPEED WARNING</c> are those signals; a config V-speed is the fallback for an
/// aircraft whose barber pole the simulator does not report.
/// </para>
/// </summary>
public static class FlightEnvelope
{
    /// <summary>Maximum positive load factor, clean. Every certified aircraft is at least this.</summary>
    public const double MaxLoadFactorCleanG = 2.5;
    /// <summary>
    /// Maximum positive load factor with the gear down or flaps selected. A lower figure
    /// because the limiting structure of a configured aircraft is the flap track and the
    /// landing gear rather than the wing.
    /// </summary>
    public const double MaxLoadFactorConfiguredG = 2.0;
    /// <summary>Maximum negative load factor, as a magnitude. -1 g is universal.</summary>
    public const double MinLoadFactorGMagnitude = 1.0;

    /// <summary>Vertical-rate limits. A descent profile publishes its own rate; a climb profile does not.</summary>
    public const double DefaultMaxClimbRateFpm = 3000;
    public const double DefaultMaxDescentRateFpm = 3000;
    /// <summary>Hard ceiling on either vertical rate, whatever the aircraft publishes.</summary>
    public const double AbsoluteMaxClimbRateFpm = 4000;
    public const double AbsoluteMaxDescentRateFpm = 4000;

    /// <summary>Regulatory speed below 10,000 ft, applied to the aircraft's own target.</summary>
    public const double SpeedLimitBelowTenThousandKt = 250;
    public const double SpeedLimitFloorFt = 10000;

    /// <summary>Redline exhaust gas temperature by engine type, Celsius.</summary>
    public const double JetEgtRedlineC = 950;
    public const double TurbopropEgtRedlineC = 850;

    /// <summary>
    /// Converts an EGT reading from Rankine (the unit MSFS 2024 reports) to Celsius,
    /// which is what the pilot's limits and the aircraft.cfg redlines are expressed in.
    /// </summary>
    public static double RankineToCelsius(double rankine)
        => (rankine - 491.67) * 5.0 / 9.0;

    /// <summary>An engine at this percentage of its maximum RPM is on the redline.</summary>
    public const double EngineRpmRedlinePercent = 100.0;

    /// <summary>A speed reading below this many knots is a placeholder, not a published limit.</summary>
    public const double MinPlausibleSpeedKt = 50;

    /// <summary>Standard gravity in ft/s², the same constant the load-factor decode uses.</summary>
    public const double StandardGravityFtPerSec2 = 32.174;

    /// <summary>
    /// Fraction of the load-factor envelope left before the pitch command is reduced. With
    /// this much headroom the pilot keeps full authority; below it the command is scaled back,
    /// and with none the elevator is moved to unload the wing rather than held against it.
    /// </summary>
    public const double LoadFactorAuthorityWindowG = 0.5;

    /// <summary>Elevator applied to unload the wing past the positive load-factor limit.</summary>
    public const double UnloadElevatorCommand = -0.3;
    /// <summary>Elevator applied to reload the wing past the negative load-factor limit.</summary>
    public const double ReloadElevatorCommand = 0.3;

    /// <summary>Maximum positive load factor for the configuration actually flown.</summary>
    public static double MaxLoadFactorG(int flapHandleIndex, bool gearDown)
        => gearDown || flapHandleIndex >= 1 ? MaxLoadFactorConfiguredG : MaxLoadFactorCleanG;

    /// <summary>
    /// The steady load factor a bank and pitch attitude puts on the airframe,
    /// <c>cos(pitch) / cos(bank)</c> - one g level, <c>1/cos(bank)</c> in a level turn, more
    /// in a pull-up. This is the fixed part of the load the measured figure is read against.
    /// </summary>
    public static double SteadyLoadFactorG(double pitchDeg, double bankDeg)
    {
        var cosBank = Math.Cos(Geo.ToRadians(Math.Abs(Math.Clamp(bankDeg, -89, 89))));
        if (cosBank < 1e-6) return 100.0;
        var cosPitch = Math.Cos(Geo.ToRadians(Math.Clamp(pitchDeg, -89, 89)));
        return Math.Clamp(cosPitch / cosBank, -20.0, 20.0);
    }

    /// <summary>
    /// Redline exhaust gas temperature for an engine type, in Celsius.
    /// <para>
    /// Zero means "no EGT limit", which is what a piston engine gets: exhaust gas temperature
    /// is a per-cylinder measurement on a recip with no single redline, and the
    /// near-ambient value the variable reports for one is not one. The types the aircraft
    /// configs publish are PISTON, TURBOPROP and JET. The EGT SimVar is reported in Rankine
    /// by MSFS 2024 and converted to Celsius at the decode boundary in
    /// <see cref="AircraftController"/>.
    /// </para>
    /// </summary>
    public static double EgtRedlineC(string? engineType)
    {
        var type = engineType ?? "";
        if (type.Contains("JET", StringComparison.OrdinalIgnoreCase)) return JetEgtRedlineC;
        if (type.Contains("TURBO", StringComparison.OrdinalIgnoreCase)) return TurbopropEgtRedlineC;
        return 0;
    }

    /// <summary>
    /// The maximum indicated speed for the aircraft, as the tightest of the sources that
    /// publish one. The barber pole the simulator computes is preferred over a config V-speed
    /// because it already accounts for the Mach limit, the configuration and the altitude.
    /// A config V-no (max structural cruising speed) is the last resort - an aircraft above
    /// Vno is outside its structural envelope even while it is below Vne.
    /// </summary>
    public static double MaxAirspeedKt(double barberPoleKt, double vneKt, double vnoKt = 0)
    {
        var best = 0.0;
        foreach (var candidate in new[] { barberPoleKt, vneKt, vnoKt })
        {
            // Negated, so a NaN candidate is rejected too: NaN fails every comparison,
            // so "candidate < floor" and "candidate < best" are both false for it and one
            // garbage reading of the barber pole used to wipe out the speed limit for the
            // whole frame - the guard then resolved to no limit at all.
            if (!(candidate >= MinPlausibleSpeedKt)) continue;
            if (!(best <= 0 || candidate < best)) continue;
            best = candidate;
        }
        return best;
    }

    /// <summary>
    /// The regulatory speed below 10,000 ft. Applied only to the aircraft's own perf-derived
    /// target - a controller's speed assignment is flown as given, the same convention the
    /// turbulence speed reduction already follows.
    /// </summary>
    public static double RegulatorySpeedKt(double altitudeFt)
        => altitudeFt > 0 && altitudeFt < SpeedLimitFloorFt ? SpeedLimitBelowTenThousandKt : 0;

    /// <summary>
    /// The slowest speed the aircraft may be flown at, from its landing stall speed. This is
    /// what stops an aircraft being flown at a fixed manoeuvring speed below its own stall -
    /// the flare, a slow approach segment and a go-around all read from here.
    /// </summary>
    public static double MinAirspeedKt(double stallKt, double vrefKt)
    {
        if (stallKt >= MinPlausibleSpeedKt) return stallKt * 1.1;
        if (vrefKt >= MinPlausibleSpeedKt) return vrefKt;
        return 0;
    }

    /// <summary>
    /// Clamps a commanded airspeed into the envelope: never above the limit for the
    /// configuration actually flown, never below the stall floor. A commanded speed the
    /// pilot is not allowed to fly is reduced rather than passed through, so the airframe is
    /// never asked to do something impossible.
    /// </summary>
    public static double ClampAirspeedKt(double commandedKt, OperatingLimits limits, int flapHandleIndex, bool gearDown)
    {
        if (!(commandedKt > 0)) return commandedKt;

        var result = commandedKt;

        var configurationLimit = limits.ConfigurationSpeedLimitKt(flapHandleIndex, gearDown);
        if (configurationLimit > 0) result = Math.Min(result, configurationLimit);

        // Raise a command that is below the stall speed, unless doing so would break the
        // configuration limit - a slow command and a low limit is a contradiction, and the
        // airframe limit is the one that must win.
        if (limits.HasSpeedFloor && result < limits.MinAirspeedKt)
        {
            var floor = configurationLimit > 0
                ? Math.Min(limits.MinAirspeedKt, configurationLimit)
                : limits.MinAirspeedKt;
            result = Math.Min(Math.Max(result, floor), configurationLimit > 0 ? configurationLimit : double.MaxValue);
        }

        return result;
    }

    /// <summary>
    /// Clamps a commanded climb or descent rate into the aircraft's envelope. Both bounds are
    /// enforced: a descent deeper than the aircraft is flown at is a structural limit, not
    /// only a comfort one.
    /// </summary>
    public static double ClampVerticalRateFpm(double commandedFpm, OperatingLimits limits)
    {
        var maxClimb = limits.MaxClimbRateFpm > 0 ? limits.MaxClimbRateFpm : DefaultMaxClimbRateFpm;
        var maxDescent = limits.MaxDescentRateFpm > 0 ? limits.MaxDescentRateFpm : DefaultMaxDescentRateFpm;
        return Math.Clamp(commandedFpm, -maxDescent, maxClimb);
    }

    /// <summary>
    /// How much the vertical speed may change per second without exceeding the load-factor
    /// limit. A pull-up is a load factor: <c>n - 1 = (V/g) * d(gamma)/dt</c>, so the rate of
    /// change of vertical speed is bounded by the headroom left before the limit. This is
    /// what turns a commanded rate change into a ramp, which is how an abrupt altitude or
    /// vertical-speed change overstresses an airframe.
    /// </summary>
    public static double MaxVerticalAccelerationFpmPerSecond(double loadFactorHeadroomG)
    {
        if (loadFactorHeadroomG <= 0) return 0;
        return Math.Min(loadFactorHeadroomG, MaxLoadFactorCleanG) * StandardGravityFtPerSec2 * 60.0;
    }

    /// <summary>
    /// Load-factor headroom in g: how much more the aircraft may pull before the limit.
    /// Negative means the limit is already exceeded.
    /// </summary>
    public static double LoadFactorHeadroomG(double measuredG, double maxLoadFactorG)
        => maxLoadFactorG - SanitizeLoadFactor(measuredG);

    /// <summary>Load-factor headroom in g at the negative (push) end of the envelope.</summary>
    public static double NegativeLoadFactorHeadroomG(double measuredG, double minLoadFactorG)
        => SanitizeLoadFactor(measuredG) + minLoadFactorG;

    /// <summary>
    /// A load-factor reading is only usable when it is finite and in a plausible range: the
    /// simulator reports a garbage value on some frames, and every comparison against a limit
    /// would pass with NaN because comparisons with NaN are false.
    /// </summary>
    public static double SanitizeLoadFactor(double measuredG)
        => measuredG > -20 && measuredG < 20 ? measuredG : 1.0;

    /// <summary>
    /// The elevator command, limited so the airframe is not pulled past its load-factor
    /// envelope in either direction.
    /// <para>
    /// The measured load factor is what counts, not the commanded pitch: a gust or a hard
    /// landing flare can already be near the limit while the pitch command is still modest.
    /// With <see cref="LoadFactorAuthorityWindowG"/> of headroom the command passes through
    /// unchanged, so normal flying is unaffected; below that it is scaled back, and past the
    /// limit the elevator is moved to restore load rather than held against the airframe.
    /// </para>
    /// </summary>
    public static double LimitElevatorForLoadFactor(double elevatorCommand, double measuredG, OperatingLimits limits)
    {
        var g = SanitizeLoadFactor(measuredG);

        var positiveHeadroom = limits.MaxLoadFactorG - g;
        if (positiveHeadroom < 0) return UnloadElevatorCommand;

        var positiveScale = positiveHeadroom >= LoadFactorAuthorityWindowG
            ? 1.0
            : Math.Clamp(positiveHeadroom / LoadFactorAuthorityWindowG, 0.0, 1.0);
        var limited = elevatorCommand * positiveScale;

        var negativeHeadroom = g + limits.MinLoadFactorG;
        if (negativeHeadroom < 0) return ReloadElevatorCommand;

        var negativeScale = negativeHeadroom >= LoadFactorAuthorityWindowG
            ? 1.0
            : Math.Clamp(negativeHeadroom / LoadFactorAuthorityWindowG, 0.0, 1.0);

        return limited * negativeScale;
    }

    /// <summary>
    /// The fraction of the throttle authority the engines may be driven to, given where they
    /// are against their own limit: 1.0 normally, reducing toward zero as the engine enters
    /// the last tenth of its temperature band, and zero at the redline. It is a *ceiling* on
    /// the commanded fraction, not a scaled version of the current lever position: a command
    /// of full power must still be answered with full power whenever there is headroom.
    /// </summary>
    public static double ThrottleAuthorityFraction(double egtC, double maxEgtC)
    {
        if (maxEgtC <= 0 || !(egtC > 0)) return 1.0;
        if (egtC >= maxEgtC) return 0.0;
        return Math.Clamp((maxEgtC - egtC) / (0.10 * maxEgtC), 0.0, 1.0);
    }

    /// <summary>
    /// The throttle fraction the engines may be driven to, given where they are against
    /// their own limit. A turbine inside its last 10% of the temperature band has its
    /// authority scaled back, and one at the redline is cut to idle: holding the lever there
    /// is what damages an engine.
    /// </summary>
    public static double MaxThrottleFraction(double throttle, double egtC, double maxEgtC)
    {
        if (throttle <= 0) return throttle;
        var authority = ThrottleAuthorityFraction(egtC, maxEgtC);
        if (authority <= 0) return 0.0;
        return throttle * authority;
    }

    /// <summary>
    /// How far the engines are into their own limit as a 0..1 fraction (1.0 = at the
    /// redline), for the reported status. Zero when no limit applies.
    /// </summary>
    public static double EngineLimitFraction(double egtC, double maxEgtC, double rpmPercent, double maxRpmPercent = 0)
    {
        var fraction = 0.0;
        if (maxEgtC > 0 && egtC > 0) fraction = Math.Max(fraction, egtC / maxEgtC);
        // The RPM limit is only a limit when one is published: defaulting it to a redline
        // reported 0.8 of an engine limit on an aircraft that has none.
        if (maxRpmPercent > 0 && rpmPercent > 0) fraction = Math.Max(fraction, rpmPercent / maxRpmPercent);
        return fraction;
    }

    /// <summary>
    /// Moves <paramref name="current"/> toward <paramref name="target"/> by at most
    /// <paramref name="maxStep"/>. This is the ramp that turns a commanded step in vertical
    /// speed into a change the airframe can carry.
    /// <para>
    /// A non-positive or non-finite step means the vertical speed may not change at all -
    /// either the load factor has no headroom, so the aircraft may not be pulled any harder,
    /// or the reading is garbage - so the current rate is carried. Returning the target
    /// there would be the one case in which a step command is applied uncorrected.
    /// </para>
    /// </summary>
    public static double RampTo(double current, double target, double maxStep)
    {
        if (!(maxStep > 0) || double.IsNaN(maxStep) || double.IsInfinity(maxStep)) return current;

        // A non-finite target carries the current rate, exactly as the doc above promises.
        // ClampVerticalRateFpm passes NaN straight through (Math.Clamp does), and
        // "Abs(delta) <= maxStep" is false for NaN while Math.Sign(NaN) throws an
        // ArithmeticException on .NET 8 - which used to escape the 20 Hz control loop.
        if (!double.IsFinite(target)) return current;
        if (!double.IsFinite(current)) return target;

        var delta = target - current;
        if (!(Math.Abs(delta) > maxStep)) return target;
        return current + Math.Sign(delta) * maxStep;
    }
}

/// <summary>
/// The live envelope guard: resolves the limits for the aircraft, watches the measured state
/// against them, and limits what the phase executors may command. One instance per pilot,
/// advanced once per frame before the executors run so every reaction sees the same state.
/// </summary>
public sealed class EnvelopeGuard
{
    private readonly OperatingLimits _limits = new();

    /// <summary>The resolved limits, refreshed by <see cref="Update"/>.</summary>
    public OperatingLimits Limits => _limits;

    /// <summary>Whether any limit was exceeded on the last frame.</summary>
    public bool AnyExceeded { get; private set; }

    /// <summary>The most serious breach seen on the last frame. Only the first is named.</summary>
    public OperatingLimit CurrentBreach { get; private set; } = OperatingLimit.None;

    /// <summary>Measured load factor on the last frame, in g.</summary>
    public double MeasuredLoadFactor { get; private set; } = 1.0;

    /// <summary>Load-factor headroom in g on the last frame. Negative means over the limit.</summary>
    public double LoadFactorHeadroom { get; private set; }

    /// <summary>Where the engines are into their own limit, 0..1.</summary>
    public double EngineLimitRatio { get; private set; }

    /// <summary>Throttle fraction the engines may be driven to, given their own limits.</summary>
    public double MaxThrottle { get; private set; } = 1.0;
    /// <summary>
    /// The vertical speed last asked for, after the configuration and rate clamp and the
    /// load-factor ramp. Read by the phase executors, which is what they command.
    /// </summary>
    public double CommandedVerticalSpeed { get; private set; }

    private double _aircraftVsFpm;

    /// <summary>
    /// Refreshes the limits from the aircraft's own performance data and resolves the current
    /// state against them. Called once per frame on the control loop, before the executors.
    /// </summary>
    public void Update(AircraftState state, AircraftPerformance perf, int flapHandleIndex, bool gearDown)
    {
        ResolveLimits(state, perf);
        Measure(state, flapHandleIndex, gearDown);
    }

    private void ResolveLimits(AircraftState state, AircraftPerformance perf)
    {
        // The simulator's own barber pole is the authority on Vmo, because it already folds
        // in the Mach limit, the configuration and the altitude; the config V-speed is the
        // fallback and V-no the one after that.
        _limits.MaxAirspeedKt = FlightEnvelope.MaxAirspeedKt(state.BarberPoleKt, perf.Vne, perf.Vno);

        _limits.MaxFlapSpeedKt = perf.Vfe > 0 ? perf.Vfe : 0;
        _limits.MaxGearSpeedKt = perf.Vle > 0 ? perf.Vle : perf.Vlo > 0 ? perf.Vlo : 0;

        // The stall floor stops the aircraft being flown below its own stall speed: Vs0 for
        // the configuration, Vref when no stall speed is published.
        _limits.MinAirspeedKt = FlightEnvelope.MinAirspeedKt(
            perf.Vso > 0 ? perf.Vso : perf.Vs1, perf.Vref);

        var egtLimit = FlightEnvelope.EgtRedlineC(perf.EngineType);
        // The runtime engine feed is the fallback signal for an aircraft whose config could
        // not be found, so its engine type is *unknown* and not merely empty: a config that
        // says PISTON is never second-guessed, because a piston's per-cylinder exhaust gas
        // temperature has no single redline. Only a turbine publishes N1/N2, and above idle
        // only a running turbine does - the same rule the checklist environment uses.
        if (egtLimit <= 0 && string.IsNullOrWhiteSpace(perf.EngineType) &&
            AIPilot.TurbineFromRuntime(state))
        {
            egtLimit = FlightEnvelope.EgtRedlineC("JET");
        }
        _limits.MaxEgtC = egtLimit;
        _limits.MaxEngineRpmPercent = egtLimit > 0 ? FlightEnvelope.EngineRpmRedlinePercent : 0;

        // No MSFS climb profile publishes a rate - it publishes speeds, Mach and a ceiling -
        // so the climb cap is a generic figure applied as a clamp, never as a target. A
        // descent profile does publish its own rate, and it is used, capped so no published
        // figure can be exceeded either.
        var publishedDescent = perf.DescentPerf.Profiles
            .Select(p => p.RateOfDescent)
            .Where(r => r > 0)
            .DefaultIfEmpty(0)
            .Max();
        if (publishedDescent <= 0) publishedDescent = FlightEnvelope.DefaultMaxDescentRateFpm;

        _limits.MaxClimbRateFpm = Math.Min(FlightEnvelope.DefaultMaxClimbRateFpm, FlightEnvelope.AbsoluteMaxClimbRateFpm);
        _limits.MaxDescentRateFpm = Math.Min(publishedDescent, FlightEnvelope.AbsoluteMaxDescentRateFpm);
    }

    private void Measure(AircraftState state, int flapHandleIndex, bool gearDown)
    {
        MeasuredLoadFactor = FlightEnvelope.SanitizeLoadFactor(state.GLoad);

        var maxG = FlightEnvelope.MaxLoadFactorG(flapHandleIndex, gearDown);
        _limits.MaxLoadFactorG = maxG;
        LoadFactorHeadroom = FlightEnvelope.LoadFactorHeadroomG(MeasuredLoadFactor, maxG);

        // Armed with the vertical speed actually being flown rather than the last command, so
        // an aircraft already descending at 2000 fpm is held there instead of being yanked
        // back to the previous command.
        _aircraftVsFpm = Math.Abs(state.VerticalSpeed) <= FlightEnvelope.AbsoluteMaxDescentRateFpm
            ? state.VerticalSpeed
            : 0;

        MaxThrottle = ThrottleAuthority(state);
        EngineLimitRatio = FlightEnvelope.EngineLimitFraction(
            state.EngineEgtC.Length > 0 ? state.EngineEgtC.Max() : 0,
            _limits.MaxEgtC,
            state.EngineMaxRpmPct.Length > 0 ? state.EngineMaxRpmPct.Max() : 0,
            _limits.MaxEngineRpmPercent);

        Classify(state, flapHandleIndex, gearDown);
    }

    /// <summary>
    /// The ceiling on the throttle fraction, in 0..1, given where the engines are against
    /// their own limit. This is a ceiling on what may be *commanded*, not the current lever
    /// position scaled: an aircraft at 80% with full authority still has full power available.
    /// </summary>
    private double ThrottleAuthority(AircraftState state)
    {
        if (!_limits.HasEgtLimit) return 1.0;

        var egt = state.EngineEgtC.Length > 0 ? state.EngineEgtC.Max() : 0;
        return FlightEnvelope.ThrottleAuthorityFraction(egt, _limits.MaxEgtC);
    }

    private void Classify(AircraftState state, int flapHandleIndex, bool gearDown)
    {
        AnyExceeded = false;
        CurrentBreach = OperatingLimit.None;

        void Note(OperatingLimit breach)
        {
            AnyExceeded = true;
            if (CurrentBreach == OperatingLimit.None) CurrentBreach = breach;
        }

        // The simulator's own over-limit flag is authoritative: it is the message the aircraft
        // itself raises, and it is read before any arithmetic is consulted.
        if (state.OverspeedWarning) Note(OperatingLimit.Airspeed);

        // Flap and gear limits are configuration limits, so they are tested against the
        // configuration actually selected rather than against the clean limit. The breach
        // is named after the limit that was actually broken: the old ternary fell through
        // to GearSpeed whenever the flaps were up, so a clean overspeed above Vne/Vno was
        // reported as "gear extended above Vle" with the gear retracted.
        var configurationLimit = _limits.ConfigurationSpeedLimitKt(flapHandleIndex, gearDown);
        if (configurationLimit > 0 && state.IndicatedAirspeed > configurationLimit + 2)
        {
            Note(flapHandleIndex >= 1 && _limits.HasFlapSpeedLimit && state.IndicatedAirspeed > _limits.MaxFlapSpeedKt
                ? OperatingLimit.FlapSpeed
                : gearDown && _limits.HasGearSpeedLimit && state.IndicatedAirspeed > _limits.MaxGearSpeedKt
                    ? OperatingLimit.GearSpeed
                    : OperatingLimit.Airspeed);
        }

        if (LoadFactorHeadroom < 0) Note(OperatingLimit.LoadFactor);
        if (FlightEnvelope.NegativeLoadFactorHeadroomG(MeasuredLoadFactor, _limits.MinLoadFactorG) < 0)
        {
            Note(OperatingLimit.LoadFactor);
        }

        if (_limits.MaxDescentRateFpm > 0 && state.VerticalSpeed < -_limits.MaxDescentRateFpm - 50)
        {
            Note(OperatingLimit.VerticalRate);
        }
        if (_limits.MaxClimbRateFpm > 0 && state.VerticalSpeed > _limits.MaxClimbRateFpm + 50)
        {
            Note(OperatingLimit.VerticalRate);
        }

        if (state.EngineEgtC.Length > 0 && _limits.HasEgtLimit && state.EngineEgtC.Max() >= _limits.MaxEgtC)
        {
            Note(OperatingLimit.EngineEgt);
        }
        // Only a published limit is a limit: MaxEngineRpmPercent is 0 for an aircraft that
        // publishes none, and "Max >= 0" is then true for every reading - which reported an
        // engine breach on every frame of every piston flight and left the panel's envelope
        // line permanently red.
        if (state.EngineMaxRpmPct.Length > 0 && _limits.MaxEngineRpmPercent > 0 &&
            state.EngineMaxRpmPct.Max() >= _limits.MaxEngineRpmPercent)
        {
            Note(OperatingLimit.EngineRpm);
        }
    }

    /// <summary>
    /// Clamps a commanded airspeed into the aircraft's envelope for the configuration actually
    /// flown. An ATC speed assignment is clamped too: a controller's instruction is not
    /// authority over the airframe, unlike a turbulence reduction which only ever slows the
    /// aircraft's own perf-derived target down.
    /// </summary>
    public double CappedAirspeedKt(double commandedKt, int flapHandleIndex, bool gearDown)
        => FlightEnvelope.ClampAirspeedKt(commandedKt, _limits, flapHandleIndex, gearDown);

    /// <summary>
    /// Clamps a commanded vertical speed into the envelope and ramps it into range at the rate
    /// the load factor allows, so a commanded step is a ramp the airframe can carry. This is
    /// the value the phase executors hand the autopilot.
    /// </summary>
    public double CappedVerticalRateFpm(double commandedFpm, double dt)
    {
        if (dt <= 0 || double.IsNaN(dt) || double.IsInfinity(dt)) dt = 0.05;

        var target = FlightEnvelope.ClampVerticalRateFpm(commandedFpm, _limits);
        var maxChange = FlightEnvelope.MaxVerticalAccelerationFpmPerSecond(LoadFactorHeadroom) * dt;
        CommandedVerticalSpeed = FlightEnvelope.RampTo(_aircraftVsFpm, target, maxChange);
        return CommandedVerticalSpeed;
    }

    /// <summary>
    /// The elevator command, limited by the load-factor envelope. Used by the direct-control
    /// path, where the pitch demand is the pilot's own rather than the autopilot's.
    /// </summary>
    public double CappedElevator(double elevatorCommand)
        => FlightEnvelope.LimitElevatorForLoadFactor(elevatorCommand, MeasuredLoadFactor, _limits);

    /// <summary>A one-line summary of the resolved envelope, for status output.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            _limits.HasAirspeedLimit
                ? $"max {Parsing.Format(_limits.MaxAirspeedKt, "0")} kt"
                : "no published speed limit",
            $"+{Parsing.Format(_limits.MaxLoadFactorG, "0.0")}g/" +
            $"-{Parsing.Format(_limits.MinLoadFactorG, "0.0")}g",
            $"climb <= {Parsing.Format(_limits.MaxClimbRateFpm, "0")} fpm, " +
            $"descent <= {Parsing.Format(_limits.MaxDescentRateFpm, "0")} fpm"
        };

        if (_limits.HasSpeedFloor) parts.Add($"stall floor {Parsing.Format(_limits.MinAirspeedKt, "0")} kt");
        if (_limits.HasFlapSpeedLimit) parts.Add($"Vfe {Parsing.Format(_limits.MaxFlapSpeedKt, "0")}");
        if (_limits.HasGearSpeedLimit) parts.Add($"Vle {Parsing.Format(_limits.MaxGearSpeedKt, "0")}");
        if (_limits.HasEgtLimit) parts.Add($"EGT redline {Parsing.Format(_limits.MaxEgtC, "0")} C");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A one-line description of the current breach, or null when the aircraft is inside its
    /// envelope. The caller rate-limits the logging.
    /// </summary>
    public string? BreachDescription()
    {
        if (!AnyExceeded) return null;

        return CurrentBreach switch
        {
            OperatingLimit.Airspeed => "airspeed above Vmo",
            OperatingLimit.LoadFactor =>
                $"load factor {Parsing.Format(MeasuredLoadFactor, "0.00")} g outside the " +
                $"+{Parsing.Format(_limits.MaxLoadFactorG, "0.0")}g/" +
                $"-{Parsing.Format(_limits.MinLoadFactorG, "0.0")}g envelope",
            OperatingLimit.VerticalRate => "vertical rate beyond the aircraft's limit",
            OperatingLimit.FlapSpeed =>
                $"flaps extended above Vfe ({Parsing.Format(_limits.MaxFlapSpeedKt, "0")} kt)",
            OperatingLimit.GearSpeed =>
                $"gear extended above Vle ({Parsing.Format(_limits.MaxGearSpeedKt, "0")} kt)",
            OperatingLimit.EngineRpm => "engine above its maximum RPM",
            OperatingLimit.EngineEgt =>
                $"EGT above the {Parsing.Format(_limits.MaxEgtC, "0")} C redline",
            _ => null
        };
    }
}
