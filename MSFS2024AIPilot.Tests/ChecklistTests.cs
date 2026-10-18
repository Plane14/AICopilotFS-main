using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// In-memory <see cref="IChecklistHost"/>: records fired events and lets tests
/// drive the simvar feed the executor subscribes to. Not sealed: <see
/// cref="FedChecklistHost"/> derives from it.
/// </summary>
internal class FakeChecklistHost : IChecklistHost
{
    public readonly Dictionary<string, double> SimVars = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Event, double Data)> Fired = new();
    private readonly Dictionary<uint, (string[] Names, Action<double[]> Callback)> _subs = new();
    private uint _next = 1;

    public void FireEvent(string eventName, double data) => Fired.Add((eventName, data));

    public uint Subscribe(string[] names, string[] units, Action<double[]> onData)
    {
        var handle = _next++;
        _subs[handle] = (names, onData);
        onData(Read(names));
        return handle;
    }

    public void Unsubscribe(uint handle) => _subs.Remove(handle);

    public void Set(string name, double value)
    {
        SimVars[name] = value;
        foreach (var sub in _subs.Values.ToList())
            sub.Callback(Read(sub.Names));
    }

    private double[] Read(string[] names) =>
        names.Select(n => SimVars.TryGetValue(n, out var v) ? v : 0).ToArray();
}

/// <summary>
/// Host that subscribes but delivers nothing until the test pushes: a real feed
/// arrives at 1 Hz, and until then every subscribed simvar reads its
/// zero-initialised value.
/// </summary>
internal sealed class SilentChecklistHost : IChecklistHost
{
    public readonly List<(string Event, double Data)> Fired = new();
    private Action<double[]>? _callback;

    public void FireEvent(string eventName, double data) => Fired.Add((eventName, data));

    public uint Subscribe(string[] names, string[] units, Action<double[]> onData)
    {
        _callback = onData;
        return 1;
    }

    public void Unsubscribe(uint handle) => _callback = null;

    public void Push(double value) => _callback!(new[] { value });
}

public class ChecklistExecutorTests
{
    private static ChecklistItem SetItem(string simVar, string eventId, bool expectOn = true) => new()
    {
        Subject = "BEACON",
        Expectation = expectOn ? "ON" : "OFF",
        Kind = ChecklistItemKind.Set,
        Tests = { ConditionNode.Verify(simVar, "bool", expectOn) },
        Actions = { new ChecklistAction { EventId = eventId, Condition = ActionCondition.TestValueFalse } }
    };

    private static ItemOutcome Run(ChecklistExecutor executor, FakeChecklistHost host, int ticks, double dt = 0.5)
    {
        var outcome = ItemOutcome.Running;
        for (int i = 0; i < ticks; i++) outcome = executor.Tick(dt);
        return outcome;
    }

