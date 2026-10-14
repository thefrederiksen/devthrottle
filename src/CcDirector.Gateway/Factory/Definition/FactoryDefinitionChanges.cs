using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// WHAT CHANGES between two versions of a factory's definition (Factory Definition, phase 1; mission goal 3 and
/// section 5.3). One pure function: the published content (or none, before the first publish) and the content being
/// compared go in, and a <see cref="FactoryDefinitionChangesDto"/> comes out whose every sentence is composed here,
/// so the command line and the Cockpit render it verbatim (rule 7).
///
/// Seats are matched by their id, so a seat whose display name changes is a change to that seat, never a removal and
/// an addition. Starts are matched by what they are: a start written the same in both is unchanged wherever it sits
/// in the list; the rest are paired by closeness within one kind (time or interval), and an unpaired one is created
/// or switched off. Lists a
/// person writes as a set (a seat's grants, rule sets, secrets, skills and files; a grant's seats; the repositories;
/// a rule set's rules) are compared as sets, so reordering one changes nothing it defines - the text difference is
/// still reported, as a change of layout only.
///
/// Deterministic: everything is ordered by seat id, rule set name, grant id or path (ordinal), whatever the order of
/// the content maps.
/// </summary>
public static class FactoryDefinitionChanges
{
    /// <summary>The longest a value is shown inside a sentence before it is cut.</summary>
    public const int MaxValueChars = 120;

    /// <summary>
    /// What changes from <paramref name="published"/> (null when nothing is published yet) to
    /// <paramref name="compared"/>. Both must parse (a saved version always does); one that does not is refused with
    /// its first problem, because what it defines cannot be read.
    /// </summary>
    public static FactoryDefinitionChangesDto Compare(
        IReadOnlyDictionary<string, string>? published,
        IReadOnlyDictionary<string, string> compared)
    {
        ArgumentNullException.ThrowIfNull(compared);
        FileLog.Write($"[FactoryDefinitionChanges] Compare: published={(published is null ? "none" : published.Count + " files")}, compared={compared.Count} files");

        var before = published is null ? null : ReadOrThrow(published, "published");
        var after = ReadOrThrow(compared, "compared");

        var dto = new FactoryDefinitionChangesDto { FirstPublish = before is null };
        CompareFactory(before, after, dto);
        CompareSeats(before, after, dto);
        CompareRules(before, after, dto);
        CompareGrants(before, after, dto);
        CompareFiles(published ?? new Dictionary<string, string>(), compared, dto);

        dto.NothingChanges = dto.Factory.Count == 0 && dto.Seats.Count == 0 && dto.Starts.Count == 0
                             && dto.Rules.Count == 0 && dto.Grants.Count == 0 && dto.Files.Count == 0;
        dto.Summary = Summarise(dto);
        FileLog.Write($"[FactoryDefinitionChanges] Compare: {dto.Summary}");
        return dto;
    }

    private static FactoryDefinition ReadOrThrow(IReadOnlyDictionary<string, string> content, string which)
    {
        var result = FactoryDefinitionCheck.Read(content);
        if (result.Definition is not null) return result.Definition;
        var first = result.Findings.First(f => f.IsError);
        FileLog.Write($"[FactoryDefinitionChanges] Compare FAILED: the {which} definition does not parse: {first.File}:{first.Line} {first.Message}");
        throw new ArgumentException(
            $"The {which} definition does not parse, so what changes cannot be shown: {first.File}:{first.Line} {first.Message}");
    }

    // -------------------------------------------------------------------------------------------------------------
    // factory.yaml
    // -------------------------------------------------------------------------------------------------------------

