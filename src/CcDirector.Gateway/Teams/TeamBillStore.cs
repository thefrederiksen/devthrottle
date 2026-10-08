using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE ONE WRITER OF A TEAM'S BILL (Teams v1, the team bill without Stripe). The owner's ruling, 7 Oct 2026: "implement
/// all of this without Stripe ... and let people renew and auto-renew for free. So we're doing everything except actually
/// charging, but we are pretending we're charging."
///
/// So the plan is real and the charge is not. A team's bill (<see cref="TeamBillEntity"/>) has a status, a seat count, a
/// price per seat, a monthly period and an auto-renew switch, and every period that begins writes a history line
/// (<see cref="TeamBillChargeEntity"/>) with the amount it would cost and US$0.00 charged. Nothing here calls the website
/// or a payment provider.
///
/// WHO MAY CALL WHAT IS NOT DECIDED HERE. These methods keep the bill's own rules - one bill per team, a renewal only of a
/// bill that is ending or ended, and so on - and nothing about the caller. The Owner-only rule is asked of
/// <see cref="TeamAccess"/> by <see cref="TeamRegistry"/> before any of them is called.
///
/// Writes are serialized under one lock inside this process. ACROSS processes - two Gateways on one database during a
/// deploy, which has happened (2026-07-30) - the database refuses the second writer: every write bumps the bill's
/// <see cref="TeamBillEntity.Version"/>, a concurrency token, and a period's history line is unique per team and period
/// start. A write that loses that race changes nothing: an Owner's action is refused with a sentence to reload, and the
/// renewal pass logs it and leaves the bill to the process that won. A team id is logged only in its hashed tenant form.
/// </summary>
public sealed class TeamBillStore
{
    /// <summary>The price of one paid seat for one month, in US cents: US$49.00.</summary>
    public const int PricePerSeatCents = 4900;

    /// <summary>The reason on a history line written when the Owner starts the plan.</summary>
    public const string ReasonStarted = "started";

    /// <summary>The reason on a history line written when the Owner renews by hand.</summary>
    public const string ReasonRenewed = "renewed";

    /// <summary>The reason on a history line written by the renewal pass.</summary>
    public const string ReasonAutoRenewed = "auto-renewed";

    // A Gateway that was down for longer than this many periods catches up by this many lines at most per pass; the next
    // pass writes the rest. Bounds one pass; it is never reached in practice.
    private const int MaxPeriodsPerPass = 24;

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;
    private readonly object _writeLock = new();

    /// <summary>TEST SEAM: called with the team id just before the renewal pass saves one bill, so a test can change the
    /// bill underneath it the way a second Gateway process would. Null in production.</summary>
    internal Action<string>? BeforeRenewalSaveForTests { get; set; }

