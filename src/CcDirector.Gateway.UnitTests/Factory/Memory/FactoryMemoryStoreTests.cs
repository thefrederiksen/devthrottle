using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Memory;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// A FACTORY'S MEMORY: the store contract (Factory Memory mission, phase 2; the mission document's section 7,
/// memory). Runs over the real Entity Framework store on a throwaway SQLite file, exactly as the Gateway runs it
/// locally.
///
/// The cases that matter most are the two-writer pair -
/// <see cref="TWO_WRITERS_ON_ONE_NOTE_AND_THE_SECOND_IS_REFUSED_WITH_THE_CURRENT_TEXT"/> and
/// <see cref="THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION"/>. Everything else here could be
/// satisfied by a store that simply overwrote, and an overwriting store loses one session's lesson while looking
/// perfectly healthy - which is the LESSONS.md failure this whole mission exists to end.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryMemoryStoreTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private const string Other = "invoice-factory";
    private const string Scout = "5e551011-0000-0000-0000-000000000001";
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private FactoryMemoryStore NewStore() => new(_h.Open());
    private static TenantId Tenant => TenantId.Local;

    private static FactoryMemoryWrite SetBySession(FactoryMemoryStore store, string name, string text, int? expect, DateTime? at = null)
        => store.Set(Tenant, TheFactory, name, text, expect, FactoryMemoryAuthorKinds.Session, Scout, at ?? Now);

    // ---------- writing and reading ----------

    [Fact]
    public void A_note_written_by_one_session_is_read_back_by_the_next()
    {
        // The whole point of the mission, in one test: a lesson survives the session that learned it.
        var store = NewStore();
        var write = SetBySession(store, "domains", "this registry needs the owner's telephone number", expect: 0);

        Assert.True(write.Ok);
        Assert.Equal(1, write.Note!.Version);

        var read = NewStore().Get(Tenant, TheFactory, "domains");
        Assert.Equal("this registry needs the owner's telephone number", read!.Text);
        Assert.Equal(FactoryMemoryAuthorKinds.Session, read.AuthorKind);
        Assert.Equal(Scout, read.AuthorId);
    }

    [Fact]
    public void A_second_write_adds_a_VERSION_and_never_edits_the_first()
    {
        var store = NewStore();
        SetBySession(store, "domains", "first", expect: 0);
        var second = SetBySession(store, "domains", "first, and also second", expect: 1);

        Assert.True(second.Ok);
        Assert.Equal(2, second.Note!.Version);

        var history = store.History(Tenant, TheFactory, "domains");
        Assert.Equal(2, history.Count);
        Assert.Equal("first, and also second", history[0].Text);
        Assert.Equal("first", history[1].Text);
    }

    [Fact]
    public void One_factorys_memory_is_not_anothers()
    {
        var store = NewStore();
        SetBySession(store, "domains", "ours", expect: 0);

        Assert.Empty(store.List(Tenant, Other));
        Assert.Null(store.Get(Tenant, Other, "domains"));
    }

    [Fact]
    public void The_listing_is_the_current_version_of_each_name()
    {
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);
        SetBySession(store, "domains", "v2", expect: 1);
        SetBySession(store, "deliverability", "authentication fails without it", expect: 0);

        var list = store.List(Tenant, TheFactory);
        Assert.Equal(new[] { "deliverability", "domains" }, list.Select(n => n.Name).ToArray());
        Assert.Equal("v2", list.Single(n => n.Name == "domains").Text);
    }

    // ---------- two writers ----------

    [Fact]
    public void TWO_WRITERS_ON_ONE_NOTE_AND_THE_SECOND_IS_REFUSED_WITH_THE_CURRENT_TEXT()
    {
        // Both read version 1 and both write. The second is refused, and the refusal carries what is actually
        // there so the loser can merge instead of reading, racing and losing again.
        var store = NewStore();
        SetBySession(store, "deliverability", "start", expect: 0);

        var first = SetBySession(store, "deliverability", "start, plus what the Scout learned", expect: 1);
        var second = SetBySession(store, "deliverability", "start, plus what the Sender learned", expect: 1);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(FactoryMemoryOutcome.Stale, second.Outcome);
        Assert.Contains("version 2", second.Refusal);
        Assert.Equal("start, plus what the Scout learned", second.Note!.Text);

        // And nothing was lost: the winner's text stands.
        Assert.Equal("start, plus what the Scout learned", store.Get(Tenant, TheFactory, "deliverability")!.Text);
    }

    [Fact]
    public void THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION()
    {
        // The property the design rests on, tested where it lives rather than trusted: the hosted Gateway runs
        // several replicas over one database, so the version check in one process is not the safeguard - the key
        // is. Two rows claiming (account, factory, name, version) cannot both exist.
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);

        using var ctx = _h.Open().CreateContext(Tenant);
        ctx.FactoryMemoryNotes.Add(new FactoryMemoryNoteEntity
        {
            TenantId = Tenant.Value, Factory = TheFactory, Name = "domains", Version = 1,
            Text = "a second version 1", AuthorKind = FactoryMemoryAuthorKinds.Session, AuthorId = Scout, WrittenAtUtc = Now,
        });

        Assert.ThrowsAny<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void Writing_a_note_that_exists_while_expecting_version_zero_is_refused_and_says_so()
    {
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);

        var again = SetBySession(store, "domains", "v1 again", expect: 0);

        Assert.Equal(FactoryMemoryOutcome.Stale, again.Outcome);
        Assert.Equal("v1", again.Note!.Text);
    }

    [Fact]
    public void Writing_a_NEW_note_while_expecting_a_version_is_refused_and_says_to_expect_zero()
    {
        var store = NewStore();

        var write = SetBySession(store, "domains", "text", expect: 3);

        Assert.Equal(FactoryMemoryOutcome.Stale, write.Outcome);
        Assert.Contains("expecting version 0", write.Refusal);
        Assert.Null(write.Note);
    }

    // ---------- the caps ----------

    [Fact]
    public void A_note_larger_than_the_cap_is_REFUSED()
    {
        var store = NewStore();
        var tooBig = new string('x', FactoryMemoryStore.MaxNoteBytes + 1);

        var write = SetBySession(store, "domains", tooBig, expect: 0);

        Assert.Equal(FactoryMemoryOutcome.NoteTooLarge, write.Outcome);
        Assert.Contains($"{FactoryMemoryStore.MaxNoteBytes}", write.Refusal);
        Assert.Null(store.Get(Tenant, TheFactory, "domains"));
    }

    [Fact]
    public void A_note_exactly_at_the_cap_is_accepted()
    {
        // The boundary in the accepting direction, so the cap is a limit rather than an approximation.
        var store = NewStore();
        var exact = new string('x', FactoryMemoryStore.MaxNoteBytes);

        Assert.True(SetBySession(store, "domains", exact, expect: 0).Ok);
    }

    [Fact]
    public void The_hundred_and_first_note_is_REFUSED_and_the_refusal_says_what_to_do()
    {
        var store = NewStore();
        for (var i = 0; i < FactoryMemoryStore.MaxNotes; i++)
            Assert.True(SetBySession(store, $"note-{i:D3}", "x", expect: 0).Ok);

        var over = SetBySession(store, "one-too-many", "x", expect: 0);

        Assert.Equal(FactoryMemoryOutcome.TooManyNotes, over.Outcome);
        Assert.Contains("Delete one it no longer needs", over.Refusal);
    }

    [Fact]
    public void Rewriting_an_EXISTING_note_at_the_note_limit_is_still_allowed()
    {
        // The cap counts names, not writes. A factory at its limit must still be able to keep learning, or the
        // memory silently freezes at the worst moment.
        var store = NewStore();
        for (var i = 0; i < FactoryMemoryStore.MaxNotes; i++)
            SetBySession(store, $"note-{i:D3}", "x", expect: 0);

        Assert.True(SetBySession(store, "note-000", "x, and something new", expect: 1).Ok);
    }

    [Fact]
    public void A_write_that_would_take_the_FACTORY_over_its_total_is_REFUSED()
    {
        var store = NewStore();
        var big = new string('x', FactoryMemoryStore.MaxNoteBytes);
        var notes = FactoryMemoryStore.MaxFactoryBytes / FactoryMemoryStore.MaxNoteBytes;
        for (var i = 0; i < notes; i++)
            Assert.True(SetBySession(store, $"big-{i:D2}", big, expect: 0).Ok);

        var over = SetBySession(store, "one-more", "x", expect: 0);

        Assert.Equal(FactoryMemoryOutcome.FactoryTooLarge, over.Outcome);
        Assert.Contains($"{FactoryMemoryStore.MaxFactoryBytes}", over.Refusal);
    }

    // ---------- delete, get of a deleted note, bringing it back, restore ----------

    [Fact]
    public void A_DELETE_IS_A_VERSION_so_the_note_is_gone_from_the_listing_but_not_from_the_history()
    {
        // The owner's decision of 26 September, and the condition he attached to it: a factory session may delete,
        // because a delete can be undone.
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);

        var deleted = store.Delete(Tenant, TheFactory, "domains", expectedVersion: 1,
            FactoryMemoryAuthorKinds.Session, Scout, Now);

        Assert.True(deleted.Ok);
        Assert.Equal(2, deleted.Note!.Version);
        Assert.Empty(store.List(Tenant, TheFactory));
        Assert.Equal(2, store.History(Tenant, TheFactory, "domains").Count);
    }

    [Fact]
    public void GETTING_a_deleted_note_says_it_was_deleted_rather_than_that_it_never_existed()
    {
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);
        store.Delete(Tenant, TheFactory, "domains", 1, FactoryMemoryAuthorKinds.Session, Scout, Now);

        var head = store.Get(Tenant, TheFactory, "domains");

        Assert.NotNull(head);
        Assert.True(head!.Deleted);
        Assert.Equal(2, head.Version);
        Assert.Equal(Scout, head.AuthorId);
    }

    [Fact]
    public void A_SET_ON_A_DELETED_NAME_BRINGS_THE_NOTE_BACK_and_continues_the_same_chain()
    {
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);
        store.Delete(Tenant, TheFactory, "domains", 1, FactoryMemoryAuthorKinds.Session, Scout, Now);

        var back = SetBySession(store, "domains", "learned again", expect: 2);

        Assert.True(back.Ok);
        Assert.Equal(3, back.Note!.Version);
        Assert.False(back.Note.Deleted);
        Assert.Equal("learned again", Assert.Single(store.List(Tenant, TheFactory)).Text);
    }

    [Fact]
    public void Deleting_a_note_that_is_not_there_is_refused_and_deleting_twice_says_when_it_went()
    {
        var store = NewStore();
        var none = store.Delete(Tenant, TheFactory, "never", 0, FactoryMemoryAuthorKinds.Session, Scout, Now);
        Assert.Equal(FactoryMemoryOutcome.NoSuchNote, none.Outcome);

        SetBySession(store, "domains", "v1", expect: 0);
        store.Delete(Tenant, TheFactory, "domains", 1, FactoryMemoryAuthorKinds.Session, Scout, Now);
        var twice = store.Delete(Tenant, TheFactory, "domains", 2, FactoryMemoryAuthorKinds.Session, Scout, Now);

        Assert.Equal(FactoryMemoryOutcome.AlreadyDeleted, twice.Outcome);
        Assert.Contains("version 2", twice.Refusal);
    }

    [Fact]
    public void A_PERSONS_RESTORE_PUTS_AN_OLD_VERSION_BACK_AS_A_NEW_ONE_AUTHORED_BY_THEM()
    {
        // A restore is not a rewind: the history still shows the delete, and the restore is its own version with
        // the person as its author. That is what makes "who changed the memory" answerable afterwards.
        var store = NewStore();
        SetBySession(store, "domains", "the good text", expect: 0);
        store.Delete(Tenant, TheFactory, "domains", 1, FactoryMemoryAuthorKinds.Session, Scout, Now);

        var restored = store.Restore(Tenant, TheFactory, "domains", version: 1,
            FactoryMemoryAuthorKinds.Person, "soren", Now);

        Assert.True(restored.Ok);
        Assert.Equal(3, restored.Note!.Version);
        Assert.Equal("the good text", restored.Note.Text);
        Assert.Equal(FactoryMemoryAuthorKinds.Person, restored.Note.AuthorKind);
        Assert.Equal("soren", restored.Note.AuthorId);
        Assert.Equal(3, store.History(Tenant, TheFactory, "domains").Count);
    }

    [Fact]
    public void Restoring_a_version_that_is_not_kept_is_refused_and_so_is_restoring_the_delete_itself()
    {
        var store = NewStore();
        SetBySession(store, "domains", "v1", expect: 0);
        store.Delete(Tenant, TheFactory, "domains", 1, FactoryMemoryAuthorKinds.Session, Scout, Now);

        Assert.Equal(FactoryMemoryOutcome.NoSuchVersion,
            store.Restore(Tenant, TheFactory, "domains", 99, FactoryMemoryAuthorKinds.Person, "soren", Now).Outcome);

        var theDelete = store.Restore(Tenant, TheFactory, "domains", 2, FactoryMemoryAuthorKinds.Person, "soren", Now);
        Assert.Equal(FactoryMemoryOutcome.NoSuchVersion, theDelete.Outcome);
        Assert.Contains("restore the version before it", theDelete.Refusal);
    }

    // ---------- pruning ----------

    [Fact]
    public void PRUNING_KEEPS_THE_LAST_FIFTY_VERSIONS_however_old_they_are()
    {
        // Fifty-nine versions written long ago, then one written NOW - which is how this arises in life, and it
        // matters to the test: a write prunes against its own clock, so the newest write is what decides whether
        // the older versions are inside the ninety days. They are not, so only the count rule keeps any of them.
        var store = NewStore();
        var longAgo = Now.AddDays(-FactoryMemoryStore.KeepDays - 10);
        for (var v = 0; v < 59; v++)
            Assert.True(SetBySession(store, "domains", $"v{v + 1}", expect: v, at: longAgo).Ok);
        Assert.True(SetBySession(store, "domains", "v60", expect: 59).Ok);

        var history = store.History(Tenant, TheFactory, "domains");
        Assert.Equal(FactoryMemoryStore.KeepVersions, history.Count);
        Assert.Equal(60, history[0].Version);
        Assert.Equal(11, history[^1].Version);
    }

    [Fact]
    public void PRUNING_KEEPS_NINETY_DAYS_even_when_that_is_MORE_than_fifty_versions()
    {
        // "Whichever keeps more" - the rule the review asked for in those words. Sixty recent versions are all
        // kept, because dropping to fifty would throw away something written this week.
        var store = NewStore();
        for (var v = 0; v < 60; v++)
            Assert.True(SetBySession(store, "domains", $"v{v + 1}", expect: v, at: Now.AddDays(-1)).Ok);

        Assert.Equal(60, store.History(Tenant, TheFactory, "domains").Count);
    }

    [Fact]
    public void Pruning_never_drops_the_current_version()
    {
        var store = NewStore();
        var longAgo = Now.AddDays(-FactoryMemoryStore.KeepDays - 10);
        for (var v = 0; v < 59; v++)
            SetBySession(store, "domains", $"v{v + 1}", expect: v, at: longAgo);
        SetBySession(store, "domains", "v60", expect: 59);

        Assert.Equal("v60", store.Get(Tenant, TheFactory, "domains")!.Text);
    }

    // ---------- names ----------

    [Fact]
    public void A_note_needs_a_name_and_a_very_long_one_is_refused()
    {
        var store = NewStore();
        Assert.Equal(FactoryMemoryOutcome.BadName, SetBySession(store, "   ", "x", expect: 0).Outcome);
        Assert.Equal(FactoryMemoryOutcome.BadName,
            SetBySession(store, new string('n', FactoryMemoryStore.MaxNameLength + 1), "x", expect: 0).Outcome);
    }

    [Fact]
    public void A_names_surrounding_space_is_trimmed_so_one_idea_does_not_become_two_notes()
    {
        var store = NewStore();
        Assert.True(SetBySession(store, "  domains  ", "v1", expect: 0).Ok);

        Assert.Equal("domains", Assert.Single(store.List(Tenant, TheFactory)).Name);
    }
}
