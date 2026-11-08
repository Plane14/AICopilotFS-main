using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Pilot;
using Xunit;

namespace MSFS2024AIPilot.Tests;

public class TaxiSpeedControllerTests
{
    private static AircraftControlProfile Profile() => AircraftControlProfile.ForKind(AircraftKind.Airplane, new AircraftPerformance());

    [Fact]
    public void Straight_taxi_uses_max_straight()
    {
        var p = Profile();
        var speed = TaxiSpeedController.TargetSpeedKt(p, 100, 0, false, false, false, false);
        Assert.Equal(p.MaxStraightTaxiKt, speed);
    }

    [Fact]
    public void Turn_ahead_slows()
    {
        var p = Profile();
        var speed = TaxiSpeedController.TargetSpeedKt(p, 20, 40, false, false, false, false);
        Assert.True(speed <= p.MaxTurnTaxiKt);
    }

    [Fact]
    public void Apron_slower()
    {
        var p = Profile();
        var speed = TaxiSpeedController.TargetSpeedKt(p, 100, 0, true, false, false, false);
        Assert.Equal(p.ApronTaxiKt, speed);
    }

    [Fact]
    public void Hold_short_decelerates()
    {
        var p = Profile();
        var speed = TaxiSpeedController.TargetSpeedKt(p, 5, 0, false, false, true, false);
        Assert.True(speed < p.MaxStraightTaxiKt);
    }

    [Fact]
    public void Adverse_weather_reduces()
    {
        var p = Profile();
        var dry = TaxiSpeedController.TargetSpeedKt(p, 100, 0, false, false, false, false);
        var wet = TaxiSpeedController.TargetSpeedKt(p, 100, 0, false, false, false, true);
        Assert.True(wet < dry);
    }
}
