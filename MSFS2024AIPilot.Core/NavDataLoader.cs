using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Reads the published navigation database out of the simulator through the facilities
/// API: the approaches at an airport (with their final approach legs and their published
/// missed approaches) and the position of any fix by ICAO code.
/// <para>
/// This is the only route to navdata from an out-of-process client. The legacy
/// <c>fs9gps</c> GPS variables that used to expose the flight plan are deprecated in
/// MSFS 2024, and the planned-route API is a WASM feature; the facilities API is the
/// documented source for a SimConnect client.
/// </para>
/// </summary>
public sealed class NavDataLoader : IDisposable
{
    /// <summary>How many fixes may be looked up at once, so a long route cannot flood the sim.</summary>
    public const int MaxConcurrentFixLookups = 6;

    private const uint ApproachDefineId = 8101;
    private const uint TransitionDefineId = 8102;
    private const uint FinalLegDefineId = 8103;
    private const uint MissedLegDefineId = 8104;
    private const uint WaypointDefineId = 8105;
    private const uint NdbDefineId = 8106;
    private const uint VorDefineId = 8107;

    private readonly SimConnectManager _sc;
    private readonly Dictionary<uint, PendingNavRequest> _pending = new();
    private readonly Dictionary<string, AirportApproaches> _airports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NavFix> _fixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fixLookupFailed = new(StringComparer.OrdinalIgnoreCase);
    // Counts how many of a fix's three lookups (waypoint, NDB, VOR) have ended. A fix is
    // only recorded as unknown when every one of them has ended without an answer.
    private readonly Dictionary<string, int> _fixEndedRequests = new(StringComparer.OrdinalIgnoreCase);
    private uint _nextRequestId = 8200;
    private bool _definitionsBuilt;

    public NavDataLoader(SimConnectManager sc)
    {
        _sc = sc;
        _sc.OnFacilityData += OnFacilityData;
        _sc.OnFacilityDataEnd += OnFacilityDataEnd;
        _sc.OnError += OnSimConnectError;
    }

    public event Action<string, IReadOnlyList<ApproachInfo>>? OnApproachesLoaded;
    public event Action<string>? OnError;

    /// <summary>True once the approaches of the airport have arrived and been assembled.</summary>
    public bool HasApproaches(string icao) =>
        _airports.TryGetValue(icao?.ToUpperInvariant() ?? "", out var a) && a.Loaded;

    /// <summary>The approaches published at the airport, empty when none have loaded.</summary>
    public IReadOnlyList<ApproachInfo> GetApproaches(string icao) =>
        _airports.TryGetValue(icao?.ToUpperInvariant() ?? "", out var a)
            ? a.Approaches
            : Array.Empty<ApproachInfo>();

    /// <summary>A fix that has been resolved from the navdata, or null when it is unknown.</summary>
    public NavFix? GetFix(string icao) =>
        _fixes.TryGetValue(icao?.ToUpperInvariant() ?? "", out var fix) ? fix : null;

    /// <summary>True when a fix has been looked for and could not be resolved.</summary>
    public bool IsFixUnknown(string icao)
    {
        // A fix whose lookup is still outstanding is not yet unknown, and saying so would
        // turn a wait into a wrong answer.
        var key = icao?.ToUpperInvariant() ?? "";
        return _fixLookupFailed.Contains(key) &&
               !_pending.Values.Any(p => p.RequestType == PendingNavRequestKind.Fix &&
                                         string.Equals(p.Icao, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asks the simulator for the approaches published at <paramref name="icao"/>. The
    /// response is asynchronous and raises <see cref="OnApproachesLoaded"/>; a request for
    /// an airport that is already loaded is ignored.
    /// </summary>
    public void RequestApproaches(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao) || !_sc.IsConnected) return;

        var key = icao.ToUpperInvariant();
        if (_airports.TryGetValue(key, out var existing) && existing.Requested)
        {
            // Already asked (or already loaded): asking again re-numbers every approach
            // and the legs would arrive as a second copy.
            return;
        }

        if (!_definitionsBuilt)
        {
            BuildDefinitions();
            _definitionsBuilt = true;
        }

        var container = _airports.TryGetValue(key, out var found) ? found : new AirportApproaches();
        container.Requested = true;
        _airports[key] = container;

        // Four definitions rather than one: the approach headers, the transitions, the
        // final legs and the missed legs each have their own payload layout, and a
        // definition the simulator rejects for one record type must not take the rest
        // of the approach down with it.
        Send(ApproachDefineId, key, PendingNavRequestKind.Approach);
        Send(TransitionDefineId, key, PendingNavRequestKind.Approach);
        Send(FinalLegDefineId, key, PendingNavRequestKind.Approach);
        Send(MissedLegDefineId, key, PendingNavRequestKind.Approach);
    }
    /// <summary>
    /// Looks the position of one fix up by ICAO code. The lookup is one-shot: a fix that
    /// resolves is cached, one that does not is remembered as unknown so a route with a
    /// name the database does not carry does not re-request it on every frame.
    /// </summary>
    public bool RequestFix(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao) || !_sc.IsConnected) return false;

