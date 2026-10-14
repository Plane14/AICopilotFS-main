using System;
using System.Collections.Generic;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The phase executors drive the sequence with <see cref="FlightPhaseManager.ForcePhase"/>,
/// while the control loop also runs the goal-based evaluation every frame. These tests
/// pin the interaction down: a forced phase has to hold, or the two fight and the
/// phase flips back and forth at frame rate (the reported Preflight/Startup loop).
/// </summary>
public class FlightPhaseManagerTests
{
    private static AircraftState ParkedOnGround() => new()
    {
        OnGround = true,
        IndicatedAirspeed = 0,
        AltitudeAGL = 0,
        Altitude = 1000,
        GroundSpeed = 0,
    };

    private static AircraftState Airborne() => new()
    {
        OnGround = false,
        IndicatedAirspeed = 180,
        AltitudeAGL = 3000,
        Altitude = 4000,
        VerticalSpeed = 1200,
    };

    private static PhaseContext AtGate() => new()
    {
        EnginesRunning = false,
        AtDepartureGateOrRamp = true,
    };

    [Fact]
    public void ForcedPhaseSurvivesGoalBasedReevaluation()
    {
        var manager = new FlightPhaseManager();
        var changed = new List<(FlightPhase From, FlightPhase To)>();
        manager.OnPhaseChanged += (from, to) => changed.Add((from, to));

        // The gate checklist has not started the engines yet, so the goal-based
        // result is Preflight while the executor has forced Startup.
        var state = ParkedOnGround();
        var context = AtGate();

        var phase = manager.EvaluatePhase(state, context);
        Assert.Equal(FlightPhase.Preflight, phase);
        Assert.False(manager.IsForced);

        manager.ForcePhase(FlightPhase.Startup);

        for (var frame = 0; frame < 200; frame++)
        {
            phase = manager.EvaluatePhase(state, context);
            Assert.Equal(FlightPhase.Startup, phase);
        }

        Assert.Equal(new[] { (FlightPhase.Unknown, FlightPhase.Preflight), (FlightPhase.Preflight, FlightPhase.Startup) }, changed);
        Assert.True(manager.IsForced);
    }

    [Fact]
    public void ForcedPhaseIsReplacedByTheNextForcedPhase()
    {
        var manager = new FlightPhaseManager();
        manager.EvaluatePhase(ParkedOnGround(), AtGate());

        manager.ForcePhase(FlightPhase.Startup);
        Assert.Equal(FlightPhase.Startup, manager.EvaluatePhase(ParkedOnGround(), AtGate()));

        manager.ForcePhase(FlightPhase.TaxiOut);
        Assert.Equal(FlightPhase.TaxiOut, manager.EvaluatePhase(ParkedOnGround(), AtGate()));
        Assert.Equal(FlightPhase.Startup, manager.PreviousPhase);
    }

