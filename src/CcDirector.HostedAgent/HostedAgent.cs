using CcDirector.AgentBrain;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;

namespace CcDirector.HostedAgent;

/// <summary>
/// A brain that HOSTS its own agent CLI: this class spawns the process attached to an
/// embedded pseudoconsole (ConPty) and drives it through an <see cref="IAgentDriver"/>
/// - the per-CLI protocol for submit / cancel / clear / transcript reads. No Director,
/// no HTTP - zero external process dependency. Create as many instances as you like in
/// one host process; each owns exactly one CLI child.
///
/// Layering (docs/plans/agent-driver.md): the backend is the terminal (transport), the
/// driver is the tool's protocol, this class is the host - lifecycle (spawn,
/// readiness, quiet clock, restart-on-crash, health) plus the IAgentBrain verbs.
///
/// Determinism rules (proven by the issue #172 spike), measured at the source:
///   1. Quiet gate - sends wait for byte-silence on the backend's own buffer clock.
///   2. The transcript is the answer channel - never the terminal screen.
///   3. A context clear starts a new agent-internal session id - tracked by listing
///      the transcript files directly (we own the working directory, no relink).
///
/// HOST PROCESS WARNING (nested ConPty): if the process hosting this class was itself
/// spawned inside a Claude Code pseudoconsole, the grandchild claude.exe exits within
/// seconds with the "--print stdin" error. Host from a clean process (service, Task
/// Scheduler launch, normal desktop app) - see CLAUDE.md "cc-director-launch".
/// </summary>
public sealed class HostedAgent : IAgentBrain
{
    private readonly HostedAgentOptions _options;
    private readonly IAgentDriver _driver;
    private readonly Func<ISessionBackend> _backendFactory;
    private readonly Action<string> _log;

    private ISessionBackend? _backend;
    private string? _agentSessionId;
    private bool _disposed;

    /// <summary>The agent-internal session id of the CURRENT conversation (changes on
    /// every context clear). Null before StartAsync.</summary>
    public string? SessionId => _agentSessionId;

    /// <summary>PID of the hosted CLI process, 0 when not running.</summary>
    public int ProcessId => _backend?.ProcessId ?? 0;

    /// <summary>The driver this host runs (which CLI, and what it can do).</summary>
    public IAgentDriver Driver => _driver;

    public HostedAgentOptions Options => _options;

    /// <summary>
    /// Create a hosted agent. The optional parameters are seams: driver defaults to
    /// <see cref="ClaudeDriver"/>, backend to a real ConPty; tests substitute fakes.
    /// </summary>
    public HostedAgent(
        HostedAgentOptions options,
        IAgentDriver? driver = null,
        Func<ISessionBackend>? backendFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driver = driver ?? new ClaudeDriver();
        _backendFactory = backendFactory ?? (() => new ConPtyBackend());
        _log = options.Log ?? BrainLog.Write;
    }

    /// <summary>
    /// Create a hosted agent for a CLI kind. Only kinds with a written-and-verified
    /// driver are supported; everything else fails loud - write the driver first.
    /// </summary>
    public static HostedAgent For(AgentKind kind, HostedAgentOptions options) => kind switch
    {
        AgentKind.ClaudeCode => new HostedAgent(options),
        _ => throw new NotSupportedException(
            $"No agent driver exists for {kind} yet. Drivers encode per-CLI keystrokes " +
            "(cancel, clear, submit) and must be live-verified per tool - see docs/plans/agent-driver.md."),
    };

    /// <summary>
    /// Spawn the agent CLI and wait until it has painted and settled (prompt-ready).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_backend is not null)
            throw new AgentBrainException("StartAsync: already started. Use RestartAsync for a fresh session.");
        if (string.IsNullOrWhiteSpace(_options.WorkingDirectory))
            throw new AgentBrainException("StartAsync: WorkingDirectory is required");
        if (!Directory.Exists(_options.WorkingDirectory))
            throw new AgentBrainException($"StartAsync: WorkingDirectory does not exist: {_options.WorkingDirectory}");

        string exe;
        try
        {
            exe = _driver.ResolveExecutable(
                string.IsNullOrWhiteSpace(_options.ExecutablePath) ? null : _options.ExecutablePath);
        }
        catch (FileNotFoundException ex)
        {
            throw new AgentBrainException($"StartAsync: {ex.Message}", ex);
        }

        var spec = _driver.BuildLaunchSpec(_options.AgentArgs, resumeSessionId: null);
        if (spec.PreassignedSessionId is null)
            throw new AgentBrainException(
                $"StartAsync: the {_driver.Kind} driver did not preassign a session id; " +
                "the hosted agent requires DriverCapabilities.PreassignedSessionId to locate transcripts.");
        _agentSessionId = spec.PreassignedSessionId;

