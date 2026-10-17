using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Receipts;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// THE GATEWAY'S RULING ON WHAT AN API-MODE SESSION SHOWS, EVENT BY EVENT AND RECEIPT BY RECEIPT (mission #4070,
/// phase 2; the event model is <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>). The project rule 7: the
/// client is dumb. Every word a client shows for a live event or a receipt is decided here, once, and travels as an
/// <see cref="ApiModeLiveItem"/> or as <see cref="ApiModeReceiptDto.Words"/>; a client never composes one.
///
/// <para><b>One ruling, two readers.</b> The live stream (<c>GET /sessions/{sid}/live</c>) keeps one instance per
/// subscriber and hands it every event in sequence order (<see cref="Rule"/>). The receipts read
/// (<c>GET /sessions/{sid}/receipts</c>) calls the static <see cref="ReceiptWords"/>. The live receipt items are worded
/// by that same static function over the same receipt shape, so a receipt reads the same on both.</para>
///
/// <para><b>What it remembers</b>, and why it is an instance: a tool card's title (the finish event carries no tool
/// name), which prompts each turn carried (so a turn that failed rewrites those prompts' receipts, event model rule 5),
/// each prompt's receipt so far, the sequence of the last receipt event ruled for it (so an older event handed over
/// after a newer one changes nothing), and, for a receipt it took from the store, the position of the last event the
/// store applied to it. Nothing else. It does no input or output and reads no clock: the same events give the same
/// items.</para>
///
/// <para><b>One ruling rules one generation.</b> The sequence guard compares sequences only, and a sequence restarts
/// with each Director generation, so it relies on its callers handing it the events of one generation, in order: the
/// stream builds a new ruling from the store the moment the held generation changes, and a poll builds one per request.
/// A ruling fed across a Director restart would refuse every receipt event of the new generation as "not after" the old
/// one.</para>
///
/// <para><b>After an incomplete replay</b> (<see cref="ApiModeEvents.ApiModeEventReplay.Complete"/> false: a Gateway
/// restart, an eviction, a new Director generation) the reader has missed events. Build a NEW ruling from the stored
/// receipts and their positions (<see cref="ApiModeLiveRuling(IEnumerable{ApiModeStoredReceipt})"/>), render those
/// receipts with <see cref="ReceiptItem"/>, and continue from there, handing each event with its position
/// (<see cref="Rule(ApiModeEvents.ApiModeHeldEvent)"/>). A tool that finishes after the reader missed its start is shown
/// with a title that says so, never a guessed one.</para>
///
/// <para><b>Thought text is hidden.</b> The event model leaves showing the agent's thinking (<c>channel: thought</c>)
/// to the Gateway; this ruling shows none of it (<see cref="ShowsThoughts"/>). The reply is what the user asked for.</para>
///
/// <para>Not thread-safe: one instance per reader, called from one thread at a time.</para>
/// </summary>
public sealed class ApiModeLiveRuling
{
    /// <summary>Whether thought text (<see cref="MessageTextChannel.Thought"/>) becomes an item. It does not: thoughts
    /// are hidden by the Gateway's ruling.</summary>
    public const bool ShowsThoughts = false;

    private readonly Dictionary<string, ApiModeReceiptDto> _receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shownReceiptWords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _turnPrompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallStarted> _tools = new(StringComparer.Ordinal);
    // For each receipt this ruling took from the store, the position of the last event the store applied to it. A reader
    // started from the stored receipts is then handed, again, events the store has already ruled on; an event at or
    // before this position is such a replay and changes nothing, and only an event after it is ruled.
    private readonly Dictionary<string, ApiModeEventPosition> _storedPositions = new(StringComparer.Ordinal);
    // The sequence of the last receipt event ruled for each prompt, so an older event handed over after a newer one
    // changes nothing.
    private readonly Dictionary<string, long> _receiptSequences = new(StringComparer.Ordinal);

