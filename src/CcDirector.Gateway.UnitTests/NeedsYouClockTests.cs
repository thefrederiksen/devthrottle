using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="NeedsYouClock"/> - the Gateway-owned per-session clock (issue #218)
/// that records when a session entered the red / NEEDS-YOU state: set on first red, held while
/// red, cleared when it leaves red, re-stamped strictly later on a re-entry.
/// </summary>
public sealed class NeedsYouClockTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    [Fact]
    public void Stamp_NotRed_ReturnsNull()
    {
        var clock = new NeedsYouClock(_time);

        var result = clock.Stamp(TenantId.Local, "s1", isRed: false);

        Assert.Null(result);
    }

    [Fact]
    public void Stamp_FirstRed_ReturnsTheClocksNow()
    {
        var clock = new NeedsYouClock(_time);

        var result = clock.Stamp(TenantId.Local, "s1", isRed: true);

        Assert.Equal(_time.GetUtcNow().UtcDateTime, result);
    }

    [Fact]
    public void Stamp_StaysRedAcrossPolls_HoldsSameValue()
    {
        var clock = new NeedsYouClock(_time);

        var first = clock.Stamp(TenantId.Local, "s1", isRed: true);
        _time.Advance(TimeSpan.FromMilliseconds(20)); // a later poll cycle
        var second = clock.Stamp(TenantId.Local, "s1", isRed: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The value must not advance while the session stays red (AC: byte-identical).
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void Stamp_LeavesRed_ClearsToNull()
    {
        var clock = new NeedsYouClock(_time);
        clock.Stamp(TenantId.Local, "s1", isRed: true);

        var afterLeaving = clock.Stamp(TenantId.Local, "s1", isRed: false);

        Assert.Null(afterLeaving);
    }

    [Fact]
    public void Stamp_ReEntersRed_SecondStampIsStrictlyLater()
    {
        var clock = new NeedsYouClock(_time);

        var first = clock.Stamp(TenantId.Local, "s1", isRed: true);
        clock.Stamp(TenantId.Local, "s1", isRed: false); // leaves red - episode ends, value goes null
        _time.Advance(TimeSpan.FromMilliseconds(20));
        var second = clock.Stamp(TenantId.Local, "s1", isRed: true); // returns to red - new episode

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.Value > first.Value,
            $"re-entry stamp {second.Value:o} should be strictly later than first {first.Value:o}");
    }

    [Fact]
    public void Stamp_TracksSessionsIndependently()
    {
        var clock = new NeedsYouClock(_time);

        var a = clock.Stamp(TenantId.Local, "a", isRed: true);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        var b = clock.Stamp(TenantId.Local, "b", isRed: true);
        // re-poll a while still red: it keeps its (earlier) value, independent of b.
        var aAgain = clock.Stamp(TenantId.Local, "a", isRed: true);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(aAgain);
        Assert.Equal(a.Value, aAgain.Value);
        Assert.True(b.Value > a.Value);
    }

    [Fact]
    public void Stamp_EmptySessionId_Throws()
    {
        var clock = new NeedsYouClock(_time);

        Assert.Throws<ArgumentException>(() => clock.Stamp(TenantId.Local, "", isRed: true));
    }

    // MTR-10 Gap C: two accounts can run sessions with the SAME id. The clock is keyed by
    // (tenant, sessionId), so one account's "left red" must never clear the other account's entry,
    // and one account's held stamp must never be reported as the other's "waiting since".
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Stamp_SameSessionIdTwoTenants_TracksIndependently()
    {
        var clock = new NeedsYouClock(_time);

        // Both accounts have a red session with the SAME id.
        var a = clock.Stamp(TenantA, "shared", isRed: true);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        var b = clock.Stamp(TenantB, "shared", isRed: true);

        Assert.NotNull(a);
        Assert.NotNull(b);
        // Distinct entries - B's later entry did not adopt or overwrite A's earlier one (a bare-sid
        // key would return A's held value here, so these would be equal).
        Assert.True(b!.Value > a!.Value,
            $"tenant B's entry {b.Value:o} should be its own, strictly later than tenant A's {a.Value:o}");

        // A holds its OWN value on a re-poll, unaffected by B sharing the id.
        var aAgain = clock.Stamp(TenantA, "shared", isRed: true);
        Assert.Equal(a.Value, aAgain);
    }

    [Fact]
    public void Stamp_OneTenantLeavesRed_DoesNotClearTheOtherTenant()
    {
        var clock = new NeedsYouClock(_time);

        var a = clock.Stamp(TenantA, "shared", isRed: true);   // A enters red
        clock.Stamp(TenantB, "shared", isRed: false);          // B leaves red on the SAME id

        // A's entry survives - B clearing the shared id cleared only B's partition. A bare-sid key
        // would have removed the one shared entry, so A's next red would re-stamp a strictly-later
        // moment instead of holding this one.
        var aAgain = clock.Stamp(TenantA, "shared", isRed: true);
        Assert.NotNull(a);
        Assert.Equal(a!.Value, aAgain);
    }
}
