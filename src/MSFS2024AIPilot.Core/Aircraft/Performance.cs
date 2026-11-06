using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>The performance model of the loaded aircraft, parsed from its cfg files.</summary>
public sealed class AircraftPerformance
{
    public string Title { get; set; } = "";
    public string IcaoType { get; set; } = "";
    public string Category { get; set; } = "";
    public string ApproachCategory { get; set; } = "";

    public double MaxGrossWeight { get; set; }     // lb
    public double MaxTakeoffWeight { get; set; }
    public double MaxLandingWeight { get; set; }
    public double MaxZeroFuelWeight { get; set; }
    public double EmptyWeight { get; set; }
    public double MaxFuelWeight { get; set; }
    public double FuelCapacityGallons { get; set; }

    public double WingSpan { get; set; }           // ft
    public double WingArea { get; set; }           // sq ft
    public double MAC { get; set; }                // ft
    public double CGForwardLimit { get; set; }     // % MAC
    public double CGAftLimit { get; set; }         // % MAC

    public int EngineCount { get; set; }
    public string EngineType { get; set; } = "";   // JET, TURBOPROP, PISTON
    public double MaxThrustPerEngine { get; set; } // lb

    // V-speeds (KCAS/KIAS)
    public double Vso { get; set; }
    public double Vs1 { get; set; }
    public double Vfe { get; set; }
    public double Vno { get; set; }
    public double Vne { get; set; }
    public double Vle { get; set; }
    public double Vlo { get; set; }
    public double Vmc { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Vref { get; set; }
    public double Vapp { get; set; }

    /// <summary>flap index → (Vr, V2, V1) knots from the takeoff performance tables.</summary>
    public Dictionary<int, (double v1, double vr, double v2)> TakeoffSpeeds { get; } = new();
    /// <summary>flap index → landing speeds.</summary>
    public Dictionary<int, (double vref, double vapp)> LandingSpeeds { get; } = new();

    /// <summary>Published takeoff ground-roll distances (distance ft → ft roll), if any.</summary>
    public List<(double weight, double distanceFt)> TakeoffRollTable { get; } = new();
    public List<(double weight, double distanceFt)> LandingDistanceTable { get; } = new();

    public double ClimbRateFpm { get; set; }
    public double CruiseSpeedKt { get; set; }
    public double DescentRateFpm { get; set; }

    public bool HasPerformanceData => Vs1 > 0 || Vy > 0 || MaxGrossWeight > 0;

    // ---- Derived speeds with sensible fallbacks ----

    public double RotationSpeed
    {
        get
        {
            var vr = TakeoffSpeeds.Values.Select(t => t.vr).Where(v => v > 0).ToList();
            if (vr.Count > 0) return vr.Average();
            return FallbackRotation();
        }
    }

    private double FallbackRotation() =>
        Vs1 > 0 ? Vs1 * 1.15 : (Vx > 0 ? Vx : 0);

    public double ApproachSpeed =>
        Vapp > 0 ? Vapp : (Vref > 0 ? Vref + 5 : (Vs0OrVs1() * 1.3));

    private double Vs0OrVs1() => Vso > 0 ? Vso : (Vs1 > 0 ? Vs1 : 50);

    public double ClimbSpeed => Vy > 0 ? Vy : (Vs1 > 0 ? Vs1 * 1.2 : 80);
    public double CruiseSpeed => CruiseSpeedKt > 0 ? CruiseSpeedKt : (Vno > 0 ? Math.Min(Vno, 250) : 120);

    /// <summary>Required runway length in feet for takeoff, with a 15% margin.</summary>
    public double RequiredTakeoffRunwayFt(double weightLbs, double windFactor = 1.0)
    {
        double roll;
        if (TakeoffRollTable.Count > 0)
            roll = Interpolate(TakeoffRollTable, weightLbs);
        else if (MaxThrustPerEngine > 0 && Vs1 > 0)
        {
            // Physics estimate: a = T/W * g - drag-ish; ground roll ~ v²/(2a) with a floor.
            var accel = Math.Max(0.05, (MaxThrustPerEngine * EngineCount) / Math.Max(weightLbs, 1) * 32.2 * 0.6);
            var vr = Math.Max(RotationSpeed, Vs1 * 1.1) * Geo.GeoMath.KnotsToMps;
            roll = vr * vr / (2 * accel) * Geo.GeoMath.FeetPerMeter;
        }
        else
        {
            var vr = Math.Max(RotationSpeed, Vs1 * 1.1);
            roll = vr * vr / (2 * 4.0); // ~4 ft/s² conservative accel
        }
        return roll * windFactor * 1.15;
    }

    public double RequiredLandingRunwayFt(double weightLbs, double windFactor = 1.0)
    {
        double dist;
        if (LandingDistanceTable.Count > 0)
            dist = Interpolate(LandingDistanceTable, weightLbs);
        else
        {
            var v = (ApproachSpeed * Geo.GeoMath.KnotsToMps);
            // ~3 m/s² deceleration with brakes/reverse.
            dist = v * v / (2 * 3.0) * Geo.GeoMath.FeetPerMeter;
        }
        return dist * windFactor * 1.15;
    }

    private static double Interpolate(List<(double weight, double distanceFt)> table, double weight)
    {
        var sorted = table.OrderBy(t => t.weight).ToList();
        if (sorted.Count == 0) return 0;
        if (weight <= sorted[0].weight) return sorted[0].distanceFt;
        if (weight >= sorted[^1].weight) return sorted[^1].distanceFt;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (weight <= sorted[i].weight)
            {
                var t = (weight - sorted[i - 1].weight) / (sorted[i].weight - sorted[i - 1].weight);
                return sorted[i - 1].distanceFt + t * (sorted[i].distanceFt - sorted[i - 1].distanceFt);
            }
        }
        return sorted[^1].distanceFt;
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
            if (line.Length == 0 || line.StartsWith(';')) continue;
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
        // Values may carry units: "120.0 lbs" etc.
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
            perf.Title = Str(g, "title");
            perf.IcaoType = Str(g, "icao_type_designator");
            perf.Category = Str(g, "wake_turbulence_category");
            perf.ApproachCategory = Str(g, "approach_category");
            perf.EngineCount = I(g, "number_of_engines", perf.EngineCount);
            perf.EngineType = Str(g, "engine_type").ToUpperInvariant();
        }
        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab)) ApplyWeights(wab, perf);
        if (sections.TryGetValue("FUEL", out var fuel))
        {
            perf.FuelCapacityGallons = D(fuel, "fuel_capacity");
            perf.MaxFuelWeight = perf.FuelCapacityGallons * D(fuel, "fuel_weight_per_gallon", 6.0);
        }
        if (sections.TryGetValue("FLIGHT_TUNING", out var t)) ApplyVSpeeds(t, perf);
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
            perf.Vlo = perf.Vle;
        }
        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab)) ApplyWeights(wab, perf);
    }

    private static void ApplyWeights(Dictionary<string, string> wab, AircraftPerformance perf)
    {
        if (D(wab, "max_gross_weight") > 0) perf.MaxGrossWeight = D(wab, "max_gross_weight");
        perf.MaxTakeoffWeight = D(wab, "max_takeoff_weight", perf.MaxGrossWeight);
        perf.MaxLandingWeight = D(wab, "max_landing_weight", perf.MaxTakeoffWeight);
        perf.MaxZeroFuelWeight = D(wab, "max_zero_fuel_weight", perf.MaxGrossWeight);
        perf.EmptyWeight = D(wab, "empty_weight", perf.EmptyWeight);
        perf.CGForwardLimit = D(wab, "cg_forward_limit") * 100;
        perf.CGAftLimit = D(wab, "cg_aft_limit") * 100;
    }

    private static void ApplyVSpeeds(Dictionary<string, string> t, AircraftPerformance perf)
    {
        // Only overwrite with nonzero values: the two cfg files can disagree and
        // zero must not erase a real speed.
        void Set(string key, Func<double> get, Action<double> set)
        {
            var v = D(t, key);
            if (v > 0) set(v); else _ = get();
        }
        Set("flaps_up_stall_speed", () => perf.Vs1, v => perf.Vs1 = v);
        Set("full_flaps_stall_speed", () => perf.Vso, v => perf.Vso = v);
        Set("max_speed_full_flaps", () => perf.Vfe, v => perf.Vfe = v);
        Set("max_speed_cruise", () => perf.Vno, v => perf.Vno = v);
        Set("max_speed", () => perf.Vne, v => perf.Vne = v);
        Set("min_control_speed", () => perf.Vmc, v => perf.Vmc = v);
        Set("best_angle_climb_speed", () => perf.Vx, v => perf.Vx = v);
        Set("best_rate_climb_speed", () => perf.Vy, v => perf.Vy = v);
        if (perf.Vy == 0) perf.Vy = D(t, "best_rate_of_climb_speed");
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

        if (sections.TryGetValue("TAKEOFF_PERFORMANCE", out var to))
        {
            if (to.TryGetValue("takeoff_ground_roll_distance_table", out var roll))
                perf.TakeoffRollTable.AddRange(ParseDistanceTable(roll));
        }
        if (sections.TryGetValue("LANDING_PERFORMANCE", out var ld))
        {
            if (ld.TryGetValue("landing_distance_table", out var dist))
                perf.LandingDistanceTable.AddRange(ParseDistanceTable(dist));
        }
        if (sections.TryGetValue("CLIMB_PERFORMANCE.0", out var climb))
        {
            perf.ClimbRateFpm = D(climb, "climb_rate", perf.ClimbRateFpm);
            var cs = D(climb, "climb_speed");
            if (cs > 0 && perf.CruiseSpeedKt == 0) perf.CruiseSpeedKt = cs;
        }
        if (sections.TryGetValue("CRUISE_PERFORMANCE.0", out var cruise))
            perf.CruiseSpeedKt = D(cruise, "cruise_speed", perf.CruiseSpeedKt);
        if (sections.TryGetValue("DESCENT_PERFORMANCE.0", out var descent))
            perf.DescentRateFpm = D(descent, "descent_rate", perf.DescentRateFpm);
    }

    private static List<(double weight, double distanceFt)> ParseDistanceTable(string data)
    {
        var result = new List<(double, double)>();
        foreach (Match m in Regex.Matches(data, @"(\d+(?:\.\d+)?)\s*:\s*(\d+(?:\.\d+)?)"))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) &&
                double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                result.Add((w, d));
        }
        return result;
    }
}