    /// <param name="db">The Gateway database. The bill tables are global, so they are read through the UNSCOPED context.</param>
    /// <param name="utcNow">The clock; the system clock when omitted.</param>
    public TeamBillStore(GatewayDatabase db, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The team's bill row, or null when the team has never started its plan.</summary>
    public TeamBillEntity? Find(string teamId)
    {
        var id = RequireTeam(teamId);
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamBills.AsNoTracking().FirstOrDefault(b => b.TeamId == id);
    }

    /// <summary>The team's billing history, newest period first. Empty when the plan has never started.</summary>
    public IReadOnlyList<TeamBillChargeEntity> History(string teamId)
    {
        var id = RequireTeam(teamId);
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamBillCharges.AsNoTracking()
            .Where(c => c.TeamId == id)
            .ToList()
            .OrderByDescending(c => c.PeriodStartUtc)
            .ThenByDescending(c => c.CreatedAtUtc)
            .ToList();
    }

    /// <summary>
    /// START THE TEAM PLAN: an active bill for one month from now, auto-renew on, at the team's paid-seat count, and its
    /// first history line. Refused when the team already has a bill - a canceled one is renewed, not started again.
    /// </summary>
    public TeamBillChangeResult Start(string teamId)
    {
        var id = RequireTeam(teamId);
        FileLog.Write($"[TeamBillStore] Start: team {LogTeam(id)}");
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            if (!ctx.Teams.AsNoTracking().Any(t => t.Id == id))
                return Refuse("Start", TeamBillChangeOutcome.NotFound, TeamRefusals.NoSuchTeam);
            var existing = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (existing is not null)
                return Refuse("Start", TeamBillChangeOutcome.Refused, string.Equals(existing.Status, EntitlementRegistry.StatusActive, StringComparison.Ordinal)
                    ? TeamBillRefusals.AlreadyStarted
                    : TeamBillRefusals.RenewInstead);

            var now = _utcNow();
            var seats = PaidSeats(ctx, id);
            var bill = new TeamBillEntity
            {
                TeamId = id,
                Status = EntitlementRegistry.StatusActive,
                Seats = seats,
                PricePerSeatCents = PricePerSeatCents,
                PlanStartedUtc = now,
                CurrentPeriodStartUtc = now,
                CurrentPeriodEndUtc = PeriodEnd(now, now),
                AutoRenew = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            };
            ctx.TeamBills.Add(bill);
            ctx.TeamBillCharges.Add(ChargeFor(bill, ReasonStarted, now));
            if (!Save(ctx, "Start", id))
                return Refuse("Start", TeamBillChangeOutcome.Refused, TeamBillRefusals.ChangedAtTheSameMoment);
            FileLog.Write($"[TeamBillStore] Start: team {LogTeam(id)} plan started, {seats} seat(s), period ends {bill.CurrentPeriodEndUtc:O}, auto-renew on, charged 0");
            return TeamBillChangeResult.Done;
        }
    }

