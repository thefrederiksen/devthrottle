using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE MINUTE CHECK AND SWITCH NOW over the real license store on a throwaway SQLite file, with a fake Director on the
/// other end of the send and a fake mail service (License Switching, contract sections 3 and 5). The clock is the
/// test's own: a "minute later" is an assignment, never a wait.
///
/// What these hold down: under 97% nothing is sent; at 97% the row is written BEFORE the send and every machine on the
/// account is sent a switch; each row of the table in contract section 3.5; an offline Director is sent the SAME switch
/// identifier again and a new one only after the first expired; a Gateway restart finishes the rows it finds; a machine
/// with an open row gets no second identifier; one owner email per event, retried when it failed and never sent again
/// once it went; bars; Switch now through the same send; and one tenant's check never reaches another tenant's Director.
/// </summary>
public sealed class LicenseSwitchServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly FakeDirectors _directors;
    private readonly FakeMail _mail = new();
    private readonly LicenseSwitchService _service;
    private DateTime _now = T0;
    private int _clockReads;
    private long _sequence;

    public LicenseSwitchServiceTests()
    {
        _store = new LicenseStore(_harness.Open());
        _directors = new FakeDirectors(() => _now);
        _service = new LicenseSwitchService(_store, new LicenseSnapshotSource(_store, new LicenseReadModel(_store)),
            _directors.SendAsync, _mail.SendAsync, () => { _clockReads++; return _now; });
    }

    public void Dispose() => _harness.Dispose();

    // ----- the fakes -----

    /// <summary>The Directors on the other end of the send. By default every Director runs the switch and answers
    /// <c>switched</c>; a test replaces <see cref="Answer"/> to be offline, slow, broken or refusing.</summary>
    private sealed class FakeDirectors
    {
        private readonly Func<DateTime> _clock;

        public FakeDirectors(Func<DateTime> clock)
        {
            _clock = clock;
            Answer = (_, request) => Task.FromResult<DirectorCommandResult?>(Ok(Result(request, LicenseSwitchOutcomes.Switched)));
        }

        public List<(string DirectorId, DirectorCommand Command, LicenseSwitchRequest Request)> Sent { get; } = new();

        public Func<string, LicenseSwitchRequest, Task<DirectorCommandResult?>> Answer { get; set; }

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)
                ?? throw new InvalidOperationException("The command carried no switch request.");
            lock (Sent) Sent.Add((directorId, command, request));
            return Answer(directorId, request);
        }

        public LicenseSwitchResult Result(LicenseSwitchRequest request, string outcome, string? reasonCode = null)
        {
            var switched = outcome != LicenseSwitchOutcomes.Failed;
            return new LicenseSwitchResult
            {
                SwitchId = request.SwitchId,
                Outcome = outcome,
                ReasonCode = reasonCode,
                Message = switched ? "The Director switched." : "The Director refused.",
                PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
                ActiveAccountEmailAfter = switched ? request.TargetAccountEmail : request.ExpectedActiveAccountEmail,
                VerifiedByUsageRead = switched,
                StartedAtUtc = _clock(),
                FinishedAtUtc = _clock().AddSeconds(1),
                ExchangeMilliseconds = switched ? 41 : 0,
            };
        }

        public static DirectorCommandResult Ok(LicenseSwitchResult result) => DirectorCommandResult.Success(JsonSerializer.Serialize(result, Web));

        public void AnswerWith(Func<LicenseSwitchRequest, DirectorCommandResult?> answer)
            => Answer = (_, request) => Task.FromResult(answer(request));

        public void Offline() => AnswerWith(_ => null);

        // What the router itself returns when the 60-second wait ends; made here so no test waits a minute for it.
        public void TimesOut() => AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout,
            CcDirector.Gateway.Api.DirectorCommandRouter.DescribeTimeout(Windows, LicenseContract.SwitchCommandTimeout)));
    }

    /// <summary>The mail service: answers from a queue, then <c>Sent</c>.</summary>
    private sealed class FakeMail
    {
        public List<(TenantId Tenant, LicenseOwnerEmail Email)> Attempts { get; } = new();

        public Queue<LicenseOwnerEmailResult> Answers { get; } = new();

        public Func<TenantId, bool>? Throws { get; set; }

        public Task<LicenseOwnerEmailResult> SendAsync(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
        {
            if (Throws?.Invoke(tenant) == true)
                throw new InvalidOperationException("The mail service broke.");
            Attempts.Add((tenant, email));
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
        }
    }

    // ----- helpers -----

    /// <summary>The machine's Director sends a report, received now, with every reading taken now.</summary>
    private Guid Reports(TenantId tenant, string machine, string director, params (string Email, bool Active, double FiveHour, double SevenDay)[] holdings)
        => _store.ApplyReport(tenant, director, ++_sequence,
            Report(machine, _now, holdings.Select(h => Holding(h.Email, h.Active, h.FiveHour, h.SevenDay, _now)).ToArray()),
            _now, TimeSpan.Zero).MachineId;

    /// <summary>A machine on account A at <paramref name="fiveHour"/> percent, holding a spare for B with room.</summary>
    private Guid OnA(string machine, string director, double fiveHour, TenantId? tenant = null)
        => Reports(tenant ?? Tenant, machine, director, (A, true, fiveHour, 20), (B, false, 10, 30));

    /// <summary>A machine on A at 98% whose only spare, B, has no room either.</summary>
    private Guid OnAWithNoRoomAnywhere() => Reports(Tenant, Windows, "dir-win", (A, true, 98, 20), (B, false, 99, 30));

    private void MinuteLater(int minutes = 1) => _now = _now.AddMinutes(minutes);

    private Task Tick(TenantId? tenant = null) => _service.TickAsync(tenant ?? Tenant, CancellationToken.None);

    private List<LicenseSwitchLogEntity> Rows(string kind, TenantId? tenant = null)
        => _store.ListLog(tenant ?? Tenant, LogStart).Where(r => r.Kind == kind).OrderBy(r => r.CreatedAtUtc).ToList();

    private LicenseSwitchLogEntity TheSwitch() => Assert.Single(Rows(LicenseLogKinds.Switch));

    private string? ActiveOn(string machine, TenantId? tenant = null)
        => new LicenseReadModel(_store).Read(tenant ?? Tenant, _now).Machines.Single(m => m.MachineName == machine).ActiveAccountEmail;

    // ----- under and at the threshold -----

    [Fact]
    public async Task Tick_UnderTheThreshold_SendsNothingAndWritesNothing()
    {
        OnA(Windows, "dir-win", 96.99);

        await Tick();

        Assert.Empty(_directors.Sent);
        Assert.Empty(_store.ListLog(Tenant, LogStart));
        Assert.Empty(_store.ListActiveBars(Tenant, _now));
        Assert.Empty(_mail.Attempts);
    }

    [Fact]
    public async Task Tick_AtTheThreshold_WritesTheRowBeforeTheSend_AndSendsToEveryMachineOnTheAccount()
    {
        OnA(Windows, "dir-win", 97);
        OnA(Mac, "dir-mac", 97);
        var rowStateWhenTheCommandArrived = new List<string?>();
        _directors.AnswerWith(request =>
        {
            lock (rowStateWhenTheCommandArrived) rowStateWhenTheCommandArrived.Add(_store.FindSwitch(Tenant, request.SwitchId)?.State);
            return FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched));
        });

        await Tick();

        // The row existed, pending, when each command reached its Director (contract section 5.5).
        Assert.Equal(new[] { LicenseLogStates.Pending, LicenseLogStates.Pending }, rowStateWhenTheCommandArrived);
        // One command per machine, each to the Director that sent that machine's last report.
        Assert.Equal(new[] { "dir-mac", "dir-win" }, _directors.Sent.Select(s => s.DirectorId).OrderBy(d => d));
        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(2, rows.Count);
        foreach (var (directorId, command, request) in _directors.Sent)
        {
            Assert.Equal((LicenseContract.SwitchVerb, ""), (command.Verb, command.SessionId));
            var row = Assert.Single(rows, r => r.SwitchId == request.SwitchId);
            Assert.Equal(directorId, row.DirectorId);
            Assert.Equal((B, A, LicenseSwitchReasons.Threshold), (request.TargetAccountEmail, request.ExpectedActiveAccountEmail, request.Reason));
            Assert.Equal((T0, T0 + LicenseContract.SwitchNotAfter), (request.RequestedAtUtc, request.NotAfterUtc));
            Assert.Equal((LicenseLogStates.Switched, 1), (row.State, row.SendCount));
        }
        Assert.Equal(2, rows.Select(r => r.SwitchId).Distinct().Count());
        // The machine's active account comes from the answer's read-back.
        Assert.Equal((B, B), (ActiveOn(Windows), ActiveOn(Mac)));
    }

    [Fact]
    public async Task Tick_SeveralMachines_AreSentTogether_NotOneAfterAnother()
    {
        OnA(Windows, "dir-win", 97);
        OnA(Mac, "dir-mac", 97);
        // Neither Director answers until BOTH commands have arrived. Sent one after another, the first would wait for
        // a second that is never sent, and the watchdog below would name the hang.
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        _directors.Answer = async (_, request) =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothArrived.SetResult();
            await bothArrived.Task;
            return FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched));
        };

        await Tick().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(Rows(LicenseLogKinds.Switch), r => Assert.Equal(LicenseLogStates.Switched, r.State));
    }

    [Fact]
    public async Task Tick_AfterAThresholdSwitch_RecordsTheBar()
    {
        OnA(Windows, "dir-win", 97);

        await Tick();

        var bar = Assert.Single(_store.ListActiveBars(Tenant, _now));
        Assert.Equal((A, LicenseWindows.FiveHour, T0.AddHours(2)), (bar.AccountEmail, bar.Window, bar.UntilUtc));
    }

    // ----- contract section 3.5: what the row records from each result -----

    [Fact]
    public async Task Result_NotConnected_StaysPendingDirectorOffline()
    {
        OnA(Windows, "dir-win", 97);
        _directors.Offline();

        await Tick();

        var row = TheSwitch();
        Assert.Equal((LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline), (row.State, row.ReasonCode));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal(0, row.SendCount); // nothing was sent
        Assert.Equal(A, ActiveOn(Windows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_TimeoutOrTunnelDropped_IsUnknown(bool tunnelDropped)
    {
        OnA(Windows, "dir-win", 97);
        if (tunnelDropped)
            _directors.Answer = (_, _) => throw new IOException("The connection closed.");
        else
            _directors.TimesOut();

        await Tick();

        var row = TheSwitch();
        Assert.Equal((LicenseLogStates.Unknown, null), (row.State, row.ReasonCode));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal((1, (DateTime?)T0), (row.SendCount, row.LastSentAtUtc));
        Assert.Contains("not known whether the command was carried out", row.Message);
    }

    [Theory]
    [InlineData(DirectorCommandStatus.BadRequest)]
    [InlineData(DirectorCommandStatus.Error)]
    public async Task Result_BadRequestOrError_IsFailedDirectorError_WithTheErrorAsTheMessage(DirectorCommandStatus status)
    {
        OnA(Windows, "dir-win", 97);
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(status, "The Director could not read the switch."));

        await Tick();

        var row = TheSwitch();
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "The Director could not read the switch."),
            (row.State, row.ReasonCode, row.Message));
        Assert.NotNull(row.FinishedAtUtc);
    }

    [Theory]
    [InlineData(LicenseSwitchOutcomes.Switched, null, B)]
    [InlineData(LicenseSwitchOutcomes.AlreadyActive, null, B)]
    [InlineData(LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.SpareNotHeld, A)]
    public async Task Result_Ok_RecordsTheBodysOutcomeAndReasonCode_AndTakesTheReadBack(string outcome, string? reasonCode, string activeAfter)
    {
        OnA(Windows, "dir-win", 97);
        _directors.AnswerWith(request => FakeDirectors.Ok(_directors.Result(request, outcome, reasonCode)));

        await Tick();

        var row = TheSwitch();
        Assert.Equal((outcome, reasonCode, activeAfter), (row.State, row.ReasonCode, row.ActiveAccountEmailAfter));
        Assert.NotNull(row.FinishedAtUtc);
        Assert.Equal(activeAfter, ActiveOn(Windows));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"switchId":"00000000000000000000000000000000","outcome":"switched","activeAccountEmailAfter":"account-b@example.com"}""")]
    [InlineData("""{"switchId":"SAME","outcome":"teleported","activeAccountEmailAfter":"account-b@example.com"}""")]
    public async Task Result_OkWithABodyThatIsNotThisSwitchsResult_IsFailedDirectorError_AndTakesNoReadBack(string body)
    {
        OnA(Windows, "dir-win", 97);
        _directors.AnswerWith(request => DirectorCommandResult.Success(body.Replace("SAME", request.SwitchId)));

        await Tick();

        var row = TheSwitch();
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError), (row.State, row.ReasonCode));
        Assert.Equal(A, ActiveOn(Windows));
    }

    // ----- contract section 5.1: the Director is offline -----

    [Fact]
    public async Task Offline_TheNextCheck_SendsTheSameSwitchIdentifierAgain()
    {
        OnA(Windows, "dir-win", 97);
        _directors.Offline();
        await Tick();
        var first = TheSwitch();

        MinuteLater();
        OnA(Windows, "dir-win", 97);
        _directors.AnswerWith(request => FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched)));
        await Tick();

        Assert.Equal(new[] { first.SwitchId, first.SwitchId }, _directors.Sent.Select(s => s.Request.SwitchId));
        // The repeat is the same switch in every field: it carries the first decision's times, not this check's.
        Assert.Equal((T0, T0 + LicenseContract.SwitchNotAfter), (_directors.Sent[1].Request.RequestedAtUtc, _directors.Sent[1].Request.NotAfterUtc));
        Assert.Equal(LicenseLogStates.Switched, TheSwitch().State);
    }

    [Fact]
    public async Task Offline_PastNotAfter_TheRowEndsFailed_AndTheCheckDecidesAfreshWithANewIdentifier()
    {
        OnA(Windows, "dir-win", 97);
        _directors.Offline();
        await Tick();
        var first = TheSwitch();

        MinuteLater(11);
        OnA(Windows, "dir-win", 97);
        _directors.AnswerWith(request => FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched)));
        await Tick();

        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(2, rows.Count);
        Assert.Equal((first.SwitchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline),
            (rows[0].SwitchId, rows[0].State, rows[0].ReasonCode));
        Assert.NotEqual(first.SwitchId, rows[1].SwitchId);
        Assert.Equal(LicenseLogStates.Switched, rows[1].State);
        Assert.Equal(rows[1].SwitchId, _directors.Sent[^1].Request.SwitchId);
        Assert.Equal(_now, _directors.Sent[^1].Request.RequestedAtUtc);
    }

    [Fact]
    public async Task Offline_WhenTheDecisionNoLongerStands_TheSwitchIsNotSentAgain()
    {
        OnA(Windows, "dir-win", 97);
        _directors.Offline();
        await Tick();

        // The account's window reset: a fresh reading shows room, so the engine no longer decides this switch.
        MinuteLater();
        OnA(Windows, "dir-win", 5);
        await Tick();

        Assert.Single(_directors.Sent);
        Assert.Equal(LicenseLogStates.Pending, TheSwitch().State);
    }

    [Fact]
    public async Task Offline_WhenTheDecisionChanges_TheWaitingRowEnds_AndTheNewDecisionGetsANewIdentifier()
    {
        Reports(Tenant, Windows, "dir-win", (A, true, 97, 20), (B, false, 10, 30), (C, false, 10, 40));
        _directors.Offline();
        await Tick();
        var first = TheSwitch();
        Assert.Equal(B, first.ToAccountEmail);

        // B filled up meanwhile, so the best target is now C: a different decision for the same machine.
        MinuteLater();
        Reports(Tenant, Windows, "dir-win", (A, true, 97, 20), (B, false, 99, 30), (C, false, 10, 40));
        await Tick();

        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(2, rows.Count);
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline), (rows[0].State, rows[0].ReasonCode));
        Assert.Equal(C, rows[1].ToAccountEmail);
        Assert.NotEqual(first.SwitchId, rows[1].SwitchId);
    }

    // ----- contract section 5.5: the Gateway restarted mid-switch -----

    /// <summary>What a Gateway that stopped mid-send leaves behind: the pending row, and nothing in memory.</summary>
    private LicenseSwitchLogEntity RowLeftByARestart()
    {
        var machineId = OnA(Windows, "dir-win", 97);
        return _store.CreatePendingSwitch(Tenant, machineId, A, B, LicenseSwitchReasons.Threshold, _now)
               ?? throw new InvalidOperationException("The row was not written.");
    }

    [Fact]
    public async Task Restart_BeforeNotAfter_ResendsTheSameIdentifier_OnlyOnceTheMachineHasReported()
    {
        var left = RowLeftByARestart();

        MinuteLater();
        await Tick();
        Assert.Empty(_directors.Sent); // the machine has not reported since the row was written

        MinuteLater();
        OnA(Windows, "dir-win", 97);
        await Tick();

        Assert.Equal(left.SwitchId, Assert.Single(_directors.Sent).Request.SwitchId);
        Assert.Equal(LicenseLogStates.Switched, TheSwitch().State);
        Assert.Equal(B, ActiveOn(Windows));
    }

    [Fact]
    public async Task Restart_AReplayedAnswer_SettlesTheRow_AndNeverOverwritesTheMachinesLaterReport()
    {
        var left = RowLeftByARestart();
        // The switch DID run before the restart, and a person has since moved the machine back to A by hand.
        var ranAt = _now.AddSeconds(30);
        MinuteLater(2);
        OnA(Windows, "dir-win", 50);
        _directors.AnswerWith(request =>
        {
            var stored = _directors.Result(request, LicenseSwitchOutcomes.Switched);
            stored.Replayed = true;
            stored.StartedAtUtc = ranAt;
            stored.FinishedAtUtc = ranAt.AddSeconds(1);
            return FakeDirectors.Ok(stored);
        });

        await Tick();

        var row = TheSwitch();
        Assert.Equal((left.SwitchId, LicenseLogStates.Switched, true), (row.SwitchId, row.State, row.Replayed));
        Assert.Equal(A, ActiveOn(Windows)); // the report is newer than the stored answer
    }

    [Fact]
    public async Task Restart_AfterNotAfter_ResendsOnceAnyway_AndTheDirectorsExpiredAnswerSettlesTheRow()
    {
        var left = RowLeftByARestart();
        MinuteLater(11);
        OnA(Windows, "dir-win", 50);
        _directors.AnswerWith(request => FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.Expired)));

        await Tick();

        Assert.Equal(left.SwitchId, Assert.Single(_directors.Sent).Request.SwitchId);
        Assert.Equal((LicenseLogStates.Failed, LicenseSwitchReasonCodes.Expired), (TheSwitch().State, TheSwitch().ReasonCode));
    }

    [Theory]
    [InlineData(true, LicenseLogStates.Switched)]
    [InlineData(false, LicenseLogStates.Failed)]
    public async Task Restart_AfterNotAfter_NoAnswerToTheOneResend_IsSettledFromTheMachinesReport(bool reportShowsTheTarget, string expected)
    {
        RowLeftByARestart();
        MinuteLater(11);
        OnA(Windows, "dir-win", 50);
        _directors.TimesOut();
        await Tick(); // the one resend after NotAfterUtc: no answer
        Assert.Single(_directors.Sent);

        MinuteLater();
        await Tick(); // no report since that send: nothing to go on yet
        Assert.Equal(LicenseLogStates.Unknown, TheSwitch().State);

        MinuteLater();
        if (reportShowsTheTarget)
            Reports(Tenant, Windows, "dir-win", (B, true, 10, 30), (A, false, 50, 20));
        else
            OnA(Windows, "dir-win", 50);
        await Tick();

        Assert.Single(_directors.Sent); // not sent a third time
        var row = TheSwitch();
        Assert.Equal(expected, row.State);
        Assert.StartsWith("Settled from the machine's report", row.Message);
        Assert.NotNull(row.FinishedAtUtc);
    }

    // ----- contract section 5.4: never a second identifier while one is open -----

    [Fact]
    public async Task OpenRow_WithNoKnownOutcome_TheMachineIsSentNoSecondIdentifier()
    {
        OnA(Windows, "dir-win", 97);
        _directors.TimesOut();
        await Tick();
        var first = TheSwitch();

        MinuteLater();
        await Tick(); // still at 97%, no report since the send
        Assert.Single(_directors.Sent);

        MinuteLater();
        OnA(Windows, "dir-win", 97); // reported: ask again, with the SAME identifier
        await Tick();

        Assert.Equal(new[] { first.SwitchId, first.SwitchId }, _directors.Sent.Select(s => s.Request.SwitchId));
        Assert.Equal(first.SwitchId, TheSwitch().SwitchId);
    }

    [Fact]
    public async Task OpenRow_WithNoKnownOutcome_ItsMachineTakesNoPartInChoosingTheTarget_AndTheOthersStillSwitch()
    {
        // Two machines on A with no spare in common: the Windows machine holds B, the Mac holds C.
        Reports(Tenant, Windows, "dir-win", (A, true, 97, 20), (B, false, 10, 30));
        var mac = Reports(Tenant, Mac, "dir-mac", (A, true, 97, 20), (C, false, 10, 30));
        // The Mac has a switch whose outcome is not known (left by a restart; it has not reported since).
        Assert.NotNull(_store.CreatePendingSwitch(Tenant, mac, A, C, LicenseSwitchReasons.Threshold, _now));

        await Tick();

        // The Mac's holdings do not veto B: the Windows machine is switched, and nobody is told there is no room.
        var sent = Assert.Single(_directors.Sent);
        Assert.Equal(("dir-win", B), (sent.DirectorId, sent.Request.TargetAccountEmail));
        Assert.Empty(_mail.Attempts);
        Assert.Empty(Rows(LicenseLogKinds.NoRoom));
    }

    [Fact]
    public async Task OpenRow_OnOneMachine_DoesNotHoldBackTheOtherMachinesOnTheAccount()
    {
        OnA(Windows, "dir-win", 97);
        OnA(Mac, "dir-mac", 97);
        _directors.Answer = (director, request) => Task.FromResult<DirectorCommandResult?>(director == "dir-mac"
            ? null
            : FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched)));

        await Tick();

        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(LicenseLogStates.Switched, rows.Single(r => r.MachineName == Windows).State);
        Assert.Equal(LicenseLogStates.Pending, rows.Single(r => r.MachineName == Mac).State);
    }

    // ----- no account has room: one owner email per event -----

    [Fact]
    public async Task NoTarget_OneEmailAcrossManyChecks_NothingSwitched_AndTheLogSaysTheOwnerWasEmailed()
    {
        OnAWithNoRoomAnywhere();
        await Tick();
        for (var i = 0; i < 3; i++)
        {
            MinuteLater();
            OnAWithNoRoomAnywhere();
            await Tick();
        }

        var attempt = Assert.Single(_mail.Attempts);
        Assert.Equal(Tenant, attempt.Tenant);
        Assert.Contains("no Claude account has room", attempt.Email.Subject);
        Assert.Empty(_directors.Sent);
        Assert.Empty(Rows(LicenseLogKinds.Switch));
        var row = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((A, LicenseLogStates.Open, LicenseGatewayReasonCodes.OwnerEmailed, T0), (row.FromAccountEmail, row.State, row.ReasonCode, row.LastSentAtUtc));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal($"No account had room for the machines on {A}, so nothing was switched; the owner was emailed.", row.Message);
    }

    [Fact]
    public async Task NoTarget_AFailedSendIsNotMarkedEmailed_IsRetriedOnALaterCheck_AndNeverSentAgainOnceItWent()
    {
        _mail.Answers.Enqueue(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached"));
        OnAWithNoRoomAnywhere();
        await Tick();

        var row = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailFailed, null), (row.ReasonCode, row.LastSentAtUtc));
        Assert.Contains("the owner has not been emailed yet because the DevThrottle account service could not be reached", row.Message);

        MinuteLater();
        OnAWithNoRoomAnywhere();
        await Tick(); // retried, and this time it goes
        Assert.Equal(2, _mail.Attempts.Count);
        row = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, _now), (row.ReasonCode, row.LastSentAtUtc));

        MinuteLater();
        OnAWithNoRoomAnywhere();
        await Tick();
        Assert.Equal(2, _mail.Attempts.Count); // never again
    }

    [Fact]
    public async Task NoTarget_WhenTheMailServiceAsksForAWait_TheRetryWaitsThatLong()
    {
        _mail.Answers.Enqueue(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "too many emails", TimeSpan.FromMinutes(3)));
        OnAWithNoRoomAnywhere();
        await Tick();

        MinuteLater(2);
        OnAWithNoRoomAnywhere();
        await Tick();
        Assert.Single(_mail.Attempts); // two minutes of the three have passed

        MinuteLater();
        OnAWithNoRoomAnywhere();
        await Tick();
        Assert.Equal(2, _mail.Attempts.Count);
    }

    [Fact]
    public async Task NoTarget_OnAGatewayWithNoAccountToEmail_IsRecordedOnce_AndNeverTriedAgain()
    {
        _mail.Answers.Enqueue(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.NoAccount, "this self-hosted Gateway has no DevThrottle account to email"));
        OnAWithNoRoomAnywhere();
        await Tick();
        MinuteLater();
        OnAWithNoRoomAnywhere();
        await Tick();

        Assert.Single(_mail.Attempts);
        var row = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((LicenseGatewayReasonCodes.NoOwnerAccount, null), (row.ReasonCode, row.LastSentAtUtc));
        Assert.Contains("no owner email was sent because this self-hosted Gateway has no DevThrottle account to email", row.Message);
    }

    [Fact]
    public async Task NoTarget_TheEventCloses_AndANewEventEmailsOnce()
    {
        OnAWithNoRoomAnywhere();
        await Tick();

        // The window reset early: a fresh reading under the threshold ends the event.
        MinuteLater();
        Reports(Tenant, Windows, "dir-win", (A, true, 20, 20), (B, false, 99, 30));
        await Tick();
        var ended = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((LicenseLogStates.Ended, _now), (ended.State, ended.FinishedAtUtc));
        Assert.Single(_mail.Attempts);

        // Out of room again: a new event, one new email, however many checks follow.
        for (var i = 0; i < 2; i++)
        {
            MinuteLater();
            OnAWithNoRoomAnywhere();
            await Tick();
        }

        Assert.Equal(2, _mail.Attempts.Count);
        var rows = Rows(LicenseLogKinds.NoRoom);
        Assert.Equal(new[] { LicenseLogStates.Ended, LicenseLogStates.Open }, rows.Select(r => r.State));
    }

    [Fact]
    public async Task NoTarget_WhenTheOutageLastsLonger_TheEventsEndMovesWithIt()
    {
        OnAWithNoRoomAnywhere();
        await Tick();
        Assert.Equal(T0.AddHours(2), Assert.Single(Rows(LicenseLogKinds.NoRoom)).NotAfterUtc); // the 5-hour reset

        // The week tires too: the outage now lasts until the weekly reset.
        MinuteLater();
        Reports(Tenant, Windows, "dir-win", (A, true, 98, 99), (B, false, 99, 30));
        await Tick();

        Assert.Equal(T0.AddDays(4), Assert.Single(Rows(LicenseLogKinds.NoRoom)).NotAfterUtc);
        Assert.Single(_mail.Attempts);
    }

    // ----- Switch now -----

    [Fact]
    public async Task SwitchNow_GoesThroughTheSameSend_WithReasonManual_AndReturnsTheIdentifiersAndWhereEachStands()
    {
        OnA(Windows, "dir-win", 50);
        OnA(Mac, "dir-mac", 50);

        var result = await _service.SwitchNowAsync(Tenant, "  Account-A@Example.com ", null, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal((null, null, B), (result.RefusalCode, result.RefusalMessage, result.TargetAccountEmail));
        Assert.Equal(new[] { Mac, Windows }, result.Switches.Select(s => s.MachineName).OrderBy(n => n));
        Assert.All(result.Switches, s => Assert.Equal(LicenseLogStates.Switched, s.State));
        Assert.Equal(result.Switches.Select(s => s.SwitchId).OrderBy(i => i), _directors.Sent.Select(s => s.Request.SwitchId).OrderBy(i => i));
        Assert.All(_directors.Sent, s => Assert.Equal((LicenseContract.SwitchVerb, LicenseSwitchReasons.Manual), (s.Command.Verb, s.Request.Reason)));
        Assert.All(Rows(LicenseLogKinds.Switch), r => Assert.Equal(LicenseSwitchReasons.Manual, r.Reason));
        Assert.Empty(_store.ListActiveBars(Tenant, _now)); // Switch now bars nothing
        Assert.Equal((B, B), (ActiveOn(Windows), ActiveOn(Mac)));
    }

    [Fact]
    public async Task SwitchNow_Refused_ReturnsTheRefusal_WritesNothingAndSendsNothing()
    {
        OnA(Windows, "dir-win", 50);

        var result = await _service.SwitchNowAsync(Tenant, A, C, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(LicenseManualRefusals.TargetNotHeld, result.RefusalCode);
        Assert.Contains(C, result.RefusalMessage);
        Assert.Null(result.TargetAccountEmail);
        Assert.Empty(result.Switches);
        Assert.Empty(_directors.Sent);
        Assert.Empty(_store.ListLog(Tenant, LogStart));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task SwitchNow_WithNoSourceAccount_Throws(string? source)
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.SwitchNowAsync(Tenant, source!, null, CancellationToken.None));

    [Fact]
    public async Task SwitchNow_ToAnOfflineDirector_ReturnsPending_AndTheMinuteCheckSendsTheSameIdentifierAgain()
    {
        OnA(Windows, "dir-win", 50);
        _directors.Offline();

        var result = await _service.SwitchNowAsync(Tenant, A, B, CancellationToken.None);

        var pending = Assert.Single(result.Switches);
        Assert.Equal((LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline), (pending.State, pending.ReasonCode));

        MinuteLater();
        OnA(Windows, "dir-win", 50);
        _directors.AnswerWith(request => FakeDirectors.Ok(_directors.Result(request, LicenseSwitchOutcomes.Switched)));
        await Tick();

        Assert.Equal(new[] { pending.SwitchId, pending.SwitchId }, _directors.Sent.Select(s => s.Request.SwitchId));
        Assert.Equal(LicenseLogStates.Switched, TheSwitch().State);
    }

    [Fact]
    public async Task SwitchNow_Again_ReplacesTheSwitchStillWaitingForAnOfflineDirector_WithANewIdentifier()
    {
        OnA(Windows, "dir-win", 50);
        _directors.Offline();
        var first = Assert.Single((await _service.SwitchNowAsync(Tenant, A, B, CancellationToken.None)).Switches);

        var second = Assert.Single((await _service.SwitchNowAsync(Tenant, A, B, CancellationToken.None)).Switches);

        Assert.NotEqual(first.SwitchId, second.SwitchId);
        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(LicenseLogStates.Failed, rows.Single(r => r.SwitchId == first.SwitchId).State);
        Assert.Equal(LicenseLogStates.Pending, rows.Single(r => r.SwitchId == second.SwitchId).State);
    }

    [Fact]
    public async Task SwitchNow_WhileAnotherSwitchsOutcomeIsNotKnown_IsRefused_AndNamesTheMachineAsSkipped()
    {
        OnA(Windows, "dir-win", 97);
        _directors.TimesOut();
        await Tick();

        var result = await _service.SwitchNowAsync(Tenant, A, B, CancellationToken.None);

        Assert.Equal(LicenseManualRefusals.NoMachineToSwitch, result.RefusalCode);
        Assert.Equal(Windows, Assert.Single(result.Skipped).MachineName);
        Assert.Single(_directors.Sent);
    }

    // ----- tenants -----

    [Fact]
    public async Task Tick_ForOneTenant_NeverSendsToAnotherTenantsDirector_OrTouchesItsLog()
    {
        OnA(Windows, "dir-of-tenant-one", 97, Tenant);
        OnA(Windows, "dir-of-tenant-two", 97, Other);

        await Tick(Tenant);

        Assert.Equal("dir-of-tenant-one", Assert.Single(_directors.Sent).DirectorId);
        Assert.Empty(_store.ListLog(Other, LogStart));
        Assert.Equal(A, ActiveOn(Windows, Other));
        Assert.Empty(_store.ListActiveBars(Other, _now));
    }

    [Fact]
    public async Task Tick_ForATenantWithNoMachines_DoesNothing_NotEvenReadTheClock()
    {
        OnA(Windows, "dir-of-tenant-two", 97, Other); // another tenant's machines are not this tenant's

        await Tick(Tenant);

        Assert.Equal(0, _clockReads);
        Assert.Empty(_directors.Sent);
        Assert.Empty(_store.ListLog(Tenant, LogStart));
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
    public async Task TickEveryTenant_ChecksEachTenantInItsOwnScope_AndOneTenantsFailureDoesNotStopTheNext()
    {
        Reports(Tenant, Windows, "dir-of-tenant-one", (A, true, 98, 20), (B, false, 99, 30)); // no room: this tenant emails
        OnA(Windows, "dir-of-tenant-two", 97, Other);
        _mail.Throws = tenant => tenant == Tenant;

        await _service.TickEveryTenantAsync(new ListedTenants(Tenant, Other), CancellationToken.None);

        Assert.Equal("dir-of-tenant-two", Assert.Single(_directors.Sent).DirectorId);
        Assert.Equal(LicenseLogStates.Switched, Assert.Single(Rows(LicenseLogKinds.Switch, Other)).State);
    }

    [Fact]
    public async Task TickEveryTenant_WithNoTenantInScope_ChecksNothing()
    {
        OnA(Windows, "dir-win", 97);

        await _service.TickEveryTenantAsync(new ListedTenants((TenantId?)null), CancellationToken.None);

        Assert.Empty(_directors.Sent);
        Assert.Empty(_store.ListLog(Tenant, LogStart));
    }
}
