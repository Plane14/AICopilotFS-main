using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>
/// The performance model of the loaded aircraft, populated from its
/// aircraft.cfg / flight_model.cfg / engines.cfg / flight_performance.cfg.
/// Ported from the previous pilot's richer model: published V-speed tables,
/// climb/cruise/descent profiles and takeoff/landing distance tables, with
/// the convenience properties the flight engine reads.
/// </summary>
public sealed class AircraftPerformance
{
    /// <summary>
    /// What the configuration reader could and could not read. A published table the
    /// parser does not recognise is a silent fallback to an estimate otherwise, and the
    /// difference is only visible in the console - so it is recorded here.
    /// </summary>
    public List<string> Notes { get; } = new();

    public string Title { get; set; } = "";
    public string IcaoType { get; set; } = "";
    public string Category { get; set; } = "";
    public string ApproachCategory { get; set; } = "";

    public double MaxGrossWeight { get; set; }     // lbs
    public double MaxTakeoffWeight { get; set; }
    public double MaxLandingWeight { get; set; }
    public double MaxZeroFuelWeight { get; set; }
    public double EmptyWeight { get; set; }
    public double MaxFuelWeight { get; set; }
    public double FuelCapacityGallons { get; set; }

    public double WingSpan { get; set; }           // feet
    public double WingArea { get; set; }           // sq ft
    public double MAC { get; set; }                // Mean Aerodynamic Chord feet
    public double CGForwardLimit { get; set; }     // % MAC
    public double CGAftLimit { get; set; }         // % MAC

    public int EngineCount { get; set; }
    public string EngineType { get; set; } = "";   // JET, TURBOPROP, PISTON, ROTOR
    public double MaxThrustPerEngine { get; set; } // lbs

    // V-speeds (KCAS/KIAS)
    public double Vso { get; set; }  // Stall speed landing config
    public double Vs1 { get; set; }  // Stall speed clean
    public double Vfe { get; set; }  // Max flaps extended
    public double Vno { get; set; }  // Max structural cruising
    public double Vne { get; set; }  // Never exceed
    public double Vle { get; set; }  // Max gear extended
    public double Vlo { get; set; }  // Max gear operating
    public double Vmc { get; set; }  // Min control speed multi-engine
    public double Vx { get; set; }   // Best angle of climb
    public double Vy { get; set; }   // Best rate of climb
    public double Vref { get; set; } // Reference landing speed
    public double Vapp { get; set; } // Approach speed
    public double Vmca { get; set; } // Min control air

    /// <summary>flap index → V-speed entries published by flight_performance.cfg.</summary>
    public Dictionary<int, VSpeedTable> TakeoffVSpeeds { get; set; } = new();
    public Dictionary<int, VSpeedTable> LandingVSpeeds { get; set; } = new();

    public ClimbPerformance ClimbPerf { get; set; } = new();
    public CruisePerformance CruisePerf { get; set; } = new();
    public DescentPerformance DescentPerf { get; set; } = new();

    public TakeoffPerformance TakeoffPerf { get; set; } = new();
    public LandingPerformance LandingPerf { get; set; } = new();

    public bool HasPerformanceData => Vs1 > 0 || Vy > 0 || MaxGrossWeight > 0;
    public bool HasFlightPerformanceConfig { get; set; }
    public bool HasAdvancedPerformance { get; set; }

    // ---- Derived speeds with sensible fallbacks ----

    /// <summary>Rotation speed: mean published Vr across flap settings, else a stall-based estimate.</summary>
    public double RotationSpeed
    {
        get
        {
            var vr = TakeoffVSpeeds.Values
                .SelectMany(t => t.Entries)
                .Select(e => e.Vr)
                .Where(v => v > 0)
                .ToList();
            if (vr.Count > 0) return vr.Average();
            return Vs1 > 0 ? Vs1 * 1.15 : (Vx > 0 ? Vx : 0);
        }
    }

    public double ApproachSpeed =>
        Vapp > 0 ? Vapp : (Vref > 0 ? Vref + 5 : (Vs0OrVs1() * 1.3));

    private double Vs0OrVs1() => Vso > 0 ? Vso : (Vs1 > 0 ? Vs1 : 50);

    public double ClimbSpeed => Vy > 0 ? Vy : (Vs1 > 0 ? Vs1 * 1.2 : 80);

