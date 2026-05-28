using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

// Issue #146: the Stop-talking pill shows/hides off PlaybackSignal. It must report state
// accurately and fire only on real transitions, so the button never flickers on no-op updates.
public class PlaybackSignalTests
{
    [Fact]
    public void StartsNotPlaying()
    {
        var s = new PlaybackSignal();
        Assert.False(s.IsPlaying);
    }

    [Fact]
    public void Begin_SetsPlayingAndRaisesOnce()
    {
        var s = new PlaybackSignal();
        var events = new List<bool>();
        s.Changed += events.Add;

        s.Begin();

        Assert.True(s.IsPlaying);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void End_AfterBegin_SetsNotPlayingAndRaisesFalse()
    {
        var s = new PlaybackSignal();
        var events = new List<bool>();
        s.Changed += events.Add;

        s.Begin();
        s.End();

        Assert.False(s.IsPlaying);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void Begin_WhenAlreadyPlaying_DoesNotRaiseAgain()
    {
        var s = new PlaybackSignal();
        var events = new List<bool>();
        s.Changed += events.Add;

        s.Begin();
        s.Begin();   // clip-to-clip without an End in between must not double-fire
        s.Begin();

        Assert.True(s.IsPlaying);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void End_WhenNotPlaying_DoesNotRaise()
    {
        var s = new PlaybackSignal();
        var events = new List<bool>();
        s.Changed += events.Add;

        s.End();     // a defensive Stop() with nothing playing must stay silent
        s.End();

        Assert.False(s.IsPlaying);
        Assert.Empty(events);
    }

    [Fact]
    public void Toggling_RaisesOnEachRealTransition()
    {
        var s = new PlaybackSignal();
        var events = new List<bool>();
        s.Changed += events.Add;

        s.Begin();
        s.End();
        s.Begin();
        s.End();

        Assert.Equal(new[] { true, false, true, false }, events);
    }
}
