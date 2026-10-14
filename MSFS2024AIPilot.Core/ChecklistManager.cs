using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

public sealed class ChecklistStatus
{
    public string Source = "None";
    public string Format = "None";
    public bool Enabled;
    public bool Loaded;
    public string Step = "";
    public int Index;
    public int Count;
    public string Item = "";
    public string Expectation = "";
    public string ItemState = "idle";
    public string Note = "";
    public List<string> Completed = new();
    public List<string> Lists = new();
}

/// <summary>
/// Drives the aircraft's own checklists from the flight phases the AI pilot
/// executes. Each mapped checklist step is run once, in item order, through
/// <see cref="ChecklistExecutor"/>; the manager only orchestrates and
/// reports, all side effects go through the executor.
/// </summary>
public sealed class ChecklistManager
{
    private readonly IChecklistHost _host;
    private readonly ChecklistExecutor _executor;
    private readonly Func<ChecklistEnvironment> _environment;

    private AircraftChecklistSet _set = AircraftChecklistSet.Empty();
    private readonly HashSet<ChecklistStep> _completed = new();
    private readonly List<ChecklistStep> _queue = new();
    private string _activeStepName = "";
    private ChecklistStep _activeStep = ChecklistStep.PREFLIGHT_GATE;
    private int _activeTotal;
    private int _index;
    /// <summary>Executable items finished in the running step, so "i/n" counts what runs.</summary>
    private int _executablesDone;

    public AircraftChecklistSet Set => _set;
    public ChecklistExecutor Executor => _executor;
    public bool Enabled { get; set; } = true;

    public event Action<string>? OnLog;
    public event Action<ChecklistItem, string>? OnItemDone;

    public ChecklistManager(IChecklistHost host, Func<ChecklistEnvironment>? environment = null)
    {
        _host = host;
        _environment = environment ?? (() => new ChecklistEnvironment());
        _executor = new ChecklistExecutor(host, null, _environment);
        _executor.OnStatus += s => { };
    }

    public bool IsLoaded => _set.IsLoaded && Enabled;

    /// <summary>
    /// Discards anything in progress and takes a new set. A running step is cancelled rather
    /// than left mid-item: the aircraft it is now flying is not the one the item list was
    /// built for.
    /// </summary>
    public void Load(AircraftChecklistSet set)
    {
        _set = set;
        _completed.Clear();
        _queue.Clear();
        _index = 0;
        _activeStepName = "";
        _activeTotal = 0;
        _executor.Cancel();
    }

    public void Reset()
    {
        _completed.Clear();
        _queue.Clear();
        _index = 0;
        _activeStepName = "";
        _activeTotal = 0;
        _executor.Cancel();
    }

    public static IReadOnlyList<ChecklistStep> StepsFor(FlightPhase phase) => phase switch
    {
        // The gate checklist covers the whole stand-hold portion: the parked
        // phase and the startup phase are the same physical checklist step.
        FlightPhase.Preflight or FlightPhase.Startup => new[]
        {
            ChecklistStep.PREFLIGHT_PUSHBACK,
            ChecklistStep.PREFLIGHT_GATE
        },

        // PREFLIGHT_PUSHBACK is also reachable from the taxi-out phase, for an aircraft
        // whose own list puts the pushback items there: a mapped step with no phase that
        // ever selects it is never run at all.
        FlightPhase.TaxiOut => new[] { ChecklistStep.PREFLIGHT_TAXI_OUT },
        FlightPhase.Takeoff => new[] { ChecklistStep.FLIGHT_RUNWAY },
        FlightPhase.InitialClimb => new[] { ChecklistStep.FLIGHT_TAKEOFF, ChecklistStep.FLIGHT_CLIMB },
        FlightPhase.Climb => new[] { ChecklistStep.FLIGHT_CLIMB },
        FlightPhase.Cruise => new[] { ChecklistStep.FLIGHT_CRUISE },
        FlightPhase.Descent => new[] { ChecklistStep.FLIGHT_DESCENT },
        // The VFR approach runs where the base approach does: it is the same physical
        // segment, flown under different rules, and a step no phase selects is dead.
        FlightPhase.Approach => new[]
        {
            ChecklistStep.LANDING_APPROACH,
            ChecklistStep.LANDING_APPROACH_VFR,
            ChecklistStep.LANDING_APPROACH_IFR
        },
        FlightPhase.FinalApproach => new[]
        {
            ChecklistStep.LANDING_TOUCHDOWN,
            ChecklistStep.LANDING_FINAL
        },
        FlightPhase.Landing or FlightPhase.Rollout => new[]
        {
            ChecklistStep.LANDING_GROUNDROLL,
            ChecklistStep.LANDING_TOUCHDOWN
        },
        FlightPhase.TaxiIn => new[] { ChecklistStep.LANDING_TAXI_IN },
        FlightPhase.Shutdown => new[] { ChecklistStep.LANDING_GATE },
        _ => Array.Empty<ChecklistStep>()
    };

