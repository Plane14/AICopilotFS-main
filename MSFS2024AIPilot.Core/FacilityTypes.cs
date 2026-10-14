using System;
using System.Runtime.InteropServices;

namespace MSFS2024AIPilot.Core;

/// <summary>Values from SIMCONNECT_FACILITY_DATA_TYPE in SimConnect.h.</summary>
public enum FacilityDataType : uint
{
    Airport = 0,
    Runway = 1,
    Start = 2,
    Frequency = 3,
    Helipad = 4,
    Approach = 5,
    ApproachTransition = 6,
    ApproachLeg = 7,
    FinalApproachLeg = 8,
    MissedApproachLeg = 9,
    Departure = 10,
    Arrival = 11,
    RunwayTransition = 12,
    EnrouteTransition = 13,
    TaxiPoint = 14,
    TaxiParking = 15,
    TaxiPath = 16,
    TaxiName = 17,
    Jetway = 18,
    Vor = 19,
    Ndb = 20,
    Waypoint = 21,
    Route = 22,
    Pavement = 23,
    ApproachLights = 24,
    Vasi = 25,
    Vdgs = 26,
    HoldingPattern = 27,
    TaxiParkingAirline = 28,
}

/// <summary>Values from SIMCONNECT_FACILITY_LIST_TYPE in SimConnect.h.</summary>
public enum FacilityListType : uint
{
    Airport = 0,
    Waypoint = 1,
    Ndb = 2,
    Vor = 3,
    Count = 4,
}

/// <summary>
/// One facility record delivered by SimConnect. The payload layout is dictated by
/// the field list supplied to <see cref="SimConnectManager.AddToFacilityDefinition"/>;
/// use <see cref="FacilityPayloadReader"/> to decode it.
/// </summary>
public readonly record struct FacilityDataRecord(
    uint UserRequestId,
    uint UniqueRequestId,
    uint ParentUniqueRequestId,
    FacilityDataType Type,
    bool IsListItem,
    uint ItemIndex,
    uint ListSize,
    byte[] Payload);

/// <summary>
/// Reads a facility payload sequentially. Fields arrive in exactly the order they were
/// added to the facility data definition, so a single sequential reader is sufficient.
/// Strings are fixed-size and null-terminated.
/// </summary>
public ref struct FacilityPayloadReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public FacilityPayloadReader(byte[] data)
    {
        _data = data;
        _offset = 0;
    }

    public bool HasMore => _offset < _data.Length;

    public double ReadFloat64()
    {
        var v = BitConverter.ToDouble(_data.Slice(_offset, 8));
        _offset += 8;
        return v;
    }

    public float ReadFloat32()
    {
        var v = BitConverter.ToSingle(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public int ReadInt32()
    {
        var v = BitConverter.ToInt32(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        var v = BitConverter.ToUInt32(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public byte ReadInt8()
    {
        var v = _data[_offset];
        _offset += 1;
        return v;
    }

    /// <summary>Reads a fixed-length, null-terminated ASCII string field.</summary>
    public string ReadString(int length)
    {
        if (_offset + length > _data.Length)
        {
            _offset = _data.Length;
            return "";
        }
        var span = _data.Slice(_offset, length);
        _offset += length;

        var end = span.IndexOf((byte)0);
        if (end < 0) end = length;
        return System.Text.Encoding.ASCII.GetString(span.Slice(0, end));
    }
}

/// <summary>
/// Facility receive structures. SIMCONNECT_RECV_FACILITY_DATA has a fixed 40-byte
/// header followed by a variable-length payload beginning at the final member; the
/// canonical struct lives in SimConnectManager.cs alongside the other receive types.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_FACILITY_DATA_END
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint RequestId;
}