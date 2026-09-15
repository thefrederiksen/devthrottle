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
/// </summary>
internal static class TurnVerdictJudge
{
    /// <summary>The model role the judge runs on.</summary>
    public const WingmanModelRole Role = WingmanModelRole.Fast;

    /// <summary>A hosted brain bound to the settings' judge timeout.</summary>
    public static HostedInferenceBrain BuildBrain(string baseUrl, string apiKey, IncludedModelId model, TurnVerdictSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HostedInferenceBrain(baseUrl, apiKey, model, log: FileLog.Write,
            callTimeout: TimeSpan.FromSeconds(settings.JudgeTimeoutSeconds));
    }
}

/// <summary>
/// The held check, as a pure function over a roster: resolve every role across the WHOLE roster, then read the
/// one session's answer. The roster arrives with every role and liveness answer nulled by the push store, so
/// asking the row first would be blind - see <see cref="VoiceModeAllSweep"/> for the same rule on the sweep.
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
    private readonly Func<TenantId, SpokenLanguage> _language;
    private readonly Func<string?> _customSpokenRules;
    private readonly Func<TenantId, string, bool> _isVoiceSession;
    private readonly ActivityEventStore? _ledger;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="judgeBrain">Builds the judge's brain for an account. Production passes a builder that goes
    /// through <see cref="TurnVerdictJudge.BuildBrain"/>, so the brain carries the settings' timeout.</param>
    /// <param name="customSpokenRules">The account's own narration instructions, or null when it uses the
    /// shipped default.</param>
    public GatewayTurnVerdictEnvironment(
        Func<TenantId, TurnVerdictSettings> settings,
        PushedSessionStore pushedSessions,
        TimeSpan streamStale,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, StoredConversation?> conversation,
        Func<TenantId, TurnVerdictSettings, IAgentBrain> judgeBrain,
        Func<TenantId, string> judgeModel,
        TurnVerdictStore store,
        Func<TenantId, SpokenLanguage> language,
        Func<string?> customSpokenRules,
        Func<TenantId, string, bool> isVoiceSession,
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
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _customSpokenRules = customSpokenRules ?? throw new ArgumentNullException(nameof(customSpokenRules));
        _isVoiceSession = isVoiceSession ?? throw new ArgumentNullException(nameof(isVoiceSession));
        _ledger = ledger;
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public TurnVerdictSettings Settings(TenantId tenant) => _settings(tenant);

    public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId)
        => TurnVerdictHeldCheck.Resolve(_pushedSessions.SnapshotFresh(tenant, _streamStale), sessionId);

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

    public async Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, CancellationToken ct)
    {
        var settings = _settings(tenant);
        var model = _judgeModel(tenant);
        using var brain = _judgeBrain(tenant, settings);
        var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(result.Text ?? "", model, result.ReplySeconds);
    }

    public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => _store.Latest(tenant, sessionId);

    public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);

    public void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict) => _store.Store(tenant, sessionId, verdict);

    public int Invalidate(TenantId tenant, string sessionId) => _store.Invalidate(tenant, sessionId);

    public bool IsVoiceSession(TenantId tenant, string sessionId) => _isVoiceSession(tenant, sessionId);

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
            _ledger.AppendBatch(new[]
            {
                new ActivityEventRecord
                {
                    EventId = Guid.NewGuid(),
                    DirectorSequence = 0,
                    OccurredUtc = _nowUtc(),
                    DirectorId = string.IsNullOrWhiteSpace(record.DirectorId) ? "gateway" : record.DirectorId,
                    SessionId = record.SessionId,
                    EventType = record.EventType,
                    Cause = record.Cause,
                    Detail = record.Detail,
                },
            });
        }
        catch (Exception ex)
        {
            // The ledger OBSERVES the seat: an append fault is logged loudly and never changes a verdict.
            FileLog.Write($"[GatewayTurnVerdictEnvironment] ledger append FAILED for {record.EventType} sid={record.SessionId}: {ex.Message}");
        }
    }

    public DateTime NowUtc() => _nowUtc();
}
