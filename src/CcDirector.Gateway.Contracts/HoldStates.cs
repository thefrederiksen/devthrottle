namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The canonical hold ("Snooze") state values as they ride the wire. The Director's hold machine
/// (<c>CcDirector.Core.Sessions.HoldState</c>) has THREE states; this is how all three cross to the
/// Gateway and every client, on <see cref="SessionDto.HoldState"/>.
///
/// Strings, not the enum, because this assembly is deliberately reference-free (it is the shared wire
/// contract) - the same reason <see cref="SessionDto.ActivityState"/> is a string. These named constants
/// are the SINGLE source of truth for the values; use them instead of bare literals.
///
/// WHY THREE VALUES AND NOT ONE BOOLEAN (defect 12, the root cause of defect 20). Until 14 July 2026 the
/// wire carried only the boolean <see cref="SessionDto.OnHold"/>, so <see cref="None"/> and
/// <see cref="DeferredHold"/> were byte-identical - both read <c>OnHold=false</c>. Nothing downstream
/// could tell "not held" from "about to be held". The Gateway's snooze expiry sweep read that boolean,
/// concluded a deferred snooze was over, and deleted its timer 15 seconds after it was asked for - so an
/// agent-requested snooze never expired. A lossy wire is not a cosmetic problem; it is how the answer
/// gets lost. See docs/new_architecture/sessions.html.
/// </summary>
public static class HoldStates
{
    /// <summary>Not held. The session's activity state is the whole story.</summary>
    public const string None = "None";

    /// <summary>Parked right now: grey "Snoozed", sunk to the bottom, never "needs you".</summary>
    public const string Held = "Held";

    /// <summary>
    /// "Snooze me when this finishes" - asked for while the agent was working, so it is NOT parked yet.
    /// The session is still working, still blue, and still reads as working. It parks (-&gt;
    /// <see cref="Held"/>) the moment the work stops.
    /// </summary>
    public const string DeferredHold = "DeferredHold";

    /// <summary>The three valid values.</summary>
    public static readonly IReadOnlyList<string> All = new[] { None, Held, DeferredHold };

    /// <summary>
    /// True when <paramref name="holdState"/> means PARKED RIGHT NOW - i.e. exactly <see cref="Held"/>.
    /// This is the one predicate <see cref="SessionDto.OnHold"/> derives from, so the boolean and the
    /// tri-state can never disagree. A <see cref="DeferredHold"/> is deliberately NOT held: it has not
    /// landed yet. Compared case-insensitively, matching every other wire-string comparison in the fold.
    /// </summary>
    public static bool IsHeld(string? holdState) =>
        string.Equals(holdState, Held, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="holdState"/> is a hold that has been ASKED FOR but has not landed - i.e.
    /// exactly <see cref="DeferredHold"/>. The fact the snooze sweep must never mistake for
    /// <see cref="None"/>.
    /// </summary>
    public static bool IsDeferred(string? holdState) =>
        string.Equals(holdState, DeferredHold, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonicalize a hold-state string (case-insensitive, trimmed) to one of the three constants, or
    /// null when it is empty or not a known value. A null return is "I do not know", never a guess -
    /// callers that need certainty (the snooze sweep) treat it as a missed read and change nothing.
    /// </summary>
    public static string? Normalize(string? holdState)
    {
        if (string.IsNullOrWhiteSpace(holdState)) return null;
        var trimmed = holdState.Trim();
        foreach (var s in All)
        {
            if (string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }
}
