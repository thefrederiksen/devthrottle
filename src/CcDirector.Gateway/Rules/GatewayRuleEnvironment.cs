using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The production wiring of <see cref="IRuleEnvironment"/> (Session Rules mission, phase 2): the
/// evaluator's reads, its ONE write into a session, and its record - each pointed at machinery that
/// already exists.
///
/// Nothing here is new plumbing, deliberately. The screen read is the same tunnel <c>screen-grid</c> the
/// supervisor and the voice cluster use; the session's facts are the pushed roster snapshot, so a session's
/// liveness is never established by dialing it; the send is the ordinary prompt verb, which is the route
/// already proven to carry a slash command into a session; the rules and the firings are the phase 1 store.
///
/// IT CANNOT PROMOTE A RULE, AND THAT IS A TYPE BOUND RATHER THAN A HABIT. It is handed
/// <see cref="IRuleReading"/>, which has no promotion on it, and <c>RulesPromotionBoundaryGuardTests</c>
/// asserts against the built assembly that nothing in this namespace holds the concrete store. Through
/// phase 2 it held the store itself and was one line away from being able to make a rule live.
///
/// THIS IS THE ONLY TYPE IN THE FEATURE THAT CAN TYPE, and that is asserted against the built assembly by
/// <c>RulesTypeNothingGuardTests</c>. The evaluator decides whether a rule is in dry run and simply never
/// calls <see cref="TypeIntoSessionAsync"/> when it is - so "dry run types nothing" holds because of the
/// shape of the code and not because of a branch somebody has to keep remembering.
///
/// TENANT SCOPE. The evaluator runs on a background task that outlives the turn-end callback, so every
/// operation touching per-tenant storage enters that tenant's scope explicitly rather than inheriting an
/// ambient one. A missing scope on a hosted Gateway would be a cross-partition read, not merely a wrong
/// answer.
/// </summary>
internal sealed class GatewayRuleEnvironment : IRuleEnvironment
{
    private readonly IRuleReading _store;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<TenantId, string, SessionDto?> _session;
    private readonly Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="store">The phase 1 rule store, seen through the NARROW seam: reading the rules,
    /// counting a rule's firings, writing one down. There is deliberately no promotion on it, so the
    /// evaluation path cannot move a rule out of dry run even by mistake (owner ruling 14, bound 6).</param>
    /// <param name="route">Resolves a tunnel caller for (tenant, director id); null means that Director is
    /// not connected, which every read treats as "cannot tell" rather than as a fault.</param>
    /// <param name="session">Reads a session's roster row from the pushed snapshot, or null when it is no
    /// longer there.</param>
    /// <param name="brainProvider">The model provider. The THINKING role is used deliberately: reading a
    /// screen against a standing instruction and deciding whether the instruction reaches it is a judgement,
    /// not a one-word classification, and it is the judgement that keeps a rule from acting on a screen that
    /// merely mentions the words.</param>
    /// <param name="enterTenantScope">Enters a tenant's storage scope for the duration of a read or write.
    /// Optional (self-host has one partition and the scope is inert).</param>
    /// <param name="nowUtc">The clock, as a seam.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public GatewayRuleEnvironment(
        IRuleReading store,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, SessionDto?> session,
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brainProvider,
        Func<TenantId, IDisposable>? enterTenantScope = null,
        Func<DateTime>? nowUtc = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _brainProvider = brainProvider ?? throw new ArgumentNullException(nameof(brainProvider));
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public DateTime NowUtc => _nowUtc();


    /// <inheritdoc />
    public IReadOnlyList<SessionRule> Rules(TenantId tenant)
    {
        using var scope = _enterTenantScope?.Invoke(tenant);
        return _store.All();
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId)
    {
        using var scope = _enterTenantScope?.Invoke(tenant);
        return _store.FiringsFor(ruleId);
    }

