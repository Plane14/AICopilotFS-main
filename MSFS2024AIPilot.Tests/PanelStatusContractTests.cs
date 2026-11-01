using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The in-sim panel is plain HTML/JS that reads /status from the host over HTTP. These
/// tests pin the field names and types the panel depends on, because a rename on the
/// C# side silently renders blank telemetry rather than failing.
/// </summary>
public class PanelStatusContractTests
{
    private static string StatusJson()
    {
        var pilot = new AIPilot();
        var body = PanelHttpBridge.BuildStatus(pilot);
        pilot.Dispose();
        return body;
    }

    [Fact]
    public void StatusExposesRootFieldsThePanelReads()
    {
        var json = StatusJson();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Rendered directly by the panel: state, phase, connected.
        Assert.True(root.TryGetProperty("state", out _), "panel reads state");
        Assert.True(root.TryGetProperty("phase", out _), "panel reads phase");
        Assert.True(root.TryGetProperty("connected", out var connected), "panel reads connected");
        Assert.True(connected.ValueKind == System.Text.Json.JsonValueKind.True ||
                    connected.ValueKind == System.Text.Json.JsonValueKind.False,
            "connected must be a boolean, the panel compares it with === true");
    }

    [Fact]
    public void ConnectedReflectsTheSimConnectLinkNotTheControlMode()
    {
        // This used to be ControlState != Idle, so a host whose simulator had dropped
        // still told the panel it was connected, while the named-pipe path tested the
        // link itself and disagreed. A fresh host is not connected to any simulator.
        var pilot = new AIPilot();
        var json = PanelHttpBridge.BuildStatus(pilot);
        pilot.Dispose();

        var connected = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("connected");

        Assert.False(connected.GetBoolean());
    }

