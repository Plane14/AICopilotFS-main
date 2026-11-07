using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Geo;
using MSFS2024AIPilot.Core.SimConnect;

namespace MSFS2024AIPilot.Core.Airports;

/// <summary>
/// Loads an airport's ground network through the SimConnect Facilities API:
/// runways, taxi points, parking spots and the taxiway paths that connect them.
/// </summary>
public sealed class AirportFacilityLoader : IDisposable
{
    private readonly SimConnectClient _sc;
    private uint _airportDefineId;
    private uint _nextRequestId = 50000;
    private readonly Dictionary<uint, PendingRequest> _pending = new();

    public event Action<AirportData>? OnAirportLoaded;

    /// <summary>Fires with the facility request id so callers can match the exact request.</summary>
    public event Action<uint, AirportData>? OnAirportLoadedForRequest;
    public event Action<string>? OnError;
    public SimConnectClient SimConnect => _sc;

    public AirportFacilityLoader(SimConnectClient sc)
    {
        _sc = sc;
        _sc.OnFacilityData += OnFacilityData;
        _sc.OnFacilityDataEnd += OnFacilityDataEnd;
        // NOTE: the facility definition is NOT built here — this loader is
        // constructed before the sim connection exists, and every
        // AddToFacilityDefinition send would be silently dropped, leaving a
        // definition the sim rejects with UnrecognizedId. It is built lazily
        // on the first request, which always happens after connect.
    }

    private bool _definitionSent;

    private void EnsureDefinition()
    {
        if (_definitionSent || _airportDefineId != 0) return;
        if (!_sc.IsConnected) return; // retry on the next request
        BuildDefinition();
        _definitionSent = true;
    }

    private void BuildDefinition()
    {
        _airportDefineId = _sc.CreateDataDefinition();
        uint def = _airportDefineId;
        void F(string name) => _sc.AddToFacilityDefinition(def, name);

        // AIRPORT
        F("OPEN AIRPORT");
        F("LATITUDE"); F("LONGITUDE"); F("ALTITUDE"); F("MAGVAR");
        F("NAME"); F("NAME64"); F("ICAO"); F("REGION");
        F("TOWER_LATITUDE"); F("TOWER_LONGITUDE"); F("TOWER_ALTITUDE");
        F("TRANSITION_ALTITUDE"); F("TRANSITION_LEVEL"); F("IS_CLOSED");
        F("COUNTRY"); F("CITY_STATE");
        F("N_RUNWAYS"); F("N_STARTS"); F("N_FREQUENCIES"); F("N_HELIPADS");
        F("N_APPROACHES"); F("N_DEPARTURES"); F("N_ARRIVALS");
        F("N_TAXI_POINTS"); F("N_TAXI_PARKINGS"); F("N_TAXI_PATHS"); F("N_TAXI_NAMES");
        F("N_JETWAYS"); F("N_VDGS"); F("N_HOLDING_PATTERNS");
        F("CLOSE AIRPORT");

        // RUNWAY
        F("OPEN RUNWAY");
        F("LATITUDE"); F("LONGITUDE"); F("ALTITUDE"); F("HEADING");
        F("LENGTH"); F("WIDTH"); F("PATTERN_ALTITUDE"); F("SLOPE"); F("TRUE_SLOPE");
        F("SURFACE");
        F("PRIMARY_ILS_ICAO"); F("PRIMARY_ILS_REGION"); F("PRIMARY_ILS_TYPE");
        F("PRIMARY_NUMBER"); F("PRIMARY_DESIGNATOR");
        F("SECONDARY_ILS_ICAO"); F("SECONDARY_ILS_REGION"); F("SECONDARY_ILS_TYPE");
        F("SECONDARY_NUMBER"); F("SECONDARY_DESIGNATOR");
        F("EDGE_LIGHTS"); F("CENTER_LIGHTS");
        F("PRIMARY_CLOSED"); F("SECONDARY_CLOSED");
        F("PRIMARY_TAKEOFF"); F("PRIMARY_LANDING");
        F("SECONDARY_TAKEOFF"); F("SECONDARY_LANDING");
        F("PRIMARY_THRESHOLD"); F("SECONDARY_THRESHOLD");
        F("CLOSE RUNWAY");

        // TAXI_NAME
        F("OPEN TAXI_NAME"); F("NAME"); F("CLOSE TAXI_NAME");

        // TAXI_POINT
        F("OPEN TAXI_POINT"); F("TYPE"); F("ORIENTATION"); F("BIAS_X"); F("BIAS_Z"); F("CLOSE TAXI_POINT");

        // TAXI_PARKING
        F("OPEN TAXI_PARKING"); F("TYPE"); F("TAXI_POINT_TYPE"); F("NAME"); F("SUFFIX");
        F("NUMBER"); F("ORIENTATION"); F("HEADING"); F("RADIUS"); F("BIAS_X"); F("BIAS_Z");
        F("CLOSE TAXI_PARKING");

        // TAXI_PATH
        F("OPEN TAXI_PATH"); F("TYPE"); F("WIDTH"); F("LEFT_HALF_WIDTH"); F("RIGHT_HALF_WIDTH");
        F("WEIGHT"); F("RUNWAY_NUMBER"); F("RUNWAY_DESIGNATOR");
        F("LEFT_EDGE"); F("LEFT_EDGE_LIGHTED"); F("RIGHT_EDGE"); F("RIGHT_EDGE_LIGHTED");
        F("CENTER_LINE"); F("CENTER_LINE_LIGHTED");
        F("START"); F("END"); F("NAME_INDEX");
        F("CLOSE TAXI_PATH");

        F("CLOSE AIRPORT");
    }

