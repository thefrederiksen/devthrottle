using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Runs a short, throwaway agentic question over a REAL driver-backed session and reads
/// back a parseable answer, then tears the session down. This is the engine the wingman,
/// recap, and summary side-work run on instead of the metered one-shot
/// <c>claude --print</c> path: a real session bills against the user's subscription. See
/// issue #509 (the engine that #510 / #511 / #512 build on).
///
/// The flow is the existing driver protocol, nothing new:
///   1. <see cref="IAgentDriver.BuildLaunchSpec"/> - spawn arguments (NEVER --print / -p);
///   2. spawn an <see cref="ISessionBackend"/> and wait for the CLI to paint and settle;
///   3. <see cref="IAgentDriver.SubmitAsync"/> - submit the prompt, which asks the agent
///      to wrap its answer in the documented delimiter pair;
///   4. <see cref="IAgentDriver.ReadWidgets"/> - read the reply from the transcript (the
///      answer channel, never the terminal screen) once it stops growing;
///   5. extract exactly the delimited block;
///   6. tear the backend down (logged "session closed").
///
/// Only driver kinds that can do the whole flow are supported: the driver must declare
/// <see cref="DriverCapabilities.TranscriptRead"/> and produce a launch spec. ClaudeCode
/// qualifies today; Pi, the generic kinds (Codex / Gemini / OpenCode), and custom /
/// <see cref="AgentKind.RawCli"/> agents do not yet, so this helper fails loud with a
/// <see cref="NotSupportedException"/> naming the agent rather than guessing.
///
/// HOST PROCESS WARNING (nested ConPty): the caller must host this from a clean process
/// (service, Task Scheduler launch, normal desktop app), not from inside a Claude Code
/// pseudoconsole - the same constraint <see cref="HostedAgent"/>-style hosting has.
/// </summary>
public sealed class SessionAskRunner
{
    /// <summary>Opening marker the agent is told to emit immediately before its answer.</summary>
    public const string AnswerBeginMarker = "===DEVTHROTTLE-ANSWER-BEGIN===";

    /// <summary>Closing marker the agent is told to emit immediately after its answer.</summary>
    public const string AnswerEndMarker = "===DEVTHROTTLE-ANSWER-END===";

    /// <summary>Default timeout, matching today's wingman call budget (issue #509 assumption).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<AgentKind, IAgentDriver> _driverFactory;
    private readonly Func<ISessionBackend> _backendFactory;
    private readonly Action<string> _log;
    private readonly double _quietSeconds;
    private readonly double _startTimeoutSeconds;
    private readonly double _pollIntervalSeconds;
    private readonly double _replyStableSeconds;

    /// <summary>
    /// Create a runner. Every parameter is a seam with a production default: the driver
    /// is selected by <see cref="AgentDrivers.For"/>, the backend is a real ConPty, the
    /// log sink is the shared director log. Tests substitute fakes and tighten the gates.
    /// </summary>
    /// <param name="driverFactory">Resolves a driver for an agent kind. Defaults to <see cref="AgentDrivers.For"/>.</param>
    /// <param name="backendFactory">Creates a terminal backend. Defaults to a real ConPty.</param>
    /// <param name="log">Diagnostic log sink. Defaults to <see cref="FileLog.Write"/>.</param>
    /// <param name="quietSeconds">Seconds the terminal must be byte-silent before a send is allowed.</param>
    /// <param name="startTimeoutSeconds">Max seconds to wait for the freshly spawned agent to settle.</param>
    /// <param name="pollIntervalSeconds">Seconds between transcript polls.</param>
    /// <param name="replyStableSeconds">Seconds the transcript must be stable before the reply is accepted.</param>
    public SessionAskRunner(
        Func<AgentKind, IAgentDriver>? driverFactory = null,
        Func<ISessionBackend>? backendFactory = null,
        Action<string>? log = null,
        double quietSeconds = 2.0,
        double startTimeoutSeconds = 120.0,
        double pollIntervalSeconds = 0.25,
        double replyStableSeconds = 1.5)
    {
        _driverFactory = driverFactory ?? AgentDrivers.For;
        _backendFactory = backendFactory ?? (() => new ConPtyBackend());
        _log = log ?? FileLog.Write;
        _quietSeconds = quietSeconds;
        _startTimeoutSeconds = startTimeoutSeconds;
        _pollIntervalSeconds = pollIntervalSeconds;
        _replyStableSeconds = replyStableSeconds;
    }

