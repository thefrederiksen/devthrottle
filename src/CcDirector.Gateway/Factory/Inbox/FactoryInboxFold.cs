using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// WHAT A FACTORY INBOX MESSAGE SAYS ON SCREEN (Factory Inbox mission, phase 1; CLAUDE.md rule 7). Pure: the rows go in,
/// the finished labels and the trail come out, and a client prints them as they are. The trail is each step the message
/// has taken - sent, received, with the Boss, merged, decided - with its own label and time.
/// </summary>
public static class FactoryInboxFold
{
    /// <summary>One message, folded with its decision (null when it has none).</summary>
    public static FactoryInboxMessageDto Message(FactoryInboxMessageEntity m, FactoryInboxDecisionEntity? decision,
        DateTime? handedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new FactoryInboxMessageDto(
            Id: m.Id.ToString("D"),
            Factory: m.Factory,
            Text: m.Text,
            RawText: m.RawText,
            Note: m.Note,
            NoteLabel: FactoryInboxWords.NoteLabel(m.Note),
            Source: m.Source,
            Surface: m.Surface,
            SentAtUtc: m.SentAtUtc,
            State: m.State,
            StateLabel: FactoryInboxWords.StateLabel(m.State, decision?.Kind),
            MergedInto: m.MergedIntoId?.ToString("D"),
            Links: FactoryInboxStore.ReadLinks(m.LinksJson),
            BundleId: m.BundleId?.ToString("D"),
            Decision: decision is null ? null : Decision(decision),
            Trail: Trail(m, decision, handedAtUtc));
    }

    /// <summary>One decision, folded.</summary>
    public static FactoryInboxDecisionDto Decision(FactoryInboxDecisionEntity d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new FactoryInboxDecisionDto(d.Kind, FactoryInboxWords.DecisionLabel(d.Kind), d.Why, d.DelegatedSessionId,
            d.TodoId?.ToString("D"), d.DecidedBySessionId, d.DecidedAtUtc);
    }

    /// <summary>The steps a message has taken, in order.</summary>
    public static IReadOnlyList<FactoryInboxTrailStepDto> Trail(FactoryInboxMessageEntity m, FactoryInboxDecisionEntity? decision,
        DateTime? handedAtUtc = null)
    {
        var trail = new List<FactoryInboxTrailStepDto>
        {
            new(FactoryInboxWords.Steps.Sent, $"Sent {FactoryInboxWords.SurfacePhrase(m.Surface)}", m.SentAtUtc),
        };
        if (m.ReceivedAtUtc is { } received)
            trail.Add(new(FactoryInboxWords.Steps.Received, "Received by the Secretary", received));
        if (m.MergedIntoId is { } into)
            trail.Add(new(FactoryInboxWords.Steps.Merged, $"Merged into message {into:D}, which repeats it", m.ReceivedAtUtc));
        if (m.BundleId is not null)
            trail.Add(new(FactoryInboxWords.Steps.WithBoss, "Handed to the Boss", handedAtUtc));
        if (decision is not null)
            trail.Add(new(FactoryInboxWords.Steps.Decided, DecidedLine(decision), decision.DecidedAtUtc));
        return trail;
    }

    /// <summary>The sentence a decision adds to the trail.</summary>
    public static string DecidedLine(FactoryInboxDecisionEntity d) => d.Kind switch
    {
        FactoryInboxWords.Decisions.Delegated => $"Delegated to session {d.DelegatedSessionId}",
        FactoryInboxWords.Decisions.Todo => "Added to the factory's to-do list",
        FactoryInboxWords.Decisions.Noted => "Noted by the Boss",
        _ => $"{FactoryInboxWords.DecisionLabel(d.Kind)}: {d.Why}",
    };

    /// <summary>
    /// The factory summary (built from the log, no agent): what came in since a time, and the decision on each. The line
    /// is the one sentence a morning message or the Summary button shows.
    /// </summary>
    public static FactoryInboxSummaryDto Summary(string factory, DateTime sinceUtc, DateTime untilUtc,
        IReadOnlyList<FactoryInboxMessageEntity> sent, IReadOnlyList<FactoryInboxDecisionEntity> decided,
        IReadOnlyDictionary<Guid, FactoryInboxDecisionEntity> decisionOfSent, IReadOnlyDictionary<Guid, DateTime> handedAt)
    {
        var byDecision = FactoryInboxWords.Decisions.All
            .Select(k => new FactoryInboxCountDto(k, FactoryInboxWords.DecisionLabel(k), decided.Count(d => d.Kind == k)))
            .Where(c => c.Count > 0)
            .ToList();
        var waiting = sent.Count(m => !decisionOfSent.ContainsKey(m.Id));
        var line = sent.Count == 0 && decided.Count == 0
            ? "Nothing came in and nothing was decided."
            : $"{Count(sent.Count, "message")} came in; {Count(decided.Count, "decision")} made" +
              (byDecision.Count == 0 ? "" : " (" + string.Join(", ", byDecision.Select(c => $"{c.Label} {c.Count}")) + ")") +
              (waiting == 0 ? "." : $"; {waiting} still waiting.");
        var items = sent.Select(m => Message(m, decisionOfSent.TryGetValue(m.Id, out var d) ? d : null,
            m.BundleId is { } b && handedAt.TryGetValue(b, out var at) ? at : null)).ToList();
        return new FactoryInboxSummaryDto(factory, sinceUtc, untilUtc, items.Count, line, byDecision, items);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>One message as every client shows it.</summary>
public sealed record FactoryInboxMessageDto(
    string Id, string Factory, string Text, string? RawText, string Note, string NoteLabel, string Source, string Surface,
    DateTime SentAtUtc, string State, string StateLabel, string? MergedInto, IReadOnlyList<FactoryInboxLinkDto> Links,
    string? BundleId, FactoryInboxDecisionDto? Decision, IReadOnlyList<FactoryInboxTrailStepDto> Trail);

/// <summary>One decision as it is shown.</summary>
public sealed record FactoryInboxDecisionDto(
    string Kind, string Label, string? Why, string? SessionId, string? TodoId, string DecidedBySessionId, DateTime DecidedAtUtc);

/// <summary>One step of a message's trail. <see cref="AtUtc"/> is null when the step has no time of its own.</summary>
public sealed record FactoryInboxTrailStepDto(string Step, string Label, DateTime? AtUtc);

/// <summary>A count of one decision kind.</summary>
public sealed record FactoryInboxCountDto(string Kind, string Label, int Count);

/// <summary>A factory's summary.</summary>
public sealed record FactoryInboxSummaryDto(
    string Factory, DateTime SinceUtc, DateTime UntilUtc, int Count, string Line,
    IReadOnlyList<FactoryInboxCountDto> ByDecision, IReadOnlyList<FactoryInboxMessageDto> Items);

/// <summary>One to-do item.</summary>
public sealed record FactoryTodoDto(string Id, string Text, string? FromMessageId, string AddedBySessionId, DateTime AddedAtUtc);