    [Fact]
    public void ForcingTheSamePhaseTwiceDoesNotRaiseASecondChange()
    {
        var manager = new FlightPhaseManager();
        var changes = 0;
        manager.OnPhaseChanged += (_, _) => changes++;

        manager.ForcePhase(FlightPhase.Startup);
        manager.ForcePhase(FlightPhase.Startup);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void EmergencyOverridesAForcedGroundPhase()
    {
        var manager = new FlightPhaseManager();
        manager.ForcePhase(FlightPhase.Startup);

        var context = AtGate();
        context.Emergency = true;

        Assert.Equal(FlightPhase.Emergency, manager.EvaluatePhase(ParkedOnGround(), context));
        Assert.False(manager.IsForced);
    }

    [Fact]
    public void GoAroundOverridesAForcedGroundPhase()
    {
        var manager = new FlightPhaseManager();
        manager.ForcePhase(FlightPhase.Rollout);

        var context = AtGate();
        context.GoAroundRequested = true;

        Assert.Equal(FlightPhase.GoAround, manager.EvaluatePhase(ParkedOnGround(), context));
        Assert.False(manager.IsForced);
    }

    [Fact]
    public void ReleasedPhaseFallsBackToGoalBasedEvaluation()
    {
        var manager = new FlightPhaseManager();
        manager.EvaluatePhase(ParkedOnGround(), AtGate());
        manager.ForcePhase(FlightPhase.TaxiOut);

        manager.ReleasePhase();

        Assert.Equal(FlightPhase.Preflight, manager.EvaluatePhase(ParkedOnGround(), AtGate()));
        Assert.False(manager.IsForced);
    }

    [Fact]
    public void ForcedGroundPhaseIsHeldThroughALateRotation()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var manager = new FlightPhaseManager(() => now, TimeSpan.FromSeconds(5));
        manager.ForcePhase(FlightPhase.Takeoff);

        // Just after liftoff the aircraft is airborne while the takeoff roll owns
        // the phase; the hold has to survive that, not flip to InitialClimb.
        for (var i = 0; i < 4; i++)
        {
            now += TimeSpan.FromSeconds(1);
            Assert.Equal(FlightPhase.Takeoff, manager.EvaluatePhase(Airborne(), AtGate()));
        }
    }

    [Fact]
    public void ForcedPhaseIsDroppedOnceTheAircraftHasLeftItBehind()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var grace = TimeSpan.FromSeconds(5);
        var manager = new FlightPhaseManager(() => now, grace);
        manager.ForcePhase(FlightPhase.TaxiOut);

        // The mismatch is seen here, so the hold survives for the grace window.
        now += TimeSpan.FromSeconds(4);
        Assert.Equal(FlightPhase.TaxiOut, manager.EvaluatePhase(Airborne(), AtGate()));

