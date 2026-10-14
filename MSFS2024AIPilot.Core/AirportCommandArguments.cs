using System;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Parsing for the console's command arguments. It lives here rather than in the host's
/// <c>Program.cs</c> so the behaviour can be exercised without a console.
/// <para>
/// "airport reload &lt;ICAO&gt;" is the case worth pinning: the reload word has to be
/// consumed <em>before</em> the ICAO is used. Method arguments are evaluated left to
/// right, so a call of the form <c>RequestAirportData(arg, force: IsReloadCommand(ref
/// arg))</c> captured <c>arg</c> as "reload KJFK" and only then stripped the word - the
/// simulator was asked for an airport called "RELOAD KJFK" while the console printed the
/// name the argument had been changed to.
/// </para>
/// </summary>
public static class AirportCommandArguments
{
    /// <summary>
    /// Reads an "airport reload &lt;ICAO&gt;" argument, consuming the reload word and
    /// leaving the ICAO in <paramref name="arg"/>. Returns true when the airport data
    /// should be requested again rather than taken from the cache.
    /// </summary>
    public static bool IsReloadCommand(ref string arg)
    {
        if (arg is null) return false;
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!parts[0].Equals("reload", StringComparison.OrdinalIgnoreCase)) return false;
        arg = parts[1];
        return true;
    }
}
