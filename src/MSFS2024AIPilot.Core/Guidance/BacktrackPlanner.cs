using System;
using MSFS2024AIPilot.Core.Airports;
using MSFS2024AIPilot.Core.Geo;

namespace MSFS2024AIPilot.Core.Guidance;

/// <summary>
/// Runway back-taxi decisions: a runway is usable from where the taxiway joins it,
/// and the length ahead of that joining point is what the takeoff roll has to fit in.
/// </summary>
public static class BacktrackPlanner
{
    /// <summary>Multiplier on the required roll before a back-taxi is demanded.</summary>
    public const double RunwayMargin = 1.15;
    /// <summary>Back-taxis shorter than this are not worth the turn, meters.</summary>
    public const double WorthwhileMeters = 150;
    /// <summary>Turn point standoff short of the threshold, meters.</summary>
    public const double TurnStandoffMeters = 40;

    /// <summary>
    /// True when the aircraft must back-taxi: the runway ahead of the joining point
    /// is shorter than the roll needs.
    /// </summary>
    public static bool RequiresBacktrack(
        AirportData airport, GroundNode joinNode, RunwayInfo runway, string runwayName,
        double requiredRunMeters, double? alongRunwayOverride = null)
    {
        var threshold = runway.ThresholdFor(runwayName);
        if (threshold is null) return false;

        double along;
        if (alongRunwayOverride is { } ao)
        {
            along = ao;
        }
        else
        {
            var (a, _, _) = GeoMath.ProjectOntoSegment(
                joinNode.Latitude, joinNode.Longitude,
                runway.PrimaryEnd.lat, runway.PrimaryEnd.lon,
                runway.SecondaryEnd.lat, runway.SecondaryEnd.lon);
            along = a;
        }

        var ahead = RunwayAheadOf(runway, along);
        return ahead < requiredRunMeters * RunwayMargin && ahead > 0;
    }

    /// <summary>
    /// How much runway lies ahead of a point <paramref name="alongRunway"/> meters
    /// from the runway centre towards the takeoff direction. The runway centre is
    /// at LengthMeters/2 from either end.
    /// </summary>
    public static double RunwayAheadOf(RunwayInfo runway, double alongRunway)
    {
        // alongRunway is distance from primary end along the runway axis.
        var fromPrimary = alongRunway;
        // Takeoff direction is from the threshold towards the far end.
        return runway.LengthMeters - fromPrimary;
    }

    /// <summary>
    /// The point to turn at for the back-taxi: short of the threshold by the
    /// standoff, so the turn happens on the strip rather than off the end.
    /// </summary>
    public static (double lat, double lon)? TurnPointFor(RunwayInfo runway, string runwayName)
    {
        var threshold = runway.ThresholdFor(runwayName);
        if (threshold is null) return null;
        // Threshold is at the near end; the turn point is just past it onto the strip.
        // Runway heading points down the strip from the threshold.
        return GeoMath.Destination(threshold.Value.lat, threshold.Value.lon, runway.HeadingDegTrue, TurnStandoffMeters);
    }
}
