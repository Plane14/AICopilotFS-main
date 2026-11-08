using AICopilotFS.Domain.State;

namespace AICopilotFS.Domain.Atc;

/// <summary>Default implementation of the flight phase engine (deterministic state machine).</summary>
public sealed class FlightPhaseEngine : IFlightPhaseEngine
{
    public PhaseDecision Evaluate(PhaseEvaluationContext context)
    {
        // Guard 1: Emergency/abnormal handling
        if (context.CurrentPhase == FlightPhase.Abnormal)
        {
            return PhaseDecision.TransitionTo(FlightPhase.Abnormal);
        }

        // Guard 2: Airborne detection from ground phases
        if (!context.AircraftState.IsOnGround &&
            (context.CurrentPhase == FlightPhase.Preflight ||
             context.CurrentPhase == FlightPhase.Startup ||
             context.CurrentPhase == FlightPhase.TaxiOut))
        {
            return PhaseDecision.TransitionTo(FlightPhase.InitialClimb);
        }

        // Dispatch to phase handler
        return context.CurrentPhase switch
        {
            FlightPhase.Preflight => HandlePreflight(context),
            FlightPhase.Startup => HandleStartup(context),
            FlightPhase.TaxiOut => HandleTaxiOut(context),
            FlightPhase.HoldingShort => HandleHoldingShort(context),
            FlightPhase.LineUp => HandleLineUp(context),
            FlightPhase.Takeoff => HandleTakeoff(context),
            FlightPhase.InitialClimb => HandleInitialClimb(context),
            FlightPhase.Climb => HandleClimb(context),
            FlightPhase.Cruise => HandleCruise(context),
            FlightPhase.Descent => HandleDescent(context),
            FlightPhase.Approach => HandleApproach(context),
            FlightPhase.FinalApproach => HandleFinalApproach(context),
            FlightPhase.GoAround => HandleGoAround(context),
            FlightPhase.Rollout => HandleRollout(context),
            FlightPhase.TaxiIn => HandleTaxiIn(context),
            FlightPhase.Shutdown => HandleShutdown(context),
            _ => PhaseDecision.Silent(),
        };
    }

    public PhaseDecision EvaluateStateChange(PhaseEvaluationContext context)
    {
        // Physical state triggers (no pilot input required)

        // Becoming airborne -> InitialClimb
        if (!context.AircraftState.IsOnGround &&
            (context.CurrentPhase == FlightPhase.Takeoff ||
             context.CurrentPhase == FlightPhase.LineUp ||
             context.CurrentPhase == FlightPhase.HoldingShort))
        {
            return PhaseDecision.TransitionTo(FlightPhase.InitialClimb);
        }

        // Stable climb through 1000 ft AGL -> Climb
        if (context.CurrentPhase == FlightPhase.InitialClimb &&
            context.AircraftState.AltitudeAboveGroundFeet.Feet > 1000)
        {
            return PhaseDecision.TransitionTo(FlightPhase.Climb);
        }

        // Climb -> Cruise (at or near target altitude)
        if (context.CurrentPhase == FlightPhase.Climb &&
            context.AircraftState.IndicatedAltitudeFeet.IsValid &&
            context.AircraftState.IndicatedAltitudeFeet.Feet >=
            (context.TerrainProfile?.MaxRequiredAltitudeFt ?? 0) - 200)
        {
            return PhaseDecision.TransitionTo(FlightPhase.Cruise);
        }

        // Cruise -> Descent (when top of descent reached)
        if (context.CurrentPhase == FlightPhase.Cruise &&
            context.TerrainProfile != null)
        {
            return PhaseDecision.TransitionTo(FlightPhase.Descent);
        }

        // Touchdown -> Rollout
        if (context.AircraftState.IsOnGround &&
            (context.CurrentPhase == FlightPhase.FinalApproach ||
             context.CurrentPhase == FlightPhase.Approach))
        {
            return PhaseDecision.TransitionTo(FlightPhase.Rollout);
        }

        // Rollout -> TaxiIn (below 5 knots)
        if (context.CurrentPhase == FlightPhase.Rollout &&
            context.AircraftState.GroundSpeedKnots.Knots < 5)
        {
            return PhaseDecision.TransitionTo(FlightPhase.TaxiIn);
        }

        // TaxiIn -> Shutdown (at stand with parking brake set)
        if (context.CurrentPhase == FlightPhase.TaxiIn &&
            context.AircraftState.IsOnGround &&
            context.AircraftState.ParkingBrakeSet &&
            context.AircraftState.GroundSpeedKnots.Knots < 1)
        {
            return PhaseDecision.TransitionTo(FlightPhase.Shutdown);
        }

        return PhaseDecision.Silent();
    }

    private static PhaseDecision HandlePreflight(PhaseEvaluationContext context) =>
        PhaseDecision.TransitionTo(FlightPhase.Startup);

    private static PhaseDecision HandleStartup(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleTaxiOut(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleHoldingShort(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleLineUp(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleTakeoff(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleInitialClimb(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleClimb(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleCruise(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleDescent(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleApproach(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleFinalApproach(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleGoAround(PhaseEvaluationContext context) =>
        PhaseDecision.TransitionTo(FlightPhase.InitialClimb);

    private static PhaseDecision HandleRollout(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleTaxiIn(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();

    private static PhaseDecision HandleShutdown(PhaseEvaluationContext context) =>
        PhaseDecision.Silent();
}
