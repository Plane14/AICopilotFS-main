using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core.State;

public enum FlightPhase
{
    Unknown,
    Preflight,
    Startup,
    TaxiOut,
    Takeoff,
    InitialClimb,
    Climb,
    Cruise,
    Descent,
    Approach,
    FinalApproach,
    GoAround,
    Landing,
    Rollout,
    TaxiIn,
    Shutdown,
    Holding,
    Divert,
    Emergency,
}

public enum PilotControlState
{
    /// <summary>The user has the aircraft; the pilot only observes.</summary>
    Manual,
    /// <summary>The pilot flies the current phase.</summary>
    AiActive,
    /// <summary>Autopilot engaged but the pilot is not driving phases.</summary>
    Paused,
}

/// <summary>Live snapshot of everything the pilot reads from the simulator.</summary>
public sealed class AircraftState
{
    // --- Position & motion ---
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }          // MSL, feet
    public double AltitudeAGL { get; set; }       // feet
    public double Heading { get; set; }           // magnetic, degrees
    public double TrueHeading { get; set; }
    public double IndicatedAirspeed { get; set; } // knots
    public double TrueAirspeed { get; set; }
    public double Mach { get; set; }
    public double GroundSpeed { get; set; }       // knots
    public double VerticalSpeed { get; set; }     // fpm
    public double Pitch { get; set; }             // degrees
    public double Bank { get; set; }              // degrees
    public double GLoad { get; set; }

    // --- Ground / configuration ---
    public bool OnGround { get; set; }
    public bool GearDown { get; set; }
    public bool ParkingBrakeSet { get; set; }
    public double FlapsHandleIndex { get; set; }
    public double FlapsPosition { get; set; }     // degrees
    public bool StallWarning { get; set; }
    public bool OverspeedWarning { get; set; }
    public double BarberPoleKt { get; set; }

    // --- Engines & fuel ---
    public int EngineCount { get; set; }
    public bool EnginesRunning { get; set; }
    public double[] EngineN1 { get; set; } = [];
    public double[] EngineN2 { get; set; } = [];
    public double[] EngineFF { get; set; } = [];          // fuel flow, lb/h
    public double[] EngineThrottle { get; set; } = [];    // 0..1
    public double[] EngineEgtC { get; set; } = [];        // Celsius
    public double[] EngineMaxRpmPct { get; set; } = [];
    public double TotalFuelWeight { get; set; }           // lb
    public double TotalFuelQuantity { get; set; }         // gallons
    public double GrossWeight { get; set; }               // lb
    public double CGPercent { get; set; }

    // --- Environment ---
    public double WindDirection { get; set; }  // degrees, direction wind comes FROM
    public double WindVelocity { get; set; }   // knots
    public double OAT { get; set; }            // Celsius
    public double Barometer { get; set; }      // inHg
    public double PressureAltitude { get; set; }
    public double SimTimeSeconds { get; set; }
    public bool AmbientInCloud { get; set; }
    public double PrecipitationState { get; set; }
    public double StructuralIcePercent { get; set; }
    public double PitotIcePercent { get; set; }
    public bool StructuralDeiceOn { get; set; }
    public double SurfaceCondition { get; set; }
    public bool SurfaceInfoValid { get; set; }

    // --- Autopilot / flight director ---
    public AutopilotStatus Autopilot { get; set; } = new();

    // --- Navigation ---
    public NavigationState Navigation { get; set; } = new();

    // --- ATC ---
    public AtcState Atc { get; set; } = new();

    // --- Pushback tug ---
    public PushbackStatus Pushback { get; set; } = new();

    // --- Radios ---
    public string? Com1ActiveFrequency { get; set; }
    public string? Com1StandbyFrequency { get; set; }
    public string? Com2ActiveFrequency { get; set; }
    public string? Com2StandbyFrequency { get; set; }
    public string? Squawk { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public long Version { get; set; }

    public AircraftState Clone()
    {
        var c = (AircraftState)MemberwiseClone();
        c.EngineN1 = (double[])EngineN1.Clone();
        c.EngineN2 = (double[])EngineN2.Clone();
        c.EngineFF = (double[])EngineFF.Clone();
        c.EngineThrottle = (double[])EngineThrottle.Clone();
        c.EngineEgtC = (double[])EngineEgtC.Clone();
        c.EngineMaxRpmPct = (double[])EngineMaxRpmPct.Clone();
        c.Autopilot = Autopilot.Clone();
        c.Navigation = Navigation.Clone();
        c.Atc = Atc.Clone();
        c.Pushback = Pushback.Clone();
        return c;
    }
}

public sealed class AutopilotStatus
{
    public bool MasterSwitch { get; set; }
    public bool FlightDirector { get; set; }
    public bool HeadingHold { get; set; }
    public bool AltitudeHold { get; set; }
    public bool VsMode { get; set; }
    public bool IaHold { get; set; }
    public bool MachHold { get; set; }
    public bool ApproachHold { get; set; }
    public bool GlideslopeHold { get; set; }
    public bool BackcourseHold { get; set; }
    public bool YawDamper { get; set; }
    public bool LNav { get; set; }
    public bool VNav { get; set; }
    public bool AutoThrottle { get; set; }
    public bool WingLeveler { get; set; }
    public double HeadingTarget { get; set; }
    public double AltitudeTarget { get; set; }
    public double VsTarget { get; set; }
    public double IaTarget { get; set; }
    public double MachTarget { get; set; }

