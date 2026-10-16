using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The simulator's own ATC publishes every clearance as a SimVar. These tests drive
/// <see cref="NativeAtcMonitor"/> with the value sequences a real flight produces and
/// assert the instructions that come out: clearances on the rising edge only, altitude
/// assignments, and the handoff to the next station.
/// </summary>
public class NativeAtcMonitorTests
{
    private static AircraftState State(Action<ATCState> configure) => State(0, configure);

    private static AircraftState State(double altitudeFeet, Action<ATCState> configure)
    {
        var state = new AircraftState { Altitude = altitudeFeet };
        configure(state.ATC);
        // The monitor reads the update time from the ATC block.
        state.ATC.NativeStateUtc = DateTime.UtcNow;
        return state;
    }

    [Fact]
    public void FirstFrameOnlyPrimesTheBaseline()
    {
        var monitor = new NativeAtcMonitor();
        var state = State(a => a.ClearedTakeoff = true);

        // A clearance that was already granted before the pilot started watching must
        // not be replayed as a fresh instruction.
        Assert.Empty(monitor.Update(state));
        Assert.Empty(monitor.Update(state));
        Assert.Equal(0, monitor.LastCount);
    }

    [Fact]
    public void TaxiClearanceIsEmittedOnTheRisingEdge()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(_ => { }));
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        var instruction = monitor.Update(State(a =>
        {
            a.DesignatedRunwayTakeoff = "24L";
            a.ClearedTaxi = true;
        })).Single();

        Assert.True(instruction.IsTaxiInstruction);
        Assert.Equal("24L", instruction.Runway);
        Assert.Contains("Cleared to taxi", instruction.RawText);
        Assert.Equal(AtcSource.Native, instruction.Source);
    }

    [Fact]
    public void TakeoffClearanceCarriesTheRunwayAndTheFlag()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        var instruction = monitor.Update(State(a =>
        {
            a.DesignatedRunwayTakeoff = "24L";
            a.ClearedTakeoff = true;
        })).Single(i => i.ClearedForTakeoff);

        Assert.True(instruction.ClearedForTakeoff);
        Assert.Equal("24L", instruction.Runway);
    }

    [Fact]
    public void LandingClearanceIsReportedOnTheRisingEdge()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayLanding = "32R"));

        var instruction = monitor.Update(State(a =>
        {
            a.DesignatedRunwayLanding = "32R";
            a.ClearedLanding = true;
        })).Single(i => i.ClearedToLand);

        Assert.Equal("32R", instruction.Runway);
        Assert.Contains("Cleared to land", instruction.RawText);
    }

    [Fact]
    public void ARepeatedRunwayAssignmentIsEmittedOnce()
    {
        // One change is emitted once. Repeating the same frame must not repeat the
        // instruction, which is what a taxi list scrolling the same runway would do.
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        Assert.Single(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "27")));
        Assert.Single(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "09")));
        Assert.Empty(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "09")));
    }

    [Fact]
    public void ARunwayChangeIsNeverSwallowed()
    {
        // The assignment path used to be suppressed by a repeat window measured against the
        // SimVar's own timestamp, so while the ATC block was refreshing a suppressed
        // assignment was dropped for good and the runway the controller had just assigned
        // was never reported at all.
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        var assigned = monitor.Update(State(a => a.DesignatedRunwayTakeoff = "09"));

        var first = Assert.Single(assigned);
        Assert.Equal("09", first.Runway);
        Assert.Empty(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "09")));
    }

    [Fact]
    public void ReturnToAnEarlierRunwayIsReportedAgain()
    {
        // A controller that assigns 24L, then 15, then 24L again has issued three
        // assignments, and the third is a change even though the value was recent.
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "15"));
        var again = monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L")).Single(i => i.Runway == "24L");

        Assert.Equal("24L", again.Runway);
    }

    [Fact]
    public void ResetClearsThePendingAssignments()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L"));

        monitor.Reset();

        // A reset re-primes the baseline, so nothing is half-reported from the old state.
        Assert.Empty(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L")));
        Assert.Empty(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L")));
    }

    [Fact]
    public void AssignedAltitudeReportsClimbVersusDescend()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(7000, a => a.AssignedAltitudeFt = 0));

        var climb = monitor.Update(State(7000, a => a.AssignedAltitudeFt = 8000)).Single();
        Assert.Contains("Climb", climb.RawText);
        Assert.Equal(8000, climb.AltitudeFt);

        var descend = monitor.Update(State(7000, a => a.AssignedAltitudeFt = 6000)).Single();
        Assert.Contains("Descend", descend.RawText);
    }

    [Fact]
    public void HandoffToTheNextStationIsDetected()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(a => a.NextControllerFrequencyMHz = 0));

        var handoff = monitor.Update(State(a =>
        {
            a.NextControllerFrequencyMHz = 121.5;
            a.NextControllerName = "Barcelona Approach";
        })).Single(i => i.IsHandoff);

        Assert.True(handoff.IsHandoff);
        Assert.Equal("121.500", handoff.HandoffFrequency);
        Assert.Contains("121.500", handoff.RawText);
    }

    [Fact]
    public void NoHandoffWhenNoStationIsComing()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(_ => { }));

        // Zero means "no future agent", which is the common case; it must not be
        // reported as a handoff to 0.000.
        Assert.Empty(monitor.Update(State(a => a.NextControllerFrequencyMHz = 0)));
    }

    [Fact]
    public void RunwayAssignmentChangeIsReportedOnce()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(_ => { }));

        var first = monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L")).Single();
        Assert.Contains("Runway 24L assigned for departure", first.RawText);

        // Same value again: the assignment did not change.
        Assert.Empty(monitor.Update(State(a => a.DesignatedRunwayTakeoff = "24L")));
    }

    [Fact]
    public void ResetReprimesTheBaseline()
    {
        var monitor = new NativeAtcMonitor();
        monitor.Update(State(_ => { }));
        monitor.Update(State(a => a.ClearedTaxi = true));

        monitor.Reset();
        Assert.Empty(monitor.Update(State(a => a.ClearedTaxi = true)));
    }
}

