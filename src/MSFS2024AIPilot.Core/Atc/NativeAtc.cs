using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.SimConnect;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Atc;

/// <summary>The kinds of clearance the pilot can request from the native ATC.</summary>
public enum AtcRequestKind
{
    None,
    IfreClearance,   // IFR clearance / flight plan activation
    Taxi,            // taxi to runway
    Takeoff,         // takeoff clearance
    Landing,         // landing clearance
    Approach,        // instrument approach
    Pushback,        // pushback approval
    ArrivalGate,     // gate assignment (informational)
    FlightFollowing, // flight following / radar service
}

public static class AtcRequestKinds
{
    public static string Label(AtcRequestKind k) => k switch
    {
        AtcRequestKind.IfreClearance => "IFR clearance",
        AtcRequestKind.Taxi => "taxi clearance",
        AtcRequestKind.Takeoff => "takeoff clearance",
        AtcRequestKind.Landing => "landing clearance",
        AtcRequestKind.Approach => "approach clearance",
        AtcRequestKind.Pushback => "pushback",
        AtcRequestKind.ArrivalGate => "arrival gate",
        AtcRequestKind.FlightFollowing => "flight following",
        _ => "request",
    };
}

public enum AtcRequestStatus
{
    Idle,
    Requested,
    Accepted,
    Cancelled,
    TimedOut,
}

/// <summary>
/// Native-ATC request engine: asks the sim's own ATC for clearances through the
/// ATC menu (ATC_MENU_OPEN + ATC_MENU_n), watches the ATC SimVars for the grant,
/// and remembers which menu lines it already tried for a given circumstance.
/// When a screen reader is supplied it reads the open menu first and presses the
/// line that actually asks for the clearance; blind probing is the fallback.
/// </summary>
public sealed class AtcRequestEngine
{
    private readonly SimConnectClient _sc;
    private AtcRequestKind _pending;
    private AtcRequestStatus _status = AtcRequestStatus.Idle;
    private DateTime _requestedUtc;
    private readonly Dictionary<string, HashSet<int>> _triedLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _learnedLines = new(StringComparer.OrdinalIgnoreCase);
    private string _circumstance = "";
    private readonly object _lock = new();

    // Per-request probing state.
    private Func<IReadOnlyList<int>>? _menuLinesProvider;
    private IReadOnlyList<int>? _recognizedLines;
    private bool _menuOpenSent;
    private DateTime _menuOpenedUtc;
    private int _lastProbedLine;
    private bool _probedAny;
    private int _pressCount;

    /// <summary>Grace period before probing the menu, ms.</summary>
    public int GraceMs { get; set; } = 800;
    /// <summary>Max menu lines to probe per request.</summary>
    public int MaxLines { get; set; } = 10;
    /// <summary>Time between probes, ms.</summary>
    public int ProbeIntervalMs { get; set; } = 1200;
    /// <summary>Request timeout, seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>How long the ATC menu is given to render before the screen is read, ms.</summary>
    public int MenuRenderMs { get; set; } = 500;

    public AtcRequestEngine(SimConnectClient sc) => _sc = sc;

    public AtcRequestKind Pending => _pending;
    public AtcRequestStatus Status => _status;

    /// <summary>
    /// The ATC_MENU_n event index that selects menu line <paramref name="line"/>
    /// (1-10 numbering, where 10 is the entry displayed as "0").
    /// </summary>
    public static int MenuEventIndex(int line) => line >= 10 ? 0 : line;

    /// <summary>
    /// Queues a request. <paramref name="menuLines"/> optionally supplies the menu
    /// lines a screen reader recognised as asking for this clearance (1-10
    /// numbering); they are tried before learned and sequential lines.
    /// </summary>
    public void Request(AtcRequestKind kind, string circumstance, Func<IReadOnlyList<int>>? menuLines = null)
    {
        lock (_lock)
        {
            _pending = kind;
            _status = AtcRequestStatus.Requested;
            _requestedUtc = DateTime.UtcNow;
            _circumstance = circumstance;
            _menuLinesProvider = menuLines;
            _recognizedLines = null;
            _menuOpenSent = false;
            _menuOpenedUtc = default;
            _lastProbedLine = 0;
            _probedAny = false;
            _pressCount = 0;
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _pending = AtcRequestKind.None;
            _status = AtcRequestStatus.Cancelled;
        }
    }

