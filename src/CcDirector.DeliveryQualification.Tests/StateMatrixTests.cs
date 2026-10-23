using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.DeliveryQualification;
using Xunit;

namespace CcDirector.DeliveryQualification.Tests;

/// <summary>
/// The working-or-waiting matrix's judgement (issue #4378): given the Director's sampled states and the true moment the
/// agent began waiting, which of the mission's targets were met. A judge that let a red-while-working stretch through,
/// or counted the session's waiting state from BEFORE the send as the answer to it, would certify the thing the owner
/// asked to have fixed.
/// </summary>
public sealed class StateMatrixTests
{
    private static readonly StateCase Turn = StateMatrix.All.Single(c => c.Name == "quick");
    private static readonly StateCase Approval = StateMatrix.All.Single(c => c.Name == "approval");
    private static readonly StateCase Picker = StateMatrix.All.Single(c => c.Name == "model-picker");

    private static List<(double, ActivityState)> Samples(params (double At, ActivityState State)[] changes)
    {
        // Expand the changes into 0.25 s samples up to 40 s, as the sampler takes them.
        var list = new List<(double, ActivityState)>();
        for (var t = 0.0; t <= 40.0; t += 0.25)
            list.Add((t, changes.Last(c => c.At <= t + 1e-9).State));
        return list;
    }

    [Fact]
    public void Judge_WorkingPromptlyThenWaitingPromptly_Passes()
    {
        var r = StateMatrix.Judge("claude", Turn, 5.0, Samples((0, ActivityState.WaitingForInput), (0.5, ActivityState.Working), (6.0, ActivityState.WaitingForInput)), "");

        Assert.True(r.Pass, r.Detail);
        Assert.Equal(1.0, r.ToWaiting);
    }

    [Fact]
    public void Judge_TheSilenceRulesTenSecondsLate_FailsAndMeasuresHowLate()
    {
        var r = StateMatrix.Judge("pi", Turn, 2.0, Samples((0, ActivityState.WaitingForInput), (1.0, ActivityState.Working), (12.25, ActivityState.WaitingForInput)), "");

        Assert.False(r.Pass);
        Assert.Equal(10.25, r.ToWaiting);
    }

    [Fact]
    public void Judge_RedWhileTheAgentWorks_IsFalseWaiting()
    {
        var r = StateMatrix.Judge("claude", Turn, 40.0,
            Samples((0, ActivityState.WaitingForInput), (0.5, ActivityState.Working), (15, ActivityState.WaitingForInput), (25, ActivityState.Working)), "");

        Assert.False(r.Pass);
        Assert.Equal(10.0, r.FalseWaiting, 1);
    }

    [Fact]
    public void Judge_TheWaitingStateFromBeforeTheSend_IsNotTheAnswerToIt()
    {
        // Never left WaitingForInput: it must not be read as having shown Working, nor as a prompt switch to waiting
        // that happened after a turn it never showed.
        var r = StateMatrix.Judge("claude", Turn, 5.0, Samples((0, ActivityState.WaitingForInput)), "");

        Assert.False(r.Pass);
        Assert.Null(r.ToWorking);
        Assert.Contains("not shown Working", r.Detail);
    }

    [Fact]
    public void Judge_AnApprovalShownOnlyAsWaitingForInput_FailsTheApprovalTarget()
    {
        var r = StateMatrix.Judge("claude", Approval, 4.0, Samples((0, ActivityState.WaitingForInput), (1, ActivityState.Working), (5, ActivityState.WaitingForInput)), "");

        Assert.False(r.Pass);
        Assert.False(r.ShowedApproval);
        Assert.Contains("approval was never shown", r.Detail);
    }

    [Fact]
    public void Judge_AnApprovalShownAsWaitingForPerm_Passes()
    {
        var r = StateMatrix.Judge("claude", Approval, 4.0, Samples((0, ActivityState.WaitingForInput), (1, ActivityState.Working), (4.5, ActivityState.WaitingForPerm)), "");

        Assert.True(r.Pass, r.Detail);
        Assert.True(r.ShowedApproval);
    }

    [Fact]
    public void Judge_APickerTheOwnerOpened_NeedsNoWorkingState()
    {
        var r = StateMatrix.Judge("codex", Picker, 1.0, Samples((0, ActivityState.WaitingForInput)), "");

        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void Timeline_ListsOnlyTheChanges()
    {
        var t = StateMatrix.Timeline([(0, ActivityState.WaitingForInput), (0.25, ActivityState.WaitingForInput), (1.0, ActivityState.Working), (12.3, ActivityState.WaitingForInput)]);

        Assert.Equal("WaitingForInput@0.0 Working@1.0 WaitingForInput@12.3", t);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-10-23T14:17:22.590Z"}""")]
    [InlineData("""{"timestamp":1792765042590}""")]
    public void Stamp_IsoAndUnixMilliseconds_AreBothRead(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var at = StateMatrix.Stamp(doc.RootElement);

        Assert.NotNull(at);
        Assert.Equal(DateTimeKind.Utc, at!.Value.Kind);
    }
}
