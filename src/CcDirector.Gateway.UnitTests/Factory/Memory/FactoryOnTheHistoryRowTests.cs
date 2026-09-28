using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// THE HISTORY ROW IS WHERE MEMBERSHIP LIVES, AND IT IS WRITTEN ONCE (Factory Memory mission, phase 1;
/// review finding 4).
///
/// Two properties are proved here, over the real store on a throwaway SQLite file:
///
///  - The row, not the in-memory roster, is what a membership check reads - so it survives a Gateway restart,
///    which <see cref="A_factory_survives_a_Gateway_restart"/> shows by reopening the database.
///  - The column is WRITE-ONCE, so a push from a Director that predates the field cannot blank it and a later
///    push cannot move a session to another factory. A field that decides who may write a factory's memory
///    must not depend on which push arrived last.
///
/// The third state - no row at all - is the spawn door's business and is proved in
/// <see cref="FactoryMembershipAtTheSpawnDoorTests"/>; here it shows up as
/// <see cref="A_session_with_no_row_reads_as_NOT_KNOWN_not_as_in_no_factory"/>.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryOnTheHistoryRowTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private const string AnotherFactory = "invoice-factory";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionHistoryStore NewStore() => new(_harness.Open());

    private static SessionDto Session(string id, string? factory) => new()
    {
        SessionId = id,
        Name = "Scout",
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        LastActivityAt = DateTime.UtcNow,
        ActivityState = "Working",
        Status = "Running",
        Factory = factory,
    };

    [Fact]
    public void A_factory_arrives_with_the_first_push_and_is_read_back()
    {
        var store = NewStore();
        store.UpsertLive("director-1", Session("s1", TheFactory), DateTime.UtcNow);

        var lookup = store.FactoryOf("s1");
        Assert.True(lookup.IsKnown);
        Assert.True(lookup.IsInAFactory);
        Assert.Equal(TheFactory, lookup.Factory);
    }

    [Fact]
    public void A_session_with_no_row_reads_as_NOT_KNOWN_not_as_in_no_factory()
    {
        var store = NewStore();
        var lookup = store.FactoryOf("never-pushed");
        Assert.False(lookup.IsKnown);
        Assert.False(lookup.IsInAFactory);
        Assert.Null(lookup.Factory);
    }

    [Fact]
    public void A_row_with_no_factory_is_a_SETTLED_answer()
    {
        var store = NewStore();
        store.UpsertLive("director-1", Session("s1", factory: null), DateTime.UtcNow);

        var lookup = store.FactoryOf("s1");
        Assert.True(lookup.IsKnown);
        Assert.False(lookup.IsInAFactory);
    }

    [Fact]
    public void A_LATER_PUSH_THAT_LOST_THE_FIELD_DOES_NOT_BLANK_IT()
    {
        // An older Director taking over mid-upgrade pushes no factory. If that blanked the column, a live
        // factory agent would fall out of its factory because of which build happened to report it last.
        var store = NewStore();
        store.UpsertLive("director-1", Session("s1", TheFactory), DateTime.UtcNow);
        store.UpsertLive("director-1", Session("s1", factory: null), DateTime.UtcNow.AddSeconds(30));

        Assert.Equal(TheFactory, store.FactoryOf("s1").Factory);
    }

    [Fact]
    public void A_LATER_PUSH_CANNOT_MOVE_A_SESSION_TO_ANOTHER_FACTORY()
    {
        // Write-once, like the parent link beside it. Membership is a birth fact; a session that could be moved
        // later could be moved into a factory whose memory it was never meant to read.
        var store = NewStore();
        store.UpsertLive("director-1", Session("s1", TheFactory), DateTime.UtcNow);
        store.UpsertLive("director-1", Session("s1", AnotherFactory), DateTime.UtcNow.AddSeconds(30));

        Assert.Equal(TheFactory, store.FactoryOf("s1").Factory);
    }

    [Fact]
    public void A_ROW_WRITTEN_WITHOUT_A_FACTORY_IS_STILL_FILLED_BY_A_LATER_PUSH_THAT_HAS_ONE()
    {
        // The other side of write-once, and the reason the guard is on the STORED value being empty rather than
        // on first sight: a row an older Director created before this field existed must still be fillable the
        // moment a current Director reports the same session.
        var store = NewStore();
        store.UpsertLive("director-1", Session("s1", factory: null), DateTime.UtcNow);
        store.UpsertLive("director-1", Session("s1", TheFactory), DateTime.UtcNow.AddSeconds(30));

        Assert.Equal(TheFactory, store.FactoryOf("s1").Factory);
    }

    [Fact]
    public void A_factory_survives_a_Gateway_restart()
    {
        // The whole reason membership is read from here and not from the pushed roster: the roster is empty for
        // a moment after a restart, and a check reading it would refuse a factory session's writes in that
        // window and copy an empty factory onto anything it spawned.
        NewStore().UpsertLive("director-1", Session("s1", TheFactory), DateTime.UtcNow);

        var afterRestart = NewStore();
        Assert.Equal(TheFactory, afterRestart.FactoryOf("s1").Factory);
    }

    [Fact]
    public void A_blank_session_id_reads_as_NOT_KNOWN_and_asks_the_database_nothing()
    {
        var store = NewStore();
        Assert.False(store.FactoryOf("").IsKnown);
        Assert.False(store.FactoryOf("   ").IsKnown);
    }
}
