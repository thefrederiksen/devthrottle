using CcDirector.ControlApi.SmartRestart;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The contract types the way down screens build against (phase 1 interface, sections 2 and 3).
///
/// Another phase is already writing screens against these names with a fake of its own, so what is pinned
/// here is what that fake assumes: the five times, the refusal of any other, and the members of the four
/// enums in the order the interface document gives them.
/// </summary>
public class SmartShutdownContractTests
{
    [Fact]
    public void Allowed_Times_AreFiveTenFifteenThirtyAndSixtyMinutes()
    {
        Assert.Equal(
            new[] { 5, 10, 15, 30, 60 },
            SmartShutdownTimes.Allowed.Select(t => (int)t.TotalMinutes).ToArray());
    }

    [Fact]
    public void Default_Time_IsTenMinutesAndIsOneOfTheAllowedTimes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), SmartShutdownTimes.Default);
        Assert.Contains(SmartShutdownTimes.Default, SmartShutdownTimes.Allowed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void SmartShutdownRequest_AnAllowedTime_IsAccepted(int minutes)
    {
        var request = new SmartShutdownRequest(
            SmartShutdownPurpose.Restart, TimeSpan.FromMinutes(minutes), "update to 2.9.0");

        Assert.Equal(TimeSpan.FromMinutes(minutes), request.TimeAllowed);
        Assert.Equal(SmartShutdownPurpose.Restart, request.Purpose);
        Assert.Equal("update to 2.9.0", request.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(90)]
    [InlineData(-10)]
    public void SmartShutdownRequest_ATimeThatIsNotAllowed_ThrowsNamingWhatWasSentAndWhatMayBe(int minutes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(minutes), null));

        Assert.Contains("5, 10, 15, 30, 60 minutes", ex.Message);
        Assert.Contains($"{minutes} minutes was asked for", ex.Message);
    }

    [Fact]
    public void SmartShutdownRequest_ATimeBetweenTwoAllowedOnes_Throws()
    {
        // Ten minutes and one second is not ten minutes. "Exactly one of" means exactly.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmartShutdownRequest(
                SmartShutdownPurpose.Close, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1), null));
    }

    [Fact]
    public void SmartShutdownRequest_CopiedWithATimeThatIsNotAllowed_Throws()
    {
        var request = new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null);

        // The refusal is on the VALUE, not on the constructor: a copy is a second way to build a request,
        // and a check only the first way passes through is no check on what the engine is handed.
        Assert.Throws<ArgumentOutOfRangeException>(() => request with { TimeAllowed = TimeSpan.FromMinutes(7) });

        var copy = request with { TimeAllowed = TimeSpan.FromMinutes(30) };
        Assert.Equal(TimeSpan.FromMinutes(30), copy.TimeAllowed);
    }

    [Fact]
    public void SmartShutdownRequest_TwoRequestsSayingTheSameThing_AreEqual()
    {
        // The time lives in a field behind the property, and a record compares its fields: this holds
        // that the refusal did not quietly break value equality.
        Assert.Equal(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(15), "why"),
            new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(15), "why"));
        Assert.NotEqual(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(15), "why"),
            new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(30), "why"));
    }

    [Fact]
    public void Enums_CarryExactlyTheMembersTheInterfaceDocumentNames_InItsOrder()
    {
        Assert.Equal(new[] { "Close", "Restart" }, Enum.GetNames<SmartShutdownPurpose>());

        Assert.Equal(
            new[]
            {
                "Starting", "Asking", "Collecting", "Interrupting", "EndingAtLimit", "Cancelling",
                "Restarting", "Finished",
            },
            Enum.GetNames<SmartShutdownPhase>());

        Assert.Equal(
            new[]
            {
                "Pending", "Asked", "NotDelivered", "Writing", "HandedOver", "Interrupted", "ShutDown",
                "EndedAtLimit", "KeptRunning", "BroughtBack",
            },
            Enum.GetNames<SmartShutdownSessionState>());

        Assert.Equal(
            new[] { "Emptied", "RestartAccepted", "RestartRefused", "Cancelled", "Refused", "Failed" },
            Enum.GetNames<SmartShutdownOutcome>());
    }

    [Fact]
    public void Interfaces_CarryExactlyTheMembersTheInterfaceDocumentNames()
    {
        Assert.Equal(
            new[] { "CheckAsync", "RecordAndLetEndAsync", "ShutDownIgnoringAllAsync", "Start" },
            typeof(ISmartShutdown).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[]
            {
                "CancelAndKeepWorking", "ShutDownNow", "add_Changed", "get_Completion", "get_Current",
                "remove_Changed",
            },
            typeof(ISmartShutdownRun).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
