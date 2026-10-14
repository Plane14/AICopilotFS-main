using System;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Turbulence intensity. MSFS publishes no turbulence variable, so this is derived from
/// the high-frequency variation of the normal load factor about its slow-moving mean - the
/// vertical gusts an accelerometer sees - by <see cref="TurbulenceTracker"/>.
/// </summary>
public enum TurbulenceLevel
{
    Calm,
    Light,
    Moderate,
    Severe
}

/// <summary>
/// Wind, turbulence, ice and surface-contamination arithmetic, written as pure functions so
/// the reactions the pilot takes can be exercised without a running simulator.
/// <para>
/// Every wind calculation takes a <em>course heading</em> in the same angular frame as the
/// wind direction - a compass/magnetic reference at every call site, matching the heading
/// the autopilot is commanded with. The frame is never mixed within a single call.
/// </para>
/// </summary>
public static class WeatherModel
{
    // Approach-speed bumps. Standard practice is to add a fraction of the steady wind and
    // the full gust increment to the reference speed, each capped; ice raises the stall
    // speed; turbulence asks for a little more margin. The three are combined under one cap
    // so a bad day never adds an unmanageable number of knots.
    public const double MaxApproachWindIncrementKt = 12;
    public const double MaxApproachIceIncrementKt = 8;
    public const double MaxApproachTurbulenceIncrementKt = 8;
    public const double MaxApproachWeatherIncrementKt = 25;

    /// <summary>A headwind at or above this many knots on final is worth logging.</summary>
    public const double SignificantCrosswindKt = 12;

    // STRUCTURAL ICE PCT is 0..100 (100 fully iced). Trace ice is already significant on a
    // wing; the approach increment saturates well before total coverage.
    public const double IcePerceptionThreshold = 1.0;
    public const double ModerateIcePercent = 15.0;
    public const double SevereIcePercent = 40.0;

    // OAT band (Celsius) in which airframe icing can occur, given visible moisture.
    public const double IcingUpperTempC = 10.0;
    public const double IcingLowerTempC = -40.0;

    /// <summary>Standard gravity in ft/s², used to convert ACCELERATION BODY Y into g.</summary>
    public const double StandardGravityFtPerSec2 = 32.174;

    // Takeoff/landing roll factors for a contaminated surface (SURFACE CONDITION enum).
    private const double WetRollFactor = 1.15;
    private const double IcyRollFactor = 2.0;
    private const double SnowRollFactor = 1.6;

    // Crosswind/gust the bank-limit softening responds to; the airframe is not chased into
    // a gust.
    private static readonly TurbulenceLevel[] _levels = (TurbulenceLevel[])Enum.GetValues(typeof(TurbulenceLevel));

    /// <summary>Headwind and crosswind resolved along a course.</summary>
    /// <param name="windFromDeg">Direction the wind blows from, compass degrees.</param>
    /// <param name="windKt">Wind speed.</param>
    /// <param name="courseDeg">Heading/course to resolve against, compass degrees.</param>
    /// <returns>
    /// <c>headwind</c> is positive for a headwind (negative for a tailwind);
    /// <c>crosswind</c> is positive when the wind is from the right.
    /// </returns>
    public static (double headwindKt, double crosswindKt) WindComponents(
        double windFromDeg, double windKt, double courseDeg)
    {
        var angle = Geo.ToRadians(Geo.NormalizeDegrees(windFromDeg - courseDeg));
        return (windKt * Math.Cos(angle), windKt * Math.Sin(angle));
    }

    /// <summary>Absolute crosswind on a course, in knots.</summary>
    public static double CrosswindMagnitudeKt(double windFromDeg, double windKt, double courseDeg)
        => Math.Abs(WindComponents(windFromDeg, windKt, courseDeg).crosswindKt);

    /// <summary>
    /// Heading offset, in degrees, that points the nose into the wind so the ground track
    /// follows the course. Positive adds to the heading (to the right), which cancels a wind
    /// from the right. A crosswind greater than the airspeed is clamped to a wing-level
    /// crosswind, because asin is only defined there.
    /// </summary>
    public static double WindCorrectionAngleDeg(double crosswindKt, double trueAirspeedKt)
    {
        if (trueAirspeedKt < 30) return 0;
        var ratio = crosswindKt / trueAirspeedKt;
        if (ratio >= 1) return 90;
        if (ratio <= -1) return -90;
        return Geo.ToDegrees(Math.Asin(ratio));
    }

