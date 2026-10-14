using System;
using System.Collections.Generic;
using System.Globalization;

namespace MSFS2024AIPilot.Core;

/// <summary>Where ATC instructions and requests are routed.</summary>
public enum AtcSource
{
    None,
    /// <summary>The simulator's own ATC (default).</summary>
    Native,
    /// <summary>BeyondATC.</summary>
    BeyondAtc
}

/// <summary>
/// Requests the AI pilot can make. Both ATC back-ends are addressed through these:
/// the native ATC via its menu key events, BeyondATC via its action-button hotkey.
/// <para>
/// There is deliberately no readback. The simulator's own ATC publishes no event for one and
/// cannot be told a pilot has acknowledged anything, so a "readback" request only opened the
/// ATC menu and pressed a line that never matched what it wanted.
/// </para>
/// </summary>
public enum AtcRequestKind
{
    /// <summary>Request the IFR/departure clearance at the gate.</summary>
    IFRClearance,
    /// <summary>Request taxi instructions to the departure runway.</summary>
    Taxi,
    /// <summary>Request line-up and takeoff clearance at the hold-short.</summary>
    Takeoff,
    /// <summary>Request the approach/landing clearance on arrival.</summary>
    Landing,
    /// <summary>
    /// Request a pushback at the gate. There is no clearance SimVar for one, so the request
    /// is confirmed by the tug attaching rather than by a flag the controller sets.
    /// </summary>
    Pushback
}

public static class AtcRequestKinds
{
    public static string Label(AtcRequestKind kind) => kind switch
    {
        AtcRequestKind.IFRClearance => "IFR clearance",
        AtcRequestKind.Taxi => "taxi",
        AtcRequestKind.Takeoff => "takeoff",
        AtcRequestKind.Landing => "landing",
        AtcRequestKind.Pushback => "pushback",
        _ => kind.ToString()
    };

    /// <summary>Parses a console argument; null when the text is not a known request.</summary>
    public static AtcRequestKind? Parse(string text) => text?.Trim().ToLowerInvariant() switch
    {
        "ifr" or "clearance" or "ifrclearance" => AtcRequestKind.IFRClearance,
        "taxi" => AtcRequestKind.Taxi,
        "takeoff" => AtcRequestKind.Takeoff,
        "landing" or "approach" => AtcRequestKind.Landing,
        "pushback" or "tug" => AtcRequestKind.Pushback,
        _ => null
    };
}

/// <summary>The numbered ATC menu of the simulator's own ATC.</summary>
public interface IAtcMenu
{
    void Open();
    void Close();
    void Select(int item);
    void ToggleWindow();
    void TuneCom1(double frequencyMHz);
    void SetTransponder(string code);
}

/// <summary>
/// Drives the default MSFS 2024 ATC. The ATC window is a menu whose entries are only
/// addressable by position, so the request engine presses one entry at a time and
/// confirms with the ATC SimVars that the request was granted (see
/// <see cref="AtcRequestEngine"/>).
/// </summary>
public sealed class SimConnectAtcMenu : IAtcMenu
{
    private readonly SimConnectManager _sc;

    public SimConnectAtcMenu(SimConnectManager simConnect) => _sc = simConnect;

    public void Open() => _sc.FireEvent("ATC_MENU_OPEN");
    public void Close() => _sc.FireEvent("ATC_MENU_CLOSE");
    public void ToggleWindow() => _sc.FireEvent("SIMUI_WINDOW_HIDESHOW");

    public void Select(int item)
    {
        if (item is < 0 or > 9) return;
        _sc.FireEvent($"ATC_MENU_{item}");
    }

