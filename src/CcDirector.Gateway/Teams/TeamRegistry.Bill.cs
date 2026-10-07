using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The team's bill on the Team page (Teams v1, the team bill without Stripe): what the Billing section shows, and the
/// Owner's four actions - start the plan, renew now, auto-renew on or off, cancel.
///
/// WHO MAY DO WHAT IS ASKED OF <see cref="TeamAccess"/>. Seeing the bill is the row "see the team's bill" (the Owner and
/// Managers); every change is the row "change the billing, or rename or delete the team" (the Owner alone). A Developer
/// or Collaborator gets no Billing section at all.
///
/// THE GATEWAY DECIDES EVERY VERDICT (product rule 7). The status and its sentence, the money lines, which buttons show,
/// the checkout's confirmation and each history line are finished here; the page renders them.
///
/// PRETENDING TO CHARGE (owner, 7 Oct 2026). Every amount is real - seats x US$49.00 - and every charge is US$0.00. No
/// sentence a person sees says "free" (owner rule); it says "No charge".
/// </summary>
public sealed partial class TeamRegistry
{
    /// <summary>The words every bill screen uses where a charge would be: never "free" (owner rule).</summary>
    public const string NoCharge = "No charge";

    /// <summary>What a Manager reads under the bill they may see and not change.</summary>
    public const string OwnerChangesTheBill = "Only the team's Owner can change the billing.";

    /// <summary>
    /// The Billing section for <paramref name="callerSubject"/>: not found for someone who is not a member, forbidden with
    /// the role table's sentence for a role that may not see the bill.
    /// </summary>
    public TeamBillViewResult DescribeTeamBill(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] DescribeTeamBill: team {LogTeam(teamId)}");

        var see = _access.Decide(teamId ?? "", caller, TeamAction.SeeTeamBill);
        if (!see.IsMember)
            return TeamBillViewResult.NotFound;
        if (!see.Allowed)
        {
            FileLog.Write($"[TeamRegistry] DescribeTeamBill: REFUSED - a {see.Role} may not see the team's bill");
            return TeamBillViewResult.Forbidden(see.Refusal!);
        }

