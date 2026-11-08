using AICopilotFS.Domain.Atc;
using AICopilotFS.Domain.State;

namespace AICopilotFS.Domain.Tests.Atc;

public class FlightPhaseEngineTests
{
    [Fact]
    public void Evaluate_UnknownPhase_ReturnsSilent()
    {
        var engine = new FlightPhaseEngine();
        var context = new PhaseEvaluationContext
        {
            CurrentPhase = FlightPhase.Unknown,
            AircraftState = new Domain.State.AircraftState(),
            FlightPlan = new FlightPlan(),
            CurrentTime = DateTimeOffset.UtcNow,
            Realism = new RealismOptions(),
        };

        var decision = engine.Evaluate(context);

        Assert.True(decision.Accepted);
        Assert.Null(decision.NewPhase);
    }

    [Fact]
    public void EvaluateStateChange_AnyPhase_ReturnsSilent()
    {
        var engine = new FlightPhaseEngine();
        var context = new PhaseEvaluationContext
        {
            CurrentPhase = FlightPhase.Preflight,
            AircraftState = new Domain.State.AircraftState(),
            FlightPlan = new FlightPlan(),
            CurrentTime = DateTimeOffset.UtcNow,
            Realism = new RealismOptions(),
        };

        var decision = engine.EvaluateStateChange(context);

        Assert.True(decision.Accepted);
        Assert.Null(decision.NewPhase);
    }

    [Fact]
    public void PhaseDecision_Silent_ReturnsAccepted()
    {
        var decision = PhaseDecision.Silent();
        Assert.True(decision.Accepted);
        Assert.Null(decision.NewPhase);
    }

    [Fact]
    public void PhaseDecision_TransitionTo_SetsNewPhase()
    {
        var decision = PhaseDecision.TransitionTo(FlightPhase.Takeoff);
        Assert.True(decision.Accepted);
        Assert.Equal(FlightPhase.Takeoff, decision.NewPhase);
    }
}
