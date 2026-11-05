using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Result of a single <c>git</c> invocation: the exit code plus captured stdout/stderr.
/// The exit code is kept because some commands (e.g. <c>merge-base --is-ancestor</c>)
/// communicate their answer through the exit status rather than output.
/// </summary>
public sealed class GitCommandResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// Runs a single <c>git</c> command in a working directory and captures its result.
/// Shared by the worktree inventory and reaper services so every git call is logged
/// and its exit code is available. Arguments are passed as a list, never as one quoted
/// string, so there is no shell quoting hazard.
///
/// The LAUNCH itself is <see cref="ProcessRunner"/>'s, which on macOS and Linux spawns through
/// posix_spawn rather than fork() - see devthrottle#4577 and the note in <see cref="RunAsync"/>.
///
/// <see cref="RunAsync"/> is virtual so tests can interleave a concurrent process action at an
/// exact point between two git commands (the compensation races in <see cref="GitBranchService"/>
/// are only reachable that way) - production code always uses this class directly.
/// </summary>
public class GitCommandRunner
{
    private readonly string _executable;

    /// <summary>Runs the <c>git</c> on this machine's PATH. This is the only production form.</summary>
    public GitCommandRunner() : this("git") { }

    /// <summary>
    /// Runs <paramref name="executable"/> instead of <c>git</c>. A TEST SEAM, and the only way to
    /// reach the missing-git branch on a machine that has git: pass a name that resolves nowhere and
    /// the launch fails for precisely the reason it fails on a clean Windows install. Production
    /// never calls this - every call site uses the parameterless constructor above.
    /// </summary>
    public GitCommandRunner(string executable)
    {
        _executable = executable;
    }

    /// <summary>
    /// Runs <c>git &lt;args&gt;</c> in <paramref name="workingDirectory"/> and returns its result.
    /// </summary>
    public virtual async Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new GitCommandResult { Success = false, ExitCode = -1, Error = $"working directory not found: {workingDirectory}" };

        FileLog.Write($"[GitCommandRunner] git {string.Join(' ', args)} (cwd={workingDirectory})");

        // Every git command goes through ProcessRunner. It used to drive System.Diagnostics.Process
        // here itself, which on macOS and Linux means fork() - and this class is by a wide margin
        // the Director's busiest launcher of child processes: 48,653 git commands between 01:14 and
        // 18:06 on 2026-11-04, about 48 a minute, against 2,401 from every other caller of
        // ProcessRunner put together. ProcessRunner spawns through posix_spawn on those platforms,
        // so routing this class through it is what takes the fork hazard of devthrottle#4577 off the
        // hot path rather than merely off one caller (#4577).
        //
        // It also means this class no longer keeps its own copies of three things ProcessRunner
        // already does, and had to be kept in step with: starting on the thread pool rather than
        // the caller's thread (#4556), draining both pipes concurrently so neither can stall the
        // other at 64 KB (issue 516), and killing what a cancelled command started.
        //
        // GIT_OPTIONAL_LOCKS=0 travels as the child's environment. It is NOT optional: a cancelled
        // read is killed below, and a killed plain git status cannot remove the index.lock it was
        // holding, which stranded a 0-byte lock file and broke every later commit, pull, checkout
        // and rebase in that checkout (issue #2872).
        var r = await ProcessRunner.RunAsync(
            _executable, args, workingDirectory, standardInput: null,
            environment: GitOptionalLocks.Off, ct).ConfigureAwait(false);

        // A machine with no git is a supported machine (devthrottle_internal issue #1048). The
        // launch failure arrives as Started=false carrying the operating system's own error code
        // rather than as an exception, and is reported the same way as the other reason a command
        // cannot run - the working directory being absent, above - so this class keeps ONE contract
        // instead of two, and puts a sentence a person can act on where the surfaces that render it
        // (the Worktrees page, the branch services) already display the Error.
        if (!r.Started)
        {
            var why = GitLaunchFailure.Describe(r.StartErrorCode, r.StandardError);
            FileLog.Write($"[GitCommandRunner] git could not be started FAILED: {why}");
            return new GitCommandResult { Success = false, ExitCode = -1, Error = why };
        }

        var result = new GitCommandResult
        {
            Success = r.ExitCode == 0,
            ExitCode = r.ExitCode,
            Output = AsLines(r.StandardOutput),
            Error = AsLines(r.StandardError),
        };
        if (!result.Success)
            FileLog.Write($"[GitCommandRunner] git exit={r.ExitCode}: {result.Error}");
        return result;
    }

    /// <summary>
    /// Re-joins captured output with THIS platform's line terminator, then trims the end.
    ///
    /// This class used to capture through OutputDataReceived, which hands over one line at a time
    /// with its terminator stripped; appending each with AppendLine rewrote every line ending as the
    /// platform's own. Reading the stream whole instead returns git's bytes as git wrote them -
    /// which on Windows is "\n" where every caller had always been given "\r\n". Normalising here
    /// keeps the output of this class byte-for-byte what it was, so routing it through ProcessRunner
    /// changes how git is STARTED and nothing at all about what its callers read.
    /// </summary>
    private static string AsLines(string captured)
    {
        if (captured.Length == 0) return "";
        var lines = captured.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }
}
