using Xunit;

namespace CcDirector.Avalonia.Tests.DevReports;

/// <summary>
/// The two promises the reports pane makes that are about what it must NOT do, and which therefore cannot be
/// proven by calling it (issue #3019). These read the pane's own source.
///
///  1. THE REPORT'S FRAME HAS NO PATH TO THE BRIDGE. The pane never subscribes to <c>FrameCreated</c> or
///     <c>FrameNavigationStarting</c>, and never adds a host object to script. If it did, the agent-written
///     report inside the sandboxed frame - the exact thing CONTRACT.md section 4 exists to contain - would be
///     able to ask for the owner's Gateway key itself, and no amount of checking the TOP-LEVEL address would
///     notice.
///  2. THE KEY IS NEVER WRITTEN DOWN. Every log line in the pane is checked for the variable that holds the
///     key. The pane logs THAT a key was handed over, never the key.
///
/// WHAT THIS DOES NOT COVER, said plainly: it reads this one file. It cannot prove that some other code in
/// the Director does not log a Gateway key, and it cannot prove what WebView2 itself writes into its own user
/// data folder. The live proof on the running Director searches the whole run for the key; that is the claim
/// this guard cannot make.
/// </summary>
public sealed class DevReportPaneKeepsTheKeyTests
{
    private static string PaneSource() => File.ReadAllText(Path.Combine(
        TestRepoRoot.Path, "src", "CcDirector.Avalonia", "Controls", "DevReportsPaneControl.axaml.cs"));

    [Theory]
    [InlineData("FrameCreated")]
    [InlineData("FrameNavigationStarting")]
    [InlineData("AddHostObjectToScript")]
    [InlineData("AddScriptToExecuteOnDocumentCreated")]
    public void ThePane_NeverOpensASeamTheReportsFrameCouldReach(string forbidden)
    {
        var source = PaneSource();

        // The names appear in the file's own explanation of why they are absent, so the check is on CODE:
        // a line that is not a comment. A comment saying "never FrameCreated" must not satisfy a guard that
        // exists to prove there is no FrameCreated.
        var offending = source
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                           && !line.StartsWith("///", StringComparison.Ordinal)
                           && !line.StartsWith("*", StringComparison.Ordinal))
            .Where(line => line.Contains(forbidden, StringComparison.Ordinal))
            .ToList();

        Assert.True(offending.Count == 0,
            $"DevReportsPaneControl uses {forbidden}, which would give the report's frame a path to the "
            + "bridge: " + string.Join(" | ", offending));
    }

    [Fact]
    public void ThePane_NeverPutsTheKeyInALogLine()
    {
        // The key lives in exactly one local, named token, and is passed to exactly one call. Any log line
        // that names it is the defect this test exists to catch.
        var offending = PaneSource()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("FileLog.Write", StringComparison.Ordinal))
            .Where(line => line.Contains("token", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("{key", StringComparison.Ordinal)
                           || line.Contains("ComposeKeyMessage", StringComparison.Ordinal))
            .ToList();

        Assert.True(offending.Count == 0,
            "a log line in DevReportsPaneControl names the Gateway key: " + string.Join(" | ", offending));
    }

    [Fact]
    public void ThePane_ReadsTheKeyInExactlyTheTwoPlacesItNeedsIt()
    {
        // The key is read from the configuration twice and no more: once to authenticate the Gateway call
        // that finds the page, and once to answer the page's request for it. Pinning the count means a third
        // reader - a cache, a field, a copy handed somewhere else - cannot appear without somebody deciding
        // it should.
        var source = PaneSource();

        Assert.Equal(2, source.Split(".Token").Length - 1);

        // And it is never kept: a key that lives in a field outlives the message it was composed into.
        var stored = source
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(".Token", StringComparison.Ordinal))
            .Where(line => line.StartsWith("_", StringComparison.Ordinal) || line.Contains("= _", StringComparison.Ordinal))
            .ToList();

        Assert.True(stored.Count == 0, "the Gateway key is stored on the pane: " + string.Join(" | ", stored));
    }
}