    [Fact]
    public void SetItem_FiresEventUntilStateConfirmed()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { FireIntervalSeconds = 0.4 });
        var item = SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON");

        executor.Begin(item);
        // First fire opportunity after the fire interval elapses.
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Contains(("BEACON_LIGHTS_ON", 1.0), host.Fired);

        // The switch takes effect: the test now passes, the item verifies after the dwell.
        host.Set("LIGHT BEACON", 1);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Equal(ItemOutcome.Completed, executor.Tick(0.5));
        Assert.False(executor.Busy);
    }

    [Fact]
    public void CheckItem_PassesWithoutFiringAnything()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var executor = new ChecklistExecutor(host);
        var item = SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON");

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Equal(ItemOutcome.Completed, executor.Tick(0.5));
        Assert.Empty(host.Fired);
    }

    [Fact]
    public void ManualItem_SettlesWithoutBlockingTheFlow()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { ManualSettleSeconds = 1.0 });
        var item = new ChecklistItem { Subject = "WALKAROUND", Expectation = "CHECK", Kind = ChecklistItemKind.Manual };

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Equal(ItemOutcome.Completed, executor.Tick(0.6));
        Assert.Contains("manual", executor.Status);
    }

    [Fact]
    public void UnsatisfiableCommand_IsReportedAsNeverConfirmed()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions
        {
            ItemTimeoutSeconds = 1.0,
            FireIntervalSeconds = 0.1,
            MaxFiresPerItem = 50
        });
        var item = SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON");

        executor.Begin(item);
        var outcome = Run(executor, host, 12, 0.2);
        Assert.Equal(ItemOutcome.Completed, outcome);
        // The command is fired once: re-firing the same event for its whole retry cap
        // toggled a toggle event twice or three times per intended change, and a checkpoint
        // whose event cannot move the state is reported as the failure it is rather than
        // being re-commanded in silence.
        Assert.Contains("state never confirmed", executor.Status);
        Assert.Single(host.Fired);
    }

    [Fact]
    public void Duration_RequiresSustainedState()
    {
        var host = new FakeChecklistHost();
        host.Set("GEAR HANDLE POSITION", 1);
        var executor = new ChecklistExecutor(host);
        var item = SetItem("GEAR HANDLE POSITION", "GEAR_DOWN");
        item.DurationSeconds = 2.0;

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, Run(executor, host, 2, 0.5)); // 1.0s of the 2.0s dwell
        Assert.Equal(ItemOutcome.Completed, Run(executor, host, 4, 0.5)); // 3.0s + min dwell
        Assert.Empty(host.Fired);
    }

    [Fact]
    public void OnceAction_FiresASingleTime()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { FireIntervalSeconds = 0.1 });
        var item = new ChecklistItem
        {
            Subject = "BEACON",
            Expectation = "ON",
            Kind = ChecklistItemKind.Set,
            // Never satisfiable, so the command keeps being offered.
            Tests = { ConditionNode.Verify("LIGHT BEACON", "bool", true) },
            Actions = { new ChecklistAction { EventId = "BEACON_LIGHTS_ON", Condition = ActionCondition.TestValueFalse, Once = true } }
        };

        executor.Begin(item);
        Run(executor, host, 10, 0.3);
        Assert.Single(host.Fired);
    }

    [Fact]
    public void ForceValidate_CompletesWithoutTheState()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { ManualSettleSeconds = 0.5 });
        var item = new ChecklistItem
        {
            Subject = "SEATBELT",
            Expectation = "ON",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("CABIN SEATBELTS ALERT SWITCH", "bool", true) },
            Actions = { new ChecklistAction { Special = SpecialActionType.ForceValidate } }
        };

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.2));
        Assert.Equal(ItemOutcome.Completed, executor.Tick(0.5));
        Assert.Empty(host.Fired);
    }

    [Fact]
    public void WaitForUserToDoIt_HoldsUntilTimeout()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { WaitForUserTimeoutSeconds = 2.0 });
        var item = new ChecklistItem
        {
            Subject = "FLIGHT CONTROLS",
            Expectation = "CHECK",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("LIGHT BEACON", "bool", true) },
            Actions = { new ChecklistAction { Special = SpecialActionType.WaitForUserToDoIt } }
        };

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, Run(executor, host, 3, 0.5)); // 1.5s < 2s
        Assert.Contains("waiting for pilot", executor.Status);
        Assert.Equal(ItemOutcome.Completed, Run(executor, host, 3, 0.5)); // 3s > 2s
    }

    [Fact]
    public void StartGuardFalse_SkipsItemSilently()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host);
        var item = SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON");
        item.StartGuard = ConditionNode.Verify("SIM ON GROUND", "bool", true); // false in the fake

        executor.Begin(item);
        // The guard is a test like any other, so it is judged once the simulator has
        // answered for its simvars: judging it in Begin, after the subscription had
        // emptied the values, compared nothing but zeros - which satisfied every "equal 0"
        // guard and skipped every real one.
        Assert.True(executor.Busy);
        Assert.Equal(ItemOutcome.Completed, executor.Tick(0.5));
        Assert.False(executor.Busy);
        Assert.Contains("block condition false", executor.Status);
        Assert.Empty(host.Fired);
    }

    [Fact]
    public void HeaderItem_IsNeverBusy()
    {
        var host = new FakeChecklistHost();
        var executor = new ChecklistExecutor(host);
        executor.Begin(new ChecklistItem { Subject = "COCKPIT PREPARATION", Kind = ChecklistItemKind.Header });
        Assert.False(executor.Busy);
    }

    [Fact]
    public void OffExpectation_FiresTheOffEvent()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { FireIntervalSeconds = 0.4 });
        var item = SetItem("LIGHT BEACON", "BEACON_LIGHTS_OFF", expectOn: false);

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Contains(("BEACON_LIGHTS_OFF", 1.0), host.Fired);

        host.Set("LIGHT BEACON", 0);
        Assert.Equal(ItemOutcome.Completed, Run(executor, host, 2, 0.5));
    }

    [Fact]
    public void LeverCommand_SendsTheScaledPayload()
    {
        var host = new FakeChecklistHost();
        // Mixture not yet cut off, so the command is actually issued.
        host.Set("GENERAL ENG MIXTURE LEVER POSITION:1", 100);
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { FireIntervalSeconds = 0.4 });
        var item = new ChecklistItem
        {
            Subject = "MIXTURE",
            Expectation = "IDLE CUT-OFF",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("GENERAL ENG MIXTURE LEVER POSITION:1", "percent", false) },
            Actions = { new ChecklistAction { EventId = "MIXTURE_SET", EventParam = 0, Condition = ActionCondition.TestValueFalse } }
        };

        executor.Begin(item);
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.5));
        Assert.Contains(("MIXTURE_SET", 0.0), host.Fired);
    }

    [Fact]
    public void FeedSilent_ItemNeitherConfirmsNorFires()
    {
        var host = new SilentChecklistHost();
        var executor = new ChecklistExecutor(host);
        executor.Begin(SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON"));

        // A subscribed simvar reads 0 until its first feed arrives, and 0 satisfies
        // an "off" test: an item that trusts that completes itself without ever
        // commanding, and leaves the switch in the state it found.
        Assert.Equal(ItemOutcome.Running, executor.Tick(0.05));
        Assert.Equal("waiting for data", executor.Status);
        for (int i = 0; i < 19; i++) Assert.Equal(ItemOutcome.Running, executor.Tick(0.05));
        Assert.Empty(host.Fired);

        host.Push(1);   // the feed arrives with the switch already on
        var outcome = ItemOutcome.Running;
        for (int i = 0; i < 20 && outcome == ItemOutcome.Running; i++) outcome = executor.Tick(0.05);
        Assert.Equal(ItemOutcome.Completed, outcome);
    }

    [Fact]
    public void FeedNeverArrives_ItemFailsWithNoData()
    {
        var host = new SilentChecklistHost();
        var executor = new ChecklistExecutor(host, new ChecklistExecutorOptions { ItemTimeoutSeconds = 5 });
        executor.Begin(SetItem("LIGHT BEACON", "BEACON_LIGHTS_ON"));

        for (int i = 0; i < 120; i++) executor.Tick(0.05);   // 6 s of silence

        Assert.Contains("no data from the sim", executor.Status);
        Assert.Empty(host.Fired);
    }
}

public class ChecklistManagerTests
{
    private static AircraftChecklistSet TwoItemSet()
    {
        var set = new AircraftChecklistSet { AircraftTitle = "Test", Format = ChecklistSetFormat.Generic };
        set.Lists.Add(new ChecklistList(ChecklistStep.PREFLIGHT_GATE, "GATE")
        {
            Items =
            {
                new ChecklistItem
                {
                    Subject = "BEACON", Expectation = "ON", Kind = ChecklistItemKind.Set,
                    Tests = { ConditionNode.Verify("LIGHT BEACON", "bool", true) },
                    Actions = { new ChecklistAction { EventId = "BEACON_LIGHTS_ON", Condition = ActionCondition.TestValueFalse } }
                },
                new ChecklistItem { Subject = "WALKAROUND", Expectation = "COMPLETE", Kind = ChecklistItemKind.Manual },
                new ChecklistItem { Subject = "PAGE HEADER", Kind = ChecklistItemKind.Header }
            }
        });
        return set;
    }

