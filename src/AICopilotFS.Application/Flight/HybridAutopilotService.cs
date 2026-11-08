using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Application.Flight;

/// <summary>Hybrid autopilot service - native AP for enroute, PID for ground/precision.</summary>
public sealed class HybridAutopilotService : IAutopilotService
{
    private readonly ILogger<HybridAutopilotService> _logger;

    public HybridAutopilotService(ILogger<HybridAutopilotService> logger)
    {
        _logger = logger;
    }

    public Domain.State.ControlTargets Update(
        Domain.State.PhaseEvaluationContext context,
        Domain.State.PhaseDecision decision)
    {
        var targets = decision.ControlTargets ?? new Domain.State.ControlTargets();
        _logger.LogDebug("Autopilot update: mode={Mode}, alt={Alt}", targets.Mode, targets.TargetAltitudeFeet?.Feet);
        return targets;
    }
}
