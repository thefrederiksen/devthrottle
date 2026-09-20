using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The turn-verdict judge's brain, built in ONE place so the timeout it carries is the one the settings say.
///
/// THE TIMEOUT IS CONSUMED HERE. <see cref="TurnVerdictSettings.JudgeTimeoutSeconds"/> is thirty seconds by
/// the slice 0 ruling; <see cref="HostedInferenceBrain"/>'s own default is sixty. A brain built anywhere else
/// would silently carry sixty, so the host builds the judge through this method and a test pins the brain it
/// returns to the settings' value.
///
/// THE ROLE IS THINKING, WITH THE THINKING TURNED OFF - and the second half of that sentence is the whole
/// point. The slice 0 grading put the judge on the fast tier (<c>devthrottle/wingman-fast</c>) because the
/// thinking tier left roughly half of its calls unanswered at the product's own ceiling of eight in flight.
/// That was true, and it was true OF THE REASONING, not of the model: reasoning is what produced 4,290 output
/// tokens a call, a median reply of 144 seconds, and the unanswered half.
///
/// Measured on 2026-09-20 over eleven models, 85 screens whose picker truth is known, and the 381 labelled
/// corpus stops, on the SAME v3 prompt (devthrottle_internal
/// <c>docs/missions/wingman-picker-flag-2026-09-19/</c>):
///
///   <c>devthrottle/wingman</c>, thinking off : 85/85 pickers, 0 invented, 79.6% agreement, p50 2.0s, p95 4.5s,
///                                              0 of 381 calls past the 20-second bound, about $20 a month
///   <c>devthrottle/wingman-fast</c> (before)  : 72/85 pickers, 13 invented, 75.1% agreement, p50 6.1s, p95 14.9s
///
/// So this is not the old thinking tier coming back. Turning the reasoning off makes the stronger model both
/// FASTER and more accurate than the fast tier it replaces, which is why the slice 0 reasoning no longer holds.
/// Putting it back on this role without re-running that measurement restores every symptom slice 0 fled.
///
/// THE NARRATION CALL MOVES WITH IT, by construction: <see cref="GatewayTurnVerdictEnvironment.AskNarratorAsync"/>
/// builds from this same brain. Today's narration prompt was measured on this model before the switch shipped,
/// on slice J's own twenty stops against slice J's own ceiling: 17 of 20, against the 14 of 20 the shipped
/// narration call scores on the fast tier. It is slower there than the judge is - half the calls over 22
/// seconds, the slowest 52.6 - and the deadline it runs under is
/// <see cref="TurnVerdictSettings.NarrationCallTimeoutSeconds"/>, sixty. A narration that misses that deadline
/// costs the better wording, not the reading: the judge's own spoken text is stored and playable first.
/// </summary>
internal static class TurnVerdictJudge
{
    /// <summary>The model role the judge runs on. See the type comment before changing it.</summary>
    public const WingmanModelRole Role = WingmanModelRole.Thinking;

    /// <summary>
    /// A hosted brain bound to the settings' judge timeout, with the model's reasoning turned OFF.
    ///
    /// THIS IS THE ONLY CALLER THAT TURNS IT OFF, and that is deliberate rather than incidental: the argument
    /// defaults to off in <see cref="HostedInferenceBrain"/> so that every other brain in the product keeps the
    /// behaviour it has today, and a test pins both halves - that this builder sets it, and that the
    /// translator's brain does not.
    /// </summary>
    public static HostedInferenceBrain BuildBrain(string baseUrl, string apiKey, IncludedModelId model, TurnVerdictSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HostedInferenceBrain(baseUrl, apiKey, model, log: FileLog.Write,
            callTimeout: TimeSpan.FromSeconds(settings.JudgeTimeoutSeconds), thinkingOff: true);
    }
}

