using System;
using System.Collections.Generic;

using AICopilotFS.Domain.ValueObjects;

namespace AICopilotFS.Domain.State;

/// <summary>Represents a flight phase.</summary>
public enum FlightPhase
{
    Unknown,
    Preflight,
    Startup,
    TaxiOut,
    HoldingShort,
    LineUp,
    Takeoff,
    InitialClimb,
    Climb,
    Cruise,
    Descent,
    Approach,
    FinalApproach,
    GoAround,
    Rollout,
    TaxiIn,
    Shutdown,
    Abnormal,
}

/// <summary>Represents a realism profile.</summary>
public enum RealismProfile
{
    Relaxed,
    Standard,
    Strict,
}

/// <summary>Context provided to the flight phase engine for evaluation.</summary>
public sealed record PhaseEvaluationContext
{
    public required FlightPhase CurrentPhase { get; init; }
    public required AircraftState AircraftState { get; init; }
    public required FlightPlan FlightPlan { get; init; }
    public required AirportData? DepartureAirport { get; init; }
    public required AirportData? DestinationAirport { get; init; }
    public required WeatherSnapshot? Weather { get; init; }
    public required TerrainProfile? TerrainProfile { get; init; }
    public required IReadOnlyList<TrafficSnapshot> Traffic { get; init; } = new List<TrafficSnapshot>();
    public required RealismOptions Realism { get; init; }
    public required DateTimeOffset CurrentTime { get; init; }
}

/// <summary>Result of a flight phase evaluation.</summary>
public sealed record PhaseDecision
{
    public bool Accepted { get; init; }
    public FlightPhase? NewPhase { get; init; }
    public ControlTargets? ControlTargets { get; init; }
    public AtcRequest? AtcRequest { get; init; }
    public ChecklistAction? ChecklistAction { get; init; }
    public string? LogMessage { get; init; }
    public IReadOnlyList<PhaseWarning> Warnings { get; init; } = new List<PhaseWarning>();

    public static PhaseDecision Silent() => new() { Accepted = true };

    public static PhaseDecision TransitionTo(FlightPhase phase) =>
        new() { Accepted = true, NewPhase = phase };
}

/// <summary>Control targets produced by the autopilot/guidance system.</summary>
public sealed record ControlTargets
{
    public double? TargetPitchDegrees { get; init; }
    public double? TargetBankDegrees { get; init; }
    public double? TargetHeadingDegrees { get; init; }
    public AltitudeFeet? TargetAltitudeFeet { get; init; }
    public SpeedKnots? TargetSpeedKnots { get; init; }
    public VerticalSpeedFpm? TargetVerticalSpeedFpm { get; init; }
    public bool UseNativeAutopilot { get; init; }
    public AutopilotMode Mode { get; init; } = AutopilotMode.HeadingHold;
    public bool GearDown { get; init; }
}

/// <summary>Autopilot modes.</summary>
public enum AutopilotMode
{
    HeadingHold,
    AltitudeHold,
    SpeedHold,
    VerticalSpeedHold,
    NavHold,
    ApproachHold,
    Disengaged,
}

/// <summary>A warning generated during phase evaluation.</summary>
public sealed record PhaseWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double Severity { get; init; } = 1.0;
    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An ATC request to be executed by the transport layer.</summary>
public sealed record AtcRequest
{
    public string Type { get; init; } = string.Empty;
    public string? Runway { get; init; }
    public string? TaxiwayNames { get; init; }
    public FrequencyMhz? Frequency { get; init; }
    public SquawkCode? Squawk { get; init; }
}

/// <summary>A checklist action to execute.</summary>
public sealed record ChecklistAction
{
    public string StepId { get; init; } = string.Empty;
    public string? SimVar { get; init; }
    public double? SimVarValue { get; init; }
    public string? EventName { get; init; }
    public int? EventValue { get; init; }
}

/// <summary>PID loop gains.</summary>
public sealed record PidGains
{
    public double Kp { get; init; }
    public double Ki { get; init; }
    public double Kd { get; init; }
    public double MinOutput { get; init; } = -1.0;
    public double MaxOutput { get; init; } = 1.0;
    public double Deadband { get; init; } = 0.004;
}

