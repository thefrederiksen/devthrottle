using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Reporting;

/// <summary>What a report says about the scan behind it.</summary>
public enum ReportVerdict
{
    /// <summary>The scan measured the disk and the report can be believed.</summary>
    Ok,

    /// <summary>
    /// The scan is a broken instrument and its numbers mean nothing. An empty or zero result lands
    /// here, and so does a volume that would not say how much of it is used - both of them say
    /// BROKEN rather than "nothing here", because the two look the same and only one of them is
    /// safe to act on.
    /// </summary>
    Broken
}

/// <summary>
/// A finished report on one scan.
///
/// The engine decides what the scan means and writes the sentences; nothing that renders this report
/// works any of it out again. A screen prints <see cref="Lines"/> as they stand and a command line
/// tool prints the same lines, so the answer a person reads on a phone is the answer an agent reads
/// in a terminal, word for word. That is critical rule 7 in CLAUDE.md, and it is why no caller is
/// given the numbers without the sentences.
/// </summary>
public sealed record ScanReport
{
    /// <summary>Whether the scan behind this report can be believed.</summary>
    public required ReportVerdict Verdict { get; init; }

    /// <summary>Why the scan is a broken instrument, in one finished sentence, or null when it is not.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What the scan saw and what it could not see.</summary>
    public required ScanResult Scan { get; init; }

    /// <summary>
    /// The bytes the volume counts as used that this scan did not see, or null when the volume would
    /// not say how much of it is used and the difference therefore has only one side.
    /// </summary>
    public required long? UnseenBytes { get; init; }

    /// <summary>The largest folders the scan recorded, largest first.</summary>
    public required IReadOnlyList<FolderTotal> LargestFolders { get; init; }

    /// <summary>The report itself: finished sentences, printed as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}
