using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using AICopilotFS.Application.Abstractions;
using AICopilotFS.Application.Options;

#if MSFS_SDK_AVAILABLE
using Microsoft.FlightSimulator.SimConnect;
#endif

namespace AICopilotFS.Infrastructure.Simulator;

/// <summary>Identifies why a SimConnect connection could not be established.</summary>
public enum SimulatorConnectionFailureKind
{
    None,
    SdkUnavailable,
    SimulatorUnavailable,
    ManagedAssemblyMissing,
    NativeLibraryMissing,
    ArchitectureMismatch,
    VersionMismatch,
    Unexpected,
}

/// <summary>MSFS SimConnect connector following StormATC patterns.</summary>
public sealed class MsfsSimConnectConnector : ISimulatorConnector
{
    private readonly ILogger<MsfsSimConnectConnector> _logger;
    private readonly SimulatorOptions _options;

    public MsfsSimConnectConnector(
        IOptions<SimulatorOptions> options,
        ILogger<MsfsSimConnectConnector> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static bool IsSdkAvailable =>
#if MSFS_SDK_AVAILABLE
        true;
#else
        false;
#endif

    public const string SdkUnavailableMessage =
        "SimConnect integration is unavailable: this build could not find the MSFS SDK. "
        + "See docs/simconnect-setup.md.";

    public string ConnectorName => "Microsoft Flight Simulator (SimConnect)";

    public SimulatorConnectionState ConnectionState { get; private set; } =
        SimulatorConnectionState.Disconnected;

    public SimulatorConnectionFailureKind LastFailureKind { get; private set; }

    public string StatusMessage { get; private set; } = "SimConnect has not been started.";

#if MSFS_SDK_AVAILABLE
    private Domain.State.WeatherSnapshot? _latestWeather;
#endif
    private readonly object _airportCacheLock = new();
    private readonly Dictionary<string, AirportData> _airportCache =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;
    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    public AirportData? GetCachedAirport(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
            return null;

        lock (_airportCacheLock)
        {
            return _airportCache.GetValueOrDefault(icao.Trim());
        }
    }

    public IReadOnlyList<AirportData> GetCachedAirports()
    {
        lock (_airportCacheLock)
        {
            return [.. _airportCache.Values];
        }
    }

    private void SetConnectionState(
        SimulatorConnectionState state,
        string? message,
        SimulatorConnectionFailureKind failureKind = SimulatorConnectionFailureKind.None)
    {
        ConnectionState = state;
        LastFailureKind = failureKind;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }

        ConnectionChanged?.Invoke(this, new SimulatorConnectionChangedEventArgs(state, message));
    }

    internal static uint ToFrequencyHertz(double frequencyMhz) =>
        checked((uint)Math.Round(frequencyMhz * 1_000_000, MidpointRounding.AwayFromZero));

    public Task<AirportData?> GetAirportDataAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icao))
            return Task.FromResult<AirportData?>(null);

#if !MSFS_SDK_AVAILABLE
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetCachedAirport(icao));
#else
        return RequestAirportDataAsync(icao.Trim().ToUpperInvariant(), cancellationToken);
#endif
    }

