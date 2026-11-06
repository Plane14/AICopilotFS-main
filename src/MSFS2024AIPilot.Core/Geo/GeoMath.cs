using System;

namespace MSFS2024AIPilot.Core.Geo;

/// <summary>Great-circle and local-tangent-plane geometry helpers.</summary>
public static class GeoMath
{
    public const double EarthRadiusMeters = 6_371_000.0;
    public const double MetersPerNm = 1852.0;
    public const double FeetPerMeter = 3.280839895;
    public const double MetersPerFoot = 0.3048;
    public const double KnotsToMps = 0.514444;
    public const double MpsToKnots = 1.0 / KnotsToMps;
    public const double FpmToMps = 0.00508;
    public const double MpsToFpm = 196.8504;

    public static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    /// <summary>Smallest signed difference a-b in degrees, in [-180, 180).</summary>
    public static double AngleDelta(double a, double b)
    {
        var d = NormalizeDeg(a) - NormalizeDeg(b);
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a))) / MetersPerNm;
    }

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        => DistanceNm(lat1, lon1, lat2, lon2) * MetersPerNm;

    /// <summary>Initial great-circle bearing from point 1 to point 2, degrees true.</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);
        return NormalizeDeg(ToDeg(Math.Atan2(y, x)));
    }

    /// <summary>Destination point given start, bearing (deg true) and distance (m).</summary>
    public static (double lat, double lon) Destination(double lat, double lon, double bearingDeg, double distanceMeters)
    {
        var d = distanceMeters / EarthRadiusMeters;
        var br = ToRad(bearingDeg);
        var lat1 = ToRad(lat);
        var lon1 = ToRad(lon);
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(br));
        var lon2 = lon1 + Math.Atan2(Math.Sin(br) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return (ToDeg(lat2), NormalizeDeg(ToDeg(lon2)));
    }

    /// <summary>Destination point from a distance in nautical miles.</summary>
    public static (double lat, double lon) DestinationNm(double lat, double lon, double bearingDeg, double distanceNm)
        => Destination(lat, lon, bearingDeg, distanceNm * MetersPerNm);

    /// <summary>
    /// Projects a point onto a local tangent plane centred on <paramref name="originLat"/>,
    /// <paramref name="originLon"/>. Returns east/north offsets in meters.
    /// Accurate to well under a metre out to a few hundred km.
    /// </summary>
    public static (double eastM, double northM) ToLocal(double lat, double lon, double originLat, double originLon)
    {
        var dLat = ToRad(lat - originLat);
        var dLon = ToRad(lon - originLon);
        var north = dLat * EarthRadiusMeters;
        var east = dLon * EarthRadiusMeters * Math.Cos(ToRad(originLat));
        return (east, north);
    }

    /// <summary>Inverse of <see cref="ToLocal"/>: lat/lon from east/north offsets in meters.</summary>
    public static (double lat, double lon) FromLocal(double eastM, double northM, double originLat, double originLon)
    {
        var dLat = northM / EarthRadiusMeters;
        var dLon = eastM / (EarthRadiusMeters * Math.Cos(ToRad(originLat)));
        return (originLat + ToDeg(dLat), NormalizeDeg(originLon + ToDeg(dLon)));
    }

    /// <summary>
    /// Projects a point onto the segment (a)-(b) in a local tangent plane.
    /// Returns (along, across, t) where along/across are meters from a along the
    /// segment axis, t is the clamped fraction in [0,1].
    /// </summary>
    public static (double along, double across, double t) ProjectOntoSegment(
        double lat, double lon,
        double aLat, double aLon, double bLat, double bLon)
    {
        var (aEast, aNorth) = ToLocal(aLat, aLon, aLat, aLon);
        var (bEast, bNorth) = ToLocal(bLat, bLon, aLat, aLon);
        var (pEast, pNorth) = ToLocal(lat, lon, aLat, aLon);

        var dx = bEast - aEast;
        var dy = bNorth - aNorth;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6) return (0, Math.Sqrt(pEast * pEast + pNorth * pNorth), 0);

        var t = ((pEast - aEast) * dx + (pNorth - aNorth) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        var projEast = aEast + t * dx;
        var projNorth = aNorth + t * dy;
        var along = Math.Sqrt((projEast - aEast) * (projEast - aEast) + (projNorth - aNorth) * (projNorth - aNorth));
        var across = Math.Sqrt((pEast - projEast) * (pEast - projEast) + (pNorth - projNorth) * (pNorth - projNorth));
        // sign of across: positive to the right of the a->b direction
        var cross = dx * (pNorth - aNorth) - dy * (pEast - aEast);
        if (cross < 0) across = -across;
        return (along, across, t);
    }

    /// <summary>Cross-track distance in meters (signed positive right of course).</summary>
    public static double CrossTrackMeters(double lat, double lon, double courseLat1, double lon1, double courseLat2, double lon2)
    {
        var (_, across, _) = ProjectOntoSegment(lat, lon, courseLat1, lon1, courseLat2, lon2);
        return across;
    }

    public static double ToRad(double deg) => deg * Math.PI / 180.0;
    public static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