        var mayChange = _access.Decide(teamId!, caller, TeamAction.BillingRenameOrDeleteTeam).Allowed;
        return TeamBillViewResult.Found(BuildBillView(teamId!.Trim(), mayChange));
    }

    /// <summary>START THE TEAM PLAN, as the Owner: one month, auto-renew on, at the team's paid-seat count.</summary>
    public TeamBillChangeResult StartTeamPlan(string teamId, string callerSubject) =>
        ChangeBill("StartTeamPlan", teamId, callerSubject, id => _bills.Start(id));

    /// <summary>RENEW NOW, as the Owner: an ending or ended plan becomes active for a new month, auto-renew on.</summary>
    public TeamBillChangeResult RenewTeamPlan(string teamId, string callerSubject) =>
        ChangeBill("RenewTeamPlan", teamId, callerSubject, id => _bills.Renew(id));

    /// <summary>AUTO-RENEW ON OR OFF, as the Owner, on a running plan.</summary>
    public TeamBillChangeResult SetTeamPlanAutoRenew(string teamId, string callerSubject, bool on) =>
        ChangeBill("SetTeamPlanAutoRenew", teamId, callerSubject, id => _bills.SetAutoRenew(id, on));

    /// <summary>CANCEL, as the Owner: the plan stays active to the end of its period, then ends.</summary>
    public TeamBillChangeResult CancelTeamPlan(string teamId, string callerSubject) =>
        ChangeBill("CancelTeamPlan", teamId, callerSubject, id => _bills.Cancel(id));

    private TeamBillChangeResult ChangeBill(string method, string teamId, string callerSubject, Func<string, TeamBillChangeResult> change)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] {method}: team {LogTeam(teamId)}");

        var decision = _access.Decide(teamId ?? "", caller, TeamAction.BillingRenameOrDeleteTeam);
        if (!decision.IsMember)
        {
            FileLog.Write($"[TeamRegistry] {method}: no such team for this caller");
            return new TeamBillChangeResult(TeamBillChangeOutcome.NotFound, TeamRefusals.NoSuchTeam);
        }
        if (!decision.Allowed)
        {
            FileLog.Write($"[TeamRegistry] {method}: REFUSED - a {decision.Role} may not change the team's bill");
            return new TeamBillChangeResult(TeamBillChangeOutcome.Forbidden, decision.Refusal);
        }

        var result = change(teamId!.Trim());
        FileLog.Write($"[TeamRegistry] {method}: team {LogTeam(teamId)} outcome={result.Outcome}");
        return result;
    }

    /// <summary>The Billing section's model for one team, for a caller who may see it.</summary>
    private TeamBillView BuildBillView(string teamId, bool mayChange)
    {
        var bill = _bills.Find(teamId);
        var history = _bills.History(teamId).Select(HistoryLine).ToList();

        if (bill is null)
        {
            int paid;
            using (var ctx = _db.CreateUnscopedContext())
                paid = TeamBillStore.PaidSeats(ctx, teamId);
            return new TeamBillView(
                State: TeamBillStates.NotStarted,
                StatusLabel: "Not started",
                StatusLine: "The team plan has not started. Members can be invited once it has.",
                Seats: paid,
                SeatsLine: SeatsWords(paid),
                PriceLine: PriceWords(TeamBillStore.PricePerSeatCents),
                AmountLine: AmountWords(paid, TeamBillStore.PricePerSeatCents),
                ChargeLine: NoCharge,
                PeriodEndUtc: null,
                PeriodEnd: null,
                AutoRenew: false,
                CanChange: mayChange,
                CanStart: mayChange,
                CanRenew: false,
                CanSetAutoRenew: false,
                CanCancel: false,
                Checkout: mayChange ? Checkout("Start the team plan", paid, TeamBillStore.PricePerSeatCents, "Start the plan") : null,
                Note: mayChange ? null : OwnerChangesTheBill,
                CancelWarning: null,
                History: history);
        }

        var active = TeamBillStore.IsActive(bill);
        var state = !active ? TeamBillStates.Ended : bill.AutoRenew ? TeamBillStates.Active : TeamBillStates.Ending;
        var end = TeamBillStore.Day(bill.CurrentPeriodEndUtc);
        var (label, line) = state switch
        {
            TeamBillStates.Active => ("Active", $"Renews on {end}. Auto-renew is on."),
            TeamBillStates.Ending => ("Ending", $"Ends on {end}. Auto-renew is off - switch it on to keep the team plan."),
            _ => ("Ended", $"Ended on {end}. Renew to start a new month."),
        };
        // Renew is offered only once the plan has ENDED (the Delivery Lead's ruling, 7 Oct 2026). An ending plan goes back
        // by switching auto-renew on, which keeps its period: a renewal would begin a second, overlapping month.
        var canRenew = mayChange && state == TeamBillStates.Ended;
        return new TeamBillView(
            State: state,
            StatusLabel: label,
            StatusLine: line,
            Seats: bill.Seats,
            SeatsLine: SeatsWords(bill.Seats),
            PriceLine: PriceWords(bill.PricePerSeatCents),
            AmountLine: AmountWords(bill.Seats, bill.PricePerSeatCents),
            ChargeLine: NoCharge,
            PeriodEndUtc: bill.CurrentPeriodEndUtc,
            PeriodEnd: end,
            AutoRenew: active && bill.AutoRenew,
            CanChange: mayChange,
            CanStart: false,
            CanRenew: canRenew,
            CanSetAutoRenew: mayChange && active,
            CanCancel: mayChange && state == TeamBillStates.Active,
            Checkout: canRenew ? Checkout("Renew the team plan", bill.Seats, bill.PricePerSeatCents, "Renew now") : null,
            Note: mayChange ? null : OwnerChangesTheBill,
            CancelWarning: mayChange && state == TeamBillStates.Active
                ? $"The team plan stays active until {end}, then ends. After that nobody can be invited or join, and paid features stop for the team. Until then, switching auto-renew back on keeps it; after it ends, you can renew at any time."
                : null,
            History: history);
    }

    private TeamBillCheckout Checkout(string title, int seats, int pricePerSeatCents, string confirmLabel)
    {
        var start = _utcNow();
        return new TeamBillCheckout(
            Title: title,
            SeatsLine: SeatsWords(seats),
            PriceLine: PriceWords(pricePerSeatCents),
            TotalLine: $"Total: {Money(seats * pricePerSeatCents)} a month",
            ChargeLine: $"{NoCharge} - you will not be charged",
            PeriodLine: $"{TeamBillStore.Day(start)} to {TeamBillStore.Day(TeamBillStore.PeriodEnd(start, start))}, then every month until you cancel",
            ConfirmLabel: confirmLabel);
    }

    private static TeamBillHistoryLine HistoryLine(TeamBillChargeEntity c) => new(
        Id: c.Id,
        Period: $"{TeamBillStore.Day(c.PeriodStartUtc)} to {TeamBillStore.Day(c.PeriodEndUtc)}",
        Seats: c.Seats,
        Amount: $"{Money(c.PricePerSeatCents)} x {SeatsWords(c.Seats)} = {Money(c.AmountCents)}",
        Charged: $"Charged: {Money(c.ChargedCents)}",
        Reason: c.Reason switch
        {
            TeamBillStore.ReasonStarted => "Plan started",
            TeamBillStore.ReasonRenewed => "Renewed",
            TeamBillStore.ReasonAutoRenewed => "Renewed automatically",
            var other => other,
        });

    /// <summary>An amount in US cents as the bill shows it: "US$1,470.00".</summary>
    public static string Money(int cents) =>
        "US$" + (cents / 100m).ToString("N2", CultureInfo.InvariantCulture);

    private static string SeatsWords(int seats) => seats == 1 ? "1 paid seat" : $"{seats} paid seats";

    private static string PriceWords(int pricePerSeatCents) => $"{Money(pricePerSeatCents)} a seat a month";

    private static string AmountWords(int seats, int pricePerSeatCents) =>
        $"{Money(pricePerSeatCents)} x {SeatsWords(seats)} = {Money(seats * pricePerSeatCents)} a month";
}

