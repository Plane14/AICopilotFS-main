using System.Text;

namespace MSFS2024AIPilot.Core;

public sealed class RandomizedFlightOptions
{
    public bool Enabled { get; set; }
    public bool StopAfterThisFlight { get; set; }
    public int ServiceMinutesBetweenLegs { get; set; } = 8;
}

public sealed class AutomationStatusSnapshot
{
    public bool Enabled { get; set; }
    public bool StopAfterThisFlight { get; set; }
    public string Status { get; set; } = "";
    public string LastPlanSummary { get; set; } = "";
    public int LegsCompleted { get; set; }
}

public sealed class RandomizedFlightFuelPlan
{
    public double TripGallons { get; set; }
    public double AlternateGallons { get; set; }
    public double ContingencyGallons { get; set; }
    public double FinalReserveGallons { get; set; }
    public double TotalGallons => TripGallons + AlternateGallons + ContingencyGallons + FinalReserveGallons;
}

public sealed class RandomizedFlightLegPlan
{
    public string DepartureIcao { get; set; } = "";
    public string DestinationIcao { get; set; } = "";
    public string AlternateIcao { get; set; } = "";
    public string DestinationRunway { get; set; } = "";
    public string AlternateRunway { get; set; } = "";
    public double TripDistanceNm { get; set; }
    public double AlternateDistanceNm { get; set; }
    public double CruiseAltitudeFt { get; set; }
    public double EstimatedMoraFt { get; set; }
    public RandomizedFlightFuelPlan Fuel { get; set; } = new();
}

