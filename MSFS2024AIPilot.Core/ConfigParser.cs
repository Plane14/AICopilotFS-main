using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MSFS2024AIPilot.Core;

public static class AircraftConfigParser
{
    public static AircraftPerformance ParseAircraftConfig(string aircraftPath)
    {
        var perf = new AircraftPerformance();
        
        var aircraftCfgPath = Path.Combine(aircraftPath, "aircraft.cfg");
        if (File.Exists(aircraftCfgPath))
        {
            ParseAircraftCfg(aircraftCfgPath, perf);
        }

        var flightModelCfgPath = Path.Combine(aircraftPath, "flight_model.cfg");
        if (File.Exists(flightModelCfgPath))
        {
            ParseFlightModelCfg(flightModelCfgPath, perf);
        }

        var enginesCfgPath = Path.Combine(aircraftPath, "engines.cfg");
        if (File.Exists(enginesCfgPath))
        {
            ParseEnginesCfg(enginesCfgPath, perf);
        }

        var flightPerfCfgPath = Path.Combine(aircraftPath, "flight_performance.cfg");
        if (File.Exists(flightPerfCfgPath))
        {
            perf.HasFlightPerformanceConfig = true;
            ParseFlightPerformanceCfg(flightPerfCfgPath, perf);
        }

        return perf;
    }

