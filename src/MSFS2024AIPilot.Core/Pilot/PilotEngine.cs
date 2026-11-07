using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Atc;
using MSFS2024AIPilot.Core.Environment;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.Guidance;
using MSFS2024AIPilot.Core.Paths;
using MSFS2024AIPilot.Core.Planning;
using MSFS2024AIPilot.Core.SimConnect;
using MSFS2024AIPilot.Core.State;
using MSFS2024AIPilot.Core.Terrain;
using MSFS2024AIPilot.Core.Traffic;

namespace MSFS2024AIPilot.Core.Pilot;

public enum PilotMode
{
    /// <summary>Observing only; user flies.</summary>
    Off,
    /// <summary>Fly the current flight plan / mission.</summary>
    SingleFlight,
    /// <summary>Keep flying legs until stopped: depart, land, service, repeat.</summary>
    Continuous,
}

public sealed class PilotStatus
{
    public PilotMode Mode { get; set; }
    public PilotControlState Control { get; set; } = PilotControlState.Manual;
    public FlightPhase Phase { get; set; } = FlightPhase.Unknown;
    public string StatusText { get; set; } = "Disconnected";
    public string AircraftTitle { get; set; } = "";
    public AircraftKind AircraftKind { get; set; } = AircraftKind.Airplane;
    public string DepartureIcao { get; set; } = "";
    public string DestinationIcao { get; set; } = "";
    public string DepartureRunway { get; set; } = "";
    public string DestinationRunway { get; set; } = "";
    public double DistanceToGoNm { get; set; }
    public double EtaMinutes { get; set; }
    public int ServiceMinutesRemaining { get; set; }
    public bool ChecklistActive { get; set; }
    public string CurrentChecklistItem { get; set; } = "";
    public string ChecklistPhaseName { get; set; } = "";
    public double ChecklistProgress { get; set; }
    public string TrafficSummary { get; set; } = "";
    public int TrafficContacts { get; set; }
    public string LastError { get; set; } = "";
}

/// <summary>
/// The AI pilot orchestrator: owns the SimConnect aircraft, the airport/facility
/// data, the continuous-flight planner, the autopilot and the phase state machine.
/// A background loop pumps SimConnect messages and advances the current phase.
/// </summary>
public sealed class PilotEngine : IDisposable
{
    private readonly SimAircraft _aircraft;
    private readonly AirportFacilityLoader _facilities;
    private readonly ContinuousFlightPlanner _planner;
    private readonly HybridAutopilot _ap;
    private readonly NavPathFollower _nav = new();
    private readonly AtcRequestEngine _atcRequests;
    private readonly AtcRadio _atcRadio;
    private readonly IGlyphRenderer _glyphRenderer = new GdiGlyphRenderer();
    private readonly Dictionary<AtcRequestKind, AtcMenuReader> _menuReaders = new();
    private readonly ChecklistManager _checklists;
    private readonly ChecklistSimHost _checklistHost;
    private readonly TurbulenceTracker _turbulence = new();
    private readonly OperatingLimits _limits = new();
    private readonly EnvelopeGuard _envelope;
    private readonly TrafficMonitor _traffic;
    private TrafficAssessment _trafficAssessment = new();
    private DateTime _lastTrafficLogUtc = DateTime.MinValue;
    private bool _goAroundForTraffic;
    private DateTime _lastLegRetryUtc = DateTime.MinValue;

    // Terrain awareness + aircraft class
    private readonly TerrainElevations _terrainElevations = new();
    private TerrainProfile? _terrainProfile;
    private AircraftKind _aircraftKind = AircraftKind.Airplane;
    private readonly Racetrack _racetrack = new();
    private bool _racetrackActive;
    private DateTime _racetrackSinceUtc;
    private double _racetrackHeadingDeg;
    private double _racetrackTargetAltFt;
    private double _terrainRaiseLoggedFt;
    private bool _gearFloatsObserved;

    private readonly object _lock = new();
    private PilotStatus _status = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    // Mission state
    private BuiltRoute? _route;
    private NavigationState? _routeNav;
    private AirportData? _departureAirport;
    private AirportData? _destinationAirport;
    private AircraftPerformance _performance = new();

    // Phase-specific working state
    private TaxiRoute? _taxiRoute;
    private int _taxiLegIndex;
    private PushbackProgress _pushbackProgress = new();
    private DateTime _phaseEnteredUtc = DateTime.UtcNow;
    private double _targetCruiseAltitudeFt = 6000;
    private DateTime _serviceUntilUtc;
    private int _serviceMinutes = 20;
    private int _legsFlown;

    // Continuous-flight options
    public bool ContinuousEnabled => _status.Mode == PilotMode.Continuous;

    public event Action<PilotStatus>? OnStatusChanged;
    public event Action<string>? OnLog;

    public PilotEngine(SimulatorPathsConfig? paths = null)
    {
        _aircraft = new SimAircraft(paths);
        _facilities = new AirportFacilityLoader(_aircraft.SimConnect);
        _planner = new ContinuousFlightPlanner(_facilities);
        _ap = new HybridAutopilot(_aircraft.SimConnect);
        _atcRequests = new AtcRequestEngine(_aircraft.SimConnect);
        _atcRadio = new AtcRadio(_aircraft.SimConnect);
        _checklistHost = new ChecklistSimHost(_aircraft.SimConnect);
        _checklists = new ChecklistManager(_checklistHost, BuildChecklistEnvironment);
        _envelope = new EnvelopeGuard(_limits);
        _traffic = new TrafficMonitor(_aircraft.SimConnect, log: Log);
        _aircraft.SimConnect.OnDataReceived += _traffic.OnData;

        _aircraft.OnStateUpdated += OnStateUpdated;
        _aircraft.OnAircraftTitleChanged += OnTitleChanged;
        _aircraft.OnAircraftDirectoryChanged += OnAircraftDirectoryChanged;
        _aircraft.OnError += msg => Log($"[SimConnect] {msg}");
        _facilities.OnError += msg => Log($"[Facilities] {msg}");
        _checklists.OnLog += Log;
        _checklists.OnItemDone += (_, status) => PublishStatusThrottled();
    }

    public PilotStatus Status { get { lock (_lock) return _status; } }
    public AircraftState? LatestState => _aircraft.IsConnected ? _aircraft.State : null;
    public SimConnectClient SimConnect => _aircraft.SimConnect;

