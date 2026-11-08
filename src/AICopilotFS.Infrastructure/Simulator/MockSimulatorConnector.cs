using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Infrastructure.Simulator;

/// <summary>Mock simulator connector for offline/testing scenarios.</summary>
public sealed class MockSimulatorConnector : ISimulatorConnector
{
    private readonly ILogger<MockSimulatorConnector> _logger;

    public MockSimulatorConnector(ILogger<MockSimulatorConnector> logger)
    {
        _logger = logger;
    }

    public string ConnectorName => "Mock (Offline)";

    public SimulatorConnectionState ConnectionState { get; private set; } = SimulatorConnectionState.Disconnected;

    public string StatusMessage { get; private set; } = "Mock simulator not connected.";

    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;
    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    public Task ConnectAsync(CancellationToken ct)
    {
        ConnectionState = SimulatorConnectionState.Connected;
        StatusMessage = "Mock simulator connected.";
        ConnectionChanged?.Invoke(this, new SimulatorConnectionChangedEventArgs(ConnectionState, StatusMessage));
        _logger.LogInformation("Mock simulator connected.");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        ConnectionState = SimulatorConnectionState.Disconnected;
        StatusMessage = "Mock simulator disconnected.";
        ConnectionChanged?.Invoke(this, new SimulatorConnectionChangedEventArgs(ConnectionState, StatusMessage));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
