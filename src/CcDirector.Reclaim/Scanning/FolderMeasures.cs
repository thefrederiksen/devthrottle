namespace CcDirector.Reclaim.Scanning;

/// <summary>What measuring one item found.</summary>
/// <param name="Bytes">The bytes it holds on this disk. Nought is a measurement, not a missing one.</param>
/// <param name="UnreadableFolders">How many of its folders would not be listed.</param>
/// <param name="NewestWriteUtc">When anything inside it was last written. Never a missing value.</param>
public sealed record MeasuredFolder(long Bytes, long UnreadableFolders, DateTimeOffset NewestWriteUtc);

/// <summary>
/// Measures an item and asks whether anything holds a file inside it open.
///
/// One implementation, used both by the rules that recommend and by the refusal gate that re-measures
/// at the moment of the move. The two must never disagree about what "the newest write inside it"
/// means, because the gate refuses an item whose age or bytes changed since the rule measured it - and
/// two implementations of one measurement drift apart exactly the way two copies of anything else do.
///
/// Both methods open nothing for keeping and change nothing: the open-file question asks for each
/// file exclusively for an instant and gives it straight back.
/// </summary>
public static class FolderMeasures
{
    /// <summary>
    /// Measure an item: how many bytes it holds, when anything in it was last written, and how many
    /// of its folders would not be listed. A file is its own whole answer; a folder is walked to the
    /// bottom. Links are skipped, never followed - bytes behind a link belong to wherever the link
    /// points, and this tool has said nothing about that place.
    /// </summary>
    /// <param name="path">The item to measure, a file or a folder.</param>
    public static MeasuredFolder Measure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new MeasuredFolder(file.Length, 0, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }

        long bytes = 0;
        long unreadable = 0;
        var newest = DateTimeOffset.MinValue;

        var pending = new Stack<string>();
        pending.Push(path);

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

                bytes += ((FileInfo)entry).Length;
                var written = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (written > newest) newest = written;
            }
        }

        return new MeasuredFolder(
            bytes, unreadable, newest == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : newest);
    }

    /// <summary>
    /// True as soon as one file inside the item is held by something else. It stops at the first one:
    /// the answer is the same whether one file is open or a hundred, and every extra file asked is
    /// another file held exclusively for an instant on a machine that is doing other work. A folder
    /// that cannot be listed is answered as in use, because something about it cannot be told.
    /// </summary>
    /// <param name="path">The item to ask about, a file or a folder.</param>
    public static bool AnythingOpenIn(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path)) return IsOpen(path);

        var pending = new Stack<string>();
        pending.Push(path);

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
                return true;
            }
            catch (IOException)
            {
                return true;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null) continue;

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                if (IsOpen(entry.FullName)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks the file system whether anything else holds the file, by asking for it exclusively for an
    /// instant and giving it straight back. Nothing is read from it and nothing is written to it.
    /// </summary>
    private static bool IsOpen(string path)
    {
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Something about this file cannot be answered, and a check that cannot answer treats the
            // file as in use. Leaning towards keeping is the whole posture of this tool.
            return true;
        }
    }
}