/// <summary>Aircraft control profile per phase.</summary>
public sealed record AircraftControlProfile
{
    public FlightPhase Phase { get; init; }
    public PidGains Pitch { get; init; } = new() { Kp = 0.1, Ki = 0.01, Kd = 0.05 };
    public PidGains Bank { get; init; } = new() { Kp = 0.1, Ki = 0.01, Kd = 0.05 };
    public PidGains Speed { get; init; } = new() { Kp = 0.05, Ki = 0.005, Kd = 0.02 };
    public PidGains Altitude { get; init; } = new() { Kp = 0.05, Ki = 0.005, Kd = 0.02 };
}

/// <summary>Terrain profile along a route.</summary>
public sealed record TerrainProfile
{
    public const double MountainousThresholdFt = 4500.0;
    public const double NormalMarginFt = 1000.0;
    public const double MountainMarginFt = 2000.0;
    public const double FallbackMarginFt = 3000.0;
    public const double SampleSpacingNm = 8.0;

    public IReadOnlyList<TerrainSegment> Segments { get; init; } = new List<TerrainSegment>();
    public bool Mountainous { get; init; }
    public bool IsFallback { get; init; }
    public double MaxRequiredAltitudeFt { get; init; }
}

/// <summary>A single terrain segment.</summary>
public sealed record TerrainSegment
{
    public double DistanceNm { get; init; }
    public double TerrainElevationFt { get; init; }
    public double RequiredAltitudeFt { get; init; }
    public bool IsMountainous { get; init; }
}

/// <summary>Realism options for ATC/phase engine.</summary>
public sealed record RealismOptions
{
    public RealismProfile Profile { get; init; } = RealismProfile.Standard;
    public bool RequireReadbackBeforeProgressing { get; init; } = true;
    public bool EnforceHoldShort { get; init; } = true;
    public bool RequireCorrectCallsign { get; init; } = true;
    public double MinimumIntentConfidence { get; init; } = 0.6;

    public static RealismOptions FromProfile(RealismProfile profile) => profile switch
    {
        RealismProfile.Relaxed => new()
        {
            RequireReadbackBeforeProgressing = false,
            EnforceHoldShort = false,
            RequireCorrectCallsign = false,
            MinimumIntentConfidence = 0.6,
        },
        RealismProfile.Standard => new(),
        RealismProfile.Strict => new()
        {
            MinimumIntentConfidence = 0.75,
            RequireReadbackBeforeProgressing = true,
            EnforceHoldShort = true,
            RequireCorrectCallsign = true,
        },
        _ => new(),
    };
}

/// <summary>Traffic snapshot for conflict assessment.</summary>
public sealed record TrafficSnapshot
{
    public string Callsign { get; init; } = string.Empty;
    public string ModelTitle { get; init; } = string.Empty;
    public GeoPosition Position { get; init; } = GeoPosition.Zero;
    public AltitudeFeet AltitudeFeet { get; init; } = AltitudeFeet.Invalid;
    public SpeedKnots GroundSpeedKnots { get; init; } = SpeedKnots.Zero;
    public HeadingDegrees MagneticHeadingDegrees { get; init; } = HeadingDegrees.Zero;
    public VerticalSpeedFpm VerticalSpeedFpm { get; init; } = VerticalSpeedFpm.Zero;
    public bool IsOnGround { get; init; }
}