    /// <summary>A ruling for a reader that starts at the first event of a complete replay.</summary>
    public ApiModeLiveRuling()
        : this(Array.Empty<ApiModeStoredReceipt>())
    {
    }

    /// <summary>A ruling that starts from the stored receipts (after an incomplete replay), each with the position of
    /// the last event the store applied to it. Each receipt counts as already shown with its own words; render them with
    /// <see cref="ReceiptItem"/>. A receipt event for one of these prompts must then be handed with its position
    /// (<see cref="Rule(ApiModeEvents.ApiModeHeldEvent)"/>).</summary>
    public ApiModeLiveRuling(IEnumerable<ApiModeStoredReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        foreach (var stored in receipts)
        {
            var copy = Copy(stored.Receipt);
            _receipts[copy.PromptId] = copy;
            _shownReceiptWords[copy.PromptId] = ReceiptWords(copy);
            _storedPositions[copy.PromptId] = stored.LastPosition;
            if (copy.TurnId is { } turn)
                PromptsOf(turn).Add(copy.PromptId);
        }
    }

    /// <summary>
    /// The items one held event changes, in the order a client applies them: <see cref="Rule(SessionEvent)"/> with the
    /// event's position, which a ruling started from the stored receipts needs to tell an event the store has already
    /// ruled on from a newer one.
    /// </summary>
    public IReadOnlyList<ApiModeLiveItem> Rule(ApiModeEvents.ApiModeHeldEvent held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return Rule(held.Event, held.Position);
    }

    /// <summary>
    /// The items one event changes, in the order a client applies them. Empty when the event shows nothing (thought
    /// text; a turn starting, which changes no receipt's words; a receipt event that leaves a receipt's words as they
    /// were). Without a position: a ruling that took a prompt's receipt from the store refuses a receipt event for that
    /// prompt, since it cannot tell whether the store has already ruled on it.
    /// </summary>
    public IReadOnlyList<ApiModeLiveItem> Rule(SessionEvent e) => Rule(e, null);

