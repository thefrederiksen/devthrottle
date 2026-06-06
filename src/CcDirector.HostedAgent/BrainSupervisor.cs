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
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HostedAgent? _agent;
    private bool _disposed;

    public BrainSupervisor(HostedAgentOptions options, Func<HostedAgentOptions, HostedAgent>? agentFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
            throw new ArgumentException("WorkingDirectory is required", nameof(options));
        _agentFactory = agentFactory ?? (o => new HostedAgent(o));
        _log = options.Log ?? BrainLog.Write;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var agent = _agent;
        _agent = null;
        if (agent is not null)
        {
            // Synchronous best-effort graceful stop (Ctrl+C, wait, then force) so the
            // host process exit never leaks a claude.exe; Dispose alone would only
            // tear the pseudoconsole down.
            try { agent.KillAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _log($"[BrainSupervisor] Dispose: graceful kill FAILED: {ex.Message}"); }
            try { agent.Dispose(); }
            catch (Exception ex) { _log($"[BrainSupervisor] Dispose: agent dispose FAILED: {ex.Message}"); }
        }
        _gate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BrainSupervisor));
    }
}
