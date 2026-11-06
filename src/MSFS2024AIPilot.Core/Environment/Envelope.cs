using System;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Environment;

public enum OperatingLimit
{
    None,
    MaxAirspeed,
    MaxLoadFactor,
    MinLoadFactor,
    MaxClimbRate,
    MaxDescentRate,
    MinAirspeed,
    EngineEgt,
    EngineRpm,
}

/// <summary>The aircraft's operating limits, derived from its own indications where possible.</summary>
public sealed class OperatingLimits
{
    /// <summary>Never-exceed speed from the aircraft's barber pole, knots. 0 = unknown.</summary>
    public double MaxAirspeedKt { get; set; }
    /// <summary>Load factor ceiling for the current configuration.</summary>
    public double MaxLoadFactorG { get; set; } = FlightEnvelope.MaxLoadFactorCleanG;
    public double MinLoadFactorG { get; set; } = FlightEnvelope.MinLoadFactorGMagnitude;
    /// <summary>Max speed with current flap setting, knots. 0 = no flap limit known.</summary>
    public double MaxFlapSpeedKt { get; set; }
    /// <summary>Max speed with gear down, knots. 0 = unknown.</summary>
    public double MaxGearSpeedKt { get; set; }
    public double MaxClimbRateFpm { get; set; } = FlightEnvelope.DefaultMaxClimbRateFpm;
    public double MaxDescentRateFpm { get; set; } = FlightEnvelope.DefaultMaxDescentRateFpm;
    /// <summary>Minimum safe airspeed (stall margin), knots. 0 = unknown.</summary>
    public double MinAirspeedKt { get; set; }
    public double MaxEgtC { get; set; }
    public double MaxEngineRpmPercent { get; set; }

    public bool HasAirspeedLimit => MaxAirspeedKt > 0;
    public bool HasFlapSpeedLimit => MaxFlapSpeedKt > 0;
    public bool HasGearSpeedLimit => MaxGearSpeedKt > 0;
    public bool HasSpeedFloor => MinAirspeedKt > 0;
    public bool HasEgtLimit => MaxEgtC > 0;

    /// <summary>The lowest speed limit that applies in the current configuration.</summary>
    public double ConfigurationSpeedLimitKt(int flapHandleIndex, bool gearDown)
    {
        double limit = double.MaxValue;
        if (HasAirspeedLimit) limit = MaxAirspeedKt;
        // Flaps/gear limits only constrain when configured; the config parser supplies
        // the numbers and this picks the applicable one.
        if (flapHandleIndex > 0 && HasFlapSpeedLimit) limit = Math.Min(limit, MaxFlapSpeedKt);
        if (gearDown && HasGearSpeedLimit) limit = Math.Min(limit, MaxGearSpeedKt);
        return limit == double.MaxValue ? 0 : limit;
    }
}

/// <summary>
/// Pure envelope arithmetic plus <see cref="EnvelopeGuard"/>, applied to every
/// command the pilot issues so it never asks the aircraft for something it cannot do.
/// </summary>
public static class FlightEnvelope
{
    public const double MaxLoadFactorCleanG = 2.5;
    public const double MaxLoadFactorConfiguredG = 2.0;
    public const double MinLoadFactorGMagnitude = 1.0;
    public const double DefaultMaxClimbRateFpm = 3000;
    public const double DefaultMaxDescentRateFpm = 3000;
    public const double AbsoluteMaxClimbRateFpm = 4000;
    public const double AbsoluteMaxDescentRateFpm = 4000;
    public const double SpeedLimitBelowTenThousandKt = 250;
    public const double SpeedLimitFloorFt = 10000;
    public const double JetEgtRedlineC = 950;
    public const double EngineRpmRedlinePct = 100;

    /// <summary>250kt below 10,000ft is the regulatory cap; above it the aircraft's own limit applies.</summary>
    public static double RegulatorySpeedCapKt(double altitudeFt) =>
        altitudeFt < SpeedLimitFloorFt ? SpeedLimitBelowTenThousandKt : double.MaxValue;

    public static double ClampAirspeed(double requestedKt, OperatingLimits limits, AircraftState state)
    {
        var cap = limits.ConfigurationSpeedLimitKt((int)Math.Round(state.FlapsHandleIndex), state.GearDown);
        var ceiling = Math.Min(cap > 0 ? cap : double.MaxValue, RegulatorySpeedCapKt(state.Altitude));
        var result = requestedKt;
        if (ceiling < double.MaxValue) result = Math.Min(result, ceiling);
        if (limits.HasSpeedFloor) result = Math.Max(result, limits.MinAirspeedKt);
        // Never command below stall margin in the current config.
        if (limits.MinAirspeedKt > 0) result = Math.Max(result, limits.MinAirspeedKt);
        return Math.Max(0, result);
    }

    public static double ClampBankDeg(double requestedDeg, TurbulenceLevel turbulence, bool onApproach)
    {
        var ceiling = WeatherModel.ControlBankDeg(turbulence);
        if (onApproach) ceiling = Math.Min(ceiling, 20);
        return Math.Clamp(requestedDeg, -ceiling, ceiling);
    }

    public static double ClampVerticalSpeedFpm(double requestedFpm, bool climbing)
    {
        var abs = Math.Abs(requestedFpm);
        var cap = climbing ? AbsoluteMaxClimbRateFpm : AbsoluteMaxDescentRateFpm;
        return Math.Clamp(requestedFpm, -cap, cap);
    }

    public static double ClampLoadFactor(double loadFactor, double maxG, double minG)
        => Math.Clamp(loadFactor, -minG, maxG);
}

/// <summary>
/// Stateful guard that watches the live state and reports when a commanded target
/// would take the aircraft outside its envelope, so the phase executors can soften
/// the command before it is sent.
/// </summary>
public sealed class EnvelopeGuard
{
    private readonly OperatingLimits _limits;

    public EnvelopeGuard(OperatingLimits limits) => _limits = limits;

    /// <summary>Returns the speed the pilot may command right now.</summary>
    public double AllowedAirspeedKt(double requestedKt, AircraftState s) =>
        FlightEnvelope.ClampAirspeed(requestedKt, _limits, s);

    public bool OverspeedRisk(double commandedKt, AircraftState s)
    {
        var cap = _limits.ConfigurationSpeedLimitKt((int)Math.Round(s.FlapsHandleIndex), s.GearDown);
        var ceiling = Math.Min(cap > 0 ? cap : double.MaxValue, FlightEnvelope.RegulatorySpeedCapKt(s.Altitude));
        return ceiling < double.MaxValue && commandedKt > ceiling * 0.98;
    }

    public bool StallRisk(AircraftState s) =>
        s.StallWarning || (_limits.HasSpeedFloor && s.IndicatedAirspeed < _limits.MinAirspeedKt * 1.1);

    public bool Overspeed(AircraftState s) =>
        s.OverspeedWarning || OverspeedRisk(s.IndicatedAirspeed, s);

    public bool EngineOverheat(AircraftState s)
    {
        if (!_limits.HasEgtLimit || s.EngineEgtC.Length == 0) return false;
        foreach (var egt in s.EngineEgtC)
            if (egt > _limits.MaxEgtC) return true;
        return false;
    }

    public bool EngineOverRpm(AircraftState s)
    {
        if (s.EngineMaxRpmPct.Length == 0) return false;
        foreach (var rpm in s.EngineMaxRpmPct)
            if (rpm > FlightEnvelope.EngineRpmRedlinePct) return true;
        return false;
    }
}
