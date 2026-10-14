using System.Globalization;
using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// THE WHOLE-DEFINITION CHECK (Factory Definition, phase 1; design section 7). One pure function: given a version's
/// content (path to text), the factory id the caller names, the account's fleet skill ids and the machines the
/// account's Directors report now, it returns every finding - errors, which refuse a save, a publish and a restore,
/// and notices, which never refuse. It reads no database and no clock.
///
/// It runs in two stages. FORMAT: every path is a place a definition holds and within its cap, every file parses (no
/// anchors, aliases, tags or duplicate keys) and passes its schema, and every factory skill has its frontmatter. Only
/// when the format is clean does the REFERENCES stage run - a seat's grants, rule sets, skills, files, checks and
/// repository resolve, the boss seat is a Boss, the two grant lists agree, each start keeps its rules - because a
/// reference into a file that did not parse would be reported as dangling when it is not.
///
/// Findings come back errors first, then by file and line; at most <see cref="MaxFindings"/>, then one more saying
/// how many were not shown.
/// </summary>
public static class FactoryDefinitionCheck
{
    /// <summary>The most findings one check returns before the "not shown" finding.</summary>
    public const int MaxFindings = 200;

    /// <summary>The role word a boss seat has.</summary>
    public const string BossRole = "Boss";

    /// <summary>The path of the factory file.</summary>
    public const string FactoryFile = "factory.yaml";

    /// <summary>The path of the grants file.</summary>
    public const string GrantsFile = "grants.yaml";

    /// <summary>Where a finding about the missing seats points.</summary>
    public const string SeatsPlace = "seats/";

    /// <summary>
    /// Check a whole definition. <paramref name="factoryId"/> is the factory the save names;
    /// <paramref name="fleetSkillIds"/> the skill ids in the account's skill store; <paramref name="connectedMachines"/>
    /// the machine names the account's Directors report now. The result carries the parsed definition when there is no
    /// error.
    /// </summary>
    public static FactoryDefinitionCheckResult Check(
        IReadOnlyDictionary<string, string> content,
        string factoryId,
        IReadOnlyCollection<string> fleetSkillIds,
        IReadOnlyCollection<string> connectedMachines)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(factoryId);
        ArgumentNullException.ThrowIfNull(fleetSkillIds);
        ArgumentNullException.ThrowIfNull(connectedMachines);
        FileLog.Write($"[FactoryDefinitionCheck] Check: factory={factoryId}, files={content.Count}, fleetSkills={fleetSkillIds.Count}, machines={connectedMachines.Count}");

        var findings = new List<DefinitionFinding>();
        var parsed = ReadFormat(content, findings);
        FactoryDefinition? definition = null;
        if (parsed is not null)
        {
            new ReferenceCheck(parsed, factoryId, fleetSkillIds, connectedMachines, findings).Run();
            if (!findings.Any(f => f.IsError)) definition = Build(parsed);
        }

