using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// The pin: a permanent, unattended judgement that a build will not start.
///
/// Two things were wrong with it and both cost real time on the owner's Mac in September 2026. Nothing
/// in the product ever removed a pin, although the field's own documentation said a newer version
/// cleared it - so a wrong judgement lasted for ever and the only cure was deleting the state file by
/// hand. And the staging check never consulted the pin at all, so the pinned build was re-downloaded on
/// every cycle and thrown away at install time: roughly a hundred megabytes an hour, for five days.
/// </summary>
public class PinnedBadVersionTests
{
    private static Version V(string text) => Version.Parse(text);

    [Fact]
    public void APinnedBuild_IsNotDownloadedAgain()
    {
        var state = new UpdaterState { PinnedBadVersion = "2.0.5" };

        // Both owners refuse a pinned build at install time, so fetching it is a hundred megabytes spent
        // to reach a refusal that is already certain.
        Assert.False(UpdateService.ShouldStage(current: V("2.0.4"), latest: V("2.0.5"), state));
    }

    [Fact]
    public void ABuildNewerThanThePin_IsStillOffered()
    {
        var state = new UpdaterState { PinnedBadVersion = "2.0.5" };

        Assert.True(UpdateService.ShouldStage(current: V("2.0.4"), latest: V("2.0.7"), state));
    }

    [Fact]
    public void AReleaseNewerThanThePin_ClearsIt()
    {
        // The owner's Mac: 2.0.5 pinned on 3 September, 2.0.7 published, and the pin still sat in the
        // file afterwards because nothing had ever been written to remove one.
        var state = new UpdaterState { PinnedBadVersion = "2.0.5" };

        UpdateService.ClearPinIfSuperseded(state, V("2.0.7"));

        Assert.Null(state.PinnedBadVersion);
    }

    [Theory]
    [InlineData("2.0.5")]   // the same build: the judgement is still about this exact thing
    [InlineData("2.0.6")]   // newer than the pin, so it clears - guarded by the assertion below
    public void ThePinSurvivesUntilSomethingStrictlyNewerArrives(string latest)
    {
        var state = new UpdaterState { PinnedBadVersion = "2.0.5" };

        UpdateService.ClearPinIfSuperseded(state, V(latest));

        if (Version.Parse(latest) > Version.Parse("2.0.5"))
            Assert.Null(state.PinnedBadVersion);
        else
            Assert.Equal("2.0.5", state.PinnedBadVersion);
    }

    [Fact]
    public void AnOlderReleaseNeverClearsThePin()
    {
        var state = new UpdaterState { PinnedBadVersion = "2.0.5" };

        UpdateService.ClearPinIfSuperseded(state, V("2.0.4"));

        Assert.Equal("2.0.5", state.PinnedBadVersion);
    }

    [Fact]
    public void APinNobodyCanRead_IsLeftAlone_RatherThanGuessedAt()
    {
        var state = new UpdaterState { PinnedBadVersion = "not-a-version" };

        UpdateService.ClearPinIfSuperseded(state, V("9.9.9"));

        Assert.Equal("not-a-version", state.PinnedBadVersion);
    }

    [Fact]
    public void WithNoPin_NothingHappens()
    {
        var state = new UpdaterState();

        UpdateService.ClearPinIfSuperseded(state, V("2.0.7"));

        Assert.Null(state.PinnedBadVersion);
    }
}
