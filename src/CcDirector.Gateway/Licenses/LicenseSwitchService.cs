using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The answer to Switch now. Either <see cref="Accepted"/> is true and <see cref="Switches"/> has one entry per machine
/// that was sent a switch, or it is false and <see cref="RefusalCode"/> and <see cref="RefusalMessage"/> say why
/// nothing was sent. The contract for the Cockpit API is <c>missions/license-switching/switch-now-method.md</c>.
/// </summary>
/// <param name="Accepted">True when at least one switch was written and sent.</param>
/// <param name="RefusalCode">A <see cref="LicenseManualRefusals"/> word when refused, else null.</param>
/// <param name="RefusalMessage">One plain sentence when refused, else null.</param>
/// <param name="TargetAccountEmail">The account the machines were sent to, lower-cased; null when refused, and null when a
/// Switch now with no target sent them to more than one account (each switch names its own).</param>
/// <param name="Switches">One per machine sent a switch; empty when refused.</param>
/// <param name="Skipped">Machines on the source account that were not sent a switch, and why.</param>
public sealed record LicenseSwitchNowResult(
    bool Accepted,
    string? RefusalCode,
    string? RefusalMessage,
    string? TargetAccountEmail,
    IReadOnlyList<LicenseSwitchNowMachine> Switches,
    IReadOnlyList<LicenseSkippedMachine> Skipped);

/// <summary>One machine's switch, as its switch log row stood when <see cref="LicenseSwitchService.SwitchNowAsync"/> returned.</summary>
/// <param name="SwitchId">The switch log row's identifier.</param>
/// <param name="MachineName">The machine it was sent to.</param>
/// <param name="OsUser">The machine's user.</param>
/// <param name="State">A <see cref="LicenseLogStates"/> word.</param>
/// <param name="ReasonCode">Why it is pending or failed, else null.</param>
/// <param name="Message">One plain sentence for the row; may be null.</param>
public sealed record LicenseSwitchNowMachine(
    string SwitchId,
    string MachineName,
    string OsUser,
    string State,
    string? ReasonCode,
    string? Message);

