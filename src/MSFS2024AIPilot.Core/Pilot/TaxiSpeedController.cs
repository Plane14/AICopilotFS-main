using MSFS2024AIPilot.Core.Aircraft;
using MSFS2024AIPilot.Core.Airports;

namespace MSFS2024AIPilot.Core.Pilot;

/// <summary>Realistic taxi speed targets (knots ground speed) for a taxi leg.</summary>
public static class TaxiSpeedController
{
    public static double TargetSpeedKt(
        AircraftControlProfile profile,
        double distanceToNodeM,
        double turnAngleDeg,
        bool apron,
        bool crossesRunway,
        bool toHoldShort,
        bool adverseWeather)
    {
        double speed;
        if (toHoldShort)
        {
            if (distanceToNodeM < 5) return 0;
            speed = profile.MaxStraightTaxiKt *
                    Math.Clamp(distanceToNodeM / Math.Max(1, profile.HoldShortDecelDistanceM), 0, 1);
        }
        else if (crossesRunway) speed = profile.RunwayCrossingTaxiKt;
        else if (apron) speed = profile.ApronTaxiKt;
        else speed = profile.MaxStraightTaxiKt;

        // Slow for an upcoming turn while still on the preceding straight.
        if (turnAngleDeg >= profile.TurnDetectionAngleDeg && distanceToNodeM < 30)
            speed = Math.Min(speed, profile.MaxTurnTaxiKt);

        // A turn leg itself is always slow.
        if (turnAngleDeg >= profile.TurnDetectionAngleDeg)
            speed = Math.Min(speed, profile.MaxTurnTaxiKt);

        if (adverseWeather) speed *= profile.TaxiSpeedWeatherFactor;

        return Math.Max(0, speed);
    }
}
