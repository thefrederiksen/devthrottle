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
/// THE ROLE IS FAST. The slice 0 grading decided the judge is the included fast tier
/// (<c>devthrottle/wingman-fast</c>): the thinking tier left roughly half of its calls unanswered at the
/// product's own ceiling of eight in flight.
///
/// IT WAS MOVED TO THE THINKING TIER WITH REASONING OFF ON 2026-09-20, AND MOVED BACK THE SAME EVENING,
/// BECAUSE IT BROKE EVERY READING IN PRODUCTION. Read this before proposing it again - the idea is sound
/// and the evidence for it was not.
///
/// What the measurement said (eleven models, 85 screens whose picker truth is known, 381 labelled corpus
/// stops, devthrottle_internal <c>docs/missions/wingman-picker-flag-2026-09-19/</c>):
/// <c>devthrottle/wingman</c> with reasoning off read 85 of 85 pickers against the fast tier's 72, invented
/// none against 13, agreed with the human label 79.6% of the time against 75.1%, and answered with a MEDIAN
/// OF 68 OUTPUT TOKENS IN 2.0 SECONDS, with no call past the twenty-second bound in 381.
///
/// What production did, within four minutes of the deploy: every reading failed, in two shapes - the judge
/// not answering inside its thirty-second deadline, and an answer that was valid JSON followed by more text,
/// which the contract refuses. Re-asked afterwards through the same endpoint with the same four fields on a
/// full-size v3 prompt, the model took 46 SECONDS AND WROTE 3,397 OUTPUT TOKENS, with the provider still
/// reporting no reasoning tokens.
///
/// THE LESSON IS ABOUT THE INSTRUMENT, NOT THE MODEL. Fifty times the output and twenty times the latency,
/// on the same model, the same prompt file, the same endpoint and the same argument, means the corpus
/// packages the measurement asked about were not like the packages a live session produces - and the
/// harness also capped output at <c>max_tokens 4000</c> where the product sends no cap at all. So the
/// measurement was answering an easier question than production asks. Anyone returning to this must first
/// make the measurement reproduce a LIVE stop - same package sizes, no output cap - and only then compare
/// models. The accuracy numbers above are not thereby disproved; they are simply not evidence about speed
/// or output length on real traffic, which is what took the Wingman down.
///
/// AND IT WOULD TAKE THE NARRATION WITH IT: <see cref="GatewayTurnVerdictEnvironment.AskNarratorAsync"/>
/// builds from this same brain, so a change to this role silently changes every spoken word too.
/// </summary>
internal static class TurnVerdictJudge
{
    /// <summary>The model role the judge runs on. See the type comment before changing it.</summary>
    public const WingmanModelRole Role = WingmanModelRole.Fast;

    /// <summary>
    /// A hosted brain bound to the settings' judge timeout.
    ///
    /// IT ASKS FOR NO CHANGE TO THE MODEL'S REASONING. <see cref="HostedInferenceBrain"/> can turn reasoning
    /// off and defaults to leaving it alone; this builder takes the default, so the judge's request body is
    /// exactly what it was before 2026-09-20. A test asserts that an ordinary brain sends no such key at all.
    /// </summary>
    /// <param name="tag">The judge's tag: <see cref="Core.HostedAi.AiFeature.TurnVerdict"/> and the account the
    /// turn belongs to. Required - the judge runs on every turn end and is the largest single use of the model.</param>
    public static HostedInferenceBrain BuildBrain(string baseUrl, string apiKey, IncludedModelId model, TurnVerdictSettings settings, Core.HostedAi.AiCallTag tag)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tag);
        return new HostedInferenceBrain(baseUrl, apiKey, model, log: FileLog.Write,
            callTimeout: TimeSpan.FromSeconds(settings.JudgeTimeoutSeconds), tag: tag);
    }

    /// <summary>The most output Call A may write: one word, with room for a stray quote or full stop. The phase 1
    /// measurement ran with this cap and averaged two output tokens a call.</summary>
    public const int CallAMaxTokens = 16;

    /// <summary>
    /// CALL A'S BRAIN (contract v4): the same model, timeout and tag as <see cref="BuildBrain"/>, asked at
    /// TEMPERATURE 0, with its reasoning OFF and its output capped at <see cref="CallAMaxTokens"/> - exactly the
    /// request the phase 1 measurement sent (devthrottle_internal docs/missions/turn-pipeline-2026-09-25/phase-1/
    /// MEASUREMENT.md, "Temperature 0, proved from the request body").
    ///
    /// WHY THIS IS NOT THE 2026-09-20 OUTAGE AGAIN (the type comment above). That was the THINKING tier asked for a
    /// five-field JSON object with no output cap, which wrote 3,397 tokens in 46 seconds. This is the fast tier, the
    /// one the judge already runs on, asked for one word with a sixteen-token cap: the answer cannot run long.
    ///
    /// ONLY CALL A. The narration call writes a paragraph and keeps <see cref="BuildBrain"/> unchanged - a cap of
    /// sixteen tokens there would cut every narration to a few words.
    /// </summary>
    public static HostedInferenceBrain BuildCallABrain(string baseUrl, string apiKey, IncludedModelId model, TurnVerdictSettings settings, Core.HostedAi.AiCallTag tag)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tag);
        return new HostedInferenceBrain(baseUrl, apiKey, model, log: FileLog.Write,
            callTimeout: TimeSpan.FromSeconds(settings.JudgeTimeoutSeconds), thinkingOff: true, tag: tag,
            temperature: 0, maxTokens: CallAMaxTokens);
    }
}

