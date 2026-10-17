using CcDirector.Core.Utilities;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// THE GATEWAY'S VERDICT ON ONE API-MODE SESSION (mission #4070, phase 2), folded once here and stamped on the row every
/// client reads (<see cref="SessionDto.ApiMode"/>). The project rule 7: the client renders it and decides nothing. Pure:
/// no input or output, no clock - the same facts give the same verdict.
///
/// <para><b>Which fact comes from where, and what survives a Gateway restart.</b></para>
/// <list type="bullet">
/// <item>Connection mode and who holds the wheel: the Director's own report on the row. Survives (the Director pushes
/// its roster again).</item>
/// <item>The turn number, working or idle, the queue, and the last turn's ending: the stored receipts. The ingest writes
/// the receipts BEFORE the event feed hands an event on, so the receipts are never behind the feed. Survives, for as long
/// as the receipts are kept (<see cref="Receipts.ApiModeReceiptStore.Retention"/>).</item>
/// <item>Whether the agent process is connected, and its model: the latest <c>ConnectionReady</c> or
/// <c>ConnectionEnded</c> this Gateway holds. Does NOT survive: the feed is empty after a restart. Then the verdict says
/// what the Director's wheel report supports, and "not known" where it supports nothing.</item>
/// </list>
/// </summary>
public static class ApiModeSessionFold
{
    /// <summary>
    /// The verdict, or null for a row that is not in API mode (a terminal-mode row gets nothing new).
    /// </summary>
    /// <param name="connectionMode">The row's <see cref="SessionDto.ConnectionMode"/>.</param>
    /// <param name="wheelHolder">The row's <see cref="SessionDto.WheelHolder"/>.</param>
    /// <param name="receipts">The session's stored receipts, oldest prompt first, as the receipt store reads them.</param>
    /// <param name="held">What the event feed holds for the session (<see cref="IApiModeEventFeed.Read"/> with no
    /// cursor).</param>
    public static ApiModeSessionDisplay? Fold(string connectionMode, string wheelHolder, IReadOnlyList<ApiModeVerdictReceipt> receipts, ApiModeEventReplay held)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(held);
        if (!string.Equals(connectionMode, ConnectionModes.Api, StringComparison.Ordinal))
            return null;

        var wheelKnown = wheelHolder is WheelHolders.Protocol or WheelHolders.User or WheelHolders.None;
        if (!wheelKnown)
            FileLog.Write($"[ApiModeSessionFold] Fold: a wheel holder this Gateway has no words for: '{wheelHolder}'");
        var userHasTheWheel = wheelHolder == WheelHolders.User;

        // ----- turns, from the receipts: numbered in the order their first prompt arrived -----
        // A set beside the list, so a long session's thousands of receipts are counted in one pass, not one scan each.
        var turnOrder = new List<string>();
        var seenTurns = new HashSet<string>(StringComparer.Ordinal);
        var outcomeOf = new Dictionary<string, ApiModeVerdictReceipt>(StringComparer.Ordinal);
        foreach (var r in receipts)
        {
            if (r.TurnId is not { } turn) continue;
            if (seenTurns.Add(turn)) turnOrder.Add(turn);
            if (r.TurnOutcome is not null && !outcomeOf.ContainsKey(turn)) outcomeOf[turn] = r;
        }
        var latestTurn = turnOrder.Count > 0 ? turnOrder[^1] : null;
        var latestTurnOpen = latestTurn is not null && !outcomeOf.ContainsKey(latestTurn);
        int? turnNumber = turnOrder.Count > 0 ? turnOrder.Count : null;

        // ----- working or idle -----
        bool? working;
        string activity;
        if (latestTurnOpen && wheelHolder == WheelHolders.Protocol)
        {
            working = true;
            activity = $"Working - turn {turnNumber}";
        }
        else if (latestTurnOpen)
        {
            // A turn started and its end never reached this Gateway, yet the protocol process no longer holds the
            // conversation. The Director always ends a turn it started (event model rule 2), so the end is missing, not
            // still to come - and "working" would be a confident wrong verdict.
            working = null;
            activity = $"Not known: turn {turnNumber} started and its end never reached the Gateway";
        }
        else
        {
            working = false;
            activity = turnNumber is null ? "Idle - no turn yet" : $"Idle after turn {turnNumber}";
        }

        // ----- the last turn's ending, when it was stopped or failed -----
        string? lastTurnEnding = null;
        if (latestTurn is not null && outcomeOf.TryGetValue(latestTurn, out var ended))
            lastTurnEnding = ended.TurnOutcome switch
            {
                "failed" => "The last turn failed: " + (ended.TurnOutcomeReason ?? "no reason was given"),
                "cancelled" => "The last turn was stopped: " + (ended.TurnOutcomeReason ?? "no reason was given"),
                _ => null,
            };

        // ----- the queue, from the receipts the Director still holds -----
        var queued = receipts.Where(r => r.State == ApiModeReceiptState.Queued).ToList();
        var queueLabel = QueueWords(queued);

        // ----- the agent process: the latest connection event held here, else what the wheel supports -----
        var (agentConnected, agentLabel) = Agent(wheelHolder, held);

