using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE ONE CHECK OF A MAIL DESK RULES DOCUMENT (Mail Desk mission, phase 1, contract section 3). One test per rule of
/// section 3, each asserting that the refusal names the field and says how to fix it - a refusal that only says "bad
/// input" leaves a person or a session guessing, and the command prints these sentences verbatim.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskRulesValidatorTests
{
    private static MailDeskRouteTarget To(string factory, string seat) => new() { Factory = factory, Seat = seat };

    /// <summary>The contract's own example document, which must pass.</summary>
    private static MailDeskRulesDocument Example() => new()
    {
        Rules = new[]
        {
            new MailDeskRule
            {
                Name = "citizenship",
                When = new MailDeskRuleWhen { SubjectContains = new[] { "citizen" }, FromAny = new[] { "a@example.com", "b@example.com" } },
                Route = To("citizenship", "intake"),
            },
        },
        Allow = new[]
        {
            new MailDeskAllowEntry { From = "*@notifications.example.com", What = "build notifications", Do = MailDeskAllowDo.Archive },
            new MailDeskAllowEntry { From = "news@example.org", What = "a newsletter I read", Do = MailDeskAllowDo.KeepForOwner },
            new MailDeskAllowEntry { From = "billing@example.net", What = "invoices", Do = MailDeskAllowDo.RouteTo("books", "clerk") },
        },
        Block = new[] { new MailDeskBlockEntry { From = "*@spammy-domain.example", Why = "cold sales, never answered" } },
        Routes = new[] { new MailDeskRouteEntry { Factory = "devthrottle", Seat = "mail-desk", Handles = "customers and users of DevThrottle" } },
    };

    private static string RefusalOf(MailDeskRulesDocument document)
    {
        var result = MailDeskRulesValidator.Validate(document);
        Assert.False(result.IsValid);
        Assert.Null(result.Document);
        return result.Refusal;
    }

    // ---------- the route cap (phase 3, section 3) ----------

    private static MailDeskRulesDocument WithRoutes(int count) => new()
    {
        Routes = Enumerable.Range(1, count)
            .Select(i => new MailDeskRouteEntry { Factory = "books", Seat = $"clerk-{i}", Handles = $"bills of kind {i}" }).ToList(),
    };

    [Fact]
    public void Validate_TwentyFiveRoutes_IsValid()
    {
        // Act
        var result = MailDeskRulesValidator.Validate(WithRoutes(MailDeskRulesValidator.MaxRoutes));

        // Assert
        Assert.True(result.IsValid, result.Refusal);
        Assert.Equal(25, result.Document!.Routes.Count);
    }

    [Fact]
    public void Validate_TheTwentySixthRoute_IsRefusedNamingTheCapAndTheFix()
    {
        // Act
        var refusal = RefusalOf(WithRoutes(26));

        // Assert
        Assert.Contains("26 routes are listed and at most 25 may be", refusal);
        Assert.Contains("remove a route before adding another", refusal);
    }

    // ---------- what passes, and the one spelling ----------

    [Fact]
    public void Validate_TheContractExample_IsValid()
    {
        // Arrange
        var document = Example();

        // Act
        var result = MailDeskRulesValidator.Validate(document);

        // Assert
        Assert.True(result.IsValid, result.Refusal);
        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Document!.Allow.Count);
    }

    [Fact]
    public void Validate_EmptyDocument_IsValid()
    {
        var result = MailDeskRulesValidator.Validate(MailDeskRulesDocument.Empty);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MixedCaseAndSpaces_FoldsPatternsFactoriesAndSeatsToOneSpelling()
    {
        // Arrange
        var document = Example() with
        {
            Allow = new[] { new MailDeskAllowEntry { From = "  News@Example.ORG ", What = " a newsletter ", Do = MailDeskAllowDo.RouteTo("Books", "Clerk") } },
            Block = new[] { new MailDeskBlockEntry { From = "*@Spammy-Domain.Example", Why = "spam" } },
        };

        // Act
        var folded = MailDeskRulesValidator.Validate(document).Document!;

        // Assert
        Assert.Equal("news@example.org", folded.Allow[0].From);
        Assert.Equal("a newsletter", folded.Allow[0].What);
        Assert.Equal("books/clerk", folded.Allow[0].Do!.Route!.Key);
        Assert.Equal("*@spammy-domain.example", folded.Block[0].From);
    }

    [Fact]
    public void Validate_RulesInAGivenOrder_KeepsThatOrderBecauseTheFirstMatchWins()
    {
        // Arrange
        var rule = Example().Rules[0];
        var document = MailDeskRulesDocument.Empty
            .WithRule(rule with { Name = "second-added-first" })
            .WithRule(rule with { Name = "alpha" });

        // Act
        var folded = MailDeskRulesValidator.Validate(document).Document!;

        // Assert
        Assert.Equal(new[] { "second-added-first", "alpha" }, folded.Rules.Select(r => r.Name));
    }

    // ---------- patterns ----------

    [Theory]
    [InlineData("news@*.example.org")]
    [InlineData("*news@example.org")]
    [InlineData("*")]
    [InlineData("*@*")]
    [InlineData("example.org")]
    [InlineData("news@")]
    [InlineData("news @example.org")]
    [InlineData("a@b@example.org")]
    public void Validate_PatternThatIsNotAnAddressOrWholeDomain_IsRefusedNamingTheFieldAndTheFix(string pattern)
    {
        var document = MailDeskRulesDocument.Empty.WithBlock(new MailDeskBlockEntry { From = pattern, Why = "spam" });

        var refusal = RefusalOf(document);

        Assert.Contains("block entry 1: 'from'", refusal);
        Assert.Contains("*@example.org", refusal);
        Assert.Contains("no other wildcard", refusal);
    }

    [Fact]
    public void Validate_BlankPattern_IsRefusedSayingWhatToGive()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry { From = " ", What = "x", Do = MailDeskAllowDo.Archive });

        var refusal = RefusalOf(document);

        Assert.Contains("allow entry 1: 'from' is needed", refusal);
    }

    [Fact]
    public void Validate_BadPatternInARulesFromAny_IsRefusedNamingTheRuleAndEntry()
    {
        var rule = Example().Rules[0] with { When = new MailDeskRuleWhen { FromAny = new[] { "a@example.com", "b@*" } } };

        var refusal = RefusalOf(MailDeskRulesDocument.Empty.WithRule(rule));

        Assert.Contains("rule 'citizenship': 'when.fromAny' entry 2", refusal);
    }

    [Fact]
    public void Validate_PatternOnBothAllowAndBlock_IsRefusedSayingRemoveItFromOne()
    {
        var document = Example() with
        {
            Block = new[] { new MailDeskBlockEntry { From = "News@example.org", Why = "too many" } },
        };

        var refusal = RefusalOf(document);

        Assert.Contains("'news@example.org' is on both the allow-list and the block list; remove it from one of them", refusal);
    }

    [Fact]
    public void Validate_PatternTwiceOnTheAllowList_IsRefused()
    {
        var document = Example().WithAllow(new MailDeskAllowEntry { From = "*@NOTIFICATIONS.example.com", What = "again", Do = MailDeskAllowDo.Archive });

        var refusal = RefusalOf(document);

        Assert.Contains("allow: '*@notifications.example.com' is on the allow-list more than once; keep one entry for it", refusal);
    }

    [Fact]
    public void Validate_PatternTwiceOnTheBlockList_IsRefused()
    {
        var document = Example().WithBlock(new MailDeskBlockEntry { From = "*@spammy-domain.example", Why = "again" });

        var refusal = RefusalOf(document);

        Assert.Contains("block: '*@spammy-domain.example' is on the block list more than once", refusal);
    }

    // ---------- rules ----------

    [Fact]
    public void Validate_RuleNamesThatDifferOnlyInCase_AreRefusedAsOneNameUsedTwice()
    {
        var rule = Example().Rules[0];
        var document = MailDeskRulesDocument.Empty.WithRule(rule).WithRule(rule with { Name = "Citizenship" });

        var refusal = RefusalOf(document);

        Assert.Contains("rules: the name 'citizenship' is used by more than one rule; give each rule its own name", refusal);
    }

    [Fact]
    public void Validate_BlankRuleName_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithRule(Example().Rules[0] with { Name = "  " });

        var refusal = RefusalOf(document);

        Assert.Contains("rules entry 1: 'name' is blank; give the rule a name", refusal);
    }

    [Fact]
    public void Validate_RuleWithNoCondition_IsRefused()
    {
        var rule = Example().Rules[0] with { When = new MailDeskRuleWhen() };

        var refusal = RefusalOf(MailDeskRulesDocument.Empty.WithRule(rule));

        Assert.Contains("rule 'citizenship': 'when' has no condition; give 'subjectContains', 'fromAny', or both", refusal);
    }

    [Fact]
    public void Validate_RuleWithNoWhenAtAll_IsRefusedAsHavingNoCondition()
    {
        var rule = Example().Rules[0] with { When = null };

        var refusal = RefusalOf(MailDeskRulesDocument.Empty.WithRule(rule));

        Assert.Contains("'when' has no condition", refusal);
    }

    [Fact]
    public void Validate_RuleWithOnlyASubjectCondition_IsValid()
    {
        var rule = Example().Rules[0] with { When = new MailDeskRuleWhen { SubjectContains = new[] { "citizen" } } };

        Assert.True(MailDeskRulesValidator.Validate(MailDeskRulesDocument.Empty.WithRule(rule)).IsValid);
    }

    [Fact]
    public void Validate_BlankSubjectWord_IsRefused()
    {
        var rule = Example().Rules[0] with { When = new MailDeskRuleWhen { SubjectContains = new[] { "citizen", " " } } };

        var refusal = RefusalOf(MailDeskRulesDocument.Empty.WithRule(rule));

        Assert.Contains("rule 'citizenship': 'when.subjectContains' entry 2 is blank", refusal);
    }

    [Fact]
    public void Validate_RuleWithNoRoute_IsRefused()
    {
        var rule = Example().Rules[0] with { Route = null };

        var refusal = RefusalOf(MailDeskRulesDocument.Empty.WithRule(rule));

        Assert.Contains("rule 'citizenship': 'route' is missing; say where the rule sends the email", refusal);
    }

    // ---------- do ----------

    [Fact]
    public void Validate_AllowEntryWithNoDo_IsRefusedListingWhatDoMayBe()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry { From = "a@example.com", What = "x" });

        var refusal = RefusalOf(document);

        Assert.Contains("allow entry 'a@example.com': 'do' is missing; give 'archive' or 'keep-for-owner' or { \"route\"", refusal);
    }

    [Fact]
    public void Validate_AllowEntryWithAnUnknownDo_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry
        {
            From = "a@example.com", What = "x", Do = new MailDeskAllowDo { Action = "delete" },
        });

        var refusal = RefusalOf(document);

        Assert.Contains("'do' is 'delete', which the Mail Desk does not know", refusal);
    }

    [Fact]
    public void Validate_AllowEntryWithBothAWordAndARoute_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry
        {
            From = "a@example.com", What = "x", Do = new MailDeskAllowDo { Action = "archive", Route = To("books", "clerk") },
        });

        var refusal = RefusalOf(document);

        Assert.Contains("'do' is either a word or a route, not both", refusal);
    }

    // ---------- factories and seats ----------

    [Fact]
    public void Validate_FactoryThatIsNotAFactoryName_IsRefusedWithTheFactoryRule()
    {
        var document = MailDeskRulesDocument.Empty.WithRoute(new MailDeskRouteEntry { Factory = "my factory!", Seat = "desk", Handles = "x" });

        var refusal = RefusalOf(document);

        Assert.Contains("routes entry 1: 'factory' is not a factory name", refusal);
        Assert.Contains("lower-case letters and digits joined by single hyphens", refusal);
    }

    [Fact]
    public void Validate_BlankSeat_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry
        {
            From = "a@example.com", What = "x", Do = MailDeskAllowDo.RouteTo("books", " "),
        });

        var refusal = RefusalOf(document);

        Assert.Contains("allow entry 'a@example.com': 'do.route': 'seat' is blank; name the seat", refusal);
    }

    // ---------- routes ----------

    [Fact]
    public void Validate_RouteListedTwice_IsRefused()
    {
        var document = Example().WithRoute(new MailDeskRouteEntry { Factory = "DevThrottle", Seat = "Mail-Desk", Handles = "again" });

        var refusal = RefusalOf(document);

        Assert.Contains("routes: 'devthrottle/mail-desk' is listed more than once; keep one line for it", refusal);
    }

    [Fact]
    public void Validate_RouteWithABlankHandlesLine_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithRoute(new MailDeskRouteEntry { Factory = "devthrottle", Seat = "mail-desk", Handles = "" });

        var refusal = RefusalOf(document);

        Assert.Contains("route 'devthrottle/mail-desk': 'handles' is blank; say in one line what this seat handles", refusal);
    }

    // ---------- what and why ----------

    [Fact]
    public void Validate_AllowEntryWithABlankWhat_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithAllow(new MailDeskAllowEntry { From = "a@example.com", What = " ", Do = MailDeskAllowDo.Archive });

        var refusal = RefusalOf(document);

        Assert.Contains("allow entry 'a@example.com': 'what' is blank; say what these emails are", refusal);
    }

    [Fact]
    public void Validate_BlockEntryWithABlankWhy_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithBlock(new MailDeskBlockEntry { From = "*@example.net", Why = "" });

        var refusal = RefusalOf(document);

        Assert.Contains("block entry '*@example.net': 'why' is blank; say why this sender is blocked", refusal);
    }

    [Fact]
    public void Validate_TextLongerThanTheLimit_IsRefused()
    {
        var document = MailDeskRulesDocument.Empty.WithBlock(new MailDeskBlockEntry
        {
            From = "*@example.net", Why = new string('x', MailDeskRulesValidator.MaxTextLength + 1),
        });

        var refusal = RefusalOf(document);

        Assert.Contains($"'why' takes at most {MailDeskRulesValidator.MaxTextLength} characters", refusal);
    }

    [Fact]
    public void Validate_SeveralProblems_ReportsEveryOne()
    {
        var document = MailDeskRulesDocument.Empty
            .WithBlock(new MailDeskBlockEntry { From = "bad", Why = "" })
            .WithRoute(new MailDeskRouteEntry { Factory = "devthrottle", Seat = "mail-desk", Handles = "" });

        var result = MailDeskRulesValidator.Validate(document);

        Assert.Equal(3, result.Problems.Count);
    }

    [Fact]
    public void Validate_NullDocument_IsRefused()
    {
        var result = MailDeskRulesValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Contains("the rules document is missing", result.Refusal);
    }

    // ---------- the JSON shape ----------

    [Fact]
    public void ToJsonAndParse_TheContractExample_RoundTripsWithEveryFormOfDo()
    {
        // Arrange
        var document = MailDeskRulesValidator.Validate(Example()).Document!;

        // Act
        var json = document.ToJson();
        var back = MailDeskRulesDocument.Parse(json);

        // Assert
        Assert.Contains("\"do\":\"archive\"", json);
        Assert.Contains("\"do\":\"keep-for-owner\"", json);
        Assert.Contains("\"do\":{\"route\":{\"factory\":\"books\",\"seat\":\"clerk\"}}", json);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void TryParse_TheContractsJsonText_ReadsIt()
    {
        const string json = """
            {
              "rules":  [ { "name": "citizenship",
                            "when": { "subjectContains": ["citizen"], "fromAny": ["a@example.com", "b@example.com"] },
                            "route": { "factory": "citizenship", "seat": "intake" } } ],
              "allow":  [ { "from": "*@notifications.example.com", "what": "build notifications", "do": "archive" },
                          { "from": "billing@example.net", "what": "invoices", "do": { "route": { "factory": "books", "seat": "clerk" } } } ],
              "block":  [ { "from": "*@spammy-domain.example", "why": "cold sales, never answered" } ],
              "routes": [ { "factory": "devthrottle", "seat": "mail-desk", "handles": "customers and users of DevThrottle" } ]
            }
            """;

        var read = MailDeskRulesDocument.TryParse(json, out var document, out var refusal);

        Assert.True(read, refusal);
        Assert.True(MailDeskRulesValidator.Validate(document).IsValid);
        Assert.Equal("books/clerk", document!.Allow[1].Do!.Route!.Key);
    }

    [Fact]
    public void TryParse_AMisspeltField_IsRefusedRatherThanDropped()
    {
        const string json = """{ "rules": [ { "name": "x", "when": { "subjectContain": ["a"] }, "route": { "factory": "a", "seat": "b" } } ] }""";

        var read = MailDeskRulesDocument.TryParse(json, out _, out var refusal);

        Assert.False(read);
        Assert.Contains("cannot be read as JSON", refusal);
    }

    [Fact]
    public void TryParse_AListLeftOut_IsAnEmptyList()
    {
        var read = MailDeskRulesDocument.TryParse("""{ "block": [] }""", out var document, out _);

        Assert.True(read);
        Assert.Empty(document!.Rules);
        Assert.Empty(document.Allow);
        Assert.Empty(document.Routes);
    }

    // ---------- removing an entry by its key ----------

    [Fact]
    public void WithoutAllow_AnyCaseOfThePattern_RemovesThatEntryOnly()
    {
        var document = Example();

        var after = document.WithoutAllow("NEWS@example.org")!;

        Assert.Equal(new[] { "*@notifications.example.com", "billing@example.net" }, after.Allow.Select(a => a.From));
    }

    [Fact]
    public void WithoutRuleWithoutRouteWithoutBlock_AKeyThatIsNotThere_IsNull()
    {
        var document = Example();

        Assert.Null(document.WithoutRule("nothing"));
        Assert.Null(document.WithoutRoute("devthrottle/nobody"));
        Assert.Null(document.WithoutBlock("x@example.com"));
        Assert.NotNull(document.WithoutRoute("DevThrottle/Mail-Desk"));
        Assert.NotNull(document.WithoutRule("CITIZENSHIP"));
    }
}
