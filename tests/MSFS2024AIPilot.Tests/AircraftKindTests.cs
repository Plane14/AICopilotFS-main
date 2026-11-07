using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Planning;
using MSFS2024AIPilot.Core.SimConnect;

namespace MSFS2024AIPilot.Tests;

public class AircraftKindTests
{
    private static AircraftPerformance Perf(string engineType = "", string icao = "", string title = "") => new()
    {
        EngineType = engineType,
        IcaoType = icao,
        Title = title,
    };

    [Fact]
    public void Rotorcraft_detected_from_engine_type()
    {
        var kind = AircraftKindDetector.Detect(null, "Airbus H125", Perf(engineType: "ROTOR"));
        Assert.Equal(AircraftKind.Rotorcraft, kind);
    }

    [Fact]
    public void Rotorcraft_detected_from_title()
    {
        var kind = AircraftKindDetector.Detect(null, "Robinson R44", Perf());
        Assert.Equal(AircraftKind.Rotorcraft, kind);
    }

    [Fact]
    public void Floatplane_detected_from_title()
    {
        var kind = AircraftKindDetector.Detect(null, "DHC-2 Beaver on Floats", Perf());
        Assert.Equal(AircraftKind.Seaplane, kind);
    }

    [Fact]
    public void Amphibian_detected_from_title()
    {
        var kind = AircraftKindDetector.Detect(null, "Cessna 208 Caravan Amphibian", Perf());
        Assert.Equal(AircraftKind.Amphibian, kind);
    }

    [Fact]
    public void Landplane_stays_airplane()
    {
        var kind = AircraftKindDetector.Detect(null, "Cessna 172 Skyhawk", Perf(engineType: "PISTON"));
        Assert.Equal(AircraftKind.Airplane, kind);
    }

    [Fact]
    public void Gear_floats_upgrades_airplane_to_amphibian()
    {
        Assert.Equal(AircraftKind.Amphibian, AircraftKindDetector.ObserveGearFloats(AircraftKind.Airplane));
        Assert.Equal(AircraftKind.Seaplane, AircraftKindDetector.ObserveGearFloats(AircraftKind.Seaplane));
        Assert.Equal(AircraftKind.Rotorcraft, AircraftKindDetector.ObserveGearFloats(AircraftKind.Rotorcraft));
    }

    [Fact]
    public void Water_runway_detected_by_surface_and_designator()
    {
        var rw = new RunwayInfo { Surface = 2 };
        Assert.True(rw.IsWater);
        rw = new RunwayInfo { Surface = 0, PrimaryDesignator = 4 };
        Assert.True(rw.IsWater);
        rw = new RunwayInfo { Surface = 0, PrimaryDesignator = 0, SecondaryDesignator = 0 };
        Assert.False(rw.IsWater);
    }

    [Fact]
    public void Airport_reports_water_runways()
    {
        var ap = new AirportData();
        ap.Runways.Add(new RunwayInfo { Surface = 0, LengthMeters = 2000 });
        Assert.False(ap.HasWaterRunway);
        ap.Runways.Add(new RunwayInfo { Surface = 29, LengthMeters = 900 }); // lake
        Assert.True(ap.HasWaterRunway);
        Assert.Single(ap.WaterRunways);
    }

    [Fact]
    public void Planner_suitability_per_kind()
    {
        var landOnly = new AirportData { IsClosed = false };
        landOnly.Runways.Add(new RunwayInfo { Surface = 0, LengthMeters = 2000 });
        var water = new AirportData { IsClosed = false };
        water.Runways.Add(new RunwayInfo { Surface = 2, LengthMeters = 400 }); // too short for wheels
        var heliport = new AirportData { IsClosed = false }; // no runways at all

        var planner = new ContinuousFlightPlanner(
            new AirportFacilityLoader(new SimConnectClient()))
        {
            RequiredRunwayMeters = 800,
        };

        Assert.True(planner.IsSuitable(landOnly, AircraftKind.Airplane));
        Assert.False(planner.IsSuitable(water, AircraftKind.Airplane)); // water too short
        Assert.True(planner.IsSuitable(water, AircraftKind.Seaplane));
        Assert.False(planner.IsSuitable(landOnly, AircraftKind.Seaplane));
        Assert.True(planner.IsSuitable(landOnly, AircraftKind.Amphibian));
        Assert.True(planner.IsSuitable(heliport, AircraftKind.Rotorcraft));
        Assert.False(planner.IsSuitable(heliport, AircraftKind.Airplane));
    }

    [Fact]
    public void PickRunway_water_only_filters_land_runways()
    {
        var ap = new AirportData();
        ap.Runways.Add(new RunwayInfo
        {
            Surface = 0, LengthMeters = 3000, HeadingDegTrue = 90,
            PrimaryNumber = 9, PrimaryDesignator = 0, SecondaryNumber = 27, SecondaryDesignator = 0,
        });
        ap.Runways.Add(new RunwayInfo
        {
            Surface = 29, LengthMeters = 1200, HeadingDegTrue = 90,
            PrimaryNumber = 9, PrimaryDesignator = 4, SecondaryNumber = 27, SecondaryDesignator = 4,
        });

        var land = ContinuousFlightPlanner.PickRunway(ap, 0, 800);
        Assert.Equal(3000, land!.Value.runway.LengthMeters);

        var water = ContinuousFlightPlanner.PickRunway(ap, 0, 800, waterOnly: true);
        Assert.Equal(29, water!.Value.runway.Surface);

        var prefer = ContinuousFlightPlanner.PickRunway(ap, 0, 800, preferWater: true);
        // 3000 m land runway still beats the 1200 m water lane on length.
        Assert.Equal(3000, prefer!.Value.runway.LengthMeters);
    }
}
