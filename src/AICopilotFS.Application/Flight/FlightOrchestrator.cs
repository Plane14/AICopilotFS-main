using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Application.Flight;

/// <summary>Orchestrates flight phases and coordinates all services.</summary>
public sealed class FlightOrchestrator : IFlightOrchestrator
{
    private readonly Domain.Atc.IFlightPhaseEngine _phaseEngine;
    private readonly IAtcStateEngine _atcEngine;
    private readonly IAutopilotService _autopilot;
    private readonly ILogger<FlightOrchestrator> _logger;

    private Domain.State.FlightPhase _currentPhase = Domain.State.FlightPhase.Unknown;

    public FlightOrchestrator(
        Domain.Atc.IFlightPhaseEngine phaseEngine,
        IAtcStateEngine atcEngine,
        IAutopilotService autopilot,
        ILogger<FlightOrchestrator> logger)
    {
        _phaseEngine = phaseEngine;
        _atcEngine = atcEngine;
        _autopilot = autopilot;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _currentPhase = Domain.State.FlightPhase.Preflight;
        _logger.LogInformation("Flight orchestrator started. Phase: {Phase}", _currentPhase);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _currentPhase = Domain.State.FlightPhase.Shutdown;
        _logger.LogInformation("Flight orchestrator stopped.");
        return Task.CompletedTask;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var context = new Domain.State.PhaseEvaluationContext
        {
            CurrentPhase = _currentPhase,
        };

        var decision = _phaseEngine.Evaluate(context);

        if (decision.NewPhase is { } newPhase)
        {
            _logger.LogInformation("Phase transition: {OldPhase} -> {NewPhase}", _currentPhase, newPhase);
            _currentPhase = newPhase;
        }

        if (decision.ControlTargets is { } targets)
        {
            // Pass to autopilot
        }

        await Task.CompletedTask;
    }
}
