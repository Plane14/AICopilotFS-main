using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// The simulator-facing surface the executor needs. Kept as an interface so
/// the engine is unit-testable without a live SimConnect connection.
/// </summary>
public interface IChecklistHost
{
    void FireEvent(string eventName, double data);
    uint Subscribe(string[] names, string[] units, Action<double[]> onData);
    void Unsubscribe(uint handle);
}

public sealed class ChecklistExecutorOptions
{
    public double FireIntervalSeconds = 0.4;
    public double ItemTimeoutSeconds = 150;
    public int MaxFiresPerItem = 60;
    public double ManualSettleSeconds = 1.5;
    public double MinDwellSeconds = 0.75;
    public double WaitForUserTimeoutSeconds = 90;
}

public enum ItemOutcome
{
    Running,
    Completed,
    Skipped
}

/// <summary>
/// Executes one <see cref="ChecklistItem"/> at a time: fires the checkpoint's
/// copilot actions while its test conditions are unmet, then verifies the
/// simvars, honouring durations and special actions. Never blocks: progress
/// happens through <see cref="Tick"/>.
/// </summary>
public sealed class ChecklistExecutor
{
    private readonly IChecklistHost _host;
    private readonly ChecklistExecutorOptions _options;
    private readonly Func<ChecklistEnvironment> _environment;

    private ChecklistItem? _item;
    private double _elapsed;
    private double _satisfiedFor;
    private double _sinceFire;
    private double _dwell;
    private int _fires;
    private int _fireCycles;
    private double _holdElapsed;
    private bool _firedOnce;
    private uint _subscription;
    private string[] _simVars = Array.Empty<string>();
    private double[] _values = Array.Empty<double>();
    private bool _needsData;
    private bool _dataSeen;
    /// <summary>Event ids already fired for this item, so a toggle is never fired twice.</summary>
    private readonly HashSet<string> _firedEvents = new(StringComparer.OrdinalIgnoreCase);
    private bool _testValueActionsFired;
    /// <summary>
    /// True while the item's block guard is waiting for the data it needs. A guard is a
    /// test, and every test is judged on real readings.
    /// </summary>
    private bool _guardPending;
    private string _status = "";

    public ChecklistItem? Current => _item;
    public string Status => _status;

    public event Action<string>? OnStatus;

    public ChecklistExecutor(IChecklistHost host, ChecklistExecutorOptions? options = null,
        Func<ChecklistEnvironment>? environment = null)
    {
        _host = host;
        _options = options ?? new ChecklistExecutorOptions();
        _environment = environment ?? (() => new ChecklistEnvironment());
    }

    public bool Busy => _item != null;

    public void Begin(ChecklistItem item)
    {
        Release();
        _item = item;
        _elapsed = 0;
        _satisfiedFor = 0;
        _sinceFire = 0;
        _dwell = _options.MinDwellSeconds;
        _fires = 0;
        _fireCycles = 0;
        _holdElapsed = 0;
        _firedOnce = false;
        _testValueActionsFired = false;
        _firedEvents.Clear();
        _guardPending = item.StartGuard != null;
        _status = "start";

        // Re-resolve text bindings against live conditions, not once at parse time:
        // "AS REQUIRED" items follow night and icing, the transponder item follows
        // the assigned code, and a lever SET event follows the engine count. Anything
        // the 2024-format parser built is left alone.
        if (item.TextBinding is SwitchCommand)
        {
            var resolved = ChecklistTextResolver.Resolve(item.Subject, item.Expectation, _environment());
            item.Tests.Clear();
            item.Actions.Clear();
            if (resolved.VerifySimVar != null)
                item.Tests.Add(ConditionNode.Verify(resolved.VerifySimVar, resolved.VerifyUnits, resolved.ExpectOn));
            foreach (var (name, data) in resolved.Events)
                item.Actions.Add(new ChecklistAction
                {
                    EventId = name,
                    EventParam = Math.Abs(data - 1d) < 0.001 ? null : data,
                    Condition = ActionCondition.TestValueFalse
                });
            // Keep the kind in step with what the binding can actually do, the way
            // the parser's own rules would have classified it.
            if (item.Actions.Count > 0) item.Kind = ChecklistItemKind.Set;
            else if (item.Tests.Count > 0) item.Kind = ChecklistItemKind.Check;
            else item.Kind = ChecklistItemKind.Manual;
        }

        if (item.Kind == ChecklistItemKind.Header)
        {
            Release();
            _status = "info";
            _item = null;
            return;
        }

        // The block guard is deferred to Tick (see _guardPending): judging it here, after
        // Release() has emptied the simvars, compared nothing but zeros.
        Subscribe(item);
    }

