using MSFS2024AIPilot.Core.Guidance;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Tests;

public class PushbackPlannerTests
{
    private static AircraftState State(bool attached = false, bool available = true, double gs = 0)
        => new()
        {
            Pushback = new PushbackStatus { Attached = attached, Available = available },
            GroundSpeed = gs,
            Latitude = 47.0,
            Longitude = 8.0,
        };

    [Fact]
    public void No_tug_offered_gives_up_immediately()
    {
        var s = State(attached: false, available: false);
        var d = PushbackPlanner.Decide(s, new PushbackProgress { StartedUtc = DateTime.UtcNow });
        Assert.Equal(PushbackDecision.GiveUp, d);
    }

    [Fact]
    public void Attached_tug_tows()
    {
        var s = State(attached: true, gs: 2);
        var d = PushbackPlanner.Decide(s, new PushbackProgress
        {
            SawTug = true,
            AttachedUtc = DateTime.UtcNow.AddSeconds(-10),
            LastMovedUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow.AddSeconds(-10),
            AttachPoint = (47.0, 8.0),
        });
        Assert.Equal(PushbackDecision.Towing, d);
    }

    [Fact]
    public void Attached_but_not_moving_is_a_stall()
    {
        var now = DateTime.UtcNow;
        var s = State(attached: true, gs: 0);
        var d = PushbackPlanner.Decide(s, new PushbackProgress
        {
            SawTug = true,
            AttachedUtc = now.AddSeconds(-60),
            LastMovedUtc = now.AddSeconds(-60),
            StartedUtc = now.AddSeconds(-60),
            AttachPoint = (47.0, 8.0),
        }, utcNow: now);
        Assert.Equal(PushbackDecision.GiveUp, d);
    }

    [Fact]
    public void Pushed_far_enough_dismisses()
    {
        var now = DateTime.UtcNow;
        var s = State(attached: true, gs: 1);
        s.Latitude = 47.001; // ~111 m north of attach point
        var d = PushbackPlanner.Decide(s, new PushbackProgress
        {
            SawTug = true,
            AttachedUtc = now.AddSeconds(-30),
            LastMovedUtc = now,
            StartedUtc = now.AddSeconds(-30),
            AttachPoint = (47.0, 8.0),
        }, utcNow: now);
        Assert.Equal(PushbackDecision.Dismiss, d);
    }
}
