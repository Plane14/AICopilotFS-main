using Microsoft.Extensions.Options;

using AICopilotFS.Application.Abstractions;
using AICopilotFS.Application.Options;

namespace AICopilotFS.Infrastructure.Simulator;

/// <summary>
/// Router that selects MSFS or Mock connector at runtime.
/// Only one connector is active at a time.
/// </summary>
public sealed class SimulatorConnectorRouter : ISimulatorConnector
{
    private readonly MsfsSimConnectConnector _msfs;
    private readonly MockSimulatorConnector _mock;
    private readonly IOptionsMonitor<SimulatorOptions> _options;
    private ISimulatorConnector _active = null!;

    public SimulatorConnectorRouter(
        MsfsSimConnectConnector msfs,
        MockSimulatorConnector mock,
        IOptionsMonitor<SimulatorOptions> options)
    {
        _msfs = msfs;
        _mock = mock;
        _options = options;
    }

    public string ConnectorName => _active.ConnectorName;

    public SimulatorConnectionState ConnectionState => _active.ConnectionState;

    public string StatusMessage => _active.StatusMessage;

    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged
    {
        add => _active.AircraftStateChanged += value;
        remove => _active.AircraftStateChanged -= value;
    }

    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged
    {
        add => _active.ConnectionChanged += value;
        remove => _active.ConnectionChanged -= value;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var preferred = _options.CurrentValue.PreferredConnector;
        _active = string.Equals(preferred, "MSFS", StringComparison.OrdinalIgnoreCase) ? _msfs : (ISimulatorConnector)_mock;
        await _active.ConnectAsync(ct);

        // If MSFS fails and auto-reconnect enabled, fall back to mock
        if (_active == _msfs && _msfs.ConnectionState != SimulatorConnectionState.Connected)
        {
            _active = _mock;
            await _active.ConnectAsync(ct);
        }
    }

    public Task DisconnectAsync(CancellationToken ct) => _active.DisconnectAsync(ct);

    public ValueTask DisposeAsync() => _active.DisposeAsync();
}
