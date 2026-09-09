using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// Whether this machine can start a windowed application, and what is allowed to follow from the answer.
///
/// The defect behind these tests: on 2026-09-03 a launcher installed a good build against a Mac whose
/// display was asleep, watched it die before its first window, and pinned it as bad for five days. The
/// rule that matters is not "is there a display" but WHICH ANSWERS MAY STOP AN UPDATE - because an
/// update held by a check that could not run is an update that silently never happens, and that is the
/// failure this whole area already had once.
/// </summary>
public class DisplayAvailabilityTests
{
    [Theory]
    [InlineData(DisplayReadiness.Asleep)]
    [InlineData(DisplayReadiness.None)]
    public void OnlyAMeasuredAbsenceOfADisplay_MayHoldAnUpdate(DisplayReadiness readiness)
    {
        // These two are positive measurements of a condition that WILL kill a starting build.
        Assert.NotNull(DisplayAvailability.DescribeObstacle(readiness));
    }

    [Theory]
    [InlineData(DisplayReadiness.Ready)]
    [InlineData(DisplayReadiness.Unknown)]
    public void ADisplayThatIsFine_OrAnAnswerNobodyHas_NeverHoldsAnUpdate(DisplayReadiness readiness)
    {
        // Unknown is every non-Mac platform, and a Mac whose window server will not answer. Treating it
        // as a problem would stop updates on Windows and Linux outright, and would let a broken probe
        // quietly freeze a whole fleet - a far worse failure than the one being fixed.
        Assert.Null(DisplayAvailability.DescribeObstacle(readiness));
    }

    [Fact]
    public void EveryReadiness_HasAnAnswer_AndTheObstacleNamesTheDisplay()
    {
        foreach (var readiness in Enum.GetValues<DisplayReadiness>())
        {
            var obstacle = DisplayAvailability.DescribeObstacle(readiness);
            if (obstacle is null) continue;

            Assert.Contains("display", obstacle, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(obstacle));
        }
    }

    [Fact]
    public void OffMacOS_TheProbeReportsThatItDoesNotKnow()
    {
        // The probe is CoreGraphics, so there is nothing to ask anywhere else. It must say so rather
        // than guessing, and it must never throw: it runs inside the launcher's background loop.
        if (OperatingSystem.IsMacOS())
            return;

        Assert.Equal(DisplayReadiness.Unknown, DisplayAvailability.Check());
    }

    [Fact]
    public void OnMacOS_TheProbeAnswersWithoutThrowing()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // Which answer depends on whether the machine running this has a screen awake, so the assertion
        // is on the contract rather than the value: it returns, and it returns something meaningful.
        var readiness = DisplayAvailability.Check();
        Assert.True(Enum.IsDefined(readiness));
    }

    [Fact]
    public void OffMacOS_WakingIsNotEvenAttempted()
    {
        // Deliberately NOT run on macOS. Wake turns a real screen on, and a test suite that lights up
        // the machine it runs on is a test suite people stop running. The macOS path is verified by
        // hand against a sleeping display; see the pull request for that run.
        if (OperatingSystem.IsMacOS())
            return;

        // Everywhere else the probe reports Unknown, and Wake must hand that back untouched rather than
        // shelling out to a tool that is not there.
        Assert.Equal(DisplayReadiness.Unknown, DisplayAvailability.Wake());
    }
}
