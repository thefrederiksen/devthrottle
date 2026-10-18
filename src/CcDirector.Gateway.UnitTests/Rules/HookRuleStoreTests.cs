using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Hook rules in the rule store (Session Rules part 2, phase 1): a rule has a kind and says where it applies,
/// a hook rule names its event, tool, handler, literal match text and result, and the write gate is
/// kind-aware (Tech Lead rulings P4 and P7). Screen rules written the September way keep working and apply
/// everywhere.
///
/// Every test runs over an isolated SQLite database through the real migrated schema. Cases that only differ
/// in their input are looped inside one test rather than given a database each, because each database build
/// is paid by everyone on every run of the local gate.
/// </summary>
public sealed class HookRuleStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private const string Repo = "github.com/thefrederiksen/devthrottle";
    private const string Sentence = "Never echo the rule probe in this repository.";

    private SessionRule CreateHook(
        SessionRuleStore store,
        RuleAppliesTo? appliesTo,
        string hookEvent = HookRuleWords.BeforeCommand,
        string tool = HookRuleWords.Shell,
        string handler = HookRuleWords.Match,
        IEnumerable<string>? match = null,
        string result = HookRuleWords.Deny) =>
        store.CreateHook(Sentence, appliesTo, hookEvent, tool, handler, match ?? new[] { "echo RULE-PROBE" }, result, Now);

    private static List<RulePrimitiveCall> GoodCalls() => new()
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    private static SessionRule CreateScreen(SessionRuleStore store, RuleAppliesTo? appliesTo = null) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, appliesTo);

    // ---- creating ----------------------------------------------------------------------------------

    [Fact]
    public void CreateHook_ValidRule_RoundTripsKindAppliesToAndTrigger()
    {
        var created = CreateHook(new SessionRuleStore(Db),
            RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }),
            match: new[] { "echo RULE-PROBE", "rm -rf /" }, result: HookRuleWords.Deny);

        var read = new SessionRuleStore(_h.Open()).Get(created.Id)!;

        Assert.Equal(RuleKinds.Hook, read.Kind);
        Assert.Equal(Sentence, read.Instruction);
        Assert.Equal(RuleState.DryRun, read.State);
        Assert.Equal(RuleAppliesToKinds.RepoOnMachine, read.AppliesTo.Kind);
        Assert.Equal(Repo, read.AppliesTo.Repo);
        Assert.Equal(new[] { "devlinux" }, read.AppliesTo.Machines);
        Assert.Empty(read.AppliesTo.ExceptMachines);
        Assert.NotNull(read.Hook);
        Assert.Equal(HookRuleWords.BeforeCommand, read.Hook!.Event);
        Assert.Equal(HookRuleWords.Shell, read.Hook.Tool);
        Assert.Equal(HookRuleWords.Match, read.Hook.Handler);
        Assert.Equal(new[] { "echo RULE-PROBE", "rm -rf /" }, read.Hook.Match);
        Assert.Equal(HookRuleWords.Deny, read.Hook.Result);
        Assert.Equal(RuleScope.AllSessions, read.Scope);
    }

    [Fact]
    public void CreateHook_RepoPastedAsAnyRemoteForm_IsStoredNormalised()
    {
        var store = new SessionRuleStore(Db);
        var fromUrl = CreateHook(store, RuleAppliesTo.InRepo("https://github.com/TheFrederiksen/devthrottle.git"));
        var fromScp = CreateHook(store, RuleAppliesTo.InRepo("git@github.com:thefrederiksen/devthrottle.git"));

        Assert.Equal(Repo, store.Get(fromUrl.Id)!.AppliesTo.Repo);
        Assert.Equal(Repo, store.Get(fromScp.Id)!.AppliesTo.Repo);
    }

    [Fact]
    public void CreateHook_MissingAppliesTo_IsRefusedBecauseWhereItAppliesMustBeSaid()
    {
        var ex = Assert.Throws<RuleRejectedException>(() => CreateHook(new SessionRuleStore(Db), appliesTo: null));
        Assert.Contains("where", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateHook_AppliesToShapesRulingP4Forbids_AreEachRefusedWithAReason()
    {
        var store = new SessionRuleStore(Db);
        var forbidden = new (string Name, RuleAppliesTo Shape)[]
        {
            ("everywhere with machines", new RuleAppliesTo(RuleAppliesToKinds.Everywhere, new[] { "devlinux" }, Array.Empty<string>(), "")),
            ("everywhere with a repo", new RuleAppliesTo(RuleAppliesToKinds.Everywhere, Array.Empty<string>(), Array.Empty<string>(), Repo)),
            ("machine with no machines", new RuleAppliesTo(RuleAppliesToKinds.Machine, Array.Empty<string>(), Array.Empty<string>(), "")),
            ("machine with both lists", new RuleAppliesTo(RuleAppliesToKinds.Machine, new[] { "a" }, new[] { "b" }, "")),
            ("machine with a repo", new RuleAppliesTo(RuleAppliesToKinds.Machine, new[] { "a" }, Array.Empty<string>(), Repo)),
            ("machine with a blank name", new RuleAppliesTo(RuleAppliesToKinds.Machine, new[] { " " }, Array.Empty<string>(), "")),
            ("repo with no repo", new RuleAppliesTo(RuleAppliesToKinds.Repo, Array.Empty<string>(), Array.Empty<string>(), "")),
            ("repo with machines", new RuleAppliesTo(RuleAppliesToKinds.Repo, new[] { "a" }, Array.Empty<string>(), Repo)),
            ("repo on machine with no machines", new RuleAppliesTo(RuleAppliesToKinds.RepoOnMachine, Array.Empty<string>(), Array.Empty<string>(), Repo)),
            ("repo on machine with no repo", new RuleAppliesTo(RuleAppliesToKinds.RepoOnMachine, new[] { "a" }, Array.Empty<string>(), "")),
            ("repo on machine with except", new RuleAppliesTo(RuleAppliesToKinds.RepoOnMachine, new[] { "a" }, new[] { "b" }, Repo)),
            ("an unknown kind", new RuleAppliesTo("galaxy", Array.Empty<string>(), Array.Empty<string>(), "")),
        };

        foreach (var (name, shape) in forbidden)
        {
            var ex = Assert.Throws<RuleRejectedException>(() => CreateHook(store, shape));
            Assert.False(string.IsNullOrWhiteSpace(ex.Reason), $"'{name}' was refused with no reason.");
        }

        Assert.Empty(store.All());
    }

    [Fact]
    public void CreateHook_EveryAppliesToShapeRulingP4Allows_IsStored()
    {
        // The presence half: a gate that refused every shape would pass the test above.
        var store = new SessionRuleStore(Db);
        CreateHook(store, RuleAppliesTo.Everywhere);
        CreateHook(store, RuleAppliesTo.OnMachines(new[] { "devlinux", "DESKTOP-1" }));
        CreateHook(store, RuleAppliesTo.ExceptOnMachines(new[] { "devlinux" }));
        CreateHook(store, RuleAppliesTo.InRepo(Repo));
        CreateHook(store, RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }));

        Assert.Equal(5, store.All().Count);
    }

    [Fact]
    public void CreateHook_EventToolHandlerOrResultNotBuiltInPhase1_IsRefusedNamingThePhaseThatBringsIt()
    {
        var store = new SessionRuleStore(Db);
        var everywhere = RuleAppliesTo.Everywhere;

        var afterEdit = Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, hookEvent: "after_edit"));
        Assert.Contains("phase 4", afterEdit.Reason, StringComparison.Ordinal);

        var editTool = Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, tool: "edit"));
        Assert.Contains("phase 4", editTool.Reason, StringComparison.Ordinal);

        var check = Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, handler: "check"));
        Assert.Contains("phase 4", check.Reason, StringComparison.Ordinal);

        var ask = Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, handler: "ask"));
        Assert.Contains("phase 5", ask.Reason, StringComparison.Ordinal);

        var tell = Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, result: "tell"));
        Assert.Contains("phase 4", tell.Reason, StringComparison.Ordinal);

        Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, hookEvent: "nonsense"));
        Assert.Throws<RuleRejectedException>(() => CreateHook(store, everywhere, result: ""));

        Assert.Empty(store.All());
    }

    [Fact]
    public void CreateHook_NoMatchTextOrABlankMatch_IsRefused()
    {
        var store = new SessionRuleStore(Db);

        var none = Assert.Throws<RuleRejectedException>(() => CreateHook(store, RuleAppliesTo.Everywhere, match: Array.Empty<string>()));
        Assert.Contains("match", none.Reason, StringComparison.OrdinalIgnoreCase);

        // A blank entry would be contained in every command, so it would match everything.
        var blank = Assert.Throws<RuleRejectedException>(() => CreateHook(store, RuleAppliesTo.Everywhere, match: new[] { "echo", "  " }));
        Assert.Contains("match", blank.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(store.All());
    }

    [Fact]
    public void CreateHook_MatchText_IsKeptExactlyAsWritten()
    {
        // Match is literal text compared ordinally (P1), so a space at the edge is part of what is matched.
        var rule = CreateHook(new SessionRuleStore(Db), RuleAppliesTo.Everywhere, match: new[] { " rm -rf " });
        Assert.Equal(new[] { " rm -rf " }, rule.Hook!.Match);
    }

    // ---- screen rules keep working -------------------------------------------------------------------

    [Fact]
    public void Create_ScreenRuleWithoutAppliesTo_IsAScreenRuleThatAppliesEverywhere()
    {
        var store = new SessionRuleStore(Db);
        var rule = CreateScreen(store);

        var read = store.Get(rule.Id)!;
        Assert.Equal(RuleKinds.Screen, read.Kind);
        Assert.Equal(RuleAppliesToKinds.Everywhere, read.AppliesTo.Kind);
        Assert.Null(read.Hook);

        // Saying everywhere out loud is the same thing.
        Assert.Equal(RuleAppliesToKinds.Everywhere, CreateScreen(store, RuleAppliesTo.Everywhere).AppliesTo.Kind);
    }

    [Fact]
    public void Create_ScreenRuleThatAppliesAnywhereNarrowerThanEverywhere_IsRefusedInPhase1()
    {
        var store = new SessionRuleStore(Db);
        foreach (var narrower in new[]
                 {
                     RuleAppliesTo.InRepo(Repo),
                     RuleAppliesTo.OnMachines(new[] { "devlinux" }),
                     RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }),
                 })
        {
            var ex = Assert.Throws<RuleRejectedException>(() => CreateScreen(store, narrower));
            Assert.Contains("screen", ex.Reason, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(store.All());
    }

    // ---- promotion -----------------------------------------------------------------------------------

    [Fact]
    public void Promote_HookRule_IsRefusedUntilPhase4AndStaysInDryRun()
    {
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store, RuleAppliesTo.Everywhere);
        var grant = RulePromotionGrant.FromAuthenticatedRequest(
            rule.Id, AnInboundRequest.FromDevice(), "I have read this rule's dry-run record and I am making it live.", Now);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(rule.Id, grant, Now));
        Assert.Contains("phase 4", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleState.DryRun, store.Get(rule.Id)!.State);
    }

    // ---- the write gate, reached round the store -----------------------------------------------------

    private static readonly string DryRun = RuleWireNames.ToWireName(nameof(RuleState.DryRun));

    private static SessionRuleEntity StraightHook(string tenant) => new()
    {
        TenantId = tenant,
        Kind = RuleKinds.Hook,
        Instruction = Sentence,
        AppliesToKind = RuleAppliesToKinds.Everywhere,
        HookEvent = HookRuleWords.BeforeCommand,
        HookTool = HookRuleWords.Shell,
        HookHandler = HookRuleWords.Match,
        HookMatch = new List<string> { "echo RULE-PROBE" },
        HookResult = HookRuleWords.Deny,
        State = DryRun,
        CreatedUtc = Now,
        UpdatedUtc = Now,
    };

    [Fact]
    public void SaveChanges_HookRuleWrittenRoundTheStore_MeetsTheSameKindAwareGate()
    {
        var breakers = new (string Name, Action<SessionRuleEntity> Break, string Expect)[]
        {
            ("a live hook rule", r => r.State = RuleWireNames.ToWireName(nameof(RuleState.Live)), "dry run"),
            ("a September scope", r => r.ScopeAgent = "claude_code", "scope"),
            ("checks", r => r.Calls = GoodCalls(), "phase 4"),
            ("a screen description", r => r.ScreenDescription = "something on the screen", "screen"),
            ("trigger words", r => r.TriggerWords = new List<string> { "limit" }, "words"),
            ("a repository not in its one spelling", r =>
            {
                r.AppliesToKind = RuleAppliesToKinds.Repo;
                r.AppliesToRepo = "https://github.com/TheFrederiksen/devthrottle.git";
            }, "spelling"),
            ("a session start rule", r => r.Kind = RuleKinds.SessionStart, "phase 5"),
            ("an unknown kind", r => r.Kind = "telepathy", "telepathy"),
        };

        foreach (var (name, breakIt, expect) in breakers)
        {
            using var ctx = Db.CreateContext();
            var row = StraightHook(ctx.ActiveTenant!);
            breakIt(row);
            ctx.SessionRules.Add(row);
            var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
            Assert.True(ex.Reason.Contains(expect, StringComparison.OrdinalIgnoreCase),
                $"{name}: expected the reason to mention '{expect}', got: {ex.Reason}");
        }

        Assert.Empty(new SessionRuleStore(Db).All());
    }

    [Fact]
    public void SaveChanges_WellFormedHookRuleRoundTheStore_IsAccepted()
    {
        using (var ctx = Db.CreateContext())
        {
            ctx.SessionRules.Add(StraightHook(ctx.ActiveTenant!));
            ctx.SaveChanges();
        }
        Assert.Equal(RuleKinds.Hook, Assert.Single(new SessionRuleStore(Db).All()).Kind);
    }

    [Fact]
    public void SaveChanges_ScreenRuleCarryingHookFields_IsRefused()
    {
        var store = new SessionRuleStore(Db);
        var rule = CreateScreen(store);

        using var ctx = Db.CreateContext();
        var row = ctx.SessionRules.First(r => r.Id == rule.Id);
        row.HookMatch = new List<string> { "echo" };

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("screen", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
