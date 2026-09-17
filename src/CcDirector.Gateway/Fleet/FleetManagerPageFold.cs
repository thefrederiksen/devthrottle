using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>Everything the page fold reads, handed in, so every section and every empty case is tested without a
/// host.</summary>
/// <param name="MarkedSessionId">The session the account marked as its Fleet Manager, or null.</param>
/// <param name="MarkedSession">The last row any Director of the account reported for the marked session, live or
/// not, or null when none has - what <see cref="FleetManagerSessions.IsFleetManager"/> is asked about.</param>
/// <param name="Roster">The account's live sessions, stamped by the roster fold (so
/// <see cref="SessionDto.HasLiveSupervisor"/> is set).</param>
/// <param name="Open">Every open record of the account.</param>
/// <param name="Recent">The account's most recent records of every status, newest first - the cards.</param>
/// <param name="LatestVerdict">The Wingman's latest stored reading of one session in this account, or null.</param>
/// <param name="TimeZone">The account's display time zone.</param>
/// <param name="NowUtc">The clock.</param>
internal sealed record FleetManagerPageInputs(
    string? MarkedSessionId,
    SessionDto? MarkedSession,
    IReadOnlyList<SessionDto> Roster,
    IReadOnlyList<FleetOutcomeDto> Open,
    IReadOnlyList<FleetOutcomeDto> Recent,
    Func<string, TurnVerdictDto?> LatestVerdict,
    TimeZoneInfo TimeZone,
    DateTime NowUtc);

/// <summary>
/// The Fleet Manager page, folded once on the Gateway (the Fleet Manager mission, step 6). Every heading, sentence,
/// age, tone, count and button - with the exact words it sends - is decided here (CLAUDE.md rule 7).
///
/// WHICH SESSION IS THE FLEET MANAGER. The one rule, <see cref="FleetManagerSessions.IsFleetManager"/>: the account's
/// mark, on a session no session owns. A marked session that is owned, or that no Director has reported, is not the
/// Fleet Manager here either, so the page shows no conversation and no sessions as its. The Fleet Manager's sessions
/// are the live sessions whose direct owner is that session.
///
/// THE CARDS ARE RECORDS. Nothing here reads the model's prose: a card is an outcome record, its fields verbatim.
///
/// "LANDED TODAY" IS NOT CLAIMED. The Gateway does not record a merge. What it does record honestly is the owner's
/// answer to each Ready card, so the third section lists the Ready cards answered today, with the answer
/// verbatim, and says in its own note what it cannot know.
/// </summary>
internal static class FleetManagerPageFold
{
    /// <summary>How many recent records the conversation shows as cards.</summary>
    public const int CardCount = 100;

    private static readonly Regex PullRequestNumber = new(@"/pull/(\d+)(?:[/?#].*)?$", RegexOptions.CultureInvariant);

    public static FleetManagerPageDto Fold(FleetManagerPageInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var marked = string.IsNullOrWhiteSpace(input.MarkedSessionId) ? null : input.MarkedSessionId.Trim();
        var fleetManager = FleetManagerSessions.IsFleetManager(input.MarkedSession, marked) ? marked : null;
        var live = input.Roster.Where(s => !IsGone(s)).ToList();
        var byId = live
            .Where(s => !string.IsNullOrEmpty(s.SessionId))
            .GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var dto = new FleetManagerPageDto
        {
            GeneratedAtUtc = input.NowUtc,
            FleetManagerSessionId = fleetManager,
            NoConversationText = fleetManager is not null
                ? null
                : marked is null
                    ? "There is no Fleet Manager conversation yet. Start the Fleet Manager, then tell it what you want here."
                    : NotTheFleetManager(input.MarkedSession, marked),
            QuickPrompts =
            {
                new FleetManagerQuickPromptDto { Label = "What did I miss?", Words = "What did I miss?" },
            },
            Cards = input.Recent
                .OrderBy(o => o.CreatedAtUtc)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select(o => Card(o, input.TimeZone, input.NowUtc))
                .ToList(),
            Waiting = Waiting(input, byId),
            UnderWay = UnderWay(live, marked, fleetManager, input.NowUtc),
            Landed = Landed(input),
            NotMine = NotMine(live, fleetManager),
        };
        dto.WaitingCount = dto.Waiting.Count;
        // The way into the walkthrough (step 7), offered only when something is waiting.
        dto.WalkthroughLabel = dto.WaitingCount > 0 ? "Take me through them" : null;
        return dto;
    }

    // ---- cards ---------------------------------------------------------------------------------------------