    /// <summary>The knots added to the approach speed for a steady headwind.</summary>
    public static double ApproachWindIncrementKt(double headwindKt)
        => Math.Clamp(0.5 * Math.Abs(headwindKt), 0, MaxApproachWindIncrementKt);

    /// <summary>The knots added to the reference (stall-margin) speed for airframe ice.</summary>
    public static double ApproachIceIncrementKt(double structuralIcePercent)
    {
        if (structuralIcePercent < IcePerceptionThreshold) return 0;
        // Linear to the moderate threshold, then a smaller step to severe, then saturated.
        var ice = Math.Min(structuralIcePercent, SevereIcePercent);
        var increment = ice <= ModerateIcePercent
            ? MaxApproachIceIncrementKt * (ice / ModerateIcePercent)
            : MaxApproachIceIncrementKt;
        return increment;
    }

    /// <summary>The knots added to the approach speed for turbulence.</summary>
    public static double ApproachTurbulenceIncrementKt(TurbulenceLevel level) => level switch
    {
        TurbulenceLevel.Calm => 0,
        TurbulenceLevel.Light => 2,
        TurbulenceLevel.Moderate => 5,
        TurbulenceLevel.Severe => MaxApproachTurbulenceIncrementKt,
        _ => 0
    };

    /// <summary>
    /// The combined approach-speed increment for wind, ice and turbulence, capped so a bad
    /// day never pushes the approach speed to something unmanageable.
    /// </summary>
    public static double ApproachWeatherIncrementKt(double headwindKt, double structuralIcePercent, TurbulenceLevel level)
    {
        var total = ApproachWindIncrementKt(headwindKt)
                    + ApproachIceIncrementKt(structuralIcePercent)
                    + ApproachTurbulenceIncrementKt(level);
        return Math.Clamp(total, 0, MaxApproachWeatherIncrementKt);
    }

    /// <summary>
    /// Whether conditions are conducive to airframe icing: moisture in cloud or falling, at a
    /// temperature in the icing band, or ice already collecting on the structure. This is what
    /// the checklist's "as required" ice-protection items are settled by.
    /// </summary>
    public static bool IcingConditions(double oatC, bool inCloud, double precipState, double structuralIcePercent)
    {
        if (structuralIcePercent >= IcePerceptionThreshold) return true;
        if (oatC > IcingUpperTempC || oatC < IcingLowerTempC) return false;
        return inCloud || precipState > 0.5;
    }

    /// <summary>
    /// Factor by which a crosswind / tailwind and a contaminated surface multiply the runway
    /// the roll needs. Written as ( (Vr - w) / (Vr + w) )^2 for the wind, the standard
    /// ground-roll scaling: a headwind shortens the roll, a tailwind lengthens it.
    /// </summary>
    public static double TakeoffRunFactor(double headwindKt, double rotationKt, double surfaceCondition, bool surfaceInfoValid)
    {
        var windFactor = WindRunFactor(headwindKt, rotationKt);
        return windFactor * (surfaceInfoValid ? SurfaceRollFactor(surfaceCondition) : 1.0);
    }

    /// <summary>Ground-roll scaling for wind alone, clamped to a sane band.</summary>
    public static double WindRunFactor(double headwindKt, double referenceKt)
    {
        if (referenceKt < 40) return 1.0;
        // Never let an extreme wind produce an unbounded factor.
        var w = Math.Clamp(headwindKt, -0.4 * referenceKt, 0.4 * referenceKt);
        var ratio = (referenceKt - w) / (referenceKt + w);
        return Math.Clamp(ratio * ratio, 0.6, 1.8);
    }

    /// <summary>Runway-length multiplier for the surface under the aircraft.</summary>
    public static double SurfaceRollFactor(double surfaceCondition) => (int)Math.Round(surfaceCondition) switch
    {
        1 => WetRollFactor,
        2 => IcyRollFactor,
        3 => SnowRollFactor,
        _ => 1.0
    };

    /// <summary>Whether the surface under the aircraft is contaminated (wet, icy or snow).</summary>
    public static bool SurfaceIsContaminated(double surfaceCondition, bool surfaceInfoValid)
        => surfaceInfoValid && (int)Math.Round(surfaceCondition) is not 0;

    /// <summary>Human-readable surface state, for status output.</summary>
    public static string SurfaceText(double surfaceCondition, bool surfaceInfoValid)
    {
        if (!surfaceInfoValid) return "unknown";
        return (int)Math.Round(surfaceCondition) switch
        {
            1 => "wet",
            2 => "icy",
            3 => "snow",
            _ => "dry"
        };
    }