/// <summary>
/// THE MINUTE CHECK AND SWITCH NOW (License Switching, MISSION.md section 5 "Gateway", contract sections 3 and 5): the
/// part of the Gateway that actually switches. Once a minute, for one tenant, <see cref="TickAsync"/> builds the picture
/// the decision engine takes, asks <see cref="LicenseDecisionEngine.Decide"/>, and carries out what it says: switches
/// sent, bars recorded, "no account has room" events opened, moved and closed, and the one owner email per event.
/// <see cref="SwitchNowAsync"/> is the same thing on demand for one account, through the same send.
///
/// A SWITCH IS SENT EXACTLY AS CONTRACT SECTION 3 SAYS. The pending log row is written FIRST, with a new switch
/// identifier; then the command goes to the Director that sent the machine's last accepted report, with a 60-second
/// wait; then the row records what came back (section 3.5):
/// - nothing came back because the Director is not connected: the row stays <c>pending</c>, <c>director-offline</c>;
/// - no answer in time, or the tunnel dropped: <c>unknown</c>;
/// - the Director refused the command or failed: <c>failed</c>, <c>director-error</c>;
/// - an answer: its outcome, and the machine's active account from the answer's read-back unless the machine has
///   reported since this Gateway sent the command (<see cref="LicenseStore.ApplySwitchReadBack"/>).
///
/// AN OPEN ROW IS FINISHED, NEVER ABANDONED (sections 5.1 and 5.5), and it is always the SAME switch identifier that
/// is sent again, so the Director answers from its store and no switch runs twice:
/// - A row known never to have left (<c>pending</c>, <c>director-offline</c>) is sent again on each check on which the
///   engine still makes the same decision for that machine, until its <c>NotAfterUtc</c>; after that it ends
///   <c>failed</c>, <c>director-offline</c>, and the next check decides afresh with a new identifier. Such a row does
///   not hold the machine back from a different decision: it is ended first, then the new one is written.
/// - A row whose outcome is not known (<c>unknown</c>, or <c>pending</c> with no reason, which is what a Gateway
///   restart mid-send leaves) is sent again once the machine has reported since the last send - before
///   <c>NotAfterUtc</c> on every such check, after it exactly once more. When even that brings no answer, the row is
///   settled from the machine's own report: <c>switched</c> when the report shows the target active, otherwise
///   <c>failed</c>, and the message says it was settled from the report.
/// - A machine with a row whose outcome is not known is never sent a second switch identifier (section 5.4).
/// - "Known never to have left" is true only while nothing is in flight: before such a row is sent again it is
///   written back to <c>pending</c> with no reason, so a send that is cut off part-way leaves a row that reads as
///   "outcome not known". <c>director-offline</c> is put back when the send returns nothing.
///
/// SENDS DO NOT WAIT FOR ONE ANOTHER, AND TENANTS DO NOT WAIT FOR ONE ANOTHER. Every send of a check is started
/// together and each waits at most its own 60 seconds; every tenant's check is started inside its own scope and they
/// are awaited together, so a Director that does not answer holds up nobody else. That holds across checks too: the
/// host starts a pass every minute whether or not the last one has finished, and a tenant whose own check is still
/// waiting is the only one skipped (<see cref="TickAsync"/>). No database context is open while a send is in flight: the store opens and closes one per call. What is
/// decided and written (rows, bars, events) happens under a short per-tenant gate that the minute check and Switch now
/// share, so the two can never write two open rows for one machine; the gate is released before anything is sent.
///
/// A SWITCH THAT KEEPS FAILING IS NOT DECIDED AGAIN EVERY MINUTE. When a Director answers a switch <c>failed</c> - or
/// answers with an error - and that is at least the second switch in a row on that machine to fail with the same
/// reason word for the same from and to, the machine is held out of the AUTOMATIC decision for a wait that grows: one
/// minute, two, four, up to an hour (<see cref="LicenseStore.HoldAfterRepeatedFailure"/>). The hold is recorded on the
/// failed row itself, so a Gateway that restarts keeps it. When every machine of a tired account that could be sent a
/// switch is held, the engine treats it as the no-room case, so the owner is told once, by the one email of the
/// event. Switch now ignores a hold, as it ignores a bar, and a Switch now that fails the same way again is one more
/// failure of the run.
///
/// ONE OWNER EMAIL PER EVENT. The event is recorded as emailed only when the mail service confirmed the send. A send
/// that failed is tried again on a later check, with the text rebuilt from that check's facts
/// (<see cref="LicenseDecisionEngine.NoRoomEmail"/>); once it has gone out it never goes again. The tries of one
/// event move apart - one minute, two, four, up to an hour, or longer when the mail service asks for longer - so a
/// send that keeps being refused is not tried every minute for the days an event can last. A Gateway that stops
/// between the service's confirmation and the row being marked would send once more after it restarts - the only way
/// to rule that out would be to mark the row before sending, which would lose the email instead.
///
/// INERT UNTIL A DIRECTOR REPORTS. A tenant none of whose machines has ever sent a usage report is left alone: one
/// quiet read, nothing written, no log line. A check that finds nothing to do writes no line of its own either.
/// </summary>
public sealed class LicenseSwitchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _send;
    private readonly LicenseOwnerEmailSender _mail;
    private readonly Func<DateTime> _nowUtc;

    // What is decided and written for one tenant happens one at a time; never held across a send.
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _deciding = new();
    // One minute check per tenant at a time, so one event's email can never be sent by two checks at once.
    private readonly ConcurrentDictionary<TenantId, byte> _ticking = new();
    // Switches whose send is in flight in THIS process: not sent again until that send has recorded its result.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SwitchId), byte> _inFlight = new();
    // Rows whose outcome is not known and which have had their one send after NotAfterUtc (contract section 5.5).
    private readonly ConcurrentDictionary<(TenantId Tenant, string SwitchId), byte> _sentAfterExpiry = new();
    // The next try of this event's email is not before this time: the Gateway's own growing wait, or the mail
    // service's when it asked for a longer one.
    private readonly ConcurrentDictionary<(TenantId Tenant, string AccountEmail), DateTime> _emailNotBefore = new();
    // How many tries of this event's email have failed in a row; it sets the growing wait.
    private readonly ConcurrentDictionary<(TenantId Tenant, string AccountEmail), int> _emailFailures = new();
    // Emails the mail service confirmed whose event's row is not marked yet: never sent again by this process. With
    // when it went, and what the event's row says happened.
    private readonly ConcurrentDictionary<(TenantId Tenant, string AccountEmail), (DateTime SentAtUtc, string Happened)> _emailConfirmed = new();

    /// <summary>The longest the Gateway itself waits between two tries of one event's owner email.</summary>
    internal static readonly TimeSpan LongestOwnEmailWait = TimeSpan.FromHours(1);

    /// <param name="store">The switch log, the bars and the no-room events.</param>
    /// <param name="snapshots">The one place the engine's picture of a tenant is built from the store.</param>
    /// <param name="send">The one way a command goes down a Director's stream (<c>GatewayHost.SendCommandAsync</c>). It
    /// reads the tenant scope in effect, so <see cref="TickAsync"/> and <see cref="SwitchNowAsync"/> must be called
    /// inside the tenant's scope.</param>
    /// <param name="mail">Sends the owner email (<see cref="LicenseOwnerMailer.SendAsync"/>).</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    internal LicenseSwitchService(LicenseStore store, LicenseSnapshotSource snapshots,
        DirectorCommandRouter.SendDirectorCommandAsync send, LicenseOwnerEmailSender mail, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    // ----- the minute check -----

    /// <summary>
    /// The minute check for every tenant: one <see cref="TickAsync"/> per tenant, each STARTED inside that tenant's
    /// scope (contract section 6 rule 4) and all awaited together. What a check decides and writes happens before its
    /// first wait, so tenants are decided one after another and only their sends and emails overlap; the scope is
    /// ambient, so everything a check does after a wait still runs in the scope it was started in. One tenant's
    /// failure is logged and does not stop another tenant's check, and one tenant's Director that does not answer
    /// does not delay another tenant's switch.
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
            // The boundary of one tenant's check: its failure must never cost another tenant its switch.
            FileLog.Write($"[LicenseSwitchService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// One minute check for one tenant. Returns when every send and every owner email it started has recorded its
    /// result. Must be called inside the tenant's scope.
    /// </summary>
    public async Task TickAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant))
            return; // inert until a Director reports: nothing read, nothing written, nothing logged
        if (!_ticking.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseSwitchService] TickAsync: tenant={tenant.ToLogString()}, the previous check has not finished - skipped");
            return;
        }
        IReadOnlyList<PlannedSend> marked = Array.Empty<PlannedSend>();
        try
        {
            TickPlan plan;
            var gate = GateFor(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                plan = PlanTick(tenant);
                // Marked only once the whole plan stands, and still under the gate: a plan that failed part-way
                // marks nothing, so no switch can be left marked with no send to clear it.
                marked = MarkInFlight(tenant, plan.Sends);
            }
            finally
            {
                gate.Release();
            }

            var work = plan.Sends.Select(send => SendAsync(tenant, send, ct))
                .Concat(plan.Emails.Select(email => EmailAsync(tenant, email, ct)))
                .ToList();
            await Task.WhenAll(work).ConfigureAwait(false);
            // A check that found nothing to do says nothing: it runs every minute for every tenant, for good.
            if (plan.Sends.Count + plan.Emails.Count + plan.Written > 0)
                FileLog.Write($"[LicenseSwitchService] TickAsync: tenant={tenant.ToLogString()}, sends={plan.Sends.Count}, emails={plan.Emails.Count}, written={plan.Written}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSwitchService] TickAsync FAILED: tenant={tenant.ToLogString()}: {ex.Message}");
            throw;
        }
        finally
        {
            ClearInFlight(tenant, marked);
            _ticking.TryRemove(tenant, out _);
        }
    }

    // ----- Switch now -----

    /// <summary>
    /// Switch now: move every machine on <paramref name="sourceAccountEmail"/> that can be sent a switch to
    /// <paramref name="targetAccountEmail"/>, or to the best target when none is named. Runs
    /// <see cref="LicenseDecisionEngine.DecideManual"/> and sends through the same path as the minute check, with reason
    /// <c>manual</c>. Returns the refusal, or - once every machine has answered or its 60-second wait has ended - the
    /// switch identifiers with where each row stood. Must be called inside the tenant's scope, which a request is.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourceAccountEmail"/> is null or blank.</exception>
    public async Task<LicenseSwitchNowResult> SwitchNowAsync(TenantId tenant, string sourceAccountEmail, string? targetAccountEmail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceAccountEmail))
            throw new ArgumentException("The account to switch away from is required", nameof(sourceAccountEmail));
        FileLog.Write($"[LicenseSwitchService] SwitchNowAsync: tenant={tenant.ToLogString()}, targetNamed={!string.IsNullOrWhiteSpace(targetAccountEmail)}");
        IReadOnlyList<PlannedSend> marked = Array.Empty<PlannedSend>();
        try
        {
            var sends = new List<PlannedSend>();
            var skipped = new List<LicenseSkippedMachine>();
            // The switch identifiers this call answers with, in the order the machines were decided: the ones it
            // wrote and sent, and the ones already waiting that it left exactly as they were.
            var answered = new List<string>();
            var alreadyWaiting = 0;
            var gate = GateFor(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _nowUtc();
                var picture = _snapshots.ReadPicture(tenant, now, Exhaustions(_store.ListOpenNoRoom(tenant)));
                var overview = picture.Overview;
                var open = Classify(tenant, picture.OpenSwitches);
                var snapshot = SnapshotToDecideOn(picture, open.Blocked);
                var verdict = LicenseDecisionEngine.DecideManual(snapshot, sourceAccountEmail, targetAccountEmail);
                if (verdict.RefusalCode is { } code)
                {
                    FileLog.Write($"[LicenseSwitchService] SwitchNowAsync: refused {code}");
                    return new LicenseSwitchNowResult(false, code, verdict.RefusalMessage, null,
                        Array.Empty<LicenseSwitchNowMachine>(), verdict.Skipped);
                }

                skipped.AddRange(verdict.Skipped);
                var machines = MachinesByName(overview);
                foreach (var decision in verdict.Switches)
                {
                    var machine =machines[MachineKey(decision.MachineName, decision.OsUser)];
                    // A new Switch now is a new decision, so it gets a new switch identifier (contract section 3.6):
                    // a switch still waiting for an offline Director is ended first, never sent beside the new one.
                    if (open.NeverSent.TryGetValue(machine.MachineId, out var waiting))
                    {
                        // The SAME switch is already waiting for this machine's Director - same account to leave,
                        // same target, not yet expired. Asking again changes nothing, so nothing is written: the
                        // answer is the waiting switch as it stands, and the minute check goes on sending it. Without
                        // this every click while a Director is offline ended one row as failed and wrote another.
                        if (now <= NotAfterOf(waiting)
                            && string.Equals(waiting.FromAccountEmail, decision.FromAccountEmail, StringComparison.Ordinal)
                            && string.Equals(waiting.ToAccountEmail, decision.TargetAccountEmail, StringComparison.Ordinal))
                        {
                            answered.Add(IdOf(waiting));
                            alreadyWaiting++;
                            continue;
                        }
                        _store.TryMark(tenant, IdOf(waiting), LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                            $"The Director on {machine.MachineName} was not connected, and a new Switch now replaced this switch before it could be sent.", now);
                    }
                    var planned = PlanNewSwitch(tenant, machine, decision, now);
                    if (planned is null)
                        skipped.Add(new LicenseSkippedMachine(machine.MachineName, machine.OsUser, decision.FromAccountEmail,
                            "a switch sent to it has not finished"));
                    else
                    {
                        sends.Add(planned);
                        answered.Add(planned.SwitchId);
                    }
                }
                marked = MarkInFlight(tenant, sends);
            }
            finally
            {
                gate.Release();
            }

            if (answered.Count == 0)
            {
                FileLog.Write("[LicenseSwitchService] SwitchNowAsync: refused, every machine was taken by another switch at the same moment");
                return new LicenseSwitchNowResult(false, LicenseManualRefusals.NoMachineToSwitch,
                    "No machine on the account can be switched now: each already has a switch that has not finished.", null,
                    Array.Empty<LicenseSwitchNowMachine>(), skipped);
            }

            await Task.WhenAll(sends.Select(send => SendAsync(tenant, send, ct))).ConfigureAwait(false);

            var targets = new HashSet<string>(StringComparer.Ordinal);
            var switches = answered.Select(switchId =>
            {
                var row = _store.FindSwitch(tenant, switchId)
                    ?? throw new InvalidOperationException($"Switch {switchId} is in this answer, and its row is gone.");
                targets.Add(row.ToAccountEmail);
                return new LicenseSwitchNowMachine(switchId, row.MachineName, row.OsUser, row.State, row.ReasonCode, row.Message);
            }).ToList();
            // One account is named only when every switch in the answer went to it; split moves name none.
            var target = targets.Count == 1 ? targets.Single() : null;
            FileLog.Write($"[LicenseSwitchService] SwitchNowAsync: switches={switches.Count}, targets={targets.Count}, alreadyWaiting={alreadyWaiting}, skipped={skipped.Count}");
            return new LicenseSwitchNowResult(true, null, null, target, switches, skipped);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSwitchService] SwitchNowAsync FAILED: {ex.Message}");
            throw;
        }
        finally
        {
            ClearInFlight(tenant, marked);
        }
    }

    // ----- what one check decides and writes (under the tenant's gate; nothing here waits on a Director) -----

    /// <param name="OutcomeUnknown">An earlier send of this row may have run.</param>
    /// <param name="AfterExpiry">This is the one send a row gets after its NotAfterUtc (contract section 5.5).</param>
    private sealed record PlannedSend(string SwitchId, Guid MachineId, string MachineName, string DirectorId,
        LicenseSwitchRequest Request, bool OutcomeUnknown, bool AfterExpiry);

    private sealed record PlannedEmail(string AccountEmail, LicenseOwnerEmail Email);

    /// <param name="Written">How many rows, bars and events this plan wrote besides the sends' own rows.</param>
    private sealed record TickPlan(List<PlannedSend> Sends, List<PlannedEmail> Emails, int Written);

    /// <summary>Mark every planned send as in flight in this process. Called under the tenant's gate, after the whole
    /// plan was made. Returns what it marked, for <see cref="ClearInFlight"/>.</summary>
    private IReadOnlyList<PlannedSend> MarkInFlight(TenantId tenant, IReadOnlyList<PlannedSend> sends)
    {
        foreach (var send in sends)
            _inFlight[(tenant, send.SwitchId)] = 0;
        return sends;
    }

    /// <summary>Clear the marks of a check or a Switch now on EVERY path out of it - finished, failed or cancelled -
    /// so a switch is never left marked with no send that will clear it.</summary>
    private void ClearInFlight(TenantId tenant, IReadOnlyList<PlannedSend> marked)
    {
        foreach (var send in marked)
            _inFlight.TryRemove((tenant, send.SwitchId), out _);
    }

    /// <summary>The open switch rows of one tenant, sorted by what may be done with each.</summary>
    /// <param name="Blocked">Machines that must not be sent another switch identifier: their open row's outcome is not
    /// known, or its send is in flight.</param>
    /// <param name="NeverSent">By machine: the open row known never to have left (the Director was not connected).</param>
    /// <param name="OutcomeUnknown">Rows that were or may have been sent, with no recorded outcome, not in flight.</param>
    private sealed record OpenRows(HashSet<Guid> Blocked, Dictionary<Guid, LicenseSwitchLogEntity> NeverSent,
        List<LicenseSwitchLogEntity> OutcomeUnknown);

    private OpenRows Classify(TenantId tenant, IReadOnlyList<LicenseSwitchLogEntity> open)
    {
        var blocked = new HashSet<Guid>();
        var neverSent = new Dictionary<Guid, LicenseSwitchLogEntity>();
        var outcomeUnknown = new List<LicenseSwitchLogEntity>();
        foreach (var row in open)
        {
            if (_inFlight.ContainsKey((tenant, IdOf(row))))
                blocked.Add(row.MachineId);
            else if (IsKnownNeverSent(row))
                neverSent[row.MachineId] = row;
            else
            {
                blocked.Add(row.MachineId);
                outcomeUnknown.Add(row);
            }
        }
        return new OpenRows(blocked, neverSent, outcomeUnknown);
    }

    private TickPlan PlanTick(TenantId tenant)
    {
        var now = _nowUtc();
        var noRoom = _store.ListOpenNoRoom(tenant);
        var picture = _snapshots.ReadPicture(tenant, now, Exhaustions(noRoom));
        var overview = picture.Overview;
        var machinesById = overview.Machines.ToDictionary(m => m.MachineId);
        var open = Classify(tenant, picture.OpenSwitches);
        var sends = new List<PlannedSend>();
        var written = 0;

        // 1. Rows whose outcome is not known: ask again with the same identifier, or settle from the machine's report.
        foreach (var row in open.OutcomeUnknown)
        {
            if (!machinesById.TryGetValue(row.MachineId, out var machine))
                throw new InvalidOperationException($"Switch {row.SwitchId} names machine {row.MachineId}, which this account does not have.");
            // Nothing is asked of a machine that has not reported since the last send: only a report shows it is
            // back, and only a report from after that send can show what the send did.
            if (machine.LastReportAtUtc <= (row.LastSentAtUtc ?? row.CreatedAtUtc))
                continue;
            var id = IdOf(row);
            var expired = now > NotAfterOf(row);
            if (!expired || !_sentAfterExpiry.ContainsKey((tenant, id)))
            {
                // The one send after NotAfterUtc is counted when it actually goes (RecordResult), not here: a plan
                // that fails, or a Director that turns out not to be connected, has asked nothing.
                sends.Add(PlanSend(row, machine, outcomeUnknown: true, afterExpiry: expired));
            }
            else if (machine.SwitchInProgressId is null)
            {
                // Past NotAfterUtc, sent once more, still no answer: the machine's own report is the answer.
                var switched = string.Equals(machine.ActiveAccountEmail, row.ToAccountEmail, StringComparison.Ordinal);
                _store.TryMark(tenant, id, switched ? LicenseLogStates.Switched : LicenseLogStates.Failed, null,
                    $"Settled from the machine's report: no answer to this switch could be had, and the report shows {machine.ActiveAccountEmail ?? "no account"} active.", now);
                _sentAfterExpiry.TryRemove((tenant, id), out _);
                open.Blocked.Remove(row.MachineId);
                written++;
            }
        }

        // 2. Rows known never to have left, past NotAfterUtc: they end, and this check decides afresh (section 5.1).
        foreach (var (machineId, row) in open.NeverSent.ToList())
        {
            if (now <= NotAfterOf(row))
                continue;
            _store.TryMark(tenant, IdOf(row), LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                $"The Director on {row.MachineName} was not connected until this switch expired at {Time(NotAfterOf(row))}; it was never sent.", now);
            open.NeverSent.Remove(machineId);
            written++;
        }

        // 3. Decide.
        var snapshot = SnapshotToDecideOn(picture, open.Blocked);
        var decisions = LicenseDecisionEngine.Decide(snapshot);

        // 4. Switches: the same decision as a waiting row is that row sent again; any other is a new row.
        var machines = MachinesByName(overview);
        foreach (var decision in decisions.Switches)
        {
            var machine = machines[MachineKey(decision.MachineName, decision.OsUser)];
            if (open.NeverSent.Remove(machine.MachineId, out var waiting))
            {
                if (SameDecision(waiting, decision))
                {
                    sends.Add(PlanSendOfWaiting(tenant, waiting, machine, now));
                    continue;
                }
                _store.TryMark(tenant, IdOf(waiting), LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                    $"The Director on {machine.MachineName} was not connected, and the decision changed before this switch could be sent.", now);
                written++;
            }
            if (PlanNewSwitch(tenant, machine, decision, now) is { } planned)
                sends.Add(planned);
        }
        // A waiting Switch now has no minute check to decide it again: it is sent again while the engine would still
        // accept the same Switch now for that machine. A waiting threshold switch the engine no longer decides is
        // simply not sent; it ends at its NotAfterUtc.
        foreach (var (machineId, waiting) in open.NeverSent)
        {
            if (waiting.Reason != LicenseSwitchReasons.Manual || !machinesById.TryGetValue(machineId, out var machine))
                continue;
            var verdict = LicenseDecisionEngine.DecideManual(snapshot, waiting.FromAccountEmail, waiting.ToAccountEmail);
            if (verdict.Switches.Any(d => MachineKey(d.MachineName, d.OsUser) == MachineKey(machine.MachineName, machine.OsUser)))
                sends.Add(PlanSendOfWaiting(tenant, waiting, machine, now));
        }

        // 5. Bars.
        foreach (var bar in decisions.NewBars)
            _store.Bar(tenant, bar.AccountEmail, bar.Window, bar.BarredUntilUtc, now);
        written += decisions.NewBars.Count + decisions.OpenedExhaustions.Count + decisions.ExtendedExhaustions.Count
                   + decisions.ClosedExhaustions.Count;

        // 6. No-room events: opened, moved, closed - and the one owner email of each.
        var emails = new List<PlannedEmail>();
        foreach (var opened in decisions.OpenedExhaustions)
        {
            // An event is opened together with its one email, and the email says which of the two it is: no
            // account had room, or the switch keeps failing.
            var its = decisions.Emails.Single(e => LicenseStore.NormalizeEmail(e.AccountEmail) == LicenseStore.NormalizeEmail(opened.AccountEmail));
            _store.OpenNoRoom(tenant, opened.AccountEmail, opened.StartedAtUtc, opened.EndsAtUtc, Happened(its));
        }
        foreach (var extended in decisions.ExtendedExhaustions)
            _store.ExtendNoRoom(tenant, extended.AccountEmail, extended.EndsAtUtc);
        foreach (var closed in decisions.ClosedExhaustions)
        {
            _store.CloseNoRoom(tenant, closed, now);
            ForgetEmailWait(tenant, LicenseStore.NormalizeEmail(closed));
            _emailConfirmed.TryRemove((tenant, LicenseStore.NormalizeEmail(closed)), out _);
        }
        foreach (var email in decisions.Emails)
            emails.Add(new PlannedEmail(LicenseStore.NormalizeEmail(email.AccountEmail), email));
        // An event from an earlier check whose email has not gone out: try again, with the text of THIS check's facts.
        var closedNow = decisions.ClosedExhaustions.Select(LicenseStore.NormalizeEmail).ToHashSet(StringComparer.Ordinal);
        foreach (var row in noRoom)
        {
            var account = row.FromAccountEmail;
            if (closedNow.Contains(account)
                || row.ReasonCode is LicenseGatewayReasonCodes.OwnerEmailed or LicenseGatewayReasonCodes.NoOwnerAccount)
                continue;
            if (_emailConfirmed.ContainsKey((tenant, account)))
            {
                // The mail service confirmed this event's email on an earlier check and the row could not be marked
                // then: mark it now, and never send it again.
                MarkEmailConfirmed(tenant, account);
                written++;
                continue;
            }
            if (_emailNotBefore.TryGetValue((tenant, account), out var notBefore) && now < notBefore)
                continue;
            if (LicenseDecisionEngine.NoRoomEmail(snapshot, account) is { } again)
                emails.Add(new PlannedEmail(account, again));
        }

        return new TickPlan(sends, emails, written);
    }

    private PlannedSend? PlanNewSwitch(TenantId tenant, LicenseMachineView machine, LicenseSwitchDecision decision, DateTime now)
    {
        var row = _store.CreatePendingSwitch(tenant, machine.MachineId, decision.FromAccountEmail, decision.TargetAccountEmail,
            decision.Reason, now, decision.Explanation);
        if (row is null)
        {
            FileLog.Write($"[LicenseSwitchService] no switch written for machine={machine.MachineId}: it already has one open");
            return null;
        }
        return PlanSend(row, machine, outcomeUnknown: false, afterExpiry: false);
    }

    /// <summary>
    /// Plan the next send of a row that is waiting for a Director that was not connected. The row says "it never
    /// left", and that stops being known the moment a command goes out, so the row is FIRST written back to
    /// <c>pending</c> with no reason. If the Gateway stops, or the result cannot be recorded, while that send is in
    /// flight, the row then reads as "outcome not known" and its machine is given no second switch identifier
    /// (contract section 5.4). <c>director-offline</c> is put back when the send returns nothing.
    /// </summary>
    private PlannedSend PlanSendOfWaiting(TenantId tenant, LicenseSwitchLogEntity waiting, LicenseMachineView machine, DateTime now)
    {
        _store.TryMark(tenant, IdOf(waiting), LicenseLogStates.Pending, null,
            $"Being sent again to the Director on {machine.MachineName}, which was not connected before.", now);
        return PlanSend(waiting, machine, outcomeUnknown: false, afterExpiry: false);
    }

    /// <summary>Plan one send of <paramref name="row"/> - its first or a repeat, always with the row's own identifier,
    /// times and accounts - to the Director that sent the machine's last accepted report, which need not be the
    /// Director the row was first written for (contract sections 2.4 and 6.4).</summary>
    private static PlannedSend PlanSend(LicenseSwitchLogEntity row, LicenseMachineView machine, bool outcomeUnknown, bool afterExpiry)
    {
        var id = IdOf(row);
        var request = new LicenseSwitchRequest
        {
            SwitchId = id,
            TargetAccountEmail = row.ToAccountEmail,
            ExpectedActiveAccountEmail = row.FromAccountEmail,
            Reason = row.Reason ?? throw new InvalidOperationException($"Switch {id} has no reason."),
            RequestedAtUtc = row.CreatedAtUtc,
            NotAfterUtc = NotAfterOf(row),
        };
        return new PlannedSend(id, machine.MachineId, machine.MachineName, machine.DirectorId, request, outcomeUnknown, afterExpiry);
    }

    /// <summary>
    /// The shared picture, with one mark judged here. <see cref="LicenseSnapshotSource"/> marks every machine that has
    /// an open switch row as mid-switch. For DECIDING - and for the Licenses page and the forecast, which show what Switch
    /// now would decide (<see cref="SnapshotSwitchNowDecidesOn"/>) -
    /// a row known never to have left its Gateway does not hold its machine back (see the class summary): the engine
    /// must still be asked about that machine, because its answer is what says whether the waiting switch still
    /// stands, and a row this check has just ended no longer holds its machine either. So the mark is set from
    /// <paramref name="blocked"/> - the machines whose open row's outcome is not known or whose send is in flight.
    /// Nothing else in the picture is touched.
    /// </summary>
    internal static LicenseSnapshot SnapshotToDecideOn(LicensePicture picture, IReadOnlySet<Guid> blocked)
    {
        var blockedMachines = picture.Overview.Machines
            .Where(m => blocked.Contains(m.MachineId))
            .Select(m => MachineKey(m.MachineName, m.OsUser))
            .ToHashSet(StringComparer.Ordinal);
        return picture.Snapshot with
        {
            Machines = picture.Snapshot.Machines
                .Select(m => m with { HasOpenSwitch = blockedMachines.Contains(MachineKey(m.MachineName, m.OsUser)) })
                .ToList(),
        };
    }

    /// <summary>True for an open switch row known never to have left this Gateway: the machine's Director was not
    /// connected. Such a row does not hold its machine back from a new decision (the class summary).</summary>
    internal static bool IsKnownNeverSent(LicenseSwitchLogEntity row)
        => row.State == LicenseLogStates.Pending && row.ReasonCode == LicenseGatewayReasonCodes.DirectorOffline;

    /// <summary>
    /// The picture Switch now decides on, for the Licenses page: every machine whose open row is NOT known never to
    /// have left is mid-switch, and no other. The page asks the engine with this, so it offers Switch now exactly
    /// where <see cref="SwitchNowAsync"/> would accept it. (A send in flight in this process is one more thing that
    /// holds a machine back there; its row is already written "pending" with no reason, so it is counted here too.)
    /// </summary>
    internal static LicenseSnapshot SnapshotSwitchNowDecidesOn(LicensePicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return SnapshotToDecideOn(picture,
            picture.OpenSwitches.Where(r => !IsKnownNeverSent(r)).Select(r => r.MachineId).ToHashSet());
    }

    private static List<LicenseExhaustion> Exhaustions(IReadOnlyList<LicenseSwitchLogEntity> openNoRoom)
        => openNoRoom.Select(r => new LicenseExhaustion(r.FromAccountEmail, r.CreatedAtUtc, NotAfterOf(r))).ToList();

    // ----- one send, and what its result writes (contract section 3.5) -----

    private async Task SendAsync(TenantId tenant, PlannedSend send, CancellationToken ct)
    {
        try
        {
            var sentAt = _nowUtc();
            var result = await DirectorCommandRouter.TrySendAsync(_send, send.DirectorId, LicenseContract.SwitchVerb, "",
                send.Request, ct, LicenseContract.SwitchCommandTimeout, send.MachineName).ConfigureAwait(false);
            RecordResult(tenant, send, sentAt, result);
        }
        finally
        {
            // Cleared as soon as this send has recorded its result; the check clears it again on its way out.
            _inFlight.TryRemove((tenant, send.SwitchId), out _);
        }
    }

    private void RecordResult(TenantId tenant, PlannedSend send, DateTime sentAtUtc, DirectorCommandResult? result)
    {
        var now = _nowUtc();
        if (result is null)
        {
            // Nothing was sent. A row whose earlier send may have run keeps saying so; any other waits for the Director.
            if (!send.OutcomeUnknown)
                _store.TryMark(tenant, send.SwitchId, LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline,
                    $"The Director on {send.MachineName} is not connected. This switch is sent again each minute until {Time(send.Request.NotAfterUtc)}.", now);
            FileLog.Write($"[LicenseSwitchService] switch={send.SwitchId}: the Director is not connected, nothing was sent");
            return;
        }

        _store.RecordSend(tenant, send.SwitchId, send.DirectorId, sentAtUtc);
        switch (result.Status)
        {
            case DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped:
                _store.TryMark(tenant, send.SwitchId, LicenseLogStates.Unknown, null, result.Error, now);
                // The command went out and the row is still open: if this was the one send after NotAfterUtc, it
                // has now been used.
                if (send.AfterExpiry)
                    _sentAfterExpiry[(tenant, send.SwitchId)] = 0;
                return;
            case DirectorCommandStatus.Ok when ReadAnswer(result, send.SwitchId) is { } answer:
                var settled = _store.TrySettle(tenant, answer, now);
                _store.ApplySwitchReadBack(tenant, send.MachineId, answer, sentAtUtc);
                if (settled && answer.Outcome == LicenseSwitchOutcomes.Failed)
                    HoldIfItKeepsFailing(tenant, send);
                break;
            case DirectorCommandStatus.Ok:
                if (_store.TryMark(tenant, send.SwitchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError,
                        $"The Director on {send.MachineName} answered, but its answer could not be read as the result of this switch.", now))
                    HoldIfItKeepsFailing(tenant, send);
                break;
            default:
                if (_store.TryMark(tenant, send.SwitchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError,
                        result.Error ?? $"The Director on {send.MachineName} returned {result.Status}.", now))
                    HoldIfItKeepsFailing(tenant, send);
                break;
        }
        // Every other result ends the row, and a row that has ended keeps no entry.
        _sentAfterExpiry.TryRemove((tenant, send.SwitchId), out _);
        FileLog.Write($"[LicenseSwitchService] switch={send.SwitchId}: director={send.DirectorId}, status={result.Status}");
    }

    /// <summary>A switch has just ended <c>failed</c> on what the machine's Director answered. When it is the same
    /// failure as the machine's switch before it, the machine is held out of the automatic decision, and the hold is
    /// written on the row (the class summary).</summary>
    private void HoldIfItKeepsFailing(TenantId tenant, PlannedSend send)
    {
        if (_store.HoldAfterRepeatedFailure(tenant, send.SwitchId) is { } until)
            FileLog.Write($"[LicenseSwitchService] switch={send.SwitchId}: the same switch failed again on {send.MachineName}; the automatic switch leaves it out until {until:O}");
    }

    /// <summary>The Director's answer, or null when the body is not the result of THIS switch with an outcome this
    /// Gateway knows - which is recorded as a Director error, never stored as if it were an outcome.</summary>
    private static LicenseSwitchResult? ReadAnswer(DirectorCommandResult result, string switchId)
    {
        if (string.IsNullOrWhiteSpace(result.BodyJson))
            return null;
        LicenseSwitchResult? answer;
        try
        {
            answer = JsonSerializer.Deserialize<LicenseSwitchResult>(result.BodyJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[LicenseSwitchService] switch={switchId}: the Director's answer is not readable: {ex.Message}");
            return null;
        }
        return answer is not null
               && string.Equals(answer.SwitchId, switchId, StringComparison.Ordinal)
               && answer.Outcome is LicenseSwitchOutcomes.Switched or LicenseSwitchOutcomes.AlreadyActive or LicenseSwitchOutcomes.Failed
            ? answer
            : null;
    }

    // ----- the owner email of one event -----

    private async Task EmailAsync(TenantId tenant, PlannedEmail planned, CancellationToken ct)
    {
        LicenseOwnerEmailResult result;
        try
        {
            result = await _mail(tenant, planned.Email, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender that throws has sent nothing this Gateway knows of: a failed send, with the growing wait.
            FileLog.Write($"[LicenseSwitchService] owner email for a no-room event: the sender FAILED ({ex.GetType().Name})");
            result = new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, $"the mail sender failed ({ex.GetType().Name})");
        }
        var now = _nowUtc();
        var account = planned.AccountEmail;
        switch (result.Outcome)
        {
            case LicenseOwnerEmailOutcome.Sent:
                // Remembered BEFORE the row is marked: if the mark fails, no later check of this process sends again.
                _emailConfirmed[(tenant, account)] = (now, Happened(planned.Email));
                MarkEmailConfirmed(tenant, account);
                break;
            case LicenseOwnerEmailOutcome.NoAccount:
                _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.NoOwnerAccount,
                    LicenseStore.EventMessage(Happened(planned.Email), $"no owner email was sent because {result.Detail}"), null);
                ForgetEmailWait(tenant, account);
                break;
            default:
                // The wait is set before the row is written, so a database that refuses the write still gets the wait.
                _emailNotBefore[(tenant, account)] = now + NextEmailWait(tenant, account, result.RetryAfter);
                _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailFailed,
                    LicenseStore.EventMessage(Happened(planned.Email), $"the owner has not been emailed yet because {result.Detail}"), null);
                break;
        }
        FileLog.Write($"[LicenseSwitchService] owner email for a no-room event: tenant={tenant.ToLogString()}, outcome={result.Outcome}");
    }

    /// <summary>Mark the open no-room event of an email the mail service confirmed. Forgotten only once the row is
    /// marked, so a database that refuses the mark costs no second email while this Gateway runs.</summary>
    private void MarkEmailConfirmed(TenantId tenant, string account)
    {
        var (sentAt, happened) = _emailConfirmed[(tenant, account)];
        _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailed,
            LicenseStore.EventMessage(happened, "the owner was emailed"), sentAt);
        _emailConfirmed.TryRemove((tenant, account), out _);
        ForgetEmailWait(tenant, account);
    }

    /// <summary>What the event's row says happened, from the event's email: the engine writes it on every email of an
    /// exhaustion event, so one without it is a defect and is not replaced by a guessed sentence.</summary>
    private static string Happened(LicenseOwnerEmail email)
        => email.Happened ?? throw new InvalidOperationException("The email of an exhaustion event does not say what happened.");

    /// <summary>
    /// How long to wait before the next try of an event's owner email after a failed one: the Gateway's own wait,
    /// which doubles with every failure in a row (one minute, two, four ... up to
    /// <see cref="LongestOwnEmailWait"/>), or the mail service's when it asked for a longer one.
    /// </summary>
    private TimeSpan NextEmailWait(TenantId tenant, string account, TimeSpan? askedFor)
    {
        var failures = _emailFailures.AddOrUpdate((tenant, account), 1, (_, n) => n + 1);
        var own = failures > 6 ? LongestOwnEmailWait : TimeSpan.FromMinutes(1 << (failures - 1));
        return askedFor is { } asked && asked > own ? asked : own;
    }

    private void ForgetEmailWait(TenantId tenant, string account)
    {
        _emailNotBefore.TryRemove((tenant, account), out _);
        _emailFailures.TryRemove((tenant, account), out _);
    }

    // ----- small helpers -----

    private SemaphoreSlim GateFor(TenantId tenant) => _deciding.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));

    private static bool SameDecision(LicenseSwitchLogEntity row, LicenseSwitchDecision decision)
        => row.FromAccountEmail == LicenseStore.NormalizeEmail(decision.FromAccountEmail)
           && row.ToAccountEmail == LicenseStore.NormalizeEmail(decision.TargetAccountEmail);

    private static Dictionary<string, LicenseMachineView> MachinesByName(LicenseOverview overview)
        => overview.Machines.ToDictionary(m => MachineKey(m.MachineName, m.OsUser), StringComparer.Ordinal);

    // The Gateway's machine is (machine name, operating system user) without regard to case (contract section 2.4).
    private static string MachineKey(string machineName, string osUser)
        => machineName.Trim().ToLowerInvariant() + "\n" + osUser.Trim().ToLowerInvariant();

    private static string IdOf(LicenseSwitchLogEntity row)
        => row.SwitchId ?? throw new InvalidOperationException($"Switch log row {row.Id} of kind '{row.Kind}' has no switch identifier.");

    private static DateTime NotAfterOf(LicenseSwitchLogEntity row)
        => row.NotAfterUtc ?? throw new InvalidOperationException($"Switch log row {row.Id} of kind '{row.Kind}' has no end time.");

    private static string Time(DateTime utc) => utc.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