    private static void CompareFactory(FactoryDefinition? before, FactoryDefinition after, FactoryDefinitionChangesDto dto)
    {
        void Field(string field, string? oldValue, string? newValue)
        {
            if (before is null && newValue is null) return;
            if (before is not null && string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
            dto.Factory.Add(new FactoryDefinitionFieldChangeDto { Field = field, Sentence = ValueChange(field, before is null, oldValue, newValue) });
        }

        Field("factory", before?.Factory, after.Factory);
        Field("title", before?.Title, after.Title);
        Field("purpose", before?.Purpose, after.Purpose);
        if (!string.Equals(before?.GoalText, after.GoalText, StringComparison.Ordinal))
        {
            var diff = DefinitionTextDiff.Compare(before?.GoalText, after.GoalText);
            dto.Factory.Add(new FactoryDefinitionFieldChangeDto
            {
                Field = "goalText",
                Sentence = before?.GoalText is null ? $"goal text set: {Lines(diff.Added)}"
                    : after.GoalText is null ? $"goal text removed: {Lines(diff.Removed)}"
                    : $"goal text changed: {AddedRemoved(diff)}",
                Diff = diff,
            });
        }
        Field("goalApprovedOn", before?.GoalApprovedOn, after.GoalApprovedOn);
        Field("bossSeat", before?.BossSeat, after.BossSeat);
        Field("defaults.machine", before?.Defaults.Machine, after.Defaults.Machine);
        Field("defaults.agent", before?.Defaults.Agent, after.Defaults.Agent);
        Field("defaults.model", before?.Defaults.Model, after.Defaults.Model);
        foreach (var line in SetChange("repository", before?.Repositories ?? Array.Empty<string>(), after.Repositories))
            dto.Factory.Add(new FactoryDefinitionFieldChangeDto { Field = "repositories", Sentence = line });
    }

    // -------------------------------------------------------------------------------------------------------------
    // Seats and their starts
    // -------------------------------------------------------------------------------------------------------------

    private static void CompareSeats(FactoryDefinition? before, FactoryDefinition after, FactoryDefinitionChangesDto dto)
    {
        var oldSeats = (before?.Seats ?? Array.Empty<SeatDefinition>()).ToDictionary(s => s.Seat, StringComparer.Ordinal);
        var newSeats = after.Seats.ToDictionary(s => s.Seat, StringComparer.Ordinal);
        foreach (var id in oldSeats.Keys.Union(newSeats.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            oldSeats.TryGetValue(id, out var o);
            newSeats.TryGetValue(id, out var n);
            if (n is null)
            {
                dto.Seats.Add(new FactoryDefinitionSeatChangeDto { Seat = id, Change = "removed", Sentence = $"Seat {Label(o!)} removed." });
                foreach (var start in o!.Starts)
                    dto.Starts.Add(StartEntry(o, "switched off", $"{o.Name}: start switched off, {When(start)}.", new List<string>()));
                continue;
            }

            var details = SeatDetails(o, before?.Defaults, n, after.Defaults);
            FactoryDefinitionTextDiffDto? briefDiff = null;
            if (!string.Equals(o?.Brief, n.Brief, StringComparison.Ordinal))
            {
                briefDiff = DefinitionTextDiff.Compare(o?.Brief, n.Brief);
                details.Add(o is null ? $"brief: {Lines(briefDiff.Added)}" : $"brief changed: {AddedRemoved(briefDiff)}");
            }
            if (o is null)
                dto.Seats.Add(new FactoryDefinitionSeatChangeDto { Seat = id, Change = "added", Sentence = $"Seat {Label(n)} added.", Details = details, BriefDiff = briefDiff });
            else if (details.Count > 0)
                dto.Seats.Add(new FactoryDefinitionSeatChangeDto { Seat = id, Change = "changed", Sentence = $"Seat {Label(n)} changed.", Details = details, BriefDiff = briefDiff });

            CompareStarts(o?.Starts ?? Array.Empty<SeatStartDefinition>(), n, dto);
        }
    }

    private static List<string> SeatDetails(SeatDefinition? o, FactoryDefinitionDefaults? oldDefaults, SeatDefinition n, FactoryDefinitionDefaults newDefaults)
    {
        var added = o is null;
        var details = new List<string>();
        void Field(string field, string? oldValue, string? newValue)
        {
            if (added && newValue is null) return;
            if (!added && string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
            details.Add(ValueChange(field, added, oldValue, newValue));
        }

        if (!added) Field("name", o!.Name, n.Name);
        Field("role", o?.Role, n.Role);
        Field("line", o?.Line, n.Line);
        // Where and how the seat runs, as it would run: a seat naming nothing runs on the factory's default.
        Field("machine", Effective(o?.Machine, oldDefaults?.Machine), Effective(n.Machine, newDefaults.Machine));
        Field("agent", Effective(o?.Agent, oldDefaults?.Agent), Effective(n.Agent, newDefaults.Agent));
        Field("model", Effective(o?.Model, oldDefaults?.Model), Effective(n.Model, newDefaults.Model));
        Field("repository", o?.Repository, n.Repository);
        details.AddRange(SetChange("grant", o?.Grants ?? Array.Empty<string>(), n.Grants));
        details.AddRange(SetChange("rule set", o?.Rules ?? Array.Empty<string>(), n.Rules));
        details.AddRange(SetChange("secret", o?.Secrets ?? Array.Empty<string>(), n.Secrets));
        details.AddRange(SetChange("factory skill", o?.FactorySkills ?? Array.Empty<string>(), n.FactorySkills));
        details.AddRange(SetChange("fleet skill", o?.FleetSkills ?? Array.Empty<string>(), n.FleetSkills));
        details.AddRange(SetChange("file", o?.Files ?? Array.Empty<string>(), n.Files));
        return details;
    }

    // A seat's own value, or "<default> (the factory's default)" when it names none.
    private static string? Effective(string? own, string? factoryDefault)
        => own ?? (factoryDefault is null ? null : $"{factoryDefault} (the factory's default)");

    private static void CompareStarts(IReadOnlyList<SeatStartDefinition> oldStarts, SeatDefinition seat, FactoryDefinitionChangesDto dto)
    {
        // A start written the same in both versions is unchanged wherever it sits.
        var removed = oldStarts.ToList();
        var added = new List<SeatStartDefinition>();
        foreach (var start in seat.Starts)
        {
            var same = removed.FindIndex(s => SameStart(s, start));
            if (same >= 0) removed.RemoveAt(same);
            else added.Add(start);
        }

        // The rest are paired by closeness, never by position, so where a person wrote a start in the list never
        // changes the account. Both sides are first put in one fixed order (what the start is, not where it sits);
        // then each published start takes the closest unpaired start of the SAME kind - a time with a time, an
        // interval with an interval - preferring the same prompt, then the same check, then the same time zone.
        // A start whose kind changes is honestly a start switched off and another created, never "moved".
        removed.Sort(CompareStartsCanonically);
        added.Sort(CompareStartsCanonically);
        var pairs = new List<(SeatStartDefinition Old, SeatStartDefinition New)>();
        var switchedOff = new List<SeatStartDefinition>();
        foreach (var o in removed)
        {
            var best = -1;
            var bestScore = -1;
            for (var i = 0; i < added.Count; i++)
            {
                if ((added[i].Cron is null) != (o.Cron is null)) continue;
                var score = (added[i].Prompt == o.Prompt ? 4 : 0) + (added[i].Check == o.Check ? 2 : 0) + (added[i].TimeZone == o.TimeZone ? 1 : 0);
                if (score > bestScore) { best = i; bestScore = score; }
            }
            if (best < 0) { switchedOff.Add(o); continue; }
            pairs.Add((o, added[best]));
            added.RemoveAt(best);
        }

        foreach (var (o, n) in pairs)
        {
            var details = StartDetails(o, n);
            var oldWhen = When(o);
            var newWhen = When(n);
            dto.Starts.Add(oldWhen != newWhen
                ? StartEntry(seat, "moved", $"{seat.Name}: start moved, {oldWhen} -> {newWhen}.", details)
                : StartEntry(seat, "changed", $"{seat.Name}: start {newWhen} changed.", details));
        }
        foreach (var o in switchedOff)
            dto.Starts.Add(StartEntry(seat, "switched off", $"{seat.Name}: start switched off, {When(o)}.", new List<string>()));
        foreach (var n in added)
            dto.Starts.Add(StartEntry(seat, "created", $"{seat.Name}: start created, {When(n)}.", StartDetails(null, n)));
    }

    // One fixed order for starts, from what they are: kind, when, then every other part.
    private static int CompareStartsCanonically(SeatStartDefinition a, SeatStartDefinition b)
    {
        int c;
        if ((c = (a.Cron is null).CompareTo(b.Cron is null)) != 0) return c;
        if ((c = string.CompareOrdinal(a.Cron, b.Cron)) != 0) return c;
        if ((c = string.CompareOrdinal(a.TimeZone, b.TimeZone)) != 0) return c;
        if ((c = Nullable.Compare(a.EverySeconds, b.EverySeconds)) != 0) return c;
        if ((c = string.CompareOrdinal(a.Check, b.Check)) != 0) return c;
        if ((c = string.CompareOrdinal(a.Prompt, b.Prompt)) != 0) return c;
        if ((c = a.PreventOverlap.CompareTo(b.PreventOverlap)) != 0) return c;
        return string.CompareOrdinal(string.Join(",", Ordered(a.Secrets)), string.Join(",", Ordered(b.Secrets)));
    }

    private static FactoryDefinitionStartChangeDto StartEntry(SeatDefinition seat, string change, string sentence, List<string> details)
        => new() { Seat = seat.Seat, Change = change, Sentence = sentence, Details = details };

    private static List<string> StartDetails(SeatStartDefinition? o, SeatStartDefinition n)
    {
        var created = o is null;
        var details = new List<string>();
        if (!string.Equals(o?.Check, n.Check, StringComparison.Ordinal) && (!created || n.Check is not null))
            details.Add(ValueChange("check", created, o?.Check, n.Check));
        details.AddRange(SetChange("secret", o?.Secrets ?? Array.Empty<string>(), n.Secrets));
        // Two cron expressions that read the same in words ("1-5" and "MON-FRI") still differ as written.
        if (o?.Cron is not null && n.Cron is not null && o.Cron != n.Cron && When(o) == When(n))
            details.Add($"cron \"{o.Cron}\" -> \"{n.Cron}\"");
        if (!string.Equals(o?.Prompt, n.Prompt, StringComparison.Ordinal))
            details.Add(created ? $"prompt {Quote(n.Prompt)}" : $"prompt {Quote(o!.Prompt)} -> {Quote(n.Prompt)}");
        if (created ? !n.PreventOverlap : o!.PreventOverlap != n.PreventOverlap)
            details.Add(created ? "prevent overlap off" : $"prevent overlap {OnOff(o!.PreventOverlap)} -> {OnOff(n.PreventOverlap)}");
        return details;
    }

    private static bool SameStart(SeatStartDefinition a, SeatStartDefinition b)
        => a.Cron == b.Cron && a.TimeZone == b.TimeZone && a.EverySeconds == b.EverySeconds && a.Check == b.Check
           && a.Prompt == b.Prompt && a.PreventOverlap == b.PreventOverlap && SameSet(a.Secrets, b.Secrets);

    /// <summary>When a start fires, in words: "Weekdays 08:00 Europe/Copenhagen", "Every 10 minutes".</summary>
    public static string When(SeatStartDefinition start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Cron is not null) return $"{FactoryScheduleText.Cron(start.Cron)} {start.TimeZone}";
        var seconds = start.EverySeconds ?? throw new InvalidOperationException("A start holds neither a cron nor an interval.");
        if (seconds % 3600 == 0) return seconds == 3600 ? "Every hour" : $"Every {seconds / 3600} hours";
        if (seconds % 60 == 0) return seconds == 60 ? "Every minute" : $"Every {seconds / 60} minutes";
        return seconds == 1 ? "Every second" : $"Every {seconds} seconds";
    }

    // -------------------------------------------------------------------------------------------------------------
    // Rules
    // -------------------------------------------------------------------------------------------------------------

    private const string Tightened = "tightened";
    private const string Loosened = "loosened";

    private static void CompareRules(FactoryDefinition? before, FactoryDefinition after, FactoryDefinitionChangesDto dto)
    {
        var oldSets = (before?.RuleSets ?? Array.Empty<RuleSetDefinition>()).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var newSets = after.RuleSets.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var name in oldSets.Keys.Union(newSets.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            oldSets.TryGetValue(name, out var o);
            newSets.TryGetValue(name, out var n);
            var tighter = new List<string>();
            var looser = new List<string>();
            // A deny or ask added is tighter, and removed is looser; an allow added is looser, and removed is tighter.
            foreach (var (kind, oldList, newList, addTightens) in new[]
            {
                ("deny", o?.Deny, n?.Deny, true),
                ("ask", o?.Ask, n?.Ask, true),
                ("allow", o?.Allow, n?.Allow, false),
            })
            {
                var oldSet = oldList ?? Array.Empty<string>();
                var newSet = newList ?? Array.Empty<string>();
                foreach (var rule in Ordered(newSet.Except(oldSet, StringComparer.Ordinal)))
                    (addTightens ? tighter : looser).Add($"{kind} {rule} added");
                foreach (var rule in Ordered(oldSet.Except(newSet, StringComparer.Ordinal)))
                    (addTightens ? looser : tighter).Add($"{kind} {rule} removed");
            }
            var which = o is null ? $"Rule set {name} (new)" : n is null ? $"Rule set {name} (removed)" : $"Rule set {name}";
            if (tighter.Count > 0)
                dto.Rules.Add(new FactoryDefinitionRuleChangeDto { RuleSet = name, Direction = Tightened, Sentence = $"{which} tightened: {string.Join("; ", tighter)}." });
            if (looser.Count > 0)
                dto.Rules.Add(new FactoryDefinitionRuleChangeDto { RuleSet = name, Direction = Loosened, Sentence = $"{which} loosened: {string.Join("; ", looser)}." });
        }

        // A seat taking a rule set is tightened; dropping one is loosened. A removed seat takes nothing any more, and
        // is reported as removed, not as loosened.
        var oldSeats = (before?.Seats ?? Array.Empty<SeatDefinition>()).ToDictionary(s => s.Seat, StringComparer.Ordinal);
        foreach (var seat in after.Seats.OrderBy(s => s.Seat, StringComparer.Ordinal))
        {
            var oldRules = oldSeats.TryGetValue(seat.Seat, out var o) ? o.Rules : Array.Empty<string>();
            foreach (var taken in Ordered(seat.Rules.Except(oldRules, StringComparer.Ordinal)))
                dto.Rules.Add(new FactoryDefinitionRuleChangeDto { Seat = seat.Seat, Direction = Tightened, Sentence = $"Seat {Label(seat)} tightened: now takes rule set {taken}." });
            foreach (var dropped in Ordered(oldRules.Except(seat.Rules, StringComparer.Ordinal)))
                dto.Rules.Add(new FactoryDefinitionRuleChangeDto { Seat = seat.Seat, Direction = Loosened, Sentence = $"Seat {Label(seat)} loosened: no longer takes rule set {dropped}." });
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Grants
    // -------------------------------------------------------------------------------------------------------------

    private static void CompareGrants(FactoryDefinition? before, FactoryDefinition after, FactoryDefinitionChangesDto dto)
    {
        var oldGrants = (before?.Grants ?? Array.Empty<GrantDefinition>()).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var newGrants = after.Grants.ToDictionary(g => g.Id, StringComparer.Ordinal);
        foreach (var id in oldGrants.Keys.Union(newGrants.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            oldGrants.TryGetValue(id, out var o);
            newGrants.TryGetValue(id, out var n);
            if (n is null)
            {
                dto.Grants.Add(new FactoryDefinitionItemChangeDto { Id = id, Change = "removed", Sentence = $"Grant {id} removed." });
                continue;
            }
            var added = o is null;
            var details = new List<string>();
            if (added || o!.Allows != n.Allows) details.Add(ValueChange("allows", added, o?.Allows, n.Allows));
            if ((added && n.Limits is not null) || (!added && o!.Limits != n.Limits)) details.Add(ValueChange("limits", added, o?.Limits, n.Limits));
            details.AddRange(SetChange("seat", o?.Seats ?? Array.Empty<string>(), n.Seats));
            if (added)
                dto.Grants.Add(new FactoryDefinitionItemChangeDto { Id = id, Change = "added", Sentence = $"Grant {id} added.", Details = details });
            else if (details.Count > 0)
                dto.Grants.Add(new FactoryDefinitionItemChangeDto { Id = id, Change = "changed", Sentence = $"Grant {id} changed.", Details = details });
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Files, checks, factory skills, and definition files whose text changes but whose meaning does not
    // -------------------------------------------------------------------------------------------------------------

    private static void CompareFiles(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, FactoryDefinitionChangesDto dto)
    {
        foreach (var path in before.Keys.Union(after.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            before.TryGetValue(path, out var o);
            after.TryGetValue(path, out var n);
            if (string.Equals(o, n, StringComparison.Ordinal)) continue;

            var (info, _) = DefinitionPaths.Classify(path);
            var place = info!.Place;
            var (kind, noun) = place switch
            {
                DefinitionPlace.File => ("file", "File"),
                DefinitionPlace.Check => ("check", "Check"),
                DefinitionPlace.SkillMain or DefinitionPlace.SkillFile => ("skill", "Skill file"),
                _ => ("definition", ""),
            };

            if (kind == "definition")
            {
                // A definition file: what it defines is reported above. Its text is reported here only when nothing
                // it defines changes, so a change of comments or layout - or a rule set holding no rules coming or
                // going - is never invisible.
                if (MeaningChanged(place, info.Name, path, dto)) continue;
                var (definitionChange, definitionSentence) = o is null ? ("added", $"{path} added; it defines nothing yet.")
                    : n is null ? ("removed", $"{path} removed; it defined nothing.")
                    : ("changed", $"{path}: its text changes but nothing it defines does (layout, order or comments only).");
                dto.Files.Add(new FactoryDefinitionFileChangeDto
                {
                    Path = path, Kind = kind, Change = definitionChange, Sentence = definitionSentence,
                    Diff = DefinitionTextDiff.Compare(o, n),
                });
                continue;
            }

            var diff = DefinitionTextDiff.Compare(o, n);
            var (change, sentence) = o is null ? ("added", $"{noun} {path} added: {Lines(diff.Added)}.")
                : n is null ? ("removed", $"{noun} {path} removed: {Lines(diff.Removed)}.")
                : ("changed", $"{noun} {path} changed: {AddedRemoved(diff)}.");
            dto.Files.Add(new FactoryDefinitionFileChangeDto { Path = path, Kind = kind, Change = change, Sentence = sentence, Diff = diff });
        }
    }

    private static bool MeaningChanged(DefinitionPlace place, string? name, string path, FactoryDefinitionChangesDto dto) => place switch
    {
        DefinitionPlace.Factory => dto.Factory.Count > 0,
        DefinitionPlace.Seat => dto.Seats.Any(s => s.Seat == name) || dto.Starts.Any(s => s.Seat == name),
        DefinitionPlace.Grants => dto.Grants.Count > 0,
        DefinitionPlace.RuleSet => dto.Rules.Any(r => r.RuleSet == name),
        _ => throw new InvalidOperationException($"'{path}' is not a definition file."),
    };

    // -------------------------------------------------------------------------------------------------------------
    // The summary
    // -------------------------------------------------------------------------------------------------------------

    private static string Summarise(FactoryDefinitionChangesDto dto)
    {
        if (dto.NothingChanges) return "Nothing changes.";
        var parts = new List<string>();
        if (dto.Factory.Count > 0) parts.Add($"{Count(dto.Factory.Count, "factory setting", "factory settings")} {(dto.FirstPublish ? "set" : "changed")}");
        AddCounts(parts, dto.Seats.Select(s => s.Change), "seat", "seats", "added", "removed", "changed");
        AddCounts(parts, dto.Starts.Select(s => s.Change), "start", "starts", "created", "moved", "changed", "switched off");
        foreach (var direction in new[] { Tightened, Loosened })
        {
            var on = dto.Rules.Where(r => r.Direction == direction).Select(r => r.RuleSet ?? r.Seat!).Distinct(StringComparer.Ordinal).ToList();
            if (on.Count > 0) parts.Add($"rules {direction} on {Join(on)}");
        }
        AddCounts(parts, dto.Grants.Select(g => g.Change), "grant", "grants", "added", "removed", "changed");
        foreach (var (kind, one, many) in new[] { ("file", "file", "files"), ("check", "check", "checks"), ("skill", "skill file", "skill files") })
            AddCounts(parts, dto.Files.Where(f => f.Kind == kind).Select(f => f.Change), one, many, "added", "removed", "changed");
        var definitionFiles = dto.Files.Where(f => f.Kind == "definition").ToList();
        AddCounts(parts, definitionFiles.Where(f => f.Change != "changed").Select(f => f.Change), "empty definition file", "empty definition files", "added", "removed");
        var layout = definitionFiles.Count(f => f.Change == "changed");
        if (layout > 0) parts.Add($"layout only in {Count(layout, "file", "files")}");

        var text = string.Join(", ", parts);
        return dto.FirstPublish ? $"First publish: {text}." : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private static void AddCounts(List<string> parts, IEnumerable<string> changes, string one, string many, params string[] order)
    {
        var list = changes.ToList();
        foreach (var change in order)
        {
            var n = list.Count(c => c == change);
            if (n > 0) parts.Add($"{Count(n, one, many)} {change}");
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Words
    // -------------------------------------------------------------------------------------------------------------

    private static string Label(SeatDefinition seat) => $"{seat.Name} ({seat.Seat})";

    // "title DevThrottle" for something new, "title A -> B" for a change, "purpose removed (was A)" for one taken away.
    private static string ValueChange(string field, bool isNew, string? oldValue, string? newValue)
    {
        if (isNew) return $"{field} {Shown(newValue)}";
        if (oldValue is null) return $"{field} set to {Shown(newValue)}";
        if (newValue is null) return $"{field} removed (was {Shown(oldValue)})";
        return $"{field} {Shown(oldValue)} -> {Shown(newValue)}";
    }

    // One line per item added or removed, compared as a set: "grant send-outreach-email added".
    private static IEnumerable<string> SetChange(string noun, IReadOnlyList<string> oldItems, IReadOnlyList<string> newItems)
    {
        foreach (var item in Ordered(newItems.Except(oldItems, StringComparer.Ordinal))) yield return $"{noun} {item} added";
        foreach (var item in Ordered(oldItems.Except(newItems, StringComparer.Ordinal))) yield return $"{noun} {item} removed";
    }

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    private static IEnumerable<string> Ordered(IEnumerable<string> items) => items.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal);

    private static string Shown(string? value)
    {
        if (value is null) return "none";
        var flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxValueChars ? flat : flat[..(MaxValueChars - 3)] + "...";
    }

    private static string Quote(string value) => $"\"{Shown(value)}\"";

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Lines(int n) => Count(n, "line", "lines");

    private static string AddedRemoved(FactoryDefinitionTextDiffDto diff)
        => diff.Added == 0 && diff.Removed == 0
            ? "only line endings change"
            : $"{Lines(diff.Added)} added, {diff.Removed} removed";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