/// <summary>
/// The held check, as a pure function over a roster: resolve every role across the WHOLE roster, then read the
/// one session's answer. The roster arrives with every role and liveness answer nulled by the push store, so
/// asking the row first would be blind - see <see cref="VoiceModeAllSweep"/> for the same rule on the sweep.
///
/// TWO ANSWERS, NOT ONE (Fleet Manager ruling, 2026-09-16). <see cref="TurnVerdictSessionState.Held"/> is "a live
/// owning session holds this one", and it is what narration reads: a held session is never read aloud to the owner.
/// <see cref="TurnVerdictSessionState.OwnedByFleetManager"/> says that owner is the account's Fleet Manager, and then
/// the session is still JUDGED automatically and its verdict stored under its own session id, like any other - while
/// it stays held for narration and for the owner's colour. Nothing here carries that verdict to the Fleet Manager;
/// that is step 4 of the Fleet Manager mission and is not built yet. Only the DIRECT live controller counts: a Worker under an Architect the
/// Fleet Manager started is held by that Architect and is not read. The Fleet Manager is the one session the
/// ACCOUNT has marked (<see cref="FleetManagerSessions"/>); no workflow seat stands in for that mark.
///
/// GAP, STATED: THE UNIVERSE IS THE FRESH ROSTER. An owning session whose stream has gone quiet past the freshness
/// horizon is absent from it, so a session it owns resolves as not held and is judged. That fails toward the owner
/// (the session is read), which is the direction the fold itself fails in.
/// </summary>
internal static class TurnVerdictHeldCheck
{
    /// <summary>The one session's facts and held answer, both out of this one roster. The roster is the push
    /// store's deep copy, so stamping roles on it touches nothing the store holds.</summary>
    /// <param name="fleetManagerSessionId">The session this account has marked as its Fleet Manager, or null when
    /// it has marked none - then every held session is held for judging too.</param>
    public static TurnVerdictSessionState Resolve(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string sessionId, string? fleetManagerSessionId)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var sessions = roster.Select(r => r.Session).Where(s => s is not null).ToList();
        FleetRoleResolver.Stamp(sessions);
        var session = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        var held = session?.HasLiveSupervisor == true;
        // HasLiveSupervisor already proved the controller is in this roster and alive, so the controller found here
        // is the live one. Asked only for a held session: an unheld session has no owner to be read for.
        var ownedByFleetManager = held && FleetManagerSessions.IsFleetManager(sessions.FirstOrDefault(
            s => string.Equals(s.SessionId, session!.ControllerSessionId, StringComparison.Ordinal)), fleetManagerSessionId);
        if (ownedByFleetManager)
            FileLog.Write($"[TurnVerdictHeldCheck] Resolve: sid={sessionId} is held by Fleet Manager {session!.ControllerSessionId} - judged and stored, not narrated");
        return new TurnVerdictSessionState(session, held, ownedByFleetManager);
    }
}

/// <summary>
/// THE LEDGER ROW ONE VERDICT RECORD BECOMES, in one place. Extracted from the environment below so that a test
/// can drive the REAL <see cref="ActivityEventStore"/> with exactly what production writes - the store refuses an
/// event type or a cause outside the closed lists in the contracts, and that refusal is CAUGHT and logged on the
/// production path, so a missing word costs the durable row and looks like nothing at all. A test that builds its
/// own row instead of this one would not be testing the thing that broke.
/// </summary>
internal static class TurnVerdictLedgerRow
{
    public static ActivityEventRecord For(TurnVerdictRecord record, DateTime nowUtc) => new()
    {
        EventId = Guid.NewGuid(),
        DirectorSequence = 0,
        OccurredUtc = nowUtc,
        DirectorId = string.IsNullOrWhiteSpace(record.DirectorId) ? "gateway" : record.DirectorId,
        SessionId = record.SessionId,
        EventType = record.EventType,
        Cause = record.Cause,
        Detail = record.Detail,
    };
}

