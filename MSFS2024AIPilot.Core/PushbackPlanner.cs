using System;

namespace MSFS2024AIPilot.Core;

/// <summary>What the stand-holding stage of the taxi-out should do next.</summary>
public enum PushbackDecision
{
    /// <summary>Stay on the stand: the tug has not been answered yet.</summary>
    Wait,
    /// <summary>The tug has the aircraft and is pushing it back.</summary>
    Towing,
    /// <summary>The pushback has done its work: stop and dismiss the tug.</summary>
    Dismiss,
    /// <summary>
    /// No pushback is going to happen - nothing is on offer, the controller never answered,
    /// or the tug is attached and not moving - so the aircraft taxis out under its own power.
    /// </summary>
    GiveUp
}

/// <summary>How long a pushback is waited for, and how far it has to carry the aircraft.</summary>
public sealed class PushbackTuning
{
    /// <summary>
    /// How long the aircraft waits on the stand for a tug before it taxes out under its own
    /// power. A controller that cannot grant a pushback must not park the aircraft at the
    /// gate for ever, exactly as an unavailable IFR clearance does not.
    /// </summary>
    public TimeSpan AttachTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>
    /// How long after attachment before the pushback may be stopped, so the tug has time to
    /// take up the load and get the aircraft rolling rather than being dismissed on the
    /// frame it hooked up.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The longest a pushback is allowed to run, whatever it has achieved.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a tug that is attached but not moving the aircraft is given before the
    /// pushback is written off. A tug that tows nothing is a pushback that is not happening.
    /// </summary>
    public TimeSpan TugStallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far the pushback has to carry the aircraft from where it was parked to count as
    /// complete. At a stand that is roughly the tail clearance the manoeuvre exists for.
    /// </summary>
    public double ClearMeters { get; init; } = 25;
}

/// <summary>Where the stand-holding stage has got to, as the flight logic observed it.</summary>
public sealed class PushbackProgress
{
    /// <summary>True once a pushback has been asked for on this stand.</summary>
    public bool Requested { get; init; }
    /// <summary>When the tug was asked for.</summary>
    public DateTime RequestedUtc { get; init; }
    /// <summary>True once the tug has been seen attached at least once.</summary>
    public bool SawTug { get; init; }
    /// <summary>When the tug attached.</summary>
    public DateTime AttachedUtc { get; init; }
    /// <summary>Where the aircraft stood when the tug attached, for measuring how far it has been pushed.</summary>
    public (double lat, double lon)? AttachPoint { get; init; }
    /// <summary>When the aircraft was last seen being moved by the tug.</summary>
    public DateTime LastMovedUtc { get; init; }
}

/// <summary>
/// Decides what the pushback the pilot asked for is doing, and when it is over.
/// <para>
/// A pushback has no clearance variable at all: the simulator publishes whether a tug is
/// attached, whether one is on offer where the aircraft stands, and which way it is
/// pointing. Everything the pilot needs to decide - ask, wait, hold, dismiss - follows from
/// those, plus where the aircraft stood when the tug hooked up. The decisions live here
/// rather than in the pilot so they are exercised without a running simulator.
/// </para>
/// </summary>
public static class PushbackPlanner
{
    /// <summary>Ground speed, in knots, above which the tug counts as moving the aircraft.</summary>
    public const double TowingGroundSpeedKt = 0.5;

    /// <summary>
    /// Whether the parking space the aircraft is on offers a pushback and none is attached.
    /// This is the simulator's own answer to "is there anything to ask for?".
    /// </summary>
    public static bool OnOffer(AircraftState s) =>
        s.OnGround && s.ATC.OnParkingSpot && s.Pushback.Available && !s.Pushback.Attached;

    /// <summary>
    /// How far the tug has carried the aircraft from where it attached, in metres.
    /// </summary>
    public static double PushedMeters(AircraftState s, PushbackProgress progress) =>
        progress.AttachPoint is { } point
            ? Geo.DistanceMeters(s.Latitude, s.Longitude, point.lat, point.lon)
            : 0;

    /// <summary>
    /// What the stand-holding stage should do now.
    /// <paramref name="now"/> is the frame time, passed in rather than read from the clock
    /// so the whole decision is a function of its arguments.
    /// </summary>
    public static PushbackDecision Decide(
        AircraftState s, PushbackProgress progress, DateTime now, PushbackTuning? tuning = null)
    {
        var t = tuning ?? new PushbackTuning();

        if (s.Pushback.Attached)
        {
            // The tug is on the nose. It is given a moment to take up the load before
            // anything is expected of it, and then either it is pushing the aircraft back or
            // the pushback is not happening.
            var attachedFor = now - progress.AttachedUtc;
            if (attachedFor < t.Settle) return PushbackDecision.Towing;

            // Done: the aircraft has been carried clear of the stand it parked on, or the
            // stand is behind it, or the manoeuvre has run as long as it is allowed to.
            if (PushedMeters(s, progress) >= t.ClearMeters) return PushbackDecision.Dismiss;
            if (!s.ATC.OnParkingSpot) return PushbackDecision.Dismiss;
            if (attachedFor >= t.MaxDuration) return PushbackDecision.Dismiss;

            // Attached, brakes off, and still not moving: write it off rather than hold the
            // aircraft on the stand for the whole duration on the strength of a tug that is
            // doing nothing.
            if (s.GroundSpeed < TowingGroundSpeedKt &&
                progress.LastMovedUtc != default &&
                now - progress.LastMovedUtc >= t.TugStallTimeout)
            {
                return PushbackDecision.GiveUp;
            }

            return PushbackDecision.Towing;
        }

        // The tug has been and gone: the dismissal has already been handled by the flight
        // logic, and all that is left is to taxi out from where the aircraft now stands.
        if (progress.SawTug) return PushbackDecision.Dismiss;

        // Nothing on offer where the aircraft stands, and nothing ever asked for: there is
        // no pushback to wait for, on a ramp or at a stand without the service.
        if (!progress.Requested) return OnOffer(s) ? PushbackDecision.Wait : PushbackDecision.GiveUp;

        return now - progress.RequestedUtc >= t.AttachTimeout
            ? PushbackDecision.GiveUp
            : PushbackDecision.Wait;
    }
}
