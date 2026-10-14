using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>Runtime behaviour of the ATC communication bridge.</summary>
public sealed class AtcOptions
{
    /// <summary>None means "auto-detect": BeyondATC when it is running, otherwise the sim ATC.</summary>
    public AtcSource PreferredSource { get; set; } = AtcSource.None;
    /// <summary>Issue phase-driven requests (clearance, taxi, takeoff, landing).</summary>
    public bool AutoRequest { get; set; } = true;
    /// <summary>Tune COM1 to the station the sim ATC hands the aircraft to.</summary>
    public bool AutoTune { get; set; } = true;
    /// <summary>Virtual key code of BeyondATC's "Fire Action 1" binding; 0 when unbound.</summary>
    public int ActionKeyVirtualKey { get; set; }
    /// <summary>Index into BeyondATC's hotkey list that holds "Fire Action 1" (0-4).</summary>
    public int ActionKeySlot { get; set; } = 3;
    /// <summary>Give the BeyondATC window focus while sending a key (off by default).</summary>
    public bool ActivateBeyondAtcWindow { get; set; }
    /// <summary>Minimum spacing between automatic requests, so the ATC menu is not hammered.</summary>
    public TimeSpan RequestCooldown { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Override for the BeyondATC log directory (defaults to its LocalLow folder).</summary>
    public string? LogDirectoryOverride { get; set; }
    /// <summary>
    /// Read the ATC menu off the screen so the right entry is pressed the first time
    /// rather than probed for. Off by default: it costs a screen capture per request and
    /// needs the simulator window to be visible, so it is a backup and not the default
    /// path. When it is off, or when it recognises nothing, probing runs as usual.
    /// </summary>
    public bool ReadMenuOffScreen { get; set; }
    /// <summary>
    /// Region of the desktop that holds the ATC menu; null lets the bridge find the
    /// simulator window. Worth setting when the simulator runs borderless or on a
    /// secondary monitor.
    /// </summary>
    public AtcCaptureRegion? MenuCaptureRegion { get; set; }
    /// <summary>Extra ATC menu phrases to recognise, beyond the known ones.</summary>
    public IReadOnlyList<string>? ExtraMenuPhrases { get; set; }
}

/// <summary>Point-in-time view of the ATC integration, for the console and the panel.</summary>
public sealed class AtcStatusSnapshot
{
    public AtcSource Source { get; init; }
    public AtcSource Preferred { get; init; }
    public bool BeyondAtcRunning { get; init; }
    public bool BeyondAtcLogFound { get; init; }
    public string? BeyondAtcLogPath { get; init; }
    public string? BeyondAtcCallsign { get; init; }
    public string? BeyondAtcAirframe { get; init; }
    public string? BeyondAtcDepartureRunway { get; init; }
    public IReadOnlyList<int> BeyondAtcHotkeys { get; init; } = Array.Empty<int>();
    public int ActionKeyVirtualKey { get; init; }
    public IReadOnlyList<string> CpdlcEndpoints { get; init; } = Array.Empty<string>();
    public AtcRequestStatus RequestStatus { get; init; }
    public string? ActiveRequest { get; init; }
    public int ProbedMenuEntry { get; init; }
    public int PressedMenuEntries { get; init; }
    public int MappedMenuCircumstances { get; init; }
    public DateTime LastRequestUtc { get; init; }
    public string? LastFailure { get; init; }
    public string? LastInstruction { get; init; }
    public DateTime LastInstructionUtc { get; init; }
    public string? Station { get; init; }
    public string? StationType { get; init; }
    public string? StationFrequency { get; init; }
    public string? NextStation { get; init; }
    public double NextStationFrequency { get; init; }
    public bool ClearedIfr { get; init; }
    public bool ClearedTaxi { get; init; }
    public bool ClearedTakeoff { get; init; }
    public bool ClearedLanding { get; init; }
    public bool IfrClearanceAvailable { get; init; }
    public string? DepartureRunway { get; init; }
    public string? ArrivalRunway { get; init; }
    public int AssignedAltitudeFt { get; init; }
    public string? ArrivalGate { get; init; }
    public bool AutoRequest { get; init; }
    public bool AutoTune { get; init; }
    public int InstructionsFromSim { get; init; }
}

/// <summary>
/// The pilot's single point of contact with an air traffic controller, over either
/// back-end:
///
/// * the simulator's own ATC, whose clearances arrive as SimVars and whose menu is
///   driven with the ATC_MENU_* key events, and
/// * BeyondATC, whose window is driven through its own key bindings and whose session
///   facts (assigned runway, callsign, CPDLC endpoint) arrive in its Player.log.
///
/// Instructions from either source are normalised into <see cref="AtcInstruction"/> and
/// raised on <see cref="OnInstructionParsed"/>, so the flight logic in
/// <see cref="AIPilot"/> never needs to know which controller is talking.
/// </summary>
public sealed class AtcCommunicationHub : IDisposable
{
    private readonly AircraftController _aircraft;
    private readonly Action<string> _log;
    private readonly AtcOptions _options;
    private readonly SimConnectAtcMenu _menu;
    private readonly AtcRequestEngine _requests;
    private readonly NativeAtcMonitor _native = new() { Source = AtcSource.Native };
    private readonly BeyondAtcWindowBridge _window = new();
    private readonly BeyondAtcCpdlcClient _cpdlc = new();
    private readonly BeyondAtcLogTail? _tail;

