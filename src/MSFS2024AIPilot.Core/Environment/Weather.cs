using System;

namespace MSFS2024AIPilot.Core.Environment;

public enum TurbulenceLevel
{
    Calm,
    Light,
    Moderate,
    Severe,
}

/// <summary>
/// Pure wind/turbulence/icing/surface arithmetic. Everything here is a function of
/// the live <see cref="State.AircraftState"/> weather fields so it can be unit-tested
/// without a simulator.
/// </summary>
public static class WeatherModel
{
    public const double StandardGravityFtPerSec2 = 32.174;

    public const double MaxApproachWindIncrementKt = 12;
    public const double MaxApproachIceIncrementKt = 8;
    public const double MaxApproachTurbulenceIncrementKt = 8;
    public const double MaxApproachWeatherIncrementKt = 25;
    public const double SignificantCrosswindKt = 12;
    public const double IcePerceptionThreshold = 1.0;
    public const double ModerateIcePercent = 15.0;
    public const double SevereIcePercent = 40.0;
    public const double IcingUpperTempC = 10.0;
    public const double IcingLowerTempC = -40.0;

    /// <summary>
    /// Headwind and crosswind components for a given course.
    /// <paramref name="windFromDeg"/> is the direction the wind blows FROM (meteorological),
    /// <paramref name="courseDeg"/> the direction of travel. Headwind positive, crosswind
    /// positive from the right.
    /// </summary>
    public static (double headwindKt, double crosswindKt) WindComponents(double windFromDeg, double windKt, double courseDeg)
    {
        // rel = angle from course to the direction the wind blows FROM.
        // Headwind is maximised when the wind comes straight down the nose
        // (rel = 0); crosswind is positive when it pushes from the right.
        var rel = Geo.GeoMath.AngleDelta(windFromDeg, courseDeg);
        var rad = Geo.GeoMath.ToRad(rel);
        var head = windKt * Math.Cos(rad);
        var cross = -windKt * Math.Sin(rad);
        return (head, cross);
    }

    public static double CrosswindMagnitudeKt(double windFromDeg, double windKt, double courseDeg)
    {
        var (_, cross) = WindComponents(windFromDeg, windKt, courseDeg);
        return Math.Abs(cross);
    }

    /// <summary>Wind-correction angle in degrees to fly a ground track into a crosswind.</summary>
    public static double WindCorrectionAngleDeg(double crosswindKt, double trueAirspeedKt)
    {
        if (trueAirspeedKt < 1) return 0;
        var angle = Math.Asin(Math.Clamp(crosswindKt / trueAirspeedKt, -1, 1));
        return Geo.GeoMath.ToDeg(angle);
    }

    public static double ApproachWindIncrementKt(double headwindKt)
        => Math.Min(MaxApproachWindIncrementKt, Math.Max(0, -headwindKt)); // only a tailwind hurts

    public static double ApproachIceIncrementKt(double structuralIcePercent)
        => Math.Min(MaxApproachIceIncrementKt, structuralIcePercent * 0.15);

    public static double ApproachTurbulenceIncrementKt(TurbulenceLevel level) => level switch
    {
        TurbulenceLevel.Calm => 0,
        TurbulenceLevel.Light => 2,
        TurbulenceLevel.Moderate => 5,
        TurbulenceLevel.Severe => MaxApproachTurbulenceIncrementKt,
        _ => 0,
    };

    public static double ApproachWeatherIncrementKt(double headwindKt, double structuralIcePercent, TurbulenceLevel level)
    {
        var w = Math.Max(0, -headwindKt) * 0.4; // tailwind component
        var i = ApproachIceIncrementKt(structuralIcePercent);
        var t = ApproachTurbulenceIncrementKt(level);
        return Math.Min(MaxApproachWeatherIncrementKt, w + i + t);
    }

    /// <summary>Visible moisture in the icing temperature band, or ice already on the airframe.</summary>
    /// <remarks>
    /// <c>AMBIENT PRECIP STATE</c> is a bit mask: 2 = None, 4 = Rain, 8 = Snow.
    /// Only Rain (4) or Snow (8) count as precipitation; None (2) does not.
    /// </remarks>
    public static bool IcingConditions(double oatC, bool inCloud, double precipState, double structuralIcePercent)
    {
        if (structuralIcePercent > IcePerceptionThreshold) return true;
        var precipMasked = ((int)Math.Round(precipState)) & 12; // 4 = Rain, 8 = Snow; 2 = None is ignored
        var moist = inCloud || precipMasked != 0;
        return moist && oatC <= IcingUpperTempC && oatC >= IcingLowerTempC;
    }

