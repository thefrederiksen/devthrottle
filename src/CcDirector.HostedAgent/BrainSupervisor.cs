using CcDirector.AgentBrain;

namespace CcDirector.HostedAgent;

/// <summary>
/// Owns ONE hosted brain on behalf of a long-lived host process (the Gateway tray app).
/// The brain is expensive (a whole claude.exe), so it is NOT started with the host -
/// the first <see cref="GetAsync"/> spawns it (create on demand) and every later call
/// returns the same warm instance. <see cref="RestartAsync"/> is the one recovery verb:
/// wedged or dead brains are not debugged, they are replaced. Disposing the supervisor
/// gracefully stops the hosted CLI so the host process never leaks a claude.exe.
///
/// All lifecycle transitions are serialized through one gate, so concurrent consumers
/// (a brief agent turn racing a Settings-window restart click) cannot double-spawn.
///
/// The agent factory is a test seam: production uses a real <see cref="HostedAgent"/>
/// (ConPty + ClaudeDriver); tests inject one built on the fake driver/backend pair.
/// </summary>
public sealed class BrainSupervisor : IDisposable
{
    private readonly HostedAgentOptions _options;
    private readonly Func<HostedAgentOptions, HostedAgent> _agentFactory;
    private readonly Action<string> _log;
    private readonly TimeSpan _disposeGracePeriod;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HostedAgent? _agent;
    private bool _disposed;

    public BrainSupervisor(
        HostedAgentOptions options,
        Func<HostedAgentOptions, HostedAgent>? agentFactory = null,
        TimeSpan? disposeGracePeriod = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
            throw new ArgumentException("WorkingDirectory is required", nameof(options));
        _agentFactory = agentFactory ?? (o => new HostedAgent(o));
        _log = options.Log ?? BrainLog.Write;
        _disposeGracePeriod = disposeGracePeriod ?? DisposeGracePeriod;
    }

    /// <summary>True once the brain has been created and started (it may still have died
    /// since - see <see cref="GetHealthAsync"/> for liveness).</summary>
    public bool IsStarted => _agent is not null;

    /// <summary>The brain's agent-internal session id, or null before first use.</summary>
    public string? SessionId => _agent?.SessionId;

    /// <summary>PID of the hosted CLI, 0 before first use / after death.</summary>
    public int ProcessId => _agent?.ProcessId ?? 0;

    /// <summary>
    /// The warm brain, spawning it on first use. The working directory is created when
    /// missing - the brain's dir is supervisor-owned scratch space, not user data.
    ///
    /// ALWAYS goes through the gate - no lock-free fast path. A caller must never receive
    /// the agent while a RestartAsync holds the gate mid-replacement: that handed out a
    /// half-torn-down brain in production (every ask failed "exited mid-turn", each failure
    /// fired another recovery - a restart storm, found live during the issue #185 rollout).
    /// Waiting here means the next ask simply blocks until the fresh brain is ready.
    /// </summary>
    public async Task<IAgentBrain> GetAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_agent is not null) return _agent;

            _log($"[BrainSupervisor] first use - starting the brain (workdir={_options.WorkingDirectory})");
            Directory.CreateDirectory(_options.WorkingDirectory);
            var agent = _agentFactory(_options);
            await agent.StartAsync(ct);
            _agent = agent;
            _log($"[BrainSupervisor] brain ready: pid={agent.ProcessId}, session={agent.SessionId}");
            return agent;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The recovery verb: restart a started brain (kill + fresh spawn, same handle), or
    /// perform the first start when nothing is running yet - so a Settings-window
    /// "restart" click always ends with a live brain.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_agent is null)
            {
                // The recovery verb doubles as the first start.
                _log($"[BrainSupervisor] RestartAsync: nothing running - starting the brain (workdir={_options.WorkingDirectory})");
                Directory.CreateDirectory(_options.WorkingDirectory);
                var agent = _agentFactory(_options);
                await agent.StartAsync(ct);
                _agent = agent;
                _log($"[BrainSupervisor] brain ready: pid={agent.ProcessId}, session={agent.SessionId}");
                return;
            }

            _log($"[BrainSupervisor] RestartAsync: oldPid={_agent.ProcessId}");
            await _agent.RestartAsync(ct);
            _log($"[BrainSupervisor] RestartAsync OK: pid={_agent.ProcessId}, session={_agent.SessionId}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Brain health; a NotStarted snapshot before first use (never spawns).</summary>
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var agent = _agent;
        if (agent is null)
        {
            return Task.FromResult(new BrainHealth
            {
                IsAlive = false,
                Status = "NotStarted",
                ActivityState = "NotStarted",
            });
        }
        return agent.GetHealthAsync(ct);
    }

    /// <summary>
    /// How long <see cref="Dispose"/> waits for each stop step (the graceful kill, then the agent
    /// dispose) before moving on (issue #880). Generous next to the hosted CLI's own 5-second
    /// graceful-exit window, but a HARD bound: a wedged brain can never hold the host's shutdown.
    /// </summary>
    public static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stops the hosted CLI and releases the supervisor, with a hard time bound on every step
    /// (issue #880). The graceful kill runs on the THREAD POOL, never inline: Dispose ran the
    /// kill's awaits inline pre-#880, so when the Gateway tray disposed the brain on its UI
    /// thread, the kill's continuations were posted back to that same blocked thread - the
    /// classic synchronous-wait-over-async deadlock. The tray froze for over an hour and the
    /// force-kill after the CLI's 5-second grace never ran. Task.Run gives the kill a
    /// context-free thread, and the bounded wait force-kills the hosted process tree if the
    /// graceful stop does not finish in time - the host's shutdown always proceeds.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var agent = _agent;
        _agent = null;
        if (agent is not null)
            StopAgentBounded(agent);
        _gate.Dispose();
    }

    private void StopAgentBounded(HostedAgent agent)
    {
        var pid = agent.ProcessId;

        var gracefulCompleted = false;
        try
        {
            var kill = Task.Run(() => agent.KillAsync());
            gracefulCompleted = kill.Wait(_disposeGracePeriod);
            if (!gracefulCompleted)
            {
                // The abandoned kill may still fault later (e.g. against the disposed agent
                // below); observe it so the failure is logged instead of lost.
                _ = kill.ContinueWith(
                    t => _log($"[BrainSupervisor] Dispose: abandoned graceful kill faulted: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (AggregateException ex)
        {
            // The kill ran to completion by FAILING - nothing is left to wait for.
            _log($"[BrainSupervisor] Dispose: graceful kill FAILED: {ex.GetBaseException().Message}");
            gracefulCompleted = true;
        }

        if (!gracefulCompleted)
        {
            _log($"[BrainSupervisor] Dispose: graceful stop did not finish within {_disposeGracePeriod.TotalSeconds:0.##}s -> force-killing the hosted process tree (pid={pid})");
            try
            {
                if (pid > 0)
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException) { /* process already gone - the goal state */ }
            catch (Exception ex) { _log($"[BrainSupervisor] Dispose: force kill FAILED: {ex.Message}"); }
        }

        try
        {
            if (!Task.Run(agent.Dispose).Wait(_disposeGracePeriod))
                _log($"[BrainSupervisor] Dispose: agent dispose did not finish within {_disposeGracePeriod.TotalSeconds:0.##}s -> abandoning it (the hosted process is already down; remaining handles are reclaimed at process exit)");
        }
        catch (AggregateException ex)
        {
            _log($"[BrainSupervisor] Dispose: agent dispose FAILED: {ex.GetBaseException().Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BrainSupervisor));
    }
}