        var kind = !wheelKnown ? "unknown"
            : userHasTheWheel ? "userHasTheWheel"
            : wheelHolder == WheelHolders.None ? "stopped"
            : working switch { true => "working", false => "idle", null => "unknown" };
        var tone = kind switch
        {
            "working" => "green",
            "userHasTheWheel" => "yellow",
            _ when lastTurnEnding is not null && latestTurn is not null && outcomeOf[latestTurn].TurnOutcome == "failed" => "red",
            _ when agentConnected == false && wheelHolder == WheelHolders.Protocol => "red",
            _ => "neutral",
        };

        return new ApiModeSessionDisplay
        {
            Kind = kind,
            Tone = tone,
            ConnectionLabel = userHasTheWheel ? "API mode - You have the wheel" : "API mode",
            UserHasTheWheel = userHasTheWheel,
            ActivityLabel = activity,
            Working = working,
            TurnNumber = turnNumber,
            QueuedCount = queued.Count,
            QueueLabel = queueLabel,
            LastTurnEnding = lastTurnEnding,
            AgentConnected = agentConnected,
            AgentLabel = agentLabel,
            WheelAction = WheelAction(wheelHolder, working),
        };
    }

    /// <summary>The disabled take-the-wheel button's reason, while a turn is known to be running.</summary>
    internal const string TurnRunningSoNoTake =
        "A turn is running. Stop it, or wait for it to end, before taking the wheel.";

    /// <summary>
    /// The one wheel button the Terminal tab shows, ruled here and nowhere else. The Gateway disables a button only on
    /// what it knows; every other refusal (the agent disconnected, a turn the Gateway cannot see, a terminal mid-turn on
    /// a hand back) is the Director's to rule, and the wheel routes pass its refusal through verbatim.
    /// </summary>
    private static ApiModeWheelAction? WheelAction(string wheelHolder, bool? working)
    {
        switch (wheelHolder)
        {
            case WheelHolders.Protocol:
                // Disabled only while a turn is KNOWN to be running: an unknown "working" is not a reason.
                return working == true
                    ? new ApiModeWheelAction { Verb = ApiModeWheelVerbs.TakeTheWheel, Label = "Take the wheel", Enabled = false, DisabledReason = TurnRunningSoNoTake }
                    : new ApiModeWheelAction { Verb = ApiModeWheelVerbs.TakeTheWheel, Label = "Take the wheel", Enabled = true };
            case WheelHolders.User:
                return new ApiModeWheelAction { Verb = ApiModeWheelVerbs.HandBack, Label = "Hand back", Enabled = true };
            case WheelHolders.None:
                // No process holds the conversation, so there is no wheel to take or hand back. What "none" means after
                // the user held the wheel (whether a hand back is still offered) is the Director's to define in phase 4,
                // and this ruling is revisited then.
                return null;
            default:
                // A wheel holder this Gateway has no words for: no button, never a guess at one.
                return null;
        }
    }

    /// <summary>The queue in words: how many prompts wait, and why (the Director's reason for each).</summary>
    internal static string? QueueWords(IReadOnlyList<ApiModeVerdictReceipt> queued)
    {
        if (queued.Count == 0) return null;
        var head = queued.Count == 1 ? "1 prompt waiting" : $"{queued.Count} prompts waiting";
        var byReason = queued
            .GroupBy(r => ApiModeLiveRuling.QueueReasonWords(r.QueueReason))
            .Select(g => (Words: g.Key, Count: g.Count()))
            .ToList();
        if (byReason.Count == 1) return $"{head}: {byReason[0].Words}";
        return $"{head}: " + string.Join(", ", byReason.Select(g => $"{g.Count} because {g.Words}"));
    }

    /// <summary>The agent label when the protocol process holds the conversation and the Gateway holds no connection
    /// event for the session: what is known, and no guess at why the event is not held.</summary>
    internal const string NoConnectionEventHeld =
        "Not known: the Gateway holds no connection event for this session. The Director reports the agent process holds the conversation";

    private static (bool? Connected, string Label) Agent(string wheelHolder, ApiModeEventReplay held)
    {
        if (wheelHolder == WheelHolders.User)
            return (false, "The agent runs in your terminal");

        var latest = held.Events.Select(h => h.Event).LastOrDefault(e => e is ConnectionReady or ConnectionEnded);
        switch (latest)
        {
            case ConnectionEnded gone:
                return (false, ApiModeLiveRuling.DisconnectedWords(gone.Reason));
            case ConnectionReady ready when wheelHolder == WheelHolders.Protocol:
                return (true, $"Agent connected ({ready.Model})");
        }

        if (wheelHolder == WheelHolders.None)
            return (false, "No agent process is running");
        if (wheelHolder != WheelHolders.Protocol)
            return (null, "Not known: the Director reports a wheel holder this Gateway does not know");
        // The protocol process holds the conversation, and no connection event is held here. With the whole
        // generation held, the agent has not said it is ready yet; with anything missing, this Gateway cannot tell -
        // and it cannot tell WHY anything is missing either (a Gateway restart, the buffer letting the session go or
        // trimming its oldest events, a new generation), so the words state only what is known (review finding 2).
        return held.Complete
            ? (false, "Agent starting")
            : (null, NoConnectionEventHeld);
    }
}