    /// <summary>
    /// Called every control frame. Checks whether the pending request has been
    /// granted (by ATC SimVar change) and, if not, probes the ATC menu.
    /// </summary>
    public void Update(AircraftState s, Action<string>? log = null)
    {
        AtcRequestKind pending;
        AtcRequestStatus status;
        DateTime requestedUtc;
        string circumstance;
        lock (_lock)
        {
            pending = _pending;
            status = _status;
            requestedUtc = _requestedUtc;
            circumstance = _circumstance;
        }
        if (pending == AtcRequestKind.None || status != AtcRequestStatus.Requested) return;

        var now = DateTime.UtcNow;

        // 1. Did the sim grant it already (e.g. cleared without a menu press)?
        if (Granted(s, pending))
        {
            RememberGrantingLine(circumstance);
            lock (_lock) { _status = AtcRequestStatus.Accepted; _pending = AtcRequestKind.None; }
            log?.Invoke($"[ATC] {AtcRequestKinds.Label(pending)} granted");
            return;
        }

        var elapsed = (now - requestedUtc).TotalMilliseconds;
        if (elapsed > TimeoutSeconds * 1000.0)
        {
            lock (_lock) { _status = AtcRequestStatus.TimedOut; _pending = AtcRequestKind.None; }
            log?.Invoke($"[ATC] {AtcRequestKinds.Label(pending)} timed out");
            return;
        }
        if (elapsed < GraceMs) return;

        // 2. Probe the ATC menu: open it, let it render, read it off the screen,
        //    then press the best candidate — one press per frame.
        ProbeMenu(s, pending, circumstance, now, log);
    }

    private void ProbeMenu(AircraftState s, AtcRequestKind kind, string circumstance, DateTime now, Action<string>? log)
    {
        // Open the menu and give it time to draw before the screen is read.
        if (!_menuOpenSent)
        {
            _sc.FireEvent("ATC_MENU_OPEN");
            _menuOpenSent = true;
            _menuOpenedUtc = now;
            return;
        }
        if ((now - _menuOpenedUtc).TotalMilliseconds < MenuRenderMs) return;

        // Read the menu once per request; the reader captures the simulator
        // window and template-matches the candidate phrases for this request.
        if (_recognizedLines == null)
        {
            try { _recognizedLines = _menuLinesProvider?.Invoke() ?? Array.Empty<int>(); }
            catch { _recognizedLines = Array.Empty<int>(); }
        }

        var signature = $"{s.Atc.ClearedTaxi}|{s.Atc.ClearedTakeoff}|{s.Atc.ClearedLanding}|{s.Atc.ClearedIfr}|{s.Atc.RunwaySelected}|{s.Atc.DesignatedRunwayTakeoff}|{s.Atc.DesignatedRunwayLanding}";
        var key = circumstance + ":" + signature;
        if (!_triedLines.TryGetValue(key, out var tried))
        {
            tried = new HashSet<int>();
            _triedLines[key] = tried;
        }

        foreach (var line in Candidates(circumstance))
        {
            if (line < 1 || line > 10 || tried.Contains(line)) continue;
            if (_pressCount >= MaxLines) break;
            tried.Add(line);
            _pressCount++;
            _lastProbedLine = line;
            _probedAny = true;
            var evt = MenuEventIndex(line);
            _sc.FireEvent("ATC_MENU_OPEN");
            _sc.FireEvent($"ATC_MENU_{evt}");
            var source = _recognizedLines is { Count: > 0 } && _recognizedLines.Contains(line) ? " (screen)" : "";
            log?.Invoke($"[ATC] trying line {line} (ATC_MENU_{evt}) for {AtcRequestKinds.Label(kind)}{source}");
            return; // one probe per frame; the grant is detected on a later frame
        }
        log?.Invoke($"[ATC] no menu entry left to try for {AtcRequestKinds.Label(kind)}");
        Cancel();
    }

