using System;
using System.IO;
using MSFS2024AIPilot.Core;
using MSFS2024AIPilot.Core.Aircraft;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Covers the MSFS 2024 aircraft.cfg / flight_model.cfg / flight_performance.cfg
/// parsing and the SimBase.Document checklist XML parser against realistic
/// package layouts written to a temp directory.
/// </summary>
public class ConfigAndChecklistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aicfg" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Aircraft_config_parser_reads_identity_weights_and_vspeeds()
    {
        Write("aircraft.cfg", """
            [GENERAL]
            atc_type = "Cessna"
            atc_model = "172 Skyhawk"
            icao_type_designator = "C172"
            icao_engine_type = "PISTON"
            icao_engine_count = 1
            Category = "Airplane"
            object_class = "Airplane"

            [WEIGHT_AND_BALANCE]
            max_gross_weight = 2550
            empty_weight = 1500

            [FUEL]
            fuel_capacity = 56
            """);
        Write("flight_model.cfg", """
            [WEIGHT_AND_BALANCE]
            max_takeoff_weight = 2550
            max_landing_weight = 2400
            max_zero_fuel_weight = 2300

            [FLIGHT_TUNING]
            flaps_up_stall_speed = 48
            full_flaps_stall_speed = 40
            max_speed_full_flaps = 85
            max_speed_cruise = 129
            max_speed = 163
            min_control_speed = 40
            best_angle_climb_speed = 63
            best_rate_of_climb_speed = 74
            max_gear_extended = 85
            """);

        var perf = AircraftConfigParser.ParseAircraftConfig(_dir);

        Assert.Equal("Cessna 172 Skyhawk", perf.Title);
        Assert.Equal("C172", perf.IcaoType);
        Assert.Equal(1, perf.EngineCount);
        Assert.Contains("PISTON", perf.EngineType);
        Assert.Equal(2550, perf.MaxGrossWeight);
        Assert.Equal(2550, perf.MaxTakeoffWeight);
        Assert.Equal(2400, perf.MaxLandingWeight);
        Assert.Equal(2300, perf.MaxZeroFuelWeight);
        Assert.Equal(48, perf.Vs1);
        Assert.Equal(40, perf.Vso);
        Assert.Equal(85, perf.Vfe);
        Assert.Equal(129, perf.Vno);
        Assert.Equal(163, perf.Vne);
        Assert.Equal(40, perf.Vmc);
        Assert.Equal(63, perf.Vx);
        Assert.Equal(74, perf.Vy);
        Assert.Equal(85, perf.Vle);
        Assert.True(perf.MaxFuelWeight > 0);
    }

    [Fact]
    public void Flight_performance_parser_decodes_nd_distance_and_vspeed_tables()
    {
        Write("flight_performance.cfg", """
            [TAKEOFF_PERFORMANCE]
            V1_table_by_flaps_and_weight = 0,1 : 2000,2500 :: 50,55 : 48,52
            Vr_table_by_flaps_and_weight = 0,1 : 2000,2500 :: 55,60 : 53,57
            takeoff_ground_roll_distance_table_by_weight_and_OAT_and_altitude = 2000,2500 : -10,30 : 0 :: 1000,1200,900,1100

            [CLIMB_PERFORMANCE.0]
            profile_name = "Climb"
            IAS_below_transition = 90
            """);

        var perf = AircraftConfigParser.ParseAircraftConfig(_dir);

        Assert.True(perf.HasFlightPerformanceConfig);
        Assert.True(perf.HasAdvancedPerformance);
        // 2 weights x 2 OATs x 1 altitude = 4 points, all positive.
        Assert.Equal(4, perf.TakeoffPerf.GroundRollTable.Count);
        Assert.True(perf.TakeoffVSpeeds.ContainsKey(0));
        Assert.True(perf.TakeoffVSpeeds.ContainsKey(1));
        Assert.Single(perf.ClimbPerf.Profiles);
        Assert.True(perf.RequiredTakeoffRunwayFt(2200, 0, 15) > 0);
    }

    [Fact]
    public void Zero_values_in_one_cfg_do_not_erase_valid_speeds()
    {
        Write("flight_model.cfg", """
            [FLIGHT_TUNING]
            flaps_up_stall_speed = 50
            max_speed = 160
            """);
        // A second file parsed later that reports zeros must not wipe them.
        Write("aircraft.cfg", """
            [FLIGHT_TUNING]
            flaps_up_stall_speed = 0
            max_speed = 0
            """);

        var perf = AircraftConfigParser.ParseAircraftConfig(_dir);
        Assert.Equal(50, perf.Vs1);
        Assert.Equal(160, perf.Vne);
    }

    [Fact]
    public void Checklist_parser_reads_msfs2024_simbase_xml()
    {
        var cfg = Write("aircraft.cfg", """
            [GENERAL]
            title = "Test Aircraft"
            atc_type = "TA"
            atc_model = "Test"
            """);
        Directory.CreateDirectory(Path.Combine(_dir, "Checklist"));
        Write("Checklist/preflight.xml", """
            <SimBase.Document Type="Checklist" version="1,0">
              <Checklist.Checklist>
                <Step ChecklistStepId="PREFLIGHT_GATE">
                  <Page SubjectTT="Preflight Gate">
                    <Checkpoint Id="brake">
                      <CheckpointDesc SubjectTT="Parking brake" ExpectationTT="SET" />
                    </Checkpoint>
                    <Checkpoint Id="fuel">
                      <CheckpointDesc SubjectTT="Fuel selector" ExpectationTT="BOTH" />
                      <Action EventID="FUEL_SELECTOR_BOTH" Copilot="TRUE" Once="TRUE" />
                    </Checkpoint>
                  </Page>
                </Step>
              </Checklist.Checklist>
            </SimBase.Document>
            """);

        var set = ChecklistParser.ParseFromAircraft("Test Aircraft", rootOverride: cfg);

        Assert.True(set.IsLoaded);
        Assert.Equal(ChecklistSetFormat.Msfs2024Xml, set.Format);
        var list = set.Find(ChecklistStep.PREFLIGHT_GATE);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal("Parking brake", list.Items[0].Subject);
        Assert.Equal("SET", list.Items[0].Expectation);
        Assert.Equal(ChecklistItemKind.Manual, list.Items[0].Kind);
        Assert.Equal(ChecklistItemKind.Set, list.Items[1].Kind);
        Assert.Single(list.Items[1].Actions);
        Assert.Equal("FUEL_SELECTOR_BOTH", list.Items[1].Actions[0].EventId);
    }

    [Fact]
    public void Checklist_parser_resolves_checkpoint_library_references()
    {
        var cfg = Write("aircraft.cfg", """
            [GENERAL]
            title = "Lib Aircraft"
            """);
        Directory.CreateDirectory(Path.Combine(_dir, "Checklist"));
        Write("Checklist/library.xml", """
            <SimBase.Document Type="CheckpointLibrary" version="1,0">
              <Checklist.CheckpointLibrary>
                <Checkpoint Id="lib_brake">
                  <CheckpointDesc SubjectTT="Parking brake" ExpectationTT="SET" />
                </Checkpoint>
              </Checklist.CheckpointLibrary>
            </SimBase.Document>
            """);
        Write("Checklist/main.xml", """
            <SimBase.Document Type="Checklist" version="1,0">
              <Checklist.Checklist>
                <IncludeCheckpointLibrary FileName="library.xml" />
                <Step ChecklistStepId="PREFLIGHT_GATE">
                  <Page>
                    <Checkpoint ReferenceId="lib_brake" />
                  </Page>
                </Step>
              </Checklist.Checklist>
            </SimBase.Document>
            """);

        var set = ChecklistParser.ParseFromAircraft("Lib Aircraft", rootOverride: cfg);

        Assert.True(set.IsLoaded);
        var list = set.Find(ChecklistStep.PREFLIGHT_GATE);
        Assert.NotNull(list);
        var item = Assert.Single(list!.Items);
        Assert.Equal("Parking brake", item.Subject);
        Assert.Equal("SET", item.Expectation);
        Assert.Equal("ref:lib_brake", item.SourceList);
    }

    [Fact]
    public void Checklist_parser_falls_back_to_generic_when_package_has_no_checklist()
    {
        var cfg = Write("aircraft.cfg", """
            [GENERAL]
            title = "NoList Aircraft"
            """);
        var set = ChecklistParser.ParseFromAircraft("NoList Aircraft", rootOverride: cfg);
        Assert.False(set.IsLoaded);
        Assert.False(set.IsAircraftSpecific);
    }
}