    internal static FleetOutcomeCardDto Card(FleetOutcomeDto o, TimeZoneInfo tz, DateTime now)
    {
        var card = new FleetOutcomeCardDto
        {
            Id = o.Id,
            Kind = o.Kind,
            Title = o.Title,
            FiledAtUtc = o.CreatedAtUtc,
            WhoLine = $"Fleet Manager - {FleetManagerPlacementFold.FormatWhen(o.CreatedAtUtc, tz, now)}",
            Answered = o.Status == FleetOutcomeStore.StatusAnswered,
        };
        if (card.Answered)
        {
            card.AnswerLabel = o.AnsweredAtUtc is { } at
                ? $"Answered {FleetManagerPlacementFold.FormatWhen(at, tz, now)}"
                : "Answered";
            card.Answer = o.Answer ?? "";
        }

        switch (o.Kind)
        {
            case FleetOutcomeStore.KindReady:
                card.Tone = FleetOutcomeCardDto.ToneReady;
                card.KindLabel = "Ready for you";
                card.Ready = ReadyCard(o.Ready ?? new FleetReadyDetails());
                if (!card.Answered)
                {
                    card.Actions.Add(new FleetCardActionDto { Label = "Merge", Style = "primary", Words = $"Merge: {o.Title}" });
                    card.Actions.Add(new FleetCardActionDto
                    {
                        Label = "Send it back...",
                        Style = "ghost",
                        AsksForWords = true,
                        WordsPrefix = $"Send it back: {o.Title}. ",
                        Placeholder = "What should change?",
                        SendLabel = "Send it back",
                    });
                }
                break;

            case FleetOutcomeStore.KindFinding:
                var finding = o.Finding ?? new FleetFindingDetails();
                card.Tone = FleetOutcomeCardDto.ToneFinding;
                card.KindLabel = "Finding";
                card.Finding = new FleetFindingCardDto
                {
                    Answer = finding.Answer,
                    Reason = string.IsNullOrWhiteSpace(finding.Reason) ? null : finding.Reason,
                    Links = finding.Links.Select((url, i) => new FleetCardLinkDto { Label = LinkLabel(url, i), Url = url }).ToList(),
                };
                // A finding stays open until it is answered, like every record, so it needs one way to say so -
                // otherwise it would count as waiting on the owner for ever.
                if (!card.Answered)
                    card.Actions.Add(new FleetCardActionDto { Label = "Got it", Style = "secondary", Words = $"Got it: {o.Title}" });
                break;

            case FleetOutcomeStore.KindDecision:
                var decision = o.Decision ?? new FleetDecisionDetails();
                card.Tone = FleetOutcomeCardDto.ToneDecision;
                card.KindLabel = "Decision - only you can make this";
                card.Decision = new FleetDecisionCardDto
                {
                    Question = string.Equals(decision.Question.Trim(), o.Title.Trim(), StringComparison.Ordinal) ? null : decision.Question,
                    Options = decision.Options
                        .Select(opt => new FleetDecisionOptionDto { Text = opt, Recommended = IsRecommended(opt, decision.Recommended) })
                        .ToList(),
                    RecommendedLabel = "Recommended",
                    Why = string.IsNullOrWhiteSpace(decision.Why) ? null : $"Why: {decision.Why}",
                };
                // One button per option, and its words are the option exactly, so the record says the answer was
                // one of the options.
                if (!card.Answered)
                {
                    foreach (var opt in card.Decision.Options)
                        card.Actions.Add(new FleetCardActionDto
                        {
                            Label = opt.Text,
                            Style = opt.Recommended ? "primary" : "secondary",
                            Words = opt.Text,
                        });
                }
                break;

            default:
                throw new InvalidOperationException($"outcome {o.Id} has kind '{o.Kind}', which the page does not draw");
        }
        return card;
    }

    private static FleetReadyCardDto ReadyCard(FleetReadyDetails r)
    {
        var facts = new List<FleetCardFactDto> { new() { Label = "Checks", Value = r.Checks } };
        if (!string.IsNullOrWhiteSpace(r.Tested)) facts.Add(new FleetCardFactDto { Label = "Tested", Value = r.Tested });
        if (!string.IsNullOrWhiteSpace(r.ReviewedBy)) facts.Add(new FleetCardFactDto { Label = "Reviewed by", Value = r.ReviewedBy });
        var number = PullRequestNumber.Match(r.PullRequest.Trim());
        return new FleetReadyCardDto
        {
            RiskLabel = $"Risk {r.Risk}",
            RiskTone = r.Risk,
            Facts = facts,
            Change = r.Change,
            PullRequest = new FleetCardLinkDto
            {
                Label = number.Success ? $"Open pull request #{number.Groups[1].Value}" : "Open pull request",
                Url = r.PullRequest,
            },
        };
    }

