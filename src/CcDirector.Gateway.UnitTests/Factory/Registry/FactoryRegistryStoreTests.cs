using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>
/// THE FACTORY REGISTRY AND ITS GOAL NUMBERS: the store contract (Factories screen mission, phase A). Runs over the
/// real Entity Framework store on a throwaway SQLite file, exactly as the Gateway runs it locally.
///
/// The cases that matter most: re-registering REPLACES the seats rather than merging them (a merge would leave a
/// seat the CEO fired on the owner's Seats tab for ever), a number can only be posted by a seat of a registered
/// factory, and the newest post is the one read back.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoryRegistryStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private FactoryRegistryStore NewStore() => new(_h.Open());

    private static RegisterFactoryRequest WarmForward() => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        Computer = "SOREN_NORTH",
        CeoSeat = "nora-hale",
        GoalText = "A cash engine of $15,000-$40,000 a year that runs without your time.",
        GoalFile = "GOAL.md",
        GoalApprovedOn = "2026-10-04",
        Seats =
        {
            new FactorySeatManifest { Id = "nora-hale", Name = "Nora Hale", Role = "CEO", BriefFile = "agents/ceo.yaml", Schedules = { "cj_a721e6" } },
            new FactorySeatManifest { Id = "savings-engineer", Name = "Savings Engineer", Role = "Savings Engineer", BriefFile = "agents/savings.yaml", Computer = "DEVLINUX" },
        },
    };

    private static PostGoalNumberRequest Number(string value = "not yet proven") => new()
    {
        Factory = "warmforward",
        Value = value,
        Unit = "propane saved this season",
        AsOf = "2026-10-06",
        Link = "https://github.com/thefrederiksen/websites/issues/276",
    };

    // ---------- archive and restore (round 2) ----------

    [Fact]
    public void Archive_KeepsTheEntry_RecordsWhoWhenAndTheSchedules_AndRestoreClearsIt()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "the owner (test)", Now.AddDays(-3));

        var archived = store.Archive(A, "warmforward", "owner (browser)", new[] { "cj_a721e6" }, Now);

        Assert.Equal(Now, archived.ArchivedAtUtc);
        Assert.Equal("owner (browser)", archived.ArchivedBy);
        Assert.Equal(new[] { "cj_a721e6" }, archived.ArchivedSchedules);
        var listed = Assert.Single(store.List(A));
        Assert.Equal(2, listed.Seats.Count);
        Assert.Equal(Now, listed.ArchivedAtUtc);

        var restored = store.Restore(A, "warmforward");
        Assert.Null(restored.ArchivedAtUtc);
        Assert.Null(restored.ArchivedBy);
        Assert.Empty(restored.ArchivedSchedules);
        Assert.Null(store.Find(A, "warmforward")!.ArchivedAtUtc);
    }

    [Fact]
    public void Register_AgainWhileArchived_StaysArchived()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "the owner (test)", Now.AddDays(-3));
        store.Archive(A, "warmforward", "owner (browser)", new[] { "cj_a721e6" }, Now);

        store.Register(A, WarmForward(), "session s-1", Now.AddMinutes(5));

        var f = store.Find(A, "warmforward")!;
        Assert.Equal(Now, f.ArchivedAtUtc);
        Assert.Equal(new[] { "cj_a721e6" }, f.ArchivedSchedules);
    }

    [Fact]
    public void Archive_Twice_Restore_WhenNotArchived_AndAnUnknownFactory_AreRefused()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "the owner (test)", Now);
        store.Archive(A, "warmforward", "owner", Array.Empty<string>(), Now);

        Assert.Equal("WarmForward is already archived.",
            Assert.Throws<FactoryViewValidationException>(() => store.Archive(A, "warmforward", "owner", Array.Empty<string>(), Now)).Message);
        store.Restore(A, "warmforward");
        Assert.Equal("WarmForward is not archived.",
            Assert.Throws<FactoryViewValidationException>(() => store.Restore(A, "warmforward")).Message);
        Assert.Throws<FactoryNotRegisteredException>(() => store.Archive(B, "warmforward", "owner", Array.Empty<string>(), Now));
    }

    // ---------- the registry ----------

    [Fact]
    public void Register_ThenList_ReadsBackEveryField()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "session s1", Now);

        var f = Assert.Single(NewStore().List(A));
        Assert.Equal("warmforward", f.Factory);
        Assert.Equal("WarmForward", f.Title);
        Assert.Equal(@"D:\ReposFred\cc-consult\ideas\warmforward-factory", f.Folder);
        Assert.Equal("SOREN_NORTH", f.Computer);
        Assert.Equal("nora-hale", f.CeoSeat);
        Assert.StartsWith("A cash engine", f.GoalText);
        Assert.Equal("GOAL.md", f.GoalFile);
        Assert.Equal("2026-10-04", f.GoalApprovedOn);
        Assert.Equal("session s1", f.RegisteredBy);
        Assert.Equal(Now, f.RegisteredAtUtc);
        Assert.Equal(new[] { "nora-hale", "savings-engineer" }, f.Seats.Select(s => s.Id));
        Assert.Equal(new[] { "cj_a721e6" }, f.Seats[0].Schedules);
        Assert.Equal("agents/ceo.yaml", f.Seats[0].BriefFile);
    }

    [Fact]
    public void Register_SeatWithoutComputer_RunsOnTheFactorysComputer_SeatWithOne_KeepsIt()
    {
        var f = NewStore().Register(A, WarmForward(), "session s1", Now);
        Assert.Equal("SOREN_NORTH", f.Seats.Single(s => s.Id == "nora-hale").Computer);
        Assert.Equal("DEVLINUX", f.Seats.Single(s => s.Id == "savings-engineer").Computer);
    }

    [Fact]
    public void Register_Again_ReplacesTheWholeRow_SeatsIncluded()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "session s1", Now);

        var second = WarmForward();
        second.Title = "WarmForward Factory";
        second.Seats.RemoveAt(1);
        second.Seats.Add(new FactorySeatManifest { Id = "value-hunter", Name = "Value Hunter", Role = "Value Hunter", BriefFile = "agents/value.yaml" });
        second.GoalText = null;
        second.GoalFile = null;
        second.GoalApprovedOn = null;
        store.Register(A, second, "the owner (device)", Now.AddHours(1));

        var f = Assert.Single(NewStore().List(A));
        Assert.Equal("WarmForward Factory", f.Title);
        Assert.Equal(new[] { "nora-hale", "value-hunter" }, f.Seats.Select(s => s.Id));
        Assert.Null(f.GoalText);
        Assert.Equal("the owner (device)", f.RegisteredBy);
        Assert.Equal(Now.AddHours(1), f.RegisteredAtUtc);
    }

    [Fact]
    public void Register_FactoryIdAndSeatIds_AreFoldedToTheOneSpelling()
    {
        var m = WarmForward();
        m.Factory = " WarmForward ";
        m.CeoSeat = "Nora-Hale";
        m.Seats[0].Id = "NORA-HALE";
        var f = NewStore().Register(A, m, "session s1", Now);
        Assert.Equal("warmforward", f.Factory);
        Assert.Equal("nora-hale", f.CeoSeat);
        Assert.Equal("nora-hale", f.Seats[0].Id);
    }

    [Fact]
    public void Register_IsPerAccount()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "session s1", Now);
        Assert.Empty(store.List(B));
        Assert.Null(store.Find(B, "warmforward"));
        Assert.NotNull(store.Find(A, "WarmForward"));
    }

    [Fact]
    public void List_IsByTitle()
    {
        var store = NewStore();
        var z = WarmForward(); z.Factory = "zeta"; z.Title = "Alpha Works";
        var y = WarmForward(); y.Factory = "alpha"; y.Title = "Zulu Works";
        store.Register(A, y, "s", Now);
        store.Register(A, z, "s", Now);
        Assert.Equal(new[] { "Alpha Works", "Zulu Works" }, store.List(A).Select(f => f.Title));
    }

    public static IEnumerable<object[]> RefusedManifests()
    {
        yield return new object[] { "no factory id", (Action<RegisterFactoryRequest>)(m => m.Factory = "") , "factory" };
        yield return new object[] { "bad factory id", (Action<RegisterFactoryRequest>)(m => m.Factory = "warm forward"), "factory" };
        yield return new object[] { "no title", (Action<RegisterFactoryRequest>)(m => m.Title = " "), "title" };
        yield return new object[] { "relative folder", (Action<RegisterFactoryRequest>)(m => m.Folder = "ideas/warmforward"), "absolute" };
        yield return new object[] { "no computer", (Action<RegisterFactoryRequest>)(m => m.Computer = ""), "computer" };
        yield return new object[] { "no seats", (Action<RegisterFactoryRequest>)(m => m.Seats.Clear()), "seat" };
        yield return new object[] { "two seats one id", (Action<RegisterFactoryRequest>)(m => m.Seats[1].Id = "nora-hale"), "Two seats" };
        yield return new object[] { "CEO not a seat", (Action<RegisterFactoryRequest>)(m => m.CeoSeat = "max-ridley"), "CEO" };
        yield return new object[] { "seat with no name", (Action<RegisterFactoryRequest>)(m => m.Seats[0].Name = ""), "name of seat" };
        yield return new object[] { "seat with no role", (Action<RegisterFactoryRequest>)(m => m.Seats[0].Role = ""), "role of seat" };
        yield return new object[] { "absolute brief", (Action<RegisterFactoryRequest>)(m => m.Seats[0].BriefFile = @"C:\briefs\ceo.yaml"), "relative" };
        yield return new object[] { "brief outside", (Action<RegisterFactoryRequest>)(m => m.Seats[0].BriefFile = "../other/ceo.yaml"), ".." };
        yield return new object[] { "schedule twice", (Action<RegisterFactoryRequest>)(m => m.Seats[0].Schedules.Add("cj_a721e6")), "twice" };
        yield return new object[] { "empty goal", (Action<RegisterFactoryRequest>)(m => m.GoalText = "  "), "goal text is empty" };
        yield return new object[] { "approval without goal", (Action<RegisterFactoryRequest>)(m => { m.GoalText = null; m.GoalFile = null; }), "no goal text" };
        yield return new object[] { "approval not a day", (Action<RegisterFactoryRequest>)(m => m.GoalApprovedOn = "4 Oct 2026"), "YYYY-MM-DD" };
    }

    [Theory]
    [MemberData(nameof(RefusedManifests))]
    public void Register_BadManifest_IsRefusedWithAReason_AndNothingIsStored(string _, Action<RegisterFactoryRequest> spoil, string reasonHas)
    {
        var store = NewStore();
        var m = WarmForward();
        spoil(m);

        var ex = Assert.Throws<FactoryViewValidationException>(() => store.Register(A, m, "session s1", Now));
        Assert.Contains(reasonHas, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List(A));
    }

    [Theory]
    [InlineData(@"D:\ReposFred\x")]
    [InlineData("D:/ReposFred/x")]
    [InlineData("/home/soren/factories/x")]
    [InlineData(@"\\server\share\x")]
    public void Register_AbsoluteFolderOnAnyOperatingSystem_IsAccepted(string folder)
    {
        var m = WarmForward();
        m.Folder = folder;
        Assert.Equal(folder, NewStore().Register(A, m, "s", Now).Folder);
    }

    [Fact]
    public void Register_NoCeo_IsAllowed()
    {
        var m = WarmForward();
        m.CeoSeat = null;
        Assert.Null(NewStore().Register(A, m, "s", Now).CeoSeat);
    }

    // ---------- goal numbers ----------

    [Fact]
    public void PostGoalNumber_KeepsEveryPost_AndTheNewestIsLatest()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        store.PostGoalNumber(A, Number("not yet proven"), "nora-hale", "sess-1", Now);
        store.PostGoalNumber(A, Number("$120"), "nora-hale", "sess-2", Now.AddDays(1));

        var read = NewStore().GoalNumbers(A, "warmforward", 20);
        Assert.Equal(2, read.Count);
        Assert.Equal("$120", read.Latest!.Value);
        Assert.Equal("sess-2", read.Latest.PostedBySession);
        Assert.Equal("nora-hale", read.Latest.PostedBy);
        Assert.Equal(new[] { "$120", "not yet proven" }, read.Posts.Select(p => p.Value));
    }

    [Fact]
    public void GoalNumbers_NoPostYet_HasNoLatest_AndCountZero()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        var read = store.GoalNumbers(A, "warmforward", 20);
        Assert.Null(read.Latest);
        Assert.Equal(0, read.Count);
        Assert.Empty(read.Posts);
    }

    [Fact]
    public void GoalNumbers_CountLimitsThePosts_NotTheTotal()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        for (var i = 0; i < 5; i++) store.PostGoalNumber(A, Number(i.ToString()), "nora-hale", null, Now.AddMinutes(i));
        var read = store.GoalNumbers(A, "warmforward", 2);
        Assert.Equal(5, read.Count);
        Assert.Equal(new[] { "4", "3" }, read.Posts.Select(p => p.Value));
    }

    [Fact]
    public void PostGoalNumber_UnregisteredFactory_IsRefusedAsNotRegistered()
    {
        var ex = Assert.Throws<FactoryNotRegisteredException>(() => NewStore().PostGoalNumber(A, Number(), "nora-hale", null, Now));
        Assert.Contains("factory register", ex.Message);
    }

    [Fact]
    public void PostGoalNumber_ByASeatTheFactoryDoesNotHave_IsRefused()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        var ex = Assert.Throws<FactoryViewValidationException>(() => store.PostGoalNumber(A, Number(), "owner-session", null, Now));
        Assert.Contains("not a seat of warmforward", ex.Message);
        Assert.Equal(0, store.GoalNumbers(A, "warmforward", 20).Count);
    }

    [Fact]
    public void PostGoalNumber_NobodyNamed_IsRefusedAskingForTheSeat()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        var ex = Assert.Throws<FactoryViewValidationException>(() => store.PostGoalNumber(A, Number(), null, null, Now));
        Assert.Contains("--by", ex.Message);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x.html")]
    [InlineData("not a link")]
    public void PostGoalNumber_LinkThatIsNotAWebAddress_IsRefused(string link)
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        var n = Number();
        n.Link = link;
        var ex = Assert.Throws<FactoryViewValidationException>(() => store.PostGoalNumber(A, n, "nora-hale", null, Now));
        Assert.Contains("http", ex.Message);
    }

    [Theory]
    [InlineData("06/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    public void PostGoalNumber_DateThatIsNotADay_IsRefused(string asOf)
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        var n = Number();
        n.AsOf = asOf;
        Assert.Throws<FactoryViewValidationException>(() => store.PostGoalNumber(A, n, "nora-hale", null, Now));
    }

    [Fact]
    public void GoalNumbers_AreThePostingAccountsOnly()
    {
        var store = NewStore();
        store.Register(A, WarmForward(), "s", Now);
        store.Register(B, WarmForward(), "s", Now);
        store.PostGoalNumber(A, Number("A's"), "nora-hale", null, Now);
        Assert.Equal(0, store.GoalNumbers(B, "warmforward", 20).Count);
        Assert.Equal(1, store.GoalNumbers(A, "warmforward", 20).Count);
    }
}
