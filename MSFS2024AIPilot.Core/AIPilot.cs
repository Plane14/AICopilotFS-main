using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core;

public enum PilotControlState
{
    Idle,
    Manual,
    AiActive,
    Disengaging,
    Error
}

public sealed class AIPilot : IDisposable
{
    private readonly AircraftController _aircraft;
    private readonly AircraftPerformance _perf = new();
    private readonly HybridAutopilot _autopilot;
    private readonly FlightPhaseManager _phaseManager = new();
    private readonly BeyondAtcBridge _beyondAtc;
    private readonly AtcCommunicationHub _atc;
    private readonly PhaseContext _phaseContext = new();
    private readonly Timer _mainLoopTimer;
    private readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(50);
    private bool _loopRunning;
    private readonly string _pipeName = "MSFS2024AIPilot.Delegation";
    private readonly CancellationTokenSource _cts = new();
    private Task? _pipeServerTask;
    private readonly PanelHttpBridge _httpBridge;
    private readonly AirportFacilitiesLoader _facilities;
    private readonly Dictionary<string, AirportData> _airports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
    private readonly SimulatorPathsConfig? _simulatorPaths;

    private PilotControlState _controlState = PilotControlState.Idle;
    private DateTime _lastFrame = DateTime.UtcNow;
    private int _frameCount;
    private string _aircraftTitle = "";
    private string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aipilot.log");
    private readonly StringBuilder _atcLog = new();

    private readonly ChecklistSimHost _checklistHost;
    private readonly ChecklistManager _checklists;
    private bool _checklistsLoaded;

    private readonly TrafficMonitor _trafficMonitor;
    private TrafficAssessment _traffic = new();
    private bool _trafficAllowsRunwayEntry = true;
    private DateTime _lastTrafficAssessUtc;

    // Wind, turbulence and ice the aircraft reacts to. Turbulence is derived from the
    // normal load factor because MSFS publishes no turbulence variable; wind and ice come
    // from the environment SimVars decoded into the weather block.
    private readonly TurbulenceTracker _turbulence = new();
    private bool _iceWarningLogged;
    private bool _severeTurbulenceLogged;
    private bool _finalWeatherLogged;

    // The aircraft's structural, aerodynamic and powerplant envelope. Resolved and watched
    // every frame before the phase executors run, so every command they make - a speed, a
    // vertical rate, a pitch attitude, a throttle position - is passed through it. Without
    // it an instruction the airframe cannot obey is flown as given: a controller's descent
    // below the aircraft's structural rate, a go-around holding full power at the EGT
    // redline, a flare at a fixed speed below the aircraft's own stall.
    private readonly EnvelopeGuard _envelope = new();
    private bool _envelopeBreachLogged;

    // The navigation database and the route built out of it. The route is what the pilot
    // follows in the air: the plan's fixes placed from navdata, ending in the published
    // approach, with the published missed approach ready for a go-around.
    private readonly NavDataLoader _navData;
    private NavRoutePlan? _navRoute;
    private NavPathFollower? _navFollower;
    private NavPathFollower? _missedFollower;
    private DateTime _lastNavFrameUtc;

    public PilotControlState ControlState => _controlState;
    public FlightPhase CurrentPhase => _phaseManager.CurrentPhase;
    /// <summary>
    /// The last consistent aircraft snapshot. Refreshed once per control loop from
    /// <see cref="AircraftController.GetSnapshot"/>, so readers on other threads
    /// (the HTTP bridge, the panel) never observe a half-updated state object.
    /// </summary>
    public AircraftState State => _latestSnapshot;
    private AircraftState _latestSnapshot = new();

    public AircraftPerformance Performance => _perf;
    public FlightPhaseManager PhaseManager => _phaseManager;
    public BeyondAtcBridge BeyondAtc => _beyondAtc;
    /// <summary>
    /// The ATC bridge: follows the simulator's own ATC through its SimVars and BeyondATC
    /// through its log and key bindings, and normalises both into instructions.
    /// </summary>
    public AtcCommunicationHub Atc => _atc;

    public event Action<string>? OnStatus;
    public event Action<FlightPhase, FlightPhase>? OnPhaseChanged;
    public event Action<PilotControlState>? OnControlStateChanged;
    public event Action<string>? OnAtcLog;

