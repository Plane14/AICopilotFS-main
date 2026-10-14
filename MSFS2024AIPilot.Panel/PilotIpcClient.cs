using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Panel;

public sealed class PilotIpcClient : IDisposable
{
    private const string PipeName = "MSFS2024AIPilot.Delegation";
    private readonly string _statusPipeName = "MSFS2024AIPilot.Status";
    private CancellationTokenSource _cts = new();
    private Task? _statusListenerTask;

    public event Action<PilotStatus>? OnStatus;
    public bool IsConnected { get; private set; }

    public async Task<bool> TryConnectAsync()
    {
        try
        {
            var response = await SendCommandAsync("{\"cmd\":\"get-status\"}");
            IsConnected = !string.IsNullOrEmpty(response);
        }
        catch
        {
            IsConnected = false;
        }
        return IsConnected;
    }

    public async Task<string> SendCommandAsync(string command)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8);

        await writer.WriteLineAsync(command);
        client.WaitForPipeDrain();
        return await reader.ReadLineAsync() ?? "";
    }

    public async Task EngageAsync()
    {
        await SendCommandAsync("{\"cmd\":\"engage\"}");
    }

    public async Task DisengageAsync()
    {
        await SendCommandAsync("{\"cmd\":\"disengage\"}");
    }

    public async Task ToggleAsync()
    {
        await SendCommandAsync("{\"cmd\":\"toggle\"}");
    }

    public async Task<PilotStatus?> GetStatusAsync()
    {
        var response = await SendCommandAsync("{\"cmd\":\"get-status\"}");
        try
        {
            // The host serialises with camelCase names (state, phase, connected) and
            // JsonSerializer is case-sensitive by default, so every property failed to
            // bind and each status came back as the Type's own defaults.
            return JsonSerializer.Deserialize<PilotStatus>(response, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public void StartStatusListener()
    {
        _statusListenerTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var server = new NamedPipeServerStream(_statusPipeName, PipeDirection.Out, 1,
                        PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);

                    using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };
                    while (server.IsConnected && !_cts.IsCancellationRequested)
                    {
                        var status = await GetStatusAsync();
                        if (status != null)
                        {
                            OnStatus?.Invoke(status);
                            await writer.WriteLineAsync(JsonSerializer.Serialize(status));
                        }
                        await Task.Delay(250, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class PilotStatus
{
    public string State { get; set; } = "Idle";
    public string Phase { get; set; } = "Unknown";
    public bool Connected { get; set; }
}