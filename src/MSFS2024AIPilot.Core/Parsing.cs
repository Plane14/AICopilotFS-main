using System.Globalization;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Locale-independent numeric parsing. Every value this product reads comes from a
/// simulator file, a SimConnect message or an ATC string, and all of those use "."
/// as the decimal separator. On a machine whose locale treats "." as a group
/// separator (es-ES, de-DE, pt-BR, ...) the framework's default culture would read
/// "22.44" as 2244 and "34000.000" as 34000000, so every parse goes through here.
/// </summary>
/// <remarks>
/// The real-number style deliberately does <b>not</b> accept a group separator. With
/// AllowThousands a value written with a comma as the decimal mark parses as a grouped
/// integer instead - "2,90" became 290, and "1,23" became 123, which is a hundred-times
/// error in exactly the class this file exists to prevent. A comma-separated group in an
/// ATC phrase ("MAINTAIN 3,000") is a spelled-out grouping, and it is stripped by the
/// caller that understands it; here a comma is simply not part of a number.
/// </remarks>
public static class Parsing
{
    private const NumberStyles RealNumber = NumberStyles.Float;
    private const NumberStyles WholeNumber = NumberStyles.Integer;

    /// <summary>
    /// Parses a real number that may be written with digit grouping, such as an ATC
    /// phrase's "3,000". The grouping is removed before parsing, so the decimal separator
    /// stays a "." and "2,90" is not silently multiplied by a hundred.
    /// </summary>
    public static bool TryGroupedDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var grouped = text.Trim().Replace(",", string.Empty);
        return double.TryParse(grouped, RealNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A real number written with digit grouping, such as "3,000".</summary>
    public static double GroupedDouble(string? text) => TryGroupedDouble(text, out var d) ? d : 0;

    public static bool TryDouble(string? text, out double value)
        => double.TryParse(text, RealNumber, CultureInfo.InvariantCulture, out value);

    public static bool TryInt32(string? text, out int value)
        => int.TryParse(text, WholeNumber, CultureInfo.InvariantCulture, out value);

    public static bool TryUInt32(string? text, out uint value)
        => uint.TryParse(text, WholeNumber, CultureInfo.InvariantCulture, out value);

    public static double Double(string? text) => TryDouble(text, out var d) ? d : 0;

    public static int Int32(string? text) => TryInt32(text, out var i) ? i : 0;

    public static uint UInt32(string? text) => TryUInt32(text, out var u) ? u : 0;

    /// <summary>Formats a double with an invariant "." separator.</summary>
    public static string Format(double value, string format = "0.##")
        => value.ToString(format, CultureInfo.InvariantCulture);
}
