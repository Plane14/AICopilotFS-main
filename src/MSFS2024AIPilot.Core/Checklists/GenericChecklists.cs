using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Procedural stand-in used when the installed aircraft ships no readable
/// checklist. It covers the systems the sim exposes through universal Asobo
/// events; gear/flaps/spoilers remain under the hard-coded phase logic in
/// <see cref="AIPilot"/> when the aircraft itself does not define them.
/// </summary>
public static class GenericChecklists
{
    public static AircraftChecklistSet Create(string aircraftTitle, bool turbine, int engineCount)
    {
        var set = new AircraftChecklistSet
        {
            AircraftTitle = aircraftTitle,
            SourcePath = "(built-in)",
            Format = ChecklistSetFormat.Generic
        };
        engineCount = Math.Clamp(engineCount, 1, 4);

        static void Add(AircraftChecklistSet s, ChecklistStep step, params ChecklistItem[] items)
        {
            var list = s.Find(step) ?? new ChecklistList(step, step.ToString());
            if (s.Find(step) == null) s.Lists.Add(list);
            list.Items.AddRange(items);
        }

        void Text(AircraftChecklistSet s, ChecklistStep step, string subject, string expectation) =>
            Add(s, step, ChecklistParser.BuildLegacyItem(subject, expectation, step.ToString()));

        // ---------------- PREFLIGHT_GATE ----------------
        Text(set, ChecklistStep.PREFLIGHT_GATE, "BATTERY SWITCH", "ON");
        Text(set, ChecklistStep.PREFLIGHT_GATE, "AVIONICS SWITCH", "ON");
        Text(set, ChecklistStep.PREFLIGHT_GATE, "EXTERIOR LIGHTS", "AS REQUIRED");
        Text(set, ChecklistStep.PREFLIGHT_GATE, "PARKING BRAKE", "SET");
        Text(set, ChecklistStep.PREFLIGHT_GATE, "FUEL PUMP", "ON");
        if (turbine) Text(set, ChecklistStep.PREFLIGHT_GATE, "THROTTLES", "IDLE");
        else Text(set, ChecklistStep.PREFLIGHT_GATE, "MIXTURE", "IDLE CUT-OFF");
        foreach (var engineStart in EngineStart(engineCount, turbine))
            Add(set, ChecklistStep.PREFLIGHT_GATE, engineStart);
        Text(set, ChecklistStep.PREFLIGHT_GATE, "GENERATOR", "ON");
        Text(set, ChecklistStep.PREFLIGHT_GATE, "PITOT HEAT", "AS REQUIRED");

        // ---------------- PREFLIGHT_TAXI_OUT ----------------
        Text(set, ChecklistStep.PREFLIGHT_TAXI_OUT, "NAV LIGHTS", "ON");
        Text(set, ChecklistStep.PREFLIGHT_TAXI_OUT, "BEACON", "ON");
        Text(set, ChecklistStep.PREFLIGHT_TAXI_OUT, "FLAPS", "UP");
        Text(set, ChecklistStep.PREFLIGHT_TAXI_OUT, "TRANSPONDER", "SET");

        // ---------------- FLIGHT_RUNWAY ----------------
        Text(set, ChecklistStep.FLIGHT_RUNWAY, "STROBES", "ON");
        Text(set, ChecklistStep.FLIGHT_RUNWAY, "LANDING LIGHTS", "AS REQUIRED");
        Text(set, ChecklistStep.FLIGHT_RUNWAY, "ANTI ICE", "AS REQUIRED");
        Text(set, ChecklistStep.FLIGHT_RUNWAY, "TRANSPONDER", "SET");

        // ---------------- FLIGHT_TAKEOFF / CLIMB ----------------
        Add(set, ChecklistStep.FLIGHT_TAKEOFF, new ChecklistItem
        {
            Subject = "LANDING GEAR",
            Expectation = "UP",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("GEAR HANDLE POSITION", "bool", false) },
            Actions = { new ChecklistAction { EventId = "GEAR_UP", Condition = ActionCondition.TestValueFalse } }
        });
        Text(set, ChecklistStep.FLIGHT_TAKEOFF, "FLAPS", "UP");
        Text(set, ChecklistStep.FLIGHT_CLIMB, "LANDING LIGHTS", "OFF");

        // ---------------- FLIGHT_DESCENT ----------------
        Text(set, ChecklistStep.FLIGHT_DESCENT, "PITOT HEAT", "AS REQUIRED");
        Text(set, ChecklistStep.FLIGHT_DESCENT, "ANTI ICE", "AS REQUIRED");
        Text(set, ChecklistStep.FLIGHT_DESCENT, "NAV LIGHTS", "ON");

