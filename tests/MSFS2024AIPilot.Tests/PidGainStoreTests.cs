using System;
using System.IO;
using MSFS2024AIPilot.Core.Aircraft;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class PidGainStoreTests
{
    [Fact]
    public void Resolve_returns_override_when_present()
    {
        var store = new PidGainStore();
        var profile = AircraftControlProfile.ForKind(AircraftKind.Rotorcraft, new AircraftPerformance());
        store.Seed("Robinson R44", profile);
        var resolved = store.Resolve("Robinson R44", AircraftKind.Airplane, new AircraftPerformance());
        Assert.Equal(AircraftKind.Rotorcraft, resolved.Kind);
        Assert.Equal(15, resolved.MaxBankDeg);
    }

    [Fact]
    public void Resolve_returns_kind_default_when_absent()
    {
        var store = new PidGainStore();
        var resolved = store.Resolve("Unknown", AircraftKind.Rotorcraft, new AircraftPerformance());
        Assert.Equal(AircraftKind.Rotorcraft, resolved.Kind);
    }

    [Fact]
    public void Round_trip_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new PidGainStore();
            var profile = AircraftControlProfile.ForKind(AircraftKind.Airplane, new AircraftPerformance { EngineType = "JET", MaxGrossWeight = 20000 });
            store.Seed("Boeing 737", profile);
            store.Save(path);

            var loaded = PidGainStore.Load(path);
            Assert.True(loaded.Overrides.ContainsKey("Boeing 737"));
            Assert.Equal(AircraftKind.Airplane, loaded.Overrides["Boeing 737"].Kind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Heavy_aircraft_gets_lower_gains()
    {
        var light = AircraftControlProfile.ForKind(AircraftKind.Airplane, new AircraftPerformance());
        var heavy = AircraftControlProfile.ForKind(AircraftKind.Airplane, new AircraftPerformance { EngineType = "JET", MaxGrossWeight = 20000 });
        Assert.True(heavy.Pitch.Kp < light.Pitch.Kp);
        Assert.True(heavy.MaxBankDeg <= light.MaxBankDeg);
    }
}
