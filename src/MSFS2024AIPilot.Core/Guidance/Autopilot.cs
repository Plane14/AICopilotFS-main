using System;
using MSFS2024AIPilot.Core.Aircraft;
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

    // Direct-control loops (per-profile instances via ApplyProfile).
    private PidLoop _pitch = new(0.02, 0.002, 0.05);
    private PidLoop _bank = new(0.03, 0.001, 0.04);
    private PidLoop _speed = new(0.01, 0.005, 0.02);
    private PidLoop _altitude = new(0.005, 0.0005, 0.02);

    private AircraftControlProfile _profile = AircraftControlProfile.ForKind(AircraftKind.Airplane, new AircraftPerformance());

    private bool _directMode;
    private bool _axesNeutralised;
    private double _lastSentPitch, _lastSentBank, _lastSentRudder, _lastSentThrottle = double.NaN;
    private bool _axesPrimed;
    private bool _apMasterSent;
    private bool _hdgHoldEngaged;

    /// <summary>Within this distance of the altitude target, altitude hold takes over from VS.</summary>
    private const double AltCaptureFt = 30;

    // Last values sent to the native autopilot (dedup — these are toggle events).
    private double? _lastHdg, _lastAlt, _lastVs, _lastIas, _lastThr;
    private bool _lastApr, _lastNav;
    private int? _lastFlap;
    private bool? _lastGear;
    private DateTime _lastDirectLog = DateTime.MinValue;

    public event Action<string>? OnLog;

    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 0.5;

    public HybridAutopilot(SimConnectClient sc, AircraftControlProfile? profile = null)
    {
        _sc = sc;
        if (profile is not null) ApplyProfile(profile);
    }

    public void ApplyProfile(AircraftControlProfile profile)
    {
        _profile = profile;
        _pitch = new PidLoop(profile.Pitch.Kp, profile.Pitch.Ki, profile.Pitch.Kd, profile.Pitch.IntegralLimit);
        _bank = new PidLoop(profile.Bank.Kp, profile.Bank.Ki, profile.Bank.Kd, profile.Bank.IntegralLimit);
        _speed = new PidLoop(profile.Speed.Kp, profile.Speed.Ki, profile.Speed.Kd, profile.Speed.IntegralLimit);
        _altitude = new PidLoop(profile.Altitude.Kp, profile.Altitude.Ki, profile.Altitude.Kd, profile.Altitude.IntegralLimit);
        ResetAll();
        Log($"Profile applied: {profile.Kind} maxBank={profile.MaxBankDeg:F0} ctrl={profile.ControlAuthority:F2}");
    }

    private void Log(string msg) => OnLog?.Invoke($"[Autopilot] {msg}");

    public ControlTargets Targets => _targets;
    public bool DirectMode => _directMode;

    /// <summary>Set the targets for the current frame.</summary>
    public void SetTargets(ControlTargets targets) => _targets = targets;

    public void ResetAll()
    {
        _pitch.Reset(); _bank.Reset(); _speed.Reset(); _altitude.Reset();
    }

    // ---------------- Native autopilot commands ----------------

    public void ApMaster(bool on)
    {
        // AP_MASTER is a toggle — callers invoke this every control tick, so
        // only transmit on an actual state change.
        if (_apMasterSent == on) return;
        _apMasterSent = on;
        _sc.FireEvent("AP_MASTER", on ? (uint)1 : (uint)0);
        Log($"AP_MASTER={(on ? "ON" : "OFF")}");
    }
    public void HeadingHold(double headingDeg)
    {
        _sc.FireEvent("HEADING_BUG_SET", (uint)Math.Round(Geo.GeoMath.NormalizeDeg(headingDeg)));
        _sc.FireEvent("AP_PANEL_HEADING_HOLD");
        Log($"HEADING_BUG_SET={headingDeg:F0} AP_PANEL_HEADING_HOLD");
    }
    public void AltitudeHold(double altitudeFt)
    {
        _sc.FireEvent("AP_ALT_VAR_SET_ENGLISH", (uint)Math.Clamp(Math.Round(altitudeFt), 0, 60000));
        _sc.FireEvent("AP_PANEL_ALTITUDE_HOLD");
        Log($"AP_ALT_VAR_SET_ENGLISH={altitudeFt:F0} AP_PANEL_ALTITUDE_HOLD");
    }
    public void VerticalSpeedHold(double vsFpm)
    {
        // AP_VS_VAR_SET_ENGLISH takes the reference vertical speed directly in
        // feet per minute (per the MSFS event docs). The event payload is
        // uint32 but MSFS interprets the high bit as sign: negative values
        // (descents) are passed via unchecked two's-complement cast.
        var clamped = Environment.FlightEnvelope.ClampVerticalSpeedFpm(vsFpm, vsFpm > 0);
        var raw = (int)Math.Round(clamped);
        _sc.FireEvent("AP_VS_VAR_SET_ENGLISH", unchecked((uint)raw));
        _sc.FireEvent("AP_PANEL_VS_HOLD");
        Log($"AP_VS_VAR_SET_ENGLISH={raw} AP_PANEL_VS_HOLD (requested={vsFpm:F0}fpm)");
    }
    public void AirspeedHold(double iasKnots)
    {
        _sc.FireEvent("AP_SPD_VAR_SET", (uint)Math.Clamp(Math.Round(iasKnots), 40, 500));
        _sc.FireEvent("AP_AIRSPEED_HOLD");
        Log($"AP_SPD_VAR_SET={iasKnots:F1} AP_AIRSPEED_HOLD");
    }
    public void ApproachHold() { _sc.FireEvent("AP_APR_HOLD"); Log("AP_APR_HOLD"); }
    public void NavHold() { _sc.FireEvent("AP_NAV1_HOLD"); Log("AP_NAV1_HOLD"); }

    /// <summary>
    /// Clears the nav/approach latch memory so the next Update re-sends the
    /// hold events. Needed after a phase that flew raw heading commands
    /// (e.g. racetrack) — otherwise the latched flag suppresses re-engaging
    /// NAV/APR and the aircraft never resumes the flight plan.
    /// </summary>
    public void ResetNavLatches() { _lastNav = false; _lastApr = false; Log("ResetNavLatches"); }
    public void AutoThrottleArm() { _sc.FireEvent("AUTO_THROTTLE_ARM"); Log("AUTO_THROTTLE_ARM"); }

    /// <summary>Selects a flap detent by index (0 = up).</summary>
    public void SelectFlaps(int index)
    {
        if (index <= 0) { _sc.FireEvent("FLAPS_UP"); Log($"SelectFlaps={index} FLAPS_UP"); return; }
        if (index == 1) { _sc.FireEvent("FLAPS_1"); Log($"SelectFlaps={index} FLAPS_1"); return; }
        if (index == 2) { _sc.FireEvent("FLAPS_2"); Log($"SelectFlaps={index} FLAPS_2"); return; }
        if (index >= 3) { _sc.FireEvent("FLAPS_3"); Log($"SelectFlaps={index} FLAPS_3"); return; }
    }

    public void Gear(bool down)
    {
        if (down) _sc.FireEvent("GEAR_DOWN"); else _sc.FireEvent("GEAR_UP");
        Log($"Gear={(down ? "DOWN" : "UP")}");
    }

    public void ParkingBrake(bool set)
    {
        // PARKING_BRAKE_SET is a set event; data 1 = set, 0 = release.
        _sc.FireEvent("PARKING_BRAKE_SET", set ? (uint)1 : (uint)0);
        Log($"PARKING_BRAKE_SET={(set ? 1u : 0u)}");
    }

    // Last brake values sent — brake axes persist in the sim, so repeat
    // identical commands are pure SimConnect spam (taxi calls these every
    // tick). -1 sentinel guarantees the first command always goes out.
    private double _lastBrakeLeft = -1, _lastBrakeRight = -1;

    public void SetBrakes(double left, double right)
    {
        left = Math.Clamp(left, 0, 1);
        right = Math.Clamp(right, 0, 1);
        if (left == _lastBrakeLeft && right == _lastBrakeRight) return;
        _lastBrakeLeft = left; _lastBrakeRight = right;
        var l = (int)Math.Round(-16383 + left * 32766.0);
        var r = (int)Math.Round(-16383 + right * 32766.0);
        _sc.FireEvent("AXIS_LEFT_BRAKE_SET", unchecked((uint)l));
        _sc.FireEvent("AXIS_RIGHT_BRAKE_SET", unchecked((uint)r));
    }

    public void ReleaseBrakes() => SetBrakes(0, 0);

    public void SetThrottleFraction(double fraction)
        => _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(fraction * 16384), 0, 16383));

    // ---------------- Direct surface control ----------------

    /// <summary>
    /// Runs one control frame. In native mode it programs the autopilot targets;
    /// in direct mode it computes axis/throttle outputs from the PID loops.
    /// </summary>
    public void Update(AircraftState s, double dt)
    {
        var t = _targets;

        // Flaps/gear are mode-independent: precision and ground phases run in
        // direct mode and must still be able to select them.
        if (t.FlapIndex is { } fl && fl != _lastFlap) { SelectFlaps(fl); _lastFlap = fl; }
        if (t.GearDown is { } g && g != _lastGear) { Gear(g); _lastGear = g; }

        if (!_directMode)
        {
            // Native autopilot: push whatever targets the phase wants. The
            // *_VAR_SET events are plain value sets and safe to repeat, but
            // the AP_PANEL_*_HOLD events TOGGLE modes — engaging them must be
            // edge-triggered or a drifting heading would flicker the AP.
            if (t.TargetHeadingDeg is { } hdg)
            {
                if (!Near(_lastHdg, hdg))
                {
                    _sc.FireEvent("HEADING_BUG_SET", (uint)Math.Round(Geo.GeoMath.NormalizeDeg(hdg)));
                    _lastHdg = hdg;
                }
                if (!_hdgHoldEngaged) { _sc.FireEvent("AP_PANEL_HEADING_HOLD"); _hdgHoldEngaged = true; }
            }
            else { _hdgHoldEngaged = false; _lastHdg = null; }

            // VS hold and altitude hold are mutually exclusive modes: while a
            // VS target is commanded and we're more than ~30 ft from the
            // altitude floor, fly the rate; close in and capture the altitude.
            var wantVs = t.TargetVsFpm is { } vsN &&
                (t.TargetAltitudeFt is not { } altFloor || Math.Abs(altFloor - s.Altitude) > AltCaptureFt);
            if (wantVs && t.TargetVsFpm is { } vsActive)
            {
                if (!Near(_lastVs, vsActive)) { VerticalSpeedHold(vsActive); _lastVs = vsActive; }
                _lastAlt = null; // force re-engage of ALT hold on capture
            }
            else if (t.TargetAltitudeFt is { } alt && !Near(_lastAlt, alt))
            {
                AltitudeHold(alt); _lastAlt = alt; _lastVs = null;
            }
            if (t.TargetAirspeedKt is { } ias && !Near(_lastIas, ias)) { AirspeedHold(ias); _lastIas = ias; }
            if (t.TrackLocalizer && !_lastApr) ApproachHold();
            if (t.TrackNav && !_lastNav) NavHold();
            _lastApr = t.TrackLocalizer;
            _lastNav = t.TrackNav;
            if (t.ThrottleFraction is { } thr && !Near(_lastThr, thr)) { SetThrottleFraction(thr); _lastThr = thr; }
            return;
        }

        // Direct mode: PID on surfaces.
        double pitchCmd = 0, bankCmd = 0, rudderCmd = 0;
        // With no explicit throttle the speed loop trims around the last
        // commanded value; starting from 0 would collapse the throttle to idle.
        double throttle = t.ThrottleFraction
            ?? (_lastSentThrottle is { } prev && !double.IsNaN(prev) ? prev : 0.45);

        // Pitch: hold altitude or vertical speed. Same VS/altitude precedence
        // as native mode — a commanded sink/climb rate wins until within
        // AltCaptureFt of the altitude target.
        var wantDirectVs = t.TargetVsFpm is { } vsT &&
            (t.TargetAltitudeFt is not { } altT2 || Math.Abs(altT2 - s.Altitude) > AltCaptureFt);
        if (t.TargetAltitudeFt is { } altT && !wantDirectVs)
        {
            var altErr = altT - s.Altitude;
            var vsCmd = Math.Clamp(altErr * 8.0, -FlightEnvelopeGuardMaxVs(s), FlightEnvelopeGuardMaxVs(s));
            pitchCmd = _pitch.Update(vsCmd - s.VerticalSpeed, dt) * t.ControlScale;
        }
        else if (t.TargetVsFpm is { } vsT2)
        {
            pitchCmd = _pitch.Update(vsT2 - s.VerticalSpeed, dt) * 0.02 * t.ControlScale;
        }

        // Bank: steer toward target heading.
        if (t.TargetHeadingDeg is { } hdgT)
        {
            var hdgErr = Geo.GeoMath.AngleDelta(hdgT, s.TrueHeading);
            var maxBank = Math.Min(t.MaxBankDeg, _profile.MaxBankDeg * t.ControlScale * _profile.ControlAuthority + 5);
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
        // Runs every control tick — rate-limit so the log file is not flooded.
        if ((DateTime.UtcNow - _lastDirectLog).TotalSeconds >= 2.0)
        {
            _lastDirectLog = DateTime.UtcNow;
            Log($"DIRECT pitch={pitchCmd:F3} bank={bankCmd:F3} rudder={rudderCmd:F3} throttle={throttle:F3} IAS={s.IndicatedAirspeed:F1}kt alt={s.Altitude:F0}ft hdg={s.TrueHeading:F0}°");
        }
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
        _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(throttle * 16384), 0, 16383));
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
        // Brake axes persist independently of the flight controls — release
        // them even when the surfaces are already neutral (dedup makes this
        // a no-op unless brakes are actually applied).
        ReleaseBrakes();
        // Already-neutral surfaces need no events — callers such as the service
        // wait invoke this every tick and would otherwise spam 60 events/s.
        if (_axesNeutralised) return;
        var neutral = (uint)Math.Round((0.0 + 1.0) * 16384.0 / 2.0);
        _sc.FireEvent("AXIS_ELEVATOR_SET", neutral);
        _sc.FireEvent("AXIS_AILERONS_SET", neutral);
        _sc.FireEvent("AXIS_RUDDER_SET", neutral);
        _axesNeutralised = true;
        Log("Neutralize");
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
        // Wheel brakes persist across phases — never carry them into a mode
        // switch (e.g. taxi hold-short braking must not bleed into takeoff).
        ReleaseBrakes();
        if (direct) { ApMaster(false); Log("Direct mode ON"); }
        else
        {
            Neutralize();
            // Mode latches must be re-armed for the next leg: NAV/APR hold are
            // one-shot events, and without this a second flight never re-engages
            // approach or GPS navigation tracking.
            // AP master was off in direct mode, which drops all holds —
            // re-arm every mode latch so they engage again.
            _lastNav = _lastApr = false;
            _hdgHoldEngaged = false;
            _lastHdg = _lastAlt = _lastVs = _lastIas = null;
            Log("Direct mode OFF");
        }
    }
}
