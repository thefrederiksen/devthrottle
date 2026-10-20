using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Rules;

/// <summary>One firing, as the evaluator hands it to whatever writes it down.</summary>
public sealed record RuleFiringDraft(
    Guid RuleId,
    string SessionId,
    string ScreenText,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveRun> Runs,
    string TypedText,
    string Outcome,
    string Grounding);

/// <summary>What one evaluation pass did. A closed set, so a caller and a test both name the same thing.</summary>
public static class RulePassOutcomes
{
    /// <summary>This account has no rules at all.</summary>
    public const string NoRules = "no-rules";

    /// <summary>The session's facts could not be read - it is no longer on the roster.</summary>
    public const string SessionNotFound = "session-not-found";

    /// <summary>The screen could not be read. Unreadable is not evidence.</summary>
    public const string ScreenUnreadable = "screen-unreadable";

    /// <summary>The pass stopped before any rule was considered (working, or the screen did not change).</summary>
    public const string StoppedBeforeAnyRule = "stopped-before-any-rule";

    /// <summary>A pass on this same session was already in flight, so this one did nothing. See the
    /// serialisation comment on <see cref="RuleEvaluator"/>: two passes that overlap can both act past the
    /// cooldown and past the daily cap, because both read the firing record before either wrote to it.</summary>
    public const string AlreadyEvaluating = "already-evaluating";

    /// <summary>Every rule was considered and passed over, each with its reason.</summary>
    public const string NoCandidates = "no-candidates";

    /// <summary>The reply named something it was not offered, or was not an answer. Recorded as a refusal.</summary>
    public const string Refused = "refused";

    /// <summary>The agent read the screen against the instruction and declined. Recorded.</summary>
    public const string Declined = "declined";

    /// <summary>The act was given up before the keystroke. Recorded.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The act could not be WRITTEN DOWN, so it was not carried out. The record is the product;
    /// an action nobody can reconstruct is an action nobody can supervise, so the record being refused is
    /// a reason not to act rather than a detail to log afterwards.</summary>
    public const string NotRecorded = "not-recorded";

    /// <summary>The reply decided to ACT and its stated reason quoted text the screen does not contain, so
    /// the act was refused (Architect ruling A12). Recorded, with what was quoted and where it was not.</summary>
    public const string Ungrounded = "ungrounded";

    /// <summary>The rule is in dry run: it decided to act, and typed nothing.</summary>
    public const string DryRun = "dry-run";

    /// <summary>The rule is live, the text was typed, and the route confirmed it started a turn.</summary>
    public const string Acted = "acted";

    /// <summary>The send left this Gateway and nothing confirmed what became of it. The record names the
    /// text that was sent and does NOT claim it landed - see <see cref="RuleSendResult.Unknown"/>.</summary>
    public const string SendUnconfirmed = "send-unconfirmed";

    /// <summary>Nothing was typed, because the keystroke never left this Gateway.</summary>
    public const string NotSent = "not-sent";
}

/// <summary>What one evaluation pass did, and the firings it wrote.</summary>
public sealed record RulePass(string What, string Detail, IReadOnlyList<RuleFiringDraft> Recorded);

/// <summary>What the send seam can answer. THREE answers, not two, because "it did not work" hides a
/// distinction that matters: a keystroke that never left this Gateway is not the same event as one
/// that went out and whose arrival nobody would confirm.</summary>
public static class RuleSendOutcomes
{
    /// <summary>It went out and the route confirmed a turn started.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>It left this Gateway and NOBODY ANSWERED FOR IT. A timeout, a dropped tunnel, or a
    /// Director that answered a failure are all this: the command went out and what became of it is not
    /// known. It is never read as a keystroke that landed and never as one that did not - the record says
    /// what was sent and says plainly that nothing confirmed it.</summary>
    public const string Unknown = "unknown";

    /// <summary>It never left this Gateway - the machine is not connected. Nothing was typed.</summary>
    public const string NotSent = "not-sent";
}

/// <summary>What the send seam answered, and the words that go on the record.</summary>
public sealed record RuleSendResult(string What, string Detail)
{
    /// <summary>It went out and the route confirmed a turn started.</summary>
    public static RuleSendResult Confirmed() => new(RuleSendOutcomes.Confirmed, "");

    /// <summary>It left this Gateway and nobody answered for it. <paramref name="detail"/> is what the
    /// route said, verbatim, so the record carries the route's own words rather than a paraphrase.</summary>
    public static RuleSendResult Unknown(string detail) => new(RuleSendOutcomes.Unknown, detail);

