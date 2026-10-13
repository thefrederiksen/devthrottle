using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE OWNER EMAIL FOR A RENEWAL THAT KEEPS FAILING (License Switching, contract section 8.1). Run by the Gateway's
/// once-a-minute license check, beside the switches, for every DevThrottle account.
///
/// WHEN. A sign-in a machine holds whose last renewal FAILED - said by <c>RenewalFailedAtUtc</c> being set in the
/// machine's report, whatever reason word came with it or none - and which ends within
/// <see cref="LicenseContract.SignInExpiryWarning"/> of now, or has already ended. A sign-in whose end the machine did
/// not say is not emailed about: nothing here can tell that it is about to end.
///
/// ONCE FOR EACH SIGN-IN, FOR EACH FAILURE STREAK. A streak starts with the first report that says the renewal failed.
/// Its warning is a row of the Licenses log (<see cref="LicenseRenewalFailureLog"/>), open while the streak lasts, and
/// what became of its email is on the row. A later failed try inside the streak moves the failure's time and sends
/// nothing.
///
/// A STREAK ENDS ON SOMETHING PRESENT, NEVER ON SOMETHING MISSING. A Director keeps its failed renewals in memory, so
/// one that has just started reports the same sign-in with NO failure time until its next try fails. A report that
/// only lacks the failure time therefore says nothing, and the row stays open. The streak ends as renewed when the
/// machine reports a NEWER sign-in: its <c>SignInExpiresAtUtc</c> is later than the end the row holds by more than
/// <see cref="LicenseContract.SignInEndTolerance"/> (one sign-in's reported end moves by seconds on every key
/// refresh, and that is not a renewal), or its <c>SignedInAtUtc</c> is later than the one the machine reported when
/// the row was opened. It also ends when the machine no longer holds the sign-in at all. A failure after that is a
/// new streak with its own email.
///
/// AN OPEN ROW WHOSE EMAIL HAS NOT GONE OUT IS TRIED AGAIN whether or not the machine's last report still carries the
/// failure time: the owner is owed that email until the streak ends.
///
/// THE SAME CARE AS THE "NO ACCOUNT HAS ROOM" EMAIL. The same channel (<see cref="LicenseOwnerMailer"/>). The row is
/// recorded as emailed only when the mail service confirmed the send; a send that failed - also a sender that threw -
/// is tried again on a later check, with the text rebuilt from that check's facts, and the tries move apart: one
/// minute, two, four, up to an hour, or longer when the mail service asks for longer. Once it has gone out it never
/// goes again. A confirmed send is remembered in this process until its row is marked, so a database that refuses
/// the mark costs no second email while this Gateway runs; and because the row is stored, an email that was marked
/// is not sent again after a restart. THE ONE EXCEPTION: a Gateway that STOPS between the mail service's confirmation
/// and the row being marked sends once more after it restarts. The only way to rule that out would be to mark the row
/// before sending, which would lose the email instead.
///
/// INERT UNTIL A DIRECTOR REPORTS, and quiet when there is nothing to do: one read, nothing written, no log line.
/// </summary>
public sealed class LicenseRenewalEmailService
{
    /// <summary>The longest the Gateway itself waits between two tries of one warning's owner email.</summary>
    internal static readonly TimeSpan LongestOwnEmailWait = TimeSpan.FromHours(1);

    /// <summary>The most of a machine's own sentence that an email carries.</summary>
    internal const int MachineSentenceLength = 200;

    private readonly LicenseStore _store;
    private readonly LicenseRenewalFailureLog _log;
    private readonly LicenseReadModel _reads;
    private readonly LicenseOwnerEmailSender _mail;
    private readonly Func<DateTime> _nowUtc;