    private static void ParseAircraftCfg(string path, AircraftPerformance perf)
    {
        var content = File.ReadAllText(path);
        var sections = ParseCfgSections(content);

        if (sections.TryGetValue("GENERAL", out var general))
        {
            perf.Title = GetValue(general, "title", "");
            perf.ICAOType = GetValue(general, "icao_type_designator", "");
            perf.Category = GetValue(general, "wake_turbulence_category", "");
            perf.EngineCount = GetIntValue(general, "number_of_engines", 1);
            perf.EngineType = GetValue(general, "engine_type", "").ToUpper();
        }

        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab))
        {
            perf.MaxGrossWeight = GetDoubleValue(wab, "max_gross_weight", 0);
            perf.MaxTakeoffWeight = GetDoubleValue(wab, "max_takeoff_weight", perf.MaxGrossWeight);
            perf.MaxLandingWeight = GetDoubleValue(wab, "max_landing_weight", perf.MaxTakeoffWeight);
            perf.MaxZeroFuelWeight = GetDoubleValue(wab, "max_zero_fuel_weight", perf.MaxGrossWeight);
            perf.EmptyWeight = GetDoubleValue(wab, "empty_weight", 0);
            perf.CGForwardLimit = GetDoubleValue(wab, "cg_forward_limit", 0) * 100;
            perf.CGAftLimit = GetDoubleValue(wab, "cg_aft_limit", 0) * 100;
        }

        if (sections.TryGetValue("FUEL", out var fuel))
        {
            perf.FuelCapacityGallons = GetDoubleValue(fuel, "fuel_capacity", 0);
            perf.MaxFuelWeight = perf.FuelCapacityGallons * GetDoubleValue(fuel, "fuel_weight_per_gallon", 6.0);
        }

        if (sections.TryGetValue("FLIGHT_TUNING", out var tuning))
        {
            // flaps_up_stall_speed is the CLEAN stall speed (Vs1); full_flaps_stall_speed
            // is the LANDING configuration stall speed (Vs0). Assigning them the other
            // way round made every Vref and climb speed derive from the wrong stall speed.
            perf.Vs1 = GetDoubleValue(tuning, "flaps_up_stall_speed", perf.Vs1);
            perf.Vso = GetDoubleValue(tuning, "full_flaps_stall_speed", perf.Vso);
            perf.Vfe = GetDoubleValue(tuning, "max_speed_full_flaps", perf.Vfe);
            perf.Vno = GetDoubleValue(tuning, "max_speed_cruise", perf.Vno);
            perf.Vne = GetDoubleValue(tuning, "max_speed", perf.Vne);
            perf.Vmc = GetDoubleValue(tuning, "min_control_speed", perf.Vmc);
            perf.Vx = GetDoubleValue(tuning, "best_angle_climb_speed", perf.Vx);
        }

        ParseContactPoints(sections, perf);
        ParseRadioAndLightConfigs(sections, perf);
    }

    /// <summary>
    /// PARSED FROM aircraft.cfg [CONTACT_POINTS]. Each "pointNN" entry carries a station
    /// (engine, empty, etc) and positional fields. Engine stations inform EGT and RPM redline
    /// mapping per engine type.
    /// </summary>
    private static void ParseContactPoints(Dictionary<string, Dictionary<string, string>> sections, AircraftPerformance perf)
    {
        if (!sections.TryGetValue("CONTACT_POINTS", out var cp)) return;

        foreach (var (key, value) in cp)
        {
            // Keys look like "point.0", "point.1", etc.
            if (!key.StartsWith("point.", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Length <= 6) continue;

            // value format: kind,x,y,z,station,...
            var parts = value.Split(',');
            if (parts.Length < 7) continue;

            var station = GetValue(cp, $"{key}_station", parts[4].Trim());
            var kind = parts[0].Trim();

            var enginePos = new EnginePosition
            {
                Station = station,
                Kind = kind,
                StationPositionAftOfCGFeet = GetDoubleValue(cp, $"{key}_pos", 0),
                StationPositionAboveGearFeet = parts.Length > 6 ? Parsing.Double(parts[6]) : 0,
                StationPositionLaterallyFeet = parts.Length > 5 ? Parsing.Double(parts[5]) : 0
            };

            perf.EngineParams.EnginePositions.Add(enginePos);
            perf.EngineParams.EngineCount = perf.EngineParams.EnginePositions.Count(e =>
                e.Station is not null && (e.Station.Contains("Engine", StringComparison.OrdinalIgnoreCase)
                    || e.Station.Contains("Prop", StringComparison.OrdinalIgnoreCase)));
            if (perf.EngineParams.EngineCount == 1 && perf.EngineParams.EnginePositions.Count > 1)
                perf.EngineParams.EngineCount = perf.EngineCount;
        }
    }

    /// <summary>
    /// PARSED FROM aircraft.cfg [RADIOS] for tuned station info, and [LIGHTS] for light
    /// states. The radio section carries the COM/NAV/GPS frequencies that the autopilot
    /// needs for handoffs; the lights section carries the beacon/strobe/taxi/landing flags
    /// so checklists can verify them as set.
    /// </summary>
    private static void ParseRadioAndLightConfigs(Dictionary<string, Dictionary<string, string>> sections, AircraftPerformance perf)
    {
        if (sections.TryGetValue("LIGHTS", out var lights))
        {
            // Light states are not a flight parameter, but the checklist executor needs to
            // know what to set them. We record only the presence of light entries so the
            // status output can note the aircraft has light controls.
            perf.Notes.Add(" LIGHTS section present");
        }

        if (sections.TryGetValue("RADIOS", out var radios))
        {
            // Radio frequencies are populated from the simulator at runtime, but recording
            // the number of COM/NAV radios helps the ATC bridge know how many are available.
            perf.Notes.Add(" RADIOS section present");
        }
    }
    private static void ParseFlightModelCfg(string path, AircraftPerformance perf)
    {
        var content = File.ReadAllText(path);
        var sections = ParseCfgSections(content);

        if (sections.TryGetValue("AIRPLANE_GEOMETRY", out var geom))
        {
            perf.WingSpan = GetDoubleValue(geom, "wing_span", 0);
            perf.WingArea = GetDoubleValue(geom, "wing_area", 0);
            perf.MAC = GetDoubleValue(geom, "mac", 0);
        }

        if (sections.TryGetValue("FLIGHT_TUNING", out var tuning))
        {
            // flaps_up_stall_speed is the CLEAN stall speed (Vs1); full_flaps_stall_speed
            // is the LANDING configuration stall speed (Vs0). Assigning them the other
            // way round made every Vref and climb speed derive from the wrong stall speed.
            perf.Vs1 = GetDoubleValue(tuning, "flaps_up_stall_speed", 0);
            perf.Vso = GetDoubleValue(tuning, "full_flaps_stall_speed", 0);
            perf.Vfe = GetDoubleValue(tuning, "max_speed_full_flaps", 0);
            perf.Vno = GetDoubleValue(tuning, "max_speed_cruise", 0);
            perf.Vne = GetDoubleValue(tuning, "max_speed", 0);

            // The gear-extended limit is published in knots here, under its own name.
            // Reading gear_locked_above_speed instead - a CONTACT_POINTS parameter, in
            // feet per second - always missed in this section, so Vle and Vlo were always
            // zero and the gear limit had to come from the simulator's barber pole alone.
            perf.Vle = GetDoubleValue(tuning, "max_gear_extended", 0);
            perf.Vlo = perf.Vle;
        }

        if (sections.TryGetValue("WEIGHT_AND_BALANCE", out var wab))
        {
            perf.MaxGrossWeight = GetDoubleValue(wab, "max_gross_weight", perf.MaxGrossWeight);
            perf.MaxTakeoffWeight = GetDoubleValue(wab, "max_takeoff_weight", perf.MaxTakeoffWeight);
            perf.MaxLandingWeight = GetDoubleValue(wab, "max_landing_weight", perf.MaxLandingWeight);
            perf.MaxZeroFuelWeight = GetDoubleValue(wab, "max_zero_fuel_weight", perf.MaxZeroFuelWeight);
            perf.EmptyWeight = GetDoubleValue(wab, "empty_weight", perf.EmptyWeight);
            perf.CGForwardLimit = GetDoubleValue(wab, "cg_forward_limit", perf.CGForwardLimit) * 100;
            perf.CGAftLimit = GetDoubleValue(wab, "cg_aft_limit", perf.CGAftLimit) * 100;
        }
    }

    private static void ParseEnginesCfg(string path, AircraftPerformance perf)
    {
        var content = File.ReadAllText(path);
        var sections = ParseCfgSections(content);

        foreach (var (name, section) in sections)
        {
            if (name.StartsWith("GENERALENGINEDATA", StringComparison.OrdinalIgnoreCase))
            {
                var thrust = GetDoubleValue(section, "max_rated_thrust", 0);
                if (thrust > 0)
                    perf.MaxThrustPerEngine = thrust;

                // Record engine type, count, and other parameters from the general section.
                perf.EngineParams.EngineType = perf.EngineType ?? GetValue(section, "engine_type", "");
                perf.EngineParams.EngineCount = GetIntValue(section, "number_of_engines", perf.EngineParams.EngineCount);
            }

            // Parse propeller parameters from [PROP*.*] sections.
            if (name.StartsWith("PROPELLER", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("PROP", StringComparison.OrdinalIgnoreCase))
            {
                var prop = new PropellerParams
                {
                    DiameterFeet = GetDoubleValue(section, "propeller_diameter", 0),
                    PitchDegrees = GetDoubleValue(section, "propeller_pitch", 0),
                    PFactorPct = GetDoubleValue(section, "p_factor", 0),
                    MomentumLossFactor = GetDoubleValue(section, "momentum_loss_factor", 1.0),
                    FixedPitch = section.TryGetValue("fixed_pitch", out var fp) ? fp.Trim('"') : null,
                    FeatheringAvailable = section.TryGetValue("feathering_available", out var fa) ? fa.Trim('"') : null
                };
                perf.EngineParams.Propellers.Add(prop);
            }
        }

        perf.EngineParams.MaxThrustPerEngine = perf.MaxThrustPerEngine;
    }

    private static void ParseFlightPerformanceCfg(string path, AircraftPerformance perf)
    {
        var content = File.ReadAllText(path);
        var sections = ParseCfgSections(content);

        if (sections.ContainsKey("TAKEOFF_PERFORMANCE"))
        {
            perf.HasAdvancedPerformance = true;
            ParseTakeoffPerformance(sections["TAKEOFF_PERFORMANCE"], perf);
        }

        if (sections.ContainsKey("LANDING_PERFORMANCE"))
        {
            perf.HasAdvancedPerformance = true;
            ParseLandingPerformance(sections["LANDING_PERFORMANCE"], perf);
        }

        if (sections.ContainsKey("CLIMB_PERFORMANCE.0"))
        {
            perf.HasAdvancedPerformance = true;
            ParseClimbPerformance(sections, perf);
        }

        if (sections.ContainsKey("CRUISE_PERFORMANCE.0"))
        {
            ParseCruisePerformance(sections, perf);
        }

        if (sections.ContainsKey("DESCENT_PERFORMANCE.0"))
        {
            ParseDescentPerformance(sections, perf);
        }
    }

    private static void ParseTakeoffPerformance(Dictionary<string, string> section, AircraftPerformance perf)
    {
        // Parse V-speed tables
        ParseVSpeedTables(section, perf, "V1_table_by_flaps_and_weight", "V1");
        ParseVSpeedTables(section, perf, "Vr_table_by_flaps_and_weight", "Vr");
        ParseVSpeedTables(section, perf, "V2_table_by_flaps_and_weight", "V2");

        // Parse distance tables. Discarding these silently is what made the runway-length
        // decision fall back to an estimate with no indication that published distances
        // were available and thrown away, so the reason is recorded for the status output.
        foreach (var (key, what) in new[]
        {
            ("takeoff_ground_roll_distance_table", "takeoff ground roll distances"),
            ("takeoff_total_distance_table", "takeoff total distances"),
        })
        {
            if (!section.TryGetValue(key, out var data) || string.IsNullOrWhiteSpace(data)) continue;
            var points = ParseDistanceTablePoints(data);
            if (points.Count > 0)
            {
                if (key.StartsWith("takeoff_ground_roll")) perf.TakeoffPerf.GroundRollTable = points;
                else perf.TakeoffPerf.TotalDistanceTable = points;
            }
            else
            {
                perf.Notes.Add($"published {what} were not recognised and are ignored");
            }
        }
    }

    private static void ParseVSpeedTables(Dictionary<string, string> section, AircraftPerformance perf, string tableName, string vSpeedName)
    {
        if (!section.TryGetValue(tableName, out var tableData)) return;

        // Format: flaps_idx1, flaps_idx2 : weight1, weight2 :: v1, v2 : v1, v2
        var parts = tableData.Split("::");
        if (parts.Length < 2) return;

        var flapsWeights = parts[0].Split(":");
        if (flapsWeights.Length < 2) return;

        var flapsIndices = flapsWeights[0].Split(",").Select(s => Parsing.Int32(s.Trim())).ToArray();
        var weights = flapsWeights[1].Split(",").Select(s => Parsing.Double(s.Trim())).ToArray();

        var values = parts[1].Split(":")
            .Select(s => s.Split(",").Select(Parsing.Double).ToArray())
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
                // Appending a new entry per pass left three duplicate-weight rows, each
                // holding only its own populated field, and after the stable sort by
                // weight the interpolation then bracketed a live weight with the row
                // whose other speeds were zero - so Vr read as 0 and the aircraft's own
                // published rotation speed was thrown away.
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

    private static void ParseDistanceTable(Dictionary<string, string> section, AircraftPerformance perf, List<LandingDataPoint> target, string label)
    {
        var keys = ReferenceEquals(target, perf.LandingPerf.GroundRollTable)
            ? new[] { "landing_ground_roll_distance_table" }
            : ReferenceEquals(target, perf.LandingPerf.TotalDistanceTable)
                ? new[] { "landing_total_distance_table" }
                : new[] { "landing_distance_table", "landing_ground_roll_distance_table" };

        bool found = false;
        foreach (var key in keys)
        {
            if (!section.TryGetValue(key, out var data) || string.IsNullOrWhiteSpace(data)) continue;
            var points = ParseDistanceTablePoints(data);
            if (points.Count > 0)
            {
                foreach (var p in points)
                {
                    target.Add(new LandingDataPoint
                    {
                        Weight = p.Weight,
                        PressureAltitude = p.PressureAltitude,
                        Temperature = p.Temperature,
                        FlapsIndex = p.FlapsIndex,
                        Distance = p.Distance,
                    });
                }
                found = true;
            }
        }

        if (!found && section.Keys.Any(k => k.Contains("distance", StringComparison.OrdinalIgnoreCase)))
        {
            perf.Notes.Add($"published {label} were not recognised and are ignored");
        }
    }

    private static List<TakeoffDataPoint> ParseDistanceTablePoints(string data)
    {
        var points = new List<TakeoffDataPoint>();
        try
        {
            // Format: flaps_idx1, flaps_idx2 : weight1, weight2 :: dist1, dist2 : dist1, dist2
            var parts = data.Split("::");
            if (parts.Length < 2) return points;

            var flapsWeights = parts[0].Split(":");
            if (flapsWeights.Length < 2) return points;

            var flapsIndices = flapsWeights[0].Split(",").Select(s => Parsing.Int32(s.Trim())).ToArray();
            var weights = flapsWeights[1].Split(",").Select(s => Parsing.Double(s.Trim())).ToArray();

            var values = parts[1].Split(":")
                .Select(s => s.Split(",").Select(Parsing.Double).ToArray())
                .ToArray();

            for (int f = 0; f < flapsIndices.Length; f++)
            {
                for (int w = 0; w < weights.Length; w++)
                {
                    if (values.Length <= f || values[f].Length <= w) continue;
                    points.Add(new TakeoffDataPoint
                    {
                        Weight = weights[w],
                        FlapsIndex = flapsIndices[f],
                        Distance = values[f][w]
                    });
                }
            }
        }
        catch
        {
            // Return empty
        }
        return points;
    }

    private static void ParseLandingPerformance(Dictionary<string, string> section, AircraftPerformance perf)
    {
        var landingGroundRoll = perf.LandingPerf.GroundRollTable;
        var landingTotal = perf.LandingPerf.TotalDistanceTable;
        ParseDistanceTable(section, perf, landingGroundRoll, "landing ground roll distances");
        ParseDistanceTable(section, perf, landingTotal, "landing total distances");
    }

    private static void ParseClimbPerformance(Dictionary<string, Dictionary<string, string>> sections, AircraftPerformance perf)
    {
        foreach (var (name, section) in sections)
        {
            if (name.StartsWith("CLIMB_PERFORMANCE"))
            {
                var profile = new ClimbProfile
                {
                    Name = GetValue(section, "name", name),
                    IASBelowTransition = GetDoubleValue(section, "IAS_below_transition", 0),
                    IASAboveTransition = GetDoubleValue(section, "IAS_above_transition", 0),
                    MachAboveTransition = GetDoubleValue(section, "Mach_above_transition", 0),
                    MaxAltitude = GetDoubleValue(section, "max_altitude", 0)
                };
                perf.ClimbPerf.Profiles.Add(profile);
            }
        }
    }

    private static void ParseCruisePerformance(Dictionary<string, Dictionary<string, string>> sections, AircraftPerformance perf)
    {
        foreach (var (name, section) in sections)
        {
            if (name.StartsWith("CRUISE_PERFORMANCE"))
            {
                var profile = new CruiseProfile
                {
                    Name = GetValue(section, "name", name),
                    IAS = GetDoubleValue(section, "IAS", 0),
                    Mach = GetDoubleValue(section, "Mach", 0),
                    Altitude = GetDoubleValue(section, "altitude", 0),
                    FuelFlowPPH = GetDoubleValue(section, "fuel_flow_pph", 0),
                    TAS = GetDoubleValue(section, "TAS", 0),
                    SpecificRange = GetDoubleValue(section, "specific_range", 0)
                };
                perf.CruisePerf.Profiles.Add(profile);
            }
        }
    }

    private static void ParseDescentPerformance(Dictionary<string, Dictionary<string, string>> sections, AircraftPerformance perf)
    {
        foreach (var (name, section) in sections)
        {
            if (name.StartsWith("DESCENT_PERFORMANCE"))
            {
                var profile = new DescentProfile
                {
                    Name = GetValue(section, "name", name),
                    IAS = GetDoubleValue(section, "IAS", 0),
                    Mach = GetDoubleValue(section, "Mach", 0),
                    RateOfDescent = GetDoubleValue(section, "rate_of_descent", 0),
                    Gradient = GetDoubleValue(section, "gradient", 0)
                };
                perf.DescentPerf.Profiles.Add(profile);
            }
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseCfgSections(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = "";
        var currentDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith(";"))
                continue;

            var match = Regex.Match(trimmed, @"^\[(.+)\]$");
            if (match.Success)
            {
                if (!string.IsNullOrEmpty(currentSection))
                {
                    sections[currentSection] = currentDict;
                }
                currentSection = match.Groups[1].Value;
                currentDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = trimmed[..eqIndex].Trim();
                var value = trimmed[(eqIndex + 1)..].Trim();
                currentDict[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(currentSection))
        {
            sections[currentSection] = currentDict;
        }

        return sections;
    }

    private static string GetValue(Dictionary<string, string> dict, string key, string defaultValue) =>
        dict.TryGetValue(key, out var v) ? v.Trim('"') : defaultValue;

    private static int GetIntValue(Dictionary<string, string> dict, string key, int defaultValue) =>
        dict.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : defaultValue;

    private static double GetDoubleValue(Dictionary<string, string> dict, string key, double defaultValue) =>
        dict.TryGetValue(key, out var v) && Parsing.TryDouble(v, out var d) ? d : defaultValue;
}

public static class FlightPlanParser
{
    public static NavigationState ParsePLN(string plnPath)
    {
        var nav = new NavigationState();
        nav.ActiveFlightPlanPath = plnPath;

        try
        {
            var doc = XDocument.Load(plnPath);
            var flightPlan = doc.Descendants("FlightPlan.FlightPlan").FirstOrDefault();
            if (flightPlan == null) return nav;

            // DepartureID/DestinationID are the ICAO codes; DepartureName/DestinationName
            // are the airport names and must NOT overwrite the ICAO.
            nav.DepartureICAO = flightPlan.Element("DepartureID")?.Value?.Trim();
            nav.ArrivalICAO = flightPlan.Element("DestinationID")?.Value?.Trim();
            nav.DepartureAirportName = flightPlan.Element("DepartureName")?.Value?.Trim();
            nav.ArrivalAirportName = flightPlan.Element("DestinationName")?.Value?.Trim();

            // DeparturePosition is documented as "Departure Runway Number".
            nav.DepartureRunway = NormalizeRunwayName(flightPlan.Element("DeparturePosition")?.Value);

            var departureLla = ParseWorldPosition(flightPlan.Element("DepartureLLA")?.Value ?? "");
            var destinationLla = ParseWorldPosition(flightPlan.Element("DestinationLLA")?.Value ?? "");
            nav.DepartureLatitude = departureLla.lat;
            nav.DepartureLongitude = departureLla.lon;
            nav.DestinationLatitude = destinationLla.lat;
            nav.DestinationLongitude = destinationLla.lon;
            nav.DepartureAltitude = departureLla.alt;
            nav.DestinationAltitude = destinationLla.alt;

            if (Parsing.TryDouble(flightPlan.Element("CruisingAlt")?.Value, out var cruisingAlt))
            {
                nav.CruiseAltitudeFt = cruisingAlt;
            }

            // MSFS 2024 plans name the procedures in plain-text detail elements
            // instead of on the waypoints. The arrival procedure appears as both
            // ArrivalDetails and DestinationDetails depending on the exporter, so
            // read whichever is present.
            var departureDetails = flightPlan.Element("DepartureDetails")?.Value?.Trim();
            var arrivalDetails = (flightPlan.Element("ArrivalDetails")?.Value
                                  ?? flightPlan.Element("DestinationDetails")?.Value)?.Trim();
            var approachDetails = flightPlan.Element("ApproachDetails")?.Value?.Trim();

            var waypoints = flightPlan.Elements("ATCWaypoint");
            foreach (var wp in waypoints)
            {
                var ident = wp.Element("ICAO")?.Element("ICAOIdent")?.Value
                            ?? wp.Attribute("id")?.Value
                            ?? "";
                var typeStr = wp.Element("ATCWaypointType")?.Value ?? "User";
                var posStr = wp.Element("WorldPosition")?.Value ?? "";
                var airway = wp.Element("ATCAirway")?.Value;
                var runwayNum = wp.Element("RunwayNumberFP")?.Value;
                var runwayDes = wp.Element("RunwayDesignatorFP")?.Value;
                var departureFP = wp.Element("DepartureFP")?.Value;
                var arrivalFP = wp.Element("ArrivalFP")?.Value;
                var approachType = wp.Element("ApproachTypeFP")?.Value;

                // MSFS 2024 native plans omit WorldPosition on waypoints entirely.
                // Keep any waypoint whose position we know and skip the rest rather
                // than recording them at 0,0 and corrupting the geometry.
                var hasPosition = !string.IsNullOrEmpty(posStr);
                var (lat, lon, alt) = hasPosition
                    ? ParseWorldPosition(posStr)
                    : (double.NaN, double.NaN, double.NaN);

                var waypoint = new FlightPlanWaypoint
                {
                    Ident = ident,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = alt,
                    Type = Enum.TryParse<WaypointType>(typeStr, true, out var wt) ? wt : WaypointType.User,
                    Airway = airway,
                    Runway = NormalizeRunwayName(runwayNum, runwayDes)
                };

                if (!string.IsNullOrEmpty(departureFP))
                {
                    nav.SIDName = departureFP;
                    waypoint.Type = WaypointType.SID;
                }
                if (!string.IsNullOrEmpty(arrivalFP))
                {
                    nav.STARName = arrivalFP;
                    waypoint.Type = WaypointType.STAR;
                }
                if (!string.IsNullOrEmpty(approachType))
                {
                    nav.ApproachName = approachType;
                    nav.ApproachType = approachType;
                    waypoint.Type = WaypointType.Approach;
                }

                nav.Waypoints.Add(waypoint);
            }

            // Runway identification comes from the waypoints: the SID fixes carry the
            // departure runway and the approach fixes carry the arrival runway.
            var departureRunway = nav.Waypoints
                .FirstOrDefault(w => w.Type == WaypointType.SID && !string.IsNullOrEmpty(w.Runway))?.Runway;
            if (!string.IsNullOrEmpty(departureRunway) && string.IsNullOrEmpty(nav.DepartureRunway))
            {
                nav.DepartureRunway = departureRunway;
            }

            var arrivalRunway = nav.Waypoints
                .LastOrDefault(w => !string.IsNullOrEmpty(w.Runway))?.Runway;
            if (!string.IsNullOrEmpty(arrivalRunway))
            {
                nav.ArrivalRunway = arrivalRunway;
            }

            // MSFS 2024 exports the procedure names as free-text details instead.
            if (string.IsNullOrEmpty(nav.SIDName) && !string.IsNullOrEmpty(departureDetails)) nav.SIDName = departureDetails;
            if (string.IsNullOrEmpty(nav.STARName) && !string.IsNullOrEmpty(arrivalDetails)) nav.STARName = arrivalDetails;
            if (string.IsNullOrEmpty(nav.ApproachName) && !string.IsNullOrEmpty(approachDetails)) nav.ApproachName = approachDetails;

            // The approach runway, when present, overrides the last waypoint's runway.
            var approachRunway = nav.Waypoints
                .FirstOrDefault(w => w.Type == WaypointType.Approach && !string.IsNullOrEmpty(w.Runway))?.Runway;
            if (!string.IsNullOrEmpty(approachRunway))
            {
                nav.ArrivalRunway = approachRunway;
            }

            // MSFS 2024 native plans carry no waypoint positions at all. The two
            // airport waypoints can still be placed from the header LLA fields, which
            // is enough for the taxi router to find them on the ground network.
            ApplyAirportPositionsToEndpoints(nav);

            CalculateWaypointDistances(nav);
        }
        catch (Exception)
        {
            // Return partial state
        }

        return nav;
    }

    /// <summary>
    /// Fills in the departure and destination waypoint positions from the plan's
    /// <c>DepartureLLA</c> / <c>DestinationLLA</c> when the waypoints themselves carry
    /// none, which is the case for plans written by the MSFS 2024 built-in planner.
    /// </summary>
    private static void ApplyAirportPositionsToEndpoints(NavigationState nav)
    {
        if (nav.DepartureLatitude == 0 && nav.DepartureLongitude == 0 &&
            nav.DestinationLatitude == 0 && nav.DestinationLongitude == 0)
        {
            return;
        }

        var airports = nav.Waypoints.Where(w => w.Type == WaypointType.Airport).ToList();
        if (airports.Count == 0) return;

        if (nav.DepartureLatitude != 0 || nav.DepartureLongitude != 0)
        {
            var first = airports[0];
            first.Latitude = nav.DepartureLatitude;
            first.Longitude = nav.DepartureLongitude;
            first.Altitude = nav.DepartureAltitude;
        }

        if (nav.DestinationLatitude != 0 || nav.DestinationLongitude != 0)
        {
            var last = airports[^1];
            last.Latitude = nav.DestinationLatitude;
            last.Longitude = nav.DestinationLongitude;
            last.Altitude = nav.DestinationAltitude;
        }
    }

    /// <summary>
    /// Normalises a runway identifier. A .pln carries a bare number ("19") and an
    /// integer designator, while runway records are named "06" / "25L", so both the
    /// padding and the designator spelling have to agree before they can be matched.
    /// </summary>
    public static string? NormalizeRunwayName(string? number, string? designator = null)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;

        var trimmed = number.Trim().ToUpperInvariant();

        // Values such as "19" / "RW25" / "25L". The embedded designator wins unless
        // the caller supplied one separately, which is how RunwayDesignatorFP arrives.
        var match = Regex.Match(trimmed, @"^(?:RW)?(\d{1,2})([LRCW])?$");
        if (match.Success)
        {
            var num = Parsing.Int32(match.Groups[1].Value);
            if (num is not (> 0 and <= 36)) return null;

            var fromNumber = match.Groups[2].Value;
            var fromArgument = NormalizeDesignator(designator);
            var suffix = !string.IsNullOrEmpty(fromNumber) ? fromNumber : fromArgument;

            return $"{num:00}{suffix}";
        }

        // Compass directions used for helipads / water runways.
        if (trimmed is "NORTH" or "SOUTH" or "EAST" or "WEST" or "NORTHEAST" or "NORTHWEST"
            or "SOUTHEAST" or "SOUTHWEST")
        {
            return trimmed;
        }

        if (int.TryParse(trimmed, out var bare) && bare is > 0 and <= 36)
        {
            var suffix = NormalizeDesignator(designator);
            return $"{bare:00}{suffix}";
        }

        return null;
    }

    private static string NormalizeDesignator(string? designator)
    {
        if (string.IsNullOrWhiteSpace(designator)) return "";
        return designator.Trim().ToUpperInvariant() switch
        {
            "NONE" or "0" => "",
            "LEFT" or "L" or "1" => "L",
            "RIGHT" or "R" or "2" => "R",
            "CENTER" or "C" or "3" => "C",
            "WATER" or "W" or "4" => "W",
            "A" or "5" => "A",
            "B" or "6" => "B",
            _ => ""
        };
    }

    private static (double lat, double lon, double alt) ParseWorldPosition(string pos)
    {
        if (string.IsNullOrEmpty(pos)) return (0, 0, 0);

        try
        {
            var parts = pos.Split(',');
            if (parts.Length != 3) return (0, 0, 0);

            var lat = ParseDMS(parts[0].Trim());
            var lon = ParseDMS(parts[1].Trim());
            var alt = Parsing.Double(parts[2].Trim().Replace("+", ""));

            return (lat, lon, alt);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static double ParseDMS(string dms)
    {
        var match = Regex.Match(dms, @"([NSWE])?(\d+)°\s*(\d+)'\s*([\d.]+)""");
        if (!match.Success) return 0;

        var hemi = match.Groups[1].Value;
        var deg = Parsing.Double(match.Groups[2].Value);
        var min = Parsing.Double(match.Groups[3].Value);
        var sec = Parsing.Double(match.Groups[4].Value);

        var result = deg + min / 60.0 + sec / 3600.0;
        if (hemi == "S" || hemi == "W") result = -result;
        return result;
    }

    private static void CalculateWaypointDistances(NavigationState nav)
    {
        // Only waypoints that carry a position contribute geometry; a MSFS 2024
        // native plan omits WorldPosition for most fixes.
        double? previousLat = null;
        double? previousLon = null;

        foreach (var wp in nav.Waypoints)
        {
            if (!wp.HasPosition)
            {
                wp.DistanceFromPrevious = 0;
                continue;
            }

            wp.DistanceFromPrevious = previousLat.HasValue
                ? CalculateDistance(previousLat.Value, previousLon!.Value, wp.Latitude, wp.Longitude)
                : 0;

            previousLat = wp.Latitude;
            previousLon = wp.Longitude;
        }
    }

    public static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 3440.065; // NM
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}