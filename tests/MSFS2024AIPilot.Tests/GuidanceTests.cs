using MSFS2024AIPilot.Core;
using MSFS2024AIPilot.Core.Environment;
using MSFS2024AIPilot.Core.Guidance;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Tests;

public class GuidanceTests
{
    [Fact]
    public void Wind_components_head_on_course()
    {
        // Wind from the north at 20 kt, flying south: full headwind.
        var (head, cross) = WeatherModel.WindComponents(0, 20, 180);
        Assert.Equal(20, head, 2);
        Assert.Equal(0, cross, 2);
    }

    [Fact]
    public void Wind_correction_sign()
    {
        var wca = WeatherModel.WindCorrectionAngleDeg(10, 120);
        Assert.True(wca > 0 && wca < 15);
    }

    [Fact]
    public void Envelope_clamps_airspeed()
    {
        var s = new AircraftState { IndicatedAirspeed = 200, BarberPoleKt = 160, OnGround = false };
        var limits = new OperatingLimits { MaxAirspeedKt = 160 };
        var clamped = FlightEnvelope.ClampAirspeed(200, limits, s);
        Assert.True(clamped <= 165);
    }

    [Fact]
    public void Checklist_bindings_recognize_common_items()
    {
        var env = new ChecklistEnvironment();
        var gear = ChecklistTextResolver.Resolve("LANDING GEAR", "DOWN", env);
        Assert.Contains(gear.Events, e => e.Name == "GEAR_DOWN");
        Assert.Equal("GEAR HANDLE POSITION", gear.VerifySimVar);

        var brake = ChecklistTextResolver.Resolve("PARKING BRAKE", "SET", env);
        Assert.Contains(brake.Events, e => e.Name == "PARKING_BRAKE_SET");
        Assert.Equal("BRAKE PARKING POSITION", brake.VerifySimVar);
    }
}
