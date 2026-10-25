using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Locks the datum layout of every requested block to the decoder in
/// <see cref="AircraftController"/>. The decoder indexes the response by datum
/// position, so a block that is not registered as one definition - or whose order
/// changes - silently reads the wrong variable (or a zero) for every field after
/// the first.
/// </summary>
public class AircraftDataLayoutTests
{
    private static (string Block, string Name, string Units)[] Block(string block) =>
        AircraftController.DataLayout.Where(d => d.Block == block).ToArray();

    [Fact]
    public void EveryBlockTheDecoderHandlesIsDefined()
    {
        string[] blocks =
        {
            "POSITION", "ATTITUDE", "SPEEDS", "ENGINES", "FUEL", "AUTOPILOT",
            "ENVIRONMENT", "NAVIGATION", "NAVRADIO", "SYSTEMS", "APMODES", "COM", "ATC", "WEATHER",
            "ENGINE_LIMITS", "PUSHBACK"
        };

        foreach (var block in blocks)
        {
            Assert.NotEmpty(Block(block));
        }
    }

    [Fact]
    public void EngineBlockCarriesSixDatumsPerEngine()
    {
        var datums = Block("ENGINES");

        Assert.Equal("NUMBER OF ENGINES", datums[0].Name);
        Assert.Equal(1 + AircraftController.MaxEngines * 6, datums.Length);

        // The decoder maps engine e (zero based) to datum 1 + e*6 and reads
        // N1/N2/fuel flow/throttle at +2/+3/+4/+5.
        for (var e = 0; e < AircraftController.MaxEngines; e++)
        {
            var engine = e + 1;
            var baseIndex = 1 + e * 6;
            Assert.Equal($"GENERAL ENG COMBUSTION:{engine}", datums[baseIndex].Name);
            Assert.Equal($"GENERAL ENG RPM:{engine}", datums[baseIndex + 1].Name);
            Assert.Equal($"TURB ENG N1:{engine}", datums[baseIndex + 2].Name);
            Assert.Equal($"TURB ENG N2:{engine}", datums[baseIndex + 3].Name);
            Assert.Equal($"TURB ENG FUEL FLOW PPH:{engine}", datums[baseIndex + 4].Name);
            Assert.Equal($"GENERAL ENG THROTTLE LEVER POSITION:{engine}", datums[baseIndex + 5].Name);
        }
    }

    [Fact]
    public void BlockOrderMatchesTheDecoderIndices()
    {
        Assert.Equal(new[] { "PLANE LATITUDE", "PLANE LONGITUDE", "PLANE ALTITUDE", "PLANE ALT ABOVE GROUND" },
            Block("POSITION").Select(d => d.Name));

        Assert.Equal(new[] { "PLANE HEADING DEGREES MAGNETIC", "PLANE HEADING DEGREES TRUE", "PLANE PITCH DEGREES", "PLANE BANK DEGREES" },
            Block("ATTITUDE").Select(d => d.Name));

        Assert.Equal(new[] { "AIRSPEED INDICATED", "AIRSPEED TRUE", "AIRSPEED MACH", "GROUND VELOCITY", "VERTICAL SPEED" },
            Block("SPEEDS").Select(d => d.Name));

        // The decoder reads gross weight from datum 3 and CG percent from datum 5.
        Assert.Equal("TOTAL WEIGHT", Block("FUEL")[3].Name);
        Assert.Equal("CG PERCENT", Block("FUEL")[5].Name);

        Assert.Equal("AUTOPILOT MASTER", Block("AUTOPILOT")[0].Name);
        Assert.Equal("SIM ON GROUND", Block("SYSTEMS")[0].Name);
        Assert.Equal("GEAR HANDLE POSITION", Block("SYSTEMS")[1].Name);
        Assert.Equal("BRAKE PARKING POSITION", Block("SYSTEMS")[3].Name);
        Assert.Equal("FLAPS HANDLE INDEX", Block("SYSTEMS")[4].Name);
        Assert.Equal("COM ACTIVE FREQUENCY:1", Block("COM")[0].Name);
        Assert.Equal("COM ACTIVE FREQUENCY:2", Block("COM")[2].Name);
        Assert.Equal("TRANSPONDER CODE:1", Block("COM")[4].Name);

        // The envelope guard reads these four by index. The barber pole is the aircraft's own
        // never-exceed needle and the two warnings are what the aircraft itself says about
        // being outside its envelope, so a shift in this block would make the guard read a
        // gear position as a speed limit.
        Assert.Equal("AIRSPEED MACH", Block("SPEEDS")[2].Name);
        Assert.Equal("STALL WARNING", Block("SYSTEMS")[7].Name);
        Assert.Equal("OVERSPEED WARNING", Block("SYSTEMS")[8].Name);
        Assert.Equal("AIRSPEED BARBER POLE", Block("SYSTEMS")[9].Name);
    }

