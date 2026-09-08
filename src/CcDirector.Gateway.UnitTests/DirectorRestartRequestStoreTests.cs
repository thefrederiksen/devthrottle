using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The pending-request store - issue #2725. The rules that keep two approvals from racing one drain and
/// keep a stale approval from restarting a Director nobody currently wants restarted.
/// </summary>
public sealed class DirectorRestartRequestStoreTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly TenantId Other = new("other-tenant");

    private static DirectorRestartRequestDto Request(string machine = "SOREN_NORTH") => new()
    {
        Machine = machine,
        DirectorId = "d-1",
        Reason = "install the launcher update",
    };

    private static (DirectorRestartRequestStore Store, Func<DateTime> Now, Action<TimeSpan> Advance) Clocked()
    {
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var store = new DirectorRestartRequestStore { Clock = () => now };
        return (store, () => now, by => now += by);
    }

    [Fact]
    public void A_second_request_while_one_is_pending_for_the_same_machine_is_refused_with_the_first()
    {
        var (store, _, _) = Clocked();
        Assert.True(store.TryCreate(Tenant, Request(), out _));

        var refused = store.TryCreate(Tenant, Request(), out var pending);

        Assert.False(refused);
        Assert.NotNull(pending);
        Assert.Equal(DirectorRestartRequestState.Pending, pending!.State);
    }

    [Fact]
    public void The_machine_name_is_matched_without_regard_to_case()
    {
        var (store, _, _) = Clocked();
        Assert.True(store.TryCreate(Tenant, Request("SOREN_NORTH"), out _));
        Assert.False(store.TryCreate(Tenant, Request("soren_north"), out _));
    }

    [Fact]
    public void An_accepted_and_running_request_blocks_a_new_one_for_that_machine_too()
    {
        var (store, _, _) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;
        Assert.Equal(RestartAcceptOutcome.Accepted, store.Accept(Tenant, id, out _));

        Assert.False(store.TryCreate(Tenant, Request(), out var open));
        Assert.Equal(DirectorRestartRequestState.Accepted, open!.State);
    }

    [Fact]
    public void An_accepted_request_that_never_reports_its_end_expires_after_three_hours_with_its_last_word()
    {
        var (store, _, advance) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;
        store.Accept(Tenant, id, out _);
        store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Accepted, Progress = "asking the launcher" }, out _);

        advance(DirectorRestartRequestStore.RunningExpiry - TimeSpan.FromMinutes(1));
        Assert.Equal(DirectorRestartRequestState.Accepted, store.Get(Tenant, id)!.State);

        advance(TimeSpan.FromMinutes(2));
        var expired = store.Get(Tenant, id)!;
        Assert.Equal(DirectorRestartRequestState.Expired, expired.State);
        Assert.Contains("asking the launcher", expired.StateReason);
        Assert.Contains("do not assume the restart happened", expired.StateReason);

        // And the machine is free for a new request again.
        Assert.True(store.TryCreate(Tenant, Request(), out _));
    }

    [Fact]
    public void A_different_machine_may_have_its_own_pending_request()
    {
        var (store, _, _) = Clocked();
        Assert.True(store.TryCreate(Tenant, Request("A"), out _));
        Assert.True(store.TryCreate(Tenant, Request("B"), out _));
    }

    [Fact]
    public void Another_tenant_does_not_see_or_block_on_this_tenants_request()
    {
        var (store, _, _) = Clocked();
        Assert.True(store.TryCreate(Tenant, Request(), out _));
        var mine = store.List(Tenant).Single();

        Assert.True(store.TryCreate(Other, Request(), out _));
        Assert.Null(store.Get(Other, mine.Id));
        Assert.Equal(RestartAcceptOutcome.NotFound, store.Accept(Other, mine.Id, out _));
    }

    [Fact]
    public void A_pending_request_can_be_accepted_once_and_only_once()
    {
        var (store, _, _) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;

        Assert.Equal(RestartAcceptOutcome.Accepted, store.Accept(Tenant, id, out var first));
        Assert.Equal(DirectorRestartRequestState.Accepted, first!.State);
        Assert.NotNull(first.AcceptedAtUtc);
        Assert.False(first.CanAccept);

        // The second accept - the racing tap - is NotPending, never a second dispatch.
        Assert.Equal(RestartAcceptOutcome.NotPending, store.Accept(Tenant, id, out var second));
        Assert.Equal(DirectorRestartRequestState.Accepted, second!.State);
    }

    [Fact]
    public void An_approval_older_than_thirty_minutes_is_refused_as_expired()
    {
        var (store, _, advance) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var created = store.List(Tenant).Single();
        Assert.True(created.CanAccept);

        advance(DirectorRestartRequestStore.Expiry);

        Assert.Equal(RestartAcceptOutcome.Expired, store.Accept(Tenant, created.Id, out var expired));
        Assert.Equal(DirectorRestartRequestState.Expired, expired!.State);
        Assert.False(expired.CanAccept);
        Assert.Contains("30 minutes", expired.StateReason);
    }

    [Fact]
    public void One_second_before_expiry_is_still_acceptable_and_the_read_says_so()
    {
        var (store, _, advance) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;

        advance(DirectorRestartRequestStore.Expiry - TimeSpan.FromSeconds(1));

        Assert.True(store.Get(Tenant, id)!.CanAccept);
        Assert.Equal(RestartAcceptOutcome.Accepted, store.Accept(Tenant, id, out _));
    }

    [Fact]
    public void An_expired_request_no_longer_blocks_a_new_one_for_that_machine()
    {
        var (store, _, advance) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        advance(DirectorRestartRequestStore.Expiry);

        Assert.True(store.TryCreate(Tenant, Request(), out _));
        Assert.Equal(2, store.List(Tenant).Count);
    }

    [Fact]
    public void Declining_closes_a_pending_request_and_nothing_else()
    {
        var (store, _, _) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;

        Assert.True(store.Decline(Tenant, id, "not now", out var declined));
        Assert.Equal(DirectorRestartRequestState.Declined, declined!.State);
        Assert.Equal("not now", declined.StateReason);

        Assert.False(store.Decline(Tenant, id, "again", out _));
        Assert.Equal(RestartAcceptOutcome.NotPending, store.Accept(Tenant, id, out _));
    }

    [Fact]
    public void A_Director_report_lands_only_on_an_accepted_request_and_only_in_its_own_three_states()
    {
        var (store, _, _) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;

        // Not yet accepted: nowhere to land.
        Assert.False(store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Accepted, Progress = "draining" }, out _));

        store.Accept(Tenant, id, out _);
        Assert.True(store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Accepted, Progress = "draining", WorkspaceId = "ws-1" }, out var running));
        Assert.Equal(DirectorRestartRequestState.Accepted, running!.State);
        Assert.Equal("draining", running.Progress);
        Assert.Equal("ws-1", running.WorkspaceId);

        // The owner's states are not a Director's to set.
        Assert.Throws<ArgumentException>(() => store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Declined, Progress = "x" }, out _));
        Assert.Throws<ArgumentException>(() => store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Pending, Progress = "x" }, out _));

        Assert.True(store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Abandoned, Progress = "a seat would not stop" }, out var closed));
        Assert.Equal(DirectorRestartRequestState.Abandoned, closed!.State);
        Assert.Equal("a seat would not stop", closed.StateReason);
        Assert.NotNull(closed.ClosedAtUtc);

        // Closed is closed.
        Assert.False(store.Report(Tenant, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Completed, Progress = "late" }, out _));
    }

    [Fact]
    public void A_closed_request_is_kept_for_a_day_then_swept()
    {
        var (store, _, advance) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var id = store.List(Tenant).Single().Id;
        store.Decline(Tenant, id, "no", out _);

        advance(DirectorRestartRequestStore.Retention - TimeSpan.FromMinutes(1));
        Assert.NotNull(store.Get(Tenant, id));

        advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.Get(Tenant, id));
    }

    [Fact]
    public void What_a_reader_gets_is_a_copy_and_editing_it_changes_nothing()
    {
        var (store, _, _) = Clocked();
        store.TryCreate(Tenant, Request(), out _);
        var read = store.List(Tenant).Single();
        read.State = DirectorRestartRequestState.Accepted;
        read.Reason = "tampered";

        var again = store.Get(Tenant, read.Id)!;
        Assert.Equal(DirectorRestartRequestState.Pending, again.State);
        Assert.Equal("install the launcher update", again.Reason);
    }
}