    /// <summary>
    /// Advances the item by <paramref name="dt"/> seconds. One item commands, waits for its
    /// own condition to be met, verifies it and then completes.
    /// </summary>
    public ItemOutcome Tick(double dt)
    {
        if (_item == null) return ItemOutcome.Completed;
        _elapsed += dt;
        _sinceFire += dt;

        var forced = _item.Actions.Any(a => a.Special == SpecialActionType.ForceValidate);
        var holdSeconds = _item.Actions
            .Where(a => a.Special == SpecialActionType.WaitForDuration)
            .Sum(a => a.Value ?? 0);

        if (_item.Kind == ChecklistItemKind.Manual && !forced)
        {
            if (_elapsed >= _options.ManualSettleSeconds) return Done("manual check (not automatable)");
            _status = "manual check";
            return ItemOutcome.Running;
        }

        // The block guard is a test like any other, so it may not be judged until the
        // simulator has answered for its simvars: a subscribed variable reads 0 until its
        // first feed, and a guard of "N1 equal 0" was therefore satisfied for every
        // turbine, while a real guard was skipped for lack of data.
        if (_guardPending)
        {
            if (_needsData && !_dataSeen)
            {
                _status = "waiting for data";
                if (_elapsed >= _options.ItemTimeoutSeconds) return Done("no data from the sim");
                return ItemOutcome.Running;
            }

            _guardPending = false;
            if (!Evaluate(_item.StartGuard!))
            {
                Release();
                _status = "block condition false";
                _item = null;
                return ItemOutcome.Completed;
            }
        }

        // A subscribed simvar reads 0 until its first feed arrives, and 0 satisfies
        // an "off" test: the item would confirm itself without ever commanding
        // anything (and a dead feed would keep it firing for its whole retry cap).
        // Wait for the first real reading before judging the test.
        if (_needsData && !_dataSeen)
        {
            _status = "waiting for data";
            if (_elapsed >= _options.ItemTimeoutSeconds) return Done("no data from the sim");
            return ItemOutcome.Running;
        }

        bool satisfied = _item.Tests.Count > 0 && _item.Tests.All(Evaluate);

        // The hold-for-pilot flag is honoured whatever the test says. Judging it only
        // inside the satisfied branch meant an item the pilot has to set waited for a
        // condition nothing was going to satisfy, burned its whole fire cap on a switch
        // that cannot be moved from here and timed out.
        var waitForUser = _item.Actions.FirstOrDefault(a =>
            a.Special is SpecialActionType.WaitForUserToDoIt or SpecialActionType.WaitUntilTestValueValidated);
        if (waitForUser != null)
        {
            _holdElapsed += dt;
            if (_holdElapsed >= _options.WaitForUserTimeoutSeconds)
                return Done("waited for user, proceeding");
            _status = _item.Tests.Count == 0
                ? "waiting for pilot"
                : $"waiting for pilot ({_holdElapsed:F0}s)";
            return ItemOutcome.Running;
        }

        // An item that carries commands but nothing to verify is not satisfied merely
        // because there is no test: it is commanded, and only then does it count as done.
        // Treating "no tests" as "already satisfied" completed items like pitot heat,
        // avionics and the exterior lights without ever firing their events.
        if (!satisfied && _item.Tests.Count == 0) return CommandOnly(dt);

        if (satisfied)
        {
            _satisfiedFor += dt;
            _dwell -= dt;

            // The actions that only make sense once the state is reached (the copilot's
            // own "value is correct" group) fire here, once, not in the command cycle.
            FireTestValueTrueActions();

            if (holdSeconds > 0)
            {
                _holdElapsed += dt;
                if (_holdElapsed < holdSeconds)
                {
                    _status = $"holding {holdSeconds - _holdElapsed:F0}s";
                    return ItemOutcome.Running;
                }
            }

            if (_satisfiedFor >= _item.DurationSeconds && _dwell <= 0)
                return Done($"OK: {_item.Display}");

            _status = "satisfied";
            return ItemOutcome.Running;
        }

        if (forced)
        {
            if (_elapsed >= _options.ManualSettleSeconds)
                return Done($"forced: {_item.Display}");
            _status = "force-validating";
            return ItemOutcome.Running;
        }

        _satisfiedFor = 0;

        if (_item.Actions.Count == 0)
        {
            _status = "waiting for condition";
            if (_elapsed >= _options.ItemTimeoutSeconds) return Done("timeout waiting for condition");
            return ItemOutcome.Running;
        }

        var maxFires = _item.Once ? 1 : _options.MaxFiresPerItem;

        if (_sinceFire < _options.FireIntervalSeconds)
        {
            _status = _fires == 0 ? "arming" : "retrying";
            return ItemOutcome.Running;
        }
        _sinceFire = 0;

        if (_fires < maxFires)
        {
            _fireCycles++;
            CommandActions();
        }

        if (_elapsed >= _options.ItemTimeoutSeconds)
        {
            // Either the events did their work and the state then confirmed - impossible
            // here - or the command never took and the item is reported as what it is: a
            // switch nothing the aircraft binds can move. That verdict belongs to an item
            // that has exhausted its commands, not merely to one that has run out of
            // attempts: an event that cannot be repeated (a toggle) is spent after once,
            // so waiting for the full fire cap would report a plain timeout for a switch
            // that is simply not bound.
            var exhausted = _fires > 0 && !HasUnfiredCommand();
            return Done(exhausted || _fires >= maxFires
                ? "state never confirmed (retry cap)"
                : $"timeout after {_fires} attempts");
        }

        // An item that has fired everything it has is waiting for the state, not retrying.
        _status = _fires >= maxFires || !HasUnfiredCommand() && _fires > 0
            ? "waiting for the state to confirm"
            : "retrying";
        return ItemOutcome.Running;
    }

