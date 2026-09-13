using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Linux side of the probe (issue #2818), parsed from captured text so it is covered on every
/// machine that runs the suite rather than only on the one that has <c>/proc/meminfo</c>.
/// </summary>
public class ProcMemInfoParsingTests
{
    private static readonly DateTime At = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Real text, trimmed, from an Ubuntu 24.04 machine with 16 gigabytes installed.</summary>
    private static readonly string[] RealMemInfo =
    [
        "MemTotal:       16316576 kB",
        "MemFree:          312044 kB",
        "MemAvailable:   11284512 kB",
        "Buffers:          198760 kB",
        "Cached:         10233188 kB",
        "SwapCached:            0 kB",
        "Active:          4821736 kB",
    ];

    [Fact]
    public void Parse_RealFile_ReadsTotalAndAvailableInBytes()
    {
        var reading = MachineMemoryProbe.ParseProcMemInfo(RealMemInfo, At);

        Assert.True(reading.CouldRead);
        Assert.Equal(16316576UL * 1024, reading.TotalBytes);
        Assert.Equal(11284512UL * 1024, reading.AvailableBytes);
        Assert.Equal(At, reading.TakenAtUtc);
    }

    [Fact]
    public void Parse_RealFile_FoldsToNormal()
    {
        // Eleven gigabytes available of sixteen. The point of this test is that the parser feeds the
        // fold correctly end to end, in the units the fold expects.
        var reading = MachineMemoryProbe.ParseProcMemInfo(RealMemInfo, At);

        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Parse_ReadsMemAvailable_NotMemFree()
    {
        // MemFree here is 312 megabytes, which would fold to Critical. MemAvailable is 11 gigabytes,
        // which is the truth. Reading the wrong line would report a healthy machine as nearly out of
        // memory and widen every deadline on it permanently.
        var reading = MachineMemoryProbe.ParseProcMemInfo(RealMemInfo, At);

        Assert.NotEqual(312044UL * 1024, reading.AvailableBytes);
        Assert.Equal(11284512UL * 1024, reading.AvailableBytes);
    }

    [Fact]
    public void Parse_MissingMemAvailable_IsUnreadable_NotZero()
    {
        // An old kernel without MemAvailable must say so. Returning zero would be a confident lie that
        // folds to Critical.
        string[] lines = ["MemTotal:       16316576 kB", "MemFree:          312044 kB"];

        var reading = MachineMemoryProbe.ParseProcMemInfo(lines, At);

        Assert.False(reading.CouldRead);
        Assert.Equal(0UL, reading.AvailableBytes);
        Assert.Contains("MemAvailable", reading.UnreadableReason!, StringComparison.Ordinal);
        Assert.Equal(MemoryPressureLevel.Unknown, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Parse_MissingMemTotal_IsUnreadable()
    {
        string[] lines = ["MemAvailable:   11284512 kB"];

        var reading = MachineMemoryProbe.ParseProcMemInfo(lines, At);

        Assert.False(reading.CouldRead);
        Assert.Contains("MemTotal", reading.UnreadableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyFile_IsUnreadable()
    {
        var reading = MachineMemoryProbe.ParseProcMemInfo([], At);

        Assert.False(reading.CouldRead);
    }

    [Fact]
    public void Parse_GarbledNumber_IsUnreadable_NotZero()
    {
        string[] lines = ["MemTotal:       not-a-number kB", "MemAvailable:   11284512 kB"];

        var reading = MachineMemoryProbe.ParseProcMemInfo(lines, At);

        Assert.False(reading.CouldRead);
    }

    [Fact]
    public void Parse_DoesNotMatchAPrefixOfAnotherKey()
    {
        // "MemTotal:" must not be satisfied by a different line that happens to start similarly, and
        // MemAvailable must not be read out of MemFree.
        string[] lines = ["MemTotalHuge:   99 kB", "MemTotal:       16316576 kB", "MemAvailable:   11284512 kB"];

        var reading = MachineMemoryProbe.ParseProcMemInfo(lines, At);

        Assert.True(reading.CouldRead);
        Assert.Equal(16316576UL * 1024, reading.TotalBytes);
    }
}
