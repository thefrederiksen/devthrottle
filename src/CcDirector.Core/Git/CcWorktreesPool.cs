using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// One pooled worktree a session holds: which repository's pool it came from, which slot it is,
/// where it is on disk, and the lease that is the only evidence the holder may give it back.
///
/// The lease is not a secret and it is not authentication - it is the coordination token the tool
/// requires so one session cannot return a slot another session is working in. It is kept with the
/// session for exactly as long as the session lives, because close is the moment it is needed.
/// </summary>
public sealed record PooledWorktree(string Repo, string Slot, string Path, string Lease);

/// <summary>What <c>cc-worktrees return</c> answered: the slot came back, or it is held and why.</summary>
public sealed record PooledWorktreeReturn(string Slot, string Path, bool Freed, string? HeldReason);

/// <summary>
/// The tool refused, in its own words. The message is the tool's own <c>error</c> line, unchanged -
/// it is what the user is shown, so nothing here paraphrases it.
///
/// <see cref="Code"/> is the tool's machine-readable code (<c>pool-full</c>, <c>held</c>,
/// <c>cannot-fetch</c>, ...) and <see cref="ExitCode"/> its exit code. A refusal is never turned into
/// a fallback: no free slot means no session, not a session in the shared checkout.
/// </summary>
public sealed class CcWorktreesRefusedException : Exception
{
    public CcWorktreesRefusedException(string code, string message, int exitCode, IReadOnlyList<string> help)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
        Help = help;
    }

    public string Code { get; }
    public int ExitCode { get; }

    /// <summary>The tool's own "what to run next" lines, in its own order.</summary>
    public IReadOnlyList<string> Help { get; }
}

/// <summary>
/// The pool of worktrees a session can be handed, as the Director sees it. One implementation
/// (<see cref="CcWorktreesPool"/>) runs the cc-worktrees command line; tests substitute their own.
/// </summary>
public interface IWorktreePool
{
    /// <summary>
    /// Take a worktree for <paramref name="holder"/> out of <paramref name="repoPath"/>'s pool.
    /// Throws <see cref="CcWorktreesRefusedException"/> when the tool refuses (pool full, a remote it
    /// cannot reach, anything else non-zero). There is no fallback - a refusal means no worktree.
    /// </summary>
    PooledWorktree Get(string repoPath, string holder, int poolSize);

    /// <summary>
    /// Give <paramref name="worktree"/> back. A slot the tool freed comes back with
    /// <see cref="PooledWorktreeReturn.Freed"/> true; a slot it held comes back with the tool's own
    /// reason and nothing is forced, retried with a stronger flag, or destroyed.
    /// </summary>
    PooledWorktreeReturn Return(PooledWorktree worktree);
}

/// <summary>
/// Runs the cc-worktrees command line and reads its AXI JSON. This is the ONLY place in the Director
/// that knows how that tool is invoked.
///
/// WHY A SUBPROCESS AND NOT A REIMPLEMENTATION. The landed-work rule - the proof that a worktree's
/// commits reached the remote before it is reset - lives in the tool and was built and inspected
/// there. A second copy of it in C# would be a second thing to keep true, and the failure mode of the
/// copy that drifts is the one this whole mission exists to prevent: work that is thrown away because
/// something decided it had landed.
///
/// NO FALLBACK. If the tool cannot be found, cannot run, or refuses, the caller is told in the tool's
/// own words and the session does not open. A session quietly started in the shared checkout instead
/// would be the exact behaviour the setting was turned on to stop.
/// </summary>
public sealed class CcWorktreesPool : IWorktreePool
{
    /// <summary>The command, as it is installed on PATH and in this Director's own tool directory.</summary>
    public const string ToolName = "cc-worktrees";

    /// <summary>
    /// An explicit path to the tool, for a machine where it is not installed yet (the manual proof,
    /// and a developer running from a checkout). It is read at every call rather than cached, so it
    /// can be set without restarting the Director.
    /// </summary>
    public const string ExecutableEnvVar = "CC_WORKTREES_EXE";

