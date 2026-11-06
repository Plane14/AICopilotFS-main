using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Atc;
using MSFS2024AIPilot.Core.Checklists;
using MSFS2024AIPilot.Core.Environment;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.Guidance;
using MSFS2024AIPilot.Core.Paths;
using MSFS2024AIPilot.Core.Planning;
using MSFS2024AIPilot.Core.SimConnect;
using MSFS2024AIPilot.Core.State;

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
    private readonly ChecklistManager _checklists;
    private readonly TurbulenceTracker _turbulence = new();
    private readonly OperatingLimits _limits = new();
    private readonly EnvelopeGuard _envelope;

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
        _checklists = new ChecklistManager(null);
        _envelope = new EnvelopeGuard(_limits);

        _aircraft.OnStateUpdated += OnStateUpdated;
        _aircraft.OnAircraftTitleChanged += OnTitleChanged;
        _aircraft.OnAircraftDirectoryChanged += OnAircraftDirectoryChanged;
        _aircraft.OnError += msg => Log($"[SimConnect] {msg}");
        _checklists.OnChecklistChanged += _ => PublishStatus();
    }

    public PilotStatus Status { get { lock (_lock) return _status; } }
    public AircraftState? LatestState => _aircraft.IsConnected ? _aircraft.State : null;
    public SimConnectClient SimConnect => _aircraft.SimConnect;

    // ---------------- Connection & control loop ----------------

    public async Task ConnectAsync(CancellationToken ct = default)
    {
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
        _targetCruiseAltitudeFt = _route.CruiseAltitudeFt;
        _nav.Reset();

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

    /// <summary>Nearest airport to the aircraft, using the facility list coordinates.</summary>
    private async Task<AirportData?> FindNearestAirportAsync(AircraftState s, CancellationToken ct)
    {
        if (!await _planner.WaitForAirportListAsync(TimeSpan.FromSeconds(30), ct)) return null;
        var near = _planner.NearestAirport(s.Latitude, s.Longitude);
        if (near is null) return null;
        Log($"[Pilot] Nearest airport: {near.Value.icao} ({near.Value.distNm:F1} nm)");
        return await _planner.LoadAirportAsync(near.Value.icao, TimeSpan.FromSeconds(20), ct);
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
            await PlanNextLegAsync(CancellationToken.None);
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
        return await PlanNextLegAsync(ct);
    }

    private void StartLoop()
    {
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _aircraft.Dispatch();
                var now = DateTime.UtcNow;
                var dt = Math.Clamp((now - last).TotalSeconds, 0.01, 0.5);
                last = now;
                Tick(dt, now);
            }
            catch (Exception ex)
            {
                Log($"[Pilot] loop error: {ex.Message}");
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // ---------------- Per-frame logic ----------------

    private void Tick(double dt, DateTime now)
    {
        var s = _aircraft.GetSnapshot();
        _turbulence.Update(s.GLoad, dt, quiet: s.OnGround);
        var turbulence = _turbulence.Level;

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

        // Checklist auto-advance: fire bound events and step through items.
        AdvanceChecklist(s, now);

        // Phase machine.
        AdvancePhase(s, now);
        ExecutePhase(s, dt, now, turbulence);
    }

    private DateTime _lastChecklistAdvanceUtc = DateTime.MinValue;

    /// <summary>
    /// Steps the active checklist: fires the sim event bound to the current item
    /// (when one is known), waits a beat, then marks it done and advances.
    /// </summary>
    private void AdvanceChecklist(AircraftState s, DateTime now)
    {
        var c = _checklists.Current;
        if (c is null || c.Complete) return;
        if (now - _lastChecklistAdvanceUtc < TimeSpan.FromMilliseconds(1200)) return;

        var item = c.Items[c.CurrentIndex];
        var evt = ChecklistBindings.EventFor(item.Text);
        if (evt is not null && item.EventName is null)
        {
            // Fire once, then mark done — the sim event is the action.
            _aircraft.SimConnect.FireEvent(evt);
            item.EventName = evt; // remember we fired it
        }

        // Items with a known simvar are considered done when the simvar reports it.
        var sv = ChecklistBindings.SimVarFor(item.Text);
        if (sv is not null)
        {
            // Simple presence check for the common cases.
            if (sv.Contains("PARKING")) { if (s.ParkingBrakeSet) { _checklists.Advance(); _lastChecklistAdvanceUtc = now; } return; }
            if (sv.Contains("GEAR")) { if (!s.GearDown || s.AltitudeAGL > 100) { _checklists.Advance(); _lastChecklistAdvanceUtc = now; } return; }
        }

        _checklists.Advance();
        _lastChecklistAdvanceUtc = now;
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
                if (!s.OnGround) { SetPhase(FlightPhase.InitialClimb); _nav.Reset(); }
                break;

            case FlightPhase.InitialClimb:
                if (s.AltitudeAGL > 1000 || s.Altitude > _targetCruiseAltitudeFt * 0.4)
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
                if (s.GroundSpeed < 5) SetPhase(FlightPhase.TaxiIn);
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
                if (_status.Mode == PilotMode.Continuous && _serviceUntilUtc == default)
                    BeginTurnaround(s);
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
        lock (_lock) { _status.Phase = phase; _status.StatusText = $"Phase: {phase}"; }
        _phaseEnteredUtc = DateTime.UtcNow;
        _checklists.SetPhase(MapChecklistPhase(phase));
        Log($"Phase → {phase}");
        PublishStatus();
    }

    private static ChecklistPhase MapChecklistPhase(FlightPhase p) => p switch
    {
        FlightPhase.Preflight => ChecklistPhase.Preflight,
        FlightPhase.Startup => ChecklistPhase.Startup,
        FlightPhase.TaxiOut => ChecklistPhase.BeforeTakeoff,
        FlightPhase.Takeoff => ChecklistPhase.BeforeTakeoff,
        FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise => ChecklistPhase.Cruise,
        FlightPhase.Descent => ChecklistPhase.Descent,
        FlightPhase.Approach or FlightPhase.FinalApproach or FlightPhase.GoAround => ChecklistPhase.Approach,
        FlightPhase.Rollout or FlightPhase.Landing => ChecklistPhase.Landing,
        FlightPhase.TaxiIn or FlightPhase.Shutdown => ChecklistPhase.Shutdown,
        _ => ChecklistPhase.Preflight,
    };

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
        // Wait for the user to complete the preflight checklist, then advance.
        var c = _checklists.Current;
        if (c is { Complete: true } || c is null)
            SetPhase(FlightPhase.Startup);
    }

    private DateTime _starterAttemptUtc = DateTime.MinValue;
    private int _starterAttempts;

    private void ExecuteStartup(AircraftState s, ControlTargets t, DateTime now)
    {
        _ap.SetDirectMode(true);
        if (!s.ParkingBrakeSet) _ap.ParkingBrake(true);
        var c = _checklists.Current;
        if (c is { Complete: true })
        {
            if (!s.EnginesRunning)
            {
                // Cold aircraft: try the starter until the engines catch or we give up.
                if (_starterAttempts < 6 && now - _starterAttemptUtc > TimeSpan.FromSeconds(4))
                {
                    _aircraft.SimConnect.FireEvent("TOGGLE_STARTER1");
                    _starterAttemptUtc = now;
                    _starterAttempts++;
                    Log("[Pilot] Attempting engine start.");
                }
                return;
            }
            // Request IFR clearance / flight following before taxi.
            _atcRequests.Request(AtcRequestKind.IfreClearance, "preflight");
            SetPhase(FlightPhase.TaxiOut);
            BeginTaxiOut(s);
        }
    }

    // ----- Taxi out -----

    private void BeginTaxiOut(AircraftState s)
    {
        _taxiLegIndex = 0;
        _taxiRoute = null;
        _pushbackProgress = new PushbackProgress { StartedUtc = DateTime.UtcNow };
        _departureRunwayName = null;
        // Pick the runway first so the taxi route can hold short correctly.
        PlanDeparture(s);
    }

    private string? _departureRunwayName;
    private string? _arrivalRunwayName;

    private void PlanDeparture(AircraftState s)
    {
        if (_departureAirport is null) return;

        // Choose the runway: into the wind, long enough for takeoff.
        var pick = ContinuousFlightPlanner.PickRunway(_departureAirport, s.WindDirection,
            _takeoffRunMeters(s));
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

        // Pushback first if the stand offers it and it hasn't been done.
        if (s.Pushback.Available && !s.Pushback.Attached && !_pushbackDone)
        {
            ExecutePushback(s, t, now);
            return;
        }
        _pushbackDone = true;

        // Wait for taxi clearance if the airport is towered.
        if (s.Atc.DepartureAirportIsTowered && !s.Atc.ClearedTaxi && !_taxiClearanceRequested)
        {
            _atcRequests.Request(AtcRequestKind.Taxi, "taxi-out");
            _taxiClearanceRequested = true;
        }

        // Route to the runway and taxi.
        if (_taxiRoute is null) { PlanDeparture(s); if (_taxiRoute is null) return; }

        var legs = _taxiRoute.Legs;
        if (_taxiLegIndex >= legs.Count)
        {
            // Reached the hold-short: hold position and request takeoff clearance.
            _ap.SetThrottleFraction(0);
            _ap.ParkingBrake(true);
            if (s.Atc.DepartureAirportIsTowered && !s.Atc.ClearedTakeoff && !_takeoffClearanceRequested)
            {
                _atcRequests.Request(AtcRequestKind.Takeoff, "hold-short");
                _takeoffClearanceRequested = true;
            }
            if (!s.Atc.DepartureAirportIsTowered || s.Atc.ClearedTakeoff)
            {
                _takeoffClearanceRequested = false;
                LineUpAndTakeoff(s, t);
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

    private bool _pushbackDone;
    private bool _taxiClearanceRequested;
    private bool _takeoffClearanceRequested;

    private void ExecutePushback(AircraftState s, ControlTargets t, DateTime now)
    {
        _ap.SetDirectMode(true);
        var decision = PushbackPlanner.Decide(s, _pushbackProgress, utcNow: now);
        switch (decision)
        {
            case PushbackDecision.Wait:
                if (!_pushbackProgress.Requested)
                {
                    _atcRequests.Request(AtcRequestKind.Pushback, "pushback");
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
                // Release the tug and continue the taxi.
                _aircraft.SimConnect.FireEvent("TUG_DISABLE");
                _ap.ParkingBrake(true);
                _pushbackProgress = _pushbackProgress with { Requested = false };
                Log("[Pilot] Pushback complete; releasing tug.");
                break;
            case PushbackDecision.GiveUp:
                _aircraft.SimConnect.FireEvent("TUG_DISABLE");
                _ap.ParkingBrake(true);
                _pushbackDone = true;
                Log("[Pilot] Pushback unavailable; taxing under own power.");
                break;
        }
    }

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
        SetPhase(FlightPhase.Takeoff);
        _ap.ParkingBrake(false);
        _takeoffClearanceRequested = false;
    }

    // ----- Takeoff -----

    private void ExecuteTakeoff(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(true);
        var perf = _performance;
        var vr = perf.RotationSpeed > 0 ? perf.RotationSpeed : Math.Max(VsEstimate(s) * 1.15, 55);
        var vy = perf.ClimbSpeed > 0 ? perf.ClimbSpeed : Math.Max(vr * 1.1, 70);

        t.TargetHeadingDeg = _departureAirport is not null && _departureRunwayName is not null
            ? _departureAirport.FindRunway(_departureRunwayName)?.HeadingDegTrue
            : (double?)null;
        t.TargetAirspeedKt = vy;
        t.ThrottleFraction = 1.0;

        if (!s.GearDown && s.AltitudeAGL > 400) t.GearDown = false;
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

        if (t.GearDown is null && s.AltitudeAGL < 3500 && s.Altitude < _targetCruiseAltitudeFt - 1000)
            t.GearDown = true;
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
        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        t.TargetAirspeedKt = Math.Min(cruiseSpeedKt(), 160);
        var vs = -Math.Min(_performance.DescentRateFpm > 0 ? _performance.DescentRateFpm : 1500, 2000);
        t.TargetVsFpm = vs;
        t.TargetAltitudeFt = _route?.FinalApproachFix?.Altitude ?? _targetCruiseAltitudeFt * 0.3;
        if (s.GearDown == false && s.AltitudeAGL < 3000) t.GearDown = true;
    }

    // ----- Approach / final -----

    private void ExecuteApproach(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        t.TrackNav = true;
        t.TargetAirspeedKt = _performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 90;
        var fafAlt = _route?.FinalApproachFix?.Altitude ?? s.Altitude;
        t.TargetAltitudeFt = fafAlt;
        t.TargetVsFpm = -Math.Min(1200, Math.Abs(fafAlt - (_destinationAirport?.ReferenceAltitudeMeters ?? 0) * GeoMath.FeetPerMeter) / 5);
        if (!s.GearDown) t.GearDown = true;
        if (s.FlapsHandleIndex < 1) t.FlapIndex = 1;
    }

    private void ExecuteFinalApproach(AircraftState s, ControlTargets t, TurbulenceLevel turbulence)
    {
        _ap.SetDirectMode(true); // precision: direct control on short final
        var rw = _arrivalRunwayName is not null && _destinationAirport is not null
            ? _destinationAirport.FindRunway(_arrivalRunwayName) : null;
        var threshold = rw?.ThresholdFor(_arrivalRunwayName ?? "") ??
                        (_route?.RunwayWaypoint is { HasPosition: true } wp ? (wp.Latitude, wp.Longitude) : ((double, double)?)null);

        if (threshold is not null)
        {
            // Fly the extended centreline to the threshold.
            var brg = rw is not null ? rw.HeadingDegTrue : GeoMath.BearingDeg(threshold.Value.Item1, threshold.Value.Item2, s.Latitude, s.Longitude);
            t.TargetHeadingDeg = brg;
        }

        var vapp = _performance.Vapp > 0 ? _performance.Vapp : (_performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 80);
        // Add wind/ice/turbulence increments.
        var (headwind, _) = WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, t.TargetHeadingDeg ?? s.TrueHeading);
        vapp += WeatherModel.ApproachWindIncrementKt(Math.Max(0, headwind))
              + WeatherModel.ApproachIceIncrementKt(s.StructuralIcePercent)
              + WeatherModel.ApproachTurbulenceIncrementKt(turbulence);
        t.TargetAirspeedKt = vapp;

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
        if (!s.GearDown) t.GearDown = true;

        // Flare and rollout handled by touchdown detection in AdvancePhase.
        if (s.OnGround) SetPhase(FlightPhase.Rollout);
    }

    private void ExecuteGoAround(AircraftState s, ControlTargets t)
    {
        _ap.SetDirectMode(false);
        _ap.ApMaster(true);
        t.TargetAltitudeFt = _targetCruiseAltitudeFt;
        t.TargetAirspeedKt = _performance.ClimbSpeed > 0 ? _performance.ClimbSpeed : 90;
        if (!s.GearDown) t.GearDown = true;
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
        _departureAirport ??= await FindNearestAirportAsync(s, ct);
        if (_departureAirport is null) { Log("[Pilot] Cannot determine departure airport."); return false; }
        _destinationAirport = destination;
        return BuildAndLoadRouteAsync(s);
    }

    private bool BuildAndLoadRouteAsync(AircraftState s)
    {
        if (_departureAirport is null || _destinationAirport is null) return false;

        // Pick runways into the wind.
        _departureRunwayName ??= ContinuousFlightPlanner.PickRunway(_departureAirport, s.WindDirection, _takeoffRunMeters(s))?.name;
        _arrivalRunwayName ??= ContinuousFlightPlanner.PickRunway(_destinationAirport, s.WindDirection,
            _landingRunMeters(s))?.name;

        var req = new RouteRequest
        {
            Departure = _departureAirport,
            Destination = _destinationAirport,
            DepartureRunway = _departureRunwayName,
            DestinationRunway = _arrivalRunwayName,
            CruiseAltitudeFt = ChooseCruiseAltitude(s),
            ClimbRateFpm = _performance.ClimbRateFpm > 0 ? _performance.ClimbRateFpm : 1500,
            DescentRateFpm = _performance.DescentRateFpm > 0 ? _performance.DescentRateFpm : 1500,
            ApproachSpeedKt = _performance.ApproachSpeed > 0 ? _performance.ApproachSpeed : 90,
            CruiseSpeedKt = cruiseSpeedKt(),
        };
        _route = RouteBuilder.Build(req);
        _targetCruiseAltitudeFt = _route.CruiseAltitudeFt;
        _nav.Reset();

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

    private double _landingRunMeters(AircraftState s)
    {
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
            TimeSpan.FromSeconds(60), ct);
        if (dest is null) { Log("[Pilot] No suitable destination found in range."); return false; }

        _departureAirport = dep;
        _destinationAirport = dest;
        _departureRunwayName = null;
        _arrivalRunwayName = null;
        var ok = BuildAndLoadRouteAsync(s);
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
        _taxiRoute = null;
        _taxiInPlanned = false;
        _pushbackDone = false;
        _taxiClearanceRequested = false;
        _takeoffClearanceRequested = false;
        _checklists.Reset(ChecklistPhase.Preflight);
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
                    await PlanNextLegAsync(CancellationToken.None);
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
            _checklists.Reload(dir);
            Log($"[Pilot] Aircraft: {_performance.Title} ({_performance.IcaoType})");
            // Rotorcraft: restrict continuous mode suitability.
            var isRotor = _performance.EngineType.Contains("ROTOR", StringComparison.OrdinalIgnoreCase)
                       || dir.Contains("rotor", StringComparison.OrdinalIgnoreCase);
            if (isRotor)
            {
                _planner.RequiredRunwayMeters = Math.Min(_planner.RequiredRunwayMeters, 200);
                _planner.MinRangeNm = 10;
            }
        }
        catch (Exception ex) { Log($"[Pilot] Performance parse failed: {ex.Message}"); }
    }

    private void OnStateUpdated(AircraftState s)
    {
        lock (_lock)
        {
            _status.Phase = s.OnGround ? _status.Phase : _status.Phase;
        }
        PublishStatusThrottled();
    }

    private void Log(string msg)
    {
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");
    }

    private void PublishStatus()
    {
        PilotStatus copy;
        lock (_lock)
        {
            copy = new PilotStatus
            {
                Mode = _status.Mode,
                Control = _status.Control,
                Phase = _status.Phase,
                StatusText = _status.StatusText,
                AircraftTitle = _status.AircraftTitle,
                DepartureIcao = _status.DepartureIcao,
                DestinationIcao = _status.DestinationIcao,
                DepartureRunway = _status.DepartureRunway,
                DestinationRunway = _status.DestinationRunway,
                DistanceToGoNm = _status.DistanceToGoNm,
                EtaMinutes = _status.EtaMinutes,
                ServiceMinutesRemaining = _status.ServiceMinutesRemaining,
                ChecklistActive = _checklists.Current is { Complete: false },
                CurrentChecklistItem = _checklists.Current is { } c && c.CurrentIndex < c.Items.Count
                    ? c.Items[c.CurrentIndex].Text : "",
                ChecklistPhaseName = _checklists.Current?.Title ?? _checklists.CurrentPhase.ToString(),
                ChecklistProgress = _checklists.Current?.Progress ?? 0,
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
        _facilities.Dispose();
        _aircraft.Dispose();
    }
}


