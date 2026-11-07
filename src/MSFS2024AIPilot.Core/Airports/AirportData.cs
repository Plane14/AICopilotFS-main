using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Geo;

namespace MSFS2024AIPilot.Core.Airports;

/// <summary>A point on the airport ground network. Positions are geographic.</summary>
public sealed class GroundNode
{
    public int Index { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }
    public GroundNodeType Type { get; set; }
    public int Orientation { get; set; }
    public double HeadingDegTrue { get; set; }

    /// <summary>Set for runway nodes: the runway end this node belongs to.</summary>
    public int RunwayNumber { get; set; }
    public int RunwayDesignator { get; set; }

    /// <summary>Set for parking nodes.</summary>
    public string? ParkingName { get; set; }
    public int ParkingNumber { get; set; }
    public int ParkingType { get; set; }
    public int TaxiPointType { get; set; }
    public double ParkingRadiusMeters { get; set; }

    /// <summary>Raw bias coordinates as reported by the simulator, kept for frame calibration.</summary>
    public double BiasX { get; set; }
    public double BiasZ { get; set; }

    public List<GroundEdge> Edges { get; } = new();
}

public enum GroundNodeType
{
    Normal,
    HoldShort,
    IlsHoldShort,
    Parking,
    RunwayEnd,
}

public sealed class GroundEdge
{
    public int From { get; set; }
    public int To { get; set; }
    public double LengthMeters { get; set; }
    public TaxiPathType PathType { get; set; }
    public double WidthMeters { get; set; }
    public int RunwayNumber { get; set; }
    public int RunwayDesignator { get; set; }
    public int NameIndex { get; set; }

    /// <summary>Published weight limit in pounds; 0 means unlimited.</summary>
    public uint WeightLimitLbs { get; set; }
}

public enum TaxiPathType
{
    None = 0,
    Taxi = 1,
    Runway = 2,
    Parking = 3,
    Path = 4,
    Closed = 5,
    Vehicle = 6,
    Road = 7,
    PaintedLine = 8,
}