    /// <summary>
    /// True while the item still has an event it has not fired. A toggle event is one: it
    /// cannot be repeated, so once it has been sent there is nothing left to command and
    /// the item is waiting for the state rather than retrying.
    /// </summary>
    private bool HasUnfiredCommand()
    {
        if (_item is null) return false;

        foreach (var action in _item.Actions)
        {
            if (action.Special != SpecialActionType.None || action.EventId == null) continue;
            if (action.Condition is ActionCondition.TestValueTrue or ActionCondition.End) continue;
            if (action.Once && _firedOnce) continue;
            if (!_firedEvents.Contains(action.EventId)) return true;
        }
        return false;
    }

    /// <summary>
    /// Commands an item that has actions but nothing to verify, and completes it after its
    /// dwell rather than reporting it as already done. A command that produced no event -
    /// nothing the binding system could act on - is a timeout, not an OK.
    /// </summary>
    private ItemOutcome CommandOnly(double dt)
    {
        if (_item is null) return ItemOutcome.Completed;

        if (_fires == 0 && _sinceFire >= _options.FireIntervalSeconds)
        {
            _sinceFire = 0;
            _fireCycles++;
            CommandActions();
        }
        _satisfiedFor += dt;
        _dwell -= dt;

        if (_fires > 0 && _dwell <= 0 && _satisfiedFor >= _item.DurationSeconds)
            return Done($"OK: {_item.Display}");

        if (_elapsed >= _options.ItemTimeoutSeconds)
            return Done($"timeout after {_fires} attempts");

        _status = _fires > 0 ? "commanding" : "no actionable event";
        return ItemOutcome.Running;
    }

    /// <summary>
    /// Fires the events that bring the item about, and reports whether anything was fired.
    /// One event is never fired twice for the same item: a toggle event registered for both
    /// the on and the off direction (the alternator, the beacon, the exterior lights) fired
    /// two or three times for one intended change, which can leave the switch where it
    /// started, and a checkpoint with no <c>Once</c> marked re-fired for its whole retry
    /// cap.
    /// </summary>
    private bool CommandActions()
    {
        if (_item is null) return false;

        bool fired = false;
        foreach (var action in _item.Actions)
        {
            if (action.Special != SpecialActionType.None || action.EventId == null) continue;
            // These are the actions for the state the item has reached, and they belong
            // to the satisfied branch: firing them here re-fired on every retry cycle.
            if (action.Condition == ActionCondition.TestValueTrue) continue;
            if (action.Condition == ActionCondition.End) continue;
            if (action.Once && _firedOnce) continue;
            if (!_firedEvents.Add(action.EventId)) continue;
            _host.FireEvent(action.EventId, action.EventParam ?? 1);
            _fires++;
            fired = true;
        }
        _firedOnce = true;

        // "commanding" once per item, then "retrying": naming every fire cycle made
        // the status alternate between the two and the console printed both on
        // every change.
        _status = fired ? (_fireCycles <= 1 ? "commanding" : "retrying") : "no actionable event";
        return fired;
    }

    /// <summary>
    /// Fires the actions the XML gates on the test being true, once, when the item's
    /// condition is met. They were unreachable before: the command loop skipped them and
    /// the satisfied branch fired nothing, so a checkpoint made only of such actions
    /// reported a timeout having done nothing.
    /// </summary>
    private void FireTestValueTrueActions()
    {
        if (_item is null || _testValueActionsFired) return;

        foreach (var action in _item.Actions)
        {
            if (action.Special != SpecialActionType.None || action.EventId == null) continue;
            if (action.Condition != ActionCondition.TestValueTrue) continue;
            if (!_firedEvents.Add(action.EventId)) continue;
            _host.FireEvent(action.EventId, action.EventParam ?? 1);
        }

        _testValueActionsFired = true;
    }

    private ItemOutcome Done(string message)
    {
        _status = message;
        OnStatus?.Invoke(message);
        Release();
        return ItemOutcome.Completed;
    }

