using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>What the reachability run concluded about the cc-devthrottle on this machine's PATH.</summary>
public enum FleetToolVerdict
{
    /// <summary>Not judged yet. Must be rendered as "checking", never as a pass.</summary>
    Unchecked,

    /// <summary>PATH resolves cc-devthrottle and it reached the fleet through the Gateway.</summary>
    Working,

    /// <summary>Nothing named cc-devthrottle is on PATH at all.</summary>
    NotFound,

    /// <summary>
    /// PATH resolves a cc-devthrottle, and it CANNOT reach the fleet even when handed a working
    /// Gateway address and a freshly registered session key. This is the fault: agents in sessions
    /// report "cannot connect to DevThrottle" while this Director's own connection is healthy -
    /// usually because PATH resolves a stale install's copy.
    /// </summary>
    CannotReachGateway,

    /// <summary>
    /// There is no Gateway for the tools to reach right now - none configured, or the tunnel is
    /// down - so the probe was not run at all.
    ///
    /// NOT A FAULT of the toolbelt, and separating it from <see cref="CannotReachGateway"/> is the
    /// whole point: "no Gateway means no agent tooling" is the mission's accepted trade, and
    /// painting the Tools fault banner for it would offer install and PATH repairs on a machine
    /// whose install has nothing wrong with it.
    /// </summary>
    NoGateway,

    /// <summary>
    /// The Gateway is connected and REFUSED the session key, so the probe never got as far as
    /// running a tool. Every session on this machine is refused the same way, and so is every
    /// session on every other machine pointed at that Gateway.
    ///
    /// This is a THIRD state, and it had to be, because it belongs to neither of its neighbours.
    /// It is not <see cref="NoGateway"/> - the Gateway is right there, answering. It is not
    /// <see cref="CannotReachGateway"/> either, and that distinction is the one that decides
    /// whether the user is sent somewhere useful: CannotReachGateway means the toolbelt is at
    /// fault, usually a stale install on PATH, and it offers install and PATH repairs. Those
    /// repairs cannot fix this. Nothing on this machine can. The Gateway is out of date and the
    /// remedy is to deploy it.
    ///
    /// Before this verdict existed the refusal produced no verdict at all, and no verdict renders
    /// as no row - which is how a fleet-wide outage sat on the Home page as blank space for five
    /// hours while the Director's own log named the cause every ten seconds (#2457, #2459).
    /// </summary>
    GatewayRefusedKey,
}

/// <summary>
/// One reachability run. <see cref="ResolvedPath"/> and <see cref="ExpectedBinDir"/> are what the
/// explanation panel shows; they never decide the verdict.
///
/// <see cref="OwnVerdict"/> is the SECOND question, and it is the one whose absence cost a morning:
/// "never mind what PATH gives me - does MY OWN copy work?" Without it the panel could only see that
/// PATH pointed somewhere else, so it offered to repoint PATH at a directory that was empty. The
/// repair reordered PATH exactly as asked, resolution fell straight through the empty directory to
/// the same stale install, and the button reported the same failure it started with.
/// </summary>
public sealed record FleetToolCheck(
    FleetToolVerdict Verdict,
    string? ResolvedPath,
    string? ExpectedBinDir,
    string Detail,
    FleetToolVerdict OwnVerdict = FleetToolVerdict.Unchecked,
    string OwnDetail = "")
{
    /// <summary>
    /// True only when repointing PATH is a repair that can actually work: PATH resolves someone
    /// else's copy, and OURS is present and proven to reach the fleet.
    ///
    /// This is the precondition the button was missing. "PATH points at another install" was treated
    /// as sufficient, which it is not - it is only half of it. The other half is that we have
    /// something worth pointing at.
    /// </summary>
    public bool CanRepairByRepointingPath
        => Verdict == FleetToolVerdict.CannotReachGateway
           && OwnVerdict == FleetToolVerdict.Working
           && IsDifferentInstall;

    /// <summary>
    /// True when this Director has no working cc-devthrottle of its own, so PATH order is not the
    /// fault and reordering it would repair nothing. The remedy is to install our tools first.
    /// </summary>
    public bool OwnToolsAreMissingOrBroken
        => OwnVerdict is FleetToolVerdict.NotFound or FleetToolVerdict.CannotReachGateway;

    /// <summary>
    /// True when the resolved tool belongs to a different install than this Director's. Explanation
    /// only - a development build legitimately runs from outside any install directory, so this must
    /// never be allowed to decide a verdict.
    /// </summary>
    public bool IsDifferentInstall
    {
        get
        {
            if (ResolvedPath is not { Length: > 0 } resolved) return false;
            if (ExpectedBinDir is not { Length: > 0 } expected) return false;
            try
            {
                var resolvedDir = Path.GetFullPath(Path.GetDirectoryName(resolved) ?? resolved)
                    .TrimEnd(Path.DirectorySeparatorChar);
                var expectedDir = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar);
                return !string.Equals(resolvedDir, expectedDir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unreadable path cannot support a claim either way, and this only ever decorates a
                // verdict that has already been reached.
                return false;
            }
        }
    }
}

