using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MSFS2024AIPilot.Core;

/// <summary>A structured event parsed out of BeyondATC's Player.log.</summary>
public sealed class BeyondAtcLogEvent
{
    public required string Tag { get; init; }
    public required string Text { get; init; }
    public int LineNumber { get; init; }
    public DateTime SeenUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Parses BeyondATC's Player.log. That file is the Unity engine log, so it carries no
/// speech transcript, but it does carry everything an external program can know about
/// the session: the assigned runways, the aircraft identity BeyondATC imported, the
/// hotkeys it imported into its preferences, and the local CPDLC server endpoint.
/// Instructions themselves are not in the file - see <see cref="AtcCommunicationHub"/>
/// for how the station/frequency state is followed instead.
/// </summary>
public static class BeyondAtcLogParser
{
    /// <summary>Directory BeyondATC keeps its logs in (LocalLow, Unity's per-user data root).</summary>
    public static string DefaultDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Skirmish Mode Games, Inc", "BeyondATC");

    public static string DefaultPlayerLog => System.IO.Path.Combine(DefaultDirectory, "Player.log");

    private static readonly Regex Tagged = new(@"^\s*\[(?<tag>[A-Za-z0-9_]+)\]\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses one line; returns null for blank or tag-less lines.</summary>
    public static BeyondAtcLogEvent? ParseLine(string? line, int lineNumber = 0, DateTime? seenUtc = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = Tagged.Match(line);
        if (!match.Success) return null;

        return new BeyondAtcLogEvent
        {
            Tag = match.Groups["tag"].Value,
            Text = match.Groups["text"].Value.Trim(),
            LineNumber = lineNumber,
            SeenUtc = seenUtc ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Reads the hotkey virtual-key codes BeyondATC imported, e.g.
    /// "[Bindings] Hotkeys imported to PlayerPrefs: [0, 32, 0, 0, 0] (defaults)".
    /// The order is the binding order in BeyondATC's options (push-to-talk slots,
    /// bring-to-front, fire action 1). A zero entry means the slot is unbound.
    /// </summary>
    public static IReadOnlyList<int>? ParseHotkeys(BeyondAtcLogEvent e)
    {
        if (!string.Equals(e.Tag, "Bindings", StringComparison.OrdinalIgnoreCase)) return null;

        var open = e.Text.IndexOf('[');
        var close = e.Text.IndexOf(']');
        if (open < 0 || close < open) return null;

        var values = new List<int>();
        foreach (var part in e.Text[(open + 1)..close].Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vk))
                values.Add(vk);
            else
                values.Add(0);
        }
        return values;
    }

    /// <summary>
    /// Reads the CPDLC endpoint BeyondATC started, e.g.
    /// "[CPDLC] http endpoint listening on http://localhost:57698/ (...)".
    /// This is the only API the application exposes, and it exists so aircraft
    /// add-ons can do CPDLC/ACARS with it.
    /// </summary>
    public static IReadOnlyList<string> ParseCpdlcEndpoints(BeyondAtcLogEvent e)
    {
        if (!string.Equals(e.Tag, "CPDLC", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();

        var endpoints = new List<string>();
        foreach (Match m in Regex.Matches(e.Text, @"https?://[^\s)\]]+", RegexOptions.CultureInvariant))
            endpoints.Add(m.Value);
        return endpoints;
    }

    /// <summary>
    /// Reads the departure/arrival runway BeyondATC allocated, e.g.
    /// "[RunwayAlloc] ASXGS planned= validRunways=[24L] ... result=24L cost=590".
    /// The leading token is the callsign, so a line is only about the player when the
    /// callsign matches the one the aircraft reports (checked by the caller).
    /// </summary>
    public static (string Callsign, string Runway)? ParseRunwayAllocation(BeyondAtcLogEvent e)
    {
        if (!string.Equals(e.Tag, "RunwayAlloc", StringComparison.OrdinalIgnoreCase)) return null;

        var result = Regex.Match(e.Text, @"result=(?<rwy>\S+)", RegexOptions.CultureInvariant);
        if (!result.Success) return null;

        var fields = e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0) return null;

        return (fields[0], result.Groups["rwy"].Value);
    }

    /// <summary>
    /// Reads the identity BeyondATC imported from the sim, e.g.
    /// "[MsfsImport] Aircraft identity: icaoType=C172, id=ASXGS, airframe=Cessna 172".
    /// </summary>
    public static (string IcaoType, string Ident, string Airframe)? ParseAircraftIdentity(BeyondAtcLogEvent e)
    {
        if (!string.Equals(e.Tag, "MsfsImport", StringComparison.OrdinalIgnoreCase)) return null;
        if (!e.Text.StartsWith("Aircraft identity:", StringComparison.OrdinalIgnoreCase)) return null;

        string? icao = null, ident = null, airframe = null;
        foreach (var field in e.Text[(e.Text.IndexOf(':') + 1)..].Split(','))
        {
            var kv = field.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "icaotype": icao = kv[1]; break;
                case "id": ident = kv[1]; break;
                case "airframe": airframe = kv[1]; break;
            }
        }
        return icao is null || ident is null ? null : (icao, ident, airframe ?? "");
    }

    /// <summary>
    /// Reads a "[Comms] 124.850 not found" style complaint. BeyondATC tunes COM1 itself,
    /// so this is the signal that its view of the frequency and the radio disagree.
    /// </summary>
    public static double? ParseUnknownFrequency(BeyondAtcLogEvent e)
    {
        if (!string.Equals(e.Tag, "Comms", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(e.Text, @"(?<mhz>\d{2,3}\.\d{2,3})\s+not found", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        return double.TryParse(m.Groups["mhz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            ? mhz
            : null;
    }

    /// <summary>True for the lifecycle lines that mark a flight starting or ending.</summary>
    public static bool IsSessionBoundary(BeyondAtcLogEvent e) =>
        (string.Equals(e.Tag, "Simconnect", StringComparison.OrdinalIgnoreCase) &&
         e.Text.Contains("closed", StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(e.Tag, "CPDLC", StringComparison.OrdinalIgnoreCase) &&
         (e.Text.Contains("Server stopped", StringComparison.OrdinalIgnoreCase) ||
          e.Text.Contains("Server started", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Follows BeyondATC's Player.log. The file is recreated when BeyondATC starts a
/// flight, so the tail resets whenever the length shrinks, and the file is opened with
/// shared access so the running application is never blocked.
/// </summary>
public sealed class BeyondAtcLogTail : IDisposable
{
    private readonly string _path;

    /// <summary>
    /// The most bytes one poll reads. A log that has been rewritten while nothing re-read
    /// it can otherwise hand the whole file to the decoder in one go.
    /// </summary>
    private const int MaxReadBytes = 1 << 20;
    private readonly Action<BeyondAtcLogEvent> _onEvent;
    private readonly Action<string>? _onError;
    private long _offset;
    private string _partial = "";
    private int _lineNumber;
    private bool _disposed;

    public string Path => _path;
    public bool IsReading { get; private set; }
    public long BytesRead { get; private set; }
    public int LastLineNumber => _lineNumber;
    public DateTime LastReadUtc { get; private set; } = DateTime.MinValue;

    public BeyondAtcLogTail(string path, Action<BeyondAtcLogEvent> onEvent, Action<string>? onError = null)
    {
        _path = path;
        _onEvent = onEvent;
        _onError = onError;
    }

    /// <summary>
    /// Reads everything appended since the last poll. Only complete lines are emitted;
    /// a trailing partial line is buffered until its terminator arrives.
    /// </summary>
    public void Poll()
    {
        if (_disposed) return;
        LastReadUtc = DateTime.UtcNow;

        System.IO.FileStream? stream = null;
        try
        {
            if (!System.IO.File.Exists(_path)) return;

            var info = new System.IO.FileInfo(_path);
            // The log is truncated and rewritten when a new flight starts.
            if (info.Length < _offset)
            {
                _offset = 0;
                _partial = "";
                _lineNumber = 0;
            }

            if (info.Length == _offset) return;

            stream = new System.IO.FileStream(_path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            stream.Seek(_offset, System.IO.SeekOrigin.Begin);
            var startBytes = stream.Position; // equals _offset once seeked

            // The bytes are read and decoded here rather than handed to a StreamReader,
            // because the byte offset and the character stream have to stay in step: the
            // offset the next poll seeks to is a byte count (FileStream.Seek and
            // FileInfo.Length work in bytes), while the text is counted in characters. A
            // reader strips a UTF-8 BOM and replaces invalid bytes with U+FFFD, so both of
            // those diverge from the real file position - the BOM makes the region after it
            // read again and emitted twice, an invalid byte pushes the offset past real
            // content and drops a line.
            var length = (int)Math.Min(info.Length - startBytes, MaxReadBytes);
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var got = stream.Read(bytes, read, length - read);
                if (got <= 0) break;
                read += got;
            }

            _offset = startBytes + read;
            BytesRead += read;
            IsReading = read > 0;

            if (read == 0) return;

            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, read);

            var buffer = _partial + text;
            var lines = buffer.Split('\n');
            _partial = lines[^1];

            // A read can end in the middle of a multi-byte character: the bytes are
            // decoded to U+FFFD here, and the continuation arrived at the start of the next
            // poll would have been read as a line of its own. Holding the replacement
            // character back means the pair is reassembled on the next read instead of a
            // corrupted line being emitted now.
            if (_partial.EndsWith("\uFFFD", StringComparison.Ordinal))
            {
                _partial = _partial.TrimEnd('\uFFFD');
            }

            for (int i = 0; i < lines.Length - 1; i++)
            {
                _lineNumber++;
                var line = lines[i].TrimEnd('\r');
                var parsed = BeyondAtcLogParser.ParseLine(line, _lineNumber, LastReadUtc);
                if (parsed != null) _onEvent(parsed);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The application is mid-write or the file is locked for the instant of the
            // rotation; the next poll picks the content up.
            _onError?.Invoke($"BeyondATC log read deferred: {ex.Message}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>Forgets the position so the next poll re-reads the file.</summary>
    public void Reset()
    {
        _offset = 0;
        _partial = "";
        _lineNumber = 0;
    }

    public void Dispose() => _disposed = true;
}
