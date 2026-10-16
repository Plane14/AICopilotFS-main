using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Loads airport facility data (runways, taxiway points, taxiway paths and parking
/// spots) through the SimConnect facilities API and assembles it into an
/// <see cref="AirportData"/> graph usable for taxi routing.
/// </summary>
public sealed class AirportFacilitiesLoader : IDisposable
{
    // Definition and request IDs. SimConnect reuses the definition until it is
    // cleared, so one dedicated ID per airport request is used.
    private const uint AirportDefineId = 7001;
    private const uint AirportRequestId = 7101;

    private readonly SimConnectManager _sc;
    private readonly Dictionary<uint, PendingAirportRequest> _pending = new();
    private uint _nextUniqueRequestId = 7200;

    public bool IsAvailable => _sc.IsConnected;

    public event Action<AirportData>? OnAirportLoaded;
    public event Action<string>? OnError;

    public AirportFacilitiesLoader(SimConnectManager sc)
    {
        _sc = sc;
        _sc.OnFacilityData += OnFacilityData;
        _sc.OnFacilityDataEnd += OnFacilityDataEnd;
    }

    /// <summary>
    /// Requests airport data for <paramref name="icao"/>. The request completes
    /// asynchronously and raises <see cref="OnAirportLoaded"/>.
    /// </summary>
    public void RequestAirport(string icao)
    {
        if (!_sc.IsConnected)
        {
            OnError?.Invoke("SimConnect is not connected; cannot request airport data.");
            return;
        }

        // The definition only needs to be built once; SimConnect retains it.
        if (!_definitionBuilt)
        {
            BuildAirportDefinition();
            _definitionBuilt = true;
        }

        var requestId = _nextUniqueRequestId++;
        _pending[requestId] = new PendingAirportRequest { Icao = icao.ToUpperInvariant() };

        _sc.RequestFacilityData(AirportDefineId, requestId, icao);
    }

    private bool _definitionBuilt;

    /// <summary>
    /// Builds the facility data definition. Field order matters: the payload of each
    /// record is laid out in exactly this order, and every OPEN needs a CLOSE.
    /// </summary>
    private void BuildAirportDefinition()
    {
        var def = AirportDefineId;

        _sc.AddToFacilityDefinition(def, "OPEN AIRPORT");
        // The airport payload order is the one the MSFS 2024 docs publish for the AIRPORT
        // entry point, field by field. LATITUDE comes first, so a definition that omitted
        // it left ParseAirport reading the longitude as the latitude and every
        // calibrated orientation wrong.
        _sc.AddToFacilityDefinition(def, "LATITUDE");
        _sc.AddToFacilityDefinition(def, "LONGITUDE");
        _sc.AddToFacilityDefinition(def, "ALTITUDE");
        _sc.AddToFacilityDefinition(def, "MAGVAR");
        _sc.AddToFacilityDefinition(def, "NAME");
        _sc.AddToFacilityDefinition(def, "NAME64");
        _sc.AddToFacilityDefinition(def, "ICAO");
        _sc.AddToFacilityDefinition(def, "REGION");
        _sc.AddToFacilityDefinition(def, "TOWER_LATITUDE");
        _sc.AddToFacilityDefinition(def, "TOWER_LONGITUDE");
        _sc.AddToFacilityDefinition(def, "TOWER_ALTITUDE");
        _sc.AddToFacilityDefinition(def, "TRANSITION_ALTITUDE");
        _sc.AddToFacilityDefinition(def, "TRANSITION_LEVEL");
        _sc.AddToFacilityDefinition(def, "IS_CLOSED");
        _sc.AddToFacilityDefinition(def, "COUNTRY");
        _sc.AddToFacilityDefinition(def, "CITY_STATE");
        _sc.AddToFacilityDefinition(def, "N_RUNWAYS");
        _sc.AddToFacilityDefinition(def, "N_STARTS");
        _sc.AddToFacilityDefinition(def, "N_FREQUENCIES");
        _sc.AddToFacilityDefinition(def, "N_HELIPADS");
        _sc.AddToFacilityDefinition(def, "N_APPROACHES");
        _sc.AddToFacilityDefinition(def, "N_DEPARTURES");
        _sc.AddToFacilityDefinition(def, "N_ARRIVALS");
        _sc.AddToFacilityDefinition(def, "N_TAXI_POINTS");
        _sc.AddToFacilityDefinition(def, "N_TAXI_PARKINGS");
        _sc.AddToFacilityDefinition(def, "N_TAXI_PATHS");
        _sc.AddToFacilityDefinition(def, "N_TAXI_NAMES");
        _sc.AddToFacilityDefinition(def, "N_JETWAYS");
        _sc.AddToFacilityDefinition(def, "N_VDGS");
        _sc.AddToFacilityDefinition(def, "N_HOLDING_PATTERNS");
        _sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");

        // Runways: needed for heading, length and runway-end identification.
        _sc.AddToFacilityDefinition(def, "OPEN RUNWAY");
        _sc.AddToFacilityDefinition(def, "LATITUDE");
        _sc.AddToFacilityDefinition(def, "LONGITUDE");
        _sc.AddToFacilityDefinition(def, "ALTITUDE");
        _sc.AddToFacilityDefinition(def, "HEADING");
        _sc.AddToFacilityDefinition(def, "LENGTH");
        _sc.AddToFacilityDefinition(def, "WIDTH");
        _sc.AddToFacilityDefinition(def, "PATTERN_ALTITUDE");
        _sc.AddToFacilityDefinition(def, "SLOPE");
        _sc.AddToFacilityDefinition(def, "TRUE_SLOPE");
        _sc.AddToFacilityDefinition(def, "SURFACE");
        _sc.AddToFacilityDefinition(def, "PRIMARY_ILS_ICAO");
        _sc.AddToFacilityDefinition(def, "PRIMARY_ILS_REGION");
        _sc.AddToFacilityDefinition(def, "PRIMARY_ILS_TYPE");
        _sc.AddToFacilityDefinition(def, "PRIMARY_NUMBER");
        _sc.AddToFacilityDefinition(def, "PRIMARY_DESIGNATOR");
        _sc.AddToFacilityDefinition(def, "SECONDARY_ILS_ICAO");
        _sc.AddToFacilityDefinition(def, "SECONDARY_ILS_REGION");
        _sc.AddToFacilityDefinition(def, "SECONDARY_ILS_TYPE");
        _sc.AddToFacilityDefinition(def, "SECONDARY_NUMBER");
        _sc.AddToFacilityDefinition(def, "SECONDARY_DESIGNATOR");
        _sc.AddToFacilityDefinition(def, "EDGE_LIGHTS");
        _sc.AddToFacilityDefinition(def, "CENTER_LIGHTS");
        _sc.AddToFacilityDefinition(def, "PRIMARY_CLOSED");
        _sc.AddToFacilityDefinition(def, "SECONDARY_CLOSED");
        _sc.AddToFacilityDefinition(def, "PRIMARY_TAKEOFF");
        _sc.AddToFacilityDefinition(def, "PRIMARY_LANDING");
        _sc.AddToFacilityDefinition(def, "SECONDARY_TAKEOFF");
        _sc.AddToFacilityDefinition(def, "SECONDARY_LANDING");
        _sc.AddToFacilityDefinition(def, "CLOSE RUNWAY");

        // Taxiway names, used to render ATC taxi route strings.
        _sc.AddToFacilityDefinition(def, "OPEN TAXI_NAME");
        _sc.AddToFacilityDefinition(def, "NAME");
        _sc.AddToFacilityDefinition(def, "CLOSE TAXI_NAME");

        // Taxiway points: the graph vertices.
        _sc.AddToFacilityDefinition(def, "OPEN TAXI_POINT");
        _sc.AddToFacilityDefinition(def, "TYPE");
        _sc.AddToFacilityDefinition(def, "ORIENTATION");
        _sc.AddToFacilityDefinition(def, "BIAS_X");
        _sc.AddToFacilityDefinition(def, "BIAS_Z");
        _sc.AddToFacilityDefinition(def, "CLOSE TAXI_POINT");

        // Parking spots: gates and ramp positions.
        _sc.AddToFacilityDefinition(def, "OPEN TAXI_PARKING");
        _sc.AddToFacilityDefinition(def, "TYPE");
        _sc.AddToFacilityDefinition(def, "TAXI_POINT_TYPE");
        _sc.AddToFacilityDefinition(def, "NAME");
        _sc.AddToFacilityDefinition(def, "SUFFIX");
        _sc.AddToFacilityDefinition(def, "NUMBER");
        _sc.AddToFacilityDefinition(def, "ORIENTATION");
        _sc.AddToFacilityDefinition(def, "HEADING");
        _sc.AddToFacilityDefinition(def, "RADIUS");
        _sc.AddToFacilityDefinition(def, "BIAS_X");
        _sc.AddToFacilityDefinition(def, "BIAS_Z");
        _sc.AddToFacilityDefinition(def, "CLOSE TAXI_PARKING");

        // Taxiway paths: the graph edges, expressed as start/end point indices.
        _sc.AddToFacilityDefinition(def, "OPEN TAXI_PATH");
        _sc.AddToFacilityDefinition(def, "TYPE");
        _sc.AddToFacilityDefinition(def, "WIDTH");
        _sc.AddToFacilityDefinition(def, "LEFT_HALF_WIDTH");
        _sc.AddToFacilityDefinition(def, "RIGHT_HALF_WIDTH");
        _sc.AddToFacilityDefinition(def, "WEIGHT");
        _sc.AddToFacilityDefinition(def, "RUNWAY_NUMBER");
        _sc.AddToFacilityDefinition(def, "RUNWAY_DESIGNATOR");
        _sc.AddToFacilityDefinition(def, "LEFT_EDGE");
        _sc.AddToFacilityDefinition(def, "LEFT_EDGE_LIGHTED");
        _sc.AddToFacilityDefinition(def, "RIGHT_EDGE");
        _sc.AddToFacilityDefinition(def, "RIGHT_EDGE_LIGHTED");
        _sc.AddToFacilityDefinition(def, "CENTER_LINE");
        _sc.AddToFacilityDefinition(def, "CENTER_LINE_LIGHTED");
        _sc.AddToFacilityDefinition(def, "START");
        _sc.AddToFacilityDefinition(def, "END");
        _sc.AddToFacilityDefinition(def, "NAME_INDEX");
        _sc.AddToFacilityDefinition(def, "CLOSE TAXI_PATH");

        _sc.AddToFacilityDefinition(def, "CLOSE AIRPORT");
    }

    private void OnFacilityData(FacilityDataRecord rec)
    {
        if (!_pending.TryGetValue(rec.UserRequestId, out var req)) return;

        try
        {
            switch (rec.Type)
            {
                case FacilityDataType.Airport:
                    ParseAirport(rec, req);
                    break;
                case FacilityDataType.Runway:
                    ParseRunway(rec, req);
                    break;
                case FacilityDataType.TaxiName:
                    ParseTaxiName(rec, req);
                    break;
                case FacilityDataType.TaxiPoint:
                    ParseTaxiPoint(rec, req);
                    break;
                case FacilityDataType.TaxiParking:
                    ParseTaxiParking(rec, req);
                    break;
                case FacilityDataType.TaxiPath:
                    ParseTaxiPath(rec, req);
                    break;
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to parse facility record {rec.Type}: {ex.Message}");
        }
    }

    private void OnFacilityDataEnd(uint userRequestId)
    {
        if (!_pending.TryGetValue(userRequestId, out var req)) return;

        try
        {
            ResolveNodePositions(req);
            BuildEdges(req);
            var airport = req.Airport;
            airport.IsLoaded = airport.Nodes.Count > 0;
            _pending.Remove(userRequestId);
            OnAirportLoaded?.Invoke(airport);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to finalise airport data: {ex.Message}");
        }
    }

    #region Record parsers

    private static void ParseAirport(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var a = req.Airport;
        // The airport payload order is the one the MSFS 2024 docs publish for the AIRPORT
        // entry point, field by field. LATITUDE comes first, so a definition that omitted
        // it left ParseAirport reading the longitude as the latitude and every
        // calibrated orientation wrong.
        a.ReferenceLatitude = r.ReadFloat64();
        a.ReferenceLongitude = r.ReadFloat64();
        a.ReferenceAltitudeMeters = r.ReadFloat64();
        a.MagneticVariationDeg = r.ReadFloat32();
        a.Name = r.ReadString(64);
        r.ReadString(64); // NAME64 - the same name in the 64-char form
        a.Icao = r.ReadString(8);
        a.Region = r.ReadString(8);
        r.ReadFloat64(); // TOWER_LATITUDE
        r.ReadFloat64(); // TOWER_LONGITUDE
        r.ReadFloat64(); // TOWER_ALTITUDE
        a.TransitionAltitudeMeters = r.ReadFloat32();
        a.TransitionLevelMeters = r.ReadFloat32();
        a.IsClosed = r.ReadInt8() != 0;
        r.ReadString(256); // COUNTRY
        r.ReadString(256); // CITY_STATE
        r.ReadInt32(); // N_RUNWAYS
        r.ReadInt32(); // N_STARTS
        r.ReadInt32(); // N_FREQUENCIES
        r.ReadInt32(); // N_HELIPADS
        r.ReadInt32(); // N_APPROACHES
        r.ReadInt32(); // N_DEPARTURES
        r.ReadInt32(); // N_ARRIVALS
        r.ReadInt32(); // N_TAXI_POINTS
        r.ReadInt32(); // N_TAXI_PARKINGS
        r.ReadInt32(); // N_TAXI_PATHS
        r.ReadInt32(); // N_TAXI_NAMES
        r.ReadInt32(); // N_JETWAYS
        r.ReadInt32(); // N_VDGS
        r.ReadInt32(); // N_HOLDING_PATTERNS
    }

    private static void ParseRunway(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        // The runway payload order is the one the MSFS 2024 docs publish for the RUNWAY
        // child of AIRPORT, field by field. LATITUDE comes first, so a definition that
        // omitted it left ParseRunway reading the longitude as the latitude and every
        // runway position wrong.
        var primaryIlsRegion = r.ReadString(8);
        var primaryIlsType = (char)r.ReadInt32();
        var secondaryIlsRegion = r.ReadString(8);
        var secondaryIlsType = (char)r.ReadInt32();
        var rw = new RunwayInfo
        {
            Latitude = r.ReadFloat64(),
            Longitude = r.ReadFloat64(),
            AltitudeMeters = r.ReadFloat64(),
            HeadingDegTrue = r.ReadFloat32(),
            LengthMeters = r.ReadFloat32(),
            WidthMeters = r.ReadFloat32(),
            PatternAltitudeMeters = r.ReadFloat32(),
            SlopeDegrees = r.ReadFloat32(),
            TrueSlopeDegrees = r.ReadFloat32(),
            Surface = r.ReadInt32(),
            PrimaryIlsIcao = r.ReadString(8),
            PrimaryIlsRegion = primaryIlsRegion,
            PrimaryIlsType = primaryIlsType,
            PrimaryNumber = r.ReadInt32(),
            PrimaryDesignator = r.ReadInt32(),
            SecondaryIlsIcao = r.ReadString(8),
            SecondaryIlsRegion = secondaryIlsRegion,
            SecondaryIlsType = secondaryIlsType,
            SecondaryNumber = r.ReadInt32(),
            SecondaryDesignator = r.ReadInt32(),
            EdgeLights = r.ReadInt8(),
            CenterLights = r.ReadInt8(),
            PrimaryClosed = r.ReadInt8() != 0,
            SecondaryClosed = r.ReadInt8() != 0,
            PrimaryTakeoff = r.ReadInt8() != 0,
            PrimaryLanding = r.ReadInt8() != 0,
            SecondaryTakeoff = r.ReadInt8() != 0,
            SecondaryLanding = r.ReadInt8() != 0,
        };
        req.Airport.Runways.Add(rw);
    }

    private static void ParseTaxiName(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        req.TaxiNames.Add(r.ReadString(32));
    }

    private static void ParseTaxiPoint(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var type = r.ReadInt32();
        var orientation = r.ReadInt32();
        var biasX = r.ReadFloat32();
        var biasZ = r.ReadFloat32();

        // Taxi points and parking spots share one index namespace, and path
        // START/END values refer into it, so the item index is the network index.
        req.RawTaxiPoints[(int)rec.ItemIndex] = new RawTaxiPoint
        {
            Index = (int)rec.ItemIndex,
            Type = type,
            Orientation = orientation,
            BiasX = biasX,
            BiasZ = biasZ
        };
    }

    private static void ParseTaxiParking(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var parking = new RawTaxiParking
        {
            Index = (int)rec.ItemIndex,
            Type = r.ReadInt32(),
            TaxiPointType = r.ReadInt32(),
            Name = r.ReadInt32(),
            Suffix = r.ReadInt32(),
            Number = (int)r.ReadUInt32(),
            Orientation = r.ReadInt32(),
            Heading = r.ReadFloat32(),
            Radius = r.ReadFloat32(),
            BiasX = r.ReadFloat32(),
            BiasZ = r.ReadFloat32()
        };
        req.RawTaxiParking.Add(parking);
    }

    private static void ParseTaxiPath(FacilityDataRecord rec, PendingAirportRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var path = new RawTaxiPath
        {
            Index = (int)rec.ItemIndex,
            Type = r.ReadInt32(),
            Width = r.ReadFloat32(),
            LeftHalfWidth = r.ReadFloat32(),
            RightHalfWidth = r.ReadFloat32(),
            Weight = r.ReadUInt32(),
            RunwayNumber = r.ReadInt32(),
            RunwayDesignator = r.ReadInt32(),
            LeftEdge = r.ReadInt32(),
            LeftEdgeLighted = r.ReadInt32(),
            RightEdge = r.ReadInt32(),
            RightEdgeLighted = r.ReadInt32(),
            CenterLine = r.ReadInt32(),
            CenterLineLighted = r.ReadInt32(),
            Start = r.ReadInt32(),
            End = r.ReadInt32(),
            NameIndex = (int)r.ReadUInt32()
        };
        req.RawTaxiPaths.Add(path);
    }

    #endregion

    /// <summary>
    /// Converts the raw bias coordinates into geographic positions. SimConnect reports
    /// the loaded airport reference frame in headers not published here, so the frame
    /// is resolved with <see cref="CalibrateFrame"/> when the aircraft position is
    /// known, and otherwise left as a documented default.
    /// </summary>
    private static void ResolveNodePositions(PendingAirportRequest req)
    {
        var airport = req.Airport;

        foreach (var p in req.RawTaxiPoints.Values)
        {
            airport.Nodes[p.Index] = new GroundNode
            {
                Index = p.Index,
                Type = p.Type switch
                {
                    2 or 5 => GroundNodeType.HoldShort,
                    4 or 6 => GroundNodeType.IlsHoldShort,
                    _ => GroundNodeType.Normal
                },
                Orientation = p.Orientation,
                BiasX = p.BiasX,
                BiasZ = p.BiasZ
            };
        }

        foreach (var p in req.RawTaxiParking)
        {
            // Taxi points and parking spots share one index namespace per the
            // Taxiway XML schema, so a parking spot either creates a new node or
            // augments an existing taxi point at the same location.
            if (!airport.Nodes.TryGetValue(p.Index, out var node))
            {
                node = new GroundNode { Index = p.Index };
                airport.Nodes[p.Index] = node;
            }

            // TAXI_POINT_TYPE carries the same hold-short classification as the taxi
            // point's own TYPE, so a parking record at an index that is also a hold-short
            // must not overwrite it: dropping it would silently remove a hold-short from
            // the graph and let routing drive the aircraft onto the runway.
            var holdShort = p.TaxiPointType switch
            {
                2 or 5 => GroundNodeType.HoldShort,
                4 or 6 => GroundNodeType.IlsHoldShort,
                _ => (GroundNodeType?)null
            };

            if (node.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort)
            {
                // A plain hold-short can be refined to an ILS hold-short, never the reverse.
                if (node.Type == GroundNodeType.HoldShort && holdShort == GroundNodeType.IlsHoldShort)
                    node.Type = GroundNodeType.IlsHoldShort;
            }
            else
            {
                node.Type = holdShort ?? GroundNodeType.Parking;
            }

            node.TaxiPointType = p.TaxiPointType;
            node.Orientation = p.Orientation;
            node.HeadingDegTrue = p.Heading;
            node.ParkingNumber = p.Number;
            node.ParkingType = p.Type;
            node.ParkingRadiusMeters = p.Radius;
            node.ParkingName = ParkingLabel(p.Name, p.Suffix);
            node.BiasX = p.BiasX;
            node.BiasZ = p.BiasZ;
        }

        airport.TaxiNames.AddRange(req.TaxiNames);
        ApplyBiasFrame(airport, BiasFrame.XEastZNorth);
    }

    private static string ParkingLabel(int name, int suffix)
    {
        var baseName = name switch
        {
            1 => "PARKING",
            2 => "N_PARKING",
            3 => "NE_PARKING",
            4 => "E_PARKING",
            5 => "SE_PARKING",
            6 => "S_PARKING",
            7 => "SW_PARKING",
            8 => "W_PARKING",
            9 => "NW_PARKING",
            10 => "GATE",
            11 => "DOCK",
            >= 12 and <= 37 => $"GATE_{(char)('A' + name - 12)}",
            _ => ""
        };
        return string.IsNullOrEmpty(baseName) ? "" : $"{baseName}{suffix}";
    }

    private static void ApplyBiasFrame(AirportData airport, BiasFrame frame)
    {
        airport.Frame = frame;
        foreach (var node in airport.Nodes.Values)
        {
            var (east, north) = frame.ToEastNorth(node.BiasX, node.BiasZ);
            var (lat, lon) = Geo.OffsetByMeters(
                airport.ReferenceLatitude, airport.ReferenceLongitude, east, north);
            node.Latitude = lat;
            node.Longitude = lon;
            node.AltitudeMeters = airport.ReferenceAltitudeMeters;
        }
    }

    /// <summary>
    /// Determines the airport local frame by testing each candidate orientation and
    /// keeping the one that places the graph closest to the aircraft's known position.
    /// The aircraft is standing on the airport, so the correct orientation yields a
    /// nearest-node distance of tens of metres while wrong ones land kilometres away.
    /// </summary>
    public BiasFrame CalibrateFrame(AirportData airport, double aircraftLat, double aircraftLon)
    {
        if (airport.Nodes.Count == 0) return airport.Frame;

        var best = BiasFrame.Unknown;
        var bestDistance = double.MaxValue;

        foreach (var frame in new[]
        {
            BiasFrame.XEastZNorth, BiasFrame.XEastZSouth,
            BiasFrame.XWestZNorth, BiasFrame.XWestZSouth,
            BiasFrame.XNorthZEast, BiasFrame.XNorthZWest,
            BiasFrame.XSouthZEast, BiasFrame.XSouthZWest,
        })
        {
            ApplyBiasFrame(airport, frame);
            var nearest = airport.Nodes.Values
                .Select(n => Geo.DistanceMeters(aircraftLat, aircraftLon, n.Latitude, n.Longitude))
                .DefaultIfEmpty(double.MaxValue)
                .Min();

            if (nearest < bestDistance)
            {
                bestDistance = nearest;
                best = frame;
            }
        }

        ApplyBiasFrame(airport, best);
        return best;
    }

    private static void BuildEdges(PendingAirportRequest req)
    {
        var airport = req.Airport;
        int resolvable = 0;
        int usable = 0;

        foreach (var p in req.RawTaxiPaths)
        {
            // Closed paths are not routable, so they count neither as resolved nor as
            // attempted: including them would understate the ratio on an airport that
            // publishes a lot of closed taxiway.
            if (p.Type == (int)TaxiPathType.Closed) continue;
            usable++;

            var start = airport.FindNode(p.Start);
            var end = airport.FindNode(p.End);
            if (start is null || end is null || start == end) continue;
            resolvable++;

            var length = Geo.DistanceMeters(start.Latitude, start.Longitude, end.Latitude, end.Longitude);
            if (length < 0.5) continue;

            // Taxiway paths carry no direction information, so both directions are
            // added. One-way restrictions are not part of this data set.
            start.Edges.Add(MakeEdge(p, p.Start, p.End, length));
            end.Edges.Add(MakeEdge(p, p.End, p.Start, length));

            // A taxi point payload carries no runway of its own; the runway a
            // hold-short guards is only recorded on the runway path attached to it.
            if (p.Type == (int)TaxiPathType.Runway && p.RunwayNumber > 0)
            {
                foreach (var node in new[] { start, end })
                {
                    if (node.RunwayNumber != 0 && node.RunwayNumber != p.RunwayNumber) continue;
                    node.RunwayNumber = p.RunwayNumber;
                    node.RunwayDesignator = p.RunwayDesignator;
                }
            }
        }

        // Diagnostic: a low ratio means the taxi point / parking index namespaces do
        // not line up as assumed, which would silently degrade routing.
        airport.PathResolutionRatio = usable == 0
            ? 0
            : (double)resolvable / usable;
    }

    private static GroundEdge MakeEdge(RawTaxiPath p, int from, int to, double length) => new()
    {
        From = from,
        To = to,
        LengthMeters = length,
        PathType = (TaxiPathType)p.Type,
        WidthMeters = p.Width,
        WeightLimitLbs = p.Weight,
        RunwayNumber = p.RunwayNumber,
        RunwayDesignator = p.RunwayDesignator,
        NameIndex = p.NameIndex
    };

    public void Dispose()
    {
        _sc.OnFacilityData -= OnFacilityData;
        _sc.OnFacilityDataEnd -= OnFacilityDataEnd;
    }

    private sealed class PendingAirportRequest
    {
        public string Icao { get; set; } = "";
        public AirportData Airport { get; } = new();
        public List<string> TaxiNames { get; } = new();
        public Dictionary<int, RawTaxiPoint> RawTaxiPoints { get; } = new();
        public List<RawTaxiParking> RawTaxiParking { get; } = new();
        public List<RawTaxiPath> RawTaxiPaths { get; } = new();
    }

    private sealed class RawTaxiPoint
    {
        public int Index;
        public int Type;
        public int Orientation;
        public float BiasX;
        public float BiasZ;
    }

    private sealed class RawTaxiParking
    {
        public int Index;
        public int Type;
        public int TaxiPointType;
        public int Name;
        public int Suffix;
        public int Number;
        public int Orientation;
        public float Heading;
        public float Radius;
        public float BiasX;
        public float BiasZ;
    }

    private sealed class RawTaxiPath
    {
        public int Index;
        public int Type;
        public float Width;
        public float LeftHalfWidth;
        public float RightHalfWidth;
        public uint Weight;
        public int RunwayNumber;
        public int RunwayDesignator;
        public int LeftEdge;
        public int LeftEdgeLighted;
        public int RightEdge;
        public int RightEdgeLighted;
        public int CenterLine;
        public int CenterLineLighted;
        public int Start;
        public int End;
        public int NameIndex;
    }
}