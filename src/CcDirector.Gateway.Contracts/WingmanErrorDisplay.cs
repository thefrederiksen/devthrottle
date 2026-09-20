namespace CcDirector.Gateway.Contracts;

/// <summary>
/// WHAT A SESSION CARD SAYS WHEN THE WINGMAN COULD NOT READ A STOP (mission "Wingman error and retry",
/// 2026-09-19). Folded once on the Gateway (<c>WingmanErrorFold</c>) and rendered verbatim by the Cockpit and the
/// mobile app through one shared component. A client never decides whether a reading failed, never words the
/// reason, and never decides whether a retry is coming - it only counts down to <see cref="NextRetryAtUtc"/>.
///
/// THE ONE INVARIANT, AND THE REASON THIS TYPE EXISTS: <see cref="Exhausted"/> is true exactly when
/// <see cref="NextRetryAtUtc"/> is null. A card says an attempt is coming only while one is booked, and says
/// "nothing more is scheduled" only when nothing is. On 19 September 2026 seven cards said "the Gateway is still
/// trying" with nothing booked for any of them.
///
/// Null on the row means no error: the reading succeeded, is being made now, or the session is working.
/// </summary>
public sealed class WingmanErrorDisplay
{
    /// <summary>The words on the small tag: "Wingman error".</summary>
    public string Tag { get; set; } = "";

    /// <summary>A short plain reason a person can read on a card. Never an exception message.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Which retry is next, counting from one, or zero when the schedule is used up.</summary>
    public int NextRetryNumber { get; set; }

    /// <summary>How many retries one stop gets in all.</summary>
    public int RetriesTotal { get; set; }

    /// <summary>How many retries are still to come, the next one included. Zero when used up.</summary>
    public int RetriesRemaining { get; set; }

    /// <summary>When the next retry is booked for (UTC), so a client counts down by itself. Null when nothing is
    /// booked.</summary>
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>True when the schedule is used up and nothing more is scheduled.</summary>
    public bool Exhausted { get; set; }

    /// <summary>The start of the retry line while a retry is booked, for example "retry 2 of 8". The client adds
    /// only the countdown after it. Empty when <see cref="Exhausted"/>.</summary>
    public string RetryLabel { get; set; } = "";

    /// <summary>The whole line when <see cref="Exhausted"/>: there was a problem and nothing more is scheduled.
    /// Empty while a retry is booked.</summary>
    public string ExhaustedText { get; set; } = "";

    /// <summary>The words on the button that asks again now. Offered from the first failure.</summary>
    public string AskAgainLabel { get; set; } = "";
}
