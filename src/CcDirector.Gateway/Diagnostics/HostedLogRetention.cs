namespace CcDirector.Gateway.Diagnostics;

/// <summary>
/// Keeps the hosted Gateway's process log folder on the shared drive bounded.
///
/// The hosted log lives on the persistent shared drive so that it survives a deploy (owner decision,
/// 9 November 2026: measure the deploy outage before fixing it - the log on the container's temporary disk
/// was erased by every deploy, which is exactly when it was needed). A folder that only ever grows is not
/// acceptable on a billed network share, so each container start removes, before its own log opens:
///
///  - every log file not written for <see cref="MaxAge"/>, and then
///  - the oldest remaining files until the folder holds at most <see cref="MaxTotalBytes"/>.
///
/// A file written within <see cref="ActiveWindow"/> is NEVER removed, whatever the size: during a deploy the
/// outgoing container is still writing its own file, and deleting it would destroy the very record this
/// folder exists to keep. The clock is passed in, so the policy is tested without the real clock.
/// </summary>
public static class HostedLogRetention
{
    /// <summary>A log file untouched for this long is removed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>The folder is trimmed, oldest first, down to this total.</summary>
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <summary>A file written this recently may belong to a running container and is always kept.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(1);

    /// <summary>What one prune did, so the caller can log it once its log is open.</summary>
    public sealed record Result(int Kept, int Removed, long BytesRemoved, long BytesKept, IReadOnlyList<string> Failures);

    /// <summary>
    /// Remove expired and excess log files from <paramref name="logDirectory"/>. Only files named
    /// <c>*.log</c> directly in that folder are considered. A folder that does not exist yet is an empty result.
    /// A file that cannot be removed is reported in <see cref="Result.Failures"/> rather than thrown: the prune
    /// runs before the log opens, and a share hiccup here must not stop the Gateway from starting.
    /// </summary>
    public static Result Prune(string logDirectory, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("A log directory is required.", nameof(logDirectory));
        if (!Directory.Exists(logDirectory))
            return new Result(0, 0, 0, 0, Array.Empty<string>());

        var files = new DirectoryInfo(logDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        var failures = new List<string>();
        var removed = 0;
        long bytesRemoved = 0;
        var kept = new List<FileInfo>();

        // Sizes are read once, up front: a FileInfo re-reads the disk after Delete and throws on a file now gone.
        var sizes = files.ToDictionary(f => f.FullName, f => f.Length);

        foreach (var file in files)
        {
            if (utcNow - file.LastWriteTimeUtc > MaxAge && TryDelete(file, failures))
            {
                removed++;
                bytesRemoved += sizes[file.FullName];
            }
            else
            {
                kept.Add(file);
            }
        }

        var total = kept.Sum(f => sizes[f.FullName]);
        // Oldest first; stop at the active window, because everything after it is newer still.
        foreach (var file in kept.ToList())
        {
            if (total <= MaxTotalBytes) break;
            if (utcNow - file.LastWriteTimeUtc < ActiveWindow) break;
            if (!TryDelete(file, failures)) continue;
            removed++;
            bytesRemoved += sizes[file.FullName];
            total -= sizes[file.FullName];
            kept.Remove(file);
        }

        return new Result(kept.Count, removed, bytesRemoved, total, failures);
    }

    private static bool TryDelete(FileInfo file, List<string> failures)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (IOException ex)
        {
            failures.Add($"{file.Name}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            failures.Add($"{file.Name}: {ex.Message}");
            return false;
        }
    }
}
