using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// The ATC text parser, and the on/off switch for the BeyondATC side of the bridge.
/// <para>
/// Everything the pilot actually hears from BeyondATC comes from its <c>Player.log</c> and
/// is decoded by <see cref="BeyondAtcLogParser"/>, because BeyondATC's own ATC speech is not
/// readable by an external program; the only exit is the action key it reads from its own
/// preferences. What is left here is the shared instruction grammar: one parser that both
/// controllers and a hand-typed transcript go through, so "CLIMB TO 10000" means the same
/// thing whichever of them said it.
/// </para>
/// </summary>
public sealed class BeyondAtcBridge
{
    private bool _enabled;

    public void Start() => _enabled = true;

    public void Stop() => _enabled = false;

    public bool IsEnabled => _enabled;

    public static AtcInstruction ParseAtcText(string text, string from)
    {
        var instruction = new AtcInstruction
        {
            RawText = text,
            Station = from,
            Timestamp = DateTime.UtcNow
        };

        var t = text.ToUpperInvariant();

        // Runway assignment. A clearance can name more than one strip - "cross runway 8
        // and hold short of runway 8R" - and the leftmost match alone took the crossing
        // strip rather than the one the aircraft is held short of, which at a
        // parallel-strip airport is the wrong runway. The last match that carries a
        // designator is the one the instruction is about, and a bare match is only the
        // fallback. The digits are bounded on the right as well, so "runway 300" no
        // longer reads as 30.
        var rwyMatches = Regex.Matches(t, @"(?:RUNWAY|RWY)\s*(\d{1,2})\s*([LRC])?(?!\d)");
        if (rwyMatches.Count > 0)
        {
            var designated = rwyMatches.Cast<Match>().LastOrDefault(m => m.Groups[2].Success);
            var rwyMatch = designated ?? rwyMatches[0];
            instruction.Runway = rwyMatch.Groups[1].Value + (rwyMatch.Groups[2].Success ? rwyMatch.Groups[2].Value : "");
        }

        // Altitude. Every spelling ATC uses has to resolve to feet, and the parser that
        // used to run here matched a two-digit flight level as two-digit feet and then
        // let a second pass correct it by accident, so the whole reading is done once
        // here, in one place, and returns null when no altitude is assigned.
        instruction.AltitudeFt = AssignedAltitudeFt(t);

        // Heading
        instruction.HeadingDeg = AssignedHeadingDeg(t);

        // A fix the controller has sent the aircraft to: "FLY DIRECT TO GLASG", "PROCEED
        // DIRECT AMOS", "DIRECT TO DOGAL". The fix name is resolved to a position by the
        // navigation database; the clearance itself is just a name.
        instruction.DirectToFix = AssignedDirectFix(t);

        // Speed
        instruction.SpeedKnots = AssignedSpeedKnots(t);

        // Mach. A bare "MACH 78" is the spoken form of 0.78.
        instruction.Mach = AssignedMach(t);

        // Squawk
        var sqkMatch = Regex.Match(t, @"SQUAWK\s*:?\s*(\d{4})");
        if (sqkMatch.Success)
        {
            instruction.Squawk = sqkMatch.Groups[1].Value;
        }

        // Taxi instructions
        if (t.Contains("TAXI")) instruction.IsTaxiInstruction = true;
        if (t.Contains("HOLD SHORT")) instruction.HoldShort = true;
        if (t.Contains("CROSS RUNWAY") || t.Contains("CROSS RWY")) instruction.CrossRunway = true;

        // A back-taxi is a runway entry the aircraft has been cleared for, so it is a
        // taxi instruction with a backtrack flag rather than a clearance of its own.
        if (t.Contains("BACKTRACK") || t.Contains("BACK TAXI") || t.Contains("BACK-TAXI"))
        {
            instruction.IsTaxiInstruction = true;
            instruction.Backtrack = true;
        }

        // Clearances
        if (t.Contains("CLEARED FOR TAKEOFF")) instruction.ClearedForTakeoff = true;
        if (t.Contains("CLEARED TO LAND")) instruction.ClearedToLand = true;
        // "cleared for the approach", "cleared for approach" and "cleared approach" are
        // the same clearance; "cleared for approach runway 25L" matched none of the
        // original two spellings, so the pilot treated an approach it was cleared for as
        // uncleared.
        if (t.Contains("CLEARED FOR APPROACH") || t.Contains("CLEARED FOR THE APPROACH") ||
            t.Contains("CLEARED APPROACH")) instruction.ClearedForApproach = true;
        if (t.Contains("CLEARED TO CROSS")) instruction.ClearedToCross = true;
        if (t.Contains("HOLD POSITION")) instruction.HoldPosition = true;
        if (t.Contains("LINE UP AND WAIT")) instruction.LineUpAndWait = true;
        if (t.Contains("GO AROUND")) instruction.GoAround = true;

        // Pushback. A pushback has no clearance variable of its own - the simulator
        // publishes the tug, not the permission - so what a controller says about one is
        // the only text there is: "pushback approved" is a tug coming, and "pushback
        // complete" is the instruction to stop and release it. Ordered so "complete" is
        // read before "approved", because "pushback completed" contains neither confusion
        // but a phrase such as "pushback approved, pushback will commence" should not read
        // as the end of the manoeuvre.
        if (t.Contains("PUSHBACK COMPLETE") || t.Contains("PUSHBACK IS COMPLETE") ||
            t.Contains("PUSHBACK COMPLETED") || t.Contains("PUSHBACK FINISHED") ||
            t.Contains("PUSHBACK DONE"))
        {
            instruction.PushbackComplete = true;
        }
        else if (t.Contains("PUSHBACK APPROVED") || t.Contains("APPROVED PUSHBACK") ||
                 t.Contains("PUSHBACK GRANTED") || t.Contains("CLEARED TO PUSH BACK") ||
                 t.Contains("PUSHBACK AT YOUR OWN RISK"))
        {
            instruction.PushbackApproved = true;
        }
        if (t.Contains("RESUME OWN NAVIGATION") || t.Contains("RESUME NORMAL NAVIGATION")) instruction.ResumeOwnNavigation = true;

        // A handoff names the station next in charge and, in almost every phrasing, the
        // frequency to reach it on. The frequency used to be read only from a literal
        // "@119.500", so "CONTACT TOWER 118.700" produced a handoff with no frequency
        // and the radio never moved.
        if (t.Contains("CONTACT") || t.Contains("MONITOR"))
        {
            // The literal "@" form carries a bounded number of decimals: the old character
            // class was [\d.]+, so it swallowed a sentence-ending period and the resulting
            // "119.500." failed Parsing.TryDouble, which silently skipped the tune.
            var at = Regex.Match(t, @"@\s*(\d{1,3}(?:\.\d{1,4})?)");
            var mhz = Regex.Match(t, @"(\d{2,3}\.\d{1,3})");
            var value = at.Success ? at.Groups[1].Value : mhz.Success ? mhz.Groups[1].Value : null;
            if (value != null)
            {
                instruction.IsHandoff = true;
                instruction.HandoffFrequency = value;
            }
        }

        // Taxi route
        if (instruction.IsTaxiInstruction)
        {
            var taxiMatch = Regex.Match(t, @"TAXI\s+TO\s+(?:HOLDING\s+POINT\s+)?(?:RUNWAY\s+\d+[LRC]?\s+)?VIA\s+(.+?)(?:\.|$)");
            if (taxiMatch.Success)
            {
                instruction.TaxiRoute = taxiMatch.Groups[1].Value.Trim();
                ExtractTaxiWaypoints(instruction.TaxiRoute, instruction);
            }
            else
            {
                // Fallback: anything after "VIA"
                var viaIdx = t.IndexOf("VIA", StringComparison.OrdinalIgnoreCase);
                if (viaIdx >= 0)
                {
                    instruction.TaxiRoute = t[(viaIdx + 3)..].Trim(' ', '.');
                    ExtractTaxiWaypoints(instruction.TaxiRoute, instruction);
                }
            }
        }

        return instruction;
    }

