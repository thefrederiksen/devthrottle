using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// THE GATEWAY'S RULING ON WHAT AN API-MODE SESSION SHOWS, EVENT BY EVENT AND RECEIPT BY RECEIPT (mission #4070,
/// phase 2; the event model is <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>). CLAUDE.md rule 7: the
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
/// and each prompt's receipt so far. Nothing else. It does no input or output and reads no clock: the same events give
/// the same items.</para>
///
/// <para><b>After an incomplete replay</b> (<see cref="ApiModeEvents.ApiModeEventReplay.Complete"/> false: a Gateway
/// restart, an eviction, a new Director generation) the reader has missed events. Build a NEW ruling from the stored
/// receipts (<see cref="ApiModeLiveRuling(IEnumerable{ApiModeReceiptDto})"/>), render those receipts with
/// <see cref="ReceiptItem"/>, and continue from there. A tool that finishes after the reader missed its start is shown
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

    /// <summary>A ruling for a reader that starts at the first event of a complete replay.</summary>
    public ApiModeLiveRuling()
        : this(Array.Empty<ApiModeReceiptDto>())
    {
    }

    /// <summary>A ruling that starts from the stored receipts (after an incomplete replay). Each receipt counts as
    /// already shown with its own words; render them with <see cref="ReceiptItem"/>.</summary>
    public ApiModeLiveRuling(IEnumerable<ApiModeReceiptDto> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        foreach (var stored in receipts)
        {
            var copy = Copy(stored);
            _receipts[copy.PromptId] = copy;
            _shownReceiptWords[copy.PromptId] = ReceiptWords(copy);
            if (copy.TurnId is { } turn)
                PromptsOf(turn).Add(copy.PromptId);
        }
    }

    /// <summary>
    /// The items one event changes, in the order a client applies them. Empty when the event shows nothing (thought
    /// text; a turn starting, which changes no receipt's words; a receipt event that leaves a receipt's words as they
    /// were).
    /// </summary>
    public IReadOnlyList<ApiModeLiveItem> Rule(SessionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            PromptQueued q => ReceiptChange(q.Sequence, q.PromptId, r =>
            {
                if (r.State != ApiModeReceiptState.Queued) return; // a late Queued after an ending changes nothing
                r.QueueReason = Word(q.Reason);
            }),
            PromptDelivered d => ReceiptChange(d.Sequence, d.PromptId, r =>
            {
                if (IsFinal(r, d)) return;
                r.State = ApiModeReceiptState.Delivered;
                r.Disposition = Word(d.Disposition);
                r.Acknowledgement = d.Acknowledgement;
            }),
            PromptRefused f => ReceiptChange(f.Sequence, f.PromptId, r =>
            {
                if (IsFinal(r, f)) return;
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
                Text = DisconnectedWords(c.Reason, c.ExitCode),
                Detail = c.StderrTail.Count > 0 ? string.Join("\n", c.StderrTail) : null,
                Tone = c.ExitCode == 0 ? "neutral" : "red",
                Sequence = c.Sequence,
            }),
            _ => throw new InvalidOperationException($"ApiModeLiveRuling has no ruling for the event type {e.GetType().Name}"),
        };
    }

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

    /// <summary>The agent process is gone, in words.</summary>
    public static string DisconnectedWords(string reason, int exitCode)
        => $"Agent disconnected: {reason} (exit code {exitCode})";

    // ----- the rest of the ruling -----

    private IReadOnlyList<ApiModeLiveItem> ReceiptChange(long sequence, string promptId, Action<ApiModeReceiptDto> change)
    {
        if (!_receipts.TryGetValue(promptId, out var receipt))
        {
            receipt = new ApiModeReceiptDto { PromptId = promptId, State = ApiModeReceiptState.Queued };
            _receipts[promptId] = receipt;
        }
        change(receipt);
        return ReceiptIfChanged(receipt, sequence);
    }

    private IReadOnlyList<ApiModeLiveItem> ReceiptIfChanged(ApiModeReceiptDto receipt, long sequence)
    {
        var words = ReceiptWords(receipt);
        if (_shownReceiptWords.TryGetValue(receipt.PromptId, out var shown) && shown == words)
            return Array.Empty<ApiModeLiveItem>();
        _shownReceiptWords[receipt.PromptId] = words;
        return new[] { ReceiptItem(receipt, sequence) };
    }

    /// <summary>Delivered and Refused are final (event model rule 1): a second ending is a Director defect, logged, and
    /// the first one stands - as the receipt store keeps it.</summary>
    private static bool IsFinal(ApiModeReceiptDto receipt, SessionEvent e)
    {
        if (receipt.State == ApiModeReceiptState.Queued) return false;
        FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={receipt.PromptId} sequence={e.Sequence}: a second ending ({e.GetType().Name}) after {receipt.State}; the first stands");
        return true;
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
