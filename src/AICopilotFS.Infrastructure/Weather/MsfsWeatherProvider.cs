using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Infrastructure.Weather;

/// <summary>MSFS-based weather provider.</summary>
public sealed class MsfsWeatherProvider : IWeatherProvider
{
    private readonly ILogger<MsfsWeatherProvider> _logger;

    public MsfsWeatherProvider(ILogger<MsfsWeatherProvider> logger)
    {
        _logger = logger;
    }

    public Task<WeatherSnapshot?> GetWeatherAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Requesting weather from MSFS SimConnect.");
        return Task.FromResult<WeatherSnapshot?>(null);
    }
}
