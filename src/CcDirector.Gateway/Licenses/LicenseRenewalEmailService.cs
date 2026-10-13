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
/// once-a-minute license check, after the switches, for every DevThrottle account.
///
/// WHEN. A sign-in a machine holds whose last renewal FAILED - said by <c>RenewalFailedAtUtc</c> being set in the
/// machine's report, whatever reason word came with it or none - and which ends within
/// <see cref="LicenseContract.SignInExpiryWarning"/> of now, or has already ended. A sign-in whose end the machine did
/// not say is not emailed about: nothing here can tell that it is about to end.
///
/// ONCE FOR EACH SIGN-IN, FOR EACH FAILURE STREAK. A streak is the time the renewal keeps failing: it starts with the
/// first report that says the renewal failed and ends with the report that says one worked. The streak's warning is a
/// row of the Licenses log (<see cref="LicenseRenewalFailureLog"/>), open while the streak lasts, and what became of
/// its email is on the row. A later failed try inside the streak moves the failure's time and sends nothing; a
/// renewal that works closes the row, and a failure after that is a new streak with its own email.
///
/// THE SAME CARE AS THE "NO ACCOUNT HAS ROOM" EMAIL. The same channel (<see cref="LicenseOwnerMailer"/>). The row is
/// recorded as emailed only when the mail service confirmed the send; a send that failed is tried again on a later
/// check, with the text rebuilt from that check's facts, and the tries move apart - one minute, two, four, up to an
/// hour, or longer when the mail service asks for longer. Once it has gone out it never goes again, and because the
/// row is stored that holds across a restart. A Gateway that stops between the service's confirmation and the row
/// being marked would send once more after it restarts - the only way to rule that out would be to mark the row
/// before sending, which would lose the email instead.
///
/// INERT UNTIL A DIRECTOR REPORTS, and quiet when there is nothing to do: one read, nothing written, no log line.
/// </summary>
public sealed class LicenseRenewalEmailService
{
    /// <summary>The longest the Gateway itself waits between two tries of one warning's owner email.</summary>
    internal static readonly TimeSpan LongestOwnEmailWait = TimeSpan.FromHours(1);

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
            var open = _log.ListOpen(tenant);
            if (failing.Count == 0 && open.Count == 0)
                return 0;

            // 1. A warning whose sign-in is no longer failing: the streak is over.
            var closed = 0;
            foreach (var row in open)
            {
                if (failing.Any(f => IsFor(row, f.Machine, f.Holding)))
                    continue;
                var machine = machines.FirstOrDefault(m => m.MachineId == row.MachineId);
                var stillHeld = machine?.Holdings.Any(h => h.AccountEmail == row.ToAccountEmail) == true;
                _log.Close(tenant, row.Id, EndedMessage(Label(machines, row.MachineName, row.OsUser), row.ToAccountEmail, stillHeld), now);
                ForgetEmailWait(tenant, row.Id);
                closed++;
            }

            // 2. A failing sign-in that is about to end, or has ended: its one email, unless it has gone out.
            var emails = new List<Task>();
            foreach (var (machine, holding) in failing)
            {
                if (holding.SignInExpiresAtUtc is not { } expires || expires - now > LicenseContract.SignInExpiryWarning)
                    continue;
                var label = Label(machines, machine.MachineName, machine.OsUser);
                var row = open.FirstOrDefault(r => IsFor(r, machine, holding))
                          ?? _log.Open(tenant, machine, holding.AccountEmail, expires,
                              WarningMessage(label, holding.AccountEmail, expires, now, "the owner has not been emailed yet"), now);
                if (row.ReasonCode is LicenseGatewayReasonCodes.OwnerEmailed or LicenseGatewayReasonCodes.NoOwnerAccount)
                    continue;
                if (_emailNotBefore.TryGetValue((tenant, row.Id), out var notBefore) && now < notBefore)
                    continue;
                emails.Add(EmailAsync(tenant, row.Id, label, holding, expires, now, ct));
            }
            await Task.WhenAll(emails).ConfigureAwait(false);

