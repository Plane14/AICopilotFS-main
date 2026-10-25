using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

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
    public int? RunwayNumber { get; set; }
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
    RunwayEnd
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

        /// <summary>
        /// Primary runway threshold position (latitude/longitude). Null when the airport
        /// data does not publish a displaced threshold for this end.
        /// </summary>
        public (double lat, double lon)? PrimaryThreshold { get; set; }
        /// <summary>
        /// Secondary runway threshold position (latitude/longitude). Null when the airport
        /// data does not publish a displaced threshold for this end.
        /// </summary>
        public (double lat, double lon)? SecondaryThreshold { get; set; }

        public string PrimaryName => RunwayName(PrimaryNumber, PrimaryDesignator);
        public string SecondaryName => RunwayName(SecondaryNumber, SecondaryDesignator);

    /// <summary>
    /// The runway records report the centre of the runway, not the thresholds, so the
    /// ends have to be derived: half the length either way along the true heading.
    /// These are the coordinates a pilot lines up on and a traffic monitor measures
    /// runway occupancy against.
    /// </summary>
    public (double lat, double lon) PrimaryEnd => EndOffset(-LengthMeters / 2);
    public (double lat, double lon) SecondaryEnd => EndOffset(LengthMeters / 2);

    /// <summary>
    /// Offsets a point along the runway by <paramref name="meters"/> from its centre,
    /// measured along the true heading. Negative is towards the primary end.
    /// </summary>
    private (double lat, double lon) EndOffset(double meters)
    {
        if (LengthMeters <= 0 || double.IsNaN(Latitude) || double.IsNaN(Longitude)) return (Latitude, Longitude);
        var heading = Geo.ToRadians(HeadingDegTrue);
        var east = Math.Sin(heading) * meters;
        var north = Math.Cos(heading) * meters;
        return Geo.OffsetByMeters(Latitude, Longitude, east, north);
    }

    /// <summary>
    /// The threshold of the end this runway identifies itself by, e.g. "13" or "31L".
    /// A runway only matches on one of its two names, so the threshold of the named end
    /// is the one that matters - lining up on the wrong end would point the aircraft
    /// the wrong way down the runway.
    /// </summary>
    public (double lat, double lon)? ThresholdFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().ToUpperInvariant();
        if (PrimaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return PrimaryEnd;
        if (SecondaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return SecondaryEnd;
        return null;
    }

    /// <summary>
    /// Formats a runway end the same way a .pln and ATC instructions spell it:
    /// two-digit number plus optional designator, e.g. "06" or "25L".
    /// </summary>
    public static string RunwayName(int number, int designator)
    {
        if (number is <= 0 or > 45) return "";

        // 37..45 are compass directions for helipads and water runways.
        if (number > 36)
        {
            return ((number - 36) switch
            {
                1 => "NORTH",
                2 => "NORTHEAST",
                3 => "EAST",
                4 => "SOUTHEAST",
                5 => "SOUTH",
                6 => "SOUTHWEST",
                7 => "WEST",
                8 => "NORTHWEST",
                _ => "LAST"
            }) + Designator(designator);
        }

        var suffix = Designator(designator);
        return $"{number:00}{suffix}";
    }

    private static string Designator(int designator) => designator switch
    {
        1 => "L",
        2 => "R",
        3 => "C",
        4 => "W",
        5 => "A",
        6 => "B",
        _ => ""
    };

    /// <summary>Returns the runway end matching <paramref name="name"/> e.g. "25L".</summary>
    public bool Matches(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Trim().ToUpperInvariant();
        return PrimaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               SecondaryName.Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }
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

    /// <summary>
    /// Fraction of taxiway paths whose both endpoints resolved to known nodes.
    /// A value well below 1 indicates the taxi point / parking index namespaces do
    /// not line up and routing will be incomplete.
    /// </summary>
    public double PathResolutionRatio { get; set; }

    public GroundNode? FindNode(int index) => Nodes.TryGetValue(index, out var n) ? n : null;

    public RunwayInfo? FindRunway(string name) =>
        Runways.FirstOrDefault(r => r.Matches(name)) ?? Runways.FirstOrDefault(r => r.PrimaryName == name || r.SecondaryName == name);

    /// <summary>
    /// The airport reference frame is only known to the simulator. Taxi points and
    /// parking spots arrive as biasX/biasZ offsets in meters from the airport
    /// reference point on the longitudinal and latitudinal axes, and the docs do not
    /// state which geographic direction those axes point. Rather than guessing, the
    /// frame is calibrated against the aircraft's own position (see
    /// <see cref="CalibrateFrame"/>), which is unambiguous because the aircraft is
    /// standing somewhere on the airport.
    /// </summary>
    public BiasFrame Frame { get; set; } = BiasFrame.XEastZNorth;
}

/// <summary>
/// Candidate orientations for the airport local frame. The longitudinal axis (biasX)
/// and latitudinal axis (biasZ) map to a pair of cardinal directions.
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
    Unknown
}

public static class BiasFrameExtensions
{
    /// <summary>Converts a bias pair into a meter offset in (east, north).</summary>
    public static (double east, double north) ToEastNorth(this BiasFrame frame, double biasX, double biasZ)
    {
        return frame switch
        {
            BiasFrame.XEastZNorth => (biasX, biasZ),
            BiasFrame.XEastZSouth => (biasX, -biasZ),
            BiasFrame.XWestZNorth => (-biasX, biasZ),
            BiasFrame.XWestZSouth => (-biasX, -biasZ),
            BiasFrame.XNorthZEast => (biasZ, biasX),
            BiasFrame.XNorthZWest => (-biasZ, biasX),
            BiasFrame.XSouthZEast => (biasZ, -biasX),
            BiasFrame.XSouthZWest => (-biasZ, -biasX),
            _ => (biasX, biasZ)
        };
    }
}