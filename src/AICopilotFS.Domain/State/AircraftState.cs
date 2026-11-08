using System.Collections.Generic;

namespace AICopilotFS.Domain.ValueObjects;

/// <summary>Represents the complete aircraft state decoded from SimConnect.</summary>
public sealed record AircraftState
{
    public DateTimeOffset Timestamp { get; init; }
    public GeoPosition Position { get; init; } = GeoPosition.Zero;
    public AltitudeFeet IndicatedAltitudeFeet { get; init; } = AltitudeFeet.Invalid;
    public AltitudeFeet PressureAltitudeFeet { get; init; } = AltitudeFeet.Invalid;
    public AltitudeFeet AltitudeAboveGroundFeet { get; init; } = AltitudeFeet.Invalid;
    public SpeedKnots GroundSpeedKnots { get; init; } = SpeedKnots.Zero;
    public SpeedKnots IndicatedAirspeedKnots { get; init; } = SpeedKnots.Zero;
    public HeadingDegrees TrueHeadingDegrees { get; init; } = HeadingDegrees.Zero;
    public HeadingDegrees MagneticHeadingDegrees { get; init; } = HeadingDegrees.Zero;
    public VerticalSpeedFpm VerticalSpeedFpm { get; init; } = VerticalSpeedFpm.Zero;
    public bool IsOnGround { get; init; }
    public bool ParkingBrakeSet { get; init; }
    public bool EnginesRunning { get; init; }
    public string AircraftTitle { get; init; } = string.Empty;
    public string AtcIdentifier { get; init; } = string.Empty;
    public bool AutopilotEngaged { get; init; }
    public bool GearHandleDown { get; init; }
    public int FlapsHandleIndex { get; init; }

    public RadioState Radio { get; init; } = new();
    public TransponderState Transponder { get; init; } = new();
    public WeatherSnapshot? Weather { get; init; }
}

/// <summary>Represents radio state.</summary>
public readonly record struct RadioState
{
    public FrequencyMhz Com1Active { get; init; } = FrequencyMhz.Zero;
    public FrequencyMhz Com1Standby { get; init; } = FrequencyMhz.Zero;
    public FrequencyMhz Com2Active { get; init; } = FrequencyMhz.Zero;
    public FrequencyMhz Com2Standby { get; init; } = FrequencyMhz.Zero;
    public bool TransmitOnCom1 { get; init; } = true;
}

/// <summary>Represents transponder state.</summary>
public readonly record struct TransponderState
{
    public SquawkCode Code { get; init; } = SquawkCode.Conspicuity;
    public int Mode { get; init; } = 3; // Mode A/C by default
}

/// <summary>Represents weather snapshot at aircraft position.</summary>
public readonly record struct WeatherSnapshot
{
    public double WindDirectionDegrees { get; init; }
    public double WindVelocityKnots { get; init; }
    public double SeaLevelPressureMb { get; init; }
    public double VisibilityMeters { get; init; }
    public double TemperatureCelsius { get; init; }
    public double TrueHeadingDegrees { get; init; }
    public double MagneticHeadingDegrees { get; init; }

    public (double DirectionDegrees, double SpeedKnots) WindComponents(double trackDegrees)
    {
        var trackRad = trackDegrees * Math.PI / 180.0;
        var windRad = WindDirectionDegrees * Math.PI / 180.0;
        var headwind = WindVelocityKnots * Math.Cos(windRad - trackRad);
        var crosswind = WindVelocityKnots * Math.Sin(windRad - trackRad);
        return (headwind, crosswind);
    }
}
