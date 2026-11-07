using System.IO;
using MSFS2024AIPilot.Core;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The fixture is a real RSC payload chunk captured from
/// https://flightchecklist.app/msfs/cessna-172 — it contains the embedded
/// phases/steps tree the provider must extract without an API key.
/// </summary>
public class FlightChecklistProviderTests
{
    private static string FixtureHtml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "flightchecklist_chunk.txt");
        return "<html><body><script>" + File.ReadAllText(path) + "</script></body></html>";
    }

    [Fact]
    public void ParsePage_extracts_phases_and_steps()
    {
        var set = FlightChecklistProvider.ParsePage(FixtureHtml(), "cessna-172");

        Assert.NotNull(set);
        Assert.True(set!.IsLoaded);
        Assert.Equal(ChecklistSetFormat.WebJson, set.Format);

        var items = set.AllItems.ToList();
        Assert.True(items.Count > 100, $"expected the full ~178-step checklist, got {items.Count}");

        // Spot-check known C172 items survive the legacy binding pipeline.
        Assert.Contains(items, i => i.Subject.Contains("Weather Briefing"));
        Assert.Contains(items, i => i.Subject.Contains("Parking Brake", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParsePage_maps_phase_names_to_checklist_steps()
    {
        var set = FlightChecklistProvider.ParsePage(FixtureHtml(), "cessna-172")!;

        // "Before Takeoff" must land on the runway step, not the parking stand.
        var runway = set.Find(ChecklistStep.FLIGHT_RUNWAY);
        Assert.NotNull(runway);
        Assert.Contains(runway!.Items, i => i.SourceList != null);

        // Shutdown/ securing items belong to the gate step.
        var gate = set.Find(ChecklistStep.LANDING_GATE);
        Assert.NotNull(gate);
    }

    [Fact]
    public void ParsePage_returns_null_for_page_without_phases()
    {
        Assert.Null(FlightChecklistProvider.ParsePage("<html><body>no data</body></html>", "x"));
    }

    [Fact]
    public void BuildPerformance_parses_real_c172_spec_strings()
    {
        // Exact strings captured from https://flightchecklist.app/msfs/cessna-172
        var perf = FlightChecklistProvider.BuildPerformance(
            engines: "1x Lycoming IO-360-L2A (180 hp)",
            maxSpeed: "233 km/h (126 kts)",
            range: "1,289 km (696 nmi)",
            ceiling: "13,500 ft",
            weight: "1,157 kg (MTOW)",
            type: "Single Engine Piston");

        Assert.NotNull(perf);
        Assert.Equal(1, perf!.EngineCount);
        Assert.Equal("PISTON", perf.EngineType);
        Assert.Equal(126, perf.Vno, 1);
        Assert.Equal(696, perf.MaxRangeNm, 1);
        Assert.Equal(13500, perf.CruisePerf.MaxCruiseAltitude, 1);
        Assert.Equal(1157 * 2.20462, perf.MaxGrossWeight, 1);
        Assert.Equal(1157 * 2.20462, perf.MaxTakeoffWeight, 1);
        Assert.True(perf.MaxThrustPerEngine > 100 && perf.MaxThrustPerEngine < 300);
    }

    [Fact]
    public void BuildPerformance_handles_jet_specs_and_missing_fields()
    {
        var perf = FlightChecklistProvider.BuildPerformance(
            engines: "2x CFM56-7B (27,000 lbf)",
            maxSpeed: "939 km/h (507 kts)",
            range: "5,435 km (2,935 nmi)",
            ceiling: "41,000 ft",
            weight: "79,016 kg (MTOW)",
            type: "Twin Engine Jet");

        Assert.NotNull(perf);
        Assert.Equal(2, perf!.EngineCount);
        Assert.Equal("JET", perf.EngineType);
        Assert.Equal(507, perf.Vno, 1);
        Assert.Equal(2935, perf.MaxRangeNm, 1);
        Assert.Equal(27000, perf.MaxThrustPerEngine, 1);
        Assert.Equal(79016 * 2.20462, perf.MaxGrossWeight, 1);

        // Missing optional fields stay at defaults — the caller keeps cfg values.
        var sparse = FlightChecklistProvider.BuildPerformance(null, null, null, null, null, "Helicopter");
        Assert.NotNull(sparse);
        Assert.Equal("ROTOR", sparse!.EngineType);
        Assert.Equal(0, sparse.MaxRangeNm);
        Assert.Null(FlightChecklistProvider.BuildPerformance(null, null, null, null, null, null));
    }
}
