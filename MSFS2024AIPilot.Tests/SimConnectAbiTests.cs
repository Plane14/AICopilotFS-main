using System;
using System.Runtime.InteropServices;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Locks down the SimConnect receive ABI. These layouts are copied from
/// SimConnect.h and must match byte for byte: a mistake here does not throw, it
/// silently returns garbage or dispatches nothing at all.
/// </summary>
public class SimConnectReceiveAbiTests
{
    [Fact]
    public void BaseRecordIsSizeVersionId()
    {
        // DWORD dwSize; DWORD dwVersion; DWORD dwID;
        Assert.Equal(12, Marshal.SizeOf<SIMCONNECT_RECV>());
        Assert.Equal(0, Marshal.OffsetOf<SIMCONNECT_RECV>(nameof(SIMCONNECT_RECV.dwSize)).ToInt64());
        Assert.Equal(4, Marshal.OffsetOf<SIMCONNECT_RECV>(nameof(SIMCONNECT_RECV.dwVersion)).ToInt64());
        Assert.Equal(8, Marshal.OffsetOf<SIMCONNECT_RECV>(nameof(SIMCONNECT_RECV.dwID)).ToInt64());
    }

    [Fact]
    public void SimObjectDataHeaderIsFortyBytes()
    {
        // base 12 + dwRequestID, dwObjectID, dwDefineID, dwFlags,
        // dwEntryNumber, dwOutOf, dwDefineCount
        Assert.Equal(40, Marshal.SizeOf<SIMCONNECT_RECV_SIMOBJECT_DATA>());
        Assert.Equal(40, SimConnectReceiveOffsets.SimObjectData);
    }

    [Fact]
    public void FacilityDataHeaderIsFortyBytes()
    {
        // base 12 + UserRequestId, UniqueRequestId, ParentUniqueRequestId, Type,
        // IsListItem, ItemIndex, ListSize
        Assert.Equal(40, Marshal.SizeOf<SIMCONNECT_RECV_FACILITY_DATA>());
        Assert.Equal(40, SimConnectReceiveOffsets.FacilityData);
    }

    [Fact]
    public void EventRecordIsTwentyFourBytes()
    {
        // base 12 + uGroupID, uEventID, dwData
        Assert.Equal(24, Marshal.SizeOf<SIMCONNECT_RECV_EVENT>());
    }

    [Fact]
    public void ExceptionRecordIsTwentyFourBytes()
    {
        // base 12 + dwException, dwSendID, dwIndex
        Assert.Equal(24, Marshal.SizeOf<SIMCONNECT_RECV_EXCEPTION>());
    }

    [Fact]
    public void SystemStateHasIntegerThenFloatThenString()
    {
        var size = Marshal.SizeOf<SIMCONNECT_RECV_SYSTEM_STATE>();
        // base 12 + dwRequestID + dwInteger(4) + fFloat(4) + char[260]
        Assert.Equal(12 + 4 + 4 + 4 + 260, size);
        Assert.Equal(12, Marshal.OffsetOf<SIMCONNECT_RECV_SYSTEM_STATE>(nameof(SIMCONNECT_RECV_SYSTEM_STATE.dwRequestID)).ToInt64());
        Assert.Equal(16, Marshal.OffsetOf<SIMCONNECT_RECV_SYSTEM_STATE>(nameof(SIMCONNECT_RECV_SYSTEM_STATE.dwInteger)).ToInt64());
        Assert.Equal(20, Marshal.OffsetOf<SIMCONNECT_RECV_SYSTEM_STATE>(nameof(SIMCONNECT_RECV_SYSTEM_STATE.fFloat)).ToInt64());
        Assert.Equal(24, Marshal.OffsetOf<SIMCONNECT_RECV_SYSTEM_STATE>(nameof(SIMCONNECT_RECV_SYSTEM_STATE.szString)).ToInt64());
    }