/// <summary>Airport data for taxi routing and facility information.</summary>
public sealed record AirportData
{
    public string Icao { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public GeoPosition ReferencePosition { get; init; } = GeoPosition.Zero;
    public AltitudeFeet ReferenceAltitudeFeet { get; init; } = AltitudeFeet.Invalid;
    public double MagneticVariationDegrees { get; init; }
    public IReadOnlyList<RunwayData> Runways { get; init; } = new List<RunwayData>();
    public IReadOnlyList<TaxiwayNode> TaxiwayNodes { get; init; } = new List<TaxiwayNode>();
    public IReadOnlyList<TaxiwayEdge> TaxiwayEdges { get; init; } = new List<TaxiwayEdge>();
    public IReadOnlyList<ParkingSpot> ParkingSpots { get; init; } = new List<ParkingSpot>();
    public IReadOnlyList<FrequencyData> Frequencies { get; init; } = new List<FrequencyData>();
    public bool SupportsTaxiRouting => TaxiwayNodes.Count > 0 && TaxiwayEdges.Count > 0;
}

/// <summary>Runway data.</summary>
public sealed record RunwayData
{
    public uint Index { get; init; }
    public GeoPosition Position { get; init; } = GeoPosition.Zero;
    public double HeadingDegrees { get; init; }
    public double LengthMeters { get; init; }
    public double WidthMeters { get; init; }
    public int Surface { get; init; }
    public int PrimaryNumber { get; init; }
    public string PrimaryDesignator { get; init; } = string.Empty;
    public int SecondaryNumber { get; init; }
    public string SecondaryDesignator { get; init; } = string.Empty;
}

/// <summary>Taxiway node (point).</summary>
public sealed record TaxiwayNode
{
    public uint Index { get; init; }
    public int Type { get; init; }
    public int Orientation { get; init; }
    public double BiasXMeters { get; init; }
    public double BiasZMeters { get; init; }
    public GeoPosition Position { get; init; } = GeoPosition.Zero;
}

/// <summary>Taxiway edge (path between nodes).</summary>
public sealed record TaxiwayEdge
{
    public uint Index { get; init; }
    public int Type { get; init; }
    public double WidthMeters { get; init; }
    public uint WeightLimitPounds { get; init; }
    public int RunwayNumber { get; init; }
    public int RunwayDesignator { get; init; }
    public uint StartIndex { get; init; }
    public uint EndIndex { get; init; }
    public uint NameIndex { get; init; }
    public string TaxiwayName { get; init; } = string.Empty;
}

/// <summary>Parking spot.</summary>
public sealed record ParkingSpot
{
    public uint Index { get; init; }
    public int Type { get; init; }
    public int TaxiPointType { get; init; }
    public int Name { get; init; }
    public int Suffix { get; init; }
    public uint Number { get; init; }
    public int Orientation { get; init; }
    public double HeadingDegrees { get; init; }
    public double RadiusMeters { get; init; }
    public double BiasXMeters { get; init; }
    public double BiasZMeters { get; init; }
}

/// <summary>Frequency data.</summary>
public sealed record FrequencyData
{
    public uint Index { get; init; }
    public int Type { get; init; }
    public uint FrequencyHertz { get; init; }
    public FrequencyMhz Mhz => new(FrequencyHertz / 1_000_000.0);
}

/// <summary>Traffic assessment result.</summary>
public sealed record TrafficAssessment
{
    public bool RunwayOccupied { get; init; }
    public bool OnFinal { get; init; }
    public bool AirborneConflict { get; init; }
    public IReadOnlyList<TrafficSnapshot> Conflicts { get; init; } = new List<TrafficSnapshot>();
}

/// <summary>Interface for traffic assessment.</summary>
public interface ITrafficAssessor
{
    TrafficAssessment Assess(IReadOnlyList<TrafficSnapshot> traffic, AirportData? airport);
}

/// <summary>Taxi route result.</summary>
public sealed record TaxiRoute
{
    public bool Found { get; init; }
    public List<uint> Nodes { get; init; } = new();
    public List<string> TaxiwayNames { get; init; } = new();
    public double TotalLengthMeters { get; init; }
}

/// <summary>Interface for taxi routing over the ground graph.</summary>
public interface ITaxiRouter
{
    TaxiRoute? FindRouteToHoldingPoint(AirportData airport, GeoPosition position, string runway);
}

/// <summary>Interface for route building.</summary>
public interface IRouteBuilder
{
    FlightPlan BuildRoute(string departureIcao, string destinationIcao, string departureRunway);
}

/// <summary>Interface for performance calculations.</summary>
public interface IPerformanceCalculator
{
    double ClimbRateFpm { get; }
    double DescentRateFpm { get; }
    double CruiseSpeed { get; }
}

/// <summary>Interface for terrain analysis.</summary>
public interface ITerrainAnalyzer
{
    double RequiredAltitudeAheadFt(GeoPosition position, double aheadNm);
    bool IsPathBlocked(GeoPosition position, double currentAltitudeFt, double lookAheadNm);
}

/// <summary>Terrain analysis segment.</summary>
public sealed record TerrainAnalysisSegment
{
    public double DistanceNm { get; init; }
    public double TerrainElevationFt { get; init; }
    public double RequiredAltitudeFt { get; init; }
}