/// <summary>The four states the Billing section names. Not stored: worked out from the bill's status and auto-renew.</summary>
public static class TeamBillStates
{
    /// <summary>The team has never started its plan.</summary>
    public const string NotStarted = "not-started";

    /// <summary>Running, and renews itself at the end of the period.</summary>
    public const string Active = "active";

    /// <summary>Running to the end of the period, then ends (auto-renew off, or cancelled).</summary>
    public const string Ending = "ending";

    /// <summary>Ended: the plan is canceled.</summary>
    public const string Ended = "ended";
}

/// <summary>What the Billing section shows one caller. Every flag and sentence is the Gateway's verdict.</summary>
/// <param name="State">not-started, active, ending or ended (<see cref="TeamBillStates"/>).</param>
/// <param name="PeriodEnd">The period's last day as the Gateway words it ("7 Nov 2026", UTC) - the same day the status line
/// names. A client renders it and never formats <paramref name="PeriodEndUtc"/> itself: in a time zone behind UTC that
/// showed the day before.</param>
/// <param name="CanChange">Whether the caller may change the bill (the Owner). False for a Manager, who sees it read-only.</param>
/// <param name="Checkout">The confirmation to show before starting or renewing; null when neither is offered.</param>
/// <param name="Note">A sentence under the bill for a caller who may not change it (a Manager); null for the Owner.</param>
/// <param name="CancelWarning">What the cancel confirmation says; null when Cancel is not offered.</param>
public sealed record TeamBillView(string State, string StatusLabel, string StatusLine, int Seats, string SeatsLine,
    string PriceLine, string AmountLine, string ChargeLine, DateTime? PeriodEndUtc, string? PeriodEnd, bool AutoRenew, bool CanChange,
    bool CanStart, bool CanRenew, bool CanSetAutoRenew, bool CanCancel, TeamBillCheckout? Checkout, string? Note,
    string? CancelWarning, IReadOnlyList<TeamBillHistoryLine> History);

/// <summary>The checkout-style confirmation shown before the plan starts or renews: seats, price, total, and no charge.</summary>
public sealed record TeamBillCheckout(string Title, string SeatsLine, string PriceLine, string TotalLine, string ChargeLine,
    string PeriodLine, string ConfirmLabel);

/// <summary>One line of the billing history, finished for display.</summary>
public sealed record TeamBillHistoryLine(string Id, string Period, int Seats, string Amount, string Charged, string Reason);

/// <summary>How a Billing section request ended.</summary>
public enum TeamBillViewOutcome
{
    /// <summary>The caller may see the bill.</summary>
    Found,

    /// <summary>No such team for this caller.</summary>
    NotFound,

    /// <summary>The caller's role may not see the bill.</summary>
    Forbidden,
}

/// <summary>The Billing section for one caller, or why it is not shown.</summary>
public sealed record TeamBillViewResult(TeamBillViewOutcome Outcome, TeamBillView? View, string? Refusal)
{
    /// <summary>No such team for this caller.</summary>
    public static readonly TeamBillViewResult NotFound = new(TeamBillViewOutcome.NotFound, null, null);

    /// <summary>The caller may see the bill.</summary>
    public static TeamBillViewResult Found(TeamBillView view) => new(TeamBillViewOutcome.Found, view, null);

    /// <summary>The caller's role may not see the bill.</summary>
    public static TeamBillViewResult Forbidden(string refusal) => new(TeamBillViewOutcome.Forbidden, null, refusal);
}
