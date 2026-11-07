using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MSFS2024AIPilot.Core.Environment;
using MSFS2024AIPilot.Core.Paths;
using MSFS2024AIPilot.Core.SimConnect;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>
/// Owns the SimConnect connection and the live <see cref="AircraftState"/>.
/// Registers one data definition per block of SimVars and decodes each response
/// into the state snapshot that the rest of the pilot reads.
/// </summary>
public sealed class SimAircraft : IDisposable
{
    public const int MaxEngines = 4;

    private readonly SimConnectClient _sc;
    private readonly SimulatorPathsConfig? _paths;
    private AircraftState _state = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<uint, string> _requestMap = new();
    private string _aircraftTitle = "";
    private string _aircraftDirectory = "";

    private uint _reqPosition, _reqAttitude, _reqSpeeds, _reqEngines, _reqEngineLimits;
    private uint _reqFuel, _reqAutopilot, _reqEnvironment, _reqNavigation, _reqNavRadio;
    private uint _reqSystems, _reqApModes, _reqCom, _reqAtc, _reqAtcText, _reqWeather, _reqPushback, _reqTitle;
    private uint _atcTextDefineId, _titleDefineId;
    private long _lastNotifiedVersion = -1;

    private const uint RequestAircraftTitle = 90001;
    private const uint EventAircraftLoaded = 90002;

    public event Action<AircraftState>? OnStateUpdated;
    public event Action<string>? OnAircraftTitleChanged;
    public event Action<string>? OnAircraftDirectoryChanged;
    public event Action<string>? OnError;

    public SimConnectClient SimConnect => _sc;
    public AircraftState State => GetSnapshot();
    public string AircraftTitle => _aircraftTitle;
    public string AircraftDirectory => _aircraftDirectory;
    public bool IsConnected => _sc.IsConnected;

    public SimAircraft(SimulatorPathsConfig? paths = null)
    {
        _paths = paths;
        _sc = new SimConnectClient();
        _sc.OnError += msg => OnError?.Invoke(msg);
        _sc.OnDisconnected += () => OnError?.Invoke("SimConnect connection closed.");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _sc.ConnectAsync(cancellationToken: ct);
        SetupDataDefinitions();
        _sc.SubscribeToSystemEvent(EventAircraftLoaded, "AircraftLoaded");
        _sc.RequestUserAircraftTitle(RequestAircraftTitle);
        _titleDefineId = _sc.CreateStringDataDefinition(("TITLE", DatumType.String256));
        _reqTitle = _sc.RequestData(_titleDefineId, period: Period.Second);
        _requestMap[_reqTitle] = "TITLE";
    }

    public void Stop() => _sc.Disconnect();

