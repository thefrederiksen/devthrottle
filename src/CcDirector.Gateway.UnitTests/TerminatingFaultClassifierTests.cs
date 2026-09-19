using CcDirector.Gateway.Supervision;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// Issue #915, step 2 of the supervisor funnel: reading a session's live screen and deciding whether the turn
// that just ended died on a fault - and which class of fault it was.
//
// This is the gate that keeps the whole feature non-interruptive, so it is pinned from both directions: the
// July 21 ENOTFOUND line MUST classify as recoverable, and a healthy screen (or a stale error a later turn has
// already scrolled past) MUST NOT.
// ============================================================================
public sealed class TerminatingFaultClassifierTests
{
    [Fact]
    public void PiApiKeyAuthFailure_IsNonRecoverable()
    {
        var fault = TerminatingFaultClassifier.Classify(
            Screen("Error: API key auth failed for provider openai-mindzie"));

        Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
        Assert.Equal("api key auth failed", fault.Signature);
    }

    /// <summary>The composer box and mode footer Claude Code draws at the bottom of every screen.</summary>
    private static readonly string[] Composer =
    {
        "╭──────────────────────────────────────────╮",
        "│ >                                        │",
        "╰──────────────────────────────────────────╯",
        "  ? for shortcuts                    Bypass permissions",
    };

    private static string[] Screen(params string[] transcript) => transcript.Concat(Composer).ToArray();

