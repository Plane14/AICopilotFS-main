using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AICopilotFS.Application.Checklists;

/// <summary>Service for checklist execution.</summary>
public sealed class ChecklistService : IChecklistService
{
    private readonly ILogger<ChecklistService> _logger;

    public ChecklistService(ILogger<ChecklistService> logger)
    {
        _logger = logger;
    }

    public Task ExecuteStepAsync(string stepId, CancellationToken ct = default)
    {
        _logger.LogInformation("Executing checklist step: {StepId}", stepId);
        return Task.CompletedTask;
    }
}
