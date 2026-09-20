using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Scanning;

/// <summary>
/// Walks a folder and counts what is under it.
///
/// It is an ordinary recursive directory walk and nothing cleverer, on purpose. The owner's ruling for
/// this mission was that speed is not a constraint - the scan runs in the background, offline and
/// slowly - so there is no fast path that reads the file table, and there is not meant to be one. A
/// plain walk was measured at twenty thousand to sixty thousand entries a second on the machine this
/// was designed for, and that is accepted.
///
/// What it will not do matters more than what it does:
///
/// - it never follows a link or a junction, and counts every one it meets;
/// - it never opens a file, so a cloud placeholder is never pulled back from the cloud to be measured;
/// - it never passes over a folder it could not read. Every one is counted and named.
/// </summary>
public sealed class DirectoryScanner
{
    // One folder on the walk. Held in a stack rather than recursed, so how deep the walk can go is
    // decided by the disk and not by the size of a thread's call stack.
    private sealed class Frame
    {
        public required string Path { get; init; }
        public required int Depth { get; init; }
        public Frame? Parent { get; init; }
        public long BytesBelow { get; set; }
        public long FilesBelow { get; set; }
        public int PendingChildren { get; set; }
    }

    /// <summary>
    /// Walk the folder and return everything seen and everything refused.
    /// </summary>
    /// <param name="options">What to walk, and how deep to record folder totals.</param>
    /// <exception cref="DirectoryNotFoundException">The root folder does not exist.</exception>
    public ScanResult Scan(ScanOptions options) => Scan(options, CancellationToken.None);

    /// <summary>
    /// Walk the folder and return everything seen and everything refused, unless asked to stop.
    ///
    /// The request to stop is looked at once for every folder, which is often enough for a walk that
    /// was measured at 1,406 seconds on a whole disk to stop within a moment of being asked, and costs
    /// nothing worth measuring. A walk that is stopped returns NOTHING: it throws, so that no caller
    /// can be handed the part of a disk that happened to be walked and take it for the whole of one.
    /// </summary>
    /// <param name="options">What to walk, and how deep to record folder totals.</param>
    /// <param name="stop">Asks the walk to stop.</param>
    /// <exception cref="DirectoryNotFoundException">The root folder does not exist.</exception>
    /// <exception cref="OperationCanceledException">The walk was asked to stop before it finished.</exception>
    public ScanResult Scan(ScanOptions options, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(options);
        FileLog.Write($"[DirectoryScanner] Scan: root={options.RootPath}, maxFolderDepth={options.MaxFolderDepth}");

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("A scan root cannot be blank.", nameof(options));

        if (options.MaxFolderDepth < ScanOptions.MinimumFolderDepth ||
            options.MaxFolderDepth > ScanOptions.MaximumFolderDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxFolderDepth,
                $"The folder depth must be between {ScanOptions.MinimumFolderDepth} and {ScanOptions.MaximumFolderDepth}.");
        }

        var root = Canonical(options.RootPath);

