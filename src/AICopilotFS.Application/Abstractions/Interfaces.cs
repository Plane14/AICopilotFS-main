using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AICopilotFS.Application.Abstractions;

/// <summary>Interface for terrain elevation providers.</summary>
public interface ITerrainProvider
{
    Task<IReadOnlyList<double>?> GetElevationsAsync(
        IReadOnlyList<(double lat, double lon)> points,
        CancellationToken ct = default);

    Task<double[]?> GetGroundGridAsync(
        double centerLat,
        double centerLon,
        int widthPoints,
        int heightPoints,
        double spacingNm,
        CancellationToken ct = default);
}

/// <summary>Interface for weather providers.</summary>
public interface IWeatherProvider
{
    Task<Domain.State.WeatherSnapshot?> GetWeatherAsync(CancellationToken ct = default);
}

/// <summary>Interface for flight plan providers.</summary>
public interface IFlightPlanProvider
{
    Task<Domain.FlightPlanning.FlightPlan?> GetFlightPlanAsync(CancellationToken ct = default);
}

/// <summary>Interface for the simulator connector.</summary>
public interface ISimulatorConnector
{
    string ConnectorName { get; }
    SimulatorConnectionState ConnectionState { get; }
    string StatusMessage { get; }
    event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;
    event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    ValueTask DisposeAsync();
}

/// <summary>Interface for ATC state engine.</summary>
public interface IAtcStateEngine
{
    Domain.Atc.AtcDecision Evaluate(Domain.Atc.AtcEvaluationContext context);
    Domain.Atc.AtcDecision EvaluateStateChange(Domain.Atc.AtcEvaluationContext context);
}

/// <summary>Interface for ATC transport (menu probing, SimVar grants).</summary>
public interface IAtcTransport
{
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

/// <summary>Interface for ATC service coordinating engine + transport.</summary>
public interface IAtcService
{
    Task ProcessTransmissionAsync(string transcript, CancellationToken ct = default);
}

/// <summary>Interface for flight orchestrator.</summary>
public interface IFlightOrchestrator
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task TickAsync(CancellationToken ct);
}

/// <summary>Interface for continuous flight service.</summary>
public interface IContinuousFlightService
{
    Task PlanNextLegAsync(CancellationToken ct = default);
}

/// <summary>Interface for checklist service.</summary>
public interface IChecklistService
{
    Task ExecuteStepAsync(string stepId, CancellationToken ct = default);
}

/// <summary>Interface for autopilot service.</summary>
public interface IAutopilotService
{
    Domain.State.ControlTargets Update(
        Domain.State.PhaseEvaluationContext context,
        Domain.State.PhaseDecision decision);
}

/// <summary>Simulator connection states.</summary>
public enum SimulatorConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Unavailable,
    Error,
}

/// <summary>Event args for aircraft state changes.</summary>
public sealed class AircraftStateChangedEventArgs : EventArgs
{
    public Domain.State.AircraftState State { get; }
    public AircraftStateChangedEventArgs(Domain.State.AircraftState state) => State = state;
}

/// <summary>Event args for simulator connection changes.</summary>
public sealed class SimulatorConnectionChangedEventArgs : EventArgs
{
    public SimulatorConnectionState State { get; }
    public string Message { get; }
    public SimulatorConnectionChangedEventArgs(SimulatorConnectionState state, string message)
    {
        State = state;
        Message = message;
    }
}

/// <summary>ATC decision (Application layer - lightweight view of Domain.Atc.AtcDecision).</summary>
public sealed record AtcDecision
{
    public bool Accepted { get; init; }
    public string? ResponseType { get; init; }
    public string? FallbackPhraseology { get; init; }
    public IReadOnlyDictionary<string, string> ApprovedFacts { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ProhibitedFacts { get; init; } = new List<string>();
    public bool AllowLanguageModelPhraseology { get; init; }
}

/// <summary>ATC evaluation context (Application layer - lightweight view).</summary>
public sealed record AtcEvaluationContext
{
    public string Callsign { get; init; } = string.Empty;
    public string Transcript { get; init; } = string.Empty;
    public string CurrentPhase { get; init; } = string.Empty;
    public Domain.State.WeatherSnapshot? Weather { get; init; }
    public Domain.ValueObjects.RealismOptions Realism { get; init; } = new();
    public DateTimeOffset CurrentTime { get; init; } = DateTimeOffset.UtcNow;
}
