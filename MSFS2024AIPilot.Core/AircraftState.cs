using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core;

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
    Landing,
    Rollout,
    TaxiIn,
    Shutdown,
    Holding,
    Divert,
    Emergency,
    GoAround
}

public enum AutopilotMode
{
    Off,
    Native,
    Direct,
    Hybrid
}

public sealed class AircraftState
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; } // MSL feet
    public double AltitudeAGL { get; set; }
    public double Heading { get; set; } // Magnetic degrees
    public double TrueHeading { get; set; }
    public double IndicatedAirspeed { get; set; } // Knots
    public double TrueAirspeed { get; set; }
    /// <summary>Mach number from AIRSPEED MACH.</summary>
    public double Mach { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; } // FPM
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double AoA { get; set; }
    public double GLoad { get; set; }
    public bool OnGround { get; set; }
    public bool GearDown { get; set; }

    /// <summary>
    /// The barber-pole airspeed the simulator computes for this aircraft (AIRSPEED BARBER
    /// POLE), in knots. It is the aircraft's own never-exceed needle and already folds in the
    /// Mach limit, the configuration and the altitude, which is why it beats a config V-speed.
    /// </summary>
    public double BarberPoleKt { get; set; }

    /// <summary>STALL WARNING: the aircraft's own stall-protection indication.</summary>
    public bool StallWarning { get; set; }

    /// <summary>OVERSPEED WARNING: the aircraft's own overspeed indication.</summary>
    public bool OverspeedWarning { get; set; }


    /// <summary>
    /// Parking brake position, from the BRAKE PARKING POSITION simulator variable. The
    /// only way to tell a set brake from a released one without guessing, because the
    /// PARKING_BRAKES key event toggles rather than sets.
    /// </summary>
    public bool ParkingBrakeSet { get; set; }

    public double FlapsPosition { get; set; } // 0-1
    public double FlapsHandleIndex { get; set; }
    public double ThrottlePosition { get; set; } // 0-1 per engine
    public double[] EngineN1 { get; set; } = [];
    public double[] EngineN2 { get; set; } = [];
    public double[] EngineFF { get; set; } = []; // Fuel flow PPH
    public double[] EngineThrottle { get; set; } = [];
    /// <summary>Exhaust gas temperature per engine, Celsius (GENERAL ENG EXHAUST GAS TEMPERATURE).</summary>
    public double[] EngineEgtC { get; set; } = [];
    /// <summary>
    /// Where each engine is against its maximum RPM as a percentage
    /// (GENERAL ENG PCT MAX RPM). 100 is the redline.
    /// </summary>
    public double[] EngineMaxRpmPct { get; set; } = [];
    public int EngineCount { get; set; }
    public bool EnginesRunning { get; set; }
    public double TotalFuelWeight { get; set; } // Lbs
    public double TotalFuelQuantity { get; set; } // Gallons
    public double GrossWeight { get; set; } // Lbs
    public double CGPercent { get; set; } // % MAC
    public double WindDirection { get; set; }
    public double WindVelocity { get; set; }
    public double OAT { get; set; } // Celsius
    public double PressureAltitude { get; set; }
    public double Barometer { get; set; } // inHg

    /// <summary>True when the aircraft is in cloud (AMBIENT IN CLOUD) - visible moisture.</summary>
    public bool AmbientInCloud { get; set; }
    /// <summary>Precipitation state (AMBIENT PRECIP STATE): 0 none, 1 rain, 2 snow, ...</summary>
    public double PrecipitationState { get; set; }
    /// <summary>Structural ice on the airframe, 0..100 (STRUCTURAL ICE PCT).</summary>
    public double StructuralIcePercent { get; set; }
    /// <summary>Ice on the pitot probe, 0..100 (PITOT ICE PCT).</summary>
    public double PitotIcePercent { get; set; }
    /// <summary>Airframe deice switch state (STRUCTURAL DEICE SWITCH).</summary>
    public bool StructuralDeiceOn { get; set; }
    /// <summary>Surface under the aircraft (SURFACE CONDITION): 0 normal, 1 wet, 2 icy, 3 snow.</summary>
    public double SurfaceCondition { get; set; }
    /// <summary>Whether SURFACE CONDITION is meaningful (SURFACE INFO VALID).</summary>
    public bool SurfaceInfoValid { get; set; }
    /// <summary>Simulator local wall-clock in seconds since midnight (SimVar LOCAL TIME).</summary>
    public double SimTimeSeconds { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Increments once per published snapshot, so readers can detect staleness.</summary>
    public long Version { get; set; }

    public AutopilotStatus Autopilot { get; set; } = new();
    public NavigationState Navigation { get; set; } = new();
    public ATCState ATC { get; set; } = new();

    /// <summary>The pushback tug, read from the PUSHBACK_* service SimVars.</summary>
    public PushbackStatus Pushback { get; set; } = new();

    /// <summary>
    /// Produces an independent copy. The nested objects are replaced with equivalent
    /// instances rather than shared, so a snapshot never aliases the live state.
    /// </summary>
    public AircraftState Clone()
    {
        return new AircraftState
        {
            Latitude = Latitude,
            Longitude = Longitude,
            Altitude = Altitude,
            AltitudeAGL = AltitudeAGL,
            Heading = Heading,
            TrueHeading = TrueHeading,
            IndicatedAirspeed = IndicatedAirspeed,
            TrueAirspeed = TrueAirspeed,
            Mach = Mach,
            GroundSpeed = GroundSpeed,
            VerticalSpeed = VerticalSpeed,
            Pitch = Pitch,
            Bank = Bank,
            AoA = AoA,
            GLoad = GLoad,
            OnGround = OnGround,
            GearDown = GearDown,
            BarberPoleKt = BarberPoleKt,
            StallWarning = StallWarning,
            OverspeedWarning = OverspeedWarning,
            ParkingBrakeSet = ParkingBrakeSet,
            FlapsPosition = FlapsPosition,
            FlapsHandleIndex = FlapsHandleIndex,
            ThrottlePosition = ThrottlePosition,
            EngineN1 = (double[])EngineN1.Clone(),
            EngineN2 = (double[])EngineN2.Clone(),
            EngineFF = (double[])EngineFF.Clone(),
            EngineThrottle = (double[])EngineThrottle.Clone(),
            EngineEgtC = (double[])EngineEgtC.Clone(),
            EngineMaxRpmPct = (double[])EngineMaxRpmPct.Clone(),
            EngineCount = EngineCount,
            EnginesRunning = EnginesRunning,
            TotalFuelWeight = TotalFuelWeight,
            TotalFuelQuantity = TotalFuelQuantity,
            GrossWeight = GrossWeight,
            CGPercent = CGPercent,
            WindDirection = WindDirection,
            WindVelocity = WindVelocity,
            OAT = OAT,
            PressureAltitude = PressureAltitude,
            Barometer = Barometer,
            AmbientInCloud = AmbientInCloud,
            PrecipitationState = PrecipitationState,
            StructuralIcePercent = StructuralIcePercent,
            PitotIcePercent = PitotIcePercent,
            StructuralDeiceOn = StructuralDeiceOn,
            SurfaceCondition = SurfaceCondition,
            SurfaceInfoValid = SurfaceInfoValid,
            SimTimeSeconds = SimTimeSeconds,
            Timestamp = Timestamp,
            Version = Version,
            Autopilot = Autopilot.Clone(),
            Navigation = Navigation.Clone(),
            ATC = ATC.Clone(),
            Pushback = Pushback.Clone()
        };
    }
}

