using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Native P/Invoke wrapper around SimConnect.dll. This is the most reliable interop
/// approach for SimConnect and gives full control over data definitions and events.
/// </summary>
public sealed partial class SimConnectManager : IDisposable
{
    private IntPtr _handle;
    private bool _connected;
    private readonly object _lock = new();
    private readonly Dictionary<string, uint> _eventMap = new();
    private readonly Dictionary<uint, string> _eventNames = new();
    /// <summary>Names of the datums of every definition, keyed by define ID.</summary>
    private readonly Dictionary<uint, List<string>> _definitionDatums = new();
    /// <summary>
    /// One send call and the packet ID SimConnect reported for it, kept in send
    /// order so an exception report can name the call even when the packet ID the
    /// server reports is not the one recorded (see <see cref="SendNamed"/>).
    /// </summary>
    internal sealed class SentCall
    {
        public uint PacketId;
        public string Description = "";
    }

    /// <summary>
    /// What was sent for the last few sent packets, keyed by packet ID. An
    /// exception report carries nothing but a packet ID and a parameter index
    /// ("NAME_UNRECOGNIZED (sendId=236, index=2)"), which is unreadable unless the
    /// call that produced the packet is remembered: the same report then names the
    /// datum or event that was rejected.
    /// </summary>
    private readonly object _sentLock = new();
    private readonly Dictionary<uint, string> _sentCalls = new();
    private readonly Queue<SentCall> _sentOrder = new();
    private const int RememberedSentCalls = 256;
    private uint _nextEventId = 1000;
    private uint _nextDefineId = 1;
    private uint _nextRequestId = 1;
    /// <summary>
    /// Keep-alive handle for this instance, so a dispatch callback can resolve the manager
    /// that owns the connection it was delivered on. Strong rather than weak: the callback
    /// arrives on an unmanaged thread that never touches the object graph otherwise, so a
    /// weak handle can be collected between the call and the callback.
    /// </summary>
    private readonly GCHandle _selfHandle;
    /// <summary>Byte size of each datum, keyed by define ID, for string-only definitions.</summary>
    private readonly Dictionary<uint, int[]> _stringDefinitions = new();

    private SimConnectRecvDelegate? _recvDelegate;
    private static readonly SimConnectRecvDelegate RecvDelegate = OnRecvNative;

    /// <summary>Set when the simulator reports it is shutting down mid-dispatch.</summary>
    /// <remarks>
    /// SimConnect_Close from inside SimConnect_CallDispatch closes the connection the DLL
    /// is still pumping messages through, which is not a documented pattern: the flag lets
    /// <see cref="Dispatch"/> close it once the callback has returned.
    /// </remarks>
    private int _quitReported;
    private DateTime _lastSendReportUtc;
    private const int SendReportIntervalSeconds = 5;

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    public event Action<SimObjectData>? OnDataReceived;
    public event Action<SimEventData>? OnEventReceived;
    public event Action<SystemStateData>? OnSystemState;
    public event Action<FacilityDataRecord>? OnFacilityData;
    public event Action<uint>? OnFacilityDataEnd;
    public event Action<StringSimObjectData>? OnStringDataReceived;

    public bool IsConnected => _connected && _handle != IntPtr.Zero;

    public SimConnectManager()
    {
        _selfHandle = GCHandle.Alloc(this);
        _recvDelegate = OnRecvNative;
        NativeBridge.Manager = this;
    }

        public async Task ConnectAsync(string appName = "MSFS2024AIPilot", CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_connected) return;