    [Fact]
    public void StepsFor_MapsEveryFlightPhase()
    {
        Assert.Contains(ChecklistStep.PREFLIGHT_GATE, ChecklistManager.StepsFor(FlightPhase.Preflight));
        Assert.Contains(ChecklistStep.PREFLIGHT_GATE, ChecklistManager.StepsFor(FlightPhase.Startup));
        Assert.Contains(ChecklistStep.PREFLIGHT_TAXI_OUT, ChecklistManager.StepsFor(FlightPhase.TaxiOut));
        Assert.Contains(ChecklistStep.FLIGHT_RUNWAY, ChecklistManager.StepsFor(FlightPhase.Takeoff));
        Assert.Contains(ChecklistStep.FLIGHT_TAKEOFF, ChecklistManager.StepsFor(FlightPhase.InitialClimb));
        Assert.Contains(ChecklistStep.FLIGHT_CLIMB, ChecklistManager.StepsFor(FlightPhase.Climb));
        Assert.Contains(ChecklistStep.FLIGHT_CRUISE, ChecklistManager.StepsFor(FlightPhase.Cruise));
        Assert.Contains(ChecklistStep.FLIGHT_DESCENT, ChecklistManager.StepsFor(FlightPhase.Descent));
        Assert.Contains(ChecklistStep.LANDING_APPROACH_IFR, ChecklistManager.StepsFor(FlightPhase.Approach));
        Assert.Contains(ChecklistStep.LANDING_FINAL, ChecklistManager.StepsFor(FlightPhase.FinalApproach));
        Assert.Contains(ChecklistStep.LANDING_GROUNDROLL, ChecklistManager.StepsFor(FlightPhase.Landing));
        Assert.Contains(ChecklistStep.LANDING_TAXI_IN, ChecklistManager.StepsFor(FlightPhase.TaxiIn));
        Assert.Contains(ChecklistStep.LANDING_GATE, ChecklistManager.StepsFor(FlightPhase.Shutdown));
    }

