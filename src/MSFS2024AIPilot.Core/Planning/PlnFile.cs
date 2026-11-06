using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Core.Planning;

/// <summary>
/// Reads and writes MSFS .pln flight plan files (AceXML). Used both to load the
/// user's own plan and to generate plans for automated flights.
/// </summary>
public static class PlnFile
{
    /// <summary>Writes a .pln AceXML document for the given waypoints.</summary>
    public static void Write(string path, string title, string departureIcao, string arrivalIcao,
        string departureName, string arrivalName, double cruiseAltitudeFt,
        IReadOnlyList<FlightPlanWaypoint> waypoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<SimBase.Document Type=\"AceXML\" version=\"1,0\">");
        sb.AppendLine("  <Descr>AceXML Document</Descr>");
        sb.AppendLine("  <FlightPlan.FlightPlan>");
        sb.AppendLine($"    <Title>{Xml(title)}</Title>");
        sb.AppendLine("    <FPType>IFR</FPType>");
        if (waypoints.Count > 0)
            sb.AppendLine($"    <CruisingAlt>{(int)Math.Round(cruiseAltitudeFt):D5}</CruisingAlt>");
        if (!string.IsNullOrEmpty(departureIcao)) sb.AppendLine($"    <DepartureID>{Xml(departureIcao)}</DepartureID>");
        if (!string.IsNullOrEmpty(arrivalIcao)) sb.AppendLine($"    <DestinationID>{Xml(arrivalIcao)}</DestinationID>");
        sb.AppendLine($"    <Descr>{Xml(title)}</Descr>");
        if (!string.IsNullOrEmpty(departureName)) sb.AppendLine($"    <DepartureName>{Xml(departureName)}</DepartureName>");
        if (!string.IsNullOrEmpty(arrivalName)) sb.AppendLine($"    <DestinationName>{Xml(arrivalName)}</DestinationName>");
        sb.AppendLine("    <AppVersion><AppVersionMajor>11</AppVersionMajor><AppVersionBuild>1</AppVersionBuild></AppVersion>");

        foreach (var wp in waypoints)
        {
            if (!wp.HasPosition) continue;
            var id = SanitizeId(wp.Ident);
            sb.AppendLine($"    <ATCWaypoint id=\"{Xml(id)}\">");
            sb.AppendLine($"      <ATCWaypointType>{WaypointTypeName(wp.Type)}</ATCWaypointType>");
            sb.AppendLine($"      <WorldPosition>{FormatDms(wp.Latitude, wp.Longitude, wp.Altitude)}</WorldPosition>");
            if (!string.IsNullOrEmpty(wp.Region) || !string.IsNullOrEmpty(wp.Ident))
            {
                sb.AppendLine("      <ICAO>");
                if (!string.IsNullOrEmpty(wp.Region)) sb.AppendLine($"        <ICAORegion>{Xml(wp.Region)}</ICAORegion>");
                if (!string.IsNullOrEmpty(wp.Ident)) sb.AppendLine($"        <ICAOIdent>{Xml(wp.Ident)}</ICAOIdent>");
                sb.AppendLine("      </ICAO>");
            }
            if (!string.IsNullOrEmpty(wp.Airway)) sb.AppendLine($"      <ATCAirway>{Xml(wp.Airway)}</ATCAirway>");
            sb.AppendLine("    </ATCWaypoint>");
        }

        sb.AppendLine("  </FlightPlan.FlightPlan>");
        sb.AppendLine("</SimBase.Document>");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>Parses a .pln file into waypoints plus plan metadata.</summary>
    public static (List<FlightPlanWaypoint> waypoints, NavigationState nav) Parse(string path)
    {
        var doc = XDocument.Load(path);
        var plan = doc.Descendants("FlightPlan.FlightPlan").FirstOrDefault();
        var nav = new NavigationState();
        var waypoints = new List<FlightPlanWaypoint>();
        if (plan is null) return (waypoints, nav);

        nav.DepartureIcao = plan.Element("DepartureID")?.Value;
        nav.ArrivalIcao = plan.Element("DestinationID")?.Value;
        nav.DepartureAirportName = plan.Element("DepartureName")?.Value;
        nav.ArrivalAirportName = plan.Element("DestinationName")?.Value;
        var cruise = plan.Element("CruisingAlt")?.Value;
        if (int.TryParse(cruise, out var alt)) nav.CruiseAltitudeFt = alt;

        foreach (var atc in plan.Descendants("ATCWaypoint"))
        {
            var wp = new FlightPlanWaypoint();
            wp.Ident = atc.Attribute("id")?.Value ?? "";
            var typeEl = atc.Element("ATCWaypointType")?.Value ?? "User";
            wp.Type = typeEl switch
            {
                "Airport" => WaypointType.Airport,
                "NDB" => WaypointType.Ndb,
                "Vor" or "VOR" => WaypointType.Vor,
                "Intersection" => WaypointType.Intersection,
                "Runway" => WaypointType.Runway,
                "SID" or "Sid" => WaypointType.Sid,
                "STAR" or "Star" => WaypointType.Star,
                "Approach" => WaypointType.Approach,
                _ => WaypointType.User,
            };
            var wpEl = atc.Element("WorldPosition")?.Value ?? "";
            var (lat, lon, altFt) = ParseWorldPosition(wpEl);
            wp.Latitude = lat; wp.Longitude = lon; wp.Altitude = altFt;
            var icao = atc.Element("ICAO");
            wp.Region = icao?.Element("ICAORegion")?.Value;
            var ident = icao?.Element("ICAOIdent")?.Value;
            if (!string.IsNullOrEmpty(ident)) wp.Ident = ident;
            wp.Airway = atc.Element("ATCAirway")?.Value;
            waypoints.Add(wp);
        }

        // Departure/destination coordinates from the first/last airport waypoints.
        var airports = waypoints.Where(w => w.Type == WaypointType.Airport && w.HasPosition).ToList();
        if (airports.Count > 0)
        {
            nav.DepartureLatitude = airports[0].Latitude;
            nav.DepartureLongitude = airports[0].Longitude;
            nav.DepartureAltitude = airports[0].Altitude;
            if (!string.IsNullOrEmpty(airports[0].Ident)) nav.DepartureIcao ??= airports[0].Ident;
        }
        if (airports.Count > 1)
        {
            var last = airports[^1];
            nav.DestinationLatitude = last.Latitude;
            nav.DestinationLongitude = last.Longitude;
            nav.DestinationAltitude = last.Altitude;
            if (!string.IsNullOrEmpty(last.Ident)) nav.ArrivalIcao ??= last.Ident;
        }

        nav.Waypoints = waypoints;
        return (waypoints, nav);
    }

