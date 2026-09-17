using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Reports;

namespace CcDirector.Gateway.Fleet;

/// <summary>Everything the walkthrough fold reads, handed in, so every item kind and every missing fact is tested
/// without a host.</summary>
/// <param name="MarkedSessionId">The session the account marked as its Fleet Manager, or null.</param>
/// <param name="MarkedSession">The last row any Director reported for the marked session, or null.</param>
/// <param name="LiveRoster">The account's sessions whose Directors have pushed recently, stamped by the roster fold.</param>
/// <param name="LastKnownSession">The last row any Director of the account reported for one session, however old.</param>
/// <param name="Open">Every open record of the account.</param>
/// <param name="RoundIds">The records of the round the owner is in, as the client sent them back; null to start one.</param>
/// <param name="Record">One record of the account by id, whatever its status, or null.</param>
/// <param name="LatestVerdict">The Wingman's latest current reading of one session, or null.</param>
/// <param name="Repositories">The account's repository reports, for the close rule.</param>
/// <param name="SnoozeMinutes">The account's default snooze length.</param>
/// <param name="TimeZone">The account's display time zone.</param>
/// <param name="NowUtc">The clock.</param>
internal sealed record FleetManagerWalkthroughInputs(
    string? MarkedSessionId,
    SessionDto? MarkedSession,
    IReadOnlyList<SessionDto> LiveRoster,
    Func<string, SessionDto?> LastKnownSession,
    IReadOnlyList<FleetOutcomeDto> Open,
    IReadOnlyList<Guid>? RoundIds,
    Func<Guid, FleetOutcomeDto?> Record,
    Func<string, TurnVerdictDto?> LatestVerdict,
    IReadOnlyList<StoredRepoState> Repositories,
    int SnoozeMinutes,
    TimeZoneInfo TimeZone,
    DateTime NowUtc);

/// <summary>What the fold knows about the session one record is about.</summary>
internal sealed record FleetWalkthroughSessionFacts(
    string SessionId,
    SessionDto? Fresh,
    SessionDto? Known,
    TurnVerdictDto? Verdict)
{
    /// <summary>The best row there is: the fresh one, else the last one reported.</summary>
    public SessionDto? Row => Fresh ?? Known;

    public bool Ended => Row is { } r && (r.Crashed || string.Equals(r.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase));

    /// <summary>Running, and its computer has reported recently.</summary>
    public bool Live => Fresh is not null && !Ended;

    public bool Snoozed => Row is { } r && !Ended
        && (r.OnHold || string.Equals(HoldStates.Normalize(r.HoldState), HoldStates.DeferredHold, StringComparison.Ordinal));
}

/// <summary>
/// "TAKE ME THROUGH THEM" (the Fleet Manager mission, step 7), folded once on the Gateway. Every heading, sentence,
/// order, count, mark and permission the walkthrough shows is decided here (CLAUDE.md rule 7); the Cockpit renders it.
///
/// THE ORDER IS THE PAGE'S. The items are the same "Waiting on you" records the page fold lists, in the same order -
/// one rule, <see cref="FleetManagerPageFold.InWaitingOrder"/> - minus the ones whose session is snoozed.
///
/// A ROUND IS FIXED WHEN IT STARTS. The first read takes the waiting records (at most <see cref="MaxRoundItems"/>) and
/// hands their ids back; every later read names them, so an item answered in this round stays in the left column as
/// done, and a record that arrives meanwhile waits for the next round and is counted in "Not in this round".
///
/// THE READING IS THE WINGMAN'S, VERBATIM. The label, summary and evidence of the session's current verdict are
/// copied as stored. When there is no current reading - no session, never read, read and refused, the session ended,
/// or its computer has gone quiet - the reading says so in a sentence, and the item is answered through its record's
/// own card instead of through the session.
///
/// BOTH PICKS ARE MARKED HERE. The session's pick is the option the verdict marks recommended; the Fleet Manager's is
/// the option whose key equals the pick stored on the record. The client never compares strings.
///
/// CLOSE IS <see cref="FleetManagerCloseRule"/>'S TO DECIDE, and the close route asks it again.
/// </summary>
internal static class FleetManagerWalkthroughFold
{
    /// <summary>The most records one round holds.</summary>
    public const int MaxRoundItems = 50;