    /// <summary>
    /// Tunes COM1 to a frequency in MHz. The standby side is set in hertz and swapped
    /// into the active position, which is the only ordering that keeps 8.33 kHz
    /// channels intact.
    /// </summary>
    public void TuneCom1(double frequencyMHz)
    {
        if (!IsComFrequency(frequencyMHz)) return;
        // The set-in-hertz events are COM_STBY_RADIO_SET_HZ (COM1), COM2_STBY_RADIO_SET_HZ
        // and COM3_STBY_RADIO_SET_HZ: there is no indexed COM1 form, and
        // COM1_STBY_RADIO_SET_HZ is not an event the sim recognises, so firing it
        // raises NAME_UNRECOGNIZED and the frequency is never set. The swap event
        // does have an indexed form (COM1_RADIO_SWAP).
        _sc.FireEvent("COM_STBY_RADIO_SET_HZ", (uint)Math.Round(frequencyMHz * 1_000_000, MidpointRounding.AwayFromZero));
        _sc.FireEvent("COM1_RADIO_SWAP");
    }

    /// <summary>
    /// True for a frequency in the civil airband COM1 range, 118.000-136.975 MHz. The
    /// upper bound used to be 140, so a frequency the COM radio does not carry was
    /// accepted and the tune event was fired for it anyway.
    /// </summary>
    internal static bool IsComFrequency(double frequencyMHz) => frequencyMHz >= 118 && frequencyMHz <= 136.975;

    /// <summary>
    /// Sets the transponder code. The CODE event takes the value in binary-coded
    /// decimal, so 4321 travels as 0x4321. Digits are validated as octal because a
    /// transponder code never contains 8 or 9.
    /// </summary>
    public void SetTransponder(string code)
    {
        var bcd = TransponderBcd(code);
        if (bcd.HasValue) _sc.FireEvent("XPNDR_SET", (uint)bcd.Value);
    }

    /// <summary>BCD-encodes a four-digit octal transponder code; null when it is not one.</summary>
    internal static int? TransponderBcd(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 4) return null;

        int bcd = 0;
        foreach (var c in code)
        {
            if (c < '0' || c > '7') return null;
            bcd = (bcd << 4) | (c - '0');
        }
        return bcd;
    }
}

/// <summary>Outcome of a request the engine made against the native ATC.</summary>
public enum AtcRequestStatus
{
    Idle,
    /// <summary>The ATC menu has been opened and is settling.</summary>
    OpeningMenu,
    /// <summary>
    /// The menu is open and the ATC SimVars are being watched before any entry is
    /// pressed: the controller grants some clearances on its own.
    /// </summary>
    WaitingForGrant,
    /// <summary>Walking the menu entries waiting for the request to be granted.</summary>
    Probing,
    Accepted,
    /// <summary>No menu entry satisfied the request.</summary>
    Failed,
    /// <summary>The request was abandoned - neither granted nor failed.</summary>
    Cancelled,
    /// <summary>The request cannot be made in the current state (e.g. no clearance needed).</summary>
    Unavailable
}

public sealed class AtcRequestOptions
{
    /// <summary>Delay after opening the menu before the ATC SimVars are watched.</summary>
    public TimeSpan MenuSettle { get; init; } = TimeSpan.FromMilliseconds(400);
    /// <summary>
    /// How long the controller is given to grant the request on its own, with nothing
    /// pressed. MSFS frequently issues a clearance once the aircraft is in position, and
    /// a request that needs no key press cannot fire an unintended one.
    /// </summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long one menu entry is given to take effect.</summary>
    public TimeSpan EntryTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>Hard ceiling for a whole request, including every entry that was tried.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>
    /// Highest menu line that is tried, in display order (1-10). The menu is only
    /// addressable by position, and the tenth line is selected by <c>ATC_MENU_0</c>:
    /// <c>ATC_MENU_1</c> is the first line.
    /// </summary>
    public int MaxEntry { get; init; } = 10;
    /// <summary>
    /// Ceiling on how many entries a single request may press, so an unrecognised menu
    /// state cannot trigger a long chain of unrelated ATC actions.
    /// </summary>
    public int MaxEntriesProbed { get; init; } = 5;
}