    /// <summary>
    /// The words a controller puts between the verb and the level it has assigned:
    /// "descend now to 4000", "climb at your discretion to 7000", "climb unrestricted to
    /// 10000". The class the old pattern carried allowed only AND and TO, so every other
    /// spelling read as no altitude at all and the aircraft held the level it was on.
    /// </summary>
    private const string FillerWords = "AND|TO|NOW|THEN|IMMEDIATELY|QUICKLY|EXPEDITE|VIA|THE|AT|ON|" +
                                       "UNRESTRICTED|WHEN|READY|YOUR|PILOTS|DISCRETION|OUT|OF|FOR";

    /// <summary>
    /// The altitude a clearance assigns, in feet. Handles the forms controllers use:
    /// "FLIGHT LEVEL 100" / "FL 100" / "FL240" are pressure altitudes in hundreds of
    /// feet, while "5000 FEET" and "CLIMB TO 10,000" are feet directly. Thousands
    /// separators are optional and the result is null when nothing is assigned.
    /// </summary>
    private static int? AssignedAltitudeFt(string text)
    {
        // Flight levels first: they are the only form whose digits mean hundreds of feet,
        // so they have to be recognised before the bare-number forms could read "100" as
        // one hundred feet. The lookahead rejects a digit and a decimal fraction but
        // accepts a sentence-ending period, so "descend and maintain FL 240." is read
        // while "FL240.5" is not.
        var flightLevel = Regex.Match(text, @"\bFL(?:IGHT\s+LEVEL)?\s*(\d{1,3})(?!\d)(?!\.\d)");
        if (flightLevel.Success)
        {
            var level = Parsing.Int32(flightLevel.Groups[1].Value);
            // A flight level below 010 is not a clearance (FL 10 is 1000 ft, which is
            // always spelled out), and a four-digit "FL1000" is a different number again.
            return level >= 10 ? level * 100 : (int?)null;
        }

        // "... 5000 FEET", "... 3000 FT". A vertical rate is not a level: "climb 2000 feet
        // per minute to 5000" used to read as 2000, which is the rate the aircraft climbs
        // at rather than the level it climbs to.
        var feet = Regex.Match(text,
            @"(\d{1,2}(?:,\d{3})+|\d{3,6})\s*(?:FEET|FT)\b(?!\s*PER\s+MIN(?:UTE)?\b)");
        if (feet.Success)
        {
            var value = Feet(feet.Groups[1].Value);
            if (value > 0) return value;
        }

        // CLIMB/DESCEND/MAINTAIN/ALTITUDE, with whatever the controller inserts between the
        // verb and the level, because "descend and maintain" is the same instruction as
        // "maintain" and "climb via the BOG1A departure to 8000" is a climb to 8000. The
        // words in between are skipped rather than matched, so a named procedure or a
        // runway designator inside the clearance does not stop the search dead. Every
        // match is tried in turn: the number must stand alone and must not be a speed, a
        // Mach number or a vertical rate, which is what keeps "MAINTAIN 250 KNOTS",
        // "REDUCE SPEED TO 210" and "climb at 250 knots to 10000" - which used to bind to
        // the 250, or to a fragment of it - from being read as altitudes. A couple of
        // hundred feet is never an en-route assignment either.
        // Each skipped token is atomic: without the group a word run with no number in it
        // ("climb" followed by a departure name and nothing else) made the engine try every
        // way of splitting that run, which never finished on a long clearance.
        foreach (Match keyworded in Regex.Matches(text,
            @"(?:CLIMB|DESCEND|MAINTAIN|ALTITUDE|REACH|LEVEL)\b" +
            @"(?:[\s,]*(?>\b(?:" + FillerWords + @")\b|[A-Z0-9]{1,15}))*?" +
            @"[\s,](\d{1,2}(?:,\d{3})+|\d{3,6})(?!\d)" +
            @"(?!\s*(?:KNOTS|KTS|KT|MACH|KM/?H|MPH))" +
            @"(?!\s*(?:(?:FEET|FT|FPM)\s+)?PER\s+MIN(?:UTE)?\b)" +
            @"(?!\s*FPM\b)"))
        {
            var value = Feet(keyworded.Groups[1].Value);
            if (value >= 500) return value;
        }

        return null;
    }