            try
            {
                var hr = SimConnect_Open(ref _handle, appName, IntPtr.Zero, 0, IntPtr.Zero, 0);
                if (hr != 0 || _handle == IntPtr.Zero)
                {
                    throw new SimConnectException($"SimConnect_Open failed with HRESULT 0x{hr:X8}. Ensure Microsoft Flight Simulator 2024 is running.");
                }
                _connected = true;
            }
            catch (DllNotFoundException ex)
            {
                throw new SimConnectException("SimConnect.dll not found. Ensure MSFS 2024 is installed.", ex);
            }
        }

        OnConnected?.Invoke();
        await Task.CompletedTask;
    }

    public void Disconnect()
    {
        DisconnectQuietly();
        // Raised outside the connection lock: a subscriber that waits for another thread
        // which is itself waiting to send something would otherwise deadlock the process.
        OnDisconnected?.Invoke();
    }

    /// <summary>Closes the connection without telling anyone, for the shutdown paths.</summary>
    private void DisconnectQuietly()
    {
        lock (_lock)
        {
            if (!_connected) return;

            try
            {
                if (_handle != IntPtr.Zero)
                {
                    SimConnect_Close(ref _handle);
                }
            }
            catch { }
            finally
            {
                _handle = IntPtr.Zero;
                _connected = false;
                _stringDefinitions.Clear();
            }
        }
    }

    public void Dispatch()
    {
        IntPtr handle;
        IntPtr context;
        lock (_lock)
        {
            if (_recvDelegate is null || !_connected || _handle == IntPtr.Zero) return;
            handle = _handle;
            context = _selfHandle.IsAllocated ? GCHandle.ToIntPtr(_selfHandle) : IntPtr.Zero;
            SimConnect_CallDispatch(handle, _recvDelegate, context);
        }

        // The simulator left: close the connection here rather than from inside the callback.
        if (Interlocked.Exchange(ref _quitReported, 0) == 1) Disconnect();
    }

    private static void OnRecvNative(IntPtr pData, IntPtr cbData, IntPtr pContext)
    {
        // Guard against a truncated record before dereferencing it.
        if (cbData == IntPtr.Zero || (int)cbData < Marshal.SizeOf<SIMCONNECT_RECV>()) return;

        var manager = ResolveManager(pContext);
        if (manager == null) return;

        var recv = Marshal.PtrToStructure<SIMCONNECT_RECV>(pData);
        int available = (int)cbData;

        switch ((SIMCONNECT_RECV_ID)recv.dwID)
        {
            case SIMCONNECT_RECV_ID.OPEN:
                break;
            case SIMCONNECT_RECV_ID.QUIT:
                // Recorded, not acted on: see Dispatch.
                Interlocked.Exchange(ref manager._quitReported, 1);
                break;
            case SIMCONNECT_RECV_ID.EXCEPTION:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_EXCEPTION>())
                {
                    var exc = Marshal.PtrToStructure<SIMCONNECT_RECV_EXCEPTION>(pData);
                    manager.OnError?.Invoke(manager.DescribeException(exc));
                }
                break;
            case SIMCONNECT_RECV_ID.EVENT:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_EVENT>())
                {
                    var evt = Marshal.PtrToStructure<SIMCONNECT_RECV_EVENT>(pData);
                    manager.OnEventReceived?.Invoke(new SimEventData(evt.uGroupID, evt.uEventID, evt.dwData));
                }
                break;
            case SIMCONNECT_RECV_ID.SIMOBJECT_DATA:
                if (available > SimConnectReceiveOffsets.SimObjectData)
                {
                    var obj = Marshal.PtrToStructure<SIMCONNECT_RECV_SIMOBJECT_DATA>(pData);
                    manager.HandleSimObjectData(obj, pData + SimConnectReceiveOffsets.SimObjectData, available - SimConnectReceiveOffsets.SimObjectData);
                }
                break;
            case SIMCONNECT_RECV_ID.SYSTEM_STATE:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_SYSTEM_STATE>())
                {
                    var sys = Marshal.PtrToStructure<SIMCONNECT_RECV_SYSTEM_STATE>(pData);
                    manager.OnSystemState?.Invoke(new SystemStateData(sys.dwRequestID, sys.dwInteger, sys.fFloat, sys.szString));
                }
                break;
            case SIMCONNECT_RECV_ID.FACILITY_DATA:
                if (available > SimConnectReceiveOffsets.FacilityData)
                {
                    manager.HandleFacilityData(pData, available - SimConnectReceiveOffsets.FacilityData);
                }
                break;
            case SIMCONNECT_RECV_ID.FACILITY_DATA_END:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_FACILITY_DATA_END>())
                {
                    var end = Marshal.PtrToStructure<SIMCONNECT_RECV_FACILITY_DATA_END>(pData);
                    manager.OnFacilityDataEnd?.Invoke(end.RequestId);
                }
                break;
        }
    }

    /// <summary>
    /// SIMCONNECT_RECV_FACILITY_DATA has a fixed 40-byte header followed by a
    /// variable-length payload (the "Data" member). The payload layout is dictated
    /// entirely by the field list passed to SimConnect_AddToFacilityDefinition, so
    /// the raw bytes are handed to the consumer for parsing.
    /// </summary>
    private void HandleFacilityData(IntPtr pData, int payloadBytes)
    {
        var header = Marshal.PtrToStructure<SIMCONNECT_RECV_FACILITY_DATA>(pData);
        if (payloadBytes <= 0) return;

        var payload = new byte[payloadBytes];
        Marshal.Copy(pData + SimConnectReceiveOffsets.FacilityData, payload, 0, payloadBytes);

        OnFacilityData?.Invoke(new FacilityDataRecord(
            header.UserRequestId,
            header.UniqueRequestId,
            header.ParentUniqueRequestId,
            (FacilityDataType)header.Type,
            header.IsListItem != 0,
            header.ItemIndex,
            header.ListSize,
            payload));
    }

    /// <summary>
    /// Reads the 8-byte values following the fixed header. The count is capped by
    /// <see cref="SIMCONNECT_RECV_SIMOBJECT_DATA.dwDefineCount"/> so a message that
    /// carries more bytes than declared datums cannot read past the buffer.
    /// </summary>
    private void HandleSimObjectData(SIMCONNECT_RECV_SIMOBJECT_DATA header, IntPtr dataPtr, int dataBytes)
    {
        if (dataBytes <= 0) return;

        // String datums are returned as fixed-width, NUL-padded fields packed back to
        // back, so they must be decoded by field width rather than by 8-byte slots.
        if (_stringDefinitions.TryGetValue(header.dwDefineID, out var sizes))
        {
            OnStringDataReceived?.Invoke(new StringSimObjectData(
                header.dwRequestID, header.dwDefineID, DecodeStrings(dataPtr, dataBytes, sizes)));
            return;
        }

        // Every datum is declared as an 8-byte FLOAT64, so one datum is one slot.
        int availableSlots = dataBytes / sizeof(double);
        int declared = (int)header.dwDefineCount;

        // dwDefineCount is the number of datums the definition holds, so a response that
        // carries fewer bytes than the definition declares is not a frame: decoding it
        // mapped every missing value to 0, and a truncated POSITION block then read as a
        // real position at 0,0 - indistinguishable from the aircraft's own. Dropping it
        // instead makes a short block look like a dropped block.
        if (declared > availableSlots) return;

        int count = declared > 0 ? declared : availableSlots;
        if (count <= 0) return;

        var buffer = new byte[count * sizeof(double)];
        Marshal.Copy(dataPtr, buffer, 0, buffer.Length);

        var values = new object[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BitConverter.ToDouble(buffer, i * sizeof(double));
        }

        OnDataReceived?.Invoke(new SimObjectData(header.dwRequestID, header.dwDefineID, header.dwObjectID, values));
    }

    /// <summary>
    /// Allocates a data definition ID. SimConnect builds a definition from repeated
    /// <see cref="AddToDataDefinition"/> calls that share one ID, so a caller that
    /// wants a block of datums must allocate a single ID and add every datum to it.
    /// </summary>
    public uint CreateDataDefinition()
    {
        lock (_lock)
        {
            return _nextDefineId++;
        }
    }

    /// <summary>
    /// Runs a SimConnect call and remembers what it sent, so the error path can say
    /// what a packet ID referred to. The call is made while holding the connection
    /// lock: <see cref="SimConnect_GetLastSentPacketID"/> then names the packet just
    /// produced, not a concurrent one.
    /// <para>
    /// That packet ID is best-effort. SimConnect batches the queued calls and hands
    /// out a packet ID for each batch, so the last sent packet ID read immediately
    /// after a call can still name the batch before it. The recorded ID is therefore
    /// kept next to the send order, and <see cref="DescribeException"/> falls back to
    /// the recorded send closest in packet number when the exact one is not there.
    /// Unattributed exceptions - the facilities calls that used to be sent outside
    /// this helper - report nothing at all, which is what made a doomed datum name
    /// indistinguishable from anything else in the log.
    /// </para>
    /// <para>
    /// The native calls return an HRESULT and used to have it discarded, so a send
    /// into a closed handle was dropped in silence and the control loop kept flying
    /// into it with nothing in the log. The result is now checked, and a send that
    /// cannot leave because the simulator is gone is one log line rather than one
    /// per frame.
    /// </para>
    /// </summary>
    private void SendNamed(string description, Func<int> send)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || !_connected)
            {
                ReportSendFailure($"{description} not sent - the simulator connection is closed");
                return;
            }

            var hr = send();
            if (hr != 0) ReportSendFailure($"{description} failed with HRESULT 0x{hr:X8}");

            uint packetId;
            try
            {
                // Receives the packet ID through an out parameter and returns the
                // HRESULT of the call itself; reading the return value as the ID
                // (an earlier version of this declaration had one parameter and
                // returned the DWORD) silently recorded S_OK as packet zero and left
                // the DLL writing four bytes through an undefined register.
                if (SimConnect_GetLastSentPacketID(_handle, out packetId) != 0) return;
            }
            catch
            {
                return; // no simulator to talk to; nothing to remember
            }

            lock (_sentLock)
            {
                if (packetId != 0) _sentCalls[packetId] = description;
                _sentOrder.Enqueue(new SentCall { PacketId = packetId, Description = description });
                while (_sentOrder.Count > RememberedSentCalls)
                {
                    _sentCalls.Remove(_sentOrder.Dequeue().PacketId);
                }
            }
        }
    }

    /// <summary>
    /// Reports a send that could not be made or came back failed, once per interval
    /// rather than once per frame: after the simulator exits there is one of these per
    /// control frame for as long as the pilot is left running.
    /// </summary>
    private void ReportSendFailure(string message)
    {
        if (DateTime.UtcNow - _lastSendReportUtc < TimeSpan.FromSeconds(SendReportIntervalSeconds)) return;
        _lastSendReportUtc = DateTime.UtcNow;
        OnError?.Invoke($"SimConnect: {message}");
    }

    /// <summary>Resolves the manager a dispatch callback belongs to.</summary>
    /// <remarks>
    /// The context SimConnect_CallDispatch was given is the connection's own handle, so
    /// two live managers can never decode one connection's records against the other's
    /// definition table. The static is only a fallback for a call made with no context.
    /// </remarks>
    private static SimConnectManager? ResolveManager(IntPtr pContext)
    {
        if (pContext != IntPtr.Zero)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(pContext);
                if (handle.IsAllocated && handle.Target is SimConnectManager fromContext) return fromContext;
            }
            catch (InvalidOperationException) { }
        }

        return NativeBridge.Manager;
    }

    /// <summary>The recorded send whose packet number is closest to, but not after, <paramref name="packetId"/>.</summary>
    internal static SentCall? ClosestRecordedSend(IEnumerable<SentCall> ordered, uint packetId)
    {
        SentCall? best = null;
        foreach (var call in ordered)
        {
            if (call.PacketId > packetId) break;
            // A recorded ID of 0 means no packet had been handed out yet at send
            // time, so it identifies no send and can never be the closest one.
            if (call.PacketId == 0) continue;
            if (best is null || call.PacketId >= best.PacketId) best = call;
        }
        return best;
    }

    /// <summary>Adds the name behind a packet ID to an exception report.</summary>
    internal string DescribeException(SIMCONNECT_RECV_EXCEPTION exc)
    {
        var report =
            $"SimConnect exception: {(SIMCONNECT_EXCEPTION)exc.dwException} " +
            $"(sendId={exc.dwSendID}, index={exc.dwIndex})";

        string what;
        lock (_sentLock)
        {
            _sentCalls.TryGetValue(exc.dwSendID, out what!);
            if (string.IsNullOrEmpty(what))
            {
                var closest = ClosestRecordedSend(_sentOrder, exc.dwSendID);
                if (closest is not null)
                {
                    what = closest.PacketId == exc.dwSendID
                        ? closest.Description
                        : $"packet {exc.dwSendID} was not recorded; nearest recorded send is packet {closest.PacketId}: {closest.Description}";
                }
            }
        }

        return string.IsNullOrEmpty(what) ? report : $"{report} [{what}]";
    }

    /// <summary>
    /// The value SimConnect.h names <c>SIMCONNECT_UNUSED</c> (<c>DWORD_MAX</c>). It
    /// is the only DatumID that makes <see cref="SimConnect_AddToDataDefinition"/>
    /// <em>append</em> a datum to a definition. Anything else - including 0 - claims
    /// a client-defined datum ID, and the second datum added to the same definition
    /// is then rejected with <c>SIMCONNECT_EXCEPTION_DUPLICATE_ID</c>, leaving the
    /// definition holding nothing but its first variable.
    /// </summary>
    internal const uint SimConnectUnusedDatumId = 0xFFFFFFFFu;

    /// <summary>
    /// Builds the native arguments of one datum of a definition. DatumName is
    /// parameter 2 and UnitsName parameter 3 of the call, so both must be names the
    /// sim recognises; the trailing DatumID is what decides append versus duplicate
    /// (see <see cref="SimConnectUnusedDatumId"/>).
    /// </summary>
    internal static (string DatumName, string Units, uint DatumType, uint DatumId) BuildNumericDatum(
        string datumName, string unitsName, DatumType datumType)
        => (datumName, unitsName, (uint)datumType, SimConnectUnusedDatumId);

    /// <summary>
    /// Builds the native arguments of one string datum. A variable whose type is
    /// string (or a structure) takes no unit: the SDK documents passing the empty
    /// string or NULL for UnitsName, and passing a unit such as "string" is
    /// rejected with <c>SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED</c> on that
    /// parameter, which leaves the whole definition unrequestable.
    /// </summary>
    internal static (string DatumName, string Units, uint DatumType, uint DatumId) BuildStringDatum(
        string datumName, DatumType size)
        => (datumName, "", (uint)size, SimConnectUnusedDatumId);

    /// <summary>Appends one datum to the definition identified by <paramref name="defineId"/>.</summary>
    public void AddToDataDefinition(uint defineId, string datumName, string unitsName, DatumType datumType = DatumType.Float64, float epsilon = 0f)
    {
        lock (_definitionDatums)
        {
            if (!_definitionDatums.TryGetValue(defineId, out var names))
            {
                names = new List<string>();
                _definitionDatums[defineId] = names;
            }
            names.Add(string.IsNullOrEmpty(unitsName) ? datumName : $"{datumName} ({unitsName})");
        }

        var datum = BuildNumericDatum(datumName, unitsName, datumType);
        SendNamed($"add datum '{datum.DatumName}' as '{datum.Units}' to definition {defineId}",
            () => SimConnect_AddToDataDefinition(_handle, defineId, datum.DatumName, datum.Units, datum.DatumType, epsilon, datum.DatumId));
    }

    /// <summary>Byte width of each string datum type, from SimConnect.h.</summary>
    internal static int StringDatumSize(DatumType type) => type switch
    {
        DatumType.String8 => 8,
        DatumType.String32 => 32,
        DatumType.String64 => 64,
        DatumType.String128 => 128,
        DatumType.String256 => 256,
        DatumType.String260 => 260,
        DatumType.StringV => 256, // SimConnect reserves a fixed buffer for StringV too
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a string datum type")
    };

    /// <summary>
    /// Builds a definition whose datums are strings (e.g. "ATC DESIGNATED RUNWAY TAKEOFF").
    /// A string definition cannot be mixed with numeric datums: the response arrives as
    /// fixed-width text fields, so the byte layout of the whole definition must be known
    /// before a response can be decoded. Responses are delivered through
    /// <see cref="OnStringDataReceived"/>. Every datum is appended with
    /// <see cref="SimConnectUnusedDatumId"/> and no unit, as <see cref="BuildStringDatum"/>
    /// explains.
    /// </summary>
    public uint CreateStringDataDefinition(params (string Name, DatumType Size)[] datums)
    {
        if (datums == null || datums.Length == 0)
            throw new ArgumentException("at least one datum is required", nameof(datums));

        var defineId = CreateDataDefinition();
        var sizes = new int[datums.Length];
        lock (_definitionDatums)
        {
            if (!_definitionDatums.TryGetValue(defineId, out var names))
            {
                names = new List<string>();
                _definitionDatums[defineId] = names;
            }
            for (int i = 0; i < datums.Length; i++)
            {
                sizes[i] = StringDatumSize(datums[i].Size);
                names.Add(datums[i].Name);
                var datum = BuildStringDatum(datums[i].Name, datums[i].Size);
                SendNamed($"add string datum '{datum.DatumName}' to definition {defineId}",
                    () => SimConnect_AddToDataDefinition(_handle, defineId, datum.DatumName, datum.Units, datum.DatumType, 0, datum.DatumId));
            }
        }
        _stringDefinitions[defineId] = sizes;
        return defineId;
    }

    /// <summary>
    /// Reads the fixed-width text fields of a string response. Each field is NUL padded
    /// to its declared width, so the text ends at the first NUL (or at the end of the
    /// buffer when the payload is shorter than the declaration).
    /// </summary>
    internal static string[] DecodeStrings(IntPtr dataPtr, int dataBytes, int[] sizes)
    {
        var results = new string[sizes.Length];
        int offset = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            int width = Math.Min(sizes[i], Math.Max(0, dataBytes - offset));
            if (width <= 0)
            {
                results[i] = "";
                continue;
            }

            var buffer = new byte[width];
            Marshal.Copy(dataPtr + offset, buffer, 0, width);
            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) end = buffer.Length;
            results[i] = Encoding.UTF8.GetString(buffer, 0, end).Trim();
            offset += sizes[i];
        }
        return results;
    }

    public uint RequestData(uint defineId, uint objectId = 0, Period period = Period.Second, uint interval = 0, uint limit = 0)
    {
        string names;
        lock (_definitionDatums)
        {
            names = _definitionDatums.TryGetValue(defineId, out var list)
                ? string.Join(", ", list)
                : "(no datums recorded)";
        }
        uint requestId = 0;
        SendNamed($"request data for definition {defineId} [{names}]", () =>
        {
            requestId = _nextRequestId++;
            return SimConnect_RequestDataOnSimObject(_handle, requestId, defineId, objectId, (uint)period, 0, 0, interval, limit);
        });
        return requestId;
    }

    /// <summary>
    /// Requests every datum of <paramref name="defineId"/> for every object of the
    /// given type within <paramref name="radiusMeters"/> of the user object (0 = all).
    /// One <see cref="OnDataReceived"/> message arrives per object, each carrying its
    /// own object ID, which is the only way an external program can observe surrounding
    /// AI and multi-player traffic.
    /// <para>
     /// The request persists until it is replaced or the client disconnects, and the
     /// simulator treats a request ID that is re-issued as a replacement for the earlier
     /// request rather than as a second one. A caller that refreshes therefore passes the
     /// ID it was handed back in through <paramref name="existingRequestId"/>: a fresh ID
     /// on every refresh allocates a new persistent request each time, and an object-type
     /// request cannot be cancelled, so they accumulate for the whole session.
     /// </para>
     /// </summary>
    public uint RequestDataOnSimObjectType(uint defineId, uint radiusMeters, SimObjectType type, uint existingRequestId = 0)
    {
        string names;
        lock (_definitionDatums)
        {
            names = _definitionDatums.TryGetValue(defineId, out var list)
                ? string.Join(", ", list)
                : "(no datums recorded)";
        }

        uint requestId = existingRequestId;
        SendNamed($"request data on sim object type {type} (definition {defineId} [{names}], radius {radiusMeters} m)",
            () =>
            {
                if (requestId == 0) requestId = _nextRequestId++;
                return SimConnect_RequestDataOnSimObjectType(_handle, requestId, defineId, radiusMeters, (uint)type);
            });
        return requestId;
    }

    public uint MapClientEventToSimEvent(string eventName)
    {
        lock (_lock)
        {
            if (_eventMap.TryGetValue(eventName, out var id)) return id;
            id = _nextEventId++;
            _eventMap[eventName] = id;
            _eventNames[id] = eventName;
            SendNamed($"map event '{eventName}' to id {id}",
                () => SimConnect_MapClientEventToSimEvent(_handle, id, eventName));
            return id;
        }
    }

    public void TransmitClientEvent(uint eventId, uint objectId = 0, uint groupId = 0, uint data = 0, EventFlags flags = EventFlags.GroupIdIsPriority)
    {
        string name;
        lock (_lock)
        {
            _eventNames.TryGetValue(eventId, out name!);
        }
        var description = string.IsNullOrEmpty(name)
            ? $"transmit event id {eventId} (data={data})"
            : $"transmit event '{name}' (data={data})";
        SendNamed(description, () =>
            SimConnect_TransmitClientEvent(_handle, objectId, eventId, data, groupId, (uint)flags));
    }

    public void FireEvent(string eventName, uint data = 1)
    {
        var id = MapClientEventToSimEvent(eventName);
        TransmitClientEvent(id, data: data);
    }

    public void SubscribeToSystemEvent(uint eventId, string systemEventName)
    {
        lock (_lock)
        {
            // Every send goes through SendNamed: an exception raised by one of these
            // reports a packet ID alone, and a call that is not remembered turns a
            // self-describing error into an opaque one.
            SendNamed($"subscribe to system event '{systemEventName}' (event id {eventId})", () =>
                SimConnect_SubscribeToSystemEvent(_handle, eventId, systemEventName));
        }
    }

    public void RequestSystemState(uint requestId, string stateName)
    {
        lock (_lock)
        {
            SendNamed($"request system state '{stateName}' (request {requestId})", () =>
                SimConnect_RequestSystemState(_handle, requestId, stateName));
        }
    }

    /// <summary>Requests the title of the currently loaded user aircraft.</summary>
    public void RequestUserAircraftTitle(uint requestId) => RequestSystemState(requestId, "AircraftLoaded");

    /// <summary>Requests the active flight plan path (as reported by the sim).</summary>
    public void RequestActiveFlightPlan(uint requestId) => RequestSystemState(requestId, "FlightPlan");

    #region Facilities API

    /// <summary>
    /// Adds one field to a facility data definition. Field names include the
    /// "OPEN X"/"CLOSE X" markers that delimit parent/child scopes, e.g.
    /// "OPEN AIRPORT", "LATITUDE", "OPEN RUNWAY", "CLOSE RUNWAY", "CLOSE AIRPORT".
    /// Every OPEN must have a matching CLOSE.
    /// </summary>
    public void AddToFacilityDefinition(uint defineId, string fieldName)
    {
        lock (_lock)
        {
            // A field name the sim does not know is answered with NAME_UNRECOGNIZED
            // on parameter 2, and the request onto that definition then fails as
            // well. Naming the field is the only way to tell which of the ~50 calls
            // of a definition was the bad one.
            SendNamed($"add facility field '{fieldName}' to definition {defineId}", () =>
                SimConnect_AddToFacilityDefinition(_handle, defineId, fieldName));
        }
    }

    /// <summary>Requests the facility data described by <paramref name="defineId"/> for an ICAO.</summary>
    public void RequestFacilityData(uint defineId, uint requestId, string icao, string region = "")
    {
        lock (_lock)
        {
            SendNamed($"request facility data for '{icao}' (definition {defineId}, request {requestId})", () =>
                SimConnect_RequestFacilityData(_handle, defineId, requestId, icao, region));
        }
    }

    /// <summary>Requests a bare list of facilities of the given type (no definition needed).</summary>
    public void RequestFacilitiesList(FacilityListType listType, uint requestId)
    {
        lock (_lock)
        {
            SendNamed($"request facility list {listType} (request {requestId})", () =>
                SimConnect_RequestFacilitiesList(_handle, (uint)listType, requestId));
        }
    }

    #endregion

    public void FlightPlanLoad(string fileName)
    {
        lock (_lock)
        {
            SendNamed($"load flight plan '{fileName}'", () => SimConnect_FlightPlanLoad(_handle, fileName));
        }
    }

    public void AISetAircraftFlightPlan(uint objectId, string flightPlanPath, uint requestId)
    {
        lock (_lock)
        {
            SendNamed($"set AI flight plan for object {objectId}: '{flightPlanPath}' (request {requestId})",
                () => SimConnect_AISetAircraftFlightPlan(_handle, objectId, flightPlanPath, requestId));
        }
    }

    public void AddClientEventToNotificationGroup(uint groupId, uint eventId, bool maskable = false)
    {
        lock (_lock)
        {
            SendNamed($"add event id {eventId} to notification group {groupId}", () =>
                SimConnect_AddClientEventToNotificationGroup(_handle, groupId, eventId, maskable ? 1 : 0));
        }
    }

    public void SetNotificationGroupPriority(uint groupId, uint priority)
    {
        lock (_lock)
        {
            SendNamed($"set notification group {groupId} priority {priority}", () =>
                SimConnect_SetNotificationGroupPriority(_handle, groupId, priority));
        }
    }

    public void ExecuteAction(string actionName, params object[] args)
    {
        lock (_lock)
        {
            var eventId = MapClientEventToSimEvent(actionName);
            if (args.Length > 0 && args[0] is double d)
            {
                TransmitClientEvent(eventId, data: (uint)d);
            }
            else if (args.Length > 0 && args[0] is uint u)
            {
                TransmitClientEvent(eventId, data: u);
            }
            else
            {
                TransmitClientEvent(eventId);
            }
        }
    }

    public void Dispose()
    {
        Disconnect();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        GC.SuppressFinalize(this);
    }

    ~SimConnectManager()
    {
        // The finalizer thread raises no events: a subscriber's state may already have been
        // collected, and SimConnect_Close from a finalizer is a last resort, not a
        // notification. The connection's Disconnect is what raises OnDisconnected.
        DisconnectQuietly();
    }
}

