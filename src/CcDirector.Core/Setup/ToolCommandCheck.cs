using System.Text.RegularExpressions;
using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>
/// Does this machine's OWN <c>cc-devthrottle</c> have the commands a new session's instructions tell it to run (issue
/// #4744)? Asked by the Director before it starts a session whose create names them
/// (<see cref="Gateway.Contracts.NewSessionRequest.RequiredToolCommands"/>), so a seat that could not do its work is
/// refused with the reason instead of started.
///
/// The question goes to the tool itself, not to a version number: each command is run as
/// <c>cc-devthrottle &lt;words&gt; --help</c>, which the command line answers with exit 0 for a command it has and exit 2
/// "No such command" for one it lacks, at any depth. That is exactly what the session would meet, so the answer cannot
/// drift from the truth the way a version table would. The tool is the one in this machine's installed Python
/// environment (<see cref="CcDevthrottleLicenseCommandLine.ResolveOwnCopy()"/>) - never whatever PATH finds, because
/// that is not necessarily the copy the session's own shim runs.
///
/// Every outcome other than "it has every command" is a refusal in one sentence: the tool missing, the tool lacking a
/// command, the tool not answering in time. None of them is read as "probably fine".
/// </summary>
public sealed partial class ToolCommandCheck
{
    /// <summary>
    /// How long one <c>--help</c> may take. A cold Python start is a second or two. It must stay well under the Gateway's
    /// 30-second wait for a create's answer: past that, the Gateway reports an unknown outcome and the refusal sentence
    /// never reaches the factory's waiting reason.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How one run of the tool is made.</summary>
    public delegate Task<ProcessRunner.Result> ToolRun(string exe, IReadOnlyList<string> args, CancellationToken ct);

    private readonly Func<string?> _resolveTool;
    private readonly ToolRun _run;
    private readonly TimeSpan _timeout;

    /// <param name="resolveTool">Where the machine's own <c>cc-devthrottle</c> is, or null when it is not installed.</param>
    /// <param name="run">How the tool is run.</param>
    /// <param name="timeout">How long one run may take; <see cref="CallTimeout"/> when null.</param>
    public ToolCommandCheck(Func<string?> resolveTool, ToolRun run, TimeSpan? timeout = null)
    {
        _resolveTool = resolveTool ?? throw new ArgumentNullException(nameof(resolveTool));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _timeout = timeout ?? CallTimeout;
    }

    /// <summary>The production check: this machine's installed <c>cc-devthrottle</c>, run for real.</summary>
    public static ToolCommandCheck ForThisMachine() =>
        new(CcDevthrottleLicenseCommandLine.ResolveOwnCopy,
            (exe, args, ct) => ProcessRunner.RunAsync(exe, args, workingDirectory: null, ct));

    /// <summary>
    /// Null when the machine's tools have every command in <paramref name="commands"/>; otherwise the one sentence that
    /// says which command is missing (or why it could not be asked) and what brings the tools up. An empty list asks
    /// nothing and answers null.
    /// </summary>
    public async Task<string?> WhyTheToolsRefuseAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) return null;

        var parsed = new List<string[]>();
        foreach (var command in commands)
        {
            var words = (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0 || words.Any(w => !CommandWord().IsMatch(w)))
                return $"The start names '{command}' as a cc-devthrottle command it needs, and that is not a command name, " +
                       "so no session was started.";
            parsed.Add(words);
        }

        var exe = _resolveTool();
        if (exe is null)
        {
            FileLog.Write("[ToolCommandCheck] REFUSED: cc-devthrottle is not installed on this machine");
            return $"This session's instructions use cc-devthrottle ({Describe(commands)}), and cc-devthrottle is not " +
                   $"installed on this machine ({CcDevthrottleLicenseCommandLine.OwnToolDirectory()}), so no session was " +
                   "started. Install the cc-* tools: Fix it on the Director's tools, or update the Director.";
        }

        // Asked all at once: each answer is a cold Python start of about two seconds, and a seat's instructions name
        // several commands. The first refusal to arrive is the answer, and the questions still running are cancelled,
        // so one slow answer never delays a refusal already known.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = parsed.Select(words => AskAsync(exe, words, stop.Token)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (await done is { } refusal)
            {
                await stop.CancelAsync();
                await Task.WhenAll(pending.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default)));
                return refusal;
            }
        }

        return null;
    }

    /// <summary>Ask the tool for one command: null when it has it, else the refusal sentence.</summary>
    private async Task<string?> AskAsync(string exe, string[] words, CancellationToken ct)
    {
        var command = string.Join(' ', words);
        // Two separate reasons to stop: the time limit, and the caller withdrawing the question (another command was
        // already refused). Only the first is a refusal; a withdrawal propagates as the cancellation it is.
        using var timer = new CancellationTokenSource(_timeout);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        ProcessRunner.Result result;
        try
        {
            result = await _run(exe, [.. words, "--help"], either.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            FileLog.Write($"[ToolCommandCheck] AskAsync FAILED: '{exe} {command} --help' did not answer within {_timeout.TotalSeconds:0}s; the start is refused");
            return $"This machine's cc-devthrottle ({exe}) did not answer within {_timeout.TotalSeconds:0} seconds when " +
                   $"asked whether it has the command 'cc-devthrottle {command}', which this session's instructions use, " +
                   "so no session was started.";
        }

        if (!result.Started)
        {
            FileLog.Write($"[ToolCommandCheck] REFUSED: '{exe}' could not be started (error {result.StartErrorCode})");
            return $"This machine's cc-devthrottle ({exe}) could not be started (error {result.StartErrorCode}) to ask " +
                   $"whether it has 'cc-devthrottle {command}', which this session's instructions use, so no session " +
                   "was started.";
        }

        if (result.ExitCode != 0)
        {
            var said = FirstErrorLine(result);
            FileLog.Write($"[ToolCommandCheck] REFUSED: '{command}' exit={result.ExitCode}: {said}");
            return $"This session's instructions use 'cc-devthrottle {command}', and the cc-devthrottle on this machine " +
                   $"({exe}) refused it ({said}), so no session was started. When it says 'No such command', the cc-* " +
                   "tools here are older than the instructions; they are brought up with a Director update, or by Fix " +
                   "it on the Director's tools.";
        }

        FileLog.Write($"[ToolCommandCheck] '{command}' is present in {exe}");
        return null;
    }

    private static string Describe(IReadOnlyList<string> commands) =>
        string.Join(", ", commands.Select(c => $"'cc-devthrottle {c.Trim()}'"));

    /// <summary>The tool's own error line ("Error: No such command 'inbox'."), else the exit code.</summary>
    private static string FirstErrorLine(ProcessRunner.Result result)
    {
        var line = (result.StandardError + "\n" + result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal));
        return line ?? $"it answered with exit code {result.ExitCode}";
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex CommandWord();
}
