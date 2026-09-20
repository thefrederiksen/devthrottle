using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The hosted image contract test is the only proof that the published hosted image refuses to start
/// without the hosted contract. On 19 September 2026 it MOVED out of the continuous integration test step,
/// where it cost 16 minutes 32 seconds of a 103-minute .NET job, and into the hosted deploy - the run that
/// actually publishes the artifact it is about.
///
/// A move like that can go wrong in two directions, and this pins both:
///
///  * The test quietly stops running anywhere. It is out of cc-director.sln, so
///    <c>dotnet test cc-director.sln</c> cannot reach it; if the deploy workflow also stopped calling it,
///    nothing would run it at all and every summary anyone looks at would stay green.
///  * It creeps back into the continuous integration step, and the sixteen minutes come back on every
///    pull request without anybody deciding to spend them.
///
/// This is a C# test on purpose, for the reason ReleaseWorkflowContractTests gives: the continuous
/// integration .NET job runs <c>dotnet test</c> and nothing else, so a guard written in another language
/// would never run, and a guard nobody runs is not a guard.
///
/// SCOPE, stated rather than implied. This reads files. It pins the arrangement - which project the test
/// is in, which solution does not contain it, which workflow calls which, and what the verdict step is
/// made of. It cannot prove what a GitHub Actions run does; only a run can do that. What it stops is the
/// edit that silently takes the proof out of the deploy.
/// </summary>
public sealed class HostedImageContractRunsBeforeEveryDeployTests
{
    private const string TestProjectDir = "src/CcDirector.Gateway.HostedImage.Tests";
    private const string TestProjectFile = TestProjectDir + "/CcDirector.Gateway.HostedImage.Tests.csproj";
    private const string TestFile = TestProjectDir + "/HostedImagePublishedArtifactTests.cs";
    private const string SolutionFile = "cc-director.sln";
    private const string DeployWorkflow = ".github/workflows/deploy-hosted-gateway.yml";
    private const string ContractWorkflow = ".github/workflows/hosted-image-contract.yml";
    private const string VerifyScript = ".github/scripts/verify_hosted_image_contract.py";

    /// <summary>The job identifier the deploy workflow gives the contract, and which its deploy job
    /// must declare a dependency on.</summary>
    private const string ContractJobId = "hosted-image-contract";

    [Fact]
    public void The_hosted_image_test_lives_in_its_own_project_and_that_project_is_not_in_the_solution()
    {
        var root = RepoRoot();

        Assert.True(File.Exists(Path.Combine(root, TestProjectFile)),
            $"{TestProjectFile} is gone. That project exists to hold the hosted image contract test "
            + "outside cc-director.sln; without it the test is either unrun or back in the continuous "
            + "integration step.");

        var test = ReadOrFail(root, TestFile);
        Assert.True(test.Contains("class HostedImagePublishedArtifactTests", StringComparison.Ordinal),
            $"{TestFile} no longer declares HostedImagePublishedArtifactTests. If it moved again, move "
            + "this guard with it - do not delete the guard.");

        // It must be in ONE place. Two copies drift, and the one nobody runs is the one that stays right.
        var oldHome = Path.Combine(root, "src", "CcDirector.Gateway.Tests",
            "HostedImagePublishedArtifactTests.cs");
        Assert.False(File.Exists(oldHome),
            "HostedImagePublishedArtifactTests is in CcDirector.Gateway.Tests as well as in its own "
            + "project. CcDirector.Gateway.Tests is in cc-director.sln, so that copy runs on every pull "
            + "request - which is the sixteen and a half minutes the move removed.");

        var solution = ReadOrFail(root, SolutionFile);

        // A control on the instrument before the claim that rests on it: an empty or truncated read of
        // the solution file would contain no project name at all, and "the name is not in here" would
        // then be true for the wrong reason.
        var projectsInSolution = Regex.Matches(solution, @"\.csproj").Count;
        Assert.True(projectsInSolution >= 20,
            $"{SolutionFile} names only {projectsInSolution} project files. That is not a solution this "
            + "repository would have, so the file was not read properly and the check below would pass "
            + "for the wrong reason.");

        Assert.False(solution.Contains("CcDirector.Gateway.HostedImage.Tests", StringComparison.Ordinal),
            $"{SolutionFile} now contains CcDirector.Gateway.HostedImage.Tests. The continuous "
            + "integration test step is `dotnet test cc-director.sln`, so adding it there puts the "
            + "16 minute 32 second hosted image test back on every pull request and every push to main. "
            + "If that is wanted, it is a decision to make deliberately - and this guard is where to "
            + "record it.");
    }