    /// <summary>Published climb IAS when no best-rate speed exists (jet profiles).</summary>
    public double ClimbProfileSpeed =>
        ClimbPerf.Profiles.Select(p => p.IASBelowTransition).Where(v => v > 0).DefaultIfEmpty(0).Max();

    public double CruiseSpeed
    {
        get
        {
            if (CruiseSpeedKt > 0) return CruiseSpeedKt;
            var tas = CruisePerf.Profiles.Select(p => p.IAS).Where(v => v > 0).DefaultIfEmpty(0).Max();
            if (tas > 0) return tas;
            return Vno > 0 ? Math.Min(Vno, 250) : 120;
        }
    }

    /// <summary>Descent rate from the published profiles, fpm.</summary>
    public double DescentRateFpm =>
        DescentPerf.Profiles.Select(p => p.RateOfDescent).Where(v => v > 0).DefaultIfEmpty(0).Max();

    /// <summary>Climb schedule IAS used as cruise-speed fallback; 0 when unpublished.</summary>
    public double CruiseSpeedKt
    {
        get => _cruiseSpeedKt;
        set => _cruiseSpeedKt = value;
    }
    private double _cruiseSpeedKt;

    public double ClimbRateFpm => 0; // climb rate is table-driven; callers use their own fallback

    /// <summary>Flap index the aircraft is most likely configured for, from live state.</summary>
    public static int FlapIndexFor(double flapsHandleIndex) =>
        Math.Clamp((int)Math.Round(flapsHandleIndex), 0, 8);

    /// <summary>Required takeoff runway length in feet for the given weight/conditions.</summary>
    public double RequiredTakeoffRunwayFt(double weightLbs, double pressureAltitude = 0, double oat = 15, int flapsIndex = 0)
    {
        var d = PerformanceCalculator.CalculateTakeoffDistance(weightLbs, pressureAltitude, oat, flapsIndex, this, true);
        if (d <= 0) return PhysicsTakeoffRollFt(weightLbs) * 1.15;
        return Math.Max(d * 1.15, PerformanceCalculator.CalculateTakeoffDistance(weightLbs, pressureAltitude, oat, flapsIndex, this, false));
    }

    /// <summary>Required landing runway length in feet for the given weight/conditions.</summary>
    public double RequiredLandingRunwayFt(double weightLbs, double pressureAltitude = 0, double oat = 15, int flapsIndex = 0)
    {
        var d = PerformanceCalculator.CalculateLandingDistance(weightLbs, pressureAltitude, oat, flapsIndex, this, true);
        if (d <= 0) return PhysicsLandingDistanceFt(weightLbs) * 1.15;
        return Math.Max(d * 1.15, PerformanceCalculator.CalculateLandingDistance(weightLbs, pressureAltitude, oat, flapsIndex, this, false));
    }

    private double PhysicsTakeoffRollFt(double weightLbs)
    {
        double accel;
        if (MaxThrustPerEngine > 0 && Vs1 > 0)
        {
            accel = Math.Max(0.05, (MaxThrustPerEngine * Math.Max(EngineCount, 1)) / Math.Max(weightLbs, 1) * 32.2 * 0.6);
            var vr = Math.Max(RotationSpeed, Vs1 * 1.1) * Geo.GeoMath.KnotsToMps;
            return vr * vr / (2 * accel) * Geo.GeoMath.FeetPerMeter;
        }
        var v = Math.Max(RotationSpeed, Vs1 * 1.1);
        return v * v / (2 * 4.0); // ~4 ft/s² conservative accel
    }

    private double PhysicsLandingDistanceFt(double weightLbs)
    {
        var v = ApproachSpeed * Geo.GeoMath.KnotsToMps;
        return v * v / (2 * 3.0) * Geo.GeoMath.FeetPerMeter; // ~3 m/s² deceleration
    }
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
    public double DefaultClimbSpeed { get; set; }
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

