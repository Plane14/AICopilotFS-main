using System;
using MSFS2024AIPilot.Core.SimConnect;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Guidance;

/// <summary>What a phase wants the autopilot to do this frame.</summary>
public sealed class ControlTargets
{
    /// <summary>Selected heading (deg true) or null to hold current.</summary>
    public double? TargetHeadingDeg { get; set; }
    /// <summary>Target altitude MSL ft.</summary>
    public double? TargetAltitudeFt { get; set; }
    /// <summary>Target indicated airspeed knots.</summary>
    public double? TargetAirspeedKt { get; set; }
    /// <summary>Target vertical speed fpm (used when no altitude target).</summary>
    public double? TargetVsFpm { get; set; }
    /// <summary>Max bank angle the lateral loop may use, degrees.</summary>
    public double MaxBankDeg { get; set; } = 25;
    /// <summary>Throttle fraction 0..1 for direct control; null = auto.</summary>
    public double? ThrottleFraction { get; set; }
    /// <summary>Flap handle index to select, or null.</summary>
    public int? FlapIndex { get; set; }
    /// <summary>Gear position: true down, false up, null = don't change.</summary>
    public bool? GearDown { get; set; }
    /// <summary>True to fly the localizer/glideslope (ILS final).</summary>
    public bool TrackLocalizer { get; set; }
    /// <summary>True to track GPS NAV mode on the flight plan.</summary>
    public bool TrackNav { get; set; }
    /// <summary>Pitch/bank control authority scale 0..1 (softened in turbulence).</summary>
    public double ControlScale { get; set; } = 1.0;
}

/// <summary>Simple PID controller with anti-windup.</summary>
public sealed class PidLoop
{
    private readonly double _kp, _ki, _kd;
    private double _integral;
    private double _prevError;
    private double _integralLimit;

    public PidLoop(double kp, double ki, double kd, double integralLimit = 100)
    {
        _kp = kp; _ki = ki; _kd = kd; _integralLimit = integralLimit;
    }

    public void Reset() { _integral = 0; _prevError = 0; }

    /// <summary>Returns the control output for the given error.</summary>
    public double Update(double error, double dt)
    {
        _integral += error * dt;
        _integral = Math.Clamp(_integral, -_integralLimit, _integralLimit);
        var derivative = dt > 0 ? (error - _prevError) / dt : 0;
        _prevError = error;
        return _kp * error + _ki * _integral + _kd * derivative;
    }
}

/// <summary>
/// Hybrid autopilot: uses the aircraft's native autopilot for enroute phases and
/// direct surface control (PID on the axis events) for ground and precision work.
/// </summary>
public sealed class HybridAutopilot
{
    private readonly SimConnectClient _sc;
    private ControlTargets _targets = new();
    private ControlTargets _active = new();

    // Direct-control loops (per-phase instances via ResetAll).
    private readonly PidLoop _pitch = new(0.02, 0.002, 0.05);
    private readonly PidLoop _bank = new(0.03, 0.001, 0.04);
    private readonly PidLoop _speed = new(0.01, 0.005, 0.02);
    private readonly PidLoop _altitude = new(0.005, 0.0005, 0.02);

    private bool _directMode;
    private bool _axesNeutralised;
    private double _lastSentPitch, _lastSentBank, _lastSentRudder, _lastSentThrottle = double.NaN;
    private bool _axesPrimed;

    // Last values sent to the native autopilot (dedup — these are toggle events).
    private double? _lastHdg, _lastAlt, _lastVs, _lastIas, _lastThr;
    private bool _lastApr, _lastNav;
    private int? _lastFlap;
    private bool? _lastGear;

    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 0.5;

    public HybridAutopilot(SimConnectClient sc) => _sc = sc;

    public ControlTargets Targets => _targets;
    public bool DirectMode => _directMode;

    /// <summary>Set the targets for the current frame.</summary>
    public void SetTargets(ControlTargets targets) => _targets = targets;

    public void ResetAll()
    {
        _pitch.Reset(); _bank.Reset(); _speed.Reset(); _altitude.Reset();
    }

    // ---------------- Native autopilot commands ----------------