    public void Cancel()
    {
        Release();
        _status = "cancelled";
    }

    private void Release()
    {
        if (_subscription != 0)
        {
            try { _host.Unsubscribe(_subscription); }
            catch { }
            _subscription = 0;
            _simVars = Array.Empty<string>();
            _values = Array.Empty<double>();
        }
        _needsData = false;
        _dataSeen = false;
        _item = null;
    }

    private void Subscribe(ChecklistItem item)
    {
        var names = new List<string>();
        foreach (var t in item.Tests)
            foreach (var v in Collect(t))
                if (!names.Contains(v)) names.Add(v);
        if (item.StartGuard != null)
            foreach (var v in Collect(item.StartGuard))
                if (!names.Contains(v)) names.Add(v);
        if (names.Count == 0) return;

        _simVars = names.ToArray();
        var units = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
            units[i] = UnitForName(names[i]);
        _values = new double[names.Count];
        _needsData = true;
        _dataSeen = false;
        _subscription = _host.Subscribe(_simVars, units, CopyValues);
    }

    private static IEnumerable<string> Collect(ConditionNode node)
    {
        foreach (var o in node.Operands)
            if (o.Name != null) yield return o.Name;
        foreach (var c in node.Children)
            foreach (var n in Collect(c)) yield return n;
    }

    private static string UnitForName(string name)
    {
        // SPOILERS HANDLE POSITION is reported in percent, so the feed must be
        // requested in percent to match what the binding verifies; FLAPS HANDLE
        // INDEX, TRANSPONDER CODE and APU SWITCH are indices/settings and are
        // requested unitless.
        if (name is "FLAPS HANDLE INDEX" or "TRANSPONDER CODE:1" or "APU SWITCH")
            return "number";
        if (name is "SPOILERS HANDLE POSITION") return "percent";
        if (name.StartsWith("GENERAL ENG MIXTURE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GENERAL ENG THROTTLE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GENERAL ENG PROPELLER", StringComparison.OrdinalIgnoreCase))
            return "percent";
        if (name.StartsWith("LIGHT", StringComparison.OrdinalIgnoreCase)) return "bool";
        return "number";
    }

    private void CopyValues(double[] values)
    {
        if (_simVars.Length == 0 || values.Length == 0) return;
        int n = Math.Min(values.Length, _values.Length);
        for (int i = 0; i < n; i++) _values[i] = values[i];
        _dataSeen = true;
    }

    private double Read(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        for (int i = 0; i < _simVars.Length && i < _values.Length; i++)
            if (string.Equals(_simVars[i], name, StringComparison.OrdinalIgnoreCase))
                return _values[i];
        return 0;
    }

    private bool Evaluate(ConditionNode node)
    {
        var ops = node.Operands.Select(o => o.Name == null ? o.Value : Read(o.Name)).ToArray();

        double result = node.Type switch
        {
            CheckOpType.Equal => ops.Length >= 2 && Math.Abs(ops[0] - ops[1]) <= (node.Tolerance ?? 0.01) ? 1 : 0,
            CheckOpType.Less => ops.Length >= 2 && ops[0] < ops[1] ? 1 : 0,
            CheckOpType.Greater => ops.Length >= 2 && ops[0] > ops[1] ? 1 : 0,
            CheckOpType.LessOrEqual => ops.Length >= 2 && ops[0] <= ops[1] ? 1 : 0,
            CheckOpType.GreaterOrEqual => ops.Length >= 2 && ops[0] >= ops[1] ? 1 : 0,
            CheckOpType.Between => ops.Length >= 3 && ops[0] >= ops[1] && ops[0] <= ops[2] ? 1 : 0,
            CheckOpType.Not => ops.Length >= 1 && Math.Abs(ops[0]) < 0.5 ? 1 : 0,
            CheckOpType.And => ops.All(x => Math.Abs(x) > 0.5) && node.Children.All(Evaluate) ? 1 : 0,
            CheckOpType.Or => ops.Any(x => Math.Abs(x) > 0.5) || node.Children.Any(Evaluate) ? 1 : 0,
            CheckOpType.Plus => ops.Length >= 2 ? ops[0] + ops[1] : ops.ElementAtOrDefault(0),
            CheckOpType.Minus => ops.Length >= 2 ? ops[0] - ops[1] : ops.ElementAtOrDefault(0),
            CheckOpType.Multiply => ops.Length >= 2 ? ops[0] * ops[1] : ops.ElementAtOrDefault(0),
            CheckOpType.Divide => ops.Length >= 2 && Math.Abs(ops[1]) > 1e-9 ? ops[0] / ops[1] : 0,
            _ => ops.ElementAtOrDefault(0)
        };
        return Math.Abs(result) > 0.5;
    }
}