    public void Tick(FlightPhase phase, double dt)
    {
        if (!Enabled || !_set.IsLoaded) return;

        // A running item always advances, even when the current phase maps to no
        // checklist step: a manually run step, or a step started before a phase
        // change, must finish rather than stall.
        if (_executor.Busy)
        {
            var current = _executor.Current;
            var outcome = _executor.Tick(dt);
            if (outcome == ItemOutcome.Running) return;

            _index++;
            if (outcome == ItemOutcome.Completed && current != null)
            {
                // Counted on the executable items only: a step that carries headers
                // otherwise reports "4/2" - the numerator counted every item, the
                // denominator only the ones that run.
                _executablesDone += current.Kind == ChecklistItemKind.Header ? 0 : 1;
                OnItemDone?.Invoke(current, _executor.Status);
                OnLog?.Invoke($"[Checklist] {_activeStepName} {_executablesDone}/{_activeTotal} - {current.Display} :: {_executor.Status}");
            }
            StartNextItem();
            return;
        }

        // Keep only un-finished steps the current phase wants. Approach also
        // runs the IFR variant when the base approach step has no items.
        var wanted = StepsFor(phase).Where(s =>
            s is ChecklistStep.LANDING_APPROACH_IFR
                ? !_completed.Contains(ChecklistStep.LANDING_APPROACH) && _set.Find(s)?.ExecutableCount > 0
                : !_completed.Contains(s))
            .ToList();

        if (wanted.Count == 0)
        {
            if (_queue.Count > 0) _queue.Clear();
            _index = 0;
            _activeStepName = "";
            return;
        }

        // Idle: (re)fill the queue and start the first item.
        if (_queue.Count == 0) _queue.AddRange(wanted);
        StartNextItem();
    }

    private void StartNextItem()
    {
        while (_queue.Count > 0)
        {
            var step = _queue[0];
            var list = _set.Find(step);

            // A step this set does not have cannot be run, and dropping it without marking
            // it complete left it in the wanted set forever: StepsFor filtered on
            // _completed, so the step was re-queued on every frame, the queue was never
            // empty, and a phase such as Startup waited for a gate step that did not
            // exist - the aircraft never left the stand.
            if (list == null)
            {
                _queue.RemoveAt(0);
                _index = 0;
                _completed.Add(step);
                continue;
            }

            if (_index >= list.Items.Count)
            {
                _queue.RemoveAt(0);
                _index = 0;
                _executablesDone = 0;
                _completed.Add(step);
                var items = list.ExecutableCount;
                var src = _set.IsAircraftSpecific ? "aircraft checklist" : "generic checklist";
                OnLog?.Invoke($"[Checklist] {step} complete ({items} items, {src})");
                continue;
            }

            var item = list.Items[_index];
            _activeStep = step;
            _activeStepName = step.ToString();
            _activeTotal = list.ExecutableCount;
            _executablesDone = list.Items.Take(_index).Count(i => i.Kind != ChecklistItemKind.Header);
            _executor.Begin(item);
            if (_executor.Busy) return;

            // Header, or a guard-false block item: skipped without report noise.
            _index++;
        }
        _activeStepName = "";
    }

    public void RunStep(ChecklistStep step)
    {
        if (!_set.IsLoaded || _set.Find(step) == null) return;
        _executor.Cancel();
        _queue.Clear();
        _queue.Add(step);
        _index = 0;
        _completed.Remove(step);
        StartNextItem();
    }

    public void SkipCurrent()
    {
        var item = _executor.Current;
        _executor.Cancel();
        if (item != null) OnLog?.Invoke($"[Checklist] item skipped by user: {item.Display}");
        _index++;
        StartNextItem();
    }

    public bool StepComplete(ChecklistStep step) => _completed.Contains(step);
    public bool GateComplete => _completed.Contains(ChecklistStep.PREFLIGHT_GATE);
    public bool TaxiOutComplete => _completed.Contains(ChecklistStep.PREFLIGHT_TAXI_OUT);
    public bool RunwayComplete => _completed.Contains(ChecklistStep.FLIGHT_RUNWAY);
    public bool ReadyForTakeoff => GateComplete && RunwayComplete;

    public ChecklistStatus Status()
    {
        var status = new ChecklistStatus
        {
            Source = _set.IsLoaded ? (_set.IsAircraftSpecific ? "Aircraft" : "Generic") : "None",
            Format = _set.Format.ToString(),
            Enabled = Enabled,
            Loaded = _set.IsLoaded
        };
        if (_set.IsLoaded)
        {
            status.Lists = _set.Lists.Select(l => $"{l.Step}: {l.ExecutableCount}").ToList();

            if (_executor.Busy && _executor.Current != null)
            {
                status.Step = _activeStepName;
                var item = _executor.Current;
                status.Item = item.Subject;
                status.Expectation = item.Expectation;
                status.ItemState = _executor.Status;
                var list = _set.Find(_activeStep);
                // Count only executable items so "i/n" stays meaningful when the
                // step also carries headers or notes (they run but are not counted).
                status.Index = list != null
                    ? list.Items.Take(list.Items.IndexOf(item) + 1).Count(i => i.Kind != ChecklistItemKind.Header)
                    : 0;
                status.Count = list?.ExecutableCount ?? 0;
            }
            else if (_activeStepName.Length > 0)
            {
                status.Step = _activeStepName;
                status.ItemState = _executor.Status;
            }
        }
        status.Completed = _completed.Select(Enum.GetName).Where(n => n != null).Select(n => n!).ToList();
        return status;
    }
}
