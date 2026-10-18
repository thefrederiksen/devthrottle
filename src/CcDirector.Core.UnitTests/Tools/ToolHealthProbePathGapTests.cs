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

    private static readonly string ShimDir = Path.Combine(Path.GetTempPath(), "fake-home", ".local", "bin");
    private static readonly string PathWithoutShimDir = string.Join(Path.PathSeparator, "/usr/bin", "/bin");
    private static readonly string PathWithShimDir = ShimDir + Path.PathSeparator + PathWithoutShimDir;

    [Fact]
    public void PathGapReason_BuiltButNotOnPath_ShimDirNotOnPath_NamesTheMissingDirectory()
    {
        var reason = ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: false, PathWithoutShimDir, ShimDir);

        Assert.Equal($"installed, but the Director's PATH is missing {ShimDir}", reason);
    }

    [Fact]
    public void PathGapReason_BuiltButNotOnPath_ShimDirOnPath_NamesTheMissingLink()
    {
        // The directory is on PATH, so the tool's own link in it is what is missing. Saying "PATH is
        // missing" here would be false and point at a remedy that changes nothing.
        var reason = ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: false, PathWithShimDir, ShimDir);

        Assert.Equal(
            $"installed, but its link {Path.Combine(ShimDir, "cc-devthrottle")} is missing; cc-devthrottle setup repair writes it",
            reason);
    }

    [Fact]
    public void PathGapReason_BuiltAndOnPath_IsNull()
    {
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: true), isWindows: false, PathWithoutShimDir, ShimDir));
    }

    [Fact]
    public void PathGapReason_NotBuilt_IsNull()
    {
        // A tool that is not installed is "not built", which is true; the gap is only for an installed one.
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: false, isOnPath: false), isWindows: false, PathWithoutShimDir, ShimDir));
    }

    [Fact]
    public void PathGapReason_OnWindows_IsNull()
    {
        Assert.Null(ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: true, PathWithoutShimDir, ShimDir));
    }

    [Fact]
    public void PathGapReason_ScratchRootWithItsBinOnPath_NamesTheLinkInThatBin()
    {
        // A Director on a scratch root writes its links in <scratch>/bin (ToolShimPath.ShimDirFor). The
        // reason must name that link, because it is the one the repair writes; naming ~/.local/bin named
        // a remedy that could never come true (review of pull request 4211, finding 1).
        var scratchBin = Path.Combine(Path.GetTempPath(), "scratch-root", "bin");
        var searchPath = scratchBin + Path.PathSeparator + PathWithoutShimDir;

        var reason = ToolHealthProbe.PathGapReason(Tool(isBuilt: true, isOnPath: false), isWindows: false, searchPath, scratchBin);

        Assert.Equal(
            $"installed, but its link {Path.Combine(scratchBin, "cc-devthrottle")} is missing; cc-devthrottle setup repair writes it",
            reason);
    }
}