        var result = Finish(findings, definition);
        FileLog.Write($"[FactoryDefinitionCheck] Check: factory={factoryId}, errors={findings.Count(f => f.IsError)}, notices={findings.Count(f => !f.IsError)}, returned={result.Findings.Count}");
        return result;
    }

    /// <summary>
    /// Parse a definition WITHOUT the account's data: the format stage only. Returns the parsed definition when the
    /// format is clean (references unchecked) - what "what changes" compares - and the format errors otherwise.
    /// </summary>
    public static FactoryDefinitionCheckResult Read(IReadOnlyDictionary<string, string> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        FileLog.Write($"[FactoryDefinitionCheck] Read: files={content.Count}");
        var findings = new List<DefinitionFinding>();
        var parsed = ReadFormat(content, findings);
        var result = Finish(findings, parsed is null ? null : Build(parsed));
        FileLog.Write($"[FactoryDefinitionCheck] Read: errors={findings.Count}, parsed={result.Definition is not null}");
        return result;
    }

    private static FactoryDefinitionCheckResult Finish(List<DefinitionFinding> findings, FactoryDefinition? definition)
    {
        var ordered = findings
            .OrderBy(f => f.IsError ? 0 : 1)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count <= MaxFindings) return new FactoryDefinitionCheckResult(ordered, definition);

        var hidden = ordered.Skip(MaxFindings).ToList();
        var shown = ordered.Take(MaxFindings).ToList();
        shown.Add(new DefinitionFinding(
            hidden.Any(f => f.IsError) ? DefinitionSeverity.Error : DefinitionSeverity.Notice,
            "", 0,
            $"{hidden.Count} more {(hidden.Count == 1 ? "finding was" : "findings were")} not shown; the check returns at most {MaxFindings} at a time. Fix these and check again to see the rest."));
        return new FactoryDefinitionCheckResult(shown, definition);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // FORMAT
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A parsed file with its path.</summary>
    private sealed record ParsedFile(string Path, DefinitionMap Map);

    /// <summary>A factory skill's frontmatter.</summary>
    private sealed record SkillFront(string Path, string Name, string Description);

    /// <summary>The format stage's result: every file parsed and schema-clean.</summary>
    private sealed class Parsed
    {
        public ParsedFile Factory { get; set; } = null!;
        public SortedDictionary<string, ParsedFile> Seats { get; } = new(StringComparer.Ordinal);
        public ParsedFile? Grants { get; set; }
        public SortedDictionary<string, ParsedFile> RuleSets { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Files { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Checks { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, SkillFront> Skills { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, List<string>> SkillFiles { get; } = new(StringComparer.Ordinal);
    }

    private static Parsed? ReadFormat(IReadOnlyDictionary<string, string> content, List<DefinitionFinding> findings)
    {
        var parsed = new Parsed();
        var errorsBefore = findings.Count;
        long totalBytes = 0;
        var sawFactory = false;
        var sawSeat = false;
        // The paths that are places, which are the only ones compared for a clash: a path already refused is
        // reported once, for its own reason.
        var placed = new List<string>();

        foreach (var path in content.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            // A null text is a caller's defect (a request that bound a missing field), never an empty file.
            var text = content[path]
                ?? throw new ArgumentException($"The definition's content holds no text for '{path}'; every path must map to a text.", nameof(content));
            var bytes = Encoding.UTF8.GetByteCount(text);
            totalBytes += bytes;

            if (DefinitionPaths.ShapeProblem(path) is { } shape)
            {
                Error(findings, path, 0, shape);
                continue;
            }
            var (info, refusal) = DefinitionPaths.Classify(path);
            if (info is null)
            {
                Error(findings, path, 0, refusal!);
                continue;
            }
            placed.Add(path);
            if (bytes > DefinitionPaths.MaxFileBytes)
            {
                Error(findings, path, 0,
                    $"'{path}' is {bytes} bytes; a file may hold at most {DefinitionPaths.MaxFileBytes} bytes ({DefinitionPaths.MaxFileBytes / 1024} KB). Shorten it or split it.");
                continue;
            }

            switch (info.Place)
            {
                case DefinitionPlace.Factory:
                    sawFactory = true;
                    if (ParseAndValidate(path, text, DefinitionFileKind.Factory, findings) is { } factory)
                        parsed.Factory = factory;
                    break;

                case DefinitionPlace.Seat:
                    sawSeat = true;
                    if (ParseAndValidate(path, text, DefinitionFileKind.Seat, findings) is { } seat)
                    {
                        var seatKey = seat.Map.Find("seat")!;
                        var written = ((DefinitionScalar)seatKey.Value).Value;
                        if (!string.Equals(written, info.Name, StringComparison.Ordinal))
                            Error(findings, path, seatKey.KeyLine,
                                $"This file is seats/{info.Name}.yaml but its seat key says '{written}'; they must be the same.");
                        parsed.Seats[info.Name!] = seat;
                    }
                    break;

                case DefinitionPlace.Grants:
                    parsed.Grants = ParseAndValidate(path, text, DefinitionFileKind.Grants, findings);
                    break;

                case DefinitionPlace.RuleSet:
                    if (ParseAndValidate(path, text, DefinitionFileKind.Rules, findings) is { } rules)
                        parsed.RuleSets[info.Name!] = rules;
                    break;

                case DefinitionPlace.File:
                    parsed.Files.Add(path);
                    break;

                case DefinitionPlace.Check:
                    parsed.Checks.Add(path);
                    break;

                case DefinitionPlace.SkillMain:
                    if (ReadSkillFront(path, info.Name!, text, findings) is { } front)
                        parsed.Skills[info.Name!] = front;
                    break;

                case DefinitionPlace.SkillFile:
                    if (!parsed.SkillFiles.TryGetValue(info.Name!, out var list))
                        parsed.SkillFiles[info.Name!] = list = new List<string>();
                    list.Add(path);
                    break;
            }
        }

        // Two paths no machine could write side by side: one name in two cases, or a name both a file and a folder.
        foreach (var (path, clash) in DefinitionPaths.Clashes(placed))
            Error(findings, path, 0, clash);
        if (totalBytes > DefinitionPaths.MaxVersionBytes)
            Error(findings, "", 0,
                $"The definition is {totalBytes} bytes; a version may hold at most {DefinitionPaths.MaxVersionBytes} bytes ({DefinitionPaths.MaxVersionBytes / (1024 * 1024)} MB). Remove or shorten files.");
        if (content.Count > DefinitionPaths.MaxFiles)
            Error(findings, "", 0,
                $"The definition holds {content.Count} files; a version may hold at most {DefinitionPaths.MaxFiles}. Remove or combine files.");
        if (!sawFactory)
            Error(findings, FactoryFile, 0,
                "The definition has no factory.yaml. Add one: it names the factory, its title and its defaults.");
        if (!sawSeat)
            Error(findings, SeatsPlace, 0, "The definition has no seat. Add at least one file seats/<seat>.yaml.");

        foreach (var (skill, files) in parsed.SkillFiles)
        {
            var main = $"skills/{skill}/{DefinitionPaths.SkillMainFile}";
            if (content.ContainsKey(main)) continue;
            foreach (var file in files)
                Error(findings, file, 0,
                    $"'{file}' is beside the factory skill '{skill}', but {main} does not exist. Add the SKILL.md or move the file.");
        }

        return findings.Count > errorsBefore ? null : parsed;
    }

    private static ParsedFile? ParseAndValidate(string path, string text, DefinitionFileKind kind, List<DefinitionFinding> findings)
    {
        var (root, parseError) = kind == DefinitionFileKind.Rules
            ? DefinitionDocument.ParseJson(path, text)
            : DefinitionDocument.ParseYaml(path, text);
        if (parseError is not null)
        {
            Error(findings, path, parseError.Line, parseError.Message);
            return null;
        }
        var problems = DefinitionSchema.For(kind).Validate(root!);
        foreach (var p in problems) Error(findings, path, p.Line, p.Message);
        return problems.Count == 0 ? new ParsedFile(path, (DefinitionMap)root!) : null;
    }

    private static SkillFront? ReadSkillFront(string path, string skill, string text, List<DefinitionFinding> findings)
    {
        if (DefinitionDocument.BeginsWithByteOrderMark(path, text) is { } bom)
        {
            Error(findings, path, bom.Line, bom.Message);
            return null;
        }
        var lines = text.Split('\n');
        var close = -1;
        if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
        {
            for (var i = 1; i < lines.Length; i++)
                if (lines[i].TrimEnd('\r') == "---") { close = i; break; }
        }
        if (close < 0)
        {
            Error(findings, path, 1,
                $"{path} must begin with frontmatter: a line '---', then name and description, then a line '---'.");
            return null;
        }

        var front = string.Join('\n', lines[1..close]);
        var (root, parseError) = DefinitionDocument.ParseYaml(path, front);
        if (parseError is not null)
        {
            // The frontmatter starts on the file's line 2.
            Error(findings, path, parseError.Line == 0 ? 1 : parseError.Line + 1, parseError.Message);
            return null;
        }
        if (root is not DefinitionMap map)
        {
            Error(findings, path, 1, $"The frontmatter of {path} must be a mapping of keys holding name and description.");
            return null;
        }

        var ok = true;
        var nameEntry = map.Find("name");
        string? name = null;
        if (nameEntry is null)
        {
            Error(findings, path, 1, $"The frontmatter of {path} needs the key 'name', equal to the folder name '{skill}'.");
            ok = false;
        }
        else if (nameEntry.Value is not DefinitionScalar { Kind: DefinitionScalarKind.Text } n || n.Value != skill)
        {
            var written = nameEntry.Value is DefinitionScalar s ? s.Value : "not text";
            Error(findings, path, nameEntry.KeyLine + 1,
                $"The skill's name is '{written}' but its folder is '{skill}'; they must be the same.");
            ok = false;
        }
        else
        {
            name = n.Value;
        }

        var descriptionEntry = map.Find("description");
        string? description = null;
        if (descriptionEntry is null)
        {
            Error(findings, path, 1, $"The frontmatter of {path} needs the key 'description', saying when to use the skill.");
            ok = false;
        }
        else if (descriptionEntry.Value is not DefinitionScalar { Kind: DefinitionScalarKind.Text } d || string.IsNullOrWhiteSpace(d.Value))
        {
            Error(findings, path, descriptionEntry.KeyLine + 1, "The skill's description must be text that is not blank.");
            ok = false;
        }
        else
        {
            description = d.Value;
        }

        return ok ? new SkillFront(path, name!, description!) : null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // REFERENCES
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class ReferenceCheck
    {
        private readonly Parsed _p;
        private readonly string _factoryId;
        private readonly HashSet<string> _fleetSkills;
        private readonly HashSet<string> _machines;
        private readonly List<DefinitionFinding> _findings;
        private readonly HashSet<string> _repositories = new(StringComparer.Ordinal);
        private string _defaultMachine = "";
        private string _defaultAgent = "";

        public ReferenceCheck(Parsed parsed, string factoryId, IReadOnlyCollection<string> fleetSkills,
            IReadOnlyCollection<string> machines, List<DefinitionFinding> findings)
        {
            _p = parsed;
            _factoryId = factoryId;
            _fleetSkills = new HashSet<string>(fleetSkills, StringComparer.Ordinal);
            _machines = new HashSet<string>(machines, StringComparer.OrdinalIgnoreCase);
            _findings = findings;
        }

        public void Run()
        {
            CheckFactory();
            var named = new Named();
            foreach (var (seatId, seat) in _p.Seats) CheckSeat(seatId, seat, named);
            CheckGrants();
            Unused(named);
        }

        /// <summary>What the seats name, for the "unused" notices.</summary>
        private sealed class Named
        {
            public HashSet<string> RuleSets { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Checks { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Skills { get; } = new(StringComparer.Ordinal);
        }

        private void CheckFactory()
        {
            var path = _p.Factory.Path;
            var map = _p.Factory.Map;

            var factory = map.Find("factory")!;
            var written = Text(factory);
            if (!string.Equals(written, _factoryId, StringComparison.Ordinal))
                Error(_findings, path, factory.KeyLine,
                    $"factory.yaml says the factory is '{written}', but this definition is being saved for '{_factoryId}'. They must be the same.");

            var approvedOn = map.Find("goalApprovedOn");
            if (approvedOn is not null)
            {
                if (map.Find("goalText") is null)
                    Error(_findings, path, approvedOn.KeyLine, "goalApprovedOn is only allowed with goalText. Add the goal or remove the date.");
                if (!DateOnly.TryParseExact(Text(approvedOn), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    Error(_findings, path, approvedOn.KeyLine, $"goalApprovedOn is '{Text(approvedOn)}', which is not a real date. Write it YYYY-MM-DD.");
            }

            if (map.Find("bossSeat") is { } boss)
            {
                var bossId = Text(boss);
                if (!_p.Seats.TryGetValue(bossId, out var bossSeat))
                    Error(_findings, path, boss.KeyLine, $"The boss seat '{bossId}' does not exist: there is no seats/{bossId}.yaml.");
                else if (Text(bossSeat.Map.Find("role")!) is var role && role != BossRole)
                    Error(_findings, path, boss.KeyLine, $"The boss seat '{bossId}' has the role '{role}'; a boss seat's role must be {BossRole}.");
            }

            var defaults = (DefinitionMap)map.Find("defaults")!.Value;
            var machine = defaults.Find("machine")!;
            _defaultMachine = Text(machine);
            _defaultAgent = Text(defaults.Find("agent")!);
            MachineNotice(path, machine.KeyLine, _defaultMachine);

            if (map.Find("repositories") is { } repositories)
                foreach (var (repository, _) in Items(repositories)) _repositories.Add(repository);
        }

        private void CheckSeat(string seatId, ParsedFile seat, Named named)
        {
            var path = seat.Path;
            var map = seat.Map;

            var machineEntry = map.Find("machine");
            var machine = machineEntry is null ? _defaultMachine : Text(machineEntry);
            if (machineEntry is not null) MachineNotice(path, machineEntry.KeyLine, machine);
            var agent = map.Find("agent") is { } agentEntry ? Text(agentEntry) : _defaultAgent;

            if (map.Find("repository") is { } repository)
            {
                var name = Text(repository);
                if (!_repositories.Contains(name))
                    Error(_findings, path, repository.KeyLine,
                        $"The seat names the repository '{name}', which factory.yaml does not list under repositories. Add it there or remove it here.");
                else
                    Notice(_findings, path, repository.KeyLine,
                        $"Whether {machine} has the repository '{name}' registered is not checked yet.");
            }

            if (map.Find("grants") is { } grants)
            {
                foreach (var (grant, line) in Items(grants))
                {
                    var holder = GrantNamed(grant);
                    if (holder is null)
                        Error(_findings, path, line, $"The seat names the grant '{grant}', which grants.yaml does not hold.");
                    else if (!Items(holder.Find("seats")!).Any(s => s.Value == seatId))
                        Error(_findings, path, line,
                            $"The seat names the grant '{grant}', but that grant's seats in grants.yaml do not list '{seatId}'. The two lists must agree.");
                }
            }

            if (map.Find("rules") is { } rules)
            {
                foreach (var (rule, line) in Items(rules))
                {
                    named.RuleSets.Add(rule);
                    if (!_p.RuleSets.ContainsKey(rule))
                        Error(_findings, path, line, $"The seat names the rule set '{rule}', but there is no rules/{rule}.json.");
                }
                // A rule that does not bind is worse than none (mission rule A4): a seat on an agent that cannot take
                // rules at launch may not name any.
                if (Items(rules).Count > 0)
                {
                    var answer = FactoryRulesSupport.For(Enum.Parse<AgentKind>(agent));
                    if (!answer.CanTake)
                        Error(_findings, path, rules.KeyLine, answer.Reason!);
                }
            }

            if (map.Find("secrets") is { } secrets)
                foreach (var (secret, line) in Items(secrets))
                    SecretNotice(path, line, machine, secret);

            if (map.Find("skills") is { Value: DefinitionMap skills })
                CheckSkills(path, skills, agent, named);

            if (map.Find("files") is { } files)
            {
                foreach (var (file, line) in Items(files))
                {
                    named.Files.Add(file);
                    if (!_p.Files.Contains(file))
                        Error(_findings, path, line, $"The seat names the file '{file}', which is not in this definition.");
                }
            }

            if (map.Find("starts") is { Value: DefinitionList starts })
                foreach (var start in starts.Items)
                    CheckStart(path, (DefinitionMap)start, machine, named);
        }

        private void CheckSkills(string path, DefinitionMap skills, string agent, Named named)
        {
            if (skills.Find("factory") is { } factorySkills)
            {
                var items = Items(factorySkills);
                foreach (var (skill, line) in items)
                {
                    named.Skills.Add(skill);
                    if (!_p.Skills.ContainsKey(skill))
                        Error(_findings, path, line, $"The seat names the factory skill '{skill}', but there is no skills/{skill}/SKILL.md.");
                }
                if (items.Count > 0)
                {
                    var answer = FactorySkillSupport.For(Enum.Parse<AgentKind>(agent));
                    if (!answer.CanLoad)
                        Error(_findings, path, factorySkills.KeyLine, answer.Reason!);
                }
            }

            if (skills.Find("fleet") is { } fleetSkills)
            {
                foreach (var (skill, line) in Items(fleetSkills))
                {
                    if (_p.Skills.ContainsKey(skill))
                        Error(_findings, path, line,
                            $"'{skill}' is both a factory skill (skills/{skill}/SKILL.md) and a fleet skill, so a seat would not know which one it got. Rename the factory skill.");
                    else if (!_fleetSkills.Contains(skill))
                        Error(_findings, path, line, $"The seat names the fleet skill '{skill}', which is not in this account's skill store.");
                }
            }
        }

        private void CheckStart(string path, DefinitionMap start, string machine, Named named)
        {
            var cron = start.Find("cron");
            var timeZone = start.Find("timeZone");
            var every = start.Find("every");
            var check = start.Find("check");
            var secrets = start.Find("secrets");
            var preventOverlap = start.Find("preventOverlap");

            if (cron is not null && every is not null)
                Error(_findings, path, start.Line, "A start has both cron and every; give exactly one.");
            if (cron is null && every is null)
                Error(_findings, path, start.Line, "A start has neither cron nor every; give exactly one.");

            if (cron is not null)
            {
                if (!CronSchedule.IsCronExpression(Text(cron)))
                    Error(_findings, path, cron.KeyLine,
                        $"'{Text(cron)}' is not a cron expression a schedule takes: five fields, minute hour day month weekday.");
                if (timeZone is null)
                    Error(_findings, path, cron.KeyLine, "A start with cron needs timeZone, the zone the time is read in.");
            }
            if (timeZone is not null)
            {
                if (cron is null)
                    Error(_findings, path, timeZone.KeyLine, "timeZone is only allowed with cron. Remove it.");
                else if (!CronSchedule.IsKnownTimeZone(Text(timeZone)))
                    Error(_findings, path, timeZone.KeyLine, $"'{Text(timeZone)}' is not a time zone a schedule takes.");
            }

            if (every is not null)
            {
                var seconds = IntervalSeconds(Text(every));
                if (seconds < TriggerDefinition.MinimumIntervalSeconds || seconds > TriggerDefinition.MaximumIntervalSeconds)
                    Error(_findings, path, every.KeyLine,
                        $"An interval of {Text(every)} is {seconds} seconds; it must be between {TriggerDefinition.MinimumIntervalSeconds} and {TriggerDefinition.MaximumIntervalSeconds} seconds.");
                if (check is null)
                    Error(_findings, path, every.KeyLine,
                        "A start with every must have a check: an interval with no check is a schedule that runs too often. Add a check, or use cron.");
            }

            if (check is not null)
            {
                var checkPath = Text(check);
                named.Checks.Add(checkPath);
                if (!_p.Checks.Contains(checkPath))
                    Error(_findings, path, check.KeyLine, $"The start names the check '{checkPath}', which is not in this definition.");
                if (preventOverlap?.Value is DefinitionScalar { Value: "false" })
                    Error(_findings, path, preventOverlap.KeyLine,
                        "A start with a check always prevents overlap, so preventOverlap cannot be false here. Remove it.");
            }

            if (secrets is not null)
            {
                if (check is null)
                    Error(_findings, path, secrets.KeyLine,
                        "secrets on a start go to its check, and this start has none. Add a check, or move the names to the seat's secrets.");
                foreach (var (secret, line) in Items(secrets))
                    SecretNotice(path, line, machine, secret);
            }
        }

        private void CheckGrants()
        {
            if (_p.Grants is null) return;
            var path = _p.Grants.Path;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var grant in GrantMaps())
            {
                var idEntry = grant.Find("id")!;
                var id = Text(idEntry);
                if (seen.TryGetValue(id, out var first))
                {
                    Error(_findings, path, idEntry.KeyLine, $"The grant id '{id}' is used twice (first on line {first}); give each grant its own id.");
                    continue;
                }
                seen[id] = idEntry.KeyLine;

                foreach (var (seat, line) in Items(grant.Find("seats")!))
                {
                    if (!_p.Seats.TryGetValue(seat, out var seatFile))
                        Error(_findings, path, line, $"The grant '{id}' lists the seat '{seat}', which does not exist: there is no seats/{seat}.yaml.");
                    else if (seatFile.Map.Find("grants") is not { } seatGrants || Items(seatGrants).All(g => g.Value != id))
                        Error(_findings, path, line,
                            $"The grant '{id}' lists the seat '{seat}', but seats/{seat}.yaml does not name it under grants. The two lists must agree.");
                }
            }
        }

        private void Unused(Named named)
        {
            foreach (var (name, file) in _p.RuleSets)
                if (!named.RuleSets.Contains(name))
                    Notice(_findings, file.Path, 0, $"The rule set '{name}' is not named by any seat.");
            foreach (var file in _p.Files)
                if (!named.Files.Contains(file))
                    Notice(_findings, file, 0, $"'{file}' is not named by any seat.");
            foreach (var check in _p.Checks)
                if (!named.Checks.Contains(check))
                    Notice(_findings, check, 0, $"The check '{check}' is not named by any start.");
            foreach (var (name, skill) in _p.Skills)
                if (!named.Skills.Contains(name))
                    Notice(_findings, skill.Path, 0, $"The factory skill '{name}' is not named by any seat.");
        }

        private IEnumerable<DefinitionMap> GrantMaps()
            => _p.Grants is null
                ? Enumerable.Empty<DefinitionMap>()
                : ((DefinitionList)_p.Grants.Map.Find("grants")!.Value).Items.Cast<DefinitionMap>();

        // The first grant with this id (a repeated id is its own error).
        private DefinitionMap? GrantNamed(string id)
            => GrantMaps().FirstOrDefault(g => Text(g.Find("id")!) == id);

        private void MachineNotice(string path, int line, string machine)
        {
            if (!_machines.Contains(machine))
                Notice(_findings, path, line, $"No Director on {machine} is connected now, so it could not be confirmed.");
        }

        private void SecretNotice(string path, int line, string machine, string secret)
            => Notice(_findings, path, line, $"Whether {machine} holds '{secret}' is not checked yet.");
    }

    /// <summary>An interval's length in seconds: <c>10m</c> is 600. The schema has already checked its form.</summary>
    public static long IntervalSeconds(string every)
    {
        ArgumentNullException.ThrowIfNull(every);
        // In long: the schema allows up to 999999h, which is more seconds than an int holds.
        var number = long.Parse(every[..^1], NumberStyles.None, CultureInfo.InvariantCulture);
        return every[^1] switch
        {
            's' => number,
            'm' => number * 60,
            'h' => number * 3600,
            _ => throw new FormatException($"'{every}' is not an interval."),
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // THE MODEL
    // ---------------------------------------------------------------------------------------------------------------

    private static FactoryDefinition Build(Parsed p)
    {
        var f = p.Factory.Map;
        var defaults = (DefinitionMap)f.Find("defaults")!.Value;
        return new FactoryDefinition(
            Factory: Text(f.Find("factory")!),
            Title: Text(f.Find("title")!),
            Purpose: OptionalText(f, "purpose"),
            GoalText: OptionalText(f, "goalText"),
            GoalApprovedOn: OptionalText(f, "goalApprovedOn"),
            BossSeat: OptionalText(f, "bossSeat"),
            Defaults: new FactoryDefinitionDefaults(
                Text(defaults.Find("machine")!), Text(defaults.Find("agent")!), OptionalText(defaults, "model")),
            Repositories: TextList(f, "repositories"),
            Seats: p.Seats.Values.Select(s => BuildSeat(s.Map)).ToList(),
            Grants: p.Grants is null
                ? Array.Empty<GrantDefinition>()
                : ((DefinitionList)p.Grants.Map.Find("grants")!.Value).Items.Cast<DefinitionMap>()
                    .Select(g => new GrantDefinition(Text(g.Find("id")!), Text(g.Find("allows")!), OptionalText(g, "limits"), TextList(g, "seats")))
                    .OrderBy(g => g.Id, StringComparer.Ordinal)
                    .ToList(),
            RuleSets: p.RuleSets.Select(r =>
            {
                var permissions = (DefinitionMap)r.Value.Map.Find("permissions")!.Value;
                return new RuleSetDefinition(r.Key, TextList(permissions, "allow"), TextList(permissions, "deny"), TextList(permissions, "ask"));
            }).ToList(),
            Skills: p.Skills.Select(s => new FactorySkillDefinition(
                s.Key, s.Value.Description,
                p.SkillFiles.TryGetValue(s.Key, out var files) ? files.OrderBy(x => x, StringComparer.Ordinal).ToList() : Array.Empty<string>()))
                .ToList(),
            Files: p.Files.ToList(),
            Checks: p.Checks.ToList());
    }

    private static SeatDefinition BuildSeat(DefinitionMap s)
    {
        var skills = s.Find("skills")?.Value as DefinitionMap;
        var starts = s.Find("starts")?.Value as DefinitionList;
        return new SeatDefinition(
            Seat: Text(s.Find("seat")!),
            Name: Text(s.Find("name")!),
            Role: Text(s.Find("role")!),
            Line: OptionalText(s, "line"),
            Machine: OptionalText(s, "machine"),
            Agent: OptionalText(s, "agent"),
            Model: OptionalText(s, "model"),
            Repository: OptionalText(s, "repository"),
            Brief: Text(s.Find("brief")!),
            Starts: starts is null ? Array.Empty<SeatStartDefinition>() : starts.Items.Cast<DefinitionMap>().Select(BuildStart).ToList(),
            Grants: TextList(s, "grants"),
            Rules: TextList(s, "rules"),
            Secrets: TextList(s, "secrets"),
            FactorySkills: skills is null ? Array.Empty<string>() : TextList(skills, "factory"),
            FleetSkills: skills is null ? Array.Empty<string>() : TextList(skills, "fleet"),
            Files: TextList(s, "files"));
    }

    private static SeatStartDefinition BuildStart(DefinitionMap st)
    {
        var every = OptionalText(st, "every");
        return new SeatStartDefinition(
            Cron: OptionalText(st, "cron"),
            TimeZone: OptionalText(st, "timeZone"),
            Every: every,
            EverySeconds: every is null ? null : IntervalSeconds(every),
            Check: OptionalText(st, "check"),
            Secrets: TextList(st, "secrets"),
            Prompt: Text(st.Find("prompt")!),
            PreventOverlap: st.Find("preventOverlap")?.Value is not DefinitionScalar { Value: "false" });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // HELPERS (each reads a value the schema has already typed)
    // ---------------------------------------------------------------------------------------------------------------

    private static string Text(DefinitionEntry entry) => ((DefinitionScalar)entry.Value).Value;

    private static string? OptionalText(DefinitionMap map, string key) => map.Find(key) is { } e ? Text(e) : null;

    private static IReadOnlyList<string> TextList(DefinitionMap map, string key)
        => map.Find(key) is { } e ? Items(e).Select(i => i.Value).ToList() : Array.Empty<string>();

    private static IReadOnlyList<(string Value, int Line)> Items(DefinitionEntry entry)
        => ((DefinitionList)entry.Value).Items.Cast<DefinitionScalar>().Select(s => (s.Value, s.Line)).ToList();

    private static void Error(List<DefinitionFinding> findings, string file, int line, string message)
        => findings.Add(new DefinitionFinding(DefinitionSeverity.Error, file, line, message));

    private static void Notice(List<DefinitionFinding> findings, string file, int line, string message)
        => findings.Add(new DefinitionFinding(DefinitionSeverity.Notice, file, line, message));
}