    /// <summary>
    /// How long the Director waits for one cc-worktrees call. The tool's own network calls are bounded
    /// at 120 seconds each and it can wait up to 300 seconds for the machine-wide lock, so this is
    /// deliberately longer than both: a timeout here would leave the Director not knowing whether the
    /// slot was taken or given back, which is worse than waiting.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<IReadOnlyList<string>, (int ExitCode, string StandardOutput, string StandardError, bool Started)> _run;
    private readonly Func<string?> _resolveExecutable;

    public CcWorktreesPool() : this(null, null)
    {
    }

    /// <param name="run">Test seam: runs the tool with the given arguments and returns its result.</param>
    /// <param name="resolveExecutable">Test seam: finds the tool, or null when it is not installed.</param>
    internal CcWorktreesPool(
        Func<IReadOnlyList<string>, (int, string, string, bool)>? run,
        Func<string?>? resolveExecutable)
    {
        _run = run ?? RunTool;
        _resolveExecutable = resolveExecutable ?? ResolveExecutable;
    }

    /// <summary>
    /// Where the tool is on this machine, or null when it is not installed. The order is: the explicit
    /// <see cref="ExecutableEnvVar"/>, then the machine's installed tool directory, then PATH. Our own
    /// copy comes before PATH for the same reason the session PATH is rewritten at launch: another
    /// install's copy in front of ours is shared state we do not control.
    /// </summary>
    public static string? ResolveExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable(ExecutableEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = ExecutableResolver.Resolve(explicitPath);
            if (resolved is not null)
                return resolved;
            FileLog.Write($"[CcWorktreesPool] {ExecutableEnvVar}={explicitPath} does not resolve to a file; falling through to the normal search");
        }

        try
        {
            var ownBin = Path.Combine(Storage.CcStorage.Bin(), ToolName);
            var own = ExecutableResolver.Resolve(ownBin);
            if (own is not null)
                return own;
        }
        catch (Exception ex)
        {
            // The tool directory is not readable on this machine. That is not a reason to stop looking.
            FileLog.Write($"[CcWorktreesPool] could not look in the machine's tool directory: {ex.Message}");
        }

