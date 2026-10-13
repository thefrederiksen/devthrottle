using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

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
/// AN OPEN ROW IS SETTLED BY WHAT HAPPENED, NEVER BY A GUESS. A row with no answer - the wait ended, the tunnel
/// dropped, or the Gateway restarted while it waited - is settled by <see cref="SettleUnansweredAsync"/> once no
/// Director could still be running it, in this order:
///
/// 1. THE DIRECTOR IS ASKED AGAIN, with the same identifier (contract sections 8.4 and 3.6). A Director that ran the
///    sign-in answers with its stored result; one that never saw it answers <c>failed</c> / <c>expired</c>, because
///    the request is past its own <c>NotAfterUtc</c>. Either is what really happened, and neither can start anything.
/// 2. ONLY WHEN NO SUCH ANSWER CAN BE HAD - the Director is not connected, or did not answer - the row is settled
///    from the machine's report, and then only from a report the Gateway accepted AFTER the last moment a Director
///    could still have been running the sign-in. A report from before that says nothing about how it ended, so until
///    one arrives the row stays open. A new spare is <c>added</c> when that report shows the machine holds it. A
///    renewal is <c>renewed</c> when the sign-in's own time in that report DIFFERS from the one the machine had
///    reported before the sign-in was asked for - never by comparing the machine's clock with the Gateway's, which
///    may be minutes apart.
///
/// The host calls it once a minute. While a row is open, a second sign-in for that machine and account is refused.
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

    /// <summary>How long the Gateway waits for a Director's answer to a sign-in asked again. The answer is a stored
    /// result or a refusal, so it comes at once or not at all.</summary>
    public static readonly TimeSpan AskAgainWait = TimeSpan.FromSeconds(60);

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
    // The sign-ins whose command is in flight in THIS process: never settled while their answer may come.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SignInId), Task> _inFlight = new();
    // The tenants whose open rows are being settled now: a pass that finds the last one still asking a Director skips.
    private readonly ConcurrentDictionary<TenantId, byte> _settling = new();

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
        // Nothing the caller sent is written to the log before it has been checked.
        FileLog.Write($"[LicenseSignInService] Start: tenant={tenant.ToLogString()}, reason={reason}");
        try
        {
            if (!LicenseApproverStore.IsAccountEmail(email))
                return Refuse(StatusCodes.BadRequest, "invalid_account", "accountEmail is the email of a Claude account.");
            if (!Guid.TryParseExact(machineId?.Trim(), "N", out var id))
                return Refuse(StatusCodes.BadRequest, "invalid_machine", "machineId is the machineId of a machine in the overview.");
            FileLog.Write($"[LicenseSignInService] Start: account={email}, machine={id:N}");

            LicenseSwitchLogEntity row;
            LicenseMachineView machine;
            string label;
            lock (_starting)
            {
                var now = _nowUtc();
                var machines = _reads.Read(tenant, now).Machines;
                var found = machines.FirstOrDefault(m => m.MachineId == id);
                if (found is null)
                    return Refuse(StatusCodes.NotFound, "unknown_machine", "No machine with that identifier has reported to this account.");
                machine = found;
                label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);

                var held = machine.Holdings.FirstOrDefault(h => h.AccountEmail == email);
                var holds = held is not null;
                if (reason == LicenseSignInReasons.Manual && holds)
                    return Refuse(StatusCodes.Conflict, "already_held", LicenseSignInFold.AlreadyHeldText(label, email));
                if (reason == LicenseSignInReasons.Renewal && !holds)
                    return Refuse(StatusCodes.Conflict, "not_held", LicenseSignInFold.NotHeldText(label, email));
                if (_log.ListOpen(tenant).Any(r => r.MachineId == machine.MachineId && r.ToAccountEmail == email))
                    return Refuse(StatusCodes.Conflict, "sign_in_running", LicenseSignInFold.AlreadyRunningText(label, email));
                if (!_isConnected(tenant, machine.DirectorId))
                    return Refuse(StatusCodes.Conflict, "director_offline", LicenseSignInFold.DirectorOfflineText(label));

                row = _log.CreatePending(tenant, machine, email, reason, LicenseSignInFold.AskedText(label, email, reason), now,
                    held?.SignedInAtUtc);
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
    /// <see cref="SettleUnansweredAsync"/> for every tenant, each inside its own scope. One tenant's failure is logged
    /// and does not stop another tenant's rows from being settled, and one Director that does not answer holds up only
    /// its own tenant.
    /// </summary>
    public async Task SettleEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var passes = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                // No scope in effect: settle nothing, never a tenant of our own choosing.
                if (tenants.Current is { } tenant)
                    passes.Add(SettleOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            // Every pass that was started is waited for, also when the walk over the tenants stopped part-way.
            await Task.WhenAll(passes).ConfigureAwait(false);
        }
    }

    private async Task SettleOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await SettleUnansweredAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The boundary of one tenant's pass: its failure must never cost another tenant its settling. The TYPE
            // only, as everywhere a Director's answer may be in reach.
            FileLog.Write($"[LicenseSignInService] SettleEveryTenantAsync: FAILED for tenant={tenant.ToLogString()}: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Settle this tenant's sign-in rows that are still open with no answer possible any more: not in flight in this
    /// process, and older than <see cref="UnansweredAfter"/>. Each is settled as the class says - by asking its
    /// Director again, and only when that gives no answer, from a report accepted after the last moment the sign-in
    /// could still have been running. A row that cannot be settled yet stays open. Returns how many it settled. A
    /// tenant with no such row costs one read and no line.
    /// </summary>
    public async Task<int> SettleUnansweredAsync(TenantId tenant, CancellationToken ct)
    {
        var open = _log.ListOpen(tenant);
        if (open.Count == 0)
            return 0;
        var now = _nowUtc();
        var due = open.Where(r => !_inFlight.ContainsKey((tenant, r.SwitchId!)) && now - r.CreatedAtUtc > UnansweredAfter).ToList();
        if (due.Count == 0)
            return 0;
        if (!_settling.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseSignInService] SettleUnansweredAsync: tenant={tenant.ToLogString()}, the previous pass has not finished - skipped");
            return 0;
        }
        IDisposable? scope = null;
        try
        {
            scope = _enterScope(tenant);
            var settled = 0;
            foreach (var row in due)
            {
                if (await SettleOneAsync(tenant, row, ct).ConfigureAwait(false))
                    settled++;
            }
            // A pass that settled nothing says nothing: a row waiting for its machine is looked at every minute.
            if (settled > 0)
                FileLog.Write($"[LicenseSignInService] SettleUnansweredAsync: tenant={tenant.ToLogString()}, settled={settled} of {due.Count}");
            return settled;
        }
        finally
        {
            scope?.Dispose();
            _settling.TryRemove(tenant, out _);
        }
    }

    /// <summary>Settle one row that is due. True when it ended; false when nothing can be known yet and it stays open.</summary>
    private async Task<bool> SettleOneAsync(TenantId tenant, LicenseSwitchLogEntity row, CancellationToken ct)
    {
        var signInId = row.SwitchId!;
        var machine = _reads.Read(tenant, _nowUtc()).Machines.FirstOrDefault(m => m.MachineId == row.MachineId);

        // 1. Ask the Director again. The request is past its NotAfterUtc, so a Director that never saw it starts
        //    nothing and says so; one that ran it answers with what it stored.
        if (machine is not null && _isConnected(tenant, machine.DirectorId))
        {
            var request = new LicenseSignInRequest
            {
                SignInId = signInId,
                AccountEmail = row.ToAccountEmail,
                Reason = row.Reason!,
                NotAfterUtc = row.NotAfterUtc!.Value,
            };
            var sentAt = _nowUtc();
            var result = await _send(machine.DirectorId, LicenseContract.SignInVerb, request, AskAgainWait, machine.MachineName, ct)
                .ConfigureAwait(false);
            if (result is not null)
                _log.RecordSend(tenant, signInId, machine.DirectorId, sentAt);
            if (result is { Status: DirectorCommandStatus.Ok } && ReadAnswer(result, signInId) is { } answer)
            {
                var ended = _log.TryFinish(tenant, signInId, answer.Outcome, answer.ReasonCode, answer.Message, _nowUtc(),
                    answer.FinishedAtUtc == default ? null : answer.FinishedAtUtc, answer.Replayed);
                FileLog.Write($"[LicenseSignInService] signIn={signInId}: asked again, director={machine.DirectorId}, outcome={answer.Outcome}, "
                              + $"reason={answer.ReasonCode ?? "-"}, replayed={answer.Replayed}, ended={ended}");
                return ended;
            }
            FileLog.Write($"[LicenseSignInService] signIn={signInId}: asked again, director={machine.DirectorId}, no answer that settles it "
                          + $"({(result is null ? "not connected" : result.Status.ToString())})");
        }

        // 2. The machine's report - but only one the Gateway accepted after the last moment a Director could still
        //    have been running this sign-in. Read again: asking may have taken a minute.
        var now = _nowUtc();
        var machines = _reads.Read(tenant, now).Machines;
        machine = machines.FirstOrDefault(m => m.MachineId == row.MachineId);
        if (machine is null || machine.LastReportAtUtc <= row.CreatedAtUtc + UnansweredAfter)
            return false;

        var label = LicenseSignInFold.Label(machines, row.MachineName, row.OsUser);
        var holding = machine.Holdings.FirstOrDefault(h => h.AccountEmail == row.ToAccountEmail);
        var isRenewal = row.Reason == LicenseSignInReasons.Renewal;
        // A renewal was made when the sign-in's own time is not the one the machine had reported before the ask
        // (kept on the row). Two times of the machine's own clock are compared, never one with the Gateway's.
        var made = isRenewal
            ? holding?.SignedInAtUtc is { } signedIn && signedIn != LicenseSignInLog.SignedInBefore(row)
            : holding is not null;
        var state = !made ? LicenseSignInOutcomes.Failed : isRenewal ? LicenseSignInOutcomes.Renewed : LicenseSignInOutcomes.Added;
        return _log.TryFinish(tenant, signInId, state, null,
            LicenseSignInFold.SettledFromReportText(label, row.ToAccountEmail, row.Reason, made), now);
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
            // The row stays open; the Gateway that starts next settles it (SettleUnansweredAsync).
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
