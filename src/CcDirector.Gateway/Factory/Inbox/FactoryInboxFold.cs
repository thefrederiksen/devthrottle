using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// WHAT A FACTORY INBOX MESSAGE SAYS ON SCREEN (Factory Inbox mission, phase 1; CLAUDE.md rule 7). Pure: the rows go in,
/// the finished labels and the trail come out, and a client prints them as they are. The trail is each step the message
/// has taken - sent, received, with the Boss, merged, decided - with its own label and time.
/// </summary>
public static class FactoryInboxFold
{
    /// <summary>One message, folded with its decision (null when it has none). <paramref name="waiting"/> is the factory's
    /// waiting sentences (phase 2, the wake): a sent message shows why it waits for a Secretary, and a message with the Boss
    /// why it waits for a Boss, in place of its plain state label.</summary>
    public static FactoryInboxMessageDto Message(FactoryInboxMessageEntity m, FactoryInboxDecisionEntity? decision,
        DateTime? handedAtUtc = null, FactoryInboxWaitingLabels? waiting = null)
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
            StateLabel: WaitingLabelOf(m, waiting) ?? StateLabelOf(m, decision),
            MergedInto: m.MergedIntoId?.ToString("D"),
            Links: FactoryInboxStore.ReadLinks(m.LinksJson),
            BundleId: m.BundleId?.ToString("D"),
            Decision: decision is null ? null : Decision(decision),
            Trail: Trail(m, decision, handedAtUtc),
            Tone: FactoryInboxWords.Tone(m.State, decision?.Kind),
            SourceLabel: FactoryInboxWords.SourceLabel(m.Source));
    }

    /// <summary>
    /// One factory's row on the Factory inbox list (phase 4): the line under its name, the waiting line, and its badge -
    /// the newest message's state in short with its tone, or "No messages" in grey. When the newest message waits for a
    /// seat that cannot be started (<paramref name="waitingLabels"/>, the factory's wake row), the row says why in
    /// <see cref="FactoryInboxRowDto.ReasonLine"/> - the same sentence the thread shows - and its badge says "Waiting"
    /// rather than the plain state (the end-to-end proof, defect 6). Every word is decided here; the screens print them.
    /// </summary>
    public static FactoryInboxRowDto Row(int waiting, FactoryInboxMessageEntity? last, FactoryInboxDecisionEntity? lastDecision,
        FactoryInboxWaitingLabels? waitingLabels = null)
    {
        if (last is null)
            return new FactoryInboxRowDto(FactoryInboxWords.NoMessagesLine, null, FactoryInboxWords.NoMessagesBadge, FactoryInboxWords.Tones.Grey, null);
        var reason = WaitingLabelOf(last, waitingLabels);
        return new FactoryInboxRowDto(
            Line: last.Text,
            WaitingLine: FactoryInboxWords.WaitingLine(waiting),
            Badge: reason is null ? StateLabelOf(last, lastDecision) : FactoryInboxWords.WaitingBadge,
            Tone: FactoryInboxWords.Tone(last.State, lastDecision?.Kind),
            ReasonLine: reason);
    }

    /// <summary>
    /// The spoken messages held for a factory (phase 4 review, finding 2): one entry per recording the Gateway still
    /// holds PENDING for its inbox, oldest first, each with its words, its time and the tone it is coloured by. The
    /// thread prints them, so a held message is never invisible and is never dropped without the thread saying so.
    /// </summary>
    public static IReadOnlyList<FactoryInboxHeldDto> Held(IReadOnlyList<Voice.PendingUpload> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return pending
            .Select(p => new FactoryInboxHeldDto(p.UploadId, FactoryInboxWords.HeldLine, FactoryInboxWords.HeldAtLabel,
                DateTime.SpecifyKind(p.LastActivityUtc, DateTimeKind.Utc), FactoryInboxWords.Tones.Amber))
            .ToList();
    }

    /// <summary>A message's state in words: its decision, or that the owner dealt with it himself (issue 4746).</summary>
    private static string StateLabelOf(FactoryInboxMessageEntity m, FactoryInboxDecisionEntity? decision)
        => m.OwnerDealtWithAtUtc is not null && decision is null
            ? FactoryInboxWords.OwnerDealtWithLabel
            : FactoryInboxWords.StateLabel(m.State, decision?.Kind);

    /// <summary>The waiting sentence a message shows, or null when it does not wait.</summary>
    private static string? WaitingLabelOf(FactoryInboxMessageEntity m, FactoryInboxWaitingLabels? waiting) => m.State switch
    {
        FactoryInboxWords.States.Sent => waiting?.Secretary,
        FactoryInboxWords.States.WithBoss => waiting?.Boss,
        _ => null,
    };

    /// <summary>A factory's waiting sentences, from its wake row (<see cref="FactoryInboxWakeStore"/>): each null when
    /// nothing waits for that seat.</summary>
    public static FactoryInboxWaitingLabels? WaitingOf(Data.Entities.FactoryInboxWakeEntity? row)
    {
        if (row is null) return null;
        var secretary = row.SecretaryWaiting is { } s
            ? FactoryInboxWords.WaitingLabel(FactoryInboxWords.WaitingFor.Secretary, s, row.SecretaryWaitingDetail)
            : null;
        var boss = row.BossWaiting is { } b
            ? FactoryInboxWords.WaitingLabel(FactoryInboxWords.WaitingFor.Boss, b, row.BossWaitingDetail)
            : null;
        return secretary is null && boss is null ? null : new FactoryInboxWaitingLabels(secretary, boss);
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
        else if (m.OwnerDealtWithAtUtc is { } dealt)
            trail.Add(new(FactoryInboxWords.Steps.Decided, FactoryInboxWords.OwnerDealtWithLabel, dealt));
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
        // A message the owner dealt with himself (issue 4746) is not waiting, and it is not a Boss decision either.
        var ownerDealt = sent.Count(m => !decisionOfSent.ContainsKey(m.Id) && m.OwnerDealtWithAtUtc is not null);
        var waiting = sent.Count(m => !decisionOfSent.ContainsKey(m.Id) && m.OwnerDealtWithAtUtc is null);
        var line = sent.Count == 0 && decided.Count == 0
            ? "Nothing came in and nothing was decided."
            : $"{Count(sent.Count, "message")} came in; {Count(decided.Count, "decision")} made" +
              (byDecision.Count == 0 ? "" : " (" + string.Join(", ", byDecision.Select(c => $"{c.Label} {c.Count}")) + ")") +
              (ownerDealt == 0 ? "" : $"; {ownerDealt} dealt with by the owner") +
              (waiting == 0 ? "." : $"; {waiting} still waiting.");
        var items = sent.Select(m => Message(m, decisionOfSent.TryGetValue(m.Id, out var d) ? d : null,
            m.BundleId is { } b && handedAt.TryGetValue(b, out var at) ? at : null)).ToList();
        return new FactoryInboxSummaryDto(factory, sinceUtc, untilUtc, items.Count, line, byDecision, items);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>What a factory's messages wait for, in words: <see cref="Secretary"/> for a sent message, <see cref="Boss"/>
/// for one with the Boss. Null when nothing waits for that seat.</summary>
public sealed record FactoryInboxWaitingLabels(string? Secretary, string? Boss);

/// <summary>One message as every client shows it.</summary>
public sealed record FactoryInboxMessageDto(
    string Id, string Factory, string Text, string? RawText, string Note, string NoteLabel, string Source, string Surface,
    DateTime SentAtUtc, string State, string StateLabel, string? MergedInto, IReadOnlyList<FactoryInboxLinkDto> Links,
    string? BundleId, FactoryInboxDecisionDto? Decision, IReadOnlyList<FactoryInboxTrailStepDto> Trail, string Tone,
    string SourceLabel);

/// <summary>A factory's row on the Factory inbox list: <see cref="Line"/> under its name (its newest message, or that it has
/// none), <see cref="WaitingLine"/> (null with no messages), a short <see cref="Badge"/> with the <see cref="Tone"/> a
/// screen colours it by, and <see cref="ReasonLine"/>: why the newest message waits, or null when it does not.</summary>
public sealed record FactoryInboxRowDto(string Line, string? WaitingLine, string Badge, string Tone, string? ReasonLine);

/// <summary>One held spoken message on a factory's thread: the recording's id, the <see cref="Line"/> to print, the label
/// of its time and the time itself (UTC), and the <see cref="Tone"/> a screen colours it by.</summary>
public sealed record FactoryInboxHeldDto(string UploadId, string Line, string AtLabel, DateTime AtUtc, string Tone);

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