    public void ApMaster(bool on) => _sc.FireEvent("AP_MASTER", on ? (uint)1 : (uint)0);
    public void HeadingHold(double headingDeg)
    {
        _sc.FireEvent("HEADING_BUG_SET", (uint)Math.Round(Geo.GeoMath.NormalizeDeg(headingDeg)));
        _sc.FireEvent("AP_PANEL_HEADING_HOLD");
    }
    public void AltitudeHold(double altitudeFt)
    {
        _sc.FireEvent("AP_ALT_VAR_SET_ENGLISH", (uint)Math.Clamp(Math.Round(altitudeFt), 0, 60000));
        _sc.FireEvent("AP_PANEL_ALTITUDE_HOLD");
    }
    public void VerticalSpeedHold(double vsFpm)
    {
        // AP_VS_VAR_SET_ENGLISH takes the value scaled by 65536/1000 fpm per unit.
        var clamped = Environment.FlightEnvelope.ClampVerticalSpeedFpm(vsFpm, vsFpm > 0);
        var raw = (int)Math.Round(clamped * 65536.0 / 1000.0);
        _sc.FireEvent("AP_VS_VAR_SET_ENGLISH", unchecked((uint)raw));
        _sc.FireEvent("AP_PANEL_VS_HOLD");
    }
    public void AirspeedHold(double iasKnots)
    {
        _sc.FireEvent("AP_SPD_VAR_SET", (uint)Math.Clamp(Math.Round(iasKnots), 40, 500));
        _sc.FireEvent("AP_AIRSPEED_HOLD");
    }
    public void ApproachHold() => _sc.FireEvent("AP_APR_HOLD");
    public void NavHold() => _sc.FireEvent("AP_NAV1_HOLD");

    /// <summary>
    /// Clears the nav/approach latch memory so the next Update re-sends the
    /// hold events. Needed after a phase that flew raw heading commands
    /// (e.g. racetrack) — otherwise the latched flag suppresses re-engaging
    /// NAV/APR and the aircraft never resumes the flight plan.
    /// </summary>
    public void ResetNavLatches() { _lastNav = false; _lastApr = false; }
    public void AutoThrottleArm() => _sc.FireEvent("AUTO_THROTTLE_ARM");

    /// <summary>Selects a flap detent by index (0 = up).</summary>
    public void SelectFlaps(int index)
    {
        if (index <= 0) { _sc.FireEvent("FLAPS_UP"); return; }
        if (index == 1) { _sc.FireEvent("FLAPS_1"); return; }
        if (index == 2) { _sc.FireEvent("FLAPS_2"); return; }
        if (index >= 3) { _sc.FireEvent("FLAPS_3"); return; }
    }

    public void Gear(bool down)
    {
        if (down) _sc.FireEvent("GEAR_DOWN"); else _sc.FireEvent("GEAR_UP");
    }

    public void ParkingBrake(bool set)
    {
        // PARKING_BRAKE_SET is a set event; data 1 = set, 0 = release.
        _sc.FireEvent("PARKING_BRAKE_SET", set ? (uint)1 : (uint)0);
    }

