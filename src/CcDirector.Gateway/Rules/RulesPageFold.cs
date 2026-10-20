using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.RuleHook;

namespace CcDirector.Gateway.Rules;

/// <summary>A tab of the Rules page (wire contract P1): its id, its label, how many rules are on it and what it
/// says when none are.</summary>
public sealed record RulesPageTab(string Id, string Label, int Count, string EmptyText);

/// <summary>One rule as the Rules page lists it (wire contract P1). Every field is a finished string.</summary>
public sealed record RulesPageRule(
    Guid Id,
    string Sentence,
    string KindLabel,
    string TriggerLabel,
    string ScopeLabel,
    string ModeLabel,
    string ModeTone,
    string HandlerLabel,
    string FiredLabel,
    string ChangedLabel,
    IReadOnlyList<string> Tabs);

/// <summary>The whole Rules page (wire contract P1).</summary>
public sealed record RulesPage(
    string Today,
    IReadOnlyList<RulesPageTab> Tabs,
    IReadOnlyList<RulesPageRule> Rules,
    RulesPreviewSuggestions PreviewSuggestions);

/// <summary>One labelled line of a rule's page (wire contract P2).</summary>
public sealed record RulePageField(string Label, string Value);

/// <summary>A rule a firing or a preview names, by id and sentence.</summary>
public sealed record RuleReference(Guid Id, string Sentence);

/// <summary>One firing on a rule's page (wire contract P2).</summary>
public sealed record RulePageFiring(
    Guid Id,
    string WhenLabel,
    string DecisionLabel,
    string Tone,
    string SessionLabel,
    string WhereLabel,
    string Subject,
    string OverrodeLabel,
    IReadOnlyList<RuleReference> Overrode,
    string OutcomeLabel);

/// <summary>One change on a rule's page (wire contract P2).</summary>
public sealed record RulePageChange(string WhenLabel, string ActionLabel, string ByLabel);

/// <summary>One rule's page (wire contract P2).</summary>
public sealed record RulePage(
    RulesPageRule Rule,
    IReadOnlyList<RulePageField> Fields,
    IReadOnlyList<RulePageFiring> Firings,
    string FiringsEmptyText,
    IReadOnlyList<RulePageChange> Changes,
    string ChangesEmptyText);

/// <summary>One rule that reaches the previewed machine and repository (wire contract P3).</summary>
public sealed record RulesPreviewRule(
    Guid Id,
    string Sentence,
    string KindLabel,
    string ScopeLabel,
    string RankLabel,
    string ResultLabel,
    string ModeLabel,
    IReadOnlyList<RuleReference> Overrides,
    IReadOnlyList<RuleReference> OverriddenBy,
    string VerdictLabel);

/// <summary>What the hook would decide for the sample command (wire contract P3).</summary>
public sealed record RulesPreviewDecision(
    string Command,
    string VerdictLabel,
    string Tone,
    string Summary,
    string DecidingRuleId,
    IReadOnlyList<string> OverriddenRuleIds,
    IReadOnlyList<string> MatchedRuleIds);

/// <summary>The machines and repositories the preview form can offer.</summary>
public sealed record RulesPreviewSuggestions(IReadOnlyList<string> Machines, IReadOnlyList<string> Repos);

/// <summary>The preview of one machine and repository (wire contract P3).</summary>
public sealed record RulesPreview(
    string Machine,
    string Repo,
    string Headline,
    IReadOnlyList<RulesPreviewRule> Rules,
    string EmptyText,
    RulesPreviewDecision? Decision,
    RulesPreviewSuggestions Suggestions);

/// <summary>
/// Everything the Rules page is folded from, read from the store once: every rule, how often each has fired (a
/// tally, never the firing rows), the machines and repositories the firings name, and the change history. One
/// rule's firing record is read separately, for that rule's page only. The fold reads only this, so it is tested
/// without HTTP. It is
/// read from the store by <c>SessionRuleWire.ReadPageSource</c>, outside this namespace, because nothing in the
/// rules namespace may hold the store that can promote (<c>RulesPromotionBoundaryGuardTests</c>).
/// </summary>
public sealed record RulesPageSource(
    IReadOnlyList<SessionRule> Rules,
    IReadOnlyDictionary<Guid, RuleFiringTally> Tallies,
    IReadOnlyList<RuleFiringPlace> FiringPlaces,
    IReadOnlyList<SessionRuleChange> Changes);

