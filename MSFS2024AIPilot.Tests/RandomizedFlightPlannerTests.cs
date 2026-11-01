using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class RandomizedFlightPlannerTests
{
    [Fact]
    public void FuelPlanUsesTripAlternateContingencyAndFinalReserve()
    {
        var perf = new AircraftPerformance
        {
            EngineType = "JET",
            CruisePerf = new CruisePerformance
            {
                Profiles = new List<CruiseProfile> { new() { TAS = 300, FuelFlowPPH = 1200 } }
            }
        };
        var state = new AircraftState();

        var fuel = RandomizedFlightPlanner.PlanFuel(perf, state, tripNm: 300, alternateNm: 90);

        Assert.True(fuel.TripGallons > 0);
        Assert.True(fuel.AlternateGallons > 0);
        Assert.Equal(fuel.TripGallons * 0.05, fuel.ContingencyGallons, 2);
        // Jet final reserve: 45 minutes.
        Assert.Equal((45.0 / 60.0) * (1200.0 / 6.7), fuel.FinalReserveGallons, 2);
        Assert.True(fuel.TotalGallons > fuel.TripGallons + fuel.AlternateGallons);
    }

    [Theory]
    [InlineData(90, 11500)]
    [InlineData(270, 10500)]
    public void CruiseAltitudeFollowsSemiCircularRule(double track, double expectedAtLeast)
    {
        var perf = new AircraftPerformance
        {
            CruisePerf = new CruisePerformance { MaxCruiseAltitude = 41000 }
        };

        var altitude = RandomizedFlightPlanner.SelectCruiseAltitudeFt(perf, distanceNm: 150, trueTrackDeg: track, moraFt: 4000);

        Assert.True(altitude >= expectedAtLeast);
        Assert.Equal(500, altitude % 1000);
    }

    [Fact]
    public void NextLegSelectsDestinationAndAlternateDifferentFromDeparture()
    {
        var airports = new List<AirportData>
        {
            Airport("KAAA", 47.45, -122.3),
            Airport("KBBB", 45.58, -122.6),
            Airport("KCCC", 37.62, -122.4),
            Airport("KDDD", 34.05, -118.2)
        };
        var perf = new AircraftPerformance
        {
            WingSpan = 118,
            Category = "M",
            EngineType = "JET",
            CruisePerf = new CruisePerformance { MaxCruiseAltitude = 39000 }
        };
        var state = new AircraftState { GrossWeight = 150000, PressureAltitude = 1000, OAT = 20 };
        var random = new Random(7);

        var ok = RandomizedFlightPlanner.TryPlanNextLeg(
            "KAAA",
            airports,
            perf,
            state,
            random,
            previousDestinationIcao: "KBBB",
            out var leg,
            out var failure);

        Assert.True(ok, failure);
        Assert.NotEqual("KAAA", leg.DestinationIcao);
        Assert.NotEqual(leg.DestinationIcao, leg.AlternateIcao);
        Assert.True(leg.Fuel.TotalGallons > 0);
    }

    private static AirportData Airport(string icao, double lat, double lon)
    {
        var airport = new AirportData
        {
            Icao = icao,
            Name = icao,
            IsLoaded = true,
            ReferenceLatitude = lat,
            ReferenceLongitude = lon,
            ReferenceAltitudeMeters = 50
        };
        airport.Runways.Add(new RunwayInfo
        {
            PrimaryNumber = 10,
            SecondaryNumber = 28,
            LengthMeters = 3500,
            WidthMeters = 45
        });
        return airport;
    }
}
