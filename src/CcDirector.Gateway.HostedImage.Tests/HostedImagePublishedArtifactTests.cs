using System.Diagnostics;
using System.Runtime.CompilerServices;
using CcDirector.Gateway.Tests;
using Xunit;

namespace CcDirector.Gateway.HostedImage.Tests;

/// <summary>
/// Published-artifact regression test for the fail-CLOSED hosted identity (production-readiness item MH-3).
///
/// Publishing the hosted host (<c>CcDirector.Gateway.Host</c>) does NOT ship a single runnable executable:
/// it also emits the referenced, UNMARKED <c>CcDirector.Gateway.dll</c> with its own
/// deps.json/runtimeconfig.json. Codex's review of PR #1968 reproduced the hole - running
/// <c>dotnet CcDirector.Gateway.dll</c> from the published output with every hosted variable unset served
/// <c>/healthz</c> 200, because the compiled-in <see cref="HostedGatewayImageAttribute"/> only identifies the
/// launcher it is stamped on, so the contract gate was a no-op for the other entry executable.
///
/// The fix makes hosted identity IMAGE-WIDE: the host bakes a marker file
/// (<see cref="GatewayHostedMode.HostedImageMarkerFileName"/>) into its published output, and
/// <see cref="GatewayHostedMode.IsHostedImage"/> reads it from the deployment directory every shipped entry
/// executable shares. This test proves the whole thing against the ACTUAL published artifact rather than a
/// unit seam: it publishes the host, then asserts that EVERY runnable entry executable in the output -
/// including the unmarked <c>CcDirector.Gateway.dll</c> Codex ran - fails closed (a non-zero exit with no
/// listener) when the hosted contract is missing.
///
/// WHERE THIS RUNS. Not in continuous integration. It is the only test in its own project, which is
/// deliberately outside <c>cc-director.sln</c>, and the hosted deploy workflow runs it as a job the deploy
/// waits on - see <c>.github/workflows/hosted-image-contract.yml</c>. It proves a property of the PUBLISHED
/// hosted image, so the run that publishes that image is the run that should have to pass it. It cost
/// 16 minutes 32 seconds of every continuous integration run before the move, measured in run 35461379953
/// on 19 September 2026.
/// </summary>
public sealed class HostedImagePublishedArtifactTests
{
    // The contract-refusal exit code GatewayEntryPoint returns (distinct from a generic fault). Asserting on
    // it proves the process refused because of the contract, not because it happened to crash for some other
    // reason - a bare "non-zero" would pass on an unrelated failure and hide a real regression.
    private const int ContractRefusalExitCode = 2;

    // The five hosted-contract variables. They are stripped from every child process so the published entry
    // executable boots into exactly the "hosted image, contract missing" state that must fail closed.
    private static readonly string[] HostedContractEnvVars =
    {
        "CC_GATEWAY_HOSTED", "CC_GATEWAY_NO_AUTH", "CC_GATEWAY_AUTH",
        "CC_GATEWAY_PUBLIC_URL", "CC_GATEWAY_DB_CONNECTION",
    };

    [Fact]
    public void Every_published_entry_executable_fails_closed_without_the_hosted_contract()
    {
        var publishDir = Path.Combine(
            Path.GetTempPath(), "cc-hosted-publish-" + Guid.NewGuid().ToString("N"));

        try
        {
            Publish(HostProjectPath(), publishDir);

            // The marker that makes identity image-wide must actually be in the shipped output - it is what
            // makes the unmarked entry (and any apphost that routes through the same managed entry point)
            // fail closed. If it is missing, the whole mechanism is absent and the assertions below would be
            // proving nothing.
            var markerPath = Path.Combine(publishDir, GatewayHostedMode.HostedImageMarkerFileName);
            Assert.True(File.Exists(markerPath),
                $"the image-wide hosted marker '{GatewayHostedMode.HostedImageMarkerFileName}' was not baked "
                + $"into the published output at {publishDir}.");

            // Every runnable "dotnet <dll>" entry the artifact ships - identified by the presence of a
            // sibling runtimeconfig.json, which is exactly what makes a dll launchable. This is the Codex
            // reproduction vector and is deterministic across platforms.
            var entryDlls = RunnableEntryDlls(publishDir);

            // The set must contain BOTH the marked host AND the unmarked Gateway dll - the whole point is
            // that the OTHER, unmarked entry ships and used to bypass the gate. An empty or single-entry set
            // would let this test pass while proving nothing (a dead test that looks like coverage).
            Assert.Contains("CcDirector.Gateway.Host.dll", entryDlls.Select(Path.GetFileName));
            Assert.Contains("CcDirector.Gateway.dll", entryDlls.Select(Path.GetFileName));
            Assert.True(entryDlls.Count >= 2,
                $"expected at least two runnable entry executables in {publishDir}, found "
                + $"{entryDlls.Count}: {string.Join(", ", entryDlls.Select(Path.GetFileName))}.");

            foreach (var dll in entryDlls)
                AssertEntryFailsClosed(publishDir, dll);
        }
        finally
        {
            TryDeleteDirectory(publishDir);
        }
    }