public sealed class RunwayInfo
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }
    public double HeadingDegTrue { get; set; }
    public double LengthMeters { get; set; }
    public double WidthMeters { get; set; }
    public double PatternAltitudeMeters { get; set; }
    public double SlopeDegrees { get; set; }
    public double TrueSlopeDegrees { get; set; }
    public int Surface { get; set; }

    public int PrimaryNumber { get; set; }
    public int PrimaryDesignator { get; set; }
    public int SecondaryNumber { get; set; }
    public int SecondaryDesignator { get; set; }

    public string PrimaryIlsIcao { get; set; } = "";
    public string PrimaryIlsRegion { get; set; } = "";
    public char PrimaryIlsType { get; set; } = 'W';
    public string SecondaryIlsIcao { get; set; } = "";
    public string SecondaryIlsRegion { get; set; } = "";
    public char SecondaryIlsType { get; set; } = 'W';

    public int EdgeLights { get; set; }
    public int CenterLights { get; set; }
    public bool PrimaryClosed { get; set; }
    public bool SecondaryClosed { get; set; }
    public bool PrimaryTakeoff { get; set; }
    public bool PrimaryLanding { get; set; }
    public bool SecondaryTakeoff { get; set; }
    public bool SecondaryLanding { get; set; }

    public (double lat, double lon)? PrimaryThreshold { get; set; }
    public (double lat, double lon)? SecondaryThreshold { get; set; }

    public string PrimaryName => RunwayName(PrimaryNumber, PrimaryDesignator);
    public string SecondaryName => RunwayName(SecondaryNumber, SecondaryDesignator);

    /// <summary>
    /// Runway records report the centre, not the thresholds, so the ends are derived:
    /// half the length either way along the true heading.
    /// </summary>
    public (double lat, double lon) PrimaryEnd => EndOffset(-LengthMeters / 2);
    public (double lat, double lon) SecondaryEnd => EndOffset(LengthMeters / 2);

    private (double lat, double lon) EndOffset(double meters)
    {
        if (LengthMeters <= 0 || double.IsNaN(Latitude) || double.IsNaN(Longitude))
            return (Latitude, Longitude);
        var heading = GeoMath.ToRad(HeadingDegTrue);
        return GeoMath.FromLocal(Math.Sin(heading) * meters, Math.Cos(heading) * meters, Latitude, Longitude);
    }

    /// <summary>
    /// The threshold of the end this runway identifies itself by, e.g. "13" or "31L".
    /// A runway only matches on one of its two names.
    /// </summary>
    public (double lat, double lon)? ThresholdFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().ToUpperInvariant();
        if (PrimaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return PrimaryEnd;
        if (SecondaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return SecondaryEnd;
        return null;
    }

    /// <summary>Formats a runway end as in a .pln: two-digit number plus optional designator.</summary>
    public static string RunwayName(int number, int designator)
    {
        if (number is <= 0 or > 45) return "";
        if (number > 36)
        {
            return (number - 36) switch
            {
                1 => "NORTH", 2 => "NORTHEAST", 3 => "EAST", 4 => "SOUTHEAST",
                5 => "SOUTH", 6 => "SOUTHWEST", 7 => "WEST", 8 => "NORTHWEST",
                _ => "LAST"
            } + Designator(designator);
        }
        return $"{number:00}{Designator(designator)}";
    }

    private static string Designator(int designator) => designator switch
    {
        1 => "L", 2 => "R", 3 => "C", 4 => "W", 5 => "A", 6 => "B", _ => ""
    };

    public bool Matches(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim().ToUpperInvariant();
        return PrimaryName.Equals(n, StringComparison.OrdinalIgnoreCase) ||
               SecondaryName.Equals(n, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Official facility SURFACE enum water values (FSX water = 2; MSFS adds
    /// ocean/water/pond/lake/river/waste-water = 26–31). The WATER runway
    /// designator (4) is accepted as a secondary signal.
    /// </summary>
    public bool IsWater =>
        Surface is 2 or >= 26 and <= 31 ||
        PrimaryDesignator == 4 || SecondaryDesignator == 4;
}

public static class RunwaySurface
{
    public const int Concrete = 0;
    public const int Grass = 1;
    public const int Water = 2;
    public const int GrassBumpy = 3;
    public const int Asphalt = 4;
    public const int Ocean = 26;
    public const int Pond = 28;
    public const int Lake = 29;
    public const int River = 30;
    public const int Unknown = 254;
    public const int Undefined = 255;
}

public sealed class AirportData
{
    public string Icao { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";

    public double ReferenceLatitude { get; set; }
    public double ReferenceLongitude { get; set; }
    public double ReferenceAltitudeMeters { get; set; }
    public float MagneticVariationDeg { get; set; }
    public float TransitionAltitudeMeters { get; set; }
    public float TransitionLevelMeters { get; set; }
    public bool IsClosed { get; set; }

    public Dictionary<int, GroundNode> Nodes { get; } = new();
    public List<RunwayInfo> Runways { get; } = new();
    public List<string> TaxiNames { get; } = new();

    public bool IsLoaded { get; set; }

    /// <summary>Fraction of taxiway paths whose endpoints resolved to known nodes.</summary>
    public double PathResolutionRatio { get; set; }

    /// <summary>
    /// The airport reference frame is only known to the simulator. Taxi points and
    /// parking spots arrive as biasX/biasZ offsets whose axis directions are not
    /// documented, so the frame is calibrated against the aircraft's own position.
    /// </summary>
    public BiasFrame Frame { get; set; } = BiasFrame.XEastZNorth;

    public GroundNode? FindNode(int index) => Nodes.TryGetValue(index, out var n) ? n : null;

    public RunwayInfo? FindRunway(string name) =>
        Runways.FirstOrDefault(r => r.Matches(name)) ??
        Runways.FirstOrDefault(r => r.PrimaryName == name || r.SecondaryName == name);

    public IEnumerable<GroundNode> ParkingNodes =>
        Nodes.Values.Where(n => n.Type == GroundNodeType.Parking);

    public IEnumerable<GroundNode> HoldShortNodes =>
        Nodes.Values.Where(n => n.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort);

    /// <summary>Runways whose declared surface is water (seaplane lanes).</summary>
    public IEnumerable<RunwayInfo> WaterRunways => Runways.Where(r => r.IsWater);

    public bool HasWaterRunway => Runways.Any(r => r.IsWater);

    public bool HasRunways => Runways.Count > 0;
}

/// <summary>
/// Candidate orientations for the airport local frame: the longitudinal axis
/// (biasX) and latitudinal axis (biasZ) map to a pair of cardinal directions.
/// </summary>
public enum BiasFrame
{
    XEastZNorth,
    XEastZSouth,
    XWestZNorth,
    XWestZSouth,
    XNorthZEast,
    XNorthZWest,
    XSouthZEast,
    XSouthZWest,
    Unknown,
}

public static class BiasFrameExtensions
{
    /// <summary>Converts a bias pair into a meter offset in (east, north).</summary>
    public static (double east, double north) ToEastNorth(this BiasFrame frame, double biasX, double biasZ) => frame switch
    {
        BiasFrame.XEastZNorth => (biasX, biasZ),
        BiasFrame.XEastZSouth => (biasX, -biasZ),
        BiasFrame.XWestZNorth => (-biasX, biasZ),
        BiasFrame.XWestZSouth => (-biasX, -biasZ),
        BiasFrame.XNorthZEast => (biasZ, biasX),
        BiasFrame.XNorthZWest => (-biasZ, biasX),
        BiasFrame.XSouthZEast => (biasZ, -biasX),
        BiasFrame.XSouthZWest => (-biasZ, -biasX),
        _ => (biasX, biasZ),
    };
}
