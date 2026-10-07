namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One line of a team's billing history: one period of the team's plan, what it would have cost and what was charged
/// (Teams v1, the team bill without Stripe). Written by <see cref="Teams.TeamBillStore"/> every time a period begins -
/// when the Owner starts or renews the plan, and when the renewal pass rolls an auto-renewing bill to the next month.
///
/// <see cref="ChargedCents"/> is always 0 today: the owner's ruling is to pretend to charge. The amount it would have
/// been (<see cref="AmountCents"/> = <see cref="Seats"/> x <see cref="PricePerSeatCents"/>) is recorded beside it, so
/// the history reads exactly as a real one will once payment is connected.
///
/// GLOBAL, like <see cref="TeamBillEntity"/>, and for the same reason. Never changed once written.
/// </summary>
public sealed class TeamBillChargeEntity
{
    /// <summary>The line's id - a GUID string generated in code.</summary>
    public string Id { get; set; } = "";

    /// <summary>The team (<see cref="TeamEntity.Id"/>).</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The period this line pays for: its start (UTC).</summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>The period this line pays for: its end (UTC).</summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>The paid seats when the period began.</summary>
    public int Seats { get; set; }

    /// <summary>The price of one seat for the period, in US cents.</summary>
    public int PricePerSeatCents { get; set; }

    /// <summary>What the period costs: seats x price per seat, in US cents.</summary>
    public int AmountCents { get; set; }

    /// <summary>What was actually charged, in US cents. 0 - nothing is charged yet.</summary>
    public int ChargedCents { get; set; }

    /// <summary>Why the period began: <c>started</c>, <c>renewed</c> (the Owner renewed by hand) or <c>auto-renewed</c>
    /// (the renewal pass).</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the line was written (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}
