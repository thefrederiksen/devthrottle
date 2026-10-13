using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// A SEAT'S BRIEF ON THE GATEWAY: the store contract (Factory Seats mission, phase 2a). Runs over the real Entity
/// Framework store on a throwaway SQLite file, exactly as the Gateway runs it locally, with the real factory
/// registry deciding which seats exist.
///
/// The cases that matter most are the ones a simpler store would pass by accident: two writers on one version
/// (<see cref="TWO_WRITERS_ON_ONE_VERSION_AND_THE_SECOND_IS_REFUSED_WITH_THE_NEWEST_TEXT"/>), a session's publish
/// (<see cref="A_SESSIONS_PUBLISH_IS_REFUSED_WITH_THE_SENTENCE_AND_NOTHING_CHANGES"/>), and another account
/// (<see cref="ANOTHER_ACCOUNT_NEVER_SEES_A_BRIEF"/>). A store that simply overwrote, or that let any writer
/// publish, would look healthy in every other test here.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryBriefStoreTests : IDisposable
{
    private const string TheFactory = "warmforward";
    private const string Boss = "boss";
    private const string Scout = "scout";
    private const string ScoutSession = "5e551011-0000-0000-0000-000000000001";
    private const string SenderSession = "5e551011-0000-0000-0000-000000000002";
    private const string Owner = "device:the-owners-browser";
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    /// <summary>A store over the harness's database, with <see cref="TheFactory"/> registered in account A.</summary>
    private FactoryBriefStore NewStore(bool registered = true)
    {
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        if (registered && registry.Find(A, TheFactory) is null) registry.Register(A, Manifest(), "the owner (test)", Now);
        return new FactoryBriefStore(db, registry);
    }

    private static RegisterFactoryRequest Manifest() => new()
    {
        Factory = TheFactory,
        Title = "WarmForward",
        Folder = "/home/soren/warmforward-factory",
        Computer = "DEVLINUX",
        BossSeat = Boss,
        Seats =
        {
            new FactorySeatManifest { Id = Boss, Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
            new FactorySeatManifest { Id = Scout, Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml" },
        },
    };

    private static FactoryBriefWrite SaveBySession(FactoryBriefStore store, string text, int? expect,
        string seat = Boss, string session = ScoutSession, DateTime? at = null)
        => store.SaveDraft(A, TheFactory, seat, text, expect, FactoryMemoryAuthorKinds.Session, session, at ?? Now);

    private static FactoryBriefWrite PublishByOwner(FactoryBriefStore store, int version, string seat = Boss, DateTime? at = null)
        => store.Publish(A, TheFactory, seat, version, FactoryMemoryAuthorKinds.Person, Owner, at ?? Now.AddMinutes(5));

    // ---------- saving and reading ----------

    [Fact]
    public void A_saved_brief_is_a_DRAFT_that_the_next_reader_gets_and_nothing_is_published_yet()
    {
        var store = NewStore();
        var saved = SaveBySession(store, "You are the boss of WarmForward.", expect: 0);

        Assert.True(saved.Ok);
        Assert.Equal(FactoryBriefOutcome.Saved, saved.Outcome);
        Assert.Equal(1, saved.Brief!.Version);

        var standing = NewStore().Standing(A, TheFactory, Boss);
        Assert.Equal("You are the boss of WarmForward.", standing.Newest!.Text);
        Assert.Equal(FactoryBriefStates.Draft, standing.Newest.State);
        Assert.Equal(FactoryMemoryAuthorKinds.Session, standing.Newest.AuthorKind);
        Assert.Equal(ScoutSession, standing.Newest.AuthorId);
        Assert.Equal(Now, standing.Newest.WrittenAtUtc);
        Assert.Null(standing.Newest.PublishedAtUtc);
        // A draft is not what a talk or a run is given.
        Assert.Null(standing.Published);
    }

    [Fact]
    public void A_second_save_adds_a_VERSION_and_never_edits_the_first()
    {
        var store = NewStore();
        SaveBySession(store, "first", expect: 0);
        var second = SaveBySession(store, "first, and also second", expect: 1);

        Assert.True(second.Ok);
        Assert.Equal(2, second.Brief!.Version);

        var history = store.History(A, TheFactory, Boss);
        Assert.Equal(new[] { 2, 1 }, history.Select(v => v.Version).ToArray());
        Assert.Equal("first, and also second", history[0].Text);
        Assert.Equal("first", history[1].Text);
    }

    [Fact]
    public void EVERY_VERSION_IS_KEPT_however_many_there_are()
    {
        // The mission's goal 5: every version kept. Factory memory prunes to fifty; a brief does not, because an
        // older version must always be there to bring back.
        var store = NewStore();
        for (var i = 0; i < 60; i++)
            Assert.True(SaveBySession(store, $"draft {i + 1}", expect: i, at: Now.AddDays(-400 + i)).Ok);

        var history = store.History(A, TheFactory, Boss);
        Assert.Equal(60, history.Count);
        Assert.Equal("draft 1", history[^1].Text);
    }

    [Fact]
    public void One_seats_brief_is_not_anothers()
    {
        var store = NewStore();
        SaveBySession(store, "the boss's brief", expect: 0);

        Assert.Null(store.Standing(A, TheFactory, Scout).Newest);
        Assert.Empty(store.History(A, TheFactory, Scout));
        // And the other seat starts its own chain at version 1.
        Assert.Equal(1, SaveBySession(store, "the scout's brief", expect: 0, seat: Scout).Brief!.Version);
    }

    [Fact]
    public void A_FACTORY_AND_A_SEAT_SPELLED_TWO_WAYS_ARE_ONE_BRIEF()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);

        var second = store.SaveDraft(A, "WarmForward", " Boss ", "v2", 1, FactoryMemoryAuthorKinds.Session, ScoutSession, Now);

        Assert.True(second.Ok);
        Assert.Equal(TheFactory, second.Brief!.Factory);
        Assert.Equal(Boss, second.Brief.Seat);
        Assert.Equal(2, store.Standing(A, "WARMFORWARD", "BOSS").Newest!.Version);
    }

    // ---------- only a registered seat has a brief ----------

    [Fact]
    public void A_BRIEF_FOR_A_FACTORY_THAT_IS_NOT_REGISTERED_IS_REFUSED_and_nothing_is_stored()
    {
        var store = NewStore(registered: false);

        var refused = SaveBySession(store, "a brief for nobody", expect: 0);

        Assert.False(refused.Ok);
        Assert.Equal(FactoryBriefOutcome.FactoryNotRegistered, refused.Outcome);
        Assert.Contains("'warmforward' is not registered", refused.Refusal);
        Assert.Contains("cc-devthrottle factory register", refused.Refusal);
        Assert.Empty(store.History(A, TheFactory, Boss));
    }

    [Fact]
    public void A_BRIEF_FOR_A_SEAT_THAT_IS_NOT_REGISTERED_IS_REFUSED_and_the_refusal_names_the_seats_there_are()
    {
        var store = NewStore();

        var refused = SaveBySession(store, "a brief for a seat nobody registered", expect: 0, seat: "night-watch");

        Assert.False(refused.Ok);
        Assert.Equal(FactoryBriefOutcome.SeatNotRegistered, refused.Outcome);
        Assert.Contains("'night-watch' is not a seat of warmforward", refused.Refusal);
        Assert.Contains("boss, scout", refused.Refusal);
        Assert.Empty(store.History(A, TheFactory, "night-watch"));
    }

    [Fact]
    public void Publishing_and_bringing_back_are_refused_for_a_seat_that_is_not_registered_too()
    {
        var store = NewStore();

        Assert.Equal(FactoryBriefOutcome.SeatNotRegistered, PublishByOwner(store, 1, seat: "night-watch").Outcome);
        Assert.Equal(FactoryBriefOutcome.SeatNotRegistered,
            store.BringBack(A, TheFactory, "night-watch", 1, FactoryMemoryAuthorKinds.Person, Owner, Now).Outcome);
    }

    [Theory]
    [InlineData("Warm Forward", "boss", FactoryBriefOutcome.BadFactory)]
    [InlineData("warmforward", "../boss", FactoryBriefOutcome.BadSeat)]
    [InlineData("warmforward", "", FactoryBriefOutcome.BadSeat)]
    public void A_factory_or_a_seat_that_is_not_one_spelling_is_REFUSED(string factory, string seat, FactoryBriefOutcome outcome)
    {
        var refused = NewStore().SaveDraft(A, factory, seat, "text", 0, FactoryMemoryAuthorKinds.Session, ScoutSession, Now);

        Assert.Equal(outcome, refused.Outcome);
    }

    // ---------- two writers ----------

    [Fact]
    public void TWO_WRITERS_ON_ONE_VERSION_AND_THE_SECOND_IS_REFUSED_WITH_THE_NEWEST_TEXT()
    {
        // Both read version 1 and both save - through two stores, as two replicas of the Gateway would. The second
        // is refused, and the refusal carries what is actually there so the loser can merge.
        var one = NewStore();
        var two = NewStore();
        SaveBySession(one, "start", expect: 0);

        var first = SaveBySession(one, "start, plus what the Scout learned", expect: 1, session: ScoutSession);
        var second = SaveBySession(two, "start, plus what the Sender learned", expect: 1, session: SenderSession);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(FactoryBriefOutcome.Stale, second.Outcome);
        Assert.Contains("is at version 2, not 1", second.Refusal);
        Assert.Equal(2, second.Brief!.Version);
        Assert.Equal("start, plus what the Scout learned", second.Brief.Text);
        // The winner's text stands, and the loser's was not stored anywhere.
        Assert.Equal(2, one.History(A, TheFactory, Boss).Count);
        Assert.Equal("start, plus what the Scout learned", one.Standing(A, TheFactory, Boss).Newest!.Text);
    }

    [Fact]
    public void THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION()
    {
        // The property the design rests on, tested where it lives: the hosted Gateway runs several replicas over
        // one database, so the version check inside one process is not the safeguard - the key is. Two rows
        // claiming (account, factory, seat, version) cannot both exist.
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);

        using var ctx = _h.Open().CreateContext(A);
        ctx.FactoryBriefs.Add(new FactoryBriefVersionEntity
        {
            TenantId = A.Value, Factory = TheFactory, Seat = Boss, Version = 1, Text = "a second version 1",
            State = FactoryBriefStates.Draft, AuthorKind = FactoryMemoryAuthorKinds.Session, AuthorId = SenderSession, WrittenAtUtc = Now,
        });

        Assert.ThrowsAny<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void ONLY_A_REAL_SECOND_WRITER_IS_CALLED_ONE()
    {
        // An insert can fail with no writer behind it. The head decides which refusal the caller gets: "another
        // writer took it" only when the wanted version now exists.
        var taken = new FactoryBriefVersionEntity { Version = 4 };

        Assert.True(FactoryBriefStore.WasTakenByAnotherWriter(taken, attemptedVersion: 4));
        Assert.False(FactoryBriefStore.WasTakenByAnotherWriter(new FactoryBriefVersionEntity { Version = 3 }, attemptedVersion: 4));
        Assert.False(FactoryBriefStore.WasTakenByAnotherWriter(null, attemptedVersion: 1));
    }

    [Fact]
    public void A_STALE_SAVE_expecting_no_brief_when_there_is_one_is_refused_and_carries_it()
    {
        var store = NewStore();
        SaveBySession(store, "what is there", expect: 0);

        var refused = SaveBySession(store, "written blind", expect: 0);

        Assert.Equal(FactoryBriefOutcome.Stale, refused.Outcome);
        Assert.Equal("what is there", refused.Brief!.Text);
        Assert.Single(store.History(A, TheFactory, Boss));
    }

    [Fact]
    public void A_first_save_that_expects_a_version_is_refused_and_says_to_expect_zero()
    {
        var store = NewStore();

        var refused = SaveBySession(store, "the first brief", expect: 3);

        Assert.Equal(FactoryBriefOutcome.Stale, refused.Outcome);
        Assert.Contains("expecting version 0", refused.Refusal);
        Assert.Null(refused.Brief);
    }

    // ---------- the size cap ----------

    [Fact]
    public void A_brief_larger_than_the_cap_is_REFUSED_and_one_exactly_at_the_cap_is_accepted()
    {
        var store = NewStore();

        var tooLarge = SaveBySession(store, new string('x', FactoryBriefStore.MaxBriefBytes + 1), expect: 0);
        Assert.Equal(FactoryBriefOutcome.TooLarge, tooLarge.Outcome);
        Assert.Contains($"at most {FactoryBriefStore.MaxBriefBytes}", tooLarge.Refusal);
        Assert.Empty(store.History(A, TheFactory, Boss));

        Assert.True(SaveBySession(store, new string('x', FactoryBriefStore.MaxBriefBytes), expect: 0).Ok);
    }

    [Fact]
    public void The_cap_counts_BYTES_so_text_outside_the_basic_alphabet_cannot_slip_past_it()
    {
        // Each of these characters is three bytes in UTF-8: a third of the cap in characters is the whole cap in bytes.
        var store = NewStore();
        var characters = FactoryBriefStore.MaxBriefBytes / 3 + 1;

        var refused = SaveBySession(store, new string('€', characters), expect: 0);

        Assert.Equal(FactoryBriefOutcome.TooLarge, refused.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public void An_empty_brief_is_REFUSED(string text)
    {
        var store = NewStore();

        Assert.Equal(FactoryBriefOutcome.Empty, SaveBySession(store, text, expect: 0).Outcome);
        Assert.Empty(store.History(A, TheFactory, Boss));
    }

    // ---------- draft, then published ----------

    [Fact]
    public void A_PERSON_PUBLISHES_A_VERSION_and_it_becomes_the_brief_a_run_is_given()
    {
        var store = NewStore();
        SaveBySession(store, "the first brief", expect: 0);

        var published = PublishByOwner(store, 1);

        Assert.True(published.Ok);
        Assert.Equal(FactoryBriefOutcome.Published, published.Outcome);
        var now = store.Standing(A, TheFactory, Boss).Published!;
        Assert.Equal(1, now.Version);
        Assert.Equal("the first brief", now.Text);
        Assert.Equal(FactoryBriefStates.Published, now.State);
        Assert.Equal(Now.AddMinutes(5), now.PublishedAtUtc);
        Assert.Equal(Owner, now.PublishedBy);
        // Who WROTE it is unchanged: publishing does not rewrite the version.
        Assert.Equal(FactoryMemoryAuthorKinds.Session, now.AuthorKind);
        Assert.Equal(ScoutSession, now.AuthorId);
        Assert.Equal(Now, now.WrittenAtUtc);
    }

    [Fact]
    public void A_DRAFT_SAVED_AFTER_A_PUBLISH_DOES_NOT_CHANGE_WHAT_A_RUN_IS_GIVEN()
    {
        var store = NewStore();
        SaveBySession(store, "published text", expect: 0);
        PublishByOwner(store, 1);

        SaveBySession(store, "a session's proposal", expect: 1);

        var standing = store.Standing(A, TheFactory, Boss);
        Assert.Equal(2, standing.Newest!.Version);
        Assert.Equal(FactoryBriefStates.Draft, standing.Newest.State);
        Assert.Equal(1, standing.Published!.Version);
        Assert.Equal("published text", standing.Published.Text);
    }

    [Fact]
    public void A_SESSIONS_PUBLISH_IS_REFUSED_WITH_THE_SENTENCE_AND_NOTHING_CHANGES()
    {
        // The mission document's P3: only a person publishes. A session that could publish its own draft would
        // decide what every later run of the seat is told.
        var store = NewStore();
        SaveBySession(store, "a session's draft", expect: 0);

        var refused = store.Publish(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Session, ScoutSession, Now);

        Assert.False(refused.Ok);
        Assert.Equal(FactoryBriefOutcome.OnlyAPersonPublishes, refused.Outcome);
        Assert.Equal(FactoryBriefStore.OnlyAPersonPublishes, refused.Refusal);
        Assert.StartsWith("Only a person publishes", refused.Refusal);
        var standing = store.Standing(A, TheFactory, Boss);
        Assert.Null(standing.Published);
        Assert.Equal(FactoryBriefStates.Draft, standing.Newest!.State);
    }

    [Fact]
    public void A_SESSIONS_BRING_BACK_IS_REFUSED_because_bringing_back_publishes()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, "v2", expect: 1);
        PublishByOwner(store, 2);

        var refused = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Session, ScoutSession, Now);

        Assert.Equal(FactoryBriefOutcome.OnlyAPersonPublishes, refused.Outcome);
        Assert.Equal(FactoryBriefStore.OnlyAPersonBringsBack, refused.Refusal);
        Assert.Equal(2, store.History(A, TheFactory, Boss).Count);
        Assert.Equal(2, store.Standing(A, TheFactory, Boss).Published!.Version);
    }

    [Fact]
    public void A_writer_that_is_neither_a_person_nor_a_session_is_a_defect_not_a_refusal()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.SaveDraft(A, TheFactory, Boss, "text", 0, "machine", null, Now));
        Assert.Throws<ArgumentException>(() => store.Publish(A, TheFactory, Boss, 1, "machine", null, Now));
        Assert.Throws<ArgumentException>(() => store.BringBack(A, TheFactory, Boss, 1, "machine", null, Now));
    }

    [Fact]
    public void Publishing_a_version_that_does_not_exist_is_refused()
    {
        var store = NewStore();
        Assert.Contains("has no brief on the Gateway yet", PublishByOwner(store, 1).Refusal);

        SaveBySession(store, "v1", expect: 0);
        var refused = PublishByOwner(store, 7);

        Assert.Equal(FactoryBriefOutcome.NoSuchVersion, refused.Outcome);
        Assert.Contains("version 7", refused.Refusal);
        Assert.Contains("newest version is 1", refused.Refusal);
    }

    [Fact]
    public void Publishing_the_published_version_again_is_refused_and_keeps_who_published_it_first()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);
        PublishByOwner(store, 1);

        var again = store.Publish(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, "device:somebody-else", Now.AddHours(1));

        Assert.Equal(FactoryBriefOutcome.AlreadyPublished, again.Outcome);
        var published = store.Standing(A, TheFactory, Boss).Published!;
        Assert.Equal(Owner, published.PublishedBy);
        Assert.Equal(Now.AddMinutes(5), published.PublishedAtUtc);
    }

    [Fact]
    public void PUBLISHING_NEVER_GOES_BACKWARDS_an_older_draft_is_refused_and_told_to_be_brought_back()
    {
        var store = NewStore();
        SaveBySession(store, "v1, never published", expect: 0);
        SaveBySession(store, "v2", expect: 1);
        PublishByOwner(store, 2);

        var refused = PublishByOwner(store, 1);

        Assert.Equal(FactoryBriefOutcome.OlderThanPublished, refused.Outcome);
        Assert.Contains("older than the published version 2", refused.Refusal);
        Assert.Contains("bring version 1 back", refused.Refusal);
        Assert.Equal(2, store.Standing(A, TheFactory, Boss).Published!.Version);
        Assert.Equal(FactoryBriefStates.Draft, store.History(A, TheFactory, Boss).Single(v => v.Version == 1).State);
    }

    // ---------- bring back ----------

    [Fact]
    public void BRINGING_BACK_AN_OLDER_VERSION_PUBLISHES_A_COPY_OF_IT_AS_THE_NEWEST_VERSION()
    {
        // The mission's QA flow: a draft, published; a second version, published; the first brought back.
        var store = NewStore();
        SaveBySession(store, "the first brief", expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, "the second brief", expect: 1);
        PublishByOwner(store, 2, at: Now.AddMinutes(10));

        var back = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, Owner, Now.AddMinutes(20), expectedVersion: 2);

        Assert.True(back.Ok);
        Assert.Equal(FactoryBriefOutcome.Published, back.Outcome);
        Assert.Equal(3, back.Brief!.Version);
        Assert.Equal("the first brief", back.Brief.Text);
        Assert.Equal(FactoryBriefStates.Published, back.Brief.State);
        // Authored and published by the person who brought it back, at that moment.
        Assert.Equal(FactoryMemoryAuthorKinds.Person, back.Brief.AuthorKind);
        Assert.Equal(Owner, back.Brief.AuthorId);
        Assert.Equal(Now.AddMinutes(20), back.Brief.PublishedAtUtc);

        var standing = store.Standing(A, TheFactory, Boss);
        Assert.Equal(3, standing.Published!.Version);
        Assert.Equal(3, standing.Newest!.Version);
        // NOT A REWIND: the history still shows the second version, and the first is as it was.
        var history = store.History(A, TheFactory, Boss);
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(v => v.Version).ToArray());
        Assert.Equal("the second brief", history[1].Text);
        Assert.Equal(Now.AddMinutes(10), history[1].PublishedAtUtc);
        Assert.Equal(ScoutSession, history[2].AuthorId);
    }

    [Fact]
    public void A_bring_back_can_LOSE_A_RACE_with_a_draft_saved_since_the_person_read_the_history()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, "v2", expect: 1);
        PublishByOwner(store, 2);
        // The owner reads the history at version 2; a session saves a draft before they press the button.
        SaveBySession(store, "v3, a session's draft", expect: 2);

        var refused = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, Owner, Now, expectedVersion: 2);

        Assert.Equal(FactoryBriefOutcome.Stale, refused.Outcome);
        Assert.Equal("v3, a session's draft", refused.Brief!.Text);
        Assert.Equal(3, store.History(A, TheFactory, Boss).Count);
    }

    [Fact]
    public void A_bring_back_that_states_no_version_goes_after_whatever_is_newest()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);
        PublishByOwner(store, 1);
        SaveBySession(store, "v2", expect: 1);
        PublishByOwner(store, 2);
        SaveBySession(store, "v3, a draft", expect: 2);

        var back = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, Owner, Now);

        Assert.True(back.Ok);
        Assert.Equal(4, back.Brief!.Version);
        Assert.Equal("v1", back.Brief.Text);
    }

    [Fact]
    public void Bringing_back_the_published_version_or_one_that_does_not_exist_is_refused()
    {
        var store = NewStore();
        SaveBySession(store, "v1", expect: 0);
        PublishByOwner(store, 1);

        var same = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, Owner, Now);
        Assert.Equal(FactoryBriefOutcome.AlreadyPublished, same.Outcome);
        Assert.Contains("nothing to bring back", same.Refusal);

        var missing = store.BringBack(A, TheFactory, Boss, 9, FactoryMemoryAuthorKinds.Person, Owner, Now);
        Assert.Equal(FactoryBriefOutcome.NoSuchVersion, missing.Outcome);
        Assert.Single(store.History(A, TheFactory, Boss));
    }

    [Fact]
    public void A_DRAFT_THAT_WAS_NEVER_PUBLISHED_CAN_BE_BROUGHT_BACK_which_is_how_an_older_draft_is_published()
    {
        var store = NewStore();
        SaveBySession(store, "v1, never published", expect: 0);
        SaveBySession(store, "v2", expect: 1);
        PublishByOwner(store, 2);

        var back = store.BringBack(A, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, Owner, Now);

        Assert.True(back.Ok);
        Assert.Equal(3, back.Brief!.Version);
        Assert.Equal("v1, never published", store.Standing(A, TheFactory, Boss).Published!.Text);
    }

    // ---------- another account ----------

    [Fact]
    public void ANOTHER_ACCOUNT_NEVER_SEES_A_BRIEF()
    {
        // Account B registers a factory and a seat with the very same ids. It must see none of A's versions, and its
        // own chain must start at version 1 rather than continue A's.
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, Manifest(), "the owner of A (test)", Now);
        registry.Register(B, Manifest(), "the owner of B (test)", Now);
        var store = new FactoryBriefStore(db, registry);
        SaveBySession(store, "account A's brief", expect: 0);
        PublishByOwner(store, 1);

        Assert.Null(store.Standing(B, TheFactory, Boss).Newest);
        Assert.Null(store.Standing(B, TheFactory, Boss).Published);
        Assert.Empty(store.History(B, TheFactory, Boss));
        // B cannot publish or bring back A's version either: for B it does not exist.
        Assert.Equal(FactoryBriefOutcome.NoSuchVersion,
            store.Publish(B, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, "device:b", Now).Outcome);
        Assert.Equal(FactoryBriefOutcome.NoSuchVersion,
            store.BringBack(B, TheFactory, Boss, 1, FactoryMemoryAuthorKinds.Person, "device:b", Now).Outcome);

        var bs = store.SaveDraft(B, TheFactory, Boss, "account B's brief", 0, FactoryMemoryAuthorKinds.Person, "device:b", Now);
        Assert.True(bs.Ok);
        Assert.Equal(1, bs.Brief!.Version);
        // And A still reads its own.
        Assert.Equal("account A's brief", store.Standing(A, TheFactory, Boss).Published!.Text);
        Assert.Single(store.History(A, TheFactory, Boss));
    }

    [Fact]
    public void A_factory_registered_in_one_account_is_not_registered_in_another()
    {
        var store = NewStore();

        var refused = store.SaveDraft(B, TheFactory, Boss, "a brief in the wrong account", 0, FactoryMemoryAuthorKinds.Person, "device:b", Now);

        Assert.Equal(FactoryBriefOutcome.FactoryNotRegistered, refused.Outcome);
    }
}