            // A check that found nothing to do says nothing: it runs every minute for every tenant, for good.
            if (emails.Count + closed > 0)
                FileLog.Write($"[LicenseRenewalEmailService] TickAsync: tenant={tenant.ToLogString()}, failing={failing.Count}, emails={emails.Count}, ended={closed}");
            return emails.Count;
        }
        finally
        {
            _ticking.TryRemove(tenant, out _);
        }
    }

    private async Task EmailAsync(TenantId tenant, Guid rowId, string label, LicenseHoldingView holding, DateTime expires,
        DateTime now, CancellationToken ct)
    {
        var account = holding.AccountEmail;
        var result = await _mail(tenant, BuildEmail(label, holding, expires, now), ct).ConfigureAwait(false);
        var recordedAt = _nowUtc();
        switch (result.Outcome)
        {
            case LicenseOwnerEmailOutcome.Sent:
                _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.OwnerEmailed,
                    WarningMessage(label, account, expires, now, "the owner was emailed"), recordedAt);
                ForgetEmailWait(tenant, rowId);
                break;
            case LicenseOwnerEmailOutcome.NoAccount:
                _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.NoOwnerAccount,
                    WarningMessage(label, account, expires, now, $"no owner email was sent because {result.Detail}"), null);
                ForgetEmailWait(tenant, rowId);
                break;
            default:
                _log.RecordEmail(tenant, rowId, LicenseGatewayReasonCodes.OwnerEmailFailed,
                    WarningMessage(label, account, expires, now, $"the owner has not been emailed yet because {result.Detail}"), null);
                _emailNotBefore[(tenant, rowId)] = recordedAt + NextEmailWait(tenant, rowId, result.RetryAfter);
                break;
        }
        FileLog.Write($"[LicenseRenewalEmailService] owner email for a failing renewal: tenant={tenant.ToLogString()}, row={rowId}, outcome={result.Outcome}");
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

    // ----- the words -----

    /// <summary>The owner email for one failing renewal. Pure: the same facts give the same email.</summary>
    /// <param name="label">What the machine is called.</param>
    /// <param name="signInExpiresAtUtc">When the sign-in ends.</param>
    public static LicenseOwnerEmail BuildEmail(string label, LicenseHoldingView holding, DateTime signInExpiresAtUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(holding);
        var account = holding.AccountEmail;
        var ended = signInExpiresAtUtc <= nowUtc;
        var subject = ended
            ? $"DevThrottle: the sign-in for {account} on {label} has expired and could not be renewed"
            : $"DevThrottle: the sign-in for {account} on {label} expires in {LicenseDisplayFold.Span(signInExpiresAtUtc - nowUtc)} and could not be renewed";

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{label} could not renew its sign-in for {account}, and that sign-in {ExpiryPhrase(signInExpiresAtUtc, nowUtc)} (at {Time(signInExpiresAtUtc)}).\r\n\r\n");
        body.Append("Why the last try failed: ");
        body.Append(LicenseDisplayFold.RenewalWhy(holding.RenewalError) is { } why ? $"{why}." : "the machine gave no reason this Gateway knows.");
        body.Append("\r\n");
        if (!string.IsNullOrWhiteSpace(holding.RenewalErrorDetail))
            body.Append(CultureInfo.InvariantCulture, $"The machine said: {holding.RenewalErrorDetail.Trim()}\r\n");
        body.Append("\r\n");
        body.Append(holding.IsActive
            ? $"{label} is on this account now: its sessions stop when the sign-in ends.\r\n"
            : $"{label} holds this account as a spare: it cannot be switched to it once the sign-in has ended.\r\n");
        body.Append(CultureInfo.InvariantCulture, $"\r\nSign {label} in to {account} again, from the Licenses page of the Cockpit or on the machine itself.\r\n");
        body.Append("\r\nThis is the only email for this failure. Another is sent only when a renewal has worked and a later one fails.");
        return new LicenseOwnerEmail(account, subject, body.ToString());
    }

    /// <summary>The sentence a warning's row shows while the streak lasts: what is wrong, then what became of the owner email.</summary>
    public static string WarningMessage(string label, string accountEmail, DateTime signInExpiresAtUtc, DateTime nowUtc, string ownerEmailClause)
        => $"Renewing the sign-in for {accountEmail} on {label} is failing, and the sign-in {ExpiryPhrase(signInExpiresAtUtc, nowUtc)}; {ownerEmailClause}.";

    /// <summary>The sentence a warning's row shows once its streak is over.</summary>
    public static string EndedMessage(string label, string accountEmail, bool stillHeld) => stillHeld
        ? $"Renewing the sign-in for {accountEmail} on {label} had been failing; a renewal has now worked."
        : $"Renewing the sign-in for {accountEmail} on {label} had been failing; {label} no longer holds that sign-in.";

    private static string ExpiryPhrase(DateTime expiresAtUtc, DateTime nowUtc) => expiresAtUtc <= nowUtc
        ? $"expired {LicenseDisplayFold.Span(nowUtc - expiresAtUtc)} ago"
        : $"expires in {LicenseDisplayFold.Span(expiresAtUtc - nowUtc)}";

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>What a machine is called: its name, with the user in brackets when another machine has the same name.</summary>
    private static string Label(IReadOnlyList<LicenseMachineView> machines, string machineName, string osUser)
        => machines.Count(m => string.Equals(m.MachineName, machineName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{machineName} ({osUser})"
            : machineName;
}
