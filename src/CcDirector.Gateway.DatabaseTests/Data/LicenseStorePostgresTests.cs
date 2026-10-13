using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The license store's statements, run against a REAL PostgreSQL - the provider the hosted Gateway uses. The unit
/// tests prove the behaviour over SQLite; this proves that every statement the once-a-minute check sends translates
/// and answers the same on PostgreSQL: a switch walked from its pending row to its settled answer and read-back, and
/// a no-room event walked from open to ended. If one of these failed on PostgreSQL the check would fail every minute
/// for every account in production, and nothing would switch.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class LicenseStorePostgresTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 3, 12, 0, DateTimeKind.Utc);

    /// <summary>Open the Gateway database on this run's own Postgres database, through the runtime constructor. The
    /// provider is chosen by a process-global variable, so it is set and put back while no other test is opening a
    /// database.</summary>
    private static GatewayDatabase OpenPostgres()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        GatewayDatabase? db = null;
        GatewayDbEnvironmentGate.WhileNobodyIsOpeningADatabase(() =>
        {
            var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, PostgresProofDatabase.Connection);
            try { db = new GatewayDatabase(new SingleTenantContext()); }
            finally { Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous); }
        });
        return db!;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private static LicenseHolding Holding(string email, bool active, DateTime readAt) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        RateLimitTier = "default_claude_max_20x",
        FiveHour = new LicenseWindow { Utilization = active ? 97 : 10, ResetsAtUtc = T0.AddHours(2) },
        SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = T0.AddDays(4) },
        ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
        ReadAtUtc = readAt,
    };

    private static LicenseUsageReport Report(string machine, string activeEmail, string spareEmail, DateTime at) => new()
    {
        MachineName = machine,
        OsUser = "owner",
        SentAtUtc = at,
        Trigger = LicenseReportTriggers.Interval,
        Holdings = [Holding(activeEmail, true, at), Holding(spareEmail, false, at)],
    };

    [RequiresPostgresFact]
    public void A_switch_walks_from_its_pending_row_to_its_settled_answer_and_read_back()
    {
        using var db = OpenPostgres();
        var store = new LicenseStore(db);
        var tenant = TenantId.Local;
        var machine = Unique("machine");
        var director = Unique("dir");
        var a = Unique("account-a") + "@example.com";
        var b = Unique("account-b") + "@example.com";

        var machineId = store.ApplyReport(tenant, director, 1, Report(machine, a, b, T0), T0, TimeSpan.Zero).MachineId;
        Assert.True(store.HasMachines(tenant));

        // The pending row, and no second open row for the same machine.
        var row = store.CreatePendingSwitch(tenant, machineId, a, b, LicenseSwitchReasons.Threshold, T0.AddSeconds(5), "Account A reached 97%.");
        Assert.NotNull(row);
        var switchId = Assert.IsType<string>(row.SwitchId);
        Assert.Null(store.CreatePendingSwitch(tenant, machineId, a, b, LicenseSwitchReasons.Threshold, T0.AddSeconds(6)));
        Assert.Contains(store.ListOpenSwitches(tenant), r => r.SwitchId == switchId);

        // Not connected, then sent, then no answer in time.
        Assert.True(store.TryMark(tenant, switchId, LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline, "The Director is not connected.", T0.AddSeconds(7)));
        Assert.True(store.TryMark(tenant, switchId, LicenseLogStates.Pending, null, "Being sent again.", T0.AddSeconds(60)));
        Assert.True(store.RecordSend(tenant, switchId, director, T0.AddSeconds(60)));
        Assert.True(store.TryMark(tenant, switchId, LicenseLogStates.Unknown, null, "No answer in 60 seconds.", T0.AddSeconds(120)));

        // The Director's answer settles it, once.
        var answer = new LicenseSwitchResult
        {
            SwitchId = switchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            Message = "The Director switched.",
            PreviousActiveAccountEmail = a,
            ActiveAccountEmailAfter = b,
            VerifiedByUsageRead = true,
            StartedAtUtc = T0.AddSeconds(61),
            FinishedAtUtc = T0.AddSeconds(62),
            ExchangeMilliseconds = 41,
        };
        Assert.True(store.TrySettle(tenant, answer, T0.AddSeconds(125)));
        Assert.False(store.TrySettle(tenant, answer, T0.AddSeconds(126)));
        Assert.False(store.TryMark(tenant, switchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "Too late.", T0.AddSeconds(127)));
        var settled = store.FindSwitch(tenant, switchId);
        Assert.NotNull(settled);
        Assert.Equal((LicenseLogStates.Switched, 1), (settled.State, settled.SendCount));
        Assert.Contains(store.ListLog(tenant, T0.AddHours(-1)), r => r.SwitchId == switchId);
        Assert.DoesNotContain(store.ListOpenSwitches(tenant), r => r.SwitchId == switchId);

        // The read-back: taken when the machine has not reported since the command was sent; a replayed one is never
        // taken once the machine has reported after the row was written.
        Assert.True(store.ApplySwitchReadBack(tenant, machineId, answer, T0.AddSeconds(60)));
        var view = Assert.Single(new LicenseReadModel(store).Read(tenant, T0.AddSeconds(130)).Machines, m => m.MachineId == machineId);
        Assert.Equal(b, view.ActiveAccountEmail);
        Assert.Equal(b, Assert.Single(view.Holdings, h => h.IsActive).AccountEmail);

        store.ApplyReport(tenant, director, 2, Report(machine, a, b, T0.AddSeconds(200)), T0.AddSeconds(200), TimeSpan.Zero);
        var replayed = new LicenseSwitchResult
        {
            SwitchId = switchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            PreviousActiveAccountEmail = a,
            ActiveAccountEmailAfter = b,
            StartedAtUtc = T0.AddSeconds(259),
            FinishedAtUtc = T0.AddSeconds(260),
            Replayed = true,
        };
        Assert.False(store.ApplySwitchReadBack(tenant, machineId, replayed, T0.AddSeconds(258)));
        Assert.False(store.ApplySwitchReadBack(tenant, machineId, answer, T0.AddSeconds(60)));

        // A bar, written and read.
        store.Bar(tenant, a, LicenseWindows.FiveHour, T0.AddHours(2), T0.AddSeconds(5));
        Assert.Contains(store.ListActiveBars(tenant, T0.AddMinutes(1)), bar => bar.AccountEmail == a && bar.Window == LicenseWindows.FiveHour);
        Assert.DoesNotContain(store.ListActiveBars(tenant, T0.AddHours(3)), bar => bar.AccountEmail == a);
    }

    [RequiresPostgresFact]
    public void A_failed_renewal_warning_is_opened_once_emailed_once_closed_and_read_back()
    {
        using var db = OpenPostgres();
        var store = new LicenseStore(db);
        var log = new LicenseRenewalFailureLog(db);
        var tenant = TenantId.Local;
        var name = Unique("machine");
        var a = Unique("account-a") + "@example.com";
        var b = Unique("account-b") + "@example.com";
        var machineId = store.ApplyReport(tenant, Unique("dir"), 1, Report(name, a, b, T0), T0, TimeSpan.Zero).MachineId;
        var machine = Assert.Single(new LicenseReadModel(store).Read(tenant, T0.AddSeconds(1)).Machines, m => m.MachineId == machineId);

        // One open warning for one sign-in: opening it again is the same row. Another sign-in has its own.
        var opened = log.Open(tenant, machine, a.ToUpperInvariant(), T0.AddHours(20), T0.AddDays(-28), "The sign-in could not be renewed.", T0);
        var again = log.Open(tenant, machine, a, T0.AddHours(21), T0.AddDays(-1), "Another message.", T0.AddMinutes(1));
        var other = log.Open(tenant, machine, b, T0.AddHours(22), null, "The sign-in could not be renewed.", T0.AddMinutes(1));
        // When the sign-in was made, as it was said when the row was opened, is kept and read back; none is none.
        Assert.Equal(T0.AddDays(-28), LicenseRenewalFailureLog.SignedInWhenOpened(Assert.Single(log.ListOpen(tenant), r => r.Id == opened.Id)));
        Assert.Null(LicenseRenewalFailureLog.SignedInWhenOpened(Assert.Single(log.ListOpen(tenant), r => r.Id == other.Id)));
        Assert.Equal(opened.Id, again.Id);
        Assert.NotEqual(opened.Id, other.Id);
        Assert.Equal((LicenseRenewalFailureLog.Kind, LicenseLogStates.Open, a, T0.AddHours(20)),
            (opened.Kind, opened.State, opened.ToAccountEmail, opened.NotAfterUtc));
        Assert.Contains(log.ListOpen(tenant), r => r.Id == opened.Id);

        // The owner email: a failed try is recorded and can be replaced; a sent one is never changed again.
        Assert.True(log.RecordEmail(tenant, opened.Id, LicenseGatewayReasonCodes.OwnerEmailFailed, "The mail service refused it.", null));
        Assert.True(log.RecordEmail(tenant, opened.Id, LicenseGatewayReasonCodes.OwnerEmailed, "The owner was emailed.", T0.AddMinutes(2)));
        Assert.False(log.RecordEmail(tenant, opened.Id, LicenseGatewayReasonCodes.OwnerEmailFailed, "Too late.", null));
        Assert.False(log.RecordEmail(tenant, opened.Id, LicenseGatewayReasonCodes.OwnerEmailed, "A second email.", T0.AddMinutes(3)));
        var emailed = Assert.Single(log.ListOpen(tenant), r => r.Id == opened.Id);
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, T0.AddMinutes(2), 1, "The owner was emailed."),
            (emailed.ReasonCode, emailed.LastSentAtUtc, emailed.SendCount, emailed.Message));

        // It ends, once; what became of the email stays on the row; the other sign-in's warning is still open.
        Assert.True(log.Close(tenant, opened.Id, "The sign-in was renewed.", T0.AddHours(1)));
        Assert.False(log.Close(tenant, opened.Id, "Again.", T0.AddHours(2)));
        Assert.False(log.RecordEmail(tenant, opened.Id, LicenseGatewayReasonCodes.NoOwnerAccount, "Too late.", null));
        Assert.DoesNotContain(log.ListOpen(tenant), r => r.Id == opened.Id);
        Assert.Contains(log.ListOpen(tenant), r => r.Id == other.Id);
        var ended = Assert.Single(store.ListLog(tenant, T0.AddHours(-1)), r => r.Id == opened.Id);
        Assert.Equal((LicenseLogStates.Ended, T0.AddHours(1), LicenseGatewayReasonCodes.OwnerEmailed, "The sign-in was renewed."),
            (ended.State, ended.FinishedAtUtc, ended.ReasonCode, ended.Message));

        // A streak that starts after this one ended is a new row.
        Assert.NotEqual(opened.Id, log.Open(tenant, machine, a, T0.AddHours(30), null, "The sign-in could not be renewed.", T0.AddHours(3)).Id);
    }

    [RequiresPostgresFact]
    public void A_no_room_event_walks_from_open_to_ended()
    {
        using var db = OpenPostgres();
        var store = new LicenseStore(db);
        var tenant = TenantId.Local;
        var account = Unique("account-a") + "@example.com";

        // One open event per account: opening it again is the same row.
        var opened = store.OpenNoRoom(tenant, account, T0, T0.AddHours(2));
        var again = store.OpenNoRoom(tenant, account, T0.AddMinutes(1), T0.AddHours(3));
        Assert.Equal(opened.Id, again.Id);
        Assert.Equal(LicenseLogKinds.NoRoom, opened.Kind);
        Assert.Contains(store.ListOpenNoRoom(tenant), r => r.Id == opened.Id);

        // Its end moves.
        Assert.True(store.ExtendNoRoom(tenant, account, T0.AddDays(4)));
        Assert.Equal(T0.AddDays(4), Assert.Single(store.ListOpenNoRoom(tenant), r => r.Id == opened.Id).NotAfterUtc);

        // The owner email: a failed try is recorded and can be replaced; a sent one is never changed again.
        Assert.True(store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailFailed,
            LicenseStore.NoRoomMessage(account, "the owner has not been emailed yet because the mail service refused it"), null));
        Assert.True(store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailed,
            LicenseStore.NoRoomMessage(account, "the owner was emailed"), T0.AddMinutes(2)));
        Assert.False(store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailFailed,
            LicenseStore.NoRoomMessage(account, "the owner has not been emailed yet"), null));
        var emailed = Assert.Single(store.ListOpenNoRoom(tenant), r => r.Id == opened.Id);
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, T0.AddMinutes(2)), (emailed.ReasonCode, emailed.LastSentAtUtc));

        // It ends, once, and stays in the log.
        Assert.True(store.CloseNoRoom(tenant, account, T0.AddHours(1)));
        Assert.False(store.CloseNoRoom(tenant, account, T0.AddHours(1)));
        Assert.DoesNotContain(store.ListOpenNoRoom(tenant), r => r.Id == opened.Id);
        var ended = Assert.Single(store.ListLog(tenant, T0.AddHours(-1)), r => r.Id == opened.Id);
        Assert.Equal((LicenseLogStates.Ended, T0.AddHours(1)), (ended.State, ended.FinishedAtUtc));
        Assert.False(store.ExtendNoRoom(tenant, account, T0.AddDays(5)));
    }

    [RequiresPostgresFact]
    public void A_sign_in_row_is_written_sent_left_unanswered_ended_and_read_back()
    {
        // The three statements of LicenseSignInLog that change a row (the send, the move to unknown, the end) and its
        // two reads, on PostgreSQL. A sign-in started from the Cockpit writes these on every start and every answer.
        using var db = OpenPostgres();
        var store = new LicenseStore(db);
        var log = new LicenseSignInLog(db);
        var tenant = TenantId.Local;
        var machineName = Unique("machine");
        var director = Unique("dir");
        var a = Unique("account-a") + "@example.com";
        var b = Unique("account-b") + "@example.com";
        var c = Unique("account-c") + "@example.com";
        var machineId = store.ApplyReport(tenant, director, 1, Report(machineName, a, b, T0), T0, TimeSpan.Zero).MachineId;
        var machine = new LicenseReadModel(store).Read(tenant, T0).Machines.Single(m => m.MachineId == machineId);
        var signedInBefore = T0.AddDays(-20).AddTicks(1230);

        var row = log.CreatePending(tenant, machine, c.ToUpperInvariant(), LicenseSignInReasons.Renewal, "Asked from the Licenses page.",
            T0.AddSeconds(5), signedInBefore);

        Assert.Matches(LicenseContract.SignInIdShape, row.SwitchId!);
        var written = log.Find(tenant, row.SwitchId!)!;
        Assert.Equal((LicenseSignInLog.Kind, machineId, machineName, "owner", director, "", c, LicenseSignInReasons.Renewal, LicenseLogStates.Pending),
            (written.Kind, written.MachineId, written.MachineName, written.OsUser, written.DirectorId, written.FromAccountEmail,
             written.ToAccountEmail, written.Reason, written.State));
        Assert.Equal((T0.AddSeconds(5), T0.AddSeconds(5) + LicenseContract.SignInNotAfter, 0), (written.CreatedAtUtc, written.NotAfterUtc, written.SendCount));
        // The sign-in time from before the ask comes back as it was stored, to PostgreSQL's own precision.
        Assert.True((LicenseSignInLog.SignedInBefore(written)!.Value - signedInBefore).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Contains(log.ListOpen(tenant), r => r.SwitchId == row.SwitchId);
        // It is never an open switch: it holds no machine back from one.
        Assert.DoesNotContain(store.ListOpenSwitches(tenant), r => r.SwitchId == row.SwitchId);

        Assert.True(log.RecordSend(tenant, row.SwitchId!, director, T0.AddSeconds(6)));
        Assert.True(log.TryMarkUnknown(tenant, row.SwitchId!, "No answer came back."));
        var unanswered = log.Find(tenant, row.SwitchId!)!;
        Assert.Equal((LicenseLogStates.Unknown, (string?)null, "No answer came back.", 1, T0.AddSeconds(6)),
            (unanswered.State, unanswered.ReasonCode, unanswered.Message, unanswered.SendCount, unanswered.LastSentAtUtc));
        Assert.Contains(log.ListOpen(tenant), r => r.SwitchId == row.SwitchId);

        // Asked again, and ended with the Director's stored answer.
        Assert.True(log.RecordSend(tenant, row.SwitchId!, director, T0.AddMinutes(16)));
        Assert.True(log.TryFinish(tenant, row.SwitchId!, LicenseSignInOutcomes.Failed, LicenseRenewalErrors.ApproverOffline,
            new string('m', LicenseStore.MaxMessageLength + 50), T0.AddMinutes(16), T0.AddMinutes(3), replayed: true));
        var ended = log.Find(tenant, row.SwitchId!)!;
        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseRenewalErrors.ApproverOffline, 2, true),
            (ended.State, ended.ReasonCode, ended.SendCount, ended.Replayed));
        Assert.Equal((T0.AddMinutes(16), T0.AddMinutes(3), LicenseStore.MaxMessageLength),
            (ended.FinishedAtUtc, ended.DirectorFinishedAtUtc, ended.Message!.Length));
        Assert.DoesNotContain(log.ListOpen(tenant), r => r.SwitchId == row.SwitchId);

        // A row that has ended is never changed again.
        Assert.False(log.TryFinish(tenant, row.SwitchId!, LicenseSignInOutcomes.Renewed, null, "late", T0.AddMinutes(20)));
        Assert.False(log.TryMarkUnknown(tenant, row.SwitchId!, "late"));
        Assert.False(log.RecordSend(tenant, row.SwitchId!, director, T0.AddMinutes(20)));
        Assert.Equal(LicenseSignInOutcomes.Failed, log.Find(tenant, row.SwitchId!)!.State);

        // And an answer that ends a row as added or renewed keeps no reason word.
        var second = log.CreatePending(tenant, machine, c, LicenseSignInReasons.Manual, "Asked.", T0.AddMinutes(30));
        Assert.Null(LicenseSignInLog.SignedInBefore(second));
        Assert.True(log.TryFinish(tenant, second.SwitchId!, LicenseSignInOutcomes.Added, "ignored", "Signed in.", T0.AddMinutes(31)));
        var added = log.Find(tenant, second.SwitchId!)!;
        Assert.Equal((LicenseSignInOutcomes.Added, (string?)null, "Signed in.", (bool?)null), (added.State, added.ReasonCode, added.Message, added.Replayed));
    }
}