        _log($"[HostedAgent] StartAsync: driver={_driver.Kind}, exe={exe}, " +
             $"workdir={_options.WorkingDirectory}, agentSessionId={_agentSessionId}");
        _backend = _backendFactory();
        _backend.Start(exe, spec.Arguments, _options.WorkingDirectory, _options.Cols, _options.Rows);

        await WaitForQuietAsync(_options.StartTimeoutSeconds, ct);
        _log($"[HostedAgent] StartAsync: READY, pid={_backend.ProcessId}");
    }

    /// <summary>
    /// Send a prompt and return the FULL reply text, read from the transcript.
    /// </summary>
    public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        var backend = RequireRunning();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new AgentBrainException("AskAsync: prompt is empty");

        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var sid = RequireSessionId();
        var widgetsBefore = _driver.ReadWidgets(sid, _options.WorkingDirectory).Count;
        _log($"[HostedAgent] AskAsync: len={prompt.Length}, widgetsBefore={widgetsBefore}");

        var t0 = DateTime.UtcNow;
        await _driver.SubmitAsync(backend, prompt);

        var deadline = t0.AddSeconds(_options.AskTimeoutSeconds);
        string? answer = null;
        double replySeconds = 0;
        int stableCount = -1;
        DateTime stableSince = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (backend.HasExited)
                throw new AgentBrainException(
                    $"AskAsync: the hosted agent exited mid-turn (status={backend.Status}). RestartAsync to recover. " +
                    $"Terminal tail: {TerminalTail(backend)}");

            var widgets = _driver.ReadWidgets(sid, _options.WorkingDirectory);
            var lastText = widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
            if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
            {
                if (answer is null)
                    replySeconds = (DateTime.UtcNow - t0).TotalSeconds;
                answer = lastText.Content;

                // Accept only once the transcript stops growing, so multi-block
                // replies (thinking + text + more text) come back whole.
                if (widgets.Count != stableCount)
                {
                    stableCount = widgets.Count;
                    stableSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - stableSince).TotalSeconds >= _options.ReplyStableSeconds)
                {
                    break;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }

        if (answer is null)
            throw new AgentBrainException(
                $"AskAsync: no reply in the transcript after {_options.AskTimeoutSeconds}s " +
                $"(agentSessionId={sid}, backend={backend.Status})");

        var usage = _driver.ReadUsage(sid, _options.WorkingDirectory);
        _log($"[HostedAgent] AskAsync OK: replySeconds={replySeconds:F1}, answerLen={answer.Length}, " +
             $"context={usage?.ContextTokens ?? 0}");
        return new AskResult
        {
            Text = answer,
            ReplySeconds = replySeconds,
            ContextTokens = usage?.ContextTokens ?? 0,
        };
    }

    /// <summary>
    /// Abort the current turn via the driver's cancel keystroke (Esc for Claude).
    /// The process stays alive and prompt-ready.
    /// </summary>
    public async Task CancelAsync(CancellationToken ct = default)
    {
        var backend = RequireRunning();
        _log("[HostedAgent] CancelAsync");
        await _driver.CancelAsync(backend);
    }

    /// <summary>
    /// Reset the conversation WITHOUT restarting the process: the driver's context
    /// clear, then track the NEW agent-internal session id from the transcript dir.
    /// </summary>
    public async Task<ClearResult> ClearAsync(CancellationToken ct = default)
    {
        var backend = RequireRunning();
        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var oldId = RequireSessionId();
        _log($"[HostedAgent] ClearAsync: oldAgentSessionId={oldId}");
        var t0 = DateTime.UtcNow;
        await _driver.ClearContextAsync(backend);

        var deadline = t0.AddSeconds(_options.ClearTimeoutSeconds);
        string? newId = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (backend.HasExited)
                throw new AgentBrainException(
                    $"ClearAsync: the hosted agent exited during the context clear (status={backend.Status})");

            // The post-clear transcript is the file that is both new (id differs)
            // and recent (>= t0 minus clock slack) - old transcripts in the same
            // working directory must not match.
            var candidate = _driver.ListTranscripts(_options.WorkingDirectory)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s.AgentSessionId)
                                     && s.AgentSessionId != oldId
                                     && s.LastWriteUtc >= t0.AddSeconds(-10));
            if (!string.IsNullOrEmpty(candidate.AgentSessionId))
            {
                newId = candidate.AgentSessionId;
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }

        if (newId is null)
            throw new AgentBrainException(
                $"ClearAsync: no new transcript appeared within {_options.ClearTimeoutSeconds}s (oldId={oldId})");

        _agentSessionId = newId;

        // Let the post-clear repaint finish before the caller's next send (rule 1).
        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var seconds = (DateTime.UtcNow - t0).TotalSeconds;
        _log($"[HostedAgent] ClearAsync OK: newAgentSessionId={newId}, seconds={seconds:F1}");
        return new ClearResult
        {
            OldClaudeSessionId = oldId,
            NewClaudeSessionId = newId,
            Seconds = seconds,
        };
    }

    /// <summary>Hard recovery: kill the hosted process and spawn a fresh one.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _log($"[HostedAgent] RestartAsync: oldPid={_backend?.ProcessId ?? 0}");
        await ShutdownBackendAsync();
        await StartAsync(ct);
    }

    /// <summary>Terminate the hosted process.</summary>
    public async Task KillAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_backend is null)
            throw new AgentBrainException("KillAsync: not started");
        _log($"[HostedAgent] KillAsync: pid={_backend.ProcessId}");
        await ShutdownBackendAsync();
    }

    /// <summary>Liveness + idle clock + token usage. Deliberately unlogged: UIs poll
    /// this every second or two; real operations log their own entry/exit.</summary>
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var backend = _backend;
        if (backend is null || _agentSessionId is null)
        {
            return Task.FromResult(new BrainHealth
            {
                IsAlive = false,
                Status = "NotStarted",
                ActivityState = "NotStarted",
            });
        }

        var usage = _driver.ReadUsage(_agentSessionId, _options.WorkingDirectory);
        var idle = IdleSeconds(backend);
        return Task.FromResult(new BrainHealth
        {
            IsAlive = !backend.HasExited,
            Status = backend.Status,
            ActivityState = backend.HasExited ? "Exited" : (idle >= _options.QuietSeconds ? "Quiet" : "Active"),
            IdleSeconds = idle,
            ContextTokens = usage?.ContextTokens ?? 0,
            TurnCount = usage?.Turns.Count ?? 0,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _backend?.Dispose();
        }
        catch (Exception ex)
        {
            // Dispose must not throw; the failure is still recorded.
            _log($"[HostedAgent] Dispose: backend dispose FAILED: {ex.Message}");
        }
        _backend = null;
    }

    // ------------------------------------------------------------- internals

    /// <summary>
    /// Rule 1: block until the CLI has painted at least once AND its terminal has been
    /// byte-silent for QuietSeconds - measured on the backend's own buffer clock, the
    /// same primitive the Director's idle clock is built on.
    /// </summary>
    private async Task WaitForQuietAsync(double timeoutSeconds, CancellationToken ct)
    {
        var backend = RequireRunning();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (backend.HasExited)
                throw new AgentBrainException(
                    $"WaitForQuiet: the hosted agent is dead (status={backend.Status}). RestartAsync to recover. " +
                    $"Terminal tail: {TerminalTail(backend)}");

            var buffer = backend.Buffer;
            if (buffer is not null && buffer.TotalBytesWritten > 0
                && IdleSeconds(backend) >= _options.QuietSeconds)
                return;

            if (DateTime.UtcNow >= deadline)
                throw new AgentBrainException(
                    $"WaitForQuiet: terminal not quiet after {timeoutSeconds}s " +
                    $"(bytes={buffer?.TotalBytesWritten ?? 0}, idle={IdleSeconds(backend):F1}s)");
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }
    }

    /// <summary>The terminal's last words (ANSI-stripped) - what the CLI printed
    /// before dying. Goes into death exceptions so failures are diagnosable from the
    /// log alone, without a terminal view attached.</summary>
    private static string TerminalTail(ISessionBackend backend)
    {
        var buffer = backend.Buffer;
        if (buffer is null) return "(no buffer)";
        var bytes = buffer.DumpAll();
        var text = Core.Drivers.ClaudeDriver.StripAnsi(System.Text.Encoding.UTF8.GetString(bytes));
        var compact = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return compact.Length <= 400 ? compact : compact[^400..];
    }

    private static double IdleSeconds(ISessionBackend backend)
    {
        var last = backend.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
        if (last == DateTime.MinValue) return 0;
        return Math.Max(0, (DateTime.UtcNow - last).TotalSeconds);
    }

    private async Task ShutdownBackendAsync()
    {
        var backend = _backend;
        if (backend is null) return;
        await backend.GracefulShutdownAsync();
        backend.Dispose();
        _backend = null;
        _agentSessionId = null;
    }

    private ISessionBackend RequireRunning()
    {
        ThrowIfDisposed();
        return _backend ?? throw new AgentBrainException("No hosted session. Call StartAsync first.");
    }

    private string RequireSessionId() =>
        _agentSessionId ?? throw new AgentBrainException("No agent session id. Call StartAsync first.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HostedAgent));
    }
}
