using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Fleet;

/// <summary>Everything the Fleet Manager setting needs from the Gateway around it, as one seam.</summary>
internal interface IFleetManagerPlacementEnvironment
{
    /// <summary>Every computer on the account, with what the Gateway knows about each.</summary>
    IReadOnlyList<FleetManagerMachineFacts> Machines(TenantId tenant);

    /// <summary>The account's fresh roster, with the Director each session is on.</summary>
    IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant);

    /// <summary>The agents a running Director offers, or null when they cannot be read.</summary>
    Task<IReadOnlyList<AgentChoiceDto>?> AgentsOfferedAsync(TenantId tenant, string directorId, CancellationToken ct);

    /// <summary>Whether that Director said on connecting that it understands the Fleet Manager's own folder.</summary>
    bool CreatesFleetManagerHome(TenantId tenant, string directorId);

    /// <summary>Start a session on a named computer - the one resolve-then-create path, launching a Director when
    /// none runs there, never a local fallback.</summary>
    Task<(bool Ok, SessionDto? Session, string? Error, string? DirectorId)> SpawnAsync(TenantId tenant, string machine,
        NewSessionRequest request, Func<string, string?> refuseDirector, CancellationToken ct);

    /// <summary>Close a session through the Gateway's ordinary close command. True when the Director carried it out.</summary>
    Task<bool> CloseSessionAsync(TenantId tenant, string directorId, string sessionId, string reason, CancellationToken ct);

    /// <summary>The account's display time zone.</summary>
    TimeZoneInfo TimeZone(TenantId tenant);

    /// <summary>Record the session in the account's history of Fleet Managers, beside the mark, as the mark route
    /// does, so the digest still finds the sessions an earlier Fleet Manager started.</summary>
    void RecordMark(TenantId tenant, string sessionId, DateTime nowUtc);

    /// <summary>Wait. The only clock the retirement loop uses, so a test can run it instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    DateTime NowUtc();
}

/// <summary>How one placement action ended: an HTTP status, the Gateway's own sentence on a refusal, and the
/// refreshed answer on success.</summary>
internal sealed record FleetManagerPlacementResult(int Status, string? Error, FleetManagerPlacementDto? Placement)
{
    public static FleetManagerPlacementResult Ok(FleetManagerPlacementDto placement) => new(200, null, placement);
    public static FleetManagerPlacementResult Refused(int status, string error) => new(status, error, null);
}

/// <summary>
/// WHERE THE ACCOUNT'S FLEET MANAGER RUNS, AND STARTING, RESTARTING AND MOVING IT (the Fleet Manager mission,
/// step 5).
///
/// SAVE records the agent and the computer together, and refuses an unknown agent, a computer that is not on the
/// account, and a computer that cannot be reached now. A default is never stored by reading it: it is written
/// only when the owner saves, or starts the Fleet Manager from it.
///
/// START runs the Fleet Manager where the setting says, through the one path that starts a session on a named
/// computer, so the launcher starts a Director there when none is running. It is refused when the marked Fleet
/// Manager is already running and when the computer cannot be reached - it never starts anywhere else. On success
/// the new session becomes the account's marked Fleet Manager.
///
/// RESTART starts a new one in the saved place and marks it, and only then retires the old one: the old one is
/// closed once its current turn has ended, through the ordinary close command, and never mid-turn. If the new one
/// does not start, the old one is left running and still marked, and the error is returned.
///
/// MOVE is save, then restart there (or start, when none is running).
/// </summary>
internal sealed class FleetManagerPlacementService : IDisposable
{
    /// <summary>The name every Fleet Manager session is started with.</summary>
    public const string SessionName = "Fleet Manager";

    /// <summary>The first prompt: fetch the conduct, then read the digest. The conduct says the rest.</summary>
    public const string FirstPrompt =
        "You are this account's Fleet Manager. Run `cc-devthrottle workflow instructions fleet-manager` and follow "
        + "it exactly, then run `cc-devthrottle fleet digest` to see where things stand.";

    /// <summary>The reason the old Fleet Manager's close is recorded with.</summary>
    public const string RetireReason = "A new Fleet Manager replaced it (restarted or moved from Settings).";

    /// <summary>How often the retirement loop looks at the old Fleet Manager.</summary>
    public static readonly TimeSpan RetirePollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long the retirement loop waits for the old Fleet Manager's turn to end before it gives up and
    /// leaves it running. It is never force-closed mid-turn.</summary>
    public static readonly TimeSpan RetireGiveUpAfter = TimeSpan.FromHours(12);

    /// <summary>How long a page read waits for a Director's agent list before answering without it.</summary>
    public static readonly TimeSpan AgentListTimeout = TimeSpan.FromSeconds(4);