    public void Dispatch()
    {
        _sc.Dispatch();
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

    public AircraftState GetSnapshot()
    {
        lock (_stateLock) return _state.Clone();
    }

    // ------------------------------------------------------------------
    // Data layout: (block, simvar, units) in declaration order.
    // ------------------------------------------------------------------

    internal static readonly (string Block, string Name, string Units)[] DataLayout = BuildLayout();

    internal static readonly (string Name, DatumType Size)[] AtcTextLayout =
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

    private static (string, string, string)[] BuildLayout()
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

        layout.Add(("ENGINES", "NUMBER OF ENGINES", "number"));
        for (uint e = 1; e <= MaxEngines; e++)
        {
            layout.Add(("ENGINES", $"GENERAL ENG COMBUSTION:{e}", "bool"));
            layout.Add(("ENGINES", $"GENERAL ENG RPM:{e}", "rpm"));
            layout.Add(("ENGINES", $"TURB ENG N1:{e}", "percent"));
            layout.Add(("ENGINES", $"TURB ENG N2:{e}", "percent"));
            layout.Add(("ENGINES", $"TURB ENG FUEL FLOW PPH:{e}", "pounds per hour"));
            layout.Add(("ENGINES", $"GENERAL ENG THROTTLE LEVER POSITION:{e}", "percent"));
        }

        // Separate block so a rejected powerplant limit name cannot take the
        // engine-state block down with it.
        layout.Add(("ENGINE_LIMITS", "NUMBER OF ENGINES", "number"));
        for (uint e = 1; e <= MaxEngines; e++)
        {
            layout.Add(("ENGINE_LIMITS", $"GENERAL ENG EXHAUST GAS TEMPERATURE:{e}", "rankine"));
            layout.Add(("ENGINE_LIMITS", $"GENERAL ENG PCT MAX RPM:{e}", "percent"));
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

            // ILS tracking for the approach.
            ("NAVRADIO", "NAV HAS LOCALIZER", "bool"),
            ("NAVRADIO", "NAV HAS GLIDE SLOPE", "bool"),
            ("NAVRADIO", "NAV LOCALIZER", "degrees"),
            ("NAVRADIO", "NAV GLIDE SLOPE ERROR", "degrees"),
            ("NAVRADIO", "NAV RADIAL ERROR", "degrees"),
            ("NAVRADIO", "NAV SIGNAL", "number"),
            ("NAVRADIO", "NAV TOFROM", "bool"),

            ("SYSTEMS", "SIM ON GROUND", "bool"),
            ("SYSTEMS", "GEAR HANDLE POSITION", "bool"),
            ("SYSTEMS", "GEAR TOTAL PCT EXTENDED", "percent"),
            ("SYSTEMS", "IS GEAR FLOATS", "bool"),
            ("SYSTEMS", "BRAKE PARKING POSITION", "bool"),
            ("SYSTEMS", "FLAPS HANDLE INDEX", "number"),
            ("SYSTEMS", "FLAPS NUM HANDLE POSITIONS", "number"),
            ("SYSTEMS", "TRAILING EDGE FLAPS LEFT ANGLE", "degrees"),
            ("SYSTEMS", "STALL WARNING", "bool"),
            ("SYSTEMS", "OVERSPEED WARNING", "bool"),
            ("SYSTEMS", "AIRSPEED BARBER POLE", "knots"),
            ("SYSTEMS", "MACH MAX OPERATE", "number"),
            ("SYSTEMS", "BARBER POLE MACH", "number"),

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

        // Native-ATC SimVars: every clearance the sim's own ATC issues.
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

        // Weather reaction inputs.
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

        // Pushback tug service SimVars.
        layout.AddRange(new[]
        {
            ("PUSHBACK", "PUSHBACK ATTACHED", "bool"),
            ("PUSHBACK", "PUSHBACK AVAILABLE", "bool"),
            ("PUSHBACK", "PUSHBACK WAIT", "bool"),
            ("PUSHBACK", "PUSHBACK STATE:1", ""),
            ("PUSHBACK", "PUSHBACK ANGLE", "radians"),
        });

        return layout.ToArray();
    }

    private void SetupDataDefinitions()
    {
        if (!_sc.IsConnected) return;

        uint Define(string block)
        {
            var id = _sc.CreateDataDefinition();
            foreach (var (_, name, units) in DataLayout.Where(d => d.Block == block))
                _sc.AddToDataDefinition(id, name, units);
            return id;
        }

        _reqPosition = _sc.RequestData(Define("POSITION"), period: Period.VisualFrame);
        _reqAttitude = _sc.RequestData(Define("ATTITUDE"), period: Period.VisualFrame);
        _reqSpeeds = _sc.RequestData(Define("SPEEDS"), period: Period.VisualFrame);
        _reqEngines = _sc.RequestData(Define("ENGINES"), period: Period.Second);
        _reqEngineLimits = _sc.RequestData(Define("ENGINE_LIMITS"), period: Period.Second);
        _reqFuel = _sc.RequestData(Define("FUEL"), period: Period.Second);
        _reqAutopilot = _sc.RequestData(Define("AUTOPILOT"), period: Period.VisualFrame);
        _reqEnvironment = _sc.RequestData(Define("ENVIRONMENT"), period: Period.Second);
        _reqNavigation = _sc.RequestData(Define("NAVIGATION"), period: Period.VisualFrame);
        _reqNavRadio = _sc.RequestData(Define("NAVRADIO"), period: Period.VisualFrame);
        _reqSystems = _sc.RequestData(Define("SYSTEMS"), period: Period.VisualFrame);
        _reqApModes = _sc.RequestData(Define("APMODES"), period: Period.VisualFrame);
        _reqCom = _sc.RequestData(Define("COM"), period: Period.Second);
        _reqAtc = _sc.RequestData(Define("ATC"), period: Period.Second);
        _reqWeather = _sc.RequestData(Define("WEATHER"), period: Period.VisualFrame);
        _reqPushback = _sc.RequestData(Define("PUSHBACK"), period: Period.Second);

        foreach (var (req, block) in new[]
        {
            (_reqPosition, "POSITION"), (_reqAttitude, "ATTITUDE"), (_reqSpeeds, "SPEEDS"),
            (_reqEngines, "ENGINES"), (_reqEngineLimits, "ENGINE_LIMITS"), (_reqFuel, "FUEL"),
            (_reqAutopilot, "AUTOPILOT"), (_reqEnvironment, "ENVIRONMENT"), (_reqNavigation, "NAVIGATION"),
            (_reqNavRadio, "NAVRADIO"), (_reqSystems, "SYSTEMS"), (_reqApModes, "APMODES"),
            (_reqCom, "COM"), (_reqAtc, "ATC"), (_reqWeather, "WEATHER"), (_reqPushback, "PUSHBACK"),
        })
            _requestMap[req] = block;

        _atcTextDefineId = _sc.CreateStringDataDefinition(AtcTextLayout);
        _reqAtcText = _sc.RequestData(_atcTextDefineId, period: Period.Second);
        _requestMap[_reqAtcText] = "ATCTEXT";

        _sc.OnDataReceived += OnDataReceived;
        _sc.OnStringDataReceived += OnStringDataReceived;
        _sc.OnSystemState += OnSystemState;
    }

    private void OnSystemState(SystemStateData data)
    {
        if (data.RequestId != RequestAircraftTitle || string.IsNullOrWhiteSpace(data.String)) return;
        var airPath = data.String;
        var dir = Path.GetDirectoryName(airPath);
        if (string.IsNullOrEmpty(dir)) return;
        UpdateAircraftIdentity(dir, _aircraftTitle);
    }

    private void OnStringDataReceived(StringSimObjectData data)
    {
        string S(int i) => i < data.Values.Length ? data.Values[i] : "";
        string? OrNull(int i)
        {
            var v = S(i);
            return string.IsNullOrWhiteSpace(v) || v.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : v;
        }

        if (data.DefineId == _titleDefineId)
        {
            var title = OrNull(0);
            if (string.IsNullOrWhiteSpace(title)) return;
            lock (_stateLock)
            {
                if (!string.Equals(title, _aircraftTitle, StringComparison.OrdinalIgnoreCase))
                {
                    _aircraftTitle = title;
                    OnAircraftTitleChanged?.Invoke(_aircraftTitle);
                }
                if (string.IsNullOrWhiteSpace(_aircraftDirectory))
                {
                    var dir = AircraftPackageLocator.LocateAircraftDirectory(_aircraftTitle, null, _paths);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        _aircraftDirectory = dir;
                        OnAircraftDirectoryChanged?.Invoke(_aircraftDirectory);
                    }
                }
            }
        }
        else if (data.DefineId == _atcTextDefineId)
        {
            lock (_stateLock)
            {
                var atc = _state.Atc;
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
                _state.Timestamp = DateTime.UtcNow;
            }
        }
    }