    [Fact]
    public void The_deploy_workflow_calls_the_contract_and_will_not_deploy_without_it()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, ContractWorkflow)),
            $"{ContractWorkflow} is gone, so nothing runs the hosted image contract test.");

        var deploy = ReadOrFail(root, DeployWorkflow);

        Assert.True(deploy.Contains($"uses: ./{ContractWorkflow}", StringComparison.Ordinal),
            $"{DeployWorkflow} no longer calls {ContractWorkflow}. The hosted deploy is the only run that "
            + "publishes the hosted image, and this is the only proof that image fails closed.");

        // Called by repository-relative path on purpose: that pins the called file to the commit being
        // shipped, so the test that gates the deploy is the test as it stands on that commit.
        Assert.False(Regex.IsMatch(deploy, @"uses:\s*[\w.-]+/[\w.-]+/\.github/workflows/hosted-image-contract\.yml@"),
            $"{DeployWorkflow} calls the contract workflow by owner/repository@ref instead of by the "
            + "relative path './'. A pinned ref runs the test as it stands somewhere else, not as it "
            + "stands on the commit being shipped.");

        Assert.True(Regex.IsMatch(deploy, @"require_main:\s*true"),
            $"{DeployWorkflow} no longer passes require_main: true to the contract workflow. Production "
            + "ships main and only main, and without this a misdirected deploy spends the whole test "
            + "before the refusal it was always going to get.");

        // The binding that makes it fail closed: the deploy job does not start unless the contract job
        // succeeded. GitHub does not run a job whose `needs` failed, errored, timed out or was skipped.
        var deployJob = JobBody(deploy, "deploy");
        Assert.False(deployJob is null, $"{DeployWorkflow} no longer has a job called 'deploy'. If it was "
            + "renamed, update this guard - do not delete it.");
        Assert.True(Regex.IsMatch(deployJob!, $@"needs:\s*(\[\s*)?{Regex.Escape(ContractJobId)}\b"),
            $"the 'deploy' job in {DeployWorkflow} no longer declares needs: {ContractJobId}. Without "
            + "that dependency the contract job and the deploy run side by side, and the image ships "
            + "whether or not the test passed. That is the whole of the fail-closed behaviour.");
    }

    [Fact]
    public void The_contract_workflow_decides_on_a_counted_presence_and_proves_the_verdict_fires()
    {
        var root = RepoRoot();
        var contract = ReadOrFail(root, ContractWorkflow);

        Assert.True(contract.Contains(TestProjectFile, StringComparison.Ordinal),
            $"{ContractWorkflow} no longer names {TestProjectFile}, so it is not running the hosted image "
            + "contract test.");

        // The inventory the run is reconciled against has to be DERIVED from the built assembly. A list
        // typed into the workflow would go stale the first time a test is added or renamed, and a stale
        // inventory passes on whatever it happens to know about.
        var listStep = StepBody(contract, "List the tests the built assembly declares");
        Assert.False(listStep is null, $"{ContractWorkflow} no longer lists the tests the assembly "
            + "declares. Without that list the verdict has nothing to reconcile the run against, and "
            + "'nothing failed' is all that is left - which is satisfied by a run that never happened.");
        Assert.True(listStep!.Contains("--list-tests", StringComparison.Ordinal),
            "The list step no longer asks the built assembly what it declares.");

        var runStep = StepBody(contract, "Run the hosted image contract test");
        Assert.False(runStep is null, $"{ContractWorkflow} no longer has a step that runs the test.");
        Assert.True(runStep!.Contains("trx", StringComparison.Ordinal),
            "The run step no longer writes a result file, so nothing records which tests actually ran.");

        var verifyStep = StepBody(contract, "Verify every declared test actually ran and passed");
        Assert.False(verifyStep is null, $"{ContractWorkflow} no longer has the verdict step. The exit "
            + "code of `dotnet test` on its own is an absence - it is satisfied by a run that discovered "
            + "no tests - and that is exactly what this step exists to refuse.");
        Assert.True(verifyStep!.Contains(VerifyScript, StringComparison.Ordinal),
            $"The verdict step no longer runs {VerifyScript}.");

        // The control. A check only ever run against the state you hope passes has demonstrated nothing.
        var controlStep = StepBody(contract, "Prove the verdict refuses a run that did not happen");
        Assert.False(controlStep is null, $"{ContractWorkflow} no longer proves the verdict FIRES before "
            + "trusting it. Without the control, a verdict that had silently stopped working would read "
            + "exactly like a hosted image that passed.");
        foreach (var arm in new[] { "declared list with no tests", "wrote no result file", "NotExecuted" })
        {
            Assert.True(controlStep!.Contains(arm, StringComparison.Ordinal),
                $"The control no longer exercises the '{arm}' case. Each of the three arms is a way the "
                + "gate can certify a run that never happened, and each has to be shown to be refused.");
        }

        var script = ReadOrFail(root, VerifyScript);
        Assert.True(script.Contains("outcome != \"Passed\"", StringComparison.Ordinal),
            $"{VerifyScript} no longer refuses a result that is not Passed. A skipped test would then "
            + "satisfy the deploy gate, which is the fail-open this whole arrangement exists to avoid.");
    }

    private static string ReadOrFail(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"{relativePath} does not exist.");
        var text = File.ReadAllText(full);
        Assert.False(string.IsNullOrWhiteSpace(text), $"{relativePath} is empty, so every assertion "
            + "about its contents would pass for the wrong reason.");
        return text;
    }

    /// <summary>
    /// The text of a top-level workflow job, from its `  &lt;id&gt;:` line to the next job at the same
    /// indentation, so an assertion about one job cannot be satisfied by text in another.
    /// </summary>
    private static string? JobBody(string yml, string jobId)
    {
        var match = Regex.Match(yml, $@"^  {Regex.Escape(jobId)}:\s*$", RegexOptions.Multiline);
        if (!match.Success) return null;
        var rest = yml[(match.Index + match.Length)..];
        var next = Regex.Match(rest, @"^  [A-Za-z0-9_-]+:\s*$", RegexOptions.Multiline);
        return next.Success ? rest[..next.Index] : rest;
    }

    /// <summary>
    /// The text of a workflow step, from its `- name:` line to the next step at the same indentation.
    /// </summary>
    private static string? StepBody(string yml, string stepName)
    {
        var marker = $"- name: {stepName}";
        var start = yml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var lineStart = yml.LastIndexOf('\n', start) + 1;
        var indent = start - lineStart;
        var rest = yml[(start + marker.Length)..];
        var next = Regex.Match(rest, $@"\n {{{indent}}}- name: ");
        return next.Success ? rest[..next.Index] : rest;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
