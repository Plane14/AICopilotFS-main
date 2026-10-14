using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// JSON options for the panel bridge. The panel is plain JavaScript and reads camelCase
/// names (status.aircraft.altitudeAGL), so the naming policy is set here rather than
/// left to the default, which is PascalCase.
/// </summary>
internal static class PanelJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Lightweight HTTP bridge so the in-sim HTML panel can talk to the host EXE.
/// Runs on localhost only.
/// </summary>
public sealed class PanelHttpBridge : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<AIPilot> _pilotProvider;
    private readonly string _prefix = "http://localhost:18540/aipilot/";
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public event Action<string>? OnLog;
    private bool _started;

    public PanelHttpBridge(Func<AIPilot> pilotProvider)
    {
        _pilotProvider = pilotProvider;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _loopTask = Task.Run(AcceptLoop);
            Log("HTTP bridge started on " + _prefix);
        }
        catch (Exception ex)
        {
            Log($"HTTP bridge failed to start: {ex.Message}. Panel will not connect.");
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"HTTP accept error: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        try
        {
            var subPath = req.Url?.LocalPath.TrimEnd('/').Substring("/aipilot".Length).TrimStart('/') ?? "";
            var pilot = _pilotProvider();

            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            string jsonResponse = "{}";

            switch (subPath)
            {
                case "status" when req.HttpMethod == "GET":
                    jsonResponse = BuildStatusJson(pilot);
                    break;

                case "toggle":
                    pilot.ToggleControl();
                    jsonResponse = BuildStatusJson(pilot);
                    break;

                case "engage":
                    pilot.SetControlState(PilotControlState.AiActive);
                    jsonResponse = BuildStatusJson(pilot);
                    break;

                case "disengage":
                    pilot.SetControlState(PilotControlState.Manual);
                    jsonResponse = BuildStatusJson(pilot);
                    break;

                case "load-latest-plan":
                    var latest = pilot.FindLatestFlightPlan();
                    if (latest != null) pilot.LoadFlightPlan(latest);
                    jsonResponse = BuildStatusJson(pilot);
                    break;

                case "atc":
                    var atcArg = req.QueryString["arg"];
                    var atcText = pilot.RunAtcCommand(atcArg);
                    jsonResponse = PanelJson.Serialize(new { ok = true, text = atcText });
                    break;
            }

            var buffer = Encoding.UTF8.GetBytes(jsonResponse);
            res.ContentType = "application/json";
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
            res.OutputStream.Close();
        }
        catch (Exception ex)
        {
            Log($"HTTP request error: {ex.Message}");
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
    }

    /// <summary>
    /// Serialises the status document the panel renders. Internal so the contract
    /// between the host and the in-sim JavaScript can be tested without MSFS.
    /// </summary>
    internal static string BuildStatus(AIPilot pilot) => BuildStatusJson(pilot);

    private static string BuildStatusJson(AIPilot pilot)
    {
        var s = pilot.State;
        var nav = s.Navigation;
        var status = new
        {
            state = pilot.ControlState.ToString(),
            phase = pilot.CurrentPhase.ToString(),
            // Asserts the SimConnect link itself, not the control mode: reporting the
            // panel as connected whenever AI control was not Idle made it lie after a
            // simulator disconnect, while the named-pipe path tests IsConnected.
            connected = pilot.SimConnected,
            holdShort = pilot.AtRunwayHoldShort,
            trafficRunwayEntrySafe = pilot.TrafficAllowsRunwayEntry,
            aircraft = new
            {
                s.AltitudeAGL,
                s.Altitude,
                s.IndicatedAirspeed,
                s.GroundSpeed,
                s.Heading,
                s.VerticalSpeed,
                s.GrossWeight,
                s.OnGround
            },
            navigation = new
            {
                nav.DepartureICAO,
                nav.ArrivalICAO,
                nav.DepartureRunway,
                nav.ArrivalRunway,
                nav.ApproachName,
                nav.CurrentWaypointIndex,
                nav.Waypoints.Count,
                nav.DistanceToNextWaypoint
            },
            airports = pilot.GetLoadedAirports().Select(a => new
            {
                a.Icao,
                nodes = a.Nodes.Count,
                runways = a.Runways.Count,
                a.PathResolutionRatio,
                frame = a.Frame.ToString(),
                holdShorts = a.Nodes.Values.Count(n =>
                    n.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort)
            }),
            nav = BuildNavJson(pilot),
            atc = BuildAtcJson(pilot),
            checklist = BuildChecklistJson(pilot),
            pushback = BuildPushbackJson(pilot),
            traffic = BuildTrafficJson(pilot),
            weather = BuildWeatherJson(pilot),
            limits = BuildLimitsJson(pilot)
        };
        return PanelJson.Serialize(status);
    }

    /// <summary>
    /// The navigation block: the route the pilot is following, the published approach it
    /// ends in, and the published missed approach that answers a go-around. The unresolved
    /// fixes are shown because a route whose names the database does not carry is the one
    /// way this can be silently wrong.
    /// </summary>
    private static object BuildNavJson(AIPilot pilot)
    {
        var nav = pilot.NavStatus();
        return new
        {
            legs = nav.RouteLegCount,
            geometryLegs = nav.RouteGeometryLegs,
            firstLeg = nav.FirstLeg,
            lastLeg = nav.LastLeg,
            unresolvedFixes = nav.UnresolvedFixes,
            activeLeg = nav.ActiveLeg,
            activeLegIndex = nav.ActiveLegIndex,
            activeLegDistanceMeters = nav.ActiveLegDistanceMeters,
            crossTrackMeters = nav.CrossTrackMeters,
            directToFix = nav.DirectToFix,
            publishedApproach = nav.ApproachText,
            approachType = nav.ApproachType,
            approachRunway = nav.ApproachRunway,
            hasPublishedApproach = nav.HasApproach,
            missedApproach = nav.MissedApproachText,
            missedApproachLegs = nav.MissedApproachLegs,
            arrivalApproachesLoaded = nav.ArrivalApproachesLoaded,
            arrivalApproachCount = nav.ArrivalApproachCount,
            hasPublishedMissedApproach = pilot.HasPublishedMissedApproach
        };
    }

    /// <summary>
    /// The ATC block: which controller is in charge, what it has cleared, and what the
    /// status command would print for the BeyondATC side.
    /// </summary>
    private static object BuildAtcJson(AIPilot pilot)
    {
        var atc = pilot.AtcStatus();
        return new
        {
            source = atc.Source.ToString(),
            preferred = atc.Preferred == AtcSource.None ? "auto" : atc.Preferred.ToString(),
            beyondAtcRunning = atc.BeyondAtcRunning,
            beyondAtcFound = atc.BeyondAtcLogFound,
            beyondAtcCallsign = atc.BeyondAtcCallsign,
            beyondAtcDepartureRunway = atc.BeyondAtcDepartureRunway,
            cpdlcEndpoints = atc.CpdlcEndpoints,
            station = atc.Station,
            stationType = atc.StationType,
            stationFrequency = atc.StationFrequency,
            nextStation = atc.NextStation,
            nextStationFrequency = atc.NextStationFrequency,
            cleared = new
            {
                ifr = atc.ClearedIfr,
                taxi = atc.ClearedTaxi,
                takeoff = atc.ClearedTakeoff,
                landing = atc.ClearedLanding
            },
            departureRunway = atc.DepartureRunway,
            arrivalRunway = atc.ArrivalRunway,
            arrivalGate = atc.ArrivalGate,
            assignedAltitudeFt = atc.AssignedAltitudeFt,
            requestStatus = atc.RequestStatus.ToString(),
            activeRequest = atc.ActiveRequest,
            lastRequestUtc = atc.LastRequestUtc,
            lastFailure = atc.LastFailure,
            lastInstruction = atc.LastInstruction,
            autoRequest = atc.AutoRequest,
            autoTune = atc.AutoTune,
            instructionsFromSim = atc.InstructionsFromSim
        };
    }

    /// <summary>
    /// The pushback block: what the tug is doing, and what the pilot has done about it.
    /// The panel needs the whole picture in one place because a taxi-out that is standing
    /// still on a stand is doing exactly what it should, and the alternative reading - an
    /// aircraft that will not move - is wrong.
    /// </summary>
    private static object BuildPushbackJson(AIPilot pilot)
    {
        var s = pilot.State;
        return new
        {
            summary = pilot.PushbackText,
            attached = pilot.PushbackAttached,
            available = pilot.PushbackAvailable,
            requested = pilot.PushbackRequested,
            waiting = pilot.HoldingForPushback,
            releasing = pilot.ReleasingPushback,
            pushedMeters = pilot.PushbackPushedMeters,
            direction = s.Pushback.Direction.ToString(),
            tugHeadingDeg = s.Pushback.AngleDegrees
        };
    }

    private static object BuildChecklistJson(AIPilot pilot)
    {
        var cs = pilot.ChecklistStatus();
        return new
        {
            cs.Source,
            cs.Format,
            cs.Enabled,
            cs.Loaded,
            cs.Step,
            cs.Index,
            cs.Count,
            cs.Item,
            cs.Expectation,
            cs.ItemState,
            cs.Completed,
            cs.Lists
        };
    }

    /// <summary>
    /// The traffic block: how many aircraft are in range and whether the runway is free.
    /// The panel shows a warning when runway entry is unsafe, which is the one piece of
    /// traffic information that changes what the pilot is doing.
    /// </summary>
    private static object BuildTrafficJson(AIPilot pilot)
    {
        var traffic = pilot.Traffic;
        return new
        {
            inRange = traffic.Contacts.Count,
            summary = traffic.Describe(),
            runwayEntrySafe = pilot.TrafficAllowsRunwayEntry,
            runwayOccupied = traffic.DepartureRunwayOccupant != null,
            aircraftOnFinal = traffic.ArrivalOnDepartureRunway != null,
            airborneConflicts = traffic.AirborneConflicts.Count
        };
    }

    /// <summary>
    /// The weather block: wind, derived turbulence, icing and the surface state, so the panel
    /// can show why the aircraft is flying the speeds and bank limits it is (a gusty or
    /// crosswind approach, a contaminated or icy runway).
    /// </summary>
    private static object BuildWeatherJson(AIPilot pilot)
    {
        var s = pilot.State;
        return new
        {
            windDirection = pilot.WindDirectionDeg,
            windSpeed = pilot.WindSpeedKt,
            crosswind = pilot.CrosswindKtOn(s.Heading),
            turbulence = pilot.Turbulence.ToString(),
            turbulenceSeverity = pilot.TurbulenceSeverity,
            icingConditions = pilot.IcingConditionsPresent,
            airframeIcePercent = pilot.StructuralIcePercent,
            inCloud = pilot.InCloud,
            surface = pilot.SurfaceText,
            surfaceContaminated = pilot.SurfaceContaminated
        };
    }

    /// <summary>
    /// The limits block: the aircraft's operating envelope and whether it is being flown
    /// inside it. The panel shows a warning when a limit is exceeded, which is the one piece
    /// of envelope information that means the pilot is not being flown the way a person
    /// would fly it - an overspeed, an over-g, a rate beyond the airframe.
    /// </summary>
    private static object BuildLimitsJson(AIPilot pilot)
    {
        var limits = pilot.Limits;
        return new
        {
            summary = pilot.LimitsText,
            exceeded = pilot.LimitExceeded,
            breach = pilot.ActiveLimitBreach,
            loadFactor = pilot.MeasuredLoadFactor,
            maxLoadFactorG = limits.MaxLoadFactorG,
            maxAirspeedKt = limits.MaxAirspeedKt,
            maxFlapSpeedKt = limits.MaxFlapSpeedKt,
            maxGearSpeedKt = limits.MaxGearSpeedKt,
            stallFloorKt = limits.MinAirspeedKt,
            maxClimbRateFpm = limits.MaxClimbRateFpm,
            maxDescentRateFpm = limits.MaxDescentRateFpm,
            egtRedlineC = limits.MaxEgtC,
            engineLimitFraction = pilot.EngineLimitFraction
        };
    }

    private void Log(string msg)
    {
        OnLog?.Invoke(msg);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
