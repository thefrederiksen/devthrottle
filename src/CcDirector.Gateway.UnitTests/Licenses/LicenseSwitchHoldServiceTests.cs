using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A SWITCH THAT KEEPS FAILING, through the minute check and Switch now (License Switching; the review of phases 2
/// and 4 as a whole, finding 1), over the real license store on a throwaway SQLite file with a fake Director that
/// refuses every switch and a fake mail service. The clock is the test's own: a "minute later" is an assignment,
/// never a wait.
///
/// What these hold down: a switch that fails the same way is not sent every minute - after the second failure in a
/// row the machine is left out for one minute, then two, four, up to an hour; the hold is on the failed row, so a
/// Gateway that restarts keeps it; when the only machines of a tired account are held the owner is told ONCE, by the
/// event the "no account has room" case uses; a machine that is not held is still switched; and Switch now ignores
/// the hold.
/// </summary>
public sealed class LicenseSwitchHoldServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";
    private const string Restored = LicenseSwitchReasonCodes.ExchangeFailedRestored;

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly List<(string DirectorId, LicenseSwitchRequest Request, DateTime At)> _sent = new();
    private readonly List<LicenseOwnerEmail> _mailed = new();
    private LicenseSwitchService _service;
    private Func<string, LicenseSwitchRequest, DirectorCommandResult?> _answer;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseSwitchHoldServiceTests()
    {
        _store = new LicenseStore(_harness.Open());
        _answer = (_, request) => Refuses(request, Restored);
        _service = NewService();
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    /// <summary>A service as a Gateway that has just started has it: nothing remembered but what is stored.</summary>
    private LicenseSwitchService NewService()
        => new(_store, new LicenseSnapshotSource(_store, new LicenseReadModel(_store)), Send, Mail, () => _now);

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)
            ?? throw new InvalidOperationException("The command carried no switch request.");
        lock (_sent) _sent.Add((directorId, request, _now));
        return Task.FromResult(_answer(directorId, request));
    }

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _mailed.Add(email);
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private DirectorCommandResult Refuses(LicenseSwitchRequest request, string reasonCode)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Failed,
            ReasonCode = reasonCode,
            Message = "The Director refused.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.ExpectedActiveAccountEmail,
            StartedAtUtc = _now,
            FinishedAtUtc = _now,
        }, Web));

    private DirectorCommandResult Switches(LicenseSwitchRequest request)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            Message = "The Director switched.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.TargetAccountEmail,
            VerifiedByUsageRead = true,
            StartedAtUtc = _now,
            FinishedAtUtc = _now,
        }, Web));

    /// <summary>The machine reports now: on account A, whose WEEKLY limit is at 98% (it resets in four days), holding
    /// a spare for B, which has room. Every machine reports before each check, as a live one does every two minutes.</summary>
    private void OnTiredA(string machine)
        => _store.ApplyReport(Tenant, "dir-" + machine, ++_sequence,
            Report(machine, _now, Holding(A, true, 20, 98, _now), Holding(B, false, 10, 30, _now)), _now, TimeSpan.Zero);

    /// <summary>One minute check, <paramref name="minute"/> minutes after the start, the machines having just reported.</summary>
    private async Task CheckAt(int minute, params string[] machines)
    {
        _now = T0.AddMinutes(minute);
        foreach (var machine in machines.Length == 0 ? [Windows] : machines)
            OnTiredA(machine);
        await _service.TickAsync(Tenant, CancellationToken.None);
    }

    private List<int> MinutesASwitchWasSent(string director = "dir-" + Windows)
        => _sent.Where(s => s.DirectorId == director).Select(s => (int)(s.At - T0).TotalMinutes).ToList();

    private List<LicenseSwitchLogEntity> Rows(string kind)
        => _store.ListLog(Tenant, LogStart).Where(r => r.Kind == kind).OrderBy(r => r.CreatedAtUtc).ToList();

    // ----- the minute check -----

    [Fact]
    public async Task TickAsync_ASwitchThatKeepsFailingTheSameWay_IsSentLaterEachTime_AndNeverMoreThanAnHourApart()
    {
        for (var minute = 0; minute <= 185; minute++)
            await CheckAt(minute);

        // Sent on the first two checks, then held one minute, two, four ... thirty-two, then an hour at a time. Before
        // the hold existed this was one switch, and one failed row, every minute: 186 of them.
        Assert.Equal(new[] { 0, 1, 2, 4, 8, 16, 32, 64, 124, 184 }, MinutesASwitchWasSent());
        var rows = Rows(LicenseLogKinds.Switch);
        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal((LicenseLogStates.Failed, Restored), (row.State, row.ReasonCode)));
        // Each hold is on the failed row it follows: none on the first, then until the minute the next one was sent.
        Assert.Equal(new DateTime?[] { null }.Concat(new[] { 2, 4, 8, 16, 32, 64, 124, 184, 244 }.Select(m => (DateTime?)T0.AddMinutes(m))),
            rows.Select(r => r.HeldUntilUtc));
    }

    [Fact]
    public async Task TickAsync_EveryMachineOfTheTiredAccountHeld_TellsTheOwnerOnce_ByTheEventOfTheNoRoomCase()
    {
        for (var minute = 0; minute <= 70; minute++)
            await CheckAt(minute);

        // Minute 3 is the first check that finds the only machine held: the event opens there and its email goes.
        var email = Assert.Single(_mailed);
        Assert.Equal("DevThrottle: the switch away from account-a@example.com keeps failing - it is at 98% of its weekly limit", email.Subject);
        Assert.Contains("the-windows-machine: its switch from account-a@example.com to account-b@example.com has failed more than once in a row "
                        + "(the exchange failed and was undone; the machine is on the account it was on), so the automatic switch leaves it out until 2026-10-11 03:16 UTC.", email.BodyText);
        var row = Assert.Single(Rows(LicenseLogKinds.NoRoom));
        Assert.Equal((A, LicenseLogStates.Open, LicenseGatewayReasonCodes.OwnerEmailed, T0.AddMinutes(3), 1),
            (row.FromAccountEmail, row.State, row.ReasonCode, row.CreatedAtUtc, row.SendCount));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal("The switch of the machines on account-a@example.com keeps failing and is tried again later each time, so nothing is switched for now; the owner was emailed.", row.Message);
    }

    [Fact]
    public async Task TickAsync_TheSwitchThatWorksAtLast_ClosesTheEvent_AndALaterRunOfFailuresIsANewEventWithItsOwnEmail()
    {
        for (var minute = 0; minute <= 3; minute++)
            await CheckAt(minute);
        Assert.Single(_mailed);

        // At minute 4 the hold has passed and the Director switches: the machine is on B, and nothing is on A.
        _answer = (_, request) => Switches(request);
        _now = T0.AddMinutes(4);
        OnTiredA(Windows);
        await _service.TickAsync(Tenant, CancellationToken.None);
        _now = T0.AddMinutes(5);
        _store.ApplyReport(Tenant, "dir-" + Windows, ++_sequence,
            Report(Windows, _now, Holding(B, true, 10, 30, _now), Holding(A, false, 20, 98, _now)), _now, TimeSpan.Zero);
        await _service.TickAsync(Tenant, CancellationToken.None);

        Assert.Equal(LicenseLogStates.Ended, Assert.Single(Rows(LicenseLogKinds.NoRoom)).State);
        Assert.Single(_mailed);
    }

    [Fact]
    public async Task TickAsync_AGatewayThatRestartsWhileAMachineIsHeld_KeepsTheHold()
    {
        for (var minute = 0; minute <= 4; minute++)
            await CheckAt(minute);
        Assert.Equal(new[] { 0, 1, 2, 4 }, MinutesASwitchWasSent());   // held until minute 8

        _service = NewService();
        for (var minute = 5; minute <= 8; minute++)
            await CheckAt(minute);

        Assert.Equal(new[] { 0, 1, 2, 4, 8 }, MinutesASwitchWasSent());
    }

    [Fact]
    public async Task TickAsync_OneMachineHeld_AndAnotherThatIsNot_SwitchesTheOther_AndTellsNobody()
    {
        for (var minute = 0; minute <= 2; minute++)
            await CheckAt(minute);                                    // the Windows machine is held until minute 4
        _answer = (director, request) => director == "dir-" + Mac ? Switches(request) : Refuses(request, Restored);

        // The Mac comes onto the same tired account.
        await CheckAt(3, Windows, Mac);

        Assert.Equal(new[] { 3 }, MinutesASwitchWasSent("dir-" + Mac));
        Assert.Equal(new[] { 0, 1, 2 }, MinutesASwitchWasSent());
        Assert.Empty(_mailed);
        Assert.Empty(Rows(LicenseLogKinds.NoRoom));
    }

    [Fact]
    public async Task TickAsync_AFailureWithAnotherReason_StartsTheCountAgain()
    {
        await CheckAt(0);
        await CheckAt(1);                                             // the second same failure: held until minute 2
        _answer = (_, request) => Refuses(request, LicenseSwitchReasonCodes.VerifyFailed);
        await CheckAt(2);                                             // another reason: the first of a new run, no hold
        await CheckAt(3);                                             // sent at once; the second of the new run: held until 4
        await CheckAt(4);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, MinutesASwitchWasSent());
        Assert.Equal(new DateTime?[] { null, T0.AddMinutes(2), null, T0.AddMinutes(4), T0.AddMinutes(6) },
            Rows(LicenseLogKinds.Switch).Select(r => r.HeldUntilUtc));
    }

    [Fact]
    public async Task TickAsync_ADirectorThatAnswersWithAnErrorEveryTime_IsHeldTheSameWay()
    {
        _answer = (_, _) => DirectorCommandResult.Fail(DirectorCommandStatus.Error, "The Director could not run the switch.");

        for (var minute = 0; minute <= 9; minute++)
            await CheckAt(minute);

        Assert.Equal(new[] { 0, 1, 2, 4, 8 }, MinutesASwitchWasSent());
        Assert.All(Rows(LicenseLogKinds.Switch), row => Assert.Equal(LicenseGatewayReasonCodes.DirectorError, row.ReasonCode));
    }

    [Fact]
    public async Task TickAsync_ADirectorThatIsNotConnected_IsNeverHeld_BecauseNothingFailed()
    {
        _answer = (_, _) => null;   // not connected: nothing is sent, the row waits

        for (var minute = 0; minute <= 25; minute++)
            await CheckAt(minute);

        Assert.All(Rows(LicenseLogKinds.Switch), row => Assert.Null(row.HeldUntilUtc));
        Assert.Empty(_store.ListHolds(Tenant, _now));
        // The same switch is offered every minute until it expires, and a new one is written after that, as before.
        Assert.Equal(26, _sent.Count);
    }

    // ----- Switch now -----

    [Fact]
    public async Task SwitchNowAsync_AHeldMachine_IsSentTheSwitchAtOnce_AndItsFailureIsOneMoreOfTheRun()
    {
        for (var minute = 0; minute <= 4; minute++)
            await CheckAt(minute);                                    // four failures; held until minute 8
        _now = T0.AddMinutes(5);
        OnTiredA(Windows);

        var result = await _service.SwitchNowAsync(Tenant, A, null, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(new[] { 0, 1, 2, 4, 5 }, MinutesASwitchWasSent());
        var byHand = Rows(LicenseLogKinds.Switch).Last();
        Assert.Equal((LicenseSwitchReasons.Manual, LicenseLogStates.Failed, Restored), (byHand.Reason, byHand.State, byHand.ReasonCode));
        // The fifth failure in a row: held eight minutes from minute 5, and the automatic switch waits for that.
        Assert.Equal(T0.AddMinutes(13), byHand.HeldUntilUtc);
        for (var minute = 6; minute <= 13; minute++)
            await CheckAt(minute);
        Assert.Equal(new[] { 0, 1, 2, 4, 5, 13 }, MinutesASwitchWasSent());
    }

    [Fact]
    public async Task SwitchNowAsync_AHeldMachineWhoseSwitchNowWorks_IsOnTheTarget()
    {
        for (var minute = 0; minute <= 2; minute++)
            await CheckAt(minute);                                    // held until minute 4
        _answer = (_, request) => Switches(request);
        _now = T0.AddMinutes(3);
        OnTiredA(Windows);

        var result = await _service.SwitchNowAsync(Tenant, A, B, CancellationToken.None);

        Assert.Equal((true, B, LicenseLogStates.Switched), (result.Accepted, result.TargetAccountEmail, Assert.Single(result.Switches).State));
        Assert.Empty(_store.ListHolds(Tenant, _now));
    }
}
