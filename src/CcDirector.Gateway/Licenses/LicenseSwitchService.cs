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
/// <param name="TargetAccountEmail">The account the machines were sent to, lower-cased; null when refused.</param>
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
/// - an answer: its outcome, and the machine's active account from the answer's read-back when it is newer than the
///   machine's last report (<see cref="LicenseStore.ApplySwitchReadBack"/>).
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
///
/// SENDS DO NOT WAIT FOR ONE ANOTHER. Every send of a check is started together and each waits at most its own 60
/// seconds. No database context is open while a send is in flight: the store opens and closes one per call. What is
/// decided and written (rows, bars, events) happens under a short per-tenant gate that the minute check and Switch now
/// share, so the two can never write two open rows for one machine; the gate is released before anything is sent.
///
/// ONE OWNER EMAIL PER EVENT. The event is recorded as emailed only when the mail service confirmed the send. A send
/// that failed is tried again on a later check, with the text rebuilt from that check's facts
/// (<see cref="LicenseDecisionEngine.NoRoomEmail"/>); once it has gone out it never goes again. A Gateway that stops
/// between the service's confirmation and the row being marked would send once more after it restarts - the only way
/// to rule that out would be to mark the row before sending, which would lose the email instead.
///
/// INERT UNTIL A DIRECTOR REPORTS. A tenant none of whose machines has ever sent a usage report is left alone: one
/// quiet read, nothing written, no log line.
/// </summary>
public sealed class LicenseSwitchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseUsageHistory _history;
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
    // The mail service asked for a wait before the next try of this event's email.
    private readonly ConcurrentDictionary<(TenantId Tenant, string AccountEmail), DateTime> _emailNotBefore = new();

    /// <param name="send">The one way a command goes down a Director's stream (<c>GatewayHost.SendCommandAsync</c>). It
    /// reads the tenant scope in effect, so <see cref="TickAsync"/> and <see cref="SwitchNowAsync"/> must be called
    /// inside the tenant's scope.</param>
    /// <param name="mail">Sends the owner email (<see cref="LicenseOwnerMailer.SendAsync"/>).</param>
    /// <param name="nowUtc">The Gateway's clock.</param>
    internal LicenseSwitchService(LicenseStore store, LicenseReadModel reads, LicenseUsageHistory history,
        DirectorCommandRouter.SendDirectorCommandAsync send, LicenseOwnerEmailSender mail, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    // ----- the minute check -----

    /// <summary>
    /// The minute check for every tenant: one <see cref="TickAsync"/> per tenant, each awaited inside that tenant's
    /// scope (contract section 6 rule 4). One tenant's failure is logged and does not stop the next tenant's check.
    /// </summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        await tenants.ForEachTenantAsync(async () =>
        {
            if (tenants.Current is not { } tenant)
                return; // no scope in effect: check nothing, never a tenant of our own choosing
            try
            {
                await TickAsync(tenant, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The boundary of one tenant's check: its failure must never cost another tenant its switch.
                FileLog.Write($"[LicenseSwitchService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
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
        FileLog.Write($"[LicenseSwitchService] TickAsync: tenant={tenant.ToLogString()}");
        try
        {
            TickPlan plan;
            var gate = GateFor(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                plan = PlanTick(tenant);
            }
            finally
            {
                gate.Release();
            }

            var work = plan.Sends.Select(send => SendAsync(tenant, send, ct))
                .Concat(plan.Emails.Select(email => EmailAsync(tenant, email, ct)))
                .ToList();
            await Task.WhenAll(work).ConfigureAwait(false);
            FileLog.Write($"[LicenseSwitchService] TickAsync: tenant={tenant.ToLogString()}, sends={plan.Sends.Count}, emails={plan.Emails.Count}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSwitchService] TickAsync FAILED: tenant={tenant.ToLogString()}: {ex.Message}");
            throw;
        }
        finally
        {
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
        try
        {
            var sends = new List<PlannedSend>();
            var skipped = new List<LicenseSkippedMachine>();
            string? target = null;
            var gate = GateFor(tenant);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _nowUtc();
                var overview = _reads.Read(tenant, now);
                var open = Classify(tenant, _store.ListOpenSwitches(tenant));
                var snapshot = BuildSnapshot(tenant, overview, open.Blocked, _store.ListOpenNoRoom(tenant), now);
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
                    target = decision.TargetAccountEmail;
                    var machine = machines[MachineKey(decision.MachineName, decision.OsUser)];
                    // A new Switch now is a new decision, so it gets a new switch identifier (contract section 3.6):
                    // a switch still waiting for an offline Director is ended first, never sent beside the new one.
                    if (open.NeverSent.TryGetValue(machine.MachineId, out var waiting))
                        _store.TryMark(tenant, IdOf(waiting), LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                            $"The Director on {machine.MachineName} was not connected, and a new Switch now replaced this switch before it could be sent.", now);
                    var planned = PlanNewSwitch(tenant, machine, decision, now);
                    if (planned is null)
                        skipped.Add(new LicenseSkippedMachine(machine.MachineName, machine.OsUser, decision.FromAccountEmail,
                            "a switch sent to it has not finished"));
                    else
                        sends.Add(planned);
                }
            }
            finally
            {
                gate.Release();
            }

            if (sends.Count == 0)
            {
                FileLog.Write("[LicenseSwitchService] SwitchNowAsync: refused, every machine was taken by another switch at the same moment");
                return new LicenseSwitchNowResult(false, LicenseManualRefusals.NoMachineToSwitch,
                    "No machine on the account can be switched now: each already has a switch that has not finished.", null,
                    Array.Empty<LicenseSwitchNowMachine>(), skipped);
            }

            await Task.WhenAll(sends.Select(send => SendAsync(tenant, send, ct))).ConfigureAwait(false);

            var switches = sends.Select(send =>
            {
                var row = _store.FindSwitch(tenant, send.SwitchId)
                    ?? throw new InvalidOperationException($"Switch {send.SwitchId} was written and sent, and its row is gone.");
                return new LicenseSwitchNowMachine(send.SwitchId, row.MachineName, row.OsUser, row.State, row.ReasonCode, row.Message);
            }).ToList();
            FileLog.Write($"[LicenseSwitchService] SwitchNowAsync: switches={switches.Count}, skipped={skipped.Count}");
            return new LicenseSwitchNowResult(true, null, null, target, switches, skipped);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSwitchService] SwitchNowAsync FAILED: {ex.Message}");
            throw;
        }
    }

    // ----- reads for the Licenses page -----

    /// <summary>
    /// The picture of one tenant that the engine and the forecast both take: every account with its freshest reading,
    /// every machine, the bars in force and the open no-room events - exactly what the minute check decides on.
    /// </summary>
    public LicenseSnapshot ReadSnapshot(TenantId tenant, DateTime nowUtc)
    {
        FileLog.Write($"[LicenseSwitchService] ReadSnapshot: tenant={tenant.ToLogString()}");
        try
        {
            var overview = _reads.Read(tenant, nowUtc);
            var open = Classify(tenant, _store.ListOpenSwitches(tenant));
            var snapshot = BuildSnapshot(tenant, overview, open.Blocked, _store.ListOpenNoRoom(tenant), nowUtc);
            FileLog.Write($"[LicenseSwitchService] ReadSnapshot: accounts={snapshot.Accounts.Count}, machines={snapshot.Machines.Count}");
            return snapshot;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSwitchService] ReadSnapshot FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The recent readings <see cref="LicenseForecaster.Forecast"/> takes its rates from: two hours, kept in
    /// memory, empty after a restart until the next reports arrive.</summary>
    public IReadOnlyList<LicenseAccountHistory> ReadHistory(TenantId tenant, DateTime nowUtc) => _history.Read(tenant, nowUtc);

    // ----- what one check decides and writes (under the tenant's gate; nothing here waits on a Director) -----

    private sealed record PlannedSend(string SwitchId, Guid MachineId, string MachineName, string DirectorId,
        LicenseSwitchRequest Request, bool OutcomeUnknown);

    private sealed record PlannedEmail(string AccountEmail, LicenseOwnerEmail Email);

    private sealed record TickPlan(List<PlannedSend> Sends, List<PlannedEmail> Emails);

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
            else if (row.State == LicenseLogStates.Pending && row.ReasonCode == LicenseGatewayReasonCodes.DirectorOffline)
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
        var overview = _reads.Read(tenant, now);
        var machinesById = overview.Machines.ToDictionary(m => m.MachineId);
        var open = Classify(tenant, _store.ListOpenSwitches(tenant));
        var sends = new List<PlannedSend>();

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
            if (now <= NotAfterOf(row) || _sentAfterExpiry.TryAdd((tenant, id), 0))
            {
                sends.Add(PlanResend(tenant, row, machine, outcomeUnknown: true));
            }
            else if (machine.SwitchInProgressId is null)
            {
                // Past NotAfterUtc, sent once more, still no answer: the machine's own report is the answer.
                var switched = string.Equals(machine.ActiveAccountEmail, row.ToAccountEmail, StringComparison.Ordinal);
                _store.TryMark(tenant, id, switched ? LicenseLogStates.Switched : LicenseLogStates.Failed, null,
                    $"Settled from the machine's report: no answer to this switch could be had, and the report shows {machine.ActiveAccountEmail ?? "no account"} active.", now);
                _sentAfterExpiry.TryRemove((tenant, id), out _);
                open.Blocked.Remove(row.MachineId);
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
        }

        // 3. Decide.
        var noRoom = _store.ListOpenNoRoom(tenant);
        var snapshot = BuildSnapshot(tenant, overview, open.Blocked, noRoom, now);
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
                    sends.Add(PlanResend(tenant, waiting, machine, outcomeUnknown: false));
                    continue;
                }
                _store.TryMark(tenant, IdOf(waiting), LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                    $"The Director on {machine.MachineName} was not connected, and the decision changed before this switch could be sent.", now);
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
                sends.Add(PlanResend(tenant, waiting, machine, outcomeUnknown: false));
        }

        // 5. Bars.
        foreach (var bar in decisions.NewBars)
            _store.Bar(tenant, bar.AccountEmail, bar.Window, bar.BarredUntilUtc, now);

        // 6. No-room events: opened, moved, closed - and the one owner email of each.
        var emails = new List<PlannedEmail>();
        foreach (var opened in decisions.OpenedExhaustions)
            _store.OpenNoRoom(tenant, opened.AccountEmail, opened.StartedAtUtc, opened.EndsAtUtc);
        foreach (var extended in decisions.ExtendedExhaustions)
            _store.ExtendNoRoom(tenant, extended.AccountEmail, extended.EndsAtUtc);
        foreach (var closed in decisions.ClosedExhaustions)
        {
            _store.CloseNoRoom(tenant, closed, now);
            _emailNotBefore.TryRemove((tenant, LicenseStore.NormalizeEmail(closed)), out _);
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
            if (_emailNotBefore.TryGetValue((tenant, account), out var notBefore) && now < notBefore)
                continue;
            if (LicenseDecisionEngine.NoRoomEmail(snapshot, account) is { } again)
                emails.Add(new PlannedEmail(account, again));
        }

        return new TickPlan(sends, emails);
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
        return PlanResend(tenant, row, machine, outcomeUnknown: false);
    }

    /// <summary>Plan one send of <paramref name="row"/> - its first or a repeat, always with the row's own identifier,
    /// times and accounts - to the Director that sent the machine's last accepted report.</summary>
    private PlannedSend PlanResend(TenantId tenant, LicenseSwitchLogEntity row, LicenseMachineView machine, bool outcomeUnknown)
    {
        var id = IdOf(row);
        _inFlight[(tenant, id)] = 0;
        var request = new LicenseSwitchRequest
        {
            SwitchId = id,
            TargetAccountEmail = row.ToAccountEmail,
            ExpectedActiveAccountEmail = row.FromAccountEmail,
            Reason = row.Reason ?? throw new InvalidOperationException($"Switch {id} has no reason."),
            RequestedAtUtc = row.CreatedAtUtc,
            NotAfterUtc = NotAfterOf(row),
        };
        return new PlannedSend(id, machine.MachineId, machine.MachineName, machine.DirectorId, request, outcomeUnknown);
    }

    private LicenseSnapshot BuildSnapshot(TenantId tenant, LicenseOverview overview, IReadOnlySet<Guid> blocked,
        IReadOnlyList<LicenseSwitchLogEntity> openNoRoom, DateTime now)
    {
        var accounts = overview.Accounts.Select(a => a.Freshest is { } reading
                ? new LicenseAccountReading(a.AccountEmail,
                    Window(reading.FiveHourUtilization, reading.FiveHourResetsAtUtc),
                    Window(reading.SevenDayUtilization, reading.SevenDayResetsAtUtc),
                    reading.JudgedReadAtUtc, reading.FromActiveHolding)
                : new LicenseAccountReading(a.AccountEmail, null, null, null, false))
            .ToList();
        var machines = overview.Machines.Select(m => new LicenseMachineState(
                m.MachineName, m.OsUser, m.ActiveAccountEmail,
                m.Holdings.Where(h => !h.IsActive).Select(h => new LicenseSpare(h.AccountEmail, h.ReadError, h.SignInExpiresAtUtc)).ToList(),
                m.LastReportAtUtc, blocked.Contains(m.MachineId), m.SwitchInProgressId,
                m.Holdings.FirstOrDefault(h => h.IsActive)?.SignInExpiresAtUtc))
            .ToList();
        var bars = _store.ListActiveBars(tenant, now).Select(b => new LicenseBar(b.AccountEmail, b.Window, b.UntilUtc)).ToList();
        var exhaustions = openNoRoom.Select(r => new LicenseExhaustion(r.FromAccountEmail, r.CreatedAtUtc, NotAfterOf(r))).ToList();
        return new LicenseSnapshot(now, accounts, machines, bars, exhaustions);
    }

    private static LicenseWindowReading? Window(double? utilization, DateTime? resetsAtUtc)
        => utilization is { } used ? new LicenseWindowReading(used, resetsAtUtc) : null;

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
                break;
            case DirectorCommandStatus.Ok when ReadAnswer(result, send.SwitchId) is { } answer:
                _store.TrySettle(tenant, answer, now);
                _store.ApplySwitchReadBack(tenant, send.MachineId, answer);
                _sentAfterExpiry.TryRemove((tenant, send.SwitchId), out _);
                break;
            case DirectorCommandStatus.Ok:
                _store.TryMark(tenant, send.SwitchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError,
                    $"The Director on {send.MachineName} answered, but its answer could not be read as the result of this switch.", now);
                break;
            default:
                _store.TryMark(tenant, send.SwitchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError,
                    result.Error ?? $"The Director on {send.MachineName} returned {result.Status}.", now);
                break;
        }
        FileLog.Write($"[LicenseSwitchService] switch={send.SwitchId}: director={send.DirectorId}, status={result.Status}");
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
        var result = await _mail(tenant, planned.Email, ct).ConfigureAwait(false);
        var now = _nowUtc();
        var account = planned.AccountEmail;
        switch (result.Outcome)
        {
            case LicenseOwnerEmailOutcome.Sent:
                _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailed,
                    LicenseStore.NoRoomMessage(account, "the owner was emailed"), now);
                _emailNotBefore.TryRemove((tenant, account), out _);
                break;
            case LicenseOwnerEmailOutcome.NoAccount:
                _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.NoOwnerAccount,
                    LicenseStore.NoRoomMessage(account, $"no owner email was sent because {result.Detail}"), null);
                break;
            default:
                _store.RecordNoRoomEmail(tenant, account, LicenseGatewayReasonCodes.OwnerEmailFailed,
                    LicenseStore.NoRoomMessage(account, $"the owner has not been emailed yet because {result.Detail}"), null);
                if (result.RetryAfter is { } wait)
                    _emailNotBefore[(tenant, account)] = now + wait;
                break;
        }
        FileLog.Write($"[LicenseSwitchService] owner email for a no-room event: tenant={tenant.ToLogString()}, outcome={result.Outcome}");
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
