using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Indexing;

/// <summary>
/// A saved scan, exactly as it is written to disk.
///
/// This file is a CONTRACT between two programs that never run at the same time: the Launcher writes
/// it in the background, and the Director reads it later to put a report on a screen without walking
/// a single folder again. So it carries its own name and its own version from the first commit, and
/// a reader that meets a version it does not know says so and stops. It never guesses at a file it
/// does not recognise, because the numbers in it are about to be shown to somebody as facts.
/// </summary>
public sealed record ScanIndex
{
    /// <summary>What this file is. A file that does not say this is not one of ours.</summary>
    public const string FormatName = "cc-cleanup-storage-index";

    /// <summary>The version of the format this library writes and the only one it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The format name, always <see cref="FormatName"/>.</summary>
    public required string Format { get; init; }

    /// <summary>The format version, always <see cref="CurrentFormatVersion"/> for a file written now.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>When the file was written, in coordinated universal time.</summary>
    public required DateTimeOffset WrittenUtc { get; init; }

    /// <summary>The scan this file holds.</summary>
    public required ScanResult Scan { get; init; }
}
