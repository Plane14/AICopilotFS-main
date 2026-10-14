using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Sends keyboard input to the BeyondATC application window. BeyondATC has no
/// automation API, so a request can only be issued the way a user issues it: through
/// the key bindings configured in its own options (push-to-talk, bring-to-front, and
/// "Fire Action 1"). Those bindings are read from BeyondATC's own preferences by
/// <see cref="BeyondAtcLogParser.ParseHotkeys"/>; see <see cref="AtcOptions"/>.
/// </summary>
public sealed class BeyondAtcWindowBridge : IDisposable
{
    private const string ProcessName = "BeyondATC";

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public IntPtr WindowHandle { get; private set; }
    public string? ProcessTitle { get; private set; }
    public DateTime LastProbeUtc { get; private set; } = DateTime.MinValue;
    public string? LastError { get; private set; }

    /// <summary>True once a BeyondATC window has been found by <see cref="Refresh"/>.</summary>
    public bool IsRunning => WindowHandle != IntPtr.Zero;

    /// <summary>
    /// Looks for a running BeyondATC window. The process list is enumerated only from
    /// the hub's source resolution, not from the panel status, so a fast HTTP poll
    /// never turns into a process scan.
    /// </summary>
    public bool Refresh()
    {
        LastProbeUtc = DateTime.UtcNow;
        WindowHandle = IntPtr.Zero;
        ProcessTitle = null;

        try
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    WindowHandle = handle;
                    ProcessTitle = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                        ? process.ProcessName
                        : process.MainWindowTitle;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return false;
    }

    /// <summary>
    /// Posts a key press and release to the BeyondATC window. The message is posted
    /// rather than injected, so no focus is stolen from the simulator; some Unity
    /// builds only consume input while focused, in which case the key is delivered but
    /// ignored by the application.
    /// </summary>
    public bool SendKey(int virtualKey, bool activate)
    {
        LastError = null;
        if (virtualKey <= 0) return false;

        var foreground = GetForegroundWindow();
        if (!Refresh() || WindowHandle == IntPtr.Zero)
        {
            LastError = "BeyondATC window not found";
            return false;
        }

        if (activate)
        {
            SetForegroundWindow(WindowHandle);
            Thread.Sleep(60);
        }

        var down = PostMessage(WindowHandle, WmKeyDown, (IntPtr)virtualKey, IntPtr.Zero);
        Thread.Sleep(20);
        var up = PostMessage(WindowHandle, WmKeyUp, (IntPtr)virtualKey, IntPtr.Zero);

        if (activate && foreground != IntPtr.Zero) SetForegroundWindow(foreground);

        if (!down || !up)
        {
            LastError = $"PostMessage failed (Marshal error {Marshal.GetLastWin32Error()})";
            return false;
        }
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Talks to the CPDLC/ACARS server BeyondATC hosts on localhost while a flight is
/// loaded. Aircraft add-ons use that endpoint to request pre-departure clearances,
/// log on to a centre and exchange altitude/direct-to requests. The protocol is only
/// published to aircraft developers, so this client is deliberately limited to
/// discovery and read-only probing: it reports what the endpoint answers so a session
/// can be observed, and never assumes a request shape that is not documented.
/// </summary>
public sealed class BeyondAtcCpdlcClient : IDisposable
{
    /// <summary>Port BeyondATC logs when it starts the server; overridden by the log line.</summary>
    public const int DefaultHttpPort = 57698;
    public const int DefaultHttpsPort = 57699;

    private static readonly string[] ProbePaths = { "/", "/health", "/api", "/cpdlc", "/api/cpdlc" };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public IReadOnlyList<string> Endpoints { get; private set; } = Array.Empty<string>();
    public string? LastResponse { get; private set; }
    public string? LastError { get; private set; }
    public DateTime LastProbeUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Records the endpoints BeyondATC announced in its log.</summary>
    public void SetEndpoints(IEnumerable<string> endpoints)
    {
        var list = endpoints.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > 0) Endpoints = list;
    }

    /// <summary>Forgets the endpoints, so the documented defaults apply again.</summary>
    public void ClearEndpoints() => Endpoints = Array.Empty<string>();

    /// <summary>Falls back to the documented ports when the log has not been seen yet.</summary>
    public string[] EffectiveEndpoints() => Endpoints.Count > 0
        ? Endpoints.ToArray()
        : new[] { $"http://localhost:{DefaultHttpPort}/" };

    /// <summary>True while <see cref="EffectiveEndpoints"/> is reporting documented defaults.</summary>
    public bool UsingDefaultPorts => Endpoints.Count == 0;

    /// <summary>GETs the root of every candidate endpoint and records what answers.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        LastProbeUtc = DateTime.UtcNow;
        var report = new StringBuilder();

        foreach (var endpoint in EffectiveEndpoints())
        {
            foreach (var path in ProbePaths)
            {
                var url = endpoint.TrimEnd('/') + path;
                try
                {
                    using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    report.AppendLine($"{url} -> {(int)response.StatusCode} {response.StatusCode}");
                    if (!string.IsNullOrWhiteSpace(body))
                        report.AppendLine(Truncate(body.Trim(), 400));
                    LastError = null;
                }
                catch (Exception ex)
                {
                    report.AppendLine($"{url} -> unreachable ({ex.GetType().Name})");
                    LastError = ex.Message;
                }
            }
        }

        LastResponse = report.ToString();
    }

    /// <summary>True when at least one endpoint answered the last probe.</summary>
    public bool IsServing => !string.IsNullOrEmpty(LastResponse) && LastResponse.Contains("-> 2");

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    public void Dispose() => _http.Dispose();
}
