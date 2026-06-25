using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #725: the PortAllocator must never hand out a port inside a Windows TCP excluded range
/// (Hyper-V / WSL / Docker / http.sys reservations). A raw bind probe reads such a port as "free",
/// but the System process shadows the socket, so a Director that bound it logged "listening" while
/// every request 404'd. These tests cover the netsh-output parser and the exclusion check (the pure
/// pieces); the real netsh read is OS-dependent and exercised live in the proof.
/// </summary>
public sealed class PortAllocatorExcludedRangeTests
{
    // A representative slice of real `netsh int ipv4 show excludedportrange protocol=tcp` output,
    // including the banner, the header row, a single-port exclusion (7882, the one that bit us), a
    // multi-port range, and a row with the administered-range "*" marker.
    private const string SampleNetshOutput = """

        Protocol tcp Port Exclusion Ranges

        Start Port    End Port
        ----------    --------
                80          80
              7882        7882
             49511       49610
             50000       50059     *
        """;

    [Fact]
    public void ParseExcludedRanges_ParsesRowsAndIgnoresBannerAndHeader()
    {
        var ranges = PortAllocator.ParseExcludedRanges(SampleNetshOutput);

        Assert.Contains((80, 80), ranges);
        Assert.Contains((7882, 7882), ranges);
        Assert.Contains((49511, 49610), ranges);
        Assert.Contains((50000, 50059), ranges);   // the trailing "*" is harmless
        Assert.Equal(4, ranges.Count);             // banner, header, and dashes are not rows
    }

    [Fact]
    public void ParseExcludedRanges_EmptyOrBlank_ReturnsEmpty()
    {
        Assert.Empty(PortAllocator.ParseExcludedRanges(""));
        Assert.Empty(PortAllocator.ParseExcludedRanges("   \n  \n"));
    }

    [Fact]
    public void IsExcludedPort_DetectsPortsInsideAndOutsideRanges()
    {
        var ranges = PortAllocator.ParseExcludedRanges(SampleNetshOutput);

        Assert.True(PortAllocator.IsExcludedPort(7882, ranges));    // the exact port that bit us
        Assert.True(PortAllocator.IsExcludedPort(49550, ranges));   // inside a multi-port range
        Assert.False(PortAllocator.IsExcludedPort(7883, ranges));   // just outside
        Assert.False(PortAllocator.IsExcludedPort(7879, ranges));   // a normal Director port
    }
}
