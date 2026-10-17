using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.UnitTests.Tools;

/// <summary>
/// What the tools row says when a tool is installed but the Director's PATH cannot find it (issue
/// devthrottle_internal#2474): "installed, but the Director's PATH is missing ~/.local/bin", never
/// "not built". Sessions find tools by PATH, so a tool only this probe can reach is not working for them.
/// </summary>
public sealed class ToolHealthProbePathGapTests
{
    private static ToolDescriptor Tool(bool isBuilt, bool isOnPath) => new(
        name: "cc-devthrottle", category: "fleet", description: "", note: null,
        binaryPath: "/home/tester/.local/share/cc-director/pyenv/bin/cc-devthrottle",
        isBuilt: isBuilt, isOnPath: isOnPath, isExpected: true, tests: Array.Empty<ToolTest>());

    [Fact]
    public void PathGapReason_BuiltButNotOnPath_OnUnix_NamesTheMissingDirectory()
    {
        var reason = ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: false);

        Assert.Equal($"installed, but the Director's PATH is missing {ToolShimPath.UnixShimDir}", reason);
    }

    [Fact]
    public void PathGapReason_BuiltAndOnPath_IsNull()
    {
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: true), isWindows: false));
    }

    [Fact]
    public void PathGapReason_NotBuilt_IsNull()
    {
        // A tool that is not installed is "not built", which is true; the gap is only for an installed one.
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: false, isOnPath: false), isWindows: false));
    }

    [Fact]
    public void PathGapReason_OnWindows_IsNull()
    {
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: true));
    }
}
