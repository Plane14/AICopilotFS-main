using System;

namespace AICopilotFS.Domain.ValueObjects;

/// <summary>Represents a geographic position (latitude/longitude in degrees).</summary>
public readonly record struct GeoPosition(double LatitudeDegrees, double LongitudeDegrees)
{
    public static readonly GeoPosition Zero = new(0, 0);

    public double DistanceNm(GeoPosition other)
    {
        var dLat = (other.LatitudeDegrees - LatitudeDegrees) * Math.PI / 180.0;
        var dLon = (other.LongitudeDegrees - LongitudeDegrees) * Math.PI / 180.0;
        var lat1 = LatitudeDegrees * Math.PI / 180.0;
        var lat2 = other.LatitudeDegrees * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        return c * 6_371_000.0 / 1852.0;
    }

    public double BearingDegreesTo(GeoPosition other)
    {
        var dLon = (other.LongitudeDegrees - LongitudeDegrees) * Math.PI / 180.0;
        var lat1 = LatitudeDegrees * Math.PI / 180.0;
        var lat2 = other.LatitudeDegrees * Math.PI / 180.0;
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return ((bearing % 360.0) + 360.0) % 360.0;
    }

    public GeoPosition Destination(double bearingDegrees, double distanceNm)
    {
        var d = distanceNm * 1852.0 / 6_371_000.0;
        var br = bearingDegrees * Math.PI / 180.0;
        var lat1 = LatitudeDegrees * Math.PI / 180.0;
        var lon1 = LongitudeDegrees * Math.PI / 180.0;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) +
                            Math.Cos(lat1) * Math.Sin(d) * Math.Cos(br));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(br) * Math.Sin(d) * Math.Cos(lat1),
            Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoPosition(
            lat2 * 180.0 / Math.PI,
            ((lon2 * 180.0 / Math.PI) % 360.0 + 360.0) % 360.0);
    }

    public static GeoPosition Parse(string s)
    {
        var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], out var lat) ||
            !double.TryParse(parts[1], out var lon))
        {
            throw new FormatException($"Invalid GeoPosition format: '{s}'. Expected 'lat,lon'.");
        }
        return new GeoPosition(lat, lon);
    }

    public override string ToString() => $"{LatitudeDegrees:F5},{LongitudeDegrees:F5}";
}

/// <summary>Represents an altitude in feet MSL.</summary>
public readonly record struct AltitudeFeet(double Feet)
{
    public static readonly AltitudeFeet Zero = new(0);
    public static readonly AltitudeFeet Invalid = new(double.NaN);

    public bool IsValid => !double.IsNaN(Feet) && double.IsFinite(Feet);

    public override string ToString() => IsValid ? $"{Feet:F0} ft" : "Invalid";

    public static AltitudeFeet FromMeters(double meters) => new(meters * 3.280839895);
}

/// <summary>Represents a speed in knots.</summary>
public readonly record struct SpeedKnots(double Knots)
{
    public static readonly SpeedKnots Zero = new(0);
    public static readonly SpeedKnots Invalid = new(double.NaN);

    public bool IsValid => !double.IsNaN(Knots) && double.IsFinite(Knots);

    public override string ToString() => IsValid ? $"{Knots:F1} kt" : "Invalid";
}

/// <summary>Represents a heading in degrees true (0-360).</summary>
public readonly record struct HeadingDegrees(double Degrees)
{
    public static readonly HeadingDegrees Zero = new(0);

    public bool IsValid => !double.IsNaN(Degrees) && Degrees >= 0 && Degrees < 360;

    public HeadingDegrees Normalized()
    {
        var d = Degrees % 360.0;
        return new HeadingDegrees(d < 0 ? d + 360.0 : d);
    }

    public override string ToString() => IsValid ? $"{Degrees:F1}\u00B0" : "Invalid";
}

/// <summary>Represents a vertical speed in feet per minute.</summary>
public readonly record struct VerticalSpeedFpm(double Fpm)
{
    public static readonly VerticalSpeedFpm Zero = new(0);

    public bool IsValid => !double.IsNaN(Fpm) && double.IsFinite(Fpm);

    public override string ToString() => IsValid ? $"{Fpm:F0} fpm" : "Invalid";
}

/// <summary>Represents the state of a radio (COM).</summary>
public readonly record struct FrequencyMhz(double Megahertz)
{
    public static readonly FrequencyMhz Zero = new(0);

    public bool IsValid => !double.IsNaN(Megahertz) && Megahertz > 0 && Megahertz < 1000;

    public override string ToString() => IsValid ? $"{Megahertz:F3} MHz" : "Invalid";
}

/// <summary>Represents a transponder squawk code.</summary>
public readonly record struct SquawkCode(string Code)
{
    public static readonly SquawkCode Conspicuity = new("1200");
    public static readonly SquawkCode Emergency = new("7700");
    public static readonly SquawkCode UnlawfulInterference = new("7500");
    public static readonly SquawkCode CommunicationsFailure = new("7600");

    public static SquawkCode FromDecimal(int code)
    {
        var digits = code is >= 0 and <= 9999
            ? code.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        return TryCreate(digits, out var result) ? result : Conspicuity;
    }

    public static bool TryCreate(string code, out SquawkCode result)
    {
        result = Conspicuity;
        if (string.IsNullOrWhiteSpace(code) || code.Length != 4)
            return false;
        foreach (var ch in code)
        {
            if (ch < '0' || ch > '7')
                return false;
        }
        if (code[0] < '1' || code[0] > '6')
            return false;
        if (code == "7500" || code == "7600" || code == "7700")
            return false;
        result = new SquawkCode(code);
        return true;
    }

    public override string ToString() => Code;
}