public struct SimObjectData
{
    public uint RequestId;
    public uint DefineId;
    public uint ObjectId;
    public object[] Values;
    public SimObjectData(uint requestId, uint defineId, uint objectId, object[] values)
    {
        RequestId = requestId;
        DefineId = defineId;
        ObjectId = objectId;
        Values = values;
    }
}

public struct SimEventData
{
    public uint GroupId;
    public uint EventId;
    public uint Data;
    public SimEventData(uint groupId, uint eventId, uint data)
    {
        GroupId = groupId;
        EventId = eventId;
        Data = data;
    }
}

/// <summary>A data response whose datums were declared as strings.</summary>
public readonly struct StringSimObjectData
{
    public uint RequestId { get; }
    public uint DefineId { get; }
    public string[] Values { get; }
    public StringSimObjectData(uint requestId, uint defineId, string[] values)
    {
        RequestId = requestId;
        DefineId = defineId;
        Values = values ?? Array.Empty<string>();
    }
}

public struct SystemStateData
{
    public uint RequestId;
    public uint Integer;
    public double Float;
    public string String;
    public SystemStateData(uint requestId, uint integer, double f, string str)
    {
        RequestId = requestId;
        Integer = integer;
        Float = f;
        String = str ?? "";
    }
}

/// <summary>
/// Values from SIMCONNECT_DATATYPE in SimConnect.h.
/// NOTE: this is `enum : int` in the header, so the underlying size is 4 bytes.
/// All datum sizes other than FLOAT64 are not 8 bytes, which shifts the layout of
/// the returned data array. Prefer FLOAT64 (the SimConnect default) for every datum
/// so each value occupies exactly one 8-byte slot.
/// </summary>
public enum DatumType : uint
{
    Invalid = 0,
    Int32 = 1,
    Int64 = 2,
    Float32 = 3,
    Float64 = 4,
    String8 = 5,
    String32 = 6,
    String64 = 7,
    String128 = 8,
    String256 = 9,
    String260 = 10,
    StringV = 11,
    InitPosition = 12,
    MarkerState = 13,
    Waypoint = 14,
    LatLonAlt = 15,
    Xyz = 16,
    Int8 = 17,
}

