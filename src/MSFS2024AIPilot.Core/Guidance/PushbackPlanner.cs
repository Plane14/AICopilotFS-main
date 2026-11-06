using System;

namespace MSFS2024AIPilot.Core.Guidance;

/// <summary>
/// The pushback decision, as a pure function of observed tug state and progress.
/// Kept simulator-free so it can be unit-tested.
/// </summary>
public enum PushbackDecision
{
    /// <summary>Nothing to do yet: no tug attached and none requested.</summary>
    Wait,
    /// <summary>The tug is attached and towing.</summary>
    Towing,
    /// <summary>Pushback complete: release the tug.</summary>
    Dismiss,
    /// <summary>Pushback failed or unavailable: tax out under own power.</summary>
    GiveUp,
}

public sealed class PushbackTuning
{
    /// <summary>How long to wait for the tug to attach after requesting, seconds.</summary>
    public TimeSpan AttachTimeout { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>Settle time after attach before judging movement, seconds.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Maximum total pushback duration, seconds.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>Attached but not moving for this long = stalled tug, seconds.</summary>
    public TimeSpan TugStallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Distance pushed that counts as clear of the stand, meters.</summary>
    public double ClearMeters { get; init; } = 25;
}

/// <summary>What the pushback stage has observed so far.</summary>
public sealed record PushbackProgress
{
    /// <summary>A pushback request has been sent.</summary>
    public bool Requested { get; init; }
    public DateTime RequestedUtc { get; init; }
    /// <summary>The tug has attached at least once.</summary>
    public bool SawTug { get; init; }
    public DateTime AttachedUtc { get; init; }
    /// <summary>Position when the tug hooked up.</summary>
    public (double lat, double lon)? AttachPoint { get; init; }
    /// <summary>Last time the aircraft moved while attached.</summary>
    public DateTime LastMovedUtc { get; init; }
    public DateTime StartedUtc { get; init; }
    /// <summary>Current distance pushed from the attach point, meters.</summary>
    public double PushedMeters { get; init; }
    /// <summary>Aircraft has left the parking space.</summary>
    public bool OffParking { get; init; }
}

public static class PushbackPlanner
{
    /// <summary>Ground speed below which the aircraft counts as stopped, knots.</summary>
    public const double TowingGroundSpeedKt = 0.5;

    /// <summary>True when the stand offers a tug and none is attached.</summary>
    public static bool OnOffer(State.AircraftState s) =>
        s.Pushback.Available && !s.Pushback.Attached;

    public static double PushedMeters(State.AircraftState s, PushbackProgress p) =>
        p.AttachPoint is { } ap
            ? Geo.GeoMath.DistanceMeters(ap.lat, ap.lon, s.Latitude, s.Longitude)
            : p.PushedMeters;

    /// <summary>
    /// Decides what the pushback stage should do now.
    /// </summary>
    public static PushbackDecision Decide(
        State.AircraftState s, PushbackProgress p, PushbackTuning? tuning = null, DateTime utcNow = default)
    {
        tuning ??= new PushbackTuning();
        if (utcNow == default) utcNow = DateTime.UtcNow;

        // Tug attached: towing.
        if (s.Pushback.Attached)
        {
            var attachedFor = utcNow - (p.AttachedUtc == default ? utcNow : p.AttachedUtc);
            if (attachedFor < tuning.Settle) return PushbackDecision.Towing;

            var pushed = PushedMeters(s, p);
            if (pushed >= tuning.ClearMeters || p.OffParking) return PushbackDecision.Dismiss;
            if (utcNow - p.StartedUtc > tuning.MaxDuration) return PushbackDecision.Dismiss;

            // Attached but not moving = stalled tug.
            if (utcNow - p.LastMovedUtc > tuning.TugStallTimeout && s.GroundSpeed < TowingGroundSpeedKt)
                return PushbackDecision.GiveUp;
            return PushbackDecision.Towing;
        }

        // No tug attached.
        if (!p.Requested)
        {
            // Stand offers a tug: wait for the request stage to fire.
            return s.Pushback.Available ? PushbackDecision.Wait : PushbackDecision.GiveUp;
        }

        // Requested but never attached: give up after the attach timeout.
        if (!p.SawTug && utcNow - p.RequestedUtc > tuning.AttachTimeout)
            return PushbackDecision.GiveUp;

        // Tug attached then released without us dismissing: treat as complete.
        if (p.SawTug) return PushbackDecision.Dismiss;

        return PushbackDecision.Wait;
    }
}