    private DateTime _lastSourceCheckUtc = DateTime.MinValue;
    private DateTime _lastAutoRequestUtc = DateTime.MinValue;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private FlightPhase _lastPhase = FlightPhase.Unknown;
    private string? _stationSignature;
    private double _lastHandoffFrequency;
    private string? _batcCallsign;
    private string? _batcAirframe;
    private string? _batcRunway;
    private IReadOnlyList<int> _batcHotkeys = Array.Empty<int>();
    private int _instructionsFromSim;
    private string? _lastInstruction;
    private DateTime _lastInstructionUtc;
    private bool _running;
    private bool _readMenuOffScreen;
    private IReadOnlyList<AtcMenuEntry> _lastMenuRead = Array.Empty<AtcMenuEntry>();
    private DateTime _lastMenuReadUtc = DateTime.MinValue;
    private Dictionary<string, AtcMenuReader> _menuReaders = new(StringComparer.Ordinal);

    public AtcCommunicationHub(AircraftController aircraft, AtcOptions? options = null, Action<string>? log = null)
    {
        _aircraft = aircraft;
        _options = options ?? new AtcOptions();
        _log = log ?? (_ => { });
        _menu = new SimConnectAtcMenu(aircraft.SimConnect);
        _requests = new AtcRequestEngine(_menu, null, msg => _log(msg));
        _readMenuOffScreen = _options.ReadMenuOffScreen;

        var logDirectory = _options.LogDirectoryOverride ?? BeyondAtcLogParser.DefaultDirectory;
        var logPath = Path.Combine(logDirectory, "Player.log");
        try
        {
            _tail = new BeyondAtcLogTail(logPath, OnBeyondAtcLogEvent, err => _log($"[AtcLink] {err}"));
        }
        catch (Exception ex)
        {
            _log($"[AtcLink] BeyondATC log tail unavailable: {ex.Message}");
        }
    }

    public AtcOptions Options => _options;
    public AtcRequestEngine Requests => _requests;
    public IAtcMenu Menu => _menu;
    public BeyondAtcWindowBridge Window => _window;
    public BeyondAtcCpdlcClient Cpdlc => _cpdlc;
    public string? LogPath => _tail?.Path;
    public AtcSource Source { get; private set; } = AtcSource.None;
    public bool IsRunning => _running;

    /// <summary>Set by the flight logic when the aircraft is holding short of the runway.</summary>
    public bool AtRunwayHoldShort { get; set; }

    /// <summary>
    /// Set by the flight logic when the aircraft is standing on a parking space that offers
    /// a pushback, prepared for one and waiting to be moved off the stand. The hub asks the
    /// controller for the tug while this is set; how the aircraft is prepared is the flight
    /// logic's business, exactly as the hold-short flag is.
    /// </summary>
    public bool PushbackWanted { get; set; }

    public event Action<AtcInstruction>? OnInstructionParsed;
    public event Action<string>? OnRawAtcText;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _log($"[AtcLink] started; watching {(_tail?.Path ?? "(no log)")}");
    }

    public void Stop()
    {
        _running = false;
        _requests.Cancel();
    }

    /// <summary>
    /// Advances both back-ends. Called once per control loop with the frame snapshot, so
    /// no value is read from a half-updated state.
    /// </summary>
    public void Tick(AircraftState snapshot)
    {
        if (!_running) return;

        ResolveSource();

        // The menu probe keeps advancing whichever controller is active. A request in
        // flight was made through the sim's ATC menu, so it is a native request even if
        // BeyondATC started running while it was being pressed - and leaving it unticked
        // left the engine permanently busy, which blocked every later request for the
        // session.
        _requests.Tick();

        if (Source == AtcSource.Native)
        {
            if (IsNativeStateFresh(snapshot)) TickNative(snapshot);
        }
        else if (Source == AtcSource.BeyondAtc)
        {
            TickBeyondAtc(snapshot);
        }

        // The request engine advances here, inside the same gate a request from another
        // thread takes to start one: see _requestGate.
        lock (_requestGate)
        {
            _requests.Tick();
        }

        TrackStation(snapshot);
    }

    // ---------------------------------------------------------------- sim ATC

    private void TickNative(AircraftState snapshot)
    {
        // The sim's ATC also hands off to the next station; tuning is the only part of
        // that the radio does not do on its own when auto-tune is switched off.
        if (Options.AutoTune)
        {
            var handoff = snapshot.ATC.NextControllerFrequencyMHz;
            if (handoff > 1 && Math.Abs(handoff - _lastHandoffFrequency) > 0.0005)
            {
                _lastHandoffFrequency = handoff;
                _menu.TuneCom1(handoff);
                _log($"[AtcLink] handoff: tuning COM1 to {handoff.ToString("0.000", CultureInfo.InvariantCulture)} " +
                     $"({snapshot.ATC.NextControllerName ?? "next station"})");
            }
            if (handoff <= 1) _lastHandoffFrequency = 0;
        }

        foreach (var instruction in _native.Update(snapshot))
        {
            _instructionsFromSim++;
            _lastInstruction = instruction.RawText;
            _lastInstructionUtc = instruction.Timestamp;
            Emit(instruction);
        }
    }