    /// <summary>Takeoff run factor from headwind and surface contamination (1.0 = book value).</summary>
    public static double TakeoffRunFactor(double headwindKt, double rotationKt, double surfaceCondition, bool surfaceInfoValid)
    {
        var wind = WindRunFactor(headwindKt, Math.Max(rotationKt, 60));
        var surface = SurfaceRollFactor(surfaceCondition);
        if (!surfaceInfoValid) surface = 1.0;
        return wind * surface;
    }

    public static double WindRunFactor(double headwindKt, double referenceKt)
    {
        // headwind shortens, tailwind lengthens: roll distance scales with the
        // square of the groundspeed at rotation, (V - headwind) / V.
        var effective = referenceKt - headwindKt;
        if (effective < referenceKt * 0.5) effective = referenceKt * 0.5;
        var ratio = effective / referenceKt;
        return Math.Clamp(ratio * ratio, 0.6, 1.8);
    }

    public static double SurfaceRollFactor(double surfaceCondition) => (int)Math.Round(surfaceCondition) switch
    {
        0 => 1.0,
        1 => 1.15,  // wet
        2 => 1.5,   // icy
        3 => 1.4,   // snow
        _ => 1.0,
    };

    public static bool SurfaceIsContaminated(double surfaceCondition, bool surfaceInfoValid)
        => surfaceInfoValid && surfaceCondition > 0.5;

    public static string SurfaceText(double surfaceCondition, bool surfaceInfoValid)
    {
        if (!surfaceInfoValid) return "unknown";
        return (int)Math.Round(surfaceCondition) switch
        {
            0 => "dry",
            1 => "wet",
            2 => "icy",
            3 => "snow",
            _ => "unknown",
        };
    }

    public static TurbulenceLevel Classify(double severity) => severity switch
    {
        < 0.15 => TurbulenceLevel.Calm,
        < 0.45 => TurbulenceLevel.Light,
        < 1.0 => TurbulenceLevel.Moderate,
        _ => TurbulenceLevel.Severe,
    };

    public static double SeverityFromRmsG(double rmsG) => Math.Max(0, rmsG - 1.0) * 4.0;

    /// <summary>Bank-angle ceiling the aircraft should hold in this turbulence.</summary>
    public static double ControlBankDeg(TurbulenceLevel level) => level switch
    {
        TurbulenceLevel.Calm => 30,
        TurbulenceLevel.Light => 20,
        TurbulenceLevel.Moderate => 12,
        TurbulenceLevel.Severe => 6,
        _ => 20,
    };

    /// <summary>How much to slow below the commanded speed in turbulence.</summary>
    public static double EnrouteSpeedReductionKt(TurbulenceLevel level, double currentKt)
    {
        var reduction = level switch
        {
            TurbulenceLevel.Calm => 0,
            TurbulenceLevel.Light => 10,
            TurbulenceLevel.Moderate => 25,
            TurbulenceLevel.Severe => 45,
            _ => 0,
        };
        return Math.Min(reduction, Math.Max(0, currentKt - 120)); // never below ~120kt floor
    }
}

/// <summary>
/// MSFS publishes no turbulence variable, so turbulence is derived from the load
/// factor: steady 1g in level flight, 1/cos(bank) turning; the high-frequency
/// deviation from a slow baseline is the gustiness, mapped to a severity.
/// </summary>
public sealed class TurbulenceTracker
{
    private double _baseline = 1.0;
    private double _severity;
    private double _rms;
    private double _sampleCount;

    public double Severity => _severity;
    public TurbulenceLevel Level => WeatherModel.Classify(_severity);
    public double BaselineG => _baseline;

    public void Reset()
    {
        _baseline = 1.0; _severity = 0; _rms = 0; _sampleCount = 0;
    }

    /// <summary>
    /// Feed one sample. <paramref name="dt"/> seconds since last sample;
    /// <paramref name="quiet"/> suppresses updates (e.g. on the ground).
    /// </summary>
    public void Update(double loadFactorG, double dt, bool quiet = false)
    {
        if (quiet || dt <= 0) return;

        // Slow baseline follows the sustained load (turns don't read as turbulence).
        var alpha = Math.Clamp(dt / 20.0, 0, 0.2);
        _baseline += (loadFactorG - _baseline) * alpha;

        var dev = loadFactorG - _baseline;
        _rms += (dev * dev - _rms) * Math.Clamp(dt / 10.0, 0, 0.5);
        _sampleCount += dt;

        // Only trust the estimate after it has warmed up.
        if (_sampleCount > 15)
            _severity = WeatherModel.SeverityFromRmsG(Math.Sqrt(_rms));
    }
}
