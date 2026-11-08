using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Infrastructure.Atc;

/// <summary>ATC transport layer - menu probing + SimVar grant detection.</summary>
public sealed class AtcRequestEngine : IAtcTransport
{
    private readonly ILogger<AtcRequestEngine> _logger;

    public AtcRequestEngine(ILogger<AtcRequestEngine> logger)
    {
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("ATC request engine connected.");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("ATC request engine disconnected.");
        return Task.CompletedTask;
    }
}

/// <summary>ATC screen capture (optional - GDI window capture).</summary>
public sealed class AtcScreenCapture
{
    private readonly ILogger<AtcScreenCapture> _logger;

    public AtcScreenCapture(ILogger<AtcScreenCapture> logger)
    {
        _logger = logger;
    }
}

/// <summary>ATC menu screen reader (optional - template matching).</summary>
public sealed class AtcMenuScreenReader
{
    private readonly ILogger<AtcMenuScreenReader> _logger;

    public AtcMenuScreenReader(ILogger<AtcMenuScreenReader> logger)
    {
        _logger = logger;
    }
}
