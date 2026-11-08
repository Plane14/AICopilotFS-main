using MSFS2024AIPilot.Core.Checklists;
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
        Assert.Equal("GEAR_DOWN", ChecklistBindings.EventFor("Landing gear down"));
        Assert.Equal("GEAR_UP", ChecklistBindings.EventFor("Gear up"));
        Assert.Equal("BRAKE PARKING POSITION", ChecklistBindings.SimVarFor("Parking brake set"));
    }

    [Fact]
    public void Precip_state_mask_ignores_none()
    {
        // AMBIENT PRECIP STATE mask: 2 = None, 4 = Rain, 8 = Snow.
        // None must NOT count as precipitation for icing.
        Assert.False(WeatherModel.IcingConditions(-5, false, 2, 0));   // None, cold, no ice
        Assert.True(WeatherModel.IcingConditions(-5, false, 4, 0));    // Rain, cold
        Assert.True(WeatherModel.IcingConditions(-5, false, 8, 0));    // Snow, cold
        Assert.True(WeatherModel.IcingConditions(-5, false, 12, 0));   // Rain + Snow
        Assert.False(WeatherModel.IcingConditions(15, false, 4, 0));   // Rain but too warm
        Assert.False(WeatherModel.IcingConditions(-5, false, 0, 0));   // No precip, not in cloud
        Assert.True(WeatherModel.IcingConditions(-5, true, 0, 0));     // In cloud, cold
    }

    [Fact]
    public void Aircraft_state_mach_limit_uses_mach_max_operate()
    {
        var s = new AircraftState
        {
            BarberPoleKt = 0,        // no barber pole, MMO is the limit
            MachMaxOperate = 0.78,   // ~516 kt at sea level
            BarberPoleMach = 0,
        };
        // MaxAirspeedLimitKt should be MMO in kt (~516)
        var limit = s.MaxAirspeedLimitKt;
        Assert.InRange(limit, 500, 530);

        // Mach limit via OperatingLimits.ConfigurationSpeedLimitKt
        var limits = new OperatingLimits
        {
            MaxAirspeedKt = 600,
            MaxMach = 0.78,
        };
        var clamped = FlightEnvelope.ClampAirspeed(400, limits, s);
        Assert.True(clamped <= 530); // clamped by MMO ~516 kt
    }

    [Fact]
    public void Aircraft_state_barber_pole_mach_limit()
    {
        var s = new AircraftState
        {
            BarberPoleKt = 0,
            MachMaxOperate = 0,
            BarberPoleMach = 0.82,
        };
        Assert.InRange(s.MaxMachLimit, 0.81, 0.83);
    }
}