/// <summary>
/// Presses ATC menu entries until the simulator reports the requested clearance.
///
/// The menu is only addressable by position and cannot be read, so the engine works by
/// elimination rather than by selection:
///
/// * the ATC SimVars are watched for a grace period first, because MSFS grants some
///   clearances on its own and an unpressed menu cannot fire an unintended action;
/// * entries are tried in display order (<c>ATC_MENU_1</c> = line 1, <c>ATC_MENU_0</c> =
///   line 10) and only until the requested clearance appears;
/// * an entry that changes the ATC state without granting the request is remembered as
///   wrong for that circumstance and is never pressed again while it is current;
/// * a line that worked before is tried first the next time.
///
/// The engine is a small state machine driven by <see cref="Tick"/>, so it never blocks
/// the control loop.
/// </summary>
public sealed class AtcRequestEngine
{
    private readonly IAtcMenu _menu;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;
    private readonly AtcRequestOptions _options;
    private readonly Dictionary<string, HashSet<int>> _forbidden = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _learned = new(StringComparer.Ordinal);

    private PendingRequest? _pending;
    private DateTime _startedUtc;
    private DateTime _entryStartedUtc;
    private bool _menuOpen;
    private string _signatureBaseline = "";
    private bool _entryBlamed;
    private readonly HashSet<int> _pressedLines = new();
    private IReadOnlyList<int>? _menuLines;

    public AtcRequestStatus Status { get; private set; } = AtcRequestStatus.Idle;
    public AtcRequestKind? ActiveKind { get; private set; }

    /// <summary>
    /// Menu line the engine is on or just pressed (1-10), or -1 when nothing has been
    /// pressed yet. Line 10 is fired as <c>ATC_MENU_0</c>.
    /// </summary>
    public int ProbedEntry { get; private set; } = -1;

    /// <summary>Entries pressed for the request in flight; the blast-radius counter.</summary>
    public int PressedEntryCount { get; private set; }

    public DateTime LastRequestUtc { get; private set; }
    public string? LastFailure { get; private set; }

    public AtcRequestEngine(IAtcMenu menu, Func<DateTime>? clock = null, Action<string>? log = null,
        AtcRequestOptions? options = null)
    {
        _menu = menu;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
        _options = options ?? new AtcRequestOptions();
    }

    /// <summary>True while a request is in flight.</summary>
    public bool IsBusy => _pending != null;

    /// <summary>
    /// Menu lines that are known not to grant the request in this circumstance, in the
    /// order they were found out. Keyed by the circumstance supplied to
    /// <see cref="Begin"/>.
    /// </summary>
    public IReadOnlyCollection<int> ForbiddenEntries(string context) =>
        !string.IsNullOrEmpty(context) && _forbidden.TryGetValue(context, out var set) ? set : Array.Empty<int>();

    /// <summary>The menu line that last granted this request in this circumstance.</summary>
    public int? LearnedEntry(string context) =>
        !string.IsNullOrEmpty(context) && _learned.TryGetValue(context, out var line) ? line : null;

    /// <summary>Circumstances the engine has mapped to a working menu line.</summary>
    public IReadOnlyList<string> MappedCircumstances => _learned.Keys.ToList();

    /// <summary>Circumstances where at least one menu line is known to do the wrong thing.</summary>
    public IReadOnlyList<string> CircumstancesWithKnownBadLines => _forbidden.Keys.ToList();

    /// <summary>Drops what was learned - everything, or just one circumstance.</summary>
    public void Forget(string? context = null)
    {
        if (string.IsNullOrEmpty(context))
        {
            _forbidden.Clear();
            _learned.Clear();
        }
        else
        {
            _forbidden.Remove(context);
            _learned.Remove(context);
        }
    }

    /// <summary>The key event that selects a menu line: <c>ATC_MENU_{line}</c>, except line 10.</summary>
    internal static int MenuEventIndex(int line) => line >= 10 ? 0 : line;

