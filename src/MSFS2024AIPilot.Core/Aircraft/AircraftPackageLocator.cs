using System;
using System.IO;

namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>
/// Resolves an aircraft package directory WITHOUT scanning the install: the
/// only accepted input is a direct path hint (the AircraftLoaded system state
/// hands us the exact aircraft.cfg location). Package/VFS scanning was removed
/// — it took ~100 s over the VFS projection and stalled engine start; aircraft
/// performance and checklist data come from the bundled flightchecklist.app
/// library keyed by the TITLE SimVar instead.
/// </summary>
public static class AircraftPackageLocator
{
    /// <summary>
    /// Resolves <paramref name="rootOverride"/> (a directory or a file inside
    /// it, e.g. the aircraft.cfg path from AircraftLoaded) to the package
    /// directory. Returns null when the hint does not point at an aircraft
    /// package. Never scans: a path that does not already look like a package
    /// is rejected rather than searched for.
    /// </summary>
    public static string? LocateAircraftDirectory(string title, string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rootOverride)) return null;
        var dir = ResolveOverrideDirectory(rootOverride);
        if (string.IsNullOrEmpty(dir)) return null;
        if (File.Exists(Path.Combine(dir, "aircraft.cfg")) ||
            File.Exists(Path.Combine(dir, "flight_model.cfg")))
            return dir;
        return null;
    }

    private static string? ResolveOverrideDirectory(string? rootOverride)
    {
        if (string.IsNullOrWhiteSpace(rootOverride)) return null;
        // The override may be the .cfg file itself or a directory containing it.
        if (File.Exists(rootOverride))
            return Path.GetDirectoryName(rootOverride);
        return Directory.Exists(rootOverride) ? rootOverride : null;
    }

    /// <summary>
    /// MSFS ships unresolved localization tokens in cfg titles and the sim's
    /// TITLE variable ("$$:Cessna $$:172"). Strip the token markers so the GUI
    /// and log show "Cessna 172".
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(title, @"\$\$:\s*", "");
        t = t.Replace("$$", "").Trim();
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ");
    }
}
