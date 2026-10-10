using System.Runtime.CompilerServices;
using CcDirector.Gateway.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The hosted image's fail-closed contract (production-readiness item MH-3) is proved in the hosted deploy
/// workflow, on the exact image digest about to be deployed - not in the test suite. It used to be
/// HostedImagePublishedArtifactTests in Gateway.Tests, which ran a Debug <c>dotnet publish</c> of the host
/// inside the test (median 15 seconds, worst 30 minutes on a loaded machine) to prove a property of an
/// artifact only the deploy builds. The proof is now a step in <c>.github/workflows/deploy-hosted-gateway.yml</c>.
///
/// This guard keeps that step honest from the default gate: it must exist, it must run BEFORE the step that
/// touches production, and it must blank every variable the contract reads - named here from the product's
/// own constants, so renaming one without updating the workflow fails here rather than leaving the deploy
/// proving a contract that no longer exists.
/// </summary>
public sealed class HostedImageContractRunsBeforeEveryDeployTests
{
    private const string ProofStepName = "- name: Prove the built image refuses to start without the hosted contract";
    private const string DeployStepName = "- name: Deploy the new image in place and measure the outage";

    [Fact]
    public void DeployWorkflow_ProvesTheHostedContractOnTheBuiltImage_BeforeItDeploys()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-hosted-gateway.yml"));

        var proofAt = workflow.IndexOf(ProofStepName, StringComparison.Ordinal);
        var deployAt = workflow.IndexOf(DeployStepName, StringComparison.Ordinal);
        Assert.True(proofAt > 0, "The hosted deploy no longer proves the built image refuses to start without the hosted contract.");
        Assert.True(deployAt > 0, "The deploy step was renamed; this guard can no longer tell whether the proof runs before it.");
        Assert.True(proofAt < deployAt, "The hosted contract proof runs AFTER the deploy step, so a fail-open image would already be live.");

        var step = workflow[proofAt..workflow.IndexOf("\n      - name:", proofAt + ProofStepName.Length, StringComparison.Ordinal)];

        // It runs the image this run built, by digest - not a tag that could point elsewhere.
        Assert.Contains("@${{ steps.digest.outputs.digest }}", step, StringComparison.Ordinal);

        // Every variable the contract reads is blanked, so the image boots into "contract missing".
        foreach (var name in new[]
                 {
                     GatewayHostedMode.HostedEnvVar, GatewayHost.AuthDisabledEnvVar, GatewayHost.AuthEnabledEnvVar,
                     GatewayPublicUrl.PublicBaseUrlEnvVar, GatewayDatabase.PostgresConnectionEnvVar,
                 })
            Assert.Contains($"-e {name}= ", step + " ", StringComparison.Ordinal);

        // The image-wide marker is checked by its real name, and both known entries must be present, so an
        // empty or single-entry set cannot pass while proving nothing.
        Assert.Contains("/app/" + GatewayHostedMode.HostedImageMarkerFileName, step, StringComparison.Ordinal);
        Assert.Contains("CcDirector.Gateway.Host.dll CcDirector.Gateway.dll", step, StringComparison.Ordinal);

        // The pass condition is a presence: exit 2 AND the refusal sentence. A process still running is a failure.
        Assert.Contains("\"$CODE\" -ne 2", step, StringComparison.Ordinal);
        Assert.Contains("REFUSING TO START", step, StringComparison.Ordinal);
        Assert.Contains("\"$CODE\" -eq 124", step, StringComparison.Ordinal);
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