    private void UpdateAircraftIdentity(string? dirHint, string? titleHint)
    {
        string? resolvedDir = null;
        if (!string.IsNullOrWhiteSpace(dirHint))
            resolvedDir = AircraftPackageLocator.LocateAircraftDirectory("", dirHint, _paths);
        if (resolvedDir == null && !string.IsNullOrWhiteSpace(titleHint))
            resolvedDir = AircraftPackageLocator.LocateAircraftDirectory(titleHint, null, _paths);
        if (resolvedDir != null && !string.Equals(_aircraftDirectory, resolvedDir, StringComparison.OrdinalIgnoreCase))
        {
            _aircraftDirectory = resolvedDir;
            OnAircraftDirectoryChanged?.Invoke(_aircraftDirectory);
        }

        var title = resolvedDir is null ? null : ExtractTitleFromConfig(resolvedDir) ?? titleHint;
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(_aircraftTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            _aircraftTitle = title;
            OnAircraftTitleChanged?.Invoke(_aircraftTitle);
        }
    }

    private static string? ExtractTitleFromConfig(string aircraftDir)
    {
        try
        {
            var cfg = Path.Combine(aircraftDir, "aircraft.cfg");
            if (!File.Exists(cfg)) return null;
            foreach (var line in File.ReadAllLines(cfg))
            {
                var t = line.Trim();
                var eq = t.IndexOf('=');
                if (eq < 0) continue;
                if (!t[..eq].Trim().Equals("title", StringComparison.OrdinalIgnoreCase)) continue;
                var v = t[(eq + 1)..].Trim();
                if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"')) v = v[1..^1];
                return v;
            }
        }
        catch { }
        return null;
    }

