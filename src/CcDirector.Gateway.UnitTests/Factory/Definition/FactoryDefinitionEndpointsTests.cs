using CcDirector.Core.Agents;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A FACTORY'S DEFINITION, THROUGH ITS ROUTES (Factory Definition mission, phase 1), driven through the route
/// handlers over the real store, the real whole-definition check and a real database. These pin what only the route
/// knows: WHO IS ASKING (a person and a session of the factory may save a draft; a person and a session in no factory
/// create, publish and restore - phase 6c, the owner's ruling of 16 October 2026; a factory's own session never
/// publishes; a session of another factory reaches nothing), and that THE CHECK RUNS BEFORE EVERY WRITE with the account's own skills and
/// machines, refusing with every finding, file and line, and storing nothing.
///
/// A refusal is proven by the answer that carries it - its status, its sentence and its findings - never by the
/// absence of an error.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionEndpointsTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string FactorySession = "74000000-0000-4000-8000-000000000001";
    private const string SecondFactorySession = "74000000-0000-4000-8000-000000000002";
    private const string OtherFactorySession = "74000000-0000-4000-8000-000000000003";
    private const string NoFactorySession = "74000000-0000-4000-8000-000000000004";
    private const string UnknownSession = "74000000-0000-4000-8000-000000000005";
    private const string NamedNoFactorySession = "74000000-0000-4000-8000-000000000006";
    private const string NamedNoFactorySessionName = "Factory Definition - Developer - make Eric's factory";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryDefinitionStore _store;
    private readonly FactoryPublishApply _apply;

    /// <summary>The account's fleet skills as the check is told them; a test may take one away.</summary>
    private readonly List<string> _fleetSkills = DefinitionFixture.FleetSkills.ToList();

    private readonly FactoryDefinitionEndpoints.Account _account;

    public FactoryDefinitionEndpointsTests()
    {
        var db = _h.Open();
        _store = new FactoryDefinitionStore(db);
        _apply = new FactoryPublishApply(db);
        // A publish drives the factory from phase 4, and until phase 5 it must be registered (it runs in its folder).
        new CcDirector.Gateway.Factory.Registry.FactoryRegistryStore(db).Register(A, DefinitionFixture.Registration(), "test", Now);
        _account = new FactoryDefinitionEndpoints.Account(_ => _fleetSkills.ToList(), _ => DefinitionFixture.Machines, DefinitionFixture.AskNobody);
    }

    public void Dispose() => _h.Dispose();

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

    /// <summary>The account's shared machine token, or a Director: neither a person's device nor a session's key.</summary>
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
        NamedNoFactorySession => SessionFactoryLookup.InNoFactory with { Name = NamedNoFactorySessionName },
        _ => SessionFactoryLookup.NotKnown,
    };

    private static TenantId? InA(HttpContext _) => A;
    private static TenantId? InB(HttpContext _) => B;

    // ---------- the routes ----------

    private IResult Save(HttpContext ctx, IReadOnlyDictionary<string, string>? content, int expect, string? note = null,
        Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryDefinitionEndpoints.SaveDraft(ctx, TheFactory,
            new SaveFactoryDefinitionDraftRequest
            {
                ExpectedVersion = expect,
                Note = note,
                Content = content is null ? null : new Dictionary<string, string>(content),
            },
            _store, tenant ?? InA, FactoryOf, _account, () => Now);

    private IResult Publish(HttpContext ctx, int version) =>
        FactoryDefinitionEndpoints.Publish(ctx, TheFactory, new PublishFactoryDefinitionRequest { Version = version },
            _store, _apply, InA, FactoryOf, _account, () => Now.AddMinutes(5)).GetAwaiter().GetResult();

    private IResult Restore(HttpContext ctx, int version, int expect = 0) =>
        FactoryDefinitionEndpoints.Restore(ctx, TheFactory, new RestoreFactoryDefinitionRequest { Version = version, ExpectedVersion = expect },
            _store, _apply, InA, FactoryOf, _account, () => Now.AddMinutes(10)).GetAwaiter().GetResult();

    private IResult Published(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryDefinitionEndpoints.ReadPublished(ctx, TheFactory, _store, tenant ?? InA, FactoryOf);

    private IResult Newest(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryDefinitionEndpoints.ReadNewest(ctx, TheFactory, _store, tenant ?? InA, FactoryOf);

    private IResult History(HttpContext ctx, Func<HttpContext, TenantId?>? tenant = null, string? query = null)
    {
        if (query is not null) ctx.Request.QueryString = new QueryString("?" + query);
        return FactoryDefinitionEndpoints.History(ctx, TheFactory, _store, tenant ?? InA, FactoryOf);
    }

    private IResult OneVersion(HttpContext ctx, string version, Func<HttpContext, TenantId?>? tenant = null) =>
        FactoryDefinitionEndpoints.ReadVersion(ctx, TheFactory, version, _store, tenant ?? InA, FactoryOf);

    // ---------- reading an answer ----------

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static FactoryDefinitionDto Version(IResult result) => Assert.IsType<JsonHttpResult<FactoryDefinitionDto>>(result).Value!;

    private static FactoryDefinitionHistoryResponse Page(IResult result) =>
        Assert.IsType<JsonHttpResult<FactoryDefinitionHistoryResponse>>(result).Value!;

    private static object? Field(IResult result, string name)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var property = value.GetType().GetProperty(name);
        Assert.True(property is not null, $"the answer has no '{name}'");
        return property!.GetValue(value);
    }

    private static string ErrorOf(IResult result) => (string)Field(result, "error")!;

    private static List<FactoryDefinitionFindingDto> FindingsOf(IResult result) =>
        Assert.IsType<List<FactoryDefinitionFindingDto>>(Field(result, "findings"));

    /// <summary>The check's refusal: 422, its sentence, and an error on this file at this line holding every word.</summary>
    private static void AssertRefusedByTheCheck(IResult result, string file, int line, params string[] words)
    {
        Assert.Equal(422, Status(result));
        Assert.Equal("CheckFailed", Field(result, "outcome"));
        Assert.Contains("the check found", ErrorOf(result));
        var findings = FindingsOf(result);
        var match = findings.FirstOrDefault(f => f.Severity == "error" && f.File == file && f.Line == line
                                                 && words.All(w => f.Message.Contains(w, StringComparison.Ordinal)));
        Assert.True(match is not null,
            $"No error at {file}:{line} holding [{string.Join(", ", words)}]. Findings:\n" +
            string.Join("\n", findings.Select(f => $"  {f.Severity} {f.File}:{f.Line} {f.Message}")));
    }

    private int VersionsKept() => _store.History(A, TheFactory).Total;

    // ---------- the flow ----------

    [Fact]
    public void THE_WHOLE_FLOW_a_draft_is_saved_published_replaced_and_the_first_is_restored()
    {
        // A session of the factory proposes the definition. The check's notices ride along on the success.
        var draft = Version(Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: 0, note: "the first definition"));
        Assert.Equal(1, draft.Version);
        Assert.Equal("draft", draft.State);
        Assert.Equal("session", draft.AuthorKind);
        Assert.Equal(FactorySession, draft.AuthorId);
        Assert.Equal("the first definition", draft.ChangeNote);
        Assert.Null(draft.PublishedVersion);
        Assert.Null(draft.Files);
        Assert.NotEmpty(draft.Findings);
        Assert.All(draft.Findings, f => Assert.Equal("notice", f.Severity));
        Assert.Contains(draft.Findings, f => f.File == "seats/outreach.yaml" && f.Message.Contains("SOREN_SOUTH"));

        var notYet = Published(Person());
        Assert.Equal(404, Status(notYet));
        Assert.Equal($"{TheFactory} has no published definition yet: its newest version, 1, is a draft", ErrorOf(notYet));

        // The owner publishes it.
        var first = Version(Publish(Person(), 1));
        Assert.Equal("published", first.State);
        // The first publish lists everything as added, so the person sees what they just published.
        Assert.True(first.Changes!.FirstPublish);
        Assert.Contains(first.Changes.Seats, s => s.Seat == "writer" && s.Change == "added");
        Assert.Equal(Now.AddMinutes(5), first.PublishedAtUtc);
        Assert.Equal(1, first.PublishedVersion);
        var read = Version(Published(Session(FactorySession)));
        Assert.Equal(DefinitionFixture.Publishable(), read.Files);

        // A second version, written and published by the owner.
        var changed = DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "Write the posts every day.");
        var saved = Version(Save(Person(), changed, expect: 1));
        Assert.Equal(2, saved.Version);
        Assert.Null(saved.Changes);
        var second = Version(Publish(Person(), 2));
        Assert.Equal(2, second.PublishedVersion);
        // What changes against the version that was published before it: only the writer's brief.
        Assert.False(second.Changes!.FirstPublish);
        var seat = Assert.Single(second.Changes.Seats);
        Assert.Equal("writer", seat.Seat);
        Assert.NotNull(seat.BriefDiff);
        Assert.Equal(changed, Version(Published(Person())).Files);

        // The first is restored: a copy of it, as version 3, published, recording where it came from.
        var back = Version(Restore(Person(), 1, expect: 2));
        Assert.Equal(3, back.Version);
        Assert.Equal("published", back.State);
        Assert.Equal(1, back.RestoredFrom);
        Assert.Equal(3, back.PublishedVersion);
        Assert.Equal("writer", Assert.Single(back.Changes!.Seats).Seat);
        Assert.Equal(DefinitionFixture.Publishable(), Version(Published(Person())).Files);

        var history = Page(History(Person()));
        Assert.Equal(TheFactory, history.Factory);
        Assert.Equal(3, history.PublishedVersion);
        Assert.Equal(new[] { 3, 2, 1 }, history.Versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public void The_newest_version_is_what_an_editor_reads_with_its_files_and_it_says_which_is_published()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        var changed = DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "Write fewer posts.");
        Save(Session(FactorySession), changed, expect: 1);

        var newest = Version(Newest(Person()));

        Assert.Equal(2, newest.Version);
        Assert.Equal("draft", newest.State);
        Assert.Equal(changed, newest.Files);
        Assert.Equal(1, newest.PublishedVersion);
        Assert.Equal(2, Version(Published(Person())).NewestVersion);
    }

    [Fact]
    public void A_factory_with_no_definition_answers_NOT_FOUND_in_words_and_its_history_is_empty()
    {
        var published = Published(Person());
        Assert.Equal(404, Status(published));
        Assert.Equal($"{TheFactory} has no definition on the Gateway", ErrorOf(published));
        Assert.Equal(0, Field(published, "newestVersion"));
        Assert.Equal(404, Status(Newest(Person())));

        var history = Page(History(Person()));
        Assert.Empty(history.Versions);
        Assert.Equal(0, history.Total);
        Assert.Null(history.PublishedVersion);
    }

    // ---------- what changes ----------

    private IResult Diff(HttpContext ctx, string? query = null)
    {
        if (query is not null) ctx.Request.QueryString = new QueryString("?" + query);
        return FactoryDefinitionEndpoints.Diff(ctx, TheFactory, _store, _apply, InA, FactoryOf);
    }

    private static FactoryDefinitionDiffResponse DiffOf(IResult result) =>
        Assert.IsType<JsonHttpResult<FactoryDefinitionDiffResponse>>(result).Value!;

    [Fact]
    public void THE_DIFF_DEFAULTS_FROM_THE_PUBLISHED_VERSION_TO_THE_NEWEST()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), expect: 1);

        var diff = DiffOf(Diff(Session(FactorySession)));

        Assert.Equal(TheFactory, diff.Factory);
        Assert.Equal(1, diff.From);
        Assert.Equal(2, diff.To);
        var seat = Assert.Single(diff.Changes.Seats);
        Assert.Equal("writer", seat.Seat);
        Assert.Contains(seat.Details, d => d.Contains("Writer") && d.Contains("Author"));
    }

    [Fact]
    public void BEFORE_THE_FIRST_PUBLISH_THE_DIFF_IS_FROM_NOTHING_and_lists_everything_as_added()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);

        var diff = DiffOf(Diff(Person()));

        Assert.Null(diff.From);
        Assert.Equal(1, diff.To);
        Assert.True(diff.Changes.FirstPublish);
        Assert.Equal(3, diff.Changes.Seats.Count);
    }

    [Fact]
    public void THE_DIFF_BETWEEN_TWO_NAMED_VERSIONS_and_between_two_with_the_same_content_says_nothing_changes()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), expect: 1);
        Save(Person(), DefinitionFixture.Publishable(), expect: 2);

        Assert.True(DiffOf(Diff(Person(), "from=1&to=3")).Changes.NothingChanges);
        var oneToTwo = DiffOf(Diff(Person(), "from=1&to=2"));
        Assert.Equal((1, 2), (oneToTwo.From, oneToTwo.To));
        Assert.False(oneToTwo.Changes.NothingChanges);
    }

    [Fact]
    public void A_DIFF_NAMING_A_VERSION_THAT_DOES_NOT_EXIST_IS_NOT_FOUND_and_one_that_is_not_a_number_is_a_BAD_REQUEST()
    {
        Assert.Equal(404, Status(Diff(Person())));

        Save(Person(), DefinitionFixture.Publishable(), expect: 0);

        var missing = Diff(Person(), "from=7");
        Assert.Equal(404, Status(missing));
        Assert.Equal($"version 7 of the definition of {TheFactory} does not exist; its newest version is 1", ErrorOf(missing));
        Assert.Equal(404, Status(Diff(Person(), "to=7")));

        var notANumber = Diff(Person(), "to=newest");
        Assert.Equal(400, Status(notANumber));
        Assert.Equal("a version to compare to is a whole number from 1, not 'newest'", ErrorOf(notANumber));
    }

    // ---------- a factory's own session never publishes (phase 6c) ----------

    [Fact]
    public void A_FACTORYS_OWN_SESSIONS_PUBLISH_IS_REFUSED_WITH_ITS_SENTENCE()
    {
        Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: 0);

        var refused = Publish(Session(FactorySession), 1);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryDefinitionStore.FactorySessionMayNotPublish(TheFactory), ErrorOf(refused));
        Assert.Equal(
            $"A session of factory '{TheFactory}' may not publish a factory's definition. A factory's own sessions save " +
            "drafts; the owner publishes them, in the Cockpit or by telling a session that is in no factory to publish.",
            ErrorOf(refused));
        Assert.Equal("FactorySessionMayNotPublish", Field(refused, "outcome"));
        Assert.Equal(404, Status(Published(Person())));
    }

    [Fact]
    public void A_FACTORYS_OWN_SESSIONS_PUBLISH_OF_A_VERSION_THE_CHECK_WOULD_REFUSE_is_still_told_it_may_not_publish()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        _fleetSkills.Remove("browsers");

        var refused = Publish(Session(FactorySession), 1);

        // What the check thinks of a version a session may not publish anyway is not the answer it needs.
        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryDefinitionStore.FactorySessionMayNotPublish(TheFactory), ErrorOf(refused));
    }

    [Fact]
    public void A_FACTORYS_OWN_SESSIONS_RESTORE_IS_REFUSED_too_because_it_publishes()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), expect: 1);
        Publish(Person(), 2);

        var refused = Restore(Session(FactorySession), 1, expect: 2);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryDefinitionStore.FactorySessionMayNotRestore(TheFactory), ErrorOf(refused));
        Assert.Equal("FactorySessionMayNotPublish", Field(refused, "outcome"));
        Assert.Equal(2, VersionsKept());
        Assert.Equal(2, Version(Published(Person())).Version);
    }

    [Fact]
    public void A_FACTORYS_OWN_SESSION_STILL_SAVES_A_DRAFT_OF_ITS_OWN_FACTORY()
    {
        var saved = Version(Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: 0));

        Assert.Equal(1, saved.Version);
        Assert.Equal("session", saved.AuthorKind);
        Assert.Equal(FactorySession, saved.AuthorId);
    }

    [Fact]
    public void A_FACTORYS_OWN_SESSION_MAY_NOT_CREATE_ANOTHER_FACTORY()
    {
        var refused = FactoryDefinitionEndpoints.SaveDraft(Session(FactorySession), "brand-new",
            new SaveFactoryDefinitionDraftRequest { ExpectedVersion = 0, Content = new Dictionary<string, string>(DefinitionFixture.Publishable()) },
            _store, InA, FactoryOf, _account, () => Now);

        Assert.Equal(403, Status(refused));
        Assert.Equal(FactoryDefinitionStore.FactorySessionMayNotCreate(TheFactory, "brand-new"), ErrorOf(refused));
        Assert.Equal("FactorySessionMayNotPublish", Field(refused, "outcome"));
        Assert.Null(_store.Standing(A, "brand-new").Newest);
    }

    // ---------- a session in no factory makes factories (phase 6c, the owner's ruling) ----------

    [Fact]
    public void A_SESSION_IN_NO_FACTORY_SAVES_PUBLISHES_AND_RESTORES_and_every_publish_records_its_id_and_name()
    {
        var builder = NamedNoFactorySession;

        var draft = Version(Save(Session(builder), DefinitionFixture.Publishable(), expect: 0));
        Assert.Equal("session", draft.AuthorKind);
        Assert.Equal(builder, draft.AuthorId);

        var published = Publish(Session(builder), 1);
        Assert.Equal(200, Status(published));
        var first = Version(published);
        Assert.Equal(1, first.PublishedVersion);
        Assert.Equal("session", first.PublishedByKind);
        Assert.Equal(builder, first.PublishedBy);
        Assert.Equal(NamedNoFactorySessionName, first.PublishedByName);
        Assert.Equal($"session {builder} ({NamedNoFactorySessionName})", first.PublishedByText);
        // The publish drove the factory, exactly as a person's does.
        Assert.NotNull(first.Applied);
        Assert.NotEmpty(first.Applied!);

        Version(Save(Session(builder), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), expect: 1));
        Assert.Equal(200, Status(Publish(Session(builder), 2)));

        var restored = Restore(Session(builder), 1, expect: 2);
        Assert.Equal(200, Status(restored));
        var copy = Version(restored);
        Assert.Equal(3, copy.Version);
        Assert.Equal(1, copy.RestoredFrom);
        Assert.Equal($"session {builder} ({NamedNoFactorySessionName})", copy.PublishedByText);

        var page = Page(History(Person()));
        Assert.Equal(new[] { "session", "session", "session" }, page.Versions.Select(v => v.PublishedByKind).ToArray());
    }

    [Fact]
    public void A_PERSONS_PUBLISH_STILL_WORKS_and_is_recorded_as_a_person()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);

        var published = Version(Publish(Person(), 1));

        Assert.Equal(1, published.PublishedVersion);
        Assert.Equal("person", published.PublishedByKind);
        Assert.Null(published.PublishedByName);
        Assert.StartsWith("a person", published.PublishedByText);
    }

    // ---------- a session outside the factory reaches nothing ----------

    [Fact]
    public void A_SESSION_OF_ANOTHER_FACTORY_MAY_NOT_READ_OR_WRITE_THIS_FACTORYS_DEFINITION()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        var outside = OtherFactorySession;

        foreach (var refused in new[]
                 {
                     Published(Session(outside)), Newest(Session(outside)), History(Session(outside)),
                     OneVersion(Session(outside), "1"), Diff(Session(outside)),
                     Save(Session(outside), DefinitionFixture.Publishable(), expect: 1),
                     Publish(Session(outside), 1), Restore(Session(outside), 1),
                 })
        {
            Assert.Equal(403, Status(refused));
            Assert.Equal(FactoryNaming.ActRefusal("definition", TheFactory), ErrorOf(refused));
        }
        Assert.Equal(1, VersionsKept());
    }

    [Fact]
    public void A_session_in_no_factory_may_read_and_write_any_factorys_definition()
    {
        // Phase 6c reversed this: it was refused the same way as a session of another factory.
        var saved = Save(Session(NoFactorySession), DefinitionFixture.Publishable(), expect: 0);

        Assert.Equal(200, Status(saved));
        Assert.Equal(1, VersionsKept());
        Assert.Equal(200, Status(Newest(Session(NoFactorySession))));
        Assert.Equal(200, Status(History(Session(NoFactorySession))));
    }

    [Fact]
    public void A_session_the_Gateway_has_no_record_of_yet_is_told_to_try_again_not_judged()
    {
        var refused = Save(Session(UnknownSession), DefinitionFixture.Publishable(), expect: 0);

        Assert.Equal(409, Status(refused));
        Assert.Equal(SpawnFactory.NotYetKnown, ErrorOf(refused));
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void THE_MACHINE_TOKEN_MAY_READ_AND_MAY_NOT_WRITE_because_a_version_records_who_wrote_it()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);

        Assert.Equal(1, Version(Published(Machine())).Version);

        foreach (var refused in new[] { Save(Machine(), DefinitionFixture.Publishable(), expect: 1), Publish(Machine(), 1), Restore(Machine(), 1) })
        {
            Assert.Equal(403, Status(refused));
            Assert.Equal("a factory's definition is written by a person, or by a session of that factory or of none", ErrorOf(refused));
        }
        Assert.Equal(1, VersionsKept());
    }

    // ---------- two writers ----------

    [Fact]
    public void A_STALE_SAVE_IS_REFUSED_AND_THE_ANSWER_CARRIES_THE_CURRENT_VERSION()
    {
        Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: 0);
        Save(Session(FactorySession), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "first change"), expect: 1);

        var refused = Save(Session(SecondFactorySession), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "second change"), expect: 1);

        Assert.Equal(409, Status(refused));
        Assert.Equal("Stale", Field(refused, "outcome"));
        Assert.Equal($"the definition of {TheFactory} is at version 2, not 1; read it again, merge, and save once more", ErrorOf(refused));
        var current = Assert.IsType<FactoryDefinitionDto>(Field(refused, "current"));
        Assert.Equal(2, current.Version);
        Assert.Equal(FactorySession, current.AuthorId);
        Assert.Equal(2, VersionsKept());
    }

    // ---------- the check refuses, with file and line, and nothing is stored ----------

    [Fact]
    public void AN_UNKNOWN_KEY_IS_REFUSED_WITH_ITS_FILE_AND_LINE_and_nothing_is_stored()
    {
        var refused = Save(Person(), DefinitionFixture.PublishableWith("seats/writer.yaml", DefinitionFixture.Writer + "\ncolour: red"), expect: 0);

        AssertRefusedByTheCheck(refused, "seats/writer.yaml", 5, "'colour' is not a key");
        Assert.Equal($"The definition was not saved: the check found 1 error in the definition of {TheFactory}. Fix each one where it is and try again.",
            ErrorOf(refused));
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_DANGLING_REFERENCE_IS_REFUSED_ON_THE_LINE_THAT_MAKES_IT()
    {
        var refused = Save(Person(), DefinitionFixture.PublishableWith("seats/writer.yaml", DefinitionFixture.Writer + "\nfiles: [files/nowhere.py]"), expect: 0);

        AssertRefusedByTheCheck(refused, "seats/writer.yaml", 5, "files/nowhere.py", "not in this definition");
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_FACTORY_SKILL_AND_A_FLEET_SKILL_WITH_THE_SAME_NAME_ARE_REFUSED_on_the_line_naming_the_fleet_one()
    {
        var content = DefinitionFixture.Publishable();
        content["skills/browsers/SKILL.md"] = "---\nname: browsers\ndescription: Our own browsers.\n---\nUse ours.\n";

        var refused = Save(Person(), content, expect: 0);

        // seats/outreach.yaml line 20 is "  fleet: [browsers]".
        AssertRefusedByTheCheck(refused, "seats/outreach.yaml", 20, "'browsers' is both a factory skill", "would not know which one it got");
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_FACTORY_SKILL_ON_AN_AGENT_THAT_CANNOT_LOAD_ONE_IS_REFUSED_WITH_THE_REASON()
    {
        // The outreach seat moves to Codex and gives up its rule sets (a Codex seat cannot carry rules either, and
        // that is its own refusal). With line 17 gone, line 20 becomes "  factory: [mail-voice]".
        var content = DefinitionFixture.Publishable();
        content["seats/outreach.yaml"] = content["seats/outreach.yaml"]
            .Replace("agent: ClaudeCode\n", "agent: Codex\n", StringComparison.Ordinal)
            .Replace("rules: [base, outreach]\n", "", StringComparison.Ordinal)
            .Replace("  fleet: [browsers]\n", "  fleet: [browsers]\n  factory: [mail-voice]\n", StringComparison.Ordinal);

        var refused = Save(Person(), content, expect: 0);

        AssertRefusedByTheCheck(refused, "seats/outreach.yaml", 20, FactorySkillSupport.CodexReason);
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_FILE_OVER_ITS_CAP_IS_REFUSED_BY_THE_CHECK_and_nothing_is_stored()
    {
        var content = DefinitionFixture.Publishable();
        content["files/huge.txt"] = new string('x', DefinitionPaths.MaxFileBytes + 1);

        var refused = Save(Person(), content, expect: 0);

        Assert.Equal(422, Status(refused));
        Assert.Contains(FindingsOf(refused), f => f.Severity == "error" && f.File == "files/huge.txt");
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_SAVE_WITH_NO_CONTENT_IS_REFUSED_BY_THE_CHECK_which_says_what_is_missing()
    {
        var refused = Save(Person(), null, expect: 0);

        Assert.Equal(422, Status(refused));
        Assert.Contains(FindingsOf(refused), f => f.Severity == "error" && f.File == "factory.yaml" && f.Line == 0);
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_PUBLISH_CHECKS_AGAIN_with_the_accounts_skills_as_they_are_NOW_and_publishes_nothing_on_an_error()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        // Between the save and the publish, the account loses the fleet skill a seat names.
        _fleetSkills.Remove("browsers");

        var refused = Publish(Person(), 1);

        AssertRefusedByTheCheck(refused, "seats/outreach.yaml", 20, "'browsers'", "not in this account's skill store");
        Assert.Equal($"Version 1 was not published: the check found 1 error in the definition of {TheFactory}. Fix each one where it is and try again.",
            ErrorOf(refused));
        Assert.Equal(404, Status(Published(Person())));
    }

    [Fact]
    public void A_RESTORE_CHECKS_TOO_and_restores_nothing_on_an_error()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), expect: 1);
        Publish(Person(), 2);
        _fleetSkills.Remove("browsers");

        var refused = Restore(Person(), 1, expect: 2);

        Assert.Equal(422, Status(refused));
        Assert.StartsWith("Version 1 was not restored:", ErrorOf(refused));
        Assert.Equal(2, VersionsKept());
    }

    // ---------- a published version the format rules now refuse (review of 4004, finding 1) ----------

    /// <summary>A definition the format rules refuse - an unknown key on line 5 of the writer - stored and published
    /// STRAIGHT THROUGH THE STORE, as one published before the rules tightened would be. The store holds paths and
    /// caps only, so it takes it.</summary>
    private static Dictionary<string, string> RefusedByTodaysRules() =>
        DefinitionFixture.PublishableWith("seats/writer.yaml", DefinitionFixture.Writer + "\ncolour: red");

    private void PublishStraightThroughTheStore(IReadOnlyDictionary<string, string> content)
    {
        var newest = _store.Standing(A, TheFactory).Newest?.Version ?? 0;
        Assert.True(_store.SaveDraft(A, TheFactory, content, newest, "person", "the owner", null, Now).Ok);
        Assert.True(_store.Publish(A, TheFactory, newest + 1, "person", "the owner", Now).Ok);
    }

    [Fact]
    public void A_PUBLISH_OVER_A_VERSION_THAT_NO_LONGER_PARSES_SUCCEEDS_and_lists_what_changes_from_nothing_with_a_notice_saying_why()
    {
        PublishStraightThroughTheStore(RefusedByTodaysRules());
        Assert.Equal(200, Status(Save(Person(), DefinitionFixture.Publishable(), expect: 1)));

        var published = Publish(Person(), 2);

        Assert.Equal(200, Status(published));
        var dto = Version(published);
        Assert.Equal(2, dto.PublishedVersion);
        Assert.True(dto.Changes!.FirstPublish);
        var notice = Assert.Single(dto.Findings, f => f.Message.StartsWith("Version 1, published until now", StringComparison.Ordinal));
        Assert.Equal("notice", notice.Severity);
        Assert.Equal("seats/writer.yaml", notice.File);
        Assert.Equal(5, notice.Line);
        Assert.Contains("no longer passes the format check at seats/writer.yaml:5", notice.Message);
        Assert.Contains("'colour' is not a key", notice.Message);
        Assert.Contains("listed as if nothing had been published before", notice.Message);
    }

    [Fact]
    public void A_RESTORE_OVER_A_VERSION_THAT_NO_LONGER_PARSES_SUCCEEDS_with_the_same_notice()
    {
        PublishStraightThroughTheStore(DefinitionFixture.Publishable());
        PublishStraightThroughTheStore(RefusedByTodaysRules());

        var restored = Restore(Person(), 1, expect: 2);

        Assert.Equal(200, Status(restored));
        var dto = Version(restored);
        Assert.Equal(3, dto.PublishedVersion);
        Assert.Equal(1, dto.RestoredFrom);
        Assert.True(dto.Changes!.FirstPublish);
        Assert.Contains(dto.Findings, f => f.Severity == "notice" && f.Message.StartsWith("Version 2, published until now", StringComparison.Ordinal));
    }

    [Fact]
    public void A_DIFF_FROM_OR_TO_A_VERSION_THAT_NO_LONGER_PARSES_IS_422_IN_WORDS_naming_the_version_and_where()
    {
        PublishStraightThroughTheStore(RefusedByTodaysRules());
        Assert.Equal(200, Status(Save(Person(), DefinitionFixture.Publishable(), expect: 1)));

        var fromIt = Diff(Person());
        Assert.Equal(422, Status(fromIt));
        Assert.Equal("DoesNotParse", Field(fromIt, "outcome"));
        Assert.Equal($"Version 1 of the definition of {TheFactory} no longer passes the format check at seats/writer.yaml:5, " +
                     "so what changes cannot be shown. Compare other versions with from and to.", ErrorOf(fromIt));

        var toIt = Diff(Person(), "from=2&to=1");
        Assert.Equal(422, Status(toIt));
        Assert.StartsWith("Version 1 of the definition", ErrorOf(toIt));
    }

    // ---------- the store's rulings come before the check (review of 4004, finding 3) ----------

    [Fact]
    public void PUBLISHING_THE_VERSION_THAT_IS_ALREADY_PUBLISHED_SAYS_SO_even_when_the_account_changed_since()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        _fleetSkills.Remove("browsers");

        var again = Publish(Person(), 1);

        Assert.Equal(409, Status(again));
        Assert.Equal("AlreadyPublished", Field(again, "outcome"));
        Assert.Equal($"version 1 is already the published definition of {TheFactory}", ErrorOf(again));
    }

    [Fact]
    public void PUBLISHING_A_VERSION_OLDER_THAN_THE_PUBLISHED_ONE_SAYS_SO_even_when_the_account_changed_since()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), expect: 1);
        Publish(Person(), 2);
        _fleetSkills.Remove("browsers");

        var older = Publish(Person(), 1);

        Assert.Equal(409, Status(older));
        Assert.Equal("OlderThanPublished", Field(older, "outcome"));
    }

    [Fact]
    public void RESTORING_THE_PUBLISHED_VERSION_SAYS_SO_even_when_the_account_changed_since()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);
        _fleetSkills.Remove("browsers");

        var restore = Restore(Person(), 1, expect: 1);

        Assert.Equal(409, Status(restore));
        Assert.Equal("AlreadyPublished", Field(restore, "outcome"));
    }

    [Fact]
    public void A_publish_of_a_version_that_does_not_exist_is_NOT_FOUND_in_the_stores_words()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);

        var refused = Publish(Person(), 9);

        Assert.Equal(404, Status(refused));
        Assert.Equal($"version 9 of the definition of {TheFactory} does not exist; its newest version is 1", ErrorOf(refused));
    }

    [Fact]
    public void Publishing_backwards_is_a_CONFLICT_that_carries_the_published_version()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Save(Person(), DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), expect: 1);
        Publish(Person(), 2);

        var refused = Publish(Person(), 1);

        Assert.Equal(409, Status(refused));
        Assert.Equal("OlderThanPublished", Field(refused, "outcome"));
        Assert.Equal(2, Assert.IsType<FactoryDefinitionDto>(Field(refused, "current")).Version);
    }

    [Fact]
    public void A_change_note_over_its_cap_is_a_BAD_REQUEST_that_says_the_cap()
    {
        var refused = Save(Person(), DefinitionFixture.Publishable(), expect: 0, note: new string('n', FactoryDefinitionStore.MaxChangeNoteLength + 1));

        Assert.Equal(400, Status(refused));
        Assert.Equal("ChangeNoteTooLong", Field(refused, "outcome"));
        Assert.Equal(0, VersionsKept());
    }

    [Fact]
    public void A_SESSIONS_DRAFT_PAST_THE_LIMIT_IS_A_CONFLICT_THAT_SAYS_TO_ASK_THE_OWNER_TO_PUBLISH()
    {
        for (var i = 0; i < FactoryDefinitionStore.MaxWaitingDrafts; i++)
            Assert.Equal(200, Status(Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: i)));

        var refused = Save(Session(FactorySession), DefinitionFixture.Publishable(), expect: FactoryDefinitionStore.MaxWaitingDrafts);

        Assert.Equal(409, Status(refused));
        Assert.Equal("TooManyDrafts", Field(refused, "outcome"));
        Assert.Contains("Ask the owner to read the drafts and publish one", ErrorOf(refused));
    }

    // ---------- the shape of a request ----------

    [Fact]
    public async Task A_write_with_no_body_is_a_BAD_REQUEST()
    {
        Assert.Equal("a body is required", ErrorOf(FactoryDefinitionEndpoints.SaveDraft(Person(), TheFactory, null, _store, InA, FactoryOf, _account, () => Now)));
        Assert.Equal("a body is required", ErrorOf(await FactoryDefinitionEndpoints.Publish(Person(), TheFactory, null, _store, _apply, InA, FactoryOf, _account, () => Now)));
        Assert.Equal("a body is required", ErrorOf(await FactoryDefinitionEndpoints.Restore(Person(), TheFactory, null, _store, _apply, InA, FactoryOf, _account, () => Now)));
    }

    [Fact]
    public void A_factory_id_that_is_not_one_is_a_BAD_REQUEST_in_the_naming_rules_words()
    {
        var refused = FactoryDefinitionEndpoints.ReadNewest(Person(), "not a factory!", _store, InA, FactoryOf);

        Assert.Equal(400, Status(refused));
        Assert.Equal("BadFactory", Field(refused, "outcome"));
    }

    [Fact]
    public void ANOTHER_ACCOUNT_NEVER_SEES_A_DEFINITION_through_any_read()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        Publish(Person(), 1);

        Assert.Equal(404, Status(Published(Person(), InB)));
        Assert.Equal(404, Status(Newest(Person(), InB)));
        Assert.Equal(404, Status(OneVersion(Person(), "1", InB)));
        Assert.Empty(Page(History(Person(), InB)).Versions);
    }

    [Fact]
    public void A_request_with_no_account_is_refused()
    {
        var refused = Published(Person(), _ => null);

        Assert.Equal(403, Status(refused));
        Assert.Equal("no account is bound to this request", ErrorOf(refused));
    }

    // ---------- reading the history ----------

    [Fact]
    public void THE_HISTORY_IS_ONE_PAGE_newest_first_that_says_how_many_there_are_and_where_the_next_starts()
    {
        for (var i = 0; i < 7; i++) Save(Person(), DefinitionFixture.Publishable(), expect: i);

        var page = Page(History(Person(), query: "limit=3"));

        Assert.Equal(7, page.Total);
        Assert.Equal(new[] { 7, 6, 5 }, page.Versions.Select(v => v.Version).ToArray());
        Assert.Equal(5, page.NextBefore);
        Assert.All(page.Versions, v => Assert.Equal(DefinitionFixture.Publishable().Count, v.FileCount));

        var last = Page(History(Person(), query: "limit=3&before=2"));
        Assert.Equal(new[] { 1 }, last.Versions.Select(v => v.Version).ToArray());
        Assert.Null(last.NextBefore);
    }

    [Theory]
    [InlineData("limit=0", "limit is how many versions one page holds: a whole number from 1 to 100, not '0'")]
    [InlineData("limit=1000", "limit is how many versions one page holds: a whole number from 1 to 100, not '1000'")]
    [InlineData("before=zero", "before is the version a page starts below: a whole number from 1, not 'zero'")]
    public void A_page_size_or_a_starting_version_that_is_not_one_is_REFUSED_in_words(string query, string sentence)
    {
        var refused = History(Person(), query: query);

        Assert.Equal(400, Status(refused));
        Assert.Equal(sentence, ErrorOf(refused));
    }

    [Fact]
    public void ONE_VERSIONS_CONTENT_IS_READ_BY_ITS_NUMBER_and_a_missing_one_says_which_is_newest()
    {
        Save(Person(), DefinitionFixture.Publishable(), expect: 0);
        var changed = DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2");
        Save(Person(), changed, expect: 1);

        Assert.Equal(DefinitionFixture.Publishable(), Version(OneVersion(Person(), "1")).Files);
        Assert.Equal(changed, Version(OneVersion(Person(), "2")).Files);

        var missing = OneVersion(Person(), "9");
        Assert.Equal(404, Status(missing));
        Assert.Equal($"version 9 of the definition of {TheFactory} does not exist; its newest version is 2", ErrorOf(missing));

        var notANumber = OneVersion(Person(), "two");
        Assert.Equal(400, Status(notANumber));
        Assert.Equal("a version is a whole number from 1, not 'two'", ErrorOf(notANumber));
    }
}