/// <summary>
/// Answers ONE question, from inside the Director, about the machine the Director is running on:
///
///     Can a session I spawn actually reach the fleet?
///
/// It exists because the two halves of the product can disagree without either being wrong. The
/// Director is a C# application; cc-devthrottle is a separate Python command line that reaches the
/// fleet through the Gateway, presenting the session key stamped into its environment. When PATH
/// resolves a cc-devthrottle belonging to an older install, every agent in every session reports
/// "cannot connect to DevThrottle" while the Director's own connection light correctly says
/// Connected. The outage is imaginary; the cost is that the agent blames the network and the owner
/// spends the morning there.
///
/// Since the Remove-the-network-port mission the Director has no listener, so the probe aims the
/// tool at exactly what a real session gets: the Gateway's address and a freshly minted, freshly
/// registered session key (see ControlApiHost.MintFleetToolProbeCredentialAsync). What passes here
/// is what an agent's command line actually does.
///
/// TWO DESIGN RULES, both learned the expensive way:
///
/// 1. The verdict is FUNCTIONAL, never structural. "Is the resolved path under my install directory"
///    looks like the same question and is not: a development build running from a local build
///    directory matches no install root and would be reported broken on a machine with no fault. So
///    the Director runs the tool PATH actually gives and sees whether it comes back. The path
///    comparison supplies the explanation and nothing else.
///
/// 2. The Director judges; the tool is never asked to judge itself. The case this check exists to
///    catch is a tool too OLD to be correct - and a tool too old to be correct is also too old to know
///    about any self-report we add today. Detection therefore lives entirely here, in the component
///    that is known-good, and reads only the exit code.
/// </summary>
public sealed class FleetToolReachability
{
    /// <summary>The command agents are told is on their PATH.</summary>
    public const string ToolName = "cc-devthrottle";

    // Any verb that requires the Gateway and the session key. `session list` is the cheapest one
    // every shipped build has had; its OUTPUT is irrelevant here, only whether it succeeded.
    private static readonly string[] ProbeArgs = ["session", "list"];

    private readonly TimeSpan _timeout;
    private readonly Func<string, string?> _resolve;

    public FleetToolReachability() : this(TimeSpan.FromSeconds(30)) { }

    public FleetToolReachability(TimeSpan timeout) : this(timeout, ExecutableResolver.Resolve) { }

