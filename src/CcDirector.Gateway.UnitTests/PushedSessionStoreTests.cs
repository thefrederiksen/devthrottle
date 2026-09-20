using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="PushedSessionStore"/> - the correctness core of the Phase 1a push stream
/// (issue #1176). Each test drives an injected clock so the staleness and idle-clock behaviour is
/// deterministic.
/// </summary>
public sealed class PushedSessionStoreTests
{
    private DateTime _now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(20);

    private PushedSessionStore NewStore() => new(() => _now);

    private static SessionDto Session(string id, string state = "Working", DateTime? lastActivity = null) => new()
    {
        SessionId = id,
        ActivityState = state,
        LastActivityAt = lastActivity,
    };

    [Fact]
    public void TryGetFresh_AfterSnapshotFromActiveConnection_ReturnsSessions()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        // Act
        var applied = store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1"), Session("s2") });
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);

        // Assert
        Assert.True(applied);
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Count);
        Assert.Contains(fresh, s => s.SessionId == "s1");
        Assert.Contains(fresh, s => s.SessionId == "s2");
    }

    /// <summary>
    /// "Raised" is the Gateway's to say (the Fleet Manager Improvement mission, phase 1). A Director that pushes a
    /// row already stamped raised must not have that stamp survive: a client renders it verbatim, so a forged one
    /// would show the owner a raised session that is not.
    /// </summary>
    [Fact]
    public void ApplySnapshot_ARowADirectorStampedRaised_IsStoredWithoutTheStamp()
    {
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        var forged = Session("s1");
        forged.Raise = new SessionRaiseDto { Raised = true, Mark = "Raised", Offer = SessionRaiseDto.OfferLower };

        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { forged }));

        var stored = Assert.Single(store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter)!);
        Assert.Equal("s1", stored.SessionId);
        Assert.Null(stored.Raise);
    }

    [Fact]
    public void SnapshotFresh_ReturnsSessionsFromAllFreshConnections_WithDirectorId()
    {
        // Issue #1200: the auto-dismiss sweep enumerates every fresh session paired with its Director.
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.RegisterConnection(TenantId.Local, "dir-B", "conn-B");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1") });
        store.ApplySnapshot(TenantId.Local, "dir-B", "conn-B", 0, new[] { Session("s2") });

        var all = store.SnapshotFresh(TenantId.Local, _staleAfter);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.DirectorId == "dir-A" && t.Session.SessionId == "s1");
        Assert.Contains(all, t => t.DirectorId == "dir-B" && t.Session.SessionId == "s2");
    }

    [Fact]
    public void SnapshotFresh_ExcludesStaleAndDisconnectedDirectors()
    {
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1") });

        // Advance past the stale window: the cache is now stale, so the sweep must not act on it.
        _now = _now.Add(_staleAfter).AddSeconds(1);

        Assert.Empty(store.SnapshotFresh(TenantId.Local, _staleAfter));
    }

    [Fact]
    public void ApplySnapshot_FromNonActiveConnection_IsRejected()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        // Act - a stale connection tries to push
        var applied = store.ApplySnapshot(TenantId.Local, "dir-A", "conn-OLD", 5, new[] { Session("s1") });

        // Assert
        Assert.False(applied);
        Assert.Null(store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter));
    }

    [Fact]
    public void ApplyDelta_WithStaleSequence_IsRejected()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 5, new[] { Session("s1") });

        // Act - sequence 5 already applied; 5 and below must be dropped
        var stale = store.ApplyDelta(TenantId.Local, "dir-A", "conn-1", 5, Session("s2"));

        // Assert
        Assert.False(stale);
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("s1", fresh[0].SessionId);
    }

    [Fact]
    public void ApplyDelta_WithNewerSequence_UpsertsSession()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 1, new[] { Session("s1", "Working") });

        // Act
        var applied = store.ApplyDelta(TenantId.Local, "dir-A", "conn-1", 2, Session("s1", "WaitingForInput"));

        // Assert
        Assert.True(applied);
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("WaitingForInput", fresh[0].ActivityState);
    }

    [Fact]
    public void ApplySnapshot_PrunesSessionsAbsentFromTheSnapshot()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 1, new[] { Session("s1"), Session("s2"), Session("s3") });

        // Act - a later snapshot no longer contains s2
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 2, new[] { Session("s1"), Session("s3") });

        // Assert
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Count);
        Assert.DoesNotContain(fresh, s => s.SessionId == "s2");
    }

    [Fact]
    public void ApplyRemove_DropsTheSession()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 1, new[] { Session("s1"), Session("s2") });

        // Act
        var removed = store.ApplyRemove(TenantId.Local, "dir-A", "conn-1", 2, "s1");

        // Assert
        Assert.True(removed);
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("s2", fresh[0].SessionId);
    }

    [Fact]
    public void RestartedDirector_NewConnectionFirstSnapshot_IsAuthoritative()
    {
        // Arrange - a first connection pushed a high sequence, then the Director process restarts and
        // dials a brand-new connection that (correctly) starts its own sequence at 0.
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 42, new[] { Session("old") });

        // Act - the restart: new connection, first snapshot at sequence 0 must NOT be rejected.
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-2");
        var applied = store.ApplySnapshot(TenantId.Local, "dir-A", "conn-2", 0, new[] { Session("fresh") });

        // Assert
        Assert.True(applied);
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("fresh", fresh[0].SessionId);
    }

    [Fact]
    public void LateDisconnectFromOldConnection_DoesNotClearTheActiveConnection()
    {
        // Arrange - a reconnect overlap: conn-2 becomes active, THEN conn-1 (superseded) disconnects.
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-2");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-2", 0, new[] { Session("s1") });

        // Act - the late disconnect of the old connection must be ignored.
        store.UnregisterConnection(TenantId.Local, "dir-A", "conn-1");

        // Assert - the active connection and its cache survive.
        Assert.True(store.IsStreamConnected(TenantId.Local, "dir-A"));
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
    }

    [Fact]
    public void UnregisterActiveConnection_MakesTryGetFreshReturnNull()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1") });

        // Act
        store.UnregisterConnection(TenantId.Local, "dir-A", "conn-1");

        // Assert - no active connection => aggregation must fall back to pull.
        Assert.False(store.IsStreamConnected(TenantId.Local, "dir-A"));
        Assert.Null(store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter));
    }

    [Fact]
    public void TryGetFresh_WhenLastPushIsOlderThanStaleWindow_ReturnsNull()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1") });

        // Act - advance the clock past the stale window with no new push.
        _now = _now.AddSeconds(21);

        // Assert
        Assert.Null(store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter));
    }

    [Fact]
    public void TryGetFresh_ReturnsDeepCopies_MutatingResultDoesNotAffectStore()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1", "Working") });

        // Act - the aggregation stamps fields on the returned object.
        var first = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(first);
        first[0].EffectiveColor = "red";
        first[0].DirectorId = "mutated";
        first[0].DriverCapabilities.Add("Interrupt");

        // Assert - a later read is pristine.
        var second = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(second);
        Assert.Null(second[0].EffectiveColor);
        Assert.Equal("", second[0].DirectorId);
        Assert.Empty(second[0].DriverCapabilities);
    }

    [Fact]
    public void TryGetFresh_RecomputesIdleSecondsFromLastActivityAt()
    {
        // Arrange - a quiet session whose last activity was 10s before the snapshot.
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");
        var lastActivity = _now.AddSeconds(-10);
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1", "WaitingForInput", lastActivity) });

        // Act - 5s later, served from cache with no new push.
        _now = _now.AddSeconds(5);
        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);

        // Assert - idle seconds reflect now - lastActivity (15s), not the frozen value at push time (10s).
        Assert.NotNull(fresh);
        Assert.Equal(15d, fresh[0].IdleSeconds, precision: 1);
    }

    [Fact]
    public void ApplySnapshot_BeforeAnyConnectionRegistered_IsRejected()
    {
        // Arrange
        var store = NewStore();

        // Act - no RegisterConnection first.
        var applied = store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[] { Session("s1") });

        // Assert
        Assert.False(applied);
        Assert.Null(store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter));
    }

    [Fact]
    public void TryGetFresh_ForUnknownDirector_ReturnsNull()
    {
        var store = NewStore();
        Assert.Null(store.TryGetFresh(TenantId.Local, "nobody", _staleAfter));
        Assert.False(store.IsStreamConnected(TenantId.Local, "nobody"));
    }

    [Fact]
    public void TryLocate_FindsSessionAndOwningDirectorFromFreshCache()
    {
        // Arrange - two Directors each pushing a distinct session.
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.RegisterConnection(TenantId.Local, "dir-B", "conn-B");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s-A") });
        store.ApplySnapshot(TenantId.Local, "dir-B", "conn-B", 0, new[] { Session("s-B") });

        // Act
        var located = store.TryLocate(TenantId.Local, "s-B", _staleAfter);

        // Assert - the session is resolved to its owning Director with zero HTTP pull.
        Assert.NotNull(located);
        Assert.Equal("dir-B", located.Value.DirectorId);
        Assert.Equal("s-B", located.Value.Session.SessionId);
    }

    [Fact]
    public void TryLocate_ReturnsDeepCopy_MutatingResultDoesNotAffectStore()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1", "Working") });

        // Act - a caller stamps fields on the located copy.
        var located = store.TryLocate(TenantId.Local, "s1", _staleAfter);
        Assert.NotNull(located);
        located.Value.Session.EffectiveColor = "red";
        located.Value.Session.DirectorId = "mutated";

        // Assert - the cache is pristine on the next read.
        var again = store.TryLocate(TenantId.Local, "s1", _staleAfter);
        Assert.NotNull(again);
        Assert.Null(again.Value.Session.EffectiveColor);
        Assert.Equal("", again.Value.Session.DirectorId);
    }

    [Fact]
    public void TryLocate_WhenCacheStale_ReturnsNull()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1") });

        // Act - advance past the stale window with no re-push.
        _now = _now.AddSeconds(21);

        // Assert - a stale cache cannot locate (matching TryGetFresh's staleness rule).
        Assert.Null(store.TryLocate(TenantId.Local, "s1", _staleAfter));
    }

    [Fact]
    public void TryLocate_AfterUnregister_ReturnsNull()
    {
        // Arrange
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1") });

        // Act - the stream disconnects.
        store.UnregisterConnection(TenantId.Local, "dir-A", "conn-A");

        // Assert - no active connection => no location from the cache.
        Assert.Null(store.TryLocate(TenantId.Local, "s1", _staleAfter));
    }

    [Fact]
    public void TryLocate_ForUnknownSession_ReturnsNull()
    {
        var store = NewStore();
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-A");
        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-A", 0, new[] { Session("s1") });

        Assert.Null(store.TryLocate(TenantId.Local, "nobody", _staleAfter));
        Assert.Null(store.TryLocate(TenantId.Local, "", _staleAfter));
    }
}
