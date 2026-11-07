using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MSFS2024AIPilot.Core;

public enum AsRequiredMode
{
    None,
    Lights,
    IceProtection
}

/// <summary>
/// What a text checklist item resolved to: which event(s) put the system in
/// the requested state, plus the simvar that confirms it.
/// </summary>
public sealed class SwitchCommand
{
    public string? VerifySimVar;
    public string VerifyUnits = "bool";
    public bool ExpectOn = true;
    public List<(string Name, double Data)> Events = new();
    public AsRequiredMode AsRequired = AsRequiredMode.None;

    /// Set when the text could not be executed; the item degrades to a manual check.</summary>
    public string ManualReason = "";
    public bool IsManual => Events.Count == 0;
}

/// <summary>Ambient hints used to settle "AS REQUIRED" items.</summary>
public sealed class ChecklistEnvironment
{
    public bool Night;
    public bool IcingRisk;
    public bool Overcast;
    public bool Turbine;
    public int EngineCount = 1;
    public string? TransponderCode;
}

/// <summary>
/// Maps free-text checklist items onto the Asobo event/simvar namespace.
/// Event and simvar names come from the MSFS 2024 SDK Key Events and
/// Simulation Variables references. Unresolvable items degrade to manual
/// checks so an unhandled line never blocks the flow.
/// </summary>
public static class ChecklistTextResolver
{
    private const double Scale = 16383.0;

    /// <summary>Declarative binding for an on/off switch.</summary>
    private sealed class SwitchBinding
    {
        public string? VerifySimVar;
        public string VerifyUnits = "bool";
        public readonly List<string> OnEvents = new();
        public readonly List<string> OffEvents = new();
        public AsRequiredMode AsRequired;
        /// Switch SET events carrying an explicit 0/1 payload.</summary>
        public readonly List<string> PayloadEvents = new();
        /// Lever events carrying a percent payload (0..16383).</summary>
        public readonly List<string> LeverEvents = new();
        public readonly Dictionary<string, string> QualifierEvents = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>True when a RETRACT/RETRACTED/STOWED expectation means retract (off).</summary>
        public bool RetractMeansOff { get; set; }
        /// <summary>
        /// True when a bare "UP" expectation means retract (off). Set for the landing gear,
        /// where "gear up" is unambiguously a retraction; deliberately NOT set for the
        /// spoilers, where a "speedbrake up" can mean deployed.
        /// </summary>
        public bool UpMeansOff { get; set; }

        public SwitchBinding(string? verify = null)
        {
            VerifySimVar = verify;
        }
    }

    private static readonly Dictionary<string, SwitchBinding> Bindings = new(StringComparer.OrdinalIgnoreCase);

    /// Keys are scanned longest-first so "SPOILERS ARM" wins over "SPOILERS".</summary>
    private static readonly string[] OrderedKeys = Array.Empty<string>();

