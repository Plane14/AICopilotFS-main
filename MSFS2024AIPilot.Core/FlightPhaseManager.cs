using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core;

public sealed class FlightPhaseManager
{
    /// <summary>
    /// How long a forced phase survives an aircraft state it does not fit. The
    /// executors drive the sequence with <see cref="ForcePhase"/>, but a phase may
    /// never pin the aircraft forever: once the state disagrees for this long the
    /// hold is dropped and goal-based evaluation takes over again.
    /// </summary>
    internal static readonly TimeSpan DefaultIncompatibilityGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What each phase needs of the aircraft's state. Declared once, and read by
    /// <see cref="PhaseFitsState"/> as well as by the registration below, so a phase's
    /// boundary is a piece of data rather than something a switch has to be kept in sync
    /// with.
    /// </summary>
    private static readonly IReadOnlyDictionary<FlightPhase, FlightPhaseDefinition> Definitions =
        new Dictionary<FlightPhase, FlightPhaseDefinition>
        {
            [FlightPhase.Preflight] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },
            [FlightPhase.Startup] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },
            [FlightPhase.TaxiOut] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },
            [FlightPhase.TaxiIn] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },
            [FlightPhase.Shutdown] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },

            // The takeoff roll runs until the aircraft is properly airborne: 400 ft AGL is
            // where the initial climb takes over, so that is where the roll stops fitting.
            [FlightPhase.Takeoff] = new() { IsGroundPhase = true, MaxAltitudeAGL = 400 },
            [FlightPhase.InitialClimb] = new() { MinAltitudeAGL = 400 },
            [FlightPhase.Climb] = new(),
            [FlightPhase.Cruise] = new(),
            [FlightPhase.Descent] = new(),

            // The approach starts at 200 ft AGL; the final approach is what is below it.
            [FlightPhase.Approach] = new() { MinAltitudeAGL = 200 },
            [FlightPhase.FinalApproach] = new(),
            [FlightPhase.Landing] = new() { IsGroundPhase = true, MaxAltitudeAGL = 400 },
            [FlightPhase.Rollout] = new() { IsGroundPhase = true, MaxAltitudeAGL = 200 },
            [FlightPhase.Holding] = new(),
            [FlightPhase.Divert] = new(),
            [FlightPhase.Emergency] = new(),
            [FlightPhase.GoAround] = new()
        };

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _incompatibilityGrace;

    private FlightPhase _currentPhase = FlightPhase.Unknown;
    private DateTime _phaseEnteredAt;
    private FlightPhase _previousPhase = FlightPhase.Unknown;

    // Set by ForcePhase: the executor owns the sequence until it forces the next
    // phase, releases the hold, or the aircraft state stops fitting the phase.
    private bool _forced;
    private DateTime _incompatibleSince;

    public FlightPhaseManager() : this(() => DateTime.UtcNow, DefaultIncompatibilityGrace) { }

    internal FlightPhaseManager(Func<DateTime> clock, TimeSpan incompatibilityGrace)
    {
        _clock = clock;
        _incompatibilityGrace = incompatibilityGrace;
        _phaseEnteredAt = _clock();
    }

    public FlightPhaseDefinition GetDefinition(FlightPhase phase) =>
        Definitions.TryGetValue(phase, out var def) ? def : new FlightPhaseDefinition();

    internal static FlightPhaseDefinition DefinitionFor(FlightPhase phase) =>
        Definitions.TryGetValue(phase, out var def) ? def : new FlightPhaseDefinition();

    public FlightPhase CurrentPhase => _currentPhase;
    public FlightPhase PreviousPhase => _previousPhase;
    public TimeSpan TimeInPhase => _clock() - _phaseEnteredAt;

    /// <summary>True while a phase forced by an executor is being held.</summary>
    public bool IsForced => _forced;

    public event Action<FlightPhase, FlightPhase>? OnPhaseChanged;

    public FlightPhase EvaluatePhase(AircraftState state, PhaseContext context)
    {
        var desired = EvaluateGoalBased(state, context);

        if (_forced)
        {
            // Emergency and a requested go-around outrank any held phase.
            if (desired == FlightPhase.Emergency ||
                (desired == FlightPhase.GoAround && context.GoAroundRequested))
            {
                ReleasePhase();
            }
            else if (PhaseFitsState(_currentPhase, state))
            {
                _incompatibleSince = default;
                desired = _currentPhase;
            }
            else
            {
                // The aircraft no longer fits the held phase. Keep holding it through
                // the grace window (a bounce after touchdown, a brief negative rate
                // on the climb-out must not flip the phase), then let go so a stalled
                // executor cannot pin a phase the aircraft has already left behind.
                if (_incompatibleSince == default) _incompatibleSince = _clock();
                if (_clock() - _incompatibleSince < _incompatibilityGrace)
                {
                    desired = _currentPhase;
                }
                else
                {
                    ReleasePhase();
                }
            }
        }

        ApplyPhase(desired);
        return desired;
    }

    /// <summary>
    /// Forces a phase and holds it: the goal-based evaluation no longer overrides
    /// it until the next <see cref="ForcePhase"/>, <see cref="ReleasePhase"/> or
    /// <see cref="Reset"/>. Without the hold, an executor that forces the next phase
    /// on one frame is undone by the goal-based result on the next, and the two
    /// flip the phase back and forth (Preflight forcing Startup while the engines
    /// are still off is exactly that loop).
    /// </summary>
    public void ForcePhase(FlightPhase phase)
    {
        _forced = true;
        _incompatibleSince = default;
        ApplyPhase(phase);
    }

    /// <summary>Drops the hold; the next evaluation is purely goal-based.</summary>
    public void ReleasePhase()
    {
        _forced = false;
        _incompatibleSince = default;
    }

    /// <summary>Clears the hold and the current phase, as on a fresh engagement.</summary>
    public void Reset()
    {
        ReleasePhase();
        ApplyPhase(FlightPhase.Unknown);
    }

    private void ApplyPhase(FlightPhase phase)
    {
        if (phase == _currentPhase) return;

        var old = _currentPhase;
        _currentPhase = phase;
        _previousPhase = old;
        _phaseEnteredAt = _clock();
        OnPhaseChanged?.Invoke(old, phase);
    }

    /// <summary>
    /// Whether the aircraft state is broadly consistent with the phase: an airborne
    /// phase needs the aircraft off the ground, a ground phase needs it on it (or
    /// close enough that a bounce or a late rotation still counts).
    /// </summary>
    internal static bool PhaseFitsState(FlightPhase phase, AircraftState state)
    {
        const double groundToleranceFeetAgl = 200;

        var definition = DefinitionFor(phase);

        // A ground phase needs the aircraft on the ground, or low enough over it that a
        // bounce or a late rotation still counts. The ceiling registered for the phase is
        // what that means for this phase in particular.
        if (definition.IsGroundPhase)
        {
            var ceiling = definition.MaxAltitudeAGL > 0 ? definition.MaxAltitudeAGL : groundToleranceFeetAgl;
            return state.OnGround || state.AltitudeAGL <= ceiling;
        }

        // An airborne phase needs the aircraft off the ground, and both boundaries
        // registered for it decide: the floor (where the phase begins, which the initial
        // climb and the approach declare) and the ceiling, which is what the takeoff roll
        // owns to. Without the floor a phase that declares one still "fits" below it, so
        // Takeoff and InitialClimb both fitted between 200 and 400 ft and a released hold
        // in that band could flip between them.
        if (!state.OnGround)
        {
            if (definition.MinAltitudeAGL > 0 && state.AltitudeAGL < definition.MinAltitudeAGL) return false;
            if (definition.MaxAltitudeAGL > 0 && state.AltitudeAGL > definition.MaxAltitudeAGL) return false;
        }

        return state.AltitudeAGL > groundToleranceFeetAgl;
    }

    private FlightPhase EvaluateGoalBased(AircraftState state, PhaseContext context)
    {
        // Emergency takes precedence
        if (context.Emergency) return FlightPhase.Emergency;
        if (context.GoAroundRequested) return FlightPhase.GoAround;

        // On the ground logic
        if (state.OnGround)
        {
            if (state.IndicatedAirspeed < 20)
            {
                // Taxiing or stationary
                if (!context.EnginesRunning)
                    return FlightPhase.Preflight;

                var distToDepartureRunway = context.DistanceToDepartureRunway;
                if (distToDepartureRunway > 0.2 && state.IndicatedAirspeed < 30 && !context.AtDepartureRunway)
                    return FlightPhase.TaxiOut;
                if (!context.DepartureClearanceReceived && context.AtDepartureGateOrRamp)
                    return FlightPhase.Startup;
                if (!context.AtTaxiStartPoint)
                    return FlightPhase.TaxiOut;
                return FlightPhase.Takeoff;
            }
            // Accelerating on runway
            if (state.IndicatedAirspeed >= 20 && context.TakeoffRoll)
                return FlightPhase.Takeoff;
        }

        // Airborne logic
        if (!state.OnGround)
        {
            if (state.AltitudeAGL < 800)
            {
                if (context.LandingRoll)
                    return FlightPhase.Rollout;
                if (state.VerticalSpeed > 300 && context.GoAroundClimb)
                    return FlightPhase.GoAround;
                if (state.AltitudeAGL < 100)
                    return FlightPhase.FinalApproach;
                return FlightPhase.Approach;
            }

            if (state.VerticalSpeed > 500 && state.Altitude < context.TargetCruiseAltitude - 1500)
                return FlightPhase.InitialClimb;
            if (state.VerticalSpeed > 300)
                return FlightPhase.Climb;
            if (state.VerticalSpeed < -300)
                return FlightPhase.Descent;
            return FlightPhase.Cruise;
        }

        return FlightPhase.Unknown;
    }
}