public enum Period : uint
{
    Never = 0,
    Once = 1,
    VisualFrame = 2,
    SimFrame = 3,
    Second = 4,
}

[Flags]
public enum EventFlags : uint
{
    Default = 0x00000000,
    // SIMCONNECT_EVENT_FLAG_FAST_REPEAT_TIMER, 0x1, is a different flag: with it set
    // the GroupID parameter of TransmitClientEvent is not read as a priority and the
    // sim answers the transmit with UNRECOGNIZED_ID on that parameter.
    GroupIdIsPriority = 0x00000010,
}

/// <summary>Values from SIMCONNECT_SIMOBJECT_TYPE in SimConnect.h.</summary>
public enum SimObjectType : uint
{
    User = 0,
    UserAircraft = 0,
    All = 1,
    Aircraft = 2,
    Helicopter = 3,
    Boat = 4,
    Ground = 5,
    HotAirBalloon = 6,
    Animal = 7,
    UserAvatar = 8,
    UserCurrent = 9,
}

public enum SIMCONNECT_RECV_ID : uint
{
    NULL = 0,
    EXCEPTION = 1,
    OPEN = 2,
    QUIT = 3,
    EVENT = 4,
    EVENT_OBJECT_ADDREMOVE = 5,
    EVENT_FILENAME = 6,
    EVENT_FRAME = 7,
    SIMOBJECT_DATA = 8,
    SIMOBJECT_DATA_BYTYPE = 9,
    WEATHER_OBSERVATION = 10,
    CLOUD_STATE = 11,
    ASSIGNED_OBJECT_ID = 12,
    RESERVED_KEY = 13,
    CUSTOM_ACTION = 14,
    SYSTEM_STATE = 15,
    CLIENT_DATA = 16,
    EVENT_WEATHER_MODE = 17,
    AIRPORT_LIST = 18,
    VOR_LIST = 19,
    NDB_LIST = 20,
    WAYPOINT_LIST = 21,
    EVENT_MULTIPLAYER_SERVER_STARTED = 22,
    EVENT_MULTIPLAYER_CLIENT_STARTED = 23,
    EVENT_MULTIPLAYER_SESSION_ENDED = 24,
    EVENT_RACE_END = 25,
    EVENT_RACE_LAP = 26,
    EVENT_EX1 = 27,
    FACILITY_DATA = 28,
    FACILITY_DATA_END = 29,
    FACILITY_MINIMAL_LIST = 30,
    JETWAY_DATA = 31,
    CONTROLLERS_LIST = 32,
    ACTION_CALLBACK = 33,
    ENUMERATE_INPUT_EVENTS = 34,
    GET_INPUT_EVENT = 35,
    SUBSCRIBE_INPUT_EVENT = 36,
    ENUMERATE_INPUT_EVENT_PARAMS = 37,
    ENUMERATE_SIMOBJECT_AND_LIVERY_LIST = 38,
    FLOW_EVENT = 39,
    CAMERA_DATA = 40,
    CAMERA_STATUS = 41,
    CAMERA_DEFINITION_LIST = 42,
    COMM_BUS = 43,
    CAMERA_WORLD_LOCKER = 44,
}

