using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// THE PROMPT'S AGE FROM SEND (Voice Delivery mission, phase 5, QA finding F6): one check, used by the Director's
/// prompt verb at receipt and again at the first keystroke, of the age the Gateway put on the request
/// (<see cref="PromptRequest.SentAtUtc"/>). On 25 September 2026 a frozen Director held a spoken command for seven
/// minutes and typed it the moment it woke: the Gateway's 5-minute rule guarded only what the Gateway still held,
/// so a command already handed to the Director had no age limit at all. Now the Director refuses one itself -
/// strictly past <see cref="MaxDeliveryAge"/> nothing is typed, and the delivery is answered not-delivered with the
/// too-old reason.
///
/// The check compares the GATEWAY'S clock (the Send time) with the DIRECTOR MACHINE'S clock. The measured age goes
/// into the reason and the one log line each refusal writes, so a skewed clock is visible; no correction is added.
/// </summary>
public static class PromptAgeLimit
{
    /// <summary>
    /// Is the prompt strictly past the limit at <paramref name="now"/>? False when it carries no Send time - a
    /// Gateway older than the field, which the Director types as today - and false AT the limit itself:
    /// 4:59 and 5:00 from Send are typed, 5:01 is not.
    /// </summary>
    /// <param name="sentAtUtc">The Send time the Gateway put on the request; null when it carried none.</param>
    /// <param name="now">The Director machine's clock, at the point of the check.</param>
    /// <param name="age">The measured age, out for the refusal's reason and log line; <c>default</c> when there is
    /// nothing to measure.</param>
    public static bool IsTooOld(DateTime? sentAtUtc, DateTime now, out TimeSpan age)
    {
        age = default;
        if (sentAtUtc is not { } sent)
            return false;
        age = now - sent;
        return age > MaxDeliveryAge.Span;
    }

    /// <summary>
    /// The refusal's reason, one string for the verb's answer, the delivery record and the Gateway's decision log:
    /// the fixed too-old word, then the measured age and where it was measured. The word is
    /// <see cref="MaxDeliveryAge.TooOldReason"/>, so the Gateway can tell a refusal for age from any other
    /// not-delivered answer.
    /// </summary>
    /// <param name="age">The measured age, as <see cref="IsTooOld"/> produced it.</param>
    /// <param name="where">Where the age was measured, in the Director's own words - "when the Director received
    /// it", "at the first keystroke".</param>
    public static string TooOldReason(TimeSpan age, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        return $"{MaxDeliveryAge.TooOldReason}: the prompt was {Describe(age)} old from Send {where}, past the " +
               $"{MaxDeliveryAge.Minutes}-minute limit; nothing was typed";
    }

    /// <summary>The measured age in plain words, for the reason and the one log line a refusal writes: 5m 01s.</summary>
    public static string Describe(TimeSpan age) => $"{(int)age.TotalMinutes}m {age.Seconds:D2}s";
}