    private sealed class PendingRequest
    {
        public required AtcRequestKind Kind { get; init; }
        public required Func<bool> Accepted { get; init; }
        public required string Description { get; init; }
        /// <summary>Identifies the circumstance, so learned lines are not applied elsewhere.</summary>
        public string Context { get; init; } = "";
        /// <summary>Signature of the whole ATC block, used to detect an unintended effect.</summary>
        public Func<string>? StateSignature { get; init; }
        /// <summary>True once the circumstance no longer matches, which aborts the probe.</summary>
        public Func<bool>? CircumstanceChanged { get; init; }
        /// <summary>Menu lines a screen reader recognised as the question being asked.</summary>
        public Func<IReadOnlyList<int>>? MenuLines { get; init; }
    }

    /// <summary>
    /// Starts a request. <paramref name="accepted"/> is polled against the ATC SimVars
    /// and decides when the request was granted. <paramref name="context"/> names the
    /// circumstance so the engine can remember what worked and what did not;
    /// <paramref name="stateSignature"/> returns a signature of the whole ATC block, and
    /// a change that does not grant the request is treated as an entry that did the wrong
    /// thing; <paramref name="circumstanceChanged"/> aborts the probe when the situation
    /// the menu was opened for is no longer current; <paramref name="menuLines"/> supplies
    /// the lines an off-screen reader recognised as the question being asked, which are
    /// tried before anything is pressed blind.
    /// </summary>
    public bool Begin(AtcRequestKind kind, Func<bool> accepted, string description,
        string? context = null, Func<string>? stateSignature = null, Func<bool>? circumstanceChanged = null,
        Func<IReadOnlyList<int>>? menuLines = null)
    {
        if (_pending != null) return false;

        _pending = new PendingRequest
        {
            Kind = kind,
            Accepted = accepted,
            Description = description,
            Context = context ?? "",
            StateSignature = stateSignature,
            CircumstanceChanged = circumstanceChanged,
            MenuLines = menuLines
        };
        _startedUtc = _clock();
        _entryStartedUtc = _startedUtc;
        _signatureBaseline = _pending.StateSignature?.Invoke() ?? "";
        _entryBlamed = false;
        _menuLines = null;
        _pressedLines.Clear();
        PressedEntryCount = 0;
        ProbedEntry = -1;
        LastFailure = null;
        Status = AtcRequestStatus.OpeningMenu;
        ActiveKind = kind;

        _menu.Open();
        _menuOpen = true;
        _log?.Invoke($"[ATC] requesting {AtcRequestKinds.Label(kind)} from the sim ATC menu");
        return true;
    }

    public void Tick()
    {
        if (_pending == null) return;

        var now = _clock();

        if (now - _startedUtc > _options.TotalTimeout)
        {
            Fail($"no ATC menu entry granted {_pending.Description}");
            return;
        }

        // Polled before anything else: the controller grants some clearances by itself,
        // and those need no key press at all.
        if (_pending.Accepted())
        {
            Grant(now);
            return;
        }

        switch (Status)
        {
            case AtcRequestStatus.OpeningMenu:
                if (now - _startedUtc >= _options.MenuSettle)
                {
                    _entryStartedUtc = now;
                    Status = AtcRequestStatus.WaitingForGrant;
                }
                break;

            case AtcRequestStatus.WaitingForGrant:
                if (now - _entryStartedUtc >= _options.GracePeriod)
                    Advance(now);
                break;

            case AtcRequestStatus.Probing:
                Probe(now);
                break;
        }
    }

    private void Probe(DateTime now)
    {
        if (UnintendedEffect())
            return;

        if (_pending!.CircumstanceChanged != null && _pending.CircumstanceChanged())
        {
            Fail($"circumstance changed while requesting {_pending.Description}");
            return;
        }

        if (now - _entryStartedUtc >= _options.EntryTimeout)
            Advance(now);
    }