        if (!Directory.Exists(root))
        {
            FileLog.Write($"[DirectoryScanner] Scan FAILED: root={root}, reason=the folder does not exist");
            throw new DirectoryNotFoundException(
                $"There is no folder at {root}. Give the scan a folder that exists.");
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();

        long filesSeen = 0;
        long foldersSeen = 0;
        long bytesSeen = 0;
        long placeholderFiles = 0;
        long placeholderBytes = 0;
        var links = new List<FoundLink>();
        var refused = new List<RefusedFolder>();
        var folderTotals = new List<FolderTotal>();

        var toExpand = new Stack<Frame>();
        toExpand.Push(new Frame { Path = root, Depth = 0, Parent = null });

        while (toExpand.Count > 0)
        {
            if (stop.IsCancellationRequested)
            {
                FileLog.Write($"[DirectoryScanner] Scan STOPPED: root={root}, files={filesSeen}, reason=asked to stop");
                stop.ThrowIfCancellationRequested();
            }

            var frame = toExpand.Pop();
            var listing = DirectoryReadResult.Read(frame.Path);

            if (!listing.WasRead)
            {
                refused.Add(new RefusedFolder(frame.Path, listing.Refusal, listing.RefusalCode));
                Complete(frame, folderTotals, options.MaxFolderDepth);
                continue;
            }

            foreach (var entry in listing.Entries)
            {
                switch (entry.Kind)
                {
                    case FileSystemEntryKind.RegularFile:
                        filesSeen++;
                        bytesSeen += entry.LengthBytes;
                        frame.FilesBelow++;
                        frame.BytesBelow += entry.LengthBytes;
                        break;

                    case FileSystemEntryKind.CloudPlaceholderFile:
                        placeholderFiles++;
                        placeholderBytes += entry.LengthBytes;
                        break;

                    case FileSystemEntryKind.Link:
                        links.Add(new FoundLink(entry.FullPath, IsDirectoryLink(entry), entry.LinkTarget));
                        break;

                    case FileSystemEntryKind.Directory:
                        foldersSeen++;
                        frame.PendingChildren++;
                        toExpand.Push(new Frame
                        {
                            Path = entry.FullPath,
                            Depth = frame.Depth + 1,
                            Parent = frame
                        });
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"The classifier returned a kind the scan does not handle: {entry.Kind}.");
                }
            }

            if (frame.PendingChildren == 0)
                Complete(frame, folderTotals, options.MaxFolderDepth);
        }

        clock.Stop();

        // Sorted so that two scans of the same tree produce the same index and the same report,
        // whatever order the file system happened to hand the entries back in.
        links.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        refused.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        folderTotals.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        var result = new ScanResult
        {
            RootPath = root,
            RootIsVolumeRoot = VolumeReader.IsVolumeRoot(root),
            StartedUtc = startedUtc,
            FinishedUtc = startedUtc.Add(clock.Elapsed),
            ElapsedSeconds = clock.Elapsed.TotalSeconds,
            FilesSeen = filesSeen,
            FoldersSeen = foldersSeen,
            BytesSeen = bytesSeen,
            PlaceholderFiles = placeholderFiles,
            PlaceholderBytesInCloud = placeholderBytes,
            Links = links,
            RefusedFolders = refused,
            FolderTotals = folderTotals,
            Volume = VolumeReader.Read(root)
        };

        FileLog.Write(
            $"[DirectoryScanner] Scan done: root={root}, files={filesSeen}, folders={foldersSeen}, " +
            $"bytes={bytesSeen}, links={links.Count}, refused={refused.Count}, " +
            $"placeholders={placeholderFiles}, seconds={clock.Elapsed.TotalSeconds:F3}");

        return result;
    }

    /// <summary>
    /// The canonical form of a path: the full path, with a trailing separator kept only where it
    /// belongs, on a volume root. Every path a scan reports is built from this, so that a scan of
    /// "D:\repos\." and a scan of "D:\repos" are one scan and not two.
    /// </summary>
    /// <param name="path">The path to put in canonical form.</param>
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path cannot be blank.", nameof(path));

        var full = Path.GetFullPath(path);
        return VolumeReader.IsVolumeRoot(full) ? full : Path.TrimEndingDirectorySeparator(full);
    }

    // A link that stands where a directory would. Asked of the link itself rather than of its target,
    // because a link whose target is gone is still a link and is still counted.
    private static bool IsDirectoryLink(ScannedEntry entry) =>
        Directory.Exists(entry.FullPath) ||
        (entry.LinkTarget is not null && Directory.Exists(entry.LinkTarget));

    // Roll a finished folder's totals into its parent, and keep rolling while each parent is finished
    // in turn. A folder is finished when every folder under it has been walked.
    private static void Complete(Frame frame, List<FolderTotal> folderTotals, int maxFolderDepth)
    {
        var current = frame;
        while (true)
        {
            if (current.Depth >= ScanOptions.MinimumFolderDepth && current.Depth <= maxFolderDepth)
                folderTotals.Add(new FolderTotal(current.Path, current.Depth, current.BytesBelow, current.FilesBelow));

            var parent = current.Parent;
            if (parent is null) return;

            parent.BytesBelow += current.BytesBelow;
            parent.FilesBelow += current.FilesBelow;
            parent.PendingChildren--;

            if (parent.PendingChildren != 0) return;
            current = parent;
        }
    }
}
