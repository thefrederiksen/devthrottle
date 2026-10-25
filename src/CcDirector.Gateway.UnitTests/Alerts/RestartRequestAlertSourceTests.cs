using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// THE RESTART REQUESTS AS ALERTS (the Alerts mission). Every state a request can reach, driven through the real
/// <see cref="DirectorRestartRequestStore"/> on its own clock, folds to the right needs-answer flag, status word and
/// sentence. The one the owner tripped over is pinned hardest: an ACCEPTED request needs no answer the moment it is
/// accepted, and carries the Director's progress line instead of a red card.
/// </summary>
public sealed class RestartRequestAlertSourceTests
{
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");

    private DateTime _now = new(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc);
    private readonly DirectorRestartRequestStore _store;
    private readonly RestartRequestAlertSource _source;

    public RestartRequestAlertSourceTests()
    {
        _store = new DirectorRestartRequestStore { Clock = () => _now };
        _source = new RestartRequestAlertSource(_store);
    }

    private string Ask(TenantId? tenant = null, string machine = "mac-mini")
    {
        var request = new DirectorRestartRequestDto
        {
            Machine = machine,
            DirectorId = "d-1",
            Reason = "the Mac Tester can run the v2.31.0 checks",
            Title = $"Restart the Director on {machine}?",
            AskedBySentence = "rob asks: the Mac Tester can run the v2.31.0 checks",
            Capability = new MachineRestartCapabilityDto(),
        };
        Assert.True(_store.TryCreate(tenant ?? Tenant, request, out _));
        return request.Id;
    }

    private AlertDto Only() => Assert.Single(_source.List(Tenant, _now));

    [Fact]
    public void Pending_NeedsAnAnswer_WithTheGatewaysOwnTitleAndSentence_AndItsExpiry()
    {
        var id = Ask();

        var alert = Only();

        Assert.True(alert.NeedsAnswer);
        Assert.Equal("restart-request:" + id, alert.Id);
        Assert.Equal(AlertKinds.RestartRequest, alert.Kind);
        Assert.Equal("Waiting", alert.Status);
        Assert.Equal("Restart the Director on mac-mini?", alert.Title);
        Assert.Equal("rob asks: the Mac Tester can run the v2.31.0 checks", alert.Summary);
        Assert.Equal(_now + DirectorRestartRequestStore.Expiry, alert.ExpiresAtUtc);
        Assert.Equal(_now, alert.CreatedAtUtc);
        Assert.Equal(_now, alert.UpdatedAtUtc);
        Assert.IsType<DirectorRestartRequestDto>(alert.Detail);
    }

    [Fact]
    public void Accepted_NeedsNoAnswerAtOnce_AndItsSummaryIsTheProgressLine()
    {
        var id = Ask();
        _now = _now.AddMinutes(2);
        Assert.Equal(RestartAcceptOutcome.Accepted, _store.Accept(Tenant, id, out _));

        var alert = Only();

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Accepted", alert.Status);
        Assert.Equal("Restarting the Director on mac-mini", alert.Title);
        Assert.Equal("Accepted; telling the Director to begin.", alert.Summary);
        Assert.Null(alert.ExpiresAtUtc);
        Assert.Equal(_now, alert.UpdatedAtUtc);
    }

    [Fact]
    public void Accepted_UpdatesInPlace_WhenTheDirectorReportsProgress()
    {
        var id = Ask();
        _store.Accept(Tenant, id, out _);
        _now = _now.AddMinutes(5);
        _store.Report(Tenant, id, new DirectorRestartProgressReport
        {
            State = DirectorRestartRequestState.Accepted,
            Progress = "2 of 3 sessions have written their handover",
        }, out _);

        var alert = Only();

        Assert.Equal("Accepted", alert.Status);
        Assert.Equal("2 of 3 sessions have written their handover.", alert.Summary);
        Assert.Equal(_now, alert.UpdatedAtUtc);
    }

    [Fact]
    public void Completed_SaysItRestarted()
    {
        var id = Ask();
        _store.Accept(Tenant, id, out _);
        _store.Report(Tenant, id, new DirectorRestartProgressReport
        {
            State = DirectorRestartRequestState.Completed,
            Progress = "the launcher accepted the guarded restart",
        }, out _);

        var alert = Only();

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Restarted", alert.Status);
        Assert.Equal("The Director on mac-mini restarted", alert.Title);
        Assert.Equal("The restart finished.", alert.Summary);
    }

    [Fact]
    public void Abandoned_SaysItStopped_AndKeepsTheDirectorsWordsForTheDetailsOnly()
    {
        var id = Ask();
        _store.Accept(Tenant, id, out _);
        _store.Report(Tenant, id, new DirectorRestartProgressReport
        {
            State = DirectorRestartRequestState.Abandoned,
            Progress = "session 104 would not stop cleanly: guardedRestartReason=busy",
        }, out _);

        var alert = Only();

        Assert.Equal("Stopped", alert.Status);
        Assert.Equal("The Director stopped without restarting. Open Details for its reason.", alert.Summary);
        Assert.DoesNotContain("guardedRestartReason", alert.Summary + alert.Title);
        Assert.Contains("guardedRestartReason", ((DirectorRestartRequestDto)alert.Detail!).StateReason);
    }

    [Fact]
    public void Declined_SaysNothingWasRestarted()
    {
        var id = Ask();
        Assert.True(_store.Decline(Tenant, id, "not now", out _));

        var alert = Only();

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Declined", alert.Status);
        Assert.Equal("You declined it. Nothing was restarted.", alert.Summary);
    }

    [Fact]
    public void ExpiredUnanswered_NeedsNoAnswer_AndSaysNobodyAccepted()
    {
        Ask();
        _now += DirectorRestartRequestStore.Expiry;

        var alert = Only();

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Expired", alert.Status);
        Assert.Equal("Nobody accepted it within 30 minutes. Nothing was restarted.", alert.Summary);
    }

    [Fact]
    public void ExpiredWhileRunning_SaysToCheckTheMachine()
    {
        var id = Ask();
        _store.Accept(Tenant, id, out _);
        _now += DirectorRestartRequestStore.RunningExpiry;

        var alert = Only();

        Assert.Equal("Expired", alert.Status);
        Assert.Equal("The Director did not say how the restart ended within 3 hours. Check that machine before assuming it restarted.",
            alert.Summary);
    }

    [Fact]
    public void AClosedRequest_LeavesTheListTwentyFourHoursAfterItLastChanged()
    {
        var id = Ask();
        _store.Decline(Tenant, id, "no", out _);

        _now += AlertWindow.Earlier;
        Assert.Single(_source.List(Tenant, _now));

        _now += TimeSpan.FromSeconds(1);
        Assert.Empty(_source.List(Tenant, _now));
    }

    [Fact]
    public void AnotherAccountsRequest_IsNeverListed()
    {
        Ask(Other);

        Assert.Empty(_source.List(Tenant, _now));
        Assert.Single(_source.List(Other, _now));
    }
}
