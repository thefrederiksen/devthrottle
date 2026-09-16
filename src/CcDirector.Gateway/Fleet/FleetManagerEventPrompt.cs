using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The ONE prompt a delivery sends to a Fleet Manager session: every event it is owed, oldest first.
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
    public const string EvidenceOpen = "<<<";
    public const string EvidenceClose = ">>>";

    /// <param name="events">The events, oldest first. At least one.</param>
    /// <param name="verdictsShown">False while the account's readings are a shadow record: the reading is left
    /// out of the prompt and the prompt says so, as the turn verdict route does for a session key.</param>
    public static string Build(IReadOnlyList<FleetManagerEventDto> events, bool verdictsShown)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("a delivery carries at least one event", nameof(events));

        var stops = events.Count(e => e.Kind == FleetManagerEventStore.KindStop);
        var died = events.Count - stops;
        var sb = new StringBuilder();
        sb.Append(FirstLinePrefix).Append(' ')
          .Append(Count(stops, "stop", "stops")).Append(" and ")
          .Append(died).Append(" died since your last turn.\n");
        sb.Append("Sessions you own. Act on each, then acknowledge it. Readings are the Wingman's, not the session's.\n");

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            sb.Append('\n');
            sb.Append("event ").Append(i + 1).Append(" of ").Append(events.Count).Append(": ").Append(e.Id).Append('\n');
            sb.Append("kind: ").Append(e.Kind).Append('\n');
            sb.Append("session: ").Append(e.SessionId).Append(" \"").Append(e.SessionName).Append("\"\n");
            if (e.Kind == FleetManagerEventStore.KindDied)
            {
                sb.Append("how: ").Append(e.Crashed == true ? "crashed" : "exited").Append('\n');
                continue;
            }

            if (e.Verdict is null)
            {
                sb.Append("verdict: none - ").Append(e.NoVerdictReason ?? "the Wingman did not read this stop")
                  .Append(". Read the session yourself.\n");
                continue;
            }
            if (!verdictsShown)
            {
                sb.Append("verdict: withheld - this account's Wingman readings are still a shadow record. Read the session yourself.\n");
                continue;
            }

            var v = e.Verdict;
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
          .Append("cc-devthrottle fleet ack <event id>. List what is still open: cc-devthrottle fleet events.\n");
        return sb.ToString();
    }

    private static string Count(int n, string one, string many) => n + " " + (n == 1 ? one : many);
}
