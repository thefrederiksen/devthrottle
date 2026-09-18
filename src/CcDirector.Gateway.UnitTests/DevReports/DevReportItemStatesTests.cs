using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>The one fold that gives a dev report item's delivery state its words (PLAN-phase-2.md, "Delivery").
/// Pinned exactly: the page and every app show these strings verbatim.</summary>
public sealed class DevReportItemStatesTests
{
    [Fact]
    public void States_EveryStatusAndLabel_AreThePlannedWords()
    {
        Assert.Equal(("queued", "Accepted"), (DevReportItemStates.QueuedState.Status, DevReportItemStates.QueuedState.Label));
        Assert.Equal(("held", "Delivered when the agent finishes its turn"), (DevReportItemStates.HeldState.Status, DevReportItemStates.HeldState.Label));
        Assert.Equal(("sending", "Sending to the session"), (DevReportItemStates.SendingState.Status, DevReportItemStates.SendingState.Label));
        Assert.Equal(("delivered", "Delivered to the session"), (DevReportItemStates.DeliveredState.Status, DevReportItemStates.DeliveredState.Label));
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (DevReportItemStates.UnconfirmedState.Status, DevReportItemStates.UnconfirmedState.Label));
        Assert.Equal(("replaced", "Replaced by a later answer"), (DevReportItemStates.ReplacedState.Status, DevReportItemStates.ReplacedState.Label));
        Assert.Equal(("refused", "This session has ended"), (DevReportItemStates.SessionEndedState.Status, DevReportItemStates.SessionEndedState.Label));
    }

    [Theory]
    [InlineData("Accepted", "delivered", "Delivered to the session")]
    [InlineData("Unconfirmed", "delivered", "Sent to the session, not confirmed")]
    [InlineData("NeverLeft", "held", "Delivered when the agent finishes its turn")]
    [InlineData("Refused", "held", "Delivered when the agent finishes its turn")]
    public void AfterSend_EachOutcome_IsItsState(string outcome, string status, string label)
    {
        var state = DevReportItemStates.AfterSend(Enum.Parse<DevReportItemStates.SendOutcome>(outcome));

        Assert.Equal(status, state.Status);
        Assert.Equal(label, state.Label);
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("held", true)]
    [InlineData("sending", true)]
    [InlineData("delivered", false)]
    [InlineData("replaced", false)]
    [InlineData("refused", false)]
    public void IsOpen_QueuedHeldAndSending_AreNotYetSettled(string status, bool open)
        => Assert.Equal(open, DevReportItemStates.IsOpen(status));

    [Theory]
    [InlineData("queued", true)]
    [InlineData("held", true)]
    [InlineData("sending", false)]
    [InlineData("delivered", false)]
    public void IsWaiting_OnlyQueuedAndHeld_MayStillBeReplacedOrDrained(string status, bool waiting)
        => Assert.Equal(waiting, DevReportItemStates.IsWaiting(status));
}