    /// <summary>
    /// True when the ATC SimVars are being refreshed. When the simulator sits in a menu
    /// the block keeps its last values, and acting on those would restart an old flight.
    /// </summary>
    private static bool IsNativeStateFresh(AircraftState snapshot) =>
        snapshot.ATC.NativeStateUtc != default &&
        DateTime.UtcNow - snapshot.ATC.NativeStateUtc < TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------- BeyondATC

    private void TickBeyondAtc(AircraftState snapshot)
    {
        // The log is polled on a timer rather than every frame: it changes a few times a
        // second at most and a stat plus a short read per second is free.
        if (DateTime.UtcNow - _lastPollUtc >= TimeSpan.FromMilliseconds(500))
        {
            _lastPollUtc = DateTime.UtcNow;
            _tail?.Poll();
        }
    }

    private void OnBeyondAtcLogEvent(BeyondAtcLogEvent e)
    {
        if (BeyondAtcLogParser.ParseHotkeys(e) is { } hotkeys)
        {
            _batcHotkeys = hotkeys;
            _log($"[AtcLink] BeyondATC hotkeys: [{string.Join(", ", hotkeys)}]");
            ApplyHotkeyConfiguration(hotkeys);
            return;
        }

        // The CPDLC server tells you both when it has started and when it stops, so the
        // two are switched on by different lines. They used to be nested inside a test for
        // "found an endpoint", which only ever matched the start line, so the stop was
        // silently unreachable.
        if (e.Text.Contains("Server stopped", StringComparison.OrdinalIgnoreCase))
        {
            _cpdlc.ClearEndpoints();
            _log("[AtcLink] BeyondATC CPDLC server stopped");
            return;
        }

        if (BeyondAtcLogParser.ParseCpdlcEndpoints(e) is { Count: > 0 } endpoints)
        {
            _cpdlc.SetEndpoints(endpoints);
            _log($"[AtcLink] BeyondATC CPDLC endpoint: {string.Join(", ", endpoints)}");
            return;
        }

        if (BeyondAtcLogParser.ParseAircraftIdentity(e) is { } identity)
        {
            _batcCallsign = identity.Ident;
            _batcAirframe = identity.Airframe;
            _log($"[AtcLink] BeyondATC tracking {identity.Ident} ({identity.Airframe}, {identity.IcaoType})");
            return;
        }

        if (BeyondAtcLogParser.ParseRunwayAllocation(e) is { } allocation)
        {
            // The log line names the aircraft the runway was allocated to; only the
            // player's own allocation matters here.
            if (_batcCallsign != null && !string.Equals(allocation.Callsign, _batcCallsign, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(allocation.Runway, _batcRunway, StringComparison.OrdinalIgnoreCase)) return;
            _batcRunway = allocation.Runway;

            Emit(new AtcInstruction
            {
                Source = AtcSource.BeyondAtc,
                Station = "GND",
                Timestamp = e.SeenUtc,
                RawText = $"[BeyondATC] Runway {allocation.Runway} assigned for departure",
                Runway = allocation.Runway
            });
            return;
        }

        if (BeyondAtcLogParser.ParseUnknownFrequency(e) is { } mhz)
        {
            _log($"[AtcLink] BeyondATC: {mhz.ToString("0.000", CultureInfo.InvariantCulture)} not a known station " +
                 "(COM1 is off the current station)");
            return;
        }

        if (BeyondAtcLogParser.IsSessionBoundary(e))
        {
            _log($"[AtcLink] BeyondATC session: {e.Text}");
        }
    }

    /// <summary>
    /// Takes the "Fire Action 1" virtual key from BeyondATC's own preference list so a
    /// request goes out on the key the user actually configured.
    /// </summary>
    private void ApplyHotkeyConfiguration(IReadOnlyList<int> hotkeys)
    {
        if (Options.ActionKeyVirtualKey != 0) return; // explicit configuration wins
        if (_options.ActionKeySlot < 0 || _options.ActionKeySlot >= hotkeys.Count) return;

        var vk = hotkeys[_options.ActionKeySlot];
        if (vk != 0) _log($"[AtcLink] BeyondATC action key from slot {_options.ActionKeySlot}: VK {vk}");
    }

    /// <summary>The action key to send, or 0 when none is configured.</summary>
    private int EffectiveActionKey
    {
        get
        {
            if (Options.ActionKeyVirtualKey != 0) return Options.ActionKeyVirtualKey;
            var slot = Options.ActionKeySlot;
            return slot >= 0 && slot < _batcHotkeys.Count ? _batcHotkeys[slot] : 0;
        }
    }

    // ---------------------------------------------------------------- requests

    /// <summary>
    /// Makes a request to whichever controller is active. For the sim ATC this walks the
    /// ATC menu and verifies the result against the ATC SimVars; for BeyondATC it sends
    /// the configured action-button key once.
    /// </summary>
    public bool Request(AtcRequestKind kind, string? reason = null,
        FlightPhase phase = FlightPhase.Unknown)
    {
        // Resolve the controller on demand: a request issued from the console may
        // arrive between two control loop frames.
        ResolveSource();

        if (!_running)
        {
            _log("[AtcLink] ATC bridge is not running - connect to the simulator first");
            return false;
        }

        if (phase != FlightPhase.Unknown) _lastPhase = phase;

        var label = AtcRequestKinds.Label(kind);

        if (Source == AtcSource.BeyondAtc)
        {
            return FireBeyondAtcAction(label, reason);
        }

        if (Source != AtcSource.Native) return false;

        var snapshot = _aircraft.GetSnapshot();
        if (!IsRequestable(kind, snapshot))
        {
            _log($"[AtcLink] {label} not available in the current state");
            return false;
        }

        var effectivePhase = phase != FlightPhase.Unknown ? phase : _lastPhase;
        var context = RequestContext(kind, snapshot, effectivePhase);
        var description = reason == null ? label : $"{label} ({reason})";

        // The engine's poll callbacks read this snapshot rather than the live state: the
        // controller is decoding into AircraftState under its own lock from a different
        // thread, so reading _aircraft.State inside a callback could mix fields from two
        // frames. A torn signature then made the engine blame a menu entry for a change
        // the simulation itself had made, and forbid that entry for the circumstance.
        lock (_requestGate)
        {
            return _requests.Begin(kind, () => Accepted(kind), description, context,
                () => StateSignature(snapshot.ATC),
                () => !string.Equals(RequestContext(kind, snapshot, effectivePhase), context,
                    StringComparison.Ordinal),
                () => MenuLinesFor(kind));
        }
    }

    /// <summary>
    /// Serialises access to the request engine. <see cref="Request"/> is reachable from the
    /// HTTP bridge, the named pipe and the console, so it lands on other threads while the
    /// 20 Hz control loop is inside the engine's tick - which mutates the same collections
    /// the request touches, and left a HashSet being Added to during a Contains.
    /// </summary>
    private readonly object _requestGate = new();

    // -------------------------------------------------- reading the menu off screen

    /// <summary>True when the ATC menu is read off the screen before entries are pressed.</summary>
    public bool ReadMenuOffScreen
    {
        get => _readMenuOffScreen;
        set => _readMenuOffScreen = value;
    }

    /// <summary>The last menu the bridge read off the screen.</summary>
    public IReadOnlyList<AtcMenuEntry> LastMenuRead => _lastMenuRead;

    /// <summary>When the menu off the screen was last read.</summary>
    public DateTime LastMenuReadUtc => _lastMenuReadUtc;

    /// <summary>
    /// Reads the ATC menu that is on screen right now, with every known phrase offered,
    /// so a human can check what the reader sees.
    /// </summary>
    public IReadOnlyList<AtcMenuEntry> ReadMenuNow()
    {
        try
        {
            var reader = MenuReaderFor(AtcRequestKind.IFRClearance, AllMenuPhrases());
            _lastMenuRead = reader.ReadMenu();
            _lastMenuReadUtc = DateTime.UtcNow;
            return _lastMenuRead;
        }
        catch (Exception ex)
        {
            _log($"[AtcLink] ATC menu could not be read: {ex.Message}");
            return Array.Empty<AtcMenuEntry>();
        }
    }

    /// <summary>
    /// Menu lines that name the request, read off the screen, or an empty list when the
    /// reader is disabled, has nothing to show or recognises nothing. The engine treats an
    /// empty answer as "probe as usual", so a bad capture costs time and never a clearance.
    /// </summary>
    private IReadOnlyList<int> MenuLinesFor(AtcRequestKind kind)
    {
        if (!_readMenuOffScreen) return Array.Empty<int>();

        try
        {
            var reader = MenuReaderFor(kind, AtcMenuPhrases.For(kind));
            var lines = reader.LinesForRequest();
            _lastMenuRead = reader.LastMenuRead;
            _lastMenuReadUtc = DateTime.UtcNow;

            _log(lines.Count > 0
                ? $"[AtcLink] ATC menu read: line {(lines.Count > 1 ? "s " : "")}" +
                  $"{string.Join(", ", lines)} ask for this, so they are pressed first"
                : "[AtcLink] ATC menu read: nothing recognised, probing the menu as usual");

            return lines;
        }
        catch (Exception ex)
        {
            _log($"[AtcLink] ATC menu could not be read: {ex.Message}");
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Readers are kept per circumstance so the rendered phrases are reused: a line
    /// number is part of the phrase, so a cold reader would re-render every candidate for
    /// every line it sees.
    /// </summary>
    private AtcMenuReader MenuReaderFor(AtcRequestKind kind, IReadOnlyList<string> phrases)
    {
        var key = string.Join('/', phrases);
        if (!_menuReaders.TryGetValue(key, out var reader))
            _menuReaders[key] = reader = new AtcMenuReader(new GdiGlyphRenderer(), kind, CreateReaderOptions(phrases));

        return reader;
    }

    private AtcMenuReaderOptions CreateReaderOptions(IReadOnlyList<string> phrases) => new()
    {
        CaptureRegion = _options.MenuCaptureRegion,
        ExtraPhrases = _options.ExtraMenuPhrases,
        PhrasesOverride = phrases
    };

    /// <summary>Every known ATC menu phrase, for reading a menu without a request in mind.</summary>
    private static IReadOnlyList<string> AllMenuPhrases() =>
        new[]
        {
            AtcRequestKind.IFRClearance, AtcRequestKind.Taxi, AtcRequestKind.Takeoff,
            AtcRequestKind.Landing, AtcRequestKind.Pushback
        }
        .SelectMany(AtcMenuPhrases.For)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Names the circumstance a request is made in. The sim ATC menu is contextual, so
    /// the same entry means different things at different points of the flight; the
    /// signature is what keeps a remembered (or forbidden) menu line from being applied
    /// to a situation it was not learned in.
    /// </summary>
    private static string RequestContext(AtcRequestKind kind, AircraftState state, FlightPhase phase) => string.Join('|',
        AtcRequestKinds.Label(kind),
        phase,
        state.ATC.StationType ?? "-",
        state.ATC.AircraftIdent ?? "-",
        state.ATC.ClearedIfr ? "ifr" : "-",
        state.ATC.ClearedTaxi ? "taxi" : "-",
        state.ATC.ClearedTakeoff ? "tko" : "-",
        state.ATC.ClearedLanding ? "ldg" : "-",
        state.ATC.DesignatedRunwayTakeoff ?? state.ATC.DesignatedRunwayLanding ?? "-",
        state.ATC.OnParkingSpot ? "spot" : "ramp",
        // Last, so the fields the context reader indexes by position keep their meaning:
        // whether a tug is on offer is part of the circumstance a pushback is asked in.
        state.Pushback.Available ? "tug" : "-");

    /// <summary>
    /// Signature of the ATC state that a menu entry can change: the clearances it can
    /// grant and what it assigns. Deliberately excludes live distances and the tuned
    /// station, which move on their own, so only a genuine ATC action counts. Because the
    /// menu cannot be read, a change here that does not grant the request is the only
    /// available proof that the pressed entry did something else.
    /// </summary>
    private static string StateSignature(ATCState atc) => string.Join(';',
        atc.ClearedIfr ? 'I' : '-',
        atc.ClearedTaxi ? 'T' : '-',
        atc.ClearedTakeoff ? 'O' : '-',
        atc.ClearedLanding ? 'L' : '-',
        atc.AssignedAltitudeFt.ToString(CultureInfo.InvariantCulture),
        atc.DesignatedRunwayTakeoff ?? "-",
        atc.DesignatedRunwayLanding ?? "-",
        atc.DesignatedGateArrival ?? "-",
        atc.DesignatedGateDeparture ?? "-");

    private bool Accepted(AtcRequestKind kind)
    {
        var atc = _aircraft.State.ATC;
        return kind switch
        {
            AtcRequestKind.IFRClearance => atc.ClearedIfr,
            AtcRequestKind.Taxi => atc.ClearedTaxi,
            AtcRequestKind.Takeoff => atc.ClearedTakeoff,
            AtcRequestKind.Landing => atc.ClearedLanding,
            // A pushback has no clearance of its own: the approved manoeuvre is observed
            // as the tug attaching to the aircraft.
            AtcRequestKind.Pushback => _aircraft.State.Pushback.Attached,
            _ => false
        };
    }

    private bool FireBeyondAtcAction(string label, string? reason)
    {
        var key = EffectiveActionKey;
        // The cooldown is recorded on the failure path too. The control loop asks for a taxi
        // request every frame while it is allowed to, and without this a missing "Fire
        // Action 1" binding produced one identical log line per frame for the whole phase.
        _lastAutoRequestUtc = DateTime.UtcNow;
        if (key == 0)
        {
            _log($"[AtcLink] cannot request {label}: BeyondATC has no action key bound. " +
                 "Bind \"Fire Action 1\" in BeyondATC's options, or set one with `atc actionkey <VK>`.");
            return false;
        }

        var sent = _window.SendKey(key, Options.ActivateBeyondAtcWindow);
        _log(sent
            ? $"[AtcLink] BeyondATC: fired action key VK {key} for {label}{(reason == null ? "" : $" ({reason})")}"
            : $"[AtcLink] BeyondATC: action key not delivered ({_window.LastError ?? "unknown"})");
        return sent;
    }

    /// <summary>True when the sim ATC has an outstanding request of this kind.</summary>
    private static bool IsRequestable(AtcRequestKind kind, AircraftState state) => kind switch
    {
        AtcRequestKind.IFRClearance => state.ATC.IFRFlightPlanAvailable && !state.ATC.ClearedIfr,
        AtcRequestKind.Taxi => !state.ATC.ClearedTaxi,
        AtcRequestKind.Takeoff => !state.ATC.ClearedTakeoff,
        AtcRequestKind.Landing => !state.ATC.ClearedLanding,
        // A tug can only be asked for standing on a parking space that offers one, and
        // only while none is attached: the request is a question, and the answer arrives
        // as the tug itself.
        AtcRequestKind.Pushback => state.ATC.OnParkingSpot && state.Pushback.Available && !state.Pushback.Attached,
        _ => false
    };

    /// <summary>
    /// Phase-driven requests: what a pilot asks for at each point of the flight. Called
    /// by the flight logic once per loop with the phase it just executed.
    /// </summary>
    public void UpdatePhaseRequests(FlightPhase phase, AircraftState snapshot)
    {
        if (!_running || !Options.AutoRequest) return;

        _lastPhase = phase;
        var atc = snapshot.ATC;

        // Recorded before the cooldown: the point at which a pilot asks is the point at
        // which they need an answer, not the point at which the request can next be sent.
        // Cleared again once the aircraft is off the approach, so a second approach asks
        // afresh rather than living with the answer to the first.
        if (phase is FlightPhase.Approach or FlightPhase.FinalApproach)
        {
            if (atc.ArrivalAirportIsTowered) LandingClearanceRequested = true;
        }
        else
        {
            LandingClearanceRequested = false;
        }

        // Same for the pushback: the question is recorded when it is asked and dropped
        // once the tug is on the aircraft or the aircraft has left the space. Availability
        // is part of "asked", because a stand that offers no pushback leaves nothing to
        // have asked for.
        if (PushbackWanted && atc.OnParkingSpot && snapshot.Pushback.Available &&
            !snapshot.Pushback.Attached)
        {
            PushbackRequested = true;
        }

        if (snapshot.Pushback.Attached || !atc.OnParkingSpot) PushbackRequested = false;

        if (DateTime.UtcNow - _lastAutoRequestUtc < Options.RequestCooldown) return;
        if (_requests.IsBusy) return;

        switch (phase)
        {
            // The pushback is asked for standing on the parking space, before anything
            // rolls, so it comes ahead of the taxi request: the tug is what moves the
            // aircraft off the stand, and asking to taxi while it is still on the nose
            // is asking for something the aircraft cannot yet do. The flight logic
            // decides when the aircraft is ready for one - gate checklist done, departure
            // clearance dealt with - and the tug being on offer is the simulator's own
            // answer that there is anything to ask for at all.
            case FlightPhase.Startup or FlightPhase.TaxiOut when PushbackWanted:
                Try(AtcRequestKind.Pushback, "at the gate");
                break;

            // A tower has to be talking for the ATC menu to carry these at all; at an
            // uncontrolled field there is nothing to request and probing the menu would
            // only open and close the ATC window.
            case FlightPhase.TaxiOut when atc.AirportIsTowered && !atc.ClearedTaxi:
                Try(AtcRequestKind.Taxi, "ready to taxi");
                break;

            case FlightPhase.TaxiOut when atc.AirportIsTowered && AtRunwayHoldShort && !atc.ClearedTakeoff:
                Try(AtcRequestKind.Takeoff, "holding short");
                break;

            case FlightPhase.Approach or FlightPhase.FinalApproach
                when atc.ArrivalAirportIsTowered && !atc.ClearedLanding:
                Try(AtcRequestKind.Landing, "established on approach");
                break;
        }

        void Try(AtcRequestKind kind, string reason)
        {
            if (Request(kind, reason, phase)) _lastAutoRequestUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Feeds ATC text into the parser by hand (tests, and the `atc process` command).</summary>
    public AtcInstruction ProcessText(string text, string? station = null)
    {
        var instruction = BeyondAtcBridge.ParseAtcText(text, station ?? "MANUAL");
        instruction.Source = Source == AtcSource.None ? AtcSource.Native : Source;
        _lastInstruction = instruction.RawText;
        _lastInstructionUtc = instruction.Timestamp;
        Emit(instruction);
        return instruction;
    }

    /// <summary>Tunes COM1 and returns the frequency that was set.</summary>
    public double TuneCom1(double frequencyMHz)
    {
        _menu.TuneCom1(frequencyMHz);
        return frequencyMHz;
    }

    public void SetTransponder(string code) => _menu.SetTransponder(code);

    /// <summary>Selects a source explicitly.</summary>
    public void SetSource(AtcSource source)
    {
        _options.PreferredSource = source;
        ResolveSource(force: true);
    }

    public IReadOnlyList<int> BeyondAtcHotkeys => _batcHotkeys;
    public string? BeyondAtcCallsign => _batcCallsign;
    public string? BeyondAtcAirframe => _batcAirframe;
    public string? BeyondAtcDepartureRunway => _batcRunway;
    public int InstructionsFromSim => _instructionsFromSim;
    public string? LastInstruction => _lastInstruction;
    public DateTime LastInstructionUtc => _lastInstructionUtc;

    /// <summary>
    /// Whether this approach has asked ATC for a landing clearance. The pilot needs to know
    /// this at the same point in the flight it asks at, rather than when the request finally
    /// clears its cooldown: without clearance, a landing is not permitted and the aircraft
    /// has to go around rather than touch down unannounced.
    /// </summary>
    public bool LandingClearanceRequested { get; private set; }

    /// <summary>
    /// Whether this stand has asked ATC for a pushback and none has been dismissed yet. The
    /// pilot needs to know at the same point it asks at rather than when the request clears
    /// its cooldown: the aircraft holds on the stand until a tug arrives or the wait runs
    /// out, and the panel and the console both report which of the two is happening.
    /// </summary>
    public bool PushbackRequested { get; private set; }

    // ---------------------------------------------------------------- plumbing

    private void ResolveSource(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastSourceCheckUtc < TimeSpan.FromSeconds(2)) return;
        _lastSourceCheckUtc = DateTime.UtcNow;

        var resolved = _options.PreferredSource != AtcSource.None
            ? _options.PreferredSource
            : (_window.Refresh() || LogLooksRecent() ? AtcSource.BeyondAtc : AtcSource.Native);

        if (resolved == Source) return;

        Source = resolved;
        _native.Reset();
        _stationSignature = null;
        _log($"[AtcLink] controller source: {DescribeSource(resolved)}");
    }

    private bool LogLooksRecent()
    {
        if (_tail == null) return false;
        try
        {
            if (!File.Exists(_tail.Path)) return false;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(_tail.Path) < TimeSpan.FromMinutes(15);
        }
        catch
        {
            return false;
        }
    }

    private void TrackStation(AircraftState snapshot)
    {
        var atc = snapshot.ATC;
        var signature = $"{atc.CurrentFrequency}|{atc.StationType}|{atc.StationIdent}";
        if (signature == _stationSignature) return;
        var initial = _stationSignature == null;
        _stationSignature = signature;

        if (initial || string.IsNullOrEmpty(atc.CurrentFrequency)) return;
        var station = atc.StationType ?? "STATION";
        RaiseText($"[{DescribeSource(Source)}] COM1 {atc.CurrentFrequency} {station} " +
                  $"{(string.IsNullOrEmpty(atc.StationIdent) ? "" : atc.StationIdent)}".TrimEnd());
    }

    private void Emit(AtcInstruction instruction)
    {
        _log($"[ATC:{instruction.Source}] {instruction.RawText}");
        OnInstructionParsed?.Invoke(instruction);
        OnRawAtcText?.Invoke(instruction.RawText);
    }

    private void RaiseText(string text)
    {
        _log($"[ATC] {text}");
        OnRawAtcText?.Invoke(text);
    }

    public static string DescribeSource(AtcSource source) => source switch
    {
        AtcSource.Native => "sim ATC",
        AtcSource.BeyondAtc => "BeyondATC",
        _ => "none"
    };

    public AtcStatusSnapshot Snapshot(AircraftState? state = null)
    {
        // Keeps the source honest even when the status is read between control frames
        // (the panel polls this several times a second).
        ResolveSource();

        var snapshotState = state ?? _aircraft.State;
        var atc = snapshotState.ATC;
        return new AtcStatusSnapshot
        {
            Source = Source,
            Preferred = _options.PreferredSource,
            BeyondAtcRunning = _window.IsRunning,
            BeyondAtcLogFound = _tail != null && File.Exists(_tail.Path),
            BeyondAtcLogPath = _tail?.Path,
            BeyondAtcCallsign = _batcCallsign,
            BeyondAtcAirframe = _batcAirframe,
            BeyondAtcDepartureRunway = _batcRunway,
            BeyondAtcHotkeys = _batcHotkeys,
            ActionKeyVirtualKey = EffectiveActionKey,
            CpdlcEndpoints = _cpdlc.EffectiveEndpoints(),
            RequestStatus = _requests.Status,
            ActiveRequest = _requests.ActiveKind.HasValue ? AtcRequestKinds.Label(_requests.ActiveKind.Value) : null,
            ProbedMenuEntry = _requests.ProbedEntry,
            PressedMenuEntries = _requests.PressedEntryCount,
            MappedMenuCircumstances = _requests.MappedCircumstances.Count,
            LastRequestUtc = _requests.LastRequestUtc,
            LastFailure = _requests.LastFailure,
            LastInstruction = _lastInstruction,
            LastInstructionUtc = _lastInstructionUtc,
            Station = atc.StationIdent,
            StationType = atc.StationType,
            StationFrequency = atc.CurrentFrequency,
            NextStation = atc.NextControllerName,
            NextStationFrequency = atc.NextControllerFrequencyMHz,
            ClearedIfr = atc.ClearedIfr,
            ClearedTaxi = atc.ClearedTaxi,
            ClearedTakeoff = atc.ClearedTakeoff,
            ClearedLanding = atc.ClearedLanding,
            IfrClearanceAvailable = atc.IFRFlightPlanAvailable,
            DepartureRunway = atc.DesignatedRunwayTakeoff ?? _batcRunway,
            ArrivalRunway = atc.DesignatedRunwayLanding,
            AssignedAltitudeFt = atc.AssignedAltitudeFt,
            ArrivalGate = atc.DesignatedGateArrival,
            AutoRequest = _options.AutoRequest,
            AutoTune = _options.AutoTune,
            InstructionsFromSim = _instructionsFromSim
        };
    }

    /// <summary>
    /// One line per circumstance whose ATC menu has been mapped: the line that works and
    /// the lines that are known to do the wrong thing. This is the observable result of
    /// probing a menu that cannot be read, and it doubles as the table that can be
    /// verified by hand with the `atc menu <n>` command.
    /// </summary>
    private IEnumerable<string> MenuKnowledgeLines()
    {
        foreach (var context in _requests.MappedCircumstances
                     .Concat(_requests.CircumstancesWithKnownBadLines).Distinct(StringComparer.Ordinal))
        {
            var learned = _requests.LearnedEntry(context);
            var forbidden = _requests.ForbiddenEntries(context).OrderBy(l => l).ToList();
            var parts = new List<string>();
            if (learned.HasValue) parts.Add($"line {learned.Value} works");
            if (forbidden.Count > 0)
                parts.Add($"line{(forbidden.Count > 1 ? "s" : "")} {string.Join(", ", forbidden)} never grant it");

            yield return $"{DescribeContext(context)}: {string.Join("; ", parts)}";
        }
    }

    private static string DescribeContext(string context)
    {
        var parts = context.Split('|');
        if (parts.Length < 10) return context;

        var held = parts[4..9].Where(p => p != "-").ToList();
        return $"{parts[0]} @ {parts[1]}/{parts[2]}{(parts[3] == "-" ? "" : " " + parts[3])}" +
               (held.Count > 0 ? $" [{string.Join(",", held)}]" : "") +
               $" rwy {parts[8]} {parts[9]}";
    }

    public string StatusText(AircraftState? state = null)
    {
        var s = Snapshot(state);
        var lines = new List<string>
        {
            $"ATC source: {DescribeSource(s.Source)} (preferred: {(s.Preferred == AtcSource.None ? "auto" : DescribeSource(s.Preferred))})",
            $"  station: COM1 {s.StationFrequency ?? "-"} {s.StationType ?? ""} {s.Station ?? ""}".TrimEnd(),
            $"  next station: {(string.IsNullOrEmpty(s.NextStation) ? "-" : s.NextStation)}" +
            (s.NextStationFrequency > 1 ? $" on {s.NextStationFrequency.ToString("0.000", CultureInfo.InvariantCulture)}" : ""),
            $"  clearances: IFR={Bool(s.ClearedIfr)} taxi={Bool(s.ClearedTaxi)} takeoff={Bool(s.ClearedTakeoff)} landing={Bool(s.ClearedLanding)}",
            $"  runways: departure={s.DepartureRunway ?? "-"} arrival={s.ArrivalRunway ?? "-"} gate={s.ArrivalGate ?? "-"}",
            $"  assigned altitude: {(s.AssignedAltitudeFt > 0 ? s.AssignedAltitudeFt + " ft" : "-")}",
            $"  auto: request={Bool(s.AutoRequest)} tune={Bool(s.AutoTune)}",
            $"  last request: {s.ActiveRequest ?? "none"} ({s.RequestStatus}, " +
            $"{s.PressedMenuEntries} entry/entries pressed, line {s.ProbedMenuEntry})",
            $"  menu knowledge: {s.MappedMenuCircumstances} circumstance(s) mapped",
            $"  menu reader: {(ReadMenuOffScreen ? "on" : "off")} (screen off by default)",
            $"  instructions from sim ATC: {s.InstructionsFromSim}"
        };

        foreach (var line in MenuKnowledgeLines()) lines.Add($"  {line}");

        if (s.LastInstruction != null)
            lines.Add($"  last instruction: {s.LastInstruction}");

        lines.Add($"  BeyondATC: running={Bool(s.BeyondAtcRunning)} log={Bool(s.BeyondAtcLogFound)}");
        if (s.BeyondAtcCallsign != null)
            lines.Add($"    callsign={s.BeyondAtcCallsign} airframe={s.BeyondAtcAirframe} rwy={s.BeyondAtcDepartureRunway ?? "-"}");
        if (s.BeyondAtcHotkeys.Count > 0)
            lines.Add($"    hotkeys: [{string.Join(", ", s.BeyondAtcHotkeys)}]");
        lines.Add($"    action key: {(s.ActionKeyVirtualKey != 0 ? "VK " + s.ActionKeyVirtualKey : "unbound")}");
        if (s.CpdlcEndpoints.Count > 0)
            lines.Add($"    cpdlc: {string.Join(", ", s.CpdlcEndpoints)}" +
                      (_cpdlc.UsingDefaultPorts ? " (documented port, not seen in the log yet)" : ""));
        if (s.LastFailure != null)
            lines.Add($"    last failure: {s.LastFailure}");

        return string.Join(Environment.NewLine, lines);

        static string Bool(bool value) => value ? "yes" : "no";
    }

    public void Dispose()
    {
        _tail?.Dispose();
        _window.Dispose();
        _cpdlc.Dispose();
    }
}
