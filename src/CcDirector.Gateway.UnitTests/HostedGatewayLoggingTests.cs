using CcDirector.Gateway;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted Gateway's process log lives on the persistent shared drive so it survives a deploy (owner
/// decision, 9 November 2026), in its own folder, kept bounded by <see cref="HostedLogRetention"/>. These
/// tests pin the location and the retention policy. The clock is passed in, and every file's last-write time
/// is set explicitly, so nothing here depends on when the test runs.
/// </summary>
public sealed class HostedGatewayLoggingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hosted-log-retention-" + Guid.NewGuid().ToString("N"));

    public HostedGatewayLoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ResolveHostedLogDirectory_StorageRoot_IsItsOwnFolderUnderLogs()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "hosted-storage-root");

        var directory = GatewayEntryPoint.ResolveHostedLogDirectory(storageRoot);

        Assert.Equal(Path.Combine(Path.GetFullPath(storageRoot), "logs", "hosted-gateway"), directory);
    }

    [Fact]
    public void ResolveHostedLogDirectory_ProductionMount_LandsOnTheShare()
    {
        // The Dockerfile's CC_DIRECTOR_ROOT, which is where the App Service mounts the Azure Files share.
        var directory = GatewayEntryPoint.ResolveHostedLogDirectory("/home/gateway/cc-director");

        Assert.Equal(Path.Combine(Path.GetFullPath("/home/gateway/cc-director"), "logs", "hosted-gateway"), directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveHostedLogDirectory_BlankRoot_Throws(string storageRoot)
    {
        Assert.Throws<ArgumentException>(() => GatewayEntryPoint.ResolveHostedLogDirectory(storageRoot));
    }

    [Fact]
    public void Prune_MissingFolder_ReturnsEmpty()
    {
        var result = HostedLogRetention.Prune(Path.Combine(_dir, "not-there"), Now);

        Assert.Equal(0, result.Kept);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Prune_FileOlderThanMaxAge_IsRemoved_RecentOneKept()
    {
        var old = Make("director-2026-10-01-aaaaaaaaaaaa.log", 10, Now - HostedLogRetention.MaxAge - TimeSpan.FromMinutes(1));
        var recent = Make("director-2026-11-08-bbbbbbbbbbbb.log", 10, Now - TimeSpan.FromDays(1));

        var result = HostedLogRetention.Prune(_dir, Now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Prune_OverTheSizeCap_RemovesOldestFirst()
    {
        // Sparse lengths stand in for real volume: SetLength does not write the bytes.
        var half = HostedLogRetention.MaxTotalBytes / 2;
        var oldest = Make("director-a.log", half, Now - TimeSpan.FromDays(3));
        var middle = Make("director-b.log", half, Now - TimeSpan.FromDays(2));
        var newest = Make("director-c.log", half, Now - TimeSpan.FromDays(1));

        var result = HostedLogRetention.Prune(_dir, Now);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.Equal(1, result.Removed);
        Assert.True(result.BytesKept <= HostedLogRetention.MaxTotalBytes);
    }

    [Fact]
    public void Prune_OverTheSizeCap_NeverRemovesAFileStillBeingWritten()
    {
        // The outgoing container of a deploy is still writing its file; it must survive any size cap.
        var active = Make("director-live.log", HostedLogRetention.MaxTotalBytes + 1, Now - TimeSpan.FromMinutes(5));

        var result = HostedLogRetention.Prune(_dir, Now);

        Assert.True(File.Exists(active));
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Prune_OnlyLogFilesInTheFolderAreConsidered()
    {
        var other = Make("notes.txt", 10, Now - TimeSpan.FromDays(60));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        var nested = Path.Combine(_dir, "sub", "director-x.log");
        File.WriteAllText(nested, "x");
        File.SetLastWriteTimeUtc(nested, Now - TimeSpan.FromDays(60));

        var result = HostedLogRetention.Prune(_dir, Now);

        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
        Assert.Equal(0, result.Removed);
    }

    private string Make(string name, long length, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, name);
        using (var fs = new FileStream(path, FileMode.Create))
            fs.SetLength(length);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
