using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The hosted deploy refuses to deploy onto a production site that is already down (issue #4515). On
/// 2026-10-26 a deploy pinned a second image onto a production site the deploy before it had left dead,
/// and recorded "unknown" as the outgoing commit and the broken image as the outgoing image. The gate is a
/// step in <c>.github/workflows/deploy-hosted-gateway.yml</c> that runs <c>scripts/check-production-healthy.sh</c>;
/// the script's behaviour against every shape of an unhealthy /healthz is proved by
/// <c>scripts/check-production-healthy.test.sh</c> against a local fake server.
///
/// This guard keeps the step honest from the default gate, without starting any process: the step exists,
/// runs BEFORE the outgoing record is read and before production is touched, cannot be switched off by an
/// <c>if:</c>, and its escape hatch is a boolean input that defaults to false and is handed to the script,
/// which writes the loud run-summary warning.
/// </summary>
public sealed class ProductionHealthGateRunsBeforeEveryDeployTests
{
    private const string GateStepName = "- name: Refuse to deploy onto a production site that is already unhealthy";
    private const string OutgoingCommitStepName = "- name: Read the outgoing (currently-live) commit";
    private const string OutgoingImageStepName = "- name: Read the outgoing (currently-live) image";
    private const string DeployStepName = "- name: Deploy the new image in place and measure the outage";
    private const string ScriptPath = "scripts/check-production-healthy.sh";

    [Fact]
    public void DeployWorkflow_RefusesUnhealthyProduction_BeforeItReadsTheOutgoingRecordOrDeploys()
    {
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");

        var gateAt = workflow.IndexOf(GateStepName, StringComparison.Ordinal);
        var outgoingCommitAt = workflow.IndexOf(OutgoingCommitStepName, StringComparison.Ordinal);
        var outgoingImageAt = workflow.IndexOf(OutgoingImageStepName, StringComparison.Ordinal);
        var deployAt = workflow.IndexOf(DeployStepName, StringComparison.Ordinal);
        Assert.True(gateAt > 0, "The hosted deploy no longer refuses to deploy onto a production site that is already unhealthy.");
        Assert.True(outgoingCommitAt > 0 && outgoingImageAt > 0, "The outgoing-record steps were renamed; this guard can no longer tell whether the gate runs before them.");
        Assert.True(deployAt > 0, "The deploy step was renamed; this guard can no longer tell whether the gate runs before it.");
        Assert.True(gateAt < outgoingCommitAt, "The production health gate runs AFTER the outgoing commit is read, so a dead production is recorded as the rollback target.");
        Assert.True(gateAt < outgoingImageAt, "The production health gate runs AFTER the outgoing image is read, so a broken image is recorded as the rollback target.");
        Assert.True(gateAt < deployAt, "The production health gate runs AFTER the deploy step, so a new image is pinned onto a dead production.");

        // Both commands that change the production site - the image pin and the app settings write, which
        // restarts the site - must come after the gate wherever they live. Found as commands, not in comments.
        var firstPin = FirstCommand(workflow, "az webapp config container set");
        var firstSettingsWrite = FirstCommand(workflow, "az webapp config appsettings set");
        Assert.True(firstPin > gateAt, "Something pins a container image before the production health gate runs.");
        Assert.True(firstSettingsWrite > gateAt, "Something writes the production app settings, restarting the site, before the production health gate runs.");

        var stepEnd = workflow.IndexOf("\n      - name:", gateAt + GateStepName.Length, StringComparison.Ordinal);
        var step = workflow[gateAt..stepEnd];

        // The step carries only its env and its script: an `if:` or `continue-on-error:` would switch the gate
        // off without the run saying so. The escape hatch goes through the script, which reports it.
        var stepKeys = Regex.Matches(step, @"^        ([a-z-]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "env", "run" }, stepKeys);
        Assert.Contains("DEPLOY_TO_UNHEALTHY_PRODUCTION: ${{ inputs.deploy_to_unhealthy_production }}", step, StringComparison.Ordinal);
        Assert.Contains($"bash {ScriptPath} \"$GATEWAY_URL\"", step, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployWorkflow_EscapeHatchInput_IsABooleanThatDefaultsToFalse()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");

        var m = Regex.Match(workflow,
            @"^      deploy_to_unhealthy_production:\n(?<body>(?:        .*\n)+)", RegexOptions.Multiline);
        Assert.True(m.Success, "The deploy workflow no longer declares the deploy_to_unhealthy_production input.");
        var body = m.Groups["body"].Value;
        Assert.Contains("        type: boolean\n", body, StringComparison.Ordinal);
        Assert.Contains("        default: false\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_RequiresACommitAndWarnsLoudlyWhenTheEscapeHatchIsUsed()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, ScriptPath));

        // The pass condition is a presence: HTTP 200 and a non-empty commit read from JSON.
        Assert.Contains("\"$code\" != \"200\"", script, StringComparison.Ordinal);
        Assert.Contains(".commit", script, StringComparison.Ordinal);
        Assert.Contains("-z \"$LIVE_COMMIT\"", script, StringComparison.Ordinal);

        // A short per-request timeout, so a dead app held by the front end is a failure, not a long wait.
        Assert.Matches(@"TIMEOUT=\$\{HEALTH_TIMEOUT_SECONDS:-(\d|1\d)\}", script);

        // The refusal names the state and points at the rollback workflow, which must exist.
        Assert.Contains("REFUSED: production is already unhealthy", script, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK_WORKFLOW=\"rollback-hosted-gateway.yml\"", script, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, ".github", "workflows", "rollback-hosted-gateway.yml")),
            "The refusal points at rollback-hosted-gateway.yml, which no longer exists.");

        // The escape hatch writes its warning into the run summary, not only the log.
        Assert.Contains("### WARNING: deployed onto UNHEALTHY production - the production health gate was SKIPPED", script, StringComparison.Ordinal);
        Assert.Contains("GITHUB_STEP_SUMMARY", script, StringComparison.Ordinal);

        // The proof that runs the script against a fake /healthz is committed beside it.
        Assert.True(File.Exists(Path.Combine(root, "scripts", "check-production-healthy.test.sh")),
            "The fake-server proof of the probe script is missing.");
    }

    /// <summary>The position of the first line that RUNS <paramref name="command"/> (possibly after <c>if !</c>),
    /// skipping comment lines, so a comment quoting the command cannot move the answer.</summary>
    private static int FirstCommand(string workflow, string command)
    {
        var m = Regex.Match(workflow, @"^[ \t]*(?:if !\s*)?" + Regex.Escape(command), RegexOptions.Multiline);
        Assert.True(m.Success, $"The deploy workflow no longer runs '{command}'; this guard cannot place it.");
        return m.Index;
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