    static ChecklistTextResolver()
    {
        static void Reg(string key, SwitchBinding b) => Bindings[key] = b;
        void RegAliases(string[] aliases, SwitchBinding b) { foreach (var a in aliases) Bindings[a] = b; }

        // MASTER_BATTERY_SET is the only payload event the sim defines for the
        // master battery: payload > 0 arms it, 0 releases it. MASTER_BATTERY_ON and
        // MASTER_BATTERY_OFF are one-shot presets in the SDK behaviour defs, so they
        // must never both be fired, or the battery is switched off again the instant
        // it was switched on and the item can never verify.
        var battery = new SwitchBinding("ELECTRICAL MASTER BATTERY")
        { PayloadEvents = { "MASTER_BATTERY_SET" } };
        Reg("BATTERY", battery);
        RegAliases(new[] { "BATT", "BATTERY SWITCH", "MASTER BATTERY", "MASTER SWITCH", "BATTERY 1", "BATTERIES", "BATTERY MASTER" }, battery);

        var alternator = new SwitchBinding("GENERAL ENG MASTER ALTERNATOR:1")
        { OnEvents = { "TOGGLE_ALTERNATOR1" }, OffEvents = { "TOGGLE_ALTERNATOR1" } };
        Reg("ALTERNATOR", alternator);
        RegAliases(new[] { "GENERATOR", "GEN", "GEN 1", "ALT 1", "ALTERNATOR BREAKER" }, alternator);

        var avionics = new SwitchBinding();
        avionics.OnEvents.Add("AVIONICS_MASTER_SET");
        avionics.OffEvents.Add("AVIONICS_MASTER_SET");
        avionics.PayloadEvents.Add("AVIONICS_MASTER_SET");
        Reg("AVIONICS", avionics);
        RegAliases(new[] { "AVIONICS MASTER", "AVIONICS SWITCH", "AVIONICS BUS", "AVIONICS POWER" }, avionics);

        var apu = new SwitchBinding("APU SWITCH") { OnEvents = { "APU_STARTER" }, OffEvents = { "APU_OFF_SWITCH" } };
        Reg("APU", apu);

        var gpu = new SwitchBinding("EXTERNAL POWER ON") { PayloadEvents = { "SET_EXTERNAL_POWER" } };
        Reg("EXTERNAL POWER", gpu);
        RegAliases(new[] { "GROUND POWER", "GPU", "GROUND POWER UNIT", "EXT POWER" }, gpu);
        // A fuel pump has one home in MSFS 2024: the modern [FUEL_SYSTEM] Pump.N block,
        // commanded with FUELSYSTEM_PUMP_ON/OFF (payload = pump index) and verified with
        // FUELSYSTEM PUMP SWITCH:index. The legacy ELECT_FUEL_PUMP1_SET is kept alongside
        // it - it is the only home a legacy [FUEL] aircraft has - but firing only that
        // event on a modern aircraft commands nothing at all: the checkpoint fired for
        // its whole retry cap against a pump switch that stayed off, which is the
        // three-minute gate-checklist stall on "FUEL PUMP - ON".
        var pump = new SwitchBinding("FUELSYSTEM PUMP SWITCH:1")
        {
            PayloadEvents = { "FUELSYSTEM_PUMP_ON", "FUELSYSTEM_PUMP_OFF", "ELECT_FUEL_PUMP1_SET" }
        };

        Reg("FUEL PUMP", pump);
        RegAliases(new[] { "FUEL BOOST PUMP", "BOOST PUMP", "AUX FUEL PUMP", "FUEL BOOSTER PUMP", "L FUEL PUMP", "FUEL PUMP 1" }, pump);

        // The beacon state is the SimVar LIGHT BEACON. A truncated name such as
        // "LIGHT BEAC" is rejected by the sim with NAME_UNRECOGNIZED, so its feed
        // never delivers and the checkpoint hangs at "waiting for data" instead of
        // ever verifying.
        Reg("BEACON", new SwitchBinding("LIGHT BEACON") { OnEvents = { "BEACON_LIGHTS_ON" }, OffEvents = { "BEACON_LIGHTS_OFF" } });
        var nav = new SwitchBinding("LIGHT NAV") { OnEvents = { "NAV_LIGHTS_ON" }, OffEvents = { "NAV_LIGHTS_OFF" } };
        Reg("NAV LIGHTS", nav);
        RegAliases(new[] { "NAV LIGHT", "NAVIGATION LIGHTS", "NAVIGATION LIGHT", "POSITION LIGHTS", "POSITION LIGHT" }, nav);
        Reg("STROBES", new SwitchBinding("LIGHT STROBE") { OnEvents = { "STROBES_ON" }, OffEvents = { "STROBES_OFF" } });
        RegAliases(new[] { "STROBE", "STROBE LIGHTS", "STROBE LIGHT", "ANTI COLLISION LIGHTS" }, Bindings["STROBES"]);
        Reg("LANDING LIGHTS", new SwitchBinding("LIGHT LANDING") { OnEvents = { "LANDING_LIGHTS_ON" }, OffEvents = { "LANDING_LIGHTS_OFF" } });
        RegAliases(new[] { "LANDING LIGHT" }, Bindings["LANDING LIGHTS"]);
        Reg("TAXI LIGHTS", new SwitchBinding("LIGHT TAXI") { OnEvents = { "TAXI_LIGHTS_ON" }, OffEvents = { "TAXI_LIGHTS_OFF" } });
        RegAliases(new[] { "TAXI LIGHT", "TAXI LAMP", "TAXI LAMPS" }, Bindings["TAXI LIGHTS"]);
        Reg("LOGO LIGHTS", new SwitchBinding("LIGHT LOGO") { PayloadEvents = { "LOGO_LIGHTS_SET" } });
        RegAliases(new[] { "LOGO LIGHT", "RECOGNITION LIGHTS", "RECOGNITION LIGHT" }, Bindings["LOGO LIGHTS"]);
        Reg("PANEL LIGHTS", new SwitchBinding("LIGHT PANEL") { PayloadEvents = { "PANEL_LIGHTS_SET" } });
        RegAliases(new[] { "PANEL LIGHT", "INSTRUMENT LIGHTS", "INSTRUMENT LIGHT", "FLOOD LIGHTS", "COCKPIT LIGHTS" }, Bindings["PANEL LIGHTS"]);
        Reg("CABIN LIGHTS", new SwitchBinding(null) { PayloadEvents = { "CABIN_LIGHTS_SET" } });
        RegAliases(new[] { "CABIN LIGHT", "DOME LIGHTS" }, Bindings["CABIN LIGHTS"]);

        var exterior = new SwitchBinding(null)
        {
            AsRequired = AsRequiredMode.Lights
        };
        exterior.OnEvents.AddRange(new[] { "NAV_LIGHTS_ON", "BEACON_LIGHTS_ON", "STROBES_ON", "LANDING_LIGHTS_ON", "TAXI_LIGHTS_ON" });
        exterior.OffEvents.AddRange(new[] { "NAV_LIGHTS_OFF", "BEACON_LIGHTS_OFF", "STROBES_OFF", "LANDING_LIGHTS_OFF", "TAXI_LIGHTS_OFF" });
        Reg("EXTERIOR LIGHTS", exterior);
        RegAliases(new[] { "EXTERIOR LIGHT", "LIGHTS", "ALL LIGHTS", "LIGHTS SET", "EXTERIOR LIGHTS SWITCH" }, exterior);

        Reg("PITOT HEAT", new SwitchBinding(null) { OnEvents = { "PITOT_HEAT_ON" }, OffEvents = { "PITOT_HEAT_OFF" }, AsRequired = AsRequiredMode.IceProtection });
        RegAliases(new[] { "PITOT", "PITOT ANTI ICE", "PITOT ANTI ICE SWITCH" }, Bindings["PITOT HEAT"]);
        Reg("ANTI ICE", new SwitchBinding("GENERAL ENG ANTI ICE POSITION:1")
        {
            OnEvents = { "ANTI_ICE_ON" },
            OffEvents = { "ANTI_ICE_OFF" },
            AsRequired = AsRequiredMode.IceProtection
        });
        RegAliases(new[] { "ENGINE ANTI ICE", "ENG ANTI ICE", "ICE PROTECTION", "PROP HEAT", "PROP DEICE", "ANTI ICE SWITCH" }, Bindings["ANTI ICE"]);
        Reg("WINDSHIELD DEICE", new SwitchBinding(null) { OnEvents = { "WINDSHIELD_DEICE_ON" }, OffEvents = { "WINDSHIELD_DEICE_OFF" }, AsRequired = AsRequiredMode.IceProtection });
        RegAliases(new[] { "WINDSHIELD ANTI ICE", "WINDOW HEAT", "WINDSHIELD DEICE" }, Bindings["WINDSHIELD DEICE"]);

        Reg("STARTER", new SwitchBinding("GENERAL ENG STARTER:1") { PayloadEvents = { "STARTER1_SET" } });
        RegAliases(new[] { "ENGINE STARTER", "STARTER", "STARTER BUTTON", "STARTER SWITCH", "ENGINE START SWITCH", "ENGINE STARTER BUTTON" }, Bindings["STARTER"]);
        var fuelValve = new SwitchBinding("GENERAL ENG FUEL VALVE:1") { PayloadEvents = { "SET_FUEL_VALVE_ENG1" } };
        Reg("FUEL VALVE", fuelValve);
        RegAliases(new[] { "ENGINE RUN", "ENGINE RUN STOP", "RUN STOP BUTTON", "ENGINE RUN/STOP BUTTON", "ENGINE FUEL", "FUEL CUTOFF", "FUEL SHUTOFF", "ENGINE MASTER", "FUEL CONTROL SWITCH", "ENGINE CONTROL SWITCH" }, fuelValve);

        // The three lever bindings verify in percent: the lever position simvars
        // report 0..100, while a binding that leaves VerifyUnits at "bool" reads as
        // a switch and would accept any non-zero lever position.
        Reg("MIXTURE", new SwitchBinding("GENERAL ENG MIXTURE LEVER POSITION:1") { VerifyUnits = "percent", LeverEvents = { "MIXTURE_SET" } });
        Reg("THROTTLE", new SwitchBinding("GENERAL ENG THROTTLE LEVER POSITION:1") { VerifyUnits = "percent", LeverEvents = { "THROTTLE_SET" } });
        RegAliases(new[] { "THROTTLES", "THRUST LEVER", "THRUST LEVERS", "POWER LEVER", "POWER LEVERS" }, Bindings["THROTTLE"]);
        Reg("CONDITION LEVER", new SwitchBinding("GENERAL ENG PROPELLER LEVER POSITION:1") { VerifyUnits = "percent", LeverEvents = { "CONDITION_LEVER_1_SET" } });
        RegAliases(new[] { "PROPELLER RPM", "PROP RPM", "PROPELLER LEVER", "RPM LEVER", "PROPELLER RPM LEVER" }, Bindings["CONDITION LEVER"]);

        var selector = new SwitchBinding("GENERAL ENG FUEL VALVE:1");
        selector.OnEvents.AddRange(new[] { "FUEL_SELECTOR_LEFT", "FUEL_SELECTOR_RIGHT", "FUEL_SELECTOR_ALL", "FUEL_SELECTOR_2_LEFT", "FUEL_SELECTOR_2_RIGHT", "FUEL_SELECTOR_2_ALL" });
        selector.OffEvents.Add("FUEL_SELECTOR_OFF");
        Reg("FUEL SELECTOR", selector);
        RegAliases(new[] { "FUEL TANK SELECTOR", "FUEL SELECTOR SWITCH" }, selector);

        var gear = new SwitchBinding("GEAR HANDLE POSITION")
        {
            OnEvents = { "GEAR_DOWN" },
            OffEvents = { "GEAR_UP" },
            RetractMeansOff = true,
            UpMeansOff = true
        };
        Reg("GEAR", gear);
        RegAliases(new[] { "LANDING GEAR", "UNDERCARRIAGE", "GEAR LEVER" }, gear);

        // The spoiler handle position SimVar is SPOILERS HANDLE POSITION, reported
        // in percent. "SPOILERS HANDLE PERCENT" is not a variable the sim knows, so
        // the request for it is refused and the spoiler checkpoints can never verify.
        var spoiler = new SwitchBinding("SPOILERS HANDLE POSITION")
        { VerifyUnits = "percent", OnEvents = { "SPOILERS_ON" }, OffEvents = { "SPOILERS_OFF" }, RetractMeansOff = true };
        Reg("SPOILERS", spoiler);
        RegAliases(new[] { "SPOILER", "SPEEDBRAKE", "SPEED BRAKE", "AIRBRAKE" }, spoiler);
        var arm = new SwitchBinding("SPOILERS HANDLE POSITION")
        { VerifyUnits = "percent", PayloadEvents = { "SPOILERS_ARM_ON" } };
        Reg("SPOILERS ARM", arm);
        RegAliases(new[] { "SPOILER ARM" }, arm);

        var brake = new SwitchBinding("BRAKE PARKING POSITION") { PayloadEvents = { "PARKING_BRAKE_SET" } };
        Reg("PARKING BRAKE", brake);
        RegAliases(new[] { "PARK BREKE", "PARKING BRAKES", "PARK BRAKE" }, brake);

        Reg("YAW DAMPER", new SwitchBinding("AUTOPILOT YAW DAMPER") { OnEvents = { "YAW_DAMPER_ON" }, OffEvents = { "YAW_DAMPER_OFF" } });

        var autobrake = new SwitchBinding(null);
        autobrake.QualifierEvents["RTO"] = "AUTOBRAKE_LO_SET";
        autobrake.QualifierEvents["MAX"] = "AUTOBRAKE_HI_SET";
        autobrake.QualifierEvents["MED"] = "AUTOBRAKE_MED_SET";
        autobrake.QualifierEvents["MEDIUM"] = "AUTOBRAKE_MED_SET";
        autobrake.QualifierEvents["MIN"] = "AUTOBRAKE_LO_SET";
        autobrake.QualifierEvents["LO"] = "AUTOBRAKE_LO_SET";
        autobrake.QualifierEvents["HI"] = "AUTOBRAKE_HI_SET";
        Reg("AUTOBRAKE", autobrake);
        RegAliases(new[] { "AUTO BRAKE", "AUTOBRAKES" }, autobrake);

        var belts = new SwitchBinding(null) { OnEvents = { "CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE" }, OffEvents = { "CABIN_SEATBELTS_ALERT_SWITCH_TOGGLE" } };
        Reg("SEATBELT", belts);
        Reg("NO SMOKING", new SwitchBinding(null) { OnEvents = { "CABIN_NO_SMOKING_ALERT_SWITCH_TOGGLE" }, OffEvents = { "CABIN_NO_SMOKING_ALERT_SWITCH_TOGGLE" } });

        // Field initializers run before the static constructor body, so the
        // longest-first key order must be built here, once the table is full.
        OrderedKeys = GetOrderedKeys();
    }