    /// <summary>
    /// True when the entry that was just pressed changed the ATC state without granting
    /// the request. The menu cannot be read, so that is the only available proof that the
    /// entry did something else: it is remembered as wrong for this circumstance and the
    /// probe stops, because pressing further entries into a menu whose contents are not
    /// what was assumed is exactly the chain of unrelated ATC actions to avoid.
    /// </summary>
    private bool UnintendedEffect()
    {
        var pending = _pending!;
        if (_entryBlamed || ProbedEntry < 1 || pending.StateSignature == null) return false;

        var current = pending.StateSignature();
        if (string.Equals(current, _signatureBaseline, StringComparison.Ordinal)) return false;

        var line = ProbedEntry;
        _entryBlamed = true;
        var remembered = !string.IsNullOrEmpty(pending.Context);
        if (remembered)
        {
            if (!_forbidden.TryGetValue(pending.Context, out var set))
                _forbidden[pending.Context] = set = new HashSet<int>();
            set.Add(line);
        }

        _log?.Invoke($"[ATC] menu line {line} changed the ATC state without granting {pending.Description}" +
                     (remembered ? " - not trying it again for this circumstance" : ""));
        Fail($"{pending.Description} not granted: menu line {line} changed the ATC state instead");
        return true;
    }

    /// <summary>Presses the next candidate line, or fails when there is none left.</summary>
    private void Advance(DateTime now)
    {
        var next = NextCandidate();
        if (next == null)
        {
            Fail(PressedEntryCount > 0
                ? $"no ATC menu entry granted {_pending!.Description}"
                : $"no ATC menu entry left to try for {_pending!.Description}");
            return;
        }

        SelectEntry(next.Value, now);
    }

    /// <summary>
    /// Candidate lines in order: what a screen reader recognised, then the line that
    /// worked for this circumstance before, then the menu in display order. Lines known to
    /// do the wrong thing, lines already tried in this request and lines past the press
    /// budget are skipped.
    /// </summary>
    private int? NextCandidate()
    {
        var pending = _pending!;
        var forbidden = ForbiddenEntries(pending.Context);

        // Direct evidence first: the menu was read and these lines ask for this. It costs
        // a screen capture, so it is read once per request and not once per candidate.
        _menuLines ??= pending.MenuLines?.Invoke();
        if (_menuLines != null)
        {
            foreach (var line in _menuLines)
                if (IsCandidate(line, forbidden)) return line;
        }

        if (LearnedEntry(pending.Context) is int remembered && IsCandidate(remembered, forbidden))
            return remembered;

        for (int line = 1; line <= 10; line++)
        {
            if (line > _options.MaxEntry) break;
            if (IsCandidate(line, forbidden)) return line;
        }

        return null;
    }

    private bool IsCandidate(int line, IReadOnlyCollection<int> forbidden) =>
        line is >= 1 and <= 10 &&
        line <= _options.MaxEntry &&
        !forbidden.Contains(line) &&
        !_pressedLines.Contains(line) &&
        (_options.MaxEntriesProbed <= 0 || PressedEntryCount < _options.MaxEntriesProbed);

    private void SelectEntry(int line, DateTime now)
    {
        ProbedEntry = line;
        _entryStartedUtc = now;
        _entryBlamed = false;
        _pressedLines.Add(line);
        PressedEntryCount++;
        Status = AtcRequestStatus.Probing;

        // The state immediately before the press is what this entry is judged against,
        // so anything that happened earlier - during the grace period, say - is absorbed
        // by the baseline and is not blamed on the entry.
        if (_pending!.StateSignature != null)
            _signatureBaseline = _pending.StateSignature();

        var eventIndex = MenuEventIndex(line);
        _log?.Invoke($"[ATC] ATC menu line {line} (ATC_MENU_{eventIndex})");
        _menu.Select(eventIndex);
    }