    /// <summary>
    /// RENEW NOW: a bill that has ENDED (canceled) becomes active for a new month from now with auto-renew on, and writes a
    /// history line; the plan's periods are anchored to this new start. Refused while the bill is still active - an ending
    /// bill goes back by switching auto-renew on, which keeps its period and writes no line (the Delivery Lead's ruling,
    /// 7 Oct 2026: a renewal of a running period would record two overlapping charges for one month) - and refused for a
    /// team that has never started its plan.
    /// </summary>
    public TeamBillChangeResult Renew(string teamId)
    {
        var id = RequireTeam(teamId);
        FileLog.Write($"[TeamBillStore] Renew: team {LogTeam(id)}");
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (bill is null)
                return Refuse("Renew", TeamBillChangeOutcome.Refused, TeamBillRefusals.NotStarted);
            if (IsActive(bill))
                return Refuse("Renew", TeamBillChangeOutcome.Refused, bill.AutoRenew
                    ? TeamBillRefusals.AlreadyRenews(Day(bill.CurrentPeriodEndUtc))
                    : TeamBillRefusals.StillRunning(Day(bill.CurrentPeriodEndUtc)));

            var now = _utcNow();
            bill.Status = EntitlementRegistry.StatusActive;
            bill.Seats = PaidSeats(ctx, id);
            bill.PlanStartedUtc = now;
            bill.CurrentPeriodStartUtc = now;
            bill.CurrentPeriodEndUtc = PeriodEnd(now, now);
            bill.AutoRenew = true;
            bill.UpdatedAtUtc = now;
            bill.Version++;
            ctx.TeamBillCharges.Add(ChargeFor(bill, ReasonRenewed, now));
            if (!Save(ctx, "Renew", id))
                return Refuse("Renew", TeamBillChangeOutcome.Refused, TeamBillRefusals.ChangedAtTheSameMoment);
            FileLog.Write($"[TeamBillStore] Renew: team {LogTeam(id)} renewed, {bill.Seats} seat(s), period ends {bill.CurrentPeriodEndUtc:O}, auto-renew on, charged 0");
            return TeamBillChangeResult.Done;
        }
    }

    /// <summary>
    /// SWITCH AUTO-RENEW on or off on an active bill. Off: the bill stays active to the end of its period and then ends.
    /// On: it rolls to the next month at the end of its period. Refused for a canceled bill (renew it instead) and for a
    /// team that has never started its plan. Setting it to what it already is succeeds and changes nothing.
    /// </summary>
    public TeamBillChangeResult SetAutoRenew(string teamId, bool on)
    {
        var id = RequireTeam(teamId);
        FileLog.Write($"[TeamBillStore] SetAutoRenew: team {LogTeam(id)} on={on}");
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (bill is null)
                return Refuse("SetAutoRenew", TeamBillChangeOutcome.Refused, TeamBillRefusals.NotStarted);
            if (!IsActive(bill))
                return Refuse("SetAutoRenew", TeamBillChangeOutcome.Refused, TeamBillRefusals.EndedRenewInstead);
            if (bill.AutoRenew == on)
            {
                FileLog.Write($"[TeamBillStore] SetAutoRenew: team {LogTeam(id)} auto-renew is already {(on ? "on" : "off")} - nothing changed");
                return TeamBillChangeResult.Done;
            }

            bill.AutoRenew = on;
            bill.UpdatedAtUtc = _utcNow();
            bill.Version++;
            if (!Save(ctx, "SetAutoRenew", id))
                return Refuse("SetAutoRenew", TeamBillChangeOutcome.Refused, TeamBillRefusals.ChangedAtTheSameMoment);
            FileLog.Write($"[TeamBillStore] SetAutoRenew: team {LogTeam(id)} auto-renew {(on ? "on" : "off")}, period ends {bill.CurrentPeriodEndUtc:O}");
            return TeamBillChangeResult.Done;
        }
    }

    /// <summary>
    /// CANCEL THE PLAN: the bill stays active to the end of its period and then ends (the renewal pass makes it canceled).
    /// Recorded as auto-renew off - an ending bill is exactly an active bill that will not renew. Refused for a bill that
    /// has already ended and for a team that has never started its plan; cancelling an already ending bill succeeds and
    /// changes nothing.
    /// </summary>
    public TeamBillChangeResult Cancel(string teamId)
    {
        var id = RequireTeam(teamId);
        FileLog.Write($"[TeamBillStore] Cancel: team {LogTeam(id)}");
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (bill is null)
                return Refuse("Cancel", TeamBillChangeOutcome.Refused, TeamBillRefusals.NotStarted);
            if (!IsActive(bill))
                return Refuse("Cancel", TeamBillChangeOutcome.Refused, TeamBillRefusals.AlreadyEnded);
            if (!bill.AutoRenew)
            {
                FileLog.Write($"[TeamBillStore] Cancel: team {LogTeam(id)} the plan is already ending - nothing changed");
                return TeamBillChangeResult.Done;
            }

            bill.AutoRenew = false;
            bill.UpdatedAtUtc = _utcNow();
            bill.Version++;
            if (!Save(ctx, "Cancel", id))
                return Refuse("Cancel", TeamBillChangeOutcome.Refused, TeamBillRefusals.ChangedAtTheSameMoment);
            FileLog.Write($"[TeamBillStore] Cancel: team {LogTeam(id)} cancelled - stays active until {bill.CurrentPeriodEndUtc:O}, then ends");
            return TeamBillChangeResult.Done;
        }
    }

    /// <summary>
    /// END THE BILL OF A TEAM BEING DELETED (Teams v1, rename, delete and leave): an active bill - running or ending -
    /// ends now, with auto-renew off, so the renewal pass never rolls it again. Its history stays. A team with no bill, or
    /// one whose bill has already ended, has nothing to end. Nothing is charged, as everywhere in this store.
    /// </summary>
    public TeamBillEndOutcome EndForDeletedTeam(string teamId)
    {
        var id = RequireTeam(teamId);
        FileLog.Write($"[TeamBillStore] EndForDeletedTeam: team {LogTeam(id)}");
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (bill is null)
            {
                FileLog.Write($"[TeamBillStore] EndForDeletedTeam: team {LogTeam(id)} never started its plan - no bill to end");
                return TeamBillEndOutcome.NoBill;
            }
            if (!IsActive(bill))
            {
                FileLog.Write($"[TeamBillStore] EndForDeletedTeam: team {LogTeam(id)} bill had already ended");
                return TeamBillEndOutcome.AlreadyEnded;
            }

            bill.Status = EntitlementRegistry.StatusCanceled;
            bill.AutoRenew = false;
            bill.UpdatedAtUtc = _utcNow();
            bill.Version++;
            if (!Save(ctx, "EndForDeletedTeam", id))
                return TeamBillEndOutcome.ChangedElsewhere;
            FileLog.Write($"[TeamBillStore] EndForDeletedTeam: team {LogTeam(id)} bill ended - the team is being deleted");
            return TeamBillEndOutcome.Ended;
        }
    }

    /// <summary>
    /// RECORD THE TEAM'S PAID-SEAT COUNT on its active bill: called after a membership change commits, and by the
    /// convergence pass. Returns what it found. A team with no bill, or a canceled one, has nothing to record; a bill that
    /// already carries the count is left alone.
    /// </summary>
    public SeatRecordOutcome RecordSeats(string teamId)
    {
        var id = RequireTeam(teamId);
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == id);
            if (bill is null || !IsActive(bill))
                return SeatRecordOutcome.NoBill;

            var paid = PaidSeats(ctx, id);
            if (bill.Seats == paid)
                return SeatRecordOutcome.InStep;

            var before = bill.Seats;
            bill.Seats = paid;
            bill.UpdatedAtUtc = _utcNow();
            bill.Version++;
            if (!Save(ctx, "RecordSeats", id))
                return SeatRecordOutcome.ChangedElsewhere;
            FileLog.Write($"[TeamBillStore] RecordSeats: team {LogTeam(id)} seats {before} -> {paid}");
            return SeatRecordOutcome.Recorded;
        }
    }

    /// <summary>
    /// THE RENEWAL PASS over every active bill whose period has ended. An auto-renewing bill rolls to the next month -
    /// as many months as have passed, each with its own history line - at the team's paid-seat count now. A bill whose
    /// auto-renew is off becomes canceled. Each bill is saved on its own, so a bill another Gateway process changed first
    /// (the database refuses the stale write) is logged and skipped without holding up the rest. Returns what the pass did.
    /// </summary>
    public TeamBillRenewalSummary RenewDue()
    {
        lock (_writeLock)
        {
            var now = _utcNow();
            List<string> due;
            using (var read = _db.CreateUnscopedContext())
            {
                due = read.TeamBills.AsNoTracking()
                    .Where(b => b.Status == EntitlementRegistry.StatusActive && b.CurrentPeriodEndUtc <= now)
                    .Select(b => b.TeamId)
                    .ToList();
            }

            var rolled = 0;
            var ended = 0;
            var lines = 0;
            var changedElsewhere = 0;
            foreach (var teamId in due)
            {
                using var ctx = _db.CreateUnscopedContext();
                var bill = ctx.TeamBills.FirstOrDefault(b => b.TeamId == teamId);
                if (bill is null || !IsActive(bill) || bill.CurrentPeriodEndUtc > now)
                {
                    // Read as due a moment ago, and no longer: another writer got there first. Nothing to do.
                    changedElsewhere++;
                    FileLog.Write($"[TeamBillStore] RenewDue: team {LogTeam(teamId)} no longer due when re-read - another writer changed it first");
                    continue;
                }

                if (!bill.AutoRenew)
                {
                    bill.Status = EntitlementRegistry.StatusCanceled;
                    bill.UpdatedAtUtc = now;
                    bill.Version++;
                    BeforeRenewalSaveForTests?.Invoke(teamId);
                    if (!Save(ctx, "RenewDue", teamId)) { changedElsewhere++; continue; }
                    ended++;
                    FileLog.Write($"[TeamBillStore] RenewDue: team {LogTeam(teamId)} ENDED - auto-renew was off and the period ended {bill.CurrentPeriodEndUtc:O}");
                    continue;
                }

                bill.Seats = PaidSeats(ctx, teamId);
                var periods = 0;
                while (bill.CurrentPeriodEndUtc <= now && periods < MaxPeriodsPerPass)
                {
                    bill.CurrentPeriodStartUtc = bill.CurrentPeriodEndUtc;
                    bill.CurrentPeriodEndUtc = PeriodEnd(bill.PlanStartedUtc, bill.CurrentPeriodStartUtc);
                    ctx.TeamBillCharges.Add(ChargeFor(bill, ReasonAutoRenewed, now));
                    periods++;
                }
                bill.UpdatedAtUtc = now;
                bill.Version++;
                BeforeRenewalSaveForTests?.Invoke(teamId);
                if (!Save(ctx, "RenewDue", teamId)) { changedElsewhere++; continue; }
                rolled++;
                lines += periods;
                FileLog.Write($"[TeamBillStore] RenewDue: team {LogTeam(teamId)} RENEWED {periods} period(s), {bill.Seats} seat(s), period now ends {bill.CurrentPeriodEndUtc:O}, charged 0");
            }

            return new TeamBillRenewalSummary(due.Count, rolled, ended, lines, changedElsewhere);
        }
    }

    /// <summary>How many of a team's members hold a paid seat (Owner, Manager, Developer - <see cref="TeamSeatRoles"/>).</summary>
    public static int PaidSeats(GatewayDbContext ctx, string teamId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Select(m => m.Role)
            .ToList()
            .Count(role => TeamSeatRoles.IsPaidSeat(TeamRoles.ToStored(role)));
    }

    /// <summary>
    /// The end of the period that begins at <paramref name="start"/>, for a plan whose periods are anchored to
    /// <paramref name="planStarted"/>: the first whole number of months after the plan's start that lies after
    /// <paramref name="start"/>. Anchoring keeps the billing day: a plan started on 31 January runs to 28 February, then to
    /// 31 March - never drifting to the 28th, as adding a month to each period's end would.
    /// </summary>
    public static DateTime PeriodEnd(DateTime planStarted, DateTime start)
    {
        if (start < planStarted)
            throw new ArgumentException($"A period cannot begin ({start:O}) before its plan started ({planStarted:O}).", nameof(start));
        var months = 1;
        while (planStarted.AddMonths(months) <= start)
            months++;
        return planStarted.AddMonths(months);
    }

    /// <summary>A bill is active when its status says so.</summary>
    public static bool IsActive(TeamBillEntity bill) =>
        string.Equals(bill.Status, EntitlementRegistry.StatusActive, StringComparison.Ordinal);

    /// <summary>A date as every bill screen shows it: "6 Nov 2026".</summary>
    public static string Day(DateTime utc) => utc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static TeamBillChargeEntity ChargeFor(TeamBillEntity bill, string reason, DateTime now) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TeamId = bill.TeamId,
        PeriodStartUtc = bill.CurrentPeriodStartUtc,
        PeriodEndUtc = bill.CurrentPeriodEndUtc,
        Seats = bill.Seats,
        PricePerSeatCents = bill.PricePerSeatCents,
        AmountCents = bill.Seats * bill.PricePerSeatCents,
        // Pretending to charge (owner, 7 Oct 2026): the amount is recorded, nothing is charged.
        ChargedCents = 0,
        Reason = reason,
        CreatedAtUtc = now,
    };

    // Saves one write. False when the database refused it because another writer - another Gateway process - changed the
    // bill first (its version moved) or already wrote this period's history line. Nothing of this write is kept.
    private static bool Save(GatewayDbContext ctx, string method, string teamId)
    {
        try
        {
            ctx.SaveChanges();
            return true;
        }
        catch (DbUpdateException ex)
        {
            FileLog.Write($"[TeamBillStore] {method}: team {LogTeam(teamId)} NOT SAVED - another writer changed the bill first ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message})");
            return false;
        }
    }

    private static TeamBillChangeResult Refuse(string method, TeamBillChangeOutcome outcome, string reason)
    {
        FileLog.Write($"[TeamBillStore] {method}: REFUSED - {reason}");
        return new TeamBillChangeResult(outcome, reason);
    }

    private static string RequireTeam(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required.", nameof(teamId));
        return teamId.Trim();
    }

    private static string LogTeam(string teamId) => new TenantId(teamId).ToLogString();
}