    /// <summary>Parses a number that may carry thousands separators, e.g. "10,000".</summary>
    private static int Feet(string digits) =>
        Parsing.Int32(digits.Replace(",", ""));

    /// <summary>
    /// The heading a clearance assigns, normalised into [0, 360). "TURN LEFT 10 DEGREES"
    /// and "HEADING 360" both mean something a pilot acts on, and a value of 360 is the
    /// same direction as 0 rather than a wrap-around.
    /// </summary>
    private static int? AssignedHeadingDeg(string text)
    {
        var m = Regex.Match(text, @"(?:HEADING|HDG|COURSE)\s*(\d{1,3})");
        if (!m.Success)
            m = Regex.Match(text, @"TURN\s+(?:LEFT|RIGHT)\s+(?:TO\s+)?(?:HEADING\s+)?(\d{1,3})");
        if (!m.Success)
            m = Regex.Match(text, @"(?:TURN|FLY|STEER)\s+(?:TO\s+)?(\d{1,3})\s*DEGREES");
        if (!m.Success) return null;

        var hdg = Parsing.Int32(m.Groups[1].Value);
        return hdg % 360;
    }

    /// <summary>
    /// The fix a direct-to clearance names, or null when there is none. "WHEN ABLE FLY
    /// DIRECT TO GLASG" and "PROCEED DIRECT DOGAL" are the same instruction as "DIRECT
    /// DOGAL" to the pilot: a named fix, resolved from the navigation database.
    /// </summary>
    private static string? AssignedDirectFix(string text)
    {
        // "THE" between the verb and the fix ("direct to THE GLASG") is not part of the fix
        // name, so it is consumed by the pattern rather than captured as a fix the nav
        // database then fails to resolve.
        var m = Regex.Match(text, @"\bDIRECT\s+(?:TO\s+)?(?:THE\s+)?([A-Z0-9]{3,5})\b");
        if (!m.Success) m = Regex.Match(text, @"\bPROCEED\s+TO\s+(?:THE\s+)?([A-Z0-9]{3,5})\b");
        if (!m.Success) return null;

        var name = m.Groups[1].Value;
        // A runway designator or a filler word is not a fix: "direct to runway 25" is the
        // runway instruction, and an article or preposition must never be looked up as a fix.
        if (name is "RWY" or "RUNWAY" or "THE" or "AND" or "FOR" or "ON" or "VIA" or "ATO" or "INTO" or "TO")
            return null;
        return name;
    }