public sealed class AutopilotStatus
{
    public bool MasterSwitch { get; set; }
    public bool FlightDirector { get; set; }
    public bool HeadingHold { get; set; }
    public bool AltitudeHold { get; set; }
    public bool VSMode { get; set; }
    public bool IASHold { get; set; }
    public bool MachHold { get; set; }
    public bool ApproachHold { get; set; }
    public bool GlideslopeHold { get; set; }
    public bool BackcourseHold { get; set; }
    public bool YawDamper { get; set; }
    public bool VNAV { get; set; }
    public bool LNAV { get; set; }
    public bool AutoThrottle { get; set; }
    public bool WingLeveler { get; set; }
    public double HeadingTarget { get; set; }
    public double AltitudeTarget { get; set; }
    public double VSTarget { get; set; }
    public double IASTarget { get; set; }
    public double MachTarget { get; set; }

    public AutopilotStatus Clone() => (AutopilotStatus)MemberwiseClone();
}

public sealed class NavigationState
{
    public string? ActiveFlightPlanPath { get; set; }
    public List<FlightPlanWaypoint> Waypoints { get; set; } = new();
    public int CurrentWaypointIndex { get; set; } = -1;
    public FlightPlanWaypoint? CurrentWaypoint => CurrentWaypointIndex >= 0 && CurrentWaypointIndex < Waypoints.Count ? Waypoints[CurrentWaypointIndex] : null;
    public FlightPlanWaypoint? NextWaypoint => CurrentWaypointIndex + 1 < Waypoints.Count ? Waypoints[CurrentWaypointIndex + 1] : null;
    public double DistanceToNextWaypoint { get; set; }
    public double BearingToNextWaypoint { get; set; }
    public double CrossTrackError { get; set; }
    public double VerticalDeviation { get; set; }
    public bool LocalizerAvailable { get; set; }
    public bool GlideSlopeAvailable { get; set; }
    public double LocalizerDeviation { get; set; }
    public double GlideSlopeDeviation { get; set; }
    public double NavSignalStrength { get; set; }
    public bool HasActiveFlightPlan => Waypoints.Count > 0 && CurrentWaypointIndex >= 0;
    public string? DepartureICAO { get; set; }
    public string? ArrivalICAO { get; set; }
    public string? DepartureAirportName { get; set; }
    public string? ArrivalAirportName { get; set; }
    public string? DepartureRunway { get; set; }
    public string? ArrivalRunway { get; set; }
    public string? SIDName { get; set; }
    public string? STARName { get; set; }
    public string? ApproachName { get; set; }
    public string? ApproachType { get; set; }
    public double CruiseAltitudeFt { get; set; }