        var key = icao.ToUpperInvariant();
        if (_fixes.ContainsKey(key) || _fixLookupFailed.Contains(key)) return true;

        if (!_definitionsBuilt)
        {
            BuildDefinitions();
            _definitionsBuilt = true;
        }

        var outstanding = _pending.Values
            .Where(p => p.RequestType == PendingNavRequestKind.Fix)
            .Select(p => p.Icao)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (outstanding >= MaxConcurrentFixLookups) return false;

        // A fix may be a waypoint, an NDB or a VOR and the database publishes no index of
        // which, so all three are asked - a fix that resolves on the second or third
        // answer is still the fix the route named. Each is its own definition, and a
        // lookup is only "unknown" when every one of them has ended without an answer.
        Send(WaypointDefineId, key, PendingNavRequestKind.Fix);
        Send(NdbDefineId, key, PendingNavRequestKind.Fix);
        Send(VorDefineId, key, PendingNavRequestKind.Fix);
        return true;
    }

    /// <summary>How many fixes are still being looked up.</summary>
    public int PendingFixLookups => _pending.Values.Count(p => p.RequestType == PendingNavRequestKind.Fix);

    /// <summary>
    /// Drops requests the simulator has not answered. A request for an airport or a fix it
    /// does not carry can simply never be answered, and a pending entry that outlives its
    /// patience would hold a fix-lookup slot for the session.
    /// </summary>
    private const double RequestTimeoutSeconds = 30;

    /// <summary>
    /// Advances the request bookkeeping. Called once per control loop: expiry used to run
    /// only inside <see cref="Send"/>, so an unanswered facility request held its pending
    /// entry - and its concurrency slot - for the session, and because the route is rebuilt
    /// only when the outstanding count FALLS, six never-answered fix lookups pinned the
    /// count and the route kept the hole it was built with until some unrelated event
    /// forced a rebuild.
    /// </summary>
    public void Tick() => ExpireRequests();

    private void ExpireRequests()
    {
        if (_pending.Count == 0) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(RequestTimeoutSeconds);
        foreach (var stale in _pending.Values.Where(p => p.StartedUtc < cutoff).ToList())
        {
            _pending.Remove(stale.requestId);

            if (stale.RequestType == PendingNavRequestKind.Fix)
            {
                var key = stale.Icao;
                if (_fixEndedRequests.ContainsKey(key))
                    _fixEndedRequests[key] = _fixEndedRequests[key] + 1;
                else
                    _fixEndedRequests[key] = 1;

                var stillOutstanding = _pending.Values.Any(p =>
                    p.RequestType == PendingNavRequestKind.Fix &&
                    string.Equals(p.Icao, key, StringComparison.OrdinalIgnoreCase));

                if (!_fixes.ContainsKey(key) && !stillOutstanding)
                {
                    _fixLookupFailed.Add(key);
                }
            }
            else
            {
                // Whatever did arrive for that request is still worth following, so the
                // outstanding set is emptied rather than left waiting for an answer that
                // is never coming - and the completion path runs, which leaving the entry
                // in the set would have skipped.
                AnswerApproachRequest(stale.Icao, stale.requestId);
            }

            OnError?.Invoke($"[NavData] no answer for {stale.Icao} ({stale.RequestType}) " +
                            $"after {RequestTimeoutSeconds:F0} s");
        }
    }

    /// <summary>
    /// Records that one approach request has been answered - either it ended normally or
    /// its patience ran out - and raises the completion when it was the last one
    /// outstanding. Both paths go through here, so a request the simulator never finished
    /// cannot leave an airport permanently unloaded while the others answered.
    /// </summary>
    private void AnswerApproachRequest(string icao, uint requestId)
    {
        var container = Container(icao);
        if (!container.OutstandingApproachRequests.Remove(requestId)) return;
        if (container.OutstandingApproachRequests.Count > 0) return;

        // Already told: a second completion for one airport is not new information, and
        // raising twice rebuilt the route twice for a single answer.
        if (container.Loaded) return;

        container.Loaded = true;
        var approaches = container.Approaches.Where(a => a.HasFinalApproach || a.HasMissedApproach).ToList();
        if (approaches.Count > 0) OnApproachesLoaded?.Invoke(icao, approaches);
    }

    private void Send(uint defineId, string icao, PendingNavRequestKind kind)
    {
        ExpireRequests();

        var requestId = _nextRequestId++;
        _pending[requestId] = new PendingNavRequest
        {
            Icao = icao,
            RequestType = kind,
            StartedUtc = DateTime.UtcNow,
            requestId = requestId
        };

        if (kind == PendingNavRequestKind.Approach)
        {
            // The approach is complete when all four of its requests have ended, so the
            // set of outstanding ones is what decides when it has arrived.
            Container(icao).OutstandingApproachRequests.Add(requestId);
        }

        _sc.RequestFacilityData(defineId, requestId, icao);
    }
    /// <summary>
    /// Builds the definitions by feeding every field of every one to the simulator, in
    /// payload order. The structures themselves are <see cref="FacilityDefinitions"/>, so
    /// they can be reviewed and tested without a simulator connected.
    /// </summary>
    private void BuildDefinitions()
    {
        foreach (var definition in FacilityDefinitions())
        {
            foreach (var field in definition.Fields)
            {
                Add(definition.DefinitionId, field);
            }
        }
    }

    /// <summary>
    /// Every facility field this client asks for, per definition, in payload order.
    /// <para>
    /// Pure, because the structure is what the simulator enforces and it has to be
    /// checkable without a running sim: a definition must open with one of the four
    /// entry points (AIRPORT, WAYPOINT, NDB, VOR), every member must nest inside a
    /// container, and a child must open before its parent closes. A definition that
    /// breaks any of those is answered field by field with
    /// <c>DATA_ERROR ... [add facility field 'X' to definition N]</c>, and after the
    /// first rejection every later field of that definition fails with it - which is
    /// how the approach, transition and leg requests silently went unanswered: the
    /// APPROACH header request declared no entry point at all, the transition, final
    /// and missed leg requests opened their leg blocks after closing APPROACH, and
    /// the fix request opened NDB and VOR as siblings of WAYPOINT in one definition.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<FacilityDefinition> FacilityDefinitions()
    {
        // Approach headers: what the approach is, and which runway it serves.
        //
        // Every one of the four approach-request definitions declares the SAME APPROACH
        // field list. The header is the identity of the approach the legs belong to, and a
        // request whose APPROACH record carried only RUNWAY_NUMBER, RUNWAY_DESIGNATOR,
        // TYPE and SUFFIX sent <see cref="ApproachRecordParser.ParseApproach"/> four fields
        // short: FafHeadingDeg then read past the end of the payload, the record was
        // discarded as an exception, the request held no ActiveApproach, and
        // AppendLeg/ParseTransition dropped every transition, final and missed leg they
        // were handed - so no approach ever carried legs and no missed approach was ever
        // assembled.
        var approachHeader = new List<string> { "OPEN AIRPORT", "ICAO", "MAGVAR", "OPEN APPROACH" };
        approachHeader.AddRange(ApproachFields());
        approachHeader.Add("CLOSE APPROACH");
        approachHeader.Add("CLOSE AIRPORT");

        // Approach transitions and the legs that fly them. The transitions belong to the
        // approach, so OPEN APPROACH_TRANSITION sits inside OPEN APPROACH, and its legs
        // inside the transition: a leg block opened after CLOSE APPROACH is a child of
        // nothing, which the simulator rejects field by field.
        //
        // MAGVAR sits after ICAO in every AIRPORT record; leaving it out here left this
        // definition's airport payload 4 bytes short of what ParseAirport reads, so every
        // transition parse threw and its magnetic variation was dropped.
        //
        // The APPROACH_TRANSITION payload order is exactly the one the MSFS 2024 docs
        // publish: TYPE, IAF_ICAO, IAF_REGION, IAF_TYPE, IAF_ALTITUDE, DME_ARC_ICAO,
        // DME_ARC_REGION, DME_ARC_TYPE, DME_ARC_RADIAL, DME_ARC_DISTANCE, NAME,
        // N_APPROACH_LEGS, then the APPROACH_LEG child. Getting the order wrong is silent
        // garbage rather than an error, which is why the parser is pinned by tests.
        var transition = new List<string> { "OPEN AIRPORT", "ICAO", "MAGVAR", "OPEN APPROACH" };
        transition.AddRange(ApproachFields());
        transition.AddRange(new[]
        {
            "OPEN APPROACH_TRANSITION",
            "TYPE",
            "IAF_ICAO",
            "IAF_REGION",
            "IAF_TYPE",
            "IAF_ALTITUDE",
            "DME_ARC_ICAO",
            "DME_ARC_REGION",
            "DME_ARC_TYPE",
            "DME_ARC_RADIAL",
            "DME_ARC_DISTANCE",
            "NAME",
            "N_APPROACH_LEGS",
            "OPEN APPROACH_LEG"
        });
        transition.AddRange(LegFields());
        transition.AddRange(new[] { "CLOSE APPROACH_LEG", "CLOSE APPROACH_TRANSITION" });
        transition.Add("CLOSE APPROACH");
        transition.Add("CLOSE AIRPORT");

        // The published final approach legs are children of the approach itself, so the
        // leg block is opened while the approach is still open, not after closing it.
        var finalLegs = new List<string> { "OPEN AIRPORT", "ICAO", "MAGVAR", "OPEN APPROACH" };
        finalLegs.AddRange(ApproachFields());
        finalLegs.Add("OPEN FINAL_APPROACH_LEG");
        finalLegs.AddRange(LegFields());
        finalLegs.AddRange(new[] { "CLOSE FINAL_APPROACH_LEG", "CLOSE APPROACH", "CLOSE AIRPORT" });

        // The published missed approach legs nest the same way - the missed approach is
        // a route that belongs to the approach, not a sibling of it.
        var missedLegs = new List<string> { "OPEN AIRPORT", "ICAO", "MAGVAR", "OPEN APPROACH" };
        missedLegs.AddRange(ApproachFields());
        missedLegs.Add("OPEN MISSED_APPROACH_LEG");
        missedLegs.AddRange(LegFields());
        missedLegs.AddRange(new[] { "CLOSE MISSED_APPROACH_LEG", "CLOSE APPROACH", "CLOSE AIRPORT" });

        // A fix by ICAO: each facility type publishes what it carries, and an entry point
        // that names no ident does not have one to declare - a field the simulator does not
        // have would be rejected and the whole definition with it. The ICAO being asked for
        // is the ICAO the answer is for, so none of these need to echo it.
        //
        // One entry point per definition: the fix lookup used to open WAYPOINT, close it,
        // and then open NDB and VOR in the same definition, and the simulator refused the
        // second entry point - so every NDB and VOR field failed and an NDB or VOR fix
        // never resolved, which left the route holding a hole it never filled.
        //
        // The NDB and VOR payloads carry every field the MSFS 2024 docs publish for them.
        // The fix lookup only reads the position (LATITUDE/LONGITUDE/ALTITUDE for NDB,
        // VOR_LATITUDE/VOR_LONGITUDE/VOR_ALTITUDE for VOR), so the extra fields are
        // requested but ignored; a definition that omits them is answered field by field
        // with DATA_ERROR and the fix never resolves.
        return new[]
        {
            new FacilityDefinition(ApproachDefineId, approachHeader),
            new FacilityDefinition(TransitionDefineId, transition),
            new FacilityDefinition(FinalLegDefineId, finalLegs),
            new FacilityDefinition(MissedLegDefineId, missedLegs),
            new FacilityDefinition(WaypointDefineId, new[]
            {
                "OPEN WAYPOINT", "LATITUDE", "LONGITUDE", "ALTITUDE", "ICAO", "REGION", "CLOSE WAYPOINT"
            }),
            new FacilityDefinition(NdbDefineId, new[]
            {
                "OPEN NDB", "LATITUDE", "LONGITUDE", "ALTITUDE", "FREQUENCY", "TYPE", "RANGE",
                "MAGVAR", "IS_TERMINAL_NDB", "NAME", "BFO_REQUIRED", "CLOSE NDB"
            }),
            new FacilityDefinition(VorDefineId, new[]
            {
                "OPEN VOR", "VOR_LATITUDE", "VOR_LONGITUDE", "VOR_ALTITUDE",
                "DME_LATITUDE", "DME_LONGITUDE", "DME_ALTITUDE",
                "GS_LATITUDE", "GS_LONGITUDE", "GS_ALTITUDE",
                "TACAN_LATITUDE", "TACAN_LONGITUDE", "TACAN_ALTITUDE",
                "IS_NAV", "IS_DME", "IS_TACAN", "HAS_GLIDE_SLOPE",
                "DME_AT_NAV", "DME_AT_GLIDE_SLOPE", "HAS_BACK_COURSE",
                "FREQUENCY", "TYPE", "NAV_RANGE", "MAGVAR",
                "LOCALIZER", "LOCALIZER_WIDTH", "GLIDE_SLOPE", "NAME",
                "DME_BIAS", "LS_CATEGORY", "IS_TRUE_REFERENCED", "CLOSE VOR"
            })
        };
    }

    private void Add(uint defineId, string field) => _sc.AddToFacilityDefinition(defineId, field);

    /// <summary>One facility definition: its SimConnect ID and its fields in payload order.</summary>
    internal sealed record FacilityDefinition(uint DefinitionId, IReadOnlyList<string> Fields);

    /// <summary>
    /// The procedure leg fields, in the order the SimConnect documentation publishes them.
    /// Shared by every request that carries legs, so a parser and its definition can never
    /// drift apart by being written twice.
    /// </summary>
    private static string[] ApproachFields() => new[]
    {
        "TYPE",
        "SUFFIX",
        "RUNWAY_NUMBER",
        "RUNWAY_DESIGNATOR",
        "FAF_ICAO",
        "FAF_REGION",
        "FAF_HEADING",
        "FAF_ALTITUDE",
        "FAF_TYPE",
        "MISSED_ALTITUDE",
        "HAS_LNAV",
        "HAS_LNAVVNAV",
        "HAS_LP",
        "HAS_LPV",
        "N_TRANSITIONS",
        "N_FINAL_APPROACH_LEGS",
        "N_MISSED_APPROACH_LEGS",
    };

    private static string[] LegFields() => new[]
    {
        "TYPE",
        "FIX_ICAO",
        "FIX_REGION",
        "FIX_TYPE",
        "FIX_LATITUDE",
        "FIX_LONGITUDE",
        "FIX_ALTITUDE",
        "FLY_OVER",
        "DISTANCE_MINUTE",
        "TRUE_DEGREE",
        "TURN_DIRECTION",
        "ORIGIN_ICAO",
        "ORIGIN_REGION",
        "ORIGIN_TYPE",
        "ORIGIN_LATITUDE",
        "ORIGIN_LONGITUDE",
        "ORIGIN_ALTITUDE",
        "THETA",
        "RHO",
        "COURSE",
        "ROUTE_DISTANCE",
        "APPROACH_ALT_DESC",
        "ALTITUDE1",
        "ALTITUDE2",
        "SPEED_LIMIT",
        "VERTICAL_ANGLE",
        "ARC_CENTER_FIX_ICAO",
        "ARC_CENTER_FIX_REGION",
        "ARC_CENTER_FIX_TYPE",
        "ARC_CENTER_FIX_LATITUDE",
        "ARC_CENTER_FIX_LONGITUDE",
        "ARC_CENTER_FIX_ALTITUDE",
        "IS_IAF",
        "IS_IF",
        "IS_FAF",
        "IS_MAP",
    };

    private void OnFacilityData(FacilityDataRecord rec)
    {
        if (!_pending.TryGetValue(rec.UserRequestId, out var request)) return;

        try
        {
            switch (rec.Type)
            {
                case FacilityDataType.Airport:
                    ParseAirport(rec, request);
                    break;
                case FacilityDataType.Approach:
                    ParseApproach(rec, request);
                    break;
                case FacilityDataType.ApproachLeg:
                    AppendLeg(rec, request);
                    break;
                case FacilityDataType.FinalApproachLeg:
                    AppendLeg(rec, request);
                    break;
                case FacilityDataType.MissedApproachLeg:
                    AppendLeg(rec, request);
                    break;
                case FacilityDataType.ApproachTransition:
                    ParseTransition(rec, request);
                    break;
                case FacilityDataType.Waypoint:
                    ParseFix(rec, request, ApproachRecordParser.ParseWaypoint(rec));
                    break;
                case FacilityDataType.Ndb:
                    ParseFix(rec, request, ApproachRecordParser.ParseNdb(rec));
                    break;
                case FacilityDataType.Vor:
                    ParseFix(rec, request, ApproachRecordParser.ParseVor(rec));
                    break;
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"[NavData] could not read a {rec.Type} record for {request.Icao}: {ex.Message}");
        }
    }

    private void ParseAirport(FacilityDataRecord rec, PendingNavRequest request)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var icao = r.ReadString(8);
        var magvar = r.ReadFloat32();

        var container = Container(request.Icao);
        container.MagneticVariationDeg = magvar;
        if (!string.IsNullOrEmpty(icao)) container.Icao = icao;
    }

    private void ParseApproach(FacilityDataRecord rec, PendingNavRequest request)
    {
        var container = Container(request.Icao);
        var approach = ApproachRecordParser.ParseApproach(rec);

        // Each of the four requests reports every approach, so a header arriving twice
        // is the normal case and must not duplicate the approach.
        var existing = container.Approaches.FirstOrDefault(a => a.Key == approach.Key);
        if (existing is not null)
        {
            // The request that carries the missed altitude is the one that has it.
            if (approach.MissedAltitudeFt > 0) existing.MissedAltitudeFt = approach.MissedAltitudeFt;
            if (!string.IsNullOrEmpty(approach.FafIcao)) existing.FafIcao = approach.FafIcao;
            if (approach.FafAltitudeFt > 0) existing.FafAltitudeFt = approach.FafAltitudeFt;
            existing.HasLnav |= approach.HasLnav;
            existing.HasLnavVnav |= approach.HasLnavVnav;
            existing.HasLp |= approach.HasLp;
            existing.HasLpv |= approach.HasLpv;
            request.ActiveApproach = existing;
        }
        else
        {
            container.Approaches.Add(approach);
            request.ActiveApproach = approach;
        }
    }

    private void AppendLeg(FacilityDataRecord rec, PendingNavRequest request)
    {
        if (request.ActiveApproach is null) return;

        var leg = ApproachRecordParser.ParseLeg(rec);
        if (!leg.HasPosition) return;

        switch (rec.Type)
        {
            case FacilityDataType.FinalApproachLeg:
                request.ActiveApproach.FinalLegs.Add(leg);
                break;
            case FacilityDataType.MissedApproachLeg:
                request.ActiveApproach.MissedLegs.Add(leg);
                break;
            default:
                request.ActiveTransition?.Legs.Add(leg);
                break;
        }
    }

    private void ParseTransition(FacilityDataRecord rec, PendingNavRequest request)
    {
        if (request.ActiveApproach is null) return;

        var transition = ApproachRecordParser.ParseTransition(rec);
        var existing = request.ActiveApproach.Transitions
            .FirstOrDefault(t => string.Equals(t.Name, transition.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            request.ActiveApproach.Transitions.Add(transition);
            request.ActiveTransition = transition;
        }
        else
        {
            request.ActiveTransition = existing;
        }
    }

    private void ParseFix(FacilityDataRecord rec, PendingNavRequest request, NavFix fix)
    {
        if (!fix.HasPosition) return;

        // The NDB and VOR entry points publish no ident, so the one asked for is the one the
        // answer is for.
        fix.Icao = request.Icao;
        _fixes[request.Icao] = fix;
    }

    private void OnFacilityDataEnd(uint userRequestId)
    {
        if (!_pending.TryGetValue(userRequestId, out var request)) return;
        _pending.Remove(userRequestId);

        // Track which of a fix's three lookups has ended. One fix is asked of three
        // definitions (waypoint, NDB, VOR), and the first end message must not record
        // the fix as unknown while the other two are still in flight - an NDB fix would
        // be discarded by the waypoint request's "not found" while the NDB answer was
        // on its way. The failure is recorded only when every request for that fix has
        // ended without an answer.
        if (request.RequestType == PendingNavRequestKind.Fix)
        {
            var key = request.Icao;
            if (_fixEndedRequests.ContainsKey(key))
                _fixEndedRequests[key] = _fixEndedRequests[key] + 1;
            else
                _fixEndedRequests[key] = 1;

            var stillOutstanding = _pending.Values.Any(p =>
                p.RequestType == PendingNavRequestKind.Fix &&
                string.Equals(p.Icao, key, StringComparison.OrdinalIgnoreCase));

            if (!_fixes.ContainsKey(key) && !stillOutstanding)
            {
                // The database has no such fix under that ICAO. Remembering that is what
                // keeps a route with an unresolvable name from re-requesting it forever.
                _fixLookupFailed.Add(key);
            }
            return;
        }

        AnswerApproachRequest(request.Icao, userRequestId);
    }

    private void OnSimConnectError(string message)
    {
        // The SimConnect connection is shared with the whole pilot. Only errors from the
        // navdata facility definitions belong here; an unrelated checklist event failure
        // otherwise lands in the log as a bogus [NavData] error and hides the real source.
        if (!IsNavDataSimConnectError(message)) return;
        OnError?.Invoke($"[NavData] {message}");
    }

    internal static bool IsNavDataSimConnectError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (message.Contains("facility", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var defineId in new[]
                 { ApproachDefineId, TransitionDefineId, FinalLegDefineId, MissedLegDefineId, WaypointDefineId, NdbDefineId, VorDefineId })
        {
            if (message.Contains($"definition {defineId}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private AirportApproaches Container(string icao)
    {
        var key = icao.ToUpperInvariant();
        if (!_airports.TryGetValue(key, out var container))
        {
            container = new AirportApproaches();
            _airports[key] = container;
        }
        return container;
    }

    public void Dispose()
    {
        _sc.OnFacilityData -= OnFacilityData;
        _sc.OnFacilityDataEnd -= OnFacilityDataEnd;
        _sc.OnError -= OnSimConnectError;
    }

    private sealed class PendingNavRequest
    {
        public string Icao { get; set; } = "";
        public PendingNavRequestKind RequestType { get; set; }
        public DateTime StartedUtc { get; set; }
        public uint requestId;
        public ApproachInfo? ActiveApproach { get; set; }
        public ApproachTransition? ActiveTransition { get; set; }
    }

    private enum PendingNavRequestKind
    {
        Approach,
        Fix
    }

    private sealed class AirportApproaches
    {
        public string Icao { get; set; } = "";
        public bool Requested { get; set; }
        public bool Loaded { get; set; }
        /// <summary>
        /// The approach requests still outstanding for this airport. When the set empties,
        /// everything the simulator has to say about the airport's approaches has arrived.
        /// </summary>
        public HashSet<uint> OutstandingApproachRequests { get; } = new();
        public float MagneticVariationDeg { get; set; }
        public List<ApproachInfo> Approaches { get; } = new();
    }
}
