using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Scanning;

/// <summary>Why one directory could not be listed.</summary>
public enum DirectoryReadRefusal
{
    /// <summary>The directory was listed. There is no refusal.</summary>
    None,

    /// <summary>The operating system refused this account permission to list the directory.</summary>
    AccessDenied,

    /// <summary>The directory was named by its parent and was gone by the time the scan reached it.</summary>
    NotFound,

    /// <summary>The file system failed the listing for some other reason. The numeric code is kept.</summary>
    ReadFailed
}

/// <summary>One entry in a directory listing, as the scan sees it.</summary>
/// <param name="FullPath">The full path of the entry.</param>
/// <param name="Kind">What the entry is.</param>
/// <param name="LengthBytes">The length the file system reports for a file; zero for a directory or a link.</param>
/// <param name="LinkTarget">What a link names, when the file system will say; null otherwise.</param>
public sealed record ScannedEntry(
    string FullPath,
    FileSystemEntryKind Kind,
    long LengthBytes,
    string? LinkTarget);

/// <summary>
/// The answer to "what is in this directory": either its entries, or the reason it could not be
/// listed. A directory that refuses access is an ordinary, expected outcome of scanning a disk - 244
/// of them refused during the design measurement - so it is returned as a value and reported, never
/// thrown away and never quietly skipped.
///
/// This is the result-object pattern the coding style guide sets out for expected failures, and it is
/// the only place in the engine that turns a file system exception into a value. Everything above it
/// reads a refusal rather than catching one.
/// </summary>
public sealed class DirectoryReadResult
{
    private DirectoryReadResult(
        IReadOnlyList<ScannedEntry> entries,
        DirectoryReadRefusal refusal,
        int refusalCode)
    {
        Entries = entries;
        Refusal = refusal;
        RefusalCode = refusalCode;
    }

    /// <summary>The entries that were listed. Empty when the directory was refused.</summary>
    public IReadOnlyList<ScannedEntry> Entries { get; }

    /// <summary>Why the directory could not be listed, or <see cref="DirectoryReadRefusal.None"/>.</summary>
    public DirectoryReadRefusal Refusal { get; }

    /// <summary>
    /// The numeric code the file system gave with a failure, or zero. The operating system's own
    /// message is deliberately NOT kept: it is translated into the machine's display language, and
    /// every line this product prints is plain ASCII. A number says the same thing in every language.
    /// </summary>
    public int RefusalCode { get; }

    /// <summary>True when the directory was listed.</summary>
    public bool WasRead => Refusal == DirectoryReadRefusal.None;

    /// <summary>
    /// List one directory without following anything. Reads attributes and lengths from the directory
    /// entries themselves, so no file is ever opened and a cloud placeholder is never recalled.
    /// </summary>
    /// <param name="directoryPath">The directory to list.</param>
    public static DirectoryReadResult Read(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("A directory path cannot be blank.", nameof(directoryPath));

        // A listing that fails part way through is reported as a refusal with NO entries, not as the
        // part that was read. The scan's whole contract is that every byte it reports was seen and
        // every place it could not see is named; half a listing presented as a whole one breaks the
        // first half of that promise silently, and the refused folder line already carries the second.
        var entries = new List<ScannedEntry>();

        try
        {
            var options = new EnumerationOptions
            {
                // Every one of these is deliberate. Nothing is hidden from the count, nothing is
                // skipped for being awkward, and a failure is never swallowed by the enumerator - it
                // comes back here as an exception and leaves as a named refusal.
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple
            };

            foreach (var info in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", options))
            {
                var isDirectory = info is DirectoryInfo;
                var kind = EntryClassifier.Classify(info.Attributes, isDirectory);
                var length = info is FileInfo file ? file.Length : 0L;
                entries.Add(new ScannedEntry(info.FullName, kind, length, info.LinkTarget));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLog.Write($"[DirectoryReadResult] Read refused: path={directoryPath}, reason=access denied");
            return new DirectoryReadResult([], DirectoryReadRefusal.AccessDenied, ex.HResult);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileLog.Write($"[DirectoryReadResult] Read refused: path={directoryPath}, reason=not found");
            return new DirectoryReadResult([], DirectoryReadRefusal.NotFound, ex.HResult);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[DirectoryReadResult] Read refused: path={directoryPath}, reason=read failed, code={ex.HResult}");
            return new DirectoryReadResult([], DirectoryReadRefusal.ReadFailed, ex.HResult);
        }

        return new DirectoryReadResult(entries, DirectoryReadRefusal.None, 0);
    }
}