public static class RandomizedFlightPlanner
{
    public static bool TryPlanNextLeg(
        string departureIcao,
        IReadOnlyCollection<AirportData> airports,
        AircraftPerformance perf,
        AircraftState state,
        Random random,
        string? previousDestinationIcao,
        out RandomizedFlightLegPlan plan,
        out string failure)
    {
        plan = new RandomizedFlightLegPlan();
        failure = "";

        if (string.IsNullOrWhiteSpace(departureIcao))
        {
            failure = "No departure ICAO is available.";
            return false;
        }

        var departure = airports.FirstOrDefault(a =>
            a.Icao.Equals(departureIcao, StringComparison.OrdinalIgnoreCase));
        if (departure is null)
        {
            failure = $"Departure airport {departureIcao} is not loaded.";
            return false;
        }

        var suitable = airports.Where(a => IsSuitableForAircraft(a, perf, state)).ToList();
        if (suitable.Count < 3)
        {
            failure = "Not enough suitable loaded airports for destination + alternate selection.";
            return false;
        }

        var destinationPool = suitable
            .Where(a => !a.Icao.Equals(departure.Icao, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(previousDestinationIcao))
        {
            var withoutPrevious = destinationPool
                .Where(a => !a.Icao.Equals(previousDestinationIcao, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (withoutPrevious.Count > 0) destinationPool = withoutPrevious;
        }

        if (destinationPool.Count == 0)
        {
            failure = "No destination airport candidate is available.";
            return false;
        }

        var destination = destinationPool[random.Next(destinationPool.Count)];
        var alternate = SelectAlternate(destination, suitable, departure.Icao);
        if (alternate is null)
        {
            failure = $"No suitable alternate airport found for {destination.Icao}.";
            return false;
        }

        var tripNm = Geo.DistanceNauticalMiles(
            departure.ReferenceLatitude, departure.ReferenceLongitude,
            destination.ReferenceLatitude, destination.ReferenceLongitude);
        var alternateNm = Geo.DistanceNauticalMiles(
            destination.ReferenceLatitude, destination.ReferenceLongitude,
            alternate.ReferenceLatitude, alternate.ReferenceLongitude);
        var track = Geo.BearingDegrees(
            departure.ReferenceLatitude, departure.ReferenceLongitude,
            destination.ReferenceLatitude, destination.ReferenceLongitude);
        var mora = EstimateMoraFt(departure, destination);
        var cruiseFt = SelectCruiseAltitudeFt(perf, tripNm, track, mora);
        var fuel = PlanFuel(perf, state, tripNm, alternateNm);

        var destinationRunway = LongestRunway(destination);
        var alternateRunway = LongestRunway(alternate);

        plan = new RandomizedFlightLegPlan
        {
            DepartureIcao = departure.Icao,
            DestinationIcao = destination.Icao,
            AlternateIcao = alternate.Icao,
            DestinationRunway = destinationRunway?.PrimaryName ?? "",
            AlternateRunway = alternateRunway?.PrimaryName ?? "",
            TripDistanceNm = tripNm,
            AlternateDistanceNm = alternateNm,
            CruiseAltitudeFt = cruiseFt,
            EstimatedMoraFt = mora,
            Fuel = fuel
        };

        return true;
    }

    internal static RandomizedFlightFuelPlan PlanFuel(
        AircraftPerformance perf,
        AircraftState state,
        double tripNm,
        double alternateNm)
    {
        var tas = perf.CruisePerf.Profiles.FirstOrDefault(p => p.TAS > 60)?.TAS;
        if (tas is null || tas <= 0) tas = state.TrueAirspeed > 60 ? state.TrueAirspeed : 140;

        var burnGph = EstimateFuelBurnGph(perf, state);
        var tripHours = Math.Max(tripNm, 0) / tas.Value;
        var alternateHours = Math.Max(alternateNm, 0) / tas.Value;
        var reserveMinutes = FinalReserveMinutes(perf);

        var trip = tripHours * burnGph;
        var alternate = alternateHours * burnGph;
        var contingency = trip * 0.05;
        var reserve = (reserveMinutes / 60.0) * burnGph;

        return new RandomizedFlightFuelPlan
        {
            TripGallons = trip,
            AlternateGallons = alternate,
            ContingencyGallons = contingency,
            FinalReserveGallons = reserve
        };
    }

    internal static double SelectCruiseAltitudeFt(
        AircraftPerformance perf,
        double distanceNm,
        double trueTrackDeg,
        double moraFt)
    {
        var ceiling = ResolveCeilingFt(perf);
        var baseAltitude = distanceNm switch
        {
            < 80 => 6000,
            < 180 => 10000,
            < 350 => 16000,
            < 650 => 24000,
            _ => 32000
        };

        var semiCircular = ApplySemiCircularRule(baseAltitude, trueTrackDeg);
        var floor = Math.Max(moraFt, 3000);
        var selected = Math.Max(semiCircular, floor);
        var capped = Math.Min(selected, ceiling);

        // Keep the semi-circular shape after capping.
        if (capped < selected)
        {
            capped = ApplySemiCircularRule(capped, trueTrackDeg);
            capped = Math.Min(capped, ceiling);
        }

        return Math.Max(3000, Math.Round(capped / 500.0) * 500.0);
    }

    internal static double ApplySemiCircularRule(double altitudeFt, double trueTrackDeg)
    {
        var thousands = Math.Floor(Math.Max(altitudeFt, 3000) / 1000.0);
        var eastbound = trueTrackDeg is >= 0 and < 180;
        if (eastbound && thousands % 2 == 0) thousands += 1;
        if (!eastbound && thousands % 2 != 0) thousands += 1;
        return thousands * 1000 + 500;
    }

    private static bool IsSuitableForAircraft(AirportData airport, AircraftPerformance perf, AircraftState state)
    {
        if (!airport.IsLoaded || airport.IsClosed || airport.Runways.Count == 0) return false;

        var requiredTakeoffMeters = RequiredRunwayMeters(perf, state, true);
        var requiredLandingMeters = RequiredRunwayMeters(perf, state, false);
        var minWidthMeters = Math.Max(18, Geo.FeetToMeters(perf.WingSpan > 0 ? perf.WingSpan * 0.45 : 60));

        return airport.Runways.Any(r =>
            !r.PrimaryClosed &&
            r.LengthMeters >= requiredTakeoffMeters &&
            r.LengthMeters >= requiredLandingMeters &&
            r.WidthMeters >= minWidthMeters);
    }

    private static double RequiredRunwayMeters(AircraftPerformance perf, AircraftState state, bool takeoff)
    {
        var meters = PerformanceCalculator.CalculateRequiredRunwayLength(perf, state, flapsIndex: 1, takeoff);
        if (meters > 100) return meters;

        return perf.Category.ToUpperInvariant() switch
        {
            "J" => 2600,
            "H" => 2400,
            "M" => 1700,
            _ => 1000
        };
    }

    private static RunwayInfo? LongestRunway(AirportData airport) =>
        airport.Runways.Where(r => !r.PrimaryClosed).OrderByDescending(r => r.LengthMeters).FirstOrDefault();

    private static AirportData? SelectAlternate(AirportData destination, List<AirportData> pool, string departureIcao)
    {
        return pool
            .Where(a =>
                !a.Icao.Equals(destination.Icao, StringComparison.OrdinalIgnoreCase) &&
                !a.Icao.Equals(departureIcao, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => Geo.DistanceMeters(
                destination.ReferenceLatitude, destination.ReferenceLongitude,
                a.ReferenceLatitude, a.ReferenceLongitude))
            .FirstOrDefault();
    }

    private static double EstimateMoraFt(AirportData departure, AirportData destination)
    {
        var floor = Math.Max(departure.ReferenceAltitudeMeters, destination.ReferenceAltitudeMeters);
        return Geo.MetersToFeet(floor) + 2000;
    }

    private static double ResolveCeilingFt(AircraftPerformance perf)
    {
        var fromCruise = perf.CruisePerf.MaxCruiseAltitude;
        var fromProfiles = perf.CruisePerf.Profiles.Where(p => p.Altitude > 0).Select(p => p.Altitude).DefaultIfEmpty(0).Max();
        var best = Math.Max(fromCruise, fromProfiles);
        return best > 5000 ? best : 39000;
    }

    private static double EstimateFuelBurnGph(AircraftPerformance perf, AircraftState state)
    {
        var livePph = state.EngineFF.Where(v => v > 0).Sum();
        if (livePph > 0) return livePph / 6.7;

        var profile = perf.CruisePerf.Profiles.FirstOrDefault(p => p.FuelFlowPPH > 0);
        if (profile is not null) return profile.FuelFlowPPH / 6.7;

        return perf.EngineType.ToUpperInvariant() switch
        {
            "JET" => Math.Max(120, perf.EngineCount * 90),
            "TURBOPROP" => Math.Max(70, perf.EngineCount * 35),
            _ => Math.Max(25, perf.EngineCount * 12)
        };
    }

    private static int FinalReserveMinutes(AircraftPerformance perf)
    {
        var turbine = perf.EngineType.Contains("JET", StringComparison.OrdinalIgnoreCase) ||
                      perf.EngineType.Contains("TURB", StringComparison.OrdinalIgnoreCase);
        return turbine ? 45 : 30;
    }
}

public static class FlightPlanWriter
{
    public static string WriteLegPlanToTempPln(RandomizedFlightLegPlan plan, AirportData departure, AirportData destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aipilot-random-legs");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"AIPilot-{departure.Icao}-{destination.Icao}-{DateTime.UtcNow:yyyyMMddHHmmss}.pln");
        File.WriteAllText(file, BuildPln(plan, departure, destination), Encoding.UTF8);
        return file;
    }

    private static string BuildPln(RandomizedFlightLegPlan plan, AirportData departure, AirportData destination)
    {
        var depLla = WorldPosition(departure.ReferenceLatitude, departure.ReferenceLongitude, Geo.MetersToFeet(departure.ReferenceAltitudeMeters));
        var dstLla = WorldPosition(destination.ReferenceLatitude, destination.ReferenceLongitude, Geo.MetersToFeet(destination.ReferenceAltitudeMeters));
        var depRwy = departure.Runways.OrderByDescending(r => r.LengthMeters).FirstOrDefault()?.PrimaryName ?? "";

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="AceXML" version="1,0">
  <Descr>AceXML Document</Descr>
  <FlightPlan.FlightPlan>
    <Title>AIPilot Randomized Leg</Title>
    <FPType>IFR</FPType>
    <CruisingAlt>{Math.Round(plan.CruiseAltitudeFt):F0}</CruisingAlt>
    <DepartureID>{departure.Icao}</DepartureID>
    <DepartureName>{XmlEscape(departure.Name)}</DepartureName>
    <DepartureLLA>{depLla}</DepartureLLA>
    <DestinationID>{destination.Icao}</DestinationID>
    <DestinationName>{XmlEscape(destination.Name)}</DestinationName>
    <DestinationLLA>{dstLla}</DestinationLLA>
    <DeparturePosition>{depRwy}</DeparturePosition>
    <ATCWaypoint id="{departure.Icao}">
      <ATCWaypointType>Airport</ATCWaypointType>
      <WorldPosition>{depLla}</WorldPosition>
      <ICAO><ICAOIdent>{departure.Icao}</ICAOIdent></ICAO>
    </ATCWaypoint>
    <ATCWaypoint id="{destination.Icao}">
      <ATCWaypointType>Airport</ATCWaypointType>
      <WorldPosition>{dstLla}</WorldPosition>
      <ICAO><ICAOIdent>{destination.Icao}</ICAOIdent></ICAO>
    </ATCWaypoint>
  </FlightPlan.FlightPlan>
</SimBase.Document>
""";
    }

    private static string WorldPosition(double lat, double lon, double altitudeFt)
        => $"{Dms(lat, 'N', 'S')},{Dms(lon, 'E', 'W')},+{altitudeFt:000000.00}";

    private static string Dms(double value, char positiveHemisphere, char negativeHemisphere)
    {
        var hemisphere = value >= 0 ? positiveHemisphere : negativeHemisphere;
        var absolute = Math.Abs(value);
        var degrees = Math.Floor(absolute);
        var minutesFloat = (absolute - degrees) * 60.0;
        var minutes = Math.Floor(minutesFloat);
        var seconds = (minutesFloat - minutes) * 60.0;
        return $"{hemisphere}{degrees:00}° {minutes:00}' {seconds:00.00}\"";
    }

    private static string XmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
