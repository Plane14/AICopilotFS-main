namespace MSFS2024AIPilot.Core.Aircraft;

/// <summary>
/// Operational class of the loaded aircraft. Drives which ground/air operations
/// the pilot may use (runways, water lanes, vertical lift).
/// </summary>
public enum AircraftKind
{
    /// <summary>Conventional wheeled fixed-wing aircraft on land runways.</summary>
    Airplane,

    /// <summary>Helicopter / gyroplane — vertical takeoff, no runway phases.</summary>
    Rotorcraft,

    /// <summary>Floatplane — can only operate from water.</summary>
    Seaplane,

    /// <summary>Amphibian — wheels + floats; can use land and water runways.</summary>
    Amphibian,
}

/// <summary>
/// Classifies the loaded aircraft from its package directory, parsed
/// performance data, and title. Detection is deliberately conservative: an
/// ambiguous aircraft stays <see cref="AircraftKind.Airplane"/>, while float
/// evidence upgrades to <see cref="AircraftKind.Seaplane"/> or
/// <see cref="AircraftKind.Amphibian"/>. The sim's <c>IS GEAR FLOATS</c> SimVar
/// is used at runtime to upgrade wheeled aircraft that turn out to carry
/// floats (amphibians).
/// </summary>
public static class AircraftKindDetector
{
    private static readonly string[] RotorKeywords =
    [
        "ROTOR", "HELICOPTER", "HELIPLANE", "HELI ", "H125", "B407", "B505",
        "EC135", "EC145", "AS350", "AS355", "UH-1", "UH1", "HUEY", "CH-47", "CH47",
        "ROBINSON", " R22", "R44", "R66", "S-76", "S76", "AW139", "AW109",
        "MD500", "MD 500", "MD520", "BELL 206", "B206", "KA-32", "MI-8", "MI8",
        "EC30", "H160", "NH90", "BK117", "H145", "CABRI", "ENSTROM",
    ];

    // Float evidence — title-ish and explicit config markers only. Bare
    // "FLOAT" is intentionally absent: it appears in unrelated cfg text and
    // would misclassify landplanes.
    private static readonly string[] FloatKeywords =
    [
        "SEAPLANE", "FLOAT PLANE", "FLOATPLANE", "FLOAT_PLANE", "ON FLOATS",
        "FLOATS ONLY", "AMPHIBIAN", "WIDECAT", "FLOAT MAINTAINER", "LAKE SPORT",
        // well-known floatplane/amphibian type names
        "BEAVER", "CUB FLOAT", "CUBS FLOAT", "CUBS ON FLOATS", "ICON A5",
        "CARAVAN FLOAT", "C-208 FLOAT", "AT-802", "AT802", "FIRE BOSS",
        "DHC-2", "DHC2", "OTTER", "PBY", "CATALINA", "GRUMMAN", "WIDGEON",
        "MALLARD", "LANCAIR", "BUCCANEER", "SUPERCAT", "SUPER CUB FLOAT",
        "F4U CORSAIR", "OS2U", "J2F", "S-38", "NC-4",
    ];

    private static readonly string[] AmphibianKeywords =
    [
        "AMPHIB", "DHC-2T", "G-21", "GRUMMAN G-21", "LAKE AMPHIB",
        "CARAVAN AMPHIB", "C-208 AMPHIB", "CUB AMPHIB", "OTTER AMPHIB",
        "S-38 AMPHIB", "WIDECAT AMPHIB",
    ];

    /// <summary>
    /// Detect the kind. <paramref name="aircraftDir"/> may be null/empty —
    /// detection then falls back to title and parsed performance only.
    /// </summary>
    public static AircraftKind Detect(string? aircraftDir, string? title, AircraftPerformance performance)
    {
        var meta = BuildMetaText(aircraftDir, title, performance);
        if (IsRotor(meta, performance)) return AircraftKind.Rotorcraft;

        var config = ScanConfigFiles(aircraftDir);
        if (IsSeaplane(meta + "\n" + config))
            return IsAmphibian(meta + "\n" + config) ? AircraftKind.Amphibian : AircraftKind.Seaplane;
        return AircraftKind.Airplane;
    }

    /// <summary>True when the aircraft class needs water for its operations.</summary>
    public static bool IsWaterKind(AircraftKind kind) =>
        kind is AircraftKind.Seaplane or AircraftKind.Amphibian;

    /// <summary>
    /// Runtime upgrade: the sim reports <c>IS GEAR FLOATS</c> when an
    /// amphibian's floats are deployed. A fixed-wing aircraft that reports
    /// floats becomes an amphibian; it is never downgraded here.
    /// </summary>
    public static AircraftKind ObserveGearFloats(AircraftKind current) =>
        current is AircraftKind.Airplane ? AircraftKind.Amphibian : current;

    private static bool IsRotor(string meta, AircraftPerformance performance)
    {
        if (performance.EngineType.Contains("ROTOR", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var kw in RotorKeywords)
            if (meta.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsSeaplane(string text)
    {
        foreach (var kw in FloatKeywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsAmphibian(string text)
    {
        foreach (var kw in AmphibianKeywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string BuildMetaText(string? aircraftDir, string? title, AircraftPerformance performance)
    {
        var dirName = aircraftDir is null ? "" : Path.GetFileName(aircraftDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Join('\n', aircraftDir ?? "", dirName, title ?? "",
            performance.Title, performance.IcaoType, performance.Category, performance.ApproachCategory);
    }

    /// <summary>
    /// Returns the concatenated text of the package's .cfg files (capped).
    /// Used only as secondary float evidence.
    /// </summary>
    private static string ScanConfigFiles(string? aircraftDir)
    {
        if (string.IsNullOrWhiteSpace(aircraftDir) || !Directory.Exists(aircraftDir)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var cfg in Directory.EnumerateFiles(aircraftDir, "*.cfg", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(cfg);
                if (fi.Length > 256 * 1024) continue;
                sb.Append(File.ReadAllText(cfg)).Append('\n');
            }
            catch { /* unreadable cfg — ignore */ }
        }
        return sb.ToString();
    }
}