    /// <summary>
    /// Candidate menu lines in order: what the screen reader recognised, then the
    /// line that granted this circumstance before, then the menu in display order.
    /// </summary>
    private IEnumerable<int> Candidates(string circumstance)
    {
        if (_recognizedLines != null)
            foreach (var line in _recognizedLines) yield return line;

        if (!string.IsNullOrEmpty(circumstance) && _learnedLines.TryGetValue(circumstance, out var learned))
            yield return learned;

        for (int line = 1; line <= 10; line++) yield return line;
    }

    private void RememberGrantingLine(string circumstance)
    {
        if (_probedAny && _lastProbedLine > 0 && !string.IsNullOrEmpty(circumstance))
            _learnedLines[circumstance] = _lastProbedLine;
    }

    /// <summary>True when the ATC SimVars show the requested clearance was issued.</summary>
    private static bool Granted(AircraftState s, AtcRequestKind kind) => kind switch
    {
        AtcRequestKind.Taxi => s.Atc.ClearedTaxi,
        AtcRequestKind.Takeoff => s.Atc.ClearedTakeoff,
        AtcRequestKind.Landing => s.Atc.ClearedLanding,
        AtcRequestKind.IfreClearance => s.Atc.ClearedIfr || !s.Atc.IfrFlightPlanAvailable,
        AtcRequestKind.Approach => s.Atc.ClearedLanding || s.Atc.ArrivalAirportIsTowered,
        AtcRequestKind.Pushback => s.Pushback.Attached,
        AtcRequestKind.ArrivalGate => !string.IsNullOrEmpty(s.Atc.DesignatedGateArrival),
        AtcRequestKind.FlightFollowing => s.Atc.AirportIsTowered,
        _ => false,
    };
}

/// <summary>
/// Radio and transponder management plus handoff detection, all through SimVars
/// and events only (no audio, no CPDLC).
/// </summary>
public sealed class AtcRadio
{
    private readonly SimConnectClient _sc;

    public AtcRadio(SimConnectClient sc) => _sc = sc;

    /// <summary>Tunes COM1 standby then swaps to active.</summary>
    public void TuneCom1(double frequencyMhz)
    {
        var hz = (uint)Math.Round(frequencyMhz * 1_000_000);
        _sc.FireEvent("COM_STBY_RADIO_SET_HZ", hz);
        _sc.FireEvent("COM1_RADIO_SWAP");
    }

    public void TuneCom2(double frequencyMhz)
    {
        var hz = (uint)Math.Round(frequencyMhz * 1_000_000);
        _sc.FireEvent("COM_STBY_RADIO_SET_HZ", hz); // COM2 shares the event with a param in some aircraft; best-effort
    }

    public void SetSquawk(string code)
    {
        if (uint.TryParse(code, out var v))
            _sc.FireEvent("XPNDR_SET", v);
    }

    /// <summary>Detects an ATC handoff: the sim publishes the next controller's frequency.</summary>
    public bool HandoffPending(AircraftState s, out double frequencyMhz, out string? name)
    {
        frequencyMhz = s.Atc.NextControllerFrequencyMHz;
        name = s.Atc.NextControllerName;
        if (frequencyMhz <= 0) return false;
        // Already tuned?
        if (s.Com1ActiveFrequency is { } active &&
            Math.Abs(ParseFreq(active) - frequencyMhz) < 0.005) return false;
        return true;
    }

    public void AcceptHandoff(AircraftState s)
    {
        if (HandoffPending(s, out var freq, out _)) TuneCom1(freq);
    }

    private static double ParseFreq(string s)
    {
        var parts = s.Split('.');
        if (parts.Length == 2 && double.TryParse(parts[0], out var a) && double.TryParse(parts[1], out var b))
            return a + b / Math.Pow(10, parts[1].Length);
        return double.TryParse(s, out var f) ? f : 0;
    }
}