        // ---------------- LANDING APPROACH ----------------
        Add(set, ChecklistStep.LANDING_APPROACH, new ChecklistItem
        {
            Subject = "LANDING GEAR",
            Expectation = "DOWN",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("GEAR HANDLE POSITION", "bool", true) },
            Actions = { new ChecklistAction { EventId = "GEAR_DOWN", Condition = ActionCondition.TestValueFalse } }
        });
        Text(set, ChecklistStep.LANDING_APPROACH, "PITOT HEAT", "ON");
        Text(set, ChecklistStep.LANDING_APPROACH, "LANDING LIGHTS", "ON");

        // ---------------- LANDING FINAL ----------------
        // No airborne "spoilers arm" item: arming cannot be verified (the handle stays
        // stowed until deploy), and a spoilers item on final resolved to SPOILERS_ON
        // deployed them in flight. The rollout step deploys them on the ground.
        Text(set, ChecklistStep.LANDING_FINAL, "PITOT HEAT", "ON");
        Text(set, ChecklistStep.LANDING_FINAL, "YAW DAMPER", "AS REQUIRED");

        // ---------------- LANDING GROUNDROLL ----------------
        Text(set, ChecklistStep.LANDING_GROUNDROLL, "SPOILERS", "ON");
        Text(set, ChecklistStep.LANDING_GROUNDROLL, "TRANSPONDER", "SET");

        // ---------------- TAXI IN ----------------
        Text(set, ChecklistStep.LANDING_TAXI_IN, "NAV LIGHTS", "ON");
        Text(set, ChecklistStep.LANDING_TAXI_IN, "TAXI LIGHTS", "ON");
        Text(set, ChecklistStep.LANDING_TAXI_IN, "STROBES", "OFF");
        Text(set, ChecklistStep.LANDING_TAXI_IN, "LANDING LIGHTS", "OFF");

        // ---------------- GATE / SHUTDOWN ----------------
        Text(set, ChecklistStep.LANDING_GATE, "PARKING BRAKE", "SET");
        Text(set, ChecklistStep.LANDING_GATE, "BEACON", "OFF");
        Text(set, ChecklistStep.LANDING_GATE, "NAV LIGHTS", "OFF");
        Text(set, ChecklistStep.LANDING_GATE, "LANDING LIGHTS", "OFF");
        Text(set, ChecklistStep.LANDING_GATE, "TAXI LIGHTS", "OFF");
        Text(set, ChecklistStep.LANDING_GATE, "AVIONICS SWITCH", "OFF");

        var shutdownItem = new ChecklistItem
        {
            Subject = "ENGINE",
            Expectation = "SHUTDOWN",
            Kind = ChecklistItemKind.Set,
            Tests = { ConditionNode.Verify("GENERAL ENG COMBUSTION:1", "bool", false) },
            Actions = { new ChecklistAction { EventId = "ENGINE_AUTO_SHUTDOWN", Condition = ActionCondition.TestValueFalse } }
        };
        Add(set, ChecklistStep.LANDING_GATE, shutdownItem);

        return set;
    }

    private static IEnumerable<ChecklistItem> EngineStart(int engineCount, bool turbine)
    {
        // One item per engine: STARTERn_SET is an idempotent set event (unlike
        // TOGGLE_STARTERn, which the executor's retry loop would toggle back
        // off while the engine is still cranking). Turbine engines need BOTH
        // STARTERn_SET and SET_STARTERn_HELD to crank; the HELD event is
        // released once combustion confirms so the starter cannot wind up.
        for (int e = 1; e <= engineCount; e++)
        {
            var item = new ChecklistItem
            {
                Subject = $"ENGINE {e} START",
                Expectation = "AUTO",
                Kind = ChecklistItemKind.Set,
                // A turbine light-off can take a minute; beyond that the engine
                // is not going to start and the gate must not stall for hours.
                TimeoutSeconds = 75,
                Tests =
                {
                    // Turbines: GENERAL ENG COMBUSTION stays false on some
                    // add-ons; fuel flow is the reliable "burning" indicator.
                    turbine
                        ? new ConditionNode
                        {
                            Type = CheckOpType.GreaterOrEqual,
                            Operands =
                            {
                                new SimVarNode { Name = $"TURB ENG FUEL FLOW PPH:{e}", Units = "pounds per hour" },
                                new SimVarNode { Constant = 5 }
                            }
                        }
                        : ConditionNode.Verify($"GENERAL ENG COMBUSTION:{e}", "bool", true)
                },
                Actions =
                {
                    new ChecklistAction
                    {
                        EventId = $"STARTER{e}_SET",
                        EventParam = 1,
                        Condition = ActionCondition.TestValueFalse
                    }
                }
            };
            if (turbine)
            {
                item.Actions.Add(new ChecklistAction
                {
                    EventId = $"SET_STARTER{e}_HELD",
                    EventParam = 1,
                    Condition = ActionCondition.TestValueFalse
                });
                item.Actions.Add(new ChecklistAction
                {
                    EventId = $"SET_STARTER{e}_HELD",
                    EventParam = 0,
                    Condition = ActionCondition.TestValueTrue
                });
            }
            yield return item;
        }
    }
}