    private IReadOnlyList<ApiModeLiveItem> Rule(SessionEvent e, ApiModeEventPosition? position)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            PromptQueued q => ReceiptChange(q, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, q)) return;
                r.State = ApiModeReceiptState.Queued;
                r.QueueReason = Word(q.Reason);
            }),
            PromptDelivered d => ReceiptChange(d, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, d)) return;
                r.State = ApiModeReceiptState.Delivered;
                r.Disposition = Word(d.Disposition);
                r.Acknowledgement = d.Acknowledgement;
            }),
            PromptRefused f => ReceiptChange(f, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, f)) return;
                r.State = ApiModeReceiptState.Refused;
                r.RefusalReason = f.Reason;
                r.RefusedBy = Word(f.RefusedBy);
            }),
            TurnStarted s => TurnStart(s),
            MessageText m => Text(m),
            ToolCallStarted t => ToolStart(t),
            ToolCallFinished t => ToolFinish(t),
            TurnEnded t => TurnEnd(t),
            PermissionRequested p => One(new ApiModeLiveItem
            {
                ItemId = "permission:" + p.RequestId,
                Kind = ApiModeLiveItemKinds.Permission,
                Mode = ApiModeLiveItemModes.Replace,
                Text = $"The agent asks: {p.Title}. Answering it from here is not built yet.",
                Tone = "yellow",
                TurnId = p.TurnId,
                Sequence = p.Sequence,
            }),
            ConnectionReady c => One(new ApiModeLiveItem
            {
                ItemId = "connection:" + c.Sequence,
                Kind = ApiModeLiveItemKinds.Connection,
                Mode = ApiModeLiveItemModes.Replace,
                Text = $"Agent connected ({c.Model})",
                Tone = "green",
                Sequence = c.Sequence,
            }),
            ConnectionEnded c => One(new ApiModeLiveItem
            {
                ItemId = "connection:" + c.Sequence,
                Kind = ApiModeLiveItemKinds.Connection,
                Mode = ApiModeLiveItemModes.Replace,
                Text = DisconnectedWords(c.Reason),
                // The exit code and the standard error lines, as the Director gave them: the reason itself is verbatim.
                Detail = string.Join("\n", c.StderrTail.Prepend($"Exit code {c.ExitCode}")),
                Tone = c.ExitCode == 0 ? "neutral" : "red",
                Sequence = c.Sequence,
            }),
            _ => throw new InvalidOperationException($"ApiModeLiveRuling has no ruling for the event type {e.GetType().Name}"),
        };
    }

    /// <summary>
    /// Whether this ruling remembers what <paramref name="e"/> builds on: the start of the tool it finishes, or the start
    /// of the turn it ends and the receipt of every prompt that turn carried. Every other event builds on nothing
    /// earlier. A reader whose ruling was brought to its cursor from held events that do not reach back to the start of
    /// the generation asks this before each event: when the answer is no, ruling the event would show a wrong picture (a
    /// tool card losing the title the reader already shows, a receipt left plain beside a failed turn), so that reader is
    /// reset instead.
    /// </summary>
    public bool Remembers(SessionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            ToolCallFinished t => _tools.ContainsKey(t.ToolCallId),
            TurnEnded t => _turnPrompts.TryGetValue(t.TurnId, out var prompts) && prompts.All(_receipts.ContainsKey),
            _ => true,
        };
    }

    /// <summary>How many second endings this ruling has logged (a Director defect each), for the tests.</summary>
    internal int SecondEndingsLogged { get; private set; }

    /// <summary>How many refused receipts a retry's later event replaced (each logged), for the tests.</summary>
    internal int SupersedesLogged { get; private set; }

    // ----- the receipt's words: ONE function for the stored read and the live stream -----

    /// <summary>
    /// A receipt's finished words (event model rule 5): <c>Queued: &lt;why&gt;</c>, <c>Delivered</c>,
    /// <c>Delivered - the turn failed: &lt;reason&gt;</c>, <c>Delivered - stopped</c>, or <c>Refused: &lt;reason&gt;</c>.
    /// The reason is the agent's (or the Director's) own words, verbatim.
    /// </summary>
    public static string ReceiptWords(ApiModeReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.State switch
        {
            ApiModeReceiptState.Queued => "Queued: " + QueueReasonWords(receipt.QueueReason),
            ApiModeReceiptState.Refused => "Refused: " + (receipt.RefusalReason ?? "no reason was given"),
            ApiModeReceiptState.Delivered => receipt.TurnOutcome switch
            {
                "failed" => "Delivered - the turn failed: " + (receipt.TurnOutcomeReason ?? "no reason was given"),
                "cancelled" => "Delivered - stopped",
                null or "done" => "Delivered",
                var unknown => Unknown("Delivered - the turn ended in a way this Gateway does not know", "turn outcome", unknown),
            },
            _ => throw new InvalidOperationException($"ApiModeLiveRuling.ReceiptWords: no words for receipt state {receipt.State}"),
        };
    }

    /// <summary>A receipt's colour hint: <c>green</c> delivered, <c>red</c> refused or its turn failed,
    /// <c>yellow</c> its turn was stopped, <c>neutral</c> still queued.</summary>
    public static string ReceiptTone(ApiModeReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.State switch
        {
            ApiModeReceiptState.Queued => "neutral",
            ApiModeReceiptState.Refused => "red",
            _ => receipt.TurnOutcome switch
            {
                "failed" => "red",
                "cancelled" => "yellow",
                null or "done" => "green",
                _ => "neutral",
            },
        };
    }

    /// <summary>A stored receipt as a live item, for a reader that starts from the receipts.</summary>
    public static ApiModeLiveItem ReceiptItem(ApiModeReceiptDto receipt, long sequence)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new ApiModeLiveItem
        {
            ItemId = "receipt:" + receipt.PromptId,
            Kind = ApiModeLiveItemKinds.Receipt,
            Mode = ApiModeLiveItemModes.Replace,
            Text = ReceiptWords(receipt),
            Tone = ReceiptTone(receipt),
            TurnId = receipt.TurnId,
            PromptId = receipt.PromptId,
            Sequence = sequence,
        };
    }

    /// <summary>Why the Director holds a prompt, in words, from the event model's word for it.</summary>
    public static string QueueReasonWords(string? reason) => reason switch
    {
        "userHasTheWheel" => "you have the wheel",
        "turnRunning" => "a turn is running",
        "agentStarting" => "the agent is starting",
        null => Unknown("the Director gave no reason", "queue reason", "(none)"),
        var unknown => Unknown($"the Director gave a reason this Gateway does not know ({unknown})", "queue reason", unknown),
    };

    /// <summary>The agent process is gone, in words; the reason is the Director's, verbatim.</summary>
    public static string DisconnectedWords(string reason) => "Agent disconnected: " + reason;

    // ----- the rest of the ruling -----

    private IReadOnlyList<ApiModeLiveItem> ReceiptChange(SessionEvent e, ApiModeEventPosition? position, Action<ApiModeReceiptDto> change)
    {
        var promptId = PromptIdOf(e);
        if (_storedPositions.TryGetValue(promptId, out var stored))
        {
            if (position is not { } at)
                throw new InvalidOperationException($"ApiModeLiveRuling: {e.GetType().Name} for prompt={promptId} was handed without its position, and this ruling took that prompt's receipt from the store; hand it as a held event");
            // At or before the last event the store applied: the store has already ruled on it, and the receipt this
            // ruling took from the store is what it decided. A routine replay after every reset, so it is not logged.
            if (!at.IsAfter(stored.Generation, stored.GenerationStartedUtc, stored.Sequence))
                return Array.Empty<ApiModeLiveItem>();
        }
        if (_receiptSequences.TryGetValue(promptId, out var last) && e.Sequence <= last)
        {
            // An older event handed over after a newer one: it never overwrites what the newer one decided.
            FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={promptId} sequence={e.Sequence}: {e.GetType().Name} is not after sequence {last}, already ruled for this prompt; it changes nothing");
            return Array.Empty<ApiModeLiveItem>();
        }
        _receiptSequences[promptId] = e.Sequence;
        if (!_receipts.TryGetValue(promptId, out var receipt))
        {
            receipt = new ApiModeReceiptDto { PromptId = promptId, State = ApiModeReceiptState.Queued };
            _receipts[promptId] = receipt;
        }
        change(receipt);
        return ReceiptIfChanged(receipt, e.Sequence);
    }

    private static string PromptIdOf(SessionEvent e) => e switch
    {
        PromptQueued q => q.PromptId,
        PromptDelivered d => d.PromptId,
        PromptRefused f => f.PromptId,
        _ => throw new InvalidOperationException($"ApiModeLiveRuling: {e.GetType().Name} is not a receipt event"),
    };

    private IReadOnlyList<ApiModeLiveItem> ReceiptIfChanged(ApiModeReceiptDto receipt, long sequence)
    {
        var words = ReceiptWords(receipt);
        if (_shownReceiptWords.TryGetValue(receipt.PromptId, out var shown) && shown == words)
            return Array.Empty<ApiModeLiveItem>();
        _shownReceiptWords[receipt.PromptId] = words;
        return new[] { ReceiptItem(receipt, sequence) };
    }

    /// <summary>
    /// Whether a receipt event replaces a receipt that already ended - as the receipt store decides it (event model
    /// section 2: a retry of a delivery that was not delivered reuses its Gateway id, and the later receipt stands).
    /// A Refused receipt is replaced by the retry's Queued, Delivered or Refused, logged. Delivered is final (rule 1): a
    /// second ending after it is a Director defect, logged, and the delivery stands; a Queued after it changes nothing.
    /// Only an event newer than what the receipt was built from reaches here: a replay of an event the store had already
    /// applied was set aside by its position first.
    /// </summary>
    private bool Supersedes(ApiModeReceiptDto receipt, SessionEvent e)
    {
        var promptId = receipt.PromptId;
        if (receipt.State == ApiModeReceiptState.Refused)
        {
            SupersedesLogged++;
            FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={promptId} sequence={e.Sequence}: SUPERSEDE, the prompt was refused (\"{receipt.RefusalReason}\") and {e.GetType().Name} is its retry under the same id; the later receipt stands");
            receipt.QueueReason = null;
            receipt.Disposition = null;
            receipt.Acknowledgement = null;
            receipt.RefusalReason = null;
            receipt.RefusedBy = null;
            receipt.TurnId = null;
            receipt.TurnOutcome = null;
            receipt.TurnOutcomeReason = null;
            return true;
        }

        if (e is PromptQueued) return false; // a delivered prompt is never queued again; the store logs it
        SecondEndingsLogged++;
        FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={promptId} sequence={e.Sequence}: a second ending ({e.GetType().Name}) after {receipt.State}; a delivered prompt is never sent again, so the delivery stands");
        return false;
    }

    private IReadOnlyList<ApiModeLiveItem> TurnStart(TurnStarted s)
    {
        var prompts = PromptsOf(s.TurnId);
        foreach (var promptId in s.PromptIds)
        {
            if (!prompts.Contains(promptId, StringComparer.Ordinal)) prompts.Add(promptId);
            if (_receipts.TryGetValue(promptId, out var receipt) && receipt.State == ApiModeReceiptState.Delivered)
                receipt.TurnId = s.TurnId;
        }
        return Array.Empty<ApiModeLiveItem>();
    }

    private static IReadOnlyList<ApiModeLiveItem> Text(MessageText m)
    {
        if (m.Channel == MessageTextChannel.Thought && !ShowsThoughts)
            return Array.Empty<ApiModeLiveItem>();
        return One(new ApiModeLiveItem
        {
            ItemId = "reply:" + m.MessageId,
            Kind = ApiModeLiveItemKinds.ReplyText,
            // The event model: a delta is appended for a live view; a final replaces everything streamed for that message.
            Mode = m.Kind == MessageTextKind.Delta ? ApiModeLiveItemModes.Append : ApiModeLiveItemModes.Replace,
            Text = m.Text,
            Tone = "neutral",
            TurnId = m.TurnId,
            Sequence = m.Sequence,
        });
    }

    private IReadOnlyList<ApiModeLiveItem> ToolStart(ToolCallStarted t)
    {
        _tools[t.ToolCallId] = t;
        return One(ToolCard(t.ToolCallId, t.ToolName, t.Input, "running", "neutral", t.TurnId, t.Sequence));
    }

    private IReadOnlyList<ApiModeLiveItem> ToolFinish(ToolCallFinished t)
    {
        var (status, tone) = t.Outcome switch
        {
            ToolCallOutcome.Succeeded => ("done", "green"),
            ToolCallOutcome.Failed => ("failed", "red"),
            ToolCallOutcome.Cancelled => ("stopped", "yellow"),
            _ => throw new InvalidOperationException($"ApiModeLiveRuling: no status words for tool outcome {t.Outcome}"),
        };
        if (_tools.Remove(t.ToolCallId, out var started))
            return One(ToolCard(t.ToolCallId, started.ToolName, started.Input, status, tone, t.TurnId, t.Sequence));
        // The reader joined after the tool started: its name and input are not here, and they are not guessed.
        return One(ToolCard(t.ToolCallId, "A tool that started before this view began", null, status, tone, t.TurnId, t.Sequence));
    }

    private static ApiModeLiveItem ToolCard(string toolCallId, string title, string? input, string status, string tone, string turnId, long sequence) => new()
    {
        ItemId = "tool:" + toolCallId,
        Kind = ApiModeLiveItemKinds.ToolCard,
        Mode = ApiModeLiveItemModes.Replace,
        Text = title,
        Status = status,
        Detail = input,
        Tone = tone,
        TurnId = turnId,
        Sequence = sequence,
    };

    private IReadOnlyList<ApiModeLiveItem> TurnEnd(TurnEnded t)
    {
        var (line, tone) = TurnEndedWords(t.Outcome, t.Reason);
        var items = new List<ApiModeLiveItem>
        {
            new()
            {
                ItemId = "turn:" + t.TurnId,
                Kind = ApiModeLiveItemKinds.TurnEnded,
                Mode = ApiModeLiveItemModes.Replace,
                Text = line,
                Tone = tone,
                TurnId = t.TurnId,
                Sequence = t.Sequence,
            },
        };
        // Rule 5: the prompts this turn carried now show its outcome beside Delivered.
        if (_turnPrompts.Remove(t.TurnId, out var prompts))
            foreach (var promptId in prompts)
            {
                if (!_receipts.TryGetValue(promptId, out var receipt) || receipt.State != ApiModeReceiptState.Delivered) continue;
                if (receipt.TurnOutcome is not null) continue; // one turn ending per receipt, as the store keeps it
                receipt.TurnId = t.TurnId;
                receipt.TurnOutcome = Word(t.Outcome);
                receipt.TurnOutcomeReason = t.Reason;
                items.AddRange(ReceiptIfChanged(receipt, t.Sequence));
            }
        // A tool card the dialect did not close would spin for ever; rule 3 says it always closes one, so any left is
        // forgotten with its turn rather than kept.
        foreach (var open in _tools.Where(kv => kv.Value.TurnId == t.TurnId).Select(kv => kv.Key).ToList())
            _tools.Remove(open);
        return items;
    }

    /// <summary>The line that ends a turn, and its colour hint.</summary>
    public static (string Line, string Tone) TurnEndedWords(TurnOutcome outcome, string? reason) => outcome switch
    {
        TurnOutcome.Done => (reason is null ? "Turn finished" : "Turn finished: " + reason, "neutral"),
        TurnOutcome.Cancelled => ("Turn stopped: " + (reason ?? "no reason was given"), "yellow"),
        TurnOutcome.Failed => ("Turn failed: " + (reason ?? "no reason was given"), "red"),
        _ => throw new InvalidOperationException($"ApiModeLiveRuling: no words for turn outcome {outcome}"),
    };

    private List<string> PromptsOf(string turnId)
    {
        if (!_turnPrompts.TryGetValue(turnId, out var prompts))
        {
            prompts = new List<string>();
            _turnPrompts[turnId] = prompts;
        }
        return prompts;
    }

    private static IReadOnlyList<ApiModeLiveItem> One(ApiModeLiveItem item) => new[] { item };

    private static string Unknown(string words, string what, string value)
    {
        FileLog.Write($"[ApiModeLiveRuling] a {what} this Gateway has no words for: '{value}'");
        return words;
    }

    /// <summary>An event-model enumeration as its word: the ingest's own, so the word is the one the receipt store keeps.</summary>
    private static string Word<TEnum>(TEnum value) where TEnum : struct, Enum => ApiModeEvents.ApiModeEventIngest.Word(value);

    private static ApiModeReceiptDto Copy(ApiModeReceiptDto r) => new()
    {
        PromptId = r.PromptId,
        State = r.State,
        QueueReason = r.QueueReason,
        Disposition = r.Disposition,
        Acknowledgement = r.Acknowledgement,
        RefusalReason = r.RefusalReason,
        RefusedBy = r.RefusedBy,
        TurnId = r.TurnId,
        TurnOutcome = r.TurnOutcome,
        TurnOutcomeReason = r.TurnOutcomeReason,
        QueuedAtUtc = r.QueuedAtUtc,
        DeliveredAtUtc = r.DeliveredAtUtc,
        RefusedAtUtc = r.RefusedAtUtc,
        TurnEndedAtUtc = r.TurnEndedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
        Words = r.Words,
        Tone = r.Tone,
    };
}