    /// <summary>Punctuation that ends a clause; a wind report and an assignment share none.</summary>
    private static readonly char[] ClauseEnds = { ',', '.', ';' };

    /// <summary>
    /// A wind report, as a controller reads one out: "WIND 270 AT 120 KNOTS". The number
    /// after it is the wind, not an airspeed that has been assigned.
    /// </summary>
    private static readonly Regex WindReport = new(@"\bWIND\s+\d{2,3}\b", RegexOptions.Compiled);

    /// <summary>The indicated airspeed a clearance assigns, in knots.</summary>
    private static int? AssignedSpeedKnots(string text)
    {
        var m = Regex.Match(text, @"(?:SPEED|SPD)\s*(?:TO\s*)?(\d{2,3})(?![\d.])");
        if (m.Success) return Parsing.Int32(m.Groups[1].Value);

        // A bare unit is only an assignment when the number is not the wind. The pattern
        // used to read "wind 270 at 120 knots" as an airspeed of 120 kt, so the pilot
        // slowed the aircraft to the surface wind; every match is tried in turn and the
        // ones sitting in a wind report are skipped.
        foreach (Match bare in Regex.Matches(text, @"(\d{2,3})\s*(?:KNOTS|KTS|KT)\b"))
        {
            var before = text[..bare.Index];
            var clause = before[(before.LastIndexOfAny(ClauseEnds) + 1)..];
            if (WindReport.IsMatch(clause)) continue;
            return Parsing.Int32(bare.Groups[1].Value);
        }

        return null;
    }