    public void SetThrottleFraction(double fraction)
        => _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(fraction * 16384), 0, 16384));

    // ---------------- Direct surface control ----------------

    /// <summary>
    /// Runs one control frame. In native mode it programs the autopilot targets;
    /// in direct mode it computes axis/throttle outputs from the PID loops.
    /// </summary>
    public void Update(AircraftState s, double dt)
    {
        var t = _targets;

        if (!_directMode)
        {
            // Native autopilot: push whatever targets the phase wants, but only
            // when they change — these events toggle modes, so re-sending every
            // frame would flicker the autopilot.
            if (t.TargetHeadingDeg is { } hdg && !Near(_lastHdg, hdg)) { HeadingHold(hdg); _lastHdg = hdg; }
            if (t.TargetAltitudeFt is { } alt && !Near(_lastAlt, alt)) { AltitudeHold(alt); _lastAlt = alt; }
            if (t.TargetVsFpm is { } vs && t.TargetAltitudeFt is null && !Near(_lastVs, vs)) { VerticalSpeedHold(vs); _lastVs = vs; }
            if (t.TargetAirspeedKt is { } ias && !Near(_lastIas, ias)) { AirspeedHold(ias); _lastIas = ias; }
            if (t.TrackLocalizer && !_lastApr) { ApproachHold(); _lastApr = true; }
            if (t.TrackNav && !_lastNav) { NavHold(); _lastNav = true; }
            if (t.ThrottleFraction is { } thr && !Near(_lastThr, thr)) { SetThrottleFraction(thr); _lastThr = thr; }
            if (t.FlapIndex is { } fl && fl != _lastFlap) { SelectFlaps(fl); _lastFlap = fl; }
            if (t.GearDown is { } g && g != _lastGear) { Gear(g); _lastGear = g; }
            return;
        }

        // Direct mode: PID on surfaces.
        double pitchCmd = 0, bankCmd = 0, rudderCmd = 0, throttle = t.ThrottleFraction ?? 0;

        // Pitch: hold altitude or vertical speed.
        if (t.TargetAltitudeFt is { } altT)
        {
            var altErr = altT - s.Altitude;
            var vsCmd = Math.Clamp(altErr * 8.0, -FlightEnvelopeGuardMaxVs(s), FlightEnvelopeGuardMaxVs(s));
            pitchCmd = _pitch.Update(vsCmd - s.VerticalSpeed, dt) * t.ControlScale;
        }
        else if (t.TargetVsFpm is { } vsT)
        {
            pitchCmd = _pitch.Update(vsT - s.VerticalSpeed, dt) * 0.02 * t.ControlScale;
        }

        // Bank: steer toward target heading.
        if (t.TargetHeadingDeg is { } hdgT)
        {
            var hdgErr = Geo.GeoMath.AngleDelta(hdgT, s.TrueHeading);
            var maxBank = Math.Min(t.MaxBankDeg, t.MaxBankDeg * t.ControlScale + 5);
            var desiredBank = Math.Clamp(hdgErr * 1.5, -maxBank, maxBank);
            bankCmd = _bank.Update(desiredBank - s.Bank, dt) * t.ControlScale;
            rudderCmd = -hdgErr / 45.0; // gentle coordination
        }

        // Speed: adjust throttle toward target IAS.
        if (t.TargetAirspeedKt is { } iasT && s.OnGround == false)
        {
            var spdErr = iasT - s.IndicatedAirspeed;
            throttle = Math.Clamp(throttle + _speed.Update(spdErr, dt) * 0.02, 0, 1);
        }

        SendAxes(pitchCmd, bankCmd, rudderCmd, throttle);
    }

    private static double FlightEnvelopeGuardMaxVs(AircraftState s) => 2500;

    /// <summary>Writes the four control axes. Values are 13-bit signed centred at 8192.</summary>
    private void SendAxes(double pitchCmd, double bankCmd, double rudderCmd, double throttle)
    {
        // Commands are -1..+1; neutral is 0.
        var pitch = Math.Clamp(pitchCmd, -1, 1);
        var bank = Math.Clamp(bankCmd, -1, 1);
        var rudder = Math.Clamp(rudderCmd, -1, 1);

        // Deadband: skip the four axis events when outputs haven't meaningfully
        // changed — at 20 Hz this cuts hundreds of no-op SimConnect events per
        // second during steady flight. ~0.4% of full-scale deflection.
        const double eps = 0.004;
        if (_axesPrimed &&
            Math.Abs(pitch - _lastSentPitch) < eps &&
            Math.Abs(bank - _lastSentBank) < eps &&
            Math.Abs(rudder - _lastSentRudder) < eps &&
            Math.Abs(throttle - _lastSentThrottle) < eps * 2 &&
            !(pitch == 0 && bank == 0 && rudder == 0 && throttle == 0 && !_axesNeutralised))
            return;

        _axesPrimed = true;
        _lastSentPitch = pitch; _lastSentBank = bank;
        _lastSentRudder = rudder; _lastSentThrottle = throttle;

        if (pitch == 0 && bank == 0 && rudder == 0 && throttle == 0)
        {
            if (_axesNeutralised) return;
            _axesNeutralised = true;
        }
        else _axesNeutralised = false;

        Axis("AXIS_ELEVATOR_SET", pitch);
        Axis("AXIS_AILERONS_SET", bank);
        Axis("AXIS_RUDDER_SET", rudder);
        _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(throttle * 16384), 0, 16384));
    }

    private void Axis(string name, double value01)
    {
        // -1..+1 → 0..16383 with neutral at ~8192.
        var raw = (uint)Math.Clamp(Math.Round((value01 + 1.0) * 16384.0 / 2.0), 0, 16383);
        _sc.FireEvent(name, raw);
    }

    /// <summary>Returns all axes to neutral and releases the throttle to idle-ish.</summary>
    public void Neutralize()
    {
        // Already-neutral surfaces need no events — callers such as the service
        // wait invoke this every tick and would otherwise spam 60 events/s.
        if (_axesNeutralised) return;
        var neutral = (uint)Math.Round((0.0 + 1.0) * 16384.0 / 2.0);
        _sc.FireEvent("AXIS_ELEVATOR_SET", neutral);
        _sc.FireEvent("AXIS_AILERONS_SET", neutral);
        _sc.FireEvent("AXIS_RUDDER_SET", neutral);
        _axesNeutralised = true;
        // The sim's surfaces are now neutral — sync the deadband cache or the
        // next matching command would be suppressed while the aircraft sits
        // with neutral controls. Throttle is untouched by Neutralize, so its
        // last-sent value stays accurate.
        _lastSentPitch = _lastSentBank = _lastSentRudder = 0;
    }

    /// <summary>Switch between native and direct control, cleaning up the other mode.</summary>
    public void SetDirectMode(bool direct)
    {
        if (_directMode == direct) return;
        _directMode = direct;
        ResetAll();
        _axesPrimed = false;
        if (direct) ApMaster(false);
        else
        {
            Neutralize();
            // Mode latches must be re-armed for the next leg: NAV/APR hold are
            // one-shot events, and without this a second flight never re-engages
            // approach or GPS navigation tracking.
            _lastNav = _lastApr = false;
        }
    }
}
