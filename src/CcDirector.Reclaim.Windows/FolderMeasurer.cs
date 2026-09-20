namespace CcDirector.Reclaim.Windows;

/// <summary>
/// One measured folder: what a walk of it counted.
/// </summary>
/// <param name="Files">How many files it holds, links not counted.</param>
/// <param name="Bytes">The bytes those files hold, measured, not estimated.</param>
/// <param name="UnreadableFolders">How many folders inside it would not be listed. A folder that
/// will not be listed is counted rather than passed over, because a walk that quietly skips what
/// it cannot read reports a smaller folder and calls it a measurement.</param>
/// <param name="NewestWriteUtc">When anything inside it was last written, judged on the files and
/// the folders themselves rather than the root's own stamp, because a folder whose own stamp is
/// old can still hold a file written a minute ago.</param>
internal sealed record MeasuredFolder(long Files, long Bytes, long UnreadableFolders, DateTimeOffset NewestWriteUtc)
{
    public static readonly MeasuredFolder Empty = new(0, 0, 0, DateTimeOffset.UnixEpoch);
}

/// <summary>
/// Walks a folder and measures it, for the rules that need a measurement.
///
/// Links are not followed, for the same reason the scanner does not follow them: a link out of a
/// folder leads somewhere the rule has said nothing about, and its target's bytes are not the
/// folder's bytes. A folder that will not be listed is counted, not skipped.
/// </summary>
internal static class FolderMeasurer
{
    public static MeasuredFolder Measure(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        long files = 0;
        long bytes = 0;
        long unreadable = 0;
        var newest = DateTimeOffset.MinValue;

        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            var next = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(next).EnumerateFileSystemInfos().ToList();
            }
            catch (UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }
            catch (IOException)
            {
                unreadable++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null) continue;

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                files++;
                bytes += ((FileInfo)entry).Length;
                var fileWritten = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (fileWritten > newest) newest = fileWritten;
            }
        }

        return new MeasuredFolder(
            files, bytes, unreadable, newest == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : newest);
    }
}