    private static string[] GetOrderedKeys()
    {
        var keys = new string[Bindings.Count];
        Bindings.Keys.CopyTo(keys, 0);
        Array.Sort(keys, static (a, b) => b.Length.CompareTo(a.Length));
        return keys;
    }

    public static string Canonicalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
            else sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static SwitchCommand Resolve(string subject, string expectation, ChecklistEnvironment env)
    {
        var cmd = new SwitchCommand();
        var canonSubject = Canonicalize(subject);
        var canonExpect = Canonicalize(expectation);

        // FLAPS / MIXTURE-ish special forms are handled before the table.
        if (ContainsWord(canonSubject, "FLAPS") || ContainsWord(canonSubject, "FLAP") ||
            canonSubject.StartsWith("FLAPS", StringComparison.Ordinal) || canonSubject is "FLAP")
        {
            return Flaps(cmd, canonExpect);
        }

        if (ContainsWord(canonSubject, "MAGNETO") || ContainsWord(canonSubject, "IGNITION"))
        {
            return Magneto(cmd, canonExpect);
        }

        if (ContainsWord(canonSubject, "TRANSPONDER") || ContainsWord(canonSubject, "XPNDR") || ContainsWord(canonSubject, "SQUAWK"))
        {
            return Transponder(cmd, canonExpect, env);
        }

        // A turbine has no mixture lever: the piston checkpoint would fire for its
        // whole retry cap against a lever that never moves (the sim still answers
        // the feed for the variable, so the item reports "state never confirmed"
        // rather than "no data"), and then the engine start runs on a rich mixture.
        // The turbine branch's THROTTLES/IDLE checkpoint covers the same ground.
        if (env.Turbine && (ContainsWord(canonSubject, "MIXTURE") || ContainsWord(canonSubject, "MIX")))
        {
            cmd.ManualReason = "no mixture lever on a turbine";
            return cmd;
        }

        foreach (var key in OrderedKeys)
        {
            var b = Bindings[key];
            if (!ContainsWord(canonSubject, key)) continue;

            if (b.OnEvents.Count == 0 && b.OffEvents.Count == 0 && b.PayloadEvents.Count == 0 &&
                b.LeverEvents.Count == 0 && b.QualifierEvents.Count == 0)
            {
                cmd.VerifySimVar = b.VerifySimVar;
                cmd.VerifyUnits = b.VerifyUnits;
                cmd.ManualReason = "informational";
                return cmd;
            }

            var on = !IsOffExpectation(canonExpect);
            // A retraction word ("gear up", "gear retract") commands the OFF state for a
            // retractable system. Without this an "UP" expectation on the gear binding
            // kept on = true and fired GEAR_DOWN, extending the gear it was asked to raise.
            if (b.RetractMeansOff && IsRetractExpectation(canonExpect)) on = false;
            if (b.UpMeansOff && canonExpect.StartsWith("UP", StringComparison.Ordinal)) on = false;

            var asRequired = canonExpect.Contains("AS REQUIRED") || canonExpect.Contains("AS NECESSARY") || canonExpect == "SET";
            if (asRequired && b.AsRequired != AsRequiredMode.None)
            {
                on = b.AsRequired switch
                {
                    AsRequiredMode.Lights => env.Night || env.Overcast,
                    AsRequiredMode.IceProtection => env.IcingRisk,
                    _ => false
                };
                cmd.AsRequired = b.AsRequired;
            }
            cmd.ExpectOn = on;

            if (TryQualifier(b, canonExpect, out var qEvent))
            {
                cmd.Events.Add((qEvent, 1));
            }
            else
            {
                // A one-shot *_ON/*_OFF preset registered as a payload event is fired
                // only for the state it names; a *_SET event is always fired, carrying
                // the requested state as its payload. Firing both presets of a pair
                // leaves the switch in the opposite state to the one asked for.
                foreach (var e in b.PayloadEvents)
                {
                    if (IsPreset(e))
                    {
                        // A preset ignores the switch state and its payload is the
                        // argument the event wants, so it always fires with 1: the
                        // pump index for FUELSYSTEM_PUMP_OFF would otherwise be 0,
                        // which addresses no pump at all.
                        if (!PresetMatchesState(e, on)) continue;
                        cmd.Events.Add((e, 1));
                    }
                    else
                    {
                        cmd.Events.Add((e, on ? 1 : 0));
                    }
                }
                foreach (var e in b.LeverEvents)
                {
                    // A lever SET event exists unindexed (MIXTURE_SET) and per engine
                    // (MIXTURE1_SET). An aircraft that binds only the indexed form
                    // ignores the unindexed event, so the checkpoint fires the whole
                    // family with the identical payload: a SET event is absolute, so
                    // repeating it can no more flip state than firing one preset of
                    // an ON/OFF pair can.
                    var payload = LeverData(e, canonExpect, on, subject);
                    foreach (var name in LeverEventFamily(e, env.EngineCount))
                        cmd.Events.Add((name, payload));
                }
                if (b.PayloadEvents.Count == 0 && b.LeverEvents.Count == 0)
                {
                    if (on) foreach (var e in b.OnEvents) cmd.Events.Add((e, 1));
                    else foreach (var e in b.OffEvents) cmd.Events.Add((e, 1));
                }
            }

            cmd.VerifySimVar = b.VerifySimVar;
            cmd.VerifyUnits = b.VerifyUnits;
            if (cmd.IsManual && b.VerifySimVar == null)
            {
                cmd.ManualReason = "no automatable binding";
            }
            return cmd;
        }

        cmd.ManualReason = "not mapped to a cockpit binding";
        return cmd;
    }

