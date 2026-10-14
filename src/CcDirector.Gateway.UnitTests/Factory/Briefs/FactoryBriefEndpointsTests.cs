using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// A SEAT'S BRIEF, THROUGH ITS ROUTES (Factory Seats mission, phase 2a), driven through the route handlers over the
/// real store, the real registry and a real database. These pin what only the route knows: WHO IS ASKING. A person
/// and a session of the factory may save a draft; only a person publishes; a session of another factory reaches
/// nothing; and a refusal carries the words and the version its caller needs.
///
/// A refusal is proven by the answer that carries it - its status and its sentence - never by the absence of an
/// error.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryBriefEndpointsTests : IDisposable
{
    private const string TheFactory = "warmforward";
    private const string Boss = "boss";
    private const string FactorySession = "72000000-0000-4000-8000-000000000001";
    private const string SecondFactorySession = "72000000-0000-4000-8000-000000000002";
    private const string OtherFactorySession = "72000000-0000-4000-8000-000000000003";
    private const string NoFactorySession = "72000000-0000-4000-8000-000000000004";
    private const string UnknownSession = "72000000-0000-4000-8000-000000000005";
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryBriefStore _store;

    public FactoryBriefEndpointsTests()
    {
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, Manifest(), "the owner of A (test)", Now);
        registry.Register(B, Manifest(), "the owner of B (test)", Now);
        _store = new FactoryBriefStore(db, registry);
    }

    public void Dispose() => _h.Dispose();

    private static RegisterFactoryRequest Manifest() => new()
    {
        Factory = TheFactory,
        Title = "WarmForward",
        Folder = "/home/soren/warmforward-factory",
        Computer = "DEVLINUX",
        BossSeat = Boss,
        Seats = { new FactorySeatManifest { Id = Boss, Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
    };

    // ---------- who is asking ----------

    private static DefaultHttpContext Person()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static DefaultHttpContext Session(string id)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(id), A, "dir-a");
        return ctx;
    }

    /// <summary>The account's shared machine token, or a Director: a verified credential that is neither a person's
    /// device nor a session's key.</summary>
    private static DefaultHttpContext Machine()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        return ctx;
    }

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        FactorySession or SecondFactorySession => SessionFactoryLookup.In(TheFactory),
        OtherFactorySession => SessionFactoryLookup.In("invoice-factory"),
        NoFactorySession => SessionFactoryLookup.InNoFactory,
        _ => SessionFactoryLookup.NotKnown,
    };

    private static TenantId? InA(HttpContext _) => A;
    private static TenantId? InB(HttpContext _) => B;

    // ---------- the routes ----------

    private IResult Save(HttpContext ctx, string text, int expect, Func<HttpContext, TenantId?>? tenant = null, string seat = Boss) =>
        FactoryBriefEndpoints.SaveDraft(ctx, TheFactory, seat, new SaveFactoryBriefDraftRequest { Text = text, ExpectedVersion = expect },
            _store, tenant ?? InA, FactoryOf, () => Now);

    private IResult Publish(HttpContext ctx, int version) =>
        FactoryBriefEndpoints.Publish(ctx, TheFactory, Boss, new PublishFactoryBriefRequest { Version = version },
            _store, InA, FactoryOf, () => Now.AddMinutes(5));

    private IResult BringBack(HttpContext ctx, int version, int expect = 0) =>
        FactoryBriefEndpoints.BringBack(ctx, TheFactory, Boss, new BringBackFactoryBriefRequest { Version = version, ExpectedVersion = expect },
            _store, InA, FactoryOf, () => Now.AddMinutes(10));

    private IResult Published(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null, string seat = Boss) =>
        FactoryBriefEndpoints.ReadPublished(ctx, TheFactory, seat, _store, tenant ?? InA, FactoryOf);

    private IResult Newest(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryBriefEndpoints.ReadNewest(ctx, TheFactory, Boss, _store, tenant ?? InA, FactoryOf);

    private IResult History(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null, string? query = null)
    {
        if (query is not null) ctx.Request.QueryString = new QueryString("?" + query);
        return FactoryBriefEndpoints.History(ctx, TheFactory, Boss, _store, tenant ?? InA, FactoryOf);
    }

    private static FactoryBriefHistoryResponse Page(IResult result) =>
        Assert.IsType<JsonHttpResult<FactoryBriefHistoryResponse>>(result).Value!;

    private IResult OneVersion(HttpContext ctx, string version, Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryBriefEndpoints.ReadVersion(ctx, TheFactory, Boss, version, _store, tenant ?? InA, FactoryOf);

    // ---------- reading an answer ----------

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static FactoryBriefDto Brief(IResult result) => Assert.IsType<JsonHttpResult<FactoryBriefDto>>(result).Value!;

    private static object? Field(IResult result, string name)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var property = value.GetType().GetProperty(name);
        Assert.True(property is not null, $"the answer has no '{name}'");
        return property!.GetValue(value);
    }

    private static string ErrorOf(IResult result) => (string)Field(result, "error")!;

    private int VersionsKept() => _store.History(A, TheFactory, Boss).Total;

    // ---------- the flow: draft, published, a second version published, the first brought back ----------

    [Fact]
    public void THE_WHOLE_FLOW_a_draft_is_saved_published_replaced_and_the_first_is_brought_back()
    {
        // A session of the factory proposes the brief.
        var draft = Brief(Save(Session(FactorySession), "You are the boss of WarmForward.", expect: 0));
        Assert.Equal(1, draft.Version);
        Assert.Equal(FactoryBriefStates.Draft, draft.State);
        Assert.Equal("session", draft.AuthorKind);
        Assert.Equal(FactorySession, draft.AuthorId);
        Assert.Null(draft.PublishedVersion);

        // Until a person publishes, a run is given nothing from here - and the answer says a draft is waiting.
        var notYet = Published(Person());
        Assert.Equal(404, Status(notYet));
        Assert.Contains("no published brief yet: its newest version, 1, is a draft", ErrorOf(notYet));

        // The owner publishes it.
        var first = Brief(Publish(Person(), 1));
        Assert.Equal(FactoryBriefStates.Published, first.State);
        Assert.Equal(Now.AddMinutes(5), first.PublishedAtUtc);
        Assert.Equal(1, first.PublishedVersion);
        Assert.Equal("You are the boss of WarmForward.", Brief(Published(Session(FactorySession))).Text);

        // A second version, written and published by the owner.
        var second = Brief(Save(Person(), "You are the boss. Post the goal number every run.", expect: 1));
        Assert.Equal(2, second.Version);
        Assert.Equal("person", second.AuthorKind);
        Assert.Equal(1, second.PublishedVersion);
        Assert.Equal(2, Brief(Publish(Person(), 2)).PublishedVersion);
        Assert.Equal("You are the boss. Post the goal number every run.", Brief(Published(Person())).Text);

        // The first is brought back: a copy of it, as version 3, published.
        var back = Brief(BringBack(Person(), 1, expect: 2));
        Assert.Equal(3, back.Version);
        Assert.Equal("You are the boss of WarmForward.", back.Text);
        Assert.Equal(FactoryBriefStates.Published, back.State);
        Assert.Equal(3, back.PublishedVersion);

        var history = Assert.IsType<JsonHttpResult<FactoryBriefHistoryResponse>>(History(Person())).Value!;
        Assert.Equal(TheFactory, history.Factory);
        Assert.Equal(Boss, history.Seat);
        Assert.Equal(3, history.PublishedVersion);
        Assert.Equal(new[] { 3, 2, 1 }, history.Versions.Select(v => v.Version).ToArray());
        Assert.All(history.Versions, v => Assert.Equal(FactoryBriefStates.Published, v.State));
    }

    [Fact]
    public void The_newest_version_is_what_an_editor_reads_and_it_says_which_version_is_published()
    {
        Save(Person(), "published", expect: 0);
        Publish(Person(), 1);
        Save(Session(FactorySession), "a newer draft", expect: 1);

        var newest = Brief(Newest(Person()));

        Assert.Equal(2, newest.Version);
        Assert.Equal("a newer draft", newest.Text);
        Assert.Equal(FactoryBriefStates.Draft, newest.State);
        Assert.Equal(2, newest.NewestVersion);
        Assert.Equal(1, newest.PublishedVersion);
        // And the published read is still version 1, and says something newer is waiting.
        var published = Brief(Published(Person()));
        Assert.Equal(1, published.Version);
        Assert.Equal(2, published.NewestVersion);
    }

    [Fact]
    public void A_seat_with_no_brief_on_the_Gateway_answers_NOT_FOUND_in_words()
    {
        var published = Published(Person());
        Assert.Equal(404, Status(published));
        Assert.Contains("has no brief on the Gateway", ErrorOf(published));
        Assert.Equal(0, Field(published, "newestVersion"));

        Assert.Equal(404, Status(Newest(Person())));

        var history = Assert.IsType<JsonHttpResult<FactoryBriefHistoryResponse>>(History(Person())).Value!;
        Assert.Empty(history.Versions);
        Assert.Null(history.PublishedVersion);
    }

    // ---------- only a person publishes ----------

    [Fact]
    public void A_SESSIONS_PUBLISH_IS_REFUSED_WITH_THE_SENTENCE_THAT_SAYS_SO()
    {
        Save(Session(FactorySession), "a session's draft", expect: 0);

        var refused = Publish(Session(FactorySession), 1);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryBriefStore.OnlyAPersonPublishes, ErrorOf(refused));
        Assert.Equal("Only a person publishes a seat's brief. A session of the factory may save a draft; the owner reads it and publishes it.",
            ErrorOf(refused));
        Assert.Equal("OnlyAPersonPublishes", Field(refused, "outcome"));
        // Nothing was published.
        Assert.Equal(404, Status(Published(Person())));
    }

    [Fact]
    public void A_SESSIONS_BRING_BACK_IS_REFUSED_too_because_it_publishes()
    {
        Save(Person(), "v1", expect: 0);
        Publish(Person(), 1);
        Save(Person(), "v2", expect: 1);
        Publish(Person(), 2);

        var refused = BringBack(Session(FactorySession), 1);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryBriefStore.OnlyAPersonBringsBack, ErrorOf(refused));
        Assert.Equal(2, VersionsKept());
        Assert.Equal(2, Brief(Published(Person())).Version);
    }

    // ---------- a session outside the factory reaches nothing ----------

    [Fact]
    public void A_SESSION_OF_ANOTHER_FACTORY_MAY_NOT_READ_OR_WRITE_THIS_FACTORYS_BRIEF()
    {
        Save(Person(), "the brief", expect: 0);
        Publish(Person(), 1);
        var outside = OtherFactorySession;

        foreach (var refused in new[]
                 {
                     Published(Session(outside)), Newest(Session(outside)), History(Session(outside)),
                     OneVersion(Session(outside), "1"),
                     Save(Session(outside), "an outsider's instructions", expect: 1),
                     Publish(Session(outside), 1), BringBack(Session(outside), 1),
                 })
        {
            Assert.Equal(403, Status(refused));
            Assert.Equal(FactoryNaming.ActRefusal("brief", TheFactory), ErrorOf(refused));
        }
        Assert.Equal(1, VersionsKept());
    }

    [Fact]
    public void A_session_in_no_factory_is_refused_the_same_way()
    {
        var refused = Save(Session(NoFactorySession), "instructions from nowhere", expect: 0);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryNaming.ActRefusal("brief", TheFactory), ErrorOf(refused));
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_session_the_Gateway_has_no_record_of_yet_is_told_to_try_again_not_judged()
    {
        var refused = Save(Session(UnknownSession), "too early", expect: 0);

        Assert.Equal(409, Status(refused));
        Assert.Equal(SpawnFactory.NotYetKnown, ErrorOf(refused));
        Assert.Equal(0, VersionsKept());
    }

    // ---------- neither a person nor a session ----------

    [Fact]
    public void THE_MACHINE_TOKEN_MAY_READ_AND_MAY_NOT_WRITE_because_a_version_records_who_wrote_it()
    {
        Save(Person(), "the brief", expect: 0);
        Publish(Person(), 1);

        Assert.Equal("the brief", Brief(Published(Machine())).Text);

        foreach (var refused in new[] { Save(Machine(), "a machine's opinion", expect: 1), Publish(Machine(), 1), BringBack(Machine(), 1) })
        {
            Assert.Equal(403, Status(refused));
            Assert.Equal("a seat's brief is written by a person, or by a session of its factory", ErrorOf(refused));
        }
        Assert.Equal(1, VersionsKept());
    }

    // ---------- two writers, through the route ----------

    [Fact]
    public void A_STALE_SAVE_IS_REFUSED_AND_THE_ANSWER_CARRIES_THE_CURRENT_VERSION()
    {
        Save(Session(FactorySession), "start", expect: 0);
        Save(Session(FactorySession), "start, and the first session's change", expect: 1);

        // A second session still holding version 1.
        var refused = Save(Session(SecondFactorySession), "start, and the second session's change", expect: 1);

        Assert.Equal(409, Status(refused));
        Assert.Equal("Stale", Field(refused, "outcome"));
        Assert.Contains("is at version 2, not 1", ErrorOf(refused));
        // What the refused writer needs in order to merge, in the refusal itself.
        var current = Assert.IsType<FactoryBriefDto>(Field(refused, "current"));
        Assert.Equal(2, current.Version);
        Assert.Equal("start, and the first session's change", current.Text);
        Assert.Equal(FactorySession, current.AuthorId);
        Assert.Equal(2, VersionsKept());
    }

    // ---------- what the store refuses, as the route answers it ----------

    [Fact]
    public void A_BRIEF_FOR_A_SEAT_THAT_IS_NOT_REGISTERED_is_NOT_FOUND_on_a_write_and_on_a_read()
    {
        var write = Save(Person(), "a brief for nobody", expect: 0, seat: "night-watch");
        Assert.Equal(404, Status(write));
        Assert.Equal("SeatNotRegistered", Field(write, "outcome"));
        Assert.Contains("'night-watch' is not a seat of warmforward", ErrorOf(write));

        var read = Published(Person(), seat: "night-watch");
        Assert.Equal(404, Status(read));
        Assert.Contains("'night-watch' is not a seat of warmforward", ErrorOf(read));
    }

    [Fact]
    public void A_brief_over_the_cap_is_a_BAD_REQUEST_that_says_the_cap()
    {
        var refused = Save(Person(), new string('x', FactoryBriefStore.MaxBriefBytes + 1), expect: 0);

        Assert.Equal(400, Status(refused));
        Assert.Equal("TooLarge", Field(refused, "outcome"));
        Assert.Contains($"at most {FactoryBriefStore.MaxBriefBytes}", ErrorOf(refused));
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void Publishing_backwards_is_a_CONFLICT_that_carries_the_published_version()
    {
        Save(Person(), "v1", expect: 0);
        Save(Person(), "v2", expect: 1);
        Publish(Person(), 2);

        var refused = Publish(Person(), 1);

        Assert.Equal(409, Status(refused));
        Assert.Equal("OlderThanPublished", Field(refused, "outcome"));
        Assert.Equal(2, Assert.IsType<FactoryBriefDto>(Field(refused, "current")).Version);
    }

    [Fact]
    public void A_write_with_no_body_is_a_BAD_REQUEST()
    {
        var refused = FactoryBriefEndpoints.SaveDraft(Person(), TheFactory, Boss, null, _store, InA, FactoryOf, () => Now);

        Assert.Equal(400, Status(refused));
        Assert.Equal(0, VersionsKept());
    }

    // ---------- another account ----------

    [Fact]
    public void ANOTHER_ACCOUNT_NEVER_SEES_A_BRIEF_through_any_read()
    {
        // Account B has a factory and a seat with the very same ids. The account comes from the request's own
        // credential, never from the path, so B's reads answer as if A had written nothing.
        Save(Person(), "account A's brief", expect: 0);
        Publish(Person(), 1);

        var published = Published(Person(), InB);
        Assert.Equal(404, Status(published));
        Assert.Contains("has no brief on the Gateway", ErrorOf(published));
        Assert.Equal(404, Status(Newest(Person(), InB)));
        Assert.Empty(Page(History(Person(), InB)).Versions);
        Assert.Equal(0, Page(History(Person(), InB)).Total);
        Assert.Equal(404, Status(OneVersion(Person(), "1", InB)));

        // And B's first save is version 1 of its own chain.
        Assert.Equal(1, Brief(Save(Person(), "account B's brief", expect: 0, tenant: InB)).Version);
        Assert.Equal("account A's brief", Brief(Published(Person())).Text);
    }

    [Fact]
    public void A_request_with_no_account_is_refused()
    {
        var refused = FactoryBriefEndpoints.ReadPublished(Person(), TheFactory, Boss, _store, _ => null, FactoryOf);

        Assert.Equal(403, Status(refused));
        Assert.Equal("no account is bound to this request", ErrorOf(refused));
    }

    // ---------- the bound on a session's writing, through the route ----------

    [Fact]
    public void A_SESSIONS_DRAFT_PAST_THE_LIMIT_IS_A_CONFLICT_THAT_SAYS_TO_ASK_THE_OWNER_TO_PUBLISH()
    {
        for (var i = 0; i < FactoryBriefStore.MaxWaitingDrafts; i++)
            Assert.Equal(200, Status(Save(Session(FactorySession), $"draft {i + 1}", expect: i)));

        var refused = Save(Session(SecondFactorySession), "one draft too many", expect: 20);

        Assert.Equal(409, Status(refused));
        Assert.Equal("TooManyDrafts", Field(refused, "outcome"));
        Assert.Equal(
            "the brief of 'boss' has 20 drafts waiting that nobody has published (versions 1 to 20), and a session may " +
            "add a draft only while fewer than 20 are waiting. Nothing was saved, and no version was removed. Ask the " +
            "owner to read the drafts and publish one: publishing makes room for more.",
            ErrorOf(refused));
        // The newest version rides the refusal.
        Assert.Equal(20, ((FactoryBriefDto)Field(refused, "current")!).Version);
        Assert.Equal(20, VersionsKept());
    }

    [Fact]
    public void A_PERSON_SAVES_AT_THE_LIMIT_and_the_persons_publish_lets_a_session_save_again()
    {
        for (var i = 0; i < 20; i++) Save(Session(FactorySession), $"draft {i + 1}", expect: i);
        Assert.Equal(409, Status(Save(Session(FactorySession), "refused", expect: 20)));

        // A person is not bound: the person is the one who makes room.
        var byOwner = Save(Person(), "the owner's own text", expect: 20);
        Assert.Equal(200, Status(byOwner));
        Assert.Equal(21, Brief(byOwner).Version);
        Assert.Equal(409, Status(Save(Session(FactorySession), "still refused", expect: 21)));

        Assert.Equal(200, Status(Publish(Person(), 21)));

        Assert.Equal(22, Brief(Save(Session(FactorySession), "there is room again", expect: 21)).Version);
        // Nothing was removed to make that room.
        Assert.Equal(22, VersionsKept());
    }

    // ---------- the bound on reading, through the route ----------

    /// <summary>A long history: a session saves, the owner publishes every tenth version.</summary>
    private void SaveALongHistory(int versions)
    {
        for (var i = 0; i < versions; i++)
        {
            Assert.Equal(200, Status(Save(Session(FactorySession), $"the text of version {i + 1}", expect: i)));
            if ((i + 1) % 10 == 0) Assert.Equal(200, Status(Publish(Person(), i + 1)));
        }
    }

    [Fact]
    public void THE_HISTORY_IS_ONE_PAGE_OF_VERSIONS_newest_first_that_says_how_many_there_are_and_where_the_next_page_starts()
    {
        SaveALongHistory(120);

        var first = Page(History(Session(FactorySession)));

        // Fifty when the reader does not say, of the 120 there are.
        Assert.Equal(FactoryBriefStore.DefaultHistoryPage, first.Versions.Count);
        Assert.Equal(120, first.Total);
        Assert.Equal(120, first.NewestVersion);
        Assert.Equal(120, first.PublishedVersion);
        Assert.Equal(120, first.Versions[0].Version);
        Assert.Equal(71, first.Versions[^1].Version);
        Assert.Equal(71, first.NextBefore);
        Assert.Equal(FactoryBriefStates.Published, first.Versions[0].State);
        Assert.Equal(FactoryBriefStates.Draft, first.Versions[1].State);
        Assert.Equal(FactorySession, first.Versions[1].AuthorId);
        Assert.Equal("the text of version 119".Length, first.Versions[1].TextBytes);
    }

    [Fact]
    public void A_LONG_HISTORY_IS_READ_PAGE_BY_PAGE_THROUGH_THE_ROUTE_by_sending_back_what_each_page_gave()
    {
        SaveALongHistory(120);

        var seen = new List<int>();
        string? query = "limit=40";
        var pages = 0;
        while (query is not null)
        {
            var page = Page(History(Person(), query: query));
            pages++;
            seen.AddRange(page.Versions.Select(v => v.Version));
            query = page.NextBefore is { } next ? $"limit=40&before={next}" : null;
        }

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(1, 120).Reverse().ToArray(), seen.ToArray());
    }

    [Theory]
    [InlineData("limit=0", "limit is how many versions one page holds: a whole number from 1 to 100, not '0'")]
    [InlineData("limit=101", "limit is how many versions one page holds: a whole number from 1 to 100, not '101'")]
    [InlineData("limit=all", "limit is how many versions one page holds: a whole number from 1 to 100, not 'all'")]
    [InlineData("before=0", "before is the version a page starts below: a whole number from 1, not '0'")]
    [InlineData("before=newest", "before is the version a page starts below: a whole number from 1, not 'newest'")]
    public void A_page_size_or_a_starting_version_that_is_not_one_is_REFUSED_in_words_never_quietly_replaced(string query, string sentence)
    {
        // A reader that asked for 500 and was quietly given 100 would believe it had seen them all.
        Save(Person(), "the brief", expect: 0);

        var refused = History(Person(), query: query);

        Assert.Equal(400, Status(refused));
        Assert.Equal(sentence, ErrorOf(refused));
    }

    [Fact]
    public void ONE_VERSIONS_TEXT_IS_READ_BY_ITS_NUMBER_which_is_how_the_history_is_read_in_full()
    {
        Save(Session(FactorySession), "the first brief", expect: 0);
        Publish(Person(), 1);
        Save(Session(FactorySession), "the second brief", expect: 1);

        var first = Brief(OneVersion(Session(FactorySession), "1"));

        Assert.Equal("the first brief", first.Text);
        Assert.Equal(FactoryBriefStates.Published, first.State);
        // It says how the seat stands, as every answer that carries a version does.
        Assert.Equal(2, first.NewestVersion);
        Assert.Equal(1, first.PublishedVersion);
        Assert.Equal("the second brief", Brief(OneVersion(Person(), "2")).Text);
        // The machine token may read, as it may read the published brief.
        Assert.Equal("the first brief", Brief(OneVersion(Machine(), "1")).Text);
    }

    [Fact]
    public void A_version_that_does_not_exist_is_NOT_FOUND_and_says_which_is_newest()
    {
        Save(Person(), "the brief", expect: 0);

        var missing = OneVersion(Person(), "9");
        Assert.Equal(404, Status(missing));
        Assert.Equal("version 9 of the brief of 'boss' does not exist; its newest version is 1", ErrorOf(missing));
        Assert.Equal(1, Field(missing, "newestVersion"));

        foreach (var notANumber in new[] { "newest", "0", "-1", "1.5" })
        {
            var refused = OneVersion(Person(), notANumber);
            Assert.Equal(400, Status(refused));
            Assert.Equal($"a version is a whole number from 1, not '{notANumber}'", ErrorOf(refused));
        }
    }

    // ---------- the guard lets a session in at all ----------

    [Fact]
    public void THE_SESSION_KEY_GUARD_ALLOWS_A_SESSION_TO_THE_BRIEF_ROUTES_AND_NOTHING_ELSE_UNDER_THEM()
    {
        // Without this the routes above are unreachable by a session in production whatever they do: these tests
        // call the handlers, and the guard sits in front of them. Publish and restore are let through on purpose,
        // so the route can refuse them in words.
        Assert.True(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/draft").Allowed);
        Assert.True(SessionKeyGuard.Check("PUT", "/factory-briefs/warmforward/boss/draft").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/history").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/versions/7").Allowed);
        Assert.True(SessionKeyGuard.Check("POST", "/factory-briefs/warmforward/boss/publish").Allowed);
        Assert.True(SessionKeyGuard.Check("POST", "/factory-briefs/warmforward/boss/restore").Allowed);

        Assert.False(SessionKeyGuard.Check("PUT", "/factory-briefs/warmforward/boss").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/factory-briefs/warmforward/boss").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/factory-briefs/warmforward/boss/draft").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/factory-briefs/warmforward/boss/draft").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/factory-briefs/warmforward/boss/history").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/publish").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/factory-briefs/warmforward/boss/anything-else").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/factory-briefs/warmforward/boss/versions/7").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/factory-briefs/warmforward/boss/versions/7").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/versions").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/factory-briefs/warmforward/boss/versions/7/text").Allowed);
    }
}
