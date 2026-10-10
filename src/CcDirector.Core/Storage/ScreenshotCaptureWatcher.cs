using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// The "take a screenshot and we'll find it" detection: watch every folder the OS could plausibly
/// drop a screenshot into, and when a new image file appears anywhere in them, report the folder it
/// landed in. This works identically on Windows 10, Windows 11, and macOS because it observes what
/// the OS actually DOES instead of guessing from settings - the user presses their normal
/// screenshot shortcut and the landing folder is the proof.
///
/// Watches subdirectories too (a OneDrive-redirected Screenshots folder lives under the watched
/// Pictures root), and listens for both Created and Renamed events (macOS writes a temporary
/// .screencapture file first and renames it to the final .png).
/// </summary>
public sealed class ScreenshotCaptureWatcher
{
    /// <summary>
    /// The platform's plausible screenshot roots that exist right now: every detection candidate
    /// plus the broad OS locations (Pictures, Desktop, OneDrive Pictures) so a capture is caught
    /// even when the exact target was not predictable. Deduplicated; nested roots are removed
    /// (watching the parent recursively already covers them).
    /// </summary>
    public static IReadOnlyList<string> WatchRoots()
    {
        var roots = new List<string>();

        foreach (var candidate in ScreenshotLocator.DetectCandidates())
            roots.Add(candidate.Path);

        void AddIfExists(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                roots.Add(Path.GetFullPath(dir));
        }

        AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        foreach (var oneDrive in new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial") })
        {
            if (!string.IsNullOrWhiteSpace(oneDrive))
                AddIfExists(Path.Combine(oneDrive, "Pictures"));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var distinct = roots.Distinct(comparer).ToList();

        // Drop any root nested inside another - the recursive watch on the parent covers it.
        var result = distinct
            .Where(dir => !distinct.Any(other =>
                !comparer.Equals(other, dir)
                && dir.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            .ToList();

        FileLog.Write($"[ScreenshotCaptureWatcher] WatchRoots -> {result.Count} root(s)");
        return result;
    }

    /// <summary>
    /// Wait until a new image file appears under any of <paramref name="roots"/> and return the
    /// directory it landed in, or null when cancelled. The caller owns the timeout via the token.
    /// </summary>
    public async Task<string?> WaitForNewScreenshotAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        FileLog.Write($"[ScreenshotCaptureWatcher] WaitForNewScreenshotAsync: watching {roots.Count} root(s)");
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchers = new List<FileSystemWatcher>();

        void OnHit(string fullPath)
        {
            if (!ScreenshotLocator.IsImageFile(fullPath)) return;
            var dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir)) return;
            FileLog.Write($"[ScreenshotCaptureWatcher] new image: {fullPath}");
            tcs.TrySetResult(dir);
        }

        try
        {
            foreach (var root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName,
                    };
                    watcher.Created += (_, e) => OnHit(e.FullPath);
                    watcher.Renamed += (_, e) => OnHit(e.FullPath);
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    // One unwatchable root (permissions, a vanished folder) must not kill the probe.
                    FileLog.Write($"[ScreenshotCaptureWatcher] cannot watch {root} FAILED: {ex.Message}");
                }
            }

            if (watchers.Count == 0)
            {
                FileLog.Write("[ScreenshotCaptureWatcher] no watchable roots");
                return null;
            }

            using var registration = ct.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                try { watcher.Dispose(); } catch { /* best effort */ }
            }
        }
    }
}