#if !MSFS_SDK_AVAILABLE

    public Task<bool> TuneCom1Async(
        double frequencyMhz,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning(
            "Cannot tune COM1 to {Frequency} MHz: the SimConnect SDK is unavailable.",
            frequencyMhz);
        return Task.FromResult(false);
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogWarning(
            "{Message} See docs/simconnect-setup.md for the supported SDK discovery options.",
            SdkUnavailableMessage);

        SetConnectionState(
            SimulatorConnectionState.Unavailable,
            SdkUnavailableMessage,
            SimulatorConnectionFailureKind.SdkUnavailable);

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetConnectionState(
            SimulatorConnectionState.Disconnected,
            "Disconnected from Microsoft Flight Simulator.");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

#else

    private enum Definitions
    {
        AircraftState = 0,
        AirportFacility = 1,
        NearbyTraffic = 2,
    }

    private enum Requests
    {
        AircraftState = 0,
        AirportFacility = 1,
        NearbyTraffic = 2,
    }

    private enum Events
    {
        SetCom1ActiveHz = 0,
        SelectCom1Transmitter = 1,
    }

    private enum NotificationGroups
    {
        Highest = 1,
    }

    private enum PumpExitReason
    {
        Cancelled,
        ConnectionLost,
        FatalFailure,
    }

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<(string Icao, TaskCompletionSource<AirportData?> Completion)> _airportRequestQueue = new();
    private readonly AutoResetEvent _airportRequestSignal = new(false);
    private readonly ConcurrentQueue<(double FrequencyMhz, TaskCompletionSource<bool> Completion)> _radioTuneQueue = new();
    private readonly AutoResetEvent _radioTuneSignal = new(false);

    private SimConnect? _simConnect;
    private AutoResetEvent? _messageSignal;
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    private bool _disposed;
    private bool _quitReceived;
    private SimulatorConnectionFailureKind _callbackFailureKind;
    private string? _callbackFailureMessage;
    private int _firstFrameLogged;
    private (string Icao, TaskCompletionSource<AirportData?> Completion)? _activeAirportRequest;
    private DateTimeOffset _nextTrafficRequestAt = DateTimeOffset.MinValue;
    private bool _trafficRequestPending;

    private static readonly (string Variable, string? Unit, SIMCONNECT_DATATYPE Type)[] DataDefinition =
    [
        ("PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64),
        ("PRESSURE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64),
        ("GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64),
        ("AIRSPEED INDICATED", "knots", SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE HEADING DEGREES MAGNETIC", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64),
        ("SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32),
        ("BRAKE PARKING POSITION", "bool", SIMCONNECT_DATATYPE.INT32),
        ("GENERAL ENG COMBUSTION:1", "bool", SIMCONNECT_DATATYPE.INT32),
        ("COM ACTIVE FREQUENCY:1", "MHz", SIMCONNECT_DATATYPE.FLOAT64),
        ("COM STANDBY FREQUENCY:1", "MHz", SIMCONNECT_DATATYPE.FLOAT64),
        ("COM ACTIVE FREQUENCY:2", "MHz", SIMCONNECT_DATATYPE.FLOAT64),
        ("COM STANDBY FREQUENCY:2", "MHz", SIMCONNECT_DATATYPE.FLOAT64),
        ("TRANSPONDER CODE:1", "number", SIMCONNECT_DATATYPE.INT32),
        ("TITLE", null, SIMCONNECT_DATATYPE.STRING256),
        ("ATC ID", null, SIMCONNECT_DATATYPE.STRING32),
        ("PLANE ALT ABOVE GROUND", "feet", SIMCONNECT_DATATYPE.FLOAT64),
        ("AUTOPILOT MASTER", "bool", SIMCONNECT_DATATYPE.INT32),
        ("GEAR HANDLE POSITION", "bool", SIMCONNECT_DATATYPE.INT32),
        ("FLAPS HANDLE INDEX", "number", SIMCONNECT_DATATYPE.FLOAT64),
        ("COM TRANSMIT:1", "bool", SIMCONNECT_DATATYPE.INT32),
        ("COM TRANSMIT:2", "bool", SIMCONNECT_DATATYPE.INT32),
        ("TRANSPONDER STATE:1", "enum", SIMCONNECT_DATATYPE.INT32),
        ("AMBIENT WIND DIRECTION", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT WIND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64),
        ("SEA LEVEL PRESSURE", "millibars", SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT VISIBILITY", "meters", SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT TEMPERATURE", "celsius", SIMCONNECT_DATATYPE.FLOAT64),
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftStateStruct
    {
        public double Latitude;
        public double Longitude;
        public double IndicatedAltitude;
        public double PressureAltitude;
        public double GroundSpeed;
        public double IndicatedAirspeed;
        public double TrueHeading;
        public double MagneticHeading;
        public double VerticalSpeed;
        public int OnGround;
        public int ParkingBrake;
        public int EngineCombustion;
        public double Com1Active;
        public double Com1Standby;
        public double Com2Active;
        public double Com2Standby;
        public int TransponderCode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string AtcIdentifier;

        public double AltitudeAboveGround;
        public int AutopilotMaster;
        public int GearHandlePosition;
        public double FlapsHandleIndex;
        public int Com1Transmit;
        public int Com2Transmit;
        public int TransponderState;
        public double AmbientWindDirection;
        public double AmbientWindVelocity;
        public double SeaLevelPressure;
        public double AmbientVisibility;
        public double AmbientTemperature;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workerTask is { IsCompleted: false })
                return;

            CleanupCompletedWorker();

            if (!ValidateRuntimeArtifacts())
                return;

            SetConnectionState(
                SimulatorConnectionState.Connecting,
                "Connecting to Microsoft Flight Simulator.");

            var initialAttempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _workerCancellation = new CancellationTokenSource();

            _workerTask = Task.Factory.StartNew(
                () => RunConnectionWorker(initialAttempt, _workerCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await initialAttempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _workerCancellation?.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private bool ValidateRuntimeArtifacts()
    {
        if (!Environment.Is64BitProcess)
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                "SimConnect cannot load because the application is not running as a 64-bit process.",
                SimulatorConnectionFailureKind.ArchitectureMismatch);
            return false;
        }

        string managedPath = Path.Combine(
            AppContext.BaseDirectory,
            "Microsoft.FlightSimulator.SimConnect.dll");

        if (!File.Exists(managedPath))
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The managed SimConnect wrapper is missing from the application folder. "
                + "Rebuild with the MSFS SDK configured.",
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return false;
        }

        string nativePath = Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        if (!File.Exists(nativePath))
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The native SimConnect.dll is missing from the application folder. "
                + "Configure MsfsSimConnectNativePath or MSFS_SIMCONNECT_NATIVE_PATH and rebuild.",
                SimulatorConnectionFailureKind.NativeLibraryMissing);
            return false;
        }

        return true;
    }

    private void RunConnectionWorker(
        TaskCompletionSource<bool> initialAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            RunConnectionLoop(initialAttempt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            initialAttempt.TrySetResult(false);
        }
        catch (Exception ex)
        {
            ReportRuntimeFailure(ex);
            initialAttempt.TrySetResult(false);
        }
    }

    private void RunConnectionLoop(
        TaskCompletionSource<bool> initialAttempt,
        CancellationToken cancellationToken)
    {
        bool connectedOnce = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    OpenConnection();
                    connectedOnce = true;
                    initialAttempt.TrySetResult(true);

                    SetConnectionState(
                        SimulatorConnectionState.Connected,
                        "Connected to Microsoft Flight Simulator.");

                    _logger.LogInformation(
                        "SimConnect connected at approximately {Rate} updates per second.",
                        _options.UpdatesPerSecond);

                    PumpExitReason exitReason = PumpConnectedSimulator(cancellationToken);
                    DisposeSimConnect();

                    if (exitReason == PumpExitReason.Cancelled)
                        return;

                    if (exitReason == PumpExitReason.FatalFailure)
                    {
                        SetConnectionState(
                            SimulatorConnectionState.Error,
                            _callbackFailureMessage ?? "SimConnect reported a fatal connection error.",
                            _callbackFailureKind == SimulatorConnectionFailureKind.None
                                ? SimulatorConnectionFailureKind.Unexpected
                                : _callbackFailureKind);
                        return;
                    }

                    if (!_options.AutoReconnect)
                    {
                        SetConnectionState(
                            SimulatorConnectionState.Disconnected,
                            "The simulator connection was lost.");
                        return;
                    }

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        "The simulator connection was lost. Reconnecting.");
                }
                catch (Exception ex) when (connectedOnce && IsRetryableConnectionFailure(ex))
                {
                    DisposeSimConnect();
                    _logger.LogWarning(
                        "SimConnect is still unavailable while reconnecting (0x{HResult:X8}).",
                        ex.HResult);

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        "Microsoft Flight Simulator is not answering SimConnect. Reconnecting.");
                }
                catch (Exception ex)
                {
                    DisposeSimConnect();
                    ReportRuntimeFailure(ex);
                    initialAttempt.TrySetResult(false);
                    return;
                }

                if (cancellationToken.WaitHandle.WaitOne(GetReconnectInterval()))
                    return;
            }
        }
        finally
        {
            DisposeSimConnect();
            initialAttempt.TrySetResult(false);
        }
    }

    private void OpenConnection()
    {
        _quitReceived = false;
        _callbackFailureKind = SimulatorConnectionFailureKind.None;
        _callbackFailureMessage = null;

        _messageSignal = new AutoResetEvent(false);
        _simConnect = new SimConnect(
            "AICopilotFS",
            IntPtr.Zero,
            0,
            _messageSignal,
            0);

        RegisterDataDefinition(_simConnect);
        RegisterTrafficDataDefinition(_simConnect);
        RegisterAirportFacilityDefinition(_simConnect);
        _simConnect.MapClientEventToSimEvent(Events.SetCom1ActiveHz, "COM_RADIO_SET_HZ");
        _simConnect.MapClientEventToSimEvent(
            Events.SelectCom1Transmitter,
            "COM1_TRANSMIT_SELECT");

        _simConnect.OnRecvSimobjectData += OnReceiveSimObjectData;
        _simConnect.OnRecvSimobjectDataBytype += OnReceiveNearbyTraffic;
        _simConnect.OnRecvFacilityData += OnReceiveFacilityData;
        _simConnect.OnRecvFacilityDataEnd += OnReceiveFacilityDataEnd;
        _simConnect.OnRecvQuit += OnReceiveQuit;
        _simConnect.OnRecvException += OnReceiveException;

        int requestedRate = Math.Max(1, _options.UpdatesPerSecond);
        uint frameInterval = (uint)Math.Max(0, (30 / requestedRate) - 1);

        _simConnect.RequestDataOnSimObject(
            Requests.AircraftState,
            Definitions.AircraftState,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            frameInterval,
            0);

        _nextTrafficRequestAt = DateTimeOffset.MinValue;
        _trafficRequestPending = false;
    }

    private static void RegisterDataDefinition(SimConnect simConnect)
    {
        foreach ((string variable, string? unit, SIMCONNECT_DATATYPE type) in DataDefinition)
        {
            simConnect.AddToDataDefinition(
                Definitions.AircraftState,
                variable,
                unit,
                type,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
        }

        simConnect.RegisterDataDefineStruct<AircraftStateStruct>(Definitions.AircraftState);
    }

    private static void RegisterTrafficDataDefinition(SimConnect simConnect)
    {
        var trafficDef = new[]
        {
            ("ATC ID", null, SIMCONNECT_DATATYPE.STRING32),
            ("TITLE", null, SIMCONNECT_DATATYPE.STRING256),
            ("PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
            ("PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
            ("INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64),
            ("GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64),
            ("PLANE HEADING DEGREES MAGNETIC", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
            ("VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64),
            ("SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32),
        };

        foreach ((string variable, string? unit, SIMCONNECT_DATATYPE type) in trafficDef)
        {
            simConnect.AddToDataDefinition(
                Definitions.NearbyTraffic,
                variable,
                unit,
                type,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
        }
    }

    private static void RegisterAirportFacilityDefinition(SimConnect simConnect)
    {
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "OPEN AIRPORT");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "LATITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "LONGITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "ALTITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "MAGVAR");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "NAME64");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "ICAO");

        AddFacilityChild(
            simConnect,
            "RUNWAY",
            [
                "LATITUDE",
                "LONGITUDE",
                "HEADING",
                "LENGTH",
                "WIDTH",
                "SURFACE",
                "PRIMARY_NUMBER",
                "PRIMARY_DESIGNATOR",
                "SECONDARY_NUMBER",
                "SECONDARY_DESIGNATOR",
            ]);
        AddFacilityChild(simConnect, "FREQUENCY", ["TYPE", "FREQUENCY"]);
        AddFacilityChild(
            simConnect,
            "TAXI_POINT",
            ["TYPE", "ORIENTATION", "BIAS_X", "BIAS_Z"]);
        AddFacilityChild(
            simConnect,
            "TAXI_PARKING",
            [
                "TYPE",
                "TAXI_POINT_TYPE",
                "NAME",
                "SUFFIX",
                "NUMBER",
                "ORIENTATION",
                "HEADING",
                "RADIUS",
                "BIAS_X",
                "BIAS_Z",
            ]);
        AddFacilityChild(
            simConnect,
            "TAXI_PATH",
            [
                "TYPE",
                "WIDTH",
                "WEIGHT",
                "RUNWAY_NUMBER",
                "RUNWAY_DESIGNATOR",
                "START",
                "END",
                "NAME_INDEX",
            ]);
        AddFacilityChild(simConnect, "TAXI_NAME", ["NAME"]);

        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "CLOSE AIRPORT");
    }

    private static void AddFacilityChild(
        SimConnect simConnect,
        string childName,
        IReadOnlyList<string> fields)
    {
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, $"OPEN {childName}");
        foreach (string field in fields)
        {
            simConnect.AddToFacilityDefinition(Definitions.AirportFacility, field);
        }

        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, $"CLOSE {childName}");
    }

    private PumpExitReason PumpConnectedSimulator(CancellationToken cancellationToken)
    {
        WaitHandle[] handles =
            [_messageSignal!, _airportRequestSignal, _radioTuneSignal, cancellationToken.WaitHandle];

        while (true)
        {
            int signalled = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
            if (signalled == 3)
                return PumpExitReason.Cancelled;

            if (signalled == 1)
                StartNextAirportFacilityRequest();
            else if (signalled == 2)
                ApplyPendingRadioTunes();
            else if (signalled == 0)
            {
                try
                {
                    _simConnect!.ReceiveMessage();
                }
                catch (COMException ex)
                {
                    _logger.LogWarning(
                        "Lost the SimConnect connection (0x{HResult:X8}).",
                        ex.HResult);
                    return PumpExitReason.ConnectionLost;
                }
            }

            RequestNearbyTrafficIfDue();

            if (_callbackFailureKind != SimulatorConnectionFailureKind.None)
                return PumpExitReason.FatalFailure;

            if (_quitReceived)
                return PumpExitReason.ConnectionLost;
        }
    }

    private void ApplyPendingRadioTunes()
    {
        while (_radioTuneQueue.TryDequeue(out var request))
        {
            if (request.Completion.Task.IsCompleted)
                continue;

            if (_simConnect is null)
            {
                request.Completion.TrySetResult(false);
                continue;
            }

            try
            {
                uint hertz = ToFrequencyHertz(request.FrequencyMhz);

                _simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.SetCom1ActiveHz,
                    hertz,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.SelectCom1Transmitter,
                    0,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

                request.Completion.TrySetResult(true);
                _logger.LogInformation(
                    "Tuned MSFS COM1 active to {Frequency} MHz and selected COM1 for transmission.",
                    request.FrequencyMhz);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetResult(false);
                _logger.LogWarning(
                    ex,
                    "Tuning MSFS COM1 to {Frequency} MHz failed.",
                    request.FrequencyMhz);
            }
        }
    }

    private void RequestNearbyTrafficIfDue()
    {
        if (_simConnect is null)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_trafficRequestPending)
        {
            _trafficRequestPending = false;
        }

        if (now < _nextTrafficRequestAt)
            return;

        _trafficRequestPending = true;
        _nextTrafficRequestAt = now.AddSeconds(5);

        try
        {
            _simConnect.RequestDataOnSimObjectType(
                Requests.NearbyTraffic,
                Definitions.NearbyTraffic,
                20_000,
                SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
        }
        catch (Exception ex)
        {
            _trafficRequestPending = false;
            _logger.LogDebug(ex, "Requesting nearby MSFS traffic failed.");
        }
    }

    private void StartNextAirportFacilityRequest()
    {
        if (_activeAirportRequest is not null || _simConnect is null)
            return;

        while (_airportRequestQueue.TryDequeue(out var request))
        {
            if (request.Completion.Task.IsCompleted)
                continue;

            _activeAirportRequest = request;
            try
            {
                _simConnect.RequestFacilityData(
                    Definitions.AirportFacility,
                    Requests.AirportFacility,
                    request.Icao,
                    string.Empty);
                _logger.LogInformation(
                    "Requested live MSFS airport facility data for {Icao}.",
                    request.Icao);
            }
            catch (Exception ex)
            {
                _activeAirportRequest = null;
                request.Completion.TrySetResult(null);
                _logger.LogWarning(
                    ex,
                    "Requesting MSFS airport facility data for {Icao} failed.",
                    request.Icao);
                _airportRequestSignal.Set();
            }

            return;
        }
    }

    private void OnReceiveSimObjectData(
        SimConnect sender,
        SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if ((Requests)data.dwRequestID != Requests.AircraftState
            || data.dwData.Length == 0
            || data.dwData[0] is not AircraftStateStruct raw)
        {
            return;
        }

        var position = new GeoPosition(raw.Latitude, raw.Longitude);
        var aircraftState = new Domain.State.AircraftState
        {
            Timestamp = DateTimeOffset.UtcNow,
            Position = position,
            IndicatedAltitudeFeet = new Domain.ValueObjects.AltitudeFeet(raw.IndicatedAltitude),
            PressureAltitudeFeet = new Domain.ValueObjects.AltitudeFeet(
                double.IsFinite(raw.PressureAltitude) ? raw.PressureAltitude : raw.IndicatedAltitude),
            GroundSpeedKnots = new Domain.ValueObjects.SpeedKnots(raw.GroundSpeed),
            IndicatedAirspeedKnots = new Domain.ValueObjects.SpeedKnots(
                double.IsFinite(raw.IndicatedAirspeed) ? raw.IndicatedAirspeed : 0),
            TrueHeadingDegrees = new Domain.ValueObjects.HeadingDegrees(raw.TrueHeading),
            MagneticHeadingDegrees = new Domain.ValueObjects.HeadingDegrees(raw.MagneticHeading),
            VerticalSpeedFpm = new Domain.ValueObjects.VerticalSpeedFpm(
                double.IsFinite(raw.VerticalSpeed) ? raw.VerticalSpeed : 0),
            IsOnGround = raw.OnGround != 0,
            ParkingBrakeSet = raw.ParkingBrake != 0,
            EnginesRunning = raw.EngineCombustion != 0,
            AircraftTitle = string.IsNullOrWhiteSpace(raw.Title)
                ? "MSFS aircraft"
                : raw.Title.Trim(),
            AtcIdentifier = raw.AtcIdentifier?.Trim() ?? string.Empty,
            AltitudeAboveGroundFeet = new Domain.ValueObjects.AltitudeFeet(
                double.IsFinite(raw.AltitudeAboveGround)
                    ? Math.Max(0, raw.AltitudeAboveGround)
                    : 0),
            AutopilotEngaged = raw.AutopilotMaster != 0,
            GearHandleDown = raw.GearHandlePosition != 0,
            FlapsHandleIndex = double.IsFinite(raw.FlapsHandleIndex)
                ? Math.Max(0, (int)Math.Round(raw.FlapsHandleIndex))
                : 0,
            Radio = new Domain.ValueObjects.RadioState
            {
                Com1Active = new Domain.ValueObjects.FrequencyMhz(raw.Com1Active),
                Com1Standby = new Domain.ValueObjects.FrequencyMhz(raw.Com1Standby),
                Com2Active = new Domain.ValueObjects.FrequencyMhz(raw.Com2Active),
                Com2Standby = new Domain.ValueObjects.FrequencyMhz(raw.Com2Standby),
                TransmitOnCom1 = raw.Com1Transmit != 0 || raw.Com2Transmit == 0,
            },
            Transponder = new Domain.ValueObjects.TransponderState
            {
                Code = SafeSquawk(raw.TransponderCode),
                Mode = SafeTransponderMode(raw.TransponderState, raw.OnGround != 0),
            },
            Weather = new Domain.State.WeatherSnapshot
            {
                WindDirectionDegrees = raw.AmbientWindDirection,
                WindVelocityKnots = raw.AmbientWindVelocity,
                SeaLevelPressureMb = raw.SeaLevelPressure,
                VisibilityMeters = raw.AmbientVisibility,
                TemperatureCelsius = raw.AmbientTemperature,
                TrueHeadingDegrees = raw.TrueHeading,
                MagneticHeadingDegrees = raw.MagneticHeading,
            },
        };

        _latestWeather = aircraftState.Weather;

        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
        {
            _logger.LogInformation(
                "Received live MSFS aircraft data: {Latitude:F5}, {Longitude:F5}, "
                + "{Altitude:F0} ft, COM1 {Com1:F3} MHz, squawk {Squawk}.",
                raw.Latitude,
                raw.Longitude,
                raw.IndicatedAltitude,
                raw.Com1Active,
                aircraftState.Transponder.Code);
        }

        AircraftStateChanged?.Invoke(this, new AircraftStateChangedEventArgs(aircraftState));
    }

    private static SquawkCode SafeSquawk(int decimalCode)
    {
        return SquawkCode.FromDecimal(decimalCode);
    }

    private static int SafeTransponderMode(int transponderState, bool onGround)
    {
        return transponderState;
    }

    private void OnReceiveNearbyTraffic(
        SimConnect sender,
        SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
    {
        if ((Requests)data.dwRequestID != Requests.NearbyTraffic)
            return;

        if (data.dwentrynumber == 0)
        {
            // Clear buffer for new batch
        }
    }

    private void OnReceiveFacilityData(
        SimConnect sender,
        SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (data.UserRequestId != (uint)Requests.AirportFacility
            || _activeAirportRequest is not (var icao, var completion)
            || data.Data.Length == 0)
        {
            return;
        }

        _activeAirportRequest = null;
        completion.TrySetResult(null);
        _airportRequestSignal.Set();
    }

    private void OnReceiveFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        // Facility data end marker
    }

    private void OnReceiveQuit(SimConnect sender, SIMCONNECT_RECV_QUIT data)
    {
        _quitReceived = true;
    }

    private void OnReceiveException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        _callbackFailureKind = SimulatorConnectionFailureKind.Unexpected;
        _callbackFailureMessage = $"SimConnect exception: {data.dwException}";
    }

    private void DisposeSimConnect()
    {
        if (_simConnect is not null)
        {
            try { _simConnect.Dispose(); } catch { }
            _simConnect = null;
        }

        _messageSignal?.Dispose();
        _messageSignal = null;
    }

    private void CleanupCompletedWorker()
    {
        if (_workerTask is { IsCompleted: true })
        {
            _workerTask = null;
            _workerCancellation?.Dispose();
            _workerCancellation = null;
        }
    }

    private void ReportRuntimeFailure(Exception ex)
    {
        _logger.LogError(ex, "SimConnect runtime failure.");
        SetConnectionState(
            SimulatorConnectionState.Error,
            ex.Message,
            SimulatorConnectionFailureKind.Unexpected);
    }

    private static bool IsRetryableConnectionFailure(Exception ex)
    {
        return ex is COMException or InvalidOperationException;
    }

    private static TimeSpan GetReconnectInterval() => TimeSpan.FromSeconds(5);

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _workerCancellation?.Cancel();
        DisposeSimConnect();
        SetConnectionState(
            SimulatorConnectionState.Disconnected,
            "Disconnected from Microsoft Flight Simulator.");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeSimConnect();
        _workerCancellation?.Dispose();
        _lifecycleLock.Dispose();
        _airportRequestSignal.Dispose();
        _radioTuneSignal.Dispose();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    public Task<bool> TuneCom1Async(
        double frequencyMhz,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ConnectionState != SimulatorConnectionState.Connected)
        {
            _logger.LogInformation(
                "Cannot tune COM1 to {Frequency} MHz: SimConnect is not connected.",
                frequencyMhz);
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _radioTuneQueue.Enqueue((frequencyMhz, completion));
        _radioTuneSignal.Set();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            return completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(false);
            _logger.LogWarning(
                "Timed out while tuning MSFS COM1 to {Frequency} MHz.",
                frequencyMhz);
            return Task.FromResult(false);
        }
    }

    private Task<AirportData?> RequestAirportDataAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ConnectionState != SimulatorConnectionState.Connected)
        {
            _logger.LogInformation(
                "Cannot request MSFS airport facility data for {Icao}: SimConnect is not connected.",
                icao);
            return Task.FromResult<AirportData?>(null);
        }

        var completion = new TaskCompletionSource<AirportData?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _airportRequestQueue.Enqueue((icao, completion));
        _airportRequestSignal.Set();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            return completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(null);
            _logger.LogWarning(
                "Timed out waiting for MSFS airport facility data for {Icao}.",
                icao);
            return Task.FromResult<AirportData?>(null);
        }
    }

#endif
}