    [Fact]
    public void Tick_RunsTheWholeStepInOrder()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());
        var log = new List<string>();
        manager.OnLog += log.Add;

        for (int i = 0; i < 40 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);

        Assert.True(manager.GateComplete);
        Assert.Empty(host.Fired); // the state was already correct
        Assert.Contains(log, l => l.Contains("PREFLIGHT_GATE complete"));
    }

    [Fact]
    public void Tick_CommandsTheStateThenVerifies()
    {
        var host = new FakeChecklistHost(); // beacon starts OFF
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        // Ticks 1 starts the item, tick 2 fires (fire interval 0.4s).
        for (int i = 0; i < 5; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);

        Assert.Contains(("BEACON_LIGHTS_ON", 1.0), host.Fired);
        host.Set("LIGHT BEACON", 1);

        for (int i = 0; i < 40 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);

        Assert.True(manager.GateComplete);
    }

    [Fact]
    public void CompletedStep_IsNotRunAgain()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        for (int i = 0; i < 40 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);
        var fired = host.Fired.Count;

        for (int i = 0; i < 10; i++) manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.Equal(fired, host.Fired.Count);
        Assert.False(manager.Executor.Busy);
    }

    [Fact]
    public void RunStep_ReplaysAStepOnDemand()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        for (int i = 0; i < 40 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.True(manager.GateComplete);

        manager.RunStep(ChecklistStep.PREFLIGHT_GATE);
        Assert.True(manager.Executor.Busy);
        for (int i = 0; i < 40 && manager.Executor.Busy; i++)
            manager.Tick(FlightPhase.Unknown, 0.5);
        Assert.True(manager.GateComplete);
    }

    [Fact]
    public void SkipCurrent_AdvancesPastTheItem()
    {
        var host = new FakeChecklistHost(); // beacon never confirms
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        for (int i = 0; i < 10 && !manager.Executor.Busy; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.True(manager.Executor.Busy);

        manager.SkipCurrent();
        // Next item is the manual walkaround; it settles, then the step completes.
        for (int i = 0; i < 40 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.True(manager.GateComplete);
    }

/// <summary>
/// Feeds every simvar the generic checklist verifies, so the steps can finish.
/// Handle positions and lever positions are verified against a range, so a
/// blanket 1 does not satisfy all of them: "flaps up" and "gear up" read handle
/// index 0 and the piston gate's "mixture idle cut-off" reads the lever at 0. A
/// 1 there leaves the checkpoint firing FLAPS_UP against a handle it believes is
/// extended, so the step never finishes.
/// </summary>
internal sealed class FedChecklistHost : FakeChecklistHost
{
    public void SatisfyAll(bool on)
    {
        var value = on ? 1d : 0d;
        foreach (var name in new[]
        {
            "ELECTRICAL MASTER BATTERY", "AVIONICS MASTER", "BRAKE PARKING POSITION",
            "FUELSYSTEM PUMP SWITCH:1", "GENERAL ENG COMBUSTION:1", "GENERAL ENG MASTER ALTERNATOR:1",
            "LIGHT NAV", "LIGHT BEACON", "LIGHT STROBE",
            "LIGHT LANDING", "LIGHT TAXI", "PITOT HEAT",
            "SPOILERS HANDLE POSITION", "AUTOPILOT YAW DAMPER",
            "GENERAL ENG ANTI ICE POSITION:1"
        })
        {
            SimVars[name] = value;
        }

        // The generic checklist only ever verifies these in its satisfied
        // direction: flaps up and gear up read handle index 0, and mixture idle
        // cut-off reads the lever at 0.
        SimVars["FLAPS HANDLE INDEX"] = 0;
        SimVars["GEAR HANDLE POSITION"] = 0;
        SimVars["GENERAL ENG MIXTURE LEVER POSITION:1"] = 0;
    }
}

    [Fact]
    public void Load_DiscardsAnythingInProgress()
    {
        // A queue that outlived its set: Reset cleared _completed but not the queue, so a
        // re-engage left the previous phase's step in the queue and Tick's refill ("if the
        // queue is empty") never happened. The aircraft then ran the climb step while
        // parked at the stand.
        var host = new FedChecklistHost();
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        for (int i = 0; i < 10 && !manager.Executor.Busy; i++)
            manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.True(manager.Executor.Busy);

        manager.Load(GenericChecklists.Create("Test", turbine: false, engineCount: 1));
        Assert.False(manager.Executor.Busy, "a new set must not inherit a running item");

        // The taxi-out step, not the gate step, now runs: the queue was refilled from it.
        host.SatisfyAll(true);
        for (int i = 0; i < 40 && !manager.TaxiOutComplete; i++)
            manager.Tick(FlightPhase.TaxiOut, 0.5);
        Assert.True(manager.TaxiOutComplete);
    }

    [Fact]
    public void AStepTheSetDoesNotHaveIsMarkedCompleteRatherThanRequeued()
    {
        // A step that is wanted but missing was dropped from the queue without being marked
        // complete, so StepsFor (which filters on _completed) re-queued it on every frame:
        // the queue was never empty, StartNextItem never reached the end, and the aircraft
        // sat at the stand waiting for a gate step the package never shipped.
        var host = new FedChecklistHost();
        var manager = new ChecklistManager(host);

        var set = new AircraftChecklistSet { Format = ChecklistSetFormat.Generic };
        set.Lists.Add(new ChecklistList(ChecklistStep.PREFLIGHT_TAXI_OUT, "taxi")
        {
            Items =
            {
                new ChecklistItem
                {
                    Subject = "BEACON", Expectation = "ON", Kind = ChecklistItemKind.Set,
                    Tests = { ConditionNode.Verify("LIGHT BEACON", "bool", true) },
                    Actions = { new ChecklistAction { EventId = "BEACON_LIGHTS_ON" } }
                }
            }
        });
        manager.Load(set);
        host.SimVars["LIGHT BEACON"] = 1;

        // Startup asks for the gate step, which this set does not have; the taxi-out phase
        // then runs the step the set does have.
        for (int i = 0; i < 40 && !manager.TaxiOutComplete; i++)
        {
            manager.Tick(FlightPhase.Startup, 0.5);
            manager.Tick(FlightPhase.TaxiOut, 0.5);
        }

        Assert.True(manager.GateComplete, "the missing gate step must be marked complete");
        Assert.True(manager.TaxiOutComplete);
    }

    [Fact]
    public void Reset_ClearsTheQueueSoANewPhaseStartsItsOwnStep()
    {
        var host = new FedChecklistHost();
        var manager = new ChecklistManager(host);
        manager.Load(GenericChecklists.Create("Test", turbine: false, engineCount: 1));
        host.SatisfyAll(true);

        for (int i = 0; i < 600 && !manager.GateComplete; i++)
            manager.Tick(FlightPhase.Startup, 0.5);
        Assert.True(manager.GateComplete);

        // Reset keeps _completed but drops the queue: with the queue kept, the next phase
        // started whatever was left of the previous one.
        manager.Reset();
        Assert.False(manager.Executor.Busy);

        for (int i = 0; i < 40 && !manager.TaxiOutComplete; i++)
            manager.Tick(FlightPhase.TaxiOut, 0.5);
        Assert.True(manager.TaxiOutComplete);
    }

    [Fact]
    public void Status_ReportsTheRunningItem()
    {
        var host = new FakeChecklistHost();
        host.Set("LIGHT BEACON", 1);
        var manager = new ChecklistManager(host);
        manager.Load(TwoItemSet());

        manager.Tick(FlightPhase.Preflight, 0.1);
        var status = manager.Status();
        Assert.True(status.Loaded);
        Assert.Equal("PREFLIGHT_GATE", status.Step);
        Assert.Equal("BEACON", status.Item);
        Assert.Equal("ON", status.Expectation);
        Assert.Equal(3, status.Lists.Count == 1 ? 3 : 0);
        Assert.Contains("PREFLIGHT_GATE: 2", status.Lists);
    }

    [Fact]
    public void DisableManager_TicksDoNothing()
    {
        var host = new FakeChecklistHost();
        var manager = new ChecklistManager(host) { Enabled = false };
        manager.Load(TwoItemSet());

        for (int i = 0; i < 10; i++) manager.Tick(FlightPhase.Preflight, 0.5);
        Assert.False(manager.Executor.Busy);
        Assert.False(manager.GateComplete);
    }
}

public class ChecklistParserTests
{
    private static string WriteAircraft(string root, string folder, string checklistXml, string? libraryXml = null)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "Checklist"));
        File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), $"[FLTSIM.0]\ntitle = \"Test Aircraft\"\n");
        File.WriteAllText(Path.Combine(dir, "Checklist", "TestAircraft_Checklist.xml"), checklistXml);
        if (libraryXml != null)
            File.WriteAllText(Path.Combine(dir, "Checklist", "Library.xml"), libraryXml);
        return dir;
    }

    private const string LibraryXml = """
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="CheckpointLibrary" version="1,0">
    <Checklist.CheckpointLibrary>
        <Checkpoint Id="TEST_BEACON">
            <CheckpointDesc SubjectTT="TT:TEST.BEACON" ExpectationTT="TT:TEST.ON"/>
            <Test>
                <TestValue>
                    <Operator OpType="GREATER_OR_EQUAL">
                        <Val SimVarName="LIGHT BEACON" Units="Boolean"/>
                        <Val Value="0.5"/>
                    </Operator>
                </TestValue>
                <Action EventID="BEACON_LIGHTS_ON" Condition="TestValueFalse" Copilot="TRUE" Once="FALSE"/>
            </Test>
        </Checkpoint>
    </Checklist.CheckpointLibrary>
</SimBase.Document>
""";

    private const string Checklist2024Xml = """
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="Checklist" version="1,0">
    <Checklist.Checklist>
        <IncludeCheckpointLibrary FileName="Library.xml"/>
        <Step ChecklistStepId="PREFLIGHT_GATE">
            <Page SubjectTT="TT:TEST.PAGE1">
                <Block SubjectTT="TT:TEST.BLOCK1">
                    <Checkpoint ReferenceId="TEST_BEACON" LevelOfDetail="STANDARD"/>
                </Block>
                <Checkpoint ReferenceId="TEST_BEACON" LevelOfDetail="PRO"/>
            </Page>
        </Step>
        <Step ChecklistStepId="FLIGHT_TAKEOFF">
            <Page SubjectTT="TT:TEST.PAGE2">
                <Checkpoint ReferenceId="MISSING_FROM_LIBRARY"/>
            </Page>
        </Step>
    </Checklist.Checklist>
</SimBase.Document>
""";

    [Fact]
    public void Parses2024Format_WithLibraryReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl2024_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAircraft(root, "testaircraft", Checklist2024Xml, LibraryXml);
            File.WriteAllText(Path.Combine(root, "testaircraft", "aircraft.loc"), """
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document>
  <Strings>
    <String ID="TT:TEST.BEACON" String="BEACON"/>
    <String ID="TT:TEST.ON" String="ON"/>
    <String ID="TT:TEST.PAGE1" String="COCKPIT PREPARATION"/>
  </Strings>
</SimBase.Document>
""");

            var set = ChecklistParser.ParseFromAircraft("Test Aircraft", rootOverride: root);

            Assert.Equal(ChecklistSetFormat.Msfs2024Xml, set.Format);
            Assert.True(set.IsAircraftSpecific);

            var gate = set.Find(ChecklistStep.PREFLIGHT_GATE);
            Assert.NotNull(gate);
            Assert.Equal("COCKPIT PREPARATION", gate!.Title);
            Assert.Single(gate.Items); // the PRO-level checkpoint is filtered out

            var standard = gate.Items[0];
            Assert.Equal("BEACON", standard.Subject);
            Assert.Equal("ON", standard.Expectation);
            Assert.Equal(ChecklistItemKind.Set, standard.Kind);
            Assert.Single(standard.Actions);
            Assert.Equal("BEACON_LIGHTS_ON", standard.Actions[0].EventId);
            Assert.Equal("LIGHT BEACON", standard.Tests[0].Operands[0].Name);
            Assert.Equal("TEST_BEACON", standard.Id);

            // The PRO-level checkpoint is filtered out of the STANDARD detail level.
            var takeoff = set.Find(ChecklistStep.FLIGHT_TAKEOFF);
            Assert.NotNull(takeoff);
            Assert.Single(takeoff!.Items);
            Assert.Equal(ChecklistItemKind.Header, takeoff.Items[0].Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Parses2024Format_IncludingAllLevels()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl2024b_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAircraft(root, "testaircraft", Checklist2024Xml, LibraryXml);
            File.WriteAllText(Path.Combine(root, "testaircraft", "aircraft.loc"),
                "<?xml version=\"1.0\"?><SimBase.Document><Strings>" +
                "<String ID=\"TT:TEST.BEACON\" String=\"BEACON\"/>" +
                "<String ID=\"TT:TEST.ON\" String=\"ON\"/>" +
                "<String ID=\"TT:TEST.PAGE1\" String=\"PAGE\"/></Strings></SimBase.Document>");

            var set = ChecklistParser.ParseFromAircraft("Test Aircraft",
                new ChecklistParser.Options { IncludeAllLevels = true }, root);

            // Block checkpoint (STANDARD) + the PRO-level checkpoint on the page.
            Assert.Equal(2, set.Find(ChecklistStep.PREFLIGHT_GATE)!.Items.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParsesLegacyTextFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "clleg_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAircraft(root, "testaircraft", """
<Checklist>
  <Group name="COCKPIT PREPARATION">
    <List name="BEFORE STARTING ENGINES">
      <Item type="title"><Text>SECTION</Text></Item>
      <Item type="actionable">
        <LabelText>BATTERY SWITCH</LabelText>
        <ActionText>ON</ActionText>
      </Item>
      <Item type="actionable">
        <LabelText>FLAPS</LabelText>
        <ActionText>UP</ActionText>
      </Item>
    </List>
    <List name="BEFORE TAKEOFF">
      <Item type="actionable">
        <LabelText>TRANSPONDER</LabelText>
        <ActionText>1200</ActionText>
      </Item>
    </List>
  </Group>
</Checklist>
""");

            var set = ChecklistParser.ParseFromAircraft("Test Aircraft", rootOverride: root);

            Assert.Equal(ChecklistSetFormat.LegacyXml, set.Format);
            var gate = set.Find(ChecklistStep.PREFLIGHT_GATE);
            Assert.NotNull(gate);
            var items = gate!.Items.Where(i => i.Kind != ChecklistItemKind.Header).ToList();
            Assert.Equal(2, items.Count);

            var battery = items[0];
            Assert.Equal("BATTERY SWITCH", battery.Subject);
            Assert.Equal(ChecklistItemKind.Set, battery.Kind);
            // One payload event, so the switch is left in the requested state.
            Assert.Single(battery.Actions);
            Assert.Equal("MASTER_BATTERY_SET", battery.Actions[0].EventId);

            var runway = set.Find(ChecklistStep.FLIGHT_RUNWAY);
            Assert.NotNull(runway);
            Assert.Equal("XPNDR_SET", runway!.Items[0].Actions[0].EventId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NoChecklistFolder_ProducesNotesAndNothingLoaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "clnone_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "testaircraft");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), "title = \"Test Aircraft\"");

            var set = ChecklistParser.ParseFromAircraft("Test Aircraft", rootOverride: root);
            Assert.False(set.IsLoaded);
            Assert.NotEmpty(set.Notes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// The package folder is a slug (e.g. "asobo_b737max") while the sim publishes the
    /// variation's title (e.g. "737 Max 8 Passengers"). Matching on the folder name alone
    /// never finds the package, so the aircraft silently fell back to the generic flow.
    /// </summary>
    [Fact]
    public void Parses2024Format_WhenFolderNameDiffersFromTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), "clslug_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAircraft(root, "asobo_b737max", Checklist2024Xml, LibraryXml);
            File.WriteAllText(Path.Combine(root, "asobo_b737max", "aircraft.cfg"), """
                [FLTSIM.0]
                Title="Asobo 737 Max Common"
                title = "737 Max 8 Passengers"
                """);

            // The title the sim publishes is the variation form, which only appears in the
            // unquoted "title =" line: the quoted Title= above is the package's own name.
            var set = ChecklistParser.ParseFromAircraft("737 Max 8 Passengers", rootOverride: root);

            Assert.Equal(ChecklistSetFormat.Msfs2024Xml, set.Format);
            Assert.True(set.IsAircraftSpecific);
            Assert.NotNull(set.Find(ChecklistStep.PREFLIGHT_GATE));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("BEFORE STARTING ENGINES", ChecklistStep.PREFLIGHT_GATE)]
    [InlineData("BEFORE TAXI", ChecklistStep.PREFLIGHT_TAXI_OUT)]
    [InlineData("BEFORE TAKEOFF", ChecklistStep.FLIGHT_RUNWAY)]
    [InlineData("TAKEOFF", ChecklistStep.FLIGHT_TAKEOFF)]
    [InlineData("CLIMB", ChecklistStep.FLIGHT_CLIMB)]
    [InlineData("CRUISE", ChecklistStep.FLIGHT_CRUISE)]
    [InlineData("DESCENT", ChecklistStep.FLIGHT_DESCENT)]
    [InlineData("APPROACH", ChecklistStep.LANDING_APPROACH)]
    [InlineData("BEFORE LANDING", ChecklistStep.LANDING_FINAL)]
    [InlineData("AFTER LANDING", ChecklistStep.LANDING_GROUNDROLL)]
    [InlineData("TAXI IN", ChecklistStep.LANDING_TAXI_IN)]
    [InlineData("SHUTDOWN", ChecklistStep.LANDING_GATE)]
    public void MapLegacyStep_CoversTheCommonListNames(string listName, ChecklistStep expected)
    {
        Assert.Equal(expected, ChecklistParser.MapLegacyStep(listName));
    }

    [Fact]
    public void Parses2024Format_AllSixteenStepsWithSequencesAndDurations()
    {
        // Real aircraft declare every step, most of them empty.
        var root = Path.Combine(Path.GetTempPath(), "clfull_" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAircraft(root, "testaircraft", """
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="Checklist" version="1,0">
    <Checklist.Checklist>
        <IncludeCheckpointLibrary FileName="Library.xml"/>
        <Step ChecklistStepId="PREFLIGHT_GATE">
            <Page SubjectTT="TT:TEST.PAGE1">
                <Checkpoint ReferenceId="TEST_GEAR"/>
                <Checkpoint ReferenceId="TEST_NO_TEST"/>
                <Checkpoint ReferenceId="TEST_SEQ"/>
            </Page>
        </Step>
        <Step ChecklistStepId="PREFLIGHT_PUSHBACK"></Step>
        <Step ChecklistStepId="PREFLIGHT_TAXI_OUT"></Step>
        <Step ChecklistStepId="FLIGHT_RUNWAY"></Step>
        <Step ChecklistStepId="FLIGHT_TAKEOFF"></Step>
        <Step ChecklistStepId="FLIGHT_CLIMB"></Step>
        <Step ChecklistStepId="FLIGHT_CRUISE"></Step>
        <Step ChecklistStepId="FLIGHT_DESCENT"></Step>
        <Step ChecklistStepId="LANDING_APPROACH"></Step>
        <Step ChecklistStepId="LANDING_APPROACH_VFR"></Step>
        <Step ChecklistStepId="LANDING_APPROACH_IFR"></Step>
        <Step ChecklistStepId="LANDING_FINAL"></Step>
        <Step ChecklistStepId="LANDING_TOUCHDOWN"></Step>
        <Step ChecklistStepId="LANDING_GROUNDROLL"></Step>
        <Step ChecklistStepId="LANDING_TAXI_IN"></Step>
        <Step ChecklistStepId="LANDING_GATE"></Step>
    </Checklist.Checklist>
</SimBase.Document>
""", """
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="CheckpointLibrary" version="1,0">
    <Checklist.CheckpointLibrary>
        <Checkpoint Id="TEST_GEAR">
            <CheckpointDesc SubjectTT="TT:TEST.GEAR" ExpectationTT="TT:TEST.DOWN"/>
            <Test>
                <TestValue>
                    <Operator OpType="GREATER_OR_EQUAL">
                        <Val SimVarName="GEAR HANDLE POSITION" Units="Boolean"/>
                        <Val Value="0.5"/>
                    </Operator>
                </TestValue>
                <Duration Value="2" Cumulative="FALSE"/>
                <Once>False</Once>
                <Action EventID="GEAR_DOWN" Condition="TestValueFalse" Copilot="TRUE"/>
            </Test>
        </Checkpoint>
        <Checkpoint Id="TEST_NO_TEST">
            <CheckpointDesc SubjectTT="TT:TEST.BELTS" ExpectationTT="TT:TEST.ON"/>
        </Checkpoint>
        <Checkpoint Id="TEST_SEQ">
            <CheckpointDesc SubjectTT="TT:TEST.SEQ" ExpectationTT="TT:TEST.DONE"/>
            <Sequence SeqType="Ordered">
                <Test>
                    <TestValue>
                        <Operator OpType="GREATER_OR_EQUAL">
                            <Val SimVarName="SIM ON GROUND" Units="Boolean"/>
                            <Val Value="0.5"/>
                        </Operator>
                    </TestValue>
                </Test>
                <Test>
                    <TestValue>
                        <Operator OpType="GREATER_OR_EQUAL">
                            <Val SimVarName="LIGHT NAV" Units="Boolean"/>
                            <Val Value="0.5"/>
                        </Operator>
                    </TestValue>
                    <Action EventID="NAV_LIGHTS_ON" Condition="TestValueFalse" Copilot="TRUE"/>
                </Test>
            </Sequence>
        </Checkpoint>
    </Checklist.CheckpointLibrary>
</SimBase.Document>
""");

            var set = ChecklistParser.ParseFromAircraft("Test Aircraft", rootOverride: root);

            // Only steps with pages produce lists; the fifteen empty ones are absent.
            Assert.Single(set.Lists);
            var items = set.Find(ChecklistStep.PREFLIGHT_GATE)!.Items;
            Assert.Equal(3, items.Count);

            // Duration and Once come from the <Test> element.
            var gear = items[0];
            Assert.Equal(2.0, gear.DurationSeconds);
            Assert.False(gear.Once);
            Assert.Equal(ChecklistItemKind.Set, gear.Kind);
            Assert.Equal("GEAR HANDLE POSITION", gear.Tests[0].Operands[0].Name);

            // A checkpoint with no test auto-validates: it degrades to Manual.
            var noTest = items[1];
            Assert.Equal(ChecklistItemKind.Manual, noTest.Kind);
            Assert.Empty(noTest.Tests);

            // An ordered <Sequence> is flattened: both sub-tests must hold.
            var seq = items[2];
            Assert.Equal(2, seq.Tests.Count);
            Assert.Equal(ChecklistItemKind.Set, seq.Kind);
            Assert.Contains(seq.Tests, t => t.Operands[0].Name == "SIM ON GROUND");
            Assert.Contains(seq.Tests, t => t.Operands[0].Name == "LIGHT NAV");
            Assert.Equal("NAV_LIGHTS_ON", seq.Actions[0].EventId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class GenericChecklistTests
{
    [Fact]
    public void CoversTheFullGateToGateFlow()
    {
        var set = GenericChecklists.Create("Test", turbine: true, engineCount: 2);

        Assert.Equal(ChecklistSetFormat.Generic, set.Format);
        Assert.False(set.IsAircraftSpecific);

        foreach (var step in new[]
        {
            ChecklistStep.PREFLIGHT_GATE, ChecklistStep.PREFLIGHT_TAXI_OUT, ChecklistStep.FLIGHT_RUNWAY,
            ChecklistStep.FLIGHT_TAKEOFF, ChecklistStep.FLIGHT_DESCENT, ChecklistStep.LANDING_APPROACH,
            ChecklistStep.LANDING_FINAL, ChecklistStep.LANDING_GROUNDROLL, ChecklistStep.LANDING_TAXI_IN,
            ChecklistStep.LANDING_GATE
        })
        {
            var list = set.Find(step);
            Assert.NotNull(list);
            Assert.True(list!.ExecutableCount > 0, $"{step} has no executable items");
        }
    }

    [Fact]
    public void EngineStart_ChecksEveryEngine()
    {
        var set = GenericChecklists.Create("Test", turbine: true, engineCount: 4);
        var start = set.Find(ChecklistStep.PREFLIGHT_GATE)!.Items
            .First(i => i.Subject == "ENGINE START");

        Assert.Equal(4, start.Tests.Count);
        Assert.Contains(start.Tests, t => t.Operands[0].Name == "GENERAL ENG COMBUSTION:4");
    }
}

public class ChecklistTextResolverTests
{
    private static readonly ChecklistEnvironment Day = new();
    private static readonly ChecklistEnvironment Night = new() { Night = true };
    private static readonly ChecklistEnvironment Icing = new() { IcingRisk = true };

    [Fact]
    public void Battery_Toggles()
    {
        var on = ChecklistTextResolver.Resolve("BATTERY SWITCH", "ON", Day);
        Assert.False(on.IsManual);
        Assert.Equal("ELECTRICAL MASTER BATTERY", on.VerifySimVar);
        // Exactly one event must be emitted: firing the one-shot ON and OFF presets
        // together switches the battery back off the instant it was switched on.
        Assert.Equal(new[] { ("MASTER_BATTERY_SET", 1.0) }, on.Events);

        var off = ChecklistTextResolver.Resolve("BATTERY SWITCH", "OFF", Day);
        Assert.Equal(new[] { ("MASTER_BATTERY_SET", 0.0) }, off.Events);
    }

    [Fact]
    public void Generator_UsesTheAlternatorToggleBinding()
    {
        var on = ChecklistTextResolver.Resolve("GENERATOR", "ON", Day);
        Assert.False(on.IsManual);
        Assert.Equal("GENERAL ENG MASTER ALTERNATOR:1", on.VerifySimVar);
        Assert.Equal(new[] { ("TOGGLE_ALTERNATOR1", 1.0) }, on.Events);

        var off = ChecklistTextResolver.Resolve("GENERATOR", "OFF", Day);
        Assert.Equal(new[] { ("TOGGLE_ALTERNATOR1", 1.0) }, off.Events);
        Assert.False(off.ExpectOn);
    }

    [Fact]
    public void Flaps_UseHandlePositions()
    {
        var up = ChecklistTextResolver.Resolve("FLAPS", "UP", Day);
        Assert.Contains(("FLAPS_UP", 1.0), up.Events);
        Assert.Equal("FLAPS HANDLE INDEX", up.VerifySimVar);
        // Retracted means index 0, which is the off state: verifying the on state
        // left the checkpoint firing FLAPS_UP for its whole retry cap.
        Assert.False(up.ExpectOn);

        var two = ChecklistTextResolver.Resolve("FLAPS", "2", Day);
        Assert.Contains(("FLAPS_2", 1.0), two.Events);
        Assert.True(two.ExpectOn);

        var down = ChecklistTextResolver.Resolve("FLAPS", "DOWN", Day);
        Assert.Contains(("FLAPS_DOWN", 1.0), down.Events);
        Assert.True(down.ExpectOn);
    }

    [Fact]
    public void Transponder_SetsTheAssignedCode()
    {
        var cmd = ChecklistTextResolver.Resolve("TRANSPONDER", "1200", Day);
        Assert.Contains(("XPNDR_SET", 0x1200), cmd.Events);
        Assert.Equal("TRANSPONDER CODE:1", cmd.VerifySimVar);
    }

    [Fact]
    public void LeverBindings_FireTheUnindexedAndPerEngineSetEvents()
    {
        var twin = new ChecklistEnvironment { EngineCount = 2 };

        // An aircraft that binds only the per-engine form ignores the unindexed
        // event, so the checkpoint fires both with the same payload: a SET event is
        // absolute, so it can no more flip state than one ON/OFF preset can.
        var cut = ChecklistTextResolver.Resolve("MIXTURE", "IDLE CUT-OFF", twin);
        Assert.Contains(("MIXTURE_SET", 0.0), cut.Events);
        Assert.Contains(("MIXTURE1_SET", 0.0), cut.Events);
        Assert.Contains(("MIXTURE2_SET", 0.0), cut.Events);
        Assert.DoesNotContain(cut.Events, e => e.Name is "MIXTURE3_SET" or "MIXTURE4_SET");

        var idle = ChecklistTextResolver.Resolve("THROTTLES", "IDLE", twin);
        Assert.Contains(idle.Events, e => e.Name == "THROTTLE_SET" && Math.Abs(e.Data - 819.15) < 0.01);
        Assert.Contains(idle.Events, e => e.Name == "THROTTLE1_SET" && Math.Abs(e.Data - 819.15) < 0.01);
        Assert.Contains(idle.Events, e => e.Name == "THROTTLE2_SET" && Math.Abs(e.Data - 819.15) < 0.01);

        var cond = ChecklistTextResolver.Resolve("CONDITION LEVER", "CUT-OFF", twin);
        Assert.Contains(cond.Events, e => e.Name == "CONDITION_LEVER_1_SET" && e.Data == 0.0);
        Assert.Contains(cond.Events, e => e.Name == "CONDITION_LEVER_2_SET" && e.Data == 0.0);
        Assert.DoesNotContain(cond.Events, e => e.Name == "CONDITION_LEVER_3_SET");
    }

    [Fact]
    public void AsRequiredLights_FollowDayNight()
    {
        var day = ChecklistTextResolver.Resolve("EXTERIOR LIGHTS", "AS REQUIRED", Day);
        Assert.True(day.Events.All(e => e.Name.EndsWith("_OFF", StringComparison.Ordinal)));

        var night = ChecklistTextResolver.Resolve("EXTERIOR LIGHTS", "AS REQUIRED", Night);
        Assert.True(night.Events.All(e => e.Name.EndsWith("_ON", StringComparison.Ordinal)));
    }

    [Fact]
    public void AsRequiredIceProtection_FollowsIcingRisk()
    {
        var clear = ChecklistTextResolver.Resolve("PITOT HEAT", "AS REQUIRED", Day);
        Assert.Contains(("PITOT_HEAT_OFF", 1.0), clear.Events);

        var icing = ChecklistTextResolver.Resolve("PITOT HEAT", "AS REQUIRED", Icing);
        Assert.Contains(("PITOT_HEAT_ON", 1.0), icing.Events);
    }

    [Fact]
    public void OneShotPreset_OnlyThePresetForTheRequestedStateFires()
    {
        // SPOILERS_ARM_ON is a one-shot arm preset: it must never be fired when the
        // item asks for the opposite state, the way the master battery pair used to be.
        var arm = ChecklistTextResolver.Resolve("SPOILERS ARM", "ARM", Day);
        Assert.Equal(new[] { ("SPOILERS_ARM_ON", 1.0) }, arm.Events);

        var disarm = ChecklistTextResolver.Resolve("SPOILERS ARM", "OFF", Day);
        Assert.Empty(disarm.Events);
    }

    [Fact]
    public void UnmappedItem_DegradesToManual()
    {
        var cmd = ChecklistTextResolver.Resolve("CABIN PRESSURE DIFFERENTIAL", "CHECK", Day);
        Assert.True(cmd.IsManual);
        Assert.False(string.IsNullOrEmpty(cmd.ManualReason));
    }

    [Fact]
    public void Magneto_SetsTheSwitchState()
    {
        var both = ChecklistTextResolver.Resolve("IGNITION SWITCH", "BOTH", Day);
        Assert.Contains(("MAGNETO1_SET", 3.0), both.Events);

        var off = ChecklistTextResolver.Resolve("MAGNETOS", "OFF", Day);
        Assert.Contains(("MAGNETO1_SET", 0.0), off.Events);
    }

    [Fact]
    public void Beacon_VerifiesTheBeaconLightSimVar()
    {
        // The beacon switch state is LIGHT BEACON. A truncated name ("LIGHT BEAC")
        // is refused by the sim with NAME_UNRECOGNIZED, its feed never delivers,
        // and the checkpoint hangs at "waiting for data" for its whole timeout.
        var on = ChecklistTextResolver.Resolve("BEACON", "ON", Day);
        Assert.Equal("LIGHT BEACON", on.VerifySimVar);
        Assert.Contains(("BEACON_LIGHTS_ON", 1.0), on.Events);
    }

    [Fact]
    public void Spoilers_VerifyTheHandlePositionSimVar()
    {
        // The handle position SimVar is SPOILERS HANDLE POSITION in percent. There
        // is no "SPOILERS HANDLE PERCENT" variable, so binding to it leaves both
        // spoiler checkpoints unverifiable.
        var on = ChecklistTextResolver.Resolve("SPOILERS", "ON", Day);
        Assert.Equal("SPOILERS HANDLE POSITION", on.VerifySimVar);
        Assert.Equal("percent", on.VerifyUnits);

        var arm = ChecklistTextResolver.Resolve("SPOILERS ARM", "ARM", Day);
        Assert.Equal("SPOILERS HANDLE POSITION", arm.VerifySimVar);
    }

    [Fact]
    public void TurbineAircraft_HasNoMixtureCheckpoint()
    {
        // A turbine has no mixture lever, so the piston checkpoint can never
        // confirm: on a jet with an unparsed aircraft config it used to burn its
        // retry cap against a lever that never moves.
        var turbine = new ChecklistEnvironment { Turbine = true, EngineCount = 2 };
        var mixture = ChecklistTextResolver.Resolve("MIXTURE", "IDLE CUT-OFF", turbine);
        Assert.True(mixture.IsManual);
        Assert.False(string.IsNullOrEmpty(mixture.ManualReason));
        Assert.Null(mixture.VerifySimVar);

        // The piston binding still applies where there is a lever.
        var piston = ChecklistTextResolver.Resolve("MIXTURE", "IDLE CUT-OFF", Day);
        Assert.False(piston.IsManual);
        Assert.Equal("GENERAL ENG MIXTURE LEVER POSITION:1", piston.VerifySimVar);
    }

    /// <summary>
    /// Every variable a binding verifies must be a variable the sim actually
    /// publishes: names come from the MSFS 2024 Simulation Variables index. A
    /// binding that names anything else is answered by the sim with
    /// NAME_UNRECOGNIZED (its feed never delivers and its checkpoint hangs), which
    /// is how "LIGHT BEAC" and "SPOILERS HANDLE PERCENT" reached a live flight.
    /// </summary>
    [Fact]
    public void EveryBinding_VerifiesASimVarTheSimPublishes()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ELECTRICAL MASTER BATTERY",
            "GENERAL ENG MASTER ALTERNATOR:1",
            "APU SWITCH",
            "EXTERNAL POWER ON",
            "FUELSYSTEM PUMP SWITCH:1",
            "LIGHT BEACON",
            "LIGHT NAV",
            "LIGHT STROBE",
            "LIGHT LANDING",
            "LIGHT TAXI",
            "LIGHT LOGO",
            "LIGHT PANEL",
            "GENERAL ENG ANTI ICE POSITION:1",
            "GENERAL ENG STARTER:1",
            "GENERAL ENG FUEL VALVE:1",
            "GENERAL ENG MIXTURE LEVER POSITION:1",
            "GENERAL ENG THROTTLE LEVER POSITION:1",
            "GENERAL ENG PROPELLER LEVER POSITION:1",
            "GEAR HANDLE POSITION",
            "SPOILERS HANDLE POSITION",
            "BRAKE PARKING POSITION",
            "AUTOPILOT YAW DAMPER",
            "FLAPS HANDLE INDEX",
            "TRANSPONDER CODE:1"
        };

        var squawked = new ChecklistEnvironment { TransponderCode = "0612" };
        var subjects = new[]
        {
            ("BATTERY SWITCH", "ON"), ("AVIONICS SWITCH", "ON"), ("APU", "ON"),
            ("EXTERNAL POWER", "ON"), ("FUEL PUMP", "ON"), ("BEACON", "ON"),
            ("NAV LIGHTS", "ON"), ("STROBES", "ON"), ("LANDING LIGHTS", "ON"),
            ("TAXI LIGHTS", "ON"), ("LOGO LIGHTS", "ON"), ("PANEL LIGHTS", "ON"),
            ("ANTI ICE", "ON"), ("WINDSHIELD DEICE", "ON"), ("STARTER", "START"),
            ("FUEL VALVE", "ON"), ("MIXTURE", "ON"), ("THROTTLES", "IDLE"),
            ("CONDITION LEVER", "MAX"), ("FUEL SELECTOR", "ALL"), ("GEAR", "DOWN"),
            ("SPOILERS", "ON"), ("SPOILERS ARM", "ARM"), ("PARKING BRAKE", "SET"),
            ("YAW DAMPER", "ON"), ("AUTOBRAKE", "MEDIUM"),
            ("FLAPS", "UP"), ("TRANSPONDER", "SET")
        };

        foreach (var (subject, expectation) in subjects)
        {
            var cmd = ChecklistTextResolver.Resolve(subject, expectation, squawked);
            if (cmd.VerifySimVar is null) continue;
            Assert.True(known.Contains(cmd.VerifySimVar),
                $"'{subject}' verifies '{cmd.VerifySimVar}', which is not in the MSFS 2024 Simulation Variables index");
        }
    }
}