    // ------------------------------------------------------------------

    /// <summary>MSFS WorldPosition: N34° 12' 36.57",W118° 29' 24.03",+000775.50</summary>
    public static string FormatDms(double lat, double lon, double altFt)
    {
        var latHem = lat >= 0 ? 'N' : 'S';
        var lonHem = lon >= 0 ? 'E' : 'W';
        var (latD, latM, latS) = DegToDms(Math.Abs(lat));
        var (lonD, lonM, lonS) = DegToDms(Math.Abs(lon));
        var alt = altFt.ToString("+000000.00", CultureInfo.InvariantCulture);
        return $"{latHem}{latD:00}° {latM:00}' {latS:00.00}\",{lonHem}{lonD:00}° {lonM:00}' {lonS:00.00}\",{alt}";
    }

    private static (int d, int m, double s) DegToDms(double deg)
    {
        var d = (int)Math.Floor(deg);
        var mFloat = (deg - d) * 60;
        var m = (int)Math.Floor(mFloat);
        var s = (mFloat - m) * 60;
        return (d, m, s);
    }

    private static (double lat, double lon, double alt) ParseWorldPosition(string wp)
    {
        // N34° 12' 36.57",W118° 29' 24.03",+000775.50
        double lat = 0, lon = 0, alt = 0;
        try
        {
            var parts = wp.Split(',');
            if (parts.Length >= 2)
            {
                lat = ParseCoord(parts[0].Trim());
                lon = ParseCoord(parts[1].Trim());
            }
            if (parts.Length >= 3) alt = double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
        }
        catch { }
        return (lat, lon, alt);
    }

    private static double ParseCoord(string s)
    {
        // e.g. N34° 12' 36.57" or N34°12'36.57"
        var hemi = s.Length > 0 && char.IsLetter(s[0]) ? s[0] : 'N';
        var sign = (hemi == 'S' || hemi == 'W') ? -1 : 1;
        var nums = System.Text.RegularExpressions.Regex.Matches(s, @"\d+")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        // degrees may include decimals: better parse with regex for deg/min/sec
        var m2 = System.Text.RegularExpressions.Regex.Match(s, @"(\d+(?:\.\d+)?)\D+(\d+(?:\.\d+)?)\D+(\d+(?:\.\d+)?)");
        if (m2.Success)
        {
            double d = double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
            double min = double.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture);
            double sec = double.Parse(m2.Groups[3].Value, CultureInfo.InvariantCulture);
            return sign * (d + min / 60.0 + sec / 3600.0);
        }
        if (nums.Length > 0) return sign * nums[0];
        return 0;
    }

    private static string WaypointTypeName(WaypointType t) => t switch
    {
        WaypointType.Airport => "Airport",
        WaypointType.Ndb => "NDB",
        WaypointType.Vor => "Vor",
        WaypointType.Intersection => "Intersection",
        WaypointType.Runway => "Runway",
        WaypointType.Sid => "SID",
        WaypointType.Star => "STAR",
        WaypointType.Approach => "Approach",
        _ => "User",
    };

    private static string SanitizeId(string ident)
    {
        var s = new string(ident.Where(c => char.IsLetterOrDigit(c)).ToArray());
        return string.IsNullOrEmpty(s) ? "WP" : s;
    }

    private static string Xml(string s) =>
        System.Security.SecurityElement.Escape(s ?? "") ?? "";
}
