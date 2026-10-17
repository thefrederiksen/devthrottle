using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The kind of a license alarm: a short stable key the raising check chooses, and the few words that name the kind of
/// problem on a phone's locked screen. A kind is a constant in the code of the check that raises it, so the push words
/// are never built from anything a machine or a person wrote.
/// </summary>
public sealed record LicenseAlarmKind
{
    private static readonly Regex KeyShape = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <param name="key">Lower-case words joined by hyphens, at most 64 characters: <c>not-ready-at-80</c>.</param>
    /// <param name="title">What kind of problem it is, in a few plain words with no full stop and never an email
    /// address: "Not ready to switch". It is what a phone's locked screen shows.</param>
    public LicenseAlarmKind(string key, string title)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64 || !KeyShape.IsMatch(key))
            throw new ArgumentException($"'{key}' is not an alarm kind: lower-case words joined by hyphens, at most 64 characters.", nameof(key));
        // Trimmed before it is checked, so a trailing space cannot hide a full stop.
        var words = title?.Trim() ?? "";
        if (words.Length == 0 || words.Length > 80 || words.Contains('@') || words.EndsWith('.')
            || words.Any(c => char.IsControl(c)))
            throw new ArgumentException("An alarm title is a few plain words on one line, with no full stop and no email address.", nameof(title));
        Key = key;
        Title = words;
    }

    public string Key { get; }
    public string Title { get; }
}

/// <summary>
/// One alarm that is true NOW, as a check found it. Its identity is the kind it is raised under, the machine and the
/// account: the same three on a later run is the same alarm, still open.
/// </summary>
/// <param name="MachineId">The machine's row (<c>LicenseMachineView.MachineId</c>); null for an alarm about no one machine.</param>
/// <param name="MachineName">What the machine is called; given exactly when <paramref name="MachineId"/> is.</param>
/// <param name="AccountEmail">The account it is about; null for an alarm about no one account.</param>
/// <param name="Problem">What is wrong, one plain-English sentence ending in a full stop. It may name the account.</param>
/// <param name="Action">The one thing to do, one plain-English sentence ending in a full stop.</param>
public sealed record LicenseAlarmCondition(Guid? MachineId, string? MachineName, string? AccountEmail, string Problem, string Action);

/// <summary>One alarm that opened, as it is pushed to the owner's phones.</summary>
public sealed record LicenseAlarmOpened(Guid AlarmId, LicenseAlarmKind Kind, string MachineName);

/// <summary>Sends the push for alarms that just opened to every phone of one tenant. Never throws.</summary>
public delegate Task LicenseAlarmPusher(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct);

/// <summary>What one <see cref="LicenseAlarmService.RaiseAsync"/> did.</summary>
public sealed record LicenseAlarmRaised(int Opened, int StillOpen, int Closed);

/// <summary>
/// THE LICENSE ALARM (License Switching phase 6): the ONE mechanism by which a problem with licenses is made loud and
/// kept loud until it is fixed. Every check that finds such a problem - a machine not ready at 80%, a machine that
/// stopped reporting, a refused report, an approver that cannot approve - raises it here, and nowhere else.
///
/// RAISED BY CONDITION, AND IDEMPOTENT. A check calls <see cref="RaiseAsync"/> with its kind and EVERY alarm of that
/// kind that is true now - an empty list when none is. An alarm that is not open yet opens: one row of
/// <c>license_alarms</c>, one row of the Licenses log, one push to the owner's phones, and one owner email. An alarm
/// already open stays open and sends nothing more; only its two sentences are brought up to date. An open alarm of that
/// kind that is not in the list closes by itself and writes a row of the Licenses log. Raising it again later opens a
/// NEW alarm, with its own push and its own email. A check never closes another kind's alarms.
///
/// ONE OWNER EMAIL FOR EACH ALARM, through the same channel and with the same care as the "no account has room" email
/// (<see cref="LicenseOwnerMailer"/>): recorded as emailed only when the mail service confirmed it, and a send that
/// failed is tried again on a later check - one minute, two, four, up to an hour, or longer when the mail service asks
/// - for as long as the alarm is open. Every alarm whose email is due in one check of one tenant goes out in ONE email.
/// A confirmed send is remembered in this process until its row is marked. The one gap is the same as that email's: a
/// Gateway that stops between the confirmation and the mark sends once more after it restarts.
///
/// ONE PUSH FOR EACH ALARM, when it opens. It carries the machine's name and the kind's title only - never an account
/// email address, because a push shows on a locked screen. It is not tried again: the banner and the email are the
/// alarm's lasting voice.
///
/// Per tenant: every read and write is in the tenant's own partition, so two tenants never see each other's alarms.
/// There is no route that raises one: only Gateway code calls this.
/// </summary>
public sealed class LicenseAlarmService
{
    /// <summary>The longest one sentence of an alarm may be.</summary>
    public const int MaxSentenceLength = 400;