/// <summary>
/// What a phase needs of the aircraft's state: which of ground or airborne it belongs to,
/// and how far above or below the ground it still fits. The registered definitions are what
/// <see cref="FlightPhaseManager.PhaseFitsState"/> reads, so a phase's boundary is declared
/// once rather than restated in a switch.
/// </summary>
public sealed class FlightPhaseDefinition
{
    /// <summary>Whether the phase needs the aircraft on the ground.</summary>
    public bool IsGroundPhase { get; init; }

    /// <summary>
    /// How high above the ground, in feet, this phase still fits. A ground phase ends above
    /// it; a value of 0 means the default ground tolerance applies.
    /// </summary>
    public double MaxAltitudeAGL { get; init; }

    /// <summary>How high above the ground, in feet, this airborne phase begins at.</summary>
    public double MinAltitudeAGL { get; init; }
}

public sealed class PhaseContext
{
    public bool EnginesRunning { get; set; }
    public bool DepartureClearanceReceived { get; set; }
    public bool AtDepartureGateOrRamp { get; set; }
    public bool AtDepartureRunway { get; set; }
    public bool AtTaxiStartPoint { get; set; }
    public double DistanceToDepartureRunway { get; set; }
    public bool TakeoffRoll { get; set; }
    public bool LandingRoll { get; set; }
    public bool GoAroundRequested { get; set; }
    public bool GoAroundClimb { get; set; }
    public double TargetCruiseAltitude { get; set; } = 34000;
    public bool Emergency { get; set; }
    public bool Holding { get; set; }
}
