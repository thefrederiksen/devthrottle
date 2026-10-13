using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE OWNER EMAIL FOR A RENEWAL THAT KEEPS FAILING (License Switching, contract section 8.1), over the real license
/// store on a throwaway SQLite file with a mail sender that records what it is given. The clock is the test's own.
///
/// What these hold down: the email goes out when a sign-in's renewal has failed AND the sign-in ends within the
/// warning time; it is keyed on the failure's time being set, never on a reason word; it goes out once for each
/// sign-in for each failure streak, also across a restart of the Gateway; a send that failed is tried again, later
/// each time, and then never again once it has gone out; a streak that ends closes its row and the next failure is a
/// new one; and one DevThrottle account is never emailed about another's machine.
/// </summary>
public sealed class LicenseRenewalEmailServiceTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _sent = new();
    private Func<LicenseOwnerEmailResult> _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
    private LicenseRenewalEmailService _service;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseRenewalEmailServiceTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _service = NewService();
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    /// <summary>A service as a Gateway that has just started has it: nothing remembered but what is stored.</summary>
    private LicenseRenewalEmailService NewService()
        => new(_store, new LicenseRenewalFailureLog(_db), new LicenseReadModel(_store), Mail, () => _now);

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _sent.Add((tenant, email));
        return Task.FromResult(_mailAnswers());
    }

    private void Reports(TenantId tenant, string machine, params LicenseHolding[] holdings)
        => _store.ApplyReport(tenant, "dir-" + machine, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero);

    /// <summary>A sign-in whose last renewal failed, ending <paramref name="endsIn"/> from now.</summary>
    private LicenseHolding Failing(string account, TimeSpan? endsIn, bool active = true, string? error = LicenseRenewalErrors.ApproverOffline,
        string? detail = null, DateTime? failedAt = null)
    {
        var holding = Holding(account, active, readAt: _now);
        holding.SignInExpiresAtUtc = endsIn is { } span ? _now + span : null;
        holding.RenewalFailedAtUtc = failedAt ?? _now.AddMinutes(-5);
        holding.RenewalError = error;
        holding.RenewalErrorDetail = detail;
        return holding;
    }

    /// <summary>A sign-in whose last renewal worked, ending <paramref name="endsIn"/> from now.</summary>
    private LicenseHolding Renewed(string account, TimeSpan endsIn, bool active = true)
    {
        var holding = Holding(account, active, readAt: _now);
        holding.SignInExpiresAtUtc = _now + endsIn;
        return holding;
    }

    private Task<int> Tick(TenantId? tenant = null) => _service.TickAsync(tenant ?? One, CancellationToken.None);

    private List<LicenseSwitchLogEntity> Rows(TenantId? tenant = null)
        => _store.ListLog(tenant ?? One, LogStart).Where(r => r.Kind == LicenseRenewalFailureLog.Kind).OrderBy(r => r.CreatedAtUtc).ToList();

    private LicenseSwitchLogEntity TheRow() => Assert.Single(Rows());

    private static readonly TimeSpan TwoDays = TimeSpan.FromDays(2);

    // ----- when the email goes out -----

    [Fact]
    public async Task AFailedRenewal_OfASignInEndingWithinTheWarningTime_EmailsTheOwnerOnce_AndWritesAnOpenRow()
    {
        Reports(One, Mac, Failing(A, TwoDays));

        Assert.Equal(1, await Tick());

        var (tenant, email) = Assert.Single(_sent);
        Assert.Equal((One, A), (tenant, email.AccountEmail));
        var row = TheRow();
        Assert.Equal((LicenseRenewalFailureLog.Kind, Mac, "owner", A, "", LicenseLogStates.Open, LicenseGatewayReasonCodes.OwnerEmailed),
            (row.Kind, row.MachineName, row.OsUser, row.ToAccountEmail, row.FromAccountEmail, row.State, row.ReasonCode));
        Assert.Equal((T0, T0 + TwoDays, T0, 1), (row.CreatedAtUtc, row.NotAfterUtc, row.LastSentAtUtc, row.SendCount));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal("Renewing the sign-in for account-a@example.com on the-mac is failing, and the sign-in expires in 2 days; the owner was emailed.", row.Message);
    }

    [Fact]
    public async Task TheEmail_SaysWhichSignInWhyAndWhatToDo_InPlainSentences()
    {
        Reports(One, Mac, Failing(A, TimeSpan.FromHours(50), detail: "The Director on the-other-machine was not connected."));

        await Tick();

        var email = Assert.Single(_sent).Email;
        Assert.Equal("DevThrottle: the sign-in for account-a@example.com on the-mac expires in 2 days 2 hours and could not be renewed", email.Subject);
        Assert.Equal(string.Join("\r\n",
            "the-mac could not renew its sign-in for account-a@example.com, and that sign-in expires in 2 days 2 hours (at 2026-10-13 05:12 UTC).",
            "",
            "Why the last try failed: the machine that approves this account was not connected.",
            "The machine said: The Director on the-other-machine was not connected.",
            "",
            "the-mac is on this account now: its sessions stop when the sign-in ends.",
            "",
            "Sign the-mac in to account-a@example.com again, from the Licenses page of the Cockpit or on the machine itself.",
            "",
            "This is the only email for this failure. Another is sent only when a renewal has worked and a later one fails."), email.BodyText);
    }

    [Fact]
    public async Task TheEmail_ForASpareThatHasAlreadyExpired_SaysSo()
    {
        Reports(One, Mac, Renewed(B, TimeSpan.FromDays(20)), Failing(A, TimeSpan.FromHours(-3), active: false, error: null));

        Assert.Equal(1, await Tick());

        var email = Assert.Single(_sent).Email;
        Assert.Equal("DevThrottle: the sign-in for account-a@example.com on the-mac has expired and could not be renewed", email.Subject);
        Assert.Contains("and that sign-in expired 3 hours ago (at 2026-10-11 00:12 UTC).", email.BodyText);
        Assert.Contains("Why the last try failed: the machine gave no reason this Gateway knows.", email.BodyText);
        Assert.Contains("the-mac holds this account as a spare: it cannot be switched to it once the sign-in has ended.", email.BodyText);
        Assert.DoesNotContain("The machine said", email.BodyText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a-word-this-gateway-does-not-know")]
    [InlineData(LicenseRenewalErrors.LoginFailed)]
    public async Task TheEmail_IsKeyedOnTheFailureBeingSaid_WhateverReasonWordCameWithItOrNone(string? error)
    {
        Reports(One, Mac, Failing(A, TwoDays, error: error));

        Assert.Equal(1, await Tick());

        Assert.Single(_sent);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, TheRow().ReasonCode);
    }

    [Fact]
    public async Task AReasonWordWithNoFailureSaid_IsNotAFailedRenewal_AndEmailsNobody()
    {
        var holding = Renewed(A, TwoDays);
        holding.RenewalError = LicenseRenewalErrors.LoginFailed;
        Reports(One, Mac, holding);

        Assert.Equal(0, await Tick());

        Assert.Empty(_sent);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task ASignInEndingSoon_WhoseLastRenewalWorked_EmailsNobody()
    {
        Reports(One, Mac, Renewed(A, TimeSpan.FromHours(5)));

        Assert.Equal(0, await Tick());

        Assert.Empty(_sent);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task AFailedRenewal_OfASignInEndingLaterThanTheWarningTime_WaitsUntilItIsWithinIt()
    {
        var justOutside = LicenseContract.SignInExpiryWarning + TimeSpan.FromMinutes(1);
        Reports(One, Mac, Failing(A, justOutside));

        Assert.Equal(0, await Tick());
        Assert.Empty(_sent);
        Assert.Empty(Rows());

        // Two minutes on, nothing else changed: the sign-in now ends within the warning time.
        _now = T0.AddMinutes(2);
        Assert.Equal(1, await Tick());

        Assert.Single(_sent);
        Assert.Equal(T0.AddMinutes(2), TheRow().CreatedAtUtc);
    }

    [Fact]
    public async Task AFailedRenewal_OfASignInEndingExactlyAtTheWarningTime_IsEmailed()
    {
        Reports(One, Mac, Failing(A, LicenseContract.SignInExpiryWarning));

        Assert.Equal(1, await Tick());

        Assert.Single(_sent);
    }

    [Fact]
    public async Task AMachineThatHasGoneQuiet_IsStillEmailedAbout_WhenItsFailingSignInComesWithinTheWarningTime()
    {
        // Five days of sign-in left when the machine last reported, and its renewal was failing then.
        Reports(One, Mac, Failing(A, TimeSpan.FromDays(5)));
        Assert.Equal(0, await Tick());

        // Three days on with no report at all: the last thing the machine said still stands, and two days are left.
        _now = T0.AddDays(3);
        Assert.Equal(1, await Tick());

        Assert.Equal("DevThrottle: the sign-in for account-a@example.com on the-mac expires in 2 days and could not be renewed",
            Assert.Single(_sent).Email.Subject);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, TheRow().ReasonCode);
    }

    [Fact]
    public async Task AFailedRenewal_OfASignInWhoseEndTheMachineDidNotSay_EmailsNobody()
    {
        Reports(One, Mac, Failing(A, endsIn: null));

        Assert.Equal(0, await Tick());

        Assert.Empty(_sent);
        Assert.Empty(Rows());
    }

    // ----- once for each sign-in, for each failure streak -----

    [Fact]
    public async Task TheSameStreak_OnLaterChecks_IsNeverEmailedAgain_AlsoWhenALaterTryFailsToo()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        await Tick();

        for (var minute = 1; minute <= 5; minute++)
        {
            _now = T0.AddMinutes(minute);
            // The Director tried again and failed again: the failure's time moves, the streak does not end.
            Reports(One, Mac, Failing(A, TwoDays - TimeSpan.FromMinutes(minute), failedAt: _now, error: LicenseRenewalErrors.LoginFailed));
            Assert.Equal(0, await Tick());
        }

        Assert.Single(_sent);
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, 1), (TheRow().ReasonCode, TheRow().SendCount));
    }

    [Fact]
    public async Task AGatewayThatRestartsInTheMiddleOfAStreak_DoesNotEmailAgain()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        await Tick();

        _service = NewService();
        _now = T0.AddMinutes(1);
        Assert.Equal(0, await Tick());
        _now = T0.AddHours(30);
        Assert.Equal(0, await Tick());

        Assert.Single(_sent);
        Assert.Single(Rows());
    }

    [Fact]
    public async Task ARenewalThatWorks_EndsTheStreak_AndALaterFailureIsANewOneWithItsOwnEmail()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        await Tick();

        _now = T0.AddHours(1);
        Reports(One, Mac, Renewed(A, TimeSpan.FromHours(10)));
        Assert.Equal(0, await Tick());

        var ended = TheRow();
        Assert.Equal((LicenseLogStates.Ended, _now, LicenseGatewayReasonCodes.OwnerEmailed), (ended.State, ended.FinishedAtUtc, ended.ReasonCode));
        Assert.Equal("Renewing the sign-in for account-a@example.com on the-mac had been failing; a renewal has now worked.", ended.Message);

        _now = T0.AddHours(2);
        Reports(One, Mac, Failing(A, TimeSpan.FromHours(9)));
        Assert.Equal(1, await Tick());

        Assert.Equal(2, _sent.Count);
        Assert.Equal(new[] { LicenseLogStates.Ended, LicenseLogStates.Open }, Rows().Select(r => r.State));
    }

    [Fact]
    public async Task ASignInTheMachineNoLongerHolds_EndsItsStreak_AndTheRowSaysSo()
    {
        Reports(One, Mac, Renewed(B, TimeSpan.FromDays(20)), Failing(A, TwoDays, active: false));
        await Tick();

        _now = T0.AddHours(1);
        Reports(One, Mac, Renewed(B, TimeSpan.FromDays(20)));
        await Tick();

        var ended = TheRow();
        Assert.Equal(LicenseLogStates.Ended, ended.State);
        Assert.Equal("Renewing the sign-in for account-a@example.com on the-mac had been failing; the-mac no longer holds that sign-in.", ended.Message);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task EachFailingSignIn_GetsItsOwnEmail_OnePerMachineAndAccount()
    {
        Reports(One, Mac, Failing(A, TwoDays), Failing(B, TimeSpan.FromHours(4), active: false));
        Reports(One, "the-windows-machine", Failing(A, TimeSpan.FromHours(30)), Renewed(C, TimeSpan.FromDays(20), active: false));

        Assert.Equal(3, await Tick());
        _now = T0.AddMinutes(1);
        Assert.Equal(0, await Tick());

        Assert.Equal(3, _sent.Count);
        Assert.Equal(new[] { (Mac, A), (Mac, B), ("the-windows-machine", A) },
            Rows().Select(r => (r.MachineName, r.ToAccountEmail)).OrderBy(r => r.MachineName).ThenBy(r => r.ToAccountEmail));
        Assert.All(Rows(), row => Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, row.ReasonCode));
        Assert.Contains(_sent, s => s.Email.Subject.Contains("for account-a@example.com on the-windows-machine "));
    }

    // ----- a send that does not go out -----

    [Fact]
    public async Task ASendThatFails_IsTriedAgainLaterEachTime_AndOnceItHasGoneOutNeverAgain()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");

        Assert.Equal(1, await Tick());
        var row = TheRow();
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailFailed, 0), (row.ReasonCode, row.SendCount));
        Assert.Null(row.LastSentAtUtc);
        Assert.Equal("Renewing the sign-in for account-a@example.com on the-mac is failing, and the sign-in expires in 2 days; the owner has not been emailed yet because the DevThrottle account service could not be reached.", row.Message);

        // Not before one minute has passed; then not before two more; then four.
        _now = T0.AddSeconds(59);
        Assert.Equal(0, await Tick());
        _now = T0.AddMinutes(1);
        Assert.Equal(1, await Tick());
        _now = T0.AddMinutes(2).AddSeconds(59);
        Assert.Equal(0, await Tick());
        _now = T0.AddMinutes(3);
        Assert.Equal(1, await Tick());
        _now = T0.AddMinutes(6).AddSeconds(59);
        Assert.Equal(0, await Tick());
        Assert.Equal(3, _sent.Count);

        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now = T0.AddMinutes(7);
        Assert.Equal(1, await Tick());
        _now = T0.AddMinutes(8);
        Assert.Equal(0, await Tick());
        _now = T0.AddHours(5);
        Assert.Equal(0, await Tick());

        Assert.Equal(4, _sent.Count);
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, 1, T0.AddMinutes(7)), (TheRow().ReasonCode, TheRow().SendCount, TheRow().LastSentAtUtc));
        Assert.Single(Rows());
    }

    [Fact]
    public async Task ASendTheMailServiceAsksToWaitFor_IsNotTriedBeforeThat()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the mail service is busy", TimeSpan.FromMinutes(30));

        await Tick();
        _now = T0.AddMinutes(29);
        Assert.Equal(0, await Tick());
        _now = T0.AddMinutes(30);
        Assert.Equal(1, await Tick());

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task ASendThatFailed_IsTriedAgainByAGatewayThatRestarted_AndTheTextIsOfThatChecksFacts()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        await Tick();

        _service = NewService();
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now = T0.AddHours(24);
        Assert.Equal(1, await Tick());

        Assert.Equal(2, _sent.Count);
        Assert.Contains("expires in 1 day ", _sent[1].Email.Subject);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, TheRow().ReasonCode);
        Assert.Single(Rows());
    }

    [Fact]
    public async Task AGatewayWithNoAccountToEmail_RecordsThat_AndNeverTriesAgain()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.NoAccount, "this self-hosted Gateway has no DevThrottle account to email");

        Assert.Equal(1, await Tick());
        _now = T0.AddHours(3);
        Assert.Equal(0, await Tick());

        Assert.Single(_sent);
        var row = TheRow();
        Assert.Equal((LicenseGatewayReasonCodes.NoOwnerAccount, 0, LicenseLogStates.Open), (row.ReasonCode, row.SendCount, row.State));
        Assert.EndsWith("; no owner email was sent because this self-hosted Gateway has no DevThrottle account to email.", row.Message);
    }

    // ----- one account never hears of another's -----

    [Fact]
    public async Task OneAccountsFailingSignIn_EmailsThatAccountsOwnerOnly_AndWritesOnlyItsLog()
    {
        Reports(One, Mac, Renewed(A, TimeSpan.FromDays(20)));
        Reports(Two, Mac, Failing(A, TwoDays));

        Assert.Equal(0, await Tick(One));
        Assert.Equal(1, await Tick(Two));
        Assert.Equal(0, await Tick(One));

        Assert.Equal(Two, Assert.Single(_sent).Tenant);
        Assert.Empty(Rows(One));
        Assert.Single(Rows(Two));
    }

    [Fact]
    public async Task AnAccountNoDirectorHasReportedFor_IsLeftAlone()
    {
        Assert.Equal(0, await Tick());

        Assert.Empty(_sent);
        Assert.Empty(Rows());
    }

    // ----- the row in the log, and the rows the switch code reads -----

    [Fact]
    public async Task TheRow_IsAnsweredByTheLogRoute_InWords_OpenWhileTheStreakLasts()
    {
        Reports(One, Mac, Failing(A, TwoDays));
        await Tick();

        var open = Assert.Single(LogEntries().EnumerateArray().ToList());

        Assert.Equal(("renewal-failed", "Renewal failing", "open", "Failing", "bad", true, A),
            (open.GetProperty("kind").GetString(), open.GetProperty("kindText").GetString(), open.GetProperty("state").GetString(),
             open.GetProperty("stateText").GetString(), open.GetProperty("stateTone").GetString(), open.GetProperty("isOpen").GetBoolean(),
             open.GetProperty("toAccountEmail").GetString()));
        Assert.Equal(TheRow().Message, open.GetProperty("summary").GetString());
        Assert.Equal(Mac, open.GetProperty("label").GetString());

        _now = T0.AddHours(1);
        Reports(One, Mac, Renewed(A, TimeSpan.FromDays(20)));
        await Tick();

        var ended = Assert.Single(LogEntries().EnumerateArray().ToList());
        Assert.Equal(("ended", "Over", "neutral", false), (ended.GetProperty("state").GetString(), ended.GetProperty("stateText").GetString(),
            ended.GetProperty("stateTone").GetString(), ended.GetProperty("isOpen").GetBoolean()));
    }

    [Fact]
    public async Task AWarningRow_IsNeverAnOpenSwitchOrANoRoomEvent_SoItHoldsNoMachineBack()
    {
        Reports(One, Mac, Failing(A, TwoDays), Renewed(B, TimeSpan.FromDays(20), active: false));
        await Tick();

        Assert.Empty(_store.ListOpenSwitches(One));
        Assert.Empty(_store.ListOpenNoRoom(One));
        var machine = new LicenseReadModel(_store).Read(One, _now).Machines.Single();
        Assert.Null(machine.SwitchInProgressId);
        Assert.NotNull(_store.CreatePendingSwitch(One, machine.MachineId, A, B, LicenseSwitchReasons.Manual, _now));
    }

    [Fact]
    public void AnEmailState_ThatIsNotOne_IsRefusedByTheLog()
    {
        var log = new LicenseRenewalFailureLog(_db);

        Assert.Throws<ArgumentException>(() => log.RecordEmail(One, Guid.NewGuid(), "sent", "x", _now));
        Assert.Throws<ArgumentException>(() => log.RecordEmail(One, Guid.NewGuid(), LicenseGatewayReasonCodes.OwnerEmailed, "x", null));
        Assert.Throws<ArgumentException>(() => log.RecordEmail(One, Guid.NewGuid(), LicenseGatewayReasonCodes.OwnerEmailFailed, "x", _now));
    }

    private JsonElement LogEntries()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        LicenseEndpoints.Log(ctx, _ => One, _store, () => _now).ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.Equal(200, ctx.Response.StatusCode);
        return JsonDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray())).RootElement.GetProperty("entries").Clone();
    }
}