/// <summary>
/// BeyondATC's Player.log is the Unity engine log. It carries no speech transcript, but
/// it is the only window into the running application, so these tests parse the lines
/// the application actually writes (copied from a real session log).
/// </summary>
public class BeyondAtcLogParserTests
{
    [Fact]
    public void HotkeysAreReadFromTheBindingsLine()
    {
        var e = BeyondAtcLogParser.ParseLine("[Bindings] Hotkeys imported to PlayerPrefs: [0, 32, 0, 0, 0] (defaults)", 1);

        Assert.NotNull(e);
        var hotkeys = BeyondAtcLogParser.ParseHotkeys(e!);
        Assert.NotNull(hotkeys);
        Assert.Equal(new[] { 0, 32, 0, 0, 0 }, hotkeys);
    }

    [Fact]
    public void HotkeyLineWithoutValuesYieldsNothing()
    {
        var e = BeyondAtcLogParser.ParseLine("[Bindings] Hotkeys imported to PlayerPrefs: (defaults)", 1);
        Assert.NotNull(e);
        Assert.Null(BeyondAtcLogParser.ParseHotkeys(e!));
    }

    [Fact]
    public void CpdlcEndpointsAreReadFromTheStartupLines()
    {
        var e = BeyondAtcLogParser.ParseLine(
            "[CPDLC] http endpoint listening on http://localhost:57698/ (127.0.0.1 + [::1]) -> http://127.0.0.1:57700");
        Assert.NotNull(e);

        var endpoints = BeyondAtcLogParser.ParseCpdlcEndpoints(e!);
        Assert.Contains("http://localhost:57698/", endpoints);
        Assert.Contains("http://127.0.0.1:57700", endpoints);
    }

    [Fact]
    public void RunwayAllocationIsRead()
    {
        var e = BeyondAtcLogParser.ParseLine(
            "[RunwayAlloc] ASXGS planned= validRunways=[24L] branch=distanceBusyness result=24L cost=590 cachedEP=True");
        Assert.NotNull(e);

        var allocation = BeyondAtcLogParser.ParseRunwayAllocation(e!);
        Assert.NotNull(allocation);
        Assert.Equal("ASXGS", allocation!.Value.Callsign);
        Assert.Equal("24L", allocation.Value.Runway);
    }

    [Fact]
    public void AircraftIdentityIsParsed()
    {
        var e = BeyondAtcLogParser.ParseLine(
            "[MsfsImport] Aircraft identity: icaoType=C172, id=ASXGS, airframe=Cessna 172");
        Assert.NotNull(e);

        var identity = BeyondAtcLogParser.ParseAircraftIdentity(e!);
        Assert.NotNull(identity);
        Assert.Equal("C172", identity!.Value.IcaoType);
        Assert.Equal("ASXGS", identity.Value.Ident);
        Assert.Equal("Cessna 172", identity.Value.Airframe);
    }

