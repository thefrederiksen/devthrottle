using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

public sealed class LeaderIdentityStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public LeaderIdentityStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LeaderIdentityTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "scheduler-leader.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Read_WhenMissing_ReturnsNull()
    {
        var store = new LeaderIdentityStore(_path);
        Assert.Null(store.Read());
    }

    [Fact]
    public void WriteThenRead_ReturnsOurOwnIdentity()
    {
        var store = new LeaderIdentityStore(_path);
        store.Write();

        var record = store.Read();
        Assert.NotNull(record);
        Assert.Equal(Environment.ProcessId, record!.Pid);
        Assert.False(string.IsNullOrEmpty(record.ExeName));
        Assert.False(string.IsNullOrEmpty(record.AcquiredAtUtc));
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = new LeaderIdentityStore(_path);
        store.Write();
        Assert.True(File.Exists(_path));

        store.Delete();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Read_WithStalePid_ReturnsNull()
    {
        // Write a file with a PID guaranteed not to exist. Windows PIDs are
        // multiples of 4, and the max user-mode PID is well under int.MaxValue.
        // 1073741820 is high enough to never be a real running process.
        File.WriteAllText(_path, """
            {
              "pid": 1073741820,
              "exePath": "C:\\fake\\ghost.exe",
              "exeName": "ghost",
              "acquiredAtUtc": "2026-01-01T00:00:00Z"
            }
            """);

        var store = new LeaderIdentityStore(_path);
        Assert.Null(store.Read());
    }

    [Fact]
    public void Read_WithMalformedJson_ReturnsNull()
    {
        File.WriteAllText(_path, "{ this isn't json");
        var store = new LeaderIdentityStore(_path);
        Assert.Null(store.Read());
    }
}
