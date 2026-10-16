using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

public sealed class AircraftController
{
    /// <summary>Highest engine index requested; the sim reports the real count at runtime.</summary>
    public const int MaxEngines = 4;

    /// <summary>An engine is considered running when N1 exceeds the idle threshold.</summary>
    private static bool EngineRunning(double[] n1) => n1.Any(n => n > 5);
    private readonly SimConnectManager _sc = new();
    private AircraftState _state = new();
    private string _aircraftTitle = "";
    private readonly Dictionary<uint, string> _requestMap = new();

    private uint _reqPosition, _reqAttitude, _reqSpeeds, _reqEngines;
    private uint _reqEngineLimits;
    private uint _reqFuel, _reqAutopilot, _reqEnvironment, _reqNavigation, _reqNavRadio;
    private uint _reqSystems, _reqApModes, _reqCom;
    private uint _reqAtc, _reqAtcText;
    private uint _reqWeather, _reqPushback;
    /// <summary>Definition ID of the ATC string block; string responses carry no request name.</summary>
    private uint _atcTextDefineId;

    public event Action<AircraftState>? OnStateUpdated;

    public AircraftState State => _state;
    public SimConnectManager SimConnect => _sc;
    public string AircraftTitle => _aircraftTitle;

    public async System.Threading.Tasks.Task StartAsync()
    {
        await _sc.ConnectAsync();
        SetupDataDefinitions();

        // Ask the sim for the loaded aircraft title once connected
        _sc.SubscribeToSystemEvent(EVENT_USER_AIRCRAFT_LOADED, "AircraftLoaded");
        _sc.RequestUserAircraftTitle(REQUEST_USER_AIRCRAFT_TITLE);
    }

    private const uint REQUEST_USER_AIRCRAFT_TITLE = 90001;
    private const uint EVENT_USER_AIRCRAFT_LOADED = 90002;

    public event Action<string>? OnAircraftTitleChanged;

    private void OnUserAircraftTitle(SystemStateData data)
    {
        // RequestSystemState is a generic channel: only the response carrying our own
        // request id is the aircraft title. Accepting any non-empty string retitles the
        // aircraft with whatever else a system-state request returns.
        if (data.RequestId != REQUEST_USER_AIRCRAFT_TITLE) return;
        if (!string.IsNullOrWhiteSpace(data.String) && data.String != _aircraftTitle)
        {
            _aircraftTitle = data.String;
            OnAircraftTitleChanged?.Invoke(_aircraftTitle);
        }
    }

    public void Stop()
    {
        _sc.Disconnect();
    }

    public void Dispose()
    {
        _sc.Dispose();
    }

    /// <summary>
    /// Every data block, with its datums in the exact order <see cref="OnDataReceived"/>
    /// indexes them: the payload of a SimConnect response arrives in declaration order,
    /// so this table is the single source of truth for both the definition that is
    /// registered with the sim and the decoder that reads the response.
    /// </summary>
    internal static readonly (string Block, string Name, string Units)[] DataLayout = BuildDataLayout();