    private readonly TenantSettingsResolver _settings;
    private readonly IFleetManagerPlacementEnvironment _env;
    private readonly TimeSpan _retirePoll;
    private readonly TimeSpan _retireGiveUp;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    // One action at a time per account: two starts racing would make two Fleet Managers.
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _gates = new();

    public FleetManagerPlacementService(TenantSettingsResolver settings, IFleetManagerPlacementEnvironment environment,
        TimeSpan? retirePoll = null, TimeSpan? retireGiveUp = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _retirePoll = retirePoll ?? RetirePollInterval;
        _retireGiveUp = retireGiveUp ?? RetireGiveUpAfter;
    }

    // ---- read --------------------------------------------------------------------------------------------

    /// <summary>The folded answer the Settings tab renders.</summary>
    public async Task<FleetManagerPlacementDto> ReadAsync(TenantId tenant, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] ReadAsync: tenant={tenant.ToLogString()}");
        try
        {
            var (_, dto) = await FoldAsync(tenant, extraLive: null, ct).ConfigureAwait(false);
            FileLog.Write($"[FleetManagerPlacementService] ReadAsync: agent={dto.Agent}, machine={dto.Machine}, " +
                          $"default={dto.IsDefault}, status={dto.Status.State}, machines={dto.Machines.Count}");
            return dto;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerPlacementService] ReadAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<(FleetManagerPlacementInputs Inputs, FleetManagerPlacementDto Dto)> FoldAsync(
        TenantId tenant, SessionDto? extraLive, CancellationToken ct)
    {
        var now = _env.NowUtc();
        var machines = _env.Machines(tenant);
        var savedAgent = _settings.FleetManagerAgent(tenant);
        var savedMachine = _settings.FleetManagerMachine(tenant);
        var placementMachine = FleetManagerAgents.Canonical(savedAgent) is not null && !string.IsNullOrWhiteSpace(savedMachine)
            ? savedMachine
            : FleetManagerPlacementFold.DefaultMachine(machines)?.Machine;

        IReadOnlyList<AgentChoiceDto>? offered = null;
        var facts = placementMachine is null
            ? null
            : machines.FirstOrDefault(m => FleetManagerPlacementFold.SameMachine(m.Machine, placementMachine));
        if (facts is not null && FleetManagerPlacementFold.RunningDirector(facts, now) is { } director)
            offered = await ReadAgentsAsync(tenant, director.DirectorId, ct).ConfigureAwait(false);

        var roster = _env.Roster(tenant).Select(r => r.Session).ToList();
        if (extraLive is not null && !roster.Any(s => string.Equals(s.SessionId, extraLive.SessionId, StringComparison.OrdinalIgnoreCase)))
            roster.Add(extraLive);

        var inputs = new FleetManagerPlacementInputs(savedAgent, savedMachine, _settings.FleetManagerSessionId(tenant),
            machines, roster, offered, _env.TimeZone(tenant), now);
        return (inputs, FleetManagerPlacementFold.Fold(inputs));
    }

    private async Task<IReadOnlyList<AgentChoiceDto>?> ReadAgentsAsync(TenantId tenant, string directorId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AgentListTimeout);
        try
        {
            return await _env.AgentsOfferedAsync(tenant, directorId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not knowing is an answer the page shows ("installed" is simply not said); it never blocks the read.
            FileLog.Write($"[FleetManagerPlacementService] agent list from director={directorId} did not answer within {AgentListTimeout.TotalSeconds:0}s");
            return null;
        }
    }

    // ---- save --------------------------------------------------------------------------------------------

    /// <summary>Save where the Fleet Manager runs. 400 with a sentence for an unknown agent, a computer not on the
    /// account, or a computer that cannot be reached now.</summary>
    public async Task<FleetManagerPlacementResult> SaveAsync(TenantId tenant, FleetManagerPlacementRequest? request, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] SaveAsync: tenant={tenant.ToLogString()}, agent={request?.Agent}, machine={request?.Machine}");
        try
        {
            var gate = Gate(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var refusal = ValidateAndSave(tenant, request);
                if (refusal is not null) return refusal;
                var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
                FileLog.Write($"[FleetManagerPlacementService] SaveAsync: saved agent={dto.Agent}, machine={dto.Machine}");
                return FleetManagerPlacementResult.Ok(dto);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementService] SaveAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private FleetManagerPlacementResult? ValidateAndSave(TenantId tenant, FleetManagerPlacementRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Agent) || string.IsNullOrWhiteSpace(request.Machine))
            return Refuse(400, "Both an agent and a computer are required: { \"agent\": \"ClaudeCode\", \"machine\": \"<computer>\" }. "
                               + "Nothing was saved.");

        var agent = FleetManagerAgents.Canonical(request.Agent);
        if (agent is null)
            return Refuse(400, $"'{request.Agent}' is not an agent the Fleet Manager can run on. Choose one of: "
                               + $"{string.Join(", ", FleetManagerAgents.All.Select(a => a.Value))}. Nothing was saved.");