    private void Grant(DateTime now)
    {
        Status = AtcRequestStatus.Accepted;
        LastRequestUtc = now;
        var description = _pending!.Description;
        RememberGrantingLine();
        _log?.Invoke(PressedEntryCount == 0
            ? $"[ATC] {description} granted without pressing a menu entry"
            : $"[ATC] {description} granted");
        Finish();
    }

    private void RememberGrantingLine()
    {
        var pending = _pending!;
        if (!string.IsNullOrEmpty(pending.Context) && ProbedEntry >= 1)
            _learned[pending.Context] = ProbedEntry;
    }

    private void Fail(string reason)
    {
        LastFailure = reason;
        LastRequestUtc = _clock();
        Status = AtcRequestStatus.Failed;
        _log?.Invoke($"[ATC] {reason}");
        Finish();
    }

    private void Finish()
    {
        _pending = null;
        ActiveKind = null;
        if (_menuOpen)
        {
            _menu.Close();
            _menuOpen = false;
        }
    }

    /// <summary>
    /// Abandons a request without reporting it as granted. The status becomes
    /// <see cref="AtcRequestStatus.Cancelled"/> - reporting Accepted here made an abandoned
    /// request show up in the panel and the console as one the controller had granted.
    /// </summary>
    public void Cancel()
    {
        if (_pending == null) return;
        _pending = null;
        ActiveKind = null;
        Status = AtcRequestStatus.Cancelled;
        if (_menuOpen)
        {
            _menu.Close();
            _menuOpen = false;
        }
    }
}