    [Fact]
    public void TheJulyTwentyFirstLine_ClassifiesAsTransientTransport()
    {
        // The exact line from the incident that produced this issue.
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "* Running gh pr checks...",
            "API Error: Unable to connect to API (ENOTFOUND)"));

        Assert.Equal(SessionFaultClass.TransientTransport, fault.Class);
        Assert.Equal("enotfound", fault.Signature);
        Assert.True(fault.IsRecoverable);
    }

    [Theory]
    [InlineData("API Error: request failed (ECONNRESET)", "econnreset")]
    [InlineData("Error: socket hang up", "socket hang up")]
    [InlineData("fetch failed: connection reset by peer", "fetch failed")]
    [InlineData("API Error: Request timed out after 60s", "request timed out")]
    public void TransportFaults_AreRecoverable(string line, string expectedSignature)
    {
        var fault = TerminatingFaultClassifier.Classify(Screen(line));
        Assert.Equal(SessionFaultClass.TransientTransport, fault.Class);
        Assert.Equal(expectedSignature, fault.Signature);
    }

    [Fact]
    public void ACleanTurnEnd_IsNoFaultAtAll()
    {
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "* Updated docs/plan.md with the new phase list",
            "I have finished the three edits and the tests pass. What next?"));

        Assert.Equal(SessionFaultClass.None, fault.Class);
    }

    [Fact]
    public void AnOldErrorHighOnTheScreen_IsNotATerminatingFault()
    {
        // THE RULE THAT STOPS THE ENGINE RE-FIRING ON A SESSION IT ALREADY RESCUED. The error is still on the
        // screen, but a whole turn has run since - so it did not terminate THIS turn and nothing may be sent.
        var transcript = new List<string> { "API Error: Unable to connect to API (ENOTFOUND)" };
        for (var i = 1; i <= TerminatingFaultClassifier.DefaultWindowLines + 2; i++)
            transcript.Add($"* Step {i} completed and verified");
        transcript.Add("All done - the branch is pushed and the pull request is open.");

        var fault = TerminatingFaultClassifier.Classify(Screen(transcript.ToArray()));

        Assert.Equal(SessionFaultClass.None, fault.Class);
    }

    [Fact]
    public void OrdinaryProseMentioningAConnectionFailure_IsNotAFault()
    {
        // The strict test on the ACTING classes: a session that merely PRINTED the words must never be typed
        // into. "connection refused" is ordinary English, and no error marker sits beside it here, so there is
        // no fault - the agent is discussing a log, not dying on one.
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "The service log shows connection refused entries from last Tuesday.",
            "Shall I open an issue for them?"));

        Assert.Equal(SessionFaultClass.None, fault.Class);
    }

    [Fact]
    public void TheSameProseWithTheAgentDyingOnIt_IsAFault()
    {
        // The other direction, so the rule above is a guard and not merely a way to miss things: the SAME
        // English phrase with a failure marker beside it is the real thing.
        var fault = TerminatingFaultClassifier.Classify(Screen("Error: connection refused by the API host"));

        Assert.Equal(SessionFaultClass.TransientTransport, fault.Class);
        Assert.Equal("connection refused", fault.Signature);
    }

    [Theory]
    [InlineData("API Error: 429 rate_limit_error - too many requests", SessionFaultClass.RateLimited)]
    [InlineData("Error: overloaded_error", SessionFaultClass.RateLimited)]
    public void ProviderThrottling_IsItsOwnClass(string line, SessionFaultClass expected)
    {
        Assert.Equal(expected, TerminatingFaultClassifier.Classify(Screen(line)).Class);
    }

    [Theory]
    [InlineData("Your credit balance is too low to run this request.")]
    [InlineData("API Error: 401 {\"type\":\"authentication_error\"}")]
    public void FaultsThatAPersonMustFix_AreNeverRecoverable(string line)
    {
        var fault = TerminatingFaultClassifier.Classify(Screen(line));
        Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
        Assert.False(fault.IsRecoverable);
    }

    // ============================================================================
    // Issue #3117: the usage-limit blocks. Every line below is verbatim from a captured turn-end screen
    // (devthrottle_internal/corpus/turn-log, 16-17 September 2026, 2,232 records over 336 sessions). The
    // shipped classifier carried one usage-limit signature - "usage limit reached" - which no agent
    // prints; over those two days it matched nothing while 23 distinct sessions showed a printed block,
    // across 27 turn-end records, of which the supervisor called 25 a clean turn end and 2 unclassified.
    //
    // A BLOCK is pinned from three directions, because the failure has two mirror images:
    //   1. the printed notice MUST be seen (the defect being fixed),
    //   2. the WARNING notices MUST NOT (a healthy session showing "94% of your weekly limit" is working),
    //   3. prose DISCUSSING a limit MUST NOT (a session reporting "Codex hit its usage limit" is healthy).
    // ============================================================================

    [Theory]
    [InlineData("│  You hit your free usage limit.", "hit your free usage limit")]                       // Grok
    [InlineData("■ You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at", "hit your usage limit.")]  // Codex
    [InlineData("■ You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit", "hit your usage limit.")]  // Codex
    [InlineData("⎿   You've hit your weekly limit · resets Sep 21, 6pm (America/Toronto)", "hit your weekly limit")]  // Claude Code
    public void APrintedUsageLimitBlock_IsNonRecoverable_NeverRetried(string line, string expectedSignature)
    {
        var fault = TerminatingFaultClassifier.Classify(Screen(line));

        Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
        Assert.Equal(expectedSignature, fault.Signature);
        Assert.False(fault.IsRecoverable);
    }

    [Theory]
    [InlineData("⚠ Heads up, you have less than 25% of your weekly limit left. Run /status for a breakdown.")]   // Codex
    [InlineData("You've used 94% of your weekly limit, which resets 21 September")]                             // Claude Code
    [InlineData("You've used 95% of your weekly limit · resets Sep 21, 6pm (America/Toronto)")]                 // Claude Code
    [InlineData("• You have 1 usage limit reset available. Run /usage to use one.")]                             // Codex
    public void AUsageLimitWarning_IsNotAFault(string line)
    {
        // A WARNING does not stop the work - it appeared on many more screens than the block did, on
        // sessions that were all still working. It must not stop, retry or escalate anything.
        var fault = TerminatingFaultClassifier.Classify(Screen(line));
        Assert.Equal(SessionFaultClass.None, fault.Class);
    }

    [Theory]
    [InlineData("- Last night's code review didn't run because Codex hit its usage limit.")]
    [InlineData("Where it's stuck. The Codex inspector hit its usage limit before writing anything, and the screen says it won't")]
    [InlineData("The blocker: it stopped immediately with \"You've hit your usage limit ... try again at Sep 22nd, 2026 4:46 AM.\" Codex")]
    [InlineData("- Codex hit its usage limit. Session 101 was the Codex Inspector, stopped at \"You've hit your usage limit\", which lasts until September 22.")]
    [InlineData("3. Data phase 1: not finished. It's blocked, and that's the real news. It hit your weekly usage limit, which")]
    public void ProseDiscussingAUsageLimit_IsNotAFault(string line)
    {
        // The negative control, and its inputs are real: all five lines are verbatim from turn-end screens
        // in the corpus, written by healthy sessions REPORTING another session's limit. The third and fourth
        // are the hard cases - prose that QUOTES the notice; the fifth is a near-miss paraphrase. The full
        // stop on the Codex signature exists precisely so the quote with an ellipsis or a comma does not
        // fire while the printed sentence does.
        var fault = TerminatingFaultClassifier.Classify(Screen(line));
        Assert.Equal(SessionFaultClass.None, fault.Class);
    }

    [Fact]
    public void AUsageLimitBlock_BesideADroppedConnection_Escalates_RatherThanRetrying()
    {
        // Precedence, the same rule as the sign-in failure: a session blocked on its limit that also shows
        // a transport fault must escalate, not retry - retrying can never get past the limit.
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "API Error: Unable to connect to API (ENOTFOUND)",
            "■ You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits"));

        Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
    }

    [Fact]
    public void AFullContextWindow_IsItsOwnClass_AndIsNotRecoverableInPhaseOne()
    {
        var fault = TerminatingFaultClassifier.Classify(Screen("Prompt is too long. Try /compact to shorten it."));
        Assert.Equal(SessionFaultClass.ContextFull, fault.Class);
        Assert.False(fault.IsRecoverable);
    }

    [Fact]
    public void ASignInFailureBesideADroppedConnection_Escalates_RatherThanRetrying()
    {
        // Precedence: the class that refuses to act wins. Retrying something that can never succeed on its
        // own is the expensive mistake.
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "API Error: Unable to connect to API (ENOTFOUND)",
            "API Error: 401 {\"type\":\"authentication_error\"}"));

        Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
    }

    [Fact]
    public void AnErrorBannerWeDoNotRecognize_IsUnclassified_NotHealthy()
    {
        // The only input the model fallback accepts. It must not read as a clean turn end.
        var fault = TerminatingFaultClassifier.Classify(Screen("API Error: the flux capacitor desynchronized"));
        Assert.Equal(SessionFaultClass.Unclassified, fault.Class);
    }

    [Fact]
    public void AnUnreadableScreen_IsNeverAFault()
    {
        Assert.Equal(SessionFaultClass.None, TerminatingFaultClassifier.Classify(null).Class);
        Assert.Equal(SessionFaultClass.None, TerminatingFaultClassifier.Classify(Array.Empty<string>()).Class);
        Assert.Equal(SessionFaultClass.None, TerminatingFaultClassifier.Classify(new[] { "", "   ", "\t" }).Class);
    }

    [Fact]
    public void TheContentWindow_DropsChrome_AndKeepsOnlyTheTail()
    {
        var window = TerminatingFaultClassifier.ContentWindow(Screen("first line", "", "second line"), windowLines: 2);

        // The composer box, the border rows, the footer and the blank line are all gone.
        Assert.Equal(new[] { "first line", "second line" }, window);
    }

    [Fact]
    public void AFaultInsideABorderedBox_IsStillRead()
    {
        // Some agents frame their errors. Stripping the border must not hide the fault.
        var fault = TerminatingFaultClassifier.Classify(Screen(
            "│ API Error: Unable to connect to API (ENOTFOUND)   │"));

        Assert.Equal(SessionFaultClass.TransientTransport, fault.Class);
    }
}