    public AutopilotStatus Clone() => (AutopilotStatus)MemberwiseClone();
}

public sealed class NavigationState
{
    public string? ActiveFlightPlanPath { get; set; }
    public List<FlightPlanWaypoint> Waypoints { get; set; } = new();
    public int CurrentWaypointIndex { get; set; } = -1;
    public double DistanceToNextWaypoint { get; set; }   // nm
    public double BearingToNextWaypoint { get; set; }     // degrees
    public double CrossTrackError { get; set; }           // nm
    public double VerticalDeviation { get; set; }
    public bool LocalizerAvailable { get; set; }
    public bool GlideSlopeAvailable { get; set; }
    public double LocalizerDeviation { get; set; }        // degrees
    public double GlideSlopeDeviation { get; set; }       // degrees
    public double NavSignalStrength { get; set; }
    public double NextWaypointLatitude { get; set; }
    public double NextWaypointLongitude { get; set; }
    public double NextWaypointAltitude { get; set; }
    public bool HasActiveFlightPlan => Waypoints.Count > 0 && CurrentWaypointIndex >= 0;

    // Flight plan metadata (departure/destination context)
    public string? DepartureIcao { get; set; }
    public string? ArrivalIcao { get; set; }
    public string? DepartureAirportName { get; set; }
    public string? ArrivalAirportName { get; set; }
    public string? DepartureRunway { get; set; }
    public string? ArrivalRunway { get; set; }
    public string? SidName { get; set; }
    public string? StarName { get; set; }
    public string? ApproachName { get; set; }
    public string? ApproachType { get; set; }
    public double CruiseAltitudeFt { get; set; }
    public double DepartureLatitude { get; set; }
    public double DepartureLongitude { get; set; }
    public double DepartureAltitude { get; set; }
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public double DestinationAltitude { get; set; }

    public NavigationState Clone()
    {
        var copy = (NavigationState)MemberwiseClone();
        copy.Waypoints = new List<FlightPlanWaypoint>(Waypoints);
        return copy;
    }
}

public sealed class AtcState
{
    public DateTime NativeStateUtc { get; set; }
    public bool ClearedIfr { get; set; }
    public bool ClearedTaxi { get; set; }
    public bool ClearedTakeoff { get; set; }
    public bool ClearedLanding { get; set; }
    public bool IfrFlightPlanAvailable { get; set; }
    public int AssignedAltitudeFt { get; set; }
    public string? DesignatedRunwayTakeoff { get; set; }
    public string? DesignatedRunwayLanding { get; set; }
    public string? DesignatedGateArrival { get; set; }
    public string? DesignatedGateDeparture { get; set; }
    public string? NextControllerName { get; set; }
    public double NextControllerFrequencyMHz { get; set; }
    public double NextControllerType { get; set; }
    public double NextControllerDistanceNm { get; set; }
    public string? NextWaypointName { get; set; }
    public double NextWaypointAltitudeM { get; set; }
    public bool RunwaySelected { get; set; }
    public double RunwayHeadingTrue { get; set; }
    public double RunwayLengthMeters { get; set; }
    public double RunwayWidthMeters { get; set; }
    public double RunwayDistanceMeters { get; set; }
    public double TaxiPathDistanceMeters { get; set; }
    public bool AirportIsTowered { get; set; }
    public bool DepartureAirportIsTowered { get; set; }
    public bool ArrivalAirportIsTowered { get; set; }
    public bool ArrivalAirportHasAtis { get; set; }
    public bool OnParkingSpot { get; set; }
    public double FlightPlanAltitudeDeviationM { get; set; }
    public double FlightPlanTrackDeviationM { get; set; }
    public string? AircraftIdent { get; set; }
    public string? StationIdent { get; set; }
    public string? StationType { get; set; }
    public string? StandbyStationType { get; set; }

    public List<string> MessageLog { get; set; } = new();

    public AtcState Clone()
    {
        var copy = (AtcState)MemberwiseClone();
        copy.MessageLog = new List<string>(MessageLog);
        return copy;
    }
}

public sealed class PushbackStatus
{
    public bool Attached { get; set; }
    public bool Available { get; set; }
    public bool Waiting { get; set; }
    public PushbackDirection Direction { get; set; }
    public double AngleRadians { get; set; }
    public double AngleDegrees
    {
        get
        {
            var d = AngleRadians * 180.0 / Math.PI;
            d %= 360.0;
            return d < 0 ? d + 360.0 : d;
        }
    }
    public PushbackStatus Clone() => (PushbackStatus)MemberwiseClone();
}

public enum PushbackDirection
{
    Straight = 0,
    Left = 1,
    Right = 2,
}