    [Fact]
    public void NavRadioUsesTheDocumentedSimVarNames()
    {
        var names = Block("NAVRADIO").Select(d => d.Name).ToArray();

        Assert.Contains("NAV TOFROM", names);
        Assert.DoesNotContain("NAV TO FROM:1", names);
        Assert.DoesNotContain("NAV TOFROM:1", names);
    }

    [Fact]
    public void WeatherBlockOrderMatchesTheDecoderIndices()
    {
        // The decoder reads the normal load factor (converted to g) from index 0, the
        // icing/moisture signals at 1-5 and the surface at 6-7. A block whose order changes
        // silently reads one datum as another, so the layout is pinned here.
        Assert.Equal(new[]
        {
            "ACCELERATION BODY Y",
            "AMBIENT IN CLOUD",
            "AMBIENT PRECIP STATE",
            "STRUCTURAL ICE PCT",
            "PITOT ICE PCT",
            "STRUCTURAL DEICE SWITCH",
            "SURFACE CONDITION",
            "SURFACE INFO VALID",
        }, Block("WEATHER").Select(d => d.Name));
    }

    /// <summary>
    /// The pushback tug is read by position like every other block, and it is the only
    /// source for "the tug is on the aircraft": a datum order that drifted would read
    /// availability as attachment and never dismiss a tug that was never there.
    /// </summary>
    [Fact]
    public void PushbackBlockOrderMatchesTheDecoderIndices()
    {
        Assert.Equal(new[]
        {
            "PUSHBACK ATTACHED",
            "PUSHBACK AVAILABLE",
            "PUSHBACK WAIT",
            "PUSHBACK STATE:1",
            "PUSHBACK ANGLE",
        }, Block("PUSHBACK").Select(d => d.Name));

        // The state datum is the documented enum, so it is requested with no unit; the
        // MSFS 2024 docs list none for it, and a unit such as "enum" is rejected as an
        // unrecognised name. The angle is radians, which is what the tug's heading is
        // published in.
        Assert.Equal("", Block("PUSHBACK")[3].Units);
        Assert.Equal("radians", Block("PUSHBACK")[4].Units);
    }

    /// <summary>
    /// The powerplant limits are their own block rather than extra datums on ENGINES, so a
    /// name the simulator rejects cannot take the engine state down with it: SimConnect
    /// answers an unrecognised datum name for the whole definition, and an empty ENGINES
    /// block would then report engine count zero on a running aircraft.
    /// </summary>
    [Fact]
    public void EngineLimitsBlockIsSeparateFromTheEngineStateBlock()
    {
        var limits = Block("ENGINE_LIMITS");

        Assert.Equal("NUMBER OF ENGINES", limits[0].Name);
        Assert.Equal(1 + AircraftController.MaxEngines * 2, limits.Length);

        for (var e = 0; e < AircraftController.MaxEngines; e++)
        {
            var engine = e + 1;
            var baseIndex = 1 + e * 2;
            Assert.Equal($"GENERAL ENG EXHAUST GAS TEMPERATURE:{engine}", limits[baseIndex].Name);
            Assert.Equal($"GENERAL ENG PCT MAX RPM:{engine}", limits[baseIndex + 1].Name);
            // MSFS 2024 reports EGT in Rankine; converted to Celsius at the decode boundary.
            Assert.Equal("rankine", limits[baseIndex].Units);
            Assert.Equal("percent", limits[baseIndex + 1].Units);
        }

        // The engine-state block still carries exactly the six datums its decoder expects.
        Assert.Equal(1 + AircraftController.MaxEngines * 6, Block("ENGINES").Length);
    }

    [Fact]
    public void EveryDatumHasAUnit()
    {
        // Every datum carries a unit except PUSHBACK STATE, which the MSFS 2024 docs
        // list with none; a unit such as "enum" is rejected as an unrecognised name.
        foreach (var d in AircraftController.DataLayout)
        {
            if (d.Name == "PUSHBACK STATE:1") continue;
            Assert.False(string.IsNullOrWhiteSpace(d.Units),
                $"datum {d.Name} in block {d.Block} has no unit");
        }
    }
}