    private static (string Block, string Name, string Units)[] BuildDataLayout()
    {
        var layout = new List<(string, string, string)>
        {
            ("POSITION", "PLANE LATITUDE", "degrees"),
            ("POSITION", "PLANE LONGITUDE", "degrees"),
            ("POSITION", "PLANE ALTITUDE", "feet"),
            ("POSITION", "PLANE ALT ABOVE GROUND", "feet"),

            ("ATTITUDE", "PLANE HEADING DEGREES MAGNETIC", "degrees"),
            ("ATTITUDE", "PLANE HEADING DEGREES TRUE", "degrees"),
            ("ATTITUDE", "PLANE PITCH DEGREES", "degrees"),
            ("ATTITUDE", "PLANE BANK DEGREES", "degrees"),

            ("SPEEDS", "AIRSPEED INDICATED", "knots"),
            ("SPEEDS", "AIRSPEED TRUE", "knots"),
            ("SPEEDS", "AIRSPEED MACH", "number"),
            ("SPEEDS", "GROUND VELOCITY", "knots"),
            ("SPEEDS", "VERTICAL SPEED", "feet per minute"),
        };

        // Engine data is requested per engine index so twin- and four-engine aircraft
        // report their real values rather than duplicating engine one on every slot.
        layout.Add(("ENGINES", "NUMBER OF ENGINES", "number"));
        for (uint engine = 1; engine <= MaxEngines; engine++)
        {
            layout.Add(("ENGINES", $"GENERAL ENG COMBUSTION:{engine}", "bool"));
            layout.Add(("ENGINES", $"GENERAL ENG RPM:{engine}", "rpm"));
            layout.Add(("ENGINES", $"TURB ENG N1:{engine}", "percent"));
            layout.Add(("ENGINES", $"TURB ENG N2:{engine}", "percent"));
            layout.Add(("ENGINES", $"TURB ENG FUEL FLOW PPH:{engine}", "pounds per hour"));
            layout.Add(("ENGINES", $"GENERAL ENG THROTTLE LEVER POSITION:{engine}", "percent"));
        }

        // Powerplant limits, kept in their own block rather than added to ENGINES. The
        // engine state above is what every phase of flight reads, so a datum name this
        // build rejects must not be able to take it down: SimConnect answers an
        // unrecognised name with NAME_UNRECOGNIZED for the whole definition, and an
        // empty ENGINES block would report engine count zero on a running aircraft.
        layout.Add(("ENGINE_LIMITS", "NUMBER OF ENGINES", "number"));
        for (uint engine = 1; engine <= MaxEngines; engine++)
        {
            layout.Add(("ENGINE_LIMITS", $"GENERAL ENG EXHAUST GAS TEMPERATURE:{engine}", "celsius"));
            layout.Add(("ENGINE_LIMITS", $"GENERAL ENG PCT MAX RPM:{engine}", "percent"));
        }

        layout.AddRange(new[]
        {
            ("FUEL", "FUEL TOTAL QUANTITY WEIGHT", "pounds"),
            ("FUEL", "FUEL TOTAL QUANTITY", "gallons"),
            ("FUEL", "FUEL TOTAL CAPACITY", "gallons"),
            ("FUEL", "TOTAL WEIGHT", "pounds"),
            ("FUEL", "EMPTY WEIGHT", "pounds"),
            ("FUEL", "CG PERCENT", "percent"),
            ("FUEL", "MAX GROSS WEIGHT", "pounds"),

            ("AUTOPILOT", "AUTOPILOT MASTER", "bool"),
            ("AUTOPILOT", "AUTOPILOT HEADING LOCK", "bool"),
            ("AUTOPILOT", "AUTOPILOT HEADING LOCK DIR", "degrees"),
            ("AUTOPILOT", "AUTOPILOT ALTITUDE LOCK", "bool"),
            ("AUTOPILOT", "AUTOPILOT ALTITUDE LOCK VAR", "feet"),
            ("AUTOPILOT", "AUTOPILOT VERTICAL HOLD VAR", "feet per minute"),
            ("AUTOPILOT", "AUTOPILOT AIRSPEED HOLD VAR", "knots"),
            ("AUTOPILOT", "AUTOPILOT MACH HOLD VAR", "number"),

            ("ENVIRONMENT", "AMBIENT WIND DIRECTION", "degrees"),
            ("ENVIRONMENT", "AMBIENT WIND VELOCITY", "knots"),
            ("ENVIRONMENT", "AMBIENT TEMPERATURE", "celsius"),
            ("ENVIRONMENT", "AMBIENT PRESSURE", "inHg"),
            ("ENVIRONMENT", "PRESSURE ALTITUDE", "feet"),
            ("ENVIRONMENT", "LOCAL TIME", "seconds"),

            ("NAVIGATION", "GPS WP DISTANCE", "nautical miles"),
            ("NAVIGATION", "GPS WP ETE", "seconds"),
            ("NAVIGATION", "GPS WP CROSS TRK", "nautical miles"),
            ("NAVIGATION", "GPS WP BEARING", "degrees"),
            ("NAVIGATION", "GPS WP NEXT LAT", "degrees"),
            ("NAVIGATION", "GPS WP NEXT LON", "degrees"),
            ("NAVIGATION", "GPS WP NEXT ALT", "feet"),
            ("NAVIGATION", "GPS FLIGHT PLAN WP INDEX", "number"),
        });

        // Navigation sensors for ILS tracking. These are documented without an index
        // (see the Radio Navigation SimVars reference), and "NAV TO FROM" is not a
        // simvar at all - the documented name is NAV TOFROM.
        layout.AddRange(new[]
        {
            ("NAVRADIO", "NAV HAS LOCALIZER", "bool"),
            ("NAVRADIO", "NAV HAS GLIDE SLOPE", "bool"),
            ("NAVRADIO", "NAV LOCALIZER", "degrees"),
            ("NAVRADIO", "NAV GLIDE SLOPE ERROR", "degrees"),
            ("NAVRADIO", "NAV RADIAL ERROR", "degrees"),
            ("NAVRADIO", "NAV SIGNAL", "number"),
            ("NAVRADIO", "NAV TOFROM", "bool"),
        });

        layout.AddRange(new[]
        {
            ("SYSTEMS", "SIM ON GROUND", "bool"),
            ("SYSTEMS", "GEAR HANDLE POSITION", "bool"),
            ("SYSTEMS", "GEAR TOTAL PCT EXTENDED", "percent"),
            ("SYSTEMS", "BRAKE PARKING POSITION", "bool"),
            ("SYSTEMS", "FLAPS HANDLE INDEX", "number"),
            ("SYSTEMS", "FLAPS NUM HANDLE POSITIONS", "number"),
            ("SYSTEMS", "TRAILING EDGE FLAPS LEFT ANGLE", "degrees"),
            ("SYSTEMS", "STALL WARNING", "bool"),
            ("SYSTEMS", "OVERSPEED WARNING", "bool"),
            ("SYSTEMS", "AIRSPEED BARBER POLE", "knots"),

            ("APMODES", "AUTOPILOT FLIGHT DIRECTOR ACTIVE", "bool"),
            ("APMODES", "AUTOPILOT VERTICAL HOLD", "bool"),
            ("APMODES", "AUTOPILOT AIRSPEED HOLD", "bool"),
            ("APMODES", "AUTOPILOT MACH HOLD", "bool"),
            ("APMODES", "AUTOPILOT APPROACH HOLD", "bool"),
            ("APMODES", "AUTOPILOT GLIDESLOPE HOLD", "bool"),
            ("APMODES", "AUTOPILOT BACKCOURSE HOLD", "bool"),
            ("APMODES", "AUTOPILOT YAW DAMPER", "bool"),
            ("APMODES", "AUTOPILOT NAV1 LOCK", "bool"),
            ("APMODES", "AUTOPILOT THROTTLE ARM", "bool"),
            ("APMODES", "AUTOPILOT WING LEVELER", "bool"),

            ("COM", "COM ACTIVE FREQUENCY:1", "mhz"),
            ("COM", "COM STANDBY FREQUENCY:1", "mhz"),
            ("COM", "COM ACTIVE FREQUENCY:2", "mhz"),
            ("COM", "COM STANDBY FREQUENCY:2", "mhz"),
            ("COM", "TRANSPONDER CODE:1", "number"),
        });

        // Default-ATC state. The sim's own ATC has no text API, but every clearance it
        // issues is published as a SimVar, which is a more reliable instruction source
        // than speech: no parsing, no latency. Values are updated once per second.
        layout.AddRange(new[]
        {
            ("ATC", "ATC CLEARED IFR", "bool"),
            ("ATC", "ATC CLEARED TAXI", "bool"),
            ("ATC", "ATC CLEARED TAKEOFF", "bool"),
            ("ATC", "ATC CLEARED LANDING", "bool"),
            ("ATC", "ATC IFR FP TO REQUEST", "bool"),
            ("ATC", "ATC ASSIGNED ALTITUDE", "feet"),
            ("ATC", "ATC RUNWAY SELECTED", "bool"),
            ("ATC", "ATC RUNWAY HEADING DEGREES TRUE", "degrees"),
            ("ATC", "ATC RUNWAY LENGTH", "meters"),
            ("ATC", "ATC RUNWAY WIDTH", "meters"),
            ("ATC", "ATC RUNWAY DISTANCE", "meters"),
            ("ATC", "ATC TAXIPATH DISTANCE", "meters"),
            ("ATC", "ATC FUTURE AGENT FREQUENCY", "number"),
            ("ATC", "ATC FUTURE AGENT DISTANCE", "number"),
            ("ATC", "ATC FUTURE AGENT TYPE", "number"),
            ("ATC", "ATC NEXT WAYPOINT ALTITUDE", "meters"),
            ("ATC", "ATC AIRPORT IS TOWERED", "bool"),
            ("ATC", "ATC DEPARTURE AIRPORT IS TOWERED", "bool"),
            ("ATC", "ATC ARRIVAL AIRPORT IS TOWERED", "bool"),
            ("ATC", "ATC ARRIVAL AIRPORT HAS ATIS", "bool"),
            ("ATC", "ATC ON PARKING SPOT", "bool"),
            ("ATC", "ATC FLIGHTPLAN DIFF ALT", "meters"),
            ("ATC", "ATC FLIGHTPLAN DIFF DISTANCE", "meters"),
        });

        // Environment the aircraft reacts to: normal load factor for turbulence, the
        // moisture/icing signals, and the surface under the wheels. Requested per visual
        // frame because the load factor has to be sampled quickly to see a gust.
        layout.AddRange(new[]
        {
            ("WEATHER", "ACCELERATION BODY Y", "feet per second squared"),
            ("WEATHER", "AMBIENT IN CLOUD", "bool"),
            ("WEATHER", "AMBIENT PRECIP STATE", "number"),
            ("WEATHER", "STRUCTURAL ICE PCT", "percent"),
            ("WEATHER", "PITOT ICE PCT", "percent"),
            ("WEATHER", "STRUCTURAL DEICE SWITCH", "bool"),
            ("WEATHER", "SURFACE CONDITION", "number"),
            ("WEATHER", "SURFACE INFO VALID", "bool"),
        });

        // The pushback tug, from the airport-services SimVars. Their own block, so a name
        // this build has wrong leaves the pushback simply unknown - no request is made and
// the aircraft taxis out under its own power - rather than disturbing the blocks
        // every phase reads. The state SimVar is indexed because the SDK documents it that
        // way; the datum carries no unit (the MSFS 2024 docs list none for it).
        layout.AddRange(new (string block, string datum, string unit)[]
        {
            ("PUSHBACK", "PUSHBACK ATTACHED", "bool"),
            ("PUSHBACK", "PUSHBACK AVAILABLE", "bool"),
            ("PUSHBACK", "PUSHBACK WAIT", "bool"),
            ("PUSHBACK", "PUSHBACK STATE:1", ""),
            ("PUSHBACK", "PUSHBACK ANGLE", "radians"),
        });

        return layout.ToArray();
    }