/// <summary>
/// Values from SIMCONNECT_EXCEPTION in SimConnect.h. The numerical order is part of
/// the wire protocol and must not be reordered.
/// </summary>
public enum SIMCONNECT_EXCEPTION : uint
{
    NONE = 0,
    ERROR = 1,
    SIZE_MISMATCH = 2,
    UNRECOGNIZED_ID = 3,
    UNOPENED = 4,
    VERSION_MISMATCH = 5,
    TOO_MANY_GROUPS = 6,
    NAME_UNRECOGNIZED = 7,
    TOO_MANY_EVENT_NAMES = 8,
    EVENT_ID_DUPLICATE = 9,
    TOO_MANY_MAPS = 10,
    TOO_MANY_OBJECTS = 11,
    TOO_MANY_REQUESTS = 12,
    WEATHER_INVALID_PORT = 13,
    WEATHER_INVALID_METAR = 14,
    WEATHER_UNABLE_TO_GET_OBSERVATION = 15,
    WEATHER_UNABLE_TO_CREATE_STATION = 16,
    WEATHER_UNABLE_TO_REMOVE_STATION = 17,
    INVALID_DATA_TYPE = 18,
    INVALID_DATA_SIZE = 19,
    DATA_ERROR = 20,
    INVALID_ARRAY = 21,
    CREATE_OBJECT_FAILED = 22,
    LOAD_FLIGHTPLAN_FAILED = 23,
    OPERATION_INVALID_FOR_OBJECT_TYPE = 24,
    ILLEGAL_OPERATION = 25,
    ALREADY_SUBSCRIBED = 26,
    INVALID_ENUM = 27,
    DEFINITION_ERROR = 28,
    DUPLICATE_ID = 29,
    DATUM_ID = 30,
    OUT_OF_BOUNDS = 31,
    ALREADY_CREATED = 32,
    OBJECT_OUTSIDE_REALITY_BUBBLE = 33,
    OBJECT_CONTAINER = 34,
    OBJECT_AI = 35,
    OBJECT_ATC = 36,
    OBJECT_SCHEDULE = 37,
    JETWAY_DATA = 38,
    ACTION_NOT_FOUND = 39,
    NOT_AN_ACTION = 40,
    INCORRECT_ACTION_PARAMS = 41,
    GET_INPUT_EVENT_FAILED = 42,
    SET_INPUT_EVENT_FAILED = 43,
    EVENT_NAME_RESERVED = 44,
    INTERNAL = 45,
    CAMERA_API = 46,
}