    /// <summary>Airport reference coordinates from the plan, when present.</summary>
    public double DepartureLatitude { get; set; }
    public double DepartureLongitude { get; set; }
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public double DestinationAltitude { get; set; }
    public double DepartureAltitude { get; set; }

    /// <summary>
    /// Shallow-copies the mutable parts. The waypoint list is copied because the
    /// flight-plan loader replaces it wholesale and a snapshot taken concurrently
    /// must not observe a half-swapped list.
    /// </summary>
    public NavigationState Clone()
    {
        var copy = (NavigationState)MemberwiseClone();
        copy.Waypoints = new List<FlightPlanWaypoint>(Waypoints);
        return copy;
    }
}

public sealed class FlightPlanWaypoint
{
    public string Ident { get; set; } = "";
    public string? Region { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; } // MSL feet
    public WaypointType Type { get; set; }
    public string? Airway { get; set; }
    public double SpeedConstraint { get; set; } // Knots, 0 = none
    public double AltitudeConstraint { get; set; } // Feet, 0 = none
    public bool IsFlyOver { get; set; }
    public double DistanceFromPrevious { get; set; }

    /// <summary>Runway this waypoint belongs to, normalised to the "25L" form.</summary>
    public string? Runway { get; set; }

    /// <summary>True when the source plan carried a position for this waypoint.</summary>
    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude);
}

public enum WaypointType
{
    Airport,
    NDB,
    VOR,
    Intersection,
    Runway,
    User,
    SID,
    STAR,
    Approach,
    MissedApproach
}

public sealed class ATCState
{
    public string? CurrentFrequency { get; set; }
    public string? MonitoringFrequency { get; set; }
    public string? ATISLetter { get; set; }
    public string? ATISInfo { get; set; }
    public string? Clearance { get; set; }
    public string? Squawk { get; set; }
    public string? ControllerName { get; set; }
    public string? ControllerType { get; set; } // DEL, GND, TWR, DEP, APP, CTR
    public List<ATCMessage> MessageLog { get; set; } = new();
    public bool CPDLCConnected { get; set; }
    public string? CPDLCCurrentCenter { get; set; }
    public List<CPDLCMessage> CPDLCMessages { get; set; } = new();

