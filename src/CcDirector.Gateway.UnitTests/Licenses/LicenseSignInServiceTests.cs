using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Core.Utilities;
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
// It captures the process-wide log, so no other test class runs beside it.
[Collection(FileLogCaptureCollection.Name)]
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
        => ReportsWithAClockDifference(TimeSpan.Zero, tenant, machine, director, holdings);

    /// <summary>The same, from a machine whose clock the Gateway measured as this far from its own.</summary>
    private string ReportsWithAClockDifference(TimeSpan difference, TenantId tenant, string machine, string director, params LicenseHolding[] holdings)
    {
        var id = _store.ApplyReport(tenant, director, ++_sequence, Report(machine, _now, holdings), _now, difference).MachineId;
        _directors.Connect(tenant, director);
        return id.ToString("N");
    }

    /// <summary>The mac on account A, holding nothing else.</summary>
    private string MacOnA(TenantId? tenant = null) => Reports(tenant ?? One, Mac, Director, Holding(A, true, readAt: _now));

    private LicenseSignInStart AddSpare(string machineId, string account = B, TenantId? tenant = null)
        => _service.Start(tenant ?? One, account, machineId, LicenseSignInReasons.Manual);

    private LicenseSignInStart Renew(string machineId, string account = A, TenantId? tenant = null)
        => _service.Start(tenant ?? One, account, machineId, LicenseSignInReasons.Renewal);

    private void TheDirectorAnswers(string outcome, string? reasonCode = null, string? message = null, bool replayed = false)
        => _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            Replayed = replayed,
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
    [InlineData("account-b@example.com\nINJECTED", "0123456789abcdef0123456789abcdef", "invalid_account")]
    [InlineData("account-b\u202eINJECTED@example.com", "0123456789abcdef0123456789abcdef", "invalid_account")]
    [InlineData(B, "the-mac\nINJECTED", "invalid_machine")]
    [InlineData(B, "INJECTED0123456789abcdef0123456789abcdef", "invalid_machine")]
    public void Start_NothingTheCallerSentIsLoggedBeforeItIsChecked(string account, string machineId, string word)
    {
        MacOnA();
        using var capture = new CapturedLog();

        var start = _service.Start(One, account, machineId, LicenseSignInReasons.Manual);
        var lines = capture.Lines();

        AssertRefused(start, 400, word);
        Assert.Contains(lines, l => l.Contains($"[LicenseSignInService] Start: REFUSED {word}"));
        Assert.DoesNotContain(lines, l => l.Contains("INJECTED"));
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

    [Fact]
    public async Task ASendThatThrows_AndAnAnswerThatCannotBeRead_AreLoggedByTheirKindOnly_NeverByTheirMessage()
    {
        var mac = MacOnA();
        using var capture = new CapturedLog();

        // A send that throws: the row ends failed, and the line names the kind of failure only.
        _directors.AnswerWith(_ => throw new InvalidOperationException("MESSAGE-OF-THE-SEND-FAILURE"));
        var first = await Finished(AddSpare(mac));
        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorError), (first.State, first.ReasonCode));

        // An answer that is not JSON: the reader's message quotes the text it could not read.
        _directors.AnswerWith(_ => DirectorCommandResult.Success("MESSAGE-OF-THE-ANSWER {"));
        Assert.True(AddSpare(mac, C).Accepted);
        await _service.WhenIdleAsync();

        var lines = capture.Lines();
        Assert.Contains(lines, l => l.EndsWith($"[LicenseSignInService] signIn={first.SwitchId} FAILED: InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("the Director's answer is not readable: JsonException"));
        Assert.DoesNotContain(lines, l => l.Contains("MESSAGE-OF-THE"));
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

    // ----- a row with no answer is settled: by asking its Director again, else from a later report -----

    private static readonly DateTime Due = T0 + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);

    // The limit only ends a test whose settling never returns: such a test fails, it does not hang.
    private Task<int> Settle(TenantId? tenant = null)
        => _service.SettleUnansweredAsync(tenant ?? One, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

    private void NoAnswerComesBack()
        => _directors.AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));

    private async Task<string> AStartedSignInWithNoAnswer(Func<string, LicenseSignInStart> start, DateTime? signedInBefore = null)
    {
        var holding = Holding(A, true, readAt: _now);
        holding.SignedInAtUtc = signedInBefore;
        var mac = Reports(One, Mac, Director, holding);
        NoAnswerComesBack();
        await Finished(start(mac));
        return mac;
    }

    /// <summary>The machine's report is accepted now, and its Director is not connected afterwards.</summary>
    private void ReportsAndIsGone(params LicenseHolding[] holdings)
    {
        Reports(One, Mac, Director, holdings);
        _directors.Disconnect(One, Director);
    }

    [Fact]
    public async Task Settle_BeforeNoDirectorCouldStillBeRunningIt_AsksNobody_AndLeavesTheRowOpen()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0 + LicenseSignInService.UnansweredAfter;

        Assert.Equal(0, await Settle());

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));
    }

    [Fact]
    public async Task Settle_AsksTheDirectorAgainWithTheSameIdentifier_InItsOwnScope_AndEndsTheRowWithItsStoredAnswer()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        var asked = TheRow();
        _now = Due;
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = LicenseSignInOutcomes.Added,
            Message = "Signed in, as the Director stored it.",
            FinishedAtUtc = T0.AddMinutes(3),
            Replayed = true,
        }));

        Assert.Equal(1, await Settle());

        var sends = _directors.Of(LicenseContract.SignInVerb);
        Assert.Equal(2, sends.Count);
        var again = (LicenseSignInRequest)sends[1].Payload;
        // The SAME identifier, with its first deadline - which has passed, so a Director that never saw it starts nothing.
        Assert.Equal((asked.SwitchId, B, LicenseSignInReasons.Manual, T0 + LicenseContract.SignInNotAfter),
            (again.SignInId, again.AccountEmail, again.Reason, again.NotAfterUtc));
        Assert.True(again.NotAfterUtc < _now);
        Assert.Equal((One, Director, LicenseSignInService.AskAgainWait), (sends[1].Scope, sends[1].DirectorId, sends[1].Timeout));

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Added, "Signed in, as the Director stored it.", true, 2),
            (row.State, row.Message, row.Replayed, row.SendCount));
        Assert.Equal((T0.AddMinutes(3), Due), (row.DirectorFinishedAtUtc, row.FinishedAtUtc));
    }

    [Fact]
    public async Task Settle_ACommandThatNeverWentOut_AndADirectorThatSaysItStartedNothing_EndsTheRowFailedExpired()
    {
        // A row written by a Gateway that stopped before it sent anything: no command has ever gone out for it.
        MacOnA();
        var machine = Assert.Single(new LicenseReadModel(_store).Read(One, _now).Machines);
        _log.CreatePending(One, machine, B, LicenseSignInReasons.Manual, "Asked.", _now);
        Assert.Equal(0, TheRow().SendCount);
        _now = Due;
        TheDirectorAnswers(LicenseSignInOutcomes.Failed, LicenseRenewalErrors.Expired, "The request arrived after its deadline; nothing was started.");

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseRenewalErrors.Expired, "The request arrived after its deadline; nothing was started.", false, 1),
            (row.State, row.ReasonCode, row.Message, row.Replayed, row.SendCount));
    }

    [Theory]
    [InlineData(true, LicenseSignInOutcomes.Added)]
    [InlineData(false, LicenseSignInOutcomes.Failed)]
    public async Task Settle_AnAnswerThatIsNotAReplay_ForACommandThatHadGoneOut_DoesNotEndTheRow_AndTheLaterReportDoes(
        bool theReportShowsTheSpare, string expected)
    {
        // The sign-in ran and its answer was lost; the Director no longer has the result it kept, so asked again it
        // says "expired, nothing was started". That is not how the sign-in ended.
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        Assert.Equal(1, TheRow().SendCount);
        _now = Due;
        TheDirectorAnswers(LicenseSignInOutcomes.Failed, LicenseRenewalErrors.Expired, "The request arrived after its deadline; nothing was started.");

        Assert.Equal(0, await Settle());
        Assert.Equal((LicenseLogStates.Unknown, (DateTime?)null, 2), (TheRow().State, TheRow().FinishedAtUtc, TheRow().SendCount));

        // The Director is still connected and still says the same. The report accepted afterwards is what decides.
        _now = Due.AddMinutes(2);
        if (theReportShowsTheSpare)
            Reports(One, Mac, Director, Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));
        else
            Reports(One, Mac, Director, Holding(A, true, readAt: _now));

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal((expected, (string?)null), (row.State, row.ReasonCode));
        Assert.StartsWith("Settled from the machine's report:", row.Message);
    }

    [Fact]
    public async Task Settle_AnAskThatGetsNoAnswerInTime_SettlesNothingInThatPass_NotEvenFromAReportThatShowsTheSpare()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = Due;
        // A report accepted after the last moment the first command could have been running, and it shows the spare.
        Reports(One, Mac, Director, Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));
        NoAnswerComesBack();

        // The ask timed out: the Director may be running the sign-in now, so this pass says nothing.
        Assert.Equal(0, await Settle());
        Assert.Equal((LicenseLogStates.Unknown, 2), (TheRow().State, TheRow().SendCount));
        Assert.Equal(0, await Settle());
        Assert.Equal((LicenseLogStates.Unknown, 3), (TheRow().State, TheRow().SendCount));

        // A pass that asks nobody settles it from that report.
        _directors.Disconnect(One, Director);
        Assert.Equal(1, await Settle());
        Assert.Equal((LicenseSignInOutcomes.Added, 3), (TheRow().State, TheRow().SendCount));
    }

    [Theory]
    [InlineData(240)]
    [InlineData(300)]
    [InlineData(-600)]
    public async Task Settle_AMachineWhoseClockIsWithinAMinuteOfTheMarginOrBeyondIt_IsNotAskedAgain_AndIsSettledFromItsReport(int secondsApart)
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        // Whatever it would say if it were asked, it must not be asked: it could judge the deadline as still ahead.
        TheDirectorAnswers(LicenseSignInOutcomes.Added, message: "Started and signed in a quarter of an hour late.");

        // Its clock difference is known from a report of before the sign-in could have ended: not asked, nothing known.
        _now = T0.AddMinutes(1);
        ReportsWithAClockDifference(TimeSpan.FromSeconds(secondsApart), One, Mac, Director, Holding(A, true, readAt: _now));
        _now = Due;
        Assert.Equal(0, await Settle());
        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));

        // A report accepted afterwards settles it, still without asking.
        _now = Due.AddMinutes(2);
        ReportsWithAClockDifference(TimeSpan.FromSeconds(secondsApart), One, Mac, Director, Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));
        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Added, 1), (row.State, row.SendCount));
        Assert.StartsWith("Settled from the machine's report:", row.Message);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));
    }

    [Fact]
    public async Task Settle_AMachineWhoseClockIsJustInsideTheMargin_IsAskedAgain()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = T0.AddMinutes(1);
        ReportsWithAClockDifference(TimeSpan.FromSeconds(239), One, Mac, Director, Holding(A, true, readAt: _now));
        _now = Due;
        TheDirectorAnswers(LicenseSignInOutcomes.Added, message: "Signed in, as the Director stored it.", replayed: true);

        Assert.Equal(1, await Settle());

        Assert.Equal((LicenseSignInOutcomes.Added, "Signed in, as the Director stored it.", 2), (TheRow().State, TheRow().Message, TheRow().SendCount));
    }

    [Theory]
    [InlineData(false, "Ended without an answer: the-mac has not been seen for a day, so it is not known whether it signed in to account-b@example.com. Add a spare can be asked for again when the machine is back.")]
    [InlineData(true, "Ended without an answer: the-mac has not been seen for a day, so it is not known whether the sign-in for account-a@example.com was renewed. Renew now can be asked for again when the machine is back.")]
    public async Task Settle_AMachineThatHasSentNoReportForADay_EndsTheRowFailed_SaysTheMachineHasNotBeenSeen_AndTheSignInCanBeAskedForAgain(
        bool renewal, string sentence)
    {
        var mac = await AStartedSignInWithNoAnswer(m => renewal ? Renew(m) : AddSpare(m), signedInBefore: T0.AddDays(-20));
        _directors.Disconnect(One, Director);

        // One second short of a day since its last report: still open.
        _now = T0 + LicenseSignInService.MachineNotSeenAfter - TimeSpan.FromSeconds(1);
        Assert.Equal(0, await Settle());
        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);

        _now = T0 + LicenseSignInService.MachineNotSeenAfter;
        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Failed, (string?)null, sentence, _now), (row.State, row.ReasonCode, row.Message, row.FinishedAtUtc));
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));

        // The machine comes back: the same sign-in is no longer refused as running.
        _directors.Connect(One, Director);
        TheDirectorAnswers(renewal ? LicenseSignInOutcomes.Renewed : LicenseSignInOutcomes.Added);
        Assert.True((renewal ? Renew(mac) : AddSpare(mac)).Accepted);
        await _service.WhenIdleAsync();
        Assert.Equal(2, Rows().Count);
    }

    [Fact]
    public async Task Settle_TheTunnelDroppedAndNoReportSince_StaysOpen_WhileTheMachineWasSeenWithinADay()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _directors.Disconnect(One, Director);

        foreach (var later in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), TimeSpan.FromHours(23) })
        {
            _now = T0 + LicenseSignInService.UnansweredAfter + later;
            Assert.Equal(0, await Settle());
        }

        // The only report is the one from before the sign-in was asked for: it says nothing about how it ended.
        var row = TheRow();
        Assert.Equal(LicenseLogStates.Unknown, row.State);
        Assert.Null(row.FinishedAtUtc);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));
    }

    [Fact]
    public async Task Settle_AReportAcceptedWhileTheSignInCouldStillBeRunning_SettlesNothing()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        // Accepted at the very last moment a Director could still have been running it: not after it.
        _now = T0 + LicenseSignInService.UnansweredAfter;
        ReportsAndIsGone(Holding(A, true, readAt: _now));
        _now = Due.AddHours(1);

        Assert.Equal(0, await Settle());

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
    }

    [Fact]
    public async Task Settle_NoDirectorToAsk_ANewSpareShownByAReportAcceptedAfterwards_EndsAdded_AndSaysHowItWasKnown()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = Due;
        ReportsAndIsGone(Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal((LicenseSignInOutcomes.Added, (string?)null, 1), (row.State, row.ReasonCode, row.SendCount));
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report shows the-mac holds a sign-in for account-b@example.com.", row.Message);
        Assert.Equal(_now, row.FinishedAtUtc);
    }

    [Fact]
    public async Task Settle_NoDirectorToAsk_ANewSpareNotShownByAReportAcceptedAfterwards_EndsFailed()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = Due;
        ReportsAndIsGone(Holding(A, true, readAt: _now));

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal(LicenseSignInOutcomes.Failed, row.State);
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report does not show a sign-in for account-b@example.com on the-mac.", row.Message);
    }

    public static TheoryData<string, DirectorCommandResult> AnswersToAskingAgainThatSettleNothing() => new()
    {
        { "the tunnel dropped", DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, "dropped") },
        { "the Director does not know the command", DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "unknown verb") },
        { "a body that is not a result", DirectorCommandResult.Success("{ not JSON") },
    };

    [Theory]
    [MemberData(nameof(AnswersToAskingAgainThatSettleNothing))]
    public async Task Settle_ADirectorThatGivesNoAnswerWhenAskedAgain_LeavesItToTheReport_OpenUntilOneComes(string what, DirectorCommandResult answer)
    {
        Assert.NotEmpty(what);
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = Due;
        _directors.AnswerWith(_ => answer);

        // Asked again, no answer that says what happened, and no report since: nothing is claimed.
        Assert.Equal(0, await Settle());
        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
        Assert.Equal(2, TheRow().SendCount);

        // A report accepted after that moment shows the spare: now it is known.
        _now = Due.AddMinutes(2);
        Reports(One, Mac, Director, Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));
        Assert.Equal(1, await Settle());
        Assert.Equal(LicenseSignInOutcomes.Added, TheRow().State);
    }

    [Fact]
    public async Task Settle_ARenewalThatWorked_OnAMachineWhoseClockIsAMinuteBehind_EndsRenewed()
    {
        await AStartedSignInWithNoAnswer(mac => Renew(mac), signedInBefore: T0.AddDays(-20));
        _now = Due;
        // The renewal finished half a minute after it was asked for. The machine's clock is a minute behind, so the
        // time it reports is EARLIER than the moment the Gateway wrote the row - and it is still a new sign-in.
        var renewed = Holding(A, true, readAt: _now);
        renewed.SignedInAtUtc = T0.AddSeconds(-30);
        ReportsAndIsGone(renewed);

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal(LicenseSignInOutcomes.Renewed, row.State);
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report shows the sign-in for account-a@example.com on the-mac was renewed.", row.Message);
    }

    [Fact]
    public async Task Settle_ARenewal_WhoseSignInIsStillTheOneFromBeforeItWasAsked_EndsFailed_WhateverTheClocksSay()
    {
        // The machine's clock is far ahead: the sign-in it held before the ask is "newer" than the row by any clock.
        var before = T0.AddHours(6);
        await AStartedSignInWithNoAnswer(mac => Renew(mac), signedInBefore: before);
        _now = Due;
        var same = Holding(A, true, readAt: _now);
        same.SignedInAtUtc = before;
        ReportsAndIsGone(same);

        Assert.Equal(1, await Settle());

        var row = TheRow();
        Assert.Equal(LicenseSignInOutcomes.Failed, row.State);
        Assert.Equal("Settled from the machine's report: no answer to this sign-in came back, and the report does not show a newer sign-in for account-a@example.com on the-mac.", row.Message);
    }

    [Theory]
    [InlineData(false, true, LicenseSignInOutcomes.Renewed)]
    [InlineData(false, false, LicenseSignInOutcomes.Failed)]
    [InlineData(true, false, LicenseSignInOutcomes.Failed)]
    public async Task Settle_ARenewal_WhenTheMachineSaidNoSignInTime_IsRenewedOnlyWhenTheReportNowSaysOne(bool saidBefore, bool saysNow, string expected)
    {
        await AStartedSignInWithNoAnswer(mac => Renew(mac), signedInBefore: saidBefore ? T0.AddDays(-20) : null);
        _now = Due;
        var holding = Holding(A, true, readAt: _now);
        holding.SignedInAtUtc = saysNow ? T0.AddMinutes(1) : null;
        ReportsAndIsGone(holding);

        Assert.Equal(1, await Settle());

        Assert.Equal(expected, TheRow().State);
    }

    [Fact]
    public async Task ASignInRow_KeepsTheSignInTimeFromBeforeTheAsk_AndThePageIsNeverToldItAsAStart()
    {
        var before = T0.AddDays(-20);
        var holding = Holding(A, true, readAt: _now);
        holding.SignedInAtUtc = before;
        var mac = Reports(One, Mac, Director, holding);
        TheDirectorAnswers(LicenseSignInOutcomes.Renewed);

        var row = await Finished(Renew(mac));

        Assert.Equal(before, LicenseSignInLog.SignedInBefore(row));
        var shown = LicenseDisplayFold.LogEntry(row);
        Assert.Null(shown.StartedOnMachineAtUtc);
        Assert.Null(shown.TimingText);
        Assert.Equal(_now.AddSeconds(40), shown.FinishedOnMachineAtUtc);
    }

    [Fact]
    public async Task Settle_NeverSettlesASignInWhoseAnswerMayStillCome()
    {
        var mac = MacOnA();
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;
        var start = AddSpare(mac);
        await _directors.WaitFor(sent => sent.Verb == LicenseContract.SignInVerb);
        _now = Due.AddHours(1);

        Assert.Equal(0, await Settle());
        Assert.Equal(LicenseLogStates.Pending, TheRow().State);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));

        answering.SetResult(FakeLicenseDirectors.Ok(new LicenseSignInResult
            { SignInId = start.Row!.SwitchId!, Outcome = LicenseSignInOutcomes.Added, FinishedAtUtc = _now }));
        await _service.WhenIdleAsync();
        Assert.Equal(LicenseSignInOutcomes.Added, TheRow().State);
    }

    [Fact]
    public async Task ARowThatCannotBeSettledYet_StillBlocksASecondSignIn_AndOnceSettledItNoLongerDoes()
    {
        var mac = await AStartedSignInWithNoAnswer(m => AddSpare(m));
        _now = Due;

        // The Director is connected and gives no answer; no report since. Nothing is known, so nothing new is started.
        Assert.Equal(0, await Settle());
        AssertRefused(AddSpare(mac), 409, "sign_in_running");

        TheDirectorAnswers(LicenseSignInOutcomes.Failed, LicenseRenewalErrors.BrowserNotSignedIn, replayed: true);
        Assert.Equal(1, await Settle());
        TheDirectorAnswers(LicenseSignInOutcomes.Added);
        Assert.True(AddSpare(mac).Accepted);
        await _service.WhenIdleAsync();

        Assert.Equal(new[] { LicenseSignInOutcomes.Failed, LicenseSignInOutcomes.Added }, Rows().Select(r => r.State));
    }

    [Fact]
    public async Task Settle_SettlesOnlyTheAccountItIsAskedFor_AndAsksNoDirectorOfAnother()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        _now = Due;

        Assert.Equal(0, await Settle(Two));

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
        Assert.Single(_directors.Of(LicenseContract.SignInVerb));
    }

    /// <summary>A tenant pass over a fixed list: each pass runs with that tenant as the one in scope.</summary>
    private sealed class ListedTenants(params TenantId?[] tenants) : ITenantPass
    {
        public TenantId? Current { get; private set; }

        public void ForEachTenant(Action pass) => throw new NotSupportedException();

        public async Task ForEachTenantAsync(Func<Task> pass, CancellationToken ct = default)
        {
            foreach (var tenant in tenants)
            {
                Current = tenant;
                await pass();
            }
            Current = null;
        }
    }

    [Fact]
    public async Task SettleEveryTenant_SettlesEachAccountsRowsInItsOwnScope_AndOneAccountsFailureDoesNotStopAnother()
    {
        await AStartedSignInWithNoAnswer(mac => AddSpare(mac));
        var macOfTwo = Reports(Two, Mac, "dir-of-two", Holding(A, true, readAt: _now));
        Assert.True(_service.Start(Two, B, macOfTwo, LicenseSignInReasons.Manual).Accepted);
        await _service.WhenIdleAsync();
        _now = Due;
        // Account one's Director throws when asked again; account two's answers.
        _directors.Answer = sent => sent.DirectorId == Director
            ? throw new InvalidOperationException("the send failed")
            : Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new LicenseSignInResult
                { SignInId = ((LicenseSignInRequest)sent.Payload).SignInId, Outcome = LicenseSignInOutcomes.Added, FinishedAtUtc = _now, Replayed = true }));

        await _service.SettleEveryTenantAsync(new ListedTenants(One, null, Two), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(LicenseLogStates.Unknown, TheRow().State);
        Assert.Equal(LicenseSignInOutcomes.Added, Assert.Single(Rows(Two)).State);
        var again = _directors.Of(LicenseContract.SignInVerb).Skip(2).ToList();
        Assert.Equal(new[] { (One, Director), (Two, "dir-of-two") }, again.Select(s => (s.Scope!.Value, s.DirectorId)));
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