        // Past the window, goal-based evaluation takes over again.
        now += grace + TimeSpan.FromSeconds(1);
        var phase = manager.EvaluatePhase(Airborne(), AtGate());
        Assert.Equal(FlightPhase.InitialClimb, phase);
        Assert.False(manager.IsForced);
    }

    [Fact]
    public void ResetClearsTheHoldAndThePhase()
    {
        var manager = new FlightPhaseManager();
        manager.EvaluatePhase(ParkedOnGround(), AtGate());
        manager.ForcePhase(FlightPhase.Startup);

        manager.Reset();

        Assert.Equal(FlightPhase.Unknown, manager.CurrentPhase);
        Assert.False(manager.IsForced);
        Assert.Equal(FlightPhase.Preflight, manager.EvaluatePhase(ParkedOnGround(), AtGate()));
    }

    [Fact]
    public void GoalBasedEvaluationWithoutAForceIsStable()
    {
        var manager = new FlightPhaseManager();
        var changed = new List<(FlightPhase From, FlightPhase To)>();
        manager.OnPhaseChanged += (from, to) => changed.Add((from, to));

        var state = ParkedOnGround();
        var context = AtGate();
        for (var frame = 0; frame < 50; frame++)
        {
            Assert.Equal(FlightPhase.Preflight, manager.EvaluatePhase(state, context));
        }

        Assert.Single(changed);
    }

    [Theory]
    [InlineData(FlightPhase.Preflight, true)]
    [InlineData(FlightPhase.Startup, true)]
    [InlineData(FlightPhase.TaxiOut, true)]
    [InlineData(FlightPhase.Takeoff, true)]
    [InlineData(FlightPhase.Rollout, true)]
    [InlineData(FlightPhase.TaxiIn, true)]
    [InlineData(FlightPhase.Shutdown, true)]
    [InlineData(FlightPhase.InitialClimb, false)]
    [InlineData(FlightPhase.Climb, false)]
    [InlineData(FlightPhase.Cruise, false)]
    [InlineData(FlightPhase.Descent, false)]
    [InlineData(FlightPhase.Approach, false)]
    [InlineData(FlightPhase.FinalApproach, false)]
    [InlineData(FlightPhase.GoAround, false)]
    public void PhaseFitsStateSeparatesGroundFromAirborne(FlightPhase phase, bool expectedGround)
    {
        var ground = ParkedOnGround();
        var air = Airborne();

        Assert.Equal(expectedGround, FlightPhaseManager.PhaseFitsState(phase, ground));
        Assert.Equal(!expectedGround, FlightPhaseManager.PhaseFitsState(phase, air));
    }

    [Fact]
    public void EveryRegisteredPhaseDeclaresWhatItNeeds()
    {
        // The definitions were registered and then never read, so a phase's boundary was
        // declared in one place and restated in a switch. Every phase has to be described by
        // the same table that decides it, or the data drifts away from the behaviour.
        var phases = new[]
        {
            FlightPhase.Preflight, FlightPhase.Startup, FlightPhase.TaxiOut, FlightPhase.Takeoff,
            FlightPhase.InitialClimb, FlightPhase.Climb, FlightPhase.Cruise, FlightPhase.Descent,
            FlightPhase.Approach, FlightPhase.FinalApproach, FlightPhase.Landing,
            FlightPhase.Rollout, FlightPhase.TaxiIn, FlightPhase.Shutdown,
            FlightPhase.Holding, FlightPhase.Divert, FlightPhase.Emergency, FlightPhase.GoAround
        };

        foreach (var phase in phases)
        {
            var definition = FlightPhaseManager.DefinitionFor(phase);

            // A ground phase is either on the ground or has a ceiling it fits under; an
            // airborne one is off the ground. Both are declared, never implied.
            if (definition.IsGroundPhase)
            {
                Assert.True(definition.MaxAltitudeAGL > 0, $"{phase} is a ground phase with no ceiling");
            }
            else
            {
                Assert.Equal(0, definition.MaxAltitudeAGL);
            }
        }
    }

    [Fact]
    public void TheTakeoffRollStopsFittingOnceAirborne()
    {
        // The registered ceiling is what releases a forced takeoff roll, so a slow climbing
        // aircraft cannot pin the phase while it is already hundreds of feet up.
        var rolling = ParkedOnGround();
        rolling.GroundSpeed = 80;

        var defineTakeoff = FlightPhaseManager.DefinitionFor(FlightPhase.Takeoff);
        Assert.True(defineTakeoff.IsGroundPhase);
        Assert.Equal(400, defineTakeoff.MaxAltitudeAGL);

        var airborneButLow = Airborne();
        airborneButLow.AltitudeAGL = 300;

        // 300 ft AGL is still the takeoff roll.
        Assert.True(FlightPhaseManager.PhaseFitsState(FlightPhase.Takeoff, airborneButLow));
    }

    [Fact]
    public void TheInitialClimbBeginsWhereTheTakeoffRollEnds()
    {
        var manager = new FlightPhaseManager();
        var defineInitialClimb = FlightPhaseManager.DefinitionFor(FlightPhase.InitialClimb);
        var defineTakeoff = FlightPhaseManager.DefinitionFor(FlightPhase.Takeoff);

        // The two phases meet at 400 ft AGL, so one of them always fits.
        Assert.Equal(defineInitialClimb.MinAltitudeAGL, defineTakeoff.MaxAltitudeAGL);
    }

    [Fact]
    public void AGoAroundRequestWinsOverAForcedApproach()
    {
        // The go-around is what the controller says and the phase it drives, so it has to
        // outrank the phase the executor is holding.
        var manager = new FlightPhaseManager();
        manager.ForcePhase(FlightPhase.Approach);

        var airborne = Airborne();
        airborne.AltitudeAGL = 400;
        airborne.VerticalSpeed = 700;
        var context = new PhaseContext { GoAroundRequested = true };

        var phase = manager.EvaluatePhase(airborne, context);

        Assert.Equal(FlightPhase.GoAround, phase);
    }
}
