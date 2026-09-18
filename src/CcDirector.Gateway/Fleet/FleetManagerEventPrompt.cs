using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The ONE prompt a delivery sends to a Fleet Manager session: every event it is owed, oldest first.
///
/// EVERY EVENT CARRIES ITS ID, AND DELIVERY IS AT LEAST ONCE: the same event can arrive again (a Gateway that
/// stopped between typing and saving the delivery sends it again), so the prompt tells the Fleet Manager to ignore an
/// id it has already handled and to acknowledge by id.
///
/// COMPACT AND PLAIN. The framing is ASCII: a fixed first line the Fleet Manager's conduct names, one block per
/// event, and the acknowledge command at the end. The Wingman's words are copied as stored - the evidence exactly,
/// never reworded and never cut short, between markers of its own so a line break inside it cannot be mistaken
/// for the framing.
/// </summary>
internal static class FleetManagerEventPrompt
{
    /// <summary>What every delivery starts with. The Fleet Manager's conduct names it.</summary>
    public const string FirstLinePrefix = "[Fleet Manager events]";

    public const string EvidenceStart = "evidence (exact, between the markers):";
    public const string WordsStart = "the owner's words (exact, between the markers):";
    public const string EvidenceOpen = "<<<";
    public const string EvidenceClose = ">>>";

    /// <param name="events">The events, oldest first. At least one. The Wingman's readings are served to the Fleet
    /// Manager whatever the account's colour switch says, as the digest serves them.</param>
    /// <param name="moreOwed">How many more events are owed after these; they are sent at the next idle moment.</param>
    public static string Build(IReadOnlyList<FleetManagerEventDto> events, int moreOwed = 0)
    {
        if (moreOwed < 0) throw new ArgumentOutOfRangeException(nameof(moreOwed), "cannot be negative");
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("a delivery carries at least one event", nameof(events));

        var stops = events.Count(e => e.Kind == FleetManagerEventStore.KindStop);
        var died = events.Count(e => e.Kind == FleetManagerEventStore.KindDied);
        var answered = events.Count(e => e.Kind == FleetManagerEventStore.KindAnswered);
        var marked = events.Any(e => e.Kind == FleetManagerEventStore.KindMarked);
        var sb = new StringBuilder();
        sb.Append(FirstLinePrefix).Append(' ');
        if (marked) sb.Append("You are now this account's Fleet Manager. ");
        sb.Append(Count(stops, "stop", "stops")).Append(" and ")
          .Append(died).Append(" died");
        if (answered > 0) sb.Append(", and ").Append(Count(answered, "card answered by the owner", "cards answered by the owner"));
        sb.Append(" since your last turn.\n");
        sb.Append("Sessions you own. Act on each, then acknowledge it by its id. Readings are the Wingman's, not the session's.\n");
        sb.Append("An event can be sent more than once: if you have already handled an event id, do not act on it again - acknowledge it.\n");
        if (moreOwed > 0)
            sb.Append(Count(moreOwed, "more event waits", "more events wait"))
              .Append(" after these; they are sent when you are next waiting for a prompt. See them all: cc-devthrottle fleet events.\n");

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            sb.Append('\n');
            sb.Append("event ").Append(i + 1).Append(" of ").Append(events.Count).Append(": ").Append(e.Id).Append('\n');
            sb.Append("kind: ").Append(e.Kind).Append('\n');
            if (e.Kind != FleetManagerEventStore.KindAnswered)
                sb.Append("session: ").Append(e.SessionId).Append(" \"").Append(e.SessionName).Append("\"\n");
            if (e.Kind == FleetManagerEventStore.KindMarked)
            {
                sb.Append("what: ").Append(e.Detail).Append('\n');
                continue;
            }
            if (e.Kind == FleetManagerEventStore.KindAnswered)
            {
                // The owner's choice, as if the owner had said it. The words are copied as stored, between markers.
                sb.Append("record: ").Append(e.OutcomeId).Append(" \"").Append(e.OutcomeTitle).Append("\"\n");
                if (!string.IsNullOrEmpty(e.SessionId)) sb.Append("about session: ").Append(e.SessionId).Append('\n');
                sb.Append("answered by: the owner, on the Fleet Manager page. The record is already answered; act on the words.\n");
                sb.Append(WordsStart).Append('\n');
                sb.Append(EvidenceOpen).Append(e.Words).Append(EvidenceClose).Append('\n');
                continue;
            }
            if (e.Kind == FleetManagerEventStore.KindDied)
            {
                sb.Append("how: ").Append(e.Crashed == true ? "crashed" : "exited").Append('\n');
                if (!string.IsNullOrEmpty(e.Detail)) sb.Append("detail: ").Append(e.Detail).Append('\n');
                continue;
            }

            if (e.Verdict is null)
            {
                sb.Append("verdict: none - ").Append(e.NoVerdictReason ?? "the Wingman did not read this stop")
                  .Append(". Read the session yourself.\n");
                continue;
            }

            var v = e.Verdict;
            // The stop's identity: a record filed about this stop names it (fleet ... --verdict), so an answer to a
            // later stop of the session never closes that record.
            sb.Append("verdictId: ").Append(v.VerdictId).Append('\n');
            if (v.Failed)
            {
                sb.Append("verdict: failed - ").Append(v.FailureReason ?? "the reading failed")
                  .Append(". Read the session yourself.\n");
                continue;
            }
            sb.Append("verdict: ").Append(v.Verdict).Append('\n');
            sb.Append("finishedKind: ").Append(v.FinishedKind ?? "-").Append('\n');
            sb.Append("label: ").Append(v.Label).Append('\n');
            sb.Append("summary: ").Append(v.Summary).Append('\n');
            sb.Append("risk: ").Append(v.Risk).Append('\n');
            sb.Append("answerVia: ").Append(v.AnswerVia).Append('\n');
            sb.Append("options: ");
            if (v.Options.Count == 0) sb.Append('-');
            for (var o = 0; o < v.Options.Count; o++)
            {
                var opt = v.Options[o];
                if (o > 0) sb.Append(" | ");
                sb.Append(opt.Key).Append("=\"").Append(opt.Send).Append('"');
                if (opt.Recommended) sb.Append(" (recommended)");
            }
            sb.Append('\n');
            sb.Append("agentRecommends: ").Append(v.AgentRecommends ?? "-").Append('\n');
            sb.Append(EvidenceStart).Append('\n');
            sb.Append(EvidenceOpen).Append(v.Evidence).Append(EvidenceClose).Append('\n');
        }

        sb.Append('\n');
        sb.Append("When you have acted on an event (filed an outcome, answered it, or decided it needs nothing): ")
          .Append("cc-devthrottle fleet ack <event id> [<event id> ...]. List what is still open: cc-devthrottle fleet events.\n");
        return sb.ToString();
    }

    private static string Count(int n, string one, string many) => n + " " + (n == 1 ? one : many);
}
