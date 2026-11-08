using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Infrastructure.FlightPlanning;

/// <summary>SimBrief flight plan provider.</summary>
public sealed class SimBriefFlightPlanProvider : IFlightPlanProvider
{
    private readonly ILogger<SimBriefFlightPlanProvider> _logger;

    public SimBriefFlightPlanProvider(ILogger<SimBriefFlightPlanProvider> logger)
    {
        _logger = logger;
    }

    public Task<FlightPlan?> GetFlightPlanAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Importing flight plan from SimBrief.");
        return Task.FromResult<FlightPlan?>(null);
    }
}

/// <summary>Bundled sample flight plan provider (fallback).</summary>
public sealed class BundledFlightPlanProvider : IFlightPlanProvider
{
    private readonly ILogger<BundledFlightPlanProvider> _logger;

    public BundledFlightPlanProvider(ILogger<BundledFlightPlanProvider> logger)
    {
        _logger = logger;
    }

    public Task<FlightPlan?> GetFlightPlanAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Using bundled sample flight plan.");
        return Task.FromResult<FlightPlan?>(null);
    }
}

/// <summary>Router between SimBrief and bundled flight plan providers.</summary>
public sealed class FlightPlanProviderRouter : IFlightPlanProvider
{
    private readonly SimBriefFlightPlanProvider _simBrief;
    private readonly BundledFlightPlanProvider _bundled;
    private readonly bool _useSimBrief;

    public FlightPlanProviderRouter(
        SimBriefFlightPlanProvider simBrief,
        BundledFlightPlanProvider bundled,
        bool useSimBrief = false)
    {
        _simBrief = simBrief;
        _bundled = bundled;
        _useSimBrief = useSimBrief;
    }

    public Task<FlightPlan?> GetFlightPlanAsync(CancellationToken ct = default)
    {
        return _useSimBrief ? _simBrief.GetFlightPlanAsync(ct) : _bundled.GetFlightPlanAsync(ct);
    }
}
