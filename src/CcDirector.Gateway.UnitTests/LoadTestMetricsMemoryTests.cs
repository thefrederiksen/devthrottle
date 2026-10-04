using System.Diagnostics;
using System.Text.Json;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The memory block of GET /diag/loadmetrics: it must say where a working set far above the managed heap is
/// (heap, memory the garbage collector holds, everything else), and on Linux the kernel's own split.
/// Serial: one fact resets the process-wide counters, which the snooze read-count facts measure by delta.
/// </summary>
[Collection(SerialMetricCollection.Name)]
public sealed class LoadTestMetricsMemoryTests
{
    [Fact]
    public void ReadLinuxResident_RealStatusLines_ReturnsBytes()
    {
        var lines = new[]
        {
            "Name:\tdotnet",
            "VmRSS:\t  464668 kB",
            "RssAnon:\t  301234 kB",
            "RssFile:\t  160000 kB",
            "RssShmem:\t    3434 kB",
            "Threads:\t31",
        };

        var result = LoadTestMetrics.ReadLinuxResident(lines);

        Assert.Equal(464668L * 1024, result["VmRSSBytes"]);
        Assert.Equal(301234L * 1024, result["RssAnonBytes"]);
        Assert.Equal(160000L * 1024, result["RssFileBytes"]);
        Assert.Equal(3434L * 1024, result["RssShmemBytes"]);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ReadLinuxResident_LineMissing_IsAbsentNotZero()
    {
        var result = LoadTestMetrics.ReadLinuxResident(new[] { "VmRSS:\t 100 kB" });

        Assert.Equal(102400L, result["VmRSSBytes"]);
        Assert.False(result.ContainsKey("RssAnonBytes"));
    }

    [Fact]
    public void ReadLinuxResident_UnexpectedUnit_Throws()
    {
        Assert.Throws<FormatException>(() => LoadTestMetrics.ReadLinuxResident(new[] { "RssAnon:\t 100 MB" }));
    }

    [Fact]
    public void Snapshot_IncludesMemoryBlock_WithCommittedAtLeastTheHeap()
    {
        // Something must live on the heap for the after-collection figure to mean anything.
        GC.Collect();

        var json = JsonSerializer.Serialize(LoadTestMetrics.Snapshot(reset: false));
        using var doc = JsonDocument.Parse(json);
        var memory = doc.RootElement.GetProperty("memory");

        var heap = memory.GetProperty("gcHeapAfterLastCollectionBytes").GetInt64();
        var committed = memory.GetProperty("gcCommittedBytes").GetInt64();
        Assert.True(heap > 0, "the heap after the last collection should be above zero");
        Assert.True(committed >= heap, $"committed {committed} should be at least the heap {heap}");
        Assert.True(memory.GetProperty("privateBytes").GetInt64() > 0);
        Assert.True(memory.GetProperty("gcConfiguration").EnumerateObject().Any(), "the GC configuration should be read, not empty");
        Assert.Equal(System.Runtime.GCSettings.IsServerGC, memory.GetProperty("gcIsServer").GetBoolean());
        var linux = memory.GetProperty("linuxResident");
        Assert.Equal(OperatingSystem.IsLinux() ? JsonValueKind.Object : JsonValueKind.Null, linux.ValueKind);
        if (OperatingSystem.IsLinux())
        {
            // The real /proc/self/status, read on a real Linux host: the figures the hosted Gateway relies on.
            Assert.True(linux.GetProperty("VmRSSBytes").GetInt64() > 0);
            Assert.True(linux.GetProperty("RssAnonBytes").GetInt64() > 0);
        }
    }

    [Fact]
    public void Snapshot_WithReset_StillReadsMemoryBeforeResetting()
    {
        LoadTestMetrics.SnoozeDbReadObserved();

        var json = JsonSerializer.Serialize(LoadTestMetrics.Snapshot(reset: true));
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("counters").GetProperty("snoozeDbReads").GetInt64() >= 1);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("memory").ValueKind);
    }
}
