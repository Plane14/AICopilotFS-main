using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// One lateral leg: a terminator fix, the fix it is flown from, and the constraints that
/// belong to it. A path is a chain of these, which is what both a flight plan and a
/// published procedure are.
/// </summary>
public sealed class NavLeg
{
    public string Ident { get; set; } = "";
    /// <summary>Fix the leg is flown from. Null on the first leg, which has no predecessor.</summary>
    public NavLeg? Previous { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Altitude of the fix above mean sea level, in feet.</summary>
    public double AltitudeFt { get; set; }
    /// <summary>Minimum altitude the leg may be flown at, in feet, or 0 when unrestricted.</summary>
    public double MinimumAltitudeFt { get; set; }
    /// <summary>Maximum altitude the leg may be flown at, in feet, or 0 when unrestricted.</summary>
    public double MaximumAltitudeFt { get; set; }
    public double SpeedLimitKt { get; set; }

    /// <summary>True when the fix has to be overflown rather than turned at.</summary>
    public bool FlyOver { get; set; }

    /// <summary>Which part of the route the leg belongs to, for status and for choosing what to follow.</summary>
    public NavLegKind Kind { get; set; } = NavLegKind.Enroute;

    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude) && (Latitude != 0 || Longitude != 0);

    public override string ToString() => $"{Kind} {Ident}";
}

public enum NavLegKind
{
    /// <summary>The departure runway threshold, where a route starts on the ground.</summary>
    RunwayStart,
    /// <summary>A published departure procedure leg.</summary>
    Sid,
    /// <summary>A leg of the route the plan files.</summary>
    Enroute,
    /// <summary>A published arrival procedure leg.</summary>
    Star,
    /// <summary>A leg of the instrument approach.</summary>
    Approach,
    /// <summary>A leg of the published missed approach.</summary>
    Missed,
    /// <summary>A fix the controller sent the aircraft to.</summary>
    Direct
}

/// <summary>
/// Where the aircraft is along a path, and which way it has to fly. Computed from the
/// aircraft's own position each frame, so nothing about it is remembered between frames:
/// the path itself is the only state, and that is what makes it safe to hand the follower
/// a plan it has never seen before while airborne.
/// </summary>
public readonly record struct NavGuidance
{
    public NavGuidance(
        int activeIndex,
        NavLeg? activeLeg,
        double desiredTrackTrueDeg,
        double crossTrackMeters,
        double distanceToTerminatorMeters,
        bool complete)
    {
        ActiveIndex = activeIndex;
        ActiveLeg = activeLeg;
        DesiredTrackTrueDeg = desiredTrackTrueDeg;
        CrossTrackMeters = crossTrackMeters;
        DistanceToTerminatorMeters = distanceToTerminatorMeters;
        Complete = complete;
    }

    /// <summary>Index of the leg being flown; -1 before the path has started.</summary>
    public int ActiveIndex { get; }

    public NavLeg? ActiveLeg { get; }

    /// <summary>
    /// Course to steer, in degrees true. A leg course corrected by the cross-track error,
    /// so an aircraft that drifted off the line is steered back onto it rather than flying
    /// straight at the fix.
    /// </summary>
    public double DesiredTrackTrueDeg { get; }

    /// <summary>How far the aircraft is off the leg's line; positive is right of course.</summary>
    public double CrossTrackMeters { get; }

    public double DistanceToTerminatorMeters { get; }

    /// <summary>True when every leg has been sequenced and the path is finished.</summary>
    public bool Complete { get; }
}

/// <summary>
/// Follows a chain of legs the way a flight management system follows a route: sequence
/// from one terminator to the next, correct cross-track, and anticipate the turn at a
/// fly-by fix instead of overshooting it.
/// <para>
/// Every rule here is geometric and driven from the aircraft's current position, which is
/// why the class can be tested without a simulator: it never reads the aircraft state, it
/// is told where the aircraft is.
/// </para>
/// </summary>
public sealed class NavPathFollower
{
    /// <summary>Cross-track angle commanded per metre off track, in degrees.</summary>
    private const double CrossTrackGainDegPerMeter = 0.010;