    // ---------------- Connection & control loop ----------------

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_aircraft.IsConnected) return;
        await _aircraft.ConnectAsync(ct);
        // Kick off the airport list so continuous mode can pick destinations.
        try { _planner.RequestAirportList(); } catch { }
        Log("Connected to the simulator.");
        StartLoop();
    }

    /// <summary>
    /// Loads a .pln flight plan for single-flight mode: parses it, resolves the
    /// departure/destination airports and arms the route for the next engage.
    /// </summary>
    public async Task<bool> LoadFlightPlanAsync(string plnPath, CancellationToken ct = default)
    {
        var (wps, nav) = PlnFile.Parse(plnPath);
        if (wps.Count == 0) { Log("[Pilot] Flight plan file has no waypoints."); return false; }
        // Shares the route-planning gate with continuous/manual planning.
        if (Interlocked.Exchange(ref _routePlanning, 1) == 1) return false;
        try { return await LoadFlightPlanCoreAsync(plnPath, wps, nav, ct); }
        finally { Interlocked.Exchange(ref _routePlanning, 0); }
    }

    private async Task<bool> LoadFlightPlanCoreAsync(string plnPath, List<FlightPlanWaypoint> wps, NavigationState nav, CancellationToken ct)
    {
        var s = _aircraft.GetSnapshot();
        _routeNav = nav;
        _routeNav.Waypoints = wps;

        _departureAirport = !string.IsNullOrEmpty(nav.DepartureIcao)
            ? await _planner.LoadAirportAsync(nav.DepartureIcao, TimeSpan.FromSeconds(20), ct)
            : await FindNearestAirportAsync(s, ct);
        _destinationAirport = !string.IsNullOrEmpty(nav.ArrivalIcao)
            ? await _planner.LoadAirportAsync(nav.ArrivalIcao, TimeSpan.FromSeconds(20), ct)
            : null;

        _route = new BuiltRoute
        {
            Departure = _departureAirport,
            Destination = _destinationAirport,
            DepartureRunway = nav.DepartureRunway,
            DestinationRunway = nav.ArrivalRunway,
            CruiseAltitudeFt = nav.CruiseAltitudeFt > 0 ? nav.CruiseAltitudeFt : ChooseCruiseAltitude(s),
        };
        _route.Waypoints.AddRange(wps);
        // The plan names its own runways — the water/traffic helpers key off
        // these fields, so they must be populated for loaded plans too.
        _departureRunwayName = nav.DepartureRunway;
        _arrivalRunwayName = nav.ArrivalRunway;
        _racetrackActive = false;
        _terrainRaiseLoggedFt = 0;
        await AnalyzeTerrainAsync(s, ct);
        _targetCruiseAltitudeFt = _route.CruiseAltitudeFt;
        _nav.Reset();
        _goAroundForTraffic = false;

        // The plan's own waypoints carry the approach: the last positioned
        // waypoint is the runway end, the one before it approximates the final
        // approach fix. Without these the phase machine never leaves Descent.
        var positioned = wps.Where(w => w.HasPosition).ToList();
        if (positioned.Count > 0)
        {
            _route.RunwayWaypoint = positioned[^1];
            _route.FinalApproachFix = positioned.Count > 1 ? positioned[^2] : positioned[^1];
        }

        lock (_lock)
        {
            _status.DepartureIcao = nav.DepartureIcao ?? _departureAirport?.Icao ?? "";
            _status.DestinationIcao = nav.ArrivalIcao ?? _destinationAirport?.Icao ?? "";
            _status.DepartureRunway = nav.DepartureRunway ?? "";
            _status.DestinationRunway = nav.ArrivalRunway ?? "";
            _status.StatusText = $"Plan loaded: {_status.DepartureIcao} → {_status.DestinationIcao}";
        }
        PublishStatus();
        Log($"[Pilot] Flight plan loaded: {Path.GetFileName(plnPath)} ({wps.Count} waypoints)");
        return true;
    }

    /// <summary>
    /// Airport the aircraft is at/near, using the facility list coordinates.
    /// Tries the nearest candidates in turn — a strip missing from the facility
    /// database or a failed load must not abort departure planning.
    /// </summary>
    private async Task<AirportData?> FindNearestAirportAsync(AircraftState s, CancellationToken ct)
    {
        if (!await _planner.WaitForAirportListAsync(TimeSpan.FromSeconds(30), ct)) return null;
        var candidates = _planner.NearestAirports(s.Latitude, s.Longitude, 8);
        if (candidates.Count == 0) return null;
        Log($"[Pilot] Position {s.Latitude:F4},{s.Longitude:F4}; nearest listed airports: " +
            string.Join(", ", candidates.Take(3).Select(c => $"{c.icao} {c.distNm:F1}nm")));
        foreach (var c in candidates)
        {
            var data = await _planner.LoadAirportAsync(c.icao, TimeSpan.FromSeconds(15), ct);
            if (data is null) { Log($"[Pilot] Facility data unavailable for {c.icao} ({c.distNm:F1} nm) — trying next."); continue; }
            Log($"[Pilot] Nearest airport: {data.Icao} ({c.distNm:F1} nm)");
            return data;
        }
        return null;
    }

    /// <summary>Starts (or resumes) the control loop.</summary>
    public void Engage(PilotMode mode = PilotMode.SingleFlight)
    {
        lock (_lock)
        {
            _status.Mode = mode;
            _status.Control = PilotControlState.AiActive;
            _status.StatusText = mode == PilotMode.Continuous ? "Continuous flights enabled" : "AI pilot engaged";
        }
        PublishStatus();
        Log(mode == PilotMode.Continuous ? "Continuous flight mode enabled." : "AI pilot engaged.");

        if (mode == PilotMode.Continuous)
            _ = Task.Run(() => EnsureContinuousDepartureAsync());
    }

    /// <summary>
    /// First leg of a continuous session: find the airport the aircraft is parked
    /// at, then plan the first destination leg.
    /// </summary>
    private async Task EnsureContinuousDepartureAsync()
    {
        try
        {
            if (_departureAirport is not null) return;
            var s = _aircraft.GetSnapshot();
            _departureAirport = await FindNearestAirportAsync(s, CancellationToken.None);
            if (_departureAirport is null) { Log("[Pilot] Could not identify the departure airport."); return; }
            Log($"[Pilot] Continuous flights departing from {_departureAirport.Icao}.");
            await TryPlanNextLegAsync(CancellationToken.None);
            if (_route is not null) SetPhase(FlightPhase.Preflight);
        }
        catch (Exception ex) { Log($"[Pilot] Continuous departure planning failed: {ex.Message}"); }
    }

    /// <summary>Pauses AI control; the aircraft is handed back to the user/autopilot.</summary>
    public void Pause()
    {
        lock (_lock) { _status.Control = PilotControlState.Paused; _status.StatusText = "Paused"; }
        _ap.Neutralize();
        PublishStatus();
        Log("AI pilot paused.");
    }

    public void Disengage()
    {
        lock (_lock)
        {
            _status.Mode = PilotMode.Off;
            _status.Control = PilotControlState.Manual;
            _status.StatusText = "AI pilot off";
        }
        _ap.Neutralize();
        _ap.ApMaster(false);
        PublishStatus();
        Log("AI pilot disengaged.");
    }

    /// <summary>Sets the continuous-flight service (turnaround) time in minutes.</summary>
    public void SetServiceMinutes(int minutes) { _serviceMinutes = Math.Clamp(minutes, 0, 240); PublishStatus(); }

    /// <summary>Forces an immediate new leg in continuous mode.</summary>
    public async Task<bool> FlyNextLegAsync(CancellationToken ct = default)
    {
        if (!ContinuousEnabled) return false;
        return await TryPlanNextLegAsync(ct);
    }

    private void StartLoop()
    {
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        _lastLoopHeartbeatUtc = DateTime.UtcNow;
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        var ct = _loopCts.Token;
        // Watchdog on its own task: if Dispatch/Tick stalls (e.g. a blocked
        // SimConnect call) the loop itself can never report it.
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(15000, ct); } catch { return; }
                var silent = DateTime.UtcNow - _lastLoopHeartbeatUtc;
                if (silent > TimeSpan.FromSeconds(20) && !_watchdogWarned)
                {
                    _watchdogWarned = true;
                    Log($"[Pilot] WARNING: control loop silent for {silent.TotalSeconds:F0}s — stalled in Tick stage '{_tickStage}'.");
                }
                else if (silent <= TimeSpan.FromSeconds(20)) _watchdogWarned = false;
            }
        }, ct);
    }

    private DateTime _lastLoopHeartbeatUtc = DateTime.MinValue;
    private bool _watchdogWarned;

    private async Task LoopAsync(CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // SimConnect messages are pumped on SimAircraft's dedicated
                // thread — this loop only reads the latest snapshot.
                var now = DateTime.UtcNow;
                _lastLoopHeartbeatUtc = now;
                _tickStage = "tick";
                var dt = Math.Clamp((now - last).TotalSeconds, 0.01, 0.5);
                last = now;
                Tick(dt, now);
            }
            catch (Exception ex)
            {
                // A persistent fault would otherwise write ~20 lines/second to
                // the log and GUI forever. Log the first occurrence and then
                // at most once every 10 s, always with the exception type.
                var errNow = DateTime.UtcNow;
                if (errNow - _lastLoopErrorLogUtc > TimeSpan.FromSeconds(10))
                {
                    _lastLoopErrorLogUtc = errNow;
                    Log($"[Pilot] loop error ({ex.GetType().Name}): {ex.Message}");
                }
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // ---------------- Per-frame logic ----------------

    private volatile string _tickStage = "start";

    private void Tick(double dt, DateTime now)
    {
        _tickStage = "snapshot";
        var s = _aircraft.GetSnapshot();
        _turbulence.Update(s.GLoad, dt, quiet: s.OnGround);
        var turbulence = _turbulence.Level;

        // Traffic awareness: refresh the object-type request and assess the
        // runway this phase cares about (departure on the ground, arrival on final).
        _tickStage = "traffic-refresh";
        _traffic.Refresh();
        _tickStage = "traffic-assess";
        _trafficAssessment = _traffic.Assess(s, TrafficRunwayFor(s));
        _tickStage = "traffic-publish";
        PublishTrafficStatus();

        // Runtime amphibian detection: the sim reports IS GEAR FLOATS once an
        // amphibian's floats are deployed — upgrade a plain Airplane accordingly.
        if (s.GearIsFloats && _aircraftKind == AircraftKind.Airplane && !_gearFloatsObserved)
        {
            _gearFloatsObserved = true;
            _aircraftKind = AircraftKindDetector.ObserveGearFloats(_aircraftKind);
            _planner.Kind = _aircraftKind;
            lock (_lock) _status.AircraftKind = _aircraftKind;
            Log($"[Pilot] Floats detected — aircraft classified as {_aircraftKind}.");
        }

        // Feed weather/turbulence into envelope limits.
        _limits.MaxAirspeedKt = s.BarberPoleKt > 0 ? s.BarberPoleKt : 0;
        _limits.MinAirspeedKt = s.StallWarning ? s.IndicatedAirspeed * 0.9 : VsEstimate(s);

        PilotStatus st;
        lock (_lock) st = _status;

        if (st.Control != PilotControlState.AiActive || st.Mode == PilotMode.Off)
        {
            _atcRequests.Update(s, Log);
            return;
        }

        // ATC radio housekeeping.
        _atcRadio.AcceptHandoff(s);
        _atcRequests.Update(s, Log);

        // Service/turnaround handling.
        if (now < _serviceUntilUtc)
        {
            _ap.Neutralize();
            UpdateServiceStatus(s, now);
            return;
        }

        // Checklists: the ported executor fires copilot actions and verifies
        // simvars for the aircraft's own checklist steps mapped to this phase.
        _tickStage = "checklist";
        _checklists.Tick(st.Phase, dt);

        // Phase machine.
        _tickStage = "phase";
        AdvancePhase(s, now);
        ExecutePhase(s, dt, now, turbulence);
        _tickStage = "done";

        // Periodic flight diagnostics (~every 5 s while AI is active).
        if (now - _lastDiagUtc > TimeSpan.FromSeconds(5))
        {
            _lastDiagUtc = now;
            LogDiagnostics(s);
        }
    }

    private DateTime _lastDiagUtc = DateTime.MinValue;
    private DateTime _lastLoopErrorLogUtc = DateTime.MinValue;
    private ControlTargets _lastTargets = new();
    private int _lastLoggedWpIndex = -1;

    /// <summary>Logs a compact one-line snapshot of flight state and targets.</summary>
    private void LogDiagnostics(AircraftState s)
    {
        var t = _lastTargets;
        var tgt = string.Join(' ', new[]
        {
            t.TargetHeadingDeg is { } h ? $"HDG {h:F0}" : null,
            t.TargetAltitudeFt is { } a ? $"ALT {a:F0}" : null,
            t.TargetAirspeedKt is { } i ? $"IAS {i:F0}" : null,
            t.TargetVsFpm is { } v ? $"VS {v:F0}" : null,
            t.ThrottleFraction is { } th ? $"THR {th:P0}" : null,
            t.GearDown is { } g ? (g ? "GEAR↓" : "GEAR↑") : null,
            t.FlapIndex is { } f ? $"FLAP {f}" : null,
        }.Where(x => x is not null));
        Log($"[Diag] {Status.Phase} | {s.IndicatedAirspeed:F0}kt {s.Altitude:F0}ft {s.VerticalSpeed:+0;-0;0}fpm " +
            $"hdg {s.TrueHeading:F0} gs {s.GroundSpeed:F0} | {Status.DistanceToGoNm:F0}nm ~{Status.EtaMinutes:F0}min | " +
            $"{(_ap.DirectMode ? "DIRECT" : "NAV-AP")} | {tgt}");
    }

    /// <summary>Logs each flight-plan waypoint capture once.</summary>
    private void LogNavProgress()
    {
        var idx = _nav.ActiveIndex;
        if (idx == _lastLoggedWpIndex || _routeNav is null) return;
        _lastLoggedWpIndex = idx;
        var wp = idx >= 0 && idx < _routeNav.Waypoints.Count ? _routeNav.Waypoints[idx] : null;
        Log($"[Nav] waypoint {idx + 1}/{_routeNav.Waypoints.Count}: {wp?.Ident ?? "?"}" +
            (wp?.HasPosition == true ? $" ({wp.Latitude:F3},{wp.Longitude:F3} @{wp.Altitude:F0}ft)" : ""));
    }

    private ChecklistEnvironment BuildChecklistEnvironment()
    {
        var s = _aircraft.IsConnected ? _aircraft.GetSnapshot() : null;
        return new ChecklistEnvironment
        {
            Night = false, // no dedicated day/night simvar is subscribed; Overcast covers the "AS REQUIRED" cases
            Overcast = s?.AmbientInCloud == true || (s?.PrecipitationState ?? 0) > 0,
            IcingRisk = (s?.StructuralIcePercent ?? 0) > 0 || ((s?.OAT ?? 15) < -10 && s?.AmbientInCloud == true),
            Turbine = _performance.EngineType.Contains("TURBO", StringComparison.OrdinalIgnoreCase) ||
                      _performance.EngineType.Contains("JET", StringComparison.OrdinalIgnoreCase) ||
                      _aircraftKind == AircraftKind.Rotorcraft,
            EngineCount = Math.Max(1, s?.EngineCount ?? _performance.EngineCount),
            TransponderCode = s?.Squawk,
        };
    }

    private void UpdateServiceStatus(AircraftState s, DateTime now)
    {
        var remaining = (int)Math.Ceiling((_serviceUntilUtc - now).TotalMinutes);
        lock (_lock)
        {
            _status.ServiceMinutesRemaining = remaining;
            _status.StatusText = $"Servicing aircraft ({remaining} min remaining)";
            _status.Phase = FlightPhase.Shutdown;
        }
        // During service we do not refuel through SimConnect (no fuel-set event is
        // reliable across aircraft); instead we wait and let the user/ground crew
        // handle it, or the sim's own servicing if available.
        PublishStatusThrottled();
    }

    private void PublishStatusThrottled()
    {
        // Publish at most every ~250 ms to avoid flooding the UI.
        var now = System.Environment.TickCount64;
        if (now - _lastPublishMs < 250) return;
        _lastPublishMs = now;
        PublishStatus();
    }
    private long _lastPublishMs;

    // ---------------- Phase machine ----------------

    private void AdvancePhase(AircraftState s, DateTime now)
    {
        var st = Status;
        var phase = st.Phase;

        switch (phase)
        {
            case FlightPhase.Unknown:
                if (s.EnginesRunning || s.OnGround) SetPhase(FlightPhase.Preflight);
                break;

            case FlightPhase.Preflight:
                // Checklists advance through ExecutePhase; engines started -> Startup.
                break;

            case FlightPhase.Startup:
                break;

            case FlightPhase.TaxiOut:
                break;

            case FlightPhase.Takeoff:
                // Rotorcraft transitions on height above ground (SIM ON GROUND
                // flickers during the initial hover); fixed-wing on liftoff.
                if (_aircraftKind == AircraftKind.Rotorcraft
                    ? s.AltitudeAGL > 80
                    : !s.OnGround) { SetPhase(FlightPhase.InitialClimb); _nav.Reset(); }
                break;

            case FlightPhase.InitialClimb:
                if (_aircraftKind == AircraftKind.Rotorcraft
                    ? s.AltitudeAGL > 400 || s.Altitude > _targetCruiseAltitudeFt - 200
                    : s.AltitudeAGL > 1000 || s.Altitude > _targetCruiseAltitudeFt * 0.4)
                    SetPhase(FlightPhase.Climb);
                break;

            case FlightPhase.Climb:
                if (s.Altitude >= _targetCruiseAltitudeFt - 200) SetPhase(FlightPhase.Cruise);
                break;

            case FlightPhase.Cruise:
                if (RemainingDistanceNm(s) < DescentDistanceNm()) SetPhase(FlightPhase.Descent);
                break;

            case FlightPhase.Descent:
                if (_route?.FinalApproachFix is not null)
                {
                    var dist = GeoMath.DistanceNm(s.Latitude, s.Longitude,
                        _route.FinalApproachFix.Latitude, _route.FinalApproachFix.Longitude);
                    if (dist < 12) SetPhase(FlightPhase.Approach);
                }
                break;

            case FlightPhase.Approach:
                if (_route?.RunwayWaypoint is not null)
                {
                    var dist = GeoMath.DistanceNm(s.Latitude, s.Longitude,
                        _route.RunwayWaypoint.Latitude, _route.RunwayWaypoint.Longitude);
                    if (dist < 3) SetPhase(FlightPhase.FinalApproach);
                }
                break;

            case FlightPhase.FinalApproach:
                if (s.AltitudeAGL < 50 && s.VerticalSpeed < -50) { /* on glidepath */ }
                if (s.OnGround) SetPhase(FlightPhase.Rollout);
                break;

            case FlightPhase.GoAround:
                if (s.AltitudeAGL > 500) SetPhase(FlightPhase.Climb);
                break;

            case FlightPhase.Rollout:
                if (s.GroundSpeed < 5)
                {
                    // Rotorcraft set down vertically — no taxi-in phase.
                    if (_aircraftKind == AircraftKind.Rotorcraft)
                    {
                        if (_status.Mode == PilotMode.Continuous) BeginTurnaround(s);
                        else SetPhase(FlightPhase.Shutdown);
                    }
                    else SetPhase(FlightPhase.TaxiIn);
                }
                break;

            case FlightPhase.TaxiIn:
                // Completed when parked or when the route is finished.
                if (_taxiRoute is { Success: true } tr && _taxiLegIndex >= tr.Legs.Count)
                {
                    if (_status.Mode == PilotMode.Continuous)
                        BeginTurnaround(s);
                    else
                        SetPhase(FlightPhase.Shutdown);
                }
                break;

            case FlightPhase.Shutdown:
                if (_status.Mode == PilotMode.Continuous)
                {
                    if (_serviceUntilUtc == default)
                    {
                        BeginTurnaround(s);
                    }
                    else if (_route is null && now - _lastLegRetryUtc > TimeSpan.FromSeconds(30))
                    {
                        // Service is over but the next leg never got planned
                        // (no destination in range, airport list late, ...).
                        // Retry instead of sitting in Shutdown forever.
                        _lastLegRetryUtc = now;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (await TryPlanNextLegAsync(CancellationToken.None) && _route is not null)
                                    SetPhase(FlightPhase.Preflight);
                            }
                            catch (Exception ex) { Log($"[Pilot] Next-leg retry failed: {ex.Message}"); }
                        });
                    }
                }
                break;
        }

        lock (_lock)
        {
            _status.Phase = phase;
            _status.DistanceToGoNm = RemainingDistanceNm(s);
            _status.EtaMinutes = EtaMinutes(s);
        }
        PublishStatusThrottled();
    }

    private void SetPhase(FlightPhase phase)
    {
        if (phase == FlightPhase.Startup) { _starterAttempts = 0; _starterAttemptUtc = DateTime.MinValue; }
        // Each new approach gets one traffic go-around — the flag must not
        // stay latched for the rest of the flight after a missed approach.
        if (phase == FlightPhase.Approach) _goAroundForTraffic = false;
        lock (_lock) { _status.Phase = phase; _status.StatusText = $"Phase: {phase}"; }
        _phaseEnteredUtc = DateTime.UtcNow;
        Log($"Phase → {phase}");
        PublishStatus();
    }

    // ---------------- Phase executors ----------------

    private void ExecutePhase(AircraftState s, double dt, DateTime now, TurbulenceLevel turbulence)
    {
        var targets = new ControlTargets
        {
            ControlScale = turbulence switch
            {
                TurbulenceLevel.Severe => 0.5,
                TurbulenceLevel.Moderate => 0.7,
                TurbulenceLevel.Light => 0.85,
                _ => 1.0,
            },
            MaxBankDeg = FlightEnvelope.ClampBankDeg(25, turbulence, onApproach: IsOnApproach(s)),
        };

        switch (Status.Phase)
        {
            case FlightPhase.Preflight:
                ExecutePreflight(s, targets);
                break;
            case FlightPhase.Startup:
                ExecuteStartup(s, targets, now);
                break;
            case FlightPhase.TaxiOut:
                ExecuteTaxiOut(s, targets, now);
                break;
            case FlightPhase.Takeoff:
                ExecuteTakeoff(s, targets);
                break;
            case FlightPhase.InitialClimb:
            case FlightPhase.Climb:
            case FlightPhase.Cruise:
                ExecuteEnroute(s, targets, turbulence);
                break;
            case FlightPhase.Descent:
                ExecuteDescent(s, targets);
                break;
            case FlightPhase.Approach:
                ExecuteApproach(s, targets);
                break;
            case FlightPhase.FinalApproach:
                ExecuteFinalApproach(s, targets, turbulence);
                break;
            case FlightPhase.GoAround:
                ExecuteGoAround(s, targets);
                break;
            case FlightPhase.Rollout:
                ExecuteRollout(s, targets);
                break;
            case FlightPhase.TaxiIn:
                ExecuteTaxiIn(s, targets);
                break;
            case FlightPhase.Shutdown:
                _ap.Neutralize();
                break;
        }

        _lastTargets = targets;
        _ap.SetTargets(targets);
        _ap.Update(s, dt);
    }

    private bool IsOnApproach(AircraftState s) =>
        Status.Phase is FlightPhase.Approach or FlightPhase.FinalApproach or FlightPhase.GoAround;

    // ----- Preflight / startup -----

    private void ExecutePreflight(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        if (!s.ParkingBrakeSet) _ap.ParkingBrake(true);
        // Wait for the gate checklists (pushback + gate), then advance.
        if (!_checklists.IsLoaded ||
            (_checklists.StepComplete(ChecklistStep.PREFLIGHT_GATE) &&
             _checklists.StepComplete(ChecklistStep.PREFLIGHT_PUSHBACK)))
            SetPhase(FlightPhase.Startup);
    }

    private DateTime _starterAttemptUtc = DateTime.MinValue;
    private int _starterAttempts;

    private void ExecuteStartup(AircraftState s, ControlTargets t, DateTime now)
    {
        _ap.SetDirectMode(true);
        if (!s.ParkingBrakeSet) _ap.ParkingBrake(true);
        if (!_checklists.IsLoaded ||
            (_checklists.StepComplete(ChecklistStep.PREFLIGHT_GATE) &&
             _checklists.StepComplete(ChecklistStep.PREFLIGHT_PUSHBACK)))
        {
            if (!s.EnginesRunning)
            {
                // Cold aircraft: try the starter until the engines catch or we give up.
                if (_starterAttempts < 6 && now - _starterAttemptUtc > TimeSpan.FromSeconds(4))
                {
                    // Fire the starter for every engine the aircraft reports.
                    var engines = Math.Clamp(s.EngineCount, 1, 4);
                    for (int e = 1; e <= engines; e++)
                        _aircraft.SimConnect.FireEvent($"TOGGLE_STARTER{e}");
                    _starterAttemptUtc = now;
                    _starterAttempts++;
                    Log($"[Pilot] Attempting engine start ({engines} engine(s)).");
                }
                return;
            }
            // Request IFR clearance / flight following before departure.
            _atcRequests.Request(AtcRequestKind.IfreClearance, "preflight", MenuLinesFor(AtcRequestKind.IfreClearance));
            if (_aircraftKind == AircraftKind.Rotorcraft)
            {
                // No runway, no pushback — but allow a low hover-taxi away from
                // the parking spot toward the departure point first.
                SetPhase(FlightPhase.TaxiOut);
                _ap.ParkingBrake(false);
            }
            else
            {
                SetPhase(FlightPhase.TaxiOut);
                BeginTaxiOut(s);
            }
        }
    }

    // ----- Taxi out -----

    private void BeginTaxiOut(AircraftState s)
    {
        _taxiLegIndex = 0;
        _taxiRoute = null;
        _taxiInPlanned = false;
        _pushbackProgress = new PushbackProgress { StartedUtc = DateTime.UtcNow };
        _pushbackDismissed = false;
        _pushbackToggleFired = false;
        _departureRunwayName = null;
        // Pick the runway first so the taxi route can hold short correctly.
        PlanDeparture(s);
    }

    private string? _departureRunwayName;
    private string? _arrivalRunwayName;

    private DateTime _lastDeparturePlanUtc = DateTime.MinValue;

    private void PlanDeparture(AircraftState s)
    {
        if (_departureAirport is null) return;
        // Called every frame while no route exists; don't replan (and re-log)
        // more than once every few seconds.
        if (DateTime.UtcNow - _lastDeparturePlanUtc < TimeSpan.FromSeconds(5)) return;
        _lastDeparturePlanUtc = DateTime.UtcNow;

        // Choose the runway: into the wind, long enough for takeoff. Water
        // kinds prefer (or require) water runways.
        var pick = ContinuousFlightPlanner.PickRunway(_departureAirport, s.WindDirection,
            _takeoffRunMeters(s),
            waterOnly: _aircraftKind == AircraftKind.Seaplane,
            preferWater: _aircraftKind == AircraftKind.Amphibian);
        if (pick is null) { Log("[Pilot] No suitable departure runway found."); return; }
        var (rw, name) = pick.Value;
        _departureRunwayName = name;

        // Build the taxi route to the hold-short for that runway.
        var start = TaxiRouter.FindNearestNode(_departureAirport, s.Latitude, s.Longitude);
        if (start is null) { Log("[Pilot] No ground graph at departure airport; cannot taxi."); return; }
        var holdShort = TaxiRouter.FindReachableHoldShort(_departureAirport, start.Index, name,
            new TaxiConstraints
            {
                RunwayName = name,
                HoldShortBefore = name,
                AircraftWeightLbs = (uint)s.GrossWeight,
            });
        if (holdShort is null) { Log("[Pilot] No reachable hold-short found."); return; }

        var route = TaxiRouter.FindRoute(_departureAirport, start.Index, holdShort.Index,
            new TaxiConstraints { RunwayName = name, HoldShortBefore = name, AircraftWeightLbs = (uint)s.GrossWeight });
        if (route.Success)
        {
            _taxiRoute = route;
            Log($"[Pilot] Taxi route: {route.Describe()}");
        }
    }

    private double _takeoffRunMeters(AircraftState s)
    {
        // Water operations need far less runway than the land performance
        // model demands — cap the requirement so short seaplane lanes qualify.
        if (AircraftKindDetector.IsWaterKind(_aircraftKind))
            return _aircraftKind == AircraftKind.Seaplane ? 500 : 600;
        return TakeoffRunMetersLand(s);
    }

    private double TakeoffRunMetersLand(AircraftState s)
    {
        // Required takeoff run including margin, from the performance model when
        // available, else a v²/2a estimate off the rotation speed.
        var weight = s.GrossWeight > 0 ? s.GrossWeight : _performance.MaxGrossWeight;
        if (_performance.HasPerformanceData && weight > 0)
        {
            var ft = _performance.RequiredTakeoffRunwayFt(weight);
            return ft * GeoMath.FeetPerMeter;
        }
        var v = Math.Max(VsEstimate(s) * 1.15, 55);
        var accel = 8.0; // m/s² conservative piston accel
        var vms = v * 0.514444;
        return vms * vms / (2 * accel) * 1.15;
    }

    /// <summary>Stall speed estimate: Vs1 from the performance model or a conservative floor.</summary>
    private double VsEstimate(AircraftState s)
    {
        if (_performance.Vs1 > 0) return _performance.Vs1;
        if (_performance.Vso > 0) return _performance.Vso * 1.1;
        return Math.Max(50, s.IndicatedAirspeed * 0.55);
    }

    private void ExecuteTaxiOut(AircraftState s, ControlTargets t, DateTime now)
    {
        _ap.SetDirectMode(true);

        // Rotorcraft: hover-taxi low toward the departure point, then climb.
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            ExecuteRotorcraftTaxiOut(s, t);
            return;
        }

        // Water operations: a floatplane/amphibian on a water runway has no
        // ground graph — no pushback, no taxi. Line up on the water and go.
        if (DepartureOnWater())
        {
            _pushbackDone = true;
            _taxiClearanceRequested = true; // seaplane bases are normally untowered
            t.GearDown = false; // amphibians: wheels up before the water roll
            LineUpAndTakeoff(s, t);
            return;
        }

        // Pushback first if the stand offers it and it hasn't been done.
        if (s.Pushback.Available && !s.Pushback.Attached && !_pushbackDone)
        {
            ExecutePushback(s, t, now);
            return;
        }
        _pushbackDone = true;

        // Wait for taxi clearance if the airport is towered. Re-request every
        // 60 s while still pending — a timed-out or denied request must not
        // leave the aircraft waiting forever.
        if (s.Atc.DepartureAirportIsTowered && !s.Atc.ClearedTaxi &&
            RequestDue(AtcRequestKind.Taxi, ref _taxiClearanceRequested, ref _lastTaxiRequestUtc, now))
        {
            _atcRequests.Request(AtcRequestKind.Taxi, "taxi-out", MenuLinesFor(AtcRequestKind.Taxi));
        }

        // Route to the runway and taxi.
        if (_taxiRoute is null) { PlanDeparture(s); if (_taxiRoute is null) return; }

        var legs = _taxiRoute.Legs;
        if (_taxiLegIndex >= legs.Count)
        {
            // Reached the hold-short: hold position and request takeoff clearance.
            _ap.SetThrottleFraction(0);
            _ap.ParkingBrake(true);
            if (s.Atc.DepartureAirportIsTowered && !s.Atc.ClearedTakeoff &&
                RequestDue(AtcRequestKind.Takeoff, ref _takeoffClearanceRequested, ref _lastTakeoffRequestUtc, now))
            {
                _atcRequests.Request(AtcRequestKind.Takeoff, "hold-short", MenuLinesFor(AtcRequestKind.Takeoff));
            }
            if (!s.Atc.DepartureAirportIsTowered || s.Atc.ClearedTakeoff)
            {
                _takeoffClearanceRequested = false;
                if (TrafficAllowsRunwayEntry()) LineUpAndTakeoff(s, t);
            }
            return;
        }

        // Follow the taxi route: steer toward the next node, taxi speed.
        var leg = legs[_taxiLegIndex];
        var toNode = _departureAirport!.FindNode(leg.ToNode);
        if (toNode is null) { _taxiLegIndex++; return; }

        var bearing = GeoMath.BearingDeg(s.Latitude, s.Longitude, toNode.Latitude, toNode.Longitude);
        var dist = GeoMath.DistanceMeters(s.Latitude, s.Longitude, toNode.Latitude, toNode.Longitude);
        t.TargetHeadingDeg = bearing;
        t.TargetAirspeedKt = null; // ground handling: use throttle directly
        t.ThrottleFraction = dist > 80 ? 0.18 : dist > 30 ? 0.10 : 0.05;

        // Slow for the hold-short: the last leg ends there.
        if (_taxiLegIndex == legs.Count - 1)
        {
            if (dist < 15)
            {
                t.ThrottleFraction = 0;
                _ap.ParkingBrake(true);
            }
        }

        if (dist < 12) { _taxiLegIndex++; return; }
    }

    /// <summary>The runway the current phase is using, for traffic assessment.</summary>
    private RunwayInfo? TrafficRunwayFor(AircraftState s)
    {
        var phase = Status.Phase;
        if (phase is FlightPhase.TaxiOut or FlightPhase.Takeoff or FlightPhase.InitialClimb
            or FlightPhase.Climb or FlightPhase.Cruise or FlightPhase.Descent)
        {
            return _departureRunwayName is not null && _departureAirport is not null
                ? _departureAirport.FindRunway(_departureRunwayName) : null;
        }
        if (phase is FlightPhase.Approach or FlightPhase.FinalApproach or FlightPhase.Rollout)
        {
            return _arrivalRunwayName is not null && _destinationAirport is not null
                ? _destinationAirport.FindRunway(_arrivalRunwayName) : null;
        }
        return null;
    }

    private string _lastTrafficSignature = "";
    private bool _lastTrafficUnsafe;

    private void PublishTrafficStatus()
    {
        var a = _trafficAssessment;
        // Signature uses coarse-grained values so the status refreshes a few
        // times a minute at most, not every tick — Describe() embeds live
        // distances that would otherwise change constantly.
        var occupant = a.DepartureRunwayOccupant;
        var onFinal = a.ArrivalOnDepartureRunway;
        var signature =
            $"{a.Contacts.Count}|{a.AirborneConflicts.Count}|" +
            $"{(occupant is null ? -1 : (int)(occupant.RangeMeters / 100))}|" +
            $"{(onFinal is null ? -1 : (int)(onFinal.RangeNauticalMiles * 2))}";
        if (signature == _lastTrafficSignature) return;
        _lastTrafficSignature = signature;
        lock (_lock)
        {
            _status.TrafficSummary = a.Contacts.Count == 0 ? "" : a.Describe();
            _status.TrafficContacts = a.Contacts.Count;
        }

        // Log only on transitions into/out of an unsafe assessment — the
        // hold/go-around paths log their own throttled messages.
        if (a.RunwayEntryUnsafe != _lastTrafficUnsafe)
        {
            _lastTrafficUnsafe = a.RunwayEntryUnsafe;
            Log($"[Traffic] {(a.RunwayEntryUnsafe ? "conflict: " : "clear: ")}{a.Describe()}");
        }
    }

    /// <summary>
    /// True when no observed traffic makes entering the runway unsafe. Holds at the
    /// hold-short while occupied; the assessment only ever says "unsafe", never guesses.
    /// </summary>
    private bool TrafficAllowsRunwayEntry()
    {
        var a = _trafficAssessment;
        if (!a.RunwayEntryUnsafe) return true;
        if (DateTime.UtcNow - _lastTrafficLogUtc > TimeSpan.FromSeconds(15))
        {
            _lastTrafficLogUtc = DateTime.UtcNow;
            Log($"[Traffic] holding: {a.Describe()}");
        }
        return false;
    }

    private bool _pushbackDone;
    private bool _pushbackDismissed;
    private bool _pushbackToggleFired;
    private DateTime _pushbackDismissUtc;
    private static readonly TimeSpan PushbackDetachTimeout = TimeSpan.FromSeconds(20);
    private bool _taxiClearanceRequested;
    private bool _takeoffClearanceRequested;
    private DateTime _lastTaxiRequestUtc = DateTime.MinValue;
    private DateTime _lastTakeoffRequestUtc = DateTime.MinValue;

    /// <summary>
    /// True when an ATC request should be (re)issued: the first time, or every
    /// 60 s after the previous attempt ended (timeout/denial) — never while the
    /// same kind is still being probed.
    /// </summary>
    private bool RequestDue(AtcRequestKind kind, ref bool requested, ref DateTime lastUtc, DateTime now)
    {
        var inFlight = _atcRequests.Pending == kind && _atcRequests.Status == AtcRequestStatus.Requested;
        if (inFlight) return false;
        if (requested && now - lastUtc < TimeSpan.FromSeconds(60)) return false;
        requested = true;
        lastUtc = now;
        return true;
    }

    /// <summary>True when departing an airport on a water runway (float/amphib).</summary>
    private bool DepartureOnWater()
    {
        if (!AircraftKindDetector.IsWaterKind(_aircraftKind)) return false;
        if (_departureAirport is null || _departureRunwayName is null) return false;
        return _departureAirport.FindRunway(_departureRunwayName)?.IsWater == true;
    }

    /// <summary>True when the arrival runway is water — gear stays up.</summary>
    private bool ArrivalOnWater()
    {
        if (!AircraftKindDetector.IsWaterKind(_aircraftKind)) return false;
        if (_destinationAirport is null || _arrivalRunwayName is null) return false;
        return _destinationAirport.FindRunway(_arrivalRunwayName)?.IsWater == true;
    }

    /// <summary>Whether the landing gear should be extended for the arrival.</summary>
    private bool ArrivalNeedsGear() => !ArrivalOnWater();

    private void ExecutePushback(AircraftState s, ControlTargets t, DateTime now)
    {
        _ap.SetDirectMode(true);
        var decision = PushbackPlanner.Decide(s, _pushbackProgress, utcNow: now);
        switch (decision)
        {
            case PushbackDecision.Wait:
                if (!_pushbackProgress.Requested)
                {
                    _atcRequests.Request(AtcRequestKind.Pushback, "pushback", MenuLinesFor(AtcRequestKind.Pushback));
                    _pushbackProgress = _pushbackProgress with
                    {
                        Requested = true,
                        RequestedUtc = now,
                        StartedUtc = now,
                    };
                    Log("[Pilot] Requesting pushback.");
                }
                t.ThrottleFraction = 0;
                _ap.ParkingBrake(true);
                break;
            case PushbackDecision.Towing:
                t.ThrottleFraction = 0;
                _ap.ParkingBrake(false);
                break;
            case PushbackDecision.Dismiss:
                if (!_pushbackDismissed)
                {
                    _pushbackDismissed = true;
                    _pushbackDismissUtc = now;
                    _pushbackProgress = _pushbackProgress with { Requested = false };
                    if (s.Pushback.Attached)
                    {
                        _aircraft.SimConnect.FireEvent("TUG_DISABLE");
                        Log("[Pilot] Pushback complete; releasing tug.");
                    }
                }
                else if (s.Pushback.Attached)
                {
                    // The tug ignored TUG_DISABLE — escalate like the previous
                    // pilot did instead of firing the event every tick.
                    var waited = now - _pushbackDismissUtc;
                    if (waited > PushbackDetachTimeout && !_pushbackToggleFired)
                    {
                        _pushbackToggleFired = true;
                        _aircraft.SimConnect.FireEvent("TOGGLE_PUSHBACK");
                        Log("[Pilot] Tug still attached; toggling pushback to release.");
                    }
                    if (waited > PushbackDetachTimeout * 3)
                    {
                        _pushbackDone = true;
                        Log("[Pilot] Tug still attached; taxiing out anyway.");
                    }
                }
                _ap.ParkingBrake(true);
                break;
            case PushbackDecision.GiveUp:
                _aircraft.SimConnect.FireEvent("TUG_DISABLE");
                _ap.ParkingBrake(true);
                _pushbackDone = true;
                Log("[Pilot] Pushback unavailable; taxing under own power.");
                break;
        }
    }

    /// <summary>
    /// Supplies the menu lines the screen reader recognises for a request kind.
    /// The reader captures the simulator window and template-matches the ATC
    /// menu's candidate phrases; a reader is cached per kind because template
    /// matching is comparatively expensive.
    /// </summary>
    private Func<IReadOnlyList<int>> MenuLinesFor(AtcRequestKind kind) => () =>
    {
        if (!_menuReaders.TryGetValue(kind, out var reader))
        {
            reader = new AtcMenuReader(_glyphRenderer, kind);
            _menuReaders[kind] = reader;
        }
        return reader.LinesForRequest();
    };

    private void LineUpAndTakeoff(AircraftState s, ControlTargets t)
    {
        // Steer onto the runway threshold, then roll.
        if (_departureAirport is not null && _departureRunwayName is not null)
        {
            var rw = _departureAirport.FindRunway(_departureRunwayName);
            var threshold = rw?.ThresholdFor(_departureRunwayName);
            if (threshold is not null && rw is not null)
            {
                var distToThreshold = GeoMath.DistanceMeters(s.Latitude, s.Longitude, threshold.Value.lat, threshold.Value.lon);
                if (distToThreshold > 30)
                {
                    t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, threshold.Value.lat, threshold.Value.lon);
                    t.ThrottleFraction = 0.08;
                    return;
                }
            }
        }
        if (!TrafficAllowsRunwayEntry()) return;
        SetPhase(FlightPhase.Takeoff);
        _ap.ParkingBrake(false);
        _takeoffClearanceRequested = false;
    }

    // ----- Takeoff -----

    private void ExecuteTakeoff(AircraftState s, ControlTargets t)
    {
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            ExecuteRotorcraftTakeoff(s, t);
            return;
        }
        _ap.SetDirectMode(true);
        var perf = _performance;
        var vr = perf.RotationSpeed > 0 ? perf.RotationSpeed : Math.Max(VsEstimate(s) * 1.15, 55);
        var vy = perf.ClimbSpeed > 0 ? perf.ClimbSpeed : Math.Max(vr * 1.1, 70);

        t.TargetHeadingDeg = _departureAirport is not null && _departureRunwayName is not null
            ? _departureAirport.FindRunway(_departureRunwayName)?.HeadingDegTrue
            : (double?)null;
        t.TargetAirspeedKt = vy;
        t.ThrottleFraction = 1.0;

        // Amphibians go wheels-up for a water departure; land arrivals keep gear.
        if (DepartureOnWater() || (s.GearDown && s.AltitudeAGL > 400 && ArrivalNeedsGear()))
            t.GearDown = false;
        if (s.FlapsHandleIndex > 0 && s.AltitudeAGL > 800) t.FlapIndex = 0;

        // Rotate: hold a gentle pitch-up once at Vr.
        if (s.IndicatedAirspeed >= vr && s.AltitudeAGL < 100)
        {
            t.TargetVsFpm = 1200;
            t.TargetAltitudeFt = null;
        }
        else if (s.AltitudeAGL >= 100)
        {
            t.TargetAltitudeFt = _targetCruiseAltitudeFt;
            t.TargetVsFpm = null;
        }

        if (s.StallWarning && s.AltitudeAGL < 200) t.TargetVsFpm = 1500;
        if (s.OnGround && s.IndicatedAirspeed < vr * 0.8) t.ThrottleFraction = 1.0;
    }

    // ----- Enroute -----

    private void ExecuteEnroute(AircraftState s, ControlTargets t, TurbulenceLevel turbulence)
    {
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            ExecuteRotorcraftEnroute(s, t);
            return;
        }

        // Active racetrack (valley departure / mountain arrival) takes over.
        if (_racetrackActive || ShouldEnterRacetrack(s))
        {
            var wasActive = _racetrackActive;
            ExecuteRacetrack(s, t);
            // Racetrack flew raw heading holds — re-arm the nav latch so the
            // flight plan is re-engaged on the next enroute frame.
            if (wasActive && !_racetrackActive) _ap.ResetNavLatches();
            return;
        }

        _ap.SetDirectMode(false); // native autopilot enroute
        _ap.ApMaster(true);

        var cruiseAlt = _route?.CruiseAltitudeFt ?? _targetCruiseAltitudeFt;
        t.TargetAltitudeFt = cruiseAlt;

        // Lateral guidance: follow the flight plan.
        if (_route is not null && _routeNav is not null && _route.Waypoints.Count > 0)
        {
            var (_, heading, target) = _nav.Update(s, _routeNav);
            t.TargetHeadingDeg = heading;
            t.TrackNav = true;
            LogNavProgress();

            // Slow to turbulence penetration speed when rough.
            if (turbulence is TurbulenceLevel.Moderate or TurbulenceLevel.Severe)
            {
                var reduced = WeatherModel.EnrouteSpeedReductionKt(turbulence, cruiseSpeedKt());
                t.TargetAirspeedKt = reduced;
            }
            else
            {
                t.TargetAirspeedKt = cruiseSpeedKt();
            }

            // Step-climb/descend toward the route altitude for the active leg.
            if (target is { AltitudeConstraint: > 0 })
                t.TargetAltitudeFt = target.AltitudeConstraint;
        }
        else
        {
            t.TargetHeadingDeg = s.TrueHeading;
            t.TargetAirspeedKt = cruiseSpeedKt();
        }

        // In-flight terrain check: terrain within the next ~12 NM may demand a
        // higher altitude than the current target — climb early.
        ApplyTerrainClearance(s, t, aheadNm: 12);

        // Gear: retract once comfortably airborne, re-extend on short final —
        // but never for a water arrival (floatplanes/amphibians wheels-up).
        if (!ArrivalNeedsGear()) t.GearDown = false;
        else if (s.AltitudeAGL > 2500) t.GearDown = false;
        else if (s.AltitudeAGL < 600) t.GearDown = true;
    }

    // ----- Rotorcraft -----

    /// <summary>Transit altitude for rotorcraft: 500 ft AGL short legs, 1,000 ft longer.</summary>
    private double RotorcraftTransitAltitudeFt(AircraftState s)
    {
        var fieldElevFt = Math.Max(
            _departureAirport?.ReferenceAltitudeMeters ?? 0,
            _destinationAirport?.ReferenceAltitudeMeters ?? 0) * GeoMath.FeetPerMeter;
        var dist = _route?.TotalDistanceNm ?? 0;
        return fieldElevFt + (dist < 15 ? 500 : 1000);
    }

    /// <summary>
    /// Rotorcraft ground handling: hover-taxi at ~30 ft AGL and ~25 kt toward a
    /// departure point ~1 NM from the airport reference along the route
    /// bearing, then transition to the takeoff climb. If the aircraft is
    /// already clear of the airport it lifts off in place.
    /// </summary>
    private double _hoverTaxiAltFt;
    private bool _hoverAltCaptured;

    private void ExecuteRotorcraftTaxiOut(AircraftState s, ControlTargets t)
    {
        var ap = _departureAirport;
        var distFromField = ap is not null
            ? GeoMath.DistanceNm(s.Latitude, s.Longitude, ap.ReferenceLatitude, ap.ReferenceLongitude)
            : 0;

        // Already clear of the airport (or no airport data): climb out in place.
        if (ap is null || distFromField > 1.5)
        {
            SetPhase(FlightPhase.Takeoff);
            return;
        }

        // Capture the hover altitude once — parking may sit well above the
        // airport reference elevation, so field+30 could be below the aircraft.
        if (!_hoverAltCaptured)
        {
            _hoverAltCaptured = true;
            _hoverTaxiAltFt = Math.Max(s.Altitude + 40,
                ap.ReferenceAltitudeMeters * GeoMath.FeetPerMeter + 30);
        }

        var (targetLat, targetLon) = RotorcraftDeparturePoint(s);
        var distNm = GeoMath.DistanceNm(s.Latitude, s.Longitude, targetLat, targetLon);

        t.GearDown = false;
        t.FlapIndex = 0;
        t.TargetAltitudeFt = _hoverTaxiAltFt;
        t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, targetLat, targetLon);
        t.TargetAirspeedKt = 25;
        t.ThrottleFraction = 0.4;

        // Arrived at the departure point (or close enough): go to climb-out.
        if (distNm < 0.15 || distFromField > 1.2)
            SetPhase(FlightPhase.Takeoff);
    }

    /// <summary>Point ~1 NM from the airport reference toward the route.</summary>
    private (double lat, double lon) RotorcraftDeparturePoint(AircraftState s)
    {
        var ap = _departureAirport;
        if (ap is null) return (s.Latitude, s.Longitude);
        var next = _routeNav?.Waypoints
            .Skip(_routeNav.CurrentWaypointIndex)
            .FirstOrDefault(w => w.HasPosition && w.Type != WaypointType.Airport)
            ?? _routeNav?.Waypoints.LastOrDefault(w => w.HasPosition);
        var bearing = next is not null
            ? GeoMath.BearingDeg(ap.ReferenceLatitude, ap.ReferenceLongitude, next.Latitude, next.Longitude)
            : (_destinationAirport is not null
                ? GeoMath.BearingDeg(ap.ReferenceLatitude, ap.ReferenceLongitude,
                    _destinationAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude)
                : s.TrueHeading);
        return GeoMath.DestinationNm(ap.ReferenceLatitude, ap.ReferenceLongitude, bearing, 1.0);
    }

    private void ExecuteRotorcraftTakeoff(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        var transit = RotorcraftTransitAltitudeFt(s);
        t.TargetAltitudeFt = transit;
        t.TargetAirspeedKt = null; // hover climb — throttle fraction drives it
        t.ThrottleFraction = 0.75;
        t.TargetHeadingDeg = InitialHeadingTowardRoute(s);
        t.GearDown = false;
    }

    private double? InitialHeadingTowardRoute(AircraftState s)
    {
        var next = _routeNav?.Waypoints
            .Skip(_routeNav.CurrentWaypointIndex)
            .FirstOrDefault(w => w.HasPosition && GeoMath.DistanceNm(s.Latitude, s.Longitude, w.Latitude, w.Longitude) > 0.2);
        if (next is not null)
            return GeoMath.BearingDeg(s.Latitude, s.Longitude, next.Latitude, next.Longitude);
        return _destinationAirport is not null
            ? GeoMath.BearingDeg(s.Latitude, s.Longitude,
                _destinationAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude)
            : (double?)null;
    }

    private void ExecuteRotorcraftEnroute(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true); // direct surface control at low level

        var transit = _route?.CruiseAltitudeFt ?? RotorcraftTransitAltitudeFt(s);
        t.TargetAltitudeFt = transit;

        // Terrain: rotorcraft keep ~800 ft above the highest ground ahead.
        // Only when terrain pushes the target above transit do we check
        // whether the climb can keep up — and slow to ~25 kt if it can't.
        var aheadNm = 8.0;
        var terrainReq = _terrainProfile?.RequiredAheadFt(s.Latitude, s.Longitude, aheadNm) ?? 0;
        var need = terrainReq > 0 ? terrainReq + 300 : 0;
        var climbingForTerrain = need > transit;
        if (climbingForTerrain)
        {
            if (need > _terrainRaiseLoggedFt + 200)
            {
                _terrainRaiseLoggedFt = need;
                Log($"[Pilot] Terrain ahead requires {need:F0} ft — adjusting.");
            }
            t.TargetAltitudeFt = need;
            // Can the aircraft reach it? Estimate climb over the ahead window.
            var climbFpm = _performance.ClimbRateFpm > 0 ? _performance.ClimbRateFpm : 1200;
            var gs = Math.Max(s.GroundSpeed, 25);
            var etaMin = aheadNm / gs * 60.0;
            var reachable = s.Altitude + climbFpm * etaMin;
            t.TargetAirspeedKt = need > reachable ? 25 : Math.Min(cruiseSpeedKt(), 90);
        }
        else
        {
            t.TargetAirspeedKt = Math.Min(cruiseSpeedKt(), 90);
        }

        if (_route is not null && _routeNav is not null && _route.Waypoints.Count > 0)
        {
            var (_, heading, _) = _nav.Update(s, _routeNav);
            t.TargetHeadingDeg = heading;
            t.TrackNav = true;
            LogNavProgress();
        }
        else
        {
            t.TargetHeadingDeg = InitialHeadingTowardRoute(s) ?? s.TrueHeading;
        }
        t.ThrottleFraction = 0.55; // PID trims around this for the speed target
    }

    // ----- Terrain avoidance -----

    /// <summary>
    /// Raises the target altitude when terrain within <paramref name="aheadNm"/>
    /// demands more than the current target (in-flight look-ahead).
    /// </summary>
    private void ApplyTerrainClearance(AircraftState s, ControlTargets t, double aheadNm)
    {
        var p = _terrainProfile;
        if (p is null || t.TargetAltitudeFt is null) return;
        var req = p.RequiredAheadFt(s.Latitude, s.Longitude, aheadNm);
        if (req <= 0) return;
        var need = req + (_aircraftKind == AircraftKind.Rotorcraft ? 300 : 0);
        if (need <= t.TargetAltitudeFt.Value) return;
        if (need > _terrainRaiseLoggedFt + 200 || need > t.TargetAltitudeFt.Value + 1)
        {
            _terrainRaiseLoggedFt = Math.Max(_terrainRaiseLoggedFt, need);
            Log($"[Pilot] Terrain ahead requires {need:F0} ft — climbing.");
        }
        t.TargetAltitudeFt = need;
    }

    /// <summary>
    /// True when the aircraft should hold a racetrack instead of descending:
    /// (a) departure — terrain ahead on the departure track needs more altitude
    /// than the climb can reach by arrival time (valley departure); or
    /// (b) arrival — inside 25 NM of the destination and terrain still demands
    /// more than the approach floor (mountain arrival).
    /// </summary>
    private bool ShouldEnterRacetrack(AircraftState s)
    {
        var p = _terrainProfile;
        if (p is null) return false;
        var phase = Status.Phase;

        // Departure case: early climb phases, still near the departure field.
        if (phase is FlightPhase.InitialClimb or FlightPhase.Climb &&
            _departureAirport is not null &&
            GeoMath.DistanceNm(s.Latitude, s.Longitude,
                _departureAirport.ReferenceLatitude, _departureAirport.ReferenceLongitude) < 60)
        {
            var req = p.RequiredAheadFt(s.Latitude, s.Longitude, 15);
            if (req > 0)
            {
                var climbFpm = _performance.ClimbRateFpm > 0 ? _performance.ClimbRateFpm : 1200;
                var gs = Math.Max(s.GroundSpeed, cruiseSpeedKt());
                var etaMin = 15.0 / gs * 60.0;
                var reachable = s.Altitude + climbFpm * etaMin;
                if (req > reachable - 200) return EnterRacetrack(s, req, DepartureTrackHeading(s));
            }
        }

        // Arrival case: descent near the destination, terrain above approach floor.
        if (phase == FlightPhase.Descent &&
            _destinationAirport is not null &&
            GeoMath.DistanceNm(s.Latitude, s.Longitude,
                _destinationAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude) < 25)
        {
            var floor = _route?.FinalApproachFix?.Altitude
                        ?? (_destinationAirport.ReferenceAltitudeMeters * GeoMath.FeetPerMeter) + 300;
            var req = p.RequiredAheadFt(s.Latitude, s.Longitude, 10);
            if (req > floor + 200) return EnterRacetrack(s, req, ArrivalTrackHeading(s));
        }
        return false;
    }

    private bool EnterRacetrack(AircraftState s, double requiredFt, double headingDeg)
    {
        _racetrack.Reset(s.Latitude, s.Longitude);
        _racetrackActive = true;
        _racetrackSinceUtc = DateTime.UtcNow;
        _racetrackHeadingDeg = headingDeg;
        _racetrackTargetAltFt = Math.Max(requiredFt + 200, s.Altitude + 500);
        Log($"[Pilot] Racetrack: terrain requires {requiredFt:F0} ft — holding at {_racetrackTargetAltFt:F0} ft.");
        return true;
    }

    private double DepartureTrackHeading(AircraftState s)
    {
        if (_departureAirport is null) return s.TrueHeading;
        if (_departureRunwayName is not null)
        {
            var h = _departureAirport.FindRunway(_departureRunwayName)?.HeadingDegTrue;
            if (h is not null) return h.Value;
        }
        return _destinationAirport is not null
            ? GeoMath.BearingDeg(_departureAirport.ReferenceLatitude, _departureAirport.ReferenceLongitude,
                _destinationAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude)
            : s.TrueHeading;
    }

    private double ArrivalTrackHeading(AircraftState s)
    {
        if (_destinationAirport is null) return s.TrueHeading;
        if (_arrivalRunwayName is not null)
        {
            var h = _destinationAirport.FindRunway(_arrivalRunwayName)?.HeadingDegTrue;
            if (h is not null) return h.Value;
        }
        return _departureAirport is not null
            ? GeoMath.BearingDeg(_destinationAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude,
                _departureAirport.ReferenceLatitude, _destinationAirport.ReferenceLongitude)
            : s.TrueHeading;
    }

    /// <summary>Flies the racetrack until terrain permits descent or the cap hits.</summary>
    private void ExecuteRacetrack(AircraftState s, ControlTargets t)
    {
        var heading = _racetrack.Update(s.Latitude, s.Longitude, _racetrackHeadingDeg) ?? _racetrackHeadingDeg;
        t.TargetHeadingDeg = heading;
        t.TargetAltitudeFt = _racetrackTargetAltFt;
        t.TargetAirspeedKt = cruiseSpeedKt();
        ApplyTerrainClearance(s, t, aheadNm: 15);
        t.TargetAltitudeFt = Math.Max(t.TargetAltitudeFt ?? 0, _racetrackTargetAltFt);

        var timedOut = DateTime.UtcNow - _racetrackSinceUtc > TimeSpan.FromMinutes(10);
        // Prefer the nav route: hand control back to the flight-plan guidance
        // once both the immediate path and the wider look-ahead window are
        // terrain-clear — the normal step-climb logic takes over from there.
        var immediateClear = (_terrainProfile?.RequiredAheadFt(s.Latitude, s.Longitude, 5) ?? 0)
                             <= s.Altitude + 300;
        var terrainClear = TerrainPermitsDescent(s);
        if (timedOut || (terrainClear && immediateClear))
        {
            _racetrackActive = false;
            Log(timedOut
                ? "[Pilot] Racetrack time cap reached — resuming route."
                : "[Pilot] Terrain cleared — resuming route.");
        }
    }

    /// <summary>True when terrain ahead no longer exceeds what we can safely fly.</summary>
    private bool TerrainPermitsDescent(AircraftState s)
    {
        var p = _terrainProfile;
        if (p is null) return true;
        var phase = Status.Phase;
        if (phase is FlightPhase.InitialClimb or FlightPhase.Climb)
        {
            var req = p.RequiredAheadFt(s.Latitude, s.Longitude, 15);
            return req <= s.Altitude + 300;
        }
        if (phase == FlightPhase.Descent)
        {
            var floor = _route?.FinalApproachFix?.Altitude
                        ?? (_destinationAirport?.ReferenceAltitudeMeters ?? 0) * GeoMath.FeetPerMeter + 300;
            var req = p.RequiredAheadFt(s.Latitude, s.Longitude, 10);
            return req <= floor + 200;
        }
        return true;
    }

    private double cruiseSpeedKt() =>
        _performance.CruiseSpeed > 0 ? _performance.CruiseSpeed : 140;

    private double DescentDistanceNm()
    {
        var alt = _targetCruiseAltitudeFt;
        var vs = _performance.DescentRateFpm > 0 ? _performance.DescentRateFpm : 1500;
        var minutes = alt / vs;
        return minutes * Math.Max(cruiseSpeedKt(), 100) / 60.0;
    }

    private double RemainingDistanceNm(AircraftState s)
    {
        if (_route is null) return 0;
        var target = _route.RunwayWaypoint ?? _route.Waypoints.LastOrDefault();
        if (target is null || !target.HasPosition) return 0;
        return GeoMath.DistanceNm(s.Latitude, s.Longitude, target.Latitude, target.Longitude);
    }

    private double EtaMinutes(AircraftState s)
    {
        var dist = RemainingDistanceNm(s);
        var gs = Math.Max(s.GroundSpeed, 60);
        return dist / gs * 60;
    }

    // ----- Descent -----

    private void ExecuteDescent(AircraftState s, ControlTargets t)
    {
        // Mountain arrival: hold a racetrack until terrain permits descent.
        if (_aircraftKind != AircraftKind.Rotorcraft && (_racetrackActive || ShouldEnterRacetrack(s)))
        {
            var wasActive = _racetrackActive;
            ExecuteRacetrack(s, t);
            if (wasActive && !_racetrackActive) _ap.ResetNavLatches();
            return;
        }

        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            _ap.SetDirectMode(true);
            t.TargetAirspeedKt = 60;
            t.TargetAltitudeFt = _route?.FinalApproachFix?.Altitude
                                 ?? RotorcraftTransitAltitudeFt(s) - 400;
            var fafAlt = t.TargetAltitudeFt ?? s.Altitude;
            t.TargetVsFpm = -Math.Clamp((fafAlt - s.Altitude) * 4, -800, -200);
            ApplyTerrainClearance(s, t, aheadNm: 8);
            return;
        }

        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        t.TargetAirspeedKt = Math.Min(cruiseSpeedKt(), 160);
        var vs = -Math.Min(_performance.DescentRateFpm > 0 ? _performance.DescentRateFpm : 1500, 2000);
        t.TargetVsFpm = vs;
        t.TargetAltitudeFt = _route?.FinalApproachFix?.Altitude ?? _targetCruiseAltitudeFt * 0.3;
        ApplyTerrainClearance(s, t, aheadNm: 10);
        if (s.GearDown == false && s.AltitudeAGL < 3000 && ArrivalNeedsGear()) t.GearDown = true;
    }

    // ----- Approach / final -----

    private void ExecuteApproach(AircraftState s, ControlTargets t)
    {
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            ExecuteRotorcraftApproach(s, t);
            return;
        }
        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        t.TrackNav = true;
        t.TargetAirspeedKt = _performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 90;
        var fafAlt = _route?.FinalApproachFix?.Altitude ?? s.Altitude;
        t.TargetAltitudeFt = fafAlt;
        t.TargetVsFpm = -Math.Min(1200, Math.Abs(fafAlt - (_destinationAirport?.ReferenceAltitudeMeters ?? 0) * GeoMath.FeetPerMeter) / 5);
        if (!s.GearDown && ArrivalNeedsGear()) t.GearDown = true;
        if (s.FlapsHandleIndex < 1) t.FlapIndex = 1;
    }

    /// <summary>
    /// Rotorcraft arrival: ~300 ft over the field, ~30 kt, then a vertical
    /// set-down onto the airport reference point (aerotto-style hover landing).
    /// </summary>
    private void ExecuteRotorcraftApproach(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        var dstElevFt = (_destinationAirport?.ReferenceAltitudeMeters ?? 0) * GeoMath.FeetPerMeter;
        var target = _route?.RunwayWaypoint;
        var distNm = target is { HasPosition: true }
            ? GeoMath.DistanceNm(s.Latitude, s.Longitude, target.Latitude, target.Longitude)
            : 0;

        t.GearDown = false;
        t.FlapIndex = 0;
        t.TargetAirspeedKt = distNm > 1.0 ? 30 : null;
        t.ThrottleFraction = 0.55;

        if (distNm > 1.0)
        {
            // Still outbound: follow the route, descend to ~300 ft AGL.
            if (_routeNav is not null)
            {
                var (_, heading, _) = _nav.Update(s, _routeNav);
                t.TargetHeadingDeg = heading;
                t.TrackNav = true;
            }
            else if (target is { HasPosition: true })
            {
                t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, target.Latitude, target.Longitude);
            }
            t.TargetAltitudeFt = dstElevFt + 300;
            t.TargetVsFpm = -Math.Clamp((dstElevFt + 300 - s.Altitude) * 4, -600, -150);
            return;
        }

        // Overhead: descend vertically onto the pad.
        t.TargetHeadingDeg = s.TrueHeading;
        t.TargetAltitudeFt = dstElevFt + 20;
        if (s.AltitudeAGL > 60) t.TargetVsFpm = -150;
        else if (s.AltitudeAGL > 15) t.TargetVsFpm = -80;
        else t.TargetVsFpm = -30; // flare / set down
        t.ThrottleFraction = s.AltitudeAGL < 30 ? 0.35 : 0.5;

        if (s.OnGround) SetPhase(FlightPhase.Rollout);
    }

    private void ExecuteFinalApproach(AircraftState s, ControlTargets t, TurbulenceLevel turbulence)
    {
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            ExecuteRotorcraftApproach(s, t);
            return;
        }
        _ap.SetDirectMode(true); // precision: direct control on short final
        var rw = _arrivalRunwayName is not null && _destinationAirport is not null
            ? _destinationAirport.FindRunway(_arrivalRunwayName) : null;
        var threshold = rw?.ThresholdFor(_arrivalRunwayName ?? "") ??
                        (_route?.RunwayWaypoint is { HasPosition: true } wp ? (wp.Latitude, wp.Longitude) : ((double, double)?)null);

        if (threshold is not null)
        {
            // Fly the extended centreline to the threshold.
            var brg = rw is not null
                ? rw.HeadingDegTrue
                : GeoMath.BearingDeg(s.Latitude, s.Longitude, threshold.Value.Item1, threshold.Value.Item2);
            t.TargetHeadingDeg = brg;
        }

        var vapp = _performance.Vapp > 0 ? _performance.Vapp : (_performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 80);
        // Add wind/ice/turbulence increments.
        var (headwind, _) = WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, t.TargetHeadingDeg ?? s.TrueHeading);
        vapp += WeatherModel.ApproachWindIncrementKt(Math.Max(0, headwind))
              + WeatherModel.ApproachIceIncrementKt(s.StructuralIcePercent)
              + WeatherModel.ApproachTurbulenceIncrementKt(turbulence);
        t.TargetAirspeedKt = vapp;

        // Another aircraft on the runway or ahead on final: go around once per
        // approach rather than land through occupied airspace.
        var ta = _trafficAssessment;
        if (!_goAroundForTraffic &&
            (ta.DepartureRunwayOccupant is not null || ta.ArrivalOnDepartureRunway is not null))
        {
            _goAroundForTraffic = true;
            Log($"[Traffic] go-around: {ta.Describe()}");
            SetPhase(FlightPhase.GoAround);
            return;
        }

        // Glidepath: ~3° to the threshold.
        if (threshold is not null)
        {
            var distNm = GeoMath.DistanceNm(s.Latitude, s.Longitude, threshold.Value.Item1, threshold.Value.Item2);
            var threshAlt = (_route?.RunwayWaypoint?.Altitude ?? 0);
            var desiredAlt = threshAlt + distNm * 6076 * Math.Tan(3 * Math.PI / 180); // ft per nm at 3°
            t.TargetAltitudeFt = desiredAlt;
            t.TargetVsFpm = -Math.Clamp((desiredAlt - s.Altitude) * 6, -1500, -300);
        }

        if (s.FlapsHandleIndex < 2) t.FlapIndex = 2;
        // Water arrival: wheels up (amphibians), floatplanes have no gear.
        if (!ArrivalNeedsGear()) t.GearDown = false;
        else if (!s.GearDown) t.GearDown = true;

        // Flare and rollout handled by touchdown detection in AdvancePhase.
        if (s.OnGround) SetPhase(FlightPhase.Rollout);
    }

    private void ExecuteGoAround(AircraftState s, ControlTargets t)
    {
        if (_aircraftKind == AircraftKind.Rotorcraft)
        {
            // Climb back to transit altitude, then re-approach.
            _ap.SetDirectMode(true);
            t.TargetAltitudeFt = RotorcraftTransitAltitudeFt(s);
            t.TargetVsFpm = 800;
            t.TargetAirspeedKt = 60;
            var next = _routeNav?.Waypoints
                .Skip(_routeNav.CurrentWaypointIndex)
                .FirstOrDefault(w => w.HasPosition && w.Type != WaypointType.MissedApproach);
            if (next is not null)
                t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, next.Latitude, next.Longitude);
            t.GearDown = false;
            return;
        }
        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        // Fly the published missed approach first when the route carries one.
        var map = _route?.MissedApproachFix;
        if (map is not null && s.AltitudeAGL < 3000)
        {
            var mapDist = GeoMath.DistanceNm(s.Latitude, s.Longitude, map.Latitude, map.Longitude);
            if (mapDist > 1.0)
                t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, map.Latitude, map.Longitude);
        }
        t.TargetAltitudeFt = _targetCruiseAltitudeFt;
        t.TargetAirspeedKt = _performance.ClimbSpeed > 0 ? _performance.ClimbSpeed : 90;
        if (ArrivalNeedsGear() && !s.GearDown) t.GearDown = true;
        if (s.FlapsHandleIndex > 0) t.FlapIndex = 0;
    }

    // ----- Rollout / taxi-in -----

    private void ExecuteRollout(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        t.ThrottleFraction = 0;
        t.TargetHeadingDeg = s.TrueHeading;
        if (s.GroundSpeed < 60)
        {
            _ap.ParkingBrake(s.GroundSpeed < 20);
            if (s.GroundSpeed < 5 && s.FlapsHandleIndex > 0) t.FlapIndex = 0;
        }
        if (!s.GearDown && s.AltitudeAGL < 20) { /* stay down until stopped */ }
    }

    private void ExecuteTaxiIn(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        _ap.ParkingBrake(false);

        if (_destinationAirport is null) { SetPhase(FlightPhase.Shutdown); return; }

        // Route to the nearest parking node.
        if (_taxiRoute is null || _taxiLegIndex >= _taxiRoute.Legs.Count && !_taxiInPlanned)
        {
            PlanTaxiIn(s);
            _taxiInPlanned = true;
        }
        if (_taxiRoute is null) { SetPhase(FlightPhase.Shutdown); return; }

        var legs = _taxiRoute.Legs;
        if (_taxiLegIndex >= legs.Count)
        {
            _ap.ParkingBrake(true);
            _ap.SetThrottleFraction(0);
            if (_status.Mode == PilotMode.Continuous) BeginTurnaround(s);
            else SetPhase(FlightPhase.Shutdown);
            return;
        }

        var leg = legs[_taxiLegIndex];
        var toNode = _destinationAirport.FindNode(leg.ToNode);
        if (toNode is null) { _taxiLegIndex++; return; }
        var dist = GeoMath.DistanceMeters(s.Latitude, s.Longitude, toNode.Latitude, toNode.Longitude);
        t.TargetHeadingDeg = GeoMath.BearingDeg(s.Latitude, s.Longitude, toNode.Latitude, toNode.Longitude);
        t.ThrottleFraction = dist > 60 ? 0.15 : dist > 25 ? 0.08 : 0.03;
        if (dist < 10) _taxiLegIndex++;
    }
    private bool _taxiInPlanned;

    private void PlanTaxiIn(AircraftState s)
    {
        if (_destinationAirport is null) return;
        var start = TaxiRouter.FindNearestNode(_destinationAirport, s.Latitude, s.Longitude);
        var parking = TaxiRouter.FindNearestParking(_destinationAirport, s.Latitude, s.Longitude);
        if (start is null || parking is null) { _taxiRoute = null; return; }
        var route = TaxiRouter.FindRoute(_destinationAirport, start.Index, parking.Index,
            new TaxiConstraints { AircraftWeightLbs = (uint)s.GrossWeight });
        if (route.Success)
        {
            _taxiRoute = route;
            _taxiLegIndex = 0;
            Log($"[Pilot] Taxi-in route: {route.Describe()}");
        }
        else _taxiRoute = null;
    }

    // ---------------- Mission planning ----------------

    /// <summary>Plans and loads a flight from the current position/airport to a destination.</summary>
    public async Task<bool> PlanFlightAsync(AirportData destination, CancellationToken ct = default)
    {
        var s = _aircraft.GetSnapshot();
        // Same gate as continuous planning — acquire before mutating any
        // shared fields so a rejected call leaves state untouched.
        if (Interlocked.Exchange(ref _routePlanning, 1) == 1) return false;
        try
        {
            _departureAirport ??= await FindNearestAirportAsync(s, ct);
            if (_departureAirport is null) { Log("[Pilot] Cannot determine departure airport."); return false; }
            _destinationAirport = destination;
            return await BuildAndLoadRouteAsync(s, ct);
        }
        finally { Interlocked.Exchange(ref _routePlanning, 0); }
    }

    private async Task<bool> BuildAndLoadRouteAsync(AircraftState s, CancellationToken ct = default)
    {
        if (_departureAirport is null || _destinationAirport is null) return false;

        var rotorcraft = _aircraftKind == AircraftKind.Rotorcraft;
        // Pick runways into the wind; rotorcraft don't use runways at all.
        if (!rotorcraft)
        {
            _departureRunwayName ??= ContinuousFlightPlanner.PickRunway(_departureAirport, s.WindDirection,
                _takeoffRunMeters(s),
                waterOnly: _aircraftKind == AircraftKind.Seaplane,
                preferWater: _aircraftKind == AircraftKind.Amphibian)?.name;
            _arrivalRunwayName ??= ContinuousFlightPlanner.PickRunway(_destinationAirport, s.WindDirection,
                _landingRunMeters(s),
                waterOnly: _aircraftKind == AircraftKind.Seaplane,
                preferWater: _aircraftKind == AircraftKind.Amphibian)?.name;
        }
        else
        {
            _departureRunwayName = null;
            _arrivalRunwayName = null;
        }

        var req = new RouteRequest
        {
            Departure = _departureAirport,
            Destination = _destinationAirport,
            DepartureRunway = _departureRunwayName,
            DestinationRunway = _arrivalRunwayName,
            CruiseAltitudeFt = rotorcraft ? 0 : ChooseCruiseAltitude(s),
            ClimbRateFpm = _performance.ClimbRateFpm > 0 ? _performance.ClimbRateFpm : 1500,
            DescentRateFpm = _performance.DescentRateFpm > 0 ? _performance.DescentRateFpm : 1500,
            ApproachSpeedKt = rotorcraft ? 30 : (_performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 90),
            CruiseSpeedKt = rotorcraft ? Math.Min(cruiseSpeedKt(), 90) : cruiseSpeedKt(),
            AircraftKind = _aircraftKind,
        };
        _route = RouteBuilder.Build(req);
        _goAroundForTraffic = false;
        _racetrackActive = false;
        _terrainRaiseLoggedFt = 0;
        _hoverAltCaptured = false;
        _nav.Reset();

        // Terrain analysis: sample elevations along the route, raise cruise and
        // enroute waypoint altitudes to the required floor. Falls back to
        // airport+3000 ft when elevation data is unavailable.
        await AnalyzeTerrainAsync(s, ct);
        _targetCruiseAltitudeFt = _route.CruiseAltitudeFt;

        // Load the route into the sim's own flight plan so the GPS follows it.
        try
        {
            var plnPath = Path.Combine(Path.GetTempPath(), "aicopilot_flight.pln");
            PlnFile.Write(plnPath, "AI Pilot",
                _departureAirport.Icao, _destinationAirport.Icao,
                _departureAirport.Name, _destinationAirport.Name,
                _route.CruiseAltitudeFt, _route.Waypoints);
            _aircraft.SimConnect.FlightPlanLoad(plnPath);
            Log($"[Pilot] Route loaded: {_departureAirport.Icao} → {_destinationAirport.Icao} ({_route.TotalDistanceNm:F0} nm)");
        }
        catch (Exception ex)
        {
            Log($"[Pilot] Flight plan load failed: {ex.Message}");
        }

        // Keep our own navigation state for guidance (the sim doesn't expose the
        // full flight plan over SimConnect).
        _routeNav = new NavigationState
        {
            Waypoints = new List<FlightPlanWaypoint>(_route.Waypoints),
            CurrentWaypointIndex = 0,
            DepartureIcao = _departureAirport.Icao,
            ArrivalIcao = _destinationAirport.Icao,
            CruiseAltitudeFt = _route.CruiseAltitudeFt,
            DepartureAirportName = _departureAirport.Name,
            ArrivalAirportName = _destinationAirport.Name,
        };

        lock (_lock)
        {
            _status.DepartureIcao = _departureAirport.Icao;
            _status.DestinationIcao = _destinationAirport.Icao;
            _status.DepartureRunway = _departureRunwayName ?? "";
            _status.DestinationRunway = _arrivalRunwayName ?? "";
            _status.StatusText = $"Flying {_departureAirport.Icao} → {_destinationAirport.Icao}";
        }
        PublishStatus();
        return true;
    }

    /// <summary>
    /// Samples terrain along the route and raises cruise/enroute altitudes to
    /// the required clearance floor. Falls back to airport+3000 ft when the
    /// elevation service is unavailable — never silently assumes flat terrain.
    /// </summary>
    private async Task AnalyzeTerrainAsync(AircraftState s, CancellationToken ct)
    {
        _terrainProfile = null;
        if (_route is null) return;

        var routePts = _route.Waypoints
            .Where(w => w.HasPosition)
            .Select(w => (w.Latitude, w.Longitude))
            .ToList();
        if (routePts.Count < 2) return;

        var samplePts = TerrainProfile.SampleRoutePoints(routePts);
        IReadOnlyList<double>? elevations = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            elevations = await _terrainElevations.GetElevationsAsync(samplePts, timeout.Token);
        }
        catch { /* network/parse failure → fallback below */ }

        if (elevations is not null && elevations.Count == samplePts.Count)
        {
            var samples = samplePts
                .Select((p, i) => new TerrainSample(p.lat, p.lon, elevations[i]))
                .ToList();
            _terrainProfile = TerrainProfile.Build(samples);
        }
        else
        {
            var maxAirportElevM = Math.Max(
                _departureAirport?.ReferenceAltitudeMeters ?? 0,
                _destinationAirport?.ReferenceAltitudeMeters ?? 0);
            _terrainProfile = TerrainProfile.Fallback(maxAirportElevM, routePts);
            Log("[Pilot] Terrain elevation data unavailable — using airport+3000 ft clearance fallback.");
        }
        if (_terrainProfile is null) return;

        var floor = _terrainProfile.MaxRequiredAltitudeFt;
        if (_terrainProfile.Mountainous)
            Log("[Pilot] Mountainous terrain on route — 2000 ft margin applied over high ground.");
        if (_terrainProfile.IsFallback)
            Log($"[Pilot] Terrain fallback floor: {floor:F0} ft MSL.");

        // Rotorcraft fly low by design; their in-flight check handles terrain.
        if (_aircraftKind == AircraftKind.Rotorcraft) return;

        if (floor > _route.CruiseAltitudeFt)
        {
            Log($"[Pilot] Terrain requires {floor:F0} ft — raising cruise from {_route.CruiseAltitudeFt:F0} ft.");
            _route.CruiseAltitudeFt = floor;
            foreach (var w in _route.Waypoints.Where(w =>
                         w.Type is WaypointType.Intersection or WaypointType.User))
            {
                if (w.Altitude < floor) w.Altitude = floor;
            }
        }
        if (floor > 20000)
            Log($"[Pilot] Warning: terrain requires {floor:F0} ft — may exceed this aircraft's ceiling.");
    }

    private double _landingRunMeters(AircraftState s)
    {
        if (AircraftKindDetector.IsWaterKind(_aircraftKind))
            return _aircraftKind == AircraftKind.Seaplane ? 500 : 600;
        var weight = s.GrossWeight > 0 ? s.GrossWeight : _performance.MaxLandingWeight;
        if (_performance.HasPerformanceData && weight > 0)
            return _performance.RequiredLandingRunwayFt(weight) * GeoMath.FeetPerMeter;
        return 800;
    }

    private double ChooseCruiseAltitude(AircraftState s)
    {
        // Rough safe altitude: transition altitude + margin, capped by aircraft.
        var baseAlt = (_departureAirport?.TransitionAltitudeMeters ?? 3000) * GeoMath.FeetPerMeter;
        var alt = Math.Ceiling(baseAlt / 1000) * 1000 + 2000;
        return Math.Min(alt, 18000);
    }

    // ---------------- Continuous flights ----------------

    /// <summary>
    /// Plans at most one leg at a time. The turnaround timer and the Shutdown
    /// retry can both fire near the end of a service window — without the gate
    /// they would pick destinations and build routes concurrently.
    /// </summary>
    private int _routePlanning;

    private async Task<bool> TryPlanNextLegAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _routePlanning, 1) == 1) return false;
        try { return await PlanNextLegAsync(ct); }
        finally { Interlocked.Exchange(ref _routePlanning, 0); }
    }

    private async Task<bool> PlanNextLegAsync(CancellationToken ct)
    {
        var s = _aircraft.GetSnapshot();
        if (!await _planner.WaitForAirportListAsync(TimeSpan.FromSeconds(10), ct))
        {
            Log("[Pilot] Airport list not available yet; retrying later.");
            return false;
        }

        // Range: fuel-limited still-air range estimate.
        var rangeNm = EstimateRangeNm(s);
        var dep = _destinationAirport ?? _departureAirport ?? await FindNearestAirportAsync(s, ct);
        if (dep is null) { Log("[Pilot] No departure airport for next leg."); return false; }

        var dest = await _planner.PickDestinationAsync(dep.ReferenceLatitude, dep.ReferenceLongitude, rangeNm,
            TimeSpan.FromSeconds(60), ct, _aircraftKind);
        if (dest is null) { Log("[Pilot] No suitable destination found in range."); return false; }

        _departureAirport = dep;
        _destinationAirport = dest;
        _departureRunwayName = null;
        _arrivalRunwayName = null;
        var ok = await BuildAndLoadRouteAsync(s, ct);
        if (ok) _legsFlown++;
        return ok;
    }

    private double EstimateRangeNm(AircraftState s)
    {
        // Still-air range ≈ fuel weight / fuel flow * speed. Rough but serviceable.
        if (s.EngineFF.Length == 0 || s.EngineFF.All(f => f <= 0)) return _planner.MaxRangeNm;
        var ffTotal = s.EngineFF.Sum();
        var fuelLb = s.TotalFuelWeight > 0 ? s.TotalFuelWeight : _performance.MaxFuelWeight;
        if (ffTotal <= 0 || fuelLb <= 0) return _planner.MaxRangeNm;
        var hours = fuelLb / ffTotal * 0.85; // 85% usable
        var gs = Math.Max(s.GroundSpeed, cruiseSpeedKt());
        return hours * gs * 0.9; // reserve
    }

    private void BeginTurnaround(AircraftState s)
    {
        // Parked at the destination: service, then plan the next leg.
        _ap.ParkingBrake(true);
        _serviceUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(_serviceMinutes);
        _departureAirport = _destinationAirport;
        _destinationAirport = null;
        _route = null;
        _terrainProfile = null;
        _racetrackActive = false;
        _hoverAltCaptured = false;
        _taxiRoute = null;
        _taxiInPlanned = false;
        _pushbackDone = false;
        _pushbackDismissed = false;
        _pushbackToggleFired = false;
        _taxiClearanceRequested = false;
        _takeoffClearanceRequested = false;
        _checklists.Reset();
        SetPhase(FlightPhase.Shutdown);
        Log($"[Pilot] Turnaround: servicing for {_serviceMinutes} min, then next leg.");
        PublishStatus();

        // Kick off next-leg planning after the service window.
        _ = Task.Run(async () =>
        {
            try
            {
                while (_serviceUntilUtc > DateTime.UtcNow) await Task.Delay(1000);
                if (ContinuousEnabled)
                {
                    await TryPlanNextLegAsync(CancellationToken.None);
                    if (_route is not null) SetPhase(FlightPhase.Preflight);
                }
            }
            catch (Exception ex) { Log($"[Pilot] Next-leg planning failed: {ex.Message}"); }
        });
    }

    // ---------------- Aircraft identity / performance ----------------

    private void OnTitleChanged(string title)
    {
        lock (_lock) { _status.AircraftTitle = title; }
        PublishStatus();
    }

    private void OnAircraftDirectoryChanged(string dir)
    {
        try
        {
            _performance = AircraftConfigParser.ParseAircraftConfig(dir);
            LoadChecklists();
            _aircraftKind = AircraftKindDetector.Detect(dir, _aircraft.AircraftTitle, _performance);
            _planner.Kind = _aircraftKind;
            lock (_lock) _status.AircraftKind = _aircraftKind;
            Log($"[Pilot] Aircraft: {_performance.Title} ({_performance.IcaoType}) — {_aircraftKind}");

            if (_aircraftKind == AircraftKind.Rotorcraft)
            {
                _planner.RequiredRunwayMeters = 0;
                _planner.MinRangeNm = 10;
            }
            else if (AircraftKindDetector.IsWaterKind(_aircraftKind))
            {
                Log($"[Pilot] {_aircraftKind}: water runways preferred for all legs.");
            }
        }
        catch (Exception ex) { Log($"[Pilot] Performance parse failed: {ex.Message}"); }
    }

    /// <summary>
    /// Loads the aircraft's own checklist set (2024 SimBase XML or legacy text
    /// format, with checkpoint libraries and .loc localization), falling back to
    /// the built-in procedural flow when the package ships none.
    /// </summary>
    private void LoadChecklists()
    {
        // The aircraft directory is already resolved by SimAircraft (the
        // AircraftLoaded path hint); pass it as the override so the parser
        // does not rescan every package root by title.
        var title = _aircraft.AircraftTitle;
        if (string.IsNullOrWhiteSpace(title) || title.StartsWith("$$", StringComparison.Ordinal))
            title = _performance.Title;
        var set = ChecklistParser.ParseFromAircraft(title,
            rootOverride: string.IsNullOrWhiteSpace(_aircraft.AircraftDirectory) ? null : _aircraft.AircraftDirectory);
        if (!set.IsLoaded)
        {
            var turbine = _performance.EngineType.Contains("TURBO", StringComparison.OrdinalIgnoreCase) ||
                          _performance.EngineType.Contains("JET", StringComparison.OrdinalIgnoreCase) ||
                          _aircraftKind == AircraftKind.Rotorcraft;
            var engines = _performance.EngineCount > 0 ? _performance.EngineCount : 1;
            set = GenericChecklists.Create(title, turbine, engines);
        }
        foreach (var note in set.Notes.Take(3)) Log($"[Checklist] {note}");
        _checklists.Load(set);
        Log($"[Checklist] Loaded {set.Lists.Count} lists ({set.Format}).");
    }

    private void OnStateUpdated(AircraftState s)
    {
        PublishStatusThrottled();
    }

    private string? _logFilePath;
    private readonly object _logFileLock = new();
    private int _logFileLines;

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        try { OnLog?.Invoke(line); } catch { /* a dead subscriber must not break the loop */ }
        // Mirror every line to a daily log file for post-flight analysis.
        // Bounded size: once a day-file exceeds ~4 MB it is rotated aside so
        // continuous flights cannot grow it without limit.
        try
        {
            lock (_logFileLock)
            {
                if (_logFilePath is null)
                {
                    var dir = Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "AICopilotFS");
                    Directory.CreateDirectory(dir);
                    _logFilePath = Path.Combine(dir, $"pilot-{DateTime.Now:yyyyMMdd}.log");
                }
                if (_logFileLines >= 60000)
                {
                    var rotated = _logFilePath + ".1";
                    try { File.Delete(rotated); } catch { }
                    try { File.Move(_logFilePath, rotated); } catch { }
                    _logFileLines = 0;
                }
                File.AppendAllText(_logFilePath, line + System.Environment.NewLine);
                _logFileLines++;
            }
        }
        catch { /* logging must never break the flight loop */ }
    }

    private void PublishStatus()
    {
        PilotStatus copy;
        var checklist = _checklists.Status();
        lock (_lock)
        {
            copy = new PilotStatus
            {
                Mode = _status.Mode,
                Control = _status.Control,
                Phase = _status.Phase,
                StatusText = _status.StatusText,
                AircraftTitle = _status.AircraftTitle,
                AircraftKind = _status.AircraftKind,
                DepartureIcao = _status.DepartureIcao,
                DestinationIcao = _status.DestinationIcao,
                DepartureRunway = _status.DepartureRunway,
                DestinationRunway = _status.DestinationRunway,
                DistanceToGoNm = _status.DistanceToGoNm,
                EtaMinutes = _status.EtaMinutes,
                ServiceMinutesRemaining = _status.ServiceMinutesRemaining,
                ChecklistActive = checklist.Loaded && checklist.Enabled,
                CurrentChecklistItem = checklist.Loaded && checklist.Item.Length > 0
                    ? checklist.Item + (checklist.Expectation.Length > 0 ? $" — {checklist.Expectation}" : "") : "",
                ChecklistPhaseName = checklist.Loaded && checklist.Step.Length > 0
                    ? checklist.Step : checklist.Source,
                ChecklistProgress = checklist.Loaded && checklist.Count > 0
                    ? (double)checklist.Index / checklist.Count : 0,
                TrafficSummary = _status.TrafficSummary,
                TrafficContacts = _status.TrafficContacts,
                LastError = _status.LastError,
            };
        }
        OnStatusChanged?.Invoke(copy);
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { }
        Disengage();
        _checklistHost.Dispose();
        _aircraft.SimConnect.OnDataReceived -= _traffic.OnData;
        _traffic.Dispose();
        _facilities.Dispose();
        _aircraft.Dispose();
    }
}