    /// <summary>
    /// Open a real session for <paramref name="kind"/>, ask <paramref name="prompt"/>,
    /// read back the answer the agent emitted inside the documented delimiter pair, and
    /// tear the session down.
    /// </summary>
    /// <param name="kind">Which agent CLI to run.</param>
    /// <param name="executablePath">Explicit path to the agent executable, or null to resolve from PATH.</param>
    /// <param name="agentArgs">Base launch arguments, or null for the driver's default. NEVER --print / -p.</param>
    /// <param name="workingDirectory">Working directory for the session. Required; must exist.</param>
    /// <param name="prompt">The question to ask. Required.</param>
    /// <param name="timeout">Max time to wait for the reply. Null uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed answer.</returns>
    /// <exception cref="ArgumentException">The working directory or prompt is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">The working directory does not exist.</exception>
    /// <exception cref="NotSupportedException">The agent kind cannot run an ask (no transcript read / launch spec).</exception>
    /// <exception cref="SessionAskTimeoutException">No reply arrived within the timeout.</exception>
    /// <exception cref="InvalidOperationException">The reply did not contain the answer delimiter block.</exception>
    public async Task<SessionAskResult> AskAsync(
        AgentKind kind,
        string? executablePath,
        string? agentArgs,
        string workingDirectory,
        string prompt,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Working directory is required", nameof(workingDirectory));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"[SessionAskRunner] Working directory does not exist: {workingDirectory}");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required", nameof(prompt));

        var budget = timeout ?? DefaultTimeout;
        var driver = _driverFactory(kind);
        RequireSupported(kind, driver);

        _log($"[SessionAskRunner] AskAsync: kind={kind}, workdir={workingDirectory}, " +
             $"promptLen={prompt.Length}, timeout={budget.TotalSeconds:F0}s");

        var spec = driver.BuildLaunchSpec(agentArgs, resumeSessionId: null);
        if (string.IsNullOrEmpty(spec.PreassignedSessionId))
            throw new NotSupportedException(
                $"[SessionAskRunner] The {kind} driver did not preassign a session id; an ask needs " +
                "DriverCapabilities.PreassignedSessionId to locate the transcript the reply lands in.");
        var agentSessionId = spec.PreassignedSessionId;

        var exe = driver.ResolveExecutable(string.IsNullOrWhiteSpace(executablePath) ? null : executablePath);

