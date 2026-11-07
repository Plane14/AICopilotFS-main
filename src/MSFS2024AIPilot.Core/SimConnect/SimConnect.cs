using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core.SimConnect;

/// <summary>
/// Native P/Invoke wrapper over SimConnect.dll. Written out-of-process friendly:
/// connect, define data, request data on a timer, receive via a dispatch pump.
/// All sends are funnelled through <see cref="SendNamed"/> so that a SimConnect
/// exception report (which carries only a packet ID) can be resolved back to the
/// exact datum/event that was rejected.
/// </summary>
public sealed class SimConnectClient : IDisposable
{
    private IntPtr _handle;
    private bool _connected;
    private readonly object _lock = new();
    private readonly Dictionary<string, uint> _eventMap = new();
    private readonly Dictionary<uint, string> _eventNames = new();
    private readonly Dictionary<uint, List<string>> _definitionDatums = new();
    private readonly Dictionary<uint, int[]> _stringDefinitions = new();
    private uint _nextEventId = 1000;
    private uint _nextDefineId = 1;
    private uint _nextRequestId = 1;
    private readonly GCHandle _selfHandle;
    // Signalled by the sim whenever SimConnect messages are pending. Passed to
    // SimConnect_Open as hEventHandle; Dispatch only calls CallDispatch when it
    // is signaled, because CallDispatch BLOCKS waiting for a message on MSFS —
    // an empty queue used to freeze the whole control loop for 30 s+.
    private readonly EventWaitHandle _msgEvent = new(false, EventResetMode.AutoReset);

    private SimConnectRecvDelegate? _recvDelegate;
    private int _quitReported;
    private DateTime _lastSendReportUtc;
    private const int SendReportIntervalSeconds = 5;

    private readonly object _sentLock = new();
    private readonly Dictionary<uint, string> _sentCalls = new();
    private readonly Queue<(uint PacketId, string Description)> _sentOrder = new();
    private const int RememberedSentCalls = 256;

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    public event Action<SimObjectData>? OnDataReceived;
    public event Action<SimEventData>? OnEventReceived;
    public event Action<SystemStateData>? OnSystemState;
    public event Action<FacilityDataRecord>? OnFacilityData;
    public event Action<uint>? OnFacilityDataEnd;
    public event Action<uint, string, double, double, double>? OnAirportListItem;
    public event Action<uint>? OnAirportListEnd;
    public event Action<StringSimObjectData>? OnStringDataReceived;

    public bool IsConnected => _connected && _handle != IntPtr.Zero;

    public SimConnectClient()
    {
        _selfHandle = GCHandle.Alloc(this);
        _recvDelegate = OnRecvNative;
        NativeBridge.Client = this;
    }

