using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core;

/// <summary>Engine parameters parsed from engines.cfg.</summary>
public sealed class EngineParameters
{
    /// <summary>Maximum rated thrust per engine (lbs), from max_rated_thrust.</summary>
    public double MaxThrustPerEngine { get; set; }
    /// <summary>Engine type from aircraft.cfg: jet, turboprop, piston, etc.</summary>
    public string EngineType { get; set; } = "";
    /// <summary>Number of engines.</summary>
    public int EngineCount { get; set; } = 1;
    /// <summary>Engine positions (station, kind, number), from CONTACT_POINTS in aircraft.cfg.</summary>
    public List<EnginePosition> EnginePositions { get; set; } = new();
    /// <summary>Propeller parameters, from engines.cfg [PROP*].</summary>
    public List<PropellerParams> Propellers { get; set; } = new();
}

/// <summary>One engine position from the CONTACT_POINTS section.</summary>
public sealed class EnginePosition
{
    public string? Station { get; set; }
    public string? Kind { get; set; }
    public double StationPositionAftOfCGFeet { get; set; }
    public double StationPositionAboveGearFeet { get; set; }
    public double StationPositionLaterallyFeet { get; set; }
}

/// <summary>Propeller parameters from engines.cfg.</summary>
public sealed class PropellerParams
{
    public double DiameterFeet { get; set; }
    public double PitchDegrees { get; set; }
    public double PFactorPct { get; set; }
    public double MomentumLossFactor { get; set; }
    public string? FixedPitch { get; set; }
    public string? FeatheringAvailable { get; set; }
}

public sealed class AircraftPerformance
{
    /// <summary>
    /// What the configuration reader could and could not read. A published table the
    /// parser does not recognise is a silent fallback to an estimate otherwise, and the
    /// difference is only visible in the console - so it is recorded here.
    /// </summary>
    public List<string> Notes { get; } = new();

    public string Title { get; set; } = "";
    public string ICAOType { get; set; } = "";
    public string Category { get; set; } = ""; // L, M, H, J
    public string ApproachCategory { get; set; } = ""; // A, B, C, D, E, H

    public double MaxGrossWeight { get; set; } // lbs
    public double MaxTakeoffWeight { get; set; }
    public double MaxLandingWeight { get; set; }
    public double MaxZeroFuelWeight { get; set; }
    public double EmptyWeight { get; set; }
    public double MaxFuelWeight { get; set; }
    public double FuelCapacityGallons { get; set; }

    public double WingSpan { get; set; } // feet
    public double WingArea { get; set; } // sq ft
    public double MAC { get; set; } // Mean Aerodynamic Chord feet
    public double CGForwardLimit { get; set; } // % MAC
    public double CGAftLimit { get; set; } // % MAC

    public int EngineCount { get; set; }
    public string EngineType { get; set; } = ""; // JET, TURBOPROP, PISTON
    public double MaxThrustPerEngine { get; set; } // lbs

    public double Vso { get; set; } // Stall speed landing config (KCAS)
    public double Vs1 { get; set; } // Stall speed clean (KCAS)
    public double Vfe { get; set; } // Max flaps extended (KIAS)
    public double Vno { get; set; } // Max structural cruising (KIAS)
    public double Vne { get; set; } // Never exceed (KIAS)
    public double Vle { get; set; } // Max gear extended (KIAS)
    public double Vlo { get; set; } // Max gear operating (KIAS)
    public double Vmc { get; set; } // Min control speed multi-engine (KIAS)
    public double Vx { get; set; } // Best angle of climb (KIAS)
    public double Vy { get; set; } // Best rate of climb (KIAS)
    public double Vref { get; set; } // Reference landing speed (KIAS)
    public double Vapp { get; set; } // Approach Speed (KIAS) = Vref + 5-10 knots
    public double Vmca { get; set; } // Min control airspeed (KIAS)

    /// <summary>Engine parameters parsed from engines.cfg.</summary>
    public EngineParameters EngineParams { get; set; } = new();

    public Dictionary<int, VSpeedTable> TakeoffVSpeeds { get; set; } = new(); // Key = flaps index
    public Dictionary<int, VSpeedTable> LandingVSpeeds { get; set; } = new();

    public ClimbPerformance ClimbPerf { get; set; } = new();
    public CruisePerformance CruisePerf { get; set; } = new();
    public DescentPerformance DescentPerf { get; set; } = new();

    public TakeoffPerformance TakeoffPerf { get; set; } = new();
    public LandingPerformance LandingPerf { get; set; } = new();

    public bool HasFlightPerformanceConfig { get; set; }
    public bool HasAdvancedPerformance { get; set; }
}

public sealed class VSpeedTable
{
    public int FlapsIndex { get; set; }
    public double FlapsAngle { get; set; }
    public List<VSpeedEntry> Entries { get; set; } = new();
}

public sealed class VSpeedEntry
{
    public double Weight { get; set; } // lbs
    public double V1 { get; set; }
    public double Vr { get; set; }
    public double V2 { get; set; }
}