    /// <summary>It never left this Gateway. <paramref name="detail"/> says why.</summary>
    public static RuleSendResult NotSent(string detail) => new(RuleSendOutcomes.NotSent, detail);
}

/// <summary>
/// Everything the evaluator needs from the Gateway around it, as one seam - the same shape, and for the
/// same reason, as the supervisor's <c>ISupervisorEnvironment</c>: production wires the real store, the
/// real tunnel read, the real model and the real prompt route, and a test wires a fake with an instrumented
/// send seam, which is what makes "dry run types nothing" a counted fact rather than a missing log line.
/// </summary>
public interface IRuleEnvironment
{
    /// <summary>Every rule this account has.</summary>
    IReadOnlyList<SessionRule> Rules(TenantId tenant);

    /// <summary>Every firing of one rule, so the cooldown and the daily cap can be counted.</summary>
    IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId);

    /// <summary>The session's facts from the pushed roster snapshot, or null when it is no longer there.</summary>
    RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId);

    /// <summary>The session's LIVE screen rows, or null when it cannot be read.</summary>
    Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Ask the agent the one question for this screen, and return its raw reply.</summary>
    Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct);

    /// <summary>THE SEND SEAM: type text into the session. The only thing in this feature that can.</summary>
    Task<RuleSendResult> TypeIntoSessionAsync(
        TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Write one firing down, and answer with its id. The id is what lets the record be written
    /// BEFORE the keystroke and completed after it.</summary>
    Guid RecordFiring(TenantId tenant, RuleFiringDraft draft);

    /// <summary>Say what became of a firing that was written down before its keystroke went out.</summary>
    void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome);

    /// <summary>The clock, as a seam.</summary>
    DateTime NowUtc { get; }
}

/// <summary>
/// THE EVALUATOR (Session Rules mission, phase 2) - the thin vertical slice from a session going idle to
/// something being typed into it, with every step written down.
///
/// THE ORDER IS THE SAFETY. Free checks first, so the common case - a session that finished a turn
/// normally - costs one screen read and nothing else and never reaches a model. Then ONE agent call
/// covering every surviving rule (Architect ruling A5), whose reply is validated against what was offered.
/// Then the checks the agent staked its decision on. Then, immediately before the keystroke and not a
/// moment earlier, the screen is READ AGAIN: a session that has moved on since the decision is abandoned.
/// And a rule in dry run never reaches the send at all.
///
/// ONE PASS AT A TIME PER SESSION, AND THAT IS WHAT MAKES THE CEILING A CEILING. Every turn-end signal
/// starts its own pass, and the free checks read a rule's prior firings BEFORE the agent call and before
/// anything is typed. Two passes that overlap in that window both see no act yet and both go on to act -
/// past the cooldown and past the daily cap, because each was counted against a record neither had written
/// to. The independent inspection of landing B proved exactly that with a synchronised probe: two
/// evaluations, two sends, two firing records. A ceiling a race can walk through is not a ceiling, and an
/// agent in a loop is the worst tail risk this feature has.
///
/// So a pass takes a per-session gate FIRST, before it reads anything, and a pass that cannot take it does
/// NOTHING and says so - <see cref="RulePassOutcomes.AlreadyEvaluating"/>. It does not queue. Queuing would
/// hand the waiting pass a decision made about a screen from before the one that was just acted on, which
/// is the same staleness the re-read exists to refuse; and a queue of stale passes behind a slow model call
/// is a pile-up nobody asked for. Dropping the overlapping pass is the direction that acts LESS, and the
/// next turn-end brings another one along.
///
/// SILENCE IS NEVER A DECISION. Every outcome that is not "there was nothing to look at" is written down as
/// a firing - the decline, the abandonment, and the refusal of an unreadable reply included. A rule that
/// did nothing because this code threw looks exactly like a rule that considered the screen and declined,
/// unless the decline is on the record. So the record is the proof, and the absence of a keystroke is not.
/// </summary>
public sealed class RuleEvaluator
{
    private readonly IRuleEnvironment _env;
    private readonly RulePrimitiveRegistry _registry;

    // The last screen evaluated per session, so "has the screen changed" costs nothing. Never a source of
    // truth about a session - only about what this Gateway has already looked at.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId, string), string> _lastScreen = new();

    // ONE PASS AT A TIME PER SESSION. See the serialisation paragraph on this class: this is the ceiling,
    // and without it the cooldown and the daily cap are both walk-throughs. A session is IN the set for
    // exactly as long as a pass on it is running, so the set is bounded by the passes in flight rather than
    // by the number of sessions this Gateway has ever seen - nothing accumulates.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId, string), byte> _passInFlight = new();