    /// <summary>Largest correction that will be commanded, in degrees.</summary>
    private const double MaxCrossTrackCorrectionDeg = 30;

    /// <summary>How far short of a fix the turn is started, as seconds of ground speed.</summary>
    private const double TurnAnticipationSeconds = 9;

    /// <summary>Ceiling on the turn anticipation so a fast aircraft does not cut a corner.</summary>
    private const double MaxTurnAnticipationMeters = 2500;

    /// <summary>Smallest turn anticipation, so a slow aircraft still starts turning in time.</summary>
    private const double MinTurnAnticipationMeters = 150;

    public NavPathFollower(IReadOnlyList<NavLeg> legs, double magneticVariationDeg = 0)
    {
        Legs = legs ?? throw new ArgumentNullException(nameof(legs));
        MagneticVariationDeg = magneticVariationDeg;
    }

    public IReadOnlyList<NavLeg> Legs { get; }

    /// <summary>Magnetic variation of the airport the path belongs to, in degrees.</summary>
    public double MagneticVariationDeg { get; set; }

    /// <summary>Index of the leg being flown, or -1 before the path has started.</summary>
    public int ActiveIndex { get; private set; } = -1;

    public NavLeg? ActiveLeg => ActiveIndex >= 0 && ActiveIndex < Legs.Count ? Legs[ActiveIndex] : null;

    /// <summary>True when every leg has been sequenced.</summary>
    public bool Complete => ActiveIndex >= Legs.Count;

    /// <summary>
    /// Starts the follower at the leg the aircraft is on, which is what loading a plan
    /// mid-flight has to do: the aircraft is already somewhere along the route and the
    /// first legs are behind it. A leg's line runs from its predecessor to its terminator,
    /// so the leg being flown is the first one whose line the aircraft has not yet run off
    /// the end of - and an aircraft standing at the start of the route is on the first leg
    /// with a line at all.
    /// </summary>
    public int ActivateAtNearestLeg(double latitude, double longitude)
    {
        ActiveIndex = FindNearestLegIndex(latitude, longitude);
        return ActiveIndex;
    }

    public int FindNearestLegIndex(double latitude, double longitude)
    {
        if (Legs.Count == 0) return -1;

        for (var i = 0; i < Legs.Count; i++)
        {
            if (!Legs[i].HasPosition) continue;

            var start = PreviousPosition(i);
            // The first leg has no predecessor, so it has no line of its own yet: the
            // aircraft is on it until a later leg's segment covers it, decided below.
            if (!start.HasValue) continue;

            var terminator = Position(Legs[i]);
            var (along, _) = ProjectOntoLeg(latitude, longitude, i);
            var length = Geo.DistanceMeters(start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);

            // Before this leg's start: the aircraft is on the leg before it, which at the
            // head of the route is the first leg. i is at least 1 here, because the first
            // leg has no predecessor and was skipped above.
            if (along < 0) return i - 1;

            // On this leg's segment: this is the leg being flown - the first one whose
            // line the aircraft has not yet run off the end of. Selecting on "along > 0"
            // alone returned a leg whose terminator was already behind the aircraft,
            // because a position past the far end still projects to a positive "along".
            if (along <= length) return i;

            // Past this leg: keep looking at the later ones.
        }

        // Past the last leg with a line, or a route that has only its first leg: the
        // aircraft belongs on the last leg that carries a position.
        for (var i = Legs.Count - 1; i >= 0; i--)
            if (Legs[i].HasPosition) return i;
        return Legs.Count - 1;
    }