    public AIPilot(SimulatorPathsConfig? simulatorPaths = null)
    {
        _simulatorPaths = simulatorPaths;
        _aircraft = new AircraftController(_simulatorPaths);
        _autopilot = new HybridAutopilot(_aircraft.SimConnect, _perf, _envelope);
        _beyondAtc = new BeyondAtcBridge();
        _atc = new AtcCommunicationHub(_aircraft, null, Log);
        _phaseManager.OnPhaseChanged += (old, newPhase) => OnPhaseChanged?.Invoke(old, newPhase);
        _httpBridge = new PanelHttpBridge(() => this);
        _facilities = new AirportFacilitiesLoader(_aircraft.SimConnect);
        _facilities.OnAirportLoaded += OnAirportFacilitiesLoaded;
        _facilities.OnError += msg => Log($"[Facilities] {msg}");

        _checklistHost = new ChecklistSimHost(_aircraft.SimConnect);
        _checklists = new ChecklistManager(_checklistHost, ChecklistEnvironment);
        _checklists.OnLog += Log;
        _checklists.OnItemDone += OnChecklistItemDone;

        // Other aircraft in the simulator. There is no traffic-list simulator variable, so
        // this is the only way to know whether the runway is occupied or something is on
        // final: SimConnect delivers one record per surrounding object, and the monitor
        // turns those into a clearance decision.
        _trafficMonitor = new TrafficMonitor(_aircraft.SimConnect, null, Log);

        _navData = new NavDataLoader(_aircraft.SimConnect);
        _navData.OnError += msg => Log(msg);
        _navData.OnApproachesLoaded += OnApproachesLoaded;

        _mainLoopTimer = new Timer(MainLoop, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True when the SimConnect connection to the simulator is up.</summary>
    public bool SimConnected => _aircraft.SimConnect.IsConnected;

    /// <summary>
    /// Surrounding traffic as last assessed, including the runway-occupancy verdict.
    /// </summary>
    public TrafficAssessment Traffic => _traffic;

    /// <summary>False when traffic makes entering the departure runway unsafe.</summary>
    public bool TrafficAllowsRunwayEntry => _trafficAllowsRunwayEntry;

    // --- Wind, turbulence and icing, as the aircraft currently senses them ---

    /// <summary>Derived turbulence intensity from the normal load factor.</summary>
    public TurbulenceLevel Turbulence => _turbulence.Level;

    /// <summary>0..1 turbulence severity, for a finer read than the level.</summary>
    public double TurbulenceSeverity => _turbulence.Severity;

    /// <summary>Direction the wind blows from, in compass degrees.</summary>
    public double WindDirectionDeg => _latestSnapshot.WindDirection;

    /// <summary>Wind speed in knots.</summary>
    public double WindSpeedKt => _latestSnapshot.WindVelocity;

    /// <summary>Structural ice on the airframe, 0..100.</summary>
    public double StructuralIcePercent => _latestSnapshot.StructuralIcePercent;

    /// <summary>True when ice is present on the airframe at or above the perception threshold.</summary>
    public bool AirframeIcePresent => _latestSnapshot.StructuralIcePercent >= WeatherModel.IcePerceptionThreshold;

    /// <summary>True when conditions are conducive to airframe icing.</summary>
    public bool IcingConditionsPresent => WeatherModel.IcingConditions(
        _latestSnapshot.OAT, _latestSnapshot.AmbientInCloud,
        _latestSnapshot.PrecipitationState, _latestSnapshot.StructuralIcePercent);

    /// <summary>True when the aircraft is in cloud / visible moisture.</summary>
    public bool InCloud => _latestSnapshot.AmbientInCloud;

    /// <summary>Human-readable surface state under the aircraft.</summary>
    public string SurfaceText => WeatherModel.SurfaceText(
        _latestSnapshot.SurfaceCondition, _latestSnapshot.SurfaceInfoValid);

    /// <summary>True when the runway surface is wet, icy or snow-covered.</summary>
    public bool SurfaceContaminated => WeatherModel.SurfaceIsContaminated(
        _latestSnapshot.SurfaceCondition, _latestSnapshot.SurfaceInfoValid);

    /// <summary>Crosswind, in knots (right positive), the aircraft sees against a course.</summary>
    public double CrosswindKtOn(double courseDeg) =>
        WeatherModel.WindComponents(_latestSnapshot.WindDirection, _latestSnapshot.WindVelocity, courseDeg).crosswindKt;

    /// <summary>True airspeed in knots, falling back to IAS when the TAS reading is not yet valid.</summary>
    private static double TrueAirspeedFor(AircraftState s) =>
        s.TrueAirspeed > 40 ? s.TrueAirspeed : s.IndicatedAirspeed;

    /// <summary>
    /// Advances the turbulence estimate and raises the one-shot environment warnings:
    /// airframe ice accumulating, and severe turbulence in the air. Kept off the ground so
    /// taxi chop is never reported as air turbulence.
    /// </summary>
    private void AssessEnvironment(AircraftState s, double dt)
    {
        _turbulence.Update(s.GLoad, dt, s.OnGround);

        var icePresent = AirframeIcePresent;
        if (icePresent && !_iceWarningLogged)
        {
            _iceWarningLogged = true;
            Log($"[Weather] Structural ice {s.StructuralIcePercent:F0}% accumulating (OAT {s.OAT:F0} C, " +
                $"in cloud {s.AmbientInCloud}) - ice protection on, +{WeatherModel.ApproachIceIncrementKt(s.StructuralIcePercent):F0} kt approach speed");
        }
        else if (!icePresent)
        {
            _iceWarningLogged = false;
        }

        if (!s.OnGround && _turbulence.Level == TurbulenceLevel.Severe && !_severeTurbulenceLogged)
        {
            _severeTurbulenceLogged = true;
            Log("[Weather] Severe turbulence - reducing enroute speed and softening bank control");
        }
        else if (_turbulence.Level != TurbulenceLevel.Severe)
        {
            _severeTurbulenceLogged = false;
        }
    }

    /// <summary>
    /// The aircraft's resolved operating envelope - speeds, load factor, vertical rates,
    /// configuration limits and engine limits - as resolved on the last frame.
    /// </summary>
    public OperatingLimits Limits => _envelope.Limits;

    /// <summary>Whether the aircraft was outside its envelope on the last frame.</summary>
    public bool LimitExceeded => _envelope.AnyExceeded;

    /// <summary>Measured load factor on the last frame, in g.</summary>
    public double MeasuredLoadFactor => _envelope.MeasuredLoadFactor;

    /// <summary>A one-line summary of the resolved envelope, for status output.</summary>
    public string LimitsText => _envelope.Describe();

    /// <summary>
    /// The limit currently being exceeded, or null when the aircraft is inside its envelope.
    /// Reported continuously rather than once, because a held condition is what the status
    /// and the panel exist to show.
    /// </summary>
    public string? ActiveLimitBreach => _envelope.BreachDescription();

    /// <summary>Where the engines are into their own limit, 0..1.</summary>
    public double EngineLimitFraction => _envelope.EngineLimitRatio;

    /// <summary>
    /// Logs a limit breach once, on the frame it starts, rather than on every frame of it.
    /// A held condition - gear down above Vle while on approach - is one log line, and the
    /// status output carries it continuously.
    /// </summary>
    private void ReportEnvelopeBreach()
    {
        var breach = _envelope.BreachDescription();
        if (breach is null)
        {
            _envelopeBreachLogged = false;
            return;
        }

        if (_envelopeBreachLogged) return;
        _envelopeBreachLogged = true;
        Log($"[Limits] {breach} - commanding within the aircraft's envelope");
    }

    /// <summary>
    /// True when the aircraft is holding short of its departure runway. Derived from its
    /// position against the hold-short, not from an ATC phrase, because that phrase is
    /// one the simulator's own ATC never publishes as a clearance variable.
    /// </summary>
    public bool AtRunwayHoldShort => _atRunwayHoldShort;

    /// <summary>
    /// Ambient hints the checklist engine uses to settle "AS REQUIRED" items:
    /// lights at night or in cloud, ice protection when the OAT is near freezing.
    /// </summary>
    private ChecklistEnvironment ChecklistEnvironment()
    {
        var s = _latestSnapshot;
        var hour = (s.SimTimeSeconds / 3600.0) % 24.0;
        var engineType = _perf.EngineType ?? "";
        bool turbine = engineType.Contains("JET", StringComparison.OrdinalIgnoreCase) ||
                       engineType.Contains("TURBO", StringComparison.OrdinalIgnoreCase);

        // When the installed aircraft config could not be parsed (a Store-installed
        // or Community aircraft whose folder is not discoverable), the engine type is
        // empty and the piston checkpoints - the mixture lever above all - would be
        // run on an aircraft that has no such lever. The live N1/N2 feed is the
        // fallback signal: only turbines publish them, and above idle only a
        // running turbine does.
        turbine |= TurbineFromRuntime(s);

        return new ChecklistEnvironment
        {
            Night = hour < 6 || hour >= 20,
            // Real icing risk: moisture (in cloud or falling) in the icing temperature band,
            // or ice already on the structure - not merely a cold day. This is what settles
            // the "as required" ice-protection switches (pitot heat, windshield deice).
            IcingRisk = WeatherModel.IcingConditions(
                s.OAT, s.AmbientInCloud, s.PrecipitationState, s.StructuralIcePercent),
            Overcast = s.AmbientInCloud,
            Turbine = turbine,
            EngineCount = Math.Max(1, Math.Max(_perf.EngineCount, s.EngineCount)),
            TransponderCode = !string.IsNullOrEmpty(s.ATC.Squawk) ? s.ATC.Squawk : null
        };
    }

    /// <summary>
    /// Whether the live engine data says the aircraft is a turbine: a turbine is the
    /// only engine type that reports N1/N2, and it reports them above idle whenever
    /// it is running.
    /// </summary>
    internal static bool TurbineFromRuntime(AircraftState s)
    {
        var max = 0.0;
        foreach (var v in s.EngineN1) max = Math.Max(max, v);
        foreach (var v in s.EngineN2) max = Math.Max(max, v);
        return max > 20;
    }

    private void OnAirportFacilitiesLoaded(AirportData airport)
    {
        _airports[airport.Icao] = airport;

        // The airport reference frame is unknown, so derive it from the aircraft
        // position whenever the aircraft is on that airport. When it is not, every
        // candidate orientation is equally wrong and the search would pick any of them:
        // the frame is left as it stands and RecalibrateAirportFrames re-runs the search
        // once the aircraft is within 20 km of the airport.
        var here = _aircraft.State;
        var nearAirport = Geo.DistanceMeters(
            here.Latitude, here.Longitude, airport.ReferenceLatitude, airport.ReferenceLongitude) < 20000;
        var frame = nearAirport
            ? _facilities.CalibrateFrame(airport, here.Latitude, here.Longitude)
            : airport.Frame;

        Log($"[Facilities] {airport.Icao}: {airport.Nodes.Count} nodes, {airport.Runways.Count} runways, " +
            $"path resolution {airport.PathResolutionRatio:P0}, frame {frame}" +
            (nearAirport ? "" : " (frame deferred until the aircraft is on the airport)"));

        // Resolve the departure/arrival runway heading from the real runway data.
        ApplyRunwayHeadings(airport);

        foreach (var rw in airport.Runways)
        {
            Log($"[Facilities] {airport.Icao} RWY {rw.PrimaryName}/{rw.SecondaryName}: " +
                $"hdg {rw.HeadingDegTrue:F0}T len {rw.LengthMeters:F0}m");
        }

        // The airport's ground graph is also the first sign that the airport is the one
        // being flown to, so its approaches are asked for now rather than waiting for the
        // plan to be reloaded.
        RequestNavData(airport.Icao);
    }

    /// <summary>
    /// Stores the runway headings resolved from real runway records. The true headings
    /// are converted to magnetic because the autopilot works in magnetic. Departure and
    /// arrival are tracked separately: a single field would be clobbered whenever the
    /// arrival airport data arrived, losing the departure heading for the takeoff roll.
    /// </summary>
    private void ApplyRunwayHeadings(AirportData airport)
    {
        var state = _aircraft.State;
        var departure = DepartureRunwayFor(state);
        var arrival = ArrivalRunwayFor(state);

        var depRunway = !string.IsNullOrEmpty(departure) ? airport.FindRunway(departure) : null;
        if (depRunway != null)
        {
            _departureRunwayHeading = TrueToMagnetic(depRunway.HeadingDegTrue, airport.MagneticVariationDeg);
            Log($"[Facilities] Departure runway {departure} magnetic heading {_departureRunwayHeading:F0}");
        }

        var arrRunway = !string.IsNullOrEmpty(arrival) ? airport.FindRunway(arrival) : null;
        if (arrRunway != null)
        {
            _arrivalRunwayHeading = TrueToMagnetic(arrRunway.HeadingDegTrue, airport.MagneticVariationDeg);
            Log($"[Facilities] Arrival runway {arrival} magnetic heading {_arrivalRunwayHeading:F0}, " +
                $"length {Geo.MetersToFeet(arrRunway.LengthMeters):F0} ft");
        }
    }

    /// <summary>
    /// Picks the runway heading that matters for the phase being executed, so the
    /// takeoff roll and the final approach use different ends of the flight.
    /// </summary>
    private void SelectRunwayHeadingFor(FlightPhase phase)
    {
        _runwayHeading = phase switch
        {
            FlightPhase.Takeoff or FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.TaxiOut
                => _departureRunwayHeading,
            FlightPhase.Descent or FlightPhase.Approach or FlightPhase.FinalApproach or FlightPhase.Landing
                => _arrivalRunwayHeading != 0 ? _arrivalRunwayHeading : _departureRunwayHeading,
            _ => _runwayHeading
        };
    }

    /// <summary>
    /// Converts a true heading to magnetic. Runway headings are published true and the
    /// autopilot works in magnetic, so this uses the airport magnetic variation.
    /// </summary>
    internal static double TrueToMagnetic(double trueHeadingDeg, float magneticVariationDeg)
    {
        var magnetic = trueHeadingDeg - magneticVariationDeg;
        while (magnetic < 0) magnetic += 360;
        while (magnetic >= 360) magnetic -= 360;
        return magnetic;
    }

    public AirportData? GetAirport(string icao) => _airports.TryGetValue(icao?.ToUpperInvariant() ?? "", out var a) ? a : null;

    #region Flight plan / navdata following

    /// <summary>The route the pilot is following, or null before one has been built.</summary>
    public NavRoutePlan? NavRoute => _navRoute;

    /// <summary>The published approach the route ends in, when the navdata carries one.</summary>
    public ApproachInfo? SelectedApproach => _navRoute?.Approach;

    /// <summary>
    /// Asks the navigation database for the approaches published at one airport. Called for
    /// both ends of the flight when the plan is loaded, because the arrival approaches are
    /// what the flight is flown to and the departure ones say where the SID goes.
    /// </summary>
    private void RequestNavData(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return;
        _navData.RequestApproaches(icao);
    }

    private void OnApproachesLoaded(string icao, IReadOnlyList<ApproachInfo> approaches)
    {
        var toRunway = approaches.Where(a => !string.IsNullOrEmpty(a.Runway)).ToList();
        Log($"[NavData] {icao}: {approaches.Count} published approaches" +
            (toRunway.Count > 0 ? $" ({string.Join(", ", toRunway.Select(a => a.Runway).Distinct())})" : ""));

        foreach (var approach in approaches.Where(a => a.HasFinalApproach || a.HasMissedApproach).Take(12))
        {
            Log($"[NavData]   {icao} approach {approach.Describe()}");
        }

        // A freshly loaded approach is what the arrival route is built out of, so the route
        // is rebuilt now rather than waiting for the next change of plan.
        RebuildNavRoute(_latestSnapshot);
    }

    /// <summary>
    /// Retries a direct-to fix that could not be resolved when the clearance arrived, once
    /// the navigation database has answered for it.
    /// </summary>
    private void ResolvePendingDirectTo()
    {
        if (string.IsNullOrEmpty(_pendingDirectTo)) return;

        var resolved = _navData.GetFix(_pendingDirectTo);
        if (resolved?.HasPosition != true) return;

        Log($"[ATC] Direct-to {_pendingDirectTo} resolved ({resolved.Latitude:F5},{resolved.Longitude:F5})");
        _atcDirectToFix = resolved;
        _directFollower = null;
        _pendingDirectTo = null;
    }

    /// <summary>
    /// Rebuilds the route out of the flight plan and the navigation database. Called when
    /// the plan loads, when navdata arrives, and when the controller assigns an arrival
    /// runway - the route is only worth following if it ends where the aircraft is going.
    /// </summary>
    public void RebuildNavRoute(AircraftState state)
    {
        var nav = state.Navigation;
        if (nav.Waypoints.Count == 0 && _navRoute is not null) return;

        var arrivalAirport = GetAirport(nav.ArrivalICAO ?? "") ??
                             _airports.Values.FirstOrDefault(a => a.IsLoaded);
        var departureAirport = GetAirport(nav.DepartureICAO ?? "");

        var previousLegs = _navRoute?.Legs.Count ?? 0;

        var route = NavPathBuilder.Build(
            nav, _navData, DepartureRunwayFor(state), ArrivalRunwayFor(state), arrivalAirport);

        _navRoute = route;
        _navFollower = route.CreateFollower();
        _missedFollower = route.HasMissedApproach ? route.CreateMissedApproachFollower() : null;
        _navRouteRebuilds++;
        ResolvePublishedApproachCourse();

        if (!route.HasRoute)
        {
            Log($"[NavData] route carries no geometry yet" +
                (route.HasUnresolvedFixes ? $" ({route.UnresolvedFixes.Count} fixes unresolved)" : ""));
            return;
        }

        if (previousLegs != route.Legs.Count || route.Approach != _previousApproach)
        {
            _previousApproach = route.Approach;
            Log($"[NavData] route: {route.Legs.Count} legs, " +
                $"approach {(route.Approach?.Describe() ?? "none")}, " +
                $"missed {route.MissedLegs.Count} legs" +
                (route.HasUnresolvedFixes
                    ? $", fixes not in the navdata: {string.Join(", ", route.UnresolvedFixes)}"
                    : ""));
        }
    }

    private int _navRouteRebuilds;
    private ApproachInfo? _previousApproach;

    /// <summary>
    /// Rebuilds the route when the set of outstanding fix lookups SHRINKS: a lookup has
    /// been answered, so a route that was missing legs when it was built may now have the
    /// geometry for them. Rebuilding on a decrease - not on every frame while any lookup is
    /// outstanding - is what stops the follower being re-created each frame (which reset
    /// leg sequencing and turn anticipation) and stops a rebuild re-issuing the outstanding
    /// lookups into the simulator's small concurrency window. Once every fix has resolved
    /// the count stops falling and the route is left alone.
    /// </summary>
    private void RebuildNavRouteIfFixesResolved(AircraftState state)
    {
        // This runs on the loop thread, which is also the thread the facility records are
        // dispatched on, so the route and its follower are only ever replaced here.
        var pending = _navData.PendingFixLookups;
        if (pending >= _lastPendingFixCount)
        {
            _lastPendingFixCount = pending;
            return;
        }

        _lastPendingFixCount = pending;
        RebuildNavRoute(state);
    }

    private int _lastPendingFixCount;

    /// <summary>
    /// Advances the lateral follower from the aircraft's position and returns where to
    /// steer, or null when there is nothing to follow (no route, or a route with no
    /// geometry) - in which case the caller falls back on the simulator's own navigation.
    /// </summary>
    private NavGuidance? NavGuidanceFor(AircraftState state)
    {
        var follower = _navFollower;
        var route = _navRoute;
        if (follower is null || route is null || !route.HasRoute) return null;
        if (state.Latitude == 0 && state.Longitude == 0) return null;

        if (DateTime.UtcNow - _lastNavFrameUtc < TimeSpan.FromMilliseconds(100))
        {
            return _lastGuidance;
        }
        _lastNavFrameUtc = DateTime.UtcNow;

        var guidance = follower.Update(state.Latitude, state.Longitude, state.GroundSpeed);
        _lastGuidance = guidance;
        return guidance;
    }

    private NavGuidance? _lastGuidance;

    /// <summary>Magnetic variation of the route the aircraft is flying, for course conversion.</summary>
    private double NavMagneticVariation(AircraftState state) => _navRoute?.MagneticVariationDeg ?? 0;

    private double _steeringHeading = double.NaN;

    /// <summary>
    /// Sends a true track to the autopilot as a magnetic heading. The heading bug is a
    /// simulator event, so a steady track does not re-send it twenty times a second: only
    /// a change worth a tenth of a degree is worth an event.
    /// </summary>
    private void SetSteeringHeading(AircraftState state, double trueTrackDeg)
    {
        var magnetic = NavPathFollower.TrueToMagnetic(trueTrackDeg, NavMagneticVariation(state));

        // Crab into wind so the ground track follows the path. The heading bug steers a
        // heading, and with a crosswind flying the path as a heading leaves a persistent
        // drift off one side; the correction cancels the crosswind component. Computed in
        // the magnetic frame against the wind's compass direction, the same frame the
        // autopilot heading is commanded in.
        var (_, crosswind) = WeatherModel.WindComponents(
            state.WindDirection, state.WindVelocity, magnetic);
        magnetic += WeatherModel.WindCorrectionAngleDeg(crosswind, TrueAirspeedFor(state));

        if (!double.IsNaN(_steeringHeading) &&
            Math.Abs(Geo.NormalizeDegrees(magnetic - _steeringHeading)) < 0.1)
        {
            return;
        }

        _steeringHeading = magnetic;
        _autopilot.SetHeading(magnetic);
    }

    /// <summary>
    /// Steers the lateral path. A controller's heading is a vector and wins; without one
    /// the plan's own path is followed where the pilot has one, and the simulator's
    /// navigation lock where it does not - which is the honest fallback, because the
    /// simulator's GPS is following the same plan.
    /// </summary>
    private void SteerLateral(AircraftState state)
    {
        if (_atcHeadingDeg.HasValue)
        {
            _autopilot.SetHeading(_atcHeadingDeg.Value);
            return;
        }

        if (_atcDirectToFix is { HasPosition: true } direct)
        {
            _directFollower ??= new NavPathFollower(new[]
            {
                new NavLeg
                {
                    Ident = direct.Icao,
                    Latitude = direct.Latitude,
                    Longitude = direct.Longitude,
                    AltitudeFt = Geo.MetersToFeet(direct.AltitudeMeters),
                    Kind = NavLegKind.Direct
                }
            }, NavMagneticVariation(state));
        }

        if (_directFollower is not null)
        {
            var toFix = _directFollower.Update(state.Latitude, state.Longitude, state.GroundSpeed);
            if (!toFix.Complete && toFix.ActiveLeg is not null)
            {
                SetSteeringHeading(state, toFix.DesiredTrackTrueDeg);
                return;
            }

            // At the fix: a direct-to ends there, and what follows is the plan again.
            _directFollower = null;
            _atcDirectToFix = null;
        }

        var guidance = NavGuidanceFor(state);
        if (guidance.HasValue && !guidance.Value.Complete && guidance.Value.ActiveLeg is not null)
        {
            SetSteeringHeading(state, guidance.Value.DesiredTrackTrueDeg);
            return;
        }

        _autopilot.EngageNavLocks();
    }

    private NavPathFollower? _directFollower;
    private NavFix? _atcDirectToFix;
    private string? _pendingDirectTo;
    private string? _atcWaypointName;
    private double _atcWaypointAltitudeFt;

    /// <summary>
    /// The simulator's own ATC publishes the fix it has sent this aircraft to on a SimVar -
    /// a name and, when a crossing restriction applies, the altitude to cross it at. It is
    /// a vector with an end point rather than a heading, so it is flown the same way a
    /// parsed direct-to is: the fix is resolved from the navigation database and steered
    /// to directly, and a crossing altitude becomes the level the aircraft is flown at.
    /// Nothing is acted on until the name has been resolved, so an ATC waypoint that is not
    /// in the navigation database is logged and left alone rather than guessed at.
    /// </summary>
    private void UpdateAtcWaypointDirect(AircraftState s)
    {
        var name = s.ATC.NextWaypointName;

        // The controller no longer has this aircraft pointed at a fix: whatever it asked for
        // has been satisfied, and the plan's own path takes over.
        if (string.IsNullOrWhiteSpace(name))
        {
            _atcWaypointName = null;
            _atcWaypointAltitudeFt = 0;
            if (_atcDirectToFix?.WasSentByAtc == true)
            {
                _atcDirectToFix = null;
                _directFollower = null;
            }
            return;
        }

        name = name.Trim().ToUpperInvariant();
        if (string.Equals(name, _atcWaypointName, StringComparison.OrdinalIgnoreCase)) return;
        _atcWaypointName = name;

        var altitudeFt = s.ATC.NextWaypointAltitudeM > 0
            ? Geo.MetersToFeet(s.ATC.NextWaypointAltitudeM)
            : 0;

        var fix = _navData.GetFix(name);
        if (fix?.HasPosition == true)
        {
            // The fix the controller named is a fix the database knows, and it is either the
            // one being flown already or somewhere new: the name is honoured either way, and
            // the crossing altitude with it.
            fix.WasSentByAtc = true;
            _atcDirectToFix = fix;
            _directFollower = null;
            _atcWaypointAltitudeFt = altitudeFt;
            if (altitudeFt > 0) _atcAltitudeFt = (int?)altitudeFt;

            Log($"[ATC] Proceeding direct to {name}" +
                (altitudeFt > 0 ? $" crossing at {altitudeFt:F0} ft" : ""));
            return;
        }

        _navData.RequestFix(name);
        Log($"[ATC] Controller has sent us to {name} - looking it up in the navigation database");
    }

    /// <summary>The crossing altitude the controller asked to be held at the fix it named.</summary>
    public double AtcWaypointAltitudeFt => _atcWaypointAltitudeFt;

    /// <summary>
    /// The published missed approach, flown leg by leg, or null when the navdata does not
    /// publish one or the route has none. This is what a go-around looks like when the
    /// procedure is known: not the runway heading, but the chain of fixes the airport
    /// publishes for exactly this situation.
    /// </summary>
    public bool HasPublishedMissedApproach => _missedFollower is not null;

    public string? MissedApproachDescription => _navRoute?.Approach is { HasMissedApproach: true } approach
        ? $"missed approach from {approach.FindMissedApproachPoint()?.Ident ?? "the MAP"}: " +
          string.Join(" > ", _navRoute!.MissedLegs.Select(l => l.Ident))
        : null;

    #endregion


    /// <summary>Returns every airport whose ground data has been loaded from the sim.</summary>
    public IReadOnlyCollection<AirportData> GetLoadedAirports() => _airports.Values.ToList();

    /// <summary>Requests airport ground data (runways, taxiway graph, parking) from the sim.</summary>
    /// <param name="force">
    /// Re-requests an airport that has already been loaded. Needed because the ground frame
    /// is calibrated against the aircraft's position: a second request while standing on
    /// the airport is what rebuilds the graph for where the aircraft actually is, rather
    /// than where it was when the data first arrived.
    /// </param>
    public void RequestAirportData(string icao, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(icao)) return;
        if (!force && _airports.ContainsKey(icao.ToUpperInvariant())) return;

        Log($"[Facilities] Requesting airport data for {icao}{(force ? " (forced reload)" : "")}");
        _facilities.RequestAirport(icao);
    }

    private bool _started;

    /// <summary>
    /// Connects to SimConnect and starts the control loop. Calling this more than
    /// once is a no-op, because a second SimConnect handle, a second HTTP listener
    /// and a second named-pipe server would all collide with the first.
    /// </summary>
    public async System.Threading.Tasks.Task StartAsync()
    {
        if (_started) return;
        _started = true;

        Log("=== MSFS 2024 AI Pilot starting ===");
        Status("Connecting to SimConnect...");

        try
        {
            await _aircraft.StartAsync();
        }
        catch (Exception ex)
        {
            Status($"SimConnect connection failed: {ex.Message}");
            Log($"ERROR: {ex}");
            return;
        }

        // Reload the aircraft's own checklists whenever the loaded aircraft changes
        // (the user can switch aircraft while the host keeps running).
        // Handlers are registered BEFORE detection so they are ready to catch
        // the AircraftLoaded/TITLE SimConnect responses, which trigger detection
        // with a resolved directory path instead of a blocking filesystem scan.
        _aircraft.OnAircraftTitleChanged += title =>
        {
            _aircraftTitle = title;
            // Resolve the package directory from the current title when the .air path is
            // still pending. This keeps the checklist and performance profile loading in
            // step with the simulator even when the TITLE SimVar arrives before the .air
            // system-state path.
            var dir = !string.IsNullOrEmpty(_aircraft.AircraftDirectory)
                ? _aircraft.AircraftDirectory
                : (!string.IsNullOrWhiteSpace(title) ? AircraftPackageLocator.LocateAircraftDirectory(title, null, _simulatorPaths) : null);

            if (!string.IsNullOrEmpty(dir))
                LoadAircraftChecklists(title, dir);
            else if (!string.IsNullOrWhiteSpace(title))
                LoadAircraftChecklists(title);
        };
        _aircraft.OnAircraftDirectoryChanged += dir =>
        {
            // Directory resolved from the .air file path: re-detect performance and
            // reload checklists against it without a filesystem scan.
            DetectAircraftPerformance(dir);
            LoadAircraftChecklists(_aircraftTitle, dir);
        };
        LoadAircraftChecklists(_aircraftTitle);

        // Auto-detect aircraft performance is deferred to OnAircraftDirectoryChanged,
        // which fires when SimConnect resolves the .air file path. This avoids a
        // blocking recursive filesystem scan at startup.

        // ATC: the sim's own controller and BeyondATC both feed the same instruction
        // handler, so the flight logic stays independent of which one is talking.
        _beyondAtc.Start();
        _atc.Start();
        _atc.OnInstructionParsed += inst => HandleAtcInstruction(inst);
        _atc.OnRawAtcText += t => OnAtcLog?.Invoke(t);

        // Traffic: subscribe alongside the aircraft controller, which is also listening
        // on OnDataReceived. The event is multicast, so both handlers run.
        _aircraft.SimConnect.OnDataReceived += _trafficMonitor.OnData;

        // The simulator going away is the one event that has to be said out loud: every
        // command the control loop then makes is refused by the wrapper, the aircraft
        // freezes mid-phase, and nothing else in the pilot reports it.
        _aircraft.SimConnect.OnDisconnected += () =>
        {
            Log("SimConnect: the simulator connection was lost - commands are no longer reaching it");
            if (_controlState == PilotControlState.AiActive)
            {
                Status("SimConnect lost - returning control");
                SetControlState(PilotControlState.Manual);
            }
        };


        // Auto-load the current flight plan if one is active in the sim
        var latest = FindLatestFlightPlan();
        if (latest != null)
        {
            LoadFlightPlan(latest);
        }
        else
        {
            // No plan: the sim's own ATC still knows where the aircraft is going, and its
            // designated runways are what the route would be built from.
            RequestNavData(_aircraft.State.Navigation.ArrivalICAO);
        }

        StartDelegationServer();
        _httpBridge.OnLog += Log;
        _httpBridge.Start();

        _controlState = PilotControlState.Manual;
        OnControlStateChanged?.Invoke(_controlState);

        _mainLoopTimer.Change(TimeSpan.Zero, LoopInterval);

        Status("Ready. Press the delegate button to engage AI control.");
        Log("Startup complete - awaiting user delegation");
    }

    /// <summary>
    /// Loads the loaded aircraft's own checklists. MSFS 2024 ships them as
    /// SimBase.Document XML (steps/pages/blocks/checkpoints with copilot
    /// actions, optionally referencing a checkpoint library); many stock and
    /// Community aircraft still use the legacy text format. When neither is
    /// readable, a built-in generic checklist keeps the flow running.
    /// <param name="rootOverride">A pre-resolved aircraft package directory to avoid a filesystem scan.</param>
    /// </summary>
    private void LoadAircraftChecklists(string title, string? rootOverride = null)
    {
        // If we have a directory override (e.g. from OnAircraftDirectoryChanged), load checklists
        // even with an empty title, since we know which aircraft directory to look in.
        if (rootOverride == null && string.IsNullOrWhiteSpace(title)) return;
        try
        {
            var parsed = ChecklistParser.ParseFromAircraft(title, rootOverride: rootOverride);
            AircraftChecklistSet set;
            if (parsed.IsLoaded)
            {
                set = parsed;
                var items = set.AllItems.Count();
                Log($"[Checklist] aircraft checklists loaded from {parsed.SourcePath} " +
                    $"({parsed.Format}, {set.Lists.Count} steps, {items} items)");
                foreach (var note in parsed.Notes) Log($"[Checklist] {note}");
            }
            else
            {
                var env = ChecklistEnvironment();
                set = GenericChecklists.Create(title, env.Turbine, env.EngineCount);
                Log("[Checklist] no aircraft checklist package found - using built-in generic checklist");
                foreach (var note in parsed.Notes) Log($"[Checklist] {note}");
            }

            _checklists.Load(set);
            _checklistsLoaded = true;
            Status($"Checklists: {set.Lists.Count} steps ({set.Format})");
            Log($"[Checklist] steps: " + string.Join(", ",
                set.Lists.Select(l => $"{l.Step}={l.ExecutableCount}")));
        }
        catch (Exception ex)
        {
            Log($"Checklist load error: {ex.Message}");
        }
    }

    private void DetectAircraftPerformance()
    {
        DetectAircraftPerformance(null);
    }

    private void DetectAircraftPerformance(string? overrideDir)
    {
        try
        {
            // 1) Runtime SimVar values (most accurate live data)
            var live = _aircraft.State;
            if (live.GrossWeight > 0) _perf.MaxGrossWeight = Math.Max(_perf.MaxGrossWeight, live.GrossWeight);

            // 2) Parse the installed aircraft configuration files
            var aircraftDir = overrideDir ?? FindUserAircraftDirectory();
            if (aircraftDir != null)
            {
                var parsed = AircraftConfigParser.ParseAircraftConfig(aircraftDir);
                MergePerformance(parsed);
                Log($"Aircraft profile loaded: {parsed.Title} ({parsed.ICAOType})");
                foreach (var note in parsed.Notes) Log($"Aircraft profile: {note}");
                Status($"Aircraft: {parsed.Title}");
            }
            else
            {
                Log("No aircraft config found - using runtime/dictionary estimates");
            }
        }
        catch (Exception ex)
        {
            Log($"Aircraft detection error: {ex.Message}");
        }
    }

    private void MergePerformance(AircraftPerformance parsed)
    {
        _perf.Title = parsed.Title;
        _perf.ICAOType = parsed.ICAOType;
        _perf.Category = parsed.Category;
        _perf.ApproachCategory = parsed.ApproachCategory;

        if (parsed.MaxGrossWeight > 0) _perf.MaxGrossWeight = parsed.MaxGrossWeight;
        if (parsed.MaxTakeoffWeight > 0) _perf.MaxTakeoffWeight = parsed.MaxTakeoffWeight;
        if (parsed.MaxLandingWeight > 0) _perf.MaxLandingWeight = parsed.MaxLandingWeight;
        if (parsed.MaxZeroFuelWeight > 0) _perf.MaxZeroFuelWeight = parsed.MaxZeroFuelWeight;
        if (parsed.EmptyWeight > 0) _perf.EmptyWeight = parsed.EmptyWeight;
        if (parsed.FuelCapacityGallons > 0) _perf.FuelCapacityGallons = parsed.FuelCapacityGallons;
        if (parsed.WingSpan > 0) _perf.WingSpan = parsed.WingSpan;
        if (parsed.WingArea > 0) _perf.WingArea = parsed.WingArea;
        if (parsed.MAC > 0) _perf.MAC = parsed.MAC;
        if (parsed.EngineCount > 0) _perf.EngineCount = parsed.EngineCount;
        if (!string.IsNullOrEmpty(parsed.EngineType)) _perf.EngineType = parsed.EngineType;
        if (parsed.MaxThrustPerEngine > 0) _perf.MaxThrustPerEngine = parsed.MaxThrustPerEngine;

        if (parsed.Vso > 0) _perf.Vso = parsed.Vso;
        if (parsed.Vs1 > 0) _perf.Vs1 = parsed.Vs1;
        if (parsed.Vne > 0) _perf.Vne = parsed.Vne;
        if (parsed.Vfe > 0) _perf.Vfe = parsed.Vfe;

        foreach (var (k, v) in parsed.TakeoffVSpeeds)
            _perf.TakeoffVSpeeds[k] = v;
        foreach (var (k, v) in parsed.LandingVSpeeds)
            _perf.LandingVSpeeds[k] = v;

        if (parsed.TakeoffPerf.GroundRollTable.Count > 0)
            _perf.TakeoffPerf.GroundRollTable = parsed.TakeoffPerf.GroundRollTable;
        if (parsed.TakeoffPerf.TotalDistanceTable.Count > 0)
            _perf.TakeoffPerf.TotalDistanceTable = parsed.TakeoffPerf.TotalDistanceTable;
        if (parsed.LandingPerf.GroundRollTable.Count > 0)
            _perf.LandingPerf.GroundRollTable = parsed.LandingPerf.GroundRollTable;
        if (parsed.LandingPerf.TotalDistanceTable.Count > 0)
            _perf.LandingPerf.TotalDistanceTable = parsed.LandingPerf.TotalDistanceTable;

        if (parsed.ClimbPerf.Profiles.Count > 0)
            _perf.ClimbPerf = parsed.ClimbPerf;
        if (parsed.CruisePerf.Profiles.Count > 0)
            _perf.CruisePerf = parsed.CruisePerf;
        if (parsed.DescentPerf.Profiles.Count > 0)
            _perf.DescentPerf = parsed.DescentPerf;

        // What the configuration could not tell us is as important as what it did: a
        // distance table the parser does not recognise is otherwise a silent fallback to
        // an estimate with nothing in the log to say so.
        foreach (var note in parsed.Notes)
        {
            if (!_perf.Notes.Contains(note)) _perf.Notes.Add(note);
        }

        _perf.HasFlightPerformanceConfig = parsed.HasFlightPerformanceConfig;
        _perf.HasAdvancedPerformance = parsed.HasAdvancedPerformance;

        // Derive fallback V-speeds when the config has none
        if (_perf.Vso <= 0 && _perf.Category.Length > 0)
        {
            _perf.Vso = _perf.Category.ToUpperInvariant() switch
            {
                "H" => 150, "J" => 145,
                "L" => 60,  "M" => 110,
                _ => 90
            };
        }
        _perf.Vref = _perf.Vref > 0 ? _perf.Vref : _perf.Vso * 1.3;
        _perf.Vapp = _perf.Vapp > 0 ? _perf.Vapp : _perf.Vref + 5;
    }

    private string? FindUserAircraftDirectory()
    {
        _aircraftTitle = _aircraft.AircraftTitle;
        var dirOverride = _aircraft.AircraftDirectory;
        if (string.IsNullOrWhiteSpace(_aircraftTitle) && string.IsNullOrWhiteSpace(dirOverride))
            return null;

        // Performance discovery may fall back to the first package that actually carries
        // flight-model/performance data, but only after a title/override match was tried.
        return AircraftPackageLocator.LocateAircraftDirectoryForPerformance(_aircraftTitle, dirOverride, _simulatorPaths);
    }

    public void Stop()
    {
        _mainLoopTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _cts.Cancel();
        _aircraft.Stop();
        _beyondAtc.Stop();
        _atc.Stop();
        if (_checklistsLoaded) _checklists.Executor.Cancel();
        Log("=== AI Pilot stopped ===");
    }

    private void MainLoop(object? state)
    {
        if (_loopRunning) return;
        _loopRunning = true;
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        _frameCount++;

        // Remembered once, so HandleAtcInstruction can tell an instruction raised by this
        // loop from one raised by the HTTP bridge, the named pipe or the console.
        if (_loopThreadId == 0) _loopThreadId = Environment.CurrentManagedThreadId;

        try
        {
            _aircraft.Dispatch();

            // Instructions the console or the panel sent while this loop was running are
            // applied here, on the loop thread, where the rest of the state lives.
            DrainAtcInstructions();

            // Publish one consistent snapshot for this frame; every phase executor
            // reads from it so no two values come from different SimConnect frames.
            _latestSnapshot = _aircraft.GetSnapshot();

            // Wind, turbulence and ice are advanced here, before the phase executors read
            // them, so a reaction on this frame sees this frame's environment.
            AssessEnvironment(_latestSnapshot, dt);

            // The resolved envelope for this aircraft, and the measured state against it.
            // Runs before the executors so every command they issue this frame is clamped
            // against the same limits.
            _envelope.Update(
                _latestSnapshot, _perf, (int)Math.Round(_latestSnapshot.FlapsHandleIndex),
                _latestSnapshot.GearDown);
            ReportEnvelopeBreach();

            // Traffic and the airport frames are outside the aircraft snapshot because
            // they describe the world rather than this aircraft, and both are cheap enough
            // to run every frame.
            UpdateTraffic(_latestSnapshot);
            RecalibrateAirportFrames(_latestSnapshot);

            // Fixes that the route names are looked up asynchronously, so the route is
            // rebuilt whenever one resolves: a plan read in MSFS 2024 is a list of names,
            // and this is where the names turn into geometry.
            //
            // ResolvePendingDirectTo gates itself on a non-empty pending name and on the
            // fix having a position, so it is cheap to attempt every frame and it must not
            // be gated on PendingFixLookups > 0: that count is non-zero precisely while
            // the lookup is still OUTSTANDING, so gating on it drops every direct-to whose
            // fix is not already cached, and never re-attempts it once the count reaches
            // zero.
            ResolvePendingDirectTo();
            RebuildNavRouteIfFixesResolved(_latestSnapshot);

            // Advances the navigation-database request bookkeeping: a request the simulator
            // never answers is dropped on a timer, which is what releases its concurrency
            // slot and lets the route be rebuilt again.
            _navData.Tick();

            _atc.Tick(_latestSnapshot);

            // The controller's named fix arrives on a SimVar, so it is checked on the loop
            // rather than on the ATC instruction path, which only carries parsed text.
            UpdateAtcWaypointDirect(_latestSnapshot);

            if (_controlState != PilotControlState.AiActive) return;

            UpdatePhaseContext(_latestSnapshot);
            var phase = _phaseManager.EvaluatePhase(_latestSnapshot, _phaseContext);
            ExecutePhase(phase, _latestSnapshot, dt);

            // What a pilot asks ATC for at this point of the flight (clearance, taxi,
            // takeoff, landing). The hub decides how that reaches the active controller.
            _atc.UpdatePhaseRequests(phase, _latestSnapshot);

            // The aircraft's own checklists drive switch/lever state for the
            // phase just executed; the phase executors keep the flight path.
            if (_checklistsLoaded) _checklists.Tick(phase, dt);
        }
        catch (Exception ex)
        {
            Log($"Loop error: {ex}");
        }
        finally
        {
            _loopRunning = false;
        }
    }

    private void UpdatePhaseContext(AircraftState s)
    {
        // Emergency from live sim state: an engine that is stopped while others are
        // running, or a user-raised emergency flag.
        bool engineFire = _latestSnapshot.EngineCount > 1 &&
                          _latestSnapshot.EngineN1.Any(n => n > 5) &&
                          _latestSnapshot.EngineN1.Any(n => n < 1);
        _emergency = engineFire || _userEmergencyFlag;

        _phaseContext.EnginesRunning = s.EnginesRunning;
        _phaseContext.AtDepartureRunway = _atRunwayHoldShort;
        _phaseContext.AtTaxiStartPoint = s.OnGround && s.IndicatedAirspeed < 5 && _atTaxiStart;
        _phaseContext.TakeoffRoll = s.OnGround && s.IndicatedAirspeed > 30 && _takeoffCleared;
        _phaseContext.LandingRoll = s.OnGround && s.VerticalSpeed <= 0 && s.AltitudeAGL < 50;
        _phaseContext.TargetCruiseAltitude = _targetCruiseAltitude;
        _phaseContext.Emergency = _emergency;
        _phaseContext.GoAroundRequested = _goAround;
        _phaseContext.GoAroundClimb = s.VerticalSpeed > 600;
        _phaseContext.DepartureClearanceReceived = _departureClearanceReceived;
        _phaseContext.AtDepartureGateOrRamp = s.OnGround && s.GroundSpeed < 1;
        _phaseContext.DistanceToDepartureRunway = DistanceToDepartureRunwayNauticalMiles(s);

        // Lets the ATC bridge ask for takeoff clearance only while holding short, and for
        // a pushback only while standing on a stand that offers one and is ready for it.
        _atc.AtRunwayHoldShort = _atRunwayHoldShort;
        _atc.PushbackWanted = _controlState == PilotControlState.AiActive && PushbackPrepared(s);
    }

    /// <summary>Raises or clears the emergency flag used to force the emergency phase.</summary>
    public void SetEmergency(bool emergency) => _userEmergencyFlag = emergency;
    private bool _userEmergencyFlag;

    /// <summary>
    /// Distance from the aircraft to its departure runway, in nautical miles, measured to
    /// whichever end is nearer so an aircraft still short of the strip reads a small number.
    /// <para>
    /// This used to be the distance between the second and third flight-plan waypoints,
    /// which has nothing to do with where the aircraft is: the runway is known here
    /// because the ground network carries its geometry, and the simulator reports the same
    /// quantity itself on ATC RUNWAY DISTANCE, which was previously decoded and never read.
    /// </para>
    /// </summary>
    private double DistanceToDepartureRunwayNauticalMiles(AircraftState s)
    {
        var nav = s.Navigation;
        var departure = DepartureRunwayFor(s);
        var runway = !string.IsNullOrEmpty(nav.DepartureICAO) && !string.IsNullOrEmpty(departure)
            ? GetAirport(nav.DepartureICAO)?.FindRunway(departure)
            : null;

        if (runway is { LengthMeters: > 0 } && runway.Latitude is not double.NaN)
        {
            var primary = runway.PrimaryEnd;
            var secondary = runway.SecondaryEnd;
            var toPrimary = Geo.DistanceMeters(s.Latitude, s.Longitude, primary.lat, primary.lon);
            var toSecondary = Geo.DistanceMeters(s.Latitude, s.Longitude, secondary.lat, secondary.lon);
            return Math.Min(toPrimary, toSecondary) / 1852.0;
        }

        var runwayDistanceMeters = s.ATC.RunwayDistanceMeters;
        return runwayDistanceMeters > 0 ? runwayDistanceMeters / 1852.0 : 0;
    }

    /// <summary>
    /// Re-issues the traffic request and assesses the contacts around the aircraft.
    /// Rate limited to a few times a second: the monitor's own expiry is what makes a
    /// missed cycle harmless, and assessing every frame would only re-identify the same
    /// nearest contact.
    /// </summary>
    private void UpdateTraffic(AircraftState s)
    {
        _trafficMonitor.Refresh();

        if (DateTime.UtcNow - _lastTrafficAssessUtc < TimeSpan.FromMilliseconds(250)) return;
        _lastTrafficAssessUtc = DateTime.UtcNow;

        if (!_aircraft.SimConnect.IsConnected)
        {
            _traffic = new TrafficAssessment();
            _trafficAllowsRunwayEntry = true;
            return;
        }

        // The runway traffic matters against is the one about to be used: the departure
        // strip while on the ground at an airport, the arrival strip once airborne with
        // an arrival assigned.
        var runway = RunwayForTraffic(s);
            _traffic = _trafficMonitor.Assess(s, runway);
            _trafficAllowsRunwayEntry = !_traffic.RunwayEntryUnsafe;
    }

    /// <summary>
    /// The runway the controller has told this aircraft to depart on. The simulator's own
    /// ATC publishes its designated runway on a SimVar and BeyondATC publishes the runway it
    /// allocated, and either can differ from the runway in the flight plan; ignoring that
    /// meant the pilot taxied to and departed from a strip the controller had not assigned.
    /// Only the aircraft's own controller counts - the flight plan is the fallback. The name
    /// is normalised to the "25L" form the rest of the pilot matches on.
    /// </summary>
    private string? DepartureRunwayFor(AircraftState s)
    {
        var atc = s.ATC;
        if (!string.IsNullOrWhiteSpace(_atcDepartureRunway)) return Normalize(_atcDepartureRunway);
        if (!string.IsNullOrWhiteSpace(atc.DesignatedRunwayTakeoff)) return Normalize(atc.DesignatedRunwayTakeoff);
        return Normalize(s.Navigation.DepartureRunway);
    }

    /// <summary>The runway the controller has assigned for the arrival; the plan's otherwise.</summary>
    private string? ArrivalRunwayFor(AircraftState s)
    {
        var atc = s.ATC;
        if (!string.IsNullOrWhiteSpace(_atcArrivalRunway)) return Normalize(_atcArrivalRunway);
        if (!string.IsNullOrWhiteSpace(atc.DesignatedRunwayLanding)) return Normalize(atc.DesignatedRunwayLanding);
        return Normalize(s.Navigation.ArrivalRunway);
    }

    /// <summary>
    /// Puts a runway name into the "25L" form the rest of the pilot matches on, using the
    /// .pln normaliser. A name that does not normalise is returned as it is given, because
    /// a matching failure is visible and a fabricated runway is not.
    /// </summary>
    private static string? Normalize(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway)) return null;
        return FlightPlanParser.NormalizeRunwayName(runway) ?? runway.Trim().ToUpperInvariant();
    }

    private RunwayInfo? RunwayForTraffic(AircraftState s)
    {
        var nav = s.Navigation;
        var icao = nav.DepartureICAO;
        var name = DepartureRunwayFor(s);

        // Airborne with an arrival assigned: the arrival strip is what can be occupied.
        if (!s.OnGround || string.IsNullOrEmpty(name))
        {
            var arrivalName = ArrivalRunwayFor(s);
            if (!string.IsNullOrEmpty(nav.ArrivalICAO) && !string.IsNullOrEmpty(arrivalName))
            {
                var arrivalAirport = GetAirport(nav.ArrivalICAO);
                if (arrivalAirport?.IsLoaded == true) return arrivalAirport.FindRunway(arrivalName);
            }
        }

        if (string.IsNullOrEmpty(icao) || string.IsNullOrEmpty(name)) return null;
        var airport = GetAirport(icao);
        return airport?.IsLoaded == true ? airport.FindRunway(name) : null;
    }

    private const double AirportFrameRecalibrationMeters = 200.0;
    private static readonly TimeSpan AirportFrameRecalibrationInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Re-calibrates an airport's graph frame once the aircraft is standing on it.
    /// <para>
    /// The frame is a guess: the simulator does not document which way its bias axes
    /// point, so the loader keeps the orientation that puts nearby nodes nearest this
    /// calibration point. That worked only once, at load time, which meant the arrival
    /// airport's frame was calibrated while the aircraft was still on the departure
    /// airport - where every candidate is thousands of kilometres away and the winner is
    /// arbitrary. Re-running the search when the graph is close to the aircraft fixes the
    /// arrival on touchdown without needing the frame to be right beforehand.
    /// </para>
    /// </summary>
    private void RecalibrateAirportFrames(AircraftState s)
    {
        if (DateTime.UtcNow - _lastFrameRecalibrationUtc < AirportFrameRecalibrationInterval) return;
        _lastFrameRecalibrationUtc = DateTime.UtcNow;

        foreach (var airport in _airports.Values)
        {
            if (!airport.IsLoaded) continue;
            if (airport.Nodes.Count == 0) continue;

            // Only an airport the aircraft is actually on is worth calibrating: the search
            // is meaningless against a position thousands of kilometres away.
            if (Geo.DistanceMeters(s.Latitude, s.Longitude, airport.ReferenceLatitude, airport.ReferenceLongitude) > 20000)
                continue;

            // Already right: the nearest node is where the aircraft says it is.
            var nearest = TaxiRouter.FindNearestNode(airport, s.Latitude, s.Longitude);
            if (nearest is null) continue;
            var distance = Geo.DistanceMeters(s.Latitude, s.Longitude, nearest.Latitude, nearest.Longitude);
            if (distance <= AirportFrameRecalibrationMeters) continue;

            var frame = _facilities.CalibrateFrame(airport, s.Latitude, s.Longitude);

            if (frame != airport.Frame)
            {
                Log($"[Facilities] {airport.Icao} graph frame recalibrated to {frame} " +
                    $"(nearest node was {distance:F0} m away)");
                // A frame change moves every node, so any route planned against the old
                // frame is stale and must be rebuilt.
                if (_activeTaxiRoute is { Success: true }) _activeTaxiRoute = null;
            }
        }
    }

    private DateTime _lastFrameRecalibrationUtc;


    private void ExecutePhase(FlightPhase phase, AircraftState s, double dt)
    {
        // Pick the runway heading that applies to this end of the flight.
        SelectRunwayHeadingFor(phase);

        switch (phase)
        {
            case FlightPhase.Preflight:
                ExecutePreflight();
                break;

            case FlightPhase.Startup:
                ExecuteStartup(dt);
                break;

            case FlightPhase.TaxiOut:
                ExecuteTaxiOut(s, dt);
                break;

            case FlightPhase.Takeoff:
                ExecuteTakeoff(s, dt);
                break;

            case FlightPhase.InitialClimb:
                ExecuteInitialClimb(s, dt);
                break;

            case FlightPhase.Climb:
                ExecuteClimb(s, dt);
                break;

            case FlightPhase.Cruise:
                ExecuteCruise(s, dt);
                break;

            case FlightPhase.Descent:
                ExecuteDescent(s, dt);
                break;

            case FlightPhase.Approach:
                ExecuteApproach(s, dt);
                break;

            case FlightPhase.FinalApproach:
                ExecuteFinalApproach(s, dt);
                break;

            case FlightPhase.Landing:
                ExecuteLanding(s, dt);
                break;

            case FlightPhase.Rollout:
                ExecuteRollout(s, dt);
                break;

            case FlightPhase.TaxiIn:
                ExecuteTaxiIn(s, dt);
                break;

            case FlightPhase.Shutdown:
                ExecuteShutdown(s);
                break;

            case FlightPhase.GoAround:
                ExecuteGoAround(s, dt);
                break;
        }
    }

    #region Phase Executors

    private bool _startupComplete;
    private void ExecutePreflight()
    {
        // Set parking brake, check list; AI waits for user to be ready
        // In fully-autonomous mode, we start the sequence
        if (!_startupComplete)
        {
            Status("Preflight checks...");
            _startupComplete = true;
        }
        _phaseManager.ForcePhase(FlightPhase.Startup);
    }

    private string _phaseStatus = "";
    private double _phaseStatusHeartbeat;
    private const double PhaseStatusHeartbeatSeconds = 5.0;

    /// <summary>
    /// Rounds a live value to the nearest <paramref name="step"/>, so a phase status line
    /// changes when the figure it reports changes by that much rather than on every frame.
    /// </summary>
    private static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);

    /// <summary>
    /// Announces what the pilot is doing right now. The control loop runs at 20 Hz,
    /// so a per-frame <see cref="Status"/> call floods the console with one identical
    /// line per frame (the "Running gate checklist..." wall the startup phase used to
    /// print). Instead a line is emitted when it changes and then as a heartbeat, so a
    /// wait that takes a while stays visible instead of looking like a stall.
    /// </summary>
    private void PhaseStatus(string message, double dt)
    {
        if (message != _phaseStatus)
        {
            _phaseStatus = message;
            _phaseStatusHeartbeat = 0;
            Status(message);
            return;
        }

        _phaseStatusHeartbeat += dt;
        if (_phaseStatusHeartbeat >= PhaseStatusHeartbeatSeconds)
        {
            _phaseStatusHeartbeat = 0;
            Status(message);
        }
    }

    /// <summary>
    /// A checkpoint the executor gives up on ("state never confirmed (retry cap)",
    /// "no data from the sim") leaves the aircraft in exactly that state: the
    /// mixture never cut off, the fuel pump never on. The flow moves on, so without
    /// this the console would never mention it - only the file log would.
    /// </summary>
    private void OnChecklistItemDone(ChecklistItem item, string outcome)
    {
        if (!outcome.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            Status($"Checklist: {item.Display} :: {outcome}");
    }

    /// <summary>
    /// Keeps the console informed while the gate checklist runs: one line per
    /// checkpoint, including the item's index and its live state, so a checkpoint
    /// that is commanding (or stuck) is distinguishable from an idle pilot.
    /// </summary>
    private void ReportGateChecklist(double dt)
    {
        var status = _checklists.Status();
        var where = status.Index > 0
            ? $"{status.Index}/{status.Count} {status.Item} - {status.Expectation}"
            : status.Step;
        PhaseStatus($"Gate checklist {where} ({status.ItemState})", dt);
    }

    private void ExecuteStartup(double dt)
    {
        // The gate checklist (battery, avionics, fuel pumps, engine start...) is
        // the aircraft's own pre-departure flow: finish it before requesting
        // clearance and rolling out.
        if (_checklistsLoaded && !_checklists.GateComplete)
        {
            ReportGateChecklist(dt);
            return;
        }

        PhaseStatus("Starting engines and requesting clearance...", dt);

        // Ask the active controller for the IFR clearance, then wait for it. The sim's
        // own ATC confirms with ATC CLEARED IFR, so the wait only applies there;
        // BeyondATC has no read-back channel and its own copilot answers instead.
        if (!_startupClearanceRequested)
        {
            _startupClearanceRequested = true;
            _startupClearanceAskUtc = DateTime.UtcNow;
            _startupClearanceSent = _atc.Request(AtcRequestKind.IFRClearance, "engines started", FlightPhase.Startup);
        }

        var atc = _latestSnapshot.ATC;
        bool gaveUp = _atc.Requests.Status == AtcRequestStatus.Failed ||
                      DateTime.UtcNow - _startupClearanceAskUtc > ClearanceWait;
        bool waiting = _startupClearanceSent && _atc.Source == AtcSource.Native &&
                       !atc.ClearedIfr && !gaveUp;

        if (waiting)
        {
            PhaseStatus("Waiting for IFR clearance...", dt);
            return;
        }

        _departureClearanceReceived = true;
        _phaseManager.ForcePhase(FlightPhase.TaxiOut);
    }

    private bool _startupClearanceRequested;
    private bool _startupClearanceSent;
    private DateTime _startupClearanceAskUtc;
    private static readonly TimeSpan ClearanceWait = TimeSpan.FromSeconds(90);

    private bool _taxiStarted;
    private TaxiRoute? _activeTaxiRoute;
    private int _taxiRouteLegIndex;
    private const double TaxiLegCatchDistanceMeters = 60.0;

    /// <summary>How far before the hold-short node the aircraft stops, in metres.</summary>
    /// <remarks>
    /// The hold-short node sits on the hold line, so driving to it would already be on the
    /// wrong side of the markings. The graph is calibrated to tens of metres at best, so
    /// this standoff is what keeps the aircraft short of the line rather than on it.
    /// </remarks>
    private const double TaxiHoldShortStandoffMeters = 15.0;

    /// <summary>Distance at which the aircraft counts as being at the hold-short.</summary>
    private const double TaxiHoldShortCatchMeters = 12.0;

    /// <summary>
    /// Fallback hold-short distance, in metres, from the simulator's own runway distance
    /// when no ground graph is available. 150 m is roughly the distance from a hold line
    /// to the runway centreline at a wide intersection, and it is only trusted when the
    /// aircraft is pointing down the runway, so a taxiway crossing 150 m short of the
    /// strip does not read as being at the hold-short.
    /// </summary>
    private const double RunwayHoldShortFallbackMeters = 150.0;

    /// <summary>Heading agreement, in degrees, required to trust the runway distance.</summary>
    private const double RunwayHoldShortAlignmentDeg = 30.0;

    /// <summary>Brake pressure held at the hold-short: enough to hold on a slope.</summary>
    private const uint HoldShortBrakePosition = 16000;

    /// <summary>How close to the threshold a line-up counts as complete, in metres.</summary>
    private const double TaxiLineUpCompleteMeters = 60.0;

    /// <summary>Heading agreement, in degrees, required before the roll starts.</summary>
    private const double TaxiLineUpHeadingToleranceDeg = 5.0;

    /// <summary>A line-up that has not completed in this long is retried from the hold.</summary>
    private static readonly TimeSpan TaxiLineUpTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Gap between line-up attempts, so a failed line-up cannot chatter.</summary>
    private static readonly TimeSpan TaxiLineUpRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How near the turnaround point the back-taxi counts as reached, in metres. Larger than
    /// a taxi leg catch because the turnaround is at low speed and the last metres of it are
    /// covered coasting.
    /// </summary>
    private const double BacktrackTurnCatchMeters = 25.0;

    /// <summary>
    /// How far short of the takeoff threshold the aircraft turns round, in metres. It is
    /// the distance a turn-round needs: the aircraft arrives on the centreline travelling
    /// the wrong way down the runway, so it leaves the axis by roughly this much and swings
    /// back, and it has to clear the threshold rather than run past it.
    /// </summary>
    private const double BacktrackTurnStandoffMeters = 60.0;

    /// <summary>
    /// How near the runway an entry point has to be to count as joining the strip, in
    /// metres. The hold-short line sits this far outside the paved surface.
    /// </summary>
    private const double RunwayEntryOffsetMeters = 40.0;

    /// <summary>
    /// The least runway a back-taxi is worth saving, in metres. Below this the turn round
    /// costs more than the length it recovers.
    /// </summary>
    private const double BacktrackWorthwhileMeters = 200.0;

    /// <summary>A back-taxi that has not reached its turn point in this long has failed.</summary>
    private static readonly TimeSpan BacktrackTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Margin over the computed takeoff run before a back-taxi is called for.</summary>
    private const double BacktrackRunwayMargin = 1.15;

    /// <summary>
    /// The three things a pilot does between the gate and the runway: taxi the route, stop
    /// at the hold-short, and on clearance taxi onto the strip and line up. Splitting them
    /// is what makes holding short a behaviour rather than a side effect of running out of
    /// taxiway: while the route was the whole phase, its end fell through to "steer at the
    /// runway heading", which drives onto the runway whether or not anyone cleared it.
    /// <para>
    /// A fourth stage comes first: at a stand that offers one, the aircraft asks for a
    /// pushback and waits for the tug rather than taxiing off the stand under its own
    /// power. It is a stage rather than a phase of its own because it is the same physical
    /// thing as the hold-short that follows - the aircraft standing still on the ground,
    /// waiting for permission to move.
    /// </para>
    /// </summary>
    private enum TaxiOutStage
    {
        Route,
        HoldShort,
        /// <summary>On the stand, waiting for the tug the controller was asked for.</summary>
        Pushback,
        /// <summary>On the runway, back-taxiing to the threshold the takeoff roll starts at.</summary>
        Backtrack,
        LineUp
    }

    private TaxiOutStage _taxiOutStage = TaxiOutStage.Route;

    /// <summary>The hold-short node the current route was planned to, if the graph had one.</summary>
    private GroundNode? _holdShortNode;

    /// <summary>Where the aircraft must stop, in front of <see cref="_holdShortNode"/>.</summary>
    private (double lat, double lon)? _holdShortStopPoint;

    /// <summary>Threshold of the assigned departure end, where a line-up is aimed.</summary>
    private (double lat, double lon)? _lineUpTarget;

    /// <summary>
    /// Where the aircraft turns round at the far end of a back-taxi: just short of the
    /// threshold the takeoff roll starts from, so the turn happens on the strip rather than
    /// off the end of it.
    /// </summary>
    private (double lat, double lon)? _backtrackTurnTarget;

    private DateTime _backtrackStartedUtc;
    private DateTime _lineUpStartedUtc;
    private DateTime _lineUpRetryNotBeforeUtc;
    private int _lineUpAttempts;

    private void ExecuteTaxiOut(AircraftState s, double dt)
    {
        if (!_taxiStarted)
        {
            _taxiStarted = true;
            _atTaxiStart = true;
            // At a stand that offers a pushback the aircraft does not roll under its own
            // power: it holds while the controller is asked for a tug, and the brake is
            // released when the pushback is over. Everywhere else the brake comes off here,
            // because the aircraft is leaving the gate.
            //
            // A tug already on the nose counts too: engaging in the middle of a pushback
            // has to finish it rather than taxi the stand with a tug attached.
            _taxiOutStage = (s.Pushback.Available && s.ATC.OnParkingSpot) || s.Pushback.Attached
                ? TaxiOutStage.Pushback
                : TaxiOutStage.Route;
            if (_taxiOutStage != TaxiOutStage.Pushback) ReleaseGateBrakes();
            PlanTaxiToRunway(s);
        }

        // Once rolling, we have left the taxi start point
        if (s.IndicatedAirspeed > 5) _atTaxiStart = false;

        _autopilot.Mode = AutopilotMode.Direct;

        // Arriving at the end of the route is arriving at the hold-short, not an
        // instruction to keep going: with no graph the runway distance stands in for it.
        if (_taxiOutStage == TaxiOutStage.Route && TaxiRouteReachedGoal(s))
            _taxiOutStage = TaxiOutStage.HoldShort;

        switch (_taxiOutStage)
        {
            case TaxiOutStage.Pushback:
                ExecutePushback(s, dt);
                return;
            case TaxiOutStage.HoldShort:
                ExecuteHoldShort(s, dt);
                return;
            case TaxiOutStage.Backtrack:
                ExecuteBacktrack(s, dt);
                return;
            case TaxiOutStage.LineUp:
                ExecuteLineUp(s, dt);
                return;
        }

        var (targetLat, targetLon) = GetTaxiTarget();

        if (targetLat.HasValue)
        {
            var bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
            var distance = Geo.DistanceMeters(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);

            var isFinalLeg = _activeTaxiRoute is { Success: true } &&
                             _taxiRouteLegIndex >= _activeTaxiRoute.Legs.Count - 1;

            // Advance to the next leg when close enough, so the route is followed
            // rather than beelining to the final node. The final leg is never advanced
            // past: its node is the hold-short, and abandoning it dropped the pilot into
            // the no-target branch below, which steers at the runway heading and rolls on.
            while (!isFinalLeg &&
                   _activeTaxiRoute is { Success: true } && _taxiRouteLegIndex < _activeTaxiRoute.Legs.Count &&
                   distance < TaxiLegCatchDistanceMeters)
            {
                _taxiRouteLegIndex++;
                if (_taxiRouteLegIndex >= _activeTaxiRoute.Legs.Count) break;
                var leg = _activeTaxiRoute.Legs[_taxiRouteLegIndex];
                targetLat = GetNodeLatitude(leg.ToNode);
                targetLon = GetNodeLongitude(leg.ToNode);
                if (targetLat is null) break;
                distance = Geo.DistanceMeters(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
                bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
            }

            PhaseStatus(_activeTaxiRoute?.Success == true
                ? $"Taxiing to runway {DepartureRunwayFor(s) ?? ""}"
                : "Taxiing to runway...", dt);

            // Slow down for tighter turns
            var turnRate = Math.Abs(Geo.NormalizeDegrees(bearing - s.Heading));
            _autopilot.ThrottleCommand = turnRate > 60 ? 0.15 : turnRate > 25 ? 0.22 : 0.3;
            _autopilot.BankCommand = Math.Clamp(Geo.NormalizeDegrees(bearing - s.Heading) * 0.05, -0.6, 0.6);
            _autopilot.PitchCommand = 0;
            SetWheelBrake(0);
        }
        else
        {
            // No graph available: steer toward the runway heading. This only holds the
            // aircraft's direction until the runway distance detects the hold-short.
            PhaseStatus("Taxiing to runway...", dt);
            _autopilot.ThrottleCommand = 0.25;
            if (_runwayHeading != 0)
            {
                _autopilot.BankCommand = Math.Clamp(NormalizeDeg(_runwayHeading - s.Heading) * 0.02, -0.4, 0.4);
            }
            SetWheelBrake(0);
        }

        _autopilot.SendDirectControls();
    }

    /// <summary>
    /// Holds the aircraft on the stand while the controller is asked for a pushback, lets
    /// the tow carry it off the stand, and dismisses the tug once the aircraft is clear.
    /// <para>
    /// At a stand that offers one, a pushback is what actually moves the aircraft: taxing
    /// off under the aircraft's own power is what a pilot does at a stand without the
    /// service, and doing it here is how a nosewheel gets turned past its stops on a
    /// stand that was not built for it. The wait is bounded - a controller that cannot
    /// grant a pushback must not leave the aircraft at the gate for ever - and the tug is
    /// released by state: the brakes hold the aircraft where it was left, and the tug is
    /// told to let go, because "pushback complete" is not a variable the simulator
    /// publishes and a toggle fired blind is how a tug gets pulled onto the stand.
    /// </para>
    /// </summary>
    private void ExecutePushback(AircraftState s, double dt)
    {
        _autopilot.Mode = AutopilotMode.Direct;

        var now = DateTime.UtcNow;
        TrackPushback(s, now);

        // The tug is being released: everything else waits until it has let go.
        if (_pushbackDismissingUtc != default)
        {
            ExecutePushbackDetach(s, dt, now);
            return;
        }

        // A controller that ends the pushback ends it: the phrase is as good a signal as
        // the geometry, and it arrives earlier.
        if (_pushbackCompleteInstruction && s.Pushback.Attached && _pushbackSawTug)
        {
            _pushbackCompleteInstruction = false;
            Log("[Pushback] controller reports the pushback complete");
            BeginPushbackDismissal(s);
            return;
        }

        var decision = PushbackPlanner.Decide(s, _pushbackProgress, now, _pushbackTuning);

        switch (decision)
        {
            case PushbackDecision.Towing:
                PhaseStatus("Pushing back", dt);
                // The tug does the pulling here, so the aircraft's own brakes come off: a
                // parking brake left set holds it on the stand and the pushback never
                // happens at all.
                SetParkingBrake(false);
                SetWheelBrake(0);
                HoldIdleControls();
                return;

            case PushbackDecision.Wait:
                PhaseStatus(
                    _pushbackProgress.Requested
                        ? "Pushback requested - waiting for the tug"
                        : "Requesting pushback...",
                    dt);
                HoldOnStand();
                return;

            case PushbackDecision.GiveUp:
                // Nothing is going to move this aircraft off the stand, so the aircraft does
                // it itself: this is the ramp case and the refused-request case both.
                Log(_pushbackSawTug
                    ? "[Pushback] the tug is attached but is not pushing the aircraft back - taxiing out under our own power"
                    : "[Pushback] no pushback was granted - taxiing out under our own power");
                BeginTaxiFromStand(s);
                return;

            case PushbackDecision.Dismiss:
                BeginPushbackDismissal(s);
                return;
        }
    }

    /// <summary>Idle engines and neutral surfaces: the tug is flying this part.</summary>
    private void HoldIdleControls()
    {
        _autopilot.ThrottleCommand = 0;
        _autopilot.PitchCommand = 0;
        _autopilot.BankCommand = 0;
        _autopilot.SendDirectControls();
    }

    /// <summary>Holds the aircraft where it stands on the stand while the tug is waited for.</summary>
    private void HoldOnStand()
    {
        SetWheelBrake(HoldShortBrakePosition);
        HoldIdleControls();
    }

    /// <summary>
    /// Stops the pushback and tells the tug to let go. The brakes are applied by state -
    /// the axis holds the aircraft, the parking brake is set rather than toggled - and the
    /// tug is dismissed with the event the SDK publishes for exactly that.
    /// </summary>
    private void BeginPushbackDismissal(AircraftState s)
    {
        if (!s.Pushback.Attached)
        {
            // The tug released the aircraft on its own - the controller ended the pushback,
            // or the simulator did - so there is nothing to dismiss.
            Log("[Pushback] the tug has released the aircraft - taxiing out");
            BeginTaxiFromStand(s);
            return;
        }

        SetWheelBrake(HoldShortBrakePosition);
        SetParkingBrake(true);
        FireEventOnce("TUG_DISABLE");
        _pushbackDismissingUtc = DateTime.UtcNow;
        Log($"[Pushback] pushback complete - dismissing the tug after " +
            $"{PushbackPlanner.PushedMeters(s, _pushbackProgress):F0} m");
    }

    /// <summary>
    /// Waits for the tug to let go. A tug that stays attached after the brakes are set is
    /// released with the pushback toggle, which is only safe because the state says a tug
    /// is still on the aircraft; if even that does not work the aircraft goes anyway,
    /// because standing on a stand for ever is the worse failure.
    /// </summary>
    private void ExecutePushbackDetach(AircraftState s, double dt, DateTime now)
    {
        PhaseStatus("Pushback complete - releasing the tug", dt);

        if (!s.Pushback.Attached)
        {
            BeginTaxiFromStand(s);
            return;
        }

        if (now - _pushbackDismissingUtc > PushbackDetachTimeout && !_pushbackToggleFired)
        {
            _pushbackToggleFired = true;
            Log("[Pushback] the tug has not released the aircraft - toggling pushback to let go");
            _aircraft.SimConnect.FireEvent("TOGGLE_PUSHBACK");
        }

        if (now - _pushbackDismissingUtc > PushbackDetachTimeout * 3)
        {
            Log("[Pushback] the tug is still attached - taxiing out anyway");
            BeginTaxiFromStand(s);
        }
    }

    /// <summary>
    /// Leaves the stand: brakes off, and the taxi route drawn again from where the
    /// aircraft now stands, because the route planned on arrival was planned from the
    /// parking space and the pushback has moved the aircraft off it.
    /// </summary>
    private void BeginTaxiFromStand(AircraftState s)
    {
        ReleaseGateBrakes();
        _pushbackDismissingUtc = default;
        _pushbackToggleFired = false;
        _pushbackCompleteInstruction = false;
        _taxiOutStage = TaxiOutStage.Route;
        PlanTaxiToRunway(s);
    }

    /// <summary>Lets the aircraft roll: parking brake by state, wheel brake axis to zero.</summary>
    private void ReleaseGateBrakes()
    {
        SetParkingBrake(false);
        SetWheelBrake(0);
    }

    /// <summary>How long a tug is given to let go before the aircraft goes anyway.</summary>
    private static readonly TimeSpan PushbackDetachTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a pushback is waited for, and how far it has to carry the aircraft.</summary>
    private readonly PushbackTuning _pushbackTuning = new();

    private PushbackProgress _pushbackProgress = new();

    private DateTime _pushbackRequestedUtc;
    private DateTime _pushbackAttachedUtc;
    private (double lat, double lon)? _pushbackAttachPoint;
    private DateTime _pushbackLastMovedUtc;
    private bool _pushbackSawTug;
    private DateTime _pushbackDismissingUtc;
    private bool _pushbackToggleFired;
    private bool _pushbackCompleteInstruction;
    private bool _pushbackApprovedNoted;

    /// <summary>
    /// Records what the stand-holding stage has seen, so the planner is handed a complete
    /// picture of it: when the tug was asked for, when it arrived, where the aircraft stood
    /// when it did, and when it was last actually moving.
    /// </summary>
    private void TrackPushback(AircraftState s, DateTime now)
    {
        var requested = _atc.PushbackRequested;
        if (requested && _pushbackRequestedUtc == default) _pushbackRequestedUtc = now;

        if (s.Pushback.Attached)
        {
            if (!_pushbackSawTug)
            {
                _pushbackSawTug = true;
                _pushbackAttachedUtc = now;
                _pushbackAttachPoint = (s.Latitude, s.Longitude);
                _pushbackLastMovedUtc = now;
                Log($"[Pushback] tug attached{DescribePushbackTug(s)} - " +
                    $"pushing back from {s.Latitude:F5},{s.Longitude:F5}");
            }

            if (s.GroundSpeed >= PushbackPlanner.TowingGroundSpeedKt) _pushbackLastMovedUtc = now;
        }

        _pushbackProgress = new PushbackProgress
        {
            Requested = requested,
            RequestedUtc = _pushbackRequestedUtc == default ? now : _pushbackRequestedUtc,
            SawTug = _pushbackSawTug,
            AttachedUtc = _pushbackAttachedUtc,
            AttachPoint = _pushbackAttachPoint,
            LastMovedUtc = _pushbackLastMovedUtc
        };
    }

    private static string DescribePushbackTug(AircraftState s)
    {
        var direction = s.Pushback.Direction switch
        {
            PushbackDirection.Left => "to the left",
            PushbackDirection.Right => "to the right",
            _ => "straight"
        };
        return $" heading {s.Pushback.AngleDegrees:000}, {direction}";
    }

    /// <summary>
    /// Whether the aircraft is standing on a stand that offers a pushback, prepared for
    /// one and waiting to be moved. Prepared is the flight logic's business - the gate
    /// checklist done, the departure clearance dealt with - which is what the ATC bridge
    /// needs to know before it asks a controller for anything.
    /// </summary>
    private bool PushbackPrepared(AircraftState s) =>
        s.OnGround && s.ATC.OnParkingSpot && s.Pushback.Available && !s.Pushback.Attached &&
        (!_checklistsLoaded || _checklists.GateComplete) &&
        (_departureClearanceReceived || DateTime.UtcNow - _startupClearanceAskUtc > ClearanceWait);

    /// <summary>
    /// Asks for a pushback out of band - from the console or the panel - and starts the
    /// wait for the tug whether or not the phase machinery has got there yet.
    /// </summary>
    public string RequestPushback() =>
        _atc.Request(AtcRequestKind.Pushback, "manual", CurrentPhase)
            ? "[Pushback] pushback requested"
            : $"[Pushback] cannot request a pushback {PushbackGatingReason()}";

    private string PushbackGatingReason()
    {
        var s = _latestSnapshot;
        if (!s.OnGround) return "- the aircraft is airborne";
        if (s.Pushback.Attached) return "- a tug is already attached";
        if (!s.Pushback.Available) return "- no pushback is offered where the aircraft stands";
        if (!s.ATC.OnParkingSpot) return "- the aircraft is not on a parking space";
        return "";
    }

    /// <summary>
    /// Releases a tug that is on the aircraft, from the console or the panel: stop the
    /// pushback, hold the brakes and tell the tug to let go.
    /// </summary>
    public string DismissPushback()
    {
        var s = _latestSnapshot;
        if (!s.Pushback.Attached) return "[Pushback] no tug is attached";
        if (_taxiOutStage != TaxiOutStage.Pushback) _taxiOutStage = TaxiOutStage.Pushback;
        if (_pushbackDismissingUtc == default) BeginPushbackDismissal(s);
        return "[Pushback] pushback stopped - releasing the tug";
    }

    /// <summary>One line describing the tug, for the status command and the panel.</summary>
    public string PushbackText
    {
        get
        {
            var p = _latestSnapshot.Pushback;
            if (p.Attached)
            {
                return $"tug attached, {p.Direction.ToString().ToLowerInvariant()}, " +
                       $"heading {p.AngleDegrees:000}";
            }
            if (!p.Available) return _latestSnapshot.ATC.OnParkingSpot ? "none offered at this stand" : "not on a stand";
            // PUSHBACK WAIT is the simulator's own answer that the pushback has been
            // accepted and is only waiting to begin, which is worth reporting separately
            // from a request that has not been answered at all.
            if (p.Waiting) return "tug waiting to start";
            return _atc.PushbackRequested ? "requested - waiting for the tug" : "available";
        }
    }

    /// <summary>Whether a tug is on the aircraft right now.</summary>
    public bool PushbackAttached => _latestSnapshot.Pushback.Attached;

    /// <summary>Whether the stand the aircraft is on offers a pushback.</summary>
    public bool PushbackAvailable => _latestSnapshot.Pushback.Available;

    /// <summary>Whether a pushback has been asked for at this stand and none has been dismissed.</summary>
    public bool PushbackRequested => _atc.PushbackRequested;

    /// <summary>
    /// Whether the aircraft is on the stand holding for a pushback, which is what stops the
    /// panel and the console reporting a taxi-out that is not moving as a failure.
    /// </summary>
    public bool HoldingForPushback => CurrentPhase == FlightPhase.TaxiOut && _taxiOutStage == TaxiOutStage.Pushback;

    /// <summary>
    /// Whether the taxi-out has pushed back and is waiting for the tug to let go.
    /// </summary>
    public bool ReleasingPushback => _pushbackDismissingUtc != default;

    /// <summary>
    /// How far the tug has carried the aircraft from where it attached, in metres.
    /// </summary>
    public double PushbackPushedMeters =>
        PushbackPlanner.PushedMeters(_latestSnapshot, _pushbackProgress);

    /// <summary>
    /// Whether the taxi route has delivered the aircraft to the hold-short. The route was
    /// planned to end there, so reaching its last node means the hold line is reached;
    /// without a graph the simulator's own runway distance is the only equivalent signal.
    /// </summary>
    private bool TaxiRouteReachedGoal(AircraftState s)
    {
        if (_holdShortStopPoint.HasValue)
        {
            var distance = Geo.DistanceMeters(
                s.Latitude, s.Longitude, _holdShortStopPoint.Value.lat, _holdShortStopPoint.Value.lon);
            if (distance <= TaxiHoldShortCatchMeters) return true;
        }

        // The leg sequence is never advanced past its last leg - its node is the
        // hold-short - so arrival is measured as being close to that node rather than as
        // having run out of legs.
        if (_activeTaxiRoute is { Success: true } && _activeTaxiRoute.Legs.Count > 0)
        {
            var lastNode = _activeTaxiRoute.Legs[^1].ToNode;
            var airportNode = GetAirportNode(lastNode);
            if (airportNode != null)
            {
                var toGoal = Geo.DistanceMeters(
                    s.Latitude, s.Longitude, airportNode.Latitude, airportNode.Longitude);
                if (toGoal <= TaxiLegCatchDistanceMeters) return true;
            }
        }

        // No graph: the sim reports how far the aircraft is from the runway. Only trust
        // it when pointing down the strip, so a taxiway junction near the runway is not
        // mistaken for the hold-short.
        var runwayDistance = s.ATC.RunwayDistanceMeters;
        if (runwayDistance > 0 &&
            runwayDistance < RunwayHoldShortFallbackMeters &&
            _runwayHeading != 0 &&
            Math.Abs(NormalizeDeg(_runwayHeading - s.Heading)) < RunwayHoldShortAlignmentDeg)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Stops in front of the hold-short and waits. This is the phase that was missing:
    /// the position, not an ATC phrase, is what puts the aircraft here, so the hold-short
    /// is reached whether or not the controller ever says "line up and wait". Waiting here
    /// is also what unlocks the takeoff request, which was previously gated on a flag only
    /// that phrase could set and so was never sent.
    /// </summary>
    private void ExecuteHoldShort(AircraftState s, double dt)
    {
        _autopilot.Mode = AutopilotMode.Direct;
        _autopilot.ThrottleCommand = 0;
        _autopilot.PitchCommand = 0;
        _autopilot.BankCommand = 0;
        _autopilot.SendDirectControls();
        SetWheelBrake(HoldShortBrakePosition);

        // Position-derived awareness of the airport markings, the whole point of the phase.
        _atRunwayHoldShort = true;

        // The runway the controller assigned, not the one the plan files: the route and
        // the hold-short were planned to the assigned strip, so a hold-short announcement
        // naming the plan's runway named a strip the aircraft is not holding short of.
        var runway = DepartureRunwayFor(s);
        var where = string.IsNullOrEmpty(runway) ? "the runway" : $"runway {runway}";

        if (!_takeoffCleared)
        {
            PhaseStatus($"Holding short of {where} - awaiting takeoff clearance", dt);
            return;
        }

        // A clearance is not permission to enter an occupied runway. Holding here is the
        // only safe answer to traffic landing on or standing on the strip.
        if (!_trafficAllowsRunwayEntry)
        {
            PhaseStatus($"Holding short of {where} - {_traffic.Describe()}", dt);
            return;
        }

        if (DateTime.UtcNow < _lineUpRetryNotBeforeUtc)
        {
            PhaseStatus($"Holding short of {where} - awaiting clearance", dt);
            return;
        }

        // Reached the runway and cleared onto it. Where the taxiway joins decides what the
        // aircraft does next: a strip joined away from its start point may not hold enough
        // length for the takeoff run, in which case the aircraft has to taxi up the runway
        // backwards to the threshold and turn round before it can go.
        var backtrack = BacktrackRequired(s);
        if (backtrack && _backtrackTurnTarget.HasValue)
        {
            Status($"Cleared for takeoff {where} - back-taxiing to the threshold");
            Log("[Taxi] Back-taxiing: the taxiway joins the runway short of the run this " +
                "aircraft needs");
            _taxiOutStage = TaxiOutStage.Backtrack;
            _backtrackStartedUtc = DateTime.UtcNow;
            _lineUpAttempts++;
            return;
        }

        if (backtrack)
        {
            Log("[Taxi] Back-taxi needed but the runway end is not known - lining up as far as possible");
        }

        _taxiOutStage = TaxiOutStage.LineUp;
        _lineUpStartedUtc = DateTime.UtcNow;
        _lineUpAttempts++;
        Status($"Cleared for takeoff {where} - lining up");
    }

    /// <summary>
    /// Whether the aircraft must back-taxi the runway before it can take off.
    /// <para>
    /// A runway is usable only from where the taxiway joins it: the length ahead of that
    /// point is what the takeoff roll has to fit in, so a strip joined a third of the way
    /// along does not give the aircraft its published run however long the runway is.
    /// A controller says so explicitly - "enter runway 08 and backtrack, line up and wait" -
    /// and it is also derivable from the geometry, which is what this does because the
    /// simulators publish no back-taxi flag at all.
    /// </para>
    /// </summary>
    private bool BacktrackRequired(AircraftState s)
    {
        // The controller has said so, and that is authority enough on its own.
        if (_atcBacktrack) return true;

        var runway = DepartureRunwayFor(s);
        if (string.IsNullOrEmpty(runway)) return false;

        var info = DepartureAirport(s)?.FindRunway(runway);
        if (info is not { LengthMeters: > 0 } || double.IsNaN(info.Latitude)) return false;

        var needed = TakeoffRunMeters(s) * BacktrackRunwayMargin;
        if (needed <= 0) return false;

        // Where the taxiway joins the strip: the hold-short node the route ends at, or the
        // aircraft itself when it is already on the runway.
        (double lat, double lon) entry = _holdShortNode is { } node
            ? (node.Latitude, node.Longitude)
            : (s.Latitude, s.Longitude);

        if (!BacktrackPlanner.RequiresBacktrack(info, runway, entry.lat, entry.lon, needed)) return false;

        // Not worth turning round for: the length it recovers costs more than it is worth.
        var threshold = info.ThresholdFor(runway);
        if (threshold is null) return false;

        var rollEnd = info.PrimaryEnd.Equals(threshold.Value) ? info.SecondaryEnd : info.PrimaryEnd;
        var (along, offset) = Geo.ProjectOntoSegment(
            entry.lat, entry.lon, threshold.Value.lat, threshold.Value.lon, rollEnd.lat, rollEnd.lon);

        // An entry point beside the runway rather than on it is a taxiway junction, not a
        // joining point, so it says nothing about how much runway is left.
        if (offset >= Math.Max(30.0, info.WidthMeters / 2.0 + RunwayEntryOffsetMeters)) return false;

        if (BacktrackPlanner.RunwayAheadOf(info, along) >
            info.LengthMeters - BacktrackWorthwhileMeters) return false;

        _backtrackTurnTarget = BacktrackPlanner.TurnPointFor(info, runway, BacktrackTurnStandoffMeters);

        Log($"[Taxi] Runway join leaves {BacktrackPlanner.RunwayAheadOf(info, along):F0} m of runway ahead " +
            $"({Geo.MetersToFeet(needed):F0} ft of roll needed) - back-taxiing");

        return true;
    }

    /// <summary>Length of runway the takeoff roll needs, in metres.</summary>
    private double TakeoffRunMeters(AircraftState s)
    {
        var flaps = Math.Max(0, (int)Math.Round(s.FlapsHandleIndex));
        var fromTables = PerformanceCalculator.CalculateRequiredRunwayLength(_perf, s, flaps, true);
        double baseMeters;
        if (fromTables > 0)
        {
            baseMeters = Geo.FeetToMeters(fromTables);
        }
        else
        {
            // No published table: a light aircraft accelerates at roughly 5 kt/s and a heavier
            // one at about 3 kt/s, and distance is speed squared over twice the acceleration.
            var vr = RotationSpeedFor(s);
            if (vr <= 0) return 0;

            var accelerationKtPerSecond = s.GrossWeight > 26000 ? 3.0 : 5.0;
            var speedFtPerSecond = vr * 1.68781;
            baseMeters = Geo.FeetToMeters(speedFtPerSecond * speedFtPerSecond / (2 * accelerationKtPerSecond * 1.68781));
        }

        // React to the weather and the surface: a headwind shortens the roll and a tailwind
        // lengthens it, and a wet/icy/snowy runway needs more of the strip. This is the
        // figure the back-taxi decision and the hold-short both measure the runway against.
        var (headwind, _) = WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, _runwayHeading);
        return baseMeters * WeatherModel.TakeoffRunFactor(headwind, RotationSpeedFor(s), s.SurfaceCondition, s.SurfaceInfoValid);
    }

    /// <summary>The departure airport, when its ground data is loaded.</summary>
    private AirportData? DepartureAirport(AircraftState s)
    {
        return GetAirport(s.Navigation.DepartureICAO ?? "") ?? _airports.Values.FirstOrDefault(a => a.IsLoaded);
    }

    /// <summary>
    /// Taxies up the runway to the threshold the takeoff roll starts from, turns round, and
    /// hands over to the line-up. The aircraft is rolling backwards relative to the takeoff
    /// direction, so it moves more slowly than a taxi and turns on the axis rather than
    /// off the side of it.
    /// </summary>
    private void ExecuteBacktrack(AircraftState s, double dt)
    {
        _autopilot.Mode = AutopilotMode.Direct;
        SetWheelBrake(0);

        var target = _backtrackTurnTarget;
        if (target is null)
        {
            // Nothing to back-taxi to after all: line up here.
            _taxiOutStage = TaxiOutStage.LineUp;
            _lineUpStartedUtc = DateTime.UtcNow;
            return;
        }

        var bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, target.Value.lat, target.Value.lon);
        var distance = Geo.DistanceMeters(s.Latitude, s.Longitude, target.Value.lat, target.Value.lon);
        var headingError = Geo.NormalizeDegrees(bearing - s.Heading);

        _autopilot.ThrottleCommand = Math.Abs(headingError) > 60 ? 0.12 : Math.Abs(headingError) > 25 ? 0.16 : 0.2;
        _autopilot.BankCommand = Math.Clamp(headingError * 0.04, -0.5, 0.5);
        _autopilot.PitchCommand = 0;
        _autopilot.SendDirectControls();

        PhaseStatus("Back-taxiing along the runway", dt);

        // The aircraft turns round at the threshold: it arrives travelling up the runway
        // against the roll direction, so the end of the back-taxi is the point where it
        // begins pointing back down the runway. That is short of the threshold rather than
        // past it, because the line-up that follows steers at the threshold itself - so
        // aiming past it would leave the aircraft facing away and the line-up would never
        // rejoin the centreline within its timeout. The second condition catches a turn the
        // aircraft completed early: once it is facing back down the runway and is short of
        // the threshold, the back-taxi is over whether or not it covered the last metres.
        var reachedTurnPoint = distance < BacktrackTurnCatchMeters;
        var turnedRound = Math.Abs(headingError) < TaxiLineUpHeadingToleranceDeg &&
                          distance < BacktrackTurnStandoffMeters * 2;

        if (reachedTurnPoint || turnedRound)
        {
            Log("[Taxi] Back-taxi complete - turning round to line up at the threshold");
            _backtrackTurnTarget = null;
            _taxiOutStage = TaxiOutStage.LineUp;
            _lineUpStartedUtc = DateTime.UtcNow;
            return;
        }

        // A back-taxi that runs out of time is not a back-taxi: stop, and let the retry
        // delay decide what happens next rather than taxiing off the threshold.
        if (DateTime.UtcNow - _backtrackStartedUtc > BacktrackTimeout)
        {
            Log($"[Taxi] back-taxi did not reach the turn point after {BacktrackTimeout.TotalSeconds:F0}s " +
                $"(attempt {_lineUpAttempts}) - back to holding short");
            _atcBacktrack = false;
            _lineUpRetryNotBeforeUtc = DateTime.UtcNow + TaxiLineUpRetryDelay;
            _taxiOutStage = TaxiOutStage.HoldShort;
            _backtrackTurnTarget = null;
        }
    }

    /// <summary>
    /// Taxies onto the runway and aligns with the centreline after a takeoff clearance,
    /// then hands over to the takeoff roll. Steering straight to the threshold from the
    /// hold-short rather than allowing the generic runway-heading fallback keeps the
    /// aircraft on the strip instead of cutting across it.
    /// </summary>
    private void ExecuteLineUp(AircraftState s, double dt)
    {
        _autopilot.Mode = AutopilotMode.Direct;
        SetWheelBrake(0);

        var target = _lineUpTarget;
        var bearing = _runwayHeading;
        var distance = 0.0;

        if (target.HasValue)
        {
            bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, target.Value.lat, target.Value.lon);
            distance = Geo.DistanceMeters(s.Latitude, s.Longitude, target.Value.lat, target.Value.lon);
        }

        var headingError = Geo.NormalizeDegrees(bearing - s.Heading);

        var turnRate = Math.Abs(headingError);
        _autopilot.ThrottleCommand = turnRate > 60 ? 0.15 : turnRate > 25 ? 0.22 : 0.3;
        _autopilot.BankCommand = Math.Clamp(headingError * 0.05, -0.6, 0.6);
        _autopilot.PitchCommand = 0;
        _autopilot.SendDirectControls();

        var aligned = Math.Abs(headingError) < TaxiLineUpHeadingToleranceDeg;
        var atThreshold = target.HasValue && distance < TaxiLineUpCompleteMeters;

        if ((aligned && (atThreshold || !target.HasValue)))
        {
            Status("Lined up - commencing takeoff roll");
            _phaseManager.ForcePhase(FlightPhase.Takeoff);
            return;
        }

        // A line-up that will not complete is not a line-up: go back to the hold-short
        // rather than wandering on the runway. The retry delay stops it chattering.
        if (DateTime.UtcNow - _lineUpStartedUtc > TaxiLineUpTimeout)
        {
            _lineUpRetryNotBeforeUtc = DateTime.UtcNow + TaxiLineUpRetryDelay;
            _taxiOutStage = TaxiOutStage.HoldShort;
            Log($"[Taxi] line-up did not complete after {TaxiLineUpTimeout.TotalSeconds:F0}s " +
                $"(attempt {_lineUpAttempts}) - back to holding short");
        }
    }

    /// <summary>
    /// Builds a taxi route from the aircraft's current position to the hold-short of the
    /// assigned departure runway, using the facilities taxiway graph. Falls back to direct
    /// steering when no graph data is available.
    /// <para>
    /// The hold-short is chosen by reachability rather than by proximity to the runway
    /// centre. A taxi point payload carries no runway of its own, so the runway a
    /// hold-short guards is only known from the runway taxiway paths attached to it (see
    /// <see cref="TaxiRouter.HoldShortsFor"/>); the nearest hold-short to the centre of a
    /// parallel strip can belong to the other runway, which would send the aircraft to the
    /// wrong hold line.
    /// </para>
    /// </summary>
    private void PlanTaxiToRunway(AircraftState s)
    {
        var nav = s.Navigation;
        var icao = nav.DepartureICAO;
        var airport = icao is not null ? GetAirport(icao) : null;

        // If we do not have the departure airport yet, take whatever airport data
        // we do have (usually the arrival one).
        airport ??= _airports.Values.FirstOrDefault(a => a.IsLoaded);

        _holdShortNode = null;
        _holdShortStopPoint = null;
        _lineUpTarget = null;

        if (airport is null || airport.Nodes.Count == 0)
        {
            Log("[Taxi] No airport ground data yet; taxi will use direct steering");
            _activeTaxiRoute = new TaxiRoute { Success = false, FailureReason = "no airport ground data" };
            _taxiRouteLegIndex = 0;
            return;
        }

        var current = TaxiRouter.FindNearestNode(airport, s.Latitude, s.Longitude);
        if (current is null) return;

        // The controller's runway wins over the plan's: the simulator's ATC and BeyondATC
        // both publish a designated runway, and it routinely differs from what the flight
        // plan says, so the plan's was a strip the aircraft was never cleared for.
        var runway = DepartureRunwayFor(s);
        var constraints = new TaxiConstraints
        {
            AircraftWeightLbs = s.GrossWeight > 0 ? (uint)Math.Round(s.GrossWeight) : null,

            // Wingspan comes from the aircraft model in feet and path widths come from
            // the facilities API in metres, so the conversion has to happen here. A span
            // of zero means no model was parsed and must not read as "avoid every
            // taxiway".
            MinWidthMeters = _perf.WingSpan > 0 ? Geo.FeetToMeters(_perf.WingSpan) : 0,

            // The route ends at the hold-short, so it must not be allowed onto the strip
            // it is holding short of - unless the controller has cleared the aircraft to
            // cross it, which is what a "cross runway 25R" instruction means.
            HoldShortBefore = _atcHoldShortBefore ?? runway,
            RunwayName = runway,
            CrossableRunways = _atcCrossableRunways
        };

        // Where a line-up is aimed once cleared: the threshold of the assigned end, so the
        // aircraft enters at the start of the strip rather than at its middle.
        if (!string.IsNullOrEmpty(runway))
        {
            var runwayInfo = airport.FindRunway(runway);
            if (runwayInfo?.ThresholdFor(runway) is { } threshold)
            {
                _lineUpTarget = threshold;
            }
            else
            {
                Log($"[Taxi] No threshold known for runway {runway}; line-up will steer the heading only");
            }
        }

        GroundNode? goal = null;
        if (!string.IsNullOrEmpty(runway))
        {
            goal = TaxiRouter.FindReachableHoldShort(airport, current.Index, runway, constraints);
            if (goal is null)
            {
                Log($"[Taxi] No reachable hold-short for runway {runway}; " +
                    "holding short by runway distance instead");
            }
        }

        _holdShortNode = goal;

        var route = TaxiRouter.FindRoute(airport, current.Index, goal?.Index ?? current.Index, constraints);

        // A null goal is not a route to the current node: FindRoute would be handed the
        // start as its own destination. Make that the failure it is, so the caller falls
        // back to distance-based steering rather than trusting a degenerate route.
        if (goal is null && route is { Success: true, Legs.Count: 0 })
        {
            route = new TaxiRoute
            {
                Success = false,
                FailureReason = "no reachable hold-short for the assigned runway"
            };
        }

        // A controller that names the taxiways ("taxi to the 27 holding point via ALPHA one
        // left onto BRAVO") is giving an explicit path, and the shortest route through the
        // graph is a different path. Follow the named one when it resolves.
        if (_atcTaxiChain.Count > 0 && route.Success)
        {
            var chainGoal = _atcTaxiChain.LastOrDefault(index => airport.Nodes.ContainsKey(index));
            if (chainGoal != 0)
            {
                var chained = TaxiRouter.FindRouteViaChain(airport, current.Index, chainGoal, _atcTaxiChain, constraints);
                if (chained.Success && chained.TotalLengthMeters < route.TotalLengthMeters * 1.5 + 200)
                {
                    Log($"[Taxi] Following the controller's route: {string.Join(" -> ", _atcTaxiChain)}");
                    route = chained;
                    _taxiRouteLegIndex = 0;
                }
            }
        }

        _activeTaxiRoute = route;
        _taxiRouteLegIndex = 0;

        if (goal is not null)
        {
            // The stop point is pulled back from the hold-short node along the leg that
            // arrives at it, so the aircraft stops in front of the line rather than on it.
            var arrivalBearing = BearingToGoal(airport, current, goal, route);
            var (nodeLat, nodeLon) = (goal.Latitude, goal.Longitude);
            var (lat, lon) = Geo.OffsetByMeters(
                nodeLat, nodeLon,
                -Math.Sin(Geo.ToRadians(arrivalBearing)) * TaxiHoldShortStandoffMeters,
                -Math.Cos(Geo.ToRadians(arrivalBearing)) * TaxiHoldShortStandoffMeters);
            _holdShortStopPoint = (lat, lon);
        }

        if (route.Success)
        {
            Log($"[Taxi] Route to {runway ?? "(unassigned runway)"}: {route.TotalLengthMeters:F0} m, " +
                $"{route.Legs.Count} legs, stopping at hold-short node {goal?.Index.ToString() ?? "by distance"}");
        }
        else
        {
            Log($"[Taxi] Routing failed ({route.FailureReason}); falling back to direct steering " +
                "and a distance-based hold-short");
        }
    }

    /// <summary>
    /// Heading of the last leg that arrives at the hold-short, or the bearing straight from
    /// the start node when the hold-short is where the aircraft already stands.
    /// </summary>
    private static double BearingToGoal(AirportData airport, GroundNode start, GroundNode goal, TaxiRoute route)
    {
        var finalLeg = route.Legs.LastOrDefault();

        if (finalLeg != null)
        {
            var from = airport.FindNode(finalLeg.FromNode);
            if (from != null)
            {
                return Geo.BearingDegrees(from.Latitude, from.Longitude, goal.Latitude, goal.Longitude);
            }
        }

        return Geo.BearingDegrees(start.Latitude, start.Longitude, goal.Latitude, goal.Longitude);
    }

    private (double? lat, double? lon) GetTaxiTarget()
    {
        if (_activeTaxiRoute is not { Success: true }) return (null, null);
        if (_activeTaxiRoute.Legs.Count == 0) return (null, null);

        if (_taxiRouteLegIndex >= _activeTaxiRoute.Legs.Count) return (null, null);
        var leg = _activeTaxiRoute.Legs[_taxiRouteLegIndex];

        return (GetNodeLatitude(leg.ToNode), GetNodeLongitude(leg.ToNode));
    }

    /// <summary>
    /// Applies the parking brake by state rather than by toggle. PARKING_BRAKES is a
    /// toggle key event, so firing it on an already-set brake releases it - which is how a
    /// "hold position" instruction at a gate previously let go of the aircraft it had just
    /// been told to hold.
    /// </summary>
    private void SetParkingBrake(bool on)
    {
        if (_aircraft.State.ParkingBrakeSet == on) return;
        _aircraft.SimConnect.FireEvent("PARKING_BRAKE_SET", on ? 1u : 0u);
        Log($"[Brake] parking brake {(on ? "set" : "released")}");
    }

    /// <summary>
    /// Applies wheel brake pressure, but only on a change: the axes are written every
    /// frame, and repeating a brake event 20 times a second adds nothing to holding.
    /// </summary>
    private void SetWheelBrake(uint position)
    {
        if (_wheelBrakePosition == position) return;
        _wheelBrakePosition = position;
        // Fire on every change, including the release to zero. AXIS_LEFT_BRAKE_SET holds
        // its value until it is set again, so a "0" that is not transmitted leaves the
        // brake pressure applied - which stopped the line-up and the takeoff roll from
        // ever getting under way with any brake used.
        _aircraft.SimConnect.FireEvent("AXIS_LEFT_BRAKE_SET", position);
    }

    private uint _wheelBrakePosition;

    /// <summary>Event names already fired since the corresponding state changed.</summary>
    private readonly HashSet<string> _firedEvents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fires an event once until something un-arms it. Configuration events such as
    /// GEAR_DOWN are idempotent in the simulator, so re-firing them on every frame of the
    /// approach added no information and filled the log with identical entries.
    /// </summary>
    private void FireEventOnce(string eventName)
    {
        if (!_firedEvents.Add(eventName)) return;
        _aircraft.SimConnect.FireEvent(eventName);
    }

    /// <summary>Re-arms an event so the next change of state fires it again.</summary>
    private void RearmEvent(string eventName) => _firedEvents.Remove(eventName);

    /// <summary>Re-arms the gear events so the next approach fires them again.</summary>
    private void RearmConfigurationEvents()
    {
        RearmEvent("GEAR_DOWN");
        RearmEvent("GEAR_UP");
        RearmEvent("FLAPS_2");
        RearmEvent("FLAPS_3");
    }

    private double? GetNodeLatitude(int nodeIndex)
    {
        var airport = _airports.Values.FirstOrDefault(a => a.Nodes.ContainsKey(nodeIndex));
        return airport?.Nodes[nodeIndex].Latitude;
    }

    private double? GetNodeLongitude(int nodeIndex)
    {
        var airport = _airports.Values.FirstOrDefault(a => a.Nodes.ContainsKey(nodeIndex));
        return airport?.Nodes[nodeIndex].Longitude;
    }

    /// <summary>The node itself, which the leg advance loop needs only for its position.</summary>
    private GroundNode? GetAirportNode(int nodeIndex)
    {
        var airport = _airports.Values.FirstOrDefault(a => a.Nodes.ContainsKey(nodeIndex));
        return airport?.Nodes[nodeIndex];
    }

    private double? _takeoffTargetSpeed;
    private void ExecuteTakeoff(AircraftState s, double dt)
    {
        PhaseStatus("Takeoff roll", dt);

        if (!_takeoffTargetSpeed.HasValue)
        {
            _takeoffTargetSpeed = RotationSpeedFor(s);
        }

        // Full power - bounded by the engine's own limit, so an aircraft already at its EGT
        // redline is not held there for the whole roll - holding the runway centreline,
        // rotating at Vr. Direct control is the only path here: the native autopilot would
        // fight a manual rotation.
        _autopilot.Mode = AutopilotMode.Direct;
        _autopilot.EngageMaster();               // keep the FD/yaw damper
        _autopilot.ThrottleCommand = Math.Min(1.0, _envelope.MaxThrottle);

        var headingError = NormalizeDeg(_runwayHeading - s.Heading);
        _autopilot.BankCommand = Math.Clamp(headingError * 0.02, -0.5, 0.5);

        // Centre via rudder on the runway, because ailerons alone will drag a wingtip.
        _autopilot.RudderCommand = Math.Clamp(headingError * 0.01, -0.3, 0.3);
        // Rotation is a controlled pitch command whose rate is the aircraft's own: the load
        // factor is what a rotation at speed puts on the gear, and the envelope bounds it.
        _autopilot.PitchCommand = s.IndicatedAirspeed >= _takeoffTargetSpeed
            ? _envelope.CappedElevator(0.15)
            : 0.0;
        _autopilot.SendDirectControls();

        // After liftoff, retract gear once and set climb
        if (s.AltitudeAGL > 50 && !_gearRetracted)
        {
            _aircraft.SimConnect.FireEvent("GEAR_UP");
            _gearRetracted = true;
            Log("Gear retracted");
        }
        if (s.AltitudeAGL > 400)
        {
            _phaseManager.ForcePhase(FlightPhase.InitialClimb);
        }
    }

    private void ExecuteInitialClimb(AircraftState s, double dt)
    {
        PhaseStatus($"Initial climb {RoundTo(s.VerticalSpeed, 500)} fpm", dt);

        // One control path at a time: the native autopilot owns heading, speed and
        // altitude, and releasing the axes here prevents it fighting direct control
        // inherited from the takeoff roll.
        _autopilot.ReleaseAllControls();
        _autopilot.Mode = AutopilotMode.Hybrid;

        var targetSpeed = CommandedAirspeedKt(s);

        _autopilot.EngageMaster();

        // A departure vector issued before lift-off replaces the runway heading outright;
        // otherwise the aircraft tracks the runway plus a small offset, which is what a
        // controller means by "runway heading" without having heard the phrase.
        if (_atcHeadingDeg.HasValue)
        {
            _autopilot.SetHeading(_atcHeadingDeg.Value);
        }
        else
        {
            _autopilot.SetHeading(_runwayHeading + 5);
        }

        _autopilot.SetAirspeed(targetSpeed);
        _autopilot.EngageAutoThrottle();

        if (s.Altitude > TransitionAltitudeFtFor(s))
        {
            // Above the transition altitude the departure stops being a runway heading and
            // becomes a route: the plan's own path where the navigation database has
            // provided one, and the simulator's own navigation lock where it has not.
            SteerLateral(s);
        }

        if (s.Altitude >= TargetAltitudeFt(s) - 2000)
        {
            _phaseManager.ForcePhase(FlightPhase.Cruise);
        }
    }

    /// <summary>
    /// Rotation speed for the takeoff roll. Prefers the Vr table for the configured
    /// flaps, then the landing stall speed, and only then a speed-relative guess, so a
    /// mis-loaded aircraft never rotates at a speed that stalls it.
    /// </summary>
    private double RotationSpeedFor(AircraftState s)
    {
        if (_perf.TakeoffVSpeeds.Count > 0)
        {
            var flaps = (int)Math.Round(s.FlapsHandleIndex);
            var source = _perf.TakeoffVSpeeds.TryGetValue(flaps, out var exact)
                ? exact
                : _perf.TakeoffVSpeeds.TryGetValue(0, out var clean)
                    ? clean
                    : _perf.TakeoffVSpeeds.Values.First();

            var vr = PerformanceCalculator.InterpolateVSpeed(source.Entries, s.GrossWeight, "Vr");
            if (vr > 0) return vr;
        }

        // No usable Vr table: rotate a margin above the landing stall speed, which is the
        // documented middle step before the speed-relative guess. Falling straight to
        // "current IAS + 20" sampled on the first roll frame (IAS near zero) rotates an
        // aircraft below Vs0.
        if (_perf.Vso > 0) return Math.Max(_perf.Vso * 1.15, 40);

        // Last resort: keep 20 knots above whatever the aircraft is already doing.
        return s.IndicatedAirspeed + 20;
    }

    /// <summary>
    /// Best rate of climb speed, falling back to the wake category when the aircraft
    /// config does not publish a profile.
    /// </summary>
    private double ClimbSpeedFor(AircraftState s)
    {
        if (_perf.ClimbPerf.DefaultClimbSpeed > 0) return _perf.ClimbPerf.DefaultClimbSpeed;
        if (_perf.ClimbPerf.Profiles.Count > 0)
        {
            return _perf.ClimbPerf.Profiles[0].IASBelowTransition > 0
                ? _perf.ClimbPerf.Profiles[0].IASBelowTransition
                : _perf.ClimbPerf.Profiles[0].IASAboveTransition;
        }
        return _perf.Vso > 0 ? _perf.Vso * 1.8 : s.IndicatedAirspeed + 20;
    }

    /// <summary>
    /// Published transition altitude for the departure airport in feet, defaulting to
    /// the standard 18,000 ft when the airport has none.
    /// </summary>
    private double TransitionAltitudeFtFor(AircraftState s)
    {
        var airport = GetAirport(s.Navigation.DepartureICAO ?? "");
        return airport?.TransitionAltitudeMeters > 0
            ? Geo.MetersToFeet(airport.TransitionAltitudeMeters)
            : 18000;
    }

    private void ExecuteClimb(AircraftState s, double dt)
    {
        PhaseStatus($"Climbing {RoundTo(s.VerticalSpeed, 500)} fpm", dt);

        var target = TargetAltitudeFt(s);

        // Native AP with the plan's own path for LNAV, altitude hold for target. The path
        // is followed unless the controller has issued a heading, which is a vector and
        // overrides the plan for as long as it stands; with no path of its own the pilot
        // falls back on the simulator's own navigation lock, which is following the same
        // plan.
        SteerLateral(s);

        _autopilot.SetAltitude(target);
        // The vertical rate goes through the envelope: a commanded step is ramped in at the
        // rate the load factor allows, so reaching for the cruise altitude cannot snap the
        // aircraft into a new rate in one frame.
        _autopilot.SetVerticalSpeed(_envelope.CappedVerticalRateFpm(VerticalSpeedToward(target, s), dt));
        _autopilot.SetAirspeed(CommandedAirspeedKt(s));

        if (Math.Abs(s.Altitude - target) < 1000 && Math.Abs(s.VerticalSpeed) < 300)
        {
            _phaseManager.ForcePhase(FlightPhase.Cruise);
        }
    }

    /// <summary>
    /// A vertical speed that closes on the target altitude. The magnitude is scaled to the
    /// aircraft: the same figure that is a climb for a jet is a rate a light single cannot
    /// reach without slowing below its own stall speed, so the commanded climb is taken from
    /// the aircraft's own envelope rather than from a fixed number.
    /// <para>
    /// This replaces the fixed -1800 fpm descent and the <c>Clamp(s.VerticalSpeed, -2000,
    /// 2500)</c> climb, the latter of which simply echoed whatever the aircraft was already
    /// doing and so never actually steered it toward the level it had been cleared to.
    /// </para>
    /// </summary>
    private double VerticalSpeedToward(double targetAltitudeFt, AircraftState s)
    {
        var remaining = targetAltitudeFt - s.Altitude;

        // Close enough that the native altitude hold should capture: a gentle rate rather
        // than a slam into the level, which is what overshoots and then re-captures.
        if (Math.Abs(remaining) < 1500)
        {
            var gentle = Math.Min(400, Math.Abs(remaining) / 3.0);
            return Math.Sign(remaining) * gentle;
        }

        // The aircraft's own rate envelope, and the climb is deliberately commanded at a
        // fraction of it: the figure is a limit, not a target, and a light aircraft asked
        // for its full envelope would pitch up and slow below its own stall speed.
        double magnitude;
        if (remaining > 0)
        {
            magnitude = _envelope.Limits.MaxClimbRateFpm * 0.6;

            // An aircraft already off its target speed must not be asked to climb harder:
            // trading airspeed for altitude is how a climb ends in a stall. Back off the
            // commanded rate while the aircraft is slow, whatever is commanding the hold.
            var targetSpeed = TargetAirspeedKt(s);
            if (targetSpeed > 0 && s.IndicatedAirspeed < targetSpeed - 5)
            {
                magnitude *= 0.5;
            }
        }
        else
        {
            // A descent is a powered glide path, and the aircraft's published rate is the
            // rate it is flown at.
            magnitude = _envelope.Limits.MaxDescentRateFpm;
        }

        // Taper toward the level so the rate is a profile rather than a step held to the
        // last moment.
        var taper = Math.Min(1.0, Math.Abs(remaining) / 6000.0);
        return Math.Sign(remaining) * magnitude * (0.4 + 0.6 * taper);
    }

    /// <summary>Flap handle index as a whole number, for a configuration limit lookup.</summary>
    private static int FlapIndex(AircraftState s) => (int)Math.Round(s.FlapsHandleIndex);

    private void ExecuteCruise(AircraftState s, double dt)
    {
        PhaseStatus($"Cruise at {RoundTo(s.Altitude, 1000)} ft", dt);

        _autopilot.EngageMaster();
        var target = TargetAltitudeFt(s);
        _autopilot.SetAltitude(target);

        // A heading assignment is a vector off the plan's own path; without one the plan's
        // path is followed, or the simulator's own navigation lock when the navigation
        // database has provided no geometry.
        SteerLateral(s);

        _autopilot.SetAirspeed(CommandedAirspeedKt(s));

        // Decide when to descend: the 3:1 rule against the distance actually left to fly.
        // The distance used to be the total length of the plan, computed once when it was
        // loaded and never reduced as the aircraft flew, so the rule either fired on the
        // first frame of cruise or never fired at all.
        var distanceToDest = DistanceToDestinationNauticalMiles(s);
        var todDistance = (Math.Max(0, target - _destinationElevation) / 1000.0 * 3.0) + 20; // 3:1 rule

        if (distanceToDest > 0 && distanceToDest < todDistance)
        {
            Log($"[Approach] Top of descent: {distanceToDest:F0} nm to run for {todDistance:F0} nm of descent");
            _phaseManager.ForcePhase(FlightPhase.Descent);
        }
    }

    /// <summary>
    /// Distance from the aircraft to its destination over the ground, in nautical miles.
    /// Recomputed every frame because this is the quantity the top-of-descent rule measures:
    /// the plan's own total length is only a default cruise altitude, not where the aircraft
    /// is now.
    /// </summary>
    private double DistanceToDestinationNauticalMiles(AircraftState s)
    {
        var nav = s.Navigation;
        if (nav.DestinationLatitude == 0 && nav.DestinationLongitude == 0) return 0;
        if (s.Latitude == 0 && s.Longitude == 0) return 0;

        return Geo.DistanceMeters(s.Latitude, s.Longitude, nav.DestinationLatitude, nav.DestinationLongitude) / 1852.0;
    }

    private void ExecuteDescent(AircraftState s, double dt)
    {
        PhaseStatus($"Descending to {(int)TargetAltitudeFt(s)} ft", dt);

        _autopilot.EngageMaster();

        // A controller's waypoint or altitude assignment overrides the plan on the descent
        // too: a vector to final is a heading, and the level-off is the last altitude the
        // aircraft was cleared to. Without a vector the plan's own path is followed, which
        // is what brings the aircraft onto the published approach.
        SteerLateral(s);

        var targetAlt = TargetAltitudeFt(s);
        _autopilot.SetAltitude(targetAlt);
        // The descent rate goes through the envelope. It used to be a fixed -1800 fpm, which
        // on a light aircraft is a rate it is not built for and on a jet is a rate it can
        // barely reach below the tropopause; and it is ramped, so a step change in the cleared
        // level cannot be commanded in one frame.
        _autopilot.SetVerticalSpeed(_envelope.CappedVerticalRateFpm(VerticalSpeedToward(targetAlt, s), dt));
        _autopilot.SetAirspeed(CommandedAirspeedKt(s));

        if (s.Altitude <= targetAlt + 500)
        {
            _phaseManager.ForcePhase(FlightPhase.Approach);
        }
    }

    private bool _approachSetupDone;
    private bool _gearRetracted;
    private double _runwayHeading;
    private double _departureRunwayHeading;
    private double _arrivalRunwayHeading;
    private double _destinationElevation;

    /// <summary>
    /// The flight plan's total length in nautical miles. Used to pick a cruise altitude
    /// when the plan files none. How far there is left to fly is computed every frame from
    /// the aircraft's own position, because the plan's length does not shrink as the aircraft
    /// moves towards its destination.
    /// </summary>
    private double _planDistanceNm;

    private bool _atRunwayHoldShort;
    private bool _atTaxiStart;
    private bool _takeoffCleared;
    private bool _departureClearanceReceived;
    private bool _emergency;
    private bool _goAround;
    private double _targetCruiseAltitude = 34000;
    private double _localizerDeviation;

    // What the controller has most recently told this aircraft to do. Every field is
    // written by HandleAtcInstruction and read by the phase executors, so an instruction
    // actually changes what the aircraft flies. They are cleared by a "resume own
    // navigation" and by re-engaging, because each is a decision about a controller, a
    // flight plan and an aircraft that may no longer apply.
    private string? _atcDepartureRunway;
    private string? _atcArrivalRunway;
    private int? _atcAltitudeFt;
    /// <summary>
    /// The altitude to descend to and level off at, when the controller has cleared the
    /// aircraft below its cruise level. Tracked apart from <see cref="_atcAltitudeFt"/> so a
    /// "descend and maintain" spoken while still cruising does not raise the cruise level.
    /// </summary>
    private int? _atcDescentAltitudeFt;
    private int? _atcHeadingDeg;
    private int? _atcAirspeedKt;
    private double? _atcMach;
    private bool _atcBacktrack;
    private string? _atcHoldShortBefore;
    private readonly HashSet<string> _atcCrossableRunways = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _atcTaxiChain = new();
    private bool _atcClearedToLand;
    private bool _atcClearedForApproach;
    private int _noClearanceGoArounds;

    private void ExecuteApproach(AircraftState s, double dt)
    {
        PhaseStatus("Approach", dt);

        _autopilot.EngageMaster();

        // Resolve the arrival runway heading from real runway data, falling back to
        // the localizer course when tuned.
        ResolveArrivalRunway(s);

        if (!_approachSetupDone)
        {
            _approachSetupDone = true;
            _finalWeatherLogged = false;
            _autopilot.EngageApproachMode();
            _autopilot.EngageAutoThrottle();
            Log($"[Approach] Arrival runway {ArrivalRunwayFor(s) ?? "(unspecified)"}, " +
                $"heading {_runwayHeading:F0}, plan {s.Navigation.ApproachType ?? s.Navigation.ApproachName ?? "n/a"}, " +
                $"navdata {(SelectedApproach?.Describe() ?? "no published approach")}, " +
                $"cleared: {(_atcClearedForApproach ? "approach" : "no")}");

            // The arrival weather, once, so a gusty, crosswind or contaminated-ice approach
            // is on the record before final is flown.
            var (arrHeadwind, arrCrosswind) =
                WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, _runwayHeading);
            Log($"[Weather] Approach: wind {s.WindDirection:000}/{s.WindVelocity:F0} kt " +
                $"(xw {arrCrosswind:+0;-0}, hw {arrHeadwind:F0}), turbulence {_turbulence.Level}, " +
                $"airframe ice {s.StructuralIcePercent:F0}%, {SurfaceText} runway");

            // An approach the controller has not cleared for yet is still flown to the
            // final approach fix, which is where the request is made and answered.
            if (!_atcClearedForApproach && s.ATC.ArrivalAirportIsTowered)
            {
                Log("[Approach] Awaiting the controller's approach clearance");
            }
        }

        _autopilot.EngageApproachMode();

        if (s.AltitudeAGL < 1000 && !s.GearDown)
        {
            // Configure for landing. Guarded by the gear position rather than fired every
            // frame: GEAR_DOWN is a no-op once the gear is down, but re-firing it twenty
            // times a second is a stream of identical events in the log.
            FireEventOnce("GEAR_DOWN");
            FireEventOnce("FLAPS_2");
        }

        if (s.AltitudeAGL < 200)
        {
            _phaseManager.ForcePhase(FlightPhase.FinalApproach);
        }
    }

    /// <summary>
    /// The published final approach course in degrees magnetic, or null when the route has
    /// no published approach to take one from. The final leg's course is inbound, so the
    /// course a pilot flies is its reciprocal.
    /// </summary>
    private double? _approachCourseMagneticDeg;

    /// <summary>
    /// Sets the published final approach course from the route's last approach legs. The
    /// approach course is the last leg flown inbound, which for a straight-in approach is
    /// the reciprocal of the runway heading and for a circling or offset approach is not.
    /// </summary>
    private void ResolvePublishedApproachCourse()
    {
        var route = _navRoute;
        if (route?.Approach is null) { _approachCourseMagneticDeg = null; return; }

        var finalLegs = route.Approach.FinalLegs.Where(l => l.HasPosition).ToList();
        if (finalLegs.Count < 2) { _approachCourseMagneticDeg = null; return; }

        // The inbound course is the bearing of the last two fixes in the final approach,
        // less their difference in magnetic variation. All approach fixes are in true
        // degrees, so the bearings here are true already.
        var course = Geo.BearingDegrees(
            finalLegs[^2].Latitude, finalLegs[^2].Longitude,
            finalLegs[^1].Latitude, finalLegs[^1].Longitude);
        _approachCourseMagneticDeg = TrueToMagnetic(course, (float)route.MagneticVariationDeg);
    }

    /// <summary>
    /// Determines the landing runway heading. Prefers the real runway heading from the
    /// facilities API, then the tuned localizer course, then the current track.
    /// </summary>
    private void ResolveArrivalRunway(AircraftState s)
    {
        var airport = GetAirport(s.Navigation.ArrivalICAO ?? "") ?? _airports.Values.FirstOrDefault(a => a.IsLoaded);
        if (airport is null) return;

        var runway = ArrivalRunwayFor(s);
        var rw = !string.IsNullOrEmpty(runway) ? airport.FindRunway(runway) : null;

        if (rw != null)
        {
            _arrivalRunwayHeading = TrueToMagnetic(rw.HeadingDegTrue, airport.MagneticVariationDeg);
        }
        else if (s.Navigation.LocalizerAvailable && s.Navigation.NavSignalStrength > 10)
        {
            _arrivalRunwayHeading = Geo.NormalizeDegrees(s.Heading + s.Navigation.LocalizerDeviation * 10);
        }
    }

    /// <summary>Height on final at which a missing landing clearance forces a go-around.</summary>
    private const double NoClearanceGoAroundAgl = 500;
    /// <summary>Unanswered go-arounds before the aircraft lands anyway.</summary>
    private const int MaxNoClearanceGoArounds = 2;

    /// <summary>Feet per minute of descent per knot of groundspeed on a 3-degree path.</summary>
    private const double ApproachPathSlopeFpmPerKnot = 5.32;
    /// <summary>Feet per minute of descent added per dot of glideslope deviation.</summary>
    private const double GlideSlopeGainFpmPerDot = 250;

    /// <summary>
    /// The speed limit the published final approach carries, in knots, or 0 when it carries
    /// none. The limit is published per leg; the one that binds on final is the first one at
    /// or below the aircraft's current altitude, which is the step the aircraft is flying
    /// towards.
    /// </summary>
    private double PublishedApproachSpeedLimitKt(AircraftState s)
    {
        var legs = _navRoute?.Approach?.FinalLegs;
        if (legs is null || legs.Count == 0) return 0;

        foreach (var leg in legs.Where(l => l.HasPosition))
        {
            if (!(leg.SpeedLimitKt > 40)) continue;
            var legAltitudeFt = Geo.MetersToFeet(leg.AltitudeMeters);
            // Only a restriction ahead of the aircraft applies; without a published leg
            // altitude there is nothing to order them by, so the first limit found is used.
            if (legAltitudeFt <= 0 || legAltitudeFt <= s.AltitudeAGL + 5000) return leg.SpeedLimitKt;
        }

        return 0;
    }

    /// <summary>
    /// The altitude the published final approach requires the aircraft to be at or above
    /// where it is now, in feet, or 0 when the remaining legs carry no floor. A step-down
    /// restriction was parsed onto every leg and read by nothing, so an aircraft that should
    /// have levelled at a restriction simply descended through it.
    /// </summary>
    private double PublishedApproachFloorFt(AircraftState s)
    {
        var legs = _navRoute?.Approach?.FinalLegs;
        if (legs is null || legs.Count == 0) return 0;

        var floor = 0.0;
        foreach (var leg in legs.Where(l => l.HasPosition))
        {
            var minimum = leg.MinimumAltitudeFt;
            if (!(minimum > 0)) continue;
            var legAltitudeFt = Geo.MetersToFeet(leg.AltitudeMeters);
            if (legAltitudeFt > 0 && legAltitudeFt > s.AltitudeAGL) continue; // already passed
            if (minimum > floor) floor = minimum;
        }
        return floor;
    }

    /// <summary>
    /// The altitude the published final approach requires the aircraft to be at or below
    /// where it is now, in feet, or 0 when the remaining legs carry no ceiling.
    /// </summary>
    private double PublishedApproachCeilingFt(AircraftState s)
    {
        var legs = _navRoute?.Approach?.FinalLegs;
        if (legs is null || legs.Count == 0) return 0;

        var ceiling = 0.0;
        foreach (var leg in legs.Where(l => l.HasPosition))
        {
            var maximum = leg.MaximumAltitudeFt;
            if (!(maximum > 0)) continue;
            var legAltitudeFt = Geo.MetersToFeet(leg.AltitudeMeters);
            if (legAltitudeFt > 0 && legAltitudeFt > s.AltitudeAGL) continue; // already passed
            if (ceiling <= 0 || maximum < ceiling) ceiling = maximum;
        }
        return ceiling;
    }

    /// <summary>
    /// Descent rate, in feet per minute, of a 3-degree glidepath at this groundspeed -
    /// clamped to a shallow, always-descending window so the flare is never flown at a
    /// dive rate.
    /// </summary>
    private static double BaseApproachDescentFpm(double groundSpeedKnots)
    {
        var fpm = -Math.Clamp(groundSpeedKnots * ApproachPathSlopeFpmPerKnot, 300, 1500);
        return fpm;
    }

    private void ExecuteFinalApproach(AircraftState s, double dt)
    {
        PhaseStatus($"Final approach - {RoundTo(s.AltitudeAGL, 500)} ft AGL", dt);

        _autopilot.Mode = AutopilotMode.Direct;

        // Track the localizer directly when the ILS is tuned and has signal,
        // otherwise fall back to the runway heading.
        bool hasIls = s.Navigation.LocalizerAvailable && s.Navigation.NavSignalStrength > 10;
        _localizerDeviation = hasIls ? s.Navigation.LocalizerDeviation : 0;

        // Couple to the glideslope. The vertical path is driven only by the VERTICAL
        // signal: the localizer is lateral, and mixing it into the descent rate produces
        // level flight or a dive from a lateral offset alone. With a glideslope deviation
        // available the path tracks it; otherwise a constant 3-degree path is flown from
        // groundspeed, which is a real glidepath at any approach speed rather than a fixed
        // -600 fpm that is shallow on final and steep downwind.
        double targetVsi;
        if (hasIls && s.Navigation.GlideSlopeAvailable && Math.Abs(_localizerDeviation) < 2.5)
        {
            _autopilot.EngageApproachMode();
            var basePath = BaseApproachDescentFpm(s.GroundSpeed);
            targetVsi = basePath - s.Navigation.GlideSlopeDeviation * GlideSlopeGainFpmPerDot;
        }
        else
        {
            // Without a usable glideslope, steer the approach heading directly and track
            // the constant 3-degree path.
            targetVsi = BaseApproachDescentFpm(s.GroundSpeed);
        }
        targetVsi = Math.Clamp(targetVsi, -1600, -200);

        // A published step-down is a real restriction: the approach chart publishes an
        // at-or-above and an at-or-below altitude, and the aircraft is flown to stay inside
        // that window. They were parsed and then read by nothing, so an aircraft that should
        // have levelled off at a restriction descended straight through it. The window only
        // tightens what the path already commands - it never raises a descent rate or turns
        // the approach into a climb.
        var approachFloor = PublishedApproachFloorFt(s);
        if (approachFloor > 0 && s.AltitudeAGL < approachFloor && targetVsi < 0)
        {
            // Below a floor and still descending: the next leg commands a shallower path,
            // and beyond a small tolerance the restriction is not being flown.
            targetVsi = Math.Max(targetVsi, -300);
        }

        var approachCeiling = PublishedApproachCeilingFt(s);
        if (approachCeiling > 0 && s.AltitudeAGL > approachCeiling + 300 && targetVsi > -300)
        {
            // Above a ceiling and not descending hard enough to get below it.
            targetVsi = Math.Min(targetVsi, -800);
        }

        // A final approach descent is a powered descent, and the glideslope gain can command
        // a rate the aircraft is not built for. Through the envelope, both bounds and the
        // load-factor ramp apply, so tracking a dot on the glideslope cannot overspeed the
        // vertical profile.
        targetVsi = _envelope.CappedVerticalRateFpm(targetVsi, dt);

        // The heading flown on final is the published final approach course when the
        // navigation database carries one - which is what the aircraft is actually cleared
        // to fly, and is not always the runway heading - then the runway heading, and only
        // then the current track.
        var finalCourse = _approachCourseMagneticDeg ??
                          (_runwayHeading != 0 ? _runwayHeading : s.Heading);

        // Crab into wind so the ground track follows the final approach course, and read the
        // crosswind/headwind for the speed increment. Both are resolved in the magnetic frame,
        // matching the heading the direct control is commanded with.
        var (finalHeadwind, finalCrosswind) =
            WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, finalCourse);
        var windCorrection = WeatherModel.WindCorrectionAngleDeg(finalCrosswind, TrueAirspeedFor(s));
        var approachHeading = Geo.NormalizeDegrees(finalCourse + windCorrection);

        var vref = PerformanceCalculator.CalculateVRef(_perf, s.GrossWeight, 2);
        // NaN fails every comparison, so "< 50" alone would let it through; the negated
        // form also rejects the non-finite result.
        if (!(vref >= 50)) vref = 70;
        // Ice contamination raises the stall speed, so the reference speed is bumped for it.
        vref += WeatherModel.ApproachIceIncrementKt(s.StructuralIcePercent);

        // A speed assignment is flown on the approach too - "reduce to minimum approach
        // speed" or a Mach number that only applies up here. Without one, the reference
        // speed plus a margin is what a pilot flies on final, then the wind, ice and
        // turbulence increment a real pilot adds for a gusty or contaminated approach.
        var approachSpeed = _atcAirspeedKt ?? Math.Max(vref + 10, TargetAirspeedKt(s));
        approachSpeed += WeatherModel.ApproachWeatherIncrementKt(finalHeadwind, s.StructuralIcePercent, _turbulence.Level);

        // The published final approach may carry a speed limit of its own, and that one is
        // the airport's rather than the controller's. It was parsed onto every leg and read
        // by nothing, so a 140 kt limit past the FAF was flown as whatever the aircraft
        // preferred. It caps the aircraft's own target only: an ATC speed assignment is
        // still flown exactly as given, the same convention the turbulence reduction
        // follows.
        var publishedLimit = PublishedApproachSpeedLimitKt(s);
        if (publishedLimit > 0 && !_atcAirspeedKt.HasValue && approachSpeed > publishedLimit)
        {
            approachSpeed = publishedLimit;
        }

        // The approach speed is capped into the envelope, which on a configured aircraft also
        // means Vfe and Vle: with the gear and landing flaps selected, the approach cannot be
        // flown above the speed that configuration is rated for.
        approachSpeed = _envelope.CappedAirspeedKt(approachSpeed, FlapIndex(s), s.GearDown);

        if (Math.Abs(finalCrosswind) >= WeatherModel.SignificantCrosswindKt && !_finalWeatherLogged)
        {
            _finalWeatherLogged = true;
            Log($"[Weather] Final crosswind {finalCrosswind:+0;-0} kt, headwind {finalHeadwind:F0} kt, " +
                $"turbulence {_turbulence.Level} - approach {approachSpeed:F0} kt");
        }

        // Landing configuration: gear and flaps, each only on a state change.
        if (!s.GearDown) FireEventOnce("GEAR_DOWN");
        if (s.FlapsHandleIndex < 3) FireEventOnce("FLAPS_3");

        _autopilot.UpdateDirectControl(s, new ControlTargets
        {
            TargetHeading = approachHeading,
            TargetAirspeed = approachSpeed,
            TargetVerticalSpeed = targetVsi,
            UseDirectControl = true,
            // A lower bank ceiling in turbulence keeps the aircraft from chasing gusts on
            // the localizer on a gusty final.
            MaxBankDeg = WeatherModel.ControlBankDeg(_turbulence.Level)
        }, dt);

        // No clearance, no landing. Below the decision height the only way to avoid
        // touching down unannounced is to go around, which is what a pilot does, and after
        // a couple of unanswered go-arounds the controller is not going to answer this one
        // either and the aircraft lands.
        if (s.AltitudeAGL < NoClearanceGoAroundAgl && !_atcClearedToLand &&
            _atc.LandingClearanceRequested && _noClearanceGoArounds < MaxNoClearanceGoArounds)
        {
            Log($"[ATC] No landing clearance at {s.AltitudeAGL:F0} ft AGL; going around");
            _noClearanceGoArounds++;
            _goAround = true;
        }

        if (s.AltitudeAGL < 30)
        {
            _phaseManager.ForcePhase(FlightPhase.Landing);
        }
    }

    private void ExecuteLanding(AircraftState s, double dt)
    {
        PhaseStatus("Landing", dt);

        _autopilot.Mode = AutopilotMode.Direct;

        // Flare: the pitch target is a fixed attitude and the load factor it produces is
        // bounded by the elevator limiter, because a load factor is a rate of rotation and
        // not an attitude - a fixed number of degrees is a different load factor on every
        // aircraft, while the elevator limiter reads what the airframe is actually doing.
        var flarePitch = 5.0;

        // The flare speed is the aircraft's own reference speed plus a small margin, never a
        // fixed figure: 60 knots is a light aircraft's approach speed and a heavy jet's stall
        // speed, and the envelope floor is what stops it ever being commanded below that.
        var flareSpeed = _envelope.CappedAirspeedKt(
            PerformanceCalculator.CalculateVRef(_perf, s.GrossWeight, 3) + 5,
            FlapIndex(s), s.GearDown);

        _autopilot.UpdateDirectControl(s, new ControlTargets
        {
            TargetPitch = flarePitch,
            TargetAirspeed = flareSpeed,
            UseDirectControl = true
        }, dt);

        if (s.OnGround)
        {
            _phaseManager.ForcePhase(FlightPhase.Rollout);
        }
    }

    private void ExecuteRollout(AircraftState s, double dt)
    {
        PhaseStatus("Rollout", dt);

        _autopilot.ThrottleCommand = 0.0;
        _autopilot.SendDirectControls();

        // Apply spoilers and brakes. Guarded on state because both lines used to run
        // every frame of the rollout.
        if (!_spoilersDeployed)
        {
            _aircraft.SimConnect.FireEvent("SPOILERS_ON");
            _spoilersDeployed = true;
        }
        SetWheelBrake(RolloutBrakePosition);

        if (s.GroundSpeed < 5)
        {
            // FLAPS_0 is not a SimConnect event: the SDK publishes FLAPS_1..4 and
            // FLAPS_UP, so firing the retraction by its event name leaves the handle
            // wherever it was and answers with NAME_UNRECOGNIZED.
            _aircraft.SimConnect.FireEvent("FLAPS_UP");
            _spoilersDeployed = false;
            _phaseManager.ForcePhase(FlightPhase.TaxiIn);
        }
    }

    private bool _spoilersDeployed;
    private const uint RolloutBrakePosition = 16000;

    private void ExecuteTaxiIn(AircraftState s, double dt)
    {
        PhaseStatus("Taxiing to gate...", dt);

        if (!_taxiInPlanned)
        {
            _taxiInPlanned = true;
            // Release the parking brake by state, so a brake already released by hand is
            // not toggled back on.
            SetParkingBrake(false);
            PlanTaxiToGate(s);
        }

        _autopilot.Mode = AutopilotMode.Direct;

        var (targetLat, targetLon) = GetTaxiTarget();
        if (targetLat.HasValue)
        {
            var bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
            var distance = Geo.DistanceMeters(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);

            while (_activeTaxiRoute is { Success: true } && _taxiRouteLegIndex < _activeTaxiRoute.Legs.Count &&
                   distance < TaxiLegCatchDistanceMeters)
            {
                _taxiRouteLegIndex++;
                if (_taxiRouteLegIndex >= _activeTaxiRoute.Legs.Count) break;
                var leg = _activeTaxiRoute.Legs[_taxiRouteLegIndex];
                targetLat = GetNodeLatitude(leg.ToNode);
                targetLon = GetNodeLongitude(leg.ToNode);
                if (targetLat is null) break;
                distance = Geo.DistanceMeters(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
                bearing = Geo.BearingDegrees(s.Latitude, s.Longitude, targetLat.Value, targetLon!.Value);
            }

            var turnRate = Math.Abs(Geo.NormalizeDegrees(bearing - s.Heading));
            _autopilot.ThrottleCommand = turnRate > 60 ? 0.12 : turnRate > 25 ? 0.2 : 0.28;
            _autopilot.BankCommand = Math.Clamp(Geo.NormalizeDegrees(bearing - s.Heading) * 0.05, -0.6, 0.6);
            _autopilot.PitchCommand = 0;
            _atGate = _activeTaxiRoute is { Success: true } && _taxiRouteLegIndex >= _activeTaxiRoute.Legs.Count && distance < 80;
        }
        else
        {
            // No route to a gate. Stop where the aircraft is rather than coasting: the
            // cluster of states that reached here previously left the aircraft rolling to
            // a standstill mid-taxiway, then went straight to shutdown with the engines
            // running and no brake set.
            PhaseStatus("No route to gate - stopping and securing", dt);
            _autopilot.ThrottleCommand = 0;
            _autopilot.BankCommand = 0;
            _autopilot.PitchCommand = 0;
            _autopilot.SendDirectControls();
            SetWheelBrake(HoldShortBrakePosition);
            _atGate = true;
            _unreachableGate = true;
        }

        _autopilot.SendDirectControls();

        if (_atGate && State.GroundSpeed < 1)
        {
            if (_unreachableGate) SetParkingBrake(true);
            _phaseManager.ForcePhase(FlightPhase.Shutdown);
        }
    }

    private bool _taxiInPlanned;
    private bool _atGate;

    /// <summary>True when the gate could not be reached and the aircraft stopped short of it.</summary>
    private bool _unreachableGate;

    private readonly RandomizedFlightOptions _randomizedFlights = new();
    private DateTime _automationServiceUntilUtc;
    private bool _automationPlanningInProgress;
    private string _automationStatus = "Off";
    private string _automationLastPlanSummary = "";
    private int _automationLegsCompleted;
    private string? _lastRandomDestinationIcao;

    /// <summary>Plans a route from the runway exit to an arrival gate after landing.</summary>
    private void PlanTaxiToGate(AircraftState s)
    {
        var airport = GetAirport(s.Navigation.ArrivalICAO ?? "") ?? _airports.Values.FirstOrDefault(a => a.IsLoaded);
        if (airport is null || airport.Nodes.Count == 0)
        {
            Log("[Taxi] No arrival airport ground data; parking at current position");
            _activeTaxiRoute = new TaxiRoute { Success = false, FailureReason = "no airport data" };
            _taxiRouteLegIndex = 0;
            return;
        }

        var current = TaxiRouter.FindNearestNode(airport, s.Latitude, s.Longitude);
        var gate = TaxiRouter.FindNearestParking(airport, s.Latitude, s.Longitude);
        if (current is null || gate is null || current.Index == gate.Index)
        {
            _activeTaxiRoute = new TaxiRoute { Success = false, FailureReason = "no goal node" };
            _taxiRouteLegIndex = 0;
            return;
        }

        var route = TaxiRouter.FindRoute(airport, current.Index, gate.Index, new TaxiConstraints
        {
            // No published weight is a missing reading, not a zero-weight aircraft: the
            // cast to uint wraps it, and a 4-billion-pound constraint excludes every path
            // that carries a rating.
            AircraftWeightLbs = s.GrossWeight > 0 ? (uint?)Math.Round(s.GrossWeight) : null,
        });

        _activeTaxiRoute = route;
        _taxiRouteLegIndex = 0;

        if (route.Success)
        {
            Log($"[Taxi] Route to gate {gate.ParkingName ?? gate.Index.ToString()}: {route.TotalLengthMeters:F0} m");
        }
        else
        {
            Log($"[Taxi] Inbound routing failed: {route.FailureReason}");
        }
    }

    private void ExecuteShutdown(AircraftState s)
    {
        // Secure the aircraft: parking brake set and wheel pressure released, so the pilot
        // is not handed an aircraft held in place by the brakes alone.
        SetParkingBrake(true);
        SetWheelBrake(0);

        if (TryContinueAutomationAfterShutdown(s))
        {
            return;
        }

        Status("Shutdown complete - AI pilot disengaging");
        SetControlState(PilotControlState.Manual);
    }

    private bool TryContinueAutomationAfterShutdown(AircraftState s)
    {
        if (!_randomizedFlights.Enabled) return false;
        if (_randomizedFlights.StopAfterThisFlight)
        {
            _automationStatus = "Stopped after current flight";
            _randomizedFlights.Enabled = false;
            _randomizedFlights.StopAfterThisFlight = false;
            return false;
        }

        if (!_automationPlanningInProgress)
        {
            _automationPlanningInProgress = true;
            var minutes = Math.Max(1, _randomizedFlights.ServiceMinutesBetweenLegs);
            _automationServiceUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
            _automationStatus = $"Servicing aircraft ({minutes} min)";
            Status($"Shutdown complete - servicing aircraft for {minutes} minutes before next randomized flight");
            return true;
        }

        if (DateTime.UtcNow < _automationServiceUntilUtc)
        {
            return true;
        }

        if (!TryLoadNextRandomLeg(s, out var status))
        {
            _automationStatus = status;
            _randomizedFlights.Enabled = false;
            return false;
        }

        _automationPlanningInProgress = false;
        _automationLegsCompleted++;
        _phaseManager.Reset();
        _phaseManager.ForcePhase(FlightPhase.Startup);
        _startupComplete = false;
        _taxiStarted = false;
        _takeoffCleared = false;
        _departureClearanceReceived = false;
        _startupClearanceRequested = false;
        _startupClearanceSent = false;
        Status("Randomized flight loaded - starting next leg");
        return true;
    }

    private bool TryLoadNextRandomLeg(AircraftState s, out string status)
    {
        status = "";
        var departureIcao = s.Navigation.ArrivalICAO ?? s.Navigation.DepartureICAO ?? "";
        if (string.IsNullOrWhiteSpace(departureIcao))
        {
            status = "Randomized flights stopped: no departure ICAO for next leg.";
            return false;
        }

        if (!RandomizedFlightPlanner.TryPlanNextLeg(
                departureIcao,
                _airports.Values.ToList(),
                _perf,
                s,
                _random,
                _lastRandomDestinationIcao,
                out var leg,
                out var failure))
        {
            status = $"Randomized flights stopped: {failure}";
            return false;
        }

        var departure = _airports[leg.DepartureIcao];
        var destination = _airports[leg.DestinationIcao];
        var pln = FlightPlanWriter.WriteLegPlanToTempPln(leg, departure, destination);
        LoadFlightPlan(pln);

        _targetCruiseAltitude = leg.CruiseAltitudeFt;
        _lastRandomDestinationIcao = leg.DestinationIcao;
        _automationLastPlanSummary =
            $"{leg.DepartureIcao}->{leg.DestinationIcao} ALT {leg.CruiseAltitudeFt:F0}ft " +
            $"ALTN {leg.AlternateIcao} FUEL {leg.Fuel.TotalGallons:F0}gal";
        _automationStatus =
            $"Leg planned: {leg.DepartureIcao} to {leg.DestinationIcao}, alternate {leg.AlternateIcao}";
        Log($"[Automation] {_automationLastPlanSummary}");
        status = _automationStatus;
        return true;
    }

    private void ExecuteGoAround(AircraftState s, double dt)
    {
        if (!_goAroundLogged)
        {
            _goAroundLogged = true;
            Log("[Approach] Go-around" +
                (HasPublishedMissedApproach
                    ? $" - flying the published missed approach ({MissedApproachDescription})"
                    : " - no published missed approach, flying the runway heading"));
        }

        PhaseStatus("Go-around", dt);

        _autopilot.Mode = AutopilotMode.Direct;
        _goAround = false;

        // A go-around is flown first and talked about afterwards: the aircraft has to be
        // established in the climb before the controller can be asked for anything, so the
        // approach clearance and the landing clearance are both cancelled.
        _atcClearedForApproach = false;
        _atcClearedToLand = false;

        // Go-around thrust, bounded by the engine's own limit. A missed approach is the
        // highest-power moment of the entire flight, and holding an engine at its EGT
        // redline through it is what damages it.
        _autopilot.ThrottleCommand = Math.Min(1.0, _envelope.MaxThrottle);

        // A controller who says "go around" usually follows it with a heading, which is
        // applied as soon as it arrives. Without one the *published* missed approach is
        // flown: the chain of fixes the airport publishes for exactly this situation,
        // starting at the missed approach point, rather than a runway heading that may not
        // be where the procedure goes.
        double targetHeading;
        if (_atcHeadingDeg.HasValue)
        {
            targetHeading = _atcHeadingDeg.Value;
        }
        else
        {
            var missed = MissedApproachGuidance(s);
            if (missed is { } guidance && !guidance.Complete && guidance.ActiveLeg is not null)
            {
                targetHeading = NavPathFollower.TrueToMagnetic(
                    guidance.DesiredTrackTrueDeg, NavMagneticVariation(s));
            }
            else
            {
                targetHeading = _runwayHeading;
            }
        }

        // The go-around pitch attitude, held as a target; the load factor it actually
        // produces is bounded by the elevator limiter inside the direct-control path, which
        // is the only protection that reads what the airframe is really experiencing.
        var goAroundPitch = 15.0;

        // The speed is the enroute target and the go-around floor, both capped into the
        // envelope, and the gear-up is only commanded once the aircraft is actually climbing:
        // retracting it at 200 ft on a slow go-around is a gear retraction below its own
        // takeoff-safe speed.
        var goAroundSpeed = _envelope.CappedAirspeedKt(
            Math.Max(180, TargetAirspeedKt(s)), FlapIndex(s), s.GearDown);

        _autopilot.UpdateDirectControl(s, new ControlTargets
        {
            TargetPitch = goAroundPitch,
            TargetHeading = targetHeading,
            TargetAirspeed = goAroundSpeed,
            UseDirectControl = true
        }, dt);

        if (!_gearRetracted && s.AltitudeAGL > 400 && s.VerticalSpeed > 300)
        {
            _gearRetracted = true;
            _aircraft.SimConnect.FireEvent("GEAR_UP");
        }

        // The missed approach holds at its published altitude; the go-around is over once
        // the aircraft is established in the climb and following the procedure.
        var holdAltitude = _navRoute?.Approach?.MissedAltitudeFt ?? 0;
        var established = holdAltitude > 0
            ? s.Altitude > holdAltitude - 500
            : s.AltitudeAGL > 1000;

        if (established)
        {
            Log($"[Approach] Missed approach established" +
                (holdAltitude > 0 ? $" at {s.Altitude:F0} ft (procedure altitude {holdAltitude:F0} ft)" : ""));
            _phaseManager.ForcePhase(FlightPhase.Climb);
        }
    }

    /// <summary>
    /// The published missed approach followed leg by leg from the aircraft's own position.
    /// Null when the airport publishes no geometry for one, in which case the go-around
    /// falls back on the runway heading.
    /// </summary>
    private NavGuidance? MissedApproachGuidance(AircraftState s)
    {
        var follower = _missedFollower;
        if (follower is null || !follower.Legs.Any(l => l.HasPosition)) return null;
        if (s.Latitude == 0 && s.Longitude == 0) return null;

        var guidance = follower.Update(s.Latitude, s.Longitude, s.GroundSpeed);
        return guidance.Complete ? null : guidance;
    }

    private bool _goAroundLogged;

    #endregion

    #region Control Delegation

    public void SetControlState(PilotControlState newState)
    {
        if (_controlState == newState) return;

        _controlState = newState;
        OnControlStateChanged?.Invoke(newState);

        switch (newState)
        {
            case PilotControlState.AiActive:
                Log("AI control ENGAGED");
                Status("AI pilot engaged");
                _autopilot.Mode = AutopilotMode.Hybrid;
                _startupComplete = false;
                _taxiStarted = false;
                _taxiInPlanned = false;
                _activeTaxiRoute = null;
                _taxiRouteLegIndex = 0;
                _approachSetupDone = false;

                // Every piece of flight state has to go back to how the aircraft
                // found itself. Anything left over from the last engagement is a
                // decision about an aircraft, airport or flight plan that no longer
                // exists: a stale _takeoffCleared made the aircraft roll on its own,
                // a stale _startupClearanceSent skipped the clearance request
                // altogether, and a stale _lineUpAttempts set counted the retries of
                // the previous flight.
                _startupClearanceRequested = false;
                _startupClearanceSent = false;
                _departureClearanceReceived = false;
                _takeoffCleared = false;
                _atRunwayHoldShort = false;
                _atTaxiStart = false;
                _atGate = false;
                _takeoffTargetSpeed = null;
                _gearRetracted = false;
                _goAround = false;
                _emergency = false;
                _goAroundLogged = false;
                _iceWarningLogged = false;
                _severeTurbulenceLogged = false;
                _finalWeatherLogged = false;
                _turbulence.Reset();
                _localizerDeviation = 0;
                _destinationElevation = 0;

                // Every controller assignment goes with the aircraft that received it. The
                // runway in particular: re-engaging with a different aircraft, at a
                // different airport, would otherwise keep taxiing to the last runway the
                // last controller assigned.
                _atcDepartureRunway = null;
                _atcArrivalRunway = null;
                _atcAltitudeFt = null;
                _atcDescentAltitudeFt = null;
                _atcHeadingDeg = null;
                _atcAirspeedKt = null;
                _atcMach = null;
                _atcBacktrack = false;
                _atcDirectToFix = null;
                _directFollower = null;
                _pendingDirectTo = null;
                _atcWaypointName = null;
                _atcWaypointAltitudeFt = 0;
                _atcHoldShortBefore = null;

                // The route is rebuilt from where the aircraft now is: the leg the last
                // engagement was flying has nothing to do with this one.
                _navRoute = null;
                _navFollower = null;
                _missedFollower = null;
                _steeringHeading = double.NaN;
                _approachCourseMagneticDeg = null;
                RebuildNavRoute(_aircraft.State);
                _atcTaxiChain.Clear();
                _atcCrossableRunways.Clear();
                _atcClearedToLand = false;
                _atcClearedForApproach = false;
                _noClearanceGoArounds = 0;

                // Taxi-out and hold-short state: the route and the hold-short are
                // recomputed from the aircraft's position on the first frame of the
                // engagement.
                _taxiOutStage = TaxiOutStage.Pushback;
                _holdShortNode = null;
                _holdShortStopPoint = null;
                _lineUpTarget = null;
                _lineUpAttempts = 0;
                _lineUpRetryNotBeforeUtc = DateTime.MinValue;
                _wheelBrakePosition = 0;

                // The stand-holding stage starts fresh. A tug attached during the last
                // engagement is not this engagement's pushback, and a dismissal - or the
                // wait for one - carried across an engage would leave the aircraft held on
                // a stand for a timer that expired before the aircraft arrived.
                _pushbackProgress = new PushbackProgress();
                _pushbackRequestedUtc = default;
                _pushbackAttachedUtc = default;
                _pushbackAttachPoint = null;
                _pushbackLastMovedUtc = default;
                _pushbackSawTug = false;
                _pushbackDismissingUtc = default;
                _pushbackToggleFired = false;
                _pushbackCompleteInstruction = false;
                _pushbackApprovedNoted = false;

                // Drop any hold left over from the previous engagement so the first
                // evaluation of this one starts from the aircraft's actual state.
                _phaseManager.Reset();
                _autopilot.ResetPids();
                if (_checklistsLoaded) _checklists.Reset();
                break;
            case PilotControlState.Manual:
                Log("AI control DISENGAGED");
                Status("User has control");
                _autopilot.ReleaseAllControls();
                _autopilot.DisengageMaster();
                break;
            case PilotControlState.Error:
                Log("AI pilot ERROR - control released");
                _autopilot.ReleaseAllControls();
                _autopilot.DisengageMaster();
                break;
        }
    }

    public void ToggleControl()
    {
        if (_controlState == PilotControlState.AiActive)
            SetControlState(PilotControlState.Manual);
        else
            SetControlState(PilotControlState.AiActive);
    }

    public void RequestControlFromUser() => SetControlState(PilotControlState.Manual);
    public void TakeControlFromUser() => SetControlState(PilotControlState.AiActive);

    public AutomationStatusSnapshot AutomationStatus() => new()
    {
        Enabled = _randomizedFlights.Enabled,
        StopAfterThisFlight = _randomizedFlights.StopAfterThisFlight,
        Status = _automationStatus,
        LastPlanSummary = _automationLastPlanSummary,
        LegsCompleted = _automationLegsCompleted
    };

    public string SetRandomizedFlightsEnabled(bool enabled)
    {
        _randomizedFlights.Enabled = enabled;
        if (!enabled)
        {
            _randomizedFlights.StopAfterThisFlight = false;
            _automationPlanningInProgress = false;
            _automationServiceUntilUtc = default;
            _automationStatus = "Off";
            return "Randomized flights disabled.";
        }

        _automationStatus = "Enabled - waiting for shutdown";
        return "Randomized flights enabled.";
    }

    public string RequestStopAfterThisFlight()
    {
        if (!_randomizedFlights.Enabled)
            return "Randomized flights are not enabled.";
        _randomizedFlights.StopAfterThisFlight = true;
        _automationStatus = "Will stop after the current flight";
        return "Automation will stop after this flight.";
    }

    public string ClearStopAfterThisFlight()
    {
        if (!_randomizedFlights.Enabled)
            return "Randomized flights are not enabled.";
        _randomizedFlights.StopAfterThisFlight = false;
        _automationStatus = "Enabled - continuing";
        return "Automation will continue with the next flight.";
    }

    #endregion

    #region Checklists

    /// <summary>Live checklist progress the console, HTTP bridge and panel render.</summary>
    public ChecklistStatus ChecklistStatus() => _checklists.Status();

    /// <summary>Runs one checklist step by hand (console `checklist run &lt;STEP&gt;`).</summary>
    public void RunChecklistStep(ChecklistStep step) => _checklists.RunStep(step);

    /// <summary>Skips the checkpoint currently being executed.</summary>
    public void SkipChecklistItem() => _checklists.SkipCurrent();

    /// <summary>Clears completed steps so the whole flow can run again.</summary>
    public void ResetChecklists()
    {
        _checklists.Reset();
        Log("[Checklist] progress reset");
    }

    /// <summary>Re-reads checklists from disk (after the package is edited/rebuilt).</summary>
    public void ReloadChecklists() => LoadAircraftChecklists(_aircraft.AircraftTitle);

    #endregion

    #region ATC Handling

    public void HandleAtcInstruction(AtcInstruction instruction)
    {
        Log($"[ATC] {instruction.RawText}");
        OnAtcLog?.Invoke(instruction.RawText);

        // Instruction handling is not thread safe: it mutates the phase manager, the
        // clearance flags and the control targets while the control loop is mid-update.
        // Every instruction raised from the loop itself is applied immediately; one raised
        // from the HTTP bridge, the named pipe or the console is queued and applied at the
        // top of the next frame, which is at most one control loop away.
        if (_loopThreadId != 0 && Environment.CurrentManagedThreadId != _loopThreadId)
        {
            lock (_instructionQueue)
            {
                _instructionQueue.Add(instruction);
            }
            return;
        }

        lock (_instructionLock)
        {
            ApplyAtcInstruction(instruction);
        }
    }

    private readonly List<AtcInstruction> _instructionQueue = new();
    private readonly object _instructionLock = new();
    private int _loopThreadId;

    /// <summary>Applies instructions received from a thread other than the control loop.</summary>
    private void DrainAtcInstructions()
    {
        List<AtcInstruction> pending;
        lock (_instructionQueue)
        {
            if (_instructionQueue.Count == 0) return;
            pending = new List<AtcInstruction>(_instructionQueue);
            _instructionQueue.Clear();
        }

        foreach (var instruction in pending)
        {
            try
            {
                ApplyAtcInstruction(instruction);
            }
            catch (Exception ex)
            {
                Log($"[ATC] Instruction could not be applied: {ex.Message}");
            }
        }
    }

    private void ApplyAtcInstruction(AtcInstruction instruction)
    {
        if (_controlState != PilotControlState.AiActive) return;

        if (instruction.Squawk != null)
        {
            // A squawk is a four-digit octal code: set it in the transponder rather than
            // only remembering it, so the controller sees the right code on their scope.
            _atc.SetTransponder(instruction.Squawk);
            Status($"Squawk {instruction.Squawk} set");
        }

        // A back-taxi is the one runway instruction that changes what the aircraft does on
        // the ground, so it is captured before the runway assignment below.
        if (instruction.Backtrack) _atcBacktrack = true;

        // A controller's runway beats the flight plan's, on the ground and in the air.
        if (instruction.Runway is { Length: > 0 } runwayName)
        {
            var onGround = _latestSnapshot.OnGround;
            var isDeparture = onGround ||
                (string.IsNullOrEmpty(_latestSnapshot.Navigation.ArrivalRunway) && _atcArrivalRunway is null);

            if (isDeparture) ApplyAtcDepartureRunway(runwayName);
            else ApplyAtcArrivalRunway(runwayName);
        }

        if (instruction.AltitudeFt.HasValue)
        {
            _atcAltitudeFt = instruction.AltitudeFt.Value;

            // The clearance the pilot is given to descend to is often below the cruise
            // altitude it first assigned, so the two have to be tracked apart: one number
            // could not express "level at 34000, then descend and maintain 12000".
            if (_atcAltitudeFt < _targetCruiseAltitude) _atcDescentAltitudeFt = _atcAltitudeFt;

            Status($"Altitude {instruction.AltitudeFt.Value} ft assigned");
        }

        // Mach and knots are both speed instructions an ATC issues; the speed the aircraft
        // flies is the Mach number converted at the current speed of sound once airborne.
        if (instruction.Mach.HasValue && instruction.Mach.Value is >= 0.3 and <= 1.0)
        {
            _atcMach = instruction.Mach.Value;
            Status($"Mach {_atcMach.Value:F2} assigned");
        }
        else if (instruction.SpeedKnots.HasValue && instruction.SpeedKnots.Value is >= 50 and <= 1000)
        {
            _atcAirspeedKt = instruction.SpeedKnots.Value;
            Status($"Speed {instruction.SpeedKnots.Value} kt assigned");
        }

        // A heading is an assignment: the native autopilot follows it until the controller
        // says otherwise. It is ignored on the ground, where the aircraft has taxi
        // clearances rather than headings.
        if (instruction.HeadingDeg.HasValue && !_latestSnapshot.OnGround)
        {
            _atcHeadingDeg = instruction.HeadingDeg.Value;
            Status($"Heading {_atcHeadingDeg:F0} assigned");
        }

        // "Direct to <fix>" is a vector with an end point rather than a heading: the fix is
        // looked up in the navigation database and the aircraft is steered to it, and only
        // back onto the plan's own path when it gets there or the controller releases it.
        if (!string.IsNullOrEmpty(instruction.DirectToFix) && !_latestSnapshot.OnGround)
        {
            var fixName = instruction.DirectToFix!;
            var resolved = _navData.GetFix(fixName);
            if (resolved?.HasPosition == true)
            {
                _atcDirectToFix = resolved;
                _directFollower = null;
                Status($"Direct to {fixName}");
                Log($"[ATC] Cleared direct to {fixName} " +
                    $"({resolved.Latitude:F5},{resolved.Longitude:F5})");
            }
            else
            {
                // Asked for and not yet answered: the lookup resolves asynchronously, and
                // the direct-to is re-attempted whenever navdata arrives.
                _navData.RequestFix(fixName);
                Log($"[ATC] Cleared direct to {fixName} - looking it up in the navigation database");
                _pendingDirectTo = fixName;
            }
        }

        if (instruction.ClearedForTakeoff)
        {
            _takeoffCleared = true;
            Status("Cleared for takeoff - commencing takeoff roll");
        }

        if (instruction.ClearedToLand)
        {
            _atcClearedToLand = true;
            Status("Cleared to land");
        }

        if (instruction.ClearedForApproach)
        {
            _atcClearedForApproach = true;
            Status("Cleared for the approach");
        }

        if (instruction.ClearedToCross)
        {
            // A crossing clearance is for a runway, and the parser only sets this flag
            // together with a runway name.
            if (instruction.Runway is { Length: > 0 }) _atcCrossableRunways.Add(instruction.Runway);
            PlanTaxiToRunway(_latestSnapshot);
            Status($"Cleared to cross {instruction.Runway ?? "runway"}");
        }

        // "Hold short of runway 25" is a constraint on the taxi route: the route must not
        // cross that strip. It applies while the aircraft is still taxiing out, which is
        // the only time the route is live.
        if (instruction.HoldShort && instruction.Runway is { Length: > 0 } holdRunway && _latestSnapshot.OnGround)
        {
            _atcHoldShortBefore = holdRunway;
            PlanTaxiToRunway(_latestSnapshot);
            Status($"Holding short of {holdRunway}");
        }

        // "Resume own navigation" releases every assignment the controller made, which is
        // what LNAV means: the autopilot follows the flight plan's own path again.
        if (instruction.ResumeOwnNavigation)
        {
            _atcHeadingDeg = null;
            _atcAirspeedKt = null;
            _atcMach = null;
            _atcAltitudeFt = null;
            _atcDescentAltitudeFt = null;
            _atcDirectToFix = null;
            _directFollower = null;
            _pendingDirectTo = null;
            _steeringHeading = double.NaN;
            _autopilot.EngageNavLocks();
            Status("Resume own navigation - flight plan path restored");
        }

        if (instruction.GoAround)
        {
            _goAround = true;
        }

        if (instruction.HoldPosition)
        {
            // Set the parking brake by state, not by toggle. PARKING_BRAKES switches the
            // brake, so firing it while the aircraft is already braked at a gate released
            // the very brake the instruction had just asked for.
            SetWheelBrake(HoldShortBrakePosition);
            SetParkingBrake(true);
        }

        if (instruction.IsHandoff && !string.IsNullOrEmpty(instruction.HandoffFrequency))
        {
            // Auto-tune: the standby side is set in hertz and swapped into the active
            // position, which is the only ordering that keeps 8.33 kHz channels intact.
            // The switch applies here too, not only on the hub's own handoff path: a
            // controller that had just handed the aircraft off used to move the radio even
            // after auto-tune had been turned off.
            if (_atc.Options.AutoTune && Parsing.TryDouble(instruction.HandoffFrequency, out var handoff))
            {
                _atc.TuneCom1(handoff);
                Status($"Handoff: COM1 {instruction.HandoffFrequency}");
            }
        }

        // A pushback is answered by a tug rather than by a clearance variable, so what a
        // controller says about one is recorded here and acted on by the stand-holding
        // stage: an approval is what the pilot is waiting for, and "pushback complete" is
        // the instruction to stop and release the tug.
        if (instruction.PushbackApproved && !_pushbackApprovedNoted)
        {
            _pushbackApprovedNoted = true;
            Log("[Pushback] pushback approved by ATC");
        }
        else if (!instruction.PushbackApproved)
        {
            _pushbackApprovedNoted = false;
        }

        if (instruction.PushbackComplete) _pushbackCompleteInstruction = true;

        if (instruction.LineUpAndWait)
        {
            // The instruction also used to be the only thing that set the hold-short flag,
            // and it is a phrase the simulator's own ATC never emits as a clearance var,
            // so the takeoff request was never made. The hold-short is now position
            // derived; this only forces the taxi phase so a cleared instruction issued
            // while parked still moves the aircraft.
            _atRunwayHoldShort = true;
            _phaseManager.ForcePhase(FlightPhase.TaxiOut);
        }

        // A named taxi route ("via ALPHA one left on BRAVO") is the controller's explicit
        // path, and the graph router follows it instead of its own shortest route.
        if (instruction.TaxiWaypoints.Count > 0 && _latestSnapshot.OnGround)
        {
            ApplyAtcTaxiRoute(instruction);
        }
    }

    /// <summary>
    /// Re-plans the departure taxi route for the runway the controller just assigned.
    /// The route is only live while the aircraft is still taxiing out; past that the
    /// aircraft is rolling and the runway no longer matters.
    /// </summary>
    private void ApplyAtcDepartureRunway(string runway)
    {
        if (string.Equals(_atcDepartureRunway, runway, StringComparison.OrdinalIgnoreCase)) return;

        _atcDepartureRunway = runway;
        Status($"Runway {runway} assigned");

        var state = _latestSnapshot;
        var airport = GetAirport(state.Navigation.DepartureICAO ?? "") ?? _airports.Values.FirstOrDefault(a => a.IsLoaded);
        if (airport?.FindRunway(runway) is { } info)
        {
            _departureRunwayHeading = TrueToMagnetic(info.HeadingDegTrue, airport.MagneticVariationDeg);
            Log($"[ATC] Departure runway {runway} assigned, magnetic heading {_departureRunwayHeading:F0}");
        }

        if (state.OnGround && CurrentPhase <= FlightPhase.TaxiOut && !_takeoffCleared)
        {
            PlanTaxiToRunway(state);
        }

        // The departure runway is also the first fix of the route.
        RebuildNavRoute(state);
    }

    /// <summary>Re-plans the arrival route for the runway the controller just assigned.</summary>
    private void ApplyAtcArrivalRunway(string runway)
    {
        if (string.Equals(_atcArrivalRunway, runway, StringComparison.OrdinalIgnoreCase)) return;

        _atcArrivalRunway = runway;
        Status($"Arrival runway {runway}");

        var state = _latestSnapshot;
        var airport = GetAirport(state.Navigation.ArrivalICAO ?? "");
        if (airport?.FindRunway(runway) is { } info)
        {
            _arrivalRunwayHeading = TrueToMagnetic(info.HeadingDegTrue, airport.MagneticVariationDeg);
            Log($"[ATC] Arrival runway {runway} assigned, magnetic heading {_arrivalRunwayHeading:F0}");
        }

        // The approach at this airport is not necessarily the one the controller just
        // assigned, so the published approaches are asked for and the route rebuilt.
        RequestNavData(state.Navigation.ArrivalICAO);
        RebuildNavRoute(state);
    }

    /// <summary>Resolves a named taxi route into a chain of graph nodes.</summary>
    private void ApplyAtcTaxiRoute(AtcInstruction instruction)
    {
        var state = _latestSnapshot;
        var airport = GetAirport(state.Navigation.DepartureICAO ?? "") ?? _airports.Values.FirstOrDefault(a => a.IsLoaded);
        if (airport is null || airport.Nodes.Count == 0) return;

        // The aircraft's own node is the reference the first name is resolved from: a
        // taxiway name covers many nodes, and without it the chain starts wherever that
        // taxiway happens to begin.
        var here = TaxiRouter.FindNearestNode(airport, state.Latitude, state.Longitude);
        var chain = TaxiRouter.ResolveTaxiwayChain(airport, instruction.TaxiWaypoints, here?.Index);
        if (chain.Count == 0)
        {
            Log($"[Taxi] Controller's route ({string.Join(" ", instruction.TaxiWaypoints)}) " +
                "names no taxiway on the loaded network");
            return;
        }

        _atcTaxiChain.Clear();
        _atcTaxiChain.AddRange(chain);
        PlanTaxiToRunway(state);
    }

    /// <summary>
    /// What the controller last told the aircraft to do, or the aircraft's own plan
    /// defaults when the controller has said nothing. Every phase executor reads its
    /// targets from here, which is what makes an instruction actually change the flight.
    /// </summary>
    private double TargetAltitudeFt(AircraftState s)
    {
        if (_atcAltitudeFt.HasValue) return _atcAltitudeFt.Value;

        // Descending holds the clearance it was given, which may be below where a plain
        // 3:1 descent from cruise would level off: ATC routinely steps the aircraft down in
        // stages, and only the last one given is the one to fly.
        if (_phaseManager.CurrentPhase is FlightPhase.Descent && _atcDescentAltitudeFt.HasValue)
        {
            return Math.Max(_atcDescentAltitudeFt.Value, _destinationElevation + 2000);
        }

        return _targetCruiseAltitude;
    }

    private double TargetAirspeedKt(AircraftState s)
    {
        if (_atcMach.HasValue)
        {
            // Mach is only an en-route speed: below the tropopause, and on the climb or the
            // descent, the aircraft is cleared in knots instead.
            if (s.Altitude > 28000) return MachToIas(s, _atcMach.Value);
            return ClimbSpeedFor(s);
        }

        if (_atcAirspeedKt.HasValue) return _atcAirspeedKt.Value;

        var speed = _perf.CruisePerf.DefaultCruiseSpeed > 0 ? _perf.CruisePerf.DefaultCruiseSpeed : 280;

        // In turbulence the aircraft is slowed toward its turbulence-penetration speed to
        // stay inside the structural envelope, the way a pilot flies a rough-air speed. Only
        // the aircraft's own perf-derived target is touched: an ATC speed assignment is never
        // reduced, and the ground is not a turbulence problem.
        if (!s.OnGround)
        {
            speed = WeatherModel.EnrouteSpeedReductionKt(_turbulence.Level, speed);
        }

        return speed;
    }

    /// <summary>
    /// The enroute speed target, capped into the aircraft's envelope for the configuration
    /// actually flown. Separated from <see cref="TargetAirspeedKt"/> because the cap applies
    /// to the perf-derived target and to an ATC assignment alike - a controller's instruction
    /// is not authority over the airframe - while some callers (the flare) deliberately use
    /// the raw target to compute their own bounded figure.
    /// </summary>
    private double CommandedAirspeedKt(AircraftState s)
        => _envelope.CappedAirspeedKt(TargetAirspeedKt(s), FlapIndex(s), s.GearDown);

    /// <summary>
    /// Indicated airspeed a Mach number works out to at the aircraft's current altitude and
    /// temperature. The density ratio is read back from the aircraft's own IAS and TAS rather
    /// than from an atmosphere model, so the answer follows the atmosphere the simulator is
    /// actually using.
    /// </summary>
    internal static double MachToIas(AircraftState s, double mach)
    {
        var standardCelsius = s.OAT + 273.15;
        if (standardCelsius <= 0) return 0;

        var speedOfSoundKt = 661.47 * Math.Sqrt(standardCelsius / 288.15);
        var trueAirspeedKt = mach * speedOfSoundKt;

        if (s.TrueAirspeed > 50 && s.IndicatedAirspeed > 50)
        {
            var densityRatio = s.IndicatedAirspeed / s.TrueAirspeed;
            trueAirspeedKt *= densityRatio < 0.9 ? densityRatio : 1.0;
        }

        return trueAirspeedKt;
    }

    #region ATC commands

    /// <summary>
    /// Runs the `atc` command, shared by the console, the HTTP bridge and the named
    /// pipe so every client reaches the same behaviour. Sub-commands:
    ///
    ///   atc                      status of both back-ends
    ///   atc native|beyond|auto   force a controller, or auto-detect again
    ///   atc request <kind>       clearance|taxi|takeoff|landing
    ///   atc tune <mhz>           tune COM1
    ///   atc squawk <code>        set the transponder code
    ///   atc menu open|close|<n>  drive the sim ATC menu directly
    ///   atc process <text>       feed an ATC phrase through the parser (test/manual)
    ///   atc actionkey <vk>|auto  BeyondATC "Fire Action 1" key (0 clears it)
    ///   atc autorequest on|off   phase-driven requests
    ///   atc autotune on|off      tune on handoff
    ///   atc cpdlc                probe the BeyondATC CPDLC endpoint
    /// </summary>
    public string RunAtcCommand(string? arg)
    {
        var parts = (arg ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var value = parts.Length > 1 ? parts[1].Trim() : "";

        switch (sub)
        {
            case "":
            case "status":
                return _atc.StatusText();

            case "native":
            case "sim":
                _atc.SetSource(AtcSource.Native);
                return $"ATC source: {AtcCommunicationHub.DescribeSource(_atc.Source)}";

            case "beyond":
            case "beyondatc":
                _atc.SetSource(AtcSource.BeyondAtc);
                return $"ATC source: {AtcCommunicationHub.DescribeSource(_atc.Source)}";

            case "auto":
                _atc.SetSource(AtcSource.None);
                return $"ATC source: {AtcCommunicationHub.DescribeSource(_atc.Source)} (auto-detecting)";

            case "request":
            case "req":
                var kind = AtcRequestKinds.Parse(value);
                if (kind == null)
                    return "Usage: atc request <clearance|taxi|takeoff|landing|pushback>";
                var label = AtcRequestKinds.Label(kind.Value);
                return _atc.Request(kind.Value, "manual")
                    ? $"Requested {label}"
                    : $"{label} not sent - see the ATC log";

            case "tune":
                if (!Parsing.TryDouble(value, out var mhz))
                    return "Usage: atc tune <frequency in MHz>";
                _atc.TuneCom1(mhz);
                return $"COM1 set to {Parsing.Format(mhz, "0.000")}";

            case "squawk":
                if (value.Length != 4)
                    return "Usage: atc squawk <4-digit code>";
                _atc.SetTransponder(value);
                return $"Squawk {value} set";

            case "menu":
                if (value.Equals("open", StringComparison.OrdinalIgnoreCase)) { _atc.Menu.Open(); return "ATC menu opened"; }
                if (value.Equals("close", StringComparison.OrdinalIgnoreCase)) { _atc.Menu.Close(); return "ATC menu closed"; }
                if (value.Equals("toggle", StringComparison.OrdinalIgnoreCase)) { _atc.Menu.ToggleWindow(); return "ATC window toggled"; }
                if (Parsing.TryInt32(value, out var entry)) { _atc.Menu.Select(entry); return $"ATC menu entry {entry} selected"; }
                return "Usage: atc menu open|close|toggle|<0-9>";

            case "ocr":
                return RunAtcOcrCommand(value);

            case "process":
                if (string.IsNullOrWhiteSpace(value))
                    return "Usage: atc process <instruction text>";
                var parsed = _atc.ProcessText(value);
                return $"Handled: {parsed.RawText}";

            case "actionkey":
            case "action":
                if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    _atc.Options.ActionKeyVirtualKey = 0;
                    return "BeyondATC action key follows its own binding again";
                }
                if (!Parsing.TryInt32(value, out var vk))
                    return "Usage: atc actionkey <virtual key code>|auto";
                _atc.Options.ActionKeyVirtualKey = vk;
                return $"BeyondATC action key set to VK {vk}";

            case "autorequest":
                _atc.Options.AutoRequest = IsOn(value, _atc.Options.AutoRequest);
                return $"Automatic ATC requests {(_atc.Options.AutoRequest ? "on" : "off")}";

            case "autotune":
                _atc.Options.AutoTune = IsOn(value, _atc.Options.AutoTune);
                return $"Automatic radio tuning on handoff {(_atc.Options.AutoTune ? "on" : "off")}";

            case "cpdlc":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _atc.Cpdlc.ProbeAsync();
                        Log($"[AtcLink] CPDLC probe:{Environment.NewLine}{_atc.Cpdlc.LastResponse}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[AtcLink] CPDLC probe failed: {ex.Message}");
                    }
                });
                return "Probing the BeyondATC CPDLC endpoint (see the log)";

            default:
                return "Usage: atc [status|native|beyond|auto|request <clearance|taxi|takeoff|landing|pushback>|" +
                       "tune <mhz>|squawk <code>|menu open|close|<n>|ocr show|on|off|process <text>|actionkey <vk>|" +
                       "autorequest on|off|autotune on|off|cpdlc]";
        }
    }
    /// <summary>
    /// The off-screen menu reader is the backup to probing: `show` prints what it can see
    /// on the ATC window right now, which is also how a capture region is verified by hand.
    /// </summary>
    private string RunAtcOcrCommand(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "on":
                _atc.ReadMenuOffScreen = true;
                return "ATC menu reader on: the menu is read before entries are pressed";

            case "off":
                _atc.ReadMenuOffScreen = false;
                return "ATC menu reader off: entries are found by probing";

            case "show":
            case "read":
                var region = _atc.Options.MenuCaptureRegion;
                var entries = _atc.ReadMenuNow();
                if (entries.Count == 0)
                    return "No menu lines were found. Is the ATC window open, and does the " +
                           "capture region cover it? (see atc status)";

                var lines = entries.Select(e =>
                    $"  {e.Line} (ATC_MENU_{AtcRequestEngine.MenuEventIndex(e.Line)}): " +
                    $"{(e.Text is null ? "unrecognised" : e.Text)} {e.Score:P0}");
                return $"ATC menu as read at {_atc.LastMenuReadUtc:HH:mm:ss} " +
                       $"(capture: {(region is null ? "simulator window" : region.ToString())}):" +
                       Environment.NewLine + string.Join(Environment.NewLine, lines);

            default:
                return "Usage: atc ocr show|on|off";
        }
    }

    private static bool IsOn(string value, bool current) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => !current
    };

    public string RunAutomationCommand(string? arg)
    {
        var parts = (arg ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        var value = parts.Length > 1 ? parts[1].Trim() : "";

        return sub switch
        {
            "status" or "" => DescribeAutomationStatus(),
            "on" or "enable" => SetRandomizedFlightsEnabled(true),
            "off" or "disable" => SetRandomizedFlightsEnabled(false),
            "toggle" => SetRandomizedFlightsEnabled(!_randomizedFlights.Enabled),
            "stop-after" => RequestStopAfterThisFlight(),
            "continue" => ClearStopAfterThisFlight(),
            "service" when Parsing.TryInt32(value, out var minutes) =>
                SetServiceMinutes(minutes),
            _ => "Usage: automation [status|on|off|toggle|stop-after|continue|service <minutes>]"
        };
    }

    private string SetServiceMinutes(int minutes)
    {
        _randomizedFlights.ServiceMinutesBetweenLegs = Math.Clamp(minutes, 1, 120);
        return $"Turnaround service set to {_randomizedFlights.ServiceMinutesBetweenLegs} minutes.";
    }

    private string DescribeAutomationStatus()
    {
        var mode = _randomizedFlights.Enabled ? "enabled" : "off";
        var stop = _randomizedFlights.StopAfterThisFlight ? "yes" : "no";
        var last = string.IsNullOrWhiteSpace(_automationLastPlanSummary) ? "-" : _automationLastPlanSummary;
        return $"Randomized flights: {mode}, stop-after-this-flight: {stop}, service: {_randomizedFlights.ServiceMinutesBetweenLegs} min, " +
               $"legs completed: {_automationLegsCompleted}, status: {_automationStatus}, last leg: {last}";
    }

    public AtcStatusSnapshot AtcStatus() => _atc.Snapshot(_latestSnapshot);

    /// <summary>
    /// What the navigation database and the route built out of it currently hold, for the
    /// console and the panel. This is the part of the flight that is easiest to get
    /// silently wrong: a route that names fixes the database does not carry looks like a
    /// route until the aircraft has nowhere to fly to.
    /// </summary>
    public NavStatusSnapshot NavStatus()
    {
        var route = _navRoute;
        var guidance = _lastGuidance;

        var active = route?.Legs.FirstOrDefault(l => l.HasPosition);

        // Whether the simulator has answered for the arrival airport's procedures at all is
        // the difference between an airport that publishes no approaches and an airport the
        // navigation database has not been asked about yet.
        var arrivalIcao = _latestSnapshot.Navigation.ArrivalICAO;
        return new NavStatusSnapshot
        {
            RouteLegCount = route?.Legs.Count ?? 0,
            RouteGeometryLegs = route?.Legs.Count(l => l.HasPosition) ?? 0,
            FirstLeg = active?.Ident ?? "",
            LastLeg = route?.Legs.LastOrDefault(l => l.HasPosition)?.Ident ?? "",
            UnresolvedFixes = route?.UnresolvedFixes.ToList() ?? new List<string>(),
            HasApproach = route?.HasApproach == true,
            ApproachText = route?.Approach?.Describe(),
            ApproachType = route?.Approach?.Kind.ToString() ?? "",
            ApproachRunway = route?.Approach?.Runway ?? "",
            MissedApproachLegs = route?.MissedLegs.Count(l => l.HasPosition) ?? 0,
            MissedApproachText = MissedApproachDescription,
            ActiveLegIndex = guidance?.ActiveIndex ?? -1,
            ActiveLeg = guidance?.ActiveLeg?.Ident ?? "",
            ActiveLegDistanceMeters = guidance?.DistanceToTerminatorMeters ?? 0,
            CrossTrackMeters = guidance?.CrossTrackMeters ?? 0,
            DirectToFix = _atcDirectToFix?.Icao ?? _pendingDirectTo,
            RebuildCount = _navRouteRebuilds,
            RouteText = route is null ? "" : route.Describe(),
            ArrivalApproachesLoaded = _navData.HasApproaches(arrivalIcao ?? ""),
            ArrivalApproachCount = _navData.GetApproaches(arrivalIcao ?? "").Count(a => a.HasFinalApproach)
        };
    }

    #endregion


    public string GetAtcLog() => _atcLog.ToString();

    public void LoadFlightPlan(string plnPath)
    {
        try
        {
            _aircraft.SimConnect.FlightPlanLoad(plnPath);
            var nav = FlightPlanParser.ParsePLN(plnPath);
            _aircraft.State.Navigation = nav;

            // Total distance: prefer the geographic total across waypoints that carry
            // positions, and fall back to the plan's airport coordinates. This is the plan's
            // own length, which is what a default cruise altitude is chosen from - how far
            // is left to fly is computed each frame from the aircraft, because the plan
            // does not shrink as the aircraft moves.
            var positioned = nav.Waypoints.Where(w => w.HasPosition).ToList();
            var waypointDistance = positioned.Sum(w => w.DistanceFromPrevious);
            if (waypointDistance > 0)
            {
                _planDistanceNm = waypointDistance;
            }
            else if (nav.DepartureLatitude != 0 && nav.DestinationLatitude != 0)
            {
                _planDistanceNm = FlightPlanParser.CalculateDistance(
                    nav.DepartureLatitude, nav.DepartureLongitude,
                    nav.DestinationLatitude, nav.DestinationLongitude);
            }
            else
            {
                _planDistanceNm = 0;
            }

            // Destination elevation: the plan's arrival coordinates carry an altitude,
            // otherwise zero (sea level) is the conservative assumption.
            _destinationElevation = nav.DestinationAltitude;

            // Cruise altitude: prefer the value filed in the plan, then the highest
            // waypoint altitude, then a distance-based default.
            if (nav.CruiseAltitudeFt > 0)
            {
                _targetCruiseAltitude = nav.CruiseAltitudeFt;
            }
            else
            {
                var planCruise = nav.Waypoints
                    .Where(w => w.Altitude > 1000)
                    .Select(w => w.Altitude)
                    .DefaultIfEmpty(0)
                    .Max();
                if (planCruise > 0)
                {
                    _targetCruiseAltitude = planCruise;
                }
                else
                {
                    _targetCruiseAltitude = _planDistanceNm switch
                    {
                        < 150 => 10000,
                        < 400 => 18000,
                        < 800 => 26000,
                        < 1500 => 32000,
                        _ => 37000
                    };
                }
            }

            // Departure runway heading: a runway number in the plan implies its
            // magnetic heading directly (runway 09 points at 090 degrees). The real
            // runway heading from the facilities API overrides this once it arrives.
            var depRunwayDigits = (nav.DepartureRunway ?? "").TrimStart('0').TrimEnd('L', 'R', 'C');
            if (Parsing.TryInt32(depRunwayDigits, out var depRwyNum) && depRwyNum is > 0 and <= 36)
            {
                _departureRunwayHeading = depRwyNum * 10;
            }
            else if (nav.Waypoints.Count >= 2 && nav.Waypoints[0].HasPosition && nav.Waypoints[1].HasPosition)
            {
                _departureRunwayHeading = Bearing(
                    nav.Waypoints[0].Latitude, nav.Waypoints[0].Longitude,
                    nav.Waypoints[1].Latitude, nav.Waypoints[1].Longitude);
            }

            Status($"Flight plan loaded: {nav.DepartureICAO} -> {nav.ArrivalICAO}, {_planDistanceNm:F0} NM, CRZ {_targetCruiseAltitude:F0} ft");
            Log($"Flight plan loaded from {plnPath}: {nav.Waypoints.Count} waypoints, cruise {_targetCruiseAltitude:F0} ft, dep rwy hdg {_departureRunwayHeading:F0}");

            // Request ground network data for both ends of the flight.
            if (!string.IsNullOrEmpty(nav.DepartureICAO)) RequestAirportData(nav.DepartureICAO);
            if (!string.IsNullOrEmpty(nav.ArrivalICAO) &&
                !string.Equals(nav.ArrivalICAO, nav.DepartureICAO, StringComparison.OrdinalIgnoreCase))
            {
                RequestAirportData(nav.ArrivalICAO);
            }

            // The plan is a list of names in MSFS 2024: the geometry for the route and for
            // the approach comes from the navigation database, so that is requested for both
            // ends of the flight too.
            RequestNavData(nav.DepartureICAO);
            RequestNavData(nav.ArrivalICAO);

            // Build the route now, even though most fixes will still be unresolved: the
            // fix lookups are issued here, and the route is rebuilt as they resolve.
            RebuildNavRoute(_aircraft.State);
        }
        catch (Exception ex)
        {
            Log($"Failed to load flight plan: {ex.Message}");
        }
    }

    public string? FindLatestFlightPlan()
    {
        try
        {
            if (_simulatorPaths != null)
            {
                return MsfsInstallLayout.Discover(_simulatorPaths).FindLatestFlightPlan();
            }
            return MsfsInstallLayout.Discover().FindLatestFlightPlan();
        }
        catch { }
        return null;
    }

    #endregion

    #region Delegation IPC (named pipe from WASM toolbar)

    private void StartDelegationServer()
    {
        _pipeServerTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

                    var line = await reader.ReadLineAsync();
                    var response = ProcessPipeCommand(line);
                    await writer.WriteLineAsync(response);

                    server.Disconnect();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"Pipe error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    private string ProcessPipeCommand(string? cmd)
    {
        try
        {
            var doc = JsonDocument.Parse(cmd ?? "{}");
            var root = doc.RootElement;
            var command = root.TryGetProperty("cmd", out var c) ? c.GetString() : "";
            var argument = root.TryGetProperty("arg", out var a) ? a.GetString() : "";

            return command switch
            {
                "get-status" => PanelJson.Serialize(new
                {
                    state = _controlState.ToString(),
                    phase = _phaseManager.CurrentPhase.ToString(),
                    connected = _aircraft.SimConnect.IsConnected
                }),
                "toggle" => HandleToggle(),
                "engage" => HandleEngage(),
                "disengage" => HandleDisengage(),
                "atc" => PanelJson.Serialize(new { ok = true, text = RunAtcCommand(argument) }),
                _ => PanelJson.Serialize(new { error = "unknown command" })
            };
        }
        catch (Exception ex)
        {
            return PanelJson.Serialize(new { error = ex.Message });
        }
    }

    private string HandleToggle()
    {
        ToggleControl();
        return PanelJson.Serialize(new { state = _controlState.ToString() });
    }

    private string HandleEngage()
    {
        SetControlState(PilotControlState.AiActive);
        return PanelJson.Serialize(new { state = _controlState.ToString() });
    }

    private string HandleDisengage()
    {
        SetControlState(PilotControlState.Manual);
        return PanelJson.Serialize(new { state = _controlState.ToString() });
    }

    public async Task<string> SendPipeCommandAsync(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);

            await writer.WriteLineAsync(command);
            client.WaitForPipeDrain();
            return await reader.ReadLineAsync() ?? "";
        }
        catch (Exception ex)
        {
            return PanelJson.Serialize(new { error = ex.Message });
        }
    }

    #endregion

    #region Utility

    public void Status(string msg) => OnStatus?.Invoke(msg);

    public void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { }
    }

    private static double NormalizeDeg(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    /// <summary>Initial great-circle bearing in degrees from one point to another.</summary>
    private static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        const double R2D = 180.0 / Math.PI;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var y = Math.Sin(dLon) * Math.Cos(lat2 * Math.PI / 180.0);
        var x = Math.Cos(lat1 * Math.PI / 180.0) * Math.Sin(lat2 * Math.PI / 180.0) -
                Math.Sin(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Cos(dLon);
        var bearing = Math.Atan2(y, x) * R2D;
        return bearing < 0 ? bearing + 360.0 : bearing;
    }

    #endregion

    public void Dispose()
    {
        Stop();
        _aircraft.Dispose();
        _facilities.Dispose();
        _checklistHost.Dispose();
        _httpBridge.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