    private static double RankineToCelsius(double r) => (r - 491.67) * 5.0 / 9.0;

    private void OnDataReceived(SimObjectData data)
    {
        if (!_requestMap.TryGetValue(data.RequestId, out var block)) return;
        var v = data.Values;
        double D(int i) => i < v.Length && v[i] is double d ? d : 0;

        lock (_stateLock)
        {
            switch (block)
            {
                case "POSITION":
                    _state.Latitude = D(0); _state.Longitude = D(1);
                    _state.Altitude = D(2); _state.AltitudeAGL = D(3);
                    break;
                case "ATTITUDE":
                    _state.Heading = D(0); _state.TrueHeading = D(1);
                    _state.Pitch = D(2); _state.Bank = D(3);
                    break;
                case "SPEEDS":
                    _state.IndicatedAirspeed = D(0); _state.TrueAirspeed = D(1);
                    _state.Mach = D(2); _state.GroundSpeed = D(3); _state.VerticalSpeed = D(4);
                    break;
                case "ENGINES":
                {
                    _state.EngineCount = Math.Max(1, (int)Math.Round(D(0)));
                    int count = Math.Min(MaxEngines, Math.Max(1, _state.EngineCount));
                    var combustion = new double[count]; var n1 = new double[count];
                    var n2 = new double[count]; var ff = new double[count]; var thr = new double[count];
                    for (int e = 0; e < count && e < MaxEngines; e++)
                    {
                        int b = 1 + e * 6;
                        combustion[e] = D(b); n1[e] = D(b + 2); n2[e] = D(b + 3);
                        ff[e] = D(b + 4); thr[e] = D(b + 5);
                    }
                    _state.EngineN1 = n1; _state.EngineN2 = n2; _state.EngineFF = ff; _state.EngineThrottle = thr;
                    _state.EnginesRunning = combustion.Any(c => c > 0.5) || n1.Any(n => n > 5);
                    break;
                }
                case "ENGINE_LIMITS":
                {
                    var own = (int)Math.Round(D(0));
                    int count = Math.Min(MaxEngines, Math.Max(1, own > 0 ? own : _state.EngineCount));
                    var egt = new double[count]; var rpm = new double[count];
                    for (int e = 0; e < count && e < MaxEngines; e++)
                    {
                        int b = 1 + e * 2;
                        egt[e] = RankineToCelsius(D(b)); rpm[e] = D(b + 1);
                    }
                    _state.EngineEgtC = egt; _state.EngineMaxRpmPct = rpm;
                    break;
                }
                case "FUEL":
                    _state.TotalFuelWeight = D(0); _state.TotalFuelQuantity = D(1);
                    _state.GrossWeight = D(3); _state.CGPercent = D(5);
                    break;
                case "AUTOPILOT":
                    _state.Autopilot.MasterSwitch = D(0) > 0.5;
                    _state.Autopilot.HeadingHold = D(1) > 0.5;
                    _state.Autopilot.HeadingTarget = D(2);
                    _state.Autopilot.AltitudeHold = D(3) > 0.5;
                    _state.Autopilot.AltitudeTarget = D(4);
                    _state.Autopilot.VsTarget = D(5);
                    _state.Autopilot.IaTarget = D(6);
                    _state.Autopilot.MachTarget = D(7);
                    break;
                case "ENVIRONMENT":
                    _state.WindDirection = D(0); _state.WindVelocity = D(1);
                    _state.OAT = D(2); _state.Barometer = D(3);
                    _state.PressureAltitude = D(4); _state.SimTimeSeconds = D(5);
                    break;
                case "NAVIGATION":
                    _state.Navigation.DistanceToNextWaypoint = D(0);
                    _state.Navigation.CrossTrackError = D(2);
                    _state.Navigation.BearingToNextWaypoint = D(3);
                    _state.Navigation.NextWaypointLatitude = D(4);
                    _state.Navigation.NextWaypointLongitude = D(5);
                    _state.Navigation.NextWaypointAltitude = D(6);
                    _state.Navigation.CurrentWaypointIndexSet(D(7));
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
                    _state.GearIsFloats = D(3) > 0.5;
                    _state.ParkingBrakeSet = D(4) > 0.5;
                    _state.FlapsHandleIndex = D(5);
                    _state.FlapsPosition = D(7);
                    _state.StallWarning = D(8) > 0.5;
                    _state.OverspeedWarning = D(9) > 0.5;
                    _state.BarberPoleKt = D(10);
                    break;
                case "APMODES":
                    _state.Autopilot.FlightDirector = D(0) > 0.5;
                    _state.Autopilot.VsMode = D(1) > 0.5;
                    _state.Autopilot.IaHold = D(2) > 0.5;
                    _state.Autopilot.MachHold = D(3) > 0.5;
                    _state.Autopilot.ApproachHold = D(4) > 0.5;
                    _state.Autopilot.GlideslopeHold = D(5) > 0.5;
                    _state.Autopilot.BackcourseHold = D(6) > 0.5;
                    _state.Autopilot.YawDamper = D(7) > 0.5;
                    _state.Autopilot.LNav = D(8) > 0.5;
                    _state.Autopilot.AutoThrottle = D(9) > 0.5;
                    _state.Autopilot.WingLeveler = D(10) > 0.5;
                    break;
                case "COM":
                    _state.Com1ActiveFrequency = FormatFrequency(D(0));
                    _state.Com1StandbyFrequency = FormatFrequency(D(1));
                    _state.Com2ActiveFrequency = FormatFrequency(D(2));
                    _state.Com2StandbyFrequency = FormatFrequency(D(3));
                    _state.Squawk = D(4).ToString("0000");
                    break;
                case "ATC":
                {
                    var atc = _state.Atc;
                    atc.ClearedIfr = D(0) > 0.5;
                    atc.ClearedTaxi = D(1) > 0.5;
                    atc.ClearedTakeoff = D(2) > 0.5;
                    atc.ClearedLanding = D(3) > 0.5;
                    atc.IfrFlightPlanAvailable = D(4) > 0.5;
                    atc.AssignedAltitudeFt = (int)D(5);
                    atc.RunwaySelected = D(6) > 0.5;
                    atc.RunwayHeadingTrue = D(7);
                    atc.RunwayLengthMeters = D(8);
                    atc.RunwayWidthMeters = D(9);
                    atc.RunwayDistanceMeters = D(10);
                    atc.TaxiPathDistanceMeters = D(11);
                    // ATC FUTURE AGENT FREQUENCY arrives in hertz.
                    atc.NextControllerFrequencyMHz = D(12) / 1_000_000.0;
                    atc.NextControllerDistanceNm = D(13);
                    atc.NextControllerType = D(14);
                    atc.NextWaypointAltitudeM = D(15);
                    atc.AirportIsTowered = D(16) > 0.5;
                    atc.DepartureAirportIsTowered = D(17) > 0.5;
                    atc.ArrivalAirportIsTowered = D(18) > 0.5;
                    atc.ArrivalAirportHasAtis = D(19) > 0.5;
                    atc.OnParkingSpot = D(20) > 0.5;
                    atc.FlightPlanAltitudeDeviationM = D(21);
                    atc.FlightPlanTrackDeviationM = D(22);
                    atc.NativeStateUtc = DateTime.UtcNow;
                    break;
                }
                case "WEATHER":
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

    private static string FormatFrequency(double mhz)
    {
        var asInt = (uint)(mhz * 1000);
        return $"{asInt / 1000}.{asInt % 1000:D3}";
    }

    public void Dispose() => _sc.Dispose();
}

internal static class NavigationStateExtensions
{
    public static void CurrentWaypointIndexSet(this NavigationState nav, double raw)
    {
        // GPS FLIGHT PLAN WP INDEX is 1-based in the sim; store 0-based internally.
        nav.CurrentWaypointIndex = raw > 0 ? (int)Math.Round(raw) - 1 : -1;
    }
}
