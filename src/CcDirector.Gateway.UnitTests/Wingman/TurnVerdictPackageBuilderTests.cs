using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The package builder: what one stop is judged from, and what is deliberately left out of it.
///
/// PARKED SUITE. This assembly does not run in the default gate, so these run under -Parked. That is
/// why the CONTRACT's tests live in Core.UnitTests instead: the rules that decide whether a session
/// goes quiet have to be checked at commit time, and only the assembly of the package is checked here.
///
/// Every conversation below is written from scratch. Not a byte of a real session is in this public
/// repository.
/// </summary>
public sealed class TurnVerdictPackageBuilderTests
{
    private const string SessionId = "S-1";
    private const string DirectorId = "D-1";
    private static readonly TenantId Tenant = TenantId.Local;

    private static TurnEndSignal Signal() => new(SessionId, DirectorId, Tenant, IsNewTurn: true);

    private static SessionDto Session() => new()
    {
        SessionId = SessionId,
        DirectorId = DirectorId,
        Agent = "ClaudeCode",
        Name = "devthrottle - the retention sweep",
    };

    private static ScreenGridResponse Screen(params string[] rows) => new()
    {
        SessionId = SessionId,
        Rows = rows.ToList(),
        CursorRow = rows.Length - 1,
        CursorVisible = true,
        HasGrid = true,
    };

    private static TurnWidgetDto User(string text) => new()
    {
        Kind = StoredConversationWidgets.UserTextKind,
        Content = text,
    };

    private static TurnWidgetDto Agent(string text) => new()
    {
        Kind = StoredConversationWidgets.AgentTextKind,
        Content = text,
    };

    private static TurnWidgetDto Tool(string name, string content) => new()
    {
        Kind = "ToolUse",
        Header = name,
        Content = content,
    };

    private static StoredConversation Conversation(params TurnWidgetDto[] widgets)
        => new(IsSupported: true, widgets);

    // ================================================================= what travels with the stop

