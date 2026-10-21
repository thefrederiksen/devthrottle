using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE FOUR SCREENING LAYERS (Mail Desk phase 3 contract, section 3), over a fake model whose call log is the proof of
/// which questions were asked. Each layer deciding alone, their order, a known sender skipping the sender screen, each
/// threshold below, at and above, the answers off the labels or with no confidence, no routes, the spend limit before
/// each call, and a model failure.
///
/// What this file does NOT prove, because the engine does not do it (task F wires the engine in): decision rows, a
/// reread, the third failed attempt, a mailbox's memory, and observe mode starting nothing.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskScreeningTests
{
    private const int Version = 7;
    private const string Stranger = "someone@example.net";
    private const string Marker = "PREVIEW-MARKER-7f3a";

    private static MailDeskRouteTarget To(string factory, string seat) => new() { Factory = factory, Seat = seat };

    /// <summary>A document in its one spelling, as the store holds it.</summary>
    private static MailDeskRulesVersion Rules(Func<MailDeskRulesDocument, MailDeskRulesDocument>? adjust = null)
    {
        var document = new MailDeskRulesDocument
        {
            Rules = new[]
            {
                new MailDeskRule
                {
                    Name = "citizenship",
                    When = new MailDeskRuleWhen { SubjectContains = new[] { "citizen" }, FromAny = new[] { "*@example.com" } },
                    Route = To("citizenship", "intake"),
                },
            },
            Allow = new[]
            {
                new MailDeskAllowEntry { From = "*@example.com", What = "the example family", Do = MailDeskAllowDo.Archive },
                new MailDeskAllowEntry { From = "news@example.org", What = "a newsletter I read", Do = MailDeskAllowDo.KeepForOwner },
                new MailDeskAllowEntry { From = "billing@example.org", What = "invoices", Do = MailDeskAllowDo.RouteTo("books", "clerk") },
            },
            Block = new[]
            {
                new MailDeskBlockEntry { From = "sales@example.com", Why = "cold sales, never answered" },
                new MailDeskBlockEntry { From = "*@spammy.example", Why = "a spam domain" },
            },
            Routes = new[]
            {
                new MailDeskRouteEntry { Factory = "devthrottle", Seat = "mail-desk", Handles = "customers and users of DevThrottle" },
                new MailDeskRouteEntry { Factory = "books", Seat = "clerk", Handles = "bills and receipts" },
            },
        };
        var validation = MailDeskRulesValidator.Validate(adjust is null ? document : adjust(document));
        Assert.True(validation.IsValid, validation.Refusal);
        return new MailDeskRulesVersion(Version, null, null, null, validation.Document!);
    }

    private static MailDeskMessage Message(string sender = Stranger, string subject = "A question", bool known = false,
        string preview = "Hello, I have a question about my account.")
        => new("key-1", new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc), sender, "Some One", subject, known, preview);

    private static MailDeskModelResult Label(string label, double? confidence, decimal cost = 0.0001m)
        => new(MailDeskAnswerKind.Label, label, confidence, cost, 120, 1, null, null);

    private static MailDeskModelResult OffLabels(decimal cost = 0.0001m)
        => new(MailDeskAnswerKind.OffLabels, null, null, cost, 120, 2, null, null);

    private static MailDeskModelResult Failure(string kind) => MailDeskModelResult.Failure(kind, "the hosted model answered with an error");

    /// <summary>A model that answers from two queues and logs every question it was asked.</summary>
    private sealed class FakeModel : IMailDeskModel
    {
        public readonly Queue<MailDeskModelResult> PersonOrSpam = new();
        public readonly Queue<MailDeskModelResult> Route = new();
        public readonly List<string> Calls = new();
        public readonly List<IReadOnlyList<MailDeskRouteChoice>> RouteOffers = new();
        public readonly List<MailDeskEmail> Emails = new();

        public Task<MailDeskModelResult> AskPersonOrSpamAsync(MailDeskEmail email, CancellationToken ct = default)
        {
            Calls.Add("person-or-spam");
            Emails.Add(email);
            return Task.FromResult(PersonOrSpam.Dequeue());
        }

        public Task<MailDeskModelResult> AskRouteAsync(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes, CancellationToken ct = default)
        {
            Calls.Add("route");
            Emails.Add(email);
            RouteOffers.Add(routes);
            return Task.FromResult(Route.Dequeue());
        }
    }

    private static MailDeskSpendLedger Ledger(decimal spent = 0m, decimal limit = 5.00m) => new(limit, spent);

    private static Task<MailDeskScreeningDecision> Decide(MailDeskMessage message, FakeModel model, MailDeskRulesVersion? rules = null,
        MailDeskSettings? settings = null, MailDeskSpendLedger? spend = null)
        => MailDeskScreening.DecideAsync(message, rules ?? Rules(), settings ?? MailDeskSettings.Defaults, model, spend ?? Ledger());

    // ======================================================================================================
    // Each layer deciding alone
    // ======================================================================================================

    [Fact]
    public async Task DecideAsync_RuleMatchesSubjectAndSender_RoutesByTheRuleAndAsksNothing()
    {
        // Arrange
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message("Clerk@Example.COM", "Your CITIZENSHIP papers"), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Rule, MailDeskWords.Decisions.Route, "citizenship", "intake", "rule citizenship"),
            (decision.Layer, decision.Decision, decision.RouteFactory, decision.RouteSeat, decision.Reason));
        Assert.Equal(Version, decision.RulesVersion);
        Assert.Null(decision.Confidence);
        Assert.Null(decision.CostUsd);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task DecideAsync_RuleSubjectMatchesButSenderDoesNot_TheRuleDoesNotDecide()
    {
        // Arrange: both conditions are given, so both must hold.
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message("news@example.org", "citizen news"), model);

        // Assert
        Assert.Equal(MailDeskWords.Layers.Allow, decision.Layer);
    }

    [Fact]
    public async Task DecideAsync_RuleWithOnlySubjectWords_MatchesAnySender()
    {
        // Arrange
        var rules = Rules(d => d with
        {
            Rules = new[] { new MailDeskRule { Name = "invoices", When = new MailDeskRuleWhen { SubjectContains = new[] { "bill", "invoice" } }, Route = To("books", "clerk") } },
        });

        // Act
        var decision = await Decide(Message(subject: "Your INVOICE for October"), new FakeModel(), rules);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Rule, "books", "clerk"), (decision.Layer, decision.RouteFactory, decision.RouteSeat));
    }

    [Theory]
    [InlineData("friend@example.com", MailDeskWords.Decisions.Archive, null, null, "allow *@example.com (the example family)")]
    [InlineData("News@Example.org", MailDeskWords.Decisions.KeepForOwner, null, null, "allow news@example.org (a newsletter I read)")]
    [InlineData("billing@example.org", MailDeskWords.Decisions.Route, "books", "clerk", "allow billing@example.org (invoices)")]
    public async Task DecideAsync_AllowListMatch_DecidesItsDoAndNeverAsksTheModel(string sender, string expected, string? factory,
        string? seat, string reason)
    {
        // Arrange
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message(sender), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Allow, expected, factory, seat, reason),
            (decision.Layer, decision.Decision, decision.RouteFactory, decision.RouteSeat, decision.Reason));
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task DecideAsync_BlockListMatch_JunksWithItsReasonAndAsksNothing()
    {
        // Arrange
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message("anyone@SPAMMY.example"), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Block, MailDeskWords.Decisions.Junk, "block *@spammy.example (a spam domain)"),
            (decision.Layer, decision.Decision, decision.Reason));
        Assert.Empty(model.Calls);
    }

    // ======================================================================================================
    // The order
    // ======================================================================================================

    [Fact]
    public async Task DecideAsync_MatchesRuleAllowAndBlock_TheRuleWins()
    {
        // Arrange: sales@example.com matches the rule's *@example.com, the allow-list's *@example.com and the block list.
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message("sales@example.com", "citizen question"), model);

        // Assert
        Assert.Equal(MailDeskWords.Layers.Rule, decision.Layer);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task DecideAsync_MatchesAllowAndBlock_TheAllowListWins()
    {
        // Arrange: no rule matches the subject; *@example.com allows what sales@example.com blocks.
        var model = new FakeModel();

        // Act
        var decision = await Decide(Message("sales@example.com", "A quote for you"), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Allow, MailDeskWords.Decisions.Archive), (decision.Layer, decision.Decision));
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task DecideAsync_FirstRuleInDocumentOrder_Wins()
    {
        // Arrange
        var rules = Rules(d => d with
        {
            Rules = new[]
            {
                new MailDeskRule { Name = "first", When = new MailDeskRuleWhen { SubjectContains = new[] { "order" } }, Route = To("books", "clerk") },
                new MailDeskRule { Name = "second", When = new MailDeskRuleWhen { SubjectContains = new[] { "order" } }, Route = To("citizenship", "intake") },
            },
        });

        // Act
        var decision = await Decide(Message(subject: "your order"), new FakeModel(), rules);

        // Assert
        Assert.Equal("rule first", decision.Reason);
    }

    [Fact]
    public async Task DecideAsync_SubdomainOfAllowedDomain_IsNotMatched()
    {
        // Arrange: *@example.com is that domain only.
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", 0.99));

        // Act
        var decision = await Decide(Message("someone@mail.example.com"), model);

        // Assert
        Assert.Equal(MailDeskWords.Layers.SenderScreen, decision.Layer);
    }

    [Fact]
    public async Task DecideAsync_EmptySender_MatchesNoPatternAndIsUnknown()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", 0.99));

        // Act
        var decision = await Decide(Message(""), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Junk), (decision.Layer, decision.Decision));
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
    }

    // ======================================================================================================
    // 3b. The unknown-sender screen
    // ======================================================================================================

    [Fact]
    public async Task DecideAsync_KnownSender_SkipsTheSenderScreenAndAsksOnlyTheRoute()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label("devthrottle/mail-desk", 0.93));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal(new[] { "route" }, model.Calls);
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route, "devthrottle", "mail-desk"),
            (decision.Layer, decision.Decision, decision.RouteFactory, decision.RouteSeat));
    }

    [Theory]
    [InlineData(0.97, "sender-screen: spam at 0.97 (threshold 0.95)")]
    [InlineData(0.95, "sender-screen: spam at 0.95 (threshold 0.95)")]
    public async Task DecideAsync_SpamAtOrAboveThreshold_Junks(double confidence, string reason)
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", confidence));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Junk, reason, confidence),
            (decision.Layer, decision.Decision, decision.Reason, decision.Confidence));
        Assert.Equal(0.0001m, decision.CostUsd);
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
    }

    [Fact]
    public async Task DecideAsync_SpamBelowThreshold_GoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", 0.80));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.MailDeskAgent, "sender-screen: spam at 0.80, under the threshold 0.95"),
            (decision.Layer, decision.Decision, decision.Reason));
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
    }

    [Fact]
    public async Task DecideAsync_SpamJustBelowThreshold_IsNotRoundedUpToIt()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", 0.9499));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal(MailDeskWords.Decisions.MailDeskAgent, decision.Decision);
        Assert.Equal("sender-screen: spam at 0.9499, under the threshold 0.95", decision.Reason);
    }

    [Fact]
    public async Task DecideAsync_SpamWithNoConfidence_IsJunkAndSaysNoConfidenceCame()
    {
        // Arrange: the included fast model on DeepInfra returns no log probabilities, so a label comes with none.
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", null));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.Junk, "sender-screen: spam, no confidence returned", (double?)null),
            (decision.Decision, decision.Reason, decision.Confidence));
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
    }

    [Fact]
    public async Task DecideAsync_SenderScreenOffLabels_GoesToTheMailDeskAgentAndIsCharged()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(OffLabels(0.0002m));
        var spend = Ledger(spent: 1m);

        // Act
        var decision = await Decide(Message(), model, spend: spend);

        // Assert
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.MailDeskAgent, "sender-screen: answered outside the labels"),
            (decision.Layer, decision.Decision, decision.Reason));
        Assert.Equal(0.0002m, decision.CostUsd);
        Assert.Equal(1.0002m, spend.SpentUsd);
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(0.51)]
    public async Task DecideAsync_PersonAtAnyConfidence_GoesOnToTheRouteQuestion(double confidence)
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("person", confidence, 0.0001m));
        model.Route.Enqueue(Label("books/clerk", 0.91, 0.0002m));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal(new[] { "person-or-spam", "route" }, model.Calls);
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route, "books", "clerk", 0.91),
            (decision.Layer, decision.Decision, decision.RouteFactory, decision.RouteSeat, decision.Confidence));
        Assert.Equal(0.0003m, decision.CostUsd);
        Assert.Equal(300, decision.CostMicros);
    }

    [Fact]
    public async Task DecideAsync_SpamThresholdFromSettings_IsTheOneApplied()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("spam", 0.80));
        var settings = MailDeskSettings.Defaults with { SpamThreshold = 0.75m };

        // Act
        var decision = await Decide(Message(), model, settings: settings);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.Junk, "sender-screen: spam at 0.80 (threshold 0.75)"), (decision.Decision, decision.Reason));
    }

    // ======================================================================================================
    // 4. The route question
    // ======================================================================================================

    [Theory]
    [InlineData(0.93, "route-model: devthrottle/mail-desk at 0.93 (threshold 0.90)")]
    [InlineData(0.90, "route-model: devthrottle/mail-desk at 0.90 (threshold 0.90)")]
    public async Task DecideAsync_RouteAtOrAboveThreshold_Routes(double confidence, string reason)
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label("devthrottle/mail-desk", confidence));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.Route, "devthrottle", "mail-desk", reason),
            (decision.Decision, decision.RouteFactory, decision.RouteSeat, decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_RouteBelowThreshold_GoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label("books/clerk", 0.89));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.MailDeskAgent, null, "route-model: books/clerk at 0.89, under the threshold 0.90", 0.89),
            (decision.Decision, decision.RouteFactory, decision.Reason, decision.Confidence));
    }

    [Fact]
    public async Task DecideAsync_RouteThresholdFromSettings_IsTheOneApplied()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label("books/clerk", 0.93));
        var settings = MailDeskSettings.Defaults with { RouteThreshold = 0.95m };

        // Act
        var decision = await Decide(Message(known: true), model, settings: settings);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.MailDeskAgent, "route-model: books/clerk at 0.93, under the threshold 0.95"),
            (decision.Decision, decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_RouteUnsure_GoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label(MailDeskModel.UnsureLabel, 0.99));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.MailDeskAgent, "route-model: unsure"),
            (decision.Layer, decision.Decision, decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_RouteOffLabels_GoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(OffLabels());

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.MailDeskAgent, "route-model: answered outside the labels"), (decision.Decision, decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_RouteWithNoConfidence_IsRoutedAndSaysNoConfidenceCame()
    {
        // Arrange: the included fast model on DeepInfra returns no log probabilities, so a label comes with none.
        var model = new FakeModel();
        model.Route.Enqueue(Label("books/clerk", null));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.Route, "route-model: books/clerk, no confidence returned", (double?)null),
            (decision.Decision, decision.Reason, decision.Confidence));
        Assert.Equal(("books", "clerk"), (decision.RouteFactory, decision.RouteSeat));
    }

    [Fact]
    public async Task DecideAsync_UnsureWithNoConfidence_StillGoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label(MailDeskModel.UnsureLabel, null));

        // Act
        var decision = await Decide(Message(known: true), model);

        // Assert
        Assert.Equal((MailDeskWords.Decisions.MailDeskAgent, "route-model: unsure"), (decision.Decision, decision.Reason));
    }

    [Fact]
    public async Task DecideAsync_RouteQuestion_OffersEveryRouteInDocumentOrderByItsKeyAndLine()
    {
        // Arrange
        var model = new FakeModel();
        model.Route.Enqueue(Label("books/clerk", 0.95));

        // Act
        await Decide(Message(known: true), model);

        // Assert
        Assert.Equal(new[]
        {
            new MailDeskRouteChoice("devthrottle/mail-desk", "customers and users of DevThrottle"),
            new MailDeskRouteChoice("books/clerk", "bills and receipts"),
        }, model.RouteOffers.Single());
    }

    [Fact]
    public async Task DecideAsync_NoRoutes_AsksNoRouteQuestionAndGoesToTheMailDeskAgent()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("person", 0.9, 0.0001m));
        var rules = Rules(d => d with { Routes = Array.Empty<MailDeskRouteEntry>() });

        // Act
        var decision = await Decide(Message(), model, rules);

        // Assert
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.MailDeskAgent, MailDeskScreening.NoRoutesReason, 0.0001m),
            (decision.Layer, decision.Decision, decision.Reason, decision.CostUsd));
    }

    [Fact]
    public async Task DecideAsync_KnownSenderAndNoRoutes_AsksNothing()
    {
        // Arrange
        var model = new FakeModel();
        var rules = Rules(d => d with { Routes = Array.Empty<MailDeskRouteEntry>() });

        // Act
        var decision = await Decide(Message(known: true), model, rules);

        // Assert
        Assert.Empty(model.Calls);
        Assert.Equal(MailDeskScreening.NoRoutesReason, decision.Reason);
        Assert.Null(decision.CostUsd);
    }

    // ======================================================================================================
    // The spend limit, before each call
    // ======================================================================================================

    [Fact]
    public async Task DecideAsync_SpendAtTheLimit_AsksNothingAndFailsNamingTheLimit()
    {
        // Arrange
        var model = new FakeModel();
        var spend = Ledger(spent: 5.00m, limit: 5.00m);

        // Act
        var decision = await Decide(Message(), model, spend: spend);

        // Assert
        Assert.Empty(model.Calls);
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Failed,
                "the Mail Desk spend limit of $5.00 is reached; the model was not asked", MailDeskScreening.SpendLimitErrorKind),
            (decision.Layer, decision.Decision, decision.Reason, decision.ErrorKind));
        Assert.True(decision.SpendLimitReached);
        Assert.False(decision.ModelQuestionFailed);
        Assert.Null(decision.CostUsd);
    }

    [Fact]
    public async Task DecideAsync_FirstQuestionCrossesTheLimit_TheSecondIsNeverAsked()
    {
        // Arrange: just under the limit, the sender screen is asked and its cost takes the spend over.
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("person", 0.9, 0.0002m));
        var spend = Ledger(spent: 4.9999m, limit: 5.00m);

        // Act
        var decision = await Decide(Message(), model, spend: spend);

        // Assert
        Assert.Equal(new[] { "person-or-spam" }, model.Calls);
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Failed, MailDeskScreening.SpendLimitErrorKind),
            (decision.Layer, decision.Decision, decision.ErrorKind));
        Assert.Equal(0.0002m, decision.CostUsd);
        Assert.Equal(5.0001m, spend.SpentUsd);
    }

    [Fact]
    public async Task DecideAsync_SpendLimitReached_RulesAndListsStillDecide()
    {
        // Arrange
        var model = new FakeModel();
        var spend = Ledger(spent: 9m, limit: 5m);

        // Act
        var decision = await Decide(Message("news@example.org"), model, spend: spend);

        // Assert
        Assert.Equal((MailDeskWords.Layers.Allow, MailDeskWords.Decisions.KeepForOwner), (decision.Layer, decision.Decision));
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task DecideAsync_SeveralMessagesOnOneLedger_TheCallThatWouldStartAtTheLimitIsNeverMade()
    {
        // Arrange: three known senders, each one route question at 0.0001; the limit allows two.
        var model = new FakeModel();
        for (var i = 0; i < 3; i++) model.Route.Enqueue(Label("books/clerk", 0.95, 0.0001m));
        var spend = Ledger(spent: 0m, limit: 0.0002m);
        var rules = Rules();

        // Act
        var decisions = new List<MailDeskScreeningDecision>();
        for (var i = 0; i < 3; i++)
            decisions.Add(await MailDeskScreening.DecideAsync(Message(known: true), rules, MailDeskSettings.Defaults, model, spend));

        // Assert
        Assert.Equal(new[] { "route", "route" }, model.Calls);
        Assert.Equal(new[] { MailDeskWords.Decisions.Route, MailDeskWords.Decisions.Route, MailDeskWords.Decisions.Failed },
            decisions.Select(d => d.Decision));
        Assert.True(decisions[2].SpendLimitReached);
    }

    // ======================================================================================================
    // A model failure
    // ======================================================================================================

    [Theory]
    [InlineData("http-500")]
    [InlineData("timeout")]
    [InlineData("no-usage")]
    public async Task DecideAsync_SenderScreenFails_IsFailedWithTheErrorKindToBeReadAgain(string kind)
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Failure(kind));
        var spend = Ledger(spent: 1m);

        // Act
        var decision = await Decide(Message(), model, spend: spend);

        // Assert
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Failed, kind),
            (decision.Layer, decision.Decision, decision.ErrorKind));
        Assert.True(decision.ModelQuestionFailed);
        Assert.False(decision.SpendLimitReached);
        Assert.Equal($"sender-screen: the model could not be asked ({kind}); read again next run", decision.Reason);
        Assert.Null(decision.CostUsd);
        Assert.Equal(1m, spend.SpentUsd);
    }

    [Fact]
    public async Task DecideAsync_RouteQuestionFailsAfterAnAnsweredScreen_KeepsTheScreensCost()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("person", 0.9, 0.0001m));
        model.Route.Enqueue(Failure("http-429"));

        // Act
        var decision = await Decide(Message(), model);

        // Assert
        Assert.Equal((MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Failed, "http-429", 0.0001m),
            (decision.Layer, decision.Decision, decision.ErrorKind, decision.CostUsd));
        Assert.True(decision.ModelQuestionFailed);
    }

    // ======================================================================================================
    // What the model is shown, and what is kept
    // ======================================================================================================

    [Fact]
    public async Task DecideAsync_ModelQuestion_IsShownTheSenderSubjectAndPreview()
    {
        // Arrange
        var model = new FakeModel();
        model.PersonOrSpam.Enqueue(Label("person", 0.9));
        model.Route.Enqueue(Label("books/clerk", 0.95));

        // Act
        await Decide(Message(subject: "A bill", preview: "The total is 42 dollars."), model);

        // Assert
        Assert.All(model.Emails, e => Assert.Equal(new MailDeskEmail("Some One", Stranger, "A bill", "The total is 42 dollars."), e));
    }

    [Fact]
    public async Task DecideAsync_EveryKindOfDecision_NeverPutsThePreviewInTheReason()
    {
        // Arrange: one message per outcome, each with the marker in its preview.
        var cases = new List<(MailDeskMessage Message, Action<FakeModel> Answers)>
        {
            (Message("x@example.com", "citizen", preview: Marker), _ => { }),
            (Message("news@example.org", preview: Marker), _ => { }),
            (Message("a@spammy.example", preview: Marker), _ => { }),
            (Message(preview: Marker), m => m.PersonOrSpam.Enqueue(Label("spam", 0.99))),
            (Message(preview: Marker), m => m.PersonOrSpam.Enqueue(OffLabels())),
            (Message(preview: Marker), m => { m.PersonOrSpam.Enqueue(Label("person", 0.9)); m.Route.Enqueue(Label("books/clerk", 0.95)); }),
            (Message(known: true, preview: Marker), m => m.Route.Enqueue(OffLabels())),
            (Message(known: true, preview: Marker), m => m.Route.Enqueue(Failure("http-500"))),
        };

        foreach (var (message, answers) in cases)
        {
            var model = new FakeModel();
            answers(model);

            // Act
            var decision = await Decide(message, model);

            // Assert
            Assert.DoesNotContain(Marker, decision.Reason);
            Assert.DoesNotContain('\n', decision.Reason);
        }
    }
}