    [Fact]
    public void FacilityDataRecordDecodesFromNativeLayout()
    {
        // Write a SIMCONNECT_RECV_FACILITY_DATA by hand using the documented ABI.
        var buffer = new byte[SimConnectReceiveOffsets.FacilityData];
        void Put(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

        Put(0, 40);          // dwSize
        Put(4, 1);           // dwVersion
        Put(8, (uint)SIMCONNECT_RECV_ID.FACILITY_DATA); // dwID
        Put(12, 4242);       // UserRequestId
        Put(16, 7);          // UniqueRequestId
        Put(20, 0);          // ParentUniqueRequestId
        Put(24, (uint)FacilityDataType.Runway); // Type
        Put(28, 1);          // IsListItem
        Put(32, 9);          // ItemIndex
        Put(36, 3);          // ListSize

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var record = Marshal.PtrToStructure<SIMCONNECT_RECV_FACILITY_DATA>(handle.AddrOfPinnedObject());

            Assert.Equal(4242u, record.UserRequestId);
            Assert.Equal(7u, record.UniqueRequestId);
            Assert.Equal(0u, record.ParentUniqueRequestId);
            Assert.Equal((uint)FacilityDataType.Runway, record.Type);
            Assert.Equal(1u, record.IsListItem);
            Assert.Equal(9u, record.ItemIndex);
            Assert.Equal(3u, record.ListSize);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void SimObjectDataHeaderDecodesFromNativeLayout()
    {
        var buffer = new byte[SimConnectReceiveOffsets.SimObjectData];
        void Put(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

        Put(0, 72);          // dwSize (header + 4 doubles)
        Put(4, 1);           // dwVersion
        Put(8, (uint)SIMCONNECT_RECV_ID.SIMOBJECT_DATA);
        Put(12, 5);          // dwRequestID
        Put(16, 0);          // dwObjectID
        Put(20, 3);          // dwDefineID
        Put(24, 0);          // dwFlags
        Put(28, 0);          // dwEntryNumber
        Put(32, 1);          // dwOutOf
        Put(36, 4);          // dwDefineCount

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var record = Marshal.PtrToStructure<SIMCONNECT_RECV_SIMOBJECT_DATA>(handle.AddrOfPinnedObject());

            Assert.Equal(5u, record.dwRequestID);
            Assert.Equal(3u, record.dwDefineID);
            Assert.Equal(4u, record.dwDefineCount);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void SystemStateRecordDecodesAircraftTitle()
    {
        var buffer = new byte[260 + 24];
        string title = "Airbus A320neo FlyByWire";
        var titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        titleBytes.CopyTo(buffer, 24);

        BitConverter.GetBytes(0u).CopyTo(buffer, 0);              // dwSize
        BitConverter.GetBytes(1u).CopyTo(buffer, 4);              // dwVersion
        BitConverter.GetBytes((uint)SIMCONNECT_RECV_ID.SYSTEM_STATE).CopyTo(buffer, 8); // dwID
        BitConverter.GetBytes(77u).CopyTo(buffer, 12);            // dwRequestID
        BitConverter.GetBytes(3u).CopyTo(buffer, 16);             // dwInteger
        BitConverter.GetBytes(1.25f).CopyTo(buffer, 20);          // fFloat

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var record = Marshal.PtrToStructure<SIMCONNECT_RECV_SYSTEM_STATE>(handle.AddrOfPinnedObject());

            Assert.Equal(77u, record.dwRequestID);
            Assert.Equal(3u, record.dwInteger);
            Assert.Equal(1.25f, record.fFloat, precision: 3);
            Assert.Equal(title, record.szString);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void RecvIdValuesMatchSimConnectDotH()
    {
        // The values that matter most, and that a hand-written enum is most likely
        // to get wrong because the header orders EVENT_* before SIMOBJECT_DATA.
        Assert.Equal(15u, (uint)SIMCONNECT_RECV_ID.SYSTEM_STATE);
        Assert.Equal(8u, (uint)SIMCONNECT_RECV_ID.SIMOBJECT_DATA);
        Assert.Equal(28u, (uint)SIMCONNECT_RECV_ID.FACILITY_DATA);
        Assert.Equal(29u, (uint)SIMCONNECT_RECV_ID.FACILITY_DATA_END);
        Assert.Equal(4u, (uint)SIMCONNECT_RECV_ID.EVENT);
    }

    [Fact]
    public void DatumTypeFloat64IsFour()
    {
        // enum SIMCONNECT_DATATYPE:int { INVALID, INT32, INT64, FLOAT32, FLOAT64, ... }
        Assert.Equal(4u, (uint)DatumType.Float64);
        Assert.Equal(1u, (uint)DatumType.Int32);
        Assert.Equal(3u, (uint)DatumType.Float32);
    }

    [Fact]
    public void EventFlagGroupIdIsPriorityIsSixteen()
    {
        // static const DWORD SIMCONNECT_EVENT_FLAG_FAST_REPEAT_TIMER   = 0x00000001;
        // static const DWORD SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY = 0x00000010;
        // The two are easy to swap. With FAST_REPEAT_TIMER in its place, the GroupID
        // parameter of TransmitClientEvent is not read as a priority and the sim
        // rejects the transmit with UNRECOGNIZED_ID on that parameter.
        Assert.Equal(0x10u, (uint)EventFlags.GroupIdIsPriority);
        Assert.NotEqual(1u, (uint)EventFlags.GroupIdIsPriority);
    }

    [Theory]
    [InlineData("TITLE", "AircraftLoaded")]
    [InlineData("title", "AircraftLoaded")]
    [InlineData("AircraftLoaded", "AircraftLoaded")]
    [InlineData("FlightPlan", "FlightPlan")]
    public void RequestSystemStateNameNormalizationMatchesSimConnectNames(string input, string expected)
    {
        Assert.Equal(expected, SimConnectManager.NormalizeSystemStateName(input));
    }
}