    /// <summary>Requests the full ground network for an airport (ICAO, e.g. "KLAX"). Returns the request id.</summary>
    public uint RequestAirport(string icao)
    {
        EnsureDefinition();
        var requestId = _nextRequestId++;
        var pending = new PendingRequest { Icao = icao };
        pending.Airport.Icao = icao;
        _pending[requestId] = pending;
        _sc.RequestFacilityData(_airportDefineId, requestId, icao);
        return requestId;
    }

    /// <summary>Requests the bare list of airports known to the sim (ICAO list).</summary>
    public uint RequestAirportList()
    {
        EnsureDefinition();
        var requestId = _nextRequestId++;
        _sc.RequestFacilitiesList(FacilityListType.Airport, requestId);
        return requestId;
    }

    private void OnFacilityData(FacilityDataRecord rec)
    {
        if (!_pending.TryGetValue(rec.UserRequestId, out var req)) return;
        try
        {
            switch (rec.Type)
            {
                case FacilityDataType.Airport: ParseAirport(rec, req); break;
                case FacilityDataType.Runway: ParseRunway(rec, req); break;
                case FacilityDataType.TaxiName: ParseTaxiName(rec, req); break;
                case FacilityDataType.TaxiPoint: ParseTaxiPoint(rec, req); break;
                case FacilityDataType.TaxiParking: ParseTaxiParking(rec, req); break;
                case FacilityDataType.TaxiPath: ParseTaxiPath(rec, req); break;
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
            OnAirportLoadedForRequest?.Invoke(userRequestId, airport);
            OnAirportLoaded?.Invoke(airport);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to finalise airport data: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Record parsers. The payload order is the canonical order the sim
    // publishes for each facility type (see the MSFS 2024 facilities docs);
    // it is NOT necessarily the order the fields were declared in.
    // ------------------------------------------------------------------

    private static void ParseAirport(FacilityDataRecord rec, PendingRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        var a = req.Airport;
        a.ReferenceLatitude = r.ReadFloat64();
        a.ReferenceLongitude = r.ReadFloat64();
        a.ReferenceAltitudeMeters = r.ReadFloat64();
        a.MagneticVariationDeg = r.ReadFloat32();
        a.Name = r.ReadString(64);
        r.ReadString(64); // NAME64
        var payloadIcao = r.ReadString(8);
        // Some facilities (small strips, seaplane bases) return no ICAO in the
        // payload — keep the ident we requested so callers can still match it.
        if (!string.IsNullOrEmpty(payloadIcao)) a.Icao = payloadIcao;
        a.Region = r.ReadString(8);
        r.ReadFloat64(); r.ReadFloat64(); r.ReadFloat64(); // tower pos/alt
        a.TransitionAltitudeMeters = r.ReadFloat32();
        a.TransitionLevelMeters = r.ReadFloat32();
        a.IsClosed = r.ReadInt8() != 0;
        r.ReadString(256); // COUNTRY
        r.ReadString(256); // CITY_STATE
        for (int i = 0; i < 16; i++) r.ReadInt32(); // N_* counts
    }

    private static void ParseRunway(FacilityDataRecord rec, PendingRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        // Canonical runway payload: ILS region/type pairs first, then geometry.
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
        if (r.ReadInt8() != 0) rw.PrimaryThreshold = rw.PrimaryEnd;
        if (r.ReadInt8() != 0) rw.SecondaryThreshold = rw.SecondaryEnd;
        req.Airport.Runways.Add(rw);
    }

    private static void ParseTaxiName(FacilityDataRecord rec, PendingRequest req)
        => req.TaxiNames.Add(new FacilityPayloadReader(rec.Payload).ReadString(32));

    private static void ParseTaxiPoint(FacilityDataRecord rec, PendingRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        req.RawTaxiPoints[(int)rec.ItemIndex] = new RawTaxiPoint
        {
            Index = (int)rec.ItemIndex,
            Type = r.ReadInt32(),
            Orientation = r.ReadInt32(),
            BiasX = r.ReadFloat32(),
            BiasZ = r.ReadFloat32(),
        };
    }

    private static void ParseTaxiParking(FacilityDataRecord rec, PendingRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        req.RawTaxiParking.Add(new RawTaxiParking
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
            BiasZ = r.ReadFloat32(),
        });
    }

    private static void ParseTaxiPath(FacilityDataRecord rec, PendingRequest req)
    {
        var r = new FacilityPayloadReader(rec.Payload);
        req.RawTaxiPaths.Add(new RawTaxiPath
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
            NameIndex = (int)r.ReadUInt32(),
        });
    }

    // ------------------------------------------------------------------

    private static void ResolveNodePositions(PendingRequest req)
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
                    _ => GroundNodeType.Normal,
                },
                Orientation = p.Orientation,
                BiasX = p.BiasX,
                BiasZ = p.BiasZ,
            };
        }

        foreach (var p in req.RawTaxiParking)
        {
            if (!airport.Nodes.TryGetValue(p.Index, out var node))
            {
                node = new GroundNode { Index = p.Index };
                airport.Nodes[p.Index] = node;
            }

            var holdShort = p.TaxiPointType switch
            {
                2 or 5 => GroundNodeType.HoldShort,
                4 or 6 => GroundNodeType.IlsHoldShort,
                _ => (GroundNodeType?)null,
            };

            if (node.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort)
            {
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
            _ => "",
        };
        return string.IsNullOrEmpty(baseName) ? "" : $"{baseName}{suffix}";
    }

    private static void ApplyBiasFrame(AirportData airport, BiasFrame frame)
    {
        airport.Frame = frame;
        foreach (var node in airport.Nodes.Values)
        {
            var (east, north) = frame.ToEastNorth(node.BiasX, node.BiasZ);
            var (lat, lon) = GeoMath.FromLocal(east, north, airport.ReferenceLatitude, airport.ReferenceLongitude);
            node.Latitude = lat;
            node.Longitude = lon;
            node.AltitudeMeters = airport.ReferenceAltitudeMeters;
        }
    }

    /// <summary>
    /// Determines the airport local frame by testing each candidate orientation and
    /// keeping the one that places the graph closest to the aircraft's position.
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
                .Select(n => GeoMath.DistanceMeters(aircraftLat, aircraftLon, n.Latitude, n.Longitude))
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

    private static void BuildEdges(PendingRequest req)
    {
        var airport = req.Airport;
        int resolvable = 0, usable = 0;

        foreach (var p in req.RawTaxiPaths)
        {
            if (p.Type == (int)TaxiPathType.Closed) continue;
            usable++;

            var start = airport.FindNode(p.Start);
            var end = airport.FindNode(p.End);
            if (end is null && p.Type == (int)TaxiPathType.Parking)
            {
                var parking = req.RawTaxiParking.FirstOrDefault(pk => pk.Index == p.End);
                if (parking is not null && airport.Nodes.TryGetValue(parking.Index, out var parkingNode))
                    end = parkingNode;
            }
            if (start is null || end is null || start == end) continue;
            resolvable++;

            var length = GeoMath.DistanceMeters(start.Latitude, start.Longitude, end.Latitude, end.Longitude);
            if (length < 0.5) continue;

            start.Edges.Add(MakeEdge(p, p.Start, p.End, length));
            end.Edges.Add(MakeEdge(p, p.End, p.Start, length));

            // A taxi point carries no runway of its own; the runway a hold-short
            // guards is only recorded on the runway-type paths attached to it.
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

        airport.PathResolutionRatio = usable == 0 ? 0 : (double)resolvable / usable;
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
        NameIndex = p.NameIndex,
    };

    public void Dispose()
    {
        _sc.OnFacilityData -= OnFacilityData;
        _sc.OnFacilityDataEnd -= OnFacilityDataEnd;
    }

    private sealed class PendingRequest
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
