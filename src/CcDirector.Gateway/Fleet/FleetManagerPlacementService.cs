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

    /// <summary>
    /// Move the account's mark to the waiting successor: the mark, its history, the removal of the replacement and the
    /// one event that tells it, in ONE transaction (<see cref="FleetManagerPromotionStore"/>) - then book the event's
    /// delivery (the step 4 event path: only while it waits for a prompt, at least once, by id). Throws, having written
    /// nothing, when the transaction fails; the replacement stays recorded and the next look promotes it again.
    /// </summary>
    void PromoteSuccessor(TenantId tenant, string sessionId, DateTime nowUtc);

    /// <summary>
    /// The owner marks a session (<see cref="FleetManagerPromotionStore.MarkByOwner"/>): the mark and its history, and -
    /// when the session was started to take over and never told - its one event, in ONE transaction; then book that
    /// event's delivery. True when an event was stored.
    /// </summary>
    bool MarkByOwner(TenantId tenant, string sessionId, DateTime nowUtc);

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
/// RESTART starts a new one in the saved place and records it as the SUCCESSOR - it is not marked yet. The old one
/// stays the marked, authoritative Fleet Manager (it can still file what it is working on) until its turn has ended
/// and it has been closed through the ordinary close command. Only then does the mark move to the new one, and the
/// Gateway sends the new one ONE <c>marked</c> event saying it is now the Fleet Manager and should read its digest.
/// Until then the new one waits, as its first prompt tells it to. If the new one does not start, nothing changes.
///
/// CLOSING (<see cref="FleetManagerRetirement"/>): the old one is closed only when its Director reports it Idle - on
/// two looks in a row, with no delivery typed into it in between (<see cref="FleetManagerDeliveryGate"/>, which the
/// event service also holds, so no event is typed into it while a replacement is under way). A
/// session Working, WaitingForInput or WaitingForPerm is never closed - a turn end that asks the owner something is
/// not landed work - and while it waits for the owner, the status says so in the Gateway's own sentence. If the old
/// one ends by itself (the owner closed it), the mark moves then. If the mark was changed by hand meanwhile, or the
/// new one ended, the replacement is abandoned and the old one is left alone. The successor is kept in storage
/// TOGETHER WITH the session it is to close, so a Gateway restart carries the replacement on
/// (<see cref="ResumePendingAsync"/>) and still closes only that session: a mark changed by hand before the resumed
/// loop's first look abandons it exactly as it would have without the restart.
///
/// THE MARK IS COMPARED WITH THE RECORDED OLD ONE FIRST (round 3). The replacement carries on only while the mark still
/// names that session, or while there is no mark because the Gateway ITSELF removed it from that session - which it
/// does, recording why (exited or closed) in the same save, once the old one has ended or been closed. No mark without
/// that record means the owner cleared it: the replacement is abandoned and nobody is promoted. A mark set by hand to
/// any other session abandons it too. A mark set to the waiting new one - by any route - closes nothing, and tells that
/// session once, because a session started to take over waits for that event and must not wait forever (sessions
/// waiting to be told are remembered, <see cref="TenantSettingKeys.FleetManagerWaitingSuccessors"/>). The move of the
/// mark is one transaction (<see cref="FleetManagerPromotionStore"/>), so a promotion that fails is finished by the
/// next look and the new one is always told.
///
/// MOVE checks the new place, then restarts there (or starts, when none is running). The place is SAVED ONLY AFTER
/// the new Fleet Manager has started: a failed start leaves the setting as it was.
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

    /// <summary>How many sessions started to take over, and not yet told, an account remembers.</summary>
    public const int MaxWaitingSuccessors = 20;

    /// <summary>How long after an interrupted start the sweep still looks for the session it started.</summary>
    public static readonly TimeSpan InterruptedStartWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long a page read waits for a Director's agent list before answering without it.</summary>
    public static readonly TimeSpan AgentListTimeout = TimeSpan.FromSeconds(4);

    private readonly TenantSettingsResolver _settings;
    private readonly IFleetManagerPlacementEnvironment _env;
    private readonly FleetManagerDeliveryGate _deliveryGate;
    private readonly TimeSpan _retirePoll;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    // Accounts whose replacement is being watched in this process. Which session it may close is in storage, not here.
    private readonly ConcurrentDictionary<TenantId, byte> _retiring = new();

    // One action at a time per account: two starts racing would make two Fleet Managers.
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _gates = new();

    /// <param name="deliveryGate">Shared with the event service, so a delivery and a replacement never overlap.</param>
    public FleetManagerPlacementService(TenantSettingsResolver settings, IFleetManagerPlacementEnvironment environment,
        FleetManagerDeliveryGate deliveryGate, TimeSpan? retirePoll = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _deliveryGate = deliveryGate ?? throw new ArgumentNullException(nameof(deliveryGate));
        _retirePoll = retirePoll ?? RetirePollInterval;
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
        TenantId tenant, SessionDto? extraLive, CancellationToken ct, (string Agent, string Machine)? proposed = null)
    {
        var now = _env.NowUtc();
        var machines = _env.Machines(tenant);
        // A move folds the place it was asked for, before anything is saved.
        var savedAgent = proposed?.Agent ?? _settings.FleetManagerAgent(tenant);
        var savedMachine = proposed?.Machine ?? _settings.FleetManagerMachine(tenant);
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
            machines, roster, offered, _env.TimeZone(tenant), now, _settings.FleetManagerSuccessorSessionId(tenant));
        var dto = FleetManagerPlacementFold.Fold(inputs);
        if (proposed is not null) dto.IsDefault = false;
        return (inputs, dto);
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
                if (Successor(tenant) is { } successor)
                    return ReplacementUnderWay(successor);
                var (refusal, place) = Validate(tenant, request);
                if (refusal is not null) return refusal;
                _settings.SetFleetManagerPlacement(tenant, place!.Value.Agent, place.Value.Machine, _env.NowUtc());
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

    /// <summary>Check a requested place. Nothing is saved here.</summary>
    private (FleetManagerPlacementResult? Refusal, (string Agent, string Machine)? Place) Validate(
        TenantId tenant, FleetManagerPlacementRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Agent) || string.IsNullOrWhiteSpace(request.Machine))
            return (Refuse(400, "Both an agent and a computer are required: { \"agent\": \"ClaudeCode\", \"machine\": \"<computer>\" }. "
                               + "Nothing was saved."), null);

        var agent = FleetManagerAgents.Canonical(request.Agent);
        if (agent is null)
            return (Refuse(400, $"'{request.Agent}' is not an agent the Fleet Manager can run on. Choose one of: "
                               + $"{string.Join(", ", FleetManagerAgents.All.Select(a => a.Value))}. Nothing was saved."), null);

        var now = _env.NowUtc();
        var facts = _env.Machines(tenant).FirstOrDefault(m => FleetManagerPlacementFold.SameMachine(m.Machine, request.Machine));
        if (facts is null)
            return (Refuse(400, $"'{request.Machine}' is not a computer on this account. Nothing was saved."), null);
        if (!FleetManagerPlacementFold.IsReachable(facts, now))
            return (Refuse(400, $"{facts.Machine} cannot be reached now, so the Fleet Manager cannot be placed there. "
                               + "Choose a computer that is on. Nothing was saved."), null);

        return (null, (agent, facts.Machine));
    }

    // ---- start, restart, move ----------------------------------------------------------------------------

    /// <summary>Start the Fleet Manager where the setting says. <paramref name="stampOrigin"/> records who asked,
    /// the way the spawn doors do.</summary>
    public async Task<FleetManagerPlacementResult> StartAsync(TenantId tenant, Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] StartAsync: tenant={tenant.ToLogString()}");
        return await Guarded(tenant, "StartAsync", async () =>
        {
            if (Successor(tenant) is { } successor)
                return ReplacementUnderWay(successor);
            var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
            if (dto.Status.State == FleetManagerStatusDto.StateRunning)
                return Refuse(409, $"The Fleet Manager is already running (session {dto.Status.SessionId}). "
                                   + "Restart it instead if you want a new one.");
            return await StartNewAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// THE OWNER SETS OR CLEARS THE MARK (the mark route, and the command that calls it). Taken under the same per-account
    /// gate as a replacement's look, so a replacement never reads the mark, then closes a session, around this change.
    /// A cleared mark records nothing about the Gateway, so a replacement under way reads it as the owner's and is
    /// abandoned. A session marked here that was started to take over and never told gets its one event now. Returns
    /// the stored mark.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a session id.</exception>
    public async Task<string?> SetMarkByOwnerAsync(TenantId tenant, string? sessionId, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] SetMarkByOwnerAsync: tenant={tenant.ToLogString()}, session={sessionId ?? "(clear)"}");
        try
        {
            var gate = Gate(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _env.NowUtc();
                if (sessionId is null)
                {
                    var removed = _settings.ClearFleetManagerSessionId(tenant, now);
                    FileLog.Write($"[FleetManagerPlacementService] SetMarkByOwnerAsync: mark cleared by the owner (removed={removed})");
                    return null;
                }
                var told = _env.MarkByOwner(tenant, sessionId, now);
                var stored = _settings.FleetManagerSessionId(tenant);
                FileLog.Write($"[FleetManagerPlacementService] SetMarkByOwnerAsync: mark={stored}, told={told}");
                return stored;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementService] SetMarkByOwnerAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>Start a new Fleet Manager in the saved place, mark it, then close the old one after its turn.</summary>
    public async Task<FleetManagerPlacementResult> RestartAsync(TenantId tenant, Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] RestartAsync: tenant={tenant.ToLogString()}");
        return await Guarded(tenant, "RestartAsync", async () =>
        {
            if (Successor(tenant) is { } successor)
                return ReplacementUnderWay(successor);
            var (_, dto) = await FoldAsync(tenant, null, ct).ConfigureAwait(false);
            if (dto.Status.State != FleetManagerStatusDto.StateRunning)
                return Refuse(409, "The Fleet Manager is not running, so there is nothing to restart. Start it instead.");
            return await ReplaceAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Check the new place (with the save's refusals), then restart there - or start, when none is running.
    /// The place is saved only once the new Fleet Manager has started.</summary>
    public async Task<FleetManagerPlacementResult> MoveAsync(TenantId tenant, FleetManagerPlacementRequest? request,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerPlacementService] MoveAsync: tenant={tenant.ToLogString()}, agent={request?.Agent}, machine={request?.Machine}");
        return await Guarded(tenant, "MoveAsync", async () =>
        {
            if (Successor(tenant) is { } successor)
                return ReplacementUnderWay(successor);
            var (refusal, place) = Validate(tenant, request);
            if (refusal is not null) return refusal;

            var (_, dto) = await FoldAsync(tenant, null, ct, place).ConfigureAwait(false);
            return dto.Status.State == FleetManagerStatusDto.StateRunning
                ? await ReplaceAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false)
                : await StartNewAsync(tenant, dto, stampOrigin, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<FleetManagerPlacementResult> ReplaceAsync(TenantId tenant, FleetManagerPlacementDto dto,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct)
    {
        var oldId = dto.Status.SessionId!;
        var result = await StartNewAsync(tenant, dto, stampOrigin, ct, replacing: oldId).ConfigureAwait(false);
        if (result.Status != 200)
        {
            FileLog.Write($"[FleetManagerPlacementService] replace: the new Fleet Manager did not start; {oldId} stays running and marked");
            return result;
        }

        FileLog.Write($"[FleetManagerPlacementService] replace: {result.Placement!.Status.SuccessorSessionId} waits; " +
                      $"{oldId} stays marked until its turn has ended and it has closed");
        StartRetirement(tenant);
        return result;
    }

    /// <param name="replacing">The marked Fleet Manager this one replaces, or null for a plain start. A replacement is
    /// recorded as the successor and NOT marked; a plain start is marked at once.</param>
    private async Task<FleetManagerPlacementResult> StartNewAsync(TenantId tenant, FleetManagerPlacementDto dto,
        Action<NewSessionRequest> stampOrigin, CancellationToken ct, string? replacing = null)
    {
        if (dto.Machine is null || dto.Agent is null)
            return Refuse(409, "This account has no computer for the Fleet Manager to run on, so it was not started.");

        var placement = dto.Machines.First(m => FleetManagerPlacementFold.SameMachine(m.Machine, dto.Machine));
        if (!placement.Selectable)
            return Refuse(409, $"{placement.Machine} cannot be reached now, so the Fleet Manager was not started. It never "
                               + "starts anywhere else: choose another computer and save, or start it when that one is back.");

        var facts = _env.Machines(tenant).First(m => FleetManagerPlacementFold.SameMachine(m.Machine, dto.Machine));
        var running = FleetManagerPlacementFold.RunningDirector(facts, _env.NowUtc());

        var request = BuildStartRequest(dto.Agent, replacing is not null);
        stampOrigin(request);
        // Pinned to the Director the page called running, so the capability that was checked is the one that
        // takes the create. With none running, the launcher starts one and the check runs on that one.
        request.Director = running?.DirectorId;

        // A replacement says it is starting BEFORE the start, so a Gateway that stops between the start and the save
        // below leaves a trace the sweep follows (ResumePendingAsync); the save below removes it.
        if (replacing is not null) _settings.SetFleetManagerReplacementStarting(tenant, _env.NowUtc());

        var (ok, session, error, directorId) = await _env.SpawnAsync(tenant, placement.Machine, request,
            id => _env.CreatesFleetManagerHome(tenant, id)
                ? null
                : $"The Director on {placement.Machine} is older than the Fleet Manager and must be updated before the "
                  + "Fleet Manager can run there. Update DevThrottle on that computer, then start it again.",
            ct).ConfigureAwait(false);
        if (!ok || session is null)
        {
            if (replacing is not null) _settings.ClearFleetManagerReplacementStarting(tenant);
            FileLog.Write($"[FleetManagerPlacementService] start FAILED on {placement.Machine} (director={directorId}): {error}");
            return Refuse(502, $"The Fleet Manager could not be started on {placement.Machine}: {error}");
        }

        var startedAt = _env.NowUtc();
        // Saved only now that it has started: a failed start leaves the setting as it was. A default is written here
        // too, because the owner started from it.
        _settings.SetFleetManagerPlacement(tenant, dto.Agent, placement.Machine, startedAt);
        if (replacing is null)
        {
            _settings.SetFleetManagerSessionId(tenant, session.SessionId, startedAt);
            _env.RecordMark(tenant, _settings.FleetManagerSessionId(tenant)!, startedAt);
            FileLog.Write($"[FleetManagerPlacementService] started Fleet Manager {session.SessionId} ({dto.Agent}) on " +
                          $"{placement.Machine}, director={directorId}; marked as the account's Fleet Manager");
        }
        else
        {
            // Recorded inside the delivery gate: a delivery to the old one either finished before this, or sees the
            // successor and types nothing.
            using (await _deliveryGate.EnterAsync(tenant, ct).ConfigureAwait(false))
                _settings.SetFleetManagerSuccessor(tenant, session.SessionId, replacing, startedAt);
            FileLog.Write($"[FleetManagerPlacementService] started Fleet Manager {session.SessionId} ({dto.Agent}) on " +
                          $"{placement.Machine}, director={directorId}; it waits to take over from {replacing}, which stays marked");
        }

        if (string.IsNullOrEmpty(session.MachineName)) session.MachineName = placement.Machine;
        if (string.IsNullOrEmpty(session.Agent)) session.Agent = dto.Agent;
        var (_, refreshed) = await FoldAsync(tenant, session, ct).ConfigureAwait(false);
        return FleetManagerPlacementResult.Ok(refreshed);
    }

    /// <summary>The first prompt of a Fleet Manager started to REPLACE a running one: wait for the mark. The Gateway
    /// refuses it the Fleet Manager's commands until then, and tells it with one event when the mark moves.</summary>
    public const string WaitForMarkPrompt =
        "You will be this account's Fleet Manager. The Fleet Manager running now keeps that role until it has finished its "
        + "current turn and closed, and until then the Gateway refuses you the Fleet Manager's commands. Do nothing now. "
        + "A prompt that starts with [Fleet Manager events] will tell you when you are the Fleet Manager and what to do.";

    /// <summary>The create request every Fleet Manager start sends. The origin is stated as a person's action from
    /// the Cockpit; the route overwrites it from the verified credential the way both spawn doors do.</summary>
    /// <param name="waitForMark">True for a replacement, which is told to wait for the mark instead of starting work.</param>
    public static NewSessionRequest BuildStartRequest(string agent, bool waitForMark = false) => new()
    {
        RepoPath = "",
        FleetManagerHome = true,
        Name = SessionName,
        Agent = agent,
        PrePrompt = waitForMark ? WaitForMarkPrompt : FirstPrompt,
        ControllerSessionId = null,
        Origin = SessionOriginKinds.Human,
        OriginSurface = SessionOriginSurfaces.Cockpit,
    };

    /// <summary>Carry on a replacement a previous Gateway process left under way, and finish what an interrupted one
    /// left behind. Called for every account by the sweep; does nothing when there is nothing to do.</summary>
    public async Task ResumePendingAsync(TenantId tenant)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (Successor(tenant) is not null)
        {
            StartRetirement(tenant);
            return;
        }
        if (_settings.FleetManagerReplacementStartingAt(tenant) is null && !MarkNamesAWaitingSession(tenant)) return;

        var gate = Gate(tenant);
        await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            // Re-read under the gate: a start or an owner's mark may have finished meanwhile.
            if (Successor(tenant) is not null) return;
            FindInterruptedStart(tenant);
            TellMarkedWaitingSession(tenant);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool MarkNamesAWaitingSession(TenantId tenant)
    {
        var mark = Clean(_settings.FleetManagerSessionId(tenant));
        return mark is not null && _settings.FleetManagerWaitingSuccessors(tenant).Any(w => SameId(w, mark));
    }

    /// <summary>
    /// A replacement whose Gateway stopped between starting the new Fleet Manager and recording it: remember every
    /// session named as a Fleet Manager that started since, is not marked and is not over, as waiting - so the owner
    /// marking it tells it. Nothing is closed and nothing is marked on this guess. Looked for until
    /// <see cref="InterruptedStartWindow"/> has passed, because the Director may report the session late.
    /// </summary>
    private void FindInterruptedStart(TenantId tenant)
    {
        if (_settings.FleetManagerReplacementStartingAt(tenant) is not { } since) return;
        var mark = Clean(_settings.FleetManagerSessionId(tenant));
        var waiting = _settings.FleetManagerWaitingSuccessors(tenant);
        var found = _env.Roster(tenant)
            .Select(r => r.Session)
            .Where(x => string.Equals(x.Name, SessionName, StringComparison.Ordinal)
                        && x.CreatedAt.ToUniversalTime() >= since
                        && !FleetManagerSessions.IsGone(x)
                        && !SameId(x.SessionId, mark)
                        && !waiting.Any(w => SameId(w, x.SessionId)))
            .Select(x => x.SessionId)
            .ToList();
        var now = _env.NowUtc();
        if (found.Count > 0)
        {
            _settings.AddFleetManagerWaitingSuccessors(tenant, found, now);
            FileLog.Write($"[FleetManagerPlacementService] interrupted start: {string.Join(", ", found)} started to take over "
                          + "and was never recorded; remembered as waiting, nothing closed and nothing marked");
        }
        if (now - since >= InterruptedStartWindow)
        {
            _settings.ClearFleetManagerReplacementStarting(tenant);
            FileLog.Write($"[FleetManagerPlacementService] interrupted start of {since:O}: stopped looking");
        }
    }

    /// <summary>The mark names a session that is still waiting to be told (with no replacement under way): tell it
    /// once. The promotion stores one event per session, so this never repeats one.</summary>
    private void TellMarkedWaitingSession(TenantId tenant)
    {
        if (!MarkNamesAWaitingSession(tenant)) return;
        var mark = Clean(_settings.FleetManagerSessionId(tenant))!;
        Promote(tenant, mark, $"the mark names {mark}, which was started to take over and was never told");
    }

    private void StartRetirement(TenantId tenant)
    {
        if (!_retiring.TryAdd(tenant, 0)) return;
        Track(Task.Run(async () =>
        {
            try { await RetireAfterTurnAsync(tenant).ConfigureAwait(false); }
            finally { _retiring.TryRemove(tenant, out _); }
        }));
    }

    /// <summary>
    /// Watch the marked Fleet Manager until the replacement can finish: close it once its Director reports it Idle,
    /// then move the mark to the successor. It is looked at every few seconds and never force-closed. It waits as long
    /// as it takes - the old one stays the Fleet Manager meanwhile - and the status says what it waits for.
    /// </summary>
    internal async Task RetireAfterTurnAsync(TenantId tenant)
    {
        var loggedWait = "";
        var idleSeen = new IdleSighting();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var step = await AdvanceReplacementAsync(tenant, idleSeen).ConfigureAwait(false);
                if (step.Done) return;
                if (step.Waiting != loggedWait)
                {
                    FileLog.Write($"[FleetManagerPlacementService] replacement waits: {step.Waiting}");
                    loggedWait = step.Waiting;
                }
                await _env.DelayAsync(_retirePoll, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            FileLog.Write("[FleetManagerPlacementService] retire: the Gateway is shutting down; the replacement stays recorded and carries on after the restart");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerPlacementService] retire FAILED: {ex.GetType().Name}: {ex.Message}; the replacement stays recorded");
        }
    }

    /// <summary>The Idle look the loop is waiting to confirm: which session, at which delivery generation.</summary>
    private sealed class IdleSighting
    {
        public string? SessionId;
        public long Generation;
    }

    /// <summary>One look at a replacement under way. Done when it finished or was abandoned; otherwise what it waits for.</summary>
    private async Task<(bool Done, string Waiting)> AdvanceReplacementAsync(TenantId tenant, IdleSighting idleSeen)
    {
        var gate = Gate(tenant);
        await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            // Held from the read of the old one's state to the close: no event can be typed into it in between.
            using var turn = await _deliveryGate.EnterAsync(tenant, _shutdown.Token).ConfigureAwait(false);

            var successorId = Successor(tenant);
            if (successorId is null) return (true, "");
            var replaces = Clean(_settings.FleetManagerSuccessorReplaces(tenant));
            var oldId = Clean(_settings.FleetManagerSessionId(tenant));
            if (replaces is null)
                return Abandon(tenant, $"no record says which Fleet Manager {successorId} was to replace, so nothing is closed; "
                                       + $"the mark ({oldId ?? "none"}) is left alone");

            // THE MARK IS COMPARED WITH THE RECORDED OLD ONE FIRST. Only the mark the replacement started from, or no
            // mark because the Gateway itself removed that one, lets it go on; every other mark is the owner's.
            if (oldId is null)
            {
                var cleared = _settings.FleetManagerMarkClearedByGateway(tenant);
                if (cleared is not { } byGateway || !SameId(byGateway.SessionId, replaces))
                    return Abandon(tenant, $"the mark on {replaces} was removed by hand while {successorId} waited; "
                                           + "nobody is marked by the replacement and nothing is closed");
                if (IsGone(Find(_env.Roster(tenant), successorId).Session))
                    return Abandon(tenant, $"the new Fleet Manager {successorId} ended before it took over; nobody is marked");
                return Promote(tenant, successorId, $"the Gateway unmarked {replaces} ({byGateway.Reason})");
            }
            if (SameId(oldId, successorId))
            {
                // The owner marked the successor by hand. The replacement closes nothing; the waiting session is told once.
                FileLog.Write($"[FleetManagerPlacementService] replacement ABANDONED: the mark was set by hand to {successorId}, "
                              + $"the session waiting to take over; {replaces} is left alone");
                return Promote(tenant, successorId, $"the owner marked {successorId}, which was waiting to be told");
            }
            if (!SameId(replaces, oldId))
                return Abandon(tenant, $"the mark was changed by hand from {replaces} to {oldId} while it waited; {successorId} is "
                                       + $"not marked and {oldId} is left alone");

            var roster = _env.Roster(tenant);
            if (IsGone(Find(roster, successorId).Session))
                return Abandon(tenant, $"the new Fleet Manager {successorId} ended before it took over; {oldId} stays the Fleet Manager");

            var old = Find(roster, oldId);
            if (old.Session is null)
                return Wait(idleSeen, $"{oldId} is not in this account's current roster (its Director is not reporting); it is not closed and stays marked");
            if (FleetManagerSessions.IsGone(old.Session))
            {
                _settings.ClearFleetManagerMarkByGateway(tenant, oldId, TenantSettingsResolver.MarkClearedExited, _env.NowUtc());
                return Promote(tenant, successorId, $"{oldId} has ended");
            }
            if (!FleetManagerRetirement.MayClose(old.Session))
                return Wait(idleSeen, $"{oldId} is {old.Session.ActivityState}; it is closed only when its Director reports it Idle");

            // IDLE ON TWO LOOKS, WITH NO DELIVERY BETWEEN THEM. A prompt typed just before the successor was recorded
            // may not have reached the pushed state yet; the second look sees its turn, or sees it ended.
            var generation = _deliveryGate.Generation(tenant, oldId);
            if (!SameId(idleSeen.SessionId, oldId) || idleSeen.Generation != generation)
            {
                idleSeen.SessionId = oldId;
                idleSeen.Generation = generation;
                return (false, $"{oldId} is Idle; it is closed if it is still Idle, with nothing typed into it, at the next look");
            }

            var closed = await _env.CloseSessionAsync(tenant, old.DirectorId, oldId, RetireReason, _shutdown.Token).ConfigureAwait(false);
            if (!closed)
                return (false, $"the close of {oldId} did not go through; trying again");
            _settings.ClearFleetManagerMarkByGateway(tenant, oldId, TenantSettingsResolver.MarkClearedClosed, _env.NowUtc());
            return Promote(tenant, successorId, $"{oldId} was closed after its turn ended");
        }
        finally
        {
            gate.Release();
        }
    }

    private static (bool Done, string Waiting) Wait(IdleSighting idleSeen, string why)
    {
        idleSeen.SessionId = null;
        return (false, why);
    }

    private (bool Done, string Waiting) Abandon(TenantId tenant, string why)
    {
        _settings.ClearFleetManagerSuccessor(tenant, _env.NowUtc());
        FileLog.Write($"[FleetManagerPlacementService] replacement ABANDONED: {why}");
        return (true, "");
    }

    /// <summary>Move the mark to the successor, record it, forget the replacement and tell the successor with one
    /// event - one transaction. A failure writes nothing, and the next look tries again.</summary>
    private (bool Done, string Waiting) Promote(TenantId tenant, string successorId, string why)
    {
        try
        {
            _env.PromoteSuccessor(tenant, successorId, _env.NowUtc());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementService] promotion of {successorId} FAILED ({why}): {ex.GetType().Name}: {ex.Message}; "
                          + "nothing was written and the next look tries again");
            return (false, $"moving the mark to {successorId} failed; trying again");
        }
        FileLog.Write($"[FleetManagerPlacementService] replacement DONE: {why}; {successorId} is now the marked Fleet Manager and is told so");
        return (true, "");
    }

    private static bool IsGone(SessionDto? session) => session is not null && FleetManagerSessions.IsGone(session);

    private static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    private string? Successor(TenantId tenant)
    {
        var id = _settings.FleetManagerSuccessorSessionId(tenant);
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    private static FleetManagerPlacementResult ReplacementUnderWay(string successorId)
        => Refuse(409, $"A restart or a move is already under way: the new Fleet Manager (session {successorId}) takes over "
                       + "once the one running now has finished its turn and closed. Wait for that, then try again.");

    private static (string DirectorId, SessionDto? Session) Find(IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string id)
    {
        var hit = roster.FirstOrDefault(r => SameId(r.Session.SessionId, id));
        return (hit.DirectorId, hit.Session);
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// WHEN THE OLD FLEET MANAGER MAY BE CLOSED (steps 5 and 6 fixes, the Architect's ruling): only when its Director
/// reports it Idle. Working is mid-turn. WaitingForInput and WaitingForPerm are the states that mean "needs you" - a
/// question or a permission prompt is not landed work - so they are never closed; the status says the old Fleet
/// Manager is waiting for the owner.
/// </summary>
internal static class FleetManagerRetirement
{
    public static bool MayClose(SessionDto s)
        => !FleetManagerSessions.IsGone(s) && string.Equals(s.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase);

    public static bool WaitsForOwner(SessionDto s)
        => !FleetManagerSessions.IsGone(s)
           && (string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
               || string.Equals(s.ActivityState, "WaitingForPerm", StringComparison.OrdinalIgnoreCase));
}
