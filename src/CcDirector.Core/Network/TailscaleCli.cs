using System.Diagnostics;

namespace CcDirector.Core.Network;

/// <summary>
/// The single way any cc-director process invokes the tailscale CLI.
///
/// Why this exists (issues #179 / #197 / #200): a <c>tailscale serve</c> command is a
/// whole-config read-modify-write performed by the CLI (it reads the current serve table,
/// merges the change, and writes the whole table back). Several cc-director processes on
/// one machine mutate that table: the Gateway's TailscaleServeProvisioner (443 front door
/// + back-compat local Director mappings) and every Director's
/// TailscaleServeSelfProvisioner (its own port, issue #197). Two concurrent CLI
/// invocations can interleave their read and write and silently clobber each other's
/// entries - which matches the observed "serve table loses mappings with no cc-director
/// process removing them" production failures (#179: the 443 front door vanished; #200:
/// 443 found pointing at a dead port). Every serve-MUTATING invocation therefore
/// serializes on one cross-process named mutex. Reads (<c>serve status</c>) do not mutate
/// and stay lock-free.
/// </summary>
public static class TailscaleCli
{
    /// <summary>Process timeout for one CLI invocation.</summary>
    public static TimeSpan CommandTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Cross-process mutex name serializing every serve-mutating CLI call on this
    /// machine. Global\ scope so it spans sessions; all cc-director processes run as the
    /// same user, so ACLs never block the open.</summary>
    internal const string ServeMutexName = @"Global\cc-director-tailscale-serve";

    /// <summary>How long a caller waits for the serve mutex before giving up. A holder is
    /// bounded by <see cref="CommandTimeout"/>; 30 s covers a holder that itself timed out
    /// plus one queued caller ahead of us.</summary>
    public static TimeSpan MutexAcquireTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Full path of the tailscale CLI on this machine, or null when it is not installed.
    /// On unix the bare command name is returned as a PATH fallback; on Windows there is
    /// no conventional install-to-PATH, so a missing known path means not installed.
    /// </summary>
    public static string? ResolveExePath()
    {
        foreach (var path in TailscaleIdentity.CandidateExePaths())
            if (File.Exists(path)) return path;

        if (!OperatingSystem.IsWindows())
            return "tailscale";

        return null;
    }

    /// <summary>True when the tailscale CLI is present on this machine.</summary>
    public static bool IsAvailable => ResolveExePath() is not null;

    /// <summary>
    /// Run one tailscale CLI invocation. Returns (ok, stdout, message): stdout is the raw
    /// standard output (callers parse <c>serve status --json</c> from it); message is a
    /// human-readable summary for failure logging.
    /// </summary>
    public static (bool ok, string stdout, string message) Run(string arguments)
    {
        var exe = ResolveExePath();
        if (exe is null)
            return (false, "", "tailscale CLI not found");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {exe}");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, stdout, $"timed out after {CommandTimeout.TotalSeconds:F0}s");
        }

        var message = string.Join(" | ", new[] { stdout, stderr }
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return (proc.ExitCode == 0, stdout, message.Length > 0 ? message : $"exit {proc.ExitCode}");
    }

    /// <summary>
    /// Run a serve-MUTATING invocation (<c>serve --bg ...</c> / <c>serve ... off</c>),
    /// serialized across every cc-director process on this machine via the named mutex.
    /// Failing to acquire the mutex is reported as a failed call; the caller's
    /// reconcile/retry loop tries again later.
    /// </summary>
    public static (bool ok, string stdout, string message) RunServeMutating(string arguments)
        => RunSerialized(() => Run(arguments));

    /// <summary>
    /// Mutex wrapper with an injectable action so the serialization itself is unit-testable
    /// without shelling a real CLI. Production callers use <see cref="RunServeMutating"/>.
    /// </summary>
    internal static (bool ok, string stdout, string message) RunSerialized(
        Func<(bool ok, string stdout, string message)> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, ServeMutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(MutexAcquireTimeout); }
            catch (AbandonedMutexException)
            {
                // A previous holder died while holding the mutex. The serve config itself
                // lives in tailscaled and stays consistent; safe to proceed as the new holder.
                acquired = true;
            }
            if (!acquired)
                return (false, "", $"could not acquire {ServeMutexName} within {MutexAcquireTimeout.TotalSeconds:F0}s");
            return action();
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* not owned - nothing to release */ }
            }
        }
    }
}