    /// <inheritdoc />
    public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId)
    {
        var session = _session(tenant, sessionId);
        if (session is null) return null;
        return new RuleSessionFacts(
            SessionId: sessionId,
            Agent: session.Agent ?? "",
            RepositoryPath: session.RepoPath ?? "",
            Machine: session.MachineName ?? "",
            Mission: session.MissionName ?? "",
            ActivityState: session.ActivityState ?? "");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null) return null;
        try
        {
            var grid = await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false);
            if (grid is null || !grid.HasGrid) return null;
            return grid.Rows;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayRuleEnvironment] screen read FAILED sid={sessionId}: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
    {
        try
        {
            using var brain = await _brainProvider(tenant, WingmanModelRole.Thinking, Core.HostedAi.AiFeature.Rules, ct).ConfigureAwait(false);
            var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
            return result?.Text;
        }
        catch (Exception ex)
        {
            // A model that cannot be asked leaves the screen unjudged, which the evaluator records as a
            // refusal. It never degrades into an assumption that the rule may act.
            FileLog.Write($"[GatewayRuleEnvironment] the agent could not be asked: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// THREE ANSWERS, BECAUSE TWO OF THEM WERE ONE AND THE RECORD WAS WRONG IN BOTH DIRECTIONS.
    ///
    /// The prompt verb answers "never started a turn ... parked in the composer unsubmitted" for any
    /// session whose turn is over in milliseconds - a plain shell, or an agent answering a picker - while
    /// the keystroke has in fact landed. Reading that as a failed send put a sentence into a firing record
    /// on 2 September 2026 that the session's own screen disproved. But the SAME false answer comes back
    /// when the Director refused the command, when the tunnel dropped, and when nothing answered at all,
    /// and in those the text did not land; calling all of them "typed" is the first mistake wearing the
    /// other coat. The layer below used to collapse an absent tunnel result and a remote refusal into one
    /// boolean, so this wiring could not have told them apart even if it had wanted to.
    ///
    /// So: no route, or a command that never left this Gateway, means NOTHING WAS TYPED. A Director that
    /// answered Ok is confirmed. Anything else is UNKNOWN, and the record says what was sent and that
    /// nothing confirmed it, which is the only true thing there is to say.
    /// </remarks>
    public async Task<RuleSendResult> TypeIntoSessionAsync(
        TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null)
        {
            FileLog.Write($"[GatewayRuleEnvironment] NOT typed sid={sessionId}: director {directorId} is not connected");
            return RuleSendResult.NotSent($"the machine running this session ({directorId}) is not connected.");
        }

        var request = new PromptRequest { Text = text, AppendEnter = true, WaitForIdle = false };
        var sent = await route.SendPromptAsync(sessionId, request, ct).ConfigureAwait(false);

        switch (sent.Kind)
        {
            case SessionVerbClient.PromptSendKind.Accepted:
                return RuleSendResult.Confirmed();

            case SessionVerbClient.PromptSendKind.NeverLeftTheGateway:
                FileLog.Write($"[GatewayRuleEnvironment] NOT typed sid={sessionId}: {sent.Detail}");
                return RuleSendResult.NotSent(Shorten(sent.Detail));

            // A definite Director refusal is recorded exactly as it was before the send kinds split it out of
            // Unanswered: UNKNOWN. Session Rules behave as they did; only dev reports act on the difference.
            case SessionVerbClient.PromptSendKind.DirectorRefused:
            case SessionVerbClient.PromptSendKind.Unanswered:
            default:
                FileLog.Write($"[GatewayRuleEnvironment] typing UNANSWERED sid={sessionId}: {sent.Detail}");
                return RuleSendResult.Unknown(Shorten(sent.Detail));
        }
    }

    /// <summary>The route's own words, kept short enough to read on a record.</summary>
    private static string Shorten(string? detail)
    {
        var text = (detail ?? "the route gave no reason").Trim().ReplaceLineEndings(" ");
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    /// <inheritdoc />
    public Guid RecordFiring(TenantId tenant, RuleFiringDraft draft)
    {
        using var scope = _enterTenantScope?.Invoke(tenant);
        return _store.RecordFiring(
            draft.RuleId,
            draft.SessionId,
            draft.ScreenText,
            draft.Understanding,
            draft.Decision,
            draft.Reason,
            draft.Runs,
            draft.TypedText,
            draft.Outcome,
            draft.Grounding,
            _nowUtc()).Id;
    }

    /// <inheritdoc />
    public void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome)
    {
        using var scope = _enterTenantScope?.Invoke(tenant);
        _store.CompleteFiring(firingId, typedText, outcome, _nowUtc());
    }
}
