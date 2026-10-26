using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The hosted deploy boots the image it is about to pin, with the hosted contract satisfied, and requires a
/// sustained healthy <c>/healthz</c> carrying the run's commit BEFORE production is touched (issue #4513). Until
/// then the only pre-deploy image check proved the negative - that the image refuses to start without the
/// contract - so on 2026-10-26 production was the first thing to ask whether commit 86b37ace5 starts at all.
///
/// This guard lives in the default gate, beside <see cref="HostedImageContractRunsBeforeEveryDeployTests"/>, so
/// the boot proof cannot be quietly dropped, moved after the production pin, switched off, or pointed at a
/// different image. It also pins the pass condition in the script the step runs: a presence (three consecutive
/// healthy answers, three seconds apart, carrying the commit), not an absence of errors.
/// </summary>
public sealed class HostedImageBootsBeforeEveryDeployTests
{
    private const string DigestStepName = "- name: Read the built image digest";
    private const string BootStepName = "- name: Prove the built image boots and serves before production is touched";
    private const string DeployStepName = "- name: Deploy the new image in place and measure the outage";
    private const string ScriptPath = "scripts/prove-image-boots.sh";

    [Fact]
    public void DeployWorkflow_BootsTheBuiltImage_AfterTheDigestAndBeforeTheProductionPin()
    {
        // Line endings are normalised so a Windows checkout that converts them reads the same text.
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");

        var digestAt = workflow.IndexOf(DigestStepName, StringComparison.Ordinal);
        var bootAt = workflow.IndexOf(BootStepName, StringComparison.Ordinal);
        var deployAt = workflow.IndexOf(DeployStepName, StringComparison.Ordinal);
        Assert.True(bootAt > 0, "The hosted deploy no longer proves the built image boots and serves before production is touched.");
        Assert.True(digestAt > 0, "The digest step was renamed; this guard can no longer tell whether the boot proof runs after it.");
        Assert.True(deployAt > 0, "The deploy step was renamed; this guard can no longer tell whether the boot proof runs before it.");
        Assert.True(digestAt < bootAt, "The boot proof runs BEFORE the digest is read, so it cannot be proving the image that ships.");
        Assert.True(bootAt < deployAt, "The boot proof runs AFTER the deploy step, so an image that never starts would already be live.");

        var stepEnd = workflow.IndexOf("\n      - name:", bootAt + BootStepName.Length, StringComparison.Ordinal);
        var step = workflow[bootAt..stepEnd];

        // Only env and run: an `if:`, `continue-on-error:` or `timeout-minutes:` would keep every string below
        // and stop the proof from gating the deploy.
        var stepKeys = Regex.Matches(step, @"^        ([a-z-]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "env", "run" }, stepKeys);

        // It boots the image this run built, by digest, and expects this run's commit.
        Assert.Contains("@${{ steps.digest.outputs.digest }}", step, StringComparison.Ordinal);
        // The run line is the whole line, so a trailing `|| true` cannot make the step unable to fail.
        var runLines = Regex.Matches(step, @"^        run: (.*)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { $"bash {ScriptPath} \"$IMAGE\" \"${{{{ steps.short.outputs.short }}}}\"" }, runLines);
    }

    [Fact]
    public void BootScript_PassConditionIsThreeConsecutiveHealthyAnswersCarryingTheCommit()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, ScriptPath)).Replace("\r\n", "\n");

        // The contract is satisfied, not bypassed: hosted on, an https public URL, a real database, and neither
        // auth-disable flag set anywhere.
        Assert.Contains("-e CC_GATEWAY_HOSTED=1", script, StringComparison.Ordinal);
        Assert.Contains("-e CC_GATEWAY_PUBLIC_URL=https://smoke.invalid", script, StringComparison.Ordinal);
        Assert.Contains("-e CC_GATEWAY_DB_CONNECTION=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-e CC_GATEWAY_NO_AUTH", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-e CC_GATEWAY_AUTH", script, StringComparison.Ordinal);

        // A presence: three passes, three seconds apart, each a 200 whose parsed body carries the expected commit.
        Assert.Contains("REQUIRED_PASSES=3", script, StringComparison.Ordinal);
        Assert.Contains("PASS_INTERVAL_SECONDS=3", script, StringComparison.Ordinal);
        Assert.Contains("\"$code\" != \"200\"", script, StringComparison.Ordinal);
        Assert.Contains("[ -z \"$body\" ]", script, StringComparison.Ordinal);
        Assert.Contains("jq -e 'type == \"object\"'", script, StringComparison.Ordinal);
        Assert.Contains("\"$commit\" != \"$EXPECTED_COMMIT\"", script, StringComparison.Ordinal);
        Assert.Contains("passes=0", script, StringComparison.Ordinal);

        // Bounded, and a failure prints what the container said and fails the step.
        Assert.Contains("BOOT_TIMEOUT_SECONDS", script, StringComparison.Ordinal);
        // The container's own output: stdout and stderr together, the whole log, not a tail.
        Assert.Contains(" logs \"$GW\" 2>&1", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^fail\(\) \{\n(?:.*\n)*?  exit 1\n\}", RegexOptions.Multiline), script);
        Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);

        // This guard reads text only. What the script DOES - pass a healthy fake, fail every unhealthy shape and
        // print the container's output - is proved by running it against fake Gateway images, in the web job of
        // continuous integration, so an early `exit 0` goes red there.
        Assert.True(File.Exists(Path.Combine(root, "scripts", "prove-image-boots.test.sh")),
            "The fake-image proof of the boot script is missing.");
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");
        Assert.Matches(new Regex(@"^        run: bash scripts/prove-image-boots\.test\.sh$", RegexOptions.Multiline), ci);
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
