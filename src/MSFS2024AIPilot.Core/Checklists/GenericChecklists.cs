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
        Add(set, ChecklistStep.PREFLIGHT_GATE, EngineStart(engineCount));
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

    private static ChecklistItem EngineStart(int engineCount)
    {
        var item = new ChecklistItem
        {
            Subject = "ENGINE START",
            Expectation = "AUTO",
            Kind = ChecklistItemKind.Set,
            Actions = { new ChecklistAction { EventId = "ENGINE_AUTO_START", Condition = ActionCondition.TestValueFalse } }
        };
        for (int e = 1; e <= engineCount; e++)
            item.Tests.Add(ConditionNode.Verify($"GENERAL ENG COMBUSTION:{e}", "bool", true));
        return item;
    }
}
