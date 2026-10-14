using System;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Decides when an aircraft has to back-taxi a runway, and works out where it turns round.
/// <para>
/// A runway is usable only from where the taxiway joins it. The length ahead of that point
/// is what the takeoff roll has to fit in, so a strip joined halfway along does not give an
/// aircraft its published run however long the runway is. A controller says so explicitly -
/// "enter runway 08 and backtrack, line up and wait" - and the same thing is derivable from
/// the geometry, which is what this does because neither the simulator's own ATC nor
/// BeyondATC publishes a back-taxi flag at all.
/// </para>
/// <para>
/// The whole decision is here rather than inside the pilot so it can be tested without a
/// running simulator.
/// </para>
/// </summary>
public static class BacktrackPlanner
{
    /// <summary>
    /// Whether the runway has to be back-taxed before the aircraft can take off.
    /// <paramref name="takeoffRunMeters"/> is the runway the roll needs, in metres.
    /// </summary>
    public static bool RequiresBacktrack(
        RunwayInfo runway,
        string runwayName,
        double entryLat,
        double entryLon,
        double takeoffRunMeters)
    {
        var threshold = runway.ThresholdFor(runwayName);
        if (threshold is null) return false;

        var rollEnd = runway.PrimaryEnd.Equals(threshold.Value) ? runway.SecondaryEnd : runway.PrimaryEnd;
        var (along, _) = Geo.ProjectOntoSegment(
            entryLat, entryLon, threshold.Value.lat, threshold.Value.lon, rollEnd.lat, rollEnd.lon);

        return RunwayAheadOf(runway, along) < takeoffRunMeters;
    }

    /// <summary>
    /// Length of runway left ahead of a joining point, in metres, measured from the
    /// threshold the roll starts at. An entry short of the threshold has the whole strip
    /// ahead of it, and one past the end has none.
    /// </summary>
    public static double RunwayAheadOf(RunwayInfo runway, double alongRunway)
    {
        var along = Math.Max(0, alongRunway);
        return Math.Max(0, runway.LengthMeters - along);
    }

    /// <summary>
    /// Where the aircraft turns round at the end of a back-taxi: <paramref name="standoffMeters"/>
    /// short of the threshold the roll starts from, so the turn happens on the strip rather
    /// than off the end of it.
    /// </summary>
    public static (double lat, double lon)? TurnPointFor(
        RunwayInfo runway, string runwayName, double standoffMeters)
    {
        var threshold = runway.ThresholdFor(runwayName);
        if (threshold is null) return null;

        var rollEnd = runway.PrimaryEnd.Equals(threshold.Value) ? runway.SecondaryEnd : runway.PrimaryEnd;
        var axisBearing = Geo.BearingDegrees(
            threshold.Value.lat, threshold.Value.lon, rollEnd.lat, rollEnd.lon);

        return Geo.OffsetByMeters(
            threshold.Value.lat, threshold.Value.lon,
            -Math.Sin(Geo.ToRadians(axisBearing)) * standoffMeters,
            -Math.Cos(Geo.ToRadians(axisBearing)) * standoffMeters);
    }
}