    /// <summary>Maps a deviation severity in the range 0..1 onto a turbulence level.</summary>
    public static TurbulenceLevel Classify(double severity) => severity switch
    {
        < 0.12 => _levels[(int)TurbulenceLevel.Calm],
        < 0.38 => _levels[(int)TurbulenceLevel.Light],
        < 0.68 => _levels[(int)TurbulenceLevel.Moderate],
        _ => _levels[(int)TurbulenceLevel.Severe]
    };

    /// <summary>
    /// RMS load-factor deviation, in g, mapped onto a 0..1 severity. Calm air is a few
    /// hundredths of a g of noise; roughly a quarter of a g of RMS deviation is severe.
    /// </summary>
    public static double SeverityFromRmsG(double rmsG)
        => Math.Clamp((rmsG - 0.03) / 0.22, 0, 1);

    /// <summary>Maximum bank angle to command in this turbulence, so gusts are not chased.</summary>
    public static double ControlBankDeg(TurbulenceLevel level) => level switch
    {
        TurbulenceLevel.Calm => 25,
        TurbulenceLevel.Light => 22,
        TurbulenceLevel.Moderate => 18,
        TurbulenceLevel.Severe => 13,
        _ => 25
    };

    /// <summary>
    /// How much to reduce an enroute speed target in turbulence, toward a turbulence
    /// penetration speed, without dropping it under a sane floor. ATC speed assignments are
    /// never reduced - this applies only to the aircraft's own perf-derived target.
    /// </summary>
    public static double EnrouteSpeedReductionKt(TurbulenceLevel level, double currentKt)
    {
        var reduction = level switch
        {
            TurbulenceLevel.Moderate => Math.Min(0.10 * currentKt, 40),
            TurbulenceLevel.Severe => Math.Min(0.15 * currentKt, 60),
            _ => 0
        };
        // Never slow below a sane floor, and never speed up a slow aircraft over its own
        // normal target: the floor is capped at the target itself.
        var safeFloor = Math.Min(currentKt, Math.Max(160, 0.5 * currentKt));
        return Math.Max(currentKt - reduction, safeFloor);
    }
}

/// <summary>
/// Estimates turbulence intensity from the normal load factor. A slow baseline separates the
/// steady load (1 g in level flight, 1/cos(bank) in a turn) from the high-frequency deviation
/// that is turbulence; an exponentially-weighted mean of the squared deviation is the RMS
/// read by <see cref="WeatherModel.SeverityFromRmsG"/>.
/// </summary>
public sealed class TurbulenceTracker
{
    // The steady load is tracked with a few-second time constant so a turn entry does not
    // register as turbulence, while the deviation RMS responds over about a second.
    private const double BaselineTauSeconds = 2.5;
    private const double RmsTauSeconds = 1.2;

    private double _baseline = 1.0;
    private double _meanSquare;
    private double _severity;
    private bool _primed;

    /// <summary>Current severity in the range 0..1.</summary>
    public double Severity => _severity;

    /// <summary>Current turbulence level.</summary>
    public TurbulenceLevel Level => WeatherModel.Classify(_severity);

    /// <summary>Load factor used for the baseline: 1.0 means "no gust measured".</summary>
    public double BaselineG => _baseline;

    public void Reset()
    {
        _baseline = 1.0;
        _meanSquare = 0;
        _severity = 0;
        _primed = false;
    }

    /// <summary>
    /// Advances the estimate one frame. <paramref name="quiet"/> (the aircraft is on the
    /// ground) forces a steady reading so taxi chop is not mistaken for air turbulence.
    /// </summary>
    public void Update(double loadFactorG, double dt, bool quiet = false)
    {
        var g = quiet ? 1.0 : (loadFactorG > -5 && loadFactorG < 20 ? loadFactorG : 1.0);
        if (dt <= 0 || double.IsNaN(dt) || double.IsInfinity(dt)) dt = 0.05;

        if (!_primed)
        {
            _baseline = g;
            _primed = true;
        }

        var baselineAlpha = 1.0 - Math.Exp(-dt / BaselineTauSeconds);
        _baseline += (g - _baseline) * Math.Clamp(baselineAlpha, 0, 1);

        var deviation = g - _baseline;
        var meanSquareAlpha = 1.0 - Math.Exp(-dt / RmsTauSeconds);
        _meanSquare += (deviation * deviation - _meanSquare) * Math.Clamp(meanSquareAlpha, 0, 1);

        _severity = WeatherModel.SeverityFromRmsG(Math.Sqrt(Math.Max(0, _meanSquare)));
    }
}