    private static double GetVSpeed(VSpeedEntry entry, string name) => name.ToLowerInvariant() switch
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
        if (filtered.Count == 0) filtered = table; // distance tables carry no flap axis: FlapsIndex is 0 for all
        return Interpolate3D(filtered, weight, pressureAltitude, oat, p => p.Distance);
    }

    private static double InterpolatePerformance(List<LandingDataPoint> table, double weight, double pressureAltitude, double oat, int flapsIndex)
    {
        var filtered = table.Where(p => p.FlapsIndex == flapsIndex).ToList();
        if (filtered.Count == 0) filtered = table;
        return Interpolate3D(filtered, weight, pressureAltitude, oat, p => p.Distance);
    }

    private static double Interpolate3D<T>(List<T> data, double weight, double alt, double temp, Func<T, double> selector)
    {
        if (data.Count == 0) return 0;
        if (data.Count == 1) return selector(data[0]);

        // Nearest neighbour with each axis normalised by its own span, so a
        // sea-level ISA row is not returned for a weight-heavy hot-and-high query.
        var weights = data.Select(p => AxisOf(p, "Weight")).ToList();
        var altitudes = data.Select(p => AxisOf(p, "PressureAltitude")).ToList();
        var temperatures = data.Select(p => AxisOf(p, "Temperature")).ToList();

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

    private static double AxisOf<T>(T p, string name)
    {
        var prop = p!.GetType().GetProperty(name);
        return prop?.GetValue(p) is double d ? d : 0;
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

    public static double CalculateVRef(AircraftPerformance perf, double weight, int flapsIndex)
    {
        if (perf.LandingVSpeeds.TryGetValue(flapsIndex, out var table))
        {
            return InterpolateVSpeed(table.Entries, weight, "vref");
        }
        if (perf.MaxLandingWeight <= 0) return perf.Vref;
        return perf.Vref * Math.Sqrt(weight / perf.MaxLandingWeight);
    }

    public static double CalculateVApp(AircraftPerformance perf, double weight, int flapsIndex, double windComponent)
    {
        var vref = CalculateVRef(perf, weight, flapsIndex);
        return vref + Math.Max(5, windComponent * 0.5); // Add wind correction
    }
}

/// <summary>Parses aircraft.cfg / flight_model.cfg / engines.cfg / flight_performance.cfg.</summary>
public static class AircraftConfigParser
{
    public static AircraftPerformance ParseAircraftConfig(string aircraftPath)
    {
        var perf = new AircraftPerformance();
        Try(Path.Combine(aircraftPath, "aircraft.cfg"), c => ParseAircraftCfg(c, perf));
        Try(Path.Combine(aircraftPath, "flight_model.cfg"), c => ParseFlightModelCfg(c, perf));
        Try(Path.Combine(aircraftPath, "engines.cfg"), c => ParseEnginesCfg(c, perf));
        Try(Path.Combine(aircraftPath, "flight_performance.cfg"), c => ParseFlightPerformanceCfg(c, perf));
        return perf;
    }

    private static void Try(string path, Action<string> parse)
    {
        try { if (File.Exists(path)) parse(File.ReadAllText(path)); }
        catch { /* a malformed cfg must not take the pilot down */ }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//")) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                if (!sections.ContainsKey(current)) sections[current] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            sections[current][line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    private static double D(Dictionary<string, string> s, string key, double fallback = 0)
    {
        if (!s.TryGetValue(key, out var v)) return fallback;
        var cleaned = v.Trim().Trim('"');
        var m = Regex.Match(cleaned, @"-?\d+(\.\d+)?([eE][-+]?\d+)?");
        return m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static int I(Dictionary<string, string> s, string key, int fallback = 0)
        => (int)D(s, key, fallback);

    private static string Str(Dictionary<string, string> s, string key, string fallback = "")
        => s.TryGetValue(key, out var v) ? v.Trim().Trim('"') : fallback;

    private static void ParseAircraftCfg(string content, AircraftPerformance perf)
    {
        var sections = ParseSections(content);
        if (sections.TryGetValue("GENERAL", out var g))
        {
            // aircraft.cfg publishes no "title" key: the display name is the
            // atc_type/atc_model pair, with icao_type_designator alongside.
            var atcType = Str(g, "atc_type");
            var atcModel = Str(g, "atc_model");
            perf.Title = AircraftPackageLocator.CleanTitle(Str(g, "title"));
            if (perf.Title.Length == 0)
                perf.Title = AircraftPackageLocator.CleanTitle(
                    string.Join(' ', new[] { atcType, atcModel }.Where(s => s.Length > 0)));
            perf.IcaoType = Str(g, "icao_type_designator");
            perf.Category = FirstNonEmpty(Str(g, "Category"), Str(g, "wake_turbulence_category"));
            perf.ApproachCategory = Str(g, "approach_category");

            // ICAO-designated fields are the ones MSFS actually writes; the
            // number_of_engines/engine_type spellings never existed and left
            // EngineCount at 0, which zeroed the physics runway estimate.
            perf.EngineCount = I(g, "icao_engine_count", I(g, "number_of_engines", perf.EngineCount));
            var objectClass = Str(g, "object_class");
            var propulsion = Str(g, "icao_propulsion_type");
            var engineType = Str(g, "icao_engine_type");
            if (objectClass.Equals("Helicopter", StringComparison.OrdinalIgnoreCase) ||
                objectClass.Equals("Gyroplane", StringComparison.OrdinalIgnoreCase) ||
                objectClass.Equals("TiltRotor", StringComparison.OrdinalIgnoreCase))
                perf.EngineType = "ROTOR";
            else
                perf.EngineType = FirstNonEmpty(propulsion, engineType,
                    Str(g, "engine_type")).ToUpperInvariant();
        }
        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab)) ApplyWeights(wab, perf);
        if (sections.TryGetValue("FUEL", out var fuel)) ApplyFuel(fuel, perf);
        if (sections.TryGetValue("FLIGHT_TUNING", out var t)) ApplyVSpeeds(t, perf);
    }

    private static void ApplyFuel(Dictionary<string, string> fuel, AircraftPerformance perf)
    {
        perf.FuelCapacityGallons = D(fuel, "fuel_capacity");
        var density = D(fuel, "fuel_weight_per_gallon", 0);
        if (density <= 0) density = 6.7; // jet fuel ~6.7 lb/gal; AVGAS ~6.0
        perf.MaxFuelWeight = perf.FuelCapacityGallons * density;
    }

    private static void ParseFlightModelCfg(string content, AircraftPerformance perf)
    {
        var sections = ParseSections(content);
        if (sections.TryGetValue("AIRPLANE_GEOMETRY", out var geom))
        {
            perf.WingSpan = D(geom, "wing_span");
            perf.WingArea = D(geom, "wing_area");
            perf.MAC = D(geom, "mac");
        }
        if (sections.TryGetValue("FLIGHT_TUNING", out var t))
        {
            ApplyVSpeeds(t, perf);
            if (perf.Vle == 0) perf.Vle = D(t, "max_gear_extended");
            // Vlo (max gear operating speed) is not published in flight_model.cfg;
            // the old code aliased it to Vle, which is wrong but harmless - Vlo is
            // never read. Leave it unset rather than propagate the alias.
        }
        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab)) ApplyWeights(wab, perf);
        if (sections.TryGetValue("FUEL", out var fuel) && perf.FuelCapacityGallons <= 0) ApplyFuel(fuel, perf);
    }

    private static void ApplyWeights(Dictionary<string, string> wab, AircraftPerformance perf)
    {
        if (D(wab, "max_gross_weight") > 0) perf.MaxGrossWeight = D(wab, "max_gross_weight");
        var mtow = D(wab, "max_takeoff_weight", perf.MaxTakeoffWeight > 0 ? perf.MaxTakeoffWeight : perf.MaxGrossWeight);
        perf.MaxTakeoffWeight = mtow > 0 ? mtow : perf.MaxGrossWeight;
        var mlw = D(wab, "max_landing_weight", perf.MaxLandingWeight > 0 ? perf.MaxLandingWeight : perf.MaxTakeoffWeight);
        perf.MaxLandingWeight = mlw > 0 ? mlw : perf.MaxTakeoffWeight;
        var mzfw = D(wab, "max_zero_fuel_weight", perf.MaxZeroFuelWeight > 0 ? perf.MaxZeroFuelWeight : perf.MaxGrossWeight);
        perf.MaxZeroFuelWeight = mzfw > 0 ? mzfw : perf.MaxGrossWeight;
        var ew = D(wab, "empty_weight", perf.EmptyWeight);
        if (ew > 0) perf.EmptyWeight = ew;
        perf.CGForwardLimit = D(wab, "cg_forward_limit", perf.CGForwardLimit);
        perf.CGAftLimit = D(wab, "cg_aft_limit", perf.CGAftLimit);
    }

    private static void ApplyVSpeeds(Dictionary<string, string> t, AircraftPerformance perf)
    {
        // Only overwrite with nonzero values: the two cfg files can disagree and
        // zero must not erase a real speed.
        void Set(string key, Action<double> set)
        {
            var v = D(t, key);
            if (v > 0) set(v);
        }
        Set("flaps_up_stall_speed", v => perf.Vs1 = v);
        Set("full_flaps_stall_speed", v => perf.Vso = v);
        Set("max_speed_full_flaps", v => perf.Vfe = v);
        Set("max_speed_cruise", v => perf.Vno = v);
        Set("max_speed", v => perf.Vne = v);
        Set("min_control_speed", v => perf.Vmc = v);
        Set("best_angle_climb_speed", v => perf.Vx = v);
        Set("best_rate_climb_speed", v => perf.Vy = v);
        if (perf.Vy == 0) perf.Vy = D(t, "best_rate_of_climb_speed");
        if (perf.Vle == 0) perf.Vle = D(t, "max_gear_extended");
    }

    private static void ParseEnginesCfg(string content, AircraftPerformance perf)
    {
        var sections = ParseSections(content);
        foreach (var (name, s) in sections)
        {
            if (name.StartsWith("GENERALENGINEDATA", StringComparison.OrdinalIgnoreCase))
            {
                var thrust = D(s, "max_rated_thrust");
                if (thrust > 0) perf.MaxThrustPerEngine = thrust;
            }
        }
    }

    private static void ParseFlightPerformanceCfg(string content, AircraftPerformance perf)
    {
        var sections = ParseSections(content);
        perf.HasFlightPerformanceConfig = true;

        if (sections.TryGetValue("TAKEOFF_PERFORMANCE", out var to))
        {
            perf.HasAdvancedPerformance = true;
            ParseVSpeedTables(to, perf, "V1_table_by_flaps_and_weight", "V1");
            ParseVSpeedTables(to, perf, "Vr_table_by_flaps_and_weight", "Vr");
            ParseVSpeedTables(to, perf, "V2_table_by_flaps_and_weight", "V2");
            ParseDistanceTable(to, "takeoff_ground_roll_distance_table_by_weight_and_OAT_and_altitude",
                pts => perf.TakeoffPerf.GroundRollTable = pts, perf, "takeoff ground roll");
            ParseDistanceTable(to, "takeoff_total_distance_table_by_weight_and_OAT_and_altitude",
                pts => perf.TakeoffPerf.TotalDistanceTable = pts, perf, "takeoff total");
        }

        if (sections.TryGetValue("LANDING_PERFORMANCE", out var ld))
        {
            perf.HasAdvancedPerformance = true;
            ParseDistanceTable(ld, "landing_ground_roll_distance_table_by_weight_and_OAT_and_altitude",
                pts => perf.LandingPerf.GroundRollTable = pts.Select(ToLanding).ToList(), perf, "landing ground roll");
            ParseDistanceTable(ld, "landing_total_distance_table_by_weight_and_OAT_and_altitude",
                pts => perf.LandingPerf.TotalDistanceTable = pts.Select(ToLanding).ToList(), perf, "landing total");
        }

        foreach (var (name, climb) in sections)
        {
            if (!name.StartsWith("CLIMB_PERFORMANCE", StringComparison.OrdinalIgnoreCase)) continue;
            perf.HasAdvancedPerformance = true;
            perf.ClimbPerf.Profiles.Add(new ClimbProfile
            {
                Name = Str(climb, "profile_name", name),
                IASBelowTransition = D(climb, "IAS_below_transition"),
                IASAboveTransition = D(climb, "IAS_above_transition"),
                MachAboveTransition = D(climb, "Mach"),
            });
        }

        foreach (var (name, cruise) in sections)
        {
            if (!name.StartsWith("CRUISE_PERFORMANCE", StringComparison.OrdinalIgnoreCase)) continue;
            perf.CruisePerf.Profiles.Add(new CruiseProfile
            {
                Name = Str(cruise, "profile_name", name),
                Mach = D(cruise, "Mach"),
            });
        }

        foreach (var (name, descent) in sections)
        {
            if (!name.StartsWith("DESCENT_PERFORMANCE", StringComparison.OrdinalIgnoreCase)) continue;
            perf.DescentPerf.Profiles.Add(new DescentProfile
            {
                Name = Str(descent, "profile_name", name),
                IAS = D(descent, "IAS_below_transition"),
                Mach = D(descent, "Mach"),
                RateOfDescent = D(descent, "average_descent_rate"),
            });
        }
    }

    private static LandingDataPoint ToLanding(TakeoffDataPoint p) => new()
    {
        Weight = p.Weight,
        PressureAltitude = p.PressureAltitude,
        Temperature = p.Temperature,
        FlapsIndex = p.FlapsIndex,
        Distance = p.Distance,
    };

    /// <summary>
    /// n-D table: <c>axis1v1,axis1v2,...:axis2v1,...:axisNv1,... :: v1,v2,...</c>
    /// Values are stored C-order — the last axis varies fastest. The distance
    /// tables name their axes weight/OAT/altitude in that order.
    /// </summary>
    private static void ParseDistanceTable(Dictionary<string, string> section, string key,
        Action<List<TakeoffDataPoint>> assign, AircraftPerformance perf, string what)
    {
        if (!section.TryGetValue(key, out var data) || string.IsNullOrWhiteSpace(data)) return;
        var points = DecodeDistanceTable(data);
        if (points.Count == 0)
        {
            perf.Notes.Add($"published {what} distances were not recognised and are ignored");
            return;
        }
        assign(points);
    }

    private static List<TakeoffDataPoint> DecodeDistanceTable(string data)
    {
        var sep = data.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return new List<TakeoffDataPoint>();

        var axes = data[..sep].Split(':')
            .Select(a => a.Split(',').Select(s => Parsing.Double(s.Trim())).ToArray())
            .ToArray();
        if (axes.Length < 2) return new List<TakeoffDataPoint>();

        var values = data[(sep + 2)..].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Parsing.Double(s.Trim()))
            .ToArray();

        // Expect weight : OAT : altitude (3 axes) or weight : OAT (2 axes).
        var weights = axes[0];
        var oats = axes.Length > 1 ? axes[1] : new[] { 15.0 };
        var alts = axes.Length > 2 ? axes[2] : new[] { 0.0 };

        var expected = weights.Length * oats.Length * alts.Length;
        if (values.Length != expected || expected == 0) return new List<TakeoffDataPoint>();

        var points = new List<TakeoffDataPoint>(expected);
        int idx = 0;
        foreach (var w in weights)
        foreach (var o in oats)
        foreach (var a in alts)
        {
            var d = values[idx++];
            if (d <= 0) continue;
            points.Add(new TakeoffDataPoint
            {
                Weight = w,
                Temperature = o,
                PressureAltitude = a,
                FlapsIndex = 0,
                Distance = d,
            });
        }

        // Published tables are normally feet; a median under ~800 ft almost
        // certainly means the file published metres, which would understate the
        // required runway by a third.
        if (points.Count > 0)
        {
            var median = points.Select(p => p.Distance).OrderBy(v => v).ElementAt(points.Count / 2);
            if (median > 0 && median < 800)
                foreach (var p in points) p.Distance *= Geo.GeoMath.FeetPerMeter;
        }
        return points;
    }

    private static void ParseVSpeedTables(Dictionary<string, string> section, AircraftPerformance perf, string tableName, string vSpeedName)
    {
        if (!section.TryGetValue(tableName, out var tableData)) return;

        // Format: flaps_idx1, flaps_idx2 : weight1, weight2 :: v1, v2 : v1, v2
        var parts = tableData.Split("::");
        if (parts.Length < 2) return;

        var flapsWeights = parts[0].Split(':');
        if (flapsWeights.Length < 2) return;

        var flapsIndices = flapsWeights[0].Split(',').Select(s => Parsing.Int32(s.Trim())).ToArray();
        var weights = flapsWeights[1].Split(',').Select(s => Parsing.Double(s.Trim())).ToArray();

        var values = parts[1].Split(':')
            .Select(s => s.Split(',').Select(x => Parsing.Double(x.Trim())).ToArray())
            .ToArray();

        for (int f = 0; f < flapsIndices.Length; f++)
        {
            var flapsIdx = flapsIndices[f];
            if (!perf.TakeoffVSpeeds.ContainsKey(flapsIdx))
            {
                perf.TakeoffVSpeeds[flapsIdx] = new VSpeedTable { FlapsIndex = flapsIdx };
            }

            var table = perf.TakeoffVSpeeds[flapsIdx];
            for (int w = 0; w < weights.Length; w++)
            {
                if (values.Length <= f || values[f].Length <= w) continue;

                // One row per (flaps, weight), filled in by whichever pass owns the field.
                var entry = table.Entries.FirstOrDefault(e => e.Weight == weights[w]);
                if (entry is null)
                {
                    entry = new VSpeedEntry { Weight = weights[w] };
                    table.Entries.Add(entry);
                }

                switch (vSpeedName)
                {
                    case "V1": entry.V1 = values[f][w]; break;
                    case "Vr": entry.Vr = values[f][w]; break;
                    case "V2": entry.V2 = values[f][w]; break;
                }
            }
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return "";
    }
}