/// <summary>
/// Wire layouts copied from <c>SimConnect.h</c> in the MSFS 2024 SDK. The base
/// <c>SIMCONNECT_RECV</c> is <c>{ dwSize, dwVersion, dwID }</c> — note that
/// <c>dwSize</c> comes first and there is a <c>dwVersion</c> field. Getting this
/// wrong shifts every derived struct by four bytes and breaks all matching.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_EXCEPTION
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint dwException;
    public uint dwSendID;
    public uint dwIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_OPEN
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szApplicationName;
    public uint dwApplicationVersionMajor;
    public uint dwApplicationVersionMinor;
    public uint dwApplicationBuildMajor;
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SIMCONNECT_RECV_FACILITY_DATA_END
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
        public uint RequestId;
    }

    public uint dwApplicationBuildMinor;
    public uint dwSimConnectVersionMajor;
    public uint dwSimConnectVersionMinor;
    public uint dwSimConnectBuildMajor;
    public uint dwSimConnectBuildMinor;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_EVENT
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint uGroupID;
    public uint uEventID;
    public uint dwData;
}

/// <summary>
/// 40-byte header; <c>dwDefineCount</c> 8-byte values follow. The SIMConnect.dll
/// shipped with the SDK declares a trailing union here, so the struct must not be
/// marshalled with an inline payload.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_SIMOBJECT_DATA
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint dwRequestID;
    public uint dwObjectID;
    public uint dwDefineID;
    public uint dwFlags;
    public uint dwEntryNumber;
    public uint dwOutOf;
    public uint dwDefineCount;
}