        var backend = _backendFactory();
        try
        {
            backend.Start(exe, spec.Arguments, workingDirectory, cols: 120, rows: 40);
            _log($"[SessionAskRunner] AskAsync: launched pid={backend.ProcessId}, agentSessionId={agentSessionId}");

            await WaitForQuietAsync(backend, _startTimeoutSeconds, ct);

            var wrapped = WrapPrompt(prompt);
            var widgetsBefore = driver.ReadWidgets(agentSessionId, workingDirectory).Count;

            var startedAtUtc = DateTime.UtcNow;
            await driver.SubmitAsync(backend, wrapped);

            var (rawReply, replySeconds) = await WaitForReplyAsync(
                driver, backend, agentSessionId, workingDirectory, widgetsBefore, startedAtUtc, budget, ct);

            var answer = ExtractAnswer(rawReply);
            _log($"[SessionAskRunner] AskAsync OK: replySeconds={replySeconds:F1}, answerLen={answer.Length}");
            return new SessionAskResult
            {
                Answer = answer,
                RawReply = rawReply,
                ReplySeconds = replySeconds,
            };
        }
        finally
        {
            await CloseBackendAsync(backend, agentSessionId);
        }
    }

    /// <summary>
    /// Wrap the caller's prompt with the documented output-format contract: the agent is
    /// told to emit its answer between <see cref="AnswerBeginMarker"/> and
    /// <see cref="AnswerEndMarker"/> on their own lines, which <see cref="ExtractAnswer"/>
    /// pulls back out. Public so tests assert the exact contract text.
    /// </summary>
    public static string WrapPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required", nameof(prompt));

        var sb = new StringBuilder();
        sb.Append(prompt.TrimEnd());
        sb.Append("\n\n");
        sb.Append("When you have the answer, output it - and nothing else - between these two markers, ");
        sb.Append("each on its own line:\n");
        sb.Append(AnswerBeginMarker);
        sb.Append('\n');
        sb.Append("<your answer here>\n");
        sb.Append(AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>
    /// Extract exactly the text between the first <see cref="AnswerBeginMarker"/> and the
    /// next <see cref="AnswerEndMarker"/>, trimmed. Fails loud (no fallback to the whole
    /// reply) when the markers are absent - a missing block means the contract was not
    /// honored and the caller must know. Public so tests feed it a transcript reply.
    /// </summary>
    public static string ExtractAnswer(string reply)
    {
        if (reply is null)
            throw new ArgumentNullException(nameof(reply));

        var begin = reply.IndexOf(AnswerBeginMarker, StringComparison.Ordinal);
        if (begin < 0)
            throw new InvalidOperationException(
                $"[SessionAskRunner] The reply did not contain the opening answer marker '{AnswerBeginMarker}'.");

        var contentStart = begin + AnswerBeginMarker.Length;
        var end = reply.IndexOf(AnswerEndMarker, contentStart, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException(
                $"[SessionAskRunner] The reply contained the opening answer marker but not the closing " +
                $"marker '{AnswerEndMarker}'.");

        return reply[contentStart..end].Trim();
    }

    // --------------------------------------------------------------- internals

    private static void RequireSupported(AgentKind kind, IAgentDriver driver)
    {
        if (!driver.Capabilities.HasFlag(DriverCapabilities.TranscriptRead))
            throw new NotSupportedException(
                $"[SessionAskRunner] Agent {kind} cannot answer an ask: its driver does not declare " +
                "DriverCapabilities.TranscriptRead, so there is no machine-readable channel to read the " +
                "reply from. Use a kind with a transcript-reading driver (ClaudeCode today).");
    }

    /// <summary>
    /// Block until the CLI has painted at least once AND its terminal has been byte-silent
    /// for <see cref="_quietSeconds"/> - the same swallowed-Enter guard the hosted agent
    /// uses, measured on the backend's own buffer clock.
    /// </summary>
    private async Task WaitForQuietAsync(ISessionBackend backend, double timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (backend.HasExited)
                throw new InvalidOperationException(
                    $"[SessionAskRunner] The agent exited before it was ready (status={backend.Status}).");

            var buffer = backend.Buffer;
            if (buffer is not null && buffer.TotalBytesWritten > 0 && IdleSeconds(backend) >= _quietSeconds)
                return;

            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"[SessionAskRunner] Terminal not quiet after {timeoutSeconds:F0}s " +
                    $"(bytes={buffer?.TotalBytesWritten ?? 0}, idle={IdleSeconds(backend):F1}s).");
            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), ct);
        }
    }

    /// <summary>
    /// Poll the transcript until the last Text widget of the new turn stops growing, then
    /// return it. Raises <see cref="SessionAskTimeoutException"/> when the budget runs out
    /// with no reply, so the caller surfaces rather than hangs.
    /// </summary>
    private async Task<(string Reply, double ReplySeconds)> WaitForReplyAsync(
        IAgentDriver driver,
        ISessionBackend backend,
        string agentSessionId,
        string workingDirectory,
        int widgetsBefore,
        DateTime startedAtUtc,
        TimeSpan budget,
        CancellationToken ct)
    {
        var deadline = startedAtUtc + budget;
        string? reply = null;
        double replySeconds = 0;
        var stableCount = -1;
        var stableSince = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (backend.HasExited)
                throw new InvalidOperationException(
                    $"[SessionAskRunner] The agent exited mid-turn (status={backend.Status}, " +
                    $"agentSessionId={agentSessionId}).");

            var widgets = driver.ReadWidgets(agentSessionId, workingDirectory);
            var lastText = widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
            if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
            {
                if (reply is null)
                    replySeconds = (DateTime.UtcNow - startedAtUtc).TotalSeconds;
                reply = lastText.Content;

                // Accept only once the transcript stops growing, so multi-block replies
                // (thinking + text + more text) come back whole.
                if (widgets.Count != stableCount)
                {
                    stableCount = widgets.Count;
                    stableSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - stableSince).TotalSeconds >= _replyStableSeconds)
                {
                    return (reply, replySeconds);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), ct);
        }

        if (reply is not null)
            return (reply, replySeconds);

        throw new SessionAskTimeoutException(
            $"[SessionAskRunner] No reply landed in the transcript within {budget.TotalSeconds:F0}s " +
            $"(agentSessionId={agentSessionId}, backend={backend.Status}).");
    }

    /// <summary>Tear the session down and log "session closed" so the no-orphan teardown is provable.</summary>
    private async Task CloseBackendAsync(ISessionBackend backend, string agentSessionId)
    {
        await backend.GracefulShutdownAsync();
        backend.Dispose();
        _log($"[SessionAskRunner] session closed: agentSessionId={agentSessionId}");
    }

    private static double IdleSeconds(ISessionBackend backend)
    {
        var last = backend.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
        if (last == DateTime.MinValue) return 0;
        return Math.Max(0, (DateTime.UtcNow - last).TotalSeconds);
    }
}