/// <summary>What <see cref="TeamBillStore.EndForDeletedTeam"/> did.</summary>
public enum TeamBillEndOutcome
{
    /// <summary>The team never started its plan.</summary>
    NoBill,

    /// <summary>The bill had already ended.</summary>
    AlreadyEnded,

    /// <summary>An active bill was ended.</summary>
    Ended,

    /// <summary>Another writer changed the bill at the same moment; nothing was saved.</summary>
    ChangedElsewhere,
}

/// <summary>What <see cref="TeamBillStore.RecordSeats"/> found.</summary>
public enum SeatRecordOutcome
{
    /// <summary>The bill already carried the team's paid-seat count.</summary>
    InStep,

    /// <summary>The bill carried another count and now carries the team's.</summary>
    Recorded,

    /// <summary>The team has no active bill, so there is no seat count to record.</summary>
    NoBill,

    /// <summary>Another writer changed the bill first, so this count was not saved. The convergence pass records it on
    /// its next run.</summary>
    ChangedElsewhere,
}

/// <summary>What one renewal pass did.</summary>
/// <param name="Due">Active bills whose period had ended.</param>
/// <param name="Renewed">Of those, the auto-renewing bills rolled to the next month.</param>
/// <param name="Ended">Of those, the bills whose auto-renew was off, now canceled.</param>
/// <param name="HistoryLines">History lines written by the renewals.</param>
/// <param name="ChangedElsewhere">Of those, the bills another writer changed first, left as that writer saved them.</param>
public sealed record TeamBillRenewalSummary(int Due, int Renewed, int Ended, int HistoryLines, int ChangedElsewhere);

