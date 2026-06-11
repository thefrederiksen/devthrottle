using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #291: the owner cache must be pruned when a session exits on a REACHABLE Director, so the
/// per-session WS proxy reverts to 404 (session gone) instead of #288's 503 (owner offline). These
/// unit tests pin <see cref="SessionOwnerCache.Forget"/> and
/// <see cref="SessionOwnerCache.RetainForDirector"/>, and that an offline owner's entry survives a
/// reconcile of a DIFFERENT Director (the #288 503 path must not regress).
/// </summary>
public sealed class SessionOwnerCacheTests
{
    [Fact]
    public void Forget_removes_only_the_named_session()
    {
        var cache = new SessionOwnerCache();
        cache.Remember("s1", "dir-a");
        cache.Remember("s2", "dir-a");

        cache.Forget("s1");

        Assert.Null(cache.OwnerOf("s1"));
        Assert.Equal("dir-a", cache.OwnerOf("s2"));
    }

    [Fact]
    public void Forget_is_a_noop_for_unknown_or_empty_id()
    {
        var cache = new SessionOwnerCache();
        cache.Remember("s1", "dir-a");

        cache.Forget("nope");
        cache.Forget("");

        Assert.Equal("dir-a", cache.OwnerOf("s1"));
    }

    [Fact]
    public void RetainForDirector_drops_sessions_no_longer_live_on_that_director()
    {
        var cache = new SessionOwnerCache();
        cache.Remember("live", "dir-a");
        cache.Remember("exited", "dir-a");

        // dir-a just answered and only reports "live" -> "exited" is gone.
        cache.RetainForDirector("dir-a", new[] { "live" });

        Assert.Equal("dir-a", cache.OwnerOf("live"));
        Assert.Null(cache.OwnerOf("exited"));
    }

    [Fact]
    public void RetainForDirector_drops_all_when_director_reports_no_live_sessions()
    {
        var cache = new SessionOwnerCache();
        cache.Remember("s1", "dir-a");
        cache.Remember("s2", "dir-a");

        cache.RetainForDirector("dir-a", Array.Empty<string>());

        Assert.Null(cache.OwnerOf("s1"));
        Assert.Null(cache.OwnerOf("s2"));
    }

    [Fact]
    public void RetainForDirector_never_touches_entries_owned_by_a_different_director()
    {
        // The #288 503 case: dir-b is OFFLINE (we never reconcile it). Reconciling the reachable
        // dir-a must leave dir-b's cached session intact so the WS proxy still answers 503 for it.
        var cache = new SessionOwnerCache();
        cache.Remember("offline-owned", "dir-b");
        cache.Remember("a1", "dir-a");

        cache.RetainForDirector("dir-a", Array.Empty<string>());

        Assert.Null(cache.OwnerOf("a1"));
        Assert.Equal("dir-b", cache.OwnerOf("offline-owned"));
    }

    [Fact]
    public void RetainForDirector_is_a_noop_for_empty_director_id()
    {
        var cache = new SessionOwnerCache();
        cache.Remember("s1", "dir-a");

        cache.RetainForDirector("", Array.Empty<string>());

        Assert.Equal("dir-a", cache.OwnerOf("s1"));
    }
}