    /// <summary>
    /// The Mach number a clearance assigns. "MACH 78" is the spoken shorthand for 0.78,
    /// which the parser used to read as a speed of 78 knots.
    /// </summary>
    private static double? AssignedMach(string text)
    {
        var m = Regex.Match(text, @"MACH\s*(?:TO\s*)?\d?[.](\d{1,2})");
        if (m.Success) return Parsing.Double($"0.{m.Groups[1].Value}");

        // The lookahead rejects a digit and a decimal fraction but accepts a
        // sentence-ending period, so "Maintain Mach 78." is read as 0.78. The old
        // (?![\d.]) refused the period and the whole clearance came out as no Mach at all.
        m = Regex.Match(text, @"MACH\s*(?:TO\s*)?(\d{2,3})(?!\d)(?!\.\d)");
        if (m.Success)
        {
            var spoken = Parsing.Int32(m.Groups[1].Value);
            if (spoken is >= 40 and <= 99) return spoken / 100.0;
        }

        return null;
    }

    private static void ExtractTaxiWaypoints(string route, AtcInstruction instruction)
    {
        var parts = route.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var clean = part.Trim().ToUpperInvariant();
            if (clean is "VIA" or "AND" or "TO" or "HOLD" or "SHORT" or "CROSS" or "OF" or "RUNWAY") continue;
            if (clean.Length == 0) continue;
            if (Regex.IsMatch(clean, @"^[A-Z0-9]+$"))
            {
                instruction.TaxiWaypoints.Add(clean);
            }
        }
    }
}

public sealed class AtcInstruction
{
    public string RawText { get; set; } = "";
    public string Station { get; set; } = "";
    public DateTime Timestamp { get; set; }
    /// <summary>Which ATC produced the instruction (sim ATC or BeyondATC).</summary>
    public AtcSource Source { get; set; } = AtcSource.Native;
    /// <summary>Plain-language clearance kind the instruction carries, for logging.</summary>
    public string? ClearanceType { get; set; }

    public string? Runway { get; set; }
    public int? HeadingDeg { get; set; }
    /// <summary>
    /// A fix the controller has sent the aircraft to ("direct to GLASG"). A name, not a
    /// position: the navigation database resolves it.
    /// </summary>
    public string? DirectToFix { get; set; }
    public int? AltitudeFt { get; set; }
    public int? SpeedKnots { get; set; }
    public double? Mach { get; set; }
    public string? Squawk { get; set; }
    public string? TaxiRoute { get; set; }
    public List<string> TaxiWaypoints { get; } = new();
    public string? HandoffFrequency { get; set; }

    public bool IsTaxiInstruction { get; set; }
    public bool HoldShort { get; set; }
    public bool HoldPosition { get; set; }
    public bool LineUpAndWait { get; set; }
    public bool CrossRunway { get; set; }
    public bool ClearedToCross { get; set; }
    /// <summary>
    /// True when the aircraft has been told to enter the runway and back-taxi it -
    /// "enter runway 25 and backtrack, line up and wait". The manoeuvre itself is decided
    /// geometrically as well (see <see cref="AIPilot"/>), so this flag is the controller's
    /// confirmation of a decision the pilot also reaches on its own.
    /// </summary>
    public bool Backtrack { get; set; }
    public bool ClearedForTakeoff { get; set; }
    public bool ClearedToLand { get; set; }
    public bool ClearedForApproach { get; set; }
    /// <summary>
    /// Pushback approved: a tug is on its way. A pushback is the one clearance whose
    /// approval is not published as a variable at all - the simulator reports the tug -
    /// so this is the only text that says the manoeuvre was granted rather than merely
    /// requested.
    /// </summary>
    public bool PushbackApproved { get; set; }
    /// <summary>
    /// Pushback complete: the manoeuvre is over and the tug is to be released. This is
    /// what "dismissing it" means to a controller, and it arrives before the aircraft can
    /// know geometrically that it has been pushed far enough.
    /// </summary>
    public bool PushbackComplete { get; set; }
    public bool GoAround { get; set; }
    public bool IsHandoff { get; set; }
    public bool ResumeOwnNavigation { get; set; }
}