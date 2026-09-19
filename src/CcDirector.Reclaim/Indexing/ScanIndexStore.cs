using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Indexing;

/// <summary>One saved scan, as the list of saved scans describes it.</summary>
/// <param name="IndexPath">The file the scan is saved in.</param>
/// <param name="RootPath">The folder that was scanned.</param>
/// <param name="WrittenUtc">When the file was written.</param>
/// <param name="BytesSeen">The bytes that scan saw.</param>
/// <param name="FilesSeen">The files that scan saw.</param>
public sealed record SavedScan(
    string IndexPath,
    string RootPath,
    DateTimeOffset WrittenUtc,
    long BytesSeen,
    long FilesSeen);

/// <summary>A file in the index folder that could not be read as a saved scan, and why.</summary>
/// <param name="IndexPath">The file.</param>
/// <param name="Reason">Plain ASCII words for why it could not be read.</param>
public sealed record UnreadableIndex(string IndexPath, string Reason);

/// <summary>
/// Every saved scan on this machine, and every file in the same folder that is not one.
///
/// The second list exists so a broken file is never lost between the lines of a listing. A file that
/// cannot be read is named, and the count of saved scans stays a count of scans that were really read.
/// </summary>
/// <param name="Scans">The saved scans, ordered by the folder each one scanned.</param>
/// <param name="Unreadable">The files that could not be read as a saved scan.</param>
public sealed record SavedScanListing(
    IReadOnlyList<SavedScan> Scans,
    IReadOnlyList<UnreadableIndex> Unreadable);

/// <summary>
/// Reads and writes the saved index: the file a scan leaves behind so that nothing has to walk the
/// disk again to show what it found.
/// </summary>
public static class ScanIndexStore
{
    /// <summary>The file extension every saved scan uses.</summary>
    public const string IndexFileExtension = ".json";

    private static readonly JsonSerializerOptions JsonFormat = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Where saved scans live on this machine: one folder for the whole machine, not one per
    /// Director. The scan is about the disk, and there is one disk however many Directors are
    /// running on it - so the Launcher that writes a scan and any Director that reads one resolve
    /// the same folder.
    /// </summary>
    public static string DefaultIndexDirectory() =>
        Path.Combine(CcStorage.MachineRoot(), "reclaim", "index");

    /// <summary>
    /// The file one folder's saved scan is written to.
    ///
    /// The name carries a readable form of the folder so a person can see what is in the directory,
    /// and a short fingerprint of the canonical path so that two different folders can never land on
    /// one file. The fingerprint follows the rule of the platform it runs on. On Windows the file
    /// system does not distinguish letter case, so one folder spelled two ways is one folder and must
    /// be one file: the case is folded before the fingerprint is taken, or a report asked for with a
    /// lowercase drive letter would answer that no scan was ever saved and send the caller to walk
    /// the disk again for nothing. On every other platform the exact bytes are kept, because there
    /// two spellings are two real folders and must never share one file. Trailing separators and dot
    /// segments are folded first by <see cref="DirectoryScanner.Canonical"/>, on every platform.
    /// </summary>
    /// <param name="indexDirectory">The folder saved scans live in.</param>
    /// <param name="rootPath">The folder that was scanned.</param>
    public static string PathFor(string indexDirectory, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(indexDirectory))
            throw new ArgumentException("An index directory cannot be blank.", nameof(indexDirectory));

        FileLog.Write($"[ScanIndexStore] PathFor: indexDirectory={indexDirectory}, rootPath={rootPath}");

        var canonical = DirectoryScanner.Canonical(rootPath);
        var path = Path.Combine(indexDirectory, $"{Label(canonical)}-{Fingerprint(canonical)}{IndexFileExtension}");