public sealed class ClimbPerformance
{
    public List<ClimbProfile> Profiles { get; set; } = new();
    public double DefaultClimbSpeed { get; set; } // KIAS
    public double DefaultClimbMach { get; set; }
    public double TransitionAltitude { get; set; } = 10000;
}

public sealed class ClimbProfile
{
    public string Name { get; set; } = "";
    public double IASBelowTransition { get; set; }
    public double IASAboveTransition { get; set; }
    public double MachAboveTransition { get; set; }
    public double MaxAltitude { get; set; }
}

public sealed class CruisePerformance
{
    public List<CruiseProfile> Profiles { get; set; } = new();
    public double DefaultCruiseSpeed { get; set; }
    public double DefaultCruiseMach { get; set; }
    public double MaxCruiseAltitude { get; set; }
    public double OptCruiseAltitude { get; set; }
    public double CostIndex { get; set; }
}

public sealed class CruiseProfile
{
    public string Name { get; set; } = "";
    public double IAS { get; set; }
    public double Mach { get; set; }
    public double Altitude { get; set; }
    public double FuelFlowPPH { get; set; }
    public double TAS { get; set; }
    public double SpecificRange { get; set; } // NM/lb
}

public sealed class DescentPerformance
{
    public List<DescentProfile> Profiles { get; set; } = new();
    public double DefaultDescentSpeed { get; set; }
    public double DefaultDescentMach { get; set; }
    public double IdleDescentGradient { get; set; } // NM per 1000ft
    public double CleanDescentGradient { get; set; }
}

public sealed class DescentProfile
{
    public string Name { get; set; } = "";
    public double IAS { get; set; }
    public double Mach { get; set; }
    public double RateOfDescent { get; set; } // FPM
    public double Gradient { get; set; } // NM per 1000ft
}

public sealed class TakeoffPerformance
{
    public List<TakeoffDataPoint> GroundRollTable { get; set; } = new();
    public List<TakeoffDataPoint> TotalDistanceTable { get; set; } = new();
    public List<ClimbGradientPoint> ClimbGradientTable { get; set; } = new();
}

public sealed class TakeoffDataPoint
{
    public double Weight { get; set; }
    public double PressureAltitude { get; set; }
    public double Temperature { get; set; } // ISA deviation or OAT
    public int FlapsIndex { get; set; }
    public double Distance { get; set; } // feet
    public double V1 { get; set; }
    public double Vr { get; set; }
    public double V2 { get; set; }
}

public sealed class ClimbGradientPoint
{
    public double Weight { get; set; }
    public double PressureAltitude { get; set; }
    public double Temperature { get; set; }
    public int FlapsIndex { get; set; }
    public double Gradient { get; set; } // percent
}

public sealed class LandingPerformance
{
    public List<LandingDataPoint> GroundRollTable { get; set; } = new();
    public List<LandingDataPoint> TotalDistanceTable { get; set; } = new();
}

public sealed class LandingDataPoint
{
    public double Weight { get; set; }
    public double PressureAltitude { get; set; }
    public double Temperature { get; set; }
    public int FlapsIndex { get; set; }
    public double Distance { get; set; } // feet
    public double Vref { get; set; }
}

public static class PerformanceCalculator
{
    public static double CalculateVSpeed(double weight, int flapsIndex, AircraftPerformance perf, string vSpeedName)
    {
        if (!perf.TakeoffVSpeeds.TryGetValue(flapsIndex, out var table))
            return 0;

        return InterpolateVSpeed(table.Entries, weight, vSpeedName);
    }

