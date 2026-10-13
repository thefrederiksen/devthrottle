using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// STARTING A SIGN-IN FROM THE COCKPIT over the real license store on a throwaway SQLite file, with fake Directors on
/// the other end of the command (License Switching, contract section 8.4). The clock is the test's own; a sign-in is
/// awaited through <c>WhenIdleAsync</c>, never through a wait.
///
/// What these hold down: the start answers before the Director does, with its row already written; the command is the
/// contract's, with the contract's wait; each refusal says its sentence and writes nothing; each thing the send can
/// return is recorded as the contract says; a row with no answer is settled from the machine's report; and one
/// DevThrottle account can never start a sign-in on another's machine.
/// </summary>
public sealed class LicenseSignInServiceTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private const string Mac = "the-mac";
    private const string Director = "dir-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSignInLog _log;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseSignInService _service;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseSignInServiceTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _log = new LicenseSignInLog(db);
        _service = new LicenseSignInService(_log, new LicenseReadModel(_store), _directors.IsConnected, _directors.SendAsync,
            _directors.EnterScope, () => _now, CancellationToken.None);
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    /// <summary>The machine's Director reports these holdings now, and is connected. Returns the machine's identifier.</summary>
    private string Reports(TenantId tenant, string machine, string director, params LicenseHolding[] holdings)
    {
        var id = _store.ApplyReport(tenant, director, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero).MachineId;
        _directors.Connect(tenant, director);
        return id.ToString("N");
    }

    /// <summary>The mac on account A, holding nothing else.</summary>
    private string MacOnA(TenantId? tenant = null) => Reports(tenant ?? One, Mac, Director, Holding(A, true, readAt: _now));

    private LicenseSignInStart AddSpare(string machineId, string account = B, TenantId? tenant = null)
        => _service.Start(tenant ?? One, account, machineId, LicenseSignInReasons.Manual);

    private LicenseSignInStart Renew(string machineId, string account = A, TenantId? tenant = null)
        => _service.Start(tenant ?? One, account, machineId, LicenseSignInReasons.Renewal);

    private void TheDirectorAnswers(string outcome, string? reasonCode = null, string? message = null)
        => _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = outcome,
            ReasonCode = reasonCode,
            Message = message ?? "The Director's own sentence.",
            SignedInAtUtc = outcome == LicenseSignInOutcomes.Failed ? null : _now,
            SignInExpiresAtUtc = outcome == LicenseSignInOutcomes.Failed ? null : _now.AddDays(30),
            FinishedAtUtc = _now.AddSeconds(40),
        }));

    private List<LicenseSwitchLogEntity> Rows(TenantId? tenant = null)
        => _store.ListLog(tenant ?? One, LogStart).Where(r => r.Kind == LicenseSignInLog.Kind).OrderBy(r => r.CreatedAtUtc).ToList();

    private LicenseSwitchLogEntity TheRow() => Assert.Single(Rows());

    private async Task<LicenseSwitchLogEntity> Finished(LicenseSignInStart start)
    {
        Assert.True(start.Accepted, start.RefusalText);
        await _service.WhenIdleAsync();
        return TheRow();
    }

    private void AssertRefused(LicenseSignInStart start, int status, string word)
    {
        Assert.False(start.Accepted);
        Assert.Equal((status, word), (start.Status, start.RefusalWord));
        Assert.False(string.IsNullOrWhiteSpace(start.RefusalText));
        Assert.Null(start.Row);
    }

    // ----- the start answers at once -----

    [Fact]
    public async Task Start_AnswersBeforeTheDirectorDoes_WithItsRowAlreadyWrittenPending()
    {
        var mac = MacOnA();
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rowStateWhenTheCommandArrived = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent =>
        {
            rowStateWhenTheCommandArrived.SetResult(_log.Find(One, ((LicenseSignInRequest)sent.Payload).SignInId)?.State);
            return answering.Task;
        };

        // The call is given a limit so that a call that waits for the Director is a red test, not a run that never ends.
        var start = await Task.Run(() => AddSpare(mac)).WaitAsync(TimeSpan.FromSeconds(60));

        // Start has returned while the Director still holds the command.
        Assert.True(start.Accepted);
        Assert.Equal(202, start.Status);
        Assert.Equal(LicenseLogStates.Pending, await rowStateWhenTheCommandArrived.Task.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(LicenseLogStates.Pending, TheRow().State);

        answering.SetResult(FakeLicenseDirectors.Ok(new LicenseSignInResult
            { SignInId = start.Row!.SwitchId!, Outcome = LicenseSignInOutcomes.Added, FinishedAtUtc = _now }));
        await _service.WhenIdleAsync();
        Assert.Equal(LicenseSignInOutcomes.Added, TheRow().State);
    }

    [Fact]
    public async Task Start_AddASpare_SendsLicenseSignInToTheMachinesDirector_AsTheContractSays()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Added);

        var row = await Finished(AddSpare(mac, "  Account-B@Example.com "));

        var sent = Assert.Single(_directors.Sends);
        Assert.Equal((One, Director, LicenseContract.SignInVerb, Mac), (sent.Scope, sent.DirectorId, sent.Verb, sent.MachineName));
        Assert.Equal(TimeSpan.FromMinutes(5), sent.Timeout);
        var request = Assert.IsType<LicenseSignInRequest>(sent.Payload);
        Assert.Equal((row.SwitchId, B, LicenseSignInReasons.Manual), (request.SignInId, request.AccountEmail, request.Reason));
        Assert.Matches(LicenseContract.SignInIdShape, request.SignInId);
        Assert.Equal(T0 + LicenseContract.SignInNotAfter, request.NotAfterUtc);
        Assert.Equal(DateTimeKind.Utc, request.NotAfterUtc.Kind);
    }

    [Fact]
    public async Task Start_RenewNow_SendsTheReasonRenewal()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Renewed);

        await Finished(Renew(mac));

        Assert.Equal(LicenseSignInReasons.Renewal, ((LicenseSignInRequest)Assert.Single(_directors.Sends).Payload).Reason);
    }

    [Fact]
    public async Task Start_TheRowIsOfItsOwnKind_AndNamesTheMachineTheAccountAndWhy()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Added);

        var start = AddSpare(mac);
        var written = start.Row!;
        await Finished(start);

        Assert.Equal((LicenseSignInLog.Kind, Mac, "owner", "", B, LicenseSignInReasons.Manual),
            (written.Kind, written.MachineName, written.OsUser, written.FromAccountEmail, written.ToAccountEmail, written.Reason));
        Assert.Equal(mac, written.MachineId.ToString("N"));
        Assert.Equal((T0, T0 + LicenseContract.SignInNotAfter), (written.CreatedAtUtc, written.NotAfterUtc));
        Assert.Equal("Asked the Director on the-mac to sign in to account-b@example.com as a new spare.", written.Message);
    }

    // ----- what is refused -----

    [Fact]
    public void Start_OnAMachineWhoseDirectorIsNotConnected_IsRefusedWithASentence_AndNothingIsWrittenOrSent()
    {
        var mac = MacOnA();
        _directors.Disconnect(One, Director);

        var start = AddSpare(mac);

        AssertRefused(start, 409, "director_offline");
        Assert.Equal("The Director on the-mac is not connected, so a sign-in cannot be started there.", start.RefusalText);
        Assert.Empty(Rows());
        Assert.Empty(_directors.Sends);
    }

    [Fact]
    public async Task Start_WhileASignInForThatMachineAndAccountIsRunning_IsRefusedWithASentence()
    {
        var mac = MacOnA();
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;
        var first = AddSpare(mac);
        Assert.True(first.Accepted);

        var second = AddSpare(mac);

        AssertRefused(second, 409, "sign_in_running");
        Assert.Equal("A sign-in for account-b@example.com on the-mac is already running.", second.RefusalText);
        // Another account on the same machine is a different sign-in, and is started.
        Assert.True(AddSpare(mac, C).Accepted);

        answering.SetResult(null);
        await _service.WhenIdleAsync();
        Assert.Equal(2, Rows().Count);
        Assert.Equal(2, _directors.Sends.Count);
    }

    [Fact]
    public async Task Start_AfterTheFirstSignInEnded_IsAcceptedAgain()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Failed, LicenseRenewalErrors.NoApprover);
        Assert.True(AddSpare(mac).Accepted);
        await _service.WhenIdleAsync();

        Assert.True(AddSpare(mac).Accepted);
        await _service.WhenIdleAsync();

        Assert.Equal(2, Rows().Count);
    }

    [Fact]
    public void Start_AddASpareForAnAccountTheMachineHolds_AndRenewForOneItDoesNot_AreRefusedWithASentence()
    {
        var mac = MacOnA();

        var add = AddSpare(mac, A);
        var renew = Renew(mac, B);

        AssertRefused(add, 409, "already_held");
        Assert.Equal("the-mac already holds a sign-in for account-a@example.com. Use Renew now to renew it.", add.RefusalText);
        AssertRefused(renew, 409, "not_held");
        Assert.Equal("the-mac holds no sign-in for account-b@example.com. Use Add a spare to sign it in there.", renew.RefusalText);
        Assert.Empty(Rows());
        Assert.Empty(_directors.Sends);
    }

    [Theory]
    [InlineData("", "invalid_account")]
    [InlineData("not-an-email", "invalid_account")]
    public void Start_ForAnAccountThatIsNotAnEmail_IsRefused(string account, string word)
    {
        var mac = MacOnA();

        AssertRefused(AddSpare(mac, account), 400, word);
        Assert.Empty(Rows());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("the-mac")]
    [InlineData("6f1c2a9e-4b7d-4e0f-9a3c-5b8d2e1f7a40")]
    public void Start_WithAMachineIdentifierThatIsNotOne_IsRefused(string? machineId)
    {
        MacOnA();

        AssertRefused(_service.Start(One, B, machineId, LicenseSignInReasons.Manual), 400, "invalid_machine");
        Assert.Empty(Rows());
    }

    [Fact]
    public void Start_OnAMachineOfAnotherAccount_IsRefusedAsUnknown_AndThatMachineIsSentNothing()
    {
        MacOnA(One);
        var macOfTwo = Reports(Two, Mac, "dir-mac-of-two", Holding(A, true, readAt: _now));

        var start = AddSpare(macOfTwo, B, tenant: One);

        AssertRefused(start, 404, "unknown_machine");
        Assert.Empty(_directors.Sends);
        Assert.Empty(Rows(One));
        Assert.Empty(Rows(Two));
    }

    [Fact]
    public async Task Start_TwoAccountsWithAMachineOfTheSameName_EachSignsInItsOwnMachineInItsOwnScope()
    {
        var macOfOne = Reports(One, Mac, "dir-mac-of-one", Holding(A, true, readAt: _now));
        var macOfTwo = Reports(Two, Mac, "dir-mac-of-two", Holding(A, true, readAt: _now));
        TheDirectorAnswers(LicenseSignInOutcomes.Added);

        Assert.True(AddSpare(macOfOne, B, One).Accepted);
        Assert.True(AddSpare(macOfTwo, B, Two).Accepted);
        await _service.WhenIdleAsync();

        Assert.Equal(new[] { (One, "dir-mac-of-one"), (Two, "dir-mac-of-two") },
            _directors.Sends.Select(s => (s.Scope!.Value, s.DirectorId)).OrderBy(s => s.DirectorId));
        Assert.Single(Rows(One));
        Assert.Single(Rows(Two));
    }

    // ----- what the row records of each thing the send can return -----

    [Theory]
    [InlineData(LicenseSignInOutcomes.Added)]
    [InlineData(LicenseSignInOutcomes.Renewed)]
    public async Task TheDirectorSignedIn_TheRowEndsWithItsOutcomeItsSentenceAndItsTimes(string outcome)
    {
        var mac = MacOnA();
        TheDirectorAnswers(outcome, message: "Signed in.");

        var row = await Finished(outcome == LicenseSignInOutcomes.Added ? AddSpare(mac) : Renew(mac));

        Assert.Equal((outcome, (string?)null, "Signed in."), (row.State, row.ReasonCode, row.Message));
        Assert.Equal((1, T0, T0, T0.AddSeconds(40)), (row.SendCount, row.LastSentAtUtc, row.FinishedAtUtc, row.DirectorFinishedAtUtc));
        Assert.Equal(false, row.Replayed);
    }

    [Fact]
    public async Task TheDirectorRefused_TheRowEndsFailedWithItsReasonAndItsSentence()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Failed, LicenseRenewalErrors.ApproverOffline, "The approving machine was offline.");

        var row = await Finished(AddSpare(mac));

        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseRenewalErrors.ApproverOffline, "The approving machine was offline."),
            (row.State, row.ReasonCode, row.Message));
        Assert.NotNull(row.FinishedAtUtc);
    }

    [Fact]
    public async Task TheDirectorDisconnectedBetweenTheCheckAndTheSend_TheRowEndsFailedDirectorOffline()
    {
        var mac = MacOnA();
        _directors.AnswerWith(_ => null);

        var row = await Finished(AddSpare(mac));

        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorOffline, 0), (row.State, row.ReasonCode, row.SendCount));
    }

    [Theory]
    [InlineData(DirectorCommandStatus.Timeout)]
    [InlineData(DirectorCommandStatus.TunnelDropped)]
    public async Task NoAnswerCameBack_TheRowIsUnknownAndStillOpen_AndItBlocksASecondSignIn(DirectorCommandStatus status)
    {
        var mac = MacOnA();
        var sentence = DirectorCommandRouter.DescribeTimeout(Mac, LicenseContract.SignInCommandTimeout);
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(status, sentence));

        var row = await Finished(AddSpare(mac));

        Assert.Equal((LicenseLogStates.Unknown, (string?)null, sentence, 1), (row.State, row.ReasonCode, row.Message, row.SendCount));
        Assert.Null(row.FinishedAtUtc);
        AssertRefused(AddSpare(mac), 409, "sign_in_running");
    }

    public static TheoryData<string, DirectorCommandResult> AnswersThatAreNotTheResultOfThisSignIn() => new()
    {
        { "the Director does not know the command", DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "unknown verb") },
        { "the Director failed", DirectorCommandResult.Fail(DirectorCommandStatus.Error, "boom") },
        { "an empty body", DirectorCommandResult.Success("") },
        { "a body that is not JSON", DirectorCommandResult.Success("{ not JSON") },
        { "a result for another sign-in", DirectorCommandResult.Success("""{"signInId":"22222222222222222222222222222222","outcome":"added"}""") },
        { "an outcome the contract does not have", DirectorCommandResult.Success("""{"signInId":"SELF","outcome":"switched"}""") },
    };

    [Theory]
    [MemberData(nameof(AnswersThatAreNotTheResultOfThisSignIn))]
    public async Task AnAnswerThatIsNotTheResultOfThisSignIn_TheRowEndsFailedDirectorError(string what, DirectorCommandResult answer)
    {
        Assert.NotEmpty(what);
        var mac = MacOnA();
        _directors.AnswerWith(sent => answer.BodyJson is { } body && body.Contains("SELF")
            ? DirectorCommandResult.Success(body.Replace("SELF", ((LicenseSignInRequest)sent.Payload).SignInId))
            : answer);

        var row = await Finished(AddSpare(mac));

        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorError), (row.State, row.ReasonCode));
        Assert.False(string.IsNullOrWhiteSpace(row.Message));
        Assert.NotNull(row.FinishedAtUtc);
    }

    [Fact]
    public async Task ARowThatHasEnded_IsNeverChangedAgain()
    {
        var mac = MacOnA();
        TheDirectorAnswers(LicenseSignInOutcomes.Added, message: "Signed in.");
        var row = await Finished(AddSpare(mac));

        Assert.False(_log.TryFinish(One, row.SwitchId!, LicenseSignInOutcomes.Failed, "director-error", "late", _now));
        Assert.False(_log.TryMarkUnknown(One, row.SwitchId!, "late"));
        Assert.False(_log.RecordSend(One, row.SwitchId!, "dir-other", _now));

        Assert.Equal((LicenseSignInOutcomes.Added, "Signed in.", 1), (TheRow().State, TheRow().Message, TheRow().SendCount));
    }

    // ----- a row with no answer is settled from the machine's report -----

    private async Task<string> AStartedSignInWithNoAnswer(Func<string, LicenseSignInStart> start)
    {
        var mac = MacOnA();
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        await Finished(start(mac));
        return mac;
    }

    [Fact]
    public async Task SettleUnanswered_BeforeNoDirectorCouldStillBeRunningIt_LeavesTheRowOpen()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter;

        Assert.Equal(0, _service.SettleUnanswered(One));

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
    }

    [Fact]
    public async Task SettleUnanswered_ANewSpareTheMachinesReportShows_EndsAdded_AndSaysItWasSettledFromTheReport()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);
        Reports(One, Mac, Director, Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));

        Assert.Equal(1, _service.SettleUnanswered(One));

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Added, (string?)null), (row.State, row.ReasonCode));
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report shows the-mac holds a sign-in for account-b@example.com.", row.Message);
        Assert.Equal(_now, row.FinishedAtUtc);
    }

    [Fact]
    public async Task SettleUnanswered_ANewSpareTheMachinesReportDoesNotShow_EndsFailed()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);

        Assert.Equal(1, _service.SettleUnanswered(One));

        var row = TheRow();
        Assert.Equal(LicenseSignInOutcomes.Failed, row.State);
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report does not show a sign-in for account-b@example.com on the-mac.", row.Message);
    }

    [Theory]
    [InlineData(true, LicenseSignInOutcomes.Renewed)]
    [InlineData(false, LicenseSignInOutcomes.Failed)]
    public async Task SettleUnanswered_ARenewal_EndsRenewedOnlyWhenTheReportShowsASignInMadeAfterItWasAsked(bool signedInAfter, string expected)
    {
        await AStartedSignInWithNoAnswer(mac => Renew(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);
        var holding = Holding(A, true, readAt: _now);
        holding.SignedInAtUtc = signedInAfter ? T0.AddMinutes(2) : T0.AddDays(-20);
        Reports(One, Mac, Director, holding);

        Assert.Equal(1, _service.SettleUnanswered(One));

        Assert.Equal(expected, TheRow().State);
    }

    [Fact]
    public async Task SettleUnanswered_NeverSettlesASignInWhoseAnswerMayStillCome()
    {
        var mac = MacOnA();
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;
        var start = AddSpare(mac);
        await _directors.WaitFor(sent => sent.Verb == LicenseContract.SignInVerb);
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromHours(1);

        Assert.Equal(0, _service.SettleUnanswered(One));
        Assert.Equal(LicenseLogStates.Pending, TheRow().State);

        answering.SetResult(FakeLicenseDirectors.Ok(new LicenseSignInResult
            { SignInId = start.Row!.SwitchId!, Outcome = LicenseSignInOutcomes.Added, FinishedAtUtc = _now }));
        await _service.WhenIdleAsync();
        Assert.Equal(LicenseSignInOutcomes.Added, TheRow().State);
    }

    [Fact]
    public async Task Start_SettlesARowPastItsTimeFirst_SoItNeverBlocksANewSignIn()
    {
        var mac = await AStartedSignInWithNoAnswer(m => AddSpare(m));
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);
        TheDirectorAnswers(LicenseSignInOutcomes.Added);

        Assert.True(AddSpare(mac).Accepted);
        await _service.WhenIdleAsync();

        Assert.Equal(new[] { LicenseSignInOutcomes.Failed, LicenseSignInOutcomes.Added }, Rows().Select(r => r.State));
    }

    [Fact]
    public async Task SettleUnanswered_SettlesOnlyTheAccountItIsAskedFor()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);

        Assert.Equal(0, _service.SettleUnanswered(Two));

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
    }

    // ----- the rows the switch code reads are untouched -----

    [Fact]
    public async Task ASignInRow_IsNeverAnOpenSwitch_SoItNeverHoldsAMachineBackFromOne()
    {
        var mac = MacOnA();
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        await Finished(AddSpare(mac));

        Assert.Empty(_store.ListOpenSwitches(One));
        Assert.NotNull(_store.CreatePendingSwitch(One, Guid.ParseExact(mac, "N"), A, B, LicenseSwitchReasons.Manual, _now));
    }
}
