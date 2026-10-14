using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A FACTORY'S DEFINITION ON THE GATEWAY: the store contract (Factory Definition mission, phase 1). Runs over the
/// real Entity Framework store on a throwaway SQLite file, exactly as the Gateway runs it locally. No factory is
/// registered anywhere in this file: a definition does not need one.
///
/// The cases that matter most are the ones a simpler store would pass by accident: two writers on one version
/// (<see cref="TWO_WRITERS_ON_ONE_VERSION_AND_THE_SECOND_IS_REFUSED_WITH_THE_NEWEST_VERSION"/>), a session's publish
/// (<see cref="A_SESSIONS_PUBLISH_IS_REFUSED_WITH_THE_SENTENCE_AND_NOTHING_CHANGES"/>), the content coming back
/// exactly as it went in (<see cref="THE_CONTENT_ROUND_TRIPS_BYTE_FOR_BYTE"/>), and another account
/// (<see cref="ANOTHER_ACCOUNT_NEVER_SEES_A_DEFINITION"/>).
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionStoreTests : IDisposable
{
    private const string TheFactory = "warmforward";
    private const string FactorySession = "5e551011-0000-0000-0000-000000000001";
    private const string OtherSession = "5e551011-0000-0000-0000-000000000002";
    private const string Owner = "device:the-owners-browser";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private FactoryDefinitionStore NewStore() => new(_h.Open());

    /// <summary>A small definition whose seat file says <paramref name="marker"/>, so versions can be told apart.</summary>
    private static Dictionary<string, string> Definition(string marker = "v1") => new()
    {
        ["factory.yaml"] = "factory: warmforward\ntitle: WarmForward\nbossSeat: boss\n",
        ["seats/boss.yaml"] = $"seat: boss\nrole: Boss\nbrief: |\n  {marker}\n",
    };

    private static FactoryDefinitionWrite SaveBySession(FactoryDefinitionStore store, IReadOnlyDictionary<string, string>? files,
        int? expect, string session = FactorySession, DateTime? at = null, string? note = null)
        => store.SaveDraft(A, TheFactory, files, expect, FactoryMemoryAuthorKinds.Session, session, note, at ?? Now);

    private static FactoryDefinitionWrite SaveByOwner(FactoryDefinitionStore store, IReadOnlyDictionary<string, string>? files,
        int? expect, string? note = null)
        => store.SaveDraft(A, TheFactory, files, expect, FactoryMemoryAuthorKinds.Person, Owner, note, Now);

    private static FactoryDefinitionWrite PublishByOwner(FactoryDefinitionStore store, int version, DateTime? at = null)
        => store.Publish(A, TheFactory, version, FactoryMemoryAuthorKinds.Person, Owner, at ?? Now.AddMinutes(5));

    private static string SeatText(FactoryDefinitionStore store, int version)
        => store.Find(A, TheFactory, version)!.Files["seats/boss.yaml"];

    private FactoryDefinitionStore WithALongHistory(int versions)
    {
        var store = NewStore();
        for (var i = 0; i < versions; i++)
            Assert.True(SaveByOwner(store, Definition($"draft {i + 1}"), expect: i).Ok);
        return store;
    }

    // ---------- saving and reading ----------

    [Fact]
    public void A_saved_definition_is_a_DRAFT_that_the_next_reader_gets_and_nothing_is_published_yet()
    {
        var store = NewStore();
        var saved = SaveBySession(store, Definition(), expect: 0, note: "the first definition");

        Assert.True(saved.Ok);
        Assert.Equal(FactoryDefinitionOutcome.Saved, saved.Outcome);
        Assert.Equal(1, saved.Version!.Version);

        var standing = NewStore().Standing(A, TheFactory);
        Assert.Equal(1, standing.Newest!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, standing.Newest.State);
        Assert.Equal(FactoryMemoryAuthorKinds.Session, standing.Newest.AuthorKind);
        Assert.Equal(FactorySession, standing.Newest.AuthorId);
        Assert.Equal("the first definition", standing.Newest.ChangeNote);
        Assert.Null(standing.Newest.RestoredFrom);
        Assert.Equal(2, standing.Newest.FileCount);
        Assert.Equal(Now, standing.Newest.WrittenAtUtc);
        Assert.Null(standing.Newest.PublishedAtUtc);
        Assert.Null(standing.Published);
        Assert.Equal(Definition(), NewStore().Find(A, TheFactory, 1)!.Files);
    }

    [Fact]
    public void A_second_save_adds_a_VERSION_and_never_edits_the_first()
    {
        var store = NewStore();
        SaveBySession(store, Definition("first"), expect: 0);
        var second = SaveBySession(store, Definition("second"), expect: 1);

        Assert.True(second.Ok);
        Assert.Equal(2, second.Version!.Version);
        Assert.Equal(new[] { 2, 1 }, store.History(A, TheFactory).Versions.Select(v => v.Version).ToArray());
        Assert.Contains("second", SeatText(store, 2));
        Assert.Contains("first", SeatText(store, 1));
    }

    [Fact]
    public void THE_CONTENT_ROUND_TRIPS_BYTE_FOR_BYTE()
    {
        // Whatever text a file holds comes back exactly: line endings of both kinds, tabs, quotes and backslashes,
        // characters outside the basic alphabet, a character pair, a control character and an empty file.
        var store = NewStore();
        var files = new Dictionary<string, string>
        {
            ["factory.yaml"] = "factory: warmforward\r\ntitle: \"Warm \\ Forward\"\r\n",
            ["seats/boss.yaml"] = "seat: boss\n\tbrief: café € \U0001F600 <a href='x'>&amp;</a>\u0000end",
            ["files/templates/CEO-BRIEF.md"] = "",
            ["checks/inbox.py"] = "print('{\"count\": 0}')\n",
        };

        Assert.True(SaveBySession(store, files, expect: 0).Ok);
        var read = NewStore().Find(A, TheFactory, 1)!;

        Assert.Equal(files.Count, read.Files.Count);
        foreach (var (path, text) in files)
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(text), System.Text.Encoding.UTF8.GetBytes(read.Files[path]));
        Assert.Equal(files.Values.Sum(t => System.Text.Encoding.UTF8.GetByteCount(t)), read.Summary.ContentBytes);
        Assert.Equal(4, read.Summary.FileCount);
    }

    [Fact]
    public void THE_SAME_MAP_IS_ALWAYS_THE_SAME_BYTES_whatever_order_its_files_were_given_in()
    {
        var store = NewStore();
        var forwards = new Dictionary<string, string> { ["factory.yaml"] = "f", ["grants.yaml"] = "g", ["seats/boss.yaml"] = "s" };
        var backwards = new Dictionary<string, string> { ["seats/boss.yaml"] = "s", ["grants.yaml"] = "g", ["factory.yaml"] = "f" };
        SaveByOwner(store, forwards, expect: 0);
        SaveByOwner(store, backwards, expect: 1);

        using var ctx = _h.Open().CreateContext(A);
        var rows = ctx.FactoryDefinitions.AsNoTracking().OrderBy(v => v.Version).Select(v => v.Content).ToList();
        Assert.Equal(rows[0], rows[1]);
        Assert.Equal("{\"factory.yaml\":\"f\",\"grants.yaml\":\"g\",\"seats/boss.yaml\":\"s\"}", rows[0]);
        // And the map read back is in the same ordinal order.
        Assert.Equal(new[] { "factory.yaml", "grants.yaml", "seats/boss.yaml" }, store.Find(A, TheFactory, 2)!.Files.Keys.ToArray());
    }

    [Fact]
    public void A_DEFINITION_NEEDS_NO_REGISTERED_FACTORY()
    {
        // Importing a definition is how a factory comes to exist (phase 3), so the store never asks the registry.
        // Nothing is registered in this whole file; a factory nobody has heard of takes a definition.
        var store = NewStore();

        var saved = store.SaveDraft(A, "brand-new-factory", Definition(), 0, FactoryMemoryAuthorKinds.Person, Owner, null, Now);

        Assert.True(saved.Ok);
        Assert.Equal("brand-new-factory", saved.Version!.Factory);
    }

    [Fact]
    public void A_FACTORY_SPELLED_TWO_WAYS_IS_ONE_DEFINITION()
    {
        var store = NewStore();
        SaveBySession(store, Definition(), expect: 0);

        var second = store.SaveDraft(A, " WarmForward ", Definition("v2"), 1, FactoryMemoryAuthorKinds.Session, FactorySession, null, Now);

        Assert.True(second.Ok);
        Assert.Equal(TheFactory, second.Version!.Factory);
        Assert.Equal(2, store.Standing(A, "WARMFORWARD").Newest!.Version);
        Assert.NotNull(store.Find(A, "WarmForward", 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("under_score")]
    public void A_factory_that_is_not_one_spelling_is_REFUSED_and_reads_as_nothing(string factory)
    {
        var store = NewStore();

        var refused = store.SaveDraft(A, factory, Definition(), 0, FactoryMemoryAuthorKinds.Person, Owner, null, Now);

        Assert.Equal(FactoryDefinitionOutcome.BadFactory, refused.Outcome);
        Assert.Equal(FactoryDefinitionOutcome.BadFactory, store.Publish(A, factory, 1, FactoryMemoryAuthorKinds.Person, Owner, Now).Outcome);
        Assert.Equal(FactoryDefinitionOutcome.BadFactory, store.Restore(A, factory, 1, FactoryMemoryAuthorKinds.Person, Owner, null, Now).Outcome);
        Assert.Same(FactoryDefinitionStanding.None, store.Standing(A, factory));
        Assert.Null(store.Find(A, factory, 1));
        Assert.Empty(store.History(A, factory).Versions);
    }

    // ---------- the history ----------

    [Fact]
    public void A_LONG_HISTORY_IS_READ_PAGE_BY_PAGE_and_every_version_is_seen_exactly_once()
    {
        var store = WithALongHistory(130);

        var seen = new List<int>();
        var pages = 0;
        int? before = null;
        do
        {
            var page = store.History(A, TheFactory, before);
            pages++;
            Assert.Equal(130, page.Total);
            Assert.True(page.Versions.Count <= FactoryDefinitionStore.DefaultHistoryPage);
            seen.AddRange(page.Versions.Select(v => v.Version));
            before = page.NextBefore;
        } while (before is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(1, 130).Reverse().ToArray(), seen.ToArray());
    }

    [Fact]
    public void A_page_says_where_the_next_one_starts_and_the_last_page_says_there_is_none()
    {
        var store = WithALongHistory(25);

        var first = store.History(A, TheFactory, before: null, limit: 10);
        Assert.Equal(new[] { 25, 24, 23, 22, 21, 20, 19, 18, 17, 16 }, first.Versions.Select(v => v.Version).ToArray());
        Assert.Equal(16, first.NextBefore);

        var second = store.History(A, TheFactory, before: first.NextBefore, limit: 10);
        Assert.Equal(15, second.Versions[0].Version);
        Assert.Equal(6, second.NextBefore);

        var last = store.History(A, TheFactory, before: second.NextBefore, limit: 10);
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, last.Versions.Select(v => v.Version).ToArray());
        Assert.Null(last.NextBefore);
    }

    [Fact]
    public void A_page_that_ends_exactly_on_the_oldest_version_does_not_promise_another()
    {
        var store = WithALongHistory(20);

        var page = store.History(A, TheFactory, before: 11, limit: 10);

        Assert.Equal(1, page.Versions[^1].Version);
        Assert.Null(page.NextBefore);
        var below = store.History(A, TheFactory, before: 1, limit: 10);
        Assert.Empty(below.Versions);
        Assert.Equal(20, below.Total);
        Assert.Null(below.NextBefore);
    }

    [Fact]
    public void A_page_carries_what_each_version_is_HOW_BIG_IT_IS_and_HOW_MANY_FILES_it_holds()
    {
        var store = NewStore();
        SaveBySession(store, new Dictionary<string, string> { ["factory.yaml"] = "plain" }, expect: 0, note: "a start");
        // Four characters outside the basic alphabet: three bytes each.
        SaveByOwner(store, new Dictionary<string, string> { ["factory.yaml"] = "€€€€", ["grants.yaml"] = "ab" }, expect: 1);
        PublishByOwner(store, 2, at: Now.AddMinutes(2));

        var page = store.History(A, TheFactory);

        var newest = page.Versions[0];
        Assert.Equal(2, newest.Version);
        Assert.Equal(14, newest.ContentBytes);
        Assert.Equal(2, newest.FileCount);
        Assert.Equal(FactoryDefinitionStates.Published, newest.State);
        Assert.Equal(FactoryMemoryAuthorKinds.Person, newest.AuthorKind);
        Assert.Equal(Owner, newest.AuthorId);
        Assert.Equal(Owner, newest.PublishedBy);
        Assert.Equal(Now.AddMinutes(2), newest.PublishedAtUtc);
        Assert.Null(newest.ChangeNote);
        var oldest = page.Versions[1];
        Assert.Equal(5, oldest.ContentBytes);
        Assert.Equal(1, oldest.FileCount);
        Assert.Equal("a start", oldest.ChangeNote);
        Assert.Equal(FactorySession, oldest.AuthorId);
    }

    [Fact]
    public void A_PAGE_OF_THE_HISTORY_NEVER_CARRIES_A_VERSIONS_CONTENT()
    {
        // The summary a page is made of has no content to carry. Every version is kept for ever, so a history with
        // the content in it grows until the answer is the outage.
        var properties = typeof(FactoryDefinitionVersionSummary).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(nameof(FactoryDefinitionVersionEntity.Content), properties);
        Assert.DoesNotContain(nameof(FactoryDefinitionVersion.Files), properties);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FactoryDefinitionStore.MaxHistoryPage + 1)]
    public void A_page_size_that_is_not_one_is_a_defect_in_the_caller_not_something_to_quietly_replace(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewStore().History(A, TheFactory, before: null, limit: limit));
    }

    [Fact]
    public void One_version_is_read_by_its_number_and_a_version_that_does_not_exist_is_null()
    {
        var store = NewStore();
        SaveBySession(store, Definition("the first"), expect: 0);
        SaveBySession(store, Definition("the second"), expect: 1);

        Assert.Contains("the first", SeatText(store, 1));
        Assert.Contains("the second", SeatText(store, 2));
        Assert.Null(store.Find(A, TheFactory, 3));
        Assert.Null(store.Find(A, "another-factory", 1));
    }

    // ---------- two writers ----------

    [Fact]
    public void TWO_WRITERS_ON_ONE_VERSION_AND_THE_SECOND_IS_REFUSED_WITH_THE_NEWEST_VERSION()
    {
        // Both read version 1 and both save - through two stores, as two replicas of the Gateway would.
        var one = NewStore();
        var two = NewStore();
        SaveBySession(one, Definition("start"), expect: 0);

        var first = SaveBySession(one, Definition("start, plus the first writer"), expect: 1, session: FactorySession);
        var second = SaveBySession(two, Definition("start, plus the second writer"), expect: 1, session: OtherSession);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(FactoryDefinitionOutcome.Stale, second.Outcome);
        Assert.Contains("is at version 2, not 1", second.Refusal);
        Assert.Equal(2, second.Version!.Version);
        Assert.Equal(FactorySession, second.Version.AuthorId);
        // The winner's content stands, and the loser's was not stored anywhere.
        Assert.Equal(2, one.History(A, TheFactory).Total);
        Assert.Contains("the first writer", SeatText(one, 2));
    }

    [Fact]
    public void THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION()
    {
        // The property the design rests on, tested where it lives: two rows claiming (account, factory, version)
        // cannot both exist, whatever any one process believes.
        var store = NewStore();
        SaveBySession(store, Definition(), expect: 0);

        using var ctx = _h.Open().CreateContext(A);
        ctx.FactoryDefinitions.Add(new FactoryDefinitionVersionEntity
        {
            TenantId = A.Value, Factory = TheFactory, Version = 1, Content = "{}", State = FactoryDefinitionStates.Draft,
            AuthorKind = FactoryMemoryAuthorKinds.Session, AuthorId = OtherSession, WrittenAtUtc = Now,
        });

        Assert.ThrowsAny<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void ONLY_A_REAL_SECOND_WRITER_IS_CALLED_ONE()
    {
        static FactoryDefinitionVersionSummary At(int version) => new(TheFactory, version, FactoryDefinitionStates.Draft,
            FactoryMemoryAuthorKinds.Person, null, null, null, Now, null, null, 0, 0);

        Assert.True(FactoryDefinitionStore.WasTakenByAnotherWriter(At(4), attemptedVersion: 4));
        Assert.False(FactoryDefinitionStore.WasTakenByAnotherWriter(At(3), attemptedVersion: 4));
        Assert.False(FactoryDefinitionStore.WasTakenByAnotherWriter(null, attemptedVersion: 1));
    }

    [Fact]
    public void A_STALE_SAVE_expecting_no_definition_when_there_is_one_is_refused_and_carries_it()
    {
        var store = NewStore();
        SaveBySession(store, Definition("what is there"), expect: 0);

        var refused = SaveBySession(store, Definition("written blind"), expect: 0);

        Assert.Equal(FactoryDefinitionOutcome.Stale, refused.Outcome);
        Assert.Equal(1, refused.Version!.Version);
        Assert.Equal(1, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_first_save_that_expects_a_version_is_refused_and_says_to_expect_zero()
    {
        var store = NewStore();

        var refused = SaveBySession(store, Definition(), expect: 3);

        Assert.Equal(FactoryDefinitionOutcome.Stale, refused.Outcome);
        Assert.Contains("expecting version 0", refused.Refusal);
        Assert.Null(refused.Version);
    }

    // ---------- the bound on a session's drafts ----------

    [Fact]
    public void A_SESSION_IS_REFUSED_THE_DRAFT_THAT_WOULD_CROSS_THE_LIMIT_and_the_refusal_says_what_to_do()
    {
        var store = NewStore();
        for (var i = 0; i < FactoryDefinitionStore.MaxWaitingDrafts; i++)
            Assert.True(SaveBySession(store, Definition($"draft {i + 1}"), expect: i).Ok, $"draft {i + 1} is within the limit");

        var refused = SaveBySession(store, Definition("one too many"), expect: FactoryDefinitionStore.MaxWaitingDrafts);

        Assert.Equal(FactoryDefinitionOutcome.TooManyDrafts, refused.Outcome);
        Assert.Contains($"fewer than {FactoryDefinitionStore.MaxWaitingDrafts}", refused.Refusal);
        Assert.Contains("Ask the owner to read the drafts and publish one", refused.Refusal);
        Assert.Equal(FactoryDefinitionStore.MaxWaitingDrafts, refused.Version!.Version);
        Assert.Equal(FactoryDefinitionStore.MaxWaitingDrafts, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_PERSON_IS_NOT_BOUND_because_the_person_is_the_one_who_makes_room()
    {
        var store = NewStore();
        for (var i = 0; i < FactoryDefinitionStore.MaxWaitingDrafts; i++)
            SaveBySession(store, Definition($"draft {i + 1}"), expect: i);

        var byPerson = SaveByOwner(store, Definition("the owner's own"), expect: FactoryDefinitionStore.MaxWaitingDrafts);

        Assert.True(byPerson.Ok);
        Assert.Equal(FactoryDefinitionStore.MaxWaitingDrafts + 1, byPerson.Version!.Version);
    }

    [Fact]
    public void PUBLISHING_MAKES_ROOM_and_removes_nothing()
    {
        var store = NewStore();
        for (var i = 0; i < FactoryDefinitionStore.MaxWaitingDrafts; i++)
            SaveBySession(store, Definition($"draft {i + 1}"), expect: i);
        Assert.True(PublishByOwner(store, FactoryDefinitionStore.MaxWaitingDrafts).Ok);

        var after = SaveBySession(store, Definition("after the publish"), expect: FactoryDefinitionStore.MaxWaitingDrafts);

        Assert.True(after.Ok);
        Assert.Equal(FactoryDefinitionStore.MaxWaitingDrafts + 1, store.History(A, TheFactory).Total);
    }

    // ---------- draft, then published ----------

    [Fact]
    public void A_PERSON_PUBLISHES_A_VERSION_AND_THEN_A_SECOND_ONE()
    {
        var store = NewStore();
        SaveBySession(store, Definition("first"), expect: 0);

        var first = PublishByOwner(store, 1);
        Assert.True(first.Ok);
        Assert.Equal(FactoryDefinitionOutcome.Published, first.Outcome);
        Assert.Equal(FactoryDefinitionStates.Published, first.Version!.State);
        Assert.Equal(Owner, first.Version.PublishedBy);
        Assert.Equal(1, store.Standing(A, TheFactory).Published!.Version);

        SaveBySession(store, Definition("second"), expect: 1);
        // A draft saved after a publish does not change what is published.
        Assert.Equal(1, store.Standing(A, TheFactory).Published!.Version);

        var second = PublishByOwner(store, 2, at: Now.AddMinutes(30));
        Assert.True(second.Ok);
        var standing = store.Standing(A, TheFactory);
        Assert.Equal(2, standing.Published!.Version);
        Assert.Equal(Now.AddMinutes(30), standing.Published.PublishedAtUtc);
        // The first keeps its own publish mark: the mark is never taken off.
        Assert.Equal(FactoryDefinitionStates.Published, store.Find(A, TheFactory, 1)!.Summary.State);
    }

    [Fact]
    public void A_SESSIONS_PUBLISH_IS_REFUSED_WITH_THE_SENTENCE_AND_NOTHING_CHANGES()
    {
        var store = NewStore();
        SaveBySession(store, Definition(), expect: 0);

        var refused = store.Publish(A, TheFactory, 1, FactoryMemoryAuthorKinds.Session, FactorySession, Now);

        Assert.False(refused.Ok);
        Assert.Equal(FactoryDefinitionOutcome.OnlyAPersonPublishes, refused.Outcome);
        Assert.Equal(
            "Only a person publishes a factory's definition. A session of the factory may save a draft; " +
            "the owner reads it and publishes it.",
            refused.Refusal);
        var standing = store.Standing(A, TheFactory);
        Assert.Null(standing.Published);
        Assert.Equal(FactoryDefinitionStates.Draft, standing.Newest!.State);
    }

    [Fact]
    public void Publishing_the_published_version_again_is_refused_and_keeps_who_published_it_first()
    {
        var store = NewStore();
        SaveBySession(store, Definition(), expect: 0);
        PublishByOwner(store, 1);

        var again = store.Publish(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "device:somebody-else", Now.AddHours(1));

        Assert.Equal(FactoryDefinitionOutcome.AlreadyPublished, again.Outcome);
        var published = store.Standing(A, TheFactory).Published!;
        Assert.Equal(Owner, published.PublishedBy);
        Assert.Equal(Now.AddMinutes(5), published.PublishedAtUtc);
    }

    [Fact]
    public void PUBLISHING_NEVER_GOES_BACKWARDS_an_older_draft_is_refused_and_told_to_be_restored()
    {
        var store = NewStore();
        SaveBySession(store, Definition("v1, never published"), expect: 0);
        SaveBySession(store, Definition("v2"), expect: 1);
        PublishByOwner(store, 2);

        var refused = PublishByOwner(store, 1);

        Assert.Equal(FactoryDefinitionOutcome.OlderThanPublished, refused.Outcome);
        Assert.Contains("older than the published version 2", refused.Refusal);
        Assert.Contains("restore version 1", refused.Refusal);
        Assert.Equal(2, store.Standing(A, TheFactory).Published!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, store.Find(A, TheFactory, 1)!.Summary.State);
    }

    [Fact]
    public void Publishing_a_version_that_does_not_exist_is_refused()
    {
        var store = NewStore();

        var none = PublishByOwner(store, 1);
        Assert.Equal(FactoryDefinitionOutcome.NoSuchVersion, none.Outcome);
        Assert.Contains("Save a draft first", none.Refusal);

        SaveBySession(store, Definition(), expect: 0);
        var missing = PublishByOwner(store, 7);
        Assert.Equal(FactoryDefinitionOutcome.NoSuchVersion, missing.Outcome);
        Assert.Contains("its newest version is 1", missing.Refusal);
    }

    // ---------- restore ----------

    [Fact]
    public void RESTORING_AN_OLDER_VERSION_PUBLISHES_A_COPY_OF_IT_AS_THE_NEWEST_VERSION_and_records_where_from()
    {
        // The flow the mission's check names: a draft, published; a second version, published; the first restored.
        var store = NewStore();
        SaveBySession(store, Definition("the first definition"), expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, Definition("the second definition"), expect: 1);
        PublishByOwner(store, 2, at: Now.AddMinutes(10));

        var restored = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, "back to the first",
            Now.AddMinutes(20), expectedVersion: 2);

        Assert.True(restored.Ok);
        Assert.Equal(FactoryDefinitionOutcome.Published, restored.Outcome);
        var copy = restored.Version!;
        Assert.Equal(3, copy.Version);
        Assert.Equal(1, copy.RestoredFrom);
        Assert.Equal(FactoryDefinitionStates.Published, copy.State);
        Assert.Equal(FactoryMemoryAuthorKinds.Person, copy.AuthorKind);
        Assert.Equal(Owner, copy.AuthorId);
        Assert.Equal("back to the first", copy.ChangeNote);
        Assert.Equal(Now.AddMinutes(20), copy.PublishedAtUtc);
        // The copy's content is version 1's, whole.
        Assert.Equal(store.Find(A, TheFactory, 1)!.Files, store.Find(A, TheFactory, 3)!.Files);

        var standing = store.Standing(A, TheFactory);
        Assert.Equal(3, standing.Published!.Version);
        Assert.Equal(3, standing.Newest!.Version);
        // NOT A REWIND: the history still shows the second version, and the first is as it was.
        var history = store.History(A, TheFactory).Versions;
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(v => v.Version).ToArray());
        Assert.Null(history[1].RestoredFrom);
        Assert.Contains("the second definition", SeatText(store, 2));
        Assert.Equal(FactorySession, history[2].AuthorId);
    }

    [Fact]
    public void A_restore_can_LOSE_A_RACE_with_a_draft_saved_since_the_person_read_the_history()
    {
        var store = NewStore();
        SaveBySession(store, Definition("v1"), expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, Definition("v2"), expect: 1);
        PublishByOwner(store, 2);
        SaveBySession(store, Definition("v3, a session's draft"), expect: 2);

        var refused = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, null, Now, expectedVersion: 2);

        Assert.Equal(FactoryDefinitionOutcome.Stale, refused.Outcome);
        Assert.Equal(3, refused.Version!.Version);
        Assert.Equal(3, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_restore_that_states_no_version_goes_after_whatever_is_newest()
    {
        var store = NewStore();
        SaveBySession(store, Definition("v1"), expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, Definition("v2"), expect: 1);
        PublishByOwner(store, 2);
        SaveBySession(store, Definition("v3, a draft"), expect: 2);

        var restored = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, null, Now);

        Assert.True(restored.Ok);
        Assert.Equal(4, restored.Version!.Version);
        Assert.Equal(1, restored.Version.RestoredFrom);
        Assert.Contains("v1", SeatText(store, 4));
    }

    [Fact]
    public void A_DRAFT_THAT_WAS_NEVER_PUBLISHED_CAN_BE_RESTORED_which_is_how_an_older_draft_is_published()
    {
        var store = NewStore();
        SaveBySession(store, Definition("v1, never published"), expect: 0);
        SaveBySession(store, Definition("v2"), expect: 1);
        PublishByOwner(store, 2);

        var restored = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, null, Now);

        Assert.True(restored.Ok);
        Assert.Equal(3, store.Standing(A, TheFactory).Published!.Version);
        Assert.Contains("v1, never published", SeatText(store, 3));
    }

    [Fact]
    public void Restoring_the_published_version_or_one_that_does_not_exist_is_refused()
    {
        var store = NewStore();
        SaveBySession(store, Definition(), expect: 0);
        PublishByOwner(store, 1);

        var same = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, null, Now);
        Assert.Equal(FactoryDefinitionOutcome.AlreadyPublished, same.Outcome);
        Assert.Contains("nothing to restore", same.Refusal);

        var missing = store.Restore(A, TheFactory, 9, FactoryMemoryAuthorKinds.Person, Owner, null, Now);
        Assert.Equal(FactoryDefinitionOutcome.NoSuchVersion, missing.Outcome);
        Assert.Equal(1, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_SESSIONS_RESTORE_IS_REFUSED_WITH_THE_SENTENCE_because_restoring_publishes()
    {
        var store = NewStore();
        SaveBySession(store, Definition("v1"), expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, Definition("v2"), expect: 1);
        PublishByOwner(store, 2);

        var refused = store.Restore(A, TheFactory, 1, FactoryMemoryAuthorKinds.Session, FactorySession, null, Now);

        Assert.Equal(FactoryDefinitionOutcome.OnlyAPersonPublishes, refused.Outcome);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonRestores, refused.Refusal);
        Assert.StartsWith("Only a person publishes a factory's definition", refused.Refusal);
        Assert.Equal(2, store.History(A, TheFactory).Total);
        Assert.Equal(2, store.Standing(A, TheFactory).Published!.Version);
    }

    [Fact]
    public void A_writer_that_is_neither_a_person_nor_a_session_is_a_defect_not_a_refusal()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.SaveDraft(A, TheFactory, Definition(), 0, "machine", null, null, Now));
        Assert.Throws<ArgumentException>(() => store.Publish(A, TheFactory, 1, "machine", null, Now));
        Assert.Throws<ArgumentException>(() => store.Restore(A, TheFactory, 1, "machine", null, null, Now));
    }

    // ---------- the caps ----------

    [Fact]
    public void A_file_larger_than_its_cap_is_REFUSED_by_path_and_one_exactly_at_the_cap_is_accepted()
    {
        var store = NewStore();
        var files = Definition();
        files["files/big.txt"] = new string('x', DefinitionPaths.MaxFileBytes + 1);

        var refused = SaveBySession(store, files, expect: 0);
        Assert.Equal(FactoryDefinitionOutcome.FileTooLarge, refused.Outcome);
        Assert.Contains("'files/big.txt'", refused.Refusal);
        Assert.Contains($"at most {DefinitionPaths.MaxFileBytes}", refused.Refusal);
        Assert.Equal(0, store.History(A, TheFactory).Total);

        files["files/big.txt"] = new string('x', DefinitionPaths.MaxFileBytes);
        Assert.True(SaveBySession(store, files, expect: 0).Ok);
    }

    [Fact]
    public void The_file_cap_counts_BYTES_so_text_outside_the_basic_alphabet_cannot_slip_past_it()
    {
        var store = NewStore();
        var files = Definition();
        // Three bytes each in UTF-8: a third of the cap in characters, plus one, is over the cap in bytes.
        files["files/euros.txt"] = new string('€', DefinitionPaths.MaxFileBytes / 3 + 1);

        Assert.Equal(FactoryDefinitionOutcome.FileTooLarge, SaveBySession(store, files, expect: 0).Outcome);
    }

    [Fact]
    public void A_version_larger_than_its_cap_is_REFUSED_and_one_exactly_at_the_cap_is_accepted()
    {
        var store = NewStore();
        // Sixteen files at the file cap are exactly the version cap.
        var files = new Dictionary<string, string>();
        var perFile = DefinitionPaths.MaxFileBytes;
        for (var i = 0; i < DefinitionPaths.MaxVersionBytes / perFile; i++)
            files[$"files/part{i:00}.txt"] = new string('x', perFile);

        var atCap = SaveByOwner(store, files, expect: 0);
        Assert.True(atCap.Ok, atCap.Refusal);
        Assert.Equal(DefinitionPaths.MaxVersionBytes, atCap.Version!.ContentBytes);

        files["factory.yaml"] = "x";
        var over = SaveByOwner(store, files, expect: 1);
        Assert.Equal(FactoryDefinitionOutcome.VersionTooLarge, over.Outcome);
        Assert.Contains($"at most {DefinitionPaths.MaxVersionBytes}", over.Refusal);
        Assert.Equal(1, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_version_with_more_files_than_its_cap_is_REFUSED_and_one_exactly_at_the_cap_is_accepted()
    {
        // The byte caps count text only, so an empty file costs nothing toward them; this cap is what bounds them.
        var store = NewStore();
        var files = Enumerable.Range(0, DefinitionPaths.MaxFiles).ToDictionary(i => $"files/empty{i}.txt", _ => "");

        Assert.True(SaveByOwner(store, files, expect: 0).Ok);

        files["files/one-more.txt"] = "";
        var over = SaveByOwner(store, files, expect: 1);
        Assert.Equal(FactoryDefinitionOutcome.TooManyFiles, over.Outcome);
        Assert.Contains($"at most {DefinitionPaths.MaxFiles}", over.Refusal);
    }

    [Fact]
    public void EMPTY_CONTENT_IS_REFUSED_whether_nothing_or_no_files_were_sent()
    {
        var store = NewStore();

        Assert.Equal(FactoryDefinitionOutcome.Empty, SaveBySession(store, null, expect: 0).Outcome);
        Assert.Equal(FactoryDefinitionOutcome.Empty, SaveBySession(store, new Dictionary<string, string>(), expect: 0).Outcome);
        Assert.Equal(0, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_file_with_no_text_or_text_that_is_not_UTF8_is_REFUSED()
    {
        var store = NewStore();
        var noText = Definition();
        noText["grants.yaml"] = null!;
        var halfAPair = Definition();
        halfAPair["grants.yaml"] = "grants: \ud83d";

        var refusedNull = SaveBySession(store, noText, expect: 0);
        var refusedHalf = SaveBySession(store, halfAPair, expect: 0);

        Assert.Equal(FactoryDefinitionOutcome.NotText, refusedNull.Outcome);
        Assert.Contains("'grants.yaml'", refusedNull.Refusal);
        Assert.Equal(FactoryDefinitionOutcome.NotText, refusedHalf.Outcome);
        Assert.Equal(0, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void A_change_note_over_its_cap_is_REFUSED_and_one_exactly_at_the_cap_is_kept_whole()
    {
        var store = NewStore();
        var atCap = new string('n', FactoryDefinitionStore.MaxChangeNoteLength);

        var over = SaveBySession(store, Definition(), expect: 0, note: atCap + "n");
        Assert.Equal(FactoryDefinitionOutcome.ChangeNoteTooLong, over.Outcome);
        Assert.Contains($"at most {FactoryDefinitionStore.MaxChangeNoteLength}", over.Refusal);

        Assert.True(SaveBySession(store, Definition(), expect: 0, note: atCap).Ok);
        Assert.Equal(atCap, store.Standing(A, TheFactory).Newest!.ChangeNote);
    }

    // ---------- the paths ----------

    [Theory]
    [InlineData("factory.yaml")]
    [InlineData("grants.yaml")]
    [InlineData("seats/boss.yaml")]
    [InlineData("rules/outreach.json")]
    [InlineData("files/outreach_send.py")]
    [InlineData("files/templates/deep/CEO-BRIEF.md")]
    [InlineData("checks/inbox.py")]
    [InlineData("checks/new_mail.py")]
    [InlineData("skills/prospecting/SKILL.md")]
    [InlineData("skills/prospecting/scripts/find.py")]
    public void Every_place_a_definition_holds_is_ACCEPTED(string path)
    {
        Assert.Null(FactoryDefinitionStore.WhyNotAPath(path));

        var store = NewStore();
        var files = Definition();
        files[path] = "text";
        Assert.True(SaveByOwner(store, files, expect: 0).Ok);
        Assert.Equal("text", store.Find(A, TheFactory, 1)!.Files[path]);
    }

    [Theory]
    [InlineData("seats\\boss.yaml", "contains a backslash")]
    [InlineData("/factory.yaml", "begins with a slash")]
    [InlineData("files//double.txt", "has an empty part")]
    [InlineData("files/trailing/", "has an empty part")]
    [InlineData("files/./here.txt", "has a '.' part")]
    [InlineData("files/../factory.yaml", "has a '..' part")]
    [InlineData("files/line\nbreak.txt", "control character")]
    [InlineData("", "it is empty")]
    [InlineData("README.md", "is not a place a definition holds")]
    [InlineData("Factory.yaml", "is not a place a definition holds")]
    [InlineData("factory.yaml/more", "is not a place a definition holds")]
    [InlineData("seats/boss.yml", "is not a place a definition holds")]
    [InlineData("seats/team/boss.yaml", "is not a place a definition holds")]
    [InlineData("rules/outreach.yaml", "is not a place a definition holds")]
    [InlineData("checks/inbox.sh", "is not a place a definition holds")]
    [InlineData("files", "is not a place a definition holds")]
    [InlineData("skills/prospecting", "is not a place a definition holds")]
    [InlineData("seats/.yaml", "a seat name is")]
    [InlineData("seats/Boss.yaml", "a seat name is")]
    [InlineData("skills/Prospecting/SKILL.md", "a skill name is")]
    public void A_path_that_breaks_a_rule_is_REFUSED_and_says_which_rule(string path, string words)
    {
        Assert.Contains(words, FactoryDefinitionStore.WhyNotAPath(path));

        var store = NewStore();
        var files = Definition();
        files[path] = "text";
        var refused = SaveByOwner(store, files, expect: 0);
        Assert.Equal(FactoryDefinitionOutcome.BadPath, refused.Outcome);
        Assert.Contains(words, refused.Refusal);
        Assert.Equal(0, store.History(A, TheFactory).Total);
    }

    [Fact]
    public void THE_STORE_REFUSES_EXACTLY_WHAT_THE_CHECK_REFUSES_because_it_asks_the_same_rule()
    {
        // The store holds no path the whole-definition check would refuse, whoever calls it: its answer is the
        // check's own sentence, word for word.
        foreach (var path in new[] { "notes.txt", "seats/Boss.yaml", "/factory.yaml", "checks/new_mail.py" })
            Assert.Equal(DefinitionPaths.ShapeProblem(path) ?? DefinitionPaths.Classify(path).Refusal,
                FactoryDefinitionStore.WhyNotAPath(path));
        Assert.Equal($"'notes.txt' is not a place a definition holds. The places are: {DefinitionPaths.PlacesText}.",
            FactoryDefinitionStore.WhyNotAPath("notes.txt"));
        Assert.Equal("a definition holds no file without a path", FactoryDefinitionStore.WhyNotAPath(null));
    }

    [Fact]
    public void A_path_over_its_length_is_REFUSED_and_one_exactly_at_it_is_accepted()
    {
        var atLimit = "files/" + new string('a', DefinitionPaths.MaxPathLength - "files/".Length);
        Assert.Null(FactoryDefinitionStore.WhyNotAPath(atLimit));
        Assert.Contains($"it is {DefinitionPaths.MaxPathLength + 1} characters long", FactoryDefinitionStore.WhyNotAPath(atLimit + "a"));
    }

    // ---------- another account ----------

    [Fact]
    public void ANOTHER_ACCOUNT_NEVER_SEES_A_DEFINITION()
    {
        // Account B uses the very same factory id. It must see none of A's versions, and its own chain must start at
        // version 1 rather than continue A's.
        var store = NewStore();
        SaveBySession(store, Definition("account A's"), expect: 0);
        PublishByOwner(store, 1);

        Assert.Null(store.Standing(B, TheFactory).Newest);
        Assert.Null(store.Standing(B, TheFactory).Published);
        Assert.Empty(store.History(B, TheFactory).Versions);
        Assert.Equal(0, store.History(B, TheFactory).Total);
        Assert.Null(store.Find(B, TheFactory, 1));
        // B cannot publish or restore A's version either: for B it does not exist.
        Assert.Equal(FactoryDefinitionOutcome.NoSuchVersion,
            store.Publish(B, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "device:b", Now).Outcome);
        Assert.Equal(FactoryDefinitionOutcome.NoSuchVersion,
            store.Restore(B, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "device:b", null, Now).Outcome);

        var bs = store.SaveDraft(B, TheFactory, Definition("account B's"), 0, FactoryMemoryAuthorKinds.Person, "device:b", null, Now);
        Assert.True(bs.Ok);
        Assert.Equal(1, bs.Version!.Version);
        // And A still reads its own.
        Assert.Contains("account A's", SeatText(store, 1));
        Assert.Equal(1, store.History(A, TheFactory).Total);
        Assert.Equal(1, store.Standing(A, TheFactory).Published!.Version);
    }
}
