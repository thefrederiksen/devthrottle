using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>A Mac copy that could not move into /Applications is told about once per place, not at every start (issue #4427).</summary>
public sealed class MacRelocationNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mac-notice-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ShouldShow_FirstTimeForAPlace_IsTrue_ThenQuietOnceShown()
    {
        Assert.True(MacRelocationNotice.ShouldShow("/Users/someone/Applications/Director.app", _dir));
        MacRelocationNotice.RecordShown("/Users/someone/Applications/Director.app", _dir);
        Assert.False(MacRelocationNotice.ShouldShow("/Users/someone/Applications/Director.app", _dir));
    }

    [Fact]
    public void ShouldShow_ANoticeThatWasNeverShown_IsTriedAgain()
    {
        // Review of #4427: recorded only after it appears, so a notice that failed to show is not lost.
        Assert.True(MacRelocationNotice.ShouldShow("/Users/someone/Applications/Director.app", _dir));
        Assert.True(MacRelocationNotice.ShouldShow("/Users/someone/Applications/Director.app", _dir));
    }

    [Fact]
    public void ShouldShow_ADifferentPlace_IsToldAgain()
    {
        Assert.True(MacRelocationNotice.ShouldShow("/Users/someone/Applications/Director.app", _dir));
        MacRelocationNotice.RecordShown("/Users/someone/Applications/Director.app", _dir);
        Assert.True(MacRelocationNotice.ShouldShow("/Users/someone/Desktop/Director.app", _dir));
    }
}
