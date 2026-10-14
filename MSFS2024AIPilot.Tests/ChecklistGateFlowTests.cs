using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Host that behaves like the sim rather than like a test double: an event only
/// changes a simvar when it is fired, and the simvar feed a checkpoint subscribes
/// to is pushed at the 1 Hz cadence SimConnect delivers with <c>Period.Second</c>
/// (never instantly on change). This is what makes an item that fires the wrong
/// event, or fires two opposing events at once, hang instead of completing.
/// </summary>
internal sealed class SimLikeHost : IChecklistHost
{
    public readonly Dictionary<string, double> SimVars = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Event, double Data)> Fired = new();
    private readonly Dictionary<string, Action<double>> _effects;
    private readonly HashSet<string> _inert;
    private readonly List<(string[] Names, Action<double[]> Callback)> _subs = new();

    public double SimTime;
    public int EngineCount { get; }
    private double _sinceFeed;

    /// <param name="engineCount">Engines the faked aircraft has.</param>
    /// <param name="inertEvents">
    /// Events the aircraft does not bind. An aircraft that binds only the indexed
    /// MIXTURE1_SET (or only the unindexed MIXTURE_SET) is modelled by making the
    /// other form inert.
    /// </param>
    public SimLikeHost(int engineCount, params string[] inertEvents)
    {
        _inert = new HashSet<string>(inertEvents, StringComparer.OrdinalIgnoreCase);
        EngineCount = engineCount;
        _effects = new Dictionary<string, Action<double>>
        {
            // The SDK binds MASTER_BATTERY_SET to payload > 0; ON/OFF are one-shot presets.
            ["MASTER_BATTERY_SET"] = d => SimVars["ELECTRICAL MASTER BATTERY"] = d > 0 ? 1 : 0,
            ["MASTER_BATTERY_ON"] = d => SimVars["ELECTRICAL MASTER BATTERY"] = 1,
            ["MASTER_BATTERY_OFF"] = d => SimVars["ELECTRICAL MASTER BATTERY"] = 0,
            ["AVIONICS_MASTER_SET"] = d => SimVars["AVIONICS MASTER"] = d > 0 ? 1 : 0,
            ["PARKING_BRAKE_SET"] = d => SimVars["BRAKE PARKING POSITION"] = d > 0 ? 1 : 0,
            ["ELECT_FUEL_PUMP1_SET"] = d => SimVars["FUELSYSTEM PUMP SWITCH:1"] = d > 0 ? 1 : 0,
            // The modern [FUEL_SYSTEM] home of the pump: the payload is the Pump.N
            // index, so a payload of 0 addresses no pump at all and changes nothing.
            // An MSFS 2024 stock aircraft binds only these.
            ["FUELSYSTEM_PUMP_ON"] = d => { if (d >= 1) SimVars["FUELSYSTEM PUMP SWITCH:1"] = 1; },
            ["FUELSYSTEM_PUMP_OFF"] = d => { if (d >= 1) SimVars["FUELSYSTEM PUMP SWITCH:1"] = 0; },
            ["THROTTLE_SET"] = d => SimVars["GENERAL ENG THROTTLE LEVER POSITION:1"] = d / 163.83,
            ["MIXTURE_SET"] = d => SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] = d / 163.83,
            ["MIXTURE1_SET"] = d => SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] = d / 163.83,
            ["TOGGLE_ALTERNATOR1"] = d => SimVars["GENERAL ENG MASTER ALTERNATOR:1"] =
                (SimVars.TryGetValue("GENERAL ENG MASTER ALTERNATOR:1", out var v) ? v : 0) > 0.5 ? 0 : 1,
            ["ENGINE_AUTO_START"] = d =>
            {
                for (int e = 1; e <= engineCount; e++) SimVars[$"GENERAL ENG COMBUSTION:{e}"] = 1;
            },
            ["PITOT_HEAT_ON"] = d => SimVars["PITOT HEAT"] = 1,
            ["PITOT_HEAT_OFF"] = d => SimVars["PITOT HEAT"] = 0,
            ["NAV_LIGHTS_OFF"] = d => SimVars["LIGHT NAV"] = 0,
            ["NAV_LIGHTS_ON"] = d => SimVars["LIGHT NAV"] = 1,

            // Taxi-out and runway step.
            ["BEACON_LIGHTS_ON"] = d => SimVars["LIGHT BEACON"] = 1,
            ["BEACON_LIGHTS_OFF"] = d => SimVars["LIGHT BEACON"] = 0,
            ["STROBES_ON"] = d => SimVars["LIGHT STROBE"] = 1,
            ["LANDING_LIGHTS_ON"] = d => SimVars["LIGHT LANDING"] = 1,
            ["TAXI_LIGHTS_ON"] = d => SimVars["LIGHT TAXI"] = 1,
            ["ANTI_ICE_ON"] = d => SimVars["GENERAL ENG ANTI ICE POSITION:1"] = 1,
            ["FLAPS_UP"] = d => SimVars["FLAPS HANDLE INDEX"] = 0,
            // XPNDR_SET carries the code in binary-coded decimal, exactly as
            // NativeAtc.SetTransponder hands it over.
            ["XPNDR_SET"] = d => SimVars["TRANSPONDER CODE:1"] = BcdToDecimal(d)
        };
    }

    /// <summary>Decodes a BCD squawk the way the sim reports it.</summary>
    private static double BcdToDecimal(double bcd)
    {
        var value = (uint)bcd;
        var code = 0;
        var factor = 1;
        for (var digit = 3; digit >= 0; digit--)
        {
            code += (int)((value >> (4 * digit)) & 0xF) * factor;
            factor *= 10;
        }
        return code;
    }

    public void FireEvent(string eventName, double data)
    {
        Fired.Add((eventName, data));
        if (_inert.Contains(eventName)) return;
        if (_effects.TryGetValue(eventName, out var effect)) effect(data);
    }

    public uint Subscribe(string[] names, string[] units, Action<double[]> onData)
    {
        _subs.Add((names, onData));
        return (uint)_subs.Count;
    }

    public void Unsubscribe(uint handle)
    {
    }

    public void Advance(double dt)
    {
        SimTime += dt;
        _sinceFeed += dt;
        if (_sinceFeed < 1.0) return;
        _sinceFeed = 0;
        foreach (var sub in _subs.ToList())
            sub.Callback(sub.Names.Select(n => SimVars.TryGetValue(n, out var v) ? v : 0).ToArray());
    }
}

