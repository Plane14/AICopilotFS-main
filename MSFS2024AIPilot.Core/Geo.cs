using System;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Geodetic helpers. A spherical earth with a local meters-per-degree scaling is
/// accurate to well under a metre at airport scale and avoids the cost of a full
/// WGS84 solution on every graph edge.
/// </summary>
public static class Geo
{
    private const double EarthRadiusMeters = 6371008.8;
    private const double MetersPerNauticalMile = 1852.0;

    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    public static double DistanceNauticalMiles(double lat1, double lon1, double lat2, double lon2)
        => DistanceMeters(lat1, lon1, lat2, lon2) / MetersPerNauticalMile;

    /// <summary>Initial great-circle bearing in degrees true from point 1 to point 2.</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRadians(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRadians(lat2));
        var x = Math.Cos(ToRadians(lat1)) * Math.Sin(ToRadians(lat2)) -
                Math.Sin(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Cos(dLon);
        var bearing = ToDegrees(Math.Atan2(y, x));
        return bearing < 0 ? bearing + 360.0 : bearing;
    }

    /// <summary>Normalises an angle to the range (-180, 180].</summary>
    public static double NormalizeDegrees(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees <= -180) degrees += 360;
        return degrees;
    }

    /// <summary>
    /// Offsets a position by a meter displacement in the east and north directions.
    /// Used to convert airport-local bias coordinates into latitude/longitude.
    /// </summary>
    public static (double lat, double lon) OffsetByMeters(double lat, double lon, double east, double north)
    {
        var metersPerDegLat = 111132.92 - 559.82 * Math.Cos(2 * ToRadians(lat)) + 1.175 * Math.Cos(4 * ToRadians(lat));
        var metersPerDegLon = 111412.84 * Math.Cos(ToRadians(lat)) - 93.5 * Math.Cos(3 * ToRadians(lat));

        // The longitude scale collapses to zero at the poles, so a garbage latitude would
        // otherwise divide by (near) zero and return an infinite longitude. Floor it so a
        // bad input degrades gracefully rather than poisoning every later distance.
        var lonScale = Math.Max(metersPerDegLon, 1.0);

        return (lat + north / metersPerDegLat, lon + east / lonScale);
    }

    /// <summary>
    /// Where a point falls along the straight segment from a to b. <paramref name="along"/>
    /// is the distance in metres measured from a towards b - negative is short of a, and
    /// beyond the segment length is past b - and <paramref name="offset"/> is how far the
    /// point is off the line. The pair is what tells a point genuinely on a runway from one
    /// beside it on a taxiway, which is the question a back-taxi decision has to answer.
    /// </summary>
    public static (double along, double offset) ProjectOntoSegment(
        double lat, double lon, double aLat, double aLon, double bLat, double bLon)
    {
        var along = BearingDegrees(aLat, aLon, bLat, bLon);
        var toPoint = BearingDegrees(aLat, aLon, lat, lon);
        var distance = DistanceMeters(aLat, aLon, lat, lon);
        var angle = ToRadians(toPoint - along);

        return (distance * Math.Cos(angle), Math.Abs(distance * Math.Sin(angle)));
    }

    /// <summary>Converts meters to feet.</summary>
    public static double MetersToFeet(double meters) => meters * 3.280839895;

    /// <summary>Converts feet to meters.</summary>
    public static double FeetToMeters(double feet) => feet / 3.280839895;
}