/// <summary>
/// Turns the native ATC SimVars into <see cref="AtcInstruction"/> values. The default
/// ATC communicates by voice and a numbered menu, so this is the only way to know what
/// it wants: every clearance it issues also flips a SimVar, and an instruction is
/// emitted on the transition. No audio or text parsing is involved.
/// </summary>
public sealed class NativeAtcMonitor
{
    private ATCState _previous = new();
    private bool _primed;
    /// <summary>
    /// Assignment keys suppressed by the repeat window. A change is always emitted the first
    /// time and the key is then removed, so nothing is ever swallowed: the window only
    /// suppresses a value that has already been reported, and a value that changes again
    /// later is a change rather than a repeat.
    /// </summary>
    private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);

    public AtcSource Source { get; init; } = AtcSource.Native;

    /// <summary>Number of instructions produced by the last <see cref="Update"/> call.</summary>
    public int LastCount { get; private set; }

    /// <summary>
    /// Compares the live state with the previous frame. The first call only primes the
    /// monitor so a long-ago clearance is never replayed as a fresh instruction.
    /// </summary>
    public IReadOnlyList<AtcInstruction> Update(AircraftState state)
    {
        var atc = state.ATC;
        var now = atc.NativeStateUtc == default ? DateTime.UtcNow : atc.NativeStateUtc;
        var instructions = new List<AtcInstruction>();

        if (!_primed)
        {
            _previous = atc.Clone();
            _primed = true;
            LastCount = 0;
            return instructions;
        }

        // --- clearances: emit on the rising edge only ---
        if (!_previous.ClearedTaxi && atc.ClearedTaxi)
        {
            Add(instructions, now, $"Cleared to taxi to runway {atc.DesignatedRunwayTakeoff ?? "the departure runway"}",
                i => { i.IsTaxiInstruction = true; i.Runway = atc.DesignatedRunwayTakeoff; });
        }

        if (!_previous.ClearedTakeoff && atc.ClearedTakeoff)
        {
            Add(instructions, now, $"Cleared for takeoff runway {atc.DesignatedRunwayTakeoff ?? "as assigned"}",
                i => { i.ClearedForTakeoff = true; i.Runway = atc.DesignatedRunwayTakeoff; });
        }

        if (!_previous.ClearedLanding && atc.ClearedLanding)
        {
            Add(instructions, now, $"Cleared to land runway {atc.DesignatedRunwayLanding ?? "as assigned"}",
                i => { i.ClearedToLand = true; i.Runway = atc.DesignatedRunwayLanding; });
        }

        if (!_previous.ClearedIfr && atc.ClearedIfr)
        {
            Add(instructions, now, "Cleared IFR, flight plan active",
                i => i.ClearanceType = "IFR");
        }

        if (!_previous.IFRFlightPlanAvailable && atc.IFRFlightPlanAvailable)
        {
            Add(instructions, now, "IFR clearance available - request clearance delivery",
                i => i.ClearanceType = "IFR_REQUESTABLE");
        }

        // --- assignment changes ---
        if (atc.AssignedAltitudeFt > 0 && atc.AssignedAltitudeFt != _previous.AssignedAltitudeFt)
        {
            bool climb = atc.AssignedAltitudeFt > state.Altitude;
            Add(instructions, now,
                $"{(climb ? "Climb" : "Descend")} and maintain {atc.AssignedAltitudeFt} feet",
                i => i.AltitudeFt = atc.AssignedAltitudeFt);
        }

        AddAssignment(instructions, now, "departurerunway", atc.DesignatedRunwayTakeoff,
            r => $"Runway {r} assigned for departure", i => i.Runway = atc.DesignatedRunwayTakeoff);

        AddAssignment(instructions, now, "arrivalrunway", atc.DesignatedRunwayLanding,
            r => $"Runway {r} assigned for arrival", i => i.Runway = atc.DesignatedRunwayLanding);

        AddAssignment(instructions, now, "arrivalgate", atc.DesignatedGateArrival,
            g => $"Gate {g} assigned for arrival");

        // --- handoff to the next station ---
        if (atc.NextControllerFrequencyMHz > 1 &&
            Math.Abs(atc.NextControllerFrequencyMHz - _previous.NextControllerFrequencyMHz) > 0.0005)
        {
            var freq = atc.NextControllerFrequencyMHz.ToString("0.000", CultureInfo.InvariantCulture);
            var name = string.IsNullOrWhiteSpace(atc.NextControllerName) ? "the next station" : atc.NextControllerName;
            Add(instructions, now, $"Contact {name} on {freq}",
                i => { i.IsHandoff = true; i.HandoffFrequency = freq; });
        }

        _previous = atc.Clone();
        LastCount = instructions.Count;
        return instructions;
    }

    /// <summary>
    /// Resets the baseline so the next frame starts from the current state. The pending set
    /// goes with it: a reset re-primes the monitor, so nothing is half-reported from the
    /// state it was primed on.
    /// </summary>
    public void Reset()
    {
        _primed = false;
        _pendingKeys.Clear();
    }

    private void Add(List<AtcInstruction> instructions, DateTime now, string text, Action<AtcInstruction>? apply = null)
    {
        var instruction = Build(now, text);
        apply?.Invoke(instruction);
        instructions.Add(instruction);
    }

    private void AddAssignment(List<AtcInstruction> instructions, DateTime now, string key, string? value,
        Func<string, string> text, Action<AtcInstruction>? apply = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (string.Equals(value, PreviousValue(key), StringComparison.Ordinal)) return;

        var instruction = Build(now, text(value!));
        apply?.Invoke(instruction);
        instructions.Add(instruction);

        // Reported, so this key stops pending. The pending mark is what suppresses the
        // repeat, and it is cleared on emission so a value that changes back later is a
        // change again rather than something a repeat window has already swallowed.
        _pendingKeys.Remove(key);
    }

    private AtcInstruction Build(DateTime now, string text) => new()
    {
        Source = Source,
        RawText = $"[sim ATC] {text}",
        Station = "SIM",
        Timestamp = now
    };

    private string? PreviousValue(string key) => key switch
    {
        "departurerunway" => _previous.DesignatedRunwayTakeoff,
        "arrivalrunway" => _previous.DesignatedRunwayLanding,
        "arrivalgate" => _previous.DesignatedGateArrival,
        _ => null
    };
}