    private static bool IsRecommended(string option, string? recommended)
        => recommended is not null && string.Equals(option.Trim(), recommended.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A link's label: the last part of its path (a report's file name), else the link itself.</summary>
    private static string LinkLabel(string url, int index)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var leaf = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return leaf.Length == 0 ? $"Link {index + 1}" : $"Open {leaf}";
    }

    // ---- the panel -----------------------------------------------------------------------------------------

    private static int KindRank(string kind) => kind switch
    {
        FleetOutcomeStore.KindDecision => 0,
        FleetOutcomeStore.KindReady => 1,
        _ => 2,
    };

    /// <summary>
    /// THE ORDER OF "WAITING ON YOU", the one rule the page and the walkthrough (step 7) both use: decisions, then work
    /// ready to merge, then findings; the oldest first within each kind, because it has waited longest. Every key is a
    /// fact a record never changes, so a record keeps its place whether it is open or answered - which is what lets the
    /// walkthrough keep a settled item where it was. The caller filters; this only orders.
    /// </summary>
    internal static IOrderedEnumerable<FleetOutcomeDto> InWaitingOrder(IEnumerable<FleetOutcomeDto> records)
        => records
            .OrderBy(o => KindRank(o.Kind))
            .ThenBy(o => o.CreatedAtUtc)
            .ThenBy(o => o.Id, StringComparer.Ordinal);

    /// <summary>Open records, most important first (<see cref="InWaitingOrder"/>).</summary>
    private static FleetPanelSectionDto Waiting(FleetManagerPageInputs input, IReadOnlyDictionary<string, SessionDto> live)
    {
        var items = InWaitingOrder(input.Open.Where(o => o.Status == FleetOutcomeStore.StatusOpen))
            .Select(o =>
            {
                SessionDto? session = null;
                if (!string.IsNullOrWhiteSpace(o.SessionId)) live.TryGetValue(o.SessionId, out session);
                var verdict = string.IsNullOrWhiteSpace(o.SessionId) ? null : input.LatestVerdict(o.SessionId);
                return new FleetPanelItemDto
                {
                    Id = o.Id,
                    Title = o.Title,
                    Meta = WaitingMeta(o, session),
                    Label = verdict is { Failed: false, SupersededAtUtc: null } && !string.IsNullOrWhiteSpace(verdict.Label)
                        ? verdict.Label
                        : null,
                    Age = Age(o.CreatedAtUtc, input.NowUtc),
                    Dot = FleetPanelItemDto.DotRed,
                    Attention = true,
                    SessionId = o.SessionId,
                };
            })
            .ToList();

        return new FleetPanelSectionDto
        {
            Title = "Waiting on you",
            Count = items.Count,
            Tone = items.Count > 0 ? "attention" : "plain",
            Items = items,
            EmptyText = items.Count == 0 ? "Nothing is waiting on you." : null,
        };
    }

    private static string WaitingMeta(FleetOutcomeDto o, SessionDto? session)
    {
        var parts = new List<string>();
        switch (o.Kind)
        {
            case FleetOutcomeStore.KindReady:
                parts.Add("Ready");
                if (o.Ready is { } r)
                {
                    parts.Add($"risk {r.Risk}");
                    parts.Add($"checks {r.Checks}");
                }
                break;
            case FleetOutcomeStore.KindDecision:
                parts.Add("Decision");
                break;
            default:
                parts.Add("Finding");
                break;
        }
        if (session is not null && !string.IsNullOrWhiteSpace(session.Name)) parts.Add(session.Name);
        return string.Join(" - ", parts);
    }

    /// <summary>The live sessions whose direct owner is the marked Fleet Manager, oldest first.</summary>
    private static FleetPanelSectionDto UnderWay(IReadOnlyList<SessionDto> live, string? marked, string? fleetManager,
        DateTime now)
    {
        var owned = fleetManager is null
            ? new List<SessionDto>()
            : live.Where(s => IsOwnedBy(s, fleetManager)).OrderBy(s => s.CreatedAt).ThenBy(s => s.SessionId, StringComparer.Ordinal).ToList();

        var items = owned.Select(s =>
        {
            var meta = new List<string>();
            if (SessionOrdering.RepoName(s) is { } repo) meta.Add(repo);
            if (Age(s.CreatedAt, now) is { } age) meta.Add(age);
            return new FleetPanelItemDto
            {
                Id = s.SessionId,
                Title = string.IsNullOrWhiteSpace(s.Name) ? s.SessionId : s.Name!,
                Meta = string.Join(" - ", meta),
                Dot = SessionTree.CrewState(s) switch
                {
                    SessionTree.CrewStateWorking => FleetPanelItemDto.DotBlue,
                    SessionTree.CrewStateNeedsYou => FleetPanelItemDto.DotRed,
                    _ => FleetPanelItemDto.DotGrey,
                },
                SessionId = s.SessionId,
            };
        }).ToList();

        return new FleetPanelSectionDto
        {
            Title = "Under way",
            Count = items.Count,
            Tone = "plain",
            Items = items,
            EmptyText = items.Count > 0
                ? null
                : marked is null
                    ? "No Fleet Manager is marked for this account yet, so no session is its."
                    : fleetManager is null
                        ? "The marked session is not the Fleet Manager, so no session is its."
                        : "Nothing is under way. The sessions the Fleet Manager starts show here.",
        };
    }

    /// <summary>The Ready cards the owner answered today, in the account's time zone, most recent first.</summary>
    private static FleetPanelSectionDto Landed(FleetManagerPageInputs input)
    {
        var tz = input.TimeZone;
        var today = Local(input.NowUtc, tz).Date;
        var items = input.Recent
            .Where(o => o.Kind == FleetOutcomeStore.KindReady
                        && o.Status == FleetOutcomeStore.StatusAnswered
                        && o.AnsweredAtUtc is { } at && Local(at, tz).Date == today)
            .OrderByDescending(o => o.AnsweredAtUtc)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new FleetPanelItemDto
            {
                Id = o.Id,
                Title = o.Title,
                // Step 3 lets the Fleet Manager answer a record too; the line says who did.
                Meta = $"{(o.AnsweredByRole == FleetOutcomeStore.RoleFleetManager ? "The Fleet Manager said" : "You said")} "
                       + $"\"{o.Answer}\" at {FleetManagerPlacementFold.FormatWhen(o.AnsweredAtUtc!.Value, tz, input.NowUtc)}",
                Dot = FleetPanelItemDto.DotGreen,
                SessionId = o.SessionId,
            })
            .ToList();

        return new FleetPanelSectionDto
        {
            Title = "Answered today",
            Count = items.Count,
            Tone = "plain",
            Items = items,
            EmptyText = items.Count == 0 ? "No Ready card was answered today." : null,
            Note = "What landed today needs the merge itself, which the Gateway does not record yet. "
                   + "This lists the Ready cards answered today.",
        };
    }

    /// <summary>
    /// Live sessions that are not the Fleet Manager, not owned by it, and that go red for the owner - those with no
    /// live owner of their own. Sessions another live session owns report to that session, not to the owner, so they
    /// are not counted.
    /// </summary>
    private static FleetNotMineDto NotMine(IReadOnlyList<SessionDto> live, string? marked)
    {
        var count = live.Count(s =>
            !(marked is not null && string.Equals(s.SessionId, marked, StringComparison.OrdinalIgnoreCase))
            && !(marked is not null && IsOwnedBy(s, marked))
            && !s.HasLiveSupervisor);

        return count switch
        {
            0 => new FleetNotMineDto { Count = 0, Lead = "No session asks you directly.", Rest = "Every live session is the Fleet Manager's or owned by another session." },
            1 => new FleetNotMineDto { Count = 1, Lead = "1 session is not the Fleet Manager's.", Rest = "It still asks you directly." },
            _ => new FleetNotMineDto { Count = count, Lead = $"{count} sessions are not the Fleet Manager's.", Rest = "They still ask you directly." },
        };
    }

    /// <summary>Why the marked session is not the Fleet Manager, in the words the conversation shows instead.</summary>
    private static string NotTheFleetManager(SessionDto? row, string marked)
        => row is null
            ? $"Session {marked} is marked as the Fleet Manager, but no computer on this account has reported it. "
              + "Start the Fleet Manager from Settings."
            : $"Session {marked} is marked as the Fleet Manager, but it is owned by session {row.ControllerSessionId}, "
              + "and a Fleet Manager answers to you only. Start the Fleet Manager from Settings.";

    // ---- shared --------------------------------------------------------------------------------------------

    private static bool IsOwnedBy(SessionDto s, string marked)
        => string.Equals(s.ControllerSessionId, marked, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(s.SessionId, marked, StringComparison.OrdinalIgnoreCase);

    private static bool IsGone(SessionDto s)
        => s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    private static DateTime Local(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    /// <summary>How long ago: "now" under a minute, "42m", "1h 12m", "9h", "3d". Null for a time that is not a
    /// real stamp.</summary>
    internal static string? Age(DateTime utc, DateTime nowUtc)
    {
        if (utc.Year < 2000) return null;
        var span = nowUtc - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (span < TimeSpan.FromMinutes(1)) return "now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m";
        if (span < TimeSpan.FromDays(1))
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d";
    }
}
