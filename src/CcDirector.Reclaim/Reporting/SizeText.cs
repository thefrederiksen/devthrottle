using System.Globalization;

namespace CcDirector.Reclaim.Reporting;

/// <summary>
/// Writes a number of bytes the way a report says it: the exact count, and beside it the same number
/// in words a person reads.
///
/// A kilobyte here is 1024 bytes, a megabyte 1024 kilobytes, and so on. That is what Windows shows a
/// person in its own windows, and a report that disagreed with the operating system about the size of
/// the same folder would be read as wrong even when it was right. The exact byte count is always
/// printed as well, so nothing here is ever the only answer.
/// </summary>
public static class SizeText
{
    private const long BytesInOneKilobyte = 1024L;

    private static readonly string[] UnitNames =
    [
        "kilobytes", "megabytes", "gigabytes", "terabytes", "petabytes"
    ];

    /// <summary>
    /// The size in words: "512 bytes", "1.3 kilobytes", "703.1 gigabytes". A negative number keeps
    /// its sign, because the difference between what a scan saw and what a volume counts can fall on
    /// either side and a report never hides which.
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    public static string Describe(long bytes)
    {
        if (bytes > -BytesInOneKilobyte && bytes < BytesInOneKilobyte)
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes";

        var negative = bytes < 0;
        // long.MinValue has no positive counterpart, so the size is taken as a double from the start.
        var size = Math.Abs((double)bytes) / BytesInOneKilobyte;
        var unit = 0;

        while (size >= BytesInOneKilobyte && unit < UnitNames.Length - 1)
        {
            size /= BytesInOneKilobyte;
            unit++;
        }

        var sign = negative ? "-" : string.Empty;
        return $"{sign}{size.ToString("F1", CultureInfo.InvariantCulture)} {UnitNames[unit]}";
    }

    /// <summary>
    /// A number of bytes as a report line writes it: the exact count in bytes, and the size in words
    /// beside it once the two differ.
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    public static string Exact(long bytes)
    {
        var exact = $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes";
        var described = Describe(bytes);
        return exact == described ? exact : $"{exact} ({described})";
    }
}