    [Fact]
    public void UnknownFrequencyComplaintsAreParsed()
    {
        var e = BeyondAtcLogParser.ParseLine("[Comms] 124.850 not found. Aircraft: Player");
        Assert.NotNull(e);
        Assert.Equal(124.85, BeyondAtcLogParser.ParseUnknownFrequency(e!));
    }

    [Fact]
    public void EngineNoiseIsIgnored()
    {
        Assert.Null(BeyondAtcLogParser.ParseLine("GfxDevice: creating device client; threaded=1"));
        Assert.Null(BeyondAtcLogParser.ParseLine("[Vulkan init] extensions: count=20"));
        Assert.Null(BeyondAtcLogParser.ParseLine(null));
        Assert.Null(BeyondAtcLogParser.ParseLine(""));
    }

    [Fact]
    public void SessionBoundariesAreRecognised()
    {
        Assert.True(BeyondAtcLogParser.IsSessionBoundary(BeyondAtcLogParser.ParseLine(
            "[Simconnect] Simulator has closed - disconnecting and ending the session")!));

        Assert.True(BeyondAtcLogParser.IsSessionBoundary(BeyondAtcLogParser.ParseLine(
            "[CPDLC] Server stopped")!));

        Assert.False(BeyondAtcLogParser.IsSessionBoundary(BeyondAtcLogParser.ParseLine(
            "[Traffic] Settings: Parked-5 Departure-5 Arrival-5 Enroute-5")!));
    }
}