    /// <summary>The longest the Gateway itself waits between two tries of one alarm's owner email.</summary>
    internal static readonly TimeSpan LongestOwnEmailWait = TimeSpan.FromHours(1);

    private readonly GatewayDatabase _db;
    private readonly LicenseOwnerEmailSender _mail;
    private readonly LicenseAlarmPusher _push;
    private readonly Func<DateTime> _nowUtc;

    // One raise per tenant at a time, so one identity can never be opened twice by two raises at once.
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _raising = new();
    // One email check per tenant at a time, so one alarm's email can never be sent by two checks at once.
    private readonly ConcurrentDictionary<TenantId, byte> _emailing = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Alarm), DateTime> _emailNotBefore = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Alarm), int> _emailFailures = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Alarm), DateTime> _confirmed = new();

    /// <param name="db">The Gateway database: <c>license_alarms</c> and the Licenses log.</param>
    /// <param name="mail">Sends the owner email (<see cref="LicenseOwnerMailer.SendAsync"/>).</param>
    /// <param name="push">Pushes an alarm that opened to the tenant's phones.</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    public LicenseAlarmService(GatewayDatabase db, LicenseOwnerEmailSender mail, LicenseAlarmPusher push, Func<DateTime> nowUtc)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>
    /// Say which alarms of <paramref name="kind"/> are true now for <paramref name="tenant"/>: the ones not open yet
    /// open (and are pushed and emailed), the ones still open stay open quietly, and every other open alarm of this kind
    /// closes. Returns once the database is written, the pushes are sent and the due email has recorded its result.
    /// </summary>
    /// <exception cref="ArgumentException">A condition is not well formed, or two name the same machine and account.</exception>
    public async Task<LicenseAlarmRaised> RaiseAsync(TenantId tenant, LicenseAlarmKind kind, IReadOnlyList<LicenseAlarmCondition> trueNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(trueNow);
        var wanted = trueNow.Select(Checked).ToList();
        var duplicate = wanted.GroupBy(c => (c.MachineId, c.AccountEmail)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Two alarms of kind '{kind.Key}' name the same machine and account.", nameof(trueNow));

        var gate = _raising.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        List<LicenseAlarmOpened> opened;
        int stillOpen, closed;
        try
        {
            (opened, stillOpen, closed) = Reconcile(tenant, kind, wanted, _nowUtc());
        }
        finally
        {
            gate.Release();
        }
        if (opened.Count + closed > 0)
            FileLog.Write($"[LicenseAlarmService] RaiseAsync: tenant={tenant.ToLogString()}, kind={kind.Key}, opened={opened.Count}, stillOpen={stillOpen}, closed={closed}");

        if (opened.Count > 0)
        {
            await PushAsync(tenant, opened, ct).ConfigureAwait(false);
            await SendDueEmailAsync(tenant, ct).ConfigureAwait(false);
        }
        return new LicenseAlarmRaised(opened.Count, stillOpen, closed);
    }

    /// <summary>Every alarm of the tenant that is open now, oldest first.</summary>
    /// <remarks>Quiet: every Cockpit page asks this, and a read that finds nothing leaves no line. A failure is logged
    /// by the route that asked.</remarks>
    public IReadOnlyList<LicenseAlarmEntity> ListOpen(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseAlarms.AsNoTracking()
            .Where(a => a.ClosedAtUtc == null)
            .ToList()
            .OrderBy(a => a.OpenedAtUtc).ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.MachineName, StringComparer.Ordinal).ThenBy(a => a.AccountEmail, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The minute check's part: for every tenant, inside its own scope, send the owner email of every open alarm whose
    /// email is due - one that failed before and whose wait has passed. One tenant's failure is logged and does not stop
    /// another tenant's check.
    /// </summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var checks = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                // No scope in effect: check nothing, never a tenant of our own choosing.
                if (tenants.Current is { } tenant)
                    checks.Add(CheckOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(checks).ConfigureAwait(false);
        }
    }

    private async Task CheckOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await SendDueEmailAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The boundary of one tenant's check: its failure must never cost another tenant its email.
            FileLog.Write($"[LicenseAlarmService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Send the ONE owner email of every open alarm of <paramref name="tenant"/> whose email is due, and record what
    /// became of it on each. Returns how many emails it tried to send: one, or none.
    /// </summary>
    public async Task<int> SendDueEmailAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_emailing.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseAlarmService] SendDueEmailAsync: tenant={tenant.ToLogString()}, the previous check has not finished - skipped");
            return 0;
        }
        try
        {
            var now = _nowUtc();
            var unsent = OpenUnsent(tenant);
            if (unsent.Count == 0)
                return 0; // nothing owed: nothing written, nothing logged

            // An email the mail service confirmed earlier whose row could not be marked then: mark it, never resend.
            foreach (var alarm in unsent.Where(a => _confirmed.ContainsKey((tenant, a.Id))).ToList())
            {
                MarkEmailed(tenant, alarm.Id);
                unsent.Remove(alarm);
            }
            var due = unsent
                .Where(a => !_emailNotBefore.TryGetValue((tenant, a.Id), out var notBefore) || now >= notBefore)
                .ToList();
            if (due.Count == 0)
                return 0;
            await EmailAsync(tenant, due, ct).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            _emailing.TryRemove(tenant, out _);
        }
    }

    // ----- the database part of a raise -----

    private (List<LicenseAlarmOpened> Opened, int StillOpen, int Closed) Reconcile(TenantId tenant, LicenseAlarmKind kind,
        IReadOnlyList<LicenseAlarmCondition> wanted, DateTime nowUtc)
    {
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var open = ctx.LicenseAlarms.Where(a => a.Kind == kind.Key && a.ClosedAtUtc == null).ToList();
            var opened = new List<LicenseAlarmOpened>();
            var stillOpen = 0;
            var now = Utc(nowUtc);
            foreach (var condition in wanted)
            {
                var machineId = condition.MachineId ?? Guid.Empty;
                var account = condition.AccountEmail ?? "";
                var row = open.FirstOrDefault(a => a.MachineId == machineId && a.AccountEmail == account);
                if (row is not null)
                {
                    // Still true: the words are brought up to date, and nothing is sent.
                    row.Problem = condition.Problem;
                    row.Action = condition.Action;
                    open.Remove(row);
                    stillOpen++;
                    continue;
                }
                row = new LicenseAlarmEntity
                {
                    Kind = kind.Key,
                    MachineId = machineId,
                    MachineName = condition.MachineName ?? "",
                    AccountEmail = account,
                    Problem = condition.Problem,
                    Action = condition.Action,
                    OpenedAtUtc = now,
                    TenantId = ctx.ActiveTenant!,
                };
                ctx.LicenseAlarms.Add(row);
                ctx.LicenseSwitchLog.Add(LogRow(ctx, LicenseLogKinds.AlarmOpened, row, LicenseAlarmFold.OpenedLogMessage(row.Problem, row.Action), now));
                opened.Add(new LicenseAlarmOpened(row.Id, kind, row.MachineName));
            }
            // Open, and no longer true: it closes by itself.
            foreach (var row in open)
            {
                row.ClosedAtUtc = now;
                ctx.LicenseSwitchLog.Add(LogRow(ctx, LicenseLogKinds.AlarmClosed, row, LicenseAlarmFold.ClosedLogMessage(row.Problem), now));
            }
            ctx.SaveChanges();
            foreach (var row in open)
                ForgetEmail(tenant, row.Id);
            return (opened, stillOpen, open.Count);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseAlarmService] Reconcile FAILED: tenant={tenant.ToLogString()}, kind={kind.Key}: {ex.Message}");
            throw;
        }
    }

    private static LicenseSwitchLogEntity LogRow(GatewayDbContext ctx, string logKind, LicenseAlarmEntity alarm, string message, DateTime now)
        => new()
        {
            Kind = logKind,
            MachineId = alarm.MachineId,
            MachineName = alarm.MachineName,
            ToAccountEmail = alarm.AccountEmail,
            State = LicenseLogStates.Recorded,
            Message = message.Length > LicenseStore.MaxMessageLength ? message[..LicenseStore.MaxMessageLength] : message,
            CreatedAtUtc = now,
            FinishedAtUtc = now,
            TenantId = ctx.ActiveTenant!,
        };

    private List<LicenseAlarmEntity> OpenUnsent(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseAlarms.AsNoTracking()
            .Where(a => a.ClosedAtUtc == null
                        && (a.EmailState == null || a.EmailState == LicenseGatewayReasonCodes.OwnerEmailFailed))
            .ToList()
            .OrderBy(a => a.OpenedAtUtc).ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.MachineName, StringComparer.Ordinal).ThenBy(a => a.AccountEmail, StringComparer.Ordinal)
            .ToList();
    }

    // ----- the push -----

    private async Task PushAsync(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        try
        {
            await _push(tenant, opened, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A push that fails must not fail the alarm it announces: the banner and the email still carry it.
            FileLog.Write($"[LicenseAlarmService] PushAsync FAILED: tenant={tenant.ToLogString()}, alarms={opened.Count} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    // ----- the email -----

    private async Task EmailAsync(TenantId tenant, IReadOnlyList<LicenseAlarmEntity> alarms, CancellationToken ct)
    {
        LicenseOwnerEmailResult result;
        try
        {
            result = await _mail(tenant, LicenseAlarmFold.Email(alarms), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender that throws has sent nothing this Gateway knows of: a failed send, with the growing wait.
            FileLog.Write($"[LicenseAlarmService] EmailAsync: the sender FAILED ({ex.GetType().Name}): {ex.Message}");
            result = new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, $"the mail sender failed ({ex.GetType().Name})");
        }
        var recordedAt = Utc(_nowUtc());
        switch (result.Outcome)
        {
            case LicenseOwnerEmailOutcome.Sent:
                // Remembered for every alarm BEFORE any row is marked: a mark that fails costs no second email.
                foreach (var alarm in alarms)
                    _confirmed[(tenant, alarm.Id)] = recordedAt;
                foreach (var alarm in alarms)
                    MarkEmailed(tenant, alarm.Id);
                break;
            case LicenseOwnerEmailOutcome.NoAccount:
                foreach (var alarm in alarms)
                {
                    RecordEmail(tenant, alarm.Id, LicenseGatewayReasonCodes.NoOwnerAccount, null);
                    ForgetEmail(tenant, alarm.Id);
                }
                break;
            default:
                // The waits are set before any row is written, so a database that refuses a write still gets them.
                foreach (var alarm in alarms)
                    _emailNotBefore[(tenant, alarm.Id)] = recordedAt + NextEmailWait(tenant, alarm.Id, result.RetryAfter);
                foreach (var alarm in alarms)
                    RecordEmail(tenant, alarm.Id, LicenseGatewayReasonCodes.OwnerEmailFailed, null);
                break;
        }
        FileLog.Write($"[LicenseAlarmService] EmailAsync: tenant={tenant.ToLogString()}, alarms={alarms.Count}, outcome={result.Outcome}{(result.Detail is null ? "" : $", detail={result.Detail}")}");
    }

    private void MarkEmailed(TenantId tenant, Guid alarmId)
    {
        var sentAt = _confirmed[(tenant, alarmId)];
        RecordEmail(tenant, alarmId, LicenseGatewayReasonCodes.OwnerEmailed, sentAt);
        _confirmed.TryRemove((tenant, alarmId), out _);
        ForgetEmail(tenant, alarmId);
    }

    /// <summary>Record what became of one alarm's email. An alarm already recorded as emailed is never changed.</summary>
    private void RecordEmail(TenantId tenant, Guid alarmId, string emailState, DateTime? sentAtUtc)
    {
        using var ctx = _db.CreateContext(tenant);
        var changed = ctx.LicenseAlarms
            .Where(a => a.Id == alarmId)
            .Where(a => a.EmailState == null || a.EmailState != LicenseGatewayReasonCodes.OwnerEmailed)
            .ExecuteUpdate(u => u
                .SetProperty(a => a.EmailState, emailState)
                .SetProperty(a => a.EmailSentAtUtc, sentAtUtc)) > 0;
        FileLog.Write($"[LicenseAlarmService] RecordEmail: tenant={tenant.ToLogString()}, alarm={alarmId}, state={emailState}, changed={changed}");
    }

    /// <summary>The wait before the next try of an alarm's email after a failed one: one minute, doubling with every
    /// failure in a row up to <see cref="LongestOwnEmailWait"/>, or the mail service's when it asked for longer.</summary>
    private TimeSpan NextEmailWait(TenantId tenant, Guid alarmId, TimeSpan? askedFor)
    {
        var failures = _emailFailures.AddOrUpdate((tenant, alarmId), 1, (_, n) => n + 1);
        var own = failures > 6 ? LongestOwnEmailWait : TimeSpan.FromMinutes(1 << (failures - 1));
        return askedFor is { } asked && asked > own ? asked : own;
    }

    private void ForgetEmail(TenantId tenant, Guid alarmId)
    {
        _emailNotBefore.TryRemove((tenant, alarmId), out _);
        _emailFailures.TryRemove((tenant, alarmId), out _);
    }

    // ----- checks on what a raiser passes -----

    private static LicenseAlarmCondition Checked(LicenseAlarmCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition.MachineId.HasValue != !string.IsNullOrWhiteSpace(condition.MachineName))
            throw new ArgumentException("An alarm names a machine by both its id and its name, or not at all.", nameof(condition));
        if (condition.MachineId == Guid.Empty)
            throw new ArgumentException("An alarm about a machine names a real machine id.", nameof(condition));
        var account = condition.AccountEmail is null ? null : LicenseStore.NormalizeEmail(condition.AccountEmail);
        if (account is "")
            throw new ArgumentException("An alarm about an account names the account.", nameof(condition));
        return condition with
        {
            MachineName = condition.MachineName?.Trim(),
            AccountEmail = account,
            Problem = Sentence(condition.Problem, "problem"),
            Action = Sentence(condition.Action, "action"),
        };
    }

    private static string Sentence(string? text, string what)
    {
        var sentence = text?.Trim() ?? "";
        if (sentence.Length == 0 || sentence.Length > MaxSentenceLength || !sentence.EndsWith('.')
            || sentence.Any(c => char.IsControl(c)))
            throw new ArgumentException(
                $"An alarm's {what} is one plain-English sentence on one line, ending in a full stop, at most {MaxSentenceLength} characters.",
                what);
        return sentence;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
