using System.Runtime.CompilerServices;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The deploy watch must not record a failed state read as the site leaving Running. Deploy run 42752825924
/// (2026-11-10) failed on one <c>az webapp show</c> read that failed between two Running reads, because the
/// sample was <c>st=$(az webapp show ... 2&gt;/dev/null || echo "Unknown")</c>. The sampling and its verdict
/// now live in <c>scripts/watch-site-state.sh</c>, whose behaviour - a failed read alone passes and is
/// reported, failed reads past the bound fail as not measured, a real Stopped fails - is proved against a
/// fake az by <c>scripts/watch-site-state.test.sh</c>.
///
/// This guard keeps the wiring honest from the default gate, without starting any process: the watch step
/// calls the script for both halves, its verdict decides the state failure, the old conversion of a failed
/// read into a state is gone, and continuous integration runs the proof.
/// </summary>
public sealed class DeployWatchTellsAFailedReadFromAStoppedSiteTests
{
    [Fact]
    public void DeployWorkflow_WatchStep_UsesTheScriptAndItsVerdict()
    {
        // Arrange
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");

        // Act
        var violations = Violations(workflow);

        // Assert
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void WatchSiteStateProof_IsCommittedAndRunByContinuousIntegration()
    {
        // Arrange
        var root = RepoRoot();

        // Act
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        // Assert
        Assert.True(File.Exists(Path.Combine(root, "scripts", "watch-site-state.sh")), "The site-state script is missing.");
        Assert.True(File.Exists(Path.Combine(root, "scripts", "watch-site-state.test.sh")), "The fake-backed proof of the site-state script is missing.");
        Assert.Contains("run: bash scripts/watch-site-state.test.sh", ci, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bash scripts/watch-site-state.sh sample", "bash scripts/watch-site-state.sh sampleX", "no longer samples through the script")]
    [InlineData("bash scripts/watch-site-state.sh verdict", "bash scripts/watch-site-state.sh verdictX", "no longer judges through the script")]
    [InlineData("fail=\"$state_fail\"", "fail=\"\"", "does not fail on the script's state verdict")]
    [InlineData("WATCH_DEADLINE=", "st=$(az webapp show --query state --output tsv 2>/dev/null || echo \"Unknown\")\n          WATCH_DEADLINE=", "turns a failed read into a state again")]
    public void Violations_CatchEachMutation(string find, string replace, string expectedViolation)
    {
        // Arrange
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");
        Assert.Empty(Violations(workflow));
        Assert.Contains(find, workflow, StringComparison.Ordinal);

        // Act
        var violations = Violations(workflow.Replace(find, replace, StringComparison.Ordinal));

        // Assert
        Assert.Contains(violations, v => v.Contains(expectedViolation, StringComparison.Ordinal));
    }

    private static List<string> Violations(string workflow)
    {
        var violations = new List<string>();
        if (!workflow.Contains("bash scripts/watch-site-state.sh sample \"$STATE_LOG\"", StringComparison.Ordinal))
            violations.Add("The watch step no longer samples through the script (bash scripts/watch-site-state.sh sample).");
        if (!workflow.Contains("bash scripts/watch-site-state.sh verdict \"$STATE_LOG\"", StringComparison.Ordinal))
            violations.Add("The watch step no longer judges through the script (bash scripts/watch-site-state.sh verdict).");
        if (!workflow.Contains("if [ -n \"$state_fail\" ]; then\n            fail=\"$state_fail\"", StringComparison.Ordinal))
            violations.Add("The watch step does not fail on the script's state verdict.");
        if (workflow.Contains("|| echo \"Unknown\")", StringComparison.Ordinal))
            violations.Add("The deploy workflow turns a failed read into a state again (|| echo \"Unknown\").");
        return violations;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir, ".github", "workflows", "deploy-hosted-gateway.yml")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
