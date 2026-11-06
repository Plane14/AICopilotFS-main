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
/// </summary>
public sealed class AtcRequestEngine
{
    private readonly SimConnectClient _sc;
    private AtcRequestKind _pending;
    private AtcRequestStatus _status = AtcRequestStatus.Idle;
    private DateTime _requestedUtc;
    private readonly Dictionary<string, HashSet<string>> _triedLines = new(StringComparer.OrdinalIgnoreCase);
    private string _circumstance = "";
    private readonly object _lock = new();

    /// <summary>Grace period before probing the menu, ms.</summary>
    public int GraceMs { get; set; } = 800;
    /// <summary>Max menu lines to probe per request.</summary>
    public int MaxLines { get; set; } = 10;
    /// <summary>Time between probes, ms.</summary>
    public int ProbeIntervalMs { get; set; } = 1200;
    /// <summary>Request timeout, seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public AtcRequestEngine(SimConnectClient sc) => _sc = sc;

    public AtcRequestKind Pending => _pending;
    public AtcRequestStatus Status => _status;

    public void Request(AtcRequestKind kind, string circumstance)
    {
        lock (_lock)
        {
            _pending = kind;
            _status = AtcRequestStatus.Requested;
            _requestedUtc = DateTime.UtcNow;
            _circumstance = circumstance;
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

        // 1. Did the sim grant it already (e.g. cleared without a menu press)?
        if (Granted(s, pending))
        {
            lock (_lock) { _status = AtcRequestStatus.Accepted; _pending = AtcRequestKind.None; }
            log?.Invoke($"[ATC] {AtcRequestKinds.Label(pending)} granted");
            return;
        }

        var elapsed = (DateTime.UtcNow - requestedUtc).TotalMilliseconds;
        if (elapsed > TimeoutSeconds * 1000.0)
        {
            lock (_lock) { _status = AtcRequestStatus.TimedOut; _pending = AtcRequestKind.None; }
            log?.Invoke($"[ATC] {AtcRequestKinds.Label(pending)} timed out");
            return;
        }
        if (elapsed < GraceMs) return;

        // 2. Probe the ATC menu. We cannot read the menu text without screen capture,
        //    so we try entries in order and watch for the SimVar grant.
        ProbeMenu(s, pending, circumstance, log);
    }

    private void ProbeMenu(AircraftState s, AtcRequestKind kind, string circumstance, Action<string>? log)
    {
        // Signature of the current ATC state so we don't re-press the same line.
        var signature = $"{s.Atc.ClearedTaxi}|{s.Atc.ClearedTakeoff}|{s.Atc.ClearedLanding}|{s.Atc.ClearedIfr}|{s.Atc.RunwaySelected}|{s.Atc.DesignatedRunwayTakeoff}|{s.Atc.DesignatedRunwayLanding}";

        if (!_triedLines.TryGetValue(circumstance + ":" + signature, out var tried))
        {
            tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _triedLines[circumstance + ":" + signature] = tried;
        }

        for (int i = 0; i < MaxLines; i++)
        {
            var line = $"ATC_MENU_{i}";
            if (tried.Contains(line)) continue;
            tried.Add(line);
            _sc.FireEvent("ATC_MENU_OPEN");
            _sc.FireEvent(line);
            log?.Invoke($"[ATC] trying {line} for {AtcRequestKinds.Label(kind)}");
            return; // one probe per frame; the grant is detected on a later frame
        }
        log?.Invoke($"[ATC] no menu entry left to try for {AtcRequestKinds.Label(kind)}");
        Cancel();
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