    /// <summary>Whether an event is a one-shot <c>*_ON</c>/<c>*_OFF</c> preset rather than a SET event.</summary>
    private static bool IsPreset(string eventId) =>
        eventId.EndsWith("_ON", StringComparison.OrdinalIgnoreCase) ||
        eventId.EndsWith("_OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a one-shot preset event names the requested state.</summary>
    private static bool PresetMatchesState(string eventId, bool on) =>
        (eventId.EndsWith("_ON", StringComparison.OrdinalIgnoreCase) && on) ||
        (eventId.EndsWith("_OFF", StringComparison.OrdinalIgnoreCase) && !on);

    private static double LeverData(string eventId, string canonExpect, bool on, string rawExpectation)
    {
        var number = ExtractFirstNumber(rawExpectation);
        if (canonExpect.Contains("IDLE CUT")) number = 0;
        if (number is null && (canonExpect.Contains("CUT OFF") || canonExpect.Contains("CUTOFF"))) number = 0;
        if (number is null && (canonExpect == "IDLE" || canonExpect.Contains(" THROTTLE IDLE"))) number = 5;
        if (number is null && on) return Scale;
        if (number is null) return 0;
        return Math.Clamp(number.Value * Scale / 100.0, 0, Scale);
    }

    /// <summary>
    /// An absolute lever SET event together with the per-engine forms the sim also
    /// accepts: MIXTURE_SET with MIXTURE1..N_SET, THROTTLE_SET with THROTTLE1..N_SET,
    /// and an already indexed CONDITION_LEVER_1_SET with the remaining engines. The
    /// unindexed event is kept because stock aircraft bind it, the indexed ones
    /// because an aircraft that binds only those ignores the unindexed event
    /// entirely and the checkpoint would command a lever that never moves.
    /// </summary>
    private static IEnumerable<string> LeverEventFamily(string eventId, int engineCount)
    {
        var engines = Math.Clamp(engineCount, 1, 4);
        yield return eventId;

        var m = Regex.Match(eventId, @"^(?<stem>.+?)(?<index>\d)?_SET$");
        if (!m.Success) yield break;
        var stem = m.Groups["stem"].Value;
        for (int engine = 1; engine <= engines; engine++)
        {
            var indexed = $"{stem}{engine}_SET";
            if (!string.Equals(indexed, eventId, StringComparison.OrdinalIgnoreCase))
                yield return indexed;
        }
    }

    private static SwitchCommand Flaps(SwitchCommand cmd, string canonExpect)
    {
        cmd.VerifySimVar = "FLAPS HANDLE INDEX";
        cmd.VerifyUnits = "number";
        cmd.ExpectOn = true;

        if (canonExpect == "UP" || canonExpect == "RETRACT" || canonExpect == "CLEAN" || canonExpect.Contains("FLAPS UP"))
        {
            // The handle index reads 0 with the flaps up, and Verify(index, expectedOn)
            // asks "is the index at least 0.5". Asking for the on state leaves the
            // checkpoint firing FLAPS_UP for its whole retry cap against a handle that
            // is already up, so the retraction asks for the off state instead.
            cmd.ExpectOn = false;
            cmd.Events.Add(("FLAPS_UP", 1));
        }
        else if (canonExpect == "DOWN" || canonExpect == "TAKEOFF" || canonExpect.Contains("FLAPS DOWN") || canonExpect.Contains("FULL") || canonExpect.Contains("LANDING"))
        {
            cmd.Events.Add(("FLAPS_DOWN", 1));
        }
        else
        {
            var n = ExtractFirstNumber(canonExpect);
            if (n is 0)
            {
                // A numeric zero is a retraction, the same as "UP". The handle index reads
                // 0 with the flaps up, so it asks for the off state and commands FLAPS_UP
                // - not FLAPS_DOWN, which extended the flaps it was asked to raise and
                // then confirmed a handle that was already up.
                cmd.ExpectOn = false;
                cmd.Events.Add(("FLAPS_UP", 1));
            }
            else if (n is <= 4 and > 0)
            {
                cmd.Events.Add(($"FLAPS_{(int)n.Value}", 1));
            }
            else
            {
                // A detent with no indexed event (5+): command one notch down and still
                // verify the handle left the up position, rather than claiming success
                // with the handle wherever it happened to land.
                cmd.Events.Add(("FLAPS_DOWN", 1));
            }
        }
        return cmd;
    }

    private static SwitchCommand Magneto(SwitchCommand cmd, string canonExpect)
    {
        var state = canonExpect switch
        {
            var e when e.Contains("OFF") => 0,
            var e when e.Contains("LEFT") => 1,
            var e when e.Contains("RIGHT") => 2,
            var e when e.Contains("BOTH") => 3,
            var e when e.Contains("START") => 3,
            _ => 3
        };
        cmd.Events.Add(("MAGNETO1_SET", state));
        cmd.VerifySimVar = null;
        return cmd;
    }

    private static SwitchCommand Transponder(SwitchCommand cmd, string canonExpect, ChecklistEnvironment env)
    {
        if (TryParseSquawk(canonExpect, out var bcd))
        {
            cmd.Events.Add(("XPNDR_SET", bcd));
            cmd.VerifySimVar = "TRANSPONDER CODE:1";
            cmd.VerifyUnits = "number";
            return cmd;
        }
        if (env.TransponderCode != null && TryParseSquawk(env.TransponderCode, out var code))
        {
            cmd.Events.Add(("XPNDR_SET", code));
            cmd.VerifySimVar = "TRANSPONDER CODE:1";
            cmd.VerifyUnits = "number";
            return cmd;
        }
        cmd.ManualReason = "no transponder code assigned";
        return cmd;
    }

    private static bool TryQualifier(SwitchBinding b, string canonExpect, out string eventId)
    {
        foreach (var kv in b.QualifierEvents)
        {
            if (ContainsWord(canonExpect, kv.Key) || canonExpect.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                eventId = kv.Value;
                return true;
            }
        }
        eventId = "";
        return false;
    }

    private static double? ExtractFirstNumber(string text)
    {
        var m = Regex.Match(text, @"(\d{1,4})(?!\d)");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static bool TryParseSquawk(string text, out double code)
    {
        var m = Regex.Match(text, @"\b([0-7]{4})\b");
        if (m.Success)
        {
            var v = 0;
            foreach (var c in m.Groups[1].Value) v = (v << 4) | (c - '0');
            code = v;
            return true;
        }
        code = 0;
        return false;
    }

    private static bool IsOffExpectation(string canonExpect)
    {
        if (canonExpect == "OFF" || canonExpect.StartsWith("OFF ", StringComparison.Ordinal)) return true;
        if (canonExpect == "CLOSED") return true;
        if (canonExpect.Contains("CUT OFF") || canonExpect.Contains("CUTOFF")) return true;
        if (canonExpect == "IDLE CUT OFF") return true;
        return false;
    }

    /// <summary>True for an expectation that asks a retractable system to stow.</summary>
    private static bool IsRetractExpectation(string canonExpect) =>
        canonExpect is "RETRACT" or "RETRACTED" or "STOWED" or "STOW" ||
        canonExpect.StartsWith("RETRACT", StringComparison.Ordinal) ||
        canonExpect.Contains("RETRACTED") || canonExpect.Contains("STOWED");

    private static bool ContainsWord(string haystack, string word)
    {
        var h = haystack.Trim();
        if (string.Equals(h, word.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (h.StartsWith(word, StringComparison.OrdinalIgnoreCase) && word.TrimStart() == word) return true;
        return h.Contains(" " + word.Trim() + " ", StringComparison.OrdinalIgnoreCase);
    }
}