/// <summary>
/// THE RULES PAGE, FOLDED ON THE GATEWAY (Session Rules phase 3, ruling T1). Every label, tone, tab membership,
/// count, empty text and every "decided / overrode" verdict the Cockpit and the phone show is worked out here and
/// sent as a finished string or boolean; the clients lay it out and decide nothing.
///
/// "TODAY" IS THE VIEWER'S DAY (T3). The client sends its time zone and every day and time label is worked out in
/// it. A missing or unknown zone is refused - never read as UTC - because a "fired today" that silently means a
/// different day is a wrong answer that looks right.
///
/// ONE RESOLVER (T6). The preview's decision for a sample command is made by the hook's own
/// <see cref="RuleResolver"/>, compiled into this assembly from the hook's source, over the same rules in the same
/// order the Director writes into a session's rules file. The preview cannot decide differently from the hook.
///
/// STANDING OVERRIDES (T7) are stated only when they are always true: rule A overrides rule B when both are hook
/// rules on the same event and tool, their results differ, A wins the rank contest, and some match text of A
/// contains some match text of B - so every command A matches on that text, B matches too, and A decides it.
/// </summary>
[RuleFeature]
public static class RulesPageFold
{
    /// <summary>Tab ids, in the order the page shows them (wire contract P1).</summary>
    public const string AllTab = "all";
    public const string ScreenTab = "screen";
    public const string HookTab = "hook";
    public const string SessionStartTab = "session_start";
    public const string FiredTodayTab = "fired_today";
    public const string ChangedByAgentsTab = "changed_by_agents";

    /// <summary>Tones the client maps to a style token, and nothing more.</summary>
    public const string NeutralTone = "neutral";
    public const string LiveTone = "live";
    public const string WarnTone = "warn";
    public const string DenyTone = "deny";
    public const string AllowTone = "allow";

    /// <summary>How much of a screen a screen firing's subject keeps: the bottom of the screen, where the
    /// session stopped.</summary>
    public const int ScreenSubjectMaxLength = 1000;

    /// <summary>What a tab says when no rule is on it.</summary>
    public const string AllEmptyText = "No rules yet. An agent or a person creates one with 'cc-devthrottle rules create'.";
    public const string ScreenEmptyText = "No screen rules. A screen rule reads an idle session's screen and acts on what it finds.";
    public const string HookEmptyText = "No hook rules. A hook rule decides before an agent runs a shell command.";
    public const string SessionStartEmptyText = "No session start rules. Rules that act when a session starts are not built yet.";
    public const string FiredTodayEmptyText = "No rule has fired today.";
    public const string ChangedByAgentsEmptyText = "No agent session has changed a rule.";
    public const string FiringsEmptyText = "This rule has not fired yet.";
    public const string ChangesEmptyText = "No changes recorded.";
    public const string NeverFired = "Never fired";

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <summary>
    /// How far back the tally reads firing times. The viewer's "today" began at most 25 hours ago in elapsed time
    /// (a day with a daylight saving change back is 25 hours long), whatever the zone, so 26 hours always holds it.
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(26);