    /// <summary>How many of the session's last lines the screen shows.</summary>
    public const int ScreenLines = 14;

    /// <summary>The words a close records on the record, and what the item says once it is closed.</summary>
    public const string CloseWords = "Close the session.";

    /// <summary>The reason given to the stop route for a close from here.</summary>
    public const string StopReason = "Closed by the owner from the Fleet Manager walkthrough.";

    public static FleetManagerWalkthroughDto Fold(FleetManagerWalkthroughInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var marked = string.IsNullOrWhiteSpace(input.MarkedSessionId) ? null : input.MarkedSessionId.Trim();
        var fleetManager = FleetManagerSessions.IsFleetManager(input.MarkedSession, marked) ? marked : null;
        var fresh = input.LiveRoster
            .Where(s => !string.IsNullOrEmpty(s.SessionId))
            .GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var factsCache = new Dictionary<string, FleetWalkthroughSessionFacts>(StringComparer.OrdinalIgnoreCase);
        FleetWalkthroughSessionFacts? Facts(string? sid)
        {
            if (string.IsNullOrWhiteSpace(sid)) return null;
            if (factsCache.TryGetValue(sid, out var known)) return known;
            fresh.TryGetValue(sid, out var row);
            var facts = new FleetWalkthroughSessionFacts(sid, row, row ?? input.LastKnownSession(sid), input.LatestVerdict(sid));
            factsCache[sid] = facts;
            return facts;
        }
        bool SnoozedRecord(FleetOutcomeDto o) => Facts(o.SessionId)?.Snoozed == true;

        var waiting = FleetManagerPageFold.InWaitingOrder(input.Open).ToList();

        List<FleetOutcomeDto> round;
        if (input.RoundIds is null)
        {
            round = waiting.Where(o => !SnoozedRecord(o)).Take(MaxRoundItems).ToList();
        }
        else
        {
            var found = input.RoundIds.Distinct().Take(MaxRoundItems)
                .Select(id => input.Record(id))
                .Where(o => o is not null)
                .Select(o => o!)
                .ToList();
            round = FleetManagerPageFold.InWaitingOrder(found).ToList();
        }
        var inRound = new HashSet<string>(round.Select(o => o.Id), StringComparer.OrdinalIgnoreCase);

        var dto = new FleetManagerWalkthroughDto
        {
            GeneratedAtUtc = input.NowUtc,
            FleetManagerSessionId = fleetManager,
            Title = "Take me through them",
            Intro = "One at a time, most important first. The Wingman has already read each one; your answer goes straight to that session.",
            BackLabel = "Back to the conversation",
            RoundTitle = $"This round - {round.Count}",
            RoundIds = round.Select(o => o.Id).ToList(),
        };

        for (var i = 0; i < round.Count; i++)
        {
            var o = round[i];
            dto.Items.Add(Item(o, i + 1, round.Count, Facts(o.SessionId), fleetManager, input));
        }
        dto.OpenCount = dto.Items.Count(it => !it.Done);

        var outside = waiting.Where(o => !inRound.Contains(o.Id)).ToList();
        var snoozedOutside = outside.Count(SnoozedRecord);
        var moreOutside = outside.Count - snoozedOutside;
        var working = fleetManager is null
            ? 0
            : input.LiveRoster.Count(s => string.Equals(s.ControllerSessionId, fleetManager, StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(s.SessionId, fleetManager, StringComparison.OrdinalIgnoreCase)
                                          && SessionTree.CrewState(s) == SessionTree.CrewStateWorking);
        var parts = new List<string>();
        if (moreOutside > 0) parts.Add($"{moreOutside} more waiting, for the next round");
        if (snoozedOutside > 0) parts.Add($"{snoozedOutside} you snoozed");
        if (working > 0) parts.Add($"{working} of the Fleet Manager's sessions that {(working == 1 ? "is" : "are")} working");
        dto.NotInRound = parts.Count == 0 ? null : $"Not in this round: {JoinWithAnd(parts)}.";

        if (round.Count == 0)
            dto.EmptyText = moreOutside > 0 ? "Start the next round to go through what is waiting." : "Nothing is waiting on you.";

        if (dto.OpenCount > 0)
        {
            dto.EndTitle = "End of the round";
            dto.EndText = dto.OpenCount == 1
                ? "1 item in this round is still waiting - the one you skipped."
                : $"{dto.OpenCount} items in this round are still waiting - the ones you skipped.";
            dto.AgainLabel = "Go through the skipped ones again";
        }
        else
        {
            dto.EndTitle = "That is the round.";
            dto.EndText = moreOutside switch
            {
                0 => "Everything in this round is settled. Nothing else is waiting on you.",
                1 => "Everything in this round is settled. 1 more is waiting.",
                _ => $"Everything in this round is settled. {moreOutside} more are waiting.",
            };
        }
        dto.NewRoundLabel = moreOutside > 0 ? "Start the next round" : null;
        return dto;
    }

    // ---- one item ------------------------------------------------------------------------------------------

    private static FleetWalkthroughItemDto Item(FleetOutcomeDto o, int position, int total,
        FleetWalkthroughSessionFacts? facts, string? fleetManager, FleetManagerWalkthroughInputs input)
    {
        var tz = input.TimeZone;
        var now = input.NowUtc;
        var row = facts?.Row;
        var answered = o.Status == FleetOutcomeStore.StatusAnswered;
        var snoozed = !answered && facts?.Snoozed == true;
        var done = answered || snoozed;
        var sessionName = row is null ? null : string.IsNullOrWhiteSpace(row.Name) ? row.SessionId : row.Name;
        var age = FleetManagerPageFold.Age(o.CreatedAtUtc, now);

        var item = new FleetWalkthroughItemDto
        {
            Id = o.Id,
            Position = position,
            PositionLabel = $"{position} of {total}",
            Kind = o.Kind,
            Title = o.Title,
            SessionId = o.SessionId,
            SessionName = sessionName,
            Meta = Meta(o, row, fleetManager, tz, now),
            WaitLabel = done || age is null ? null : $"waiting {age}",
            Done = done,
            SkipLabel = "Skip for now",
        };

        item.StepLine = answered
            ? AnsweredLine(o, tz, now)
            : snoozed
                ? SnoozedLine(row!, tz, now)
                : sessionName is not null
                    ? $"{sessionName}{(age is null ? "" : $" - waiting {age}")}"
                    : o.Kind switch
                    {
                        FleetOutcomeStore.KindReady => $"Ready - risk {o.Ready?.Risk}",
                        FleetOutcomeStore.KindDecision => $"Decision{(age is null ? "" : $" - waiting {age}")}",
                        _ => $"Finding{(age is null ? "" : $" - waiting {age}")}",
                    };

        item.Reading = Reading(o, facts, tz, now);
        item.Advice = new FleetWalkthroughAdviceDto
        {
            Heading = "The Fleet Manager says",
            Text = string.IsNullOrWhiteSpace(o.Advice) ? null : o.Advice,
            EmptyText = string.IsNullOrWhiteSpace(o.Advice) ? "The Fleet Manager wrote no advice for this one." : null,
        };
        item.Screen = Screen(facts);

        var card = FleetManagerPageFold.Card(o, tz, now);
        var verdict = facts?.Verdict;
        var answerable = !done && facts is { Live: true } && verdict is { Failed: false }
                         && (verdict.Options.Count > 0 || IsParkedReply(verdict));
        if (done)
        {
            item.AnswerMode = "none";
            item.Card = card;
        }
        else if (answerable)
        {
            item.AnswerMode = "session";
            item.Answer = Answer(verdict!, o.FleetManagerPick);
        }
        else
        {
            item.AnswerMode = "fleet-manager";
            item.Card = card;
        }

        item.Snooze = Snooze(facts, done, fleetManager, input.SnoozeMinutes);
        item.Open = new FleetWalkthroughOpenDto { Offered = row is not null, Label = "Open the session" };
        item.Close = Close(o, facts, done, fleetManager, input);
        return item;
    }

    private static string Meta(FleetOutcomeDto o, SessionDto? row, string? fleetManager, TimeZoneInfo tz, DateTime now)
    {
        if (row is null)
            return o.Kind switch
            {
                FleetOutcomeStore.KindReady => "Ready for you - not about one session",
                FleetOutcomeStore.KindDecision => "Decision - not about one session",
                _ => "Finding - not about one session",
            };
        var parts = new List<string>();
        if (SessionOrdering.RepoName(row) is { } repo) parts.Add(repo);
        if (!string.IsNullOrWhiteSpace(row.MachineName)) parts.Add(row.MachineName);
        if (!string.IsNullOrWhiteSpace(row.AgentToolDisplay)) parts.Add(row.AgentToolDisplay);
        if (row.CreatedAt.Year >= 2000)
        {
            var when = FleetManagerPlacementFold.FormatWhen(row.CreatedAt, tz, now);
            parts.Add(fleetManager is not null && string.Equals(row.ControllerSessionId, fleetManager, StringComparison.OrdinalIgnoreCase)
                ? $"started by the Fleet Manager {when}"
                : $"started {when}");
        }
        return string.Join(" - ", parts);
    }

    private static string AnsweredLine(FleetOutcomeDto o, TimeZoneInfo tz, DateTime now)
    {
        var when = o.AnsweredAtUtc is { } at ? $" - {FleetManagerPlacementFold.FormatWhen(at, tz, now)}" : "";
        var who = o.AnsweredByRole == FleetOutcomeStore.RoleFleetManager ? "The Fleet Manager" : "You";
        if (string.Equals(o.Answer, CloseWords, StringComparison.Ordinal))
            return $"{who} closed the session{when}";
        return $"{who} said \"{o.Answer}\"{when}";
    }

    private static string SnoozedLine(SessionDto row, TimeZoneInfo tz, DateTime now)
        => row.SnoozeUntil is { } until
            ? $"Snoozed until {FleetManagerPlacementFold.FormatWhen(until, tz, now)}"
            : "Snoozed - the clock starts when it stops working";

    private static FleetWalkthroughReadingDto Reading(FleetOutcomeDto o, FleetWalkthroughSessionFacts? facts,
        TimeZoneInfo tz, DateTime now)
    {
        var reading = new FleetWalkthroughReadingDto { Heading = "What it needs from you - read by the Wingman" };
        if (facts is null)
        {
            reading.Note = "This item is not about one session, so the Wingman has nothing to read. The Fleet Manager's record is below.";
            return reading;
        }
        if (facts.Row is null)
        {
            reading.Note = "No computer on this account has reported this session, so there is no reading of it.";
            return reading;
        }
        if (facts.Ended)
        {
            reading.Note = "This session has ended, so there is nothing on its screen to answer.";
            return reading;
        }

        var v = facts.Verdict;
        if (v is null)
        {
            reading.Note = facts.Fresh is null
                ? "The session's computer has not reported recently, and the Wingman has no current reading of it."
                : string.Equals(facts.Fresh.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(facts.Fresh.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase)
                    ? "This session is working, so there is no stop for the Wingman to read yet."
                    : "The Wingman has no reading of this session's current stop. It reads each stop when the session finishes a turn.";
            return reading;
        }
        if (v.Failed)
        {
            reading.Note = $"The Wingman could not read this stop: {v.FailureReason}";
            return reading;
        }

        reading.Available = true;
        reading.Label = v.Label;
        reading.Summary = string.IsNullOrWhiteSpace(v.Summary) ? null : v.Summary;
        reading.EvidenceLead = string.IsNullOrWhiteSpace(v.Evidence) ? null : "In its own words:";
        reading.Evidence = string.IsNullOrWhiteSpace(v.Evidence) ? null : v.Evidence;
        reading.AgentRecommendsLead = string.IsNullOrWhiteSpace(v.AgentRecommends) ? null : "It recommends:";
        reading.AgentRecommends = string.IsNullOrWhiteSpace(v.AgentRecommends) ? null : v.AgentRecommends;
        reading.RiskLine = string.IsNullOrWhiteSpace(v.Risk) || v.Risk == "none" ? null : $"Risk: {v.Risk}";
        if (facts.Fresh is null)
            reading.Note = $"This reading is from {FleetManagerPlacementFold.FormatWhen(v.JudgedAtUtc, tz, now)}. The session's "
                           + "computer has not reported since, so its screen may have changed. Answer it once the computer is back.";
        return reading;
    }

    private static FleetWalkthroughScreenDto Screen(FleetWalkthroughSessionFacts? facts)
    {
        var screen = new FleetWalkthroughScreenDto
        {
            Heading = "The screen",
            Lines = ScreenLines,
            LoadingText = "Reading the session's last lines...",
        };
        if (facts is null) return screen;
        if (facts.Live)
            screen.Offered = true;
        else
            screen.Note = facts.Ended
                ? "The session has ended, so there is no screen to show."
                : "The session's computer is not reporting, so its screen cannot be read.";
        return screen;
    }

    private static bool IsParkedReply(TurnVerdictDto v)
        => v.AnswerVia == "keys" && v.Menu is not null && v.Options.Count == 0;

    private static FleetWalkthroughAnswerDto Answer(TurnVerdictDto v, string? pick)
    {
        var pickIndex = PickIndex(pick, v);
        var answer = new FleetWalkthroughAnswerDto
        {
            VerdictId = v.VerdictId,
            Question = v.AnswerVia == "keys" && !string.IsNullOrWhiteSpace(v.Menu?.Question) ? v.Menu!.Question : null,
            Multiple = v.Menu?.SelectionMode == "multiple",
            ParkedReply = IsParkedReply(v),
            SendChosenLabel = "Send the chosen options",
            ParkedReplyLabel = "Send the typed reply",
            SendingText = "Sending...",
            RecordFailedLead = "The session took your answer, but the Fleet Manager's record was not updated:",
            PickNote = !string.IsNullOrWhiteSpace(pick) && pickIndex < 0
                ? $"The Fleet Manager picked \"{pick}\", which is not one of the options on the screen now."
                : null,
        };
        for (var i = 0; i < v.Options.Count; i++)
        {
            var opt = v.Options[i];
            var sessionPick = opt.Recommended;
            var fmPick = i == pickIndex;
            answer.Options.Add(new FleetWalkthroughOptionDto
            {
                Index = i,
                Label = opt.Key,
                Note = string.IsNullOrWhiteSpace(opt.Note) ? null : opt.Note,
                SessionPick = sessionPick,
                FleetManagerPick = fmPick,
                MarkText = (sessionPick, fmPick) switch
                {
                    (true, true) => "its pick and the Fleet Manager's pick",
                    (true, false) => "its pick",
                    (false, true) => "Fleet Manager's pick",
                    _ => null,
                },
            });
        }
        return answer;
    }

    /// <summary>The position of the option whose key is the pick, or -1. Keys are compared exactly (after trimming both),
    /// because a pick is stored only after <see cref="CheckPick"/> found it among the keys.</summary>
    internal static int PickIndex(string? pick, TurnVerdictDto v)
    {
        if (string.IsNullOrWhiteSpace(pick)) return -1;
        var wanted = pick.Trim();
        for (var i = 0; i < v.Options.Count; i++)
            if (string.Equals(v.Options[i].Key.Trim(), wanted, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>
    /// Whether a pick may be stored for a record about <paramref name="sessionId"/>: it must name one of the options of
    /// that session's CURRENT reading. The refusal says what is wrong and lists the keys there are.
    /// </summary>
    /// <exception cref="ArgumentException">The pick names no option that can be picked now.</exception>
    internal static void CheckPick(string? pick, string? sessionId, TurnVerdictDto? verdict)
    {
        if (pick is null) return;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("a pick names one of the Wingman's options for the record's session, and this record "
                                        + "is about no session; file it with --session, or leave the pick out");
        if (verdict is null || verdict.Failed)
            throw new ArgumentException($"session {sessionId} has no current Wingman reading, so there are no options to pick "
                                        + "from; leave the pick out, or set it once the Wingman has read the stop");
        if (verdict.Options.Count == 0)
            throw new ArgumentException($"the Wingman's current reading of session {sessionId} offers no options, so there is "
                                        + "nothing to pick; leave the pick out");
        if (PickIndex(pick, verdict) < 0)
            throw new ArgumentException($"pick '{pick}' is not one of the Wingman's options for session {sessionId}; use one of: "
                                        + string.Join(", ", verdict.Options.Select(opt => $"'{opt.Key}'")));
    }

    private static FleetWalkthroughSnoozeDto Snooze(FleetWalkthroughSessionFacts? facts, bool done, string? fleetManager,
        int minutes)
    {
        var snooze = new FleetWalkthroughSnoozeDto { Label = $"Snooze {FormatLength(minutes)}", Minutes = minutes };
        if (facts is null || done) return snooze;
        if (fleetManager is not null && string.Equals(facts.SessionId, fleetManager, StringComparison.OrdinalIgnoreCase))
            snooze.Note = "The Fleet Manager itself is not snoozed from here.";
        else if (facts.Ended)
            snooze.Note = "The session has ended, so it cannot be snoozed.";
        else if (!facts.Live)
            snooze.Note = "The session's computer is not reporting, so it cannot be snoozed now.";
        else
            snooze.Offered = true;
        return snooze;
    }

    private static FleetWalkthroughCloseDto Close(FleetOutcomeDto o, FleetWalkthroughSessionFacts? facts, bool done,
        string? fleetManager, FleetManagerWalkthroughInputs input)
    {
        var close = new FleetWalkthroughCloseDto
        {
            Label = "Close the session...",
            ConfirmTitle = "Close this session?",
            ConfirmLabel = "Close it",
            BusyLabel = "Closing...",
        };
        if (facts is null || done) return close;
        var verdict = Decide(facts, fleetManager, input);
        close.Offered = verdict.Allowed;
        close.RefusedText = verdict.Refusal;
        if (verdict.Allowed)
        {
            var name = facts.Row is { } r && !string.IsNullOrWhiteSpace(r.Name) ? r.Name : facts.SessionId;
            close.ConfirmTitle = $"Close {name}?";
            close.ConfirmMessage = $"{verdict.Evidence} Your answer is recorded for the Fleet Manager as \"{CloseWords}\"";
        }
        return close;
    }

    /// <summary>The close rule over this fold's facts - the same call the close route makes.</summary>
    internal static FleetCloseVerdict Decide(FleetWalkthroughSessionFacts facts, string? fleetManager,
        FleetManagerWalkthroughInputs input)
        => FleetManagerCloseRule.Decide(facts.Row, facts.Live, fleetManager ?? input.MarkedSessionId, facts.Verdict,
            input.Repositories, input.TimeZone, input.NowUtc);

    /// <summary>A snooze length in words, as the Cockpit's snooze menu writes it (snoozeFormat.ts).</summary>
    internal static string FormatLength(int minutes)
    {
        const int hour = 60;
        const int day = 24 * 60;
        if (minutes < 1) return $"{minutes} minutes";
        if (minutes < hour) return Plural(minutes, "minute");
        if (minutes < day)
        {
            var hours = minutes / hour;
            var rest = minutes % hour;
            return rest == 0 ? Plural(hours, "hour") : $"{Plural(hours, "hour")} {Plural(rest, "minute")}";
        }
        var days = minutes / day;
        var restMinutes = minutes % day;
        if (restMinutes == 0) return Plural(days, "day");
        var h = (int)Math.Round(restMinutes / (double)hour, MidpointRounding.AwayFromZero);
        return h == 0 ? $"{Plural(days, "day")} {Plural(restMinutes, "minute")}" : $"{Plural(days, "day")} {Plural(h, "hour")}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string JoinWithAnd(IReadOnlyList<string> parts)
        => parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}",
        };
}