/// <summary>
/// The production wiring of <see cref="ITurnVerdictEnvironment"/>: every leg is machinery that already exists.
/// The roster and the facts are the push store's fresh snapshot, so nothing is dialled to ask whether a
/// session is alive; the screen is the tunnel read; the conversation is the Gateway's own store; the judge is a
/// hosted call through <see cref="TurnVerdictJudge"/>; the verdicts are <see cref="TurnVerdictStore"/>; and the
/// record is the durable activity ledger, inside the owning account's scope.
/// </summary>
internal sealed class GatewayTurnVerdictEnvironment : ITurnVerdictEnvironment
{
    private readonly Func<TenantId, TurnVerdictSettings> _settings;
    private readonly PushedSessionStore _pushedSessions;
    private readonly TimeSpan _streamStale;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<TenantId, string, StoredConversation?> _conversation;
    private readonly Func<TenantId, TurnVerdictSettings, IAgentBrain> _judgeBrain;
    private readonly Func<TenantId, string> _judgeModel;
    private readonly TurnVerdictStore _store;
    private readonly TurnVerdictTraceWriter _traces;
    private readonly Func<TenantId, SpokenLanguage> _language;
    private readonly Func<string?> _customSpokenRules;
    private readonly Func<TenantId, string, bool> _isVoiceSession;
    private readonly Func<TenantId, string?> _fleetManagerSessionId;
    private readonly Func<TenantId, NarrationPlan> _narrationPlan;
    private readonly ActivityEventStore? _ledger;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="judgeBrain">Builds the judge's brain for an account. Production passes a builder that goes
    /// through <see cref="TurnVerdictJudge.BuildBrain"/>, so the brain carries the settings' timeout.</param>
    /// <param name="customSpokenRules">The account's own narration instructions, or null when it uses the
    /// shipped default.</param>
    /// <param name="fleetManagerSessionId">The session an account has marked as its Fleet Manager, or null. Production
    /// reads the account's <c>fleet_manager_session_id</c> setting.</param>
    public GatewayTurnVerdictEnvironment(
        Func<TenantId, TurnVerdictSettings> settings,
        PushedSessionStore pushedSessions,
        TimeSpan streamStale,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, StoredConversation?> conversation,
        Func<TenantId, TurnVerdictSettings, IAgentBrain> judgeBrain,
        Func<TenantId, string> judgeModel,
        TurnVerdictStore store,
        TurnVerdictTraceWriter traces,
        Func<TenantId, SpokenLanguage> language,
        Func<string?> customSpokenRules,
        Func<TenantId, string, bool> isVoiceSession,
        Func<TenantId, string?> fleetManagerSessionId,
        Func<TenantId, NarrationPlan> narrationPlan,
        ActivityEventStore? ledger = null,
        Func<TenantId, IDisposable>? enterTenantScope = null,
        Func<DateTime>? nowUtc = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _pushedSessions = pushedSessions ?? throw new ArgumentNullException(nameof(pushedSessions));
        _streamStale = streamStale;
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _judgeBrain = judgeBrain ?? throw new ArgumentNullException(nameof(judgeBrain));
        _judgeModel = judgeModel ?? throw new ArgumentNullException(nameof(judgeModel));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _customSpokenRules = customSpokenRules ?? throw new ArgumentNullException(nameof(customSpokenRules));
        _isVoiceSession = isVoiceSession ?? throw new ArgumentNullException(nameof(isVoiceSession));
        _fleetManagerSessionId = fleetManagerSessionId ?? throw new ArgumentNullException(nameof(fleetManagerSessionId));
        _narrationPlan = narrationPlan ?? throw new ArgumentNullException(nameof(narrationPlan));
        _ledger = ledger;
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public TurnVerdictSettings Settings(TenantId tenant) => _settings(tenant);

    public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId)
        => TurnVerdictHeldCheck.Resolve(
            _pushedSessions.SnapshotFresh(tenant, _streamStale), sessionId, _fleetManagerSessionId(tenant));

