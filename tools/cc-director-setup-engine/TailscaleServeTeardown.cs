using System.Diagnostics;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of tearing down CC Director's Tailscale Serve mapping.</summary>
public sealed record TailscaleTeardownResult(bool Attempted, bool Removed, string? Error);

/// <summary>
/// Removes ONLY CC Director's Tailscale Serve front-door mapping (issue #257): the
/// https://&lt;tailnet&gt;:443 -&gt; gateway proxy the Gateway's TailscaleServeProvisioner asserts.
/// It runs exactly the inverse of that provisioner's front-door command - <c>serve --https=443 off</c>
/// - and touches nothing else, so any other `tailscale serve` config on the machine is left intact
/// (Assumption 5).
///
/// If the tailscale CLI is not installed, teardown is a no-op (remote HTTPS was never provisioned
/// on this machine) - reported as "not attempted", never an error. The OS call is isolated behind
/// the <see cref="Runner"/> seam so the policy is unit-testable without a real tailnet.
/// </summary>
public static class TailscaleServeTeardown
{
    /// <summary>The public front-door HTTPS port the Gateway maps (mirrors TailscaleServeProvisioner).</summary>
    public const int FrontDoorHttpsPort = 443;

    /// <summary>Runs one tailscale invocation. Returns whether the CLI was available and, if so,
    /// the exit code + stderr. The seam: tests substitute a fake; production shells out.</summary>
    public delegate (bool Available, int ExitCode, string Error) Runner(string arguments);

    /// <summary>Remove the 443 front-door mapping. Absent tailscale CLI -&gt; not attempted (no-op).
    /// "already off"/"does not exist" from the CLI counts as removed (idempotent).</summary>
    public static TailscaleTeardownResult RemoveFrontDoor(Runner? runner = null)
    {
        var run = runner ?? DefaultRunner;
        var (available, exitCode, error) = run($"serve --https={FrontDoorHttpsPort} off");
        if (!available)
            return new TailscaleTeardownResult(Attempted: false, Removed: false, Error: null);

        if (exitCode == 0 || IsAlreadyOff(error))
            return new TailscaleTeardownResult(Attempted: true, Removed: true, Error: null);

        return new TailscaleTeardownResult(Attempted: true, Removed: false,
            Error: string.IsNullOrWhiteSpace(error) ? $"tailscale exit {exitCode}" : error.Trim());
    }

    /// <summary>A non-zero exit whose message means the mapping was already gone is still success.
    /// Pure, so the idempotency rule is unit-testable.</summary>
    public static bool IsAlreadyOff(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no serve config", StringComparison.OrdinalIgnoreCase));

    /// <summary>tailscale-CLI-backed runner. Reports Available=false when the CLI is absent so
    /// teardown is a clean no-op on machines that never had remote HTTPS.</summary>
    public static (bool Available, int ExitCode, string Error) DefaultRunner(string arguments)
    {
        // External-process boundary: own the try-catch (a missing CLI or a launch failure becomes
        // "not available", so teardown degrades to a no-op instead of failing the whole uninstall).
        try
        {
            var psi = new ProcessStartInfo("tailscale", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (Available: false, ExitCode: -1, Error: "could not start tailscale");
            var stderr = p.StandardError.ReadToEnd();
            _ = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return (Available: true, p.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            // Win32Exception "file not found" => CLI not installed => clean no-op.
            return (Available: false, ExitCode: -1, Error: ex.Message);
        }
    }
}