    [Fact]
    public void Build_KeepsTheLastFourTurnsAndNoMore()
    {
        var widgets = new List<TurnWidgetDto>();
        for (var turn = 1; turn <= 6; turn++)
        {
            widgets.Add(User($"ask number {turn}"));
            widgets.Add(Agent($"answer number {turn}"));
        }

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), Conversation(widgets.ToArray()), Screen("> "), previousVerdictLabel: null);

        // Turn six's answer IS the stop being judged, so the four turns before it are two, three, four
        // and five. One and two are out because four is the bound; six is out because it is the stop.
        Assert.DoesNotContain("ask number 1", package.RecentTurns);
        Assert.Contains("ask number 2", package.RecentTurns);
        Assert.Contains("ask number 5", package.RecentTurns);
        Assert.Contains("answer number 5", package.RecentTurns);
        Assert.DoesNotContain("answer number 6", package.RecentTurns);
        Assert.Equal("answer number 6", package.LatestReply);
    }

    [Fact]
    public void Build_TheJudgedTurnNeverTakesOneOfTheFourSlots_WhateverItIsMadeOf()
    {
        // The shape this used to get wrong. The turn being judged is not "the person spoke and then the
        // agent replied" - it is everything from the person's last message onwards, and a real one
        // usually says something, calls a tool, and only then replies. The rule used to be "drop the last
        // turn only if the agent said nothing in it", which is false of every turn that did any work, so
        // the judged turn was kept and the judge saw three turns of history plus the ask it was already
        // reading the answer to.
        var widgets = new List<TurnWidgetDto>();
        for (var turn = 1; turn <= 4; turn++)
        {
            widgets.Add(User($"ask number {turn}"));
            widgets.Add(Agent($"answer number {turn}"));
        }
        widgets.Add(User("the ask being judged"));
        widgets.Add(Agent("thinking about it out loud"));
        widgets.Add(Tool("Grep", "pattern=RetentionSweep, 412 matches"));
        widgets.Add(Agent("the reply being judged"));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), Conversation(widgets.ToArray()), Screen("> "), previousVerdictLabel: null);

        Assert.Equal("the reply being judged", package.LatestReply);

        // Four and only four turns, and every one of them is a turn BEFORE the stop.
        Assert.Equal(4, CountTurns(package.RecentTurns));
        for (var turn = 1; turn <= 4; turn++)
        {
            Assert.Contains($"ask number {turn}", package.RecentTurns);
            Assert.Contains($"answer number {turn}", package.RecentTurns);
        }
        Assert.DoesNotContain("the ask being judged", package.RecentTurns);
        Assert.DoesNotContain("thinking about it out loud", package.RecentTurns);
        Assert.DoesNotContain("the reply being judged", package.RecentTurns);
    }

    /// <summary>How many turns the recent-context block holds: one per thing the person said in it.</summary>
    private static int CountTurns(string recentTurns)
        => recentTurns.Split("You: ").Length - 1;

    [Fact]
    public void Build_DropsToolCallsAndTheirResults()
    {
        var conversation = Conversation(
            User("find the retention sweep"),
            Tool("Grep", "pattern=RetentionSweep, 412 matches across 88 files"),
            Tool("Read", "src/CcDirector.Gateway/Retention/RetentionSweep.cs, 1200 lines"),
            Agent("Found it in the retention sweep."),
            User("now delete the old rows"),
            Agent("Deleted them, and the test covers it."));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.Contains("find the retention sweep", package.RecentTurns);
        Assert.Contains("Found it in the retention sweep.", package.RecentTurns);
        Assert.DoesNotContain("412 matches", package.RecentTurns);
        Assert.DoesNotContain("1200 lines", package.RecentTurns);
    }

    [Fact]
    public void Build_CutsALongReplyFromItsStartSoTheEndSurvives()
    {
        var opening = new string('a', TurnVerdictPackage.MaxLatestReplyChars);
        var reply = "OPENING-" + opening + "-THE-DECISION-IS-AT-THE-END";
        var conversation = Conversation(User("do the work"), Agent(reply));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.NotNull(package.LatestReply);
        Assert.Equal(TurnVerdictPackage.MaxLatestReplyChars, package.LatestReply!.Length);
        Assert.EndsWith("-THE-DECISION-IS-AT-THE-END", package.LatestReply);
        Assert.DoesNotContain("OPENING-", package.LatestReply);
    }

    [Fact]
    public void Build_CutsTheRecentTurnsFromTheirOldestEnd()
    {
        var bulk = new string('b', TurnVerdictPackage.MaxRecentTurnsChars);
        var conversation = Conversation(
            User("OLDEST-ASK " + bulk),
            Agent("NEWEST-ANSWER-BEFORE-THE-STOP"),
            User("and now the last thing"),
            Agent("the reply being judged"));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackage.MaxRecentTurnsChars, package.RecentTurns.Length);
        Assert.EndsWith("NEWEST-ANSWER-BEFORE-THE-STOP", package.RecentTurns);
        Assert.DoesNotContain("OLDEST-ASK", package.RecentTurns);

        // The ask that opened the turn being judged is not "before" the stop and is excluded; the judge
        // reads the reply to it as the thing being judged.
        Assert.DoesNotContain("and now the last thing", package.RecentTurns);
        Assert.Equal("the reply being judged", package.LatestReply);
    }

    // ================================================================= no conversation

    [Fact]
    public void Build_NoConversation_SaysSoRatherThanLookingEmpty()
    {
        var package = TurnVerdictPackageBuilder.Build(
            Signal(),
            Session(),
            new StoredConversation(IsSupported: false, Array.Empty<TurnWidgetDto>()),
            Screen("some agent that keeps no conversation", "> "),
            previousVerdictLabel: null);

        Assert.False(package.ConversationAvailable);
        Assert.Null(package.LatestReply);
        Assert.Equal("", package.RecentTurns);
        Assert.Null(package.FirstUserPrompt);
        // The screen is all there is, and it is carried in full so the receipt can come off it.
        Assert.Equal(2, package.ScreenRows.Count);
        Assert.NotEqual("", package.ScreenHash);
    }

    [Fact]
    public void Build_SupportedButEmptyConversation_IsAlsoNotAvailable()
    {
        var package = TurnVerdictPackageBuilder.Build(
            Signal(),
            Session(),
            new StoredConversation(IsSupported: true, Array.Empty<TurnWidgetDto>()),
            Screen("> "),
            previousVerdictLabel: null);

        Assert.False(package.ConversationAvailable);
    }

    [Fact]
    public void Build_SupportedAndNonEmptyConversation_IsAvailable()
    {
        // The guard the two tests above cannot give. They prove a MISSING conversation reports false, so
        // a substituted constant false stays green under them; only this fails when that substitution is
        // made. The fact decides what the judge is told about what it is NOT being shown.
        var conversation = Conversation(
            User("Delete the rows the retention window has passed."),
            Agent("Deleted them, and the test covers it."));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.True(package.ConversationAvailable);
    }

    // ================================================================= the two kinds

    [Fact]
    public void Build_ReplyAfterTheLastThingThePersonSaid_IsAnAgentReplyStop()
    {
        var conversation = Conversation(User("run the sweep"), Agent("Done, and nothing is needed from you."));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackageKind.AgentReply, package.Kind);
        Assert.Equal("Done, and nothing is needed from you.", package.LatestReply);
        Assert.Null(package.FailureText);
        Assert.Equal("agent-reply", TurnVerdictPackage.WireName(package.Kind));
    }

    [Fact]
    public void Build_NoReplyAndAFailureOnScreen_IsATerminalFailureStop()
    {
        var conversation = Conversation(
            User("run the sweep"),
            Agent("starting now"),
            User("keep going"));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(),
            Session(),
            conversation,
            Screen("Error: connection reset by peer while reading the response", ""),
            previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackageKind.TerminalFailure, package.Kind);
        Assert.Null(package.LatestReply);
        Assert.NotNull(package.FailureText);
        Assert.Contains("connection reset by peer", package.FailureText);
        Assert.Equal("terminal-failure", TurnVerdictPackage.WireName(package.Kind));
    }

    [Fact]
    public void Build_NoConversationAndAFailureOnScreen_IsStillATerminalFailureStop()
    {
        // A screen-only agent whose turn ended on a failure. The narration path has nothing to select
        // from, so the screen is classified directly; otherwise this stop would be judged as an ordinary
        // reply stop and a calm verdict on it would be accepted.
        var package = TurnVerdictPackageBuilder.Build(
            Signal(),
            Session(),
            new StoredConversation(IsSupported: false, Array.Empty<TurnWidgetDto>()),
            Screen("Error: connection reset by peer while reading the response", ""),
            previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackageKind.TerminalFailure, package.Kind);
        Assert.Contains("connection reset by peer", package.FailureText);
    }

    [Fact]
    public void Build_NoReplyAndNoFailure_IsAnAgentReplyStopWithNoReply()
    {
        // The person spoke last and the screen shows nothing wrong. There is no reply to judge, but the
        // stop is not a failure either, so a calm verdict on it stays possible and the receipt must come
        // off the screen.
        var conversation = Conversation(User("run the sweep"), Agent("starting now"), User("keep going"));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("working on it", "> "), previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackageKind.AgentReply, package.Kind);
        Assert.Null(package.LatestReply);
        Assert.Null(package.FailureText);
    }

    // ================================================================= the rest of the facts

    [Fact]
    public void Build_CarriesTheFactsAJudgeNeedsAndNamesTheOnesNothingProduces()
    {
        var conversation = Conversation(
            User("Delete the rows the retention window has passed."),
            Agent("Done."));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: "Retention sweep done");

        Assert.Equal("devthrottle - the retention sweep", package.SessionTitle);
        Assert.Equal("ClaudeCode", package.AgentKind);
        Assert.Equal("Delete the rows the retention window has passed.", package.FirstUserPrompt);
        Assert.Equal("Retention sweep done", package.PreviousVerdictLabel);

        // NOT PROVEN, and deliberately so: nothing in this build stamps a turn-end cause, a turn-end
        // confidence, a pending wake-up count or a next scheduled wake. These are null for every session
        // today, the judge is told so in as many words, and this test exists to fail the day a producer
        // appears and nobody wires it through here.
        Assert.Null(package.TurnEndCause);
        Assert.Null(package.TurnEndConfidence);
        Assert.Null(package.PendingWakeUps);
        Assert.Null(package.NextScheduledWakeUtc);
    }

    [Fact]
    public void Build_CutsTheFirstUserPromptAtItsBound()
    {
        var longPrompt = new string('p', TurnVerdictPackage.MaxFirstUserPromptChars + 200);
        var conversation = Conversation(User(longPrompt), Agent("Done."));

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("> "), previousVerdictLabel: null);

        Assert.Equal(TurnVerdictPackage.MaxFirstUserPromptChars, package.FirstUserPrompt!.Length);
    }

    [Fact]
    public void Build_UnreadableScreen_CarriesNoRowsRatherThanAnEmptyScreen()
    {
        // A session with no server-side grid parser. An empty row list is honest; a list of blank rows
        // would look like a screen that was read and found empty, and a judge cannot tell those apart.
        var conversation = Conversation(User("run it"), Agent("Done."));
        var noGrid = new ScreenGridResponse { SessionId = SessionId, HasGrid = false };

        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, noGrid, previousVerdictLabel: null);

        Assert.Empty(package.ScreenRows);
        Assert.Equal("", package.ScreenHash);
    }

    [Fact]
    public void Build_ScreenHash_IsOverTheWholeGridAndChangesWithAnyRepaint()
    {
        var conversation = Conversation(User("run it"), Agent("Done."));
        var first = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("line one", "line two"), previousVerdictLabel: null);
        var repainted = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen("line one", "line two changed"), previousVerdictLabel: null);

        Assert.NotEqual(first.ScreenHash, repainted.ScreenHash);
        Assert.Equal(
            WingmanScreenVerdictCache.HashRows(new[] { "line one", "line two" }),
            first.ScreenHash);
    }

    // ========================================================= the hash, against a fixed vector
    //
    // The three tests below exist because the one above cannot catch a wrong hash: it compares the
    // builder with the same implementation the builder calls, so a hash that dropped the first row
    // would agree with itself and stay green. A hash that silently ignores a row is how a changed
    // screen gets served a stale verdict, which is the one thing the fingerprint exists to prevent.

    /// <summary>The three rows the vector below was computed from. Any change to them changes the
    /// literal, which is the point: the literal is an independent statement of what the hash IS.</summary>
    private static readonly string[] FixedRows =
    {
        "the retention sweep is done",
        "nothing is needed from you",
        "> ",
    };

    /// <summary>SHA-256 over those three rows joined with line feeds, as upper-case hexadecimal.
    /// Computed outside this program, so it is a fact about the algorithm rather than a re-statement
    /// of this repository's code.</summary>
    private const string FixedRowsHash =
        "F5CF73E839EB835DBB38DD7CE5D545737A53F76517127CE1953CDF553BE747C2";

    [Fact]
    public void ScreenHash_OfKnownRows_IsTheKnownValue()
    {
        Assert.Equal(FixedRowsHash, WingmanScreenVerdictCache.HashRows(FixedRows));

        var conversation = Conversation(User("run it"), Agent("Done."));
        var package = TurnVerdictPackageBuilder.Build(
            Signal(), Session(), conversation, Screen(FixedRows), previousVerdictLabel: null);

        Assert.Equal(FixedRowsHash, package.ScreenHash);
    }

    [Fact]
    public void ScreenHash_ChangesWhenTheFIRSTRowChanges()
    {
        var changed = FixedRows.ToArray();
        changed[0] = "the retention sweep is still running";

        Assert.NotEqual(FixedRowsHash, WingmanScreenVerdictCache.HashRows(changed));
    }

    [Fact]
    public void ScreenHash_ChangesWhenTheLASTRowChanges()
    {
        var changed = FixedRows.ToArray();
        changed[^1] = "> y";

        Assert.NotEqual(FixedRowsHash, WingmanScreenVerdictCache.HashRows(changed));
    }
}