    /// <param name="resolve">How a command name becomes a concrete executable. Defaults to the same
    /// rules CreateProcess uses, so the check finds the binary a spawned session would actually run;
    /// injectable so tests need not mutate the process-global PATH.</param>
    public FleetToolReachability(TimeSpan timeout, Func<string, string?> resolve)
    {
        _timeout = timeout;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>
    /// Resolve cc-devthrottle the way a spawned session would, then make it prove it can reach the
    /// fleet with the same environment a session gets.
    /// </summary>
    /// <param name="gatewayUrl">The Gateway base URL - the literal value the Director stamps into
    /// every session as CC_GATEWAY_URL, so the check and a real session ask the same question of
    /// the same door.</param>
    /// <param name="sessionKey">A minted, REGISTERED session key (CC_GATEWAY_SESSION_KEY). The
    /// caller owns its lifetime and revokes it after this run.</param>
    /// <param name="expectedBinDir">This Director's own tool bin directory. Explanation only.</param>
    public async Task<FleetToolCheck> RunAsync(
        string gatewayUrl, string sessionKey, string? expectedBinDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("A Gateway base address is required.", nameof(gatewayUrl));
        if (string.IsNullOrWhiteSpace(sessionKey))
            throw new ArgumentException("A registered session key is required.", nameof(sessionKey));

        FileLog.Write($"[FleetToolReachability] RunAsync: probing {ToolName} against the Gateway at {gatewayUrl}");

        var resolved = _resolve(ToolName);
        if (resolved is null)
        {
            FileLog.Write($"[FleetToolReachability] {ToolName} is not on PATH");
            var (missingOwnVerdict, missingOwnDetail) =
                await ProbeOwnCopyAsync(expectedBinDir, gatewayUrl, sessionKey, ct);
            return new FleetToolCheck(
                FleetToolVerdict.NotFound, null, expectedBinDir,
                $"Nothing named {ToolName} is on this machine's PATH.",
                missingOwnVerdict, missingOwnDetail);
        }

        var (exitCode, output) = await RunProbeAsync(resolved, gatewayUrl, sessionKey, ct);

        if (exitCode == 0)
        {
            FileLog.Write($"[FleetToolReachability] {ToolName} at {resolved} reached the fleet");
            return new FleetToolCheck(
                FleetToolVerdict.Working, resolved, expectedBinDir, "reached the fleet through the Gateway");
        }

        // Log the reason HERE, where it is known. A red badge whose cause is not in the log is a
        // second investigation for whoever finds it.
        var detail = FirstMeaningfulLine(output) ?? $"exit {exitCode}";
        FileLog.Write(
            $"[FleetToolReachability] {ToolName} at {resolved} FAILED to reach the Gateway at {gatewayUrl}: {detail}");

        // PATH gave us something that does not work. That alone does not say whether OUR copy would,
        // and the two faults have different repairs - so ask the second question before reporting.
        var (ownVerdict, ownDetail) = await ProbeOwnCopyAsync(expectedBinDir, gatewayUrl, sessionKey, ct);
        return new FleetToolCheck(
            FleetToolVerdict.CannotReachGateway, resolved, expectedBinDir, detail, ownVerdict, ownDetail);
    }

    /// <summary>
    /// Run the same functional probe against THIS Director's own copy, addressed by its full path so
    /// PATH cannot answer for it.
    ///
    /// It runs only when PATH has already failed, because it exists to tell two faults apart: PATH
    /// resolves someone else's working copy (repoint), or we have no working copy to point at
    /// (install first, then repoint). Nothing structural is consulted - a directory that exists and a
    /// tool that runs are different claims, and it was the first standing in for the second that made
    /// the repair impossible.
    /// </summary>
    private async Task<(FleetToolVerdict Verdict, string Detail)> ProbeOwnCopyAsync(
        string? expectedBinDir, string gatewayUrl, string sessionKey, CancellationToken ct)
    {
        // A development build has no install directory of its own. No directory is not a fault, and it
        // is not a pass either - it is a question that cannot be asked here.
        if (string.IsNullOrWhiteSpace(expectedBinDir))
            return (FleetToolVerdict.Unchecked, "");

        var own = _resolve(Path.Combine(expectedBinDir, ToolName));
        if (own is null)
        {
            FileLog.Write(
                $"[FleetToolReachability] this Director has no {ToolName} of its own in {expectedBinDir}");
            return (FleetToolVerdict.NotFound, $"There is no {ToolName} in {expectedBinDir}.");
        }

        var (exitCode, output) = await RunProbeAsync(own, gatewayUrl, sessionKey, ct);
        if (exitCode == 0)
        {
            FileLog.Write($"[FleetToolReachability] this Director's own {ToolName} at {own} reached the fleet");
            return (FleetToolVerdict.Working, "reached the fleet through the Gateway");
        }

        var detail = FirstMeaningfulLine(output) ?? $"exit {exitCode}";
        FileLog.Write(
            $"[FleetToolReachability] this Director's own {ToolName} at {own} FAILED to reach the Gateway: {detail}");
        return (FleetToolVerdict.CannotReachGateway, detail);
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(
        string toolPath, string gatewayUrl, string sessionKey, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in ProbeArgs) psi.ArgumentList.Add(arg);

        // Exactly the pair a spawned session receives. Without them the tool has no door to aim at
        // and would fail for a reason that says nothing about the fault we are looking for.
        psi.Environment["CC_GATEWAY_URL"] = gatewayUrl;
        psi.Environment["CC_GATEWAY_SESSION_KEY"] = sessionKey;

        var captured = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) captured.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) captured.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return (-1, $"timed out after {_timeout.TotalSeconds:0}s");
            }

            return (process.ExitCode, captured.ToString());
        }
        catch (Exception ex)
        {
            // A tool old enough to reject the arguments, or broken enough not to launch, is exactly the
            // case this check exists for. It is a failure, reported as one, with its real reason.
            FileLog.Write($"[FleetToolReachability] launch error for {toolPath} FAILED: {ex.Message}");
            return (-1, $"could not run it: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
        }
        return null;
    }
}