    /// <summary>
    /// String-valued SimVars, declared as one definition. SimConnect returns these as
    /// fixed-width text fields, so they cannot share a definition with the numeric
    /// datums above; see <see cref="SimConnectManager.CreateStringDataDefinition"/>.
    /// String datums carry no unit - the SDK requires an empty UnitsName, and a unit
    /// such as "string" is rejected as an unrecognised name.
    /// </summary>
    internal static readonly (string Name, DatumType Size)[] AtcStringLayout =
    {
        ("ATC DESIGNATED RUNWAY TAKEOFF", DatumType.String32),
        ("ATC DESIGNATED RUNWAY LANDING", DatumType.String32),
        ("ATC DESIGNATED GATE ARRIVAL", DatumType.String32),
        ("ATC DESIGNATED GATE DEPARTURE", DatumType.String32),
        ("ATC FUTURE AGENT NAME", DatumType.String32),
        ("ATC NEXT WAYPOINT NAME", DatumType.String32),
        ("ATC ID", DatumType.String32),
        ("COM ACTIVE FREQ IDENT:1", DatumType.String32),
        ("COM ACTIVE FREQ TYPE:1", DatumType.String32),
    };

    /// <summary>
    /// Registers one data definition per block and returns its ID to request. Every
    /// datum of a block must be added to the same definition ID: SimConnect assembles
    /// a definition from repeated SimConnect_AddToDataDefinition calls sharing an ID,
    /// so allocating an ID per datum leaves each definition holding a single variable
    /// and the rest of the block is never requested.
    /// </summary>
    private uint DefineBlock(string block)
    {
        var defineId = _sc.CreateDataDefinition();
        foreach (var (_, name, units) in DataLayout.Where(d => d.Block == block))
        {
            _sc.AddToDataDefinition(defineId, name, units);
        }
        return defineId;
    }

