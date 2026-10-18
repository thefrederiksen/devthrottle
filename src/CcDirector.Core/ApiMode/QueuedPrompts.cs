using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// The prompts of one session still waiting for their receipt, in the order they were queued, as the session's own
/// events say (API mode mission, issue #4070, phase 4): a <see cref="PromptQueued"/> puts its prompt at the end of the
/// line, and its <see cref="PromptDelivered"/> or <see cref="PromptRefused"/> takes it out. That is exactly the line the
/// Gateway keeps from the same events, so it is what the Director re-announces at the start of every new generation it
/// sends (<see cref="Reannounce"/>): the Gateway treats a generation as one Director queue, and a hole in the push starts
/// a new generation inside one Director process whose queue survives (the Delivery Lead's ruling on the review of pull
/// request #4178, finding 1).
///
/// Immutable: <see cref="After"/> answers a new value and leaves this one as it was, so a value can be kept beside the
/// event it stood before. An event that is not a prompt's answers this same instance.
/// </summary>
public sealed class QueuedPrompts
{
    /// <summary>Nothing queued.</summary>
    public static readonly QueuedPrompts Empty = new([]);

    private readonly IReadOnlyList<PromptQueued> _prompts;

    private QueuedPrompts(IReadOnlyList<PromptQueued> prompts) => _prompts = prompts;

    /// <summary>Each prompt still queued, oldest first, as its last <see cref="PromptQueued"/> said it.</summary>
    public IReadOnlyList<PromptQueued> Prompts => _prompts;

    public int Count => _prompts.Count;

    /// <summary>The line after <paramref name="e"/>.</summary>
    public QueuedPrompts After(SessionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            // A prompt queued again (a retry under the same id, after its refusal) joins the end of the line.
            PromptQueued q => new QueuedPrompts([.. Without(q.PromptId), q]),
            PromptDelivered d => Leaves(d.PromptId),
            PromptRefused f => Leaves(f.PromptId),
            _ => this,
        };
    }

    /// <summary>The line after every event of <paramref name="events"/>, in order.</summary>
    public QueuedPrompts After(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var line = this;
        foreach (var e in events)
            line = line.After(e);
        return line;
    }

    /// <summary>
    /// One <see cref="PromptQueued"/> for each prompt still queued, oldest first: its id, its text, its reason and the time
    /// it was queued as its own event said them, its place in this line as <see cref="PromptQueued.Position"/> (1 is the
    /// next prompt to be written), and the sequence <paramref name="firstSequence"/> counted up by one.
    /// </summary>
    public IReadOnlyList<PromptQueued> Reannounce(long firstSequence)
    {
        var events = new List<PromptQueued>(_prompts.Count);
        for (var i = 0; i < _prompts.Count; i++)
            events.Add(_prompts[i] with { Sequence = firstSequence + i, Position = i + 1 });
        return events;
    }

    private QueuedPrompts Leaves(string promptId)
    {
        if (!_prompts.Any(p => p.PromptId == promptId))
            return this;
        return new QueuedPrompts(Without(promptId));
    }

    private List<PromptQueued> Without(string promptId) =>
        _prompts.Where(p => !string.Equals(p.PromptId, promptId, StringComparison.Ordinal)).ToList();
}