    /// <summary>
    /// Advances the follower to <paramref name="latitude"/>, <paramref name="longitude"/>
    /// and returns the guidance for that position. Called once per control frame.
    /// </summary>
    public NavGuidance Update(double latitude, double longitude, double groundSpeedKnots = 0)
    {
        if (Legs.Count == 0)
        {
            return new NavGuidance(-1, null, 0, 0, 0, true);
        }

        if (ActiveIndex < 0) ActivateAtNearestLeg(latitude, longitude);

        if (ActiveIndex >= Legs.Count)
        {
            return new NavGuidance(ActiveIndex, null, 0, 0, 0, true);
        }

        var groundSpeedMetersPerSecond = Math.Max(0, groundSpeedKnots) * 0.514444;

        for (var guard = 0; guard <= Legs.Count; guard++)
        {
            if (ActiveIndex >= Legs.Count) break;

            var leg = Legs[ActiveIndex];
            if (!leg.HasPosition)
            {
                ActiveIndex++;
                continue;
            }

            var terminator = Position(leg);
            var distanceToFix = Geo.DistanceMeters(latitude, longitude, terminator.lat, terminator.lon);

            var catchRadius = CatchRadiusMeters(groundSpeedMetersPerSecond);

            // A leg is finished when the fix is inside the catch radius, or when the
            // aircraft has genuinely passed it: past the abeam point of a fix the aircraft
            // is no longer approaching it, and waiting for the radius would let an
            // overshoot run on down the next leg.
            double? along = null;
            double crossTrack = 0;
            var start = PreviousPosition(ActiveIndex);
            if (start.HasValue)
            {
                (along, crossTrack) = ProjectOntoLeg(latitude, longitude, ActiveIndex);
            }

            var passed = false;
            if (along.HasValue && start.HasValue)
            {
                var legLength = Geo.DistanceMeters(
                    start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);
                passed = along.Value > legLength && Math.Abs(crossTrack) < 1500;
            }

            var isLast = ActiveIndex == Legs.Count - 1;
            var anticipation = TurnAnticipationMeters(groundSpeedMetersPerSecond);
            var turnEarly = !isLast &&
                            !Legs[ActiveIndex].FlyOver &&
                            distanceToFix < catchRadius + anticipation;

            if (distanceToFix <= catchRadius || passed || turnEarly)
            {
                // The last leg completes rather than advancing past the end of the path:
                // the end of an approach is the threshold, and running past it is not a leg.
                if (isLast && !turnEarly)
                {
                    ActiveIndex++;
                    break;
                }

                ActiveIndex++;
                continue;
            }

            return Guidance(latitude, longitude, crossTrack, distanceToFix,
                groundSpeedMetersPerSecond);
        }

        return new NavGuidance(ActiveIndex, null, 0, 0, 0, true);
    }

    private NavGuidance Guidance(
        double latitude, double longitude, double crossTrack, double distanceToFix,
        double groundSpeedMetersPerSecond)
    {
        var index = ActiveIndex;
        var leg = Legs[index];
        var start = PreviousPosition(index);
        var terminator = Position(leg);

        double course;
        if (start.HasValue)
        {
            course = Geo.BearingDegrees(start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);
        }
        else
        {
            // The first leg has no predecessor, so the leg is the straight line from where
            // the aircraft is to its terminator.
            course = Geo.BearingDegrees(latitude, longitude, terminator.lat, terminator.lon);
        }

        var correction = Math.Clamp(-crossTrack * CrossTrackGainDegPerMeter,
            -MaxCrossTrackCorrectionDeg, MaxCrossTrackCorrectionDeg);
        var desired = Geo.NormalizeDegrees(course + correction);

        // Near the fix the course to steer by is the bearing to the next one, blended in
        // over the anticipation distance: a fly-by turn started at the fix is a turn made
        // too late, and the corner is cut from the outside.
        if (!Complete && index + 1 < Legs.Count)
        {
            var next = Legs[index + 1];
            if (next.HasPosition)
            {
                var nextTerminator = Position(next);
                var toNext = Geo.BearingDegrees(latitude, longitude, nextTerminator.lat, nextTerminator.lon);
                var toFix = Geo.BearingDegrees(latitude, longitude, terminator.lat, terminator.lon);

                var anticipation = TurnAnticipationMeters(groundSpeedMetersPerSecond);
                var blend = anticipation <= 0
                    ? 0
                    : 1.0 - Math.Clamp(distanceToFix / anticipation, 0, 1);

                if (blend > 0)
                {
                    var delta = Geo.NormalizeDegrees(toNext - toFix);
                    desired = Geo.NormalizeDegrees(toFix + delta * blend * 0.5);
                }
            }
        }

        return new NavGuidance(index, leg, desired, crossTrack, distanceToFix, false);
    }