/// <summary>Offsets of the payload in the fixed-size receive headers.</summary>
internal static class SimConnectReceiveOffsets
{
    /// <summary>Header of SIMCONNECT_RECV_SIMOBJECT_DATA: base 12 + 7 DWORDs.</summary>
    public const int SimObjectData = 40;

    /// <summary>Header of SIMCONNECT_RECV_FACILITY_DATA: base 12 + 7 DWORDs.</summary>
    public const int FacilityData = 40;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_SYSTEM_STATE
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint dwRequestID;
    public uint dwInteger;
    public float fFloat;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szString;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_FACILITY_DATA
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint UserRequestId;
    public uint UniqueRequestId;
    public uint ParentUniqueRequestId;
    public uint Type;
    public uint IsListItem;
    public uint ItemIndex;
    public uint ListSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_AIRPORT_LIST
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint dwRequestID;
    public uint dwArraySize;
    public uint dwEntryNumber;
    public uint dwOutOf;
}

internal delegate void SimConnectRecvDelegate(IntPtr pData, IntPtr cbData, IntPtr pContext);

internal static class NativeBridge
{
    public static SimConnectManager? Manager { get; set; }
}

public partial class SimConnectManager
{}

public partial class SimConnectManager
{
    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_Open(
        ref IntPtr phSimConnect,
        [MarshalAs(UnmanagedType.LPStr)] string szName,
        IntPtr hWnd,
        uint dwUserEvent,
        IntPtr hEvent,
        uint dwConfigIndex);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern void SimConnect_Close(ref IntPtr phSimConnect);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_CallDispatch(
        IntPtr hSimConnect,
        SimConnectRecvDelegate pfnDispatch,
        IntPtr pContext);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_AddToDataDefinition(
        IntPtr hSimConnect,
        uint DefineID,
        [MarshalAs(UnmanagedType.LPStr)] string DatumName,
        [MarshalAs(UnmanagedType.LPStr)] string UnitsName,
        uint DatumType,
        float fEpsilon,
        uint DatumID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_RequestDataOnSimObject(
        IntPtr hSimConnect,
        uint RequestID,
        uint DefineID,
        uint ObjectID,
        uint Period,
        uint Flags,
        uint origin,
        uint interval,
        uint limit);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_MapClientEventToSimEvent(
        IntPtr hSimConnect,
        uint EventID,
        [MarshalAs(UnmanagedType.LPStr)] string EventName);

    /// <summary>
    /// HRESULT SimConnect_GetLastSentPacketID(HANDLE, DWORD* pdwSendID): the packet ID is
    /// returned through the out parameter and the return value is the HRESULT of the call.
    /// Declaring this as <c>uint (IntPtr)</c> - one parameter, the DWORD as the return -
    /// read S_OK back as packet zero, so no send was ever attributed, and left the native
    /// call writing four bytes through a register the stub never set up.
    /// </summary>
    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwSendID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_TransmitClientEvent(
        IntPtr hSimConnect,
        uint ObjectID,
        uint EventID,
        uint dwData,
        uint dwGroupID,
        uint dwFlags);

    // SimConnect_SetDataOnSimObject(HANDLE, DefineID, ObjectID, Flags, ArrayCount, cbUnitSize, pDataSet)
    // There is no uOffset parameter; an early prototype of this wrapper had one,
    // which shifted the pointer into cbUnitSize and silently wrote nothing.
    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_SetDataOnSimObject(
        IntPtr hSimConnect,
        uint DefineID,
        uint ObjectID,
        uint Flags,
        uint ArrayCount,
        uint cbUnitSize,
        IntPtr pDataSet);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_SubscribeToSystemEvent(
        IntPtr hSimConnect,
        uint EventID,
        [MarshalAs(UnmanagedType.LPStr)] string SystemEventName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_RequestSystemState(
        IntPtr hSimConnect,
        uint RequestID,
        [MarshalAs(UnmanagedType.LPStr)] string szState);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_FlightPlanLoad(
        IntPtr hSimConnect,
        [MarshalAs(UnmanagedType.LPStr)] string szFileName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_AISetAircraftFlightPlan(
        IntPtr hSimConnect,
        uint ObjectID,
        [MarshalAs(UnmanagedType.LPStr)] string szFlightPlanPath,
        uint RequestID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_RequestDataOnSimObjectType(
        IntPtr hSimConnect,
        uint RequestID,
        uint DefineID,
        uint dwRadiusMeters,
        uint ObjectType);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_AddClientEventToNotificationGroup(
        IntPtr hSimConnect,
        uint uGroupID,
        uint uEventID,
        int bMaskable);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_SetNotificationGroupPriority(
        IntPtr hSimConnect,
        uint uGroupID,
        uint uNotificationPriority);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_AddToFacilityDefinition(
        IntPtr hSimConnect,
        uint DefineID,
        [MarshalAs(UnmanagedType.LPStr)] string FieldName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_RequestFacilityData(
        IntPtr hSimConnect,
        uint DefineID,
        uint RequestID,
        [MarshalAs(UnmanagedType.LPStr)] string ICAO,
        [MarshalAs(UnmanagedType.LPStr)] string Region);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    internal static extern int SimConnect_RequestFacilitiesList(
        IntPtr hSimConnect,
        uint type,
        uint RequestID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int SimConnect_RequestFacilityData_EX1(
        IntPtr hSimConnect,
        uint DefineID,
        uint RequestID,
        [MarshalAs(UnmanagedType.LPStr)] string ICAO,
        [MarshalAs(UnmanagedType.LPStr)] string Region,
        char Type);
}

public sealed class SimConnectException : Exception
{
    public SimConnectException(string message) : base(message) { }
    public SimConnectException(string message, Exception inner) : base(message, inner) { }
}