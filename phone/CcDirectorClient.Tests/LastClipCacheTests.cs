using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

// Issue #148: FIFO caches only the last spoken clip (briefing or wingman answer) so Replay can
// re-play it, and drops it when the session starts working again so stale audio never replays.
public class LastClipCacheTests
{
    [Fact]
    public void StartsEmpty()
    {
        var c = new LastClipCache();
        Assert.False(c.HasClip);
        Assert.Null(c.Clip);
    }

    [Fact]
    public void Set_MakesClipAvailable()
    {
        var c = new LastClipCache();
        var clip = new byte[] { 1, 2, 3 };

        c.Set(clip);

        Assert.True(c.HasClip);
        Assert.Same(clip, c.Clip);
    }

    [Fact]
    public void Set_KeepsOnlyTheLastClip()
    {
        var c = new LastClipCache();
        var first = new byte[] { 1 };
        var second = new byte[] { 2, 2 };

        c.Set(first);
        c.Set(second);

        Assert.Same(second, c.Clip);
    }

    [Fact]
    public void Set_NullOrEmpty_ClearsRatherThanCachingNothingPlayable()
    {
        var c = new LastClipCache();
        c.Set(new byte[] { 9 });

        c.Set(System.Array.Empty<byte>());
        Assert.False(c.HasClip);
        Assert.Null(c.Clip);

        c.Set(new byte[] { 9 });
        c.Set(null);
        Assert.False(c.HasClip);
    }

    [Fact]
    public void Clear_DropsTheClip()
    {
        var c = new LastClipCache();
        c.Set(new byte[] { 1, 2, 3 });

        c.Clear();

        Assert.False(c.HasClip);
        Assert.Null(c.Clip);
    }
}
