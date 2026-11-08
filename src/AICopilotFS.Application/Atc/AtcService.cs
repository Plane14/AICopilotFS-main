using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Application.Atc;

/// <summary>ATC service coordinating the state engine and transport layer.</summary>
public sealed class AtcService : IAtcService
{
    private readonly IAtcStateEngine _engine;
    private readonly IAtcTransport _transport;
    private readonly ILogger<AtcService> _logger;

    public AtcService(
        IAtcStateEngine engine,
        IAtcTransport transport,
        ILogger<AtcService> logger)
    {
        _engine = engine;
        _transport = transport;
        _logger = logger;
    }

    public async Task ProcessTransmissionAsync(string transcript, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing ATC transmission: {Transcript}", transcript);

        var context = new Domain.Atc.AtcEvaluationContext
        {
            Callsign = string.Empty,
            Transcript = transcript,
            CurrentPhase = Domain.Atc.AtcFlightPhase.Preflight,
            CurrentTime = DateTimeOffset.UtcNow,
        };

        var decision = _engine.Evaluate(context);

        if (!decision.Accepted)
        {
            _logger.LogWarning("ATC request rejected: {Reason}", decision.RejectionReason);
        }
        else
        {
            _logger.LogInformation("ATC decision: {ResponseType}", decision.ResponseType);
        }

        await Task.CompletedTask;
    }
}
