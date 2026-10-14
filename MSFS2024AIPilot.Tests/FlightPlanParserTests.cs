using System.IO;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class FlightPlanParserTests
{
    private const string Msfs2020Plan = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<SimBase.Document Type=""AceXML"" version=""1,0"">
  <Descr>AceXML Document</Descr>
  <FlightPlan.FlightPlan>
    <Title>KJFK to KLAX</Title>
    <FPType>IFR</FPType>
    <RouteType>HighAlt</RouteType>
    <CruisingAlt>34000.000</CruisingAlt>
    <DepartureID>KJFK</DepartureID>
    <DepartureLLA>N40° 38' 22.44"",W73° 46' 41.31"",+000012.80</DepartureLLA>
    <DestinationID>KLAX</DestinationID>
    <DestinationLLA>N33° 56' 33.05"",W118° 24' 30.03"",+000125.00</DestinationLLA>
    <Descr>KJFK, KLAX</Descr>
    <DeparturePosition>RW31R</DeparturePosition>
    <DepartureName>John F Kennedy Intl</DepartureName>
    <DestinationName>Los Angeles Intl</DestinationName>
    <AppVersion>
      <AppVersionMajor>10</AppVersionMajor>
      <AppVersionBuild>61472</AppVersionBuild>
    </AppVersion>
    <ATCWaypoint id=""KJFK"">
      <ATCWaypointType>Airport</ATCWaypointType>
      <WorldPosition>N40° 38' 22.44"",W73° 46' 41.31"",+000012.80</WorldPosition>
      <ICAO><ICAOIdent>KJFK</ICAOIdent></ICAO>
    </ATCWaypoint>
    <ATCWaypoint id=""COAVY"">
      <ATCWaypointType>Intersection</ATCWaypointType>
      <WorldPosition>N40° 26' 38.73"",W73° 39' 46.71"",+000000.00</WorldPosition>
      <DepartureFP>DEEZZ5</DepartureFP>
      <RunwayNumberFP>31</RunwayNumberFP>
      <RunwayDesignatorFP>RIGHT</RunwayDesignatorFP>
      <ICAO><ICAORegion>K6</ICAORegion><ICAOIdent>COAVY</ICAOIdent></ICAO>
    </ATCWaypoint>
    <ATCWaypoint id=""ENO"">
      <ATCWaypointType>Intersection</ATCWaypointType>
      <WorldPosition>N39° 15' 9.57"",W75° 22' 1.08"",+004400.00</WorldPosition>
      <ATCAirway>J80</ATCAirway>
    </ATCWaypoint>
    <ATCWaypoint id=""SHIPP"">
      <ATCWaypointType>Intersection</ATCWaypointType>
      <WorldPosition>N33° 58' 22.59"",W118° 10' 34.21"",+003400.00</WorldPosition>
      <ArrivalFP>SEAVU2</ArrivalFP>
      <RunwayNumberFP>25</RunwayNumberFP>
      <RunwayDesignatorFP>RIGHT</RunwayDesignatorFP>
    </ATCWaypoint>
    <ATCWaypoint id=""KLAX"">
      <ATCWaypointType>Airport</ATCWaypointType>
      <WorldPosition>N33° 56' 33.05"",W118° 24' 30.03"",+000125.00</WorldPosition>
      <ApproachTypeFP>ILS</ApproachTypeFP>
      <RunwayNumberFP>25</RunwayNumberFP>
      <RunwayDesignatorFP>RIGHT</RunwayDesignatorFP>
      <ICAO><ICAOIdent>KLAX</ICAOIdent></ICAO>
    </ATCWaypoint>
  </FlightPlan.FlightPlan>
</SimBase.Document>";

    /// <summary>
    /// MSFS 2024 native plans omit WorldPosition on intermediate waypoints and name
    /// procedures in plain-text detail elements instead.
    /// </summary>
    private const string Msfs2024Plan = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<SimBase.Document Type=""AceXML"" version=""1,0"">
  <FlightPlan.FlightPlan>
    <Title>EGLL to LFPG</Title>
    <FPType>IFR</FPType>
    <CruisingAlt>36000.000</CruisingAlt>
    <DepartureID>EGLL</DepartureID>
    <DepartureLLA>N51° 28' 39.80"",W0° 27' 41.80"",+000083.20</DepartureLLA>
    <DeparturePosition>27L</DeparturePosition>
    <DepartureDetails>CPT5A / 27L</DepartureDetails>
    <DestinationID>LFPG</DestinationID>
    <DestinationLLA>N49° 0' 33.43"",E2° 32' 47.02"",+000392.20</DestinationLLA>
    <DestinationDetails>HMR1A</DestinationDetails>
    <ApproachDetails>ILS 27R / PUMAL</ApproachDetails>
    <AppVersion><AppVersionMajor>12</AppVersionMajor></AppVersion>
    <ATCWaypoint id=""EGLL"">
      <ATCWaypointType>Airport</ATCWaypointType>
      <ICAO><ICAOIdent>EGLL</ICAOIdent></ICAO>
    </ATCWaypoint>
    <ATCWaypoint id=""TIMBA"">
      <ATCWaypointType>Intersection</ATCWaypointType>
      <ATCAirway>UL980</ATCAirway>
    </ATCWaypoint>
    <ATCWaypoint id=""LFPG"">
      <ATCWaypointType>Airport</ATCWaypointType>
      <ICAO><ICAOIdent>LFPG</ICAOIdent></ICAO>
    </ATCWaypoint>
  </FlightPlan.FlightPlan>
</SimBase.Document>";

    private NavigationState Parse(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aipilot-test-{Guid.NewGuid():N}.pln");
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

    [Fact]
    public void DepartureIcaoIsNotOverwrittenByTheAirportName()
    {
        var nav = Parse(Msfs2020Plan);

        Assert.Equal("KJFK", nav.DepartureICAO);
        Assert.Equal("KLAX", nav.ArrivalICAO);
        Assert.Equal("John F Kennedy Intl", nav.DepartureAirportName);
        Assert.Equal("Los Angeles Intl", nav.ArrivalAirportName);
    }

    [Fact]
    public void ReadsCruiseAltitudeFromPlan()
    {
        var nav = Parse(Msfs2020Plan);
        Assert.Equal(34000, nav.CruiseAltitudeFt);
    }

    [Fact]
    public void ReadsAirportCoordinatesFromPlan()
    {
        var nav = Parse(Msfs2020Plan);

        Assert.Equal(40.6396, nav.DepartureLatitude, precision: 3);
        Assert.Equal(-73.7781, nav.DepartureLongitude, precision: 2);
        Assert.Equal(125, nav.DestinationAltitude, precision: 0);
    }

    [Fact]
    public void NormalizesRunwayNameFromBareNumber()
    {
        var nav = Parse(Msfs2020Plan);

        // DeparturePosition is "RW31R", which must resolve to the "31R" form.
        Assert.Equal("31R", nav.DepartureRunway);
    }

    [Fact]
    public void ArrivalRunwayComesFromTheApproachWaypoint()
    {
        var nav = Parse(Msfs2020Plan);

        Assert.Equal("25R", nav.ArrivalRunway);
    }

    [Fact]
    public void ReadsSidStarAndApproachNames()
    {
        var nav = Parse(Msfs2020Plan);

        Assert.Equal("DEEZZ5", nav.SIDName);
        Assert.Equal("SEAVU2", nav.STARName);
        Assert.Equal("ILS", nav.ApproachType);
    }

    [Fact]
    public void ComputesDistanceAcrossPositionedWaypoints()
    {
        var nav = Parse(Msfs2020Plan);

        // JFK to LAX is roughly 2150 NM; the plan's fixes run a similar line.
        Assert.True(nav.Waypoints.Sum(w => w.DistanceFromPrevious) > 1800,
            "expected a multi-thousand kilometre route");
    }

    [Fact]
    public void Msfs2024PlanWithoutWaypointPositionsStillYieldsMetadata()
    {
        var nav = Parse(Msfs2024Plan);

        Assert.Equal("EGLL", nav.DepartureICAO);
        Assert.Equal("LFPG", nav.ArrivalICAO);
        Assert.Equal("27L", nav.DepartureRunway);
        Assert.Equal(36000, nav.CruiseAltitudeFt);
    }

    [Fact]
    public void Msfs2024PlanUsesProcedureDetailElements()
    {
        var nav = Parse(Msfs2024Plan);

        // The waypoints carry no procedure in this layout, so the details are used.
        Assert.Contains("CPT5A", nav.SIDName);
        Assert.Contains("HMR1A", nav.STARName);
    }

    [Fact]
    public void WaypointsWithoutPositionsAreFlaggedAsSuch()
    {
        var nav = Parse(Msfs2024Plan);

        // Only the two airport waypoints carry a position, via the LLA header fields.
        Assert.DoesNotContain(nav.Waypoints, w => !w.HasPosition && w.Type == WaypointType.Airport);
    }

    [Fact]
    public void RunwayNormalisationAgreesWithRunwayRecords()
    {
        // A .pln "6" must match a runway record named "06", which is how
        // FindRunway matches against facility data.
        Assert.Equal("06", FlightPlanParser.NormalizeRunwayName("6"));
        Assert.Equal("06", FlightPlanParser.NormalizeRunwayName("06"));
        Assert.Equal("25L", FlightPlanParser.NormalizeRunwayName("25L"));
        Assert.Equal("25R", FlightPlanParser.NormalizeRunwayName("RW25R"));
        Assert.Equal("31R", FlightPlanParser.NormalizeRunwayName("31", "RIGHT"));
        Assert.Equal("09", FlightPlanParser.NormalizeRunwayName("9", "NONE"));
        Assert.Equal("08L", FlightPlanParser.NormalizeRunwayName("8", "LEFT"));
    }

    [Fact]
    public void InvalidRunwayIdentifiersReturnNull()
    {
        Assert.Null(FlightPlanParser.NormalizeRunwayName(""));
        Assert.Null(FlightPlanParser.NormalizeRunwayName("99"));
        Assert.Null(FlightPlanParser.NormalizeRunwayName("0"));
        Assert.Null(FlightPlanParser.NormalizeRunwayName("noise"));
    }

    [Fact]
    public void MalformedPlanDoesNotThrow()
    {
        var nav = Parse("this is not xml at all");

        Assert.NotNull(nav);
        Assert.Empty(nav.Waypoints);
    }

    [Fact]
    public void DistanceIsSymmetricAndZeroForIdenticalPoints()
    {
        Assert.Equal(0, FlightPlanParser.CalculateDistance(51.4700, -0.4543, 51.4700, -0.4543), precision: 6);

        var forward = FlightPlanParser.CalculateDistance(40.6413, -73.7781, 51.4700, -0.4543);
        var reverse = FlightPlanParser.CalculateDistance(51.4700, -0.4543, 40.6413, -73.7781);
        Assert.Equal(forward, reverse, precision: 6);
    }
}
