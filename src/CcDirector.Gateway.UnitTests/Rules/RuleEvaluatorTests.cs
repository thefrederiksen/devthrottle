using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE EVALUATOR (Session Rules mission, phase 2): the thin vertical slice from a session going idle to
/// something being typed into it, with every step recorded.
///
/// The acceptance rows proved here, each as a PRESENCE:
///
///   * a DECLINE is a RECORDED FIRING, not silence. A rule that did nothing because the evaluator threw
///     looks exactly like a rule that declined, so the decline is proved by the presence of its record
///     with its reason, never by the absence of a keystroke.
///   * DRY RUN TYPES NOTHING, proved by an INSTRUMENTED SEND SEAM counted at zero - the fake environment
///     counts every call to the one method that can type - together with the recorded firing that says
///     what it WOULD have typed. The count alone would pass just as happily if the evaluator had crashed
///     before reaching the send, which is why the record is asserted alongside it.
///   * THE SCREEN IS RE-READ IMMEDIATELY BEFORE ACTING, and a screen that changed between the decision and
///     the keystroke is abandoned with the abandonment recorded.
///   * a reply that names something it was not offered is RECORDED AS A REFUSAL, not swallowed.
///   * the free checks cost nothing: a working session never reaches the model, and the pass says why.
/// </summary>
public sealed class RuleEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";

    private const string TheNotice =
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    private static readonly string[] BlockedScreen =
    {
        "C:\\scratch>echo " + TheNotice,
        TheNotice,
        "C:\\scratch>",
    };

    private static readonly string TheSentence =
        "If a session's screen says it has run out of its model allowance, type the command that shows me what is left.";

    // ---- the fake environment ----------------------------------------------------------------------

    /// <summary>
    /// Everything the evaluator touches, with the two things that matter INSTRUMENTED: how many times the
    /// model was asked, and how many times something was typed into a session. The send counter is the seam
    /// the "dry run types nothing" row is proved on - there is exactly one method here that can type, so a
    /// count of zero is a count of the real thing rather than a grep over a log.
    /// </summary>
    private sealed class FakeRuleEnvironment : IRuleEnvironment
    {
        public List<SessionRule> StoredRules { get; } = new();
        public List<SessionRuleFiring> StoredFirings { get; } = new();
        public List<RuleFiringDraft> Recorded { get; } = new();

        /// <summary>The screens the session shows, in the order they are read. The last one is repeated
        /// once the list runs out, so a test that wants the screen to CHANGE between the decision and the
        /// keystroke supplies two, and a test that wants it stable supplies one.</summary>
        public List<IReadOnlyList<string>?> Screens { get; } = new();
        private int _screenReads;

        public string ActivityState { get; set; } = "WaitingForInput";

        /// <summary>The activity states the session reports, in the order they are asked for; the last one
        /// repeats once the list runs out. Empty means <see cref="ActivityState"/> every time. This is how a
        /// test makes the session START WORKING during the agent call - the gap the evaluator's decision is
        /// made across - while leaving the visible rows exactly as they were.</summary>
        public List<string> ActivityStatesInOrder { get; } = new();
        private int _factReads;
        public string? AgentReply { get; set; }

        public int ScreenReads => _screenReads;
        public int AgentCalls { get; private set; }
        public string? LastPrompt { get; private set; }

        /// <summary>THE SEND SEAM. Every keystroke this feature can produce goes through here.</summary>
        public List<string> Typed { get; } = new();
        public RuleSendResult SendResult { get; set; } = RuleSendResult.Confirmed();

        public DateTime NowUtc { get; set; } = Now;

        public IReadOnlyList<SessionRule> Rules(TenantId tenant) => StoredRules;

        /// <summary>Run with the firings ALREADY READ and about to be handed back, which is INSIDE the free
        /// checks and therefore before the evaluator has recorded that it has looked at this screen. That is
        /// the exact window an overlapping pass arrives in - one pass holding a snapshot of the record that
        /// another pass is about to write to - so it is where a test has to hold a pass to make the overlap
        /// deterministic rather than hoping for a race. Holding it BEFORE the read instead would let the
        /// held pass see the other pass's act when it resumed, and the cooldown would hide the defect.</summary>
        public Action? WhileHoldingTheFiringsSnapshot { get; set; }

        /// <summary>Whether a firing this environment is told about becomes visible to the free checks, the
        /// way a real store's does. Off by default so the tests written before it keep their exact shape;
        /// on, it is what makes the cooldown and the daily cap real in a test rather than assumed.</summary>
        public bool FiringsAreVisibleToTheFreeChecks { get; set; }

        public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId)
        {
            List<SessionRuleFiring> snapshot;
            lock (StoredFirings) snapshot = StoredFirings.Where(f => f.RuleId == ruleId).ToList();
            WhileHoldingTheFiringsSnapshot?.Invoke();
            return snapshot;
        }

        /// <summary>The repository the session is working in. Settable so a test can give it a string that
        /// could not appear by accident and then look for it where it must not be.</summary>
        public string RepositoryPath { get; set; } = @"D:\ReposFred\scratch";

        /// <summary>How many times the session's facts were asked for. A re-read immediately before the
        /// keystroke is a PRESENCE, and this is what counts it.</summary>
        public int FactReads => _factReads;

        /// <summary>Set to make the session vanish from the roster after this many fact reads.</summary>
        public int? SessionVanishesAfterFactRead { get; set; }

        public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId)
        {
            var index = _factReads;
            _factReads++;
            if (SessionVanishesAfterFactRead is { } vanishAfter && index >= vanishAfter) return null;
            var state = ActivityStatesInOrder.Count == 0
                ? ActivityState
                : ActivityStatesInOrder[Math.Min(index, ActivityStatesInOrder.Count - 1)];
            return new(sessionId, "RawCli", RepositoryPath, "SOREN_NORTH", "Session Rules", state);
        }

        public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
            TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            var index = Math.Min(_screenReads, Screens.Count - 1);
            _screenReads++;
            return Task.FromResult(Screens.Count == 0 ? null : Screens[index]);
        }

        public Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
        {
            AgentCalls++;
            LastPrompt = prompt;
            return Task.FromResult(AgentReply);
        }

        /// <summary>Read at the moment the send seam is reached, so a test can assert that the record
        /// already existed BEFORE the keystroke rather than inferring it from the order of a list.</summary>
        public Func<int>? RecordedWhenTyping { get; set; }

        /// <summary>How many firings had been written when the send seam was reached.</summary>
        public int RecordsWhenTheSendHappened { get; private set; } = -1;

        public Task<RuleSendResult> TypeIntoSessionAsync(
            TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            if (RecordedWhenTyping is not null) RecordsWhenTheSendHappened = RecordedWhenTyping();
            lock (Typed) Typed.Add(text);
            return Task.FromResult(SendResult);
        }

        /// <summary>Set to make the store refuse to write a firing with this decision - what a real store
        /// does for a rule deleted mid-pass, a record it will not accept, or a database that is down.</summary>
        public string? RefuseToRecordDecision { get; set; }

        /// <summary>What each written firing was completed with afterwards, by id.</summary>
        public Dictionary<Guid, (string TypedText, string Outcome)> Completed { get; } = new();

        public Guid RecordFiring(TenantId tenant, RuleFiringDraft draft)
        {
            if (string.Equals(RefuseToRecordDecision, draft.Decision, StringComparison.Ordinal))
                throw new RuleRejectedException(
                    "this record was refused by the store, which is what a rule deleted during the model " +
                    "call looks like from here.");

            var id = Guid.NewGuid();
            lock (Recorded)
            {
                _rowOf[id] = Recorded.Count;
                Recorded.Add(draft);
            }
            lock (WrittenIds) WrittenIds.Add(id);
            if (!FiringsAreVisibleToTheFreeChecks) return id;
            lock (StoredFirings)
                StoredFirings.Add(new SessionRuleFiring(
                    id, draft.RuleId, draft.SessionId, NowUtc, draft.ScreenText,
                    draft.Understanding, draft.Decision, draft.Reason, draft.Runs, draft.TypedText,
                    draft.Outcome, draft.Grounding));
            return id;
        }

        /// <summary>The ids handed out, in order.</summary>
        public List<Guid> WrittenIds { get; } = new();

        /// <summary>Where each written firing sits in <see cref="Recorded"/>, so completing one UPDATES the
        /// row rather than adding a second - which is what a real store does, and what makes
        /// <see cref="Recorded"/> the record a reader would actually find.</summary>
        private readonly Dictionary<Guid, int> _rowOf = new();

        public void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome)
        {
            lock (Completed) Completed[firingId] = (typedText, outcome);
            lock (Recorded)
            {
                if (!_rowOf.TryGetValue(firingId, out var row)) return;
                Recorded[row] = Recorded[row] with { TypedText = typedText, Outcome = outcome };
            }
        }
    }

    private static SessionRule Rule(
        RuleState state = RuleState.DryRun,
        IReadOnlyList<string>? triggerWords = null,
        Guid? id = null) => new(
            id ?? Guid.NewGuid(),
            TheSentence,
            "A session stopped on a provider allowance notice.",
            triggerWords ?? new[] { "reached your", "limit" },
            Array.Empty<RulePrimitiveCall>(),
            RuleScope.AllSessions,
            300,
            5,
            state,
            state == RuleState.Live ? "device-9f2c" : "",
            Now,
            Now);

    private static FakeRuleEnvironment EnvironmentWith(SessionRule rule, params IReadOnlyList<string>[] screens)
    {
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(rule);
        foreach (var screen in screens.Length == 0 ? new[] { BlockedScreen } : screens)
            env.Screens.Add(screen);
        return env;
    }

    /// <summary>The check the agent names by default: the words really are on this screen.</summary>
    private const string TheWordsAreThere =
        "{ \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"reached your\"] } }";

    /// <summary>An ACT reply. Its reason QUOTES THE SCREEN, because an act whose reason cites nothing the
    /// screen contains is refused - see the grounding tests below. The default reason used to cite nothing,
    /// which is what let every act test here pass while the bound did not exist.</summary>
    private static string ActReply(Guid ruleId, string type = "/usage-credits", string? checks = null)
    {
        var theChecks = checks ?? TheWordsAreThere;
        return $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The session itself is blocked on its model allowance and cannot run a turn.",
          "decision": "act",
          "reason": "The screen says 'reached your Fable 5 limit', which is the session's own state and not a discussion of one.",
          "checks": [ {{theChecks}} ],
          "type": "{{type}}"
        }
        """;
    }

    private static string DeclineReply(Guid ruleId, string reason) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The screen shows the notice being talked about, not the session reporting its own state.",
          "decision": "decline",
          "reason": "{{reason}}"
        }
        """;

    private static Task<RulePass> Run(FakeRuleEnvironment env) =>
        new RuleEvaluator(env).EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

    // ---- the free checks cost nothing ---------------------------------------------------------------

    [Fact]
    public async Task A_working_session_never_reaches_the_model_and_the_pass_says_why()
    {
        var env = EnvironmentWith(Rule());
        env.ActivityState = "Working";

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.StoppedBeforeAnyRule, pass.What);
        Assert.Equal(RuleCandidateFilter.SessionIsWorking, pass.Detail);
        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task A_screen_without_any_rules_words_never_reaches_the_model_and_every_rule_has_a_reason()
    {
        var env = EnvironmentWith(Rule(triggerWords: new[] { "no such words anywhere" }));

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NoCandidates, pass.What);
        Assert.Contains("none of the words this rule watches for", pass.Detail);
        Assert.Equal(0, env.AgentCalls);
    }

    [Fact]
    public async Task An_unreadable_screen_is_not_evidence_and_stops_the_pass_with_a_reason()
    {
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(Rule());
        env.Screens.Add(null);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.ScreenUnreadable, pass.What);
        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
    }

    // ---- the decline is a RECORDED FIRING ------------------------------------------------------------

    [Fact]
    public async Task A_decline_is_written_down_with_its_reason_and_nothing_is_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id,
            "This screen is a document quoting the notice, not a session reporting its own state.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);

        // The PRESENCE that proves it: a recorded firing saying it declined, and why.
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(rule.Id, firing.RuleId);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("quoting the notice", firing.Reason);
        Assert.Equal("", firing.TypedText);
        Assert.Equal(TheNotice, ScreenTextOf(firing));
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task A_declines_record_carries_what_the_agent_understood_as_well_as_what_it_decided()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "not this session's own state.");

        await Run(env);

        var firing = Assert.Single(env.Recorded);
        Assert.Contains("talked about", firing.Understanding);
    }

    // ---- dry run types nothing ----------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_rule_types_nothing_and_records_what_it_would_have_typed()
    {
        var rule = Rule(state: RuleState.DryRun);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits");

        var pass = await Run(env);

        // The instrumented send seam, counted at zero...
        Assert.Empty(env.Typed);
        // ...and the PRESENCE that says the evaluator really did get all the way there.
        Assert.Equal(RulePassOutcomes.DryRun, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("dry run", firing.Outcome);
        Assert.Contains("/usage-credits", firing.Outcome);
    }

    // ---- a live rule types exactly the composed text -------------------------------------------------

    [Fact]
    public async Task A_live_rule_types_exactly_what_the_agent_composed_and_records_it()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("/usage-credits", firing.TypedText);
        Assert.Contains("typed", firing.Outcome);
    }

    [Fact]
    public async Task The_checks_the_agent_named_are_run_and_their_answers_are_on_the_record()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        var firing = Assert.Single(env.Recorded);
        var run = Assert.Single(firing.Runs);
        Assert.Equal("matches_any", run.Name);
        Assert.Equal("text=<screen_text>, terms=reached your", run.Arguments);
        Assert.Equal("true", run.Answer);
    }

    [Fact]
    public async Task A_check_the_agent_staked_its_decision_on_that_answers_no_abandons_the_act()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits",
            checks: "{ \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"not on this screen at all\"] } }");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.Contains("matches_any", firing.Reason);
    }

    // ---- the screen is re-read immediately before acting ---------------------------------------------

    [Fact]
    public async Task The_screen_is_read_again_immediately_before_the_keystroke()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule, BlockedScreen, BlockedScreen);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        Assert.Equal(2, env.ScreenReads);
        Assert.Single(env.Typed);
    }

    [Fact]
    public async Task A_screen_that_changed_between_the_decision_and_the_keystroke_is_abandoned_and_written_down()
    {
        var rule = Rule(state: RuleState.Live);
        var movedOn = new[] { "C:\\scratch>dir", "Volume in drive C has no label.", "C:\\scratch>" };
        var env = EnvironmentWith(rule, BlockedScreen, movedOn);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.Contains("changed", firing.Reason);
        Assert.Equal("", firing.TypedText);
    }

    [Fact]
    public async Task A_screen_that_cannot_be_read_again_is_abandoned_rather_than_typed_into()
    {
        var rule = Rule(state: RuleState.Live);
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(rule);
        env.Screens.Add(BlockedScreen);
        env.Screens.Add(null);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        Assert.Contains("could not be read", Assert.Single(env.Recorded).Reason);
    }

    // ---- a reply naming something it was not offered is RECORDED as a refusal -------------------------

    [Fact]
    public async Task A_reply_naming_a_rule_that_was_not_offered_is_recorded_as_a_refusal()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        var neverOffered = Guid.NewGuid();
        env.AgentReply = ActReply(neverOffered);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(rule.Id, firing.RuleId);     // recorded against the rule that WAS in play
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains(neverOffered.ToString(), firing.Reason);
    }

    [Fact]
    public async Task A_reply_naming_a_check_the_product_does_not_ship_is_recorded_as_a_refusal()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits",
            checks: "{ \"name\": \"run_shell\", \"arguments\": { \"command\": \"whoami\" } }");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        Assert.Contains("run_shell", Assert.Single(env.Recorded).Reason);
    }

    [Fact]
    public async Task A_model_that_says_nothing_is_recorded_as_a_refusal_and_never_read_as_permission()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = null;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        Assert.Equal(RuleDecisions.Refused, Assert.Single(env.Recorded).Decision);
    }

    // ---- the record is the product ------------------------------------------------------------------

    [Fact]
    public async Task Every_firing_carries_the_screen_it_was_decided_on()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        Assert.Contains(TheNotice, Assert.Single(env.Recorded).ScreenText);
    }

    [Fact]
    public async Task A_send_that_never_left_the_gateway_is_recorded_as_nothing_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SendResult = RuleSendResult.NotSent("that machine is not connected.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotSent, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("nothing was typed", firing.Outcome);
        Assert.Contains("not connected", firing.Outcome);
    }

    /// <summary>
    /// A SEND NOBODY ANSWERED FOR IS NOT A SEND THAT LANDED, AND IT IS NOT ONE THAT DID NOT.
    ///
    /// Two defects meet on this line and pull in opposite directions, so the record has to refuse both. The
    /// prompt route answers "never started a turn ... parked in the composer unsubmitted" for a session
    /// whose turn is over in milliseconds - a shell - while the keystroke has in fact landed; reading that
    /// as "the send did not land" put a sentence into a real firing record that the session's own screen
    /// disproved. But the same answer also comes back when the Director refused the command outright, when
    /// the tunnel dropped, and when nothing answered at all - and in those the text did NOT land. Writing
    /// "typed into the session" for all of them is the first lie wearing the other coat.
    ///
    /// So an unanswered send records: what was SENT, in the outcome, in full; and NO typed text, because
    /// typed text is this product's word for "this reached the session" and nothing here confirmed that.
    /// The session's screen is named as the only evidence either way.
    /// </summary>
    [Fact]
    public async Task A_send_nobody_answered_for_names_the_text_it_sent_and_does_not_claim_it_landed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SendResult = RuleSendResult.Unknown(
            "never started a turn within 8 beats: the agent produced under 2048 bytes.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.SendUnconfirmed, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        var firing = Assert.Single(env.Recorded);

        // The claim that must not be made: typed text is the product's word for "it reached the session".
        Assert.Equal("", firing.TypedText);

        // And the presence that must be there, so nothing is lost by refusing the claim: the record still
        // says exactly what was put on the wire, and says the screen is the evidence.
        Assert.Contains("/usage-credits", firing.Outcome);
        Assert.Contains("nothing confirmed", firing.Outcome);
        Assert.Contains("screen", firing.Outcome);
        Assert.DoesNotContain("did not land", firing.Outcome);
        Assert.DoesNotContain("never reached", firing.Outcome);
        Assert.DoesNotContain("typed into the session", firing.Outcome);
    }

    // ---- ruling A11: what decides WHETHER a rule applies is the screen ------------------------------

    [Fact]
    public async Task The_question_the_agent_is_asked_carries_the_screen_and_the_instruction_and_not_the_machine_state()
    {
        // RULING A11, MADE TESTABLE. The owner ruled that the terminal screen is the only input, and
        // ruling 15 ships checks that take a clock and the session's repository root as ARGUMENTS - so the
        // two are not in conflict, but nothing separated them except a convention, and a convention is not
        // a bound. This is the separation, enforced: the values that would let the agent decide whether an
        // instruction applies from something other than the screen are NOT in the question it is asked.
        //
        // The check the agent may ask for can still be handed those values when it RUNS - that is what
        // RuleRuntime is for, and it happens after the decision. What cannot happen is the decision being
        // made from them.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.RepositoryPath = @"D:\ReposFred\zz-distinctive-repository-name";
        env.NowUtc = new DateTime(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        env.AgentReply = DeclineReply(rule.Id, "the screen is not what the instruction is about.");

        await Run(env);

        var prompt = env.LastPrompt;
        Assert.NotNull(prompt);

        // A PRESENCE first, so the assertions below cannot pass over an empty or truncated question: the
        // screen and the account's own sentence must both really be in it.
        Assert.Contains("reached your", prompt!, StringComparison.Ordinal);
        Assert.Contains(TheSentence, prompt, StringComparison.Ordinal);

        // And then what must NOT be: the machine state the owner deferred.
        Assert.DoesNotContain("zz-distinctive-repository-name", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2031", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SOREN_NORTH", prompt, StringComparison.Ordinal);
    }

    // ---- ruling A12: an act's reason has to be grounded in the screen it was given ------------------

    /// <summary>An ACT reply whose reason quotes something that is not on the screen.</summary>
    private static string UngroundedActReply(Guid ruleId) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "the screen says 'YOUR SUBSCRIPTION HAS BEEN CANCELLED', so the allowance is gone.",
          "checks": [ ],
          "type": "/usage-credits"
        }
        """;

    [Fact]
    public async Task An_act_whose_reason_quotes_text_the_screen_does_not_contain_is_refused_and_types_nothing()
    {
        // THE BOUND RULING A12 ASKS FOR. A rule that acts on evidence that was not there is the same
        // unfaithfulness that produced a decline quoting a screen from twelve minutes earlier, pointed in
        // the direction that does something.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = UngroundedActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Empty(env.Typed);

        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("YOUR SUBSCRIPTION HAS BEEN CANCELLED", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decline_whose_reason_quotes_text_the_screen_does_not_contain_is_recorded_with_the_mismatch_noted()
    {
        // Declining is the direction that does nothing, so the decline stands - but the record has to show
        // the unfaithfulness rather than smooth it over.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id,
            "the echo output explicitly says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING'.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Empty(env.Typed);

        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("THE SCREEN HAS MOVED ON", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_firing_says_what_the_grounding_check_found_even_when_there_was_nothing_to_check()
    {
        // THE PRESENCE. A run in which the grounding check never executed must not look identical to one
        // in which it ran and found nothing wrong, so the statement is never blank on any firing.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.DryRun, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.NotEqual("", firing.Grounding);
        Assert.Contains("grounding:", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_says_the_grounding_check_did_not_apply_rather_than_saying_nothing()
    {
        // The reason on a refusal is the Gateway's own words, not the agent's, so there is nothing of the
        // agent's to check. Saying that is not the same as leaving it blank.
        var env = EnvironmentWith(Rule());
        env.AgentReply = "I am not going to answer in JSON.";

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleReasonGrounding.NotTheAgentsReason, firing.Grounding);
    }

    [Fact]
    public async Task An_act_whose_reason_quotes_the_screen_faithfully_still_acts()
    {
        // The PRESENCE half of the bound. A grounding check that refused everything would pass the tests
        // above while making the feature impossible, and they could not tell the difference.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "the screen says 'reached your Fable 5 limit', which is the session's own state.",
          "checks": [ ],
          "type": "/usage-credits"
        }
        """;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
    }

    // ---- an act must cite the screen ----------------------------------------------------------------

    /// <summary>
    /// AN ACT WHOSE REASON CITES NOTHING FROM THE SCREEN IS REFUSED, and this is the half of ruling A12 that
    /// was missing.
    ///
    /// The grounding check refused a reason that quoted words the screen does not contain. It did not refuse
    /// a reason that quoted NOTHING - it answered "there was nothing to check" and called that grounded. So
    /// an agent could avoid the whole check by writing a plausible sentence with no quotation in it, and act
    /// on evidence that nobody can go back and verify. An absence was being read as positive grounding,
    /// which is the exact shape this mission's own standard forbids.
    ///
    /// A DECLINE stays permissive - declining is the direction that does nothing - but its record says
    /// plainly that nothing was cited, so a decline that cited nothing cannot be mistaken for one whose
    /// citation was checked and held.
    /// </summary>
    [Fact]
    public async Task An_act_whose_reason_cites_nothing_from_the_screen_is_refused()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "This session has plainly run out of its allowance and the instruction covers it exactly.",
          "checks": [ ],
          "type": "/usage-credits"
        }
        """;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains("cite", firing.Grounding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_decline_that_cites_nothing_is_still_recorded_and_the_record_says_it_cited_nothing()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "the screen is only talking about a limit, not reporting one.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("cite", firing.Grounding, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the record exists before the keystroke ------------------------------------------------------

    /// <summary>
    /// AN ACTION THE PRODUCT CANNOT ACCOUNT FOR IS AN ACTION NOBODY CAN SUPERVISE.
    ///
    /// The evaluator typed first and recorded afterwards. Everything that can make the record fail - a rule
    /// deleted during the model call, a record the store will not accept, a database that is down - then
    /// happened AFTER something had already been done to somebody's session, and the only trace was a log
    /// line. The record is the product; it has to exist before the side effect and be reconciled after it.
    ///
    /// So the store's refusal is a REASON NOT TO ACT, not a detail discovered too late. Here the store
    /// refuses the act's record, and nothing may be typed.
    /// </summary>
    [Fact]
    public async Task An_act_whose_record_the_store_refuses_is_not_carried_out()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.RefuseToRecordDecision = RuleDecisions.Act;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotRecorded, pass.What);
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task The_record_of_an_act_is_written_before_the_keystroke_and_completed_after_it()
    {
        // THE PRESENCE, and it is what makes the test above mean something: a pass that simply refused to
        // act would satisfy "nothing was typed" while proving nothing about the ordering. Here the act goes
        // through, and the record must have been WRITTEN before the send and COMPLETED afterwards - two
        // observable events on one row, in that order.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.RecordedWhenTyping = () => env.WrittenIds.Count;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));

        // Written first: by the time the send seam was reached, the record already existed.
        Assert.Equal(1, env.RecordsWhenTheSendHappened);

        // Completed after: the same row, told what became of the keystroke.
        var id = Assert.Single(env.WrittenIds);
        Assert.True(env.Completed.ContainsKey(id),
            "the firing was written before the keystroke and never completed afterwards, so the record " +
            "still says only that a keystroke was about to go.");
        Assert.Contains("/usage-credits", env.Completed[id].Outcome);
        Assert.Equal("/usage-credits", env.Completed[id].TypedText);
    }

    // ---- still idle, immediately before the keystroke -----------------------------------------------

    /// <summary>
    /// THE SESSION MUST STILL BE IDLE WHEN THE KEYSTROKE GOES, NOT MERELY WHEN THE DECISION WAS MADE.
    ///
    /// "Idle sessions only" is a primary bound, and it was read ONCE, before the agent call. Between that
    /// read and the keystroke there is a model call - the longest gap in the whole pass - and the only thing
    /// re-read across it was the terminal screen. A new owner turn makes the session Working before any of
    /// its output appears, so the visible rows are briefly identical and the stale decision types anyway,
    /// straight into a turn somebody else just started.
    ///
    /// Screen equality is not proof that a session is still idle. This holds the screen still and changes
    /// only the thing that actually says whether the session is working.
    /// </summary>
    [Fact]
    public async Task A_session_that_started_working_during_the_agent_call_is_abandoned_though_the_screen_is_unchanged()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        // Idle when the pass starts; working by the time the keystroke would go. The screen never changes.
        env.ActivityStatesInOrder.Add("WaitingForInput");
        env.ActivityStatesInOrder.Add(RuleCandidateFilter.WorkingState);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Contains("working", pass.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(pass.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.True(env.FactReads >= 2,
            "the session's facts were read " + env.FactReads + " time(s). A bound that is checked before a " +
            "model call and never again is a bound about a moment that has passed.");
    }

    [Fact]
    public async Task A_session_that_left_the_roster_during_the_agent_call_is_abandoned()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SessionVanishesAfterFactRead = 1;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        Assert.Single(pass.Recorded);
    }

    // ---- two passes on one session ------------------------------------------------------------------

    /// <summary>
    /// TWO OVERLAPPING PASSES ON ONE SESSION MUST NOT BOTH ACT, and this is the shape that let them.
    ///
    /// Every turn-end signal starts its own pass, and the free checks read a rule's prior firings BEFORE the
    /// agent call and before anything is typed. Two passes that overlap in that window both see no act yet,
    /// and both go on to act - past the cooldown and past the daily cap, because both were counted against a
    /// state that neither of them had written to yet. The independent inspection of landing B proved exactly
    /// that with a synchronised probe: two evaluations, two sends, two firing records.
    ///
    /// A CEILING A RACE CAN WALK THROUGH IS NOT A CEILING, and an agent in a loop is the worst tail risk
    /// this feature has. So the test holds the first pass open in the window the inspection used - inside the
    /// firings read - runs a second pass to completion while it is held, and requires that exactly one send
    /// and exactly one act reach the world.
    /// </summary>
    [Fact]
    public async Task Two_passes_that_overlap_on_one_session_act_once_and_the_second_says_why()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.FiringsAreVisibleToTheFreeChecks = true;
        env.AgentReply = ActReply(rule.Id);

        // One evaluator, because production holds one: the Gateway arms a single evaluator and every
        // turn-end signal calls it. Two evaluator objects would be a different question than the one asked.
        var evaluator = new RuleEvaluator(env);

        var firstPassIsInTheWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letTheFirstPassGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        env.WhileHoldingTheFiringsSnapshot = () =>
        {
            // ONLY the first reader is held. A gate that held every reader would deadlock the fixed code as
            // surely as it would hold the broken code, and a test that cannot finish proves nothing.
            if (Interlocked.Increment(ref readers) != 1) return;
            firstPassIsInTheWindow.SetResult();
            letTheFirstPassGo.Task.Wait(TimeSpan.FromSeconds(30));
        };

        var first = Task.Run(() => evaluator.EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None));
        await firstPassIsInTheWindow.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await evaluator.EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        letTheFirstPassGo.SetResult();
        var firstPass = await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RulePassOutcomes.Acted, firstPass.What);
        Assert.Equal(RulePassOutcomes.AlreadyEvaluating, second.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        Assert.Single(env.Recorded, r => r.Decision == RuleDecisions.Act);
        Assert.Empty(second.Recorded);
    }

    private static string ScreenTextOf(RuleFiringDraft draft) =>
        draft.ScreenText.Split('\n').Select(l => l.TrimEnd()).Last(l => l.Contains("reached your"));
    // ---- ruling P8: hook rules never reach the model call ---------------------------------------------

    [Fact]
    public async Task EvaluateAsync_AccountWithOnlyHookRules_NeverReadsTheScreenOrReachesTheModel()
    {
        var hook = Rule() with
        {
            Kind = RuleKinds.Hook,
            Hook = new HookRuleTrigger(HookRuleWords.BeforeCommand, HookRuleWords.Shell, HookRuleWords.Match,
                new[] { "reached your" }, HookRuleWords.Deny),
        };
        var env = EnvironmentWith(hook);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NoRules, pass.What);
        Assert.Contains("screen rules", pass.Detail, StringComparison.Ordinal);
        Assert.Equal(0, env.ScreenReads);
        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task EvaluateAsync_SeveralHookRulesSkipped_SaysTheSharedReasonOnceWithTheCount()
    {
        // Seen live on Windows: the no-candidates line repeated the hook-rule sentence once per hook rule,
        // four times in one line. The same reason is said once, with how many rules it applies to.
        var env = EnvironmentWith(Rule(triggerWords: new[] { "no such words anywhere" }));
        for (var i = 0; i < 4; i++)
            env.StoredRules.Add(Rule() with
            {
                Kind = RuleKinds.Hook,
                Hook = new HookRuleTrigger(HookRuleWords.BeforeCommand, HookRuleWords.Shell, HookRuleWords.Match,
                    new[] { "reached your" }, HookRuleWords.Deny),
            });

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NoCandidates, pass.What);
        const string hookReason = "only screen rules are evaluated against a screen";
        var first = pass.Detail.IndexOf(hookReason, StringComparison.Ordinal);
        Assert.True(first >= 0, pass.Detail);
        Assert.Equal(-1, pass.Detail.IndexOf(hookReason, first + 1, StringComparison.Ordinal));
        Assert.Contains("4 rules: this is a hook rule", pass.Detail, StringComparison.Ordinal);
        Assert.Contains("none of the words this rule watches for", pass.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("1 rule", pass.Detail, StringComparison.Ordinal);
    }
}