        FileLog.Write($"[ScanIndexStore] PathFor done: rootPath={rootPath}, path={path}");
        return path;
    }

    /// <summary>
    /// Write one scan to one file, creating the folder it belongs in.
    /// </summary>
    /// <param name="indexPath">The file to write.</param>
    /// <param name="scan">The scan to save.</param>
    /// <param name="writtenUtc">When the file is being written.</param>
    public static void Save(string indexPath, ScanResult scan, DateTimeOffset writtenUtc)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (string.IsNullOrWhiteSpace(indexPath))
            throw new ArgumentException("An index path cannot be blank.", nameof(indexPath));

        FileLog.Write($"[ScanIndexStore] Save: path={indexPath}, root={scan.RootPath}");

        var folder = Path.GetDirectoryName(Path.GetFullPath(indexPath));
        if (string.IsNullOrEmpty(folder))
            throw new InvalidOperationException($"The index path {indexPath} names no folder to write into.");

        Directory.CreateDirectory(folder);

        var index = new ScanIndex
        {
            Format = ScanIndex.FormatName,
            FormatVersion = ScanIndex.CurrentFormatVersion,
            WrittenUtc = writtenUtc,
            Scan = scan
        };

        File.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonFormat), new UTF8Encoding(false));
        FileLog.Write($"[ScanIndexStore] Save done: path={indexPath}, bytes={scan.BytesSeen}");
    }

    /// <summary>
    /// Read one saved scan back.
    ///
    /// Every way this can go wrong throws with the exact command that puts it right, because a report
    /// built from a file this code did not fully understand would be presented to somebody as fact.
    /// </summary>
    /// <param name="indexPath">The file to read.</param>
    /// <exception cref="FileNotFoundException">There is no saved scan at that path.</exception>
    /// <exception cref="InvalidDataException">The file is not a saved scan this version can read.</exception>
    public static ScanIndex Load(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
            throw new ArgumentException("An index path cannot be blank.", nameof(indexPath));

        FileLog.Write($"[ScanIndexStore] Load: path={indexPath}");

        if (!File.Exists(indexPath))
        {
            FileLog.Write($"[ScanIndexStore] Load FAILED: path={indexPath}, reason=there is no saved scan there");
            throw new FileNotFoundException(
                $"There is no saved scan at {indexPath}. Run: cc-cleanup-storage scan \"<folder>\"",
                indexPath);
        }

        var index = ReadIndexFile(indexPath);

        if (!string.Equals(index.Format, ScanIndex.FormatName, StringComparison.Ordinal))
        {
            FileLog.Write($"[ScanIndexStore] Load FAILED: path={indexPath}, reason=format is {index.Format}");
            throw new InvalidDataException(
                $"The file {indexPath} says it is {index.Format} and a saved scan says it is " +
                $"{ScanIndex.FormatName}. Run: cc-cleanup-storage scan \"<folder>\"");
        }

        if (index.FormatVersion != ScanIndex.CurrentFormatVersion)
        {
            FileLog.Write($"[ScanIndexStore] Load FAILED: path={indexPath}, reason=version {index.FormatVersion}");
            throw new InvalidDataException(
                $"The saved scan at {indexPath} is version " +
                $"{index.FormatVersion.ToString(CultureInfo.InvariantCulture)} and this version of " +
                $"cc-cleanup-storage reads version " +
                $"{ScanIndex.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture)} only. " +
                $"Run: cc-cleanup-storage scan \"{index.Scan.RootPath}\"");
        }

        FileLog.Write($"[ScanIndexStore] Load done: path={indexPath}, root={index.Scan.RootPath}");
        return index;
    }

    /// <summary>
    /// Every saved scan in a folder, and every file in it that is not one.
    ///
    /// A folder that does not exist yet is not an error and is not a mystery: no scan has been saved
    /// on this machine, and both lists come back empty so the caller can say so in as many words.
    /// </summary>
    /// <param name="indexDirectory">The folder saved scans live in.</param>
    public static SavedScanListing List(string indexDirectory)
    {
        if (string.IsNullOrWhiteSpace(indexDirectory))
            throw new ArgumentException("An index directory cannot be blank.", nameof(indexDirectory));

        FileLog.Write($"[ScanIndexStore] List: directory={indexDirectory}");

        var scans = new List<SavedScan>();
        var unreadable = new List<UnreadableIndex>();

        if (!Directory.Exists(indexDirectory))
        {
            FileLog.Write($"[ScanIndexStore] List done: directory={indexDirectory}, scans=0, unreadable=0, " +
                          "reason=the directory does not exist yet");
            return new SavedScanListing(scans, unreadable);
        }

        var files = Directory.GetFiles(indexDirectory, "*" + IndexFileExtension);
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var read = ReadForListing(file);
            if (read.Scan is not null) scans.Add(read.Scan);
            else if (read.Unreadable is not null) unreadable.Add(read.Unreadable);
        }

        scans.Sort(static (left, right) => string.CompareOrdinal(left.RootPath, right.RootPath));

        FileLog.Write($"[ScanIndexStore] List done: directory={indexDirectory}, scans={scans.Count}, " +
                      $"unreadable={unreadable.Count}");
        return new SavedScanListing(scans, unreadable);
    }

    // The one place a saved scan is turned from text into an object. A file that is not readable JSON
    // is an expected outcome of reading a folder full of files, so it leaves here as a named error and
    // never as a half-built index.
    private static ScanIndex ReadIndexFile(string indexPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(indexPath);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[ScanIndexStore] ReadIndexFile FAILED: path={indexPath}, code={ex.HResult}");
            throw new InvalidDataException(
                $"The saved scan at {indexPath} could not be read, code " +
                $"{ex.HResult.ToString(CultureInfo.InvariantCulture)}. " +
                $"Run: cc-cleanup-storage scan \"<folder>\"", ex);
        }

        ScanIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<ScanIndex>(text, JsonFormat);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[ScanIndexStore] ReadIndexFile FAILED: path={indexPath}, reason=not a saved scan");
            throw new InvalidDataException(
                $"The file {indexPath} is not a saved scan. Run: cc-cleanup-storage scan \"<folder>\"", ex);
        }

        if (index is null)
        {
            throw new InvalidDataException(
                $"The file {indexPath} holds nothing. Run: cc-cleanup-storage scan \"<folder>\"");
        }

        return index;
    }

    private static (SavedScan? Scan, UnreadableIndex? Unreadable) ReadForListing(string file)
    {
        try
        {
            var index = Load(file);
            return (new SavedScan(
                file,
                index.Scan.RootPath,
                index.WrittenUtc,
                index.Scan.BytesSeen,
                index.Scan.FilesSeen), null);
        }
        catch (InvalidDataException)
        {
            // Named rather than thrown: one unreadable file must not take the whole listing with it,
            // and it must not disappear from it either. The caller prints both lists.
            return (null, new UnreadableIndex(file, "the file is not a saved scan this version can read"));
        }
    }

    // A readable stem for the file name. Only letters, digits and hyphens survive, so the name is safe
    // on every file system, and it is never the only thing telling two folders apart - the fingerprint
    // beside it does that.
    private static string Label(string canonicalPath)
    {
        var builder = new StringBuilder(canonicalPath.Length);
        foreach (var character in canonicalPath)
        {
            var keep = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (character is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (keep) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var label = builder.ToString().Trim('-');
        if (label.Length > 48) label = label[..48].Trim('-');
        return label.Length == 0 ? "folder" : label;
    }

    // The fingerprint of one canonical path. On Windows the whole path is folded to one case first,
    // because that file system does not tell folders apart by letter case and one folder spelled two
    // ways must be one file. Everywhere else the exact bytes are hashed, because there two spellings
    // are two real folders and must never share one file.
    private static string Fingerprint(string canonicalPath)
    {
        var toHash = OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(toHash));
        return Convert.ToHexStringLower(digest)[..12];
    }
}