    [Fact]
    public void StatusCarriesTheTrafficBlock()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());

        Assert.True(doc.RootElement.TryGetProperty("traffic", out var traffic), "panel reads traffic");

        foreach (var name in new[] { "inRange", "runwayEntrySafe", "runwayOccupied", "aircraftOnFinal" })
        {
            Assert.True(traffic.TryGetProperty(name, out _), $"panel reads traffic.{name}");
        }

        // The traffic block also asserts the runway-entry verdict separately, so the panel
        // can warn without parsing the summary string.
        Assert.True(doc.RootElement.TryGetProperty("trafficRunwayEntrySafe", out _));
    }

    [Fact]
    public void AircraftBlockHasTheTelemetryFields()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var aircraft = doc.RootElement.GetProperty("aircraft");

        foreach (var name in new[] { "altitudeAGL", "altitude", "indicatedAirspeed", "groundSpeed",
                                     "heading", "verticalSpeed", "grossWeight", "onGround" })
        {
            Assert.True(aircraft.TryGetProperty(name, out _), $"panel reads aircraft.{name}");
        }
    }

    [Fact]
    public void NavigationBlockHasTheRouteFields()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var nav = doc.RootElement.GetProperty("navigation");

        foreach (var name in new[] { "departureICAO", "arrivalICAO", "departureRunway" })
        {
            Assert.True(nav.TryGetProperty(name, out _), $"panel reads navigation.{name}");
        }
    }

    [Fact]
    public void StatusCarriesTheNavdataRouteBlock()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());

        // The navigation block is the plan as parsed; the navdata block is the route built
        // out of it and the navigation database, which is what the pilot actually follows.
        Assert.True(doc.RootElement.TryGetProperty("nav", out var nav), "panel reads nav");

        foreach (var name in new[] { "legs", "geometryLegs", "unresolvedFixes", "activeLeg",
                                     "activeLegDistanceMeters", "crossTrackMeters", "directToFix",
                                     "publishedApproach", "approachType", "approachRunway",
                                     "hasPublishedApproach", "missedApproach", "missedApproachLegs",
                                     "hasPublishedMissedApproach" })
        {
            Assert.True(nav.TryGetProperty(name, out _), $"panel reads nav.{name}");
        }

        // A host with no simulator has no route, and the panel renders zeros rather than
        // failing on a missing one.
        var legs = nav.GetProperty("legs");
        Assert.True(legs.ValueKind == System.Text.Json.JsonValueKind.Number);
        Assert.Equal(0, legs.GetInt32());
    }

    [Fact]
    public void NumericTelemetryIsEncodedAsNumbersNotStrings()
    {
        // The panel does Number(value) + Math.round(), which works for a real number
        // and silently yields NaN for a quoted one, so these must not be strings.
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var aircraft = doc.RootElement.GetProperty("aircraft");

        foreach (var name in new[] { "altitudeAGL", "indicatedAirspeed", "heading",
                                     "verticalSpeed", "grossWeight" })
        {
            var value = aircraft.GetProperty(name);
            Assert.True(value.ValueKind == System.Text.Json.JsonValueKind.Number,
                $"aircraft.{name} should be a JSON number");
        }
    }

    [Fact]
    public void PhaseNamesUseThePanelVocabulary()
    {
        // The panel's JS table maps these exact phase names; an unknown name would
        // render the raw enum string instead.
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var phase = doc.RootElement.GetProperty("phase").GetString();

        var panelNames = new[]
        {
            "Unknown", "Preflight", "Startup", "TaxiOut", "Takeoff", "InitialClimb",
            "Climb", "Cruise", "Descent", "Approach", "FinalApproach", "Landing",
            "Rollout", "TaxiIn", "Shutdown", "Holding", "Divert", "Emergency", "GoAround"
        };

        Assert.Contains(phase, panelNames);
    }

    [Fact]
    public void ControlStateUsesAiActiveAndManual()
    {
        // The panel decides "AI FLYING" from state === "AiActive", so those two
        // spellings are part of the contract.
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var state = doc.RootElement.GetProperty("state").GetString();

        Assert.Contains(state, new[] { "Idle", "Manual", "AiActive", "Disengaging", "Error" });
    }

    [Fact]
    public void AirportsBlockReportsLoadedGroundData()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("airports", out var airports));
        Assert.True(airports.ValueKind == System.Text.Json.JsonValueKind.Array);
    }

    [Fact]
    public void AtcBlockReportsWhichControllerIsInCharge()
    {
        // The panel's ATC readout needs to know which back-end is active and what the
        // controller has cleared, without parsing raw text.
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        var atc = doc.RootElement.GetProperty("atc");

        Assert.True(atc.TryGetProperty("source", out var source));
        Assert.Contains(source.GetString(), new[] { "None", "Native", "BeyondAtc" });

        Assert.True(atc.TryGetProperty("beyondAtcRunning", out var running));
        Assert.True(running.ValueKind == System.Text.Json.JsonValueKind.True ||
                    running.ValueKind == System.Text.Json.JsonValueKind.False);

        foreach (var name in new[] { "stationFrequency", "departureRunway", "arrivalRunway",
                                     "requestStatus", "lastInstruction" })
        {
            Assert.True(atc.TryGetProperty(name, out _), $"panel reads atc.{name}");
        }

        var cleared = atc.GetProperty("cleared");
        foreach (var name in new[] { "ifr", "taxi", "takeoff", "landing" })
        {
            var value = cleared.GetProperty(name);
            Assert.True(value.ValueKind == System.Text.Json.JsonValueKind.True ||
                        value.ValueKind == System.Text.Json.JsonValueKind.False,
                $"atc.cleared.{name} must be a boolean");
        }
    }

    [Fact]
    public void StatusCarriesTheWeatherBlock()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());

        Assert.True(doc.RootElement.TryGetProperty("weather", out var weather), "panel reads weather");

        foreach (var name in new[] { "windSpeed", "crosswind", "turbulence", "icingConditions",
                                     "airframeIcePercent", "inCloud", "surfaceContaminated" })
        {
            Assert.True(weather.TryGetProperty(name, out _), $"panel reads weather.{name}");
        }

        // Turbulence is rendered from its enum name; the booleans and numbers must keep
        // their JSON types so the panel's === comparisons and arithmetic work.
        Assert.True(weather.GetProperty("icingConditions").ValueKind ==
                    System.Text.Json.JsonValueKind.True ||
                    weather.GetProperty("icingConditions").ValueKind ==
                    System.Text.Json.JsonValueKind.False);
        Assert.True(weather.GetProperty("crosswind").ValueKind == System.Text.Json.JsonValueKind.Number);
        Assert.True(weather.GetProperty("airframeIcePercent").ValueKind == System.Text.Json.JsonValueKind.Number);
    }

    [Fact]
    public void StatusCarriesTheLimitsBlock()
    {
        // The panel's envelope readout decides "ENVELOPE OK" from exceeded === false, so the
        // boolean must stay a boolean and the breach must be a string or null.
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());

        Assert.True(doc.RootElement.TryGetProperty("limits", out var limits), "panel reads limits");

        Assert.True(limits.TryGetProperty("exceeded", out var exceeded));
        Assert.True(exceeded.ValueKind == System.Text.Json.JsonValueKind.True ||
                    exceeded.ValueKind == System.Text.Json.JsonValueKind.False,
            "limits.exceeded must be a boolean, the panel compares it with === true");

        Assert.True(limits.TryGetProperty("breach", out var breach));
        Assert.True(breach.ValueKind == System.Text.Json.JsonValueKind.String ||
                    breach.ValueKind == System.Text.Json.JsonValueKind.Null,
            "limits.breach is a description or null when inside the envelope");

        foreach (var name in new[] { "maxAirspeedKt", "maxLoadFactorG", "loadFactor",
                                     "maxFlapSpeedKt", "maxGearSpeedKt", "stallFloorKt",
                                     "maxClimbRateFpm", "maxDescentRateFpm",
                                     "egtRedlineC", "engineLimitFraction", "summary" })
        {
            Assert.True(limits.TryGetProperty(name, out _), $"panel reads limits.{name}");
        }

        // A fresh host has no simulator, so no aircraft and no envelope: the speed limit is
        // zero and the panel must not render a Vmo it cannot know.
        Assert.Equal(0, limits.GetProperty("maxAirspeedKt").GetDouble());
    }

    [Fact]
    public void StatusCarriesTheAutomationBlock()
    {
        var doc = System.Text.Json.JsonDocument.Parse(StatusJson());
        Assert.True(doc.RootElement.TryGetProperty("automation", out var automation), "panel reads automation");

        foreach (var name in new[] { "enabled", "stopAfterThisFlight", "status", "lastPlanSummary", "legsCompleted" })
        {
            Assert.True(automation.TryGetProperty(name, out _), $"panel reads automation.{name}");
        }
    }

    [Fact]
    public void ControlStateTransitionUpdatesState()
    {
        var pilot = new AIPilot();
        try
        {
            pilot.SetControlState(PilotControlState.AiActive);
            var json = PanelHttpBridge.BuildStatus(pilot);
            Assert.Contains("AiActive", json);

            pilot.SetControlState(PilotControlState.Manual);
            json = PanelHttpBridge.BuildStatus(pilot);
            Assert.Contains("Manual", json);
        }
        finally
        {
            pilot.Dispose();
        }
    }
}