    public async Task ConnectAsync(string appName = "MSFS2024AIPilot", CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_connected) return;
        }

        const int connectTimeoutSeconds = 30;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(connectTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var openTask = Task.Run(() =>
        {
            lock (_lock)
            {
                if (_connected) return;
                try
                {
                    var hr = SimConnect_Open(ref _handle, appName, IntPtr.Zero, 0, _msgEvent.SafeWaitHandle.DangerousGetHandle(), 0);
                    if (hr != 0 || _handle == IntPtr.Zero)
                        throw new SimConnectException($"SimConnect_Open failed with HRESULT 0x{hr:X8}. Ensure Microsoft Flight Simulator 2024 is running.");
                    _connected = true;
                }
                catch (DllNotFoundException ex)
                {
                    throw new SimConnectException("SimConnect.dll not found. Ensure MSFS 2024 is installed.", ex);
                }
            }
        });

        var delay = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
        var completed = await Task.WhenAny(openTask, delay);
        if (completed != openTask)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("SimConnect connection was cancelled.", cancellationToken);
            throw new SimConnectException(
                $"Timed out after {connectTimeoutSeconds} seconds connecting to SimConnect. " +
                "Ensure MSFS 2024 is running and you are in a flight - the connection is not accepted at the main menu or while a flight is loading.");
        }

        await openTask;
        OnConnected?.Invoke();
    }

    public void Disconnect()
    {
        DisconnectQuietly();
        OnDisconnected?.Invoke();
    }

    /// <summary>
    /// Serialises <see cref="SimConnect_CallDispatch"/> against
    /// <see cref="DisconnectQuietly"/> only — NOT against sends. Holding the
    /// send lock while pumping messages let a stalled dispatch freeze every
    /// SimConnect caller (control loop, ATC, facility loads).
    /// </summary>
    private readonly object _dispatchLock = new();

    private void DisconnectQuietly()
    {
        lock (_dispatchLock)
        lock (_lock)
        {
            if (!_connected) return;
            try
            {
                if (_handle != IntPtr.Zero) SimConnect_Close(ref _handle);
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

    /// <summary>Pumps the SimConnect message queue. Call periodically from the host loop.</summary>
    /// <summary>
    /// Pumps pending SimConnect messages. With <paramref name="idleWaitMs"/> &gt; 0
    /// the call sleeps on the message event first — the intended use is a
    /// dedicated pump thread that blocks until the sim has something to send.
    /// </summary>
    public void Dispatch(int idleWaitMs = 0)
    {
        IntPtr handle;
        IntPtr context;
        lock (_lock)
        {
            if (_recvDelegate is null || !_connected || _handle == IntPtr.Zero) return;
            handle = _handle;
            context = _selfHandle.IsAllocated ? GCHandle.ToIntPtr(_selfHandle) : IntPtr.Zero;
        }
        // _dispatchLock keeps Disconnect from closing the handle mid-pump;
        // _lock stays free so sends from other threads are never blocked by
        // message processing.
        lock (_dispatchLock)
        {
            lock (_lock)
            {
                if (!_connected || _handle != handle) return;
            }
            // CallDispatch blocks until a message arrives on MSFS — only call
            // it while the sim says messages are pending, otherwise an idle
            // sim (loading, paused, menu) freezes the caller indefinitely.
            // WaitOne already consumed one signal when idleWaitMs > 0; with
            // idleWaitMs == 0 we still need a pending-message check first or
            // CallDispatch would block on an empty queue.
            if (idleWaitMs > 0 ? !_msgEvent.WaitOne(idleWaitMs) : !_msgEvent.WaitOne(0)) return;
            var pumps = 0;
            do
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long cbBefore = Interlocked.Read(ref _callbackTicks);
                SimConnect_CallDispatch(handle, _recvDelegate, context);
                sw.Stop();
                // A multi-second pump is either the sim not delivering or our
                // receive callback grinding — the delta tells which.
                if (sw.ElapsedMilliseconds > 2000)
                {
                    var cbMs = TimeSpan.FromTicks(Interlocked.Read(ref _callbackTicks) - cbBefore).TotalMilliseconds;
                    OnError?.Invoke($"[SimConnect] dispatch pump took {sw.ElapsedMilliseconds} ms (receive callbacks {cbMs:F0} ms)");
                }
                pumps++;
            }
            while (_msgEvent.WaitOne(0) && pumps < 500);
        }
        if (Interlocked.Exchange(ref _quitReported, 0) == 1) Disconnect();
    }

    private static long _callbackTicks;

    private static void OnRecvNative(IntPtr pData, IntPtr cbData, IntPtr pContext)
    {
        var cbSw = System.Diagnostics.Stopwatch.StartNew();
        try { OnRecvNativeCore(pData, cbData, pContext); }
        finally { Interlocked.Add(ref _callbackTicks, cbSw.ElapsedTicks); }
    }

    private static void OnRecvNativeCore(IntPtr pData, IntPtr cbData, IntPtr pContext)
    {
        if (cbData == IntPtr.Zero || (int)cbData < Marshal.SizeOf<SIMCONNECT_RECV>()) return;

        var client = ResolveClient(pContext);
        if (client == null) return;

        var recv = Marshal.PtrToStructure<SIMCONNECT_RECV>(pData);
        int available = (int)cbData;

        switch ((SimConnectRecvId)recv.dwID)
        {
            case SimConnectRecvId.Quit:
                Interlocked.Exchange(ref client._quitReported, 1);
                break;
            case SimConnectRecvId.Exception:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_EXCEPTION>())
                {
                    var exc = Marshal.PtrToStructure<SIMCONNECT_RECV_EXCEPTION>(pData);
                    client.OnError?.Invoke(client.DescribeException(exc));
                }
                break;
            case SimConnectRecvId.Event:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_EVENT>())
                {
                    var evt = Marshal.PtrToStructure<SIMCONNECT_RECV_EVENT>(pData);
                    client.OnEventReceived?.Invoke(new SimEventData(evt.uGroupID, evt.uEventID, evt.dwData));
                }
                break;
            case SimConnectRecvId.SimObjectData:
                if (available > SimConnectReceiveOffsets.SimObjectData)
                {
                    var obj = Marshal.PtrToStructure<SIMCONNECT_RECV_SIMOBJECT_DATA>(pData);
                    client.HandleSimObjectData(obj, pData + SimConnectReceiveOffsets.SimObjectData, available - SimConnectReceiveOffsets.SimObjectData);
                }
                break;
            case SimConnectRecvId.SystemState:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_SYSTEM_STATE>())
                {
                    var sys = Marshal.PtrToStructure<SIMCONNECT_RECV_SYSTEM_STATE>(pData);
                    client.OnSystemState?.Invoke(new SystemStateData(sys.dwRequestID, sys.dwInteger, sys.fFloat, sys.szString));
                }
                break;
            case SimConnectRecvId.FacilityData:
                if (available > SimConnectReceiveOffsets.FacilityData)
                {
                    client.HandleFacilityData(pData, available - SimConnectReceiveOffsets.FacilityData);
                }
                break;
            case SimConnectRecvId.FacilityDataEnd:
                if (available >= Marshal.SizeOf<SIMCONNECT_RECV_FACILITY_DATA_END>())
                {
                    var end = Marshal.PtrToStructure<SIMCONNECT_RECV_FACILITY_DATA_END>(pData);
                    client.OnFacilityDataEnd?.Invoke(end.RequestId);
                }
                break;
            case SimConnectRecvId.AirportList:
                if (available > SimConnectReceiveOffsets.AirportList)
                {
                    var list = Marshal.PtrToStructure<SIMCONNECT_RECV_AIRPORT_LIST>(pData);
                    var payloadBytes = available - SimConnectReceiveOffsets.AirportList;
                    // Entries are SIMCONNECT_DATA_FACILITY_AIRPORT records:
                    //   Ident[9] + Region[3] + Latitude(double) + Longitude(double) + Altitude(double, meters)
                    const int entrySize = 9 + 3 + 8 + 8 + 8;
                    for (int off = 0; off + entrySize <= payloadBytes; off += entrySize)
                    {
                        var identBytes = new byte[9];
                        Marshal.Copy(pData + SimConnectReceiveOffsets.AirportList + off, identBytes, 0, 9);
                        var ident = Encoding.ASCII.GetString(identBytes).TrimEnd('\0', ' ');
                        if (ident.Length == 0) continue;
                        var lat = ReadDouble(pData, SimConnectReceiveOffsets.AirportList + off + 12);
                        var lon = ReadDouble(pData, SimConnectReceiveOffsets.AirportList + off + 20);
                        var alt = ReadDouble(pData, SimConnectReceiveOffsets.AirportList + off + 28);
                        client.OnAirportListItem?.Invoke(list.dwRequestID, ident, lat, lon, alt);
                    }
                    if (list.dwEntryNumber + 1 >= list.dwOutOf || list.dwOutOf == 0)
                        client.OnAirportListEnd?.Invoke(list.dwRequestID);
                }
                break;
        }
    }

    internal static double ReadDouble(IntPtr p, int offset)
    {
        var buf = new byte[8];
        Marshal.Copy(p + offset, buf, 0, 8);
        return BitConverter.ToDouble(buf, 0);
    }

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

    private void HandleSimObjectData(SIMCONNECT_RECV_SIMOBJECT_DATA header, IntPtr dataPtr, int dataBytes)
    {
        if (dataBytes <= 0) return;

        if (_stringDefinitions.TryGetValue(header.dwDefineID, out var sizes))
        {
            OnStringDataReceived?.Invoke(new StringSimObjectData(
                header.dwRequestID, header.dwDefineID, DecodeStrings(dataPtr, dataBytes, sizes)));
            return;
        }

        int availableSlots = dataBytes / sizeof(double);
        int declared = (int)header.dwDefineCount;
        if (declared > availableSlots) return;
        int count = declared > 0 ? declared : availableSlots;
        if (count <= 0) return;

        var buffer = new byte[count * sizeof(double)];
        Marshal.Copy(dataPtr, buffer, 0, buffer.Length);

        var values = new object[count];
        for (int i = 0; i < count; i++)
            values[i] = BitConverter.ToDouble(buffer, i * sizeof(double));

        OnDataReceived?.Invoke(new SimObjectData(header.dwRequestID, header.dwDefineID, header.dwObjectID, values));
    }

    public uint CreateDataDefinition()
    {
        lock (_lock) return _nextDefineId++;
    }

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
                if (SimConnect_GetLastSentPacketID(_handle, out packetId) != 0) return;
            }
            catch
            {
                return;
            }

            lock (_sentLock)
            {
                if (packetId != 0) _sentCalls[packetId] = description;
                _sentOrder.Enqueue((packetId, description));
                while (_sentOrder.Count > RememberedSentCalls)
                    _sentCalls.Remove(_sentOrder.Dequeue().PacketId);
            }
        }
    }

    private void ReportSendFailure(string message)
    {
        if (DateTime.UtcNow - _lastSendReportUtc < TimeSpan.FromSeconds(SendReportIntervalSeconds)) return;
        _lastSendReportUtc = DateTime.UtcNow;
        OnError?.Invoke($"SimConnect: {message}");
    }

    private static SimConnectClient? ResolveClient(IntPtr pContext)
    {
        if (pContext != IntPtr.Zero)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(pContext);
                if (handle.IsAllocated && handle.Target is SimConnectClient fromContext) return fromContext;
            }
            catch (InvalidOperationException) { }
        }
        return NativeBridge.Client;
    }

    private string DescribeException(SIMCONNECT_RECV_EXCEPTION exc)
    {
        var report = $"SimConnect exception: {(SimConnectExceptionCode)exc.dwException} (sendId={exc.dwSendID}, index={exc.dwIndex})";
        string what;
        lock (_sentLock)
        {
            if (!_sentCalls.TryGetValue(exc.dwSendID, out what!))
            {
                (uint PacketId, string Description)? best = null;
                foreach (var call in _sentOrder)
                {
                    if (call.PacketId > exc.dwSendID) break;
                    if (call.PacketId == 0) continue;
                    if (best is null || call.PacketId >= best.Value.PacketId) best = call;
                }
                if (best is { } b)
                    what = b.PacketId == exc.dwSendID ? b.Description
                        : $"packet {exc.dwSendID} was not recorded; nearest recorded send is packet {b.PacketId}: {b.Description}";
                else what = "";
            }
        }
        return string.IsNullOrEmpty(what) ? report : $"{report} [{what}]";
    }

    /// <summary>SIMCONNECT_UNUSED: the only DatumID that makes AddToDataDefinition append.</summary>
    public const uint UnusedDatumId = 0xFFFFFFFFu;

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
        SendNamed($"add datum '{datumName}' as '{unitsName}' to definition {defineId}",
            () => SimConnect_AddToDataDefinition(_handle, defineId, datumName, unitsName, (uint)datumType, epsilon, UnusedDatumId));
    }

    public static int StringDatumSize(DatumType type) => type switch
    {
        DatumType.String8 => 8,
        DatumType.String32 => 32,
        DatumType.String64 => 64,
        DatumType.String128 => 128,
        DatumType.String256 => 256,
        DatumType.String260 => 260,
        DatumType.StringV => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a string datum type")
    };

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
                SendNamed($"add string datum '{datums[i].Name}' to definition {defineId}",
                    () => SimConnect_AddToDataDefinition(_handle, defineId, datums[i].Name, "", (uint)datums[i].Size, 0, UnusedDatumId));
            }
        }
        _stringDefinitions[defineId] = sizes;
        return defineId;
    }

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

    /// <summary>
    /// Removes a data definition and stops every request still feeding from it.
    /// SimConnect caps the number of live requests per client, so long sessions
    /// must release definitions they no longer need.
    /// </summary>
    public void ClearDataDefinition(uint defineId)
    {
        if (defineId == 0 || _handle == IntPtr.Zero) return;
        SendNamed($"clear data definition {defineId}", () => SimConnect_ClearDataDefinition(_handle, defineId));
    }

    public uint RequestData(uint defineId, uint objectId = 0, Period period = Period.Second, uint interval = 0, uint limit = 0)
    {
        string names;
        lock (_definitionDatums)
        {
            names = _definitionDatums.TryGetValue(defineId, out var list) ? string.Join(", ", list) : "(no datums recorded)";
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
    /// given type within <paramref name="radiusMeters"/> of the user object.
    /// Re-issuing a request with the same ID replaces it; a fresh ID allocates a new
    /// persistent request, so callers should pass their existing ID when refreshing.
    /// </summary>
    public uint RequestDataOnSimObjectType(uint defineId, uint radiusMeters, SimObjectType type, uint existingRequestId = 0)
    {
        uint requestId = existingRequestId;
        SendNamed($"request data on sim object type {type} (definition {defineId}, radius {radiusMeters} m)", () =>
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
            SendNamed($"map event '{eventName}' to id {id}", () => SimConnect_MapClientEventToSimEvent(_handle, id, eventName));
            return id;
        }
    }

    public void TransmitClientEvent(uint eventId, uint objectId = 0, uint groupId = 0, uint data = 0, EventFlags flags = EventFlags.GroupIdIsPriority)
    {
        string name;
        lock (_lock) _eventNames.TryGetValue(eventId, out name!);
        var description = string.IsNullOrEmpty(name)
            ? $"transmit event id {eventId} (data={data})"
            : $"transmit event '{name}' (data={data})";
        SendNamed(description, () => SimConnect_TransmitClientEvent(_handle, objectId, eventId, data, groupId, (uint)flags));
    }

    public void FireEvent(string eventName, uint data = 1)
    {
        var id = MapClientEventToSimEvent(eventName);
        TransmitClientEvent(id, data: data);
    }

    public void SubscribeToSystemEvent(uint eventId, string systemEventName)
    {
        lock (_lock)
            SendNamed($"subscribe to system event '{systemEventName}' (event id {eventId})",
                () => SimConnect_SubscribeToSystemEvent(_handle, eventId, systemEventName));
    }

    public void RequestSystemState(uint requestId, string stateName)
    {
        lock (_lock)
            SendNamed($"request system state '{stateName}' (request {requestId})",
                () => SimConnect_RequestSystemState(_handle, requestId, stateName));
    }

    public void RequestUserAircraftTitle(uint requestId) => RequestSystemState(requestId, "AircraftLoaded");

    #region Facilities API

    public void AddToFacilityDefinition(uint defineId, string fieldName)
    {
        lock (_lock)
            SendNamed($"add facility field '{fieldName}' to definition {defineId}",
                () => SimConnect_AddToFacilityDefinition(_handle, defineId, fieldName));
    }

    public void RequestFacilityData(uint defineId, uint requestId, string icao, string region = "")
    {
        lock (_lock)
            SendNamed($"request facility data for '{icao}' (definition {defineId}, request {requestId})",
                () => SimConnect_RequestFacilityData(_handle, defineId, requestId, icao, region));
    }

    public void RequestFacilitiesList(FacilityListType listType, uint requestId)
    {
        lock (_lock)
            SendNamed($"request facility list {listType} (request {requestId})",
                () => SimConnect_RequestFacilitiesList(_handle, (uint)listType, requestId));
    }

    #endregion

    public void FlightPlanLoad(string fileName)
    {
        lock (_lock)
            SendNamed($"load flight plan '{fileName}'", () => SimConnect_FlightPlanLoad(_handle, fileName));
    }

    public void AddClientEventToNotificationGroup(uint groupId, uint eventId, bool maskable = false)
    {
        lock (_lock)
            SendNamed($"add event id {eventId} to notification group {groupId}",
                () => SimConnect_AddClientEventToNotificationGroup(_handle, groupId, eventId, maskable ? 1 : 0));
    }

    public void SetNotificationGroupPriority(uint groupId, uint priority)
    {
        lock (_lock)
            SendNamed($"set notification group {groupId} priority {priority}",
                () => SimConnect_SetNotificationGroupPriority(_handle, groupId, priority));
    }

    /// <summary>Executes a sim event by name with an optional integer payload (axis values are doubles).</summary>
    public void ExecuteAction(string actionName, params object[] args)
    {
        lock (_lock)
        {
            var eventId = MapClientEventToSimEvent(actionName);
            if (args.Length > 0 && args[0] is double d)
                TransmitClientEvent(eventId, data: (uint)d);
            else if (args.Length > 0 && args[0] is uint u)
                TransmitClientEvent(eventId, data: u);
            else
                TransmitClientEvent(eventId);
        }
    }

    /// <summary>
    /// Sets a client-defined SimVar on the user aircraft. Requires the SimConnect CFG
    /// definition installed (SimConnect.cfg AICTLS/Client data definitions).
    /// </summary>
    public void SetDataOnSimObject(uint defineId, uint objectId, uint arrayCount, byte[] data)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            SendNamed($"set data on sim object {objectId} (define {defineId})",
                () => SimConnect_SetDataOnSimObject(_handle, defineId, objectId, 0, arrayCount, (uint)data.Length, ptr));
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        Disconnect();
        _msgEvent.Dispose();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        GC.SuppressFinalize(this);
    }

    ~SimConnectClient()
    {
        DisconnectQuietly();
    }

#region Native SimConnect API

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_Open(ref IntPtr phSimConnect, [MarshalAs(UnmanagedType.LPStr)] string szName, IntPtr hWnd, uint dwUserEvent, IntPtr hEvent, uint dwConfigIndex);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern void SimConnect_Close(ref IntPtr phSimConnect);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_CallDispatch(IntPtr hSimConnect, SimConnectRecvDelegate pfnDispatch, IntPtr pContext);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint DefineID, [MarshalAs(UnmanagedType.LPStr)] string DatumName, [MarshalAs(UnmanagedType.LPStr)] string UnitsName, uint DatumType, float fEpsilon, uint DatumID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_RequestDataOnSimObject(IntPtr hSimConnect, uint RequestID, uint DefineID, uint ObjectID, uint Period, uint Flags, uint origin, uint interval, uint limit);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_RequestDataOnSimObjectType(IntPtr hSimConnect, uint RequestID, uint DefineID, uint dwRadiusMeters, uint ObjectType);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_ClearDataDefinition(IntPtr hSimConnect, uint DefineID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_MapClientEventToSimEvent(IntPtr hSimConnect, uint EventID, [MarshalAs(UnmanagedType.LPStr)] string EventName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwSendID);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_TransmitClientEvent(IntPtr hSimConnect, uint ObjectID, uint EventID, uint dwData, uint dwGroupID, uint dwFlags);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_SetDataOnSimObject(IntPtr hSimConnect, uint DefineID, uint ObjectID, uint Flags, uint ArrayCount, uint cbUnitSize, IntPtr pDataSet);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_SubscribeToSystemEvent(IntPtr hSimConnect, uint EventID, [MarshalAs(UnmanagedType.LPStr)] string SystemEventName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_RequestSystemState(IntPtr hSimConnect, uint RequestID, [MarshalAs(UnmanagedType.LPStr)] string szState);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_FlightPlanLoad(IntPtr hSimConnect, [MarshalAs(UnmanagedType.LPStr)] string szFileName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_AddClientEventToNotificationGroup(IntPtr hSimConnect, uint uGroupID, uint uEventID, int bMaskable);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_SetNotificationGroupPriority(IntPtr hSimConnect, uint uGroupID, uint uNotificationPriority);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_AddToFacilityDefinition(IntPtr hSimConnect, uint DefineID, [MarshalAs(UnmanagedType.LPStr)] string FieldName);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int SimConnect_RequestFacilityData(IntPtr hSimConnect, uint DefineID, uint RequestID, [MarshalAs(UnmanagedType.LPStr)] string ICAO, [MarshalAs(UnmanagedType.LPStr)] string Region);

    [DllImport("SimConnect.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    private static extern int SimConnect_RequestFacilitiesList(IntPtr hSimConnect, uint type, uint RequestID);

    #endregion
}

public sealed record SimObjectData(uint RequestId, uint DefineId, uint ObjectId, object[] Values);
public sealed record SimEventData(uint GroupId, uint EventId, uint Data);
public sealed record StringSimObjectData(uint RequestId, uint DefineId, string[] Values);
public sealed record SystemStateData(uint RequestId, uint Integer, double Float, string String);
public readonly record struct FacilityDataRecord(
    uint UserRequestId,
    uint UniqueRequestId,
    uint ParentUniqueRequestId,
    FacilityDataType Type,
    bool IsListItem,
    uint ItemIndex,
    uint ListSize,
    byte[] Payload);

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

public enum FacilityListType : uint
{
    Airport = 0,
    Waypoint = 1,
    Ndb = 2,
    Vor = 3,
    Count = 4,
}

/// <summary>Reads a facility payload sequentially in the order fields were declared.</summary>
public ref struct FacilityPayloadReader
{
    private ReadOnlySpan<byte> _data;
    private int _offset;

    public FacilityPayloadReader(byte[] data)
    {
        _data = data;
        _offset = 0;
    }

    private void Ensure(int bytes)
    {
        if (_offset + bytes > _data.Length)
            throw new InvalidOperationException($"Facility payload underrun: need {bytes} bytes at offset {_offset} of {_data.Length}");
    }

    public double ReadFloat64()
    {
        Ensure(8);
        double v = BitConverter.ToDouble(_data.Slice(_offset, 8));
        _offset += 8;
        return v;
    }

    public float ReadFloat32()
    {
        Ensure(4);
        float v = BitConverter.ToSingle(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public int ReadInt32()
    {
        Ensure(4);
        int v = BitConverter.ToInt32(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        uint v = BitConverter.ToUInt32(_data.Slice(_offset, 4));
        _offset += 4;
        return v;
    }

    public byte ReadInt8()
    {
        Ensure(1);
        byte v = _data[_offset];
        _offset += 1;
        return v;
    }

    public string ReadString(int maxLength)
    {
        Ensure(Math.Min(maxLength, _data.Length - _offset));
        int available = Math.Min(maxLength, _data.Length - _offset);
        int end = _offset;
        int limit = _offset + available;
        while (end < limit && _data[end] != 0) end++;
        var s = Encoding.ASCII.GetString(_data.Slice(_offset, end - _offset)).TrimEnd('\0', ' ');
        _offset += maxLength;
        if (_offset > _data.Length) _offset = _data.Length;
        return s;
    }

    public bool ReadBool() => ReadInt32() != 0;

    public bool HasMore => _offset < _data.Length;
}

/// <summary>SIMCONNECT_DATATYPE values from SimConnect.h (4-byte enum).</summary>
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
    GroupIdIsPriority = 0x00000010,
}

public enum SimObjectType : uint
{
    User = 0,
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

public enum SimConnectRecvId : uint
{
    Null = 0,
    Exception = 1,
    Open = 2,
    Quit = 3,
    Event = 4,
    EventObjectAddRemove = 5,
    EventFilename = 6,
    EventFrame = 7,
    SimObjectData = 8,
    SimObjectDataByType = 9,
    WeatherObservation = 10,
    CloudState = 11,
    AssignedObjectId = 12,
    ReservedKey = 13,
    CustomAction = 14,
    SystemState = 15,
    ClientData = 16,
    EventWeatherMode = 17,
    AirportList = 18,
    VorList = 19,
    NdbList = 20,
    WaypointList = 21,
    EventMultiplayerServerStarted = 22,
    EventMultiplayerClientStarted = 23,
    EventMultiplayerSessionEnded = 24,
    EventRaceEnd = 25,
    EventRaceLap = 26,
    EventEx1 = 27,
    FacilityData = 28,
    FacilityDataEnd = 29,
    FacilityMinimalList = 30,
    JetwayData = 31,
    ControllersList = 32,
    ActionCallback = 33,
    EnumerateInputEvents = 34,
    GetInputEvent = 35,
    SubscribeInputEvent = 36,
    EnumerateInputEventParams = 37,
    EnumerateSimObjectAndLiveryList = 38,
    FlowEvent = 39,
    CameraData = 40,
    CameraStatus = 41,
    CameraDefinitionList = 42,
    CommBus = 43,
    CameraWorldLocker = 44,
}

public enum SimConnectExceptionCode : uint
{
    None = 0,
    Error = 1,
    SizeMismatch = 2,
    UnrecognizedId = 3,
    Unopened = 4,
    VersionMismatch = 5,
    TooManyGroups = 6,
    NameUnrecognized = 7,
    TooManyEventNames = 8,
    EventIdDuplicate = 9,
    TooManyMaps = 10,
    TooManyObjects = 11,
    TooManyRequests = 12,
    WeatherInvalidPort = 13,
    WeatherInvalidMetar = 14,
    WeatherUnableToGetObservation = 15,
    WeatherUnableToCreateStation = 16,
    WeatherUnableToRemoveStation = 17,
    InvalidDataType = 18,
    InvalidDataSize = 19,
    DataError = 20,
    InvalidArray = 21,
    CreateObjectFailed = 22,
    LoadFlightplanFailed = 23,
    OperationInvalidForObjectType = 24,
    IllegalOperation = 25,
    AlreadySubscribed = 26,
    InvalidEnum = 27,
    DefinitionError = 28,
    DuplicateId = 29,
    DatumId = 30,
    OutOfBounds = 31,
    AlreadyCreated = 32,
    ObjectOutsideRealityBubble = 33,
    ObjectContainer = 34,
    ObjectAi = 35,
    ObjectAtc = 36,
    ObjectSchedule = 37,
    JetwayData = 38,
    ActionNotFound = 39,
    NotAnAction = 40,
    IncorrectActionParams = 41,
    GetInputEventFailed = 42,
    SetInputEventFailed = 43,
    EventNameReserved = 44,
    Internal = 45,
    CameraApi = 46,
}

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

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SIMCONNECT_RECV_FACILITY_DATA_END
{
    public uint dwSize;
    public uint dwVersion;
    public uint dwID;
    public uint RequestId;
}

internal static class SimConnectReceiveOffsets
{
    public const int SimObjectData = 40;
    public const int FacilityData = 40;
    /// <summary>Header of SIMCONNECT_RECV_AIRPORT_LIST: base 12 + 4 DWORDs.</summary>
    public const int AirportList = 28;
}

internal delegate void SimConnectRecvDelegate(IntPtr pData, IntPtr cbData, IntPtr pContext);

internal static class NativeBridge
{
    public static SimConnectClient? Client { get; set; }
}


public sealed class SimConnectException : Exception
{
    public SimConnectException(string message) : base(message) { }
    public SimConnectException(string message, Exception inner) : base(message, inner) { }
}
