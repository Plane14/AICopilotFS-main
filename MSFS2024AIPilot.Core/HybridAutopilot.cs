using System;

namespace MSFS2024AIPilot.Core;

public sealed class HybridAutopilot
{
    private readonly SimConnectManager _sc;
    private readonly AircraftPerformance _perf;
    private readonly EnvelopeGuard _envelope;
    private AutopilotMode _mode = AutopilotMode.Off;

    // PID state for direct control. The derivative term of each loop needs the previous
    // error, which is why the state lives on the class: a local re-zeroed on every call
    // turns the derivative into a proportional term on the raw error.
    private readonly PidLoop _pitchPid = new(0.06, 0.002, 0.02);
    private readonly PidLoop _bankPid = new(0.05, 0.002, 0.03);
    private readonly PidLoop _speedPid = new(0.02, 0.001, 0.01);
    private readonly PidLoop _altitudePid = new(0.0008, 0.00002, 0.0006);

    // Direct control outputs
    public double PitchCommand { get; set; }
    public double BankCommand { get; set; }
    public double RudderCommand { get; set; }
    public double ThrottleCommand { get; set; }

    public event Action<string>? OnLog;

    public AutopilotMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                ResetPids();
                Log($"Autopilot mode: {value}");
            }
        }
    }

    /// <summary>
    /// Direct control that reads its envelope from the guard the phase loop already advances,
    /// so the limits the executors apply and the ones applied here come from one place
    /// rather than two separately-resolved copies.
    /// </summary>
    public HybridAutopilot(SimConnectManager sc, AircraftPerformance perf, EnvelopeGuard envelope)
    {
        _sc = sc;
        _perf = perf;
        // Never null: without a guard the direct-control path would apply no envelope at
        // all, and a PID pitch loop driven straight onto the axis is exactly where the
        // airframe gets no protection. A default guard carries the generic limits every
        // aircraft has - a load factor above all - until the caller supplies its own.
        _envelope = envelope ?? new EnvelopeGuard();
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void ResetPids()
    {
        _pitchPid.Reset();
        _bankPid.Reset();
        _speedPid.Reset();
        _altitudePid.Reset();
    }

    #region Native Autopilot Commands

    public void EngageMaster()
    {
        _sc.FireEvent("AP_MASTER");
        Log("AP_MASTER on");
    }

    public void DisengageMaster()
    {
        _sc.FireEvent("AP_MASTER", 0);
        Log("AP_MASTER off");
    }

    public void SetHeading(double headingDeg)
    {
        _sc.FireEvent("HEADING_BUG_SET", (uint)Math.Round(headingDeg));
        _sc.FireEvent("AP_PANEL_HEADING_HOLD");
    }

    public void SetAltitude(double altitudeFt)
    {
        _sc.FireEvent("AP_ALT_VAR_SET_ENGLISH", (uint)Math.Round(altitudeFt));
        _sc.FireEvent("AP_PANEL_ALTITUDE_HOLD");
    }

    public void SetVerticalSpeed(double vsFpm)
    {
        _sc.FireEvent("AP_VS_VAR_SET_ENGLISH", unchecked((uint)Math.Round(vsFpm * 65536.0 / 1000.0)));
        _sc.FireEvent("AP_PANEL_VS_HOLD");
    }

    public void SetAirspeed(double iasKnots)
    {
        _sc.FireEvent("AP_SPD_VAR_SET", (uint)Math.Round(iasKnots));
        _sc.FireEvent("AP_AIRSPEED_HOLD");
    }

    public void SetMach(double mach)
    {
        _sc.FireEvent("AP_MACH_VAR_SET", (uint)Math.Round(mach * 100));
        _sc.FireEvent("AP_MACH_HOLD");
    }

    public void EngageApproachMode()
    {
        _sc.FireEvent("AP_APR_HOLD");
    }

    public void EngageNavLocks()
    {
        _sc.FireEvent("AP_NAV1_HOLD");
    }

    public void EngageAutoThrottle()
    {
        _sc.FireEvent("AUTO_THROTTLE_ARM");
    }

    public void SetThrottle(double fraction)
    {
        // Use throttle set event (0..16384)
        _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(fraction * 16384), 0, 16384));
    }

    #endregion

    #region Direct Surface Control (PID)

    public void UpdateDirectControl(AircraftState state, ControlTargets targets, double dt)
    {
        if (dt <= 0 || double.IsNaN(dt)) dt = 0.016;

        // Pitch control (target vertical speed or target pitch)
        double pitchError;
        if (targets.TargetVerticalSpeed.HasValue)
        {
            var desiredPitch = PitchForVerticalSpeed(targets.TargetVerticalSpeed.Value, state.GroundSpeed);
            pitchError = desiredPitch - state.Pitch;
        }
        else if (targets.TargetPitch.HasValue)
        {
            pitchError = targets.TargetPitch.Value - state.Pitch;
        }
        else
        {
            pitchError = 0;
        }

        var pitchOutput = _pitchPid.Step(pitchError, dt);
        // The elevator is bounded by the load factor the airframe can carry, which is the
        // protection that matters most on this path: the native autopilot will not fly
        // outside its own envelope, but a PID pitch loop driven straight onto the axis will
        // follow a commanded 15-degree attitude into the ground at whatever rate is needed.
        PitchCommand = Math.Clamp(_envelope.CappedElevator(pitchOutput), -1.0, 1.0);

        // Bank control (target heading)
        if (targets.TargetHeading.HasValue)
        {
            var headingError = NormalizeHeading(targets.TargetHeading.Value - state.Heading);
            // A lower bank ceiling in turbulence keeps the aircraft from working the
            // ailerons against every gust; the scaling softens the response further.
            var maxBank = targets.MaxBankDeg ?? 25;
            var desiredBank = Math.Clamp(headingError * 2.0, -maxBank, maxBank);
            var bankError = desiredBank - state.Bank;
            var bankOutput = _bankPid.Step(bankError, dt);
            var scale = targets.ControlScale ?? 1.0;
            BankCommand = Math.Clamp(bankOutput * scale, -1.0, 1.0);
        }
        else
        {
            BankCommand = 0;
        }

        // Rudder for coordination
        RudderCommand = 0; // simplified

        // Throttle control (target airspeed)
        if (targets.TargetAirspeed.HasValue)
        {
            // The commanded speed is capped into the envelope for the configuration actually
            // flown, so a PID chasing an impossible target saturates the throttle rather than
            // the aircraft being flown at one.
            var targetAirspeed = _envelope.CappedAirspeedKt(
                targets.TargetAirspeed.Value, FlapIndex(state), state.GearDown);
            var speedError = targetAirspeed - state.IndicatedAirspeed;
            // The speed error is carried across calls by the PID state, so the derivative
            // term sees the change since the previous frame rather than the whole error:
            // seeded from zero every call it was kd * error / dt regardless of history,
            // which at the 20 Hz loop rate saturated the throttle between idle and full on
            // any error of a few knots.
            var throttleOutput = _speedPid.Step(speedError, dt);
            // Full power is bounded by the engine limit as well, so an aircraft at its EGT
            // redline is not held there by the speed loop.
            var throttleCeiling = Math.Min(1.0, _envelope.MaxThrottle);
            ThrottleCommand = Math.Clamp(throttleOutput, 0.0, throttleCeiling);
        }

        // Altitude hold: gently bias pitch when the aircraft is off an assigned altitude
        if (targets.TargetAltitude.HasValue && state.VerticalSpeed >= 0)
        {
            var altError = targets.TargetAltitude.Value - state.Altitude;
            // The altitude bias runs on the wall-clock spacing between updates. Reading a
            // timestamp assigned at the top of this same call would return a dt of
            // microseconds, which collapses the integral and makes the derivative a single
            // clamped spike instead of a slope.
            var bias = _altitudePid.Step(altError);
            // The load-factor limiter is applied to the combined command, not to the pitch
            // the loop chose before the bias was added: at 2.6 g the limiter returned -0.30
            // and a bias of +1.0 then took the sum to +0.70 - a pull past the limit the
            // limiter had just been asked to prevent.
            PitchCommand = Math.Clamp(_envelope.CappedElevator(PitchCommand + bias), -1.0, 1.0);
        }

        // Send controls to sim (axis events)
        _sc.FireEvent("AXIS_ELEVATOR_SET", (uint)Math.Clamp(Math.Round((PitchCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("AXIS_AILERONS_SET", (uint)Math.Clamp(Math.Round((BankCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("AXIS_RUDDER_SET", (uint)Math.Clamp(Math.Round((RudderCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(ThrottleCommand * 16384), 0, 16384));
    }

    public void SendDirectControls()
    {
        _sc.FireEvent("AXIS_ELEVATOR_SET", (uint)Math.Clamp(Math.Round((PitchCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("AXIS_AILERONS_SET", (uint)Math.Clamp(Math.Round((BankCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("AXIS_RUDDER_SET", (uint)Math.Clamp(Math.Round((RudderCommand + 1.0) * 16384 / 2.0), 0, 16383));
        _sc.FireEvent("THROTTLE_SET", (uint)Math.Clamp(Math.Round(ThrottleCommand * 16384), 0, 16384));
    }

    /// <summary>Flap handle index as a whole number, for a configuration limit lookup.</summary>
    private static int FlapIndex(AircraftState s) => (int)Math.Round(s.FlapsHandleIndex);

    private static double PitchForVerticalSpeed(double targetVs, double groundSpeedKt)
    {
        // Rough approximation: pitch angle (degrees) for a given vertical speed at current ground speed
        if (groundSpeedKt < 30) groundSpeedKt = 30;
        var fpmToFps = targetVs / 60.0;
        var gsFps = groundSpeedKt * 1.68781;
        var climbAngle = Math.Atan2(fpmToFps, gsFps);
        return climbAngle * 180.0 / Math.PI;
    }

    private static double NormalizeHeading(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    #endregion

    /// <summary>
    /// Centres the axes the pilot was driving directly, so disengaging leaves the aircraft
    /// neutrally trimmed rather than holding whatever bank and pitch were last commanded.
    /// <para>
    /// Thrust is deliberately left alone: an autopilot disconnect does not move the
    /// throttles, and forcing the levers to a predicted value could close them on an
    /// aircraft that is airborne on the user's throttle quadrant. The last throttle value
    /// written by <c>THROTTLE_SET</c> stays where it was for exactly that reason.
    /// </para>
    /// </summary>
    public void ReleaseAllControls()
    {
        // Neutral is the midpoint a zero command maps to on the dispatcher below -
        // (0 + 1) * 16384 / 2 = 8192. Sending 16383 wrote one extreme instead of the
        // midpoint, so releasing the axes kicked the surfaces to full deflection right
        // when control was being handed back, rather than leaving them trimmed neutral.
        var neutral = (uint)Math.Round((0.0 + 1.0) * 16384.0 / 2.0);
        _sc.FireEvent("AXIS_ELEVATOR_SET", neutral);
        _sc.FireEvent("AXIS_AILERONS_SET", neutral);
        _sc.FireEvent("AXIS_RUDDER_SET", neutral);
        ResetPids();
    }
}

public struct ControlTargets
{
    public double? TargetHeading { get; set; }
    public double? TargetAltitude { get; set; }
    public double? TargetVerticalSpeed { get; set; }
    public double? TargetAirspeed { get; set; }
    public double? TargetMach { get; set; }
    public double? TargetPitch { get; set; }
    public bool UseDirectControl { get; set; }

    /// <summary>
    /// Maximum bank angle, in degrees, to command when tracking <see cref="TargetHeading"/>.
    /// A null value uses the default limit. Lowered in turbulence so gusts are not chased.
    /// </summary>
    public double? MaxBankDeg { get; set; }

    /// <summary>
    /// Scales the direct-control outputs (1.0 = full, null = full). Lowered to soften the
    /// banking response in turbulence so the surface is not worked on every gust.
    /// </summary>
    public double? ControlScale { get; set; }
}

/// <summary>
/// One PID loop, keeping its own integral and previous error across calls. The state is
/// what makes the derivative term a slope rather than a scale factor: re-seeding the
/// previous error to zero on every call turns kd into a multiplier on the raw error,
/// which at the 20 Hz control rate saturates the output on any small offset.
/// </summary>
public sealed class PidLoop
{
    private readonly double _kp;
    private readonly double _ki;
    private readonly double _kd;
    private double _integral;
    private double _lastError;
    private DateTime _lastUpdateUtc = DateTime.UtcNow;
    /// <summary>
    /// False until the loop has seen one error, so the derivative term can be primed from
    /// it. Seeding the previous error at 0 instead made the first step after a reset
    /// kd * error / dt: a 15 degree pitch error then returned 6.0 and clamped to 1.0, i.e.
    /// a full elevator deflection on one frame - and Reset() ran on every mode change, so
    /// every engage, taxi-to-takeoff, final approach and go-around started that way.
    /// </summary>
    private bool _primed;

    public PidLoop(double kp, double ki, double kd)
    {
        _kp = kp;
        _ki = ki;
        _kd = kd;
    }

    public double Integral => _integral;

    /// <summary>
    /// Advances the loop by <paramref name="dt"/> seconds for <paramref name="error"/>,
    /// returning the clamped output. A non-positive, non-finite or implausibly large
    /// interval is replaced with one frame at the 20 Hz loop rate, so a stalled control
    /// loop cannot divide by zero, freeze a stale derivative, or integrate a multi-minute
    /// gap into a single step.
    /// </summary>
    public double Step(double error, double dt)
    {
        if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0) dt = 0.05;
        // The bias loops run on the wall clock, so after a long gap (a descent, a stall)
        // the first frame would integrate a huge dt at once.
        dt = Math.Clamp(dt, 0.001, 0.25);

        var p = _kp * error;
        var d = _primed ? _kd * (error - _lastError) / dt : 0.0;
        _lastError = error;
        _primed = true;

        // Anti-windup: the integral only grows while it still has something to say - once
        // the combined command has saturated, integrating further just means the output
        // stays pinned at the limit long after the error has gone. Back-calculation from
        // the difference between the clamped and unclamped command restores it at the same
        // rate, and the decay term bleeds a stale integral away when the error is small
        // rather than freezing it where the saturation left it.
        var unclamped = p + _integral + d;
        var clamped = Math.Clamp(unclamped, -1.0, 1.0);

        _integral = Math.Clamp(
            _integral + _ki * error * dt + Ka * (clamped - unclamped),
            -1.0 - p,
            1.0 - p);

        // A small error must not leave a saturated integral holding the command: without
        // this the loop kept a 60 % throttle for tens of seconds after the aircraft had
        // already reached the commanded speed.
        if (Math.Abs(error) < 0.05) _integral *= 0.5;

        return Math.Clamp(p + _integral + d, -1.0, 1.0);
    }

    /// <summary>Back-calculation gain: how fast the integral unwinds when the output saturates.</summary>
    private const double Ka = 1.0;

    /// <summary>
    /// Advances the loop using the wall-clock gap since the previous call, used by the
    /// altitude bias, which is not driven by the control loop's own dt.
    /// </summary>
    public double Step(double error)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastUpdateUtc).TotalSeconds;
        _lastUpdateUtc = now;
        return Step(error, dt);
    }

    public void Reset()
    {
        _integral = 0;
        _lastError = 0;
        _primed = false;
        _lastUpdateUtc = DateTime.UtcNow;
    }
}
