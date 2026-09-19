using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Reclaim.Scanning;

namespace CcCleanupStorage;

/// <summary>A folder that refused its listing, with the plain words for why beside the code.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Reason">Plain words for why the listing was refused.</param>
/// <param name="Code">The number the file system gave.</param>
public sealed record RefusedFolderJson(string Path, string Reason, int Code);

/// <summary>What one folder holds.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Depth">How far below the scan root it sits.</param>
/// <param name="BytesSeen">Bytes seen in it and everything under it.</param>
/// <param name="FilesSeen">Files seen in it and everything under it.</param>
public sealed record FolderJson(string Path, int Depth, long BytesSeen, long FilesSeen);

/// <summary>
/// A whole report, as a machine reads it. Every field the report holds is here: this is the shape
/// other code parses, so it does not shrink to suit a screen and it does not change when a filter is
/// applied - the rows change, the shape does not.
/// </summary>
public sealed record ReportJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the command succeeded and the report can be believed.</summary>
    public required bool Ok { get; init; }

    /// <summary>Either "ok" or "broken".</summary>
    public required string Verdict { get; init; }

    /// <summary>Why the scan is a broken instrument, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>The folder that was scanned.</summary>
    public required string RootPath { get; init; }

    /// <summary>True when that folder is the root of its own volume.</summary>
    public required bool RootIsVolumeRoot { get; init; }

    /// <summary>Where the scan is saved, or null when nothing was saved.</summary>
    public required string? IndexPath { get; init; }

    /// <summary>When the walk started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the walk finished.</summary>
    public required DateTimeOffset FinishedUtc { get; init; }

    /// <summary>How long the walk took.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Ordinary files seen.</summary>
    public required long FilesSeen { get; init; }

    /// <summary>Folders seen, not counting the scan root.</summary>
    public required long FoldersSeen { get; init; }

    /// <summary>Bytes of ordinary files seen.</summary>
    public required long BytesSeen { get; init; }

    /// <summary>
    /// The bytes the volume counts as used that the scan did not see, or null when the volume would
    /// not say how much of it is used.
    /// </summary>
    public required long? UnseenBytes { get; init; }

    /// <summary>What the volume says about itself.</summary>
    public required VolumeUsage Volume { get; init; }

    /// <summary>Cloud placeholder files seen.</summary>
    public required long PlaceholderFiles { get; init; }

    /// <summary>The bytes those placeholders hold in a cloud store, none of them on this disk.</summary>
    public required long PlaceholderBytesInCloud { get; init; }

    /// <summary>Every link and junction found, and none of them were followed.</summary>
    public required IReadOnlyList<FoundLink> Links { get; init; }

    /// <summary>Every folder that refused its listing.</summary>
    public required IReadOnlyList<RefusedFolderJson> RefusedFolders { get; init; }

    /// <summary>The largest folders, biggest first, as many as were asked for.</summary>
    public required IReadOnlyList<FolderJson> LargestFolders { get; init; }

    /// <summary>The report itself, in finished sentences, exactly as the text answer prints them.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>One saved scan, as the list of saved scans gives it.</summary>
/// <param name="RootPath">The folder that was scanned.</param>
/// <param name="ScannedUtc">When the scan was saved.</param>
/// <param name="BytesSeen">The bytes that scan saw.</param>
/// <param name="FilesSeen">The files that scan saw.</param>
/// <param name="IndexPath">The file it is saved in.</param>
public sealed record SavedScanJson(
    string RootPath,
    DateTimeOffset ScannedUtc,
    long BytesSeen,
    long FilesSeen,
    string IndexPath);

/// <summary>A file in the index folder that is not a saved scan.</summary>
/// <param name="IndexPath">The file.</param>
/// <param name="Reason">Why it could not be read.</param>
public sealed record UnreadableIndexJson(string IndexPath, string Reason);

/// <summary>The saved scans on this machine, as a machine reads them.</summary>
public sealed record SavedScansJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True: a listing with nothing in it is still a listing.</summary>
    public required bool Ok { get; init; }

    /// <summary>The folder saved scans live in.</summary>
    public required string IndexDirectory { get; init; }

    /// <summary>How many saved scans were read.</summary>
    public required int Count { get; init; }

    /// <summary>The saved scans.</summary>
    public required IReadOnlyList<SavedScanJson> Scans { get; init; }

    /// <summary>The files in the same folder that are not saved scans.</summary>
    public required IReadOnlyList<UnreadableIndexJson> Unreadable { get; init; }
}

/// <summary>Anything that went wrong, as a machine reads it.</summary>
public sealed record ErrorJson
{
    /// <summary>The command that was asked for.</summary>
    public required string Command { get; init; }

    /// <summary>Always false.</summary>
    public required bool Ok { get; init; }

    /// <summary>A short word an agent can branch on: usage, folder-not-found, no-saved-scan, and so on.</summary>
    public required string Code { get; init; }

    /// <summary>What went wrong, in one line, and what to do about it.</summary>
    public required string Message { get; init; }
}

/// <summary>The one place this tool decides how machine-readable text is shaped.</summary>
public static class JsonShape
{
    /// <summary>
    /// Indented, with names in the usual style for this kind of output, and every enumeration written
    /// as its name rather than as a number - a number in a saved answer means nothing to the reader
    /// and changes meaning the day somebody adds a value in the middle.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