    /// <summary>
    /// Run one published entry executable as <c>dotnet &lt;dll&gt;</c> with the hosted contract stripped from
    /// its environment, and assert it fails closed: it must EXIT with the contract-refusal code, not stay up
    /// serving. A process that is still alive after the wait is the fail-OPEN regression (it started a
    /// listener) - that is the exact failure this test exists to catch, so it is killed and the test fails.
    /// </summary>
    private static void AssertEntryFailsClosed(string publishDir, string entryDll)
    {
        // Reserved for the whole child run rather than probed and released (issue #1156). The entry is
        // expected to refuse on the missing hosted contract BEFORE it ever binds, so holding the port does
        // not change the passing path - but it stops another process claiming the number mid-test, which
        // would otherwise turn this contract assertion into an unrelated address-conflict failure. If the
        // entry ever regresses and does try to bind, it fails here too, which is the correct outcome.
        using var deadPort = DeadPortReservation.Reserve();
        var psi = new ProcessStartInfo("dotnet", $"\"{entryDll}\" --port {deadPort.Port}")
        {
            WorkingDirectory = publishDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var name in HostedContractEnvVars)
            psi.Environment.Remove(name);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        // The contract gate runs BEFORE the host is built, so a fail-closed entry exits in well under this
        // budget; the generous window only absorbs cold dotnet startup. A process still running at the end
        // has passed the gate and started serving - the regression.
        var exited = proc.WaitForExit(TimeSpan.FromSeconds(60));

        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Assert.Fail(
                $"{Path.GetFileName(entryDll)} did NOT fail closed: it was still running (serving on port "
                + $"{deadPort.Port}) after 60s with the hosted contract missing. The hosted image must refuse to start, "
                + "not boot as a self-host Gateway.");
        }

        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        Assert.True(proc.ExitCode == ContractRefusalExitCode,
            $"{Path.GetFileName(entryDll)} exited {proc.ExitCode}, expected {ContractRefusalExitCode} "
            + $"(hosted contract refusal). stdout: {outText} | stderr: {errText}");

        // The refusal must name the contract, so an operator learns why - and so this asserts the RIGHT exit
        // 2, not some coincidental one.
        Assert.Contains("REFUSING TO START", outText + errText);
    }

    /// <summary>Publish the host project to <paramref name="outputDir"/>, skipping the release-only web
    /// (npm) build targets so the publish needs no Node.js and stays fast. Fails the test loudly with the
    /// publish output if it does not succeed.</summary>
    private static void Publish(string projectPath, string outputDir)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"publish \"{projectPath}\" -c Debug -o \"{outputDir}\" --nologo "
            + "-p:RunMobileBuild=false -p:RunCockpitBuild=false -p:RunWorkspaceTypecheck=false")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit(TimeSpan.FromMinutes(5));
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Assert.Fail($"publishing {projectPath} did not finish within 5 minutes.");
        }

        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        Assert.True(proc.ExitCode == 0,
            $"publishing {projectPath} failed with exit {proc.ExitCode}. stdout: {outText} | stderr: {errText}");
    }

    /// <summary>Every runnable "dotnet &lt;dll&gt;" entry in a published output: a dll with a sibling
    /// runtimeconfig.json (which is what a framework-dependent app needs to launch).</summary>
    private static List<string> RunnableEntryDlls(string publishDir)
    {
        var entries = new List<string>();
        foreach (var config in Directory.GetFiles(publishDir, "*.runtimeconfig.json"))
        {
            // "X.runtimeconfig.json" -> "X.dll"
            var name = Path.GetFileName(config);
            var stem = name[..^".runtimeconfig.json".Length];
            var dll = Path.Combine(publishDir, stem + ".dll");
            if (File.Exists(dll))
                entries.Add(dll);
        }
        return entries;
    }

    /// <summary>The hosted host csproj, located from this source file's own path - the tests always run from
    /// a checkout, and bin-relative paths would break under different runners.</summary>
    private static string HostProjectPath([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.HostedImage.Tests/HostedImagePublishedArtifactTests.cs
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        return Path.Combine(repoRoot, "src", "CcDirector.Gateway.Host", "CcDirector.Gateway.Host.csproj");
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort test cleanup */ }
    }
}