    /// <summary>The earliest firing time the tally must read for "today" to be right in any zone at
    /// <paramref name="nowUtc"/>.</summary>
    public static DateTime RecentWindowStartUtc(DateTime nowUtc)
    {
        var start = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) - RecentWindow;
        FileLog.Write($"[RulesPageFold] RecentWindowStartUtc: now={nowUtc:O} start={start:O}");
        return start;
    }

    /// <summary>
    /// The viewer's time zone, from the IANA name the client sent (ruling T3).
    /// </summary>
    /// <exception cref="RuleRejectedException">No zone was sent, or this Gateway does not know it.</exception>
    public static TimeZoneInfo ReadZone(string? tz)
    {
        FileLog.Write($"[RulesPageFold] ReadZone: tz={tz}");
        if (string.IsNullOrWhiteSpace(tz))
            throw new RuleRejectedException(
                "say which time zone the page is read in: tz=<IANA zone>, for example tz=Europe/Copenhagen. " +
                "\"Today\" is the viewer's day, so it is never assumed.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz.Trim(), out var zone))
            throw new RuleRejectedException(
                $"'{tz}' is not a time zone this Gateway knows. Send an IANA zone such as Europe/Copenhagen.");
        FileLog.Write($"[RulesPageFold] ReadZone: resolved {zone.Id}");
        return zone;
    }

    /// <summary>The Rules page (wire contract P1): the tabs with their counts, and every rule newest first.</summary>
    public static RulesPage Page(RulesPageSource source, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zone);
        FileLog.Write($"[RulesPageFold] Page: rules={source.Rules.Count} zone={zone.Id}");

        var today = DayOf(nowUtc, zone);
        var rules = source.Rules
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => ListItem(source, r, zone, today))
            .ToList();
        var tabs = new[]
        {
            Tab(AllTab, "All", AllEmptyText),
            Tab(ScreenTab, "Screen", ScreenEmptyText),
            Tab(HookTab, "Hook", HookEmptyText),
            Tab(SessionStartTab, "Session start", SessionStartEmptyText),
            Tab(FiredTodayTab, "Fired today", FiredTodayEmptyText),
            Tab(ChangedByAgentsTab, "Changed by agents", ChangedByAgentsEmptyText),
        };
        var page = new RulesPage(today.ToString("dddd d MMMM", English), tabs, rules, Suggestions(source));
        FileLog.Write(
            $"[RulesPageFold] Page: today={page.Today} counts={string.Join(",", tabs.Select(t => t.Id + "=" + t.Count))}");
        return page;

        RulesPageTab Tab(string id, string label, string emptyText) =>
            new(id, label, rules.Count(r => r.Tabs.Contains(id)), emptyText);
    }

    /// <summary>
    /// One rule's page (wire contract P2), or null when the account has no rule with this id. Its firing record is
    /// <paramref name="firings"/>, that rule's complete record, newest first.
    /// </summary>
    public static RulePage? RulePageFor(
        RulesPageSource source, Guid ruleId, IReadOnlyList<SessionRuleFiring> firings, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(firings);
        ArgumentNullException.ThrowIfNull(zone);
        FileLog.Write($"[RulesPageFold] RulePageFor: rule={ruleId} zone={zone.Id}");

        var rule = source.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
        {
            FileLog.Write($"[RulesPageFold] RulePageFor: no rule {ruleId}");
            return null;
        }

        var today = DayOf(nowUtc, zone);
        var item = ListItem(source, rule, zone, today);
        var fields = new List<RulePageField>
        {
            new("Sentence", rule.Instruction),
            new("Kind", item.KindLabel + " - " + LowerFirst(item.TriggerLabel)),
            new("Scope", item.ScopeLabel + " (" + SpecificityOf(rule.AppliesTo.Kind) + ")"),
            new("Mode", ModeDescription(rule)),
            new("Handler", HandlerDescription(rule)),
            new("Promoted by", string.IsNullOrWhiteSpace(rule.PromotedBy) ? "Not promoted" : rule.PromotedBy),
            new("Created", WhenLabel(rule.CreatedUtc, zone, today)),
        };
        var record = firings
            .Where(f => f.RuleId == rule.Id)
            .OrderByDescending(f => f.OccurredUtc)
            .Select(f => Firing(source, f, zone, today))
            .ToList();
        var changes = source.Changes
            .Where(c => c.RuleId == rule.Id)
            .Select(c => new RulePageChange(WhenLabel(c.OccurredUtc, zone, today), ActionLabel(c.Action), ByLabel(c)))
            .ToList();

        FileLog.Write($"[RulesPageFold] RulePageFor: rule={ruleId} firings={record.Count} changes={changes.Count}");
        return new RulePage(item, fields, record, FiringsEmptyText, changes, ChangesEmptyText);
    }

    /// <summary>
    /// The preview of one machine and repository (wire contract P3): every rule that reaches them, most specific
    /// first, which rule always overrides which (T7), and - when a sample command is given - what the hook would
    /// decide for it, decided by the hook's own resolver (T6).
    /// </summary>
    /// <exception cref="RuleRejectedException">No machine was named.</exception>
    public static RulesPreview Preview(RulesPageSource source, string? machine, string? repo, string? command)
    {
        ArgumentNullException.ThrowIfNull(source);
        FileLog.Write($"[RulesPageFold] Preview: machine={machine} repo={repo} command={(string.IsNullOrEmpty(command) ? "none" : "given")}");
        if (string.IsNullOrWhiteSpace(machine))
            throw new RuleRejectedException("the preview is worked out for one machine - say which: machine=<name>.");
        var theMachine = machine.Trim();
        var theRepo = GitRemoteIdentity.Normalise(repo);
        var where = theRepo.Length > 0 ? $"{theMachine} in {theRepo}" : $"{theMachine} with no repository";

        // The rules in the order the Director writes them into a session's rules file (wire contract W1): newest
        // first, then most specific first, keeping that order within a rank. The deciding rule is the first winner
        // in file order, so the preview has to read them in the same order to name the same one.
        var reaching = source.Rules
            .OrderByDescending(r => r.CreatedUtc)
            .Where(r => r.AppliesTo.Reaches(theMachine, theRepo))
            .OrderByDescending(r => r.AppliesTo.Rank)
            .ToList();
        var hooks = reaching.Where(r => r.Kind == RuleKinds.Hook && r.Hook is not null).ToList();

        var items = reaching.Select(r => PreviewItem(r, hooks)).ToList();
        var headline = reaching.Count == 1
            ? $"1 rule reaches {where}."
            : $"{reaching.Count} rules reach {where}.";
        var decision = string.IsNullOrEmpty(command) ? null : Decide(hooks, command, where);

        var preview = new RulesPreview(
            theMachine, theRepo, headline, items, $"No rule reaches {where}.", decision, Suggestions(source));
        FileLog.Write(
            $"[RulesPageFold] Preview: machine={theMachine} repo={theRepo} rules={items.Count} " +
            $"decision={decision?.VerdictLabel ?? "none"}");
        return preview;
    }

    /// <summary>
    /// Whether hook rule <paramref name="a"/> always overrides hook rule <paramref name="b"/> (ruling T7): the same
    /// event and tool, different results, A wins the rank contest of ruling P3, and some match text of A contains
    /// some match text of B. Screen rules never override and are never overridden.
    /// </summary>
    internal static bool Overrides(SessionRule a, SessionRule b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Id == b.Id || a.Kind != RuleKinds.Hook || b.Kind != RuleKinds.Hook) return false;
        if (a.Hook is not { } ha || b.Hook is not { } hb) return false;
        if (!string.Equals(ha.Event, hb.Event, StringComparison.Ordinal)
            || !string.Equals(ha.Tool, hb.Tool, StringComparison.Ordinal)
            || string.Equals(ha.Result, hb.Result, StringComparison.Ordinal))
            return false;
        var winsRank = a.AppliesTo.Rank > b.AppliesTo.Rank
            || (a.AppliesTo.Rank == b.AppliesTo.Rank && ha.Result == HookRuleWords.Deny && hb.Result == HookRuleWords.Allow);
        return winsRank && ha.Match.Any(textA => hb.Match.Any(textB => textA.Contains(textB, StringComparison.Ordinal)));
    }

    // ---- the list item ---------------------------------------------------------------------------------------

    private static RulesPageRule ListItem(RulesPageSource source, SessionRule rule, TimeZoneInfo zone, DateOnly today)
    {
        var tally = source.Tallies.TryGetValue(rule.Id, out var t) ? t : null;
        var firedToday = tally?.RecentUtc.Count(at => DayOf(at, zone) == today) ?? 0;
        var lastBySession = source.Changes.FirstOrDefault(c => c.RuleId == rule.Id && !string.IsNullOrWhiteSpace(c.SessionId));

        var tabs = new List<string> { AllTab, KindTab(rule.Kind) };
        if (firedToday > 0) tabs.Add(FiredTodayTab);
        if (lastBySession is not null) tabs.Add(ChangedByAgentsTab);

        return new RulesPageRule(
            rule.Id,
            rule.Instruction,
            KindLabel(rule.Kind),
            TriggerLabel(rule),
            ScopeLabel(rule.AppliesTo),
            ModeLabel(rule.State),
            rule.State == RuleState.Live ? LiveTone : NeutralTone,
            HandlerLabel(rule),
            FiredLabel(tally, firedToday, zone, today),
            lastBySession is null
                ? ""
                : $"{ActionLabel(lastBySession.Action)} by {SessionLabel(lastBySession.SessionId!, lastBySession.Agent)} " +
                  AtLabel(lastBySession.OccurredUtc, zone, today),
            tabs);
    }

    private static string FiredLabel(RuleFiringTally? tally, int firedToday, TimeZoneInfo zone, DateOnly today)
    {
        if (tally is null || tally.Total == 0) return NeverFired;
        return firedToday > 0
            ? $"Fired {Times(firedToday)} today, last {AtLabel(tally.NewestUtc, zone, today)}"
            : $"Fired {Times(tally.Total)}, last {AtLabel(tally.NewestUtc, zone, today)}";
    }

    private static string Times(int count) => count == 1 ? "1 time" : $"{count} times";

    private static string KindTab(string kind) => kind switch
    {
        RuleKinds.Screen => ScreenTab,
        RuleKinds.Hook => HookTab,
        RuleKinds.SessionStart => SessionStartTab,
        _ => throw new InvalidOperationException($"'{kind}' is not a kind of rule this page can show."),
    };

    private static string KindLabel(string kind) => kind switch
    {
        RuleKinds.Screen => "Screen",
        RuleKinds.Hook => "Hook",
        RuleKinds.SessionStart => "Session start",
        _ => throw new InvalidOperationException($"'{kind}' is not a kind of rule this page can show."),
    };

    private static string TriggerLabel(SessionRule rule) => rule.Kind switch
    {
        RuleKinds.Screen => "When a session goes idle",
        RuleKinds.SessionStart => "When a session starts",
        RuleKinds.Hook => HookTriggerLabel(rule.Hook
            ?? throw new InvalidOperationException($"hook rule {rule.Id} has no trigger.")),
        _ => throw new InvalidOperationException($"'{rule.Kind}' is not a kind of rule this page can show."),
    };

    private static string HookTriggerLabel(HookRuleTrigger hook) => (hook.Event, hook.Tool) switch
    {
        (HookRuleWords.BeforeCommand, HookRuleWords.Shell) => "Before a shell command",
        _ => throw new InvalidOperationException(
            $"the hook event '{hook.Event}' on the tool '{hook.Tool}' has no words on this page yet."),
    };

    private static string ScopeLabel(RuleAppliesTo appliesTo) => appliesTo.Kind switch
    {
        RuleAppliesToKinds.Everywhere => "Everywhere",
        RuleAppliesToKinds.Machine => appliesTo.Machines.Count > 0
            ? "On " + JoinAnd(appliesTo.Machines)
            : "On every machine except " + JoinAnd(appliesTo.ExceptMachines),
        RuleAppliesToKinds.Repo => "In " + appliesTo.Repo,
        RuleAppliesToKinds.RepoOnMachine => "In " + appliesTo.Repo + " on " + JoinAnd(appliesTo.Machines),
        _ => throw new InvalidOperationException($"'{appliesTo.Kind}' is not a place a rule can apply."),
    };

    private static string PlaceName(string appliesToKind) => appliesToKind switch
    {
        RuleAppliesToKinds.Everywhere => "everywhere",
        RuleAppliesToKinds.Machine => "machine",
        RuleAppliesToKinds.Repo => "repo",
        RuleAppliesToKinds.RepoOnMachine => "repo on machine",
        _ => throw new InvalidOperationException($"'{appliesToKind}' is not a place a rule can apply."),
    };

    private static string SpecificityOf(string appliesToKind)
    {
        var rank = RuleAppliesToKinds.RankOf(appliesToKind);
        return rank switch
        {
            4 => "most specific: " + PlaceName(appliesToKind),
            1 => "least specific: " + PlaceName(appliesToKind),
            _ => $"{PlaceName(appliesToKind)}, rank {rank} of 4",
        };
    }

    private static string RankLabel(string appliesToKind) =>
        UpperFirst(PlaceName(appliesToKind)) + $" (rank {RuleAppliesToKinds.RankOf(appliesToKind)})";

    private static string ModeLabel(RuleState state) => state switch
    {
        RuleState.DryRun => "Dry run",
        RuleState.Live => "Live",
        _ => throw new InvalidOperationException($"'{state}' is not a rule state this page can show."),
    };

    private static string ModeDescription(SessionRule rule) => (rule.State, rule.Kind) switch
    {
        (RuleState.DryRun, RuleKinds.Screen) => "Dry run - records what it would have done; nothing is typed",
        (RuleState.DryRun, _) => "Dry run - records what it would have done; nothing is blocked",
        (RuleState.Live, RuleKinds.Screen) => "Live - types into the session when the rule applies",
        (RuleState.Live, _) => "Live - carries out what it decides",
        _ => throw new InvalidOperationException($"'{rule.State}' is not a rule state this page can show."),
    };

    private static string HandlerLabel(SessionRule rule) => rule.Kind switch
    {
        RuleKinds.Hook => $"Match {QuotedOr(Trigger(rule).Match)} - {Trigger(rule).Result}",
        RuleKinds.Screen => "Reads the screen: " + rule.ScreenDescription,
        RuleKinds.SessionStart => "Not built yet",
        _ => throw new InvalidOperationException($"'{rule.Kind}' is not a kind of rule this page can show."),
    };

    private static string HandlerDescription(SessionRule rule) => rule.Kind switch
    {
        RuleKinds.Hook => $"Match: the command contains {QuotedOr(Trigger(rule).Match)}. Result: {Trigger(rule).Result}",
        RuleKinds.Screen => $"Reads the screen for: {rule.ScreenDescription}. " + (rule.Calls.Count == 0
            ? "Checks: none"
            : "Checks: " + string.Join("; ", rule.Calls.Select(c => c.Describe()))),
        RuleKinds.SessionStart => "Session start rules are not built yet",
        _ => throw new InvalidOperationException($"'{rule.Kind}' is not a kind of rule this page can show."),
    };

    private static HookRuleTrigger Trigger(SessionRule rule) =>
        rule.Hook ?? throw new InvalidOperationException($"hook rule {rule.Id} has no trigger.");

    private static string ResultLabel(SessionRule rule) => rule.Kind switch
    {
        RuleKinds.Hook => UpperFirst(Trigger(rule).Result),
        RuleKinds.Screen => "Acts on the screen",
        RuleKinds.SessionStart => "Not built yet",
        _ => throw new InvalidOperationException($"'{rule.Kind}' is not a kind of rule this page can show."),
    };

    // ---- the firing record -----------------------------------------------------------------------------------

    private static RulePageFiring Firing(RulesPageSource source, SessionRuleFiring firing, TimeZoneInfo zone, DateOnly today)
    {
        var isHook = firing.Kind == RuleKinds.Hook;
        var (decisionLabel, tone) = DecisionOf(firing);
        var overrode = firing.OverriddenRuleIds.Select(id => Reference(source, id)).ToList();
        return new RulePageFiring(
            firing.Id,
            WhenLabel(firing.OccurredUtc, zone, today),
            decisionLabel,
            tone,
            SessionLabel(firing.SessionId, firing.Agent),
            isHook
                ? firing.Machine + " - " + (firing.Repo.Length > 0 ? firing.Repo : "no repository")
                : "Read from the session's screen",
            isHook ? firing.Subject : BottomOf(firing.ScreenText),
            isHook ? OverrodeLabel(overrode.Count) : "",
            overrode,
            UpperFirst(firing.Outcome));
    }

    private static (string Label, string Tone) DecisionOf(SessionRuleFiring firing) => firing.Decision switch
    {
        HookRuleWords.WouldDeny => ("Would have denied", DenyTone),
        HookRuleWords.WouldAllow => ("Would have allowed", AllowTone),
        RuleDecisions.Act => ActDecisionOf(firing),
        RuleDecisions.Decline => ("Declined - the rule did not apply", NeutralTone),
        RuleDecisions.Abandoned => ("Gave up before typing", WarnTone),
        RuleDecisions.Refused => ("Refused an unusable answer", WarnTone),
        _ => throw new InvalidOperationException($"firing {firing.Id} has a decision '{firing.Decision}' this page cannot name."),
    };

    /// <summary>
    /// What became of an "act" firing, read from how the evaluator began its outcome (<see cref="RuleActOutcomes"/>),
    /// never from the typed text alone: a live rule whose keystroke did not land also typed nothing, and calling that
    /// "would have acted" would tell a person a live firing was a rehearsal.
    /// </summary>
    private static (string Label, string Tone) ActDecisionOf(SessionRuleFiring firing)
    {
        var outcome = firing.Outcome ?? "";
        if (outcome.StartsWith(RuleActOutcomes.DryRunPrefix, StringComparison.Ordinal)) return ("Would have acted", NeutralTone);
        if (outcome.StartsWith(RuleActOutcomes.TypedPrefix, StringComparison.Ordinal)) return ("Typed into the session", LiveTone);
        if (outcome.StartsWith(RuleActOutcomes.NotTypedPrefix, StringComparison.Ordinal)) return ("Tried to type - nothing was sent", WarnTone);
        if (outcome.StartsWith(RuleActOutcomes.SentUnconfirmedPrefix, StringComparison.Ordinal)) return ("Sent - nothing confirmed it landed", WarnTone);
        if (outcome.StartsWith(RuleActOutcomes.AboutToTypePrefix, StringComparison.Ordinal)) return ("Started to type - never completed", WarnTone);
        throw new InvalidOperationException($"firing {firing.Id} acted with an outcome this page cannot name: '{outcome}'.");
    }

    private static string OverrodeLabel(int count) => count switch
    {
        0 => "Overrode no rule",
        1 => "Overrode 1 rule",
        _ => $"Overrode {count} rules",
    };

    private static RuleReference Reference(RulesPageSource source, Guid id)
    {
        var rule = source.Rules.FirstOrDefault(r => r.Id == id);
        return new RuleReference(id, rule?.Instruction ?? $"(deleted rule {id})");
    }

    private static string BottomOf(string screen) =>
        screen.Length <= ScreenSubjectMaxLength ? screen : "..." + screen[^ScreenSubjectMaxLength..];

    /// <summary>"session &lt;id&gt; (&lt;agent&gt;)", in lower case as the wire contract writes it (P1 amendment).</summary>
    private static string SessionLabel(string sessionId, string? agent) =>
        string.IsNullOrWhiteSpace(agent) ? $"session {sessionId}" : $"session {sessionId} ({agent})";

    private static string ActionLabel(string action) => action switch
    {
        RuleChangeActions.Create => "Created",
        RuleChangeActions.Change => "Changed",
        RuleChangeActions.Promote => "Promoted",
        RuleChangeActions.Delete => "Deleted",
        _ => throw new InvalidOperationException($"'{action}' is not a change a rule can have."),
    };

    private static string ByLabel(SessionRuleChange change) =>
        string.IsNullOrWhiteSpace(change.SessionId) ? change.ChangedBy : SessionLabel(change.SessionId, change.Agent);

    // ---- the preview -----------------------------------------------------------------------------------------

    private static RulesPreviewRule PreviewItem(SessionRule rule, IReadOnlyList<SessionRule> hooks)
    {
        var overrides = hooks.Where(other => Overrides(rule, other)).Select(r => new RuleReference(r.Id, r.Instruction)).ToList();
        var overriddenBy = hooks.Where(other => Overrides(other, rule)).Select(r => new RuleReference(r.Id, r.Instruction)).ToList();
        return new RulesPreviewRule(
            rule.Id,
            rule.Instruction,
            KindLabel(rule.Kind),
            ScopeLabel(rule.AppliesTo),
            RankLabel(rule.AppliesTo.Kind),
            ResultLabel(rule),
            ModeLabel(rule.State),
            overrides,
            overriddenBy,
            VerdictLabel(rule, overrides, overriddenBy));
    }

    private static string VerdictLabel(SessionRule rule, IReadOnlyList<RuleReference> overrides, IReadOnlyList<RuleReference> overriddenBy)
    {
        if (rule.Kind != RuleKinds.Hook)
            return "Never overrides a hook rule and is never overridden by one.";
        if (overrides.Count == 0 && overriddenBy.Count == 0)
            return "Overrides no rule, and no rule overrides it.";
        var parts = new List<string>();
        if (overrides.Count > 0)
            parts.Add($"Overrides {RulesCount(overrides.Count)}: {QuotedList(overrides)}");
        if (overriddenBy.Count > 0)
            parts.Add($"Overridden by {RulesCount(overriddenBy.Count)}: {QuotedList(overriddenBy)}");
        return string.Join(". ", parts);
    }

    private static string RulesCount(int count) => count == 1 ? "1 rule" : $"{count} rules";

    private static string QuotedList(IReadOnlyList<RuleReference> rules) =>
        string.Join(", ", rules.Select(r => "\"" + r.Sentence + "\""));

    /// <summary>The hook's decision for one command, made by the hook's own resolver over the rules as the
    /// Director would write them into a session's rules file (rulings T6, P3, P13).</summary>
    private static RulesPreviewDecision Decide(IReadOnlyList<SessionRule> hooks, string command, string where)
    {
        var file = new RulesFile
        {
            Version = 1,
            Rules = hooks.Select(r => new HookRule
            {
                Id = r.Id.ToString(),
                Rank = r.AppliesTo.Rank,
                AppliesTo = r.AppliesTo.Kind,
                Event = r.Hook!.Event,
                Tool = r.Hook.Tool,
                Match = r.Hook.Match.ToList(),
                Result = r.Hook.Result,
                Reason = r.Instruction,
                State = RuleWireNames.ToWireName(r.State.ToString()),
            }).ToList(),
        };
        var resolution = RuleResolver.Decide(RuleResolver.Validate(file), command);
        if (resolution.DecidingRule is null)
            return new RulesPreviewDecision(
                command,
                "No rule matched this command",
                NeutralTone,
                $"No hook rule that reaches {where} matches this command, so the hook records nothing.",
                "",
                Array.Empty<string>(),
                Array.Empty<string>());

        var byId = hooks.ToDictionary(r => r.Id.ToString(), r => r);
        string Named(string id) => $"\"{byId[id].Instruction}\" ({PlaceName(byId[id].AppliesTo.Kind)})";
        var deciding = resolution.DecidingRule.Id!;
        var overrode = resolution.OverriddenRuleIds.Count == 0
            ? "It overrode no rule."
            : "It overrode " + JoinAnd(resolution.OverriddenRuleIds.Select(Named).ToList()) + ".";
        var deny = resolution.Decision == RuleResolver.WouldDeny;
        return new RulesPreviewDecision(
            command,
            deny ? "Would deny" : "Would allow",
            deny ? DenyTone : AllowTone,
            $"{Named(deciding)} decides. {overrode}",
            deciding,
            resolution.OverriddenRuleIds,
            resolution.MatchedRuleIds);
    }

    private static RulesPreviewSuggestions Suggestions(RulesPageSource source)
    {
        var machines = source.Rules
            .SelectMany(r => r.AppliesTo.Machines.Concat(r.AppliesTo.ExceptMachines))
            .Concat(source.FiringPlaces.Select(f => f.Machine))
            .Select(m => (m ?? "").Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var repos = source.Rules
            .Select(r => r.AppliesTo.Repo)
            .Concat(source.FiringPlaces.Select(f => f.Repo))
            .Select(GitRemoteIdentity.Normalise)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        return new RulesPreviewSuggestions(machines, repos);
    }

    // ---- days, times and words -------------------------------------------------------------------------------

    private static DateOnly DayOf(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>"Today at 10:42", "Yesterday at 10:42", "18 October at 14:02", or with the year when it is not
    /// this year.</summary>
    private static string WhenLabel(DateTime utc, TimeZoneInfo zone, DateOnly today)
    {
        var local = Local(utc, zone);
        var day = DateOnly.FromDateTime(local);
        var time = local.ToString("HH:mm", English);
        if (day == today) return $"Today at {time}";
        if (day == today.AddDays(-1)) return $"Yesterday at {time}";
        var date = day.Year == today.Year ? local.ToString("d MMMM", English) : local.ToString("d MMMM yyyy", English);
        return $"{date} at {time}";
    }

    /// <summary>"at 10:42" today, "yesterday at 10:42", otherwise "on 18 October at 14:02" - to follow a verb.</summary>
    private static string AtLabel(DateTime utc, TimeZoneInfo zone, DateOnly today)
    {
        var when = WhenLabel(utc, zone, today);
        if (when.StartsWith("Today ", StringComparison.Ordinal)) return when["Today ".Length..];
        if (when.StartsWith("Yesterday ", StringComparison.Ordinal)) return "yesterday " + when["Yesterday ".Length..];
        return "on " + when;
    }

    private static string QuotedOr(IReadOnlyList<string> texts) =>
        string.Join(" or ", texts.Select(t => "\"" + t + "\""));

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string UpperFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