    public async Task<ScreenGridResponse?> ReadScreenGridAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var owner = string.IsNullOrWhiteSpace(directorId)
            ? _pushedSessions.TryLocate(tenant, sessionId, _streamStale)?.DirectorId
            : directorId;
        if (string.IsNullOrWhiteSpace(owner)) return null;
        var route = _route(tenant, owner);
        if (route is null) return null;
        try
        {
            return await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[GatewayTurnVerdictEnvironment] screen read FAILED sid={sessionId}: {ex.Message}");
            return null;
        }
    }

    public StoredConversation? ReadConversation(TenantId tenant, string sessionId) => _conversation(tenant, sessionId);

    public SpokenLanguage Language(TenantId tenant) => _language(tenant);

    public string? CustomSpokenRules() => _customSpokenRules();

    public string JudgeModel(TenantId tenant) => _judgeModel(tenant);

    public async Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        // The brain is built with THIS call's deadline: the account's own for a first attempt, the wider one for
        // the single re-attempt a listened-to stop may get. Built through the same builder either way.
        var settings = _settings(tenant) with { JudgeTimeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds) };
        var model = _judgeModel(tenant);
        using var brain = _judgeBrain(tenant, settings);
        var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(result.Text ?? "", model, result.ReplySeconds);
    }

    public async Task<TurnVerdictJudgeAnswer> AskNarratorAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        // The same model and the same builder as the judge, with the narration call's own deadline: slice I measured
        // the old translator's prompt on this model, and a second provider would be a second thing to keep working.
        var settings = _settings(tenant) with { JudgeTimeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds) };
        var model = _judgeModel(tenant);
        using var brain = _judgeBrain(tenant, settings);
        var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(result.Text ?? "", model, result.ReplySeconds);
    }

    public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => _store.Latest(tenant, sessionId);

    public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);

    public OwnedSessionsFacts? OwnedSessions(TenantId tenant, string sessionId)
        => TurnVerdictOwnedSessions.For(_pushedSessions.SnapshotFresh(tenant, _streamStale), sessionId);

    public void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict) => _store.Store(tenant, sessionId, verdict);

    public int Invalidate(TenantId tenant, string sessionId) => _store.Invalidate(tenant, sessionId);

    public bool IsVoiceSession(TenantId tenant, string sessionId) => _isVoiceSession(tenant, sessionId);

    public NarrationPlan PlanForNarration(TenantId tenant) => _narrationPlan(tenant);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public void Record(TurnVerdictRecord record)
    {
        if (record is null) return;
        FileLog.Write($"[TurnVerdictService] {record.EventType} cause={record.Cause} sid={record.SessionId} " +
                      $"director={record.DirectorId} tenant={record.Tenant.ToLogString()}: {record.Detail}");
        if (_ledger is null) return;
        try
        {
            using var scope = _enterTenantScope?.Invoke(record.Tenant);
            _ledger.AppendBatch(new[] { TurnVerdictLedgerRow.For(record, _nowUtc()) });
        }
        catch (Exception ex)
        {
            // The ledger OBSERVES the seat: an append fault is logged loudly and never changes a verdict.
            FileLog.Write($"[GatewayTurnVerdictEnvironment] ledger append FAILED for {record.EventType} sid={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>Hands the trace to the writer: one non-blocking queue write that cannot throw, so the verdict path
    /// neither waits for the copy nor sees its faults. The writer logs and counts a drop or a failed write.</summary>
    public void RecordTrace(TenantId tenant, TurnVerdictTrace trace) => _traces.Enqueue(tenant, trace);

    /// <summary>A trace the seat could not hand in at all: logged and counted by the writer, with the other losses.</summary>
    public void TraceNotKept(TenantId tenant, TurnVerdictTrace trace, string cause) => _traces.NotKept(trace, cause);

    public DateTime NowUtc() => _nowUtc();
}