/// <summary>
/// The log tail follows a file that the running application rewrites, so the tests use
/// real files: appending, a partial line split across writes, and the truncation that
/// happens when a new flight starts.
/// </summary>
public class BeyondAtcLogTailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batc-tail-" + Guid.NewGuid().ToString("n"));
    private readonly List<BeyondAtcLogEvent> _events = new();

    public BeyondAtcLogTailTests() => Directory.CreateDirectory(_root);

    private string LogPath => Path.Combine(_root, "Player.log");

    private BeyondAtcLogTail Tail()
    {
        var tail = new BeyondAtcLogTail(LogPath, _events.Add);
        tail.Poll(); // position at the start of a fresh file
        return tail;
    }

    [Fact]
    public void AppendedLinesAreEmittedOnce()
    {
        File.WriteAllText(LogPath, "[ATIS] Refreshing all metars.\n");
        var tail = Tail();
        _events.Clear();

        File.AppendAllText(LogPath, "[Comms] 124.850 not found. Aircraft: Player\n");
        tail.Poll();

        var e = Assert.Single(_events);
        Assert.Equal("Comms", e.Tag);

        tail.Poll();
        Assert.Single(_events); // nothing new, nothing repeated
    }

    [Fact]
    public void PartialLinesAreBufferedUntilTheTerminatorArrives()
    {
        File.WriteAllText(LogPath, "");
        var tail = Tail();

        File.AppendAllText(LogPath, "[RunwayAlloc] ASXGS ");
        tail.Poll();
        Assert.Empty(_events);

        File.AppendAllText(LogPath, "result=24L\n");
        tail.Poll();
        var e = Assert.Single(_events);
        Assert.Equal("RunwayAlloc", e.Tag);
        Assert.Contains("result=24L", e.Text);
    }

    [Fact]
    public void RewrittenFileRestartsFromTheBeginning()
    {
        File.WriteAllText(LogPath, "[Comms] 124.850 not found. Aircraft: Player\n");
        var tail = Tail();
        _events.Clear();

        // A new flight truncates and rewrites the file.
        File.WriteAllText(LogPath, "[CPDLC] up\n");
        tail.Poll();

        var e = Assert.Single(_events);
        Assert.Equal("CPDLC", e.Tag);
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        var tail = Tail();
        tail.Poll();
        Assert.Empty(_events);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void LineNumbersAdvanceAcrossPolls()
    {
        File.WriteAllText(LogPath, "[Traffic] Settings: Parked-5\n");
        var tail = Tail();

        File.AppendAllText(LogPath, "[Traffic] Settings: Departure-5\n");
        tail.Poll();

        Assert.Equal(2, tail.LastLineNumber);
    }

    [Fact]
    public void ANonAsciiByteDoesNotRepeatTheLineAfterIt()
    {
        // The offset used to advance by the character count of what was read rather than by
        // the bytes the file is seeked past, so the degree signs and accented airport names
        // a log carries desynchronised the two and the region after them was read twice.
        File.WriteAllText(LogPath, "");
        var tail = Tail();

        File.AppendAllText(LogPath, "[Comms] 120.500 now used, Café Ñoño, aéroport\n");
        tail.Poll();

        var first = Assert.Single(_events);

        tail.Poll();
        Assert.Single(_events);
        Assert.Same(first, _events[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>
/// The ATC menu of the simulator is only addressable by position and cannot be read, so
/// the request engine works by elimination: watch the ATC SimVars first, then press lines
/// in display order, never press a line that was found to do the wrong thing, and stop
/// rather than chain unintended ATC actions. These tests drive it with a fake menu and a
/// fake clock.
/// </summary>
public class AtcRequestEngineTests
{
    private sealed class FakeMenu : IAtcMenu
    {
        public List<string> Actions { get; } = new();
        public int LastIndex { get; private set; } = -1;

        public void Open() => Actions.Add("open");
        public void Close() => Actions.Add("close");
        public void ToggleWindow() => Actions.Add("toggle");

        public void Select(int item)
        {
            LastIndex = item;
            Actions.Add($"select:{item}");
        }

        public void TuneCom1(double frequencyMHz) => Actions.Add($"tune:{frequencyMHz:0.000}");
        public void SetTransponder(string code) => Actions.Add($"xpndr:{code}");

        public int CountSelections(int item) => Actions.Count(a => a == $"select:{item}");

        /// <summary>Forgets what was pressed, so the next request starts from a clean menu.</summary>
        public void Reset() => LastIndex = -1;
    }

    private static AtcRequestOptions Options(int maxEntry = 10, int maxEntriesProbed = 5,
        double totalTimeoutSeconds = 20) => new()
        {
            MenuSettle = TimeSpan.FromMilliseconds(400),
            EntryTimeout = TimeSpan.FromMilliseconds(1500),
            TotalTimeout = TimeSpan.FromSeconds(totalTimeoutSeconds),
            MaxEntry = maxEntry,
            MaxEntriesProbed = maxEntriesProbed,
            GracePeriod = TimeSpan.FromSeconds(2)
        };

    private static void RunUntilIdle(AtcRequestEngine engine, ref DateTime now, int stepMs = 100)
    {
        while (engine.IsBusy)
        {
            now = now.AddMilliseconds(stepMs);
            engine.Tick();
        }
    }

    [Fact]
    public void OpensTheMenuThenWalksTheLinesInDisplayOrder()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());

        // Line 2 is the one that grants the takeoff clearance on this fake menu.
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => menu.LastIndex == 2, "takeoff clearance"));

        // The menu settles, then the controller is given its chance.
        Assert.Equal(AtcRequestStatus.OpeningMenu, engine.Status);
        now = now.AddMilliseconds(500);
        engine.Tick();
        Assert.Equal(AtcRequestStatus.WaitingForGrant, engine.Status);
        Assert.Equal(-1, engine.ProbedEntry);
        Assert.Empty(menu.Actions.Where(a => a.StartsWith("select:")));

        // The grace period expires, so the first line is pressed: ATC_MENU_1, not 0.
        now = now.AddMilliseconds(2100);
        engine.Tick();
        Assert.Equal(AtcRequestStatus.Probing, engine.Status);
        Assert.Equal(1, engine.ProbedEntry);

        // Line 1 does not work, so the next one is tried.
        now = now.AddMilliseconds(1600);
        engine.Tick();
        Assert.Equal(2, engine.ProbedEntry);

        // Line 2 works: the request completes and the menu is closed again.
        engine.Tick();
        Assert.Equal(AtcRequestStatus.Accepted, engine.Status);
        Assert.Null(engine.ActiveKind);
        Assert.False(engine.IsBusy);

        Assert.Equal(new[] { "open", "select:1", "select:2", "close" }, menu.Actions);
        Assert.Equal(2, engine.PressedEntryCount);
    }

    [Fact]
    public void NoLineWorksUntilTheTimeoutIsReported()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options(maxEntry: 3));

        Assert.True(engine.Begin(AtcRequestKind.Landing, () => false, "landing clearance"));

        RunUntilIdle(engine, ref now);

        Assert.Equal(AtcRequestStatus.Failed, engine.Status);
        Assert.Contains("landing clearance", engine.LastFailure);
        // Only lines 1-3 are in scope, and each is pressed once.
        Assert.Equal(3, menu.Actions.Count(a => a.StartsWith("select:")));
        Assert.Equal(3, engine.PressedEntryCount);
    }

    [Fact]
    public void OnlyLinesInsideThePressBudgetArePressed()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null,
            Options(maxEntriesProbed: 3, totalTimeoutSeconds: 60));

        Assert.True(engine.Begin(AtcRequestKind.Landing, () => false, "landing clearance"));

        RunUntilIdle(engine, ref now);

        Assert.Equal(AtcRequestStatus.Failed, engine.Status);
        Assert.Equal(3, engine.PressedEntryCount);
        Assert.Equal(new[] { "select:1", "select:2", "select:3" },
            menu.Actions.Where(a => a.StartsWith("select:")));
    }

    [Fact]
    public void TheTenthLineIsFiredAsTheZeroEvent()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null,
            Options(maxEntriesProbed: 10, totalTimeoutSeconds: 120));

        Assert.True(engine.Begin(AtcRequestKind.Landing, () => false, "landing clearance"));

        RunUntilIdle(engine, ref now);

        Assert.Equal(10, engine.PressedEntryCount);
        Assert.Equal(1, menu.CountSelections(0));   // only line 10 uses ATC_MENU_0
        Assert.Equal(1, menu.CountSelections(9));
        Assert.Equal(10, menu.Actions.Count(a => a.StartsWith("select:")));
    }

    [Fact]
    public void TheControllerIsGivenAChanceToGrantBeforeAnythingIsPressed()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());

        var granted = false;
        Assert.True(engine.Begin(AtcRequestKind.IFRClearance, () => granted, "IFR clearance"));

        now = now.AddMilliseconds(500);
        engine.Tick();

        // The controller grants it on its own: no menu entry is pressed at all.
        granted = true;
        now = now.AddMilliseconds(1000);
        engine.Tick();

        Assert.Equal(AtcRequestStatus.Accepted, engine.Status);
        Assert.False(engine.IsBusy);
        Assert.Equal(0, engine.PressedEntryCount);
        Assert.Equal(-1, engine.ProbedEntry);
        Assert.Equal(new[] { "open", "close" }, menu.Actions);
    }

    [Fact]
    public void ALineThatChangesTheWrongThingIsNeverPressedAgainForThatCircumstance()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());

        // Line 3 exists on this fake menu but does something else: it changes the ATC
        // state without granting the takeoff clearance.
        Func<string> signature = () => menu.LastIndex == 3 ? "atis|assigned|24L" : "cleared for taxi|24L";

        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => false, "takeoff clearance",
            "takeoff|TaxiOut|TWR|ASXGS", signature));
        RunUntilIdle(engine, ref now);

        Assert.Equal(AtcRequestStatus.Failed, engine.Status);
        Assert.Contains(3, engine.ForbiddenEntries("takeoff|TaxiOut|TWR|ASXGS"));
        Assert.Contains("line 3", engine.LastFailure);
        Assert.Equal(1, menu.CountSelections(3));
        // The probe stopped instead of continuing into a menu it no longer understood.
        Assert.True(engine.PressedEntryCount < 5);

        // A second request in the same circumstance must not touch that line again.
        menu.Actions.Clear();
        menu.Reset();
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => false, "takeoff clearance",
            "takeoff|TaxiOut|TWR|ASXGS", signature));
        RunUntilIdle(engine, ref now);

        Assert.Equal(0, menu.CountSelections(3));
        Assert.True(engine.PressedEntryCount >= 1);
        Assert.Contains(3, engine.ForbiddenEntries("takeoff|TaxiOut|TWR|ASXGS"));
    }

    [Fact]
    public void AForgottenLineCanBeTriedAgain()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());
        Func<string> signature = () => menu.LastIndex == 2 ? "elsewhere" : "here";

        engine.Begin(AtcRequestKind.Taxi, () => false, "taxi", "ctx", signature);
        RunUntilIdle(engine, ref now);
        Assert.Contains(2, engine.ForbiddenEntries("ctx"));

        engine.Forget("ctx");

        Assert.Empty(engine.ForbiddenEntries("ctx"));
        menu.Actions.Clear();
        menu.Reset();
        engine.Begin(AtcRequestKind.Taxi, () => false, "taxi", "ctx", signature);
        RunUntilIdle(engine, ref now);
        Assert.Equal(1, menu.CountSelections(2));
    }

    [Fact]
    public void AChangeOfCircumstanceAbortsTheProbeWithoutBlamingAnEntry()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());

        var movedOn = false;
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => false, "takeoff clearance",
            "takeoff|TaxiOut|TWR", () => "unchanged", () => movedOn));

        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();
        Assert.Equal(AtcRequestStatus.Probing, engine.Status);

        // The tower handed the aircraft off while the menu was open.
        movedOn = true;
        now = now.AddMilliseconds(100);
        engine.Tick();

        Assert.Equal(AtcRequestStatus.Failed, engine.Status);
        Assert.Contains("circumstance changed", engine.LastFailure);
        Assert.False(engine.IsBusy);
        Assert.Empty(engine.ForbiddenEntries("takeoff|TaxiOut|TWR"));
        // It stopped on the entry that had been pressed, without walking the rest.
        Assert.Equal(1, engine.PressedEntryCount);
    }

    [Fact]
    public void TheLineThatWorkedBeforeIsTriedFirst()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var engine = new AtcRequestEngine(menu, () => now, null, Options());

        // First time round: lines 1 and 2 fail, line 3 grants it.
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => menu.LastIndex == 3, "takeoff clearance", "ctx"));
        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();
        now = now.AddMilliseconds(1600);
        engine.Tick();
        now = now.AddMilliseconds(1600);
        engine.Tick();
        engine.Tick();
        Assert.Equal(AtcRequestStatus.Accepted, engine.Status);
        Assert.Equal(3, engine.LearnedEntry("ctx"));

        // Second time round: line 3 is tried first, with no preamble. The clock the
        // engine reads is the same variable, so time keeps moving forward.
        menu.Actions.Clear();
        menu.Reset();
        now = now.AddSeconds(30);
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => menu.LastIndex == 3, "takeoff clearance", "ctx"));
        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();

        // The next tick sees the clearance and closes the menu.
        engine.Tick();
        Assert.Equal(3, engine.ProbedEntry);
        Assert.Equal(new[] { "open", "select:3", "close" }, menu.Actions);
    }

    [Fact]
    public void OnlyOneRequestCanBeInFlight()
    {
        var menu = new FakeMenu();
        var engine = new AtcRequestEngine(menu, () => DateTime.UtcNow);

        Assert.True(engine.Begin(AtcRequestKind.Taxi, () => true, "taxi"));
        Assert.False(engine.Begin(AtcRequestKind.Takeoff, () => true, "takeoff"));
    }

    [Fact]
    public void CancelStopsWithoutReportingSuccess()
    {
        var menu = new FakeMenu();
        var engine = new AtcRequestEngine(menu, () => DateTime.UtcNow);

        engine.Begin(AtcRequestKind.Taxi, () => false, "taxi");
        engine.Cancel();

        Assert.False(engine.IsBusy);
        Assert.Contains("close", menu.Actions);
        // Abandoning a request must not report it as granted: the status used to become
        // Accepted, so an abandoned request showed up as one the controller had granted.
        Assert.Equal(AtcRequestStatus.Cancelled, engine.Status);
    }

    [Fact]
    public void TransponderCodesAreEncodedAsBcd()
    {
        // 4321 travels as 0x4321 in the XPNDR_SET event payload.
        Assert.Equal(0x4321, SimConnectAtcMenu.TransponderBcd("4321"));

        // 8 and 9 are not transponder digits, and the code must be four digits.
        Assert.Null(SimConnectAtcMenu.TransponderBcd("4391"));
        Assert.Null(SimConnectAtcMenu.TransponderBcd("432"));
        Assert.Null(SimConnectAtcMenu.TransponderBcd(""));
        Assert.Null(SimConnectAtcMenu.TransponderBcd(null));
    }

    [Fact]
    public void TuningRejectsOutOfBandFrequencies()
    {
        Assert.True(SimConnectAtcMenu.IsComFrequency(124.85));
        Assert.True(SimConnectAtcMenu.IsComFrequency(118.005));
        Assert.False(SimConnectAtcMenu.IsComFrequency(0));
        Assert.False(SimConnectAtcMenu.IsComFrequency(200));
        Assert.False(SimConnectAtcMenu.IsComFrequency(-118));
    }
}

