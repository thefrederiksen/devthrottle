using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The answer to starting a sign-in. Either <see cref="Accepted"/> is true and <see cref="Row"/> is the sign-in's row
/// of the log as it was written, or it is false and <see cref="RefusalWord"/>, <see cref="RefusalText"/> and
/// <see cref="Status"/> say why nothing was started.
/// </summary>
/// <param name="Status">The HTTP status the route answers with.</param>
/// <param name="Row">The sign-in's log row, in <c>pending</c>; null when refused.</param>
/// <param name="Machine">The machine it was started on; null when refused.</param>
/// <param name="Label">What that machine is called; null when refused.</param>
public sealed record LicenseSignInStart(bool Accepted, int Status, string? RefusalWord, string? RefusalText,
    LicenseSwitchLogEntity? Row, LicenseMachineView? Machine, string? Label);

/// <summary>
/// STARTING A SIGN-IN FROM THE COCKPIT (License Switching, contract section 8.4): "Add a spare" and "Renew now".
/// <see cref="Start"/> writes the sign-in's row of the log, sends <c>license-sign-in</c> to the Director that last
/// reported for the machine, and returns - it does not wait the minutes a sign-in takes. What the Director answers is
/// recorded on that same row, so the outcome reaches the Licenses page through the log route.
///
/// WHAT IS REFUSED, each with a finished sentence and before anything is written: an account that is not an email; a
/// machine this DevThrottle account does not have; "Add a spare" for an account the machine already holds, and "Renew
/// now" for one it does not; a machine whose Director is not connected; and a sign-in for that machine and account
/// that is already running. "Running" is an open row - one whose Director has not answered - so it holds across the
/// requests of every Cockpit, and across a Gateway restart until the row is settled.
///
/// WHAT THE ROW RECORDS (the table of contract section 3.5, for a sign-in): nothing was sent because the Director was
/// not connected - <c>failed</c>, <c>director-offline</c>; no answer in time, or the tunnel dropped - <c>unknown</c>;
/// the Director refused the command or failed - <c>failed</c>, <c>director-error</c>; an answer - its outcome, its
/// reason and its sentence.
///
/// AN OPEN ROW IS SETTLED, NEVER LEFT. A row with no answer - the wait ended, the tunnel dropped, or the Gateway
/// restarted while it waited - is settled by <see cref="SettleUnanswered"/> once no Director could still be running
/// it: from the machine's own report, which is the only thing that shows whether the sign-in was made, and the row's
/// sentence says that is how it was known. The host calls it once a minute; <see cref="Start"/> calls it first, so a
/// row past its time never blocks a new sign-in.
///
/// It never touches a sign-in key or what completes a sign-in: the command carries an identifier, an email, a reason
/// word and a time. When the Director needs another machine's approval it asks for it by itself (section 8.3).
/// </summary>
public sealed class LicenseSignInService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>After this long from when a sign-in was asked for, no Director can still be running it: it refuses to
    /// start after <see cref="LicenseContract.SignInNotAfter"/> and answers within
    /// <see cref="LicenseContract.SignInCommandTimeout"/> of starting.</summary>
    public static readonly TimeSpan UnansweredAfter = LicenseContract.SignInNotAfter + LicenseContract.SignInCommandTimeout;

    private readonly LicenseSignInLog _log;
    private readonly LicenseReadModel _reads;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly LicenseCommandSender _send;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _lifetime;

    // What is checked and written for a start happens one at a time, so two requests at the same moment cannot both
    // find no sign-in running and write two rows. Never held across a send.
    private readonly object _starting = new();
    // The sign-ins whose command is in flight in THIS process: never settled from a report while their answer may come.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SignInId), Task> _inFlight = new();

    internal LicenseSignInService(LicenseSignInLog log, LicenseReadModel reads, Func<TenantId, string, bool> isConnected,
        LicenseCommandSender send, Func<TenantId, IDisposable> enterScope, Func<DateTime> nowUtc, CancellationToken lifetime)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _lifetime = lifetime;
    }

    /// <summary>
    /// Start one sign-in and return at once.
    /// </summary>
    /// <param name="reason"><see cref="LicenseSignInReasons.Manual"/> for "Add a spare",
    /// <see cref="LicenseSignInReasons.Renewal"/> for "Renew now".</param>
    /// <param name="machineId">The machine's identifier as the overview gives it.</param>
    public LicenseSignInStart Start(TenantId tenant, string? accountEmail, string? machineId, string reason)
    {
        if (reason is not (LicenseSignInReasons.Manual or LicenseSignInReasons.Renewal))
            throw new ArgumentException($"'{reason}' is not a sign-in reason.", nameof(reason));
        var email = LicenseStore.NormalizeEmail(accountEmail);
        FileLog.Write($"[LicenseSignInService] Start: tenant={tenant.ToLogString()}, account={email}, machine={machineId}, reason={reason}");
        try
        {
            if (!LicenseApproverStore.IsAccountEmail(email))
                return Refuse(StatusCodes.BadRequest, "invalid_account", "accountEmail is the email of a Claude account.");
            if (!Guid.TryParseExact(machineId?.Trim(), "N", out var id))
                return Refuse(StatusCodes.BadRequest, "invalid_machine", "machineId is the machineId of a machine in the overview.");

            LicenseSwitchLogEntity row;
            LicenseMachineView machine;
            string label;
            lock (_starting)
            {
                SettleUnanswered(tenant);
                var now = _nowUtc();
                var machines = _reads.Read(tenant, now).Machines;
                var found = machines.FirstOrDefault(m => m.MachineId == id);
                if (found is null)
                    return Refuse(StatusCodes.NotFound, "unknown_machine", "No machine with that identifier has reported to this account.");
                machine = found;
                label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);

                var holds = machine.Holdings.Any(h => h.AccountEmail == email);
                if (reason == LicenseSignInReasons.Manual && holds)
                    return Refuse(StatusCodes.Conflict, "already_held", LicenseSignInFold.AlreadyHeldText(label, email));
                if (reason == LicenseSignInReasons.Renewal && !holds)
                    return Refuse(StatusCodes.Conflict, "not_held", LicenseSignInFold.NotHeldText(label, email));
                if (_log.ListOpen(tenant).Any(r => r.MachineId == machine.MachineId && r.ToAccountEmail == email))
                    return Refuse(StatusCodes.Conflict, "sign_in_running", LicenseSignInFold.AlreadyRunningText(label, email));
                if (!_isConnected(tenant, machine.DirectorId))
                    return Refuse(StatusCodes.Conflict, "director_offline", LicenseSignInFold.DirectorOfflineText(label));

                row = _log.CreatePending(tenant, machine, email, reason, LicenseSignInFold.AskedText(label, email, reason), now);
            }

            var signInId = row.SwitchId!;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight[(tenant, signInId)] = done.Task;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(tenant, row, machine, label).ConfigureAwait(false);
                }
                finally
                {
                    _inFlight.TryRemove((tenant, signInId), out _);
                    done.SetResult();
                }
            });
            FileLog.Write($"[LicenseSignInService] Start: signIn={signInId} written and being sent to director={machine.DirectorId}");
            return new LicenseSignInStart(true, StatusCodes.Accepted, null, null, row, machine, label);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInService] Start FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Completes when every sign-in started so far has recorded its result. For tests, which wait on this
    /// and never on a clock.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.ToArray());

    /// <summary>
    /// Settle this tenant's sign-in rows that are still open with no answer possible any more: not in flight in this
    /// process, and older than <see cref="UnansweredAfter"/>. Each ends as what the machine's latest report shows - a
    /// new spare that is held is <c>added</c>, a renewal whose sign-in is newer than the row is <c>renewed</c>, and
    /// anything else <c>failed</c>. Returns how many it settled. A tenant with no such row costs one read and no line.
    /// </summary>
    public int SettleUnanswered(TenantId tenant)
    {
        var open = _log.ListOpen(tenant);
        if (open.Count == 0)
            return 0;
        var now = _nowUtc();
        var due = open.Where(r => !_inFlight.ContainsKey((tenant, r.SwitchId!)) && now - r.CreatedAtUtc > UnansweredAfter).ToList();
        if (due.Count == 0)
            return 0;

        var machines = _reads.Read(tenant, now).Machines;
        foreach (var row in due)
        {
            var machine = machines.FirstOrDefault(m => m.MachineId == row.MachineId);
            var label = LicenseSignInFold.Label(machines, row.MachineName, row.OsUser);
            var holding = machine?.Holdings.FirstOrDefault(h => h.AccountEmail == row.ToAccountEmail);
            var made = row.Reason == LicenseSignInReasons.Renewal
                ? holding?.SignedInAtUtc is { } signedIn && signedIn > row.CreatedAtUtc
                : holding is not null;
            var state = !made ? LicenseSignInOutcomes.Failed
                : row.Reason == LicenseSignInReasons.Renewal ? LicenseSignInOutcomes.Renewed
                : LicenseSignInOutcomes.Added;
            _log.TryFinish(tenant, row.SwitchId!, state, null, LicenseSignInFold.SettledFromReportText(label, row.ToAccountEmail, row.Reason, made), now);
        }
        FileLog.Write($"[LicenseSignInService] SettleUnanswered: tenant={tenant.ToLogString()}, settled={due.Count}");
        return due.Count;
    }

    /// <summary>One sign-in: send it, and write what came back on its row. The entry point of a background task, so
    /// nothing above it would see a failure: it never throws.</summary>
    private async Task RunAsync(TenantId tenant, LicenseSwitchLogEntity row, LicenseMachineView machine, string label)
    {
        var signInId = row.SwitchId!;
        using var scope = _enterScope(tenant);
        try
        {
            var request = new LicenseSignInRequest
            {
                SignInId = signInId,
                AccountEmail = row.ToAccountEmail,
                Reason = row.Reason!,
                NotAfterUtc = row.NotAfterUtc!.Value,
            };
            var sentAt = _nowUtc();
            var result = await _send(machine.DirectorId, LicenseContract.SignInVerb, request, LicenseContract.SignInCommandTimeout,
                machine.MachineName, _lifetime).ConfigureAwait(false);
            Record(tenant, signInId, machine, label, sentAt, result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The row stays open; the Gateway that starts next settles it from the machine's report.
            FileLog.Write($"[LicenseSignInService] signIn={signInId}: stopped, the Gateway is stopping");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInService] signIn={signInId} FAILED: {ex.GetType().Name}: {ex.Message}");
            _log.TryFinish(tenant, signInId, LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorError,
                $"The Gateway failed while sending the sign-in to {label} ({ex.GetType().Name}).", _nowUtc());
        }
    }

    private void Record(TenantId tenant, string signInId, LicenseMachineView machine, string label, DateTime sentAtUtc,
        DirectorCommandResult? result)
    {
        var now = _nowUtc();
        if (result is null)
        {
            _log.TryFinish(tenant, signInId, LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                $"The Director on {label} was not connected when the sign-in was sent, so nothing was started.", now);
            FileLog.Write($"[LicenseSignInService] signIn={signInId}: the Director is not connected, nothing was sent");
            return;
        }

        _log.RecordSend(tenant, signInId, machine.DirectorId, sentAtUtc);
        switch (result.Status)
        {
            case DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped:
                _log.TryMarkUnknown(tenant, signInId, result.Error);
                break;
            case DirectorCommandStatus.Ok when ReadAnswer(result, signInId) is { } answer:
                _log.TryFinish(tenant, signInId, answer.Outcome, answer.ReasonCode, answer.Message, now,
                    answer.FinishedAtUtc == default ? null : answer.FinishedAtUtc, answer.Replayed);
                break;
            case DirectorCommandStatus.Ok:
                _log.TryFinish(tenant, signInId, LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorError,
                    $"The Director on {label} answered, but its answer could not be read as the result of this sign-in.", now);
                break;
            default:
                _log.TryFinish(tenant, signInId, LicenseSignInOutcomes.Failed, LicenseGatewayReasonCodes.DirectorError,
                    $"The Director on {label} returned {result.Status}.", now);
                break;
        }
        var recorded = _log.Find(tenant, signInId);
        FileLog.Write($"[LicenseSignInService] signIn={signInId}: account={recorded?.ToAccountEmail}, machine={machine.MachineName}, "
                      + $"director={machine.DirectorId}, status={result.Status}, outcome={recorded?.State}, reason={recorded?.ReasonCode ?? "-"}");
    }

    /// <summary>The Director's answer, or null when the body is not the result of THIS sign-in with an outcome this
    /// Gateway knows - which is recorded as a Director error, never stored as if it were an outcome.</summary>
    private static LicenseSignInResult? ReadAnswer(DirectorCommandResult result, string signInId)
    {
        if (string.IsNullOrWhiteSpace(result.BodyJson))
            return null;
        LicenseSignInResult? answer;
        try
        {
            answer = JsonSerializer.Deserialize<LicenseSignInResult>(result.BodyJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[LicenseSignInService] signIn={signInId}: the Director's answer is not readable: {ex.Message}");
            return null;
        }
        return answer is not null
               && string.Equals(answer.SignInId, signInId, StringComparison.Ordinal)
               && answer.Outcome is LicenseSignInOutcomes.Added or LicenseSignInOutcomes.Renewed or LicenseSignInOutcomes.Failed
            ? answer
            : null;
    }

    private static LicenseSignInStart Refuse(int status, string word, string sentence)
    {
        FileLog.Write($"[LicenseSignInService] Start: REFUSED {word}");
        return new LicenseSignInStart(false, status, word, sentence, null, null, null);
    }

    /// <summary>The statuses a start answers with, named so this class needs no web types.</summary>
    private static class StatusCodes
    {
        public const int Accepted = 202;
        public const int BadRequest = 400;
        public const int NotFound = 404;
        public const int Conflict = 409;
    }
}
