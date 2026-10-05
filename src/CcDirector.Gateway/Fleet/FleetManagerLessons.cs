using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE BLOCK OF LESSONS EVERY FLEET MANAGER IS GIVEN (issue #3559). The owner's confirmed lessons, in the
/// owner's words, written once here and put into every text the Gateway or the Director already owns at the
/// moments a Fleet Manager's context starts: its first prompt on a plain start
/// (<see cref="FleetManagerPlacementService.BuildStartRequest"/>), the <c>marked</c> event on a restart, a move or a
/// mark by hand (<see cref="FleetManagerEventPrompt"/>), and the session-start context a compaction or a clear
/// injects (the preamble the Director keeps for the marked session).
///
/// ONLY CONFIRMED LESSONS. A lesson the Fleet Manager kept with its own key waits for the owner; it is never in this
/// block (<see cref="FleetPreferenceStore"/> says why). The caller passes
/// <see cref="FleetPreferenceStore.ConfirmedLessons"/>, and this refuses anything else rather than filtering it, so a
/// caller that passed the wrong list fails loudly instead of quietly injecting an unconfirmed lesson.
///
/// THE WORDS ARE COPIED AS STORED, between markers of their own, exactly as the owner's answer is in an
/// <c>answered</c> event - so a line break inside a lesson cannot be mistaken for the framing (and the store refuses a
/// lesson that contains a marker). Nothing is shortened: the store caps the count and the length instead (at most 20
/// of 500 characters), and the block carries the owner's words only - the one line about the mistake stays in the
/// digest, the lesson event and the owner's list - so it stays within about 10,000 characters plus its framing.
/// </summary>
public static class FleetManagerLessons
{
    /// <summary>What the block starts with. The workflow names it.</summary>
    public const string Heading = "[Fleet Manager lessons]";

    public const string Open = "<<<";
    public const string Close = ">>>";

    /// <summary>
    /// The block, or null when there are no confirmed lessons - a Fleet Manager with none is given no heading
    /// saying so, because "you have no lessons" is not news it can act on.
    /// </summary>
    /// <param name="confirmed">The account's confirmed lessons, oldest first.</param>
    /// <exception cref="ArgumentException">A row is not a confirmed lesson.</exception>
    public static string? Build(IReadOnlyList<FleetPreferenceDto> confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        if (confirmed.Count == 0) return null;
        foreach (var l in confirmed)
        {
            if (l.Kind != FleetPreferenceStore.KindLesson)
                throw new ArgumentException($"row {l.Id} is a {l.Kind}, not a lesson; only lessons go in the lessons block", nameof(confirmed));
            if (l.ConfirmedByOwnerAtUtc is null)
                throw new ArgumentException($"lesson {l.Id} is not confirmed by the owner; only confirmed lessons are given to a Fleet Manager", nameof(confirmed));
        }

        var sb = new StringBuilder();
        sb.Append(Heading).Append(' ')
          .Append(confirmed.Count == 1 ? "1 lesson" : confirmed.Count + " lessons")
          .Append(" from the owner's corrections of this account's Fleet Managers. Obey them before anything else.\n");
        for (var i = 0; i < confirmed.Count; i++)
        {
            var l = confirmed[i];
            sb.Append("lesson ").Append(i + 1).Append(", kept ")
              .Append(l.CreatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" (the owner's words, exact, between the markers):\n");
            sb.Append(Open).Append(l.Text).Append(Close).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary><paramref name="text"/> with the block in front of it, or the text alone when there is no block.</summary>
    public static string Prepend(string? block, string text)
        => string.IsNullOrEmpty(block) ? text : block + "\n" + text;
}