    public static double InterpolateVSpeed(List<VSpeedEntry> entries, double weight, string vSpeedName)
    {
        if (entries.Count == 0) return 0;
        if (entries.Count == 1) return GetVSpeed(entries[0], vSpeedName);

        var sorted = entries.OrderBy(e => e.Weight).ToList();
        
        if (weight <= sorted[0].Weight) return GetVSpeed(sorted[0], vSpeedName);
        if (weight >= sorted[^1].Weight) return GetVSpeed(sorted[^1], vSpeedName);

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (weight >= sorted[i].Weight && weight <= sorted[i + 1].Weight)
            {
                var w1 = sorted[i].Weight;
                var w2 = sorted[i + 1].Weight;
                var v1 = GetVSpeed(sorted[i], vSpeedName);
                var v2 = GetVSpeed(sorted[i + 1], vSpeedName);
                var ratio = (weight - w1) / (w2 - w1);
                return v1 + (v2 - v1) * ratio;
            }
        }
        return 0;
    }

    private static double GetVSpeed(VSpeedEntry entry, string name) => name.ToLower() switch
    {
        "v1" => entry.V1,
        "vr" => entry.Vr,
        "v2" => entry.V2,
        _ => 0
    };

    public static double CalculateTakeoffDistance(double weight, double pressureAltitude, double oat, int flapsIndex, AircraftPerformance perf, bool groundRollOnly = false)
    {
        var table = groundRollOnly ? perf.TakeoffPerf.GroundRollTable : perf.TakeoffPerf.TotalDistanceTable;
        return InterpolatePerformance(table, weight, pressureAltitude, oat, flapsIndex);
    }

    public static double CalculateLandingDistance(double weight, double pressureAltitude, double oat, int flapsIndex, AircraftPerformance perf, bool groundRollOnly = false)
    {
        var table = groundRollOnly ? perf.LandingPerf.GroundRollTable : perf.LandingPerf.TotalDistanceTable;
        return InterpolatePerformance(table, weight, pressureAltitude, oat, flapsIndex);
    }

    private static double InterpolatePerformance(List<TakeoffDataPoint> table, double weight, double pressureAltitude, double oat, int flapsIndex)
    {
        var filtered = table.Where(p => p.FlapsIndex == flapsIndex).ToList();
        if (filtered.Count == 0) return 0;

        return Interpolate3D(filtered, weight, pressureAltitude, oat, p => p.Distance);
    }

    private static double InterpolatePerformance(List<LandingDataPoint> table, double weight, double pressureAltitude, double oat, int flapsIndex)
    {
        var filtered = table.Where(p => p.FlapsIndex == flapsIndex).ToList();
        if (filtered.Count == 0) return 0;

        return Interpolate3D(filtered, weight, pressureAltitude, oat, p => p.Distance);
    }

    private static double Interpolate3D<T>(List<T> data, double weight, double alt, double temp, Func<T, double> selector) where T : class
    {
        if (data.Count == 0) return 0;
        if (data.Count == 1) return selector(data[0]);

        // Nearest neighbour, but with the three axes normalised before they are compared.
        // Summing raw pounds, feet and degrees made the units worth whatever their
        // magnitude was: 8,000 ft and 40 C scored 8,040 against 2,000 lb's 2,000, so a
        // sea-level ISA row was returned for a weight-heavy hot-and-high query. Each axis
        // is divided by its own span across the table, so a position a third of the way
        // along one axis weighs the same as a third along another.
        var weights = data.Select(p => (double)p.GetType().GetProperty("Weight")!.GetValue(p)!).ToList();
        var altitudes = data.Select(p => (double)p.GetType().GetProperty("PressureAltitude")!.GetValue(p)!).ToList();
        var temperatures = data.Select(p => (double)p.GetType().GetProperty("Temperature")!.GetValue(p)!).ToList();

        double weightSpan = SpanOf(weights);
        double altitudeSpan = SpanOf(altitudes);
        double temperatureSpan = SpanOf(temperatures);

        var closest = data
            .Select((p, i) => (
                point: p,
                score:
                    AxisDistance(weights[i], weight, weightSpan) +
                    AxisDistance(altitudes[i], alt, altitudeSpan) +
                    AxisDistance(temperatures[i], temp, temperatureSpan)))
            .OrderBy(x => x.score)
            .First()
            .point;

        return selector(closest);
    }

    /// <summary>
    /// Distance along one axis, as a fraction of that axis's own span. An axis with no
    /// spread carries no information, so it contributes nothing rather than dividing by
    /// zero.
    /// </summary>
    private static double AxisDistance(double from, double to, double span)
    {
        var delta = Math.Abs(to - from);
        if (!double.IsFinite(delta)) return 0;
        return span > 0 ? delta / span : 0;
    }

    /// <summary>The range covered by a set of axis values.</summary>
    private static double SpanOf(List<double> values)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return max >= min ? max - min : 0;
    }

    public static double CalculateRequiredRunwayLength(AircraftPerformance perf, AircraftState state, int flapsIndex, bool takeoff)
    {
        var weight = state.GrossWeight;
        var alt = state.PressureAltitude;
        var oat = state.OAT;

        if (takeoff)
        {
            var groundRoll = CalculateTakeoffDistance(weight, alt, oat, flapsIndex, perf, true);
            var totalDist = CalculateTakeoffDistance(weight, alt, oat, flapsIndex, perf, false);
            return Math.Max(groundRoll * 1.15, totalDist); // 15% safety margin
        }
        else
        {
            var groundRoll = CalculateLandingDistance(weight, alt, oat, flapsIndex, perf, true);
            var totalDist = CalculateLandingDistance(weight, alt, oat, flapsIndex, perf, false);
            return Math.Max(groundRoll * 1.15, totalDist);
        }
    }

    public static double CalculateVRef(AircraftPerformance perf, double weight, int flapsIndex)
    {
        if (perf.LandingVSpeeds.TryGetValue(flapsIndex, out var table))
        {
            return InterpolateVSpeed(table.Entries, weight, "vref");
        }
        // No table, or no landing weight published (a Store/Community aircraft with no
        // flight_performance.cfg): the weight correction cannot be formed, so do not divide
        // by zero and hand back NaN - which every downstream "< 50" guard treats as a real
        // number because comparisons with NaN are false. Fall back to the published Vref.
        if (perf.MaxLandingWeight <= 0) return perf.Vref;
        return perf.Vref * Math.Sqrt(weight / perf.MaxLandingWeight);
    }

    public static double CalculateVApp(AircraftPerformance perf, double weight, int flapsIndex, double windComponent)
    {
        var vref = CalculateVRef(perf, weight, flapsIndex);
        return vref + Math.Max(5, windComponent * 0.5); // Add wind correction
    }
}