    private void SetupDataDefinitions()
    {
        if (!_sc.IsConnected) return;

        _reqPosition = _sc.RequestData(DefineBlock("POSITION"), period: Period.VisualFrame);
        _reqAttitude = _sc.RequestData(DefineBlock("ATTITUDE"), period: Period.VisualFrame);
        _reqSpeeds = _sc.RequestData(DefineBlock("SPEEDS"), period: Period.VisualFrame);
        _reqEngines = _sc.RequestData(DefineBlock("ENGINES"), period: Period.Second);
        _reqEngineLimits = _sc.RequestData(DefineBlock("ENGINE_LIMITS"), period: Period.Second);
        _reqFuel = _sc.RequestData(DefineBlock("FUEL"), period: Period.Second);
        _reqAutopilot = _sc.RequestData(DefineBlock("AUTOPILOT"), period: Period.VisualFrame);
        _reqEnvironment = _sc.RequestData(DefineBlock("ENVIRONMENT"), period: Period.Second);
        _reqNavigation = _sc.RequestData(DefineBlock("NAVIGATION"), period: Period.VisualFrame);
        _reqNavRadio = _sc.RequestData(DefineBlock("NAVRADIO"), period: Period.VisualFrame);
        _reqSystems = _sc.RequestData(DefineBlock("SYSTEMS"), period: Period.VisualFrame);
        _reqApModes = _sc.RequestData(DefineBlock("APMODES"), period: Period.VisualFrame);
        _reqCom = _sc.RequestData(DefineBlock("COM"), period: Period.Second);
        _reqAtc = _sc.RequestData(DefineBlock("ATC"), period: Period.Second);
        _reqWeather = _sc.RequestData(DefineBlock("WEATHER"), period: Period.VisualFrame);
        _reqPushback = _sc.RequestData(DefineBlock("PUSHBACK"), period: Period.Second);
        _atcTextDefineId = _sc.CreateStringDataDefinition(AtcStringLayout);
        _reqAtcText = _sc.RequestData(_atcTextDefineId, period: Period.Second);

        _requestMap[_reqPosition] = "POSITION";
        _requestMap[_reqAttitude] = "ATTITUDE";
        _requestMap[_reqSpeeds] = "SPEEDS";
        _requestMap[_reqEngines] = "ENGINES";
        _requestMap[_reqEngineLimits] = "ENGINE_LIMITS";
        _requestMap[_reqFuel] = "FUEL";
        _requestMap[_reqAutopilot] = "AUTOPILOT";
        _requestMap[_reqEnvironment] = "ENVIRONMENT";
        _requestMap[_reqNavigation] = "NAVIGATION";
        _requestMap[_reqNavRadio] = "NAVRADIO";
        _requestMap[_reqSystems] = "SYSTEMS";
        _requestMap[_reqApModes] = "APMODES";
        _requestMap[_reqCom] = "COM";
        _requestMap[_reqAtc] = "ATC";
        _requestMap[_reqWeather] = "WEATHER";
        _requestMap[_reqPushback] = "PUSHBACK";

        _sc.OnDataReceived += OnDataReceived;
        _sc.OnStringDataReceived += OnStringDataReceived;
        _sc.OnSystemState += OnUserAircraftTitle;
        _sc.OnError += err => Console.Error.WriteLine($"[SimConnect] {err}");
    }