    /// <summary>Identity of the station tuned on COM1, e.g. "LEBL_GND" (COM ACTIVE FREQ IDENT:1).</summary>
    public string? StationIdent { get; set; }
    /// <summary>Type of the station tuned on COM1: ATIS, UNI, CTAF, GND, TWR, CLR, APPR, DEP, FSS, AWS.</summary>
    public string? StationType { get; set; }
    /// <summary>Type of the station on the COM1 standby side.</summary>
    public string? StandbyStationType { get; set; }

    // --- Default (sim-native) ATC state, read from the ATC SimVars ---
    // The native ATC only speaks and offers a numbered menu, but every clearance it
    // issues is also published as a SimVar, so the pilot can both follow the
    // instructions and detect the handoffs without parsing audio.
    public DateTime NativeStateUtc { get; set; }
    public bool ClearedIfr { get; set; }
    public bool ClearedTaxi { get; set; }
    public bool ClearedTakeoff { get; set; }
    public bool ClearedLanding { get; set; }
    /// <summary>An IFR flight plan is loaded and can be cleared at the current airport.</summary>
    public bool IFRFlightPlanAvailable { get; set; }
    public int AssignedAltitudeFt { get; set; }
    public string? DesignatedRunwayTakeoff { get; set; }
    public string? DesignatedRunwayLanding { get; set; }
    public string? DesignatedGateArrival { get; set; }
    public string? DesignatedGateDeparture { get; set; }
    /// <summary>Station the native ATC will hand the aircraft to next (ATC FUTURE AGENT NAME).</summary>
    public string? NextControllerName { get; set; }
    public double NextControllerFrequencyMHz { get; set; }
    public double NextControllerType { get; set; }
    public double NextControllerDistanceNM { get; set; }
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

    /// <summary>Registration the sim's ATC addresses the aircraft by (ATC ID).</summary>
    public string? AircraftIdent { get; set; }

    public ATCState Clone()
    {
        var copy = (ATCState)MemberwiseClone();
        copy.MessageLog = new List<ATCMessage>(MessageLog);
        copy.CPDLCMessages = new List<CPDLCMessage>(CPDLCMessages);
        return copy;
    }
}

/// <summary>Which way a pushback is being flown, as the simulator enumerates it.</summary>
public enum PushbackDirection
{
    Straight = 0,
    Left = 1,
    Right = 2
}

/// <summary>
/// The pushback tug, from the airport-services SimVars the SDK publishes for it. These are
/// the only signals a pushback offers an external program: there is no clearance SimVar, so
/// "approved" is observed as the tug attaching and "complete" as it letting go.
/// </summary>
public sealed class PushbackStatus
{
    /// <summary>PUSHBACK ATTACHED: the tug is attached to the aircraft.</summary>
    public bool Attached { get; set; }
    /// <summary>PUSHBACK AVAILABLE: a pushback is offered on the parking space the aircraft is on.</summary>
    public bool Available { get; set; }
    /// <summary>PUSHBACK WAIT: the simulator is waiting for the pushback to begin.</summary>
    public bool Waiting { get; set; }
    /// <summary>PUSHBACK STATE:index - the manoeuvre the tug is flying.</summary>
    public PushbackDirection Direction { get; set; }
    /// <summary>PUSHBACK ANGLE: the tug's heading, in radians.</summary>
    public double AngleRadians { get; set; }

    /// <summary>The tug's heading in compass degrees, normalised into [0, 360).</summary>
    public double AngleDegrees
    {
        get
        {
            var degrees = AngleRadians * 180.0 / Math.PI;
            degrees %= 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }
    }

    public PushbackStatus Clone() => (PushbackStatus)MemberwiseClone();
}

public sealed class ATCMessage
{
    public DateTime Time { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsReadback { get; set; }
}

public sealed class CPDLCMessage
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = ""; // REQUEST, REPORT, CLEARANCE, etc.
    public string Text { get; set; } = "";
    public string Status { get; set; } = ""; // SENT, RECEIVED, ACKNOWLEDGED
    public string? Reference { get; set; }
}