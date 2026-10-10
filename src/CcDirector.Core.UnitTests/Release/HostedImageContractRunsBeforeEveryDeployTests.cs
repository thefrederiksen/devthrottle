using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The hosted image's fail-closed contract (production-readiness item MH-3) is proved in the hosted deploy
/// workflow, on the exact image digest about to be deployed - not in the test suite. It used to be
/// HostedImagePublishedArtifactTests in Gateway.Tests, which ran a Debug <c>dotnet publish</c> of the host
/// inside the test (median 15 seconds, worst 30 minutes on a loaded machine) to prove a property of an
/// artifact only the deploy builds. The proof is now a step in <c>.github/workflows/deploy-hosted-gateway.yml</c>.
///
/// This guard keeps that step honest from the default gate, which is why it lives here and not beside the
/// constants in the parked Gateway.UnitTests: a parked guard tells nobody anything at commit time. It checks
/// the step exists, runs BEFORE the step that touches production, cannot be switched off, and blanks exactly
/// the variables the contract reads. Those are taken from the Gateway's own source - every
/// <c>readEnv(...)</c> in <c>HostedStartupContract.Assert</c>, resolved to its constant's value - so renaming
/// a variable, or teaching the contract a new one, fails here rather than leaving the deploy proving a
/// contract that no longer exists.
/// </summary>
public sealed class HostedImageContractRunsBeforeEveryDeployTests
{
    private const string ProofStepName = "- name: Prove the built image refuses to start without the hosted contract";
    private const string DeployStepName = "- name: Deploy the new image in place and measure the outage";

    [Fact]
    public void DeployWorkflow_ProvesTheHostedContractOnTheBuiltImage_BeforeItDeploys()
    {
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "deploy-hosted-gateway.yml"));

        var proofAt = workflow.IndexOf(ProofStepName, StringComparison.Ordinal);
        var deployAt = workflow.IndexOf(DeployStepName, StringComparison.Ordinal);
        Assert.True(proofAt > 0, "The hosted deploy no longer proves the built image refuses to start without the hosted contract.");
        Assert.True(deployAt > 0, "The deploy step was renamed; this guard can no longer tell whether the proof runs before it.");
        Assert.True(proofAt < deployAt, "The hosted contract proof runs AFTER the deploy step, so a fail-open image would already be live.");

        var stepEnd = workflow.IndexOf("\n      - name:", proofAt + ProofStepName.Length, StringComparison.Ordinal);
        var step = workflow[proofAt..stepEnd];

        // The step carries only its env and its script: an `if:` or `continue-on-error:` would keep every string
        // below and stop the proof from gating the deploy.
        var stepKeys = Regex.Matches(step, @"^        ([a-z-]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "env", "run" }, stepKeys);

        // It runs the image this run built, by digest - not a tag that could point elsewhere.
        Assert.Contains("@${{ steps.digest.outputs.digest }}", step, StringComparison.Ordinal);

        // Every variable the contract reads is blanked, so the image boots into "contract missing".
        var contractVariables = ContractVariables(root);
        Assert.Equal(5, contractVariables.Count);
        foreach (var name in contractVariables)
            Assert.Contains($"-e {name}= ", step + " ", StringComparison.Ordinal);

        // The image-wide marker is checked by its real name, and both known entries must be present, so an
        // empty or single-entry set cannot pass while proving nothing.
        var marker = ConstantValue(root, Path.Combine("src", "CcDirector.Gateway", "GatewayHostedMode.cs"), "HostedImageMarkerFileName");
        Assert.Contains("/app/" + marker, step, StringComparison.Ordinal);
        Assert.Contains("CcDirector.Gateway.Host.dll CcDirector.Gateway.dll", step, StringComparison.Ordinal);

        // The pass condition is a presence: exit 2 AND the refusal sentence. A process still running is a failure.
        Assert.Contains("\"$CODE\" -ne 2", step, StringComparison.Ordinal);
        Assert.Contains("REFUSING TO START", step, StringComparison.Ordinal);
        Assert.Contains("\"$CODE\" -eq 124", step, StringComparison.Ordinal);
    }

    /// <summary>The values of the variables <c>HostedStartupContract.Assert</c> reads: every
    /// <c>readEnv(Type.Constant)</c> in it, resolved to the constant's string in that type's own source file
    /// (<c>Data.GatewayDatabase</c> is <c>Data/GatewayDatabase.cs</c>). Reading only those files keeps this
    /// guard cheap enough for the default gate.</summary>
    private static List<string> ContractVariables(string root)
    {
        var gatewaySrc = Path.Combine("src", "CcDirector.Gateway");
        var contract = File.ReadAllText(Path.Combine(root, gatewaySrc, "HostedStartupContract.cs"));
        var reads = Regex.Matches(contract, @"readEnv\(([\w.]+)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.True(reads.Count > 0, "HostedStartupContract.cs no longer reads its variables through readEnv(...); this guard cannot find them.");

        var values = new List<string>();
        foreach (var read in reads)
        {
            var parts = read.Split('.');
            Assert.True(parts.Length >= 2, $"readEnv({read}) does not name a constant on a type; this guard cannot resolve it.");
            var typeFile = Path.Combine(new[] { gatewaySrc }.Concat(parts[..^1]).ToArray()) + ".cs";
            values.Add(ConstantValue(root, typeFile, parts[^1]));
        }
        return values;
    }

    private static string ConstantValue(string root, string relativeFile, string name)
    {
        var text = File.ReadAllText(Path.Combine(root, relativeFile));
        var m = Regex.Match(text, $@"const string {Regex.Escape(name)} = ""([^""]+)""");
        Assert.True(m.Success, $"Could not find the constant {name} in {relativeFile}.");
        return m.Groups[1].Value;
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