/// <summary>
/// The hub joins the two back-ends: it finds BeyondATC from its log, follows the runway
/// it allocates, and reads the action key it imported into its own preferences.
/// </summary>
public class AtcCommunicationHubTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batc-hub-" + Guid.NewGuid().ToString("n"));

    public AtcCommunicationHubTests() => Directory.CreateDirectory(_root);

    private static void WriteLog(string root, string content) =>
        File.WriteAllText(Path.Combine(root, "Player.log"), content);

    private AtcCommunicationHub Hub(AtcSource preferred, List<string> log)
    {
        var hub = new AtcCommunicationHub(new AircraftController(),
            new AtcOptions { PreferredSource = preferred, LogDirectoryOverride = _root },
            log.Add);
        hub.Start();
        return hub;
    }

    private (AtcCommunicationHub hub, AircraftController aircraft) NativeHub(Action<AircraftState>? configure = null)
    {
        var aircraft = new AircraftController();
        configure?.Invoke(aircraft.State);

        var hub = new AtcCommunicationHub(aircraft,
            new AtcOptions { PreferredSource = AtcSource.Native, LogDirectoryOverride = _root },
            _ => { });
        hub.Start();
        return (hub, aircraft);
    }

    [Fact]
    public void BeyondAtcFactsAreReadFromItsLog()
    {
        WriteLog(_root, string.Join('\n', new[]
        {
            "[Bindings] Hotkeys imported to PlayerPrefs: [0, 32, 0, 122, 0] (defaults)",
            "[CPDLC] http endpoint listening on http://localhost:57698/ (127.0.0.1 + [::1]) -> http://127.0.0.1:57700",
            "[MsfsImport] Aircraft identity: icaoType=C172, id=ASXGS, airframe=Cessna 172",
            "[RunwayAlloc] ASXGS planned= validRunways=[24L] branch=distanceBusyness result=24L cost=590 cachedEP=True",
            ""
        }));

        using var hub = Hub(AtcSource.BeyondAtc, new List<string>());
        var instructions = new List<AtcInstruction>();
        hub.OnInstructionParsed += instructions.Add;

        hub.Tick(new AircraftState());

        Assert.Equal(AtcSource.BeyondAtc, hub.Source);
        Assert.Equal("ASXGS", hub.BeyondAtcCallsign);
        Assert.Equal("Cessna 172", hub.BeyondAtcAirframe);
        Assert.Equal("24L", hub.BeyondAtcDepartureRunway);
        Assert.Equal(new[] { 0, 32, 0, 122, 0 }, hub.BeyondAtcHotkeys);
        Assert.Contains("http://localhost:57698/", hub.Cpdlc.EffectiveEndpoints());

        var instruction = Assert.Single(instructions);
        Assert.Equal(AtcSource.BeyondAtc, instruction.Source);
        Assert.Contains("Runway 24L assigned for departure", instruction.RawText);
    }

    [Fact]
    public void ARequestWithoutAnActionKeyIsRefusedRatherThanGuessed()
    {
        WriteLog(_root, "[Bindings] Hotkeys imported to PlayerPrefs: [0, 32, 0, 0, 0] (defaults)\n");

        var log = new List<string>();
        using var hub = Hub(AtcSource.BeyondAtc, log);

        hub.Request(AtcRequestKind.Taxi, "ready to roll");

        Assert.False(hub.Requests.IsBusy);
        Assert.Contains(log, line => line.Contains("cannot request", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheActionKeyComesFromBeyondAtcsOwnPreferences()
    {
        // The log line carries the bindings BeyondATC imported; slot 3 is "Fire Action 1".
        WriteLog(_root, "[Bindings] Hotkeys imported to PlayerPrefs: [0, 32, 0, 122, 0] (defaults)\n");

        using var hub = Hub(AtcSource.BeyondAtc, new List<string>());
        hub.Tick(new AircraftState());

        // The binding is taken from BeyondATC's own preference list, so no key is
        // invented here.
        Assert.Equal(122, hub.Snapshot().ActionKeyVirtualKey);
    }

    [Fact]
    public void AnOldLogFallsBackToTheSimAtc()
    {
        WriteLog(_root, "[Traffic] Settings: Parked-5\n");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Player.log"), DateTime.UtcNow.AddDays(-1));

        using var hub = Hub(AtcSource.None, new List<string>());
        hub.Tick(new AircraftState());

        // No BeyondATC window is running and its log is from another day, so the
        // simulator's own ATC is the controller.
        Assert.Equal(AtcSource.Native, hub.Source);
    }

    [Fact]
    public void AFreshLogMakesBeyondAtcTheController()
    {
        WriteLog(_root, "[Traffic] Settings: Parked-5\n");

        using var hub = Hub(AtcSource.None, new List<string>());
        hub.Tick(new AircraftState());

        Assert.Equal(AtcSource.BeyondAtc, hub.Source);
    }

    [Fact]
    public void ManualTextIsParsedAndRaised()
    {
        using var hub = Hub(AtcSource.BeyondAtc, new List<string>());
        var instructions = new List<AtcInstruction>();
        hub.OnInstructionParsed += instructions.Add;

        var instruction = hub.ProcessText("ASXGS, cleared to land runway 25L");

        Assert.Contains(instructions, i => i == instruction);
        Assert.True(instruction.ClearedToLand);
        Assert.Equal("25L", instruction.Runway);
    }

    [Fact]
    public void AStationChangeBypassesTheAutoRequestCooldown()
    {
        var (hub, aircraft) = NativeHub(state =>
        {
            state.ATC.AirportIsTowered = true;
            state.ATC.CurrentFrequency = "124.850";
            state.ATC.StationType = "ACTIVE COM";
            state.ATC.StationIdent = "LISBON";
        });
        using var _ = hub;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());
        Assert.True(hub.Requests.IsBusy);

        hub.Requests.Cancel();
        aircraft.State.ATC.CurrentFrequency = "121.755";
        aircraft.State.ATC.StationType = "GND";
        aircraft.State.ATC.StationIdent = "LPPT";

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.True(hub.Requests.IsBusy);
        Assert.Equal(AtcRequestKind.Taxi, hub.Requests.ActiveKind);
    }

    [Fact]
    public void TheAutoRequestCooldownStillAppliesWhileTheContextIsUnchanged()
    {
        var (hub, aircraft) = NativeHub(state =>
        {
            state.ATC.AirportIsTowered = true;
            state.ATC.CurrentFrequency = "124.850";
            state.ATC.StationType = "ACTIVE COM";
            state.ATC.StationIdent = "LISBON";
        });
        using var _ = hub;

        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());
        Assert.True(hub.Requests.IsBusy);

        hub.Requests.Cancel();
        hub.UpdatePhaseRequests(FlightPhase.TaxiOut, aircraft.GetSnapshot());

        Assert.False(hub.Requests.IsBusy);
        Assert.Equal(AtcRequestStatus.Cancelled, hub.Requests.Status);
    }

    private static AircraftState Approaching() => new()
    {
        Altitude = 3000,
        AltitudeAGL = 1800
    };

    [Fact]
    public void TheLandingRequestIsRecordedBeforeItCanBeSent()
    {
        // A pilot asks at a point in the flight, not when the cooldown next expires, and
        // the pilot needs to know whether it has asked by the time it reaches the decision
        // height - where there is no time left to ask.
        using var hub = Hub(AtcSource.Native, new List<string>());

        var state = Approaching();
        state.ATC.ArrivalAirportIsTowered = true;
        hub.UpdatePhaseRequests(FlightPhase.Approach, state);

        Assert.True(hub.LandingClearanceRequested);
    }

    [Fact]
    public void AnUntoweredArrivalAsksForNothing()
    {
        using var hub = Hub(AtcSource.Native, new List<string>());

        var state = Approaching();
        state.ATC.ArrivalAirportIsTowered = false;
        hub.UpdatePhaseRequests(FlightPhase.Approach, state);

        Assert.False(hub.LandingClearanceRequested);
    }

    [Fact]
    public void TheLandingRequestIsClearedOffTheApproach()
    {
        // A second approach has to ask afresh rather than living with the answer to the first.
        using var hub = Hub(AtcSource.Native, new List<string>());

        var state = Approaching();
        state.ATC.ArrivalAirportIsTowered = true;
        hub.UpdatePhaseRequests(FlightPhase.Approach, state);
        Assert.True(hub.LandingClearanceRequested);

        hub.UpdatePhaseRequests(FlightPhase.Cruise, new AircraftState());
        Assert.False(hub.LandingClearanceRequested);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>
/// String datums arrive as fixed-width, NUL-padded text rather than as 8-byte numbers,
/// so the decoder is pinned against a payload laid out the way SimConnect builds it.
/// </summary>
public class SimConnectStringPayloadTests
{
    [Fact]
    public void StringsAreTrimmedAtTheirTerminator()
    {
        var payload = new byte[64];
        WriteField(payload, 0, 32, "24L");
        WriteField(payload, 32, 32, "Barcelona Approach");

        var values = SimConnectManager.DecodeStrings(
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(payload, 0),
            payload.Length, new[] { 32, 32 });

        Assert.Equal(new[] { "24L", "Barcelona Approach" }, values);
    }

    [Fact]
    public void ShortPayloadsYieldEmptyFieldsRatherThanThrowing()
    {
        var payload = new byte[8];
        var values = SimConnectManager.DecodeStrings(
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(payload, 0),
            8, new[] { 32, 32 });

        Assert.Equal(new[] { "", "" }, values);
    }

    [Fact]
    public void EmptyFieldStaysEmpty()
    {
        var payload = new byte[32];
        var values = SimConnectManager.DecodeStrings(
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(payload, 0),
            32, new[] { 32 });

        Assert.Equal("", values[0]);
    }

    private static void WriteField(byte[] payload, int offset, int width, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, payload, offset, Math.Min(bytes.Length, width));
    }
}