/// <summary>
/// The held check, as a pure function over a roster: resolve every role across the WHOLE roster, then read the
/// one session's answer. The roster arrives with every role and liveness answer nulled by the push store, so
/// asking the row first would be blind - see <see cref="VoiceModeAllSweep"/> for the same rule on the sweep.
///
/// ONE ANSWER (owner ruling, 2026-09-25). <see cref="TurnVerdictSessionState.Held"/> is "a live owning session holds
/// this one", and every automatic request - the verdict, the narration and the speech - stands down for it, whoever
/// the owner is. The Fleet Manager's own sessions are not an exception: it is a coding agent and reads them itself.
///
/// GAP, STATED: THE UNIVERSE IS THE FRESH ROSTER. An owning session whose stream has gone quiet past the freshness
/// horizon is absent from it, so a session it owns resolves as not held and is judged. That fails toward the owner
/// (the session is read), which is the direction the fold itself fails in.
/// </summary>
internal static class TurnVerdictHeldCheck
{
    /// <summary>The one session's facts and held answer, both out of this one roster. The roster is the push
    /// store's deep copy, so stamping roles on it touches nothing the store holds.</summary>
    public static TurnVerdictSessionState Resolve(IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var sessions = roster.Select(r => r.Session).Where(s => s is not null).ToList();
        FleetRoleResolver.Stamp(sessions);
        var session = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        return new TurnVerdictSessionState(session, session?.HasLiveSupervisor == true);
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
    private readonly Func<TenantId, TurnVerdictSettings, string, bool, IAgentBrain> _judgeBrain;
    private readonly Func<TenantId, string> _judgeModel;
    private readonly TurnVerdictStore _store;
    private readonly TurnVerdictTraceWriter _traces;
    private readonly Func<TenantId, SpokenLanguage> _language;
    private readonly Func<string?> _customSpokenRules;
    private readonly Func<TenantId, string, bool> _isVoiceSession;
    private readonly Func<TenantId, NarrationPlan> _narrationPlan;
    private readonly ActivityEventStore? _ledger;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="judgeBrain">Builds the judge's brain for an account, a usage feature, and whether it is CALL A's
    /// one-word question (true) or a call that writes prose or probes the host (false). Production passes a builder
    /// that goes through <see cref="TurnVerdictJudge.BuildCallABrain"/> for Call A and
    /// <see cref="TurnVerdictJudge.BuildBrain"/> otherwise, so every brain carries the settings' timeout and the call
    /// site receives its own usage tag.</param>
    /// <param name="customSpokenRules">The account's own narration instructions, or null when it uses the
    /// shipped default.</param>
    public GatewayTurnVerdictEnvironment(
        Func<TenantId, TurnVerdictSettings> settings,
        PushedSessionStore pushedSessions,
        TimeSpan streamStale,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, StoredConversation?> conversation,
        Func<TenantId, TurnVerdictSettings, string, bool, IAgentBrain> judgeBrain,
        Func<TenantId, string> judgeModel,
        TurnVerdictStore store,
        TurnVerdictTraceWriter traces,
        Func<TenantId, SpokenLanguage> language,
        Func<string?> customSpokenRules,
        Func<TenantId, string, bool> isVoiceSession,
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
        _narrationPlan = narrationPlan ?? throw new ArgumentNullException(nameof(narrationPlan));
        _ledger = ledger;
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public TurnVerdictSettings Settings(TenantId tenant) => _settings(tenant);

    public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId)
        => TurnVerdictHeldCheck.Resolve(
            _pushedSessions.SnapshotFresh(tenant, _streamStale), sessionId);

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
        using var brain = _judgeBrain(tenant, settings, Core.HostedAi.AiFeature.TurnVerdict, true);
        var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(result.Text ?? "", model, result.ReplySeconds);
    }

    public async Task<TurnVerdictJudgeAnswer> AskRecoveryProbeAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var settings = _settings(tenant) with { JudgeTimeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds) };
        var model = _judgeModel(tenant);
        using var brain = _judgeBrain(tenant, settings, Core.HostedAi.AiFeature.TurnVerdictProbe, false);
        var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(result.Text ?? "", model, result.ReplySeconds);
    }

    public async Task<TurnVerdictJudgeAnswer> AskNarratorAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        // The same model and the same builder as the judge, with the narration call's own deadline: slice I measured
        // the old translator's prompt on this model, and a second provider would be a second thing to keep working.
        var settings = _settings(tenant) with { JudgeTimeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds) };
        var model = _judgeModel(tenant);
        using var brain = _judgeBrain(tenant, settings, Core.HostedAi.AiFeature.TurnVerdict, false);
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