    /// <summary>
    /// Decodes one data request response into the live state. Runs on the control loop
    /// thread from <see cref="Dispatch"/>, so it is the only writer; readers go through
    /// <see cref="GetSnapshot"/>.
    /// </summary>
    private void OnDataReceived(SimObjectData data)
    {
        if (!_requestMap.TryGetValue(data.RequestId, out var blockName)) return;

        var v = data.Values;
        double D(int i) => i < v.Length && v[i] is double d ? d : 0;

        lock (_stateLock)
        {
            switch (blockName)
            {
                case "POSITION":
                    _state.Latitude = D(0);
                    _state.Longitude = D(1);
                    _state.Altitude = D(2);
                    _state.AltitudeAGL = D(3);
                    break;
                case "ATTITUDE":
                    _state.Heading = D(0);
                    _state.TrueHeading = D(1);
                    _state.Pitch = D(2);
                    _state.Bank = D(3);
                    break;
                case "SPEEDS":
                    _state.IndicatedAirspeed = D(0);
                    _state.TrueAirspeed = D(1);
                    _state.Mach = D(2);
                    _state.GroundSpeed = D(3);
                    _state.VerticalSpeed = D(4);
                    break;
            case "ENGINES":
                {
                    _state.EngineCount = (int)Math.Round(D(0));
                    int count = Math.Max(1, _state.EngineCount);

                    // 6 datums per engine after the leading engine count: the first is
                    // GENERAL ENG COMBUSTION (the type-independent "is it running" flag),
                    // then RPM, N1, N2, fuel flow and throttle position.
                    var combustion = new double[count];
                    var n1 = new double[count];
                    var n2 = new double[count];
                    var ff = new double[count];
                    var throttle = new double[count];

                    for (int e = 0; e < count && e < MaxEngines; e++)
                    {
                        int baseIndex = 1 + e * 6;
                        combustion[e] = D(baseIndex);
                        n1[e] = D(baseIndex + 2);
                        n2[e] = D(baseIndex + 3);
                        ff[e] = D(baseIndex + 4);
                        throttle[e] = D(baseIndex + 5);
                    }

                    _state.EngineN1 = n1;
                    _state.EngineN2 = n2;
                    _state.EngineFF = ff;
                    _state.EngineThrottle = throttle;
                    // N1 is a turbine figure and reads zero on a piston, so keying
                    // "running" off N1 alone pins every recip in Preflight forever. The
                    // combustion flag the block already carries works for both.
                    _state.EnginesRunning = combustion.Any(c => c > 0.5) || EngineRunning(n1);
                    break;
                }
                case "ENGINE_LIMITS":
                    {
                        // Two datums per engine after the leading engine count: exhaust gas
                        // temperature and where the engine is against its maximum RPM.
                        //
                        // The count is this block's own leading datum, not the ENGINES
                        // block's cached field: the two travel on different periods, and
                        // reading the other block's copy here meant that if its definition
                        // were ever rejected - the very reason this block is separate - a
                        // twin reported only engine 1's EGT and RPM, so an engine-2
                        // exceedance was invisible to EnvelopeGuard.
                        var ownCount = (int)Math.Round(D(0));
                        var count = Math.Max(1, ownCount > 0 ? ownCount : _state.EngineCount);
                        var egt = new double[count];
                        var maxRpm = new double[count];

                        for (int e = 0; e < count && e < MaxEngines; e++)
                        {
                            int baseIndex = 1 + e * 2;
                            egt[e] = D(baseIndex);
                            maxRpm[e] = D(baseIndex + 1);
                        }

                        _state.EngineEgtC = egt;
                        _state.EngineMaxRpmPct = maxRpm;
                        break;
                    }
            case "FUEL":
                _state.TotalFuelWeight = D(0);
                _state.TotalFuelQuantity = D(1);
                _state.GrossWeight = D(3);
                _state.CGPercent = D(5);
                break;
            case "AUTOPILOT":
                _state.Autopilot.MasterSwitch = D(0) > 0.5;
                _state.Autopilot.HeadingHold = D(1) > 0.5;
                _state.Autopilot.HeadingTarget = D(2);
                _state.Autopilot.AltitudeHold = D(3) > 0.5;
                _state.Autopilot.AltitudeTarget = D(4);
                _state.Autopilot.VSTarget = D(5);
                _state.Autopilot.IASTarget = D(6);
                _state.Autopilot.MachTarget = D(7);
                break;
            case "ENVIRONMENT":
                _state.WindDirection = D(0);
                _state.WindVelocity = D(1);
                _state.OAT = D(2);
                _state.Barometer = D(3);
                _state.PressureAltitude = D(4);
                _state.SimTimeSeconds = D(5);
                break;
            case "NAVIGATION":
                _state.Navigation.DistanceToNextWaypoint = D(0);
                _state.Navigation.CrossTrackError = D(2);
                _state.Navigation.BearingToNextWaypoint = D(3);
                _state.Navigation.CurrentWaypointIndex = (int)D(7);
                break;
            case "NAVRADIO":
                _state.Navigation.LocalizerAvailable = D(0) > 0.5;
                _state.Navigation.GlideSlopeAvailable = D(1) > 0.5;
                _state.Navigation.LocalizerDeviation = D(2);
                _state.Navigation.GlideSlopeDeviation = D(3);
                _state.Navigation.NavSignalStrength = D(5);
                break;
                case "SYSTEMS":
                    _state.OnGround = D(0) > 0.5;
                    _state.GearDown = D(1) > 0.5;
                    _state.ParkingBrakeSet = D(3) > 0.5;
                    _state.FlapsHandleIndex = D(4);
                    // The aircraft's own limit indications. The barber pole is the
                    // never-exceed needle this airframe is displaying, which is the one
                    // speed limit that already accounts for Mach, configuration and
                    // altitude; the two warnings are what the aircraft itself says about
                    // being outside its envelope.
                    _state.StallWarning = D(7) > 0.5;
                    _state.OverspeedWarning = D(8) > 0.5;
                    _state.BarberPoleKt = D(9);
                    break;
            case "APMODES":
                _state.Autopilot.FlightDirector = D(0) > 0.5;
                _state.Autopilot.VSMode = D(1) > 0.5;
                _state.Autopilot.IASHold = D(2) > 0.5;
                _state.Autopilot.MachHold = D(3) > 0.5;
                _state.Autopilot.ApproachHold = D(4) > 0.5;
                _state.Autopilot.GlideslopeHold = D(5) > 0.5;
                _state.Autopilot.BackcourseHold = D(6) > 0.5;
                _state.Autopilot.YawDamper = D(7) > 0.5;
                _state.Autopilot.LNAV = D(8) > 0.5;
                _state.Autopilot.AutoThrottle = D(9) > 0.5;
                _state.Autopilot.WingLeveler = D(10) > 0.5;
                break;
            case "COM":
                _state.ATC.CurrentFrequency = FormatFrequency(D(0));
                _state.ATC.MonitoringFrequency = FormatFrequency(D(2));
                _state.ATC.Squawk = D(4).ToString("0000");
                break;
            case "ATC":
                {
                    var atc = _state.ATC;
                    atc.ClearedIfr = D(0) > 0.5;
                    atc.ClearedTaxi = D(1) > 0.5;
                    atc.ClearedTakeoff = D(2) > 0.5;
                    atc.ClearedLanding = D(3) > 0.5;
                    atc.IFRFlightPlanAvailable = D(4) > 0.5;
                    atc.AssignedAltitudeFt = (int)D(5);
                    atc.RunwaySelected = D(6) > 0.5;
                    atc.RunwayHeadingTrue = D(7);
                    atc.RunwayLengthMeters = D(8);
                    atc.RunwayWidthMeters = D(9);
                    atc.RunwayDistanceMeters = D(10);
                    atc.TaxiPathDistanceMeters = D(11);
                    // ATC FUTURE AGENT FREQUENCY arrives in hertz ("tuning COM1 to
                    // 119450000.000" in the handoff log): 119450000 Hz is Barcelona
                    // Control on 119.450 MHz. Everything downstream of the state speaks
                    // MHz, so the conversion happens here, at the decode boundary.
                    atc.NextControllerFrequencyMHz = D(12) / 1_000_000.0;
                    atc.NextControllerDistanceNM = D(13);
                    atc.NextControllerType = D(14);
                    atc.NextWaypointAltitudeM = D(15);
                    atc.AirportIsTowered = D(16) > 0.5;
                    atc.DepartureAirportIsTowered = D(17) > 0.5;
                    atc.ArrivalAirportIsTowered = D(18) > 0.5;
                    atc.ArrivalAirportHasAtis = D(19) > 0.5;
                    atc.OnParkingSpot = D(20) > 0.5;
                    atc.FlightPlanAltitudeDeviationM = D(21);
                    atc.FlightPlanTrackDeviationM = D(22);
                    // "None" and -1 are the sim's way of saying "nothing assigned"; do not
                    // carry them into the pilot state or the hub treats them as new data.
                    atc.NativeStateUtc = DateTime.UtcNow;
                    break;
                }
                case "WEATHER":
                    // Normal load factor in g: the accelerometer's vertical reading is about
                    // one gravity in level flight, so dividing the ft/s² figure by standard
                    // gravity gives units G FORCE reports and the turbulence estimate expects.
                    _state.GLoad = D(0) / WeatherModel.StandardGravityFtPerSec2;
                    _state.AmbientInCloud = D(1) > 0.5;
                    _state.PrecipitationState = D(2);
                    _state.StructuralIcePercent = D(3);
                    _state.PitotIcePercent = D(4);
                    _state.StructuralDeiceOn = D(5) > 0.5;
                    _state.SurfaceCondition = D(6);
                    _state.SurfaceInfoValid = D(7) > 0.5;
                    break;
                case "PUSHBACK":
                    // The tug is a ground service, so its state is a ground service's:
                    // attached when the towhead is on the aircraft, available when the
                    // parking space offers one at all. The state SimVar is the documented
                    // enum - straight, left or right - and is left alone when the
                    // simulator reports something outside it.
                    _state.Pushback.Attached = D(0) > 0.5;
                    _state.Pushback.Available = D(1) > 0.5;
                    _state.Pushback.Waiting = D(2) > 0.5;
                    _state.Pushback.Direction = Enum.IsDefined(typeof(PushbackDirection), (int)Math.Round(D(3)))
                        ? (PushbackDirection)(int)Math.Round(D(3))
                        : PushbackDirection.Straight;
                    _state.Pushback.AngleRadians = D(4);
                    break;
            }

            _state.Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Decodes the string-valued ATC SimVars. A field the sim reports as "None" is
    /// normalised to null so callers can test for an assignment with a single check.
    /// </summary>
    private void OnStringDataReceived(StringSimObjectData data)
    {
        if (data.DefineId != _atcTextDefineId) return;

        string S(int i) => i < data.Values.Length ? data.Values[i] : "";
        string? OrNull(int i)
        {
            var value = S(i);
            return string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }

        lock (_stateLock)
        {
            var atc = _state.ATC;
            atc.DesignatedRunwayTakeoff = OrNull(0);
            atc.DesignatedRunwayLanding = OrNull(1);
            atc.DesignatedGateArrival = OrNull(2);
            atc.DesignatedGateDeparture = OrNull(3);
            atc.NextControllerName = OrNull(4);
            atc.NextWaypointName = OrNull(5);
            atc.AircraftIdent = OrNull(6);
            atc.StationIdent = OrNull(7);
            atc.StationType = OrNull(8);
            atc.NativeStateUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Formats a MHz frequency as "xxx.yyy". Built from integers so the result cannot
    /// depend on the current thread culture.
    /// </summary>
    private static string FormatFrequency(double mhz)
    {
        var asInt = (uint)(mhz * 1000);
        return $"{(asInt / 1000)}.{(asInt % 1000):D3}";
    }

    private readonly object _stateLock = new();

    /// <summary>
    /// Returns a consistent copy of the live state. The SimConnect dispatch callback
    /// writes field values from the control loop thread while the HTTP bridge and the
    /// panel read the state from their own threads, so readers take a snapshot rather
    /// than racing on individual fields.
    /// </summary>
    public AircraftState GetSnapshot()
    {
        lock (_stateLock)
        {
            return _state.Clone();
        }
    }

    public void Dispatch()
    {
        _sc.Dispatch();

        // Publish whatever the callbacks decoded, atomically, then notify.
        long version;
        lock (_stateLock)
        {
            _state.Version++;
            version = _state.Version;
        }

        if (version != _lastNotifiedVersion)
        {
            _lastNotifiedVersion = version;
            OnStateUpdated?.Invoke(GetSnapshot());
        }
    }

    private long _lastNotifiedVersion;
}