    /// <summary>The course of the leg at <paramref name="index"/>, in degrees true.</summary>
    public double LegCourseTrueDeg(int index)
    {
        if (index < 0 || index >= Legs.Count) return 0;

        var terminator = Position(Legs[index]);
        var start = PreviousPosition(index);
        if (start.HasValue)
        {
            return Geo.BearingDegrees(start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);
        }

        return 0;
    }

    /// <summary>Magnetic course of the leg, which is what the autopilot steers.</summary>
    public double LegCourseMagneticDeg(int index)
    {
        var trueCourse = LegCourseTrueDeg(index);
        return trueCourse == 0 ? 0 : TrueToMagnetic(trueCourse, MagneticVariationDeg);
    }

    /// <summary>
    /// Converts a true course to magnetic using the path's own variation, so a caller that
    /// already has a true track does not have to know where the variation came from.
    /// </summary>
    public static double TrueToMagnetic(double trueCourseDeg, double magneticVariationDeg)
    {
        var magnetic = trueCourseDeg - magneticVariationDeg;
        while (magnetic < 0) magnetic += 360;
        while (magnetic >= 360) magnetic -= 360;
        return magnetic;
    }

    /// <summary>How far from a fix still counts as reaching it.</summary>
    public static double CatchRadiusMeters(double groundSpeedMetersPerSecond) =>
        Math.Clamp(groundSpeedMetersPerSecond * 1.2 + 120, 120, 3000);

    /// <summary>How far short of a fix the turn to the next leg is started.</summary>
    public static double TurnAnticipationMeters(double groundSpeedMetersPerSecond) =>
        Math.Clamp(groundSpeedMetersPerSecond * TurnAnticipationSeconds,
            MinTurnAnticipationMeters, MaxTurnAnticipationMeters);

    /// <summary>
    /// How far the aircraft is off the leg's line and how far along it it is. The
    /// cross-track is signed: positive is right of the course, which is what makes the
    /// correction in <see cref="Guidance"/> point back towards the line rather than away
    /// from it. <see cref="Geo.ProjectOntoSegment"/> returns an unsigned offset, so the
    /// side is recovered from the bearing of the aircraft about the leg's start.
    /// </summary>
    private (double along, double signedCrossTrack) ProjectOntoLeg(
        double latitude, double longitude, int index)
    {
        var start = PreviousPosition(index);
        var terminator = Position(Legs[index]);
        if (!start.HasValue) return (0, 0);

        var (along, offset) = Geo.ProjectOntoSegment(
            latitude, longitude, start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);

        var course = Geo.BearingDegrees(start.Value.lat, start.Value.lon, terminator.lat, terminator.lon);
        var toAircraft = Geo.BearingDegrees(start.Value.lat, start.Value.lon, latitude, longitude);
        var side = Geo.NormalizeDegrees(toAircraft - course);

        // Right of course at an airport scale is the side a positive bearing difference
        // from the leg's course describes, on either hemisphere of a north-up frame.
        return (along, side >= 0 ? offset : -offset);
    }

    private (double lat, double lon) Position(NavLeg leg) => (leg.Latitude, leg.Longitude);

    private (double lat, double lon)? PreviousPosition(int index)
    {
        var leg = Legs[index];
        if (leg.Previous is { HasPosition: true } previous) return (previous.Latitude, previous.Longitude);

        // Without an explicit predecessor, the previous leg's terminator is where this
        // leg starts, which is how a route read off a flight plan is chained.
        for (var i = index - 1; i >= 0; i--)
        {
            if (Legs[i].HasPosition) return (Legs[i].Latitude, Legs[i].Longitude);
        }

        return null;
    }
}
