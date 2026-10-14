using System.Globalization;
using System.IO;
using System.Threading;
using MSFS2024AIPilot.Core;
using Xunit;
namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Regression tests for locale-dependent numeric parsing. The default culture of the
/// machine that built and runs this suite treats "." as a group separator, so
/// framework-default parsing silently turns "22.44" into 2244. Every value this
/// product reads comes from a simulator file or a SimConnect message and must parse
/// identically regardless of the machine's locale.
/// </summary>
public class LocaleIndependenceTests : IDisposable
{
    private readonly CultureInfo _original;

    public LocaleIndependenceTests()
    {
        _original = Thread.CurrentThread.CurrentCulture;
        // A culture where "." is a group separator and "," is the decimal separator.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("es-ES");
    }

    public void Dispose()
    {
        Thread.CurrentThread.CurrentCulture = _original;
    }

    /// <summary>
    /// The envelope's reported strings are built under the caller's culture, and this test
    /// runs under es-ES, where "2.90" formats as "2,90". They are written to the log and to
    /// the panel, and one of them is what a user reads to find out why the pilot did what it
    /// did, so the separator may not follow the machine.
    /// </summary>
    [Fact]
    public void EnvelopeStringsKeepTheDotSeparator()
    {
        var perf = new AircraftPerformance
        {
            Vne = 340, Vfe = 200, Vle = 250, Vso = 140, Vref = 150,
            EngineType = "JET", EngineCount = 2
        };

        var state = new AircraftState
        {
            // At Vfe but not over it, so the flap limit is not the breach reported: the
            // load factor of 2.9 g past the 2.0 g configured limit is the one that is.
            IndicatedAirspeed = 200, VerticalSpeed = 0, GLoad = 2.9,
            FlapsHandleIndex = 3, GearDown = false, BarberPoleKt = 0,
            EngineN1 = [40], EngineN2 = [70], EngineThrottle = [1.0],
            EngineEgtC = [FlightEnvelope.JetEgtRedlineC + 20], EngineMaxRpmPct = [90]
        };

        var guard = new EnvelopeGuard();
        guard.Update(state, perf, 3, false);

        var description = guard.Describe();
        // Flaps 3 selected, so the tighter configured limit is the one reported.
        Assert.Contains("2.0g", description);
        Assert.Contains("Vfe 200", description);
        Assert.Contains("EGT redline 950 C", description);
        // The only commas in the summary are the list separators; no number carries a
        // comma as its decimal separator.
        Assert.DoesNotContain("2,0g", description);
        Assert.DoesNotContain("950,", description);

        var breach = guard.BreachDescription();
        Assert.NotNull(breach);
        // The load factor reads 2.90 with a dot, not 2,90 with a comma.
        Assert.Contains("2.90", breach!);
        Assert.DoesNotContain(",", breach);
    }

    [Fact]
    public void ParsingDoubleIsInvariant()
    {
        Assert.Equal(22.44, Parsing.Double("22.44"));
        Assert.Equal(34000.0, Parsing.Double("34000.000"));
        Assert.Equal(0.5, Parsing.Double("0.5"));
        Assert.Equal(-73.7781, Parsing.Double("-73.7781"));
    }

    [Fact]
    public void ParsingIntIsInvariant()
    {
        Assert.Equal(31, Parsing.Int32("31"));
        Assert.Equal(0, Parsing.Int32("not a number"));
    }

    [Fact]
    public void ParsingUIntIsInvariant()
    {
        Assert.Equal(120000u, Parsing.UInt32("120000"));
    }

    [Fact]
    public void DmsDegreesParseUnderGroupSeparatorCulture()
    {
        // "N40° 38' 22.44\"" must become 40.6396, not 41.2566.
        var nav = ParseOneWaypoint();
        var wp = Assert.Single(nav.Waypoints);

        Assert.True(wp.HasPosition);
        Assert.Equal(40.6396, wp.Latitude, precision: 4);
        Assert.Equal(-73.7781, wp.Longitude, precision: 4);
    }

    [Fact]
    public void CruiseAltitudeParsesUnderGroupSeparatorCulture()
    {
        Assert.Equal(34000, ParseOneWaypoint().CruiseAltitudeFt);
    }

    [Fact]
    public void AtcFlightLevelParsesUnderGroupSeparatorCulture()
    {
        var instruction = BeyondAtcBridge.ParseAtcText("Climb and maintain FL350", "DEP");
        Assert.Equal(35000, instruction.AltitudeFt);
    }

    [Fact]
    public void AtcMachParsesUnderGroupSeparatorCulture()
    {
        var instruction = BeyondAtcBridge.ParseAtcText("Reduce Mach to 0.78", "CTR");
        Assert.Equal(0.78, instruction.Mach!.Value, precision: 2);
    }

    [Fact]
    public void ConfigValuesParseUnderGroupSeparatorCulture()
    {
        // Use the real config filenames the parser looks for.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"locale-cfg-{Guid.NewGuid():N}")).FullName;
        File.WriteAllText(Path.Combine(dir, "flight_model.cfg"), """
            [FLIGHT_TUNING]
            flaps_up_stall_speed = 110.5
            max_speed_cruise = 250.25
            """);
        File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), """
            [GENERAL]
            title = "Test Aircraft"
            icao_type_designator = "TST"
            number_of_engines = 2
            """);

        try
        {
            var parsed = AircraftConfigParser.ParseAircraftConfig(dir);

        // 110.5 must not become 110, and 250.25 must not become 25000.
        // flaps_up_stall_speed is the CLEAN stall speed (Vs1); full_flaps_stall_speed is
        // the LANDING stall speed (Vs0). They were assigned the other way round, so this
        // asserts the corrected mapping: the clean figure lands in Vs1.
        Assert.Equal(110.5, parsed.Vs1);
        Assert.Equal(250.25, parsed.Vno);
        Assert.Equal("TST", parsed.ICAOType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static NavigationState ParseOneWaypoint()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <SimBase.Document Type="AceXML" version="1,0">
              <FlightPlan.FlightPlan>
                <CruisingAlt>34000.000</CruisingAlt>
                <DepartureID>KJFK</DepartureID>
                <ATCWaypoint id="KJFK">
                  <ATCWaypointType>Airport</ATCWaypointType>
                  <WorldPosition>N40° 38' 22.44",W73° 46' 41.31",+000012.80</WorldPosition>
                  <ICAO><ICAOIdent>KJFK</ICAOIdent></ICAO>
                </ATCWaypoint>
              </FlightPlan.FlightPlan>
            </SimBase.Document>
            """;

        var path = Path.Combine(Path.GetTempPath(), $"locale-plan-{Guid.NewGuid():N}.pln");
        File.WriteAllText(path, xml);
        try
        {
            return FlightPlanParser.ParsePLN(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
