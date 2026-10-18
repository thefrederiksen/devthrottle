using System.Globalization;
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
/// after a newer one changes nothing), for a receipt it took from the store, the position of the last event the store
/// applied to it, and the order the prompts arrived in with the position each was last changed at (so a queued
/// receipt's place in line is counted as the receipts read counts it, <see cref="PlaceInLine"/>). Nothing else. It does
/// no input or output and reads no clock: the same events give the same items.</para>
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
    // Every receipt's prompt in the order the receipts read lists them (the order the prompts arrived; a retry of a refused
    // prompt arrives again, at the end), and the position of the last event that changed each one, where it was handed
    // with one: what a queued receipt's place in line is counted from, and what a lost ending is ruled against, as the
    // store's row keeps it (ApiModeLostTurnEnd.Applies).
    private readonly List<string> _order = new();
    private readonly Dictionary<string, ApiModeEventPosition> _lastPositions = new(StringComparer.Ordinal);

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
            _order.Add(copy.PromptId);
            _storedPositions[copy.PromptId] = stored.LastPosition;
            _lastPositions[copy.PromptId] = stored.LastPosition;
            if (copy.TurnId is { } turn)
                PromptsOf(turn).Add(copy.PromptId);
        }
        PlaceInLine(InQueueOrder());
        foreach (var receipt in _receipts.Values)
            _shownReceiptWords[receipt.PromptId] = ReceiptWords(receipt);
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
        var lost = ApiModeLostTurnEnd.Proves(e, out var continuing)
            ? LostTurnEnds(e, continuing, position)
            : Array.Empty<ApiModeLiveItem>();
        var items = RuleEvent(e, position);
        return lost.Count == 0 ? items : lost.Concat(items).ToList();
    }

    private IReadOnlyList<ApiModeLiveItem> RuleEvent(SessionEvent e, ApiModeEventPosition? position)
    {
        return e switch
        {
            PromptQueued q => ReceiptChange(q, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, q)) return;
                r.State = ApiModeReceiptState.Queued;
                KeepPromptText(r, q.PromptText);
                r.QueueReason = Word(q.Reason);
            }),
            PromptDelivered d => ReceiptChange(d, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, d)) return;
                KeepPromptText(r, d.PromptText);
                r.State = ApiModeReceiptState.Delivered;
                r.Disposition = Word(d.Disposition);
                r.Acknowledgement = d.Acknowledgement;
            }),
            PromptRefused f => ReceiptChange(f, position, r =>
            {
                if (r.State != ApiModeReceiptState.Queued && !Supersedes(r, f)) return;
                KeepPromptText(r, f.PromptText);
                r.State = ApiModeReceiptState.Refused;
                r.RefusalReason = f.Reason;
                r.RefusedBy = Word(f.RefusedBy);
            }),
            TurnStarted s => TurnStart(s, position),
            MessageText m => Text(m),
            ToolCallStarted t => ToolStart(t),
            ToolCallFinished t => ToolFinish(t),
            TurnEnded t => TurnEnd(t, position),
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
    /// A receipt's finished words (event model rule 5): <c>Queued, &lt;place&gt; in line: &lt;why&gt;</c> (the place from
    /// <see cref="ApiModeReceiptDto.QueuePosition"/>; <c>Queued: &lt;why&gt;</c> when no place is known, never a guessed
    /// one), <c>Delivered</c>, <c>Delivered - the turn failed: &lt;reason&gt;</c>, <c>Delivered - stopped</c>,
    /// <c>Delivered - the turn's end was lost when the Director reconnected</c> (the Gateway's ruling for a turn whose
    /// end fell in a hole, <see cref="ApiModeLostTurnEnd"/>), or <c>Refused: &lt;reason&gt;</c>. The reason is the
    /// agent's (or the Director's) own words, verbatim.
    /// </summary>
    public static string ReceiptWords(ApiModeReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.State switch
        {
            ApiModeReceiptState.Queued => receipt.QueuePosition is { } place
                ? $"Queued, {Ordinal(place)} in line: {QueueReasonWords(receipt.QueueReason)}"
                : "Queued: " + QueueReasonWords(receipt.QueueReason),
            ApiModeReceiptState.Refused => "Refused: " + (receipt.RefusalReason ?? "no reason was given"),
            ApiModeReceiptState.Delivered => receipt.TurnOutcome switch
            {
                "failed" => "Delivered - the turn failed: " + (receipt.TurnOutcomeReason ?? "no reason was given"),
                "cancelled" => "Delivered - stopped",
                ApiModeLostTurnEnd.Outcome => LostWords,
                null or "done" => "Delivered",
                var unknown => Unknown("Delivered - the turn ended in a way this Gateway does not know", "turn outcome", unknown),
            },
            _ => throw new InvalidOperationException($"ApiModeLiveRuling.ReceiptWords: no words for receipt state {receipt.State}"),
        };
    }

    /// <summary>The words of a receipt whose turn's end was lost (<see cref="ApiModeLostTurnEnd"/>).</summary>
    public const string LostWords = "Delivered - the turn's end was lost when the Director reconnected";

    /// <summary>A receipt's colour hint: <c>green</c> delivered, <c>red</c> refused or its turn failed,
    /// <c>yellow</c> its turn was stopped, <c>neutral</c> still queued or its turn's end was lost (the delivery is
    /// certain; how the turn ended is not known).</summary>
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
                ApiModeLostTurnEnd.Outcome => "neutral",
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
            PromptText = receipt.PromptText,
            Sequence = sequence,
        };
    }

    /// <summary>A place in line as English words it: 1st, 2nd, 3rd, 4th ... 11th, 12th, 13th ... 21st, 22nd ... 101st, 111th.</summary>
    public static string Ordinal(int place)
    {
        if (place < 1)
            throw new ArgumentOutOfRangeException(nameof(place), place, "a place in line counts from 1");
        var suffix = (place % 100) is 11 or 12 or 13
            ? "th"
            : (place % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return place.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>
    /// Sets each receipt's <see cref="ApiModeReceiptDto.QueuePosition"/>: where a still-queued prompt stands NOW, one plus
    /// the still-queued receipts ahead of it, so a waiting prompt moves up as the ones ahead are delivered, refused or
    /// stopped. The receipts must be in the order the store reads them (the order their prompts arrived, which is the
    /// Director's queue order: it writes the oldest waiting prompt first). Null for every receipt not queued.
    ///
    /// <para>Only the newest Director generation among the receipts holds a queue: a Director restart starts a new one,
    /// and the queue of the one before is gone with it. A receipt still queued from an older generation has no place
    /// that can be known, so it gets none (its words stay <c>Queued: &lt;why&gt;</c>) and is not counted ahead of
    /// anyone. On a fresh queue the place equals the Director's own <see cref="PromptQueued.Position"/>, which counts from
    /// 1 the same way.</para>
    ///
    /// <para>The receipts read (<c>GET /sessions/{sid}/receipts</c>) and the live stream both call this over the stored
    /// receipts, and a live ruling calls it over its own after every receipt event, so the two agree.</para>
    /// </summary>
    public static void PlaceInLine(IReadOnlyList<ApiModeStoredReceipt> inStoreOrder)
    {
        ArgumentNullException.ThrowIfNull(inStoreOrder);
        PlaceInLine(inStoreOrder.Select(s => (s.Receipt, (ApiModeEventPosition?)s.LastPosition)).ToList());
    }

    /// <summary><see cref="PlaceInLine(IReadOnlyList{ApiModeStoredReceipt})"/> over receipts whose last position may be
    /// unknown (an event handed without one): such a receipt counts as of the newest generation, the one this ruling
    /// rules.</summary>
    private static void PlaceInLine(IReadOnlyList<(ApiModeReceiptDto Receipt, ApiModeEventPosition? Last)> inQueueOrder)
    {
        ApiModeEventPosition? newest = null;
        foreach (var (_, last) in inQueueOrder)
            if (last is { } at && (newest is not { } seen || at.IsLaterGenerationThan(seen.Generation, seen.GenerationStartedUtc)))
                newest = at;

        var place = 0;
        foreach (var (receipt, last) in inQueueOrder)
        {
            var inTheQueue = receipt.State == ApiModeReceiptState.Queued
                && (last is not { } at || newest is not { } current || string.Equals(at.Generation, current.Generation, StringComparison.Ordinal));
            receipt.QueuePosition = inTheQueue ? ++place : null;
        }
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
            _order.Add(promptId);
        }
        change(receipt);
        Touch(promptId, position);

        return WaitingIfMoved(e.Sequence, receipt);
    }

    /// <summary>
    /// The place in line is true now: an event may have taken a prompt out of the queue (or, by starting a newer Director
    /// generation, ended the old queue), so every place is counted again, and each still-queued receipt whose words
    /// changed is shown again - <paramref name="changed"/>, the receipt the event itself changed, first.
    /// </summary>
    private List<ApiModeLiveItem> WaitingIfMoved(long sequence, ApiModeReceiptDto? changed)
    {
        PlaceInLine(InQueueOrder());
        var items = new List<ApiModeLiveItem>();
        if (changed is not null) items.AddRange(ReceiptIfChanged(changed, sequence));
        foreach (var waiting in _order.Select(p => _receipts[p]))
            if (!ReferenceEquals(waiting, changed) && waiting.State == ApiModeReceiptState.Queued)
                items.AddRange(ReceiptIfChanged(waiting, sequence));
        return items;
    }

    private List<(ApiModeReceiptDto Receipt, ApiModeEventPosition? Last)> InQueueOrder()
        => _order.Select(p => (_receipts[p], _lastPositions.TryGetValue(p, out var at) ? at : (ApiModeEventPosition?)null)).ToList();

    /// <summary>The position of the last event that changed a receipt, as the store keeps it on the row.</summary>
    private void Touch(string promptId, ApiModeEventPosition? position)
    {
        if (position is { } at) _lastPositions[promptId] = at;
        else _lastPositions.Remove(promptId);
    }

    /// <summary><see cref="Touch"/> that never moves a receipt's position backwards, as the store keeps it for a turn
    /// event (it touches a row only for a newer event, and a real ending that replaces a lost one may be older).</summary>
    private void TouchForward(string promptId, ApiModeEventPosition? position)
    {
        if (!IsNewerThanLast(promptId, position)) return;
        Touch(promptId, position);
    }

    /// <summary>Whether an event at <paramref name="position"/> comes after the last one applied to the receipt, as the
    /// store's forward-only rule asks it of the row. An event handed without a position, or a receipt with no last
    /// position, counts as newer.</summary>
    private bool IsNewerThanLast(string promptId, ApiModeEventPosition? position)
        => position is not { } at || !_lastPositions.TryGetValue(promptId, out var last)
            || at.IsAfter(last.Generation, last.GenerationStartedUtc, last.Sequence);

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
    /// applied was set aside by its position first. The prompt's text is kept, as the store keeps it: a retry under the
    /// same id is the same prompt, so it carries the same words.
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
            // The retry arrived now, so it is read after every prompt that arrived before it, as the store reads it.
            _order.Remove(promptId);
            _order.Add(promptId);
            return true;
        }

        if (e is PromptQueued) return false; // a delivered prompt is never queued again; the store logs it
        SecondEndingsLogged++;
        FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={promptId} sequence={e.Sequence}: a second ending ({e.GetType().Name}) after {receipt.State}; a delivered prompt is never sent again, so the delivery stands");
        return false;
    }

    private IReadOnlyList<ApiModeLiveItem> TurnStart(TurnStarted s, ApiModeEventPosition? position)
    {
        var prompts = PromptsOf(s.TurnId);
        var replaced = new List<ApiModeReceiptDto>();
        var relinked = new List<ApiModeReceiptDto>();
        foreach (var promptId in s.PromptIds)
        {
            if (!prompts.Contains(promptId, StringComparer.Ordinal)) prompts.Add(promptId);
            if (_receipts.TryGetValue(promptId, out var receipt) && receipt.State == ApiModeReceiptState.Delivered)
            {
                if (receipt.TurnId is null && receipt.TurnOutcome == ApiModeLostTurnEnd.Outcome)
                {
                    // The receipt store's rule (ApiModeLostTurnEnd): the turn's real start links a lost receipt ruled
                    // with no turn whatever its position, and clears the lost ending only when it is newer than the
                    // receipt's last position. An older one keeps it lost, so the real TurnEnded replaces it here and in
                    // the store alike.
                    receipt.TurnId = s.TurnId;
                    if (IsNewerThanLast(promptId, position))
                    {
                        FileLog.Write($"[ApiModeLiveRuling] Rule: session={s.SessionId} prompt={promptId} turn={s.TurnId} sequence={s.Sequence}: the prompt was ruled {ApiModeLostTurnEnd.Outcome} with no turn; its turn's real start, newer than the ruling, replaces the lost ending");
                        receipt.TurnOutcome = null;
                        receipt.TurnOutcomeReason = null;
                        Touch(promptId, position);
                        replaced.Add(receipt);
                    }
                    else
                    {
                        FileLog.Write($"[ApiModeLiveRuling] Rule: session={s.SessionId} prompt={promptId} turn={s.TurnId} sequence={s.Sequence}: the prompt was ruled {ApiModeLostTurnEnd.Outcome} with no turn; its turn's real start is older than the ruling, so it is linked and stays {ApiModeLostTurnEnd.Outcome} until the turn's real end");
                        relinked.Add(receipt);
                    }
                    continue;
                }
                receipt.TurnId = s.TurnId;
                Touch(promptId, position);
            }
        }
        // It changes no receipt's words, unless it replaced a lost ending or is the first event of a newer generation to
        // reach a receipt. A lost receipt it linked keeps its words but now carries its turn, as the store's row does, so
        // that receipt is sent again.
        var items = replaced.SelectMany(r => ReceiptIfChanged(r, s.Sequence)).ToList();
        items.AddRange(relinked.Select(r => ReceiptItem(r, s.Sequence)));
        items.AddRange(WaitingIfMoved(s.Sequence, null));
        return items;
    }

    /// <summary>
    /// The lost ending (<see cref="ApiModeLostTurnEnd"/>), under the condition the receipt store applies: <paramref name="e"/>
    /// proves every earlier turn is over, and it is of a later generation than the last event applied to a receipt whose
    /// turn has no outcome. That last position is the one the store keeps on the row: the stored one for a receipt taken
    /// from the store, moved on by each event this ruling applies to it (<see cref="Touch"/>). One ruling rules one
    /// generation, so in practice only a receipt taken from the store and not touched since can qualify. A receipt whose
    /// last event was handed without a position counts as of this ruling's generation, as <see cref="PlaceInLine"/>
    /// counts it, and never qualifies.
    /// </summary>
    private IReadOnlyList<ApiModeLiveItem> LostTurnEnds(SessionEvent e, ApiModeLostTurnEnd.Continuing continuing, ApiModeEventPosition? position)
    {
        var items = new List<ApiModeLiveItem>();
        foreach (var promptId in _order)
        {
            if (!_lastPositions.TryGetValue(promptId, out var last)) continue;
            var receipt = _receipts[promptId];
            if (position is not { } at)
            {
                if (ApiModeLostTurnEnd.IsOpenOutside(continuing, promptId, receipt.State, receipt.TurnId, receipt.TurnOutcome))
                    throw new InvalidOperationException($"ApiModeLiveRuling: {e.GetType().Name} was handed without its position, and prompt={promptId}'s receipt, whose turn has no ending, was last changed at {last}; hand it as a held event");
                continue;
            }
            if (!ApiModeLostTurnEnd.Applies(at, continuing, promptId, receipt.State, receipt.TurnId, receipt.TurnOutcome, last.Generation, last.GenerationStartedUtc))
                continue;
            FileLog.Write($"[ApiModeLiveRuling] Rule: session={e.SessionId} prompt={promptId} turn={receipt.TurnId ?? "(its start was lost too)"}: its last event was at {last} and {ApiModeLostTurnEnd.Describe(e, at)} proves the turn is over; the turn's ending is ruled {ApiModeLostTurnEnd.Outcome}");
            receipt.TurnOutcome = ApiModeLostTurnEnd.Outcome;
            receipt.TurnOutcomeReason = null;
            Touch(promptId, at);
            items.AddRange(ReceiptIfChanged(receipt, e.Sequence));
        }
        // The store's rows moved on to the proof's generation, which may end an older generation's queue.
        if (items.Count > 0) items.AddRange(WaitingIfMoved(e.Sequence, null));
        return items;
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

    private IReadOnlyList<ApiModeLiveItem> TurnEnd(TurnEnded t, ApiModeEventPosition? position)
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
        // Rule 5: the prompts this turn carried now show its outcome beside Delivered. One turn ending per receipt, as
        // the store keeps it, with the store's one exception: when none of them is still open, the turn's real ending
        // replaces a lost one (ApiModeLostTurnEnd), whatever its position. A receipt still open under the same turn id
        // belongs to a turn running now, and this TurnEnded is that turn's (turn ids are unique per Director process
        // only; phase 2 review, observation C).
        if (_turnPrompts.Remove(t.TurnId, out var prompts))
        {
            var delivered = prompts
                .Select(p => _receipts.TryGetValue(p, out var r) && r.State == ApiModeReceiptState.Delivered ? r : null)
                .OfType<ApiModeReceiptDto>()
                .ToList();
            var open = delivered.Where(r => r.TurnOutcome is null).ToList();
            var targets = open.Count > 0 ? open : delivered.Where(r => r.TurnOutcome == ApiModeLostTurnEnd.Outcome).ToList();
            foreach (var receipt in targets)
            {
                if (receipt.TurnOutcome is not null)
                    FileLog.Write($"[ApiModeLiveRuling] Rule: session={t.SessionId} prompt={receipt.PromptId} turn={t.TurnId} sequence={t.Sequence}: the turn's real ending arrived after it was ruled lost; it replaces the lost ending");
                receipt.TurnId = t.TurnId;
                receipt.TurnOutcome = Word(t.Outcome);
                receipt.TurnOutcomeReason = t.Reason;
                TouchForward(receipt.PromptId, position);
                items.AddRange(ReceiptIfChanged(receipt, t.Sequence));
            }
        }
        // A tool card the dialect did not close would spin for ever; rule 3 says it always closes one, so any left is
        // forgotten with its turn rather than kept.
        foreach (var open in _tools.Where(kv => kv.Value.TurnId == t.TurnId).Select(kv => kv.Key).ToList())
            _tools.Remove(open);
        items.AddRange(WaitingIfMoved(t.Sequence, null));
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

    /// <summary>The first event that carries the prompt's text sets it, verbatim, as the receipt store keeps it; a later
    /// event never blanks or replaces it. Called only after an event has passed the finality check, so it sets the text
    /// under the same condition as the store: an event the store sets aside (a late one after Delivered) sets no text
    /// here either, and the live reader is never shown text the stored receipt does not have.</summary>
    private static void KeepPromptText(ApiModeReceiptDto receipt, string? promptText)
    {
        if (receipt.PromptText is null && !string.IsNullOrEmpty(promptText))
            receipt.PromptText = promptText;
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
        PromptText = r.PromptText,
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
        QueuePosition = r.QueuePosition,
        Words = r.Words,
        Tone = r.Tone,
    };
}
