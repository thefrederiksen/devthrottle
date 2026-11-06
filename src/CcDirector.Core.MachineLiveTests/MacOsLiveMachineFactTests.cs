using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// THE macOS LOCK ITSELF, TESTED. These run everywhere and always, because
/// <see cref="MacOsLiveMachineFactAttribute.SkipReason"/> is a pure function of two facts handed to it.
///
/// A gate nobody has watched refuse anything is indistinguishable from one that lets everything
/// through, and this gate's whole job is to stop 36 machine-reading tests running where they cannot
/// work. The reason it exists at all is that the attribute it builds on gates the opt-in and the core
/// count but NOT the platform, so a macOS test on a Linux runner with the opt-in set would run and fail
/// rather than skip.
/// </summary>
[Trait("Category", "Resources")]
public class MacOsLiveMachineFactTests
{
    [Fact]
    public void OnAMacWithTheOptIn_ItRuns()
    {
        Assert.Null(MacOsLiveMachineFactAttribute.SkipReason("the processor", enabled: true, onMacOs: true));
    }

    [Fact]
    public void OnAMacWithoutTheOptIn_ItSkipsAndSaysHowToRunIt()
    {
        var reason = MacOsLiveMachineFactAttribute.SkipReason("the processor", enabled: false, onMacOs: true);

        Assert.NotNull(reason);
        Assert.Contains(LiveMachineFactAttribute.Variable, reason, StringComparison.Ordinal);
        Assert.Contains("the processor", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OffAMac_ItSkipsWhetherOrNotTheOptInIsSet(bool enabled)
    {
        // THE BRANCH THAT MATTERS, and it is why this attribute exists rather than the plain one. With
        // the opt-in set on a Linux or Windows runner, the general attribute would let these tests run
        // and they would fail on the first Mach call.
        var reason = MacOsLiveMachineFactAttribute.SkipReason("IOKit", enabled, onMacOs: false);

        Assert.NotNull(reason);
        Assert.Contains("only on a Mac", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OffAMac_TheReasonDoesNotPromiseThatTheVariableWouldHelp()
    {
        // A reader on Linux who is told to "set CC_DIRECTOR_LIVE_MACHINE_TESTS=1 to run it" has been
        // promised a run they can never have. The permanent reason wins, and it says so explicitly.
        var reason = MacOsLiveMachineFactAttribute.SkipReason("IOKit", enabled: false, onMacOs: false);

        Assert.NotNull(reason);
        Assert.Contains("will not make it run here", reason, StringComparison.Ordinal);
    }
}
