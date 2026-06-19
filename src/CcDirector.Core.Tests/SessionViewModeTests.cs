using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for the mobile view-mode change notification added for the in-voice-mode ear icon
/// (issue #554). The desktop session view-model listens to <see cref="Session.OnViewModeChanged"/>
/// so the ear indicator appears and clears live as a phone enters and leaves the Voice tab, and
/// the derived <see cref="Session.VoiceMode"/> / <see cref="Session.MobileMode"/> flags must track
/// <see cref="Session.ViewMode"/> exactly.
/// </summary>
public sealed class SessionViewModeTests
{
    private static Session NewSession() => new Session(
        Guid.NewGuid(),
        repoPath: @"C:\test\repo",
        workingDirectory: @"C:\test\repo",
        claudeArgs: null,
        backend: new StubSessionBackend(),
        claudeSessionId: "claude-test",
        activityState: ActivityState.Idle,
        createdAt: DateTimeOffset.UtcNow,
        customName: null,
        customColor: null);

    [Fact]
    public void VoiceMode_IsTrue_OnlyWhenViewModeIsVoice()
    {
        using var s = NewSession();

        Assert.False(s.VoiceMode); // default Off

        s.ViewMode = MobileViewMode.Text;
        Assert.False(s.VoiceMode);

        s.ViewMode = MobileViewMode.Voice;
        Assert.True(s.VoiceMode);

        s.ViewMode = MobileViewMode.Off;
        Assert.False(s.VoiceMode);
    }

    [Fact]
    public void SettingViewMode_RaisesOnViewModeChanged_WithOldAndNew()
    {
        using var s = NewSession();
        var events = new List<(MobileViewMode Old, MobileViewMode New)>();
        s.OnViewModeChanged += (oldMode, newMode) => events.Add((oldMode, newMode));

        s.ViewMode = MobileViewMode.Voice;
        s.ViewMode = MobileViewMode.Off;

        Assert.Equal(2, events.Count);
        Assert.Equal((MobileViewMode.Off, MobileViewMode.Voice), events[0]);
        Assert.Equal((MobileViewMode.Voice, MobileViewMode.Off), events[1]);
    }

    [Fact]
    public void SettingViewMode_ToSameValue_DoesNotRaise()
    {
        using var s = NewSession();
        s.ViewMode = MobileViewMode.Voice;

        var raised = 0;
        s.OnViewModeChanged += (_, _) => raised++;
        s.ViewMode = MobileViewMode.Voice; // unchanged

        Assert.Equal(0, raised);
    }
}
