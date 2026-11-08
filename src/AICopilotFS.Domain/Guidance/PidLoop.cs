namespace AICopilotFS.Domain.Guidance;

/// <summary>A simple PID controller with deadbanding and anti-windup.</summary>
public sealed class PidLoop
{
    private readonly Domain.State.PidGains _gains;
    private double _integral;
    private double _previousError;
    private bool _initialized;

    public PidLoop(Domain.State.PidGains gains)
    {
        _gains = gains;
    }

    public double Update(double error, double dt)
    {
        if (dt <= 0) return 0;

        // Deadband
        if (Math.Abs(error) < _gains.Deadband)
        {
            _previousError = error;
            return _initialized ? _integral : 0;
        }

        // Proportional
        var p = _gains.Kp * error;

        // Integral with anti-windup (clamp)
        _integral += error * dt;
        _integral = Math.Clamp(_integral, _gains.MinOutput, _gains.MaxOutput);
        var i = _gains.Ki * _integral;

        // Derivative
        var d = _gains.Kd * (error - _previousError) / dt;
        _previousError = error;
        _initialized = true;

        var output = p + i + d;
        return Math.Clamp(output, _gains.MinOutput, _gains.MaxOutput);
    }

    public void Reset()
    {
        _integral = 0;
        _previousError = 0;
        _initialized = false;
    }
}
