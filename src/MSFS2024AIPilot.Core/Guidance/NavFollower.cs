using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Guidance;

/// <summary>
/// Follows the active flight plan's lateral path: sequences waypoints and produces
/// the heading the autopilot should fly, with cross-track correction and wind
/// correction so the ground track (not just the heading) follows the course.
/// </summary>
public sealed class NavPathFollower
{
    /// <summary>Cross-track error beyond which a direct intercept is commanded, nm.</summary>
    public double MaxCrossTrackNm { get; set; } = 2.0;
    /// <summary>Waypoint capture radius, nm.</summary>
    public double CaptureRadiusNm { get; set; } = 0.5;
    /// <summary>Turn anticipation distance, nm.</summary>
    public double TurnAnticipationNm { get; set; } = 3.0;

    private int _activeIndex = -1;
    private bool _hasCaptured;

    /// <summary>The waypoint index currently being flown to.</summary>
    public int ActiveIndex => _activeIndex;

    public void Reset() { _activeIndex = -1; _hasCaptured = false; }

    /// <summary>
    /// Advances the waypoint sequence and returns the desired ground track and
    /// commanded heading for this frame.
    /// </summary>
    public (double groundTrackDeg, double headingDeg, FlightPlanWaypoint? target) Update(
        AircraftState s, NavigationState nav, double windCorrectionScale = 1.0)
    {
        var wps = nav.Waypoints;
        if (wps.Count == 0) return (s.TrueHeading, s.TrueHeading, null);

        // Trust the sim's own GPS waypoint index when it is ahead of ours.
        var simIndex = nav.CurrentWaypointIndex;
        if (simIndex > _activeIndex) _activeIndex = simIndex;
        if (_activeIndex < 0) _activeIndex = Math.Max(0, simIndex);
        if (_activeIndex >= wps.Count) _activeIndex = wps.Count - 1;

        // Before the first capture, skip waypoints we have already passed:
        // rotorcraft routes start at the departure airport reference point,
        // which is ~1 NM astern once the hover climb-out transitions to
        // enroute — without this the aircraft would be commanded back to it.
        if (!_hasCaptured)
        {
            while (_activeIndex < wps.Count - 1 && wps[_activeIndex].HasPosition)
            {
                var wp = wps[_activeIndex];
                var brgTo = GeoMath.BearingDeg(s.Latitude, s.Longitude, wp.Latitude, wp.Longitude);
                if (Math.Abs(GeoMath.AngleDelta(brgTo, s.TrueHeading)) <= 100) break;
                _activeIndex++;
            }
        }

        // Advance when close to the active waypoint.
        var target = wps[_activeIndex];
        if (target.HasPosition)
        {
            var dist = GeoMath.DistanceNm(s.Latitude, s.Longitude, target.Latitude, target.Longitude);
            if (dist < CaptureRadiusNm && _activeIndex < wps.Count - 1)
            {
                _activeIndex++;
                _hasCaptured = true;
                target = wps[_activeIndex];
            }
        }

        // Desired ground track: bearing to the target, or to the next leg when
        // inside the turn anticipation distance of the current one.
        var trackTarget = target;
        if (_activeIndex + 1 < wps.Count)
        {
            var next = wps[_activeIndex + 1];
            if (next.HasPosition && target.HasPosition)
            {
                var distToTarget = GeoMath.DistanceNm(s.Latitude, s.Longitude, target.Latitude, target.Longitude);
                if (distToTarget < TurnAnticipationNm)
                {
                    // Lead the turn: aim at a point partway to the next waypoint.
                    var brg = GeoMath.BearingDeg(target.Latitude, target.Longitude, next.Latitude, next.Longitude);
                    var (lat, lon) = GeoMath.DestinationNm(target.Latitude, target.Longitude, brg, 0.5);
                    trackTarget = new FlightPlanWaypoint { Latitude = lat, Longitude = lon, Ident = target.Ident };
                }
            }
        }

        if (!trackTarget.HasPosition)
            return (s.TrueHeading, s.TrueHeading, target);

        var track = GeoMath.BearingDeg(s.Latitude, s.Longitude, trackTarget.Latitude, trackTarget.Longitude);

        // Wind correction: fly a heading that puts the ground track on course.
        // WindComponents returns crosswind positive when the wind blows from the
        // right of the course; the aircraft then heads left of the track.
        var (_, cross) = Environment.WeatherModel.WindComponents(s.WindDirection, s.WindVelocity, track);
        var tas = Math.Max(Math.Max(s.TrueAirspeed, s.IndicatedAirspeed), 60);
        var wca = Environment.WeatherModel.WindCorrectionAngleDeg(Math.Abs(cross), tas) * windCorrectionScale;
        var heading = GeoMath.NormalizeDeg(track + (cross > 0 ? -wca : cross < 0 ? wca : 0));

        return (track, heading, target);
    }

    /// <summary>Cross-track distance to the active leg, meters.</summary>
    public double CrossTrackMeters(AircraftState s, NavigationState nav)
    {
        var wps = nav.Waypoints;
        if (wps.Count < 2 || _activeIndex < 0 || _activeIndex >= wps.Count - 1) return 0;
        var a = wps[_activeIndex];
        var b = wps[_activeIndex + 1];
        if (!a.HasPosition || !b.HasPosition) return 0;
        return GeoMath.CrossTrackMeters(s.Latitude, s.Longitude, a.Latitude, a.Longitude, b.Latitude, b.Longitude);
    }
}