/// <summary>
/// The gate checklist is what AIPilot.ExecuteStartup blocks on, so its wall-clock
/// cost is what the user sees at the stand: every checkpoint must confirm from the
/// live feed rather than burning its retry cap, and the switches it drives must be
/// left in the state the item asked for.
/// </summary>
public class ChecklistGateFlowTests
{
    private const double LoopDt = 0.05; // AIPilot's 20 Hz main loop

    private static (double Elapsed, Dictionary<string, double> ItemSeconds, bool Complete) RunGate(
        bool turbine, int engines)
    {
        var host = new SimLikeHost(engines);
        return RunGate(host, turbine);
    }

    private static (double Elapsed, Dictionary<string, double> ItemSeconds, bool Complete) RunGate(
        SimLikeHost host, bool turbine)
    {
        var manager = new ChecklistManager(host);
        manager.Load(GenericChecklists.Create("Test", turbine, host.EngineCount));

        var itemSeconds = new Dictionary<string, double>();
        string last = "";
        double startedAt = 0;
        while (!manager.GateComplete && host.SimTime < 600)
        {
            manager.Tick(FlightPhase.Startup, LoopDt);
            host.Advance(LoopDt);

            var status = manager.Status();
            var current = status.Index > 0 ? $"{status.Index}. {status.Item}" : status.Step;
            if (current != last)
            {
                if (last.Length > 0) itemSeconds[last] = host.SimTime - startedAt;
                last = current;
                startedAt = host.SimTime;
            }
        }
        if (last.Length > 0) itemSeconds[last] = host.SimTime - startedAt;
        return (host.SimTime, itemSeconds, manager.GateComplete);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void GateChecklist_CompletesAndLeavesEverySwitchAsRequested(bool turbine, int engines)
    {
        var (elapsed, itemSeconds, complete) = RunGate(turbine, engines);

        Assert.True(complete);
        // A checkpoint that cannot confirm burns its retry cap; with the correct
        // events the whole gate flow is a few seconds of sim time.
        Assert.True(elapsed < 20, $"gate checklist took {elapsed:F1}s of sim time");
        foreach (var (item, seconds) in itemSeconds)
            Assert.True(seconds < 8, $"{item} took {seconds:F1}s, so its event never confirmed the state");
    }

    [Fact]
    public void GateChecklist_LeavesTheMasterBatteryOn()
    {
        var host = new SimLikeHost(2);
        var manager = new ChecklistManager(host);
        manager.Load(GenericChecklists.Create("Test", turbine: true, engineCount: 2));

        while (!manager.GateComplete && host.SimTime < 600)
        {
            manager.Tick(FlightPhase.Startup, LoopDt);
            host.Advance(LoopDt);
        }

        Assert.True(manager.GateComplete);
        // Firing the ON preset and then the OFF preset every cycle leaves the
        // battery switched off, which stops every later checkpoint from working.
        Assert.True(host.SimVars.TryGetValue("ELECTRICAL MASTER BATTERY", out var battery) && battery > 0.5,
            "master battery was left off");
    }

    /// <summary>
    /// The fuel pump has one home in MSFS 2024 - the [FUEL_SYSTEM] Pump.N block,
    /// commanded with FUELSYSTEM_PUMP_ON/OFF (payload = pump index) and verified
    /// with FUELSYSTEM PUMP SWITCH:index. The legacy ELECT_FUEL_PUMP1_SET is dead
    /// on such an aircraft, so a binding that fires only it left "FUEL PUMP - ON"
    /// re-firing against a switch that stayed off for the whole retry cap, which
    /// is the gate-checklist stall seen at LEMD on stock MSFS 2024 aircraft.
    /// </summary>
    [Fact]
    public void GateChecklist_FuelPump_ConfirmsOnTheModernFuelSystemAlone()
    {
        var host = new SimLikeHost(1, "ELECT_FUEL_PUMP1_SET");
        var (elapsed, itemSeconds, complete) = RunGate(host, turbine: false);

        Assert.True(complete);
        Assert.True(elapsed < 20, $"gate checklist took {elapsed:F1}s of sim time");
        Assert.Contains("FUELSYSTEM_PUMP_ON", host.Fired.Select(f => f.Event));
        // The pump index is carried in the payload: 0 addresses no pump at all.
        Assert.Contains(host.Fired, f =>
            f.Event == "FUELSYSTEM_PUMP_ON" && f.Data >= 1);
        Assert.True(host.SimVars.TryGetValue("FUELSYSTEM PUMP SWITCH:1", out var pump) && pump > 0.5,
            "the fuel pump was left off");
    }

    /// <summary>
    /// Runs the gate checklist and collects every item outcome the manager logs.
    /// </summary>
    private static List<string> RunGateCollectingOutcomes(SimLikeHost host)
    {
        var outcomes = new List<string>();
        var manager = new ChecklistManager(host);
        manager.OnLog += outcomes.Add;
        manager.Load(GenericChecklists.Create("Test", turbine: false, engineCount: 1));

        while (!manager.GateComplete && host.SimTime < 600)
        {
            manager.Tick(FlightPhase.Startup, LoopDt);
            host.Advance(LoopDt);
        }
        return outcomes;
    }

    [Fact]
    public void GateChecklist_LeverThatNeverMoves_IsReportedAsFailed()
    {
        // An aircraft that binds neither the unindexed nor the per-engine mixture SET
        // event: the lever stays where it is. The checkpoint used to fire for its
        // whole retry cap (about 26 s at the gate) and then be dropped without a
        // word, leaving the engine running on a rich mixture.
        var host = new SimLikeHost(1, "MIXTURE_SET", "MIXTURE1_SET");
        host.SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] = 100;   // full rich
        var outcomes = RunGateCollectingOutcomes(host);

        Assert.Contains(outcomes, o =>
            o.Contains("MIXTURE - IDLE CUT-OFF :: state never confirmed (retry cap)", StringComparison.Ordinal));
        Assert.Contains("MIXTURE_SET", host.Fired.Select(f => f.Event));
        Assert.Contains("MIXTURE1_SET", host.Fired.Select(f => f.Event));
        Assert.True(host.SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] > 0.5, "the lever moved");
    }

    [Fact]
    public void GateChecklist_IndexedLeverEventAloneAnswers()
    {
        // The same aircraft binding only the per-engine form: the unindexed event
        // does nothing, so the checkpoint passes only because the per-engine one is
        // fired as well.
        var host = new SimLikeHost(1, "MIXTURE_SET");
        host.SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] = 100;
        var outcomes = RunGateCollectingOutcomes(host);

        Assert.Contains(outcomes, o =>
            o.Contains("MIXTURE - IDLE CUT-OFF :: OK:", StringComparison.Ordinal));
        Assert.True(host.SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] < 0.5, "mixture still rich");
    }

    /// <summary>
    /// The taxi-out step runs while the aircraft is rolling to the runway, where a
    /// stranded checkpoint is invisible: the console only reports the gate step.
    /// Every item must confirm from its own feed, so a simvar name the sim does
    /// not publish (as "LIGHT BEAC" was) shows up here as an unfinished step rather
    /// than in the log of a live flight.
    /// </summary>
    [Theory]
    [InlineData(FlightPhase.TaxiOut, "TaxiOutComplete")]
    [InlineData(FlightPhase.Takeoff, "RunwayComplete")]
    public void AirborneGroundSteps_ConfirmFromTheirOwnFeeds(FlightPhase phase, string completion)
    {
        var host = new SimLikeHost(2);
        host.SimVars["TRANSPONDER CODE:1"] = 1200;
        var manager = new ChecklistManager(host);
        manager.Load(GenericChecklists.Create("Test", turbine: false, engineCount: 2));

        var outcomes = new List<string>();
        manager.OnLog += outcomes.Add;

        var done = false;
        while (host.SimTime < 120)
        {
            manager.Tick(phase, LoopDt);
            host.Advance(LoopDt);

            done = completion == "TaxiOutComplete" ? manager.TaxiOutComplete : manager.RunwayComplete;
            if (done) break;
        }

        Assert.True(done, $"step did not complete; last state: {manager.Status().Item} - {manager.Status().ItemState}");
        // A checkpoint whose feed never arrives stays at "waiting for data" for its
        // whole timeout (150 s), and one whose command never confirms burns its retry
        // cap (60 fires, about 24 s), so a healthy step finishes in well under 20 s of
        // sim time. The FLAPS-up retraction used to sit in the second category: the
        // handle index reads 0 when up, so it verified the wrong end of the range.
        Assert.True(host.SimTime < 20, $"the step took {host.SimTime:F1}s of sim time, so an item could not confirm");
        Assert.All(outcomes, o =>
            Assert.DoesNotContain("state never confirmed", o, StringComparison.Ordinal));
    }
}

