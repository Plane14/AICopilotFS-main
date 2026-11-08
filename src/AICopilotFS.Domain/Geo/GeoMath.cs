namespace AICopilotFS.Domain.Geo;

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

    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(lat2) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);
        return NormalizeDeg(ToDeg(Math.Atan2(y, x)));
    }

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

    public static (double lat, double lon) DestinationNm(double lat, double lon, double bearingDeg, double distanceNm)
        => Destination(lat, lon, bearingDeg, distanceNm * MetersPerNm);

    public static (double eastM, double northM) ToLocal(double lat, double lon, double originLat, double originLon)
    {
        var dLat = ToRad(lat - originLat);
        var dLon = ToRad(lon - originLon);
        var north = dLat * EarthRadiusMeters;
        var east = dLon * EarthRadiusMeters * Math.Cos(ToRad(originLat));
        return (east, north);
    }

    public static (double lat, double lon) FromLocal(double eastM, double northM, double originLat, double originLon)
    {
        var dLat = northM / EarthRadiusMeters;
        var dLon = eastM / (EarthRadiusMeters * Math.Cos(ToRad(originLat)));
        return (originLat + ToDeg(dLat), NormalizeDeg(originLon + ToDeg(dLon)));
    }

    public static double ToRad(double deg) => deg * Math.PI / 180.0;
    public static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
