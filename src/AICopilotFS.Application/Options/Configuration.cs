using System;

namespace AICopilotFS.Application.Options;

/// <summary>Simulator configuration options.</summary>
public sealed class SimulatorOptions
{
    public string PreferredConnector { get; set; } = "MSFS";
    public int UpdatesPerSecond { get; set; } = 20;
    public bool AutoReconnect { get; set; } = true;
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Flight configuration options.</summary>
public sealed class FlightOptions
{
    public double DefaultCruiseSpeedKt { get; set; } = 140;
    public double DefaultClimbRateFpm { get; set; } = 1200;
    public double DefaultDescentRateFpm { get; set; } = 1500;
}

/// <summary>Terrain configuration options.</summary>
public sealed class TerrainOptions
{
    public double PreFlightSampleSpacingNm { get; set; } = 8.0;
    public int InFlightGridWidthPoints { get; set; } = 10;
    public int InFlightGridHeightPoints { get; set; } = 5;
    public double InFlightLookAheadNm { get; set; } = 15.0;
    public TimeSpan InFlightUpdateInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Continuous flight options.</summary>
public sealed class ContinuousFlightOptions
{
    public double MinRangeNm { get; set; } = 50;
    public double MaxRangeNm { get; set; } = 500;
    public int ServiceTimeMinutes { get; set; } = 30;
    public double ReserveFactor { get; set; } = 0.9;
}

/// <summary>Realism options.</summary>
public sealed class RealismOptions
{
    public Domain.State.RealismProfile Profile { get; set; } = Domain.State.RealismProfile.Standard;
    public bool RequireReadbackBeforeProgressing { get; set; } = true;
    public bool EnforceHoldShort { get; set; } = true;
    public bool RequireCorrectCallsign { get; set; } = true;
    public double MinimumIntentConfidence { get; set; } = 0.6;
}

/// <summary>Checklist options.</summary>
public sealed class ChecklistOptions
{
    public bool Enabled { get; set; } = true;
    public string ChecklistDirectory { get; set; } = "flightchecklists";
}
