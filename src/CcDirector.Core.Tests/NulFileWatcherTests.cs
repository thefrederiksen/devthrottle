using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class NulFileWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public NulFileWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NulWatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ToExtendedLengthPath_AddsPrefix()
    {
        var result = NulFileWatcher.ToExtendedLengthPath(@"D:\path\NUL");
        Assert.Equal(@"\\?\D:\path\NUL", result);
    }

    [Fact]
    public void ToExtendedLengthPath_DoesNotDoublePrefix()
    {
        var result = NulFileWatcher.ToExtendedLengthPath(@"\\?\D:\path\NUL");
        Assert.Equal(@"\\?\D:\path\NUL", result);
    }

    [Fact]
    public void TryDeleteNulFile_ReturnsFalseWhenNoFile()
    {
        var nulPath = Path.Combine(_tempDir, "NUL");
        var result = NulFileWatcher.TryDeleteNulFile(nulPath);
        Assert.False(result);
    }

    [Fact]
    public void Dispose_StopsWatcher()
    {
        var watcher = new NulFileWatcher(_tempDir, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
        watcher.Start();
        watcher.Dispose();

        // Should not throw — double-dispose should be safe too
        watcher.Dispose();
    }

    /// <summary>
    /// Integration test: attempts to delete a real nul file at a known location.
    /// This test will be skipped if no nul file exists at the target path.
    /// </summary>
    [Fact]
    public void TryDeleteNulFile_RealFile_Integration()
    {
        // Known locations where nul files commonly appear
        var knownPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "nul"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "nul"),
        };

        string? foundPath = null;
        foreach (var path in knownPaths)
        {
            var extendedPath = NulFileWatcher.ToExtendedLengthPath(path);
            if (File.Exists(extendedPath))
            {
                foundPath = path;
                break;
            }
        }

        if (foundPath == null)
        {
            // No nul file found - skip this test
            System.Diagnostics.Debug.WriteLine("[Test] No nul file found at known locations - skipping");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[Test] Found nul file at: {foundPath}");

        // Attempt deletion
        var result = NulFileWatcher.TryDeleteNulFile(foundPath);

        Assert.True(result, $"TryDeleteNulFile should return true for {foundPath}");

        var extendedAfter = NulFileWatcher.ToExtendedLengthPath(foundPath);
        Assert.False(File.Exists(extendedAfter), $"NUL file should be deleted at {foundPath}");

        System.Diagnostics.Debug.WriteLine($"[Test] Successfully deleted nul file: {foundPath}");
    }

    public void Dispose()
    {
        try
        {
            // Clean up temp directory, using extended paths for any NUL files
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    var extended = file.StartsWith(@"\\?\") ? file : @"\\?\" + file;
                    try { File.Delete(extended); } catch { }
                }
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