/// <summary>How a change to a team's bill ended.</summary>
public enum TeamBillChangeOutcome
{
    /// <summary>The change was made (or there was nothing to change).</summary>
    Done,

    /// <summary>No such team for this caller.</summary>
    NotFound,

    /// <summary>The caller's role may not make this change.</summary>
    Forbidden,

    /// <summary>The bill's own rules refuse it - for example renewing a bill that already renews itself.</summary>
    Refused,
}

/// <summary>The result of a change to a team's bill: the outcome and, unless it was done, the plain-words reason.</summary>
public sealed record TeamBillChangeResult(TeamBillChangeOutcome Outcome, string? Refusal)
{
    /// <summary>The change was made.</summary>
    public static readonly TeamBillChangeResult Done = new(TeamBillChangeOutcome.Done, null);
}

/// <summary>The plain-words reasons a change to a team's bill is refused. Never the word "free" (owner rule).</summary>
public static class TeamBillRefusals
{
    /// <summary>Starting a plan that is already running.</summary>
    public const string AlreadyStarted = "The team plan is already running.";

    /// <summary>Starting a plan that has ended.</summary>
    public const string RenewInstead = "The team plan has ended. Renew it to start a new month.";

    /// <summary>Any change to a plan that was never started.</summary>
    public const string NotStarted = "The team plan has not started yet. Start it first.";

    /// <summary>Switching auto-renew on a plan that has ended.</summary>
    public const string EndedRenewInstead = "The team plan has ended, so it has no auto-renew to change. Renew it to start a new month.";

    /// <summary>Cancelling a plan that has ended.</summary>
    public const string AlreadyEnded = "The team plan has already ended.";

    /// <summary>Any change that lost a race with another writer of the same bill.</summary>
    public const string ChangedAtTheSameMoment = "The team plan changed at the same moment. Reload the page to see it, then try again.";

    /// <summary>Renewing a plan that is still running with auto-renew off.</summary>
    public static string StillRunning(string day) => $"The team plan is still running until {day}. Switch auto-renew on to keep it going.";

    /// <summary>Renewing a plan that already renews itself.</summary>
    public static string AlreadyRenews(string day) => $"The team plan already renews by itself on {day}.";
}