        var now = _env.NowUtc();
        var facts = _env.Machines(tenant).FirstOrDefault(m => FleetManagerPlacementFold.SameMachine(m.Machine, request.Machine));
        if (facts is null)
            return Refuse(400, $"'{request.Machine}' is not a computer on this account. Nothing was saved.");
        if (!FleetManagerPlacementFold.IsReachable(facts, now))
            return Refuse(400, $"{facts.Machine} cannot be reached now, so the Fleet Manager cannot be placed there. "
                               + "Choose a computer that is on. Nothing was saved.");

        _settings.SetFleetManagerPlacement(tenant, agent, facts.Machine, now);
        return null;
    }

    // ---- start, restart, move ----------------------------------------------------------------------------

    /// <summary>Start the Fleet Manager where the setting says. <paramref name="stampOrigin"/> records who asked,
    /// the way the spawn doors do.</summary>
    public async Task<FleetManagerPlacementResult> StartAsync(TenantId tenant, Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] StartAsync: tenant={tenant.ToLogString()}");
        return await Guarded(tenant, "StartAsync", async () =>
        {
            var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
            if (dto.Status.State == FleetManagerStatusDto.StateRunning)
                return Refuse(409, $"The Fleet Manager is already running (session {dto.Status.SessionId}). "
                                   + "Restart it instead if you want a new one.");
            return await StartNewAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Start a new Fleet Manager in the saved place, mark it, then close the old one after its turn.</summary>
    public async Task<FleetManagerPlacementResult> RestartAsync(TenantId tenant, Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] RestartAsync: tenant={tenant.ToLogString()}");
        return await Guarded(tenant, "RestartAsync", async () =>
        {
            var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
            if (dto.Status.State != FleetManagerStatusDto.StateRunning)
                return Refuse(409, "The Fleet Manager is not running, so there is nothing to restart. Start it instead.");
            return await ReplaceAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Save the new place (with the save's refusals), then restart there - or start, when none is running.</summary>
    public async Task<FleetManagerPlacementResult> MoveAsync(TenantId tenant, FleetManagerPlacementRequest? request,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] MoveAsync: tenant={tenant.ToLogString()}, agent={request?.Agent}, machine={request?.Machine}");
        return await Guarded(tenant, "MoveAsync", async () =>
        {
            var refusal = ValidateAndSave(tenant, request);
            if (refusal is not null) return refusal;

            var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
            return dto.Status.State == FleetManagerStatusDto.StateRunning
                ? await ReplaceAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false)
                : await StartNewAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<FleetManagerPlacementResult> ReplaceAsync(TenantId tenant, FleetManagerPlacementDto dto,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        var oldId = dto.Status.SessionId!;
        var result = await StartNewAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        if (result.Status != 200)
        {
            FileLog.Write($"[FleetManagerPlacementService] replace: the new Fleet Manager did not start; {oldId} stays running and marked");
            return result;
        }

        FileLog.Write($"[FleetManagerPlacementService] replace: {result.Placement!.Status.SessionId} is marked; retiring {oldId} after its current turn");
        Track(Task.Run(() => RetireAfterTurnAsync(tenant, oldId)));
        return result;
    }

    private async Task<FleetManagerPlacementResult> StartNewAsync(TenantId tenant, FleetManagerPlacementDto dto,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        if (dto.Machine is null || dto.Agent is null)
            return Refuse(409, "This account has no computer for the Fleet Manager to run on, so it was not started.");

        var placement = dto.Machines.First(m => FleetManagerPlacementFold.SameMachine(m.Machine, dto.Machine));
        if (!placement.Selectable)
            return Refuse(409, $"{placement.Machine} cannot be reached now, so the Fleet Manager was not started. It never "
                               + "starts anywhere else: choose another computer and save, or start it when that one is back.");

        var facts = _env.Machines(tenant).First(m => FleetManagerPlacementFold.SameMachine(m.Machine, dto.Machine));
        var running = FleetManagerPlacementFold.RunningDirector(facts, _env.NowUtc());

        var request = BuildStartRequest(dto.Agent);
        stampOrigin(request);
        // Pinned to the Director the page called running, so the capability that was checked is the one that
        // takes the create. With none running, the launcher starts one and the check runs on that one.
        request.Director = running?.DirectorId;

        var (ok, session, error, directorId) = await _env.SpawnAsync(tenant, placement.Machine, request,
            id => _env.CreatesFleetManagerHome(tenant, id)
                ? null
                : $"The Director on {placement.Machine} is older than the Fleet Manager and must be updated before the "
                  + "Fleet Manager can run there. Update DevThrottle on that computer, then start it again.",
            ct).ConfigureAwait(false);
        if (!ok || session is null)
        {
            FileLog.Write($"[FleetManagerPlacementService] start FAILED on {placement.Machine} (director={directorId}): {error}");
            return Refuse(502, $"The Fleet Manager could not be started on {placement.Machine}: {error}");
        }

        var markedAt = _env.NowUtc();
        _settings.SetFleetManagerSessionId(tenant, session.SessionId, markedAt);
        _env.RecordMark(tenant, _settings.FleetManagerSessionId(tenant)!, markedAt);
        if (dto.IsDefault)
            _settings.SetFleetManagerPlacement(tenant, dto.Agent, dto.Machine, _env.NowUtc());
        FileLog.Write($"[FleetManagerPlacementService] started Fleet Manager {session.SessionId} ({dto.Agent}) on " +
                      $"{placement.Machine}, director={directorId}; marked as the account's Fleet Manager");

        if (string.IsNullOrEmpty(session.MachineName)) session.MachineName = placement.Machine;
        if (string.IsNullOrEmpty(session.Agent)) session.Agent = dto.Agent;
        var (_, refreshed) = await FoldAsync(tenant, session, ct).ConfigureAwait(false);
        return FleetManagerPlacementResult.Ok(refreshed);
    }

    /// <summary>The create request every Fleet Manager start sends. The origin is stated as a person's action from
    /// the Cockpit; the route overwrites it from the verified credential the way both spawn doors do.</summary>
    public static NewSessionRequest BuildStartRequest(string agent) => new()
    {
        RepoPath = "",
        FleetManagerHome = true,
        Name = SessionName,
        Agent = agent,
        PrePrompt = FirstPrompt,
        ControllerSessionId = null,
        Origin = SessionOriginKinds.Human,
        OriginSurface = SessionOriginSurfaces.Cockpit,
    };

    /// <summary>
    /// Close the old Fleet Manager once its current turn has ended. It is looked at every few seconds; when it is
    /// waiting for a prompt it is closed through the ordinary close command. A session asking a permission question
    /// is not idle - closing it would throw away the turn - so it waits. It is never force-closed; after
    /// <see cref="RetireGiveUpAfter"/> the loop gives up and leaves it running, and the log says so.
    /// </summary>
    internal async Task RetireAfterTurnAsync(TenantId tenant, string oldSessionId)
    {
        var deadline = _env.NowUtc() + _retireGiveUp;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var found = _env.Roster(tenant).FirstOrDefault(r =>
                    string.Equals(r.Session.SessionId, oldSessionId, StringComparison.OrdinalIgnoreCase));
                if (found.Session is not null)
                {
                    var s = found.Session;
                    if (s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                    {
                        FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId}: it has already ended");
                        return;
                    }
                    if (IsIdle(s))
                    {
                        var closed = await _env.CloseSessionAsync(tenant, found.DirectorId, oldSessionId, RetireReason, _shutdown.Token)
                            .ConfigureAwait(false);
                        if (closed)
                        {
                            FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId}: closed after its turn ended");
                            return;
                        }
                        FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId}: the close did not go through; trying again");
                    }
                }

                if (_env.NowUtc() >= deadline)
                {
                    FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId} GAVE UP after {_retireGiveUp.TotalHours:0} hours: " +
                                  "its turn never ended while this Gateway could see it. It is left running and is no longer the marked Fleet Manager.");
                    return;
                }
                await _env.DelayAsync(_retirePoll, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId}: the Gateway is shutting down; it is left running");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerPlacementService] retire {oldSessionId} FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Waiting for a prompt. The same reading the Fleet Manager's event delivery uses.</summary>
    private static bool IsIdle(SessionDto s)
        => string.Equals(s.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase)
           || string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase);

    /// <summary>Wait for every retirement started so far. For tests and shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray()).ConfigureAwait(false);
    }

    private async Task<FleetManagerPlacementResult> Guarded(TenantId tenant, string name, Func<Task<FleetManagerPlacementResult>> body)
    {
        try
        {
            var gate = Gate(tenant);
            await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var result = await body().ConfigureAwait(false);
                FileLog.Write($"[FleetManagerPlacementService] {name}: status={result.Status}{(result.Error is null ? "" : $", {result.Error}")}");
                return result;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementService] {name} FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private SemaphoreSlim Gate(TenantId tenant) => _gates.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));

    private static FleetManagerPlacementResult Refuse(int status, string error)
    {
        FileLog.Write($"[FleetManagerPlacementService] refused ({status}): {error}");
        return FleetManagerPlacementResult.Refused(status, error);
    }

    private void Track(Task task)
    {
        _running[task] = 0;
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    // The source is cancelled and not disposed: a retirement still waiting on it reads the token after this.
    public void Dispose() => _shutdown.Cancel();
}
