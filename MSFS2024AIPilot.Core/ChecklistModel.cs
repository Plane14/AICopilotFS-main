using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// The simulator's own flight-checklist taxonomy. Steps come from the
/// ChecklistStepId attribute of the aircraft checklist XML and are mapped to
/// the flight phases the AI pilot drives.
/// </summary>
public enum ChecklistStep
{
    PREFLIGHT_GATE,
    PREFLIGHT_PUSHBACK,
    PREFLIGHT_TAXI_OUT,
    FLIGHT_RUNWAY,
    FLIGHT_TAKEOFF,
    FLIGHT_CLIMB,
    FLIGHT_CRUISE,
    FLIGHT_DESCENT,
    LANDING_APPROACH,
    LANDING_APPROACH_VFR,
    LANDING_APPROACH_IFR,
    LANDING_FINAL,
    LANDING_TOUCHDOWN,
    LANDING_GROUNDROLL,
    LANDING_TAXI_IN,
    LANDING_GATE
}

public enum ChecklistItemKind
{
    /// Group/page title or free text note: shown but never executed.
    Header,
    /// Verify a state only.
    Check,
    /// Command a state and verify it.
    Set,
    /// No automatable action found: waits briefly and reports.
    Manual
}

public enum CheckOpType
{
    Equal,
    Less,
    Greater,
    LessOrEqual,
    GreaterOrEqual,
    Between,
    Not,
    And,
    Or,
    Plus,
    Minus,
    Multiply,
    Divide
}

public enum ActionCondition
{
    TestValueFalse,
    TestValueTrue,
    Init,
    End,
    Any
}

public enum SpecialActionType
{
    None,
    ForceValidate,
    WaitForUserToDoIt,
    WaitUntilTestValueValidated,
    WaitForDuration
}

/// <summary>One operand of a condition: either a constant or a simvar read.</summary>
public sealed class SimVarNode
{
    public double? Constant;
    public string? Name;
    public string Units = "bool";

    public bool IsConstant => Constant.HasValue;
    public double Value => Constant ?? 0;

    public override string ToString() => IsConstant
        ? Constant!.Value.ToString("0.###")
        : $"{Name}({Units})";
}

/// <summary>
/// Recursive boolean/arith expression tree over simvars. Evaluated by
/// <see cref="ChecklistExecutor"/> against a live simvar feed. Unsupported
/// operators degrade to "operand[0]" so a malformed expression never blocks
/// a checklist.
/// </summary>
public sealed class ConditionNode
{
    public CheckOpType Type = CheckOpType.Equal;
    public List<SimVarNode> Operands = new();
    public List<ConditionNode> Children = new();
    public double? Tolerance;

    public static ConditionNode Verify(string simVar, string units, bool expectedOn)
    {
        return new ConditionNode
        {
            Type = expectedOn ? CheckOpType.GreaterOrEqual : CheckOpType.Less,
            Operands =
            {
                new SimVarNode { Name = simVar, Units = units },
                new SimVarNode { Constant = 0.5 }
            }
        };
    }
}

/// <summary>One executable instruction the copilot action XML defines.</summary>
public sealed class ChecklistAction
{
    public string? EventId;
    public double? EventParam;
    public ActionCondition Condition = ActionCondition.TestValueFalse;
    public bool Once;
    public bool Copilot;
    public SpecialActionType Special = SpecialActionType.None;
    public double? Value;
    public string? FlowEvent;

    public bool IsExecutable => EventId != null || Special is not SpecialActionType.None;
}

/// <summary>A single checklist item (checkpoint), executable end to end.</summary>
public sealed class ChecklistItem
{
    public string Id = "";
    public string Subject = "";
    public string Expectation = "";
    public string? Clue;
    public ChecklistItemKind Kind = ChecklistItemKind.Check;
    public List<ConditionNode> Tests = new();
    public List<ChecklistAction> Actions = new();
    public double DurationSeconds;
    public bool Once;
    public bool Facultative;
    /// Set for legacy text items: the resolver output, re-resolved at execution
    /// time so "AS REQUIRED" uses live weather and assigned squawks.</summary>
    public object? TextBinding;
    /// Evaluated once when the item starts; a false guard skips the item silently
    /// (used for 2024-format BlockCondition).</summary>
    public ConditionNode? StartGuard;
    public string SourceList = "";

    public string Display => string.IsNullOrEmpty(Expectation)
        ? Subject
        : $"{Subject} - {Expectation}";

    public IEnumerable<string> SimVars() =>
        Tests.SelectMany(Collect).Concat(new[] { "" }).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Collect(ConditionNode node)
    {
        foreach (var o in node.Operands)
            if (o.Name != null) yield return o.Name;
        foreach (var c in node.Children)
            foreach (var n in Collect(c)) yield return n;
    }
}

/// <summary>One executable step of a checklist, flattened to ordered items.</summary>
public sealed class ChecklistList
{
    public ChecklistStep Step;
    public string Title = "";
    public List<ChecklistItem> Items = new();

    public ChecklistList(ChecklistStep step, string title)
    {
        Step = step;
        Title = title;
    }

    public int ExecutableCount => Items.Count(i => i.Kind != ChecklistItemKind.Header);
}

public enum ChecklistSetFormat
{
    None,
    /// Aircraft ships the documented MSFS 2024 SimBase.Document XML with copilot actions.
    Msfs2024Xml,
    /// Aircraft ships the legacy &lt;Checklist&gt;&lt;Group&gt;&lt;List&gt; text format.
    LegacyXml,
    /// Built-in procedural stand-in so the AI can still run a full flow.
    Generic
}

/// <summary>The aircraft's complete, executable checklist set.</summary>
public sealed class AircraftChecklistSet
{
    public string AircraftTitle = "";
    public string SourcePath = "";
    public ChecklistSetFormat Format = ChecklistSetFormat.None;
    public List<ChecklistList> Lists = new();
    public List<string> Notes = new();

    public bool IsLoaded => Lists.Count > 0;
    public bool IsFallback => Format == ChecklistSetFormat.Generic;
    public bool IsAircraftSpecific => Format is ChecklistSetFormat.Msfs2024Xml or ChecklistSetFormat.LegacyXml;

    public ChecklistList? Find(ChecklistStep step) => Lists.Find(l => l.Step == step);
    public IEnumerable<ChecklistItem> AllItems => Lists.SelectMany(l => l.Items);

    public bool HasAny(Func<ChecklistItem, bool> predicate) =>
        Lists.Any(l => l.Items.Any(predicate));

    public static AircraftChecklistSet Empty() => new();
}