    /// <param name="environment">Everything it reads and writes.</param>
    /// <param name="registry">The verified checks a reply may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">The environment is null.</exception>
    public RuleEvaluator(IRuleEnvironment environment, RulePrimitiveRegistry? registry = null)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Evaluate one idle transition. Returns what the pass did and the firings it wrote, so a caller and a
    /// test both read the same account of it rather than inferring one from what did not happen.
    /// </summary>
    public async Task<RulePass> EvaluateAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        // TAKEN BEFORE ANYTHING IS READ. A gate taken later would still leave the window the overlap
        // actually uses: the free checks read the firing record, and the record is not written until after
        // the send.
        var key = (tenant, sessionId);
        if (!_passInFlight.TryAdd(key, 0))
            return Nothing(RulePassOutcomes.AlreadyEvaluating,
                $"a pass on session {sessionId} was already running, so this one did nothing. Two passes " +
                "that overlap would both count against a firing record neither had written to yet, and " +
                "both could act past the cooldown and past the daily cap.");

        try
        {
            return await EvaluateOnceAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _passInFlight.TryRemove(key, out _);
        }
    }

    /// <summary>One pass, with the per-session gate already held. Everything below assumes it.</summary>
    private async Task<RulePass> EvaluateOnceAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var rules = _env.Rules(tenant);
        if (rules is null || rules.Count == 0)
            return Nothing(RulePassOutcomes.NoRules, "this account has no rules.");

        // ONLY SCREEN RULES ARE EVALUATED AGAINST A SCREEN (Tech Lead ruling P8). A hook rule is decided by the
        // hook on the session's own machine; reading a screen and paying for a model call on its behalf would
        // be work for a rule that cannot act here. The free checks turn one down out loud as well.
        if (!rules.Any(RuleCandidateFilter.ReadsTheScreen))
            return Nothing(RulePassOutcomes.NoRules,
                "this account has no screen rules - its rules are decided by the agent's hook, not by the screen.");

        var facts = _env.ReadSessionFacts(tenant, sessionId);
        if (facts is null)
            return Nothing(RulePassOutcomes.SessionNotFound,
                $"session {sessionId} is not on the roster, so there is nothing to look at.");

        var rows = await _env.ReadScreenRowsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        if (rows is null)
            return Nothing(RulePassOutcomes.ScreenUnreadable,
                "the session's screen could not be read, and unreadable is not evidence.");

        var screen = Join(rows);
        var screenKey = (tenant, sessionId);
        _lastScreen.TryGetValue(screenKey, out var previous);

        var candidates = RuleCandidateFilter.Choose(
            rules, facts, screen, previous, id => _env.FiringsFor(tenant, id), _env.NowUtc);

        _lastScreen[screenKey] = screen;

        if (candidates.StoppedBecause is not null)
            return Nothing(RulePassOutcomes.StoppedBeforeAnyRule, candidates.StoppedBecause);

        if (candidates.Chosen.Count == 0)
            return Nothing(RulePassOutcomes.NoCandidates, SkippedReasons(candidates.Skipped));

        FileLog.Write($"[RuleEvaluator] sid={sessionId}: {candidates.Chosen.Count} rule(s) worth asking about");

        var prompt = RuleAgentContract.BuildPrompt(candidates.Chosen, rows, _registry);
        var raw = await _env.AskAgentAsync(tenant, prompt, ct).ConfigureAwait(false);
        var reading = RuleAgentContract.Read(raw, candidates.Chosen, _registry);

        if (reading.Refusal is not null)
        {
            // Recorded against EVERY rule that was in play. The pass covered all of them, none of them
            // fired, and each one's record has to show that its evaluation was refused rather than showing
            // nothing at all - which is exactly what a rule that crashed would show.
            var written = candidates.Chosen
                .Select(rule => Record(tenant, new RuleFiringDraft(
                    rule.Id, sessionId, screen, "", RuleDecisions.Refused, reading.Refusal,
                    Array.Empty<RulePrimitiveRun>(), "", "nothing was typed.",
                    RuleReasonGrounding.NotTheAgentsReason)))
                .ToList();
            return new RulePass(RulePassOutcomes.Refused, reading.Refusal, written);
        }

        var reply = reading.Reply!;
        var chosen = candidates.Chosen.First(r => r.Id == reply.RuleId);

        var runtime = new RuleRuntime(screen, facts.RepositoryPath, _env.NowUtc, FirstFailureUtc: null);
        var checks = RuleCheckRunner.Run(reply.Checks, runtime, _registry);

        // RULING A12: IS THE STATED REASON GROUNDED IN THE SCREEN IT WAS GIVEN? Computed before either
        // branch, so the answer is on the record whichever way the decision went, and so a run where this
        // never happened cannot look like a run where it happened and found nothing wrong.
        var grounding = RuleReasonGrounding.Check(reply.Reason, screen);

        if (reply.Decision == RuleDecisions.Decline)
        {
            // A decline stands even when its reason quotes something that is not there - declining is the
            // direction that does nothing, and the record should show what actually happened. But the
            // mismatch is NOTED rather than smoothed over, because it is the same unfaithfulness that
            // would be an act on evidence that was not there.
            var declined = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Decline, reply.Reason,
                checks.Runs, "", "declined - nothing was typed.", grounding.Statement));
            return new RulePass(RulePassOutcomes.Declined, reply.Reason, new[] { declined });
        }

        if (!grounding.CanCarryAnAct)
        {
            // AN ACT ON EVIDENCE THAT WAS NOT THERE IS REFUSED, AND SO IS AN ACT ON NO EVIDENCE AT ALL.
            // Recorded as a refusal carrying the grounding statement, so the reader sees which of the two it
            // was rather than an act that never happened.
            var ungrounded = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Refused, reply.Reason,
                checks.Runs, "",
                grounding.HasCitation
                    ? "nothing was typed: the reason for acting cites text this screen does not contain."
                    : "nothing was typed: the reason for acting cites nothing from this screen, so there is " +
                      "nothing anybody could check it against.",
                grounding.Statement));
            FileLog.Write($"[RuleEvaluator] sid={sessionId}: act REFUSED, {grounding.Statement}");
            return new RulePass(RulePassOutcomes.Ungrounded, grounding.Statement, new[] { ungrounded });
        }

        if (checks.Problem is not null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs, checks.Problem);

        // THE RE-READ. Immediately before the keystroke and after every decision has been made, because a
        // screen that moved on in between belongs to a different moment than the one that was judged.
        var rowsNow = await _env.ReadScreenRowsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        if (rowsNow is null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the screen could not be read again immediately before typing, so nothing was typed.");

        if (!string.Equals(Join(rowsNow), screen, StringComparison.Ordinal))
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the screen changed between the decision and the keystroke, so the decision was about a " +
                "screen that is no longer there and nothing was typed.");

        // AND THE SESSION ITSELF IS RE-READ, NOT ONLY ITS SCREEN. "Idle sessions only" is a primary bound
        // and it was read once, before the model call - the longest gap in the pass. A new owner turn makes
        // a session Working before any of its output appears, so the visible rows can be identical while the
        // session is no longer idle, and the stale decision would type straight into somebody else's turn.
        // Screen equality is not proof of idleness.
        var factsNow = _env.ReadSessionFacts(tenant, sessionId);
        if (factsNow is null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the session left the roster between the decision and the keystroke, so nothing was typed.");

        if (string.Equals(factsNow.ActivityState, RuleCandidateFilter.WorkingState, StringComparison.OrdinalIgnoreCase))
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the session started working between the decision and the keystroke - its screen had not " +
                "caught up yet - so nothing was typed. A rule only ever acts on an idle session.");

        var scopeNow = RuleCandidateFilter.WhyOutOfScope(chosen.Scope, factsNow);
        if (scopeNow is not null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the session no longer matches what this rule watches, so nothing was typed: " + scopeNow);

        if (chosen.State == RuleState.DryRun)
        {
            var wouldHave = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
                checks.Runs, "",
                "dry run: nothing was typed. It would have typed: " + reply.TextToType,
                grounding.Statement));
            return new RulePass(RulePassOutcomes.DryRun, reply.TextToType, new[] { wouldHave });
        }

        // THE RECORD EXISTS BEFORE THE KEYSTROKE DOES. Everything that can make a record fail - a rule
        // deleted during the model call, a record this store will not accept, a database that is down -
        // used to happen AFTER something had been done to somebody's session, leaving the action with
        // nothing durable to account for it and a log line as the only trace. The record is the product,
        // so it is written first as an INTENT and reconciled afterwards with what actually happened.
        //
        // A refusal here is therefore a reason NOT to act. Only the store's own stated refusal is caught,
        // and only so the pass can say what happened in words; any other failure propagates, which stops
        // the send just as effectively because it happens before it.
        var intent = new RuleFiringDraft(
            chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
            checks.Runs, "",
            "about to type into the session: " + reply.TextToType +
            ". This record was written BEFORE the keystroke went out; if it still says only this, nothing " +
            "ever came back to say what became of it.",
            grounding.Statement);

        Guid firingId;
        try
        {
            firingId = Write(tenant, intent);
        }
        catch (RuleRejectedException ex)
        {
            FileLog.Write($"[RuleEvaluator] sid={sessionId}: act NOT carried out, the record was refused: {ex.Reason}");
            return Nothing(RulePassOutcomes.NotRecorded,
                "nothing was typed, because what would have been done could not be written down first: " +
                ex.Reason);
        }

        var sent = await _env.TypeIntoSessionAsync(tenant, directorId, sessionId, reply.TextToType, ct)
            .ConfigureAwait(false);

        // A keystroke that never left this Gateway is the only case in which nothing was typed. The
        // record says so, and the typed text is blank, because claiming text that never went anywhere
        // would be the same lie in the other direction.
        //
        // TYPED TEXT IS THIS PRODUCT'S WORD FOR "IT REACHED THE SESSION", so it is written only when
        // something said so. An unanswered send names what went on the wire, in the outcome, and claims
        // nothing about what became of it - the route answers the same way for a shell whose turn was over
        // in milliseconds (the text DID land) as for a Director that refused the command outright (it did
        // not), and a record that picks one of those is wrong half the time in whichever direction it picks.
        var confirmed = sent.What == RuleSendOutcomes.Confirmed;
        var (what, typed, outcome) = sent.What switch
        {
            RuleSendOutcomes.NotSent => (
                RulePassOutcomes.NotSent, "", "nothing was typed: " + sent.Detail),
            RuleSendOutcomes.Confirmed => (
                RulePassOutcomes.Acted, reply.TextToType, "typed into the session: " + reply.TextToType),
            _ => (
                RulePassOutcomes.SendUnconfirmed, "",
                "sent to the machine running this session: " + reply.TextToType +
                " - and nothing confirmed what became of it (" + sent.Detail +
                "). The session's screen is the only evidence of whether it landed."),
        };

        Complete(tenant, firingId, typed, outcome);

        var acted = intent with { TypedText = typed, Outcome = outcome };
        return new RulePass(what, confirmed ? reply.TextToType : sent.Detail, new[] { acted });
    }

    private RulePass Abandon(
        TenantId tenant, SessionRule rule, string sessionId, string screen,
        RuleAgentReply reply, IReadOnlyList<RulePrimitiveRun> runs, string why)
    {
        // The reason on an abandonment is this Gateway's own words, not the agent's, so there is nothing of
        // the agent's to check against the screen. Saying that is not the same as saying nothing.
        var firing = Record(tenant, new RuleFiringDraft(
            rule.Id, sessionId, screen, reply.Understanding, RuleDecisions.Abandoned, why,
            runs, "", "abandoned - nothing was typed.", RuleReasonGrounding.NotTheAgentsReason));
        return new RulePass(RulePassOutcomes.Abandoned, why, new[] { firing });
    }

    private RuleFiringDraft Record(TenantId tenant, RuleFiringDraft draft)
    {
        Write(tenant, draft);
        return draft;
    }

    /// <summary>Tell the record what became of the keystroke it was written for. A completion that is
    /// refused leaves the intent standing, which says in its own words that nothing came back - that is a
    /// worse record than the truth and a far better one than an action with no row at all.</summary>
    private void Complete(TenantId tenant, Guid firingId, string typedText, string outcome)
    {
        try
        {
            _env.CompleteFiring(tenant, firingId, typedText, outcome);
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"[RuleEvaluator] firing {firingId} could not be completed: {ex.Message}. The record still " +
                "says the keystroke was about to go out.");
        }
    }

    private Guid Write(TenantId tenant, RuleFiringDraft draft)
    {
        FileLog.Write(
            $"[RuleEvaluator] firing: rule={draft.RuleId} sid={draft.SessionId} " +
            $"decision={draft.Decision} typed={(draft.TypedText.Length > 0 ? "yes" : "no")}");
        return _env.RecordFiring(tenant, draft);
    }

    private static RulePass Nothing(string what, string detail)
    {
        FileLog.Write($"[RuleEvaluator] {what}: {detail}");
        return new RulePass(what, detail, Array.Empty<RuleFiringDraft>());
    }

    /// <summary>Why every rule was skipped, each different reason said once. A reason several rules share -
    /// every hook rule gives the same one - carries how many rules it applies to, instead of being repeated
    /// once per rule.</summary>
    private static string SkippedReasons(IReadOnlyList<RuleSkipped> skipped) =>
        string.Join(" ", skipped
            .GroupBy(s => s.Reason, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} rules: {g.Key}"));

    /// <summary>The screen as one piece of text - what a check reads, what the record keeps, and what the
    /// re-read is compared against.</summary>
    private static string Join(IReadOnlyList<string> rows) =>
        string.Join("\n", rows.Select(r => r.TrimEnd()));
}
