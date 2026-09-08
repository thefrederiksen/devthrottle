using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>A tool refused the call; the message names what was tried and what to do.</summary>
public sealed class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

/// <summary>One session in the week's universe: its row (or null), its human prompts in the week (in time
/// order, ASCII cached), its name, and its records in the week.</summary>
public sealed class SessionEntry
{
    public required string Id { get; init; }
    public required string Id8 { get; init; }
    public MentorSession? Row { get; init; }
    public required List<MentorRecord> Human { get; init; }
    public required List<MentorRecord> Records { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// The port of mentor_tools/surface.py ToolSurface: the eleven tools the mentor agent calls. The text side
/// (session_index, session_prompts, session_outcomes, prompt_search, cite, verify_quote, turn_record, note)
/// needs no metrics document; the metrics side (week_overview, prior_weeks, dimension_candidates) reads the
/// document <see cref="Document"/> builds once from the store, exactly as the reference caches it.
///
/// The numbers week_overview answers are the numbers <see cref="Metrics.BuildMetrics"/> computes, by
/// construction: the document is built live from the store and cached for the surface's lifetime.
/// prior_weeks applies the week's source coverage, exactly the rule the baseline uses. dimension_candidates
/// proposes and the model judges: a fixed rule per dimension over ALL the week's human prompts, and the
/// share it answers is a candidate count from a rule, never a level.
///
/// One method per tool, every one wrapped so the call is logged whether it succeeds or raises: a refusal
/// (<see cref="ToolError"/>) is logged with ok=false and its message; any other exception is logged with
/// ok=false, its type and its message; both are then re-raised unchanged. No method takes an account or
/// tenant argument: the surface is built over one <see cref="MentorStore"/>, bound to one account and week.
///
/// Session arguments resolve by ONE rule everywhere (<see cref="ResolveSession"/>): a full session id;
/// else a unique eight-character hex prefix; else an exact session name that is unique in the week;
/// anything else raises naming what was tried. A minute argument is YYYY-MM-DD HH:MM LOCAL in the
/// account's zone, the same string the prompts file stamps.
///
/// Every text this surface answers is passed through the packet's ASCII transliteration, because that is
/// the form prompts-human.md holds and the form the report checker compares a fragment against.
///
/// Every answer has EXACTLY the keys, values, orders and types the reference answers, and every refusal
/// message is the reference's message character for character: the parity diff reads both.
/// </summary>
public sealed class ToolSurface
{
    public const string MinuteFormat = "yyyy-MM-dd HH:mm";
    private static readonly Regex MinuteRe = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex Id8Re = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    public const int DefaultSearchLimit = 20;
    public const int SnippetMargin = 60;
    public static readonly TimeSpan TurnWindow = TimeSpan.FromMinutes(30);
    public const int TerminalRows = 40;
    public const int MessagesEitherSide = 3;
    public const int MessageChars = 400;
    public const string MessageMarker = " [...]";
    public const int MaxFragment = 200;
    public const string NotesFile = "notes.md";
    public const string Unnamed = "(unnamed session)";
    public const string SummaryScope = "the session row holds one summary, of the session's final state; earlier contexts are not "
        + "summarised separately";
    public const int MaxPriorWeeks = 8;

    /// <summary>The six prompting dimensions of slots.DIMENSIONS, in the rubric's order.</summary>
    public static readonly string[] DimensionKeys =
    {
        "specific_target", "check_agent_can_run", "one_task_per_prompt", "corrections_carry_reason", "not_re_explaining", "session_hygiene",
    };
    public static readonly string[] BothLists = { "specific_target", "check_agent_can_run", "corrections_carry_reason" };
    public const int MultiAskMinWords = 20;
    public const int CorrectionReasonMinWords = 12;

    /// <summary>The two phrase lists dimension_candidates adds to the metrics' word lists; matched on a word
    /// boundary, case-insensitive, by <see cref="MarkerMatch"/>.</summary>
    public static readonly string[] MultiAskMarkers = { "also", "and while you are at it", "then", "after that", "as well" };
    public static readonly string[] ReExplainMarkers = { "as I said", "again", "I told you", "like I said" };

    public MentorStore Store { get; }
    public ToolLog Log { get; }
    public string RunDir { get; }
    public string WeekLabel { get; }

    private readonly Ascii _ascii = new();
    private readonly object _gate = new();
    private bool _built;
    private List<string> _ordered = new();
    private Dictionary<string, SessionEntry> _entries = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _names = new(StringComparer.Ordinal);
    private ReportCheck.Prompts? _promptsIndex;
    private WeekData? _week;
    private Dictionary<string, object?>? _document;

    public ToolSurface(MentorStore store, ToolLog log)
    {
        Store = store;
        Log = log;
        RunDir = Path.GetDirectoryName(log.Path) ?? ".";
        WeekLabel = store.WeekLabel;
        MentorReaders.ParseIsoWeek(WeekLabel);
    }

    /// <summary>The report checker's index over the week's human prompts, as the reference builds it.</summary>
    public ReportCheck.Prompts PromptsIndex { get { Build(); return _promptsIndex!; } }

    // ------------------------------------------------------------------ the index

    private void Build()
    {
        lock (_gate)
        {
            if (_built) return;
            var week = Store.WeekDataFor(WeekLabel);
            var human = PromptsFile.HumanPrompts(week);
            var sessions = Store.Sessions();
            var ordered = PromptsFile.OrderedSessions(human, sessions);
            var entries = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
            foreach (var entry in ordered)
            {
                var name = _ascii.Text(PromptsFile.SessionName(entry.Row, entry.Records));
                foreach (var record in entry.Records)
                    record.AsciiText ??= _ascii.Text(record.Text);
                entries[entry.Id] = new SessionEntry
                {
                    Id = entry.Id, Id8 = PromptsFile.Id8(entry.Id), Row = entry.Row, Human = entry.Records,
                    Records = week.RecordsOf(entry.Id), Name = name,
                };
            }
            foreach (var (sid, row) in week.Sessions)
            {
                if (entries.ContainsKey(sid)) continue;
                var name = _ascii.Text(PromptsFile.SessionName(row, new List<MentorRecord>()));
                entries[sid] = new SessionEntry
                {
                    Id = sid, Id8 = PromptsFile.Id8(sid), Row = row, Human = new List<MentorRecord>(),
                    Records = week.RecordsOf(sid), Name = name,
                };
            }
            var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in entries.Values)
            {
                if (!names.TryGetValue(entry.Name, out var ids)) names[entry.Name] = ids = new List<string>();
                ids.Add(entry.Id);
            }
            var index = new ReportCheck.Prompts(human.Count);
            foreach (var entry in entries.Values)
                foreach (var record in entry.Human)
                    index.Add(entry.Name, entry.Id8, Stamp(record.Ts), record.AsciiText!);
            _ordered = ordered.Select(e => e.Id).ToList();
            _entries = entries;
            _names = names;
            _promptsIndex = index;
            _week = week;
            _built = true;
        }
    }

    public string Stamp(DateTime utc) => Store.Zone.Stamp(utc);

    /// <summary>A minute 'YYYY-MM-DD HH:MM' local as a UTC instant; anything else is refused with the
    /// reference's message.</summary>
    public DateTime ParseMinute(string? at)
    {
        if (at is null || !MinuteRe.IsMatch(at))
            throw new ToolError("a minute is 'YYYY-MM-DD HH:MM' local (the prompts file's stamp), got '" + (at ?? "None") + "'.");
        // The regular expression admits a trailing newline (as Python's $ does) that strptime then refuses.
        var trailing = at.Length > MinuteFormat.Length ? at.Substring(MinuteFormat.Length) : "";
        if (trailing.Length > 0)
            throw new ToolError("a minute is 'YYYY-MM-DD HH:MM' local, got '" + at + "': unconverted data remains: " + trailing);
        if (!DateTime.TryParseExact(at, MinuteFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
            throw new ToolError("a minute is 'YYYY-MM-DD HH:MM' local, got '" + at + "': time data '" + at + "' does not match format '%Y-%m-%d %H:%M'");
        return Store.Zone.ToUtc(naive);
    }

    /// <summary>A full session id; else a unique id8 prefix; else an exact session name unique in the week.
    /// Anything else raises naming what was tried and the candidates of an ambiguous name.</summary>
    public SessionEntry ResolveSession(string? session)
    {
        Build();
        if (session is null || PyText.Strip(session).Length == 0)
            throw new ToolError("a session is a full id, an id8 prefix or an exact session name; got " + PyText.Repr(session));
        if (_entries.TryGetValue(session, out var direct)) return direct;
        if (Id8Re.IsMatch(session))
        {
            var matches = _entries.Keys.Where(sid => sid.StartsWith(session, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1) return _entries[matches[0]];
            if (matches.Count > 1)
                throw new ToolError("id8 '" + session + "' is not unique in " + WeekLabel + ": "
                    + string.Join(", ", matches.OrderBy(m => m, StringComparer.Ordinal)));
        }
        var ids = _names.TryGetValue(session, out var named) ? named : new List<string>();
        if (ids.Count == 1) return _entries[ids[0]];
        if (ids.Count > 1)
            throw new ToolError("session name '" + session + "' names " + ids.Count + " sessions in "
                + WeekLabel + "; use an id8 instead: " + string.Join(", ", ids.Select(PromptsFile.Id8).OrderBy(s => s, StringComparer.Ordinal)));
        throw new ToolError("no session '" + session + "' in " + WeekLabel + ": tried it as a full id, "
            + "as an id8 prefix and as an exact session name. session_index() lists the week's sessions.");
    }

    public Dictionary<string, object?> Header(SessionEntry entry)
    {
        var row = entry.Row;
        var human = entry.Human;
        string repo, agent, startedBy, startLocal, endLocal;
        bool startedInWeek;
        long? agentTurns, peakContext;
        string? ending;
        if (row is not null)
        {
            repo = !string.IsNullOrEmpty(row.RepoName) ? row.RepoName : PromptsFile.NoRepoName;
            agent = string.IsNullOrEmpty(row.Agent) ? "unknown" : row.Agent;
            startedBy = row.OriginKind;
            startLocal = Stamp(row.Started);
            endLocal = row.Ended is not null ? Stamp(row.Ended.Value) : PromptsFile.OpenAtExtract;
            startedInWeek = _week!.Start <= row.Started && row.Started < _week.End;
            agentTurns = row.AgentTurns;
            peakContext = row.PeakContext;
            ending = row.Ending;
        }
        else
        {
            repo = PromptsFile.NoSessionRow;
            var agents = entry.Records.Where(r => !string.IsNullOrEmpty(r.Agent)).Select(r => r.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
            agent = (agents.Count > 0 ? string.Join("/", agents) : "unknown") + " (from the prompt log)";
            startedBy = PromptsFile.NoSessionRow;
            startLocal = PromptsFile.NoSessionRow;
            endLocal = PromptsFile.NoSessionRow;
            DateTime? earliest = null;
            foreach (var r in Store.Records())
                if (r.Session == entry.Id && (earliest is null || r.Ts < earliest)) earliest = r.Ts;
            startedInWeek = earliest is not null && _week!.Start <= earliest && earliest < _week.End;
            agentTurns = null;
            peakContext = null;
            ending = null;
        }
        return new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["id8"] = entry.Id8,
            ["name"] = entry.Name,
            ["repo"] = _ascii.Text(repo),
            ["agent"] = agent,
            ["started_by"] = startedBy,
            ["start_local"] = startLocal,
            ["end_local"] = endLocal,
            ["started_in_week"] = startedInWeek,
            ["human_prompts"] = human.Count,
            ["agent_turns"] = agentTurns,
            ["peak_context"] = peakContext,
            ["ending"] = ending,
            ["first_prompt_local"] = human.Count > 0 ? Stamp(human[0].Ts) : null,
            ["last_prompt_local"] = human.Count > 0 ? Stamp(human[^1].Ts) : null,
        };
    }

    public Dictionary<string, object?> PromptEntry(MentorRecord record) => new()
    {
        ["at"] = Stamp(record.Ts),
        ["ts_utc"] = MentorReaders.StampUtc(record.Ts),
        ["modality"] = record.OriginModality,
        ["surface"] = record.OriginSurface,
        ["words"] = record.Words,
        ["stripped"] = record.OriginStrip,
        ["text"] = record.AsciiText,
    };

    // ------------------------------------------------------------------ the logged call

    private T Logged<T>(string tool, Dictionary<string, object?> args, Func<(T Answer, string Summary, Dictionary<string, object?>? Extra)> call)
    {
        var started = Stopwatch.StartNew();
        (T Answer, string Summary, Dictionary<string, object?>? Extra) result;
        try
        {
            result = call();
        }
        catch (ToolError error)
        {
            Log.Record(tool, args, false, error.Message, started.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (Exception error)
        {
            // EVERY other failure is a call too: the log records the type and the message, ok false, and
            // the exception goes on unchanged - an audit built from the log cannot leave out a whole class
            // of failure.
            Log.Record(tool, args, false, error.GetType().Name + ": " + error.Message, started.Elapsed.TotalMilliseconds);
            throw;
        }
        Log.Record(tool, args, true, result.Summary, started.Elapsed.TotalMilliseconds, result.Extra);
        return result.Answer;
    }

    // ------------------------------------------------------------------ orientation

    /// <summary>One row per session with at least one human prompt in the week, in the prompts file's
    /// session index order.</summary>
    public List<Dictionary<string, object?>> SessionIndex()
        => Logged<List<Dictionary<string, object?>>>("session_index", new Dictionary<string, object?>(), () =>
        {
            Build();
            var rows = _ordered.Select(sid => Header(_entries[sid])).ToList();
            return (rows, rows.Count + " sessions", null);
        });

    // ------------------------------------------------------------------ drilling in

    /// <summary>The session's header and every human prompt of it inside the week, in time order.</summary>
    public Dictionary<string, object?> SessionPrompts(string? session)
        => Logged("session_prompts", new Dictionary<string, object?> { ["session"] = session }, () =>
        {
            var entry = ResolveSession(session);
            var answer = new Dictionary<string, object?>
            {
                ["header"] = Header(entry),
                ["prompts"] = entry.Human.Select(PromptEntry).ToList(),
            };
            return (answer, entry.Id8 + " " + entry.Human.Count + " prompts", new Dictionary<string, object?> { ["session_id8"] = entry.Id8 });
        });

    /// <summary>Every human prompt of the week with its session entry, in time order (ts, then pid).</summary>
    public List<(MentorRecord Record, SessionEntry Entry)> WeekHuman()
    {
        Build();
        var pairs = new List<(MentorRecord, SessionEntry)>();
        foreach (var entry in _entries.Values)
            foreach (var record in entry.Human)
                pairs.Add((record, entry));
        return pairs.OrderBy(p => p.Item1.Ts).ThenBy(p => p.Item1.Pid, StringComparer.Ordinal).ToList();
    }

    private static void CheckLimit(int limit)
    {
        if (limit < 1) throw new ToolError("limit is a whole number of at least 1, got " + limit.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>One search or candidate hit: the session (full id, id8, name), the minute, the word count
    /// and a one-line snippet of the ASCII text around [position, position + length).</summary>
    public Dictionary<string, object?> Hit(MentorRecord record, SessionEntry entry, int position, int length)
    {
        var text = record.AsciiText!;
        var begin = Math.Max(0, position - SnippetMargin);
        var end = Math.Min(text.Length, position + length + SnippetMargin);
        return new Dictionary<string, object?>
        {
            ["session_id"] = entry.Id,
            ["session_id8"] = entry.Id8,
            ["session_name"] = entry.Name,
            ["at"] = Stamp(record.Ts),
            ["words"] = record.Words,
            ["snippet"] = string.Join(" ", PyText.Split(text.Substring(begin, end - begin))),
        };
    }

    /// <summary>Case-insensitive substring search over the week's human prompt texts, in time order; with
    /// <paramref name="session"/> (resolved by the one rule) over that session's human prompts only, and
    /// total is then the count in that session.</summary>
    public Dictionary<string, object?> PromptSearch(string? query, int limit = DefaultSearchLimit, string? session = null)
        => Logged<Dictionary<string, object?>>("prompt_search", new Dictionary<string, object?> { ["query"] = query, ["limit"] = limit, ["session"] = session }, () =>
        {
            if (query is null || PyText.Strip(query).Length == 0)
                throw new ToolError("query must be a non-empty string.");
            CheckLimit(limit);
            var records = WeekHuman();
            SessionEntry? only = null;
            if (session is not null)
            {
                only = ResolveSession(session);
                records = records.Where(p => p.Entry.Id == only.Id).ToList();
            }
            // The texts are ASCII after transliteration, so a per-character lower-casing is Python's str.lower on them.
            var needle = query.ToLowerInvariant();
            var hits = new List<Dictionary<string, object?>>();
            var total = 0;
            foreach (var (record, entry) in records)
            {
                var position = record.AsciiText!.ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal);
                if (position < 0) continue;
                total++;
                if (hits.Count >= limit) continue;
                hits.Add(Hit(record, entry, position, PyText.Length(query)));
            }
            var answer = new Dictionary<string, object?> { ["query"] = query, ["total"] = total, ["hits"] = hits };
            var summary = "total=" + total + " hits=" + hits.Count;
            if (only is not null) summary += " session=" + only.Id8;
            return (answer, summary, null);
        });

    // ------------------------------------------------------------------ the metrics side

    /// <summary>The earliest of <paramref name="markers"/> found in <paramref name="text"/> on a word boundary,
    /// case-insensitive: (position, marker), or null when no marker is in the text.</summary>
    public static (int Position, string Marker)? MarkerMatch(string text, IReadOnlyList<string> markers)
    {
        (int Position, string Marker)? best = null;
        foreach (var marker in markers)
        {
            var found = Regex.Match(text, @"\b" + Regex.Escape(marker) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (found.Success && (best is null || found.Index < best.Value.Position)) best = (found.Index, marker);
        }
        return best;
    }

    /// <summary>The metrics document for the run's week (<c>metrics.build_metrics</c>), built once from the store.</summary>
    public Dictionary<string, object?> Document()
    {
        lock (_gate)
        {
            _document ??= Metrics.BuildMetrics(Store.World(), WeekLabel, ToolLog.UtcNow(), Store.Hours);
            return _document;
        }
    }

    private static Dictionary<string, object?> Dict(object? value, string what)
        => value as Dictionary<string, object?> ?? throw new MentorDataException("The document's " + what + " is not an object.");

    /// <summary>The metrics document for the run's week, computed live from the store.</summary>
    public Dictionary<string, object?> WeekOverview()
        => Logged<Dictionary<string, object?>>("week_overview", new Dictionary<string, object?>(), () =>
        {
            var document = Document();
            var count = Metrics.GroupOrder.Sum(group => Dict(document[group], group).Count);
            return (document, Metrics.GroupOrder.Length + " groups, " + count + " metrics", null);
        });

    /// <summary>`&lt;group&gt;.&lt;key&gt;` over up to n prior ISO weeks, newest first; a week any of the metric's
    /// sources does not fully cover answers {"covered": false, "sources": [...]}. Also the baseline the
    /// document carries for the metric and the weeks that fed it.</summary>
    public Dictionary<string, object?> PriorWeeks(string? metric, int n = 4)
        => Logged<Dictionary<string, object?>>("prior_weeks", new Dictionary<string, object?> { ["metric"] = metric, ["n"] = n }, () =>
        {
            if (metric is null || metric.Count(c => c == '.') != 1)
                throw new ToolError("metric is '<group>.<key>', for example rhythm.sessions_started; known groups: " + string.Join(", ", Metrics.GroupOrder));
            var parts = metric.Split('.');
            var group = parts[0];
            var key = parts[1];
            if (!Metrics.GroupIds.TryGetValue(group, out var keys))
                throw new ToolError("unknown metric group '" + group + "'; known groups: " + string.Join(", ", Metrics.GroupOrder));
            if (!keys.Contains(key))
                throw new ToolError("unknown metric '" + key + "' in group '" + group + "'; its keys: " + string.Join(", ", keys));
            if (n < 1 || n > MaxPriorWeeks)
                throw new ToolError("n is a whole number from 1 to " + MaxPriorWeeks + ", got " + n.ToString(CultureInfo.InvariantCulture));
            var sources = Metrics.MetricSources(key);
            var labels = MentorReaders.PriorWeeks(WeekLabel, n);
            labels.Reverse();
            var weeks = new List<object?>();
            var coveredCount = 0;
            foreach (var label in labels)
            {
                var week = Store.Week(label);
                if (!week.CoveredBy(sources))
                {
                    weeks.Add(new Dictionary<string, object?> { ["week"] = label, ["covered"] = false, ["sources"] = sources.Cast<object?>().ToList() });
                    continue;
                }
                var (values, _) = Metrics.ComputeAll(week, forBaseline: true);
                weeks.Add(new Dictionary<string, object?> { ["week"] = label, ["covered"] = true, ["value"] = values[key] });
                coveredCount++;
            }
            var entry = Dict(Dict(Document()[group], group)[key], metric);
            var answer = new Dictionary<string, object?>
            {
                ["metric"] = metric,
                ["unit"] = entry["unit"],
                ["definition"] = entry["definition"],
                ["source"] = entry["source"],
                ["week"] = WeekLabel,
                ["value"] = entry["value"],
                ["baseline"] = entry["baseline"],
                ["baseline_weeks"] = entry["baseline_weeks"],
                ["prior_weeks"] = weeks,
            };
            return (answer, metric + ": " + weeks.Count + " prior weeks, " + coveredCount + " covered", null);
        });

    /// <summary>A tool proposes, the model judges: over ALL the week's human prompts, how many a fixed rule
    /// flags for one of the six prompting dimensions, the rule in one sentence, and up to limit flagged
    /// prompts in time order - for the dimensions with a with/without pair, two lists. share is a candidate
    /// count from a rule, never a level.</summary>
    public Dictionary<string, object?> DimensionCandidates(string? dimension, int limit = DefaultSearchLimit)
        => Logged<Dictionary<string, object?>>("dimension_candidates", new Dictionary<string, object?> { ["dimension"] = dimension, ["limit"] = limit }, () =>
        {
            if (dimension is null || !DimensionKeys.Contains(dimension))
                throw new ToolError("dimension is one of " + string.Join(", ", DimensionKeys) + "; got " + PyText.Repr(dimension));
            CheckLimit(limit);
            var pairs = WeekHuman();
            var total = pairs.Count;
            var answer = new Dictionary<string, object?> { ["dimension"] = dimension, ["total"] = (long)total, ["heuristic"] = true };
            var with = new List<Dictionary<string, object?>>();
            var without = new List<Dictionary<string, object?>>();
            var flagged = new List<Dictionary<string, object?>>();
            string rule;
            switch (dimension)
            {
                case "specific_target":
                    foreach (var (record, entry) in pairs)
                    {
                        if (Metrics.HasSpecificityMarker(record.Text)) with.Add(Hit(record, entry, 0, SnippetMargin));
                        else if (record.Words >= Metrics.ShortPromptWords) without.Add(Hit(record, entry, 0, SnippetMargin));
                    }
                    flagged = with;
                    rule = "with: prompts carrying a specificity marker - a path, an issue number, a URL or a quoted "
                        + "string (metrics.has_specificity_marker); without: prompts of at least "
                        + Metrics.ShortPromptWords + " words with no marker; flagged counts the with list.";
                    break;
                case "check_agent_can_run":
                    foreach (var (record, entry) in pairs)
                    {
                        if (Metrics.HasDoneCriteria(record.Text, record.Words)) with.Add(Hit(record, entry, 0, SnippetMargin));
                        else if (record.Words >= Metrics.DoneCriteriaMinWords) without.Add(Hit(record, entry, 0, SnippetMargin));
                    }
                    flagged = with;
                    rule = "with: prompts of at least " + Metrics.DoneCriteriaMinWords + " words carrying one of "
                        + string.Join(", ", Metrics.DoneCriteriaWords) + " (metrics.has_done_criteria); without: prompts of "
                        + "at least " + Metrics.DoneCriteriaMinWords + " words with none; flagged counts the with list.";
                    break;
                case "one_task_per_prompt":
                    foreach (var (record, entry) in pairs)
                    {
                        if (record.Words < MultiAskMinWords) continue;
                        var found = MarkerMatch(AsciiOf(record), MultiAskMarkers);
                        if (found is null) continue;
                        var one = Hit(record, entry, found.Value.Position, found.Value.Marker.Length);
                        one["marker"] = found.Value.Marker;
                        flagged.Add(one);
                    }
                    rule = "flagged: prompts of at least " + MultiAskMinWords + " words carrying one of "
                        + string.Join(", ", MultiAskMarkers) + " on a word boundary (MULTI_ASK_MARKERS).";
                    break;
                case "corrections_carry_reason":
                    foreach (var (record, entry) in pairs)
                    {
                        if (!Metrics.IsCorrectionCandidate(record.Text)) continue;
                        var one = Hit(record, entry, 0, SnippetMargin);
                        flagged.Add(one);
                        (record.Words >= CorrectionReasonMinWords ? with : without).Add(one);
                    }
                    rule = "flagged: prompts whose first words are a correction marker (metrics.is_correction_candidate: "
                        + string.Join(", ", Metrics.CorrectionMarkers.Select(PyText.Strip)) + "); with: those of at least "
                        + CorrectionReasonMinWords + " words; without: those under " + CorrectionReasonMinWords
                        + " words, a bare no carrying no reason.";
                    break;
                case "not_re_explaining":
                {
                    var clusters = ListOf(Dict(Dict(Document()["prompt_shape"], "prompt_shape")["repeated_instruction_clusters"], "repeated_instruction_clusters")["value"], "repeated_instruction_clusters value");
                    var members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cluster in clusters)
                        foreach (var pid in ListOf(Dict(cluster, "cluster")["prompt_ids"], "a cluster's prompt_ids"))
                            members.Add(pid as string ?? throw new MentorDataException("A prompt id in a cluster is not a string."));
                    foreach (var (record, entry) in pairs)
                    {
                        var inCluster = members.Contains(record.Pid);
                        var found = MarkerMatch(AsciiOf(record), ReExplainMarkers);
                        if (!inCluster && found is null) continue;
                        var one = Hit(record, entry, found?.Position ?? 0, found?.Marker.Length ?? SnippetMargin);
                        one["marker"] = found?.Marker;
                        one["in_cluster"] = inCluster;
                        flagged.Add(one);
                    }
                    rule = "flagged: members of the overview's repeated_instruction_clusters (" + clusters.Count
                        + " clusters, " + members.Count + " prompts) plus prompts carrying one of "
                        + string.Join(", ", ReExplainMarkers) + " on a word boundary (RE_EXPLAIN_MARKERS).";
                    break;
                }
                default:
                {
                    var p90 = Dict(Dict(Document()["arc"], "arc")["peak_context_tokens_p90"], "peak_context_tokens_p90")["value"];
                    foreach (var (record, entry) in pairs)
                    {
                        var peak = entry.Row?.PeakContext;
                        if (p90 is not long threshold || peak is null || peak.Value < threshold) continue;
                        var one = Hit(record, entry, 0, SnippetMargin);
                        one["peak_context"] = peak.Value;
                        flagged.Add(one);
                    }
                    rule = "flagged: prompts in sessions whose peak_context is at or above the overview's "
                        + "arc.peak_context_tokens_p90 (" + (p90 is null ? "no session in the week reports a peak context, so nothing is flagged"
                            : PyNumbers.Str(p90) + " tokens") + ").";
                    break;
                }
            }
            answer["flagged"] = (long)flagged.Count;
            answer["share"] = Metrics.Share(flagged.Count, total);
            answer["rule"] = rule;
            if (BothLists.Contains(dimension))
            {
                answer["with_total"] = (long)with.Count;
                answer["without_total"] = (long)without.Count;
                answer["with"] = with.Take(limit).Cast<object?>().ToList();
                answer["without"] = without.Take(limit).Cast<object?>().ToList();
            }
            else
            {
                answer["hits"] = flagged.Take(limit).Cast<object?>().ToList();
            }
            var summary = "dimension=" + dimension + " total=" + total + " flagged=" + flagged.Count + " share=" + PyNumbers.Str(answer["share"]);
            return (answer, summary, null);
        });

    private static List<object?> ListOf(object? value, string what)
        => value as List<object?> ?? throw new MentorDataException("The document's " + what + " is not a list.");

    /// <summary>The ASCII text the index cached on a human record; every record the surface answers has one.</summary>
    private static string AsciiOf(MentorRecord record)
        => record.AsciiText ?? throw new MentorDataException("The record at " + record.Pid + " has no ASCII text; the index was not built over it.");

    private Dictionary<string, object?> CutMessage(JsonElement message)
    {
        var parts = new List<string>();
        if (message.TryGetProperty("Parts", out var partsElement) && partsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in partsElement.EnumerateArray())
            {
                var kind = StringOrNull(part, "Kind");
                if (kind == "Text") parts.Add(_ascii.Text(StringOrNull(part, "Text") ?? ""));
                else if (kind == "ToolUse") parts.Add("[ToolUse " + (StringOrNull(part, "ToolName") ?? "None") + "]");
                else if (kind == "ToolResult") parts.Add("[ToolResult]");
                else parts.Add("[" + (kind ?? "None") + "]");
            }
        }
        var text = string.Join(" ", PyText.Split(string.Join(" ", parts)));
        if (PyText.Length(text) > MessageChars)
            text = new string(text.EnumerateRunes().Take(MessageChars).SelectMany(r => r.ToString()).ToArray()) + MessageMarker;
        return new Dictionary<string, object?>
        {
            ["role"] = StringOrNull(message, "Role"),
            ["timestamp"] = StringOrNull(message, "Timestamp"),
            ["text"] = text,
        };
    }

    private static string? StringOrNull(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static JsonElement? ObjectOrNull(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : null;

    private static readonly Regex IsoStampRe = new(
        @"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,9}))?)?(Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant);

    /// <summary>Python's <c>datetime.fromisoformat</c> on a conversation message's Timestamp, then UTC when
    /// naive: the instant, or null when the text is not a timestamp.</summary>
    private static DateTime? ParseIsoStamp(string? stamp)
    {
        if (stamp is null) return null;
        var match = IsoStampRe.Match(stamp);
        if (!match.Success) return null;
        try
        {
            var when = new DateTime(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0, DateTimeKind.Unspecified);
            if (match.Groups[7].Success)
            {
                var fraction = match.Groups[7].Value.PadRight(7, '0').Substring(0, 7);
                when = when.AddTicks(long.Parse(fraction, CultureInfo.InvariantCulture));
            }
            var offset = TimeSpan.Zero;
            if (match.Groups[8].Success && match.Groups[8].Value != "Z")
            {
                var text = match.Groups[8].Value.Replace(":", "");
                var sign = text[0] == '-' ? -1 : 1;
                offset = new TimeSpan(sign * int.Parse(text.Substring(1, 2), CultureInfo.InvariantCulture), sign * int.Parse(text.Substring(3, 2), CultureInfo.InvariantCulture), 0);
            }
            return DateTime.SpecifyKind(when - offset, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>From the turn-log corpus: the record for that session whose captured_at_utc is nearest to
    /// <paramref name="at"/> (local) within 30 minutes; {"available": false, ...} when there is none.</summary>
    public Dictionary<string, object?> TurnRecord(string? session, string? at)
        => Logged("turn_record", new Dictionary<string, object?> { ["session"] = session, ["at"] = at }, () =>
        {
            var entry = ResolveSession(session);
            var moment = ParseMinute(at);
            var day = Store.Zone.Day(moment);
            var days = new List<string> { day };
            foreach (var edge in new[] { moment - TurnWindow, moment + TurnWindow })
            {
                var edgeDay = Store.Zone.Day(edge);
                if (!days.Contains(edgeDay)) days.Add(edgeDay);
            }
            var candidates = new List<TurnLogRecord>();
            foreach (var oneDay in days) candidates.AddRange(Store.TurnLogRecords(entry.Id, oneDay));
            var thatDay = Store.TurnLogRecords(entry.Id, day);
            var within = candidates.Where(r => (r.Captured - moment).Duration() <= TurnWindow).ToList();
            var extra = new Dictionary<string, object?> { ["session_id8"] = entry.Id8 };
            if (within.Count == 0)
            {
                var reason = "the corpus has no record for session " + entry.Id8 + " within 30 minutes of " + at
                    + " local; " + (thatDay.Count > 0 ? thatDay.Count + " records for that session on " + day
                        : "no record for that session on " + day);
                var missing = new Dictionary<string, object?>
                {
                    ["available"] = false, ["reason"] = reason, ["session_id8"] = entry.Id8, ["records_that_day"] = thatDay.Count,
                };
                return (missing, "available=false records_that_day=" + thatDay.Count, extra);
            }
            var record = within.OrderBy(r => (r.Captured - moment).Duration()).ThenBy(r => r.Captured).First();
            var captured = record.Captured;
            var rows = new List<string>();
            var terminal = ObjectOrNull(record.Root, "terminal");
            if (terminal is not null && terminal.Value.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
                foreach (var row in rowsElement.EnumerateArray())
                    rows.Add(_ascii.Text(row.ValueKind == JsonValueKind.String ? row.GetString() : null));
            rows = rows.Where(row => PyText.Strip(row).Length > 0).ToList();
            if (rows.Count > TerminalRows) rows = rows.Skip(rows.Count - TerminalRows).ToList();
            var before = new List<JsonElement>();
            var after = new List<JsonElement>();
            var conversation = ObjectOrNull(record.Root, "conversation");
            if (conversation is not null && conversation.Value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var stamp = StringOrNull(message, "Timestamp");
                    var when = ParseIsoStamp(stamp)
                        ?? throw new ToolError("a conversation message at " + record.Where + " has an unparseable Timestamp: " + PyText.Repr(stamp));
                    (when <= moment ? before : after).Add(message);
                }
            }
            var momentInfo = ObjectOrNull(record.Root, "moment");
            var answer = new Dictionary<string, object?>
            {
                ["available"] = true,
                ["session_id8"] = entry.Id8,
                ["record_id"] = StringOrNull(record.Root, "record_id"),
                ["captured_at_utc"] = StringOrNull(record.Root, "captured_at_utc"),
                ["captured_at_local"] = Stamp(captured),
                ["offset_seconds"] = (long)(captured - moment).TotalSeconds,
                ["activity_state_before"] = momentInfo is null ? null : StringOrNull(momentInfo.Value, "activity_state_before"),
                ["activity_state_after"] = momentInfo is null ? null : StringOrNull(momentInfo.Value, "activity_state_after"),
                ["rows"] = rows,
                ["messages_before"] = before.Skip(Math.Max(0, before.Count - MessagesEitherSide)).Select(CutMessage).ToList(),
                ["messages_after"] = after.Take(MessagesEitherSide).Select(CutMessage).ToList(),
                ["records_that_day"] = thatDay.Count,
            };
            var summary = "available=true record=" + (StringOrNull(record.Root, "record_id") ?? "None") + " offset_s="
                + (long)(captured - moment).TotalSeconds + " rows=" + rows.Count;
            return (answer, summary, extra);
        });

    /// <summary>From the session row: what the session produced and how it ended; {"row": false} with the
    /// prompt-log header when the session has no row.</summary>
    public Dictionary<string, object?> SessionOutcomes(string? session)
        => Logged("session_outcomes", new Dictionary<string, object?> { ["session"] = session }, () =>
        {
            var entry = ResolveSession(session);
            var header = Header(entry);
            var raw = Store.SessionRow(entry.Id);
            var extra = new Dictionary<string, object?> { ["session_id8"] = entry.Id8 };
            if (raw is null)
                return (new Dictionary<string, object?> { ["row"] = false, ["header"] = header }, entry.Id8 + " row=false", extra);
            List<string> Items(List<object?>? column, string name)
                => (column ?? new List<object?>()).Select(x => _ascii.Text(PyText.Str(x, "session_history row " + entry.Id + " " + name))).ToList();
            var commits = Items(raw.Commits, "CommitsJson");
            var pullRequests = Items(raw.PullRequests, "PullRequestsJson");
            var leftUnverified = Items(raw.LeftUnverified, "LeftUnverifiedJson");
            var answer = new Dictionary<string, object?>
            {
                ["row"] = true,
                ["header"] = header,
                ["summary_kind"] = raw.Entity.SummaryKind,
                ["summary_text"] = !string.IsNullOrEmpty(raw.Entity.SummaryText) ? _ascii.Text(raw.Entity.SummaryText) : null,
                ["what_was_built"] = Items(raw.WhatWasBuilt, "WhatWasBuiltJson"),
                ["left_unverified"] = leftUnverified,
                ["branches"] = Items(raw.Branches, "BranchesJson"),
                ["pull_requests"] = pullRequests,
                ["commits"] = commits,
                ["ending"] = raw.Entity.EndingKind,
                ["agent_turns"] = raw.Entity.AgentTurnCount,
                ["turn_count"] = raw.Entity.TurnCount,
                ["start_local"] = header["start_local"],
                ["end_local"] = header["end_local"],
                ["summary_scope"] = SummaryScope,
            };
            var summary = entry.Id8 + " row=true commits=" + commits.Count + " prs=" + pullRequests.Count + " unverified=" + leftUnverified.Count;
            return (answer, summary, extra);
        });

    // ------------------------------------------------------------------ keeping itself honest

    private List<MentorRecord> PromptsAt(SessionEntry entry, string at) => entry.Human.Where(r => Stamp(r.Ts) == at).ToList();

    /// <summary>The prompt(s) the developer sent in that session at that local minute, with the citation
    /// string exactly as the report checker reads it.</summary>
    public Dictionary<string, object?> Cite(string? session, string? at)
        => Logged("cite", new Dictionary<string, object?> { ["session"] = session, ["at"] = at }, () =>
        {
            var entry = ResolveSession(session);
            ParseMinute(at);
            var found = PromptsAt(entry, at!);
            if (found.Count == 0)
            {
                var stamps = entry.Human.Select(r => Stamp(r.Ts)).ToList();
                var earlier = stamps.Where(s => string.CompareOrdinal(s, at) < 0).ToList();
                var later = stamps.Where(s => string.CompareOrdinal(s, at) > 0).ToList();
                throw new ToolError("no human prompt in session " + entry.Id8 + " (" + entry.Name + ") at " + at
                    + "; nearest before: " + (earlier.Count > 0 ? earlier[^1] : "none")
                    + "; nearest after: " + (later.Count > 0 ? later[0] : "none") + ".");
            }
            var citation = entry.Name + ", " + at;
            var prompts = found.Select(r => new Dictionary<string, object?>
            {
                ["text"] = r.AsciiText, ["words"] = r.Words, ["modality"] = r.OriginModality, ["surface"] = r.OriginSurface,
            }).ToList();
            var answer = new Dictionary<string, object?> { ["citation"] = citation, ["session_id8"] = entry.Id8, ["prompts"] = prompts };
            return (answer, citation + ": " + found.Count + " prompt(s)", new Dictionary<string, object?> { ["citation"] = citation, ["session_id8"] = entry.Id8 });
        });

    /// <summary>True only when the fragment is a character-for-character substring of a prompt at that place,
    /// under the report checker's rule (at least MinFragment characters after stripping whitespace, or a whole
    /// prompt), and at most 200 characters. Beside ok: how many human prompts the session has at that minute
    /// and how many of them hold the fragment.</summary>
    public Dictionary<string, object?> VerifyQuote(string? session, string? at, string? fragment)
        => Logged("verify_quote", new Dictionary<string, object?> { ["session"] = session, ["at"] = at, ["fragment"] = fragment }, () =>
        {
            var entry = ResolveSession(session);
            ParseMinute(at);
            var citation = entry.Name + ", " + at;
            var extra = new Dictionary<string, object?> { ["citation"] = citation, ["fragment"] = fragment, ["session_id8"] = entry.Id8 };
            if (fragment is null) throw new ToolError("fragment must be a string.");
            var found = PromptsAt(entry, at!);
            var matched = found.Count(r => r.AsciiText!.Contains(fragment, StringComparison.Ordinal));
            var counts = " " + matched + " of the " + found.Count + " prompts at that minute hold the fragment.";
            Dictionary<string, object?> answer;
            var length = PyText.Length(fragment);
            if (length > MaxFragment)
                answer = new Dictionary<string, object?> { ["ok"] = false, ["reason"] = "the fragment is " + length + " characters; at most " + MaxFragment + " are quoted." };
            else if (fragment.Contains('"') || fragment.Contains('\n') || fragment.Contains('\r'))
                answer = new Dictionary<string, object?> { ["ok"] = false, ["reason"] = "a fragment cannot carry a double quotation mark or a line break; the citation form cannot hold it." };
            else if (found.Count == 0)
                answer = new Dictionary<string, object?> { ["ok"] = false, ["reason"] = "no human prompt in session " + entry.Id8 + " at " + at + "." };
            else
            {
                var line = citation + " (\"" + fragment + "\")";
                try
                {
                    ReportCheck.CheckText(line, PromptsIndex);
                    answer = new Dictionary<string, object?> { ["ok"] = true, ["reason"] = "the fragment is a substring of a prompt at " + citation + "." };
                }
                catch (ReportCheckException error)
                {
                    // The first failure is the fragment's; the checker's closing line only says the
                    // one-line report proved nothing, which is known.
                    var first = PyText.SplitLines(error.Message)[0];
                    var reason = first.StartsWith("line 1: ", StringComparison.Ordinal) ? first.Substring("line 1: ".Length) : first;
                    answer = new Dictionary<string, object?> { ["ok"] = false, ["reason"] = reason };
                }
            }
            if (found.Count > 1) answer["reason"] = (string)answer["reason"]! + counts;
            answer["prompts_at_minute"] = found.Count;
            answer["prompts_matched"] = matched;
            var ok = (bool)answer["ok"]!;
            extra["verified"] = ok;
            return (answer, "verified=" + (ok ? "true" : "false"), extra);
        });

    /// <summary>Append one paragraph to the run's notes.md; answer the note count.</summary>
    public Dictionary<string, object?> Note(string? text)
        => Logged<Dictionary<string, object?>>("note", new Dictionary<string, object?> { ["chars"] = text is null ? 0 : PyText.Length(text) }, () =>
        {
            if (text is null || PyText.Strip(text).Length == 0)
                throw new ToolError("a note must be a non-empty string.");
            var path = Path.Combine(RunDir, NotesFile);
            var paragraph = string.Join(" ", PyText.Split(text));
            File.AppendAllText(path, _ascii.Text(paragraph) + "\n\n", new UTF8Encoding(false));
            var count = File.ReadAllText(path, new UTF8Encoding(false)).Replace("\r\n", "\n").Split("\n\n").Count(block => PyText.Strip(block).Length > 0);
            return (new Dictionary<string, object?> { ["notes"] = count }, "note " + count, null);
        });
}
