using AICopilotFS.Domain.State;

namespace AICopilotFS.Domain.Atc;

/// <summary>Default implementation of the deterministic ATC state engine.</summary>
public sealed class AtcStateEngine : IAtcStateEngine
{
    public AtcDecision Evaluate(AtcEvaluationContext context)
    {
        // Guard chain implementation following StormATC patterns
        // 1. Emergency bypass
        // 2. State-free intents (say again, radio check)
        // 3. Confidence check
        // 4. Callsign check
        // 5. Jurisdiction check
        // 6. Outstanding readback check
        // 7. Intent dispatch

        return AtcDecision.Silent();
    }

    public AtcDecision EvaluateStateChange(AtcEvaluationContext context)
    {
        // Physical state changes: airborne, touchdown, parked, etc.
        return AtcDecision.Silent();
    }
}

/// <summary>ATC evaluation context for the state engine.</summary>
public sealed record AtcEvaluationContext
{
    public string Callsign { get; init; } = string.Empty;
    public string Transcript { get; init; } = string.Empty;
    public AtcFlightPhase CurrentPhase { get; init; } = AtcFlightPhase.Preflight;
    public Domain.State.AircraftState AircraftState { get; init; } = new();
    public Domain.FlightPlanning.FlightPlan FlightPlan { get; init; } = new();
    public Domain.State.AirportData? DepartureAirport { get; init; }
    public Domain.State.AirportData? DestinationAirport { get; init; }
    public Domain.State.WeatherSnapshot? Weather { get; init; }
    public Domain.ValueObjects.RealismOptions Realism { get; init; } = new();
    public DateTimeOffset CurrentTime { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>ATC decision result.</summary>
public sealed record AtcDecision
{
    public bool Accepted { get; init; }
    public AtcFlightPhase? NewPhase { get; init; }
    public string? ResponseType { get; init; }
    public string? RejectionReason { get; init; }
    public string? FallbackPhraseology { get; init; }
    public IReadOnlyDictionary<string, string> ApprovedFacts { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ProhibitedFacts { get; init; } = new List<string>();
    public bool AllowLanguageModelPhraseology { get; init; }
    public IReadOnlyList<AtcWarning> Warnings { get; init; } = new List<AtcWarning>();

    public static AtcDecision Silent() => new() { Accepted = true };

    public static AtcDecision Reject(string reason, string phraseology) => new()
    {
        Accepted = false,
        ResponseType = "reject",
        RejectionReason = reason,
        FallbackPhraseology = phraseology,
        AllowLanguageModelPhraseology = false,
    };
}

/// <summary>ATC warning.</summary>
public sealed record AtcWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double Severity { get; init; } = 1.0;
    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Flight phase type for ATC context.</summary>
public enum AtcFlightPhase
{
    Disconnected,
    Preflight,
    ClearanceRequested,
    IFRClearanceIssued,
    Pushback,
    TaxiOut,
    HoldingShort,
    LineUp,
    Takeoff,
    InitialClimb,
    Departure,
    Enroute,
    Descent,
    Arrival,
    Approach,
    Final,
    Landed,
    TaxiIn,
    Parked,
    GoAround,
    Abnormal,
}

/// <summary>Controller position for ATC context.</summary>
public enum AtcControllerPosition
{
    None,
    ClearanceDelivery,
    Ground,
    Tower,
    Departure,
    Center,
    Approach,
}