        return ExecutableResolver.Resolve(ToolName);
    }

    /// <inheritdoc />
    public PooledWorktree Get(string repoPath, string holder, int poolSize)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("Repository path is required", nameof(repoPath));
        if (string.IsNullOrWhiteSpace(holder)) throw new ArgumentException("Holder is required", nameof(holder));
        if (poolSize < 1) throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize, "The pool size must be at least 1");

        FileLog.Write($"[CcWorktreesPool] Get: repo={repoPath}, holder={holder}, poolSize={poolSize}");
        var json = Invoke(new[] { "get", "--repo", repoPath, "--holder", holder, "--pool-size", poolSize.ToString(), "--json" });

        var lease = new PooledWorktree(
            Text(json, "repo") ?? repoPath,
            Text(json, "slot") ?? throw Malformed("get", "slot"),
            Text(json, "path") ?? throw Malformed("get", "path"),
            Text(json, "lease") ?? throw Malformed("get", "lease"));

        FileLog.Write($"[CcWorktreesPool] Get: slot={lease.Slot}, path={lease.Path}");
        return lease;
    }

    /// <inheritdoc />
    public PooledWorktreeReturn Return(PooledWorktree worktree)
    {
        if (worktree is null) throw new ArgumentNullException(nameof(worktree));

        FileLog.Write($"[CcWorktreesPool] Return: slot={worktree.Slot}, path={worktree.Path}");
        try
        {
            var json = Invoke(new[] { "return", worktree.Path, "--lease", worktree.Lease, "--json" });
            var state = Text(json, "state");
            if (!string.Equals(state, "free", StringComparison.Ordinal))
            {
                // Exit 0 says the return succeeded; a state that is not "free" contradicts it. Nothing
                // here decides which one to believe - the slot is treated as held, which is the answer
                // that loses nothing.
                var reason = $"cc-worktrees returned successfully but reported the slot as \"{state}\" rather than free";
                FileLog.Write($"[CcWorktreesPool] Return: slot={worktree.Slot} {reason}");
                return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: false, HeldReason: reason);
            }

            FileLog.Write($"[CcWorktreesPool] Return: slot={worktree.Slot} is free");
            return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
        catch (CcWorktreesRefusedException ex)
        {
            // HELD IS NOT A FAILURE OF THE RETURN - it is the return working. The slot keeps whatever is
            // in it and the reason travels back for the session's row. Every other refusal (a lease that
            // no longer matches, a remote that cannot be reached, the tool missing) is also reported as
            // held: in every one of them the Director does not know that the slot came back, and
            // recording it as free would hand the next session a worktree somebody else's work is in.
            FileLog.Write($"[CcWorktreesPool] Return: slot={worktree.Slot} held: {ex.Message}");
            return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: false, HeldReason: ex.Message);
        }
    }

    /// <summary>
    /// Runs one command and returns its parsed JSON object, or throws
    /// <see cref="CcWorktreesRefusedException"/> carrying the tool's own words.
    /// </summary>
    private JsonElement Invoke(IReadOnlyList<string> args)
    {
        var (exitCode, stdout, stderr, started) = _run(args);

        if (!started)
        {
            var exe = _resolveExecutable();
            var what = exe is null
                ? $"{ToolName} is not installed on this machine and is not on this Director's PATH"
                : $"{ToolName} at {exe} could not be started: {stderr.Trim()}";
            throw new CcWorktreesRefusedException("tool-not-runnable", what, exitCode,
                new[] { $"Install {ToolName}, or set {ExecutableEnvVar} to its path" });
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "null" : stdout);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new CcWorktreesRefusedException("unreadable-answer",
                $"{ToolName} answered with something that is not JSON (exit {exitCode}): {Trim(stdout)}{Trim(stderr)}",
                exitCode, new[] { $"{ToolName} {string.Join(' ', args)}" });
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new CcWorktreesRefusedException("unreadable-answer",
                $"{ToolName} answered with no result object (exit {exitCode}): {Trim(stdout)}{Trim(stderr)}",
                exitCode, new[] { $"{ToolName} {string.Join(' ', args)}" });

        if (exitCode != 0 || root.TryGetProperty("error", out _))
        {
            var message = Text(root, "error")
                ?? $"{ToolName} failed with exit code {exitCode}{Trim(stderr)}";
            throw new CcWorktreesRefusedException(Text(root, "code") ?? "error", message, exitCode, HelpLines(root));
        }

        return root;
    }

    private static IReadOnlyList<string> HelpLines(JsonElement root)
    {
        if (!root.TryGetProperty("help", out var help) || help.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return help.EnumerateArray()
            .Where(h => h.ValueKind == JsonValueKind.String)
            .Select(h => h.GetString()!)
            .ToList();
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Trim(string text) =>
        string.IsNullOrWhiteSpace(text) ? "" : " " + text.Trim();

    private static InvalidOperationException Malformed(string command, string field) =>
        new($"{ToolName} {command} answered without a \"{field}\", so the Director cannot say which worktree it was given.");

    /// <summary>
    /// Starts the tool and waits for it. Run on the thread pool deliberately: this is called from
    /// session close, which reaches here from the desktop's own thread, and a synchronous wait on a
    /// task that resumes on that thread would deadlock.
    /// </summary>
    private (int, string, string, bool) RunTool(IReadOnlyList<string> args)
    {
        var exe = _resolveExecutable();
        if (exe is null)
            return (-1, "", $"{ToolName} was not found", false);

        using var cts = new CancellationTokenSource(CallTimeout);
        var result = Task.Run(() => ProcessRunner.RunAsync(exe, args, workingDirectory: null, cts.Token)).GetAwaiter().GetResult();
        return (result.ExitCode, result.StandardOutput, result.StandardError, result.Started);
    }
}
