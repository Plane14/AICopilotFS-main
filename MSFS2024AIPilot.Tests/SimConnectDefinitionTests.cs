using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// Locks down how a data definition is registered with the sim. Both defects these
/// tests guard against are silent: the call returns S_OK and only the receive
/// stream shows the damage, as a burst of exceptions rather than as a crash.
/// </summary>
public class SimConnectDefinitionTests
{
    /// <summary>
    /// DWORD DatumID = SIMCONNECT_UNUSED, and SIMCONNECT_UNUSED is DWORD_MAX, in
    /// SimConnect.h. Anything else - zero included - claims a client-defined datum
    /// ID: the first datum of a definition is accepted, every later one comes back
    /// SIMCONNECT_EXCEPTION_DUPLICATE_ID, and the definition ends up holding a
    /// single variable while the caller believes the whole block was registered.
    /// </summary>
    [Fact]
    public void AppendedDatumsUseSimConnectUnusedNotZero()
    {
        Assert.Equal(uint.MaxValue, SimConnectManager.SimConnectUnusedDatumId);

        var numeric = SimConnectManager.BuildNumericDatum("PLANE LATITUDE", "degrees", DatumType.Float64);
        Assert.Equal(SimConnectManager.SimConnectUnusedDatumId, numeric.DatumId);
        Assert.NotEqual(0u, numeric.DatumId);

        var text = SimConnectManager.BuildStringDatum("ATC ID", DatumType.String32);
        Assert.Equal(SimConnectManager.SimConnectUnusedDatumId, text.DatumId);
        Assert.NotEqual(0u, text.DatumId);
    }

    /// <summary>
    /// A variable whose type is a string takes no unit. The SDK documents passing
    /// the empty string (or NULL) as UnitsName; passing "string" instead is
    /// answered with SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED, after which the
    /// definition cannot be requested at all (UNRECOGNIZED_ID on the request).
    /// </summary>
    [Fact]
    public void StringDatumsCarryNoUnit()
    {
        foreach (var (name, size) in AircraftController.AtcStringLayout)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));

            var datum = SimConnectManager.BuildStringDatum(name, size);
            Assert.Equal("", datum.Units);
            Assert.DoesNotContain("string", datum.Units, StringComparison.OrdinalIgnoreCase);
            Assert.Equal((uint)size, datum.DatumType);
        }
    }

    [Fact]
    public void StringDatumSizesMatchSimConnectDotH()
    {
        Assert.Equal(8, SimConnectManager.StringDatumSize(DatumType.String8));
        Assert.Equal(32, SimConnectManager.StringDatumSize(DatumType.String32));
        Assert.Equal(64, SimConnectManager.StringDatumSize(DatumType.String64));
        Assert.Equal(128, SimConnectManager.StringDatumSize(DatumType.String128));
        Assert.Equal(256, SimConnectManager.StringDatumSize(DatumType.String256));
        Assert.Equal(260, SimConnectManager.StringDatumSize(DatumType.String260));
        Assert.Equal(256, SimConnectManager.StringDatumSize(DatumType.StringV));
    }

    /// <summary>String datums must be the only contents of a string definition.</summary>
    [Fact]
    public void EveryAtcTextDatumIsAStringType()
    {
        Assert.NotEmpty(AircraftController.AtcStringLayout);
        Assert.All(AircraftController.AtcStringLayout, d =>
            Assert.Contains(d.Size, new[]
            {
                DatumType.String8, DatumType.String32, DatumType.String64,
                DatumType.String128, DatumType.String256, DatumType.String260, DatumType.StringV
            }));
    }

    /// <summary>
    /// An exception report is unreadable unless the call that produced the packet
    /// is named. SimConnect batches queued calls and hands out packet IDs per
    /// batch, so the packet ID read back at send time can lag the packet the server
    /// blames: the report must then fall back to the recorded send closest in
    /// packet number rather than to nothing at all.
    /// </summary>
    [Fact]
    public void ExceptionAttribution_FallsBackToTheClosestRecordedSend()
    {
        var calls = new[]
        {
            new SimConnectManager.SentCall { PacketId = 0, Description = "unflushed call, no packet yet" },
            new SimConnectManager.SentCall { PacketId = 12, Description = "add datum 'LIGHT NAV' as 'bool' to definition 3" },
            new SimConnectManager.SentCall { PacketId = 13, Description = "request data for definition 3 [LIGHT NAV (bool)]" },
            new SimConnectManager.SentCall { PacketId = 14, Description = "add datum 'LIGHT BEAC' as 'bool' to definition 4" }
        };

        var exact = SimConnectManager.ClosestRecordedSend(calls, 13);
        Assert.NotNull(exact);
        Assert.Contains("request data", exact!.Description, StringComparison.Ordinal);

        // The blocked packet: the server blames one packet past the last call that
        // recorded an ID, so the failing call must still be the one named.
        var lagging = SimConnectManager.ClosestRecordedSend(calls, 15);
        Assert.NotNull(lagging);
        Assert.Contains("LIGHT BEAC", lagging!.Description, StringComparison.Ordinal);

        // Nothing that old was recorded.
        Assert.Null(SimConnectManager.ClosestRecordedSend(calls, 11));
    }
}
