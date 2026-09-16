using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// WHAT THE DISPLAY PUSH PRUNES TO, proved at the store that answers it and then through the memory that acts
/// on the answer.
///
/// WHY THIS FILE EXISTS. The display push carries ONE Director's sessions, so it cannot prune to its own rows
/// without dropping every other Director's watch on every push. It asks
/// <see cref="PushedSessionStore.KnownSessionIds"/> instead - and that method had NO test at all, so the guard
/// inside it could be tightened back to "connected only" and nothing would have gone red. A Director that has
/// gone quiet would then have its sessions pruned, which reads "I cannot see it this second" as "it is gone":
/// the same defect the account-wide prune exists to prevent, one level down.
///
/// THE ROSTER SERVES WHAT THE GATEWAY LAST KNEW, ALWAYS - an offline Director's sessions are still on it - so
/// "known" is the honest set and "connected" is not. Sessions leave when the Director's entry is FORGOTTEN,
/// which is when they have really gone.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class KnownSessionIdsBoundsTheSnoozeMemoryTests
{
    private static readonly TenantId Account = TenantId.Local;
    private static readonly DateTime Armed = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static SessionDto Session(string id, string directorId) => new()
    {
        SessionId = id,
        DirectorId = directorId,
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
    };

    /// <summary>Two Directors that have both pushed; the second one's tunnel is then closed, which is what a
    /// machine going quiet looks like to the store.</summary>
    private static PushedSessionStore TwoDirectorsOneOfThemDisconnected()
    {
        var store = new PushedSessionStore(() => Armed);
        store.RegisterConnection(Account, "dir-1", "conn-1");
        Assert.True(store.ApplySnapshot(Account, "dir-1", "conn-1", 1, new[] { Session("s1", "dir-1") }));

        store.RegisterConnection(Account, "dir-2", "conn-2");
        Assert.True(store.ApplySnapshot(Account, "dir-2", "conn-2", 1, new[] { Session("s2", "dir-2") }));
        Assert.True(store.UnregisterConnection(Account, "dir-2", "conn-2"));
        return store;
    }

    [Fact]
    public void ADisconnectedDirectorsSessions_AreStillKnown()
    {
        var store = TwoDirectorsOneOfThemDisconnected();

        var known = store.KnownSessionIds(Account);

        Assert.Equal(new[] { "s1", "s2" }, known.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // And the contrast that makes the choice visible: the connected-only view has lost s2 already.
        Assert.DoesNotContain(store.SnapshotConnected(Account), x => x.Session.SessionId == "s2");
    }

    [Fact]
    public void ADirectorThatHasNeverPushed_ContributesNothing()
    {
        // The one guard kept: a Director that has dialled in and said nothing yet has no sessions to name, so
        // it must not make the account look emptier or fuller than it is.
        var store = TwoDirectorsOneOfThemDisconnected();
        store.RegisterConnection(Account, "dir-3", "conn-3");

        Assert.Equal(new[] { "s1", "s2" }, store.KnownSessionIds(Account).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AForgottenDirectorsSessions_AreNoLongerKnown()
    {
        // The other end of it: when the Director's entry is forgotten, its sessions really have gone, and the
        // memory should be free to drop them.
        var store = TwoDirectorsOneOfThemDisconnected();

        Assert.True(store.ForgetIfDisconnected(Account, "dir-2"));

        Assert.Equal(new[] { "s1" }, store.KnownSessionIds(Account).ToArray());
    }

    [Fact]
    public void TheDisplayPushPrune_KeepsAQuietDirectorsWatch_AndDropsAForgottenOnes()
    {
        // THE STORE'S ANSWER, FED TO THE MEMORY THAT ACTS ON IT - the join neither a store test nor a fold test
        // can make on its own, and the one the display push actually performs. Restoring the connected-only
        // guard fails the middle assertion: s2's watch would be pruned by a Director that has merely gone quiet.
        using var harness = new Data.GatewayDbTestHarness();
        var db = harness.Open();
        var snoozeRegistry = new SnoozeRegistry(db, harness.LegacyPath("snoozes.json"));
        var store = TwoDirectorsOneOfThemDisconnected();
        var watch = new SnoozeExpiryReJudge();

        snoozeRegistry.Snooze("s1", Armed.AddMinutes(30), "dir-1");
        snoozeRegistry.Snooze("s2", Armed.AddMinutes(30), "dir-2");
        var holds = snoozeRegistry.HoldSnapshotFor(new[] { "s1", "s2" });
        var rows = new[] { Session("s1", "dir-1"), Session("s2", "dir-2") };

        // A fold that sees the whole account arms both watches.
        watch.Observe(Account, rows, holds, Armed.AddSeconds(1), store.KnownSessionIds(Account));
        Assert.Equal(2, watch.Watching);

        // NOW THE DISPLAY PUSH: dir-1 pushes ITS sessions, and names the account's roster from the store. dir-2
        // is disconnected but still KNOWN, so its watch survives a push it was not part of.
        watch.Observe(Account, new[] { rows[0] }, holds, Armed.AddSeconds(2), store.KnownSessionIds(Account));
        Assert.Equal(2, watch.Watching);

        // And when dir-2 is forgotten, its session really has gone, and the next push drops it.
        Assert.True(store.ForgetIfDisconnected(Account, "dir-2"));
        watch.Observe(Account, new[] { rows[0] }, holds, Armed.AddSeconds(3), store.KnownSessionIds(Account));
        Assert.Equal(1, watch.Watching);
    }
}