    // One check per tenant at a time, so one warning's email can never be sent by two checks at once.
    private readonly ConcurrentDictionary<TenantId, byte> _ticking = new();
    // The next try of this warning's email is not before this time.
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Row), DateTime> _emailNotBefore = new();
    // How many tries of this warning's email have failed in a row; it sets the growing wait.
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Row), int> _emailFailures = new();
    // Emails the mail service confirmed whose row is not marked yet: never sent again by this process.
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Row), (string Message, DateTime SentAtUtc)> _confirmed = new();

    /// <param name="store">Asked only whether any machine of the account has ever reported.</param>
    /// <param name="log">The failed-renewal rows of the Licenses log.</param>
    /// <param name="reads">The machines and the sign-ins each holds, as last reported.</param>
    /// <param name="mail">Sends the owner email (<see cref="LicenseOwnerMailer.SendAsync"/>).</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    public LicenseRenewalEmailService(LicenseStore store, LicenseRenewalFailureLog log, LicenseReadModel reads,
        LicenseOwnerEmailSender mail, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>
    /// One check for every tenant, each inside its own scope. One tenant's failure is logged and does not stop
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
            // Every check that was started is waited for, also when the pass itself stopped part-way.
            await Task.WhenAll(checks).ConfigureAwait(false);
        }
    }

    private async Task CheckOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await TickAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The boundary of one tenant's check: its failure must never cost another tenant its email.
            FileLog.Write($"[LicenseRenewalEmailService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>One check for one tenant. Returns when every email it started has recorded its result, with how many
    /// emails it tried to send.</summary>
    public async Task<int> TickAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant))
            return 0; // inert until a Director reports: nothing read, nothing written, nothing logged
        if (!_ticking.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseRenewalEmailService] TickAsync: tenant={tenant.ToLogString()}, the previous check has not finished - skipped");
            return 0;
        }
        try
        {
            var now = _nowUtc();
            var machines = _reads.Read(tenant, now).Machines;
            var failing = machines
                .SelectMany(m => m.Holdings.Where(h => h.RenewalFailedAtUtc is not null).Select(h => (Machine: m, Holding: h)))
                .ToList();
            var open = _log.ListOpen(tenant).ToList();
            if (failing.Count == 0 && open.Count == 0)
                return 0;

            // 1. A warning whose streak is over: the machine reports a newer sign-in, or no longer holds it. A report
            //    that only lacks the failure time ends nothing.
            var closed = 0;
            foreach (var row in open.ToList())
            {
                var holding = machines.FirstOrDefault(m => m.MachineId == row.MachineId)
                    ?.Holdings.FirstOrDefault(h => h.AccountEmail == row.ToAccountEmail);
                if (holding is not null && !IsNewerSignIn(row, holding))
                    continue;
                _log.Close(tenant, row.Id, EndedMessage(Label(machines, row.MachineName, row.OsUser), row.ToAccountEmail, holding is not null), now);
                ForgetEmailWait(tenant, row.Id);
                _confirmed.TryRemove((tenant, row.Id), out _);
                open.Remove(row);
                closed++;
            }

            // 2. An email the mail service confirmed on an earlier check, whose row could not be marked then: mark it
            //    now, and never send it again. Before any email of this check is started, so a database that still
            //    refuses ends this check with nothing in flight.
            var marked = new HashSet<Guid>();
            foreach (var row in open.Where(r => _confirmed.ContainsKey((tenant, r.Id))))
            {
                MarkConfirmed(tenant, row.Id);
                marked.Add(row.Id);
            }

            // 3. A failing sign-in that is about to end, or has ended: its one email, unless it has gone out. And an
            //    open row whose email has not gone out, also when the machine's last report no longer carries the
            //    failure time: the row is still open, so the owner is still owed its email.
            var due = new List<(LicenseMachineView Machine, LicenseHoldingView Holding, DateTime Expires, LicenseSwitchLogEntity? Row)>();
            foreach (var (machine, holding) in failing)
            {
                if (holding.SignInExpiresAtUtc is { } expires && expires - now <= LicenseContract.SignInExpiryWarning)
                    due.Add((machine, holding, expires, open.FirstOrDefault(r => IsFor(r, machine, holding))));
            }
            foreach (var row in open.Where(r => !failing.Any(f => IsFor(r, f.Machine, f.Holding))))
            {
                // Still held, or step 1 would have ended it.
                var machine = machines.First(m => m.MachineId == row.MachineId);
                var holding = machine.Holdings.First(h => h.AccountEmail == row.ToAccountEmail);
                due.Add((machine, holding, holding.SignInExpiresAtUtc ?? row.NotAfterUtc!.Value, row));
            }

            var emails = new List<Task>();
            try
            {
                foreach (var (machine, holding, expires, known) in due)
                {
                    var label = Label(machines, machine.MachineName, machine.OsUser);
                    var facts = new Facts(label, holding, expires, machine.LastReportAtUtc);
                    var row = known
                              ?? _log.Open(tenant, machine, holding.AccountEmail, expires, holding.SignedInAtUtc,
                                  WarningMessage(facts, "the owner has not been emailed yet"), now);
                    if (marked.Contains(row.Id)
                        || row.ReasonCode is LicenseGatewayReasonCodes.OwnerEmailed or LicenseGatewayReasonCodes.NoOwnerAccount)
                        continue;
                    if (_emailNotBefore.TryGetValue((tenant, row.Id), out var notBefore) && now < notBefore)
                        continue;
                    emails.Add(EmailAsync(tenant, row.Id, facts, now, ct));
                }
            }
            finally
            {
                // Every email that was started is waited for before the next check of this account may start.
                await Task.WhenAll(emails).ConfigureAwait(false);
            }

            // A check that found nothing to do says nothing: it runs every minute for every tenant, for good.
            if (emails.Count + closed + marked.Count > 0)
                FileLog.Write($"[LicenseRenewalEmailService] TickAsync: tenant={tenant.ToLogString()}, failing={failing.Count}, emails={emails.Count}, ended={closed}, marked={marked.Count}");
            return emails.Count;
        }
        finally
        {
            _ticking.TryRemove(tenant, out _);
        }
    }

    private async Task EmailAsync(TenantId tenant, Guid rowId, Facts facts, DateTime now, CancellationToken ct)
    {
        LicenseOwnerEmailResult result;
        try
        {
            result = await _mail(tenant, BuildEmail(facts.Label, facts.Holding, facts.ExpiresAtUtc, facts.LastReportAtUtc, now), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender that throws has sent nothing this Gateway knows of: a failed send, with the growing wait.
            FileLog.Write($"[LicenseRenewalEmailService] owner email for a failing renewal: the sender FAILED ({ex.GetType().Name})");
            result = new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, $"the mail sender failed ({ex.GetType().Name})");
        }
        var recordedAt = _nowUtc();
        switch (result.Outcome)
        {
            case LicenseOwnerEmailOutcome.Sent:
                // Remembered BEFORE the row is marked: if the mark fails, no later check of this process sends again.
                _confirmed[(tenant, rowId)] = (WarningMessage(facts, "the owner was emailed"), recordedAt);
                MarkConfirmed(tenant, rowId);
                break;
            case LicenseOwnerEmailOutcome.NoAccount:
                _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.NoOwnerAccount,
                    WarningMessage(facts, $"no owner email was sent because {result.Detail}"), null);
                ForgetEmailWait(tenant, rowId);
                break;
            default:
                // The wait is set before the row is written, so a database that refuses the write still gets the wait.
                _emailNotBefore[(tenant, rowId)] = recordedAt + NextEmailWait(tenant, rowId, result.RetryAfter);
                _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.OwnerEmailFailed,
                    WarningMessage(facts, $"the owner has not been emailed yet because {result.Detail}"), null);
                break;
        }
        FileLog.Write($"[LicenseRenewalEmailService] owner email for a failing renewal: tenant={tenant.ToLogString()}, row={rowId}, outcome={result.Outcome}");
    }

    /// <summary>Mark the row of an email the mail service confirmed. Forgotten only once the row is marked.</summary>
    private void MarkConfirmed(TenantId tenant, Guid rowId)
    {
        var (message, sentAt) = _confirmed[(tenant, rowId)];
        _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.OwnerEmailed, message, sentAt);
        _confirmed.TryRemove((tenant, rowId), out _);
        ForgetEmailWait(tenant, rowId);
    }

    /// <summary>
    /// How long to wait before the next try of a warning's owner email after a failed one: the Gateway's own wait,
    /// which doubles with every failure in a row (one minute, two, four ... up to <see cref="LongestOwnEmailWait"/>),
    /// or the mail service's when it asked for a longer one.
    /// </summary>
    private TimeSpan NextEmailWait(TenantId tenant, Guid rowId, TimeSpan? askedFor)
    {
        var failures = _emailFailures.AddOrUpdate((tenant, rowId), 1, (_, n) => n + 1);
        var own = failures > 6 ? LongestOwnEmailWait : TimeSpan.FromMinutes(1 << (failures - 1));
        return askedFor is { } asked && asked > own ? asked : own;
    }

    private void ForgetEmailWait(TenantId tenant, Guid rowId)
    {
        _emailNotBefore.TryRemove((tenant, rowId), out _);
        _emailFailures.TryRemove((tenant, rowId), out _);
    }

    private static bool IsFor(LicenseSwitchLogEntity row, LicenseMachineView machine, LicenseHoldingView holding)
        => row.MachineId == machine.MachineId && row.ToAccountEmail == holding.AccountEmail;

    /// <summary>True when the machine reports a sign-in NEWER than the one the row was opened for: it ends later than
    /// the end the row holds by more than <see cref="LicenseContract.SignInEndTolerance"/>, or it was made later than
    /// the one the machine reported then. Both are times of the machine's own clock compared with each other, never
    /// with the Gateway's. The sign-in time has no drift - the tool reads it from its own record - so it needs no
    /// tolerance.</summary>
    private static bool IsNewerSignIn(LicenseSwitchLogEntity row, LicenseHoldingView holding)
        => (holding.SignInExpiresAtUtc is { } ends && row.NotAfterUtc is { } endedBefore && ends > endedBefore + LicenseContract.SignInEndTolerance)
           || (holding.SignedInAtUtc is { } signedIn && LicenseRenewalFailureLog.SignedInWhenOpened(row) is { } signedInBefore && signedIn > signedInBefore);

    /// <summary>What one warning is written from: the machine's label, the sign-in as last reported, when it ends, and
    /// when the machine last reported.</summary>
    private sealed record Facts(string Label, LicenseHoldingView Holding, DateTime ExpiresAtUtc, DateTime LastReportAtUtc);

    // ----- the words -----

    /// <summary>The owner email for one failing renewal. Pure: the same facts give the same email.</summary>
    /// <param name="label">What the machine is called.</param>
    /// <param name="signInExpiresAtUtc">When the sign-in ends.</param>
    /// <param name="lastReportAtUtc">When the Gateway accepted the machine's last report: everything said is as of then.</param>
    public static LicenseOwnerEmail BuildEmail(string label, LicenseHoldingView holding, DateTime signInExpiresAtUtc,
        DateTime lastReportAtUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(holding);
        var account = holding.AccountEmail;
        var ended = signInExpiresAtUtc <= nowUtc;
        var subject = ended
            ? $"DevThrottle: the sign-in for {account} on {label} has expired and could not be renewed"
            : $"DevThrottle: the sign-in for {account} on {label} expires in {LicenseDisplayFold.Span(signInExpiresAtUtc - nowUtc)} and could not be renewed";

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{label} could not renew its sign-in for {account}, and that sign-in {ExpiryPhrase(signInExpiresAtUtc, nowUtc)} (at {Time(signInExpiresAtUtc)}).\r\n\r\n");

        // How old the facts are. The failure's time is of the machine's own clock, so its distance is only said when
        // it is not in the future by the Gateway's.
        var failedAt = holding.RenewalFailedAtUtc is { } failed ? DateTime.SpecifyKind(failed, DateTimeKind.Utc) : (DateTime?)null;
        if (failedAt is { } at)
            body.Append(at <= nowUtc
                ? $"The last try to renew it failed {LicenseDisplayFold.Span(nowUtc - at)} ago (at {Time(at)} by the machine's clock).\r\n"
                : $"The last try to renew it failed at {Time(at)} by the machine's clock.\r\n");
        else
            body.Append(CultureInfo.InvariantCulture, $"An earlier report of {label} said the renewal had failed. Its last report does not say when or why, and it does not show a newer sign-in.\r\n");
        body.Append(CultureInfo.InvariantCulture, $"{label} last reported to DevThrottle {LicenseDisplayFold.Span(nowUtc - lastReportAtUtc)} ago (at {Time(lastReportAtUtc)}). This email is written from that report; what {label} has done since is not known here.\r\n\r\n");

        // The Gateway's own sentence for the reason. The machine's own sentence only when the Gateway knows no
        // reason, as the Licenses page does - on one line, with nothing that is not text, and cut short.
        if (failedAt is not null)
        {
            body.Append("Why the last try failed: ");
            if (LicenseDisplayFold.RenewalWhy(holding.RenewalError) is { } why)
                body.Append(CultureInfo.InvariantCulture, $"{why}.");
            else if (OneLine(holding.RenewalErrorDetail, MachineSentenceLength) is { Length: > 0 } said)
                body.Append(CultureInfo.InvariantCulture, $"the machine gave no reason this Gateway knows. The machine said: {said}");
            else
                body.Append("the machine gave no reason this Gateway knows.");
            body.Append("\r\n\r\n");
        }

        body.Append((holding.IsActive, ended) switch
        {
            (true, false) => $"{label} is on this account now: its sessions stop when the sign-in ends.\r\n",
            (true, true) => $"{label} is on this account now, and the sign-in has ended.\r\n",
            (false, false) => $"{label} holds this account as a spare: it cannot be switched to it once the sign-in has ended.\r\n",
            (false, true) => $"{label} holds this account as a spare, and the sign-in has ended: it cannot be switched to it until it is signed in again.\r\n",
        });
        body.Append(CultureInfo.InvariantCulture, $"\r\nSign {label} in to {account} again, from the Licenses page of the Cockpit or on the machine itself.\r\n");
        body.Append("\r\nThis is the only email for this failure. Another is sent only when the machine has reported a newer sign-in, or has stopped holding this one, and a later renewal fails.");
        return new LicenseOwnerEmail(account, subject, body.ToString());
    }

    /// <summary>The sentence a warning's row shows while the streak lasts: what is wrong, how old the facts are, then
    /// what became of the owner email. Every time in it is a moment, never a distance, so the stored sentence stays
    /// true however long the row is open.</summary>
    public static string WarningMessage(string label, string accountEmail, DateTime signInExpiresAtUtc, DateTime? renewalFailedAtUtc,
        DateTime lastReportAtUtc, string ownerEmailClause)
    {
        var failed = renewalFailedAtUtc is { } at ? $"the last try failed at {Time(at)} by the machine's clock; " : "";
        return $"Renewing the sign-in for {accountEmail} on {label} is failing, and the sign-in ends at {Time(signInExpiresAtUtc)} "
               + $"({failed}{label} last reported at {Time(lastReportAtUtc)}); {ownerEmailClause}.";
    }

    private static string WarningMessage(Facts facts, string ownerEmailClause)
        => WarningMessage(facts.Label, facts.Holding.AccountEmail, facts.ExpiresAtUtc, facts.Holding.RenewalFailedAtUtc, facts.LastReportAtUtc, ownerEmailClause);

    /// <summary>The sentence a warning's row shows once its streak is over.</summary>
    public static string EndedMessage(string label, string accountEmail, bool stillHeld) => stillHeld
        ? $"Renewing the sign-in for {accountEmail} on {label} had been failing; {label} now reports a newer sign-in."
        : $"Renewing the sign-in for {accountEmail} on {label} had been failing; {label} no longer holds that sign-in.";

    private static string ExpiryPhrase(DateTime expiresAtUtc, DateTime nowUtc) => expiresAtUtc <= nowUtc
        ? $"expired {LicenseDisplayFold.Span(nowUtc - expiresAtUtc)} ago"
        : $"expires in {LicenseDisplayFold.Span(expiresAtUtc - nowUtc)}";

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Text from a machine made safe to put into an email: every control, format and separator character becomes a
    /// space, runs of spaces become one, and what is left is cut to <paramref name="longest"/> characters. Never more
    /// than one line.
    /// </summary>
    internal static string OneLine(string? text, int longest)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var line = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var space = char.IsWhiteSpace(c) || LicenseApproverStore.NotReadable(c);
            if (!space)
                line.Append(c);
            else if (line.Length > 0 && line[^1] != ' ')
                line.Append(' ');
        }
        var clean = line.ToString().Trim();
        return clean.Length > longest ? clean[..longest].TrimEnd() + " (cut short)" : clean;
    }

    /// <summary>What a machine is called: its name, with the user in brackets when another machine has the same name.
    /// On one line: it goes into an email's subject.</summary>
    private static string Label(IReadOnlyList<LicenseMachineView> machines, string machineName, string osUser)
        => OneLine(machines.Count(m => string.Equals(m.MachineName, machineName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{machineName} ({osUser})"
            : machineName, 300);
}
