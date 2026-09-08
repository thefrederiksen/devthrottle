using System.Globalization;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of the reference's <c>metrics.py</c> from its DEFINITIONS down: the metrics document, byte for
/// byte. Every metric definition, the group order, the helpers, the eight compute groups, the baseline
/// rule and the assembly are the reference's, sentence for sentence, and the numbers it answers are
/// compared against the reference's own document in the parity test.
///
/// THE VALUE MODEL. The document is built from the plain values <see cref="ParityJson"/> writes - null,
/// bool, string, <c>long</c> for a Python int, <c>double</c> for a Python float, insertion-ordered
/// dictionaries and lists - so the port always knows which numbers are floats: a share is a double even
/// when it is whole, a count is a long, and a baseline is a long exactly when the reference's
/// <c>baseline_of</c> made it one.
///
/// THE FLOATS. Every rounding goes through <see cref="PyNumbers.Round"/> (the exact half-to-even rule of
/// Python's <c>round</c>); every duration through <see cref="PyNumbers.TotalSeconds"/>; every sum runs in the
/// reference's operation order, which is the readers' row order.
/// </summary>
public static class Metrics
{
    public const int BaselineWeeks = 4;
    public const int MinBaselineWeeks = 2;

    /// <summary>The four sources a metric can rest on (a DEFINITION's source is one or two joined by +).</summary>
    public static readonly string[] SourceNames = { "prompt-log", "session_history", "activity_events", "dictation_transcripts" };
    public const string BaselineNotePrefix = "no baseline yet: ";

    public static readonly HashSet<string> WaitingStates = new(StringComparer.Ordinal) { "WaitingForInput", "WaitingForPerm" };

    public static readonly string[] CorrectionMarkers =
    {
        "no,", "no ", "nope", "wrong", "not what i", "that's not", "thats not", "still ", "again",
        "you didn't", "you did not", "stop", "undo", "revert",
    };
    public static readonly string[] DoneCriteriaWords = { "verify", "prove", "test", "until", "must", "should show", "expected" };
    public const int DoneCriteriaMinWords = 20;
    public const int ShortPromptWords = 8;
    public const int LongPromptWords = 300;
    public const int ClusterMinWords = 8;
    public const int ClusterShingle = 5;
    public const double ClusterJaccard = 0.5;
    public const int ClusterMinSize = 3;
    public const int ClusterMinSessions = 2;
    public const int ClusterTop = 20;
    public const int CorrectionSessionMinPrompts = 10;
    public const int WakingStartHour = 8;
    public const int WakingEndHour = 22;
    public const long ContextBucket = 100000;
    public const int ContextBuckets = 10;
    public const long Context400K = 400000;
    public const long Context700K = 700000;

    // The reference's patterns with Python's whitespace set where they say \s or \S (PyText).
    // The reference: \S*[\\/]\S*\.[A-Za-z0-9]{1,5}(?=$|[\s,;:)\]'"]) - a token with a slash OR a backslash and a
    // dotted extension. The class must hold both separators: with the slash alone, every Windows path is missed.
    private static readonly Regex PathRe = new(PyText.NonSpaceClass + @"*[\\/]" + PyText.NonSpaceClass + @"*\.[A-Za-z0-9]{1,5}(?=$|" + PyText.SpaceClass + @"|[,;:)\]'""])", RegexOptions.CultureInvariant);
    private static readonly Regex IssueRe = new(@"#\d+", RegexOptions.CultureInvariant);
    private static readonly Regex UrlRe = new("http", RegexOptions.CultureInvariant);
    private static readonly Regex QuoteRe = new("\"[^\"]+\"", RegexOptions.CultureInvariant);
    private static readonly Regex NonWordRe = new("(?:(?![a-z0-9])" + PyText.NonSpaceClass + ")+", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceRe = new(PyText.SpaceClass + "+", RegexOptions.CultureInvariant);

    public const string PercentileNote = "Percentiles use the nearest-rank method (the k-th smallest value with "
        + "k = ceil(p/100 * n)); an in-week median is the 50th percentile by that rule. "
        + "The baseline is the conventional median of the prior weeks' values (the mean of the "
        + "two middle values when their count is even).";

    /// <summary>One metric's definition: id -> (group, unit, source, definition). One sentence each; the code does exactly this.</summary>
    public sealed record Definition(string Key, string Group, string Unit, string Source, string Text);

    public static readonly Definition[] Definitions =
    {
        new("prompts_by_hour_of_week", "rhythm", "count", "prompt-log+activity_events",
            "A 7x24 matrix (Monday first, hour 0 first) counting the week's human prompts (origin human, see origin.py) by local weekday and hour of their own timestamp."),
        new("days", "rhythm", "per-day", "prompt-log+activity_events",
            "Per local day Monday to Sunday: whether any human prompt (origin human, see origin.py) fell on it, the local HH:MM of the first and last human prompt, and the human prompt count; active_days counts the days with any human prompt."),
        new("human_prompts_by_surface_by_hour", "rhythm", "count", "prompt-log+activity_events",
            "For each surface desktop, phone, cockpit and unknown, 24 counts of the week's human prompts (origin human, see origin.py) by the local hour of day of their own timestamp, the surface being the prompt's origin_surface."),
        new("business_hours_share", "rhythm", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) whose local timestamp falls inside the account's configured business hours, Monday to Friday, local time (hour at or after start and before end), and the share outside them, with both counts and the configured start and end hours beside the shares."),
        new("sessions_started", "rhythm", "count", "session_history",
            "The number of sessions whose StartedAtUtc falls inside the week in local time."),
        new("session_minutes_median", "rhythm", "minutes", "session_history",
            "The nearest-rank median of EndedAtUtc minus StartedAtUtc in minutes over the week's sessions that have EndedAtUtc set; open sessions are excluded and counted in coverage."),
        new("session_minutes_p90", "rhythm", "minutes", "session_history",
            "The nearest-rank 90th percentile of EndedAtUtc minus StartedAtUtc in minutes over the week's sessions that have EndedAtUtc set."),
        new("max_concurrent_sessions", "rhythm", "count", "session_history",
            "Per local day, the largest number of sessions whose interval from StartedAtUtc to EndedAtUtc (or the extract time while open) covers one instant of that day, and the week's maximum of those."),
        new("endings", "arc", "count", "session_history",
            "The week's sessions counted by EndingKind, with null counted as open, and the same counts split by AgentKind."),
        new("turns_per_session_median", "arc", "turns", "session_history",
            "The nearest-rank median of AgentTurnCount over the week's sessions that report it; sessions with a null AgentTurnCount do not count and are named in coverage."),
        new("turns_per_session_p90", "arc", "turns", "session_history",
            "The nearest-rank 90th percentile of AgentTurnCount over the week's sessions that report it."),
        new("long_tail_session_ids", "arc", "session-ids", "session_history",
            "The ids of the week's sessions whose AgentTurnCount is at or above the nearest-rank 90th percentile."),
        new("peak_context_tokens_median", "arc", "tokens", "session_history",
            "The nearest-rank median of PeakContextTokens over the week's sessions that report it, which only Claude Code rows do."),
        new("peak_context_tokens_p90", "arc", "tokens", "session_history",
            "The nearest-rank 90th percentile of PeakContextTokens over the week's sessions that report it, which only Claude Code rows do."),
        new("share_over_400k", "arc", "share", "session_history",
            "The share of the week's sessions reporting PeakContextTokens whose value is above 400000."),
        new("share_over_700k", "arc", "share", "session_history",
            "The share of the week's sessions reporting PeakContextTokens whose value is above 700000."),
        new("peak_context_histogram", "arc", "count", "session_history",
            "The week's reported PeakContextTokens values counted into buckets of 100000 tokens up to 1000000, with everything at or above 1000000 in the last bucket."),
        new("contexts_per_session_median", "arc", "contexts", "prompt-log",
            "The nearest-rank median, over sessions with at least one user prompt in the week, of the number of distinct non-null contextId values on that session's user prompts in the week; it describes sessions, not the person, so it rests on all user prompts whatever their origin."),
        new("agent_quiet_hours", "waiting", "hours", "activity_events+session_history",
            "For each session that has a session_history row, the hours from every transition into WaitingForInput or WaitingForPerm to the next transition out of it, to its session-exited event, to its EndedAtUtc in session_history, or to the end of the week or the extract time if earlier, clipped to the week and summed over sessions; the product marks a session waiting after ten seconds of terminal silence and cannot tell a finished task, a question, slow thinking, or a background job apart, so these are quiet hours, not hours attributable to the developer."),
        new("all_sessions_quiet_hours", "waiting", "hours", "activity_events+session_history",
            "The hours in the week during which at least one session with a session_history row was open (from its first state-changing event until it reached Exited or its EndedAtUtc) and every open session was inside a quiet interval as defined for agent_quiet_hours; the product marks a session waiting after ten seconds of terminal silence and cannot tell a finished task, a question, slow thinking, or a background job apart, so this is time when every open session was quiet, not time attributable to the developer."),
        new("human_response_minutes_median", "waiting", "minutes", "activity_events+prompt-log",
            "The nearest-rank median, over transitions into a waiting state inside the week, of the minutes until that session's next user prompt or next turn-submitted event, whichever came first, counting a wait only when both its start and that reply fall between 08:00 and 22:00 local on the same local day; a wait with no reply, or whose reply falls outside that window, is left out; it describes sessions, not the person, so it rests on all user prompts whatever their origin."),
        new("waiting_stretches_per_session_median", "waiting", "stretches", "session_history",
            "The nearest-rank median of WaitingStretchCount over the week's sessions where it is not null."),
        new("session_row_lifetime_idle_hours", "waiting", "hours", "session_history",
            "The sum of CumulativeIdleSeconds divided by 3600 over the sessions STARTED in the week where it is not null: the session rows' own lifetime idle counter, which can include time after the week, can be up to five minutes stale, and is not comparable to agent_quiet_hours."),
        new("prompt_words", "prompt_shape", "words", "prompt-log+activity_events",
            "Over the week's human prompts (origin human, see origin.py): the nearest-rank median, 10th and 90th percentiles of the prompt's word count (the product's wordCount, or when origin.py stripped a framing the composer or the agent tool prepended, the count of the words that remain, split on whitespace as the product counts), the share with fewer than 8 words, the share with more than 300 words, and the count of human prompts."),
        new("modality_share", "prompt_shape", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) whose origin_modality is typed or voice; a human prompt always carries one of the two, so there is no unknown key."),
        new("surface_share", "prompt_shape", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) whose origin_surface is desktop, cockpit, phone, or unknown."),
        new("correction_candidates_share", "prompt_shape", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) whose first 40 characters, lower-cased and stripped, start with one of the correction markers listed in the code; a heuristic, not a fact."),
        new("correction_candidate_session_ids", "prompt_shape", "per-session", "prompt-log+activity_events",
            "For each session with 10 or more human prompts (origin human, see origin.py) in the week, that session's correction-candidate share and its human prompt count; a heuristic, not a fact."),
        new("specificity_markers_share", "prompt_shape", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) containing a file path (a token with a slash or backslash and a dotted extension), an issue number (# followed by digits), a URL (http), or a double-quoted string; a heuristic, not a fact."),
        new("done_criteria_share", "prompt_shape", "share", "prompt-log+activity_events",
            "The share of the week's human prompts (origin human, see origin.py) of 20 or more words whose lower-cased text contains verify, prove, test, until, must, should show, or expected; a heuristic, not a fact."),
        new("repeated_instruction_clusters", "prompt_shape", "clusters", "prompt-log+activity_events",
            "Single-link clusters of the week's human prompts (origin human, see origin.py) of 8 or more words, linking two prompts from different sessions when the Jaccard similarity of their 5-word shingle sets (lower-cased, punctuation stripped) is at least 0.5, keeping clusters of size 3 or more spanning 2 or more sessions, top 20 by size."),
        new("sessions_with_pull_requests_share", "outcomes", "share", "session_history",
            "The share of the week's sessions whose PullRequestsJson is a non-null, non-empty JSON array."),
        new("sessions_with_commits_share", "outcomes", "share", "session_history",
            "The share of the week's sessions whose CommitsJson is a non-null, non-empty JSON array."),
        new("left_unverified_items_per_session_mean", "outcomes", "items", "session_history",
            "The mean length of the LeftUnverifiedJson array (null counting as zero items) over the week's sessions whose SummaryKind is not null."),
        new("summary_coverage", "outcomes", "share", "session_history",
            "The share of the week's sessions by SummaryKind, with null reported under the key null."),
        new("voice_words", "voice", "words", "dictation_transcripts+prompt-log+activity_events",
            "The count of the week's transcripts, the sum of words in their RawText (spoken), the sum of the word count (as for prompt_words) over the week's human prompts (origin human, see origin.py) whose origin_modality is typed, and voice_share_of_known_modality_words = spoken transcript words over (spoken transcript words + words of human prompts whose origin_modality is typed); user prompts whose origin is unresolved are outside that ratio and their share is reported beside it as unresolved_prompt_share (unresolved user prompts over all user prompts) and unresolved_word_share (their words over all user-prompt words)."),
        new("cleanup_applied_share", "voice", "share", "dictation_transcripts",
            "The share of the week's transcripts with CleanupApplied true."),
        new("changed_words_per_transcript_mean", "voice", "words", "dictation_transcripts",
            "Over the week's transcripts whose CleanedText differs from RawText, the mean number of words replaced, inserted or deleted between the two word lists as difflib sees them."),
        new("prompts_by_origin", "origin", "count", "prompt-log+activity_events",
            "The week's user prompts counted, with their word counts (as for prompt_words) summed, by origin (human, agent, framework, unresolved, see origin.py), the human ones also split by origin_modality (typed, voice) and by origin_surface (desktop, phone, cockpit, unknown), and by_rule counting the origin_rule each prompt was classified under."),
        new("sessions_by_origin", "origin", "count", "session_history",
            "The week's sessions counted by OriginKind (human, agent, schedule, unknown; a NULL column counts as unknown, because SessionOrigin.cs records the origin at birth and never guesses it, and a Director that predates the field records nothing), and the human ones by OriginSurface (desktop, cockpit, phone, cli, cron, workflow, api, unknown; NULL counts as unknown)."),
        new("standing_sessions", "origin", "session-ids", "session_history",
            "The count and sorted ids of the week's sessions whose OriginKind is agent or schedule and whose lifetime from StartedAtUtc to EndedAtUtc, or to the extract time while still open, exceeds 24 hours."),
        new("turns_by_driver", "origin", "count", "prompt-log+activity_events",
            "The week's user prompts counted by origin (see origin.py): human is the developer's own turns, agent is turns sent by messaging from other sessions, framework is text the product or the agent tool authored, unresolved is what could not be told apart."),
        new("human_prompts_by_repo", "repos", "count", "prompt-log+activity_events+session_history",
            "The week's human prompts (origin human, see origin.py) counted by the repository name as the session row their sessionId names carries it (owner/repo), sorted by count descending then name; a prompt whose session has no row counts under the key no session row, and a row whose RepoName is NULL or empty under no repository name."),
        new("human_sessions_by_repo", "repos", "count", "session_history",
            "The week's sessions whose OriginKind is human counted by the repository name as the session row carries it (owner/repo), sorted by count descending then name; a NULL or empty RepoName counts under no repository name."),
        new("human_prompts_by_session", "repos", "count", "prompt-log+activity_events",
            "The week's human prompts (origin human, see origin.py) counted by the sessionId on the record, sorted by count descending then id, so the report can re-sum a topic's sessions from their ids; per-session ids carry no baseline."),
    };

    private static readonly Dictionary<string, Definition> DefinitionByKey = Definitions.ToDictionary(d => d.Key, d => d, StringComparer.Ordinal);

    /// <summary>Metrics whose value is a per-id table: no baseline, and a coverage note saying so.</summary>
    public static readonly HashSet<string> NoBaseline = new(StringComparer.Ordinal) { "human_prompts_by_session" };
    public const string NoBaselineNote = "no baseline: per-session ids";

    /// <summary>Owner ruling 12: carried as coverage hours_caution on every metric about when prompts were sent, and at the top level.</summary>
    public const string HoursCautionSentence = "This report counts prompts and when they were sent, never time at the computer; the work is "
        + "intermittent, and no figure here is hours at the desk.";
    public static readonly string[] HoursMetrics = { "prompts_by_hour_of_week", "days", "human_prompts_by_surface_by_hour", "business_hours_share" };

    public static readonly string[] GroupOrder = { "rhythm", "arc", "waiting", "prompt_shape", "outcomes", "voice", "origin", "repos" };

    public const string NoSessionRow = "no session row";
    public const string NoRepositoryName = "no repository name";
    public const int StandingSessionHours = 24;
    public static readonly string[] OriginKinds = { "human", "agent", "schedule", "unknown" };
    public static readonly string[] OriginSurfaces = { "desktop", "cockpit", "phone", "cli", "cron", "workflow", "api", "unknown" };

    /// <summary>The #2639 sentence: the count of prompts that carry no input stamp and could not be told apart, and their share of the week's prompt words.</summary>
    public const string UnresolvedSentenceTemplate = "{count} prompts ({percent}% of the week's prompt words) carry no input stamp and could not be "
        + "told apart between you and your sessions (product issue 2639); they are outside every number "
        + "about you.";

    /// <summary>The chat relay disclosure, carried verbatim at the top level and on prompts_by_origin.</summary>
    public const string ChatRelaySentence = "Prompts sent through the chat relay are stamped as product text by the product today and are "
        + "not counted here (product issue 2639).";

    /// <summary>group -> the metric keys of that group, in definition order.</summary>
    public static readonly Dictionary<string, List<string>> GroupIds = GroupOrder.ToDictionary(
        group => group, group => Definitions.Where(d => d.Group == group).Select(d => d.Key).ToList(), StringComparer.Ordinal);

    public static Definition DefinitionOf(string key)
        => DefinitionByKey.TryGetValue(key, out var definition) ? definition : throw new MentorDataException("No metric '" + key + "'.");

    /// <summary>The SourceNames a metric rests on, from its definition's source field.</summary>
    public static List<string> MetricSources(string key)
    {
        var sources = DefinitionOf(key).Source.Split('+').ToList();
        foreach (var source in sources)
            if (!SourceNames.Contains(source))
                throw new MentorDataException("Metric '" + key + "' names a source '" + source + "' that is not in SourceNames.");
        return sources;
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>The k-th smallest value with k = ceil(percent/100 * n); null on no values.</summary>
    public static object? NearestRank(IReadOnlyList<long> values, int percent)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(v => v).ToList();
        return ordered[Rank(percent, ordered.Count) - 1];
    }

    public static object? NearestRank(IReadOnlyList<double> values, int percent)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(v => v).ToList();
        return ordered[Rank(percent, ordered.Count) - 1];
    }

    private static int Rank(int percent, int count)
    {
        var k = (int)Math.Ceiling(percent / 100.0 * count);
        return Math.Max(1, Math.Min(k, count));
    }

    /// <summary>round(part / whole, 4), or null when whole is zero.</summary>
    public static object? Share(long part, long whole)
    {
        if (whole == 0) return null;
        return PyNumbers.Round((double)part / whole, 4);
    }

    public static object? Mean(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return null;
        return PyNumbers.Round((double)values.Sum() / values.Count, 4);
    }

    public static double Hours(double seconds) => PyNumbers.Round(seconds / 3600.0, 3);

    private static long Weekday(DateTime local) => ((int)local.DayOfWeek + 6) % 7;

    private static Dictionary<string, object?> With(Dictionary<string, object?> cov, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(cov);
        copy[key] = value;
        return copy;
    }

    // ---------------------------------------------------------------- group A

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeRhythm(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var matrix = new long[7][];
        for (var i = 0; i < 7; i++) matrix[i] = new long[24];
        var perDay = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 7; i++)
            perDay.Add(new Dictionary<string, object?> { ["active"] = false, ["first_prompt_local"] = null, ["last_prompt_local"] = null, ["prompts"] = 0L });
        var strip = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var surface in Origin.Surfaces) strip[surface] = new long[24];
        long inside = 0;
        long outside = 0;
        var startHour = week.Hours.Start;
        var endHour = week.Hours.End;
        foreach (var prompt in week.HumanPrompts)
        {
            var local = week.Local(prompt.Ts);
            var day = (int)Weekday(local);
            matrix[day][local.Hour]++;
            if (prompt.OriginSurface is null || !strip.TryGetValue(prompt.OriginSurface, out var row))
                throw new MentorDataException("The human prompt at " + prompt.Pid + " carries the origin surface " + PyText.Repr(prompt.OriginSurface)
                    + ", which is not one of " + string.Join(", ", Origin.Surfaces) + ".");
            row[local.Hour]++;
            if (day < 5 && startHour <= local.Hour && local.Hour < endHour) inside++;
            else outside++;
            var entry = perDay[day];
            entry["prompts"] = (long)entry["prompts"]! + 1;
            entry["active"] = true;
            var stamp = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (entry["first_prompt_local"] is not string first || string.CompareOrdinal(stamp, first) < 0) entry["first_prompt_local"] = stamp;
            if (entry["last_prompt_local"] is not string last || string.CompareOrdinal(stamp, last) > 0) entry["last_prompt_local"] = stamp;
        }
        var humanCov = week.PromptCov(week.HumanPrompts);
        output["prompts_by_hour_of_week"] = matrix.Select(row => (object?)row.Cast<object?>().ToList()).ToList();
        cov["prompts_by_hour_of_week"] = humanCov;
        output["days"] = new Dictionary<string, object?> { ["days"] = perDay.Cast<object?>().ToList(), ["active_days"] = (long)perDay.Count(d => (bool)d["active"]!) };
        cov["days"] = humanCov;
        var stripValue = new Dictionary<string, object?>();
        foreach (var surface in Origin.Surfaces) stripValue[surface] = strip[surface].Cast<object?>().ToList();
        output["human_prompts_by_surface_by_hour"] = stripValue;
        cov["human_prompts_by_surface_by_hour"] = humanCov;
        output["business_hours_share"] = new Dictionary<string, object?>
        {
            ["inside"] = Share(inside, inside + outside),
            ["outside"] = Share(outside, inside + outside),
            ["inside_count"] = inside,
            ["outside_count"] = outside,
            ["start"] = (long)startHour,
            ["end"] = (long)endHour,
        };
        cov["business_hours_share"] = humanCov;
        foreach (var key in HoursMetrics)
            cov[key] = With((Dictionary<string, object?>)cov[key]!, "hours_caution", HoursCautionSentence);

        output["sessions_started"] = (long)week.Sessions.Count;
        cov["sessions_started"] = week.SessionCov(week.Sessions);
        var closed = week.Sessions.Where(s => s.Ended is not null).ToList();
        var minutes = closed.Select(s => PyNumbers.Round(PyNumbers.TotalSeconds(s.Ended!.Value - s.Started) / 60.0, 1)).ToList();
        output["session_minutes_median"] = NearestRank(minutes, 50);
        output["session_minutes_p90"] = NearestRank(minutes, 90);
        var closedCov = week.SessionCov(closed);
        closedCov["open_sessions_excluded"] = (long)(week.Sessions.Count - closed.Count);
        cov["session_minutes_median"] = closedCov;
        cov["session_minutes_p90"] = closedCov;

        var byDay = new List<long>();
        for (var i = 0; i < 7; i++)
            byDay.Add(MaxConcurrent(week.World.Sessions, week.Days[i], week.Days[i + 1], week.World.ExtractTime));
        output["max_concurrent_sessions"] = new Dictionary<string, object?> { ["by_day"] = byDay.Cast<object?>().ToList(), ["max"] = byDay.Max() };
        cov["max_concurrent_sessions"] = week.SessionCov(week.Sessions);
        return (output, cov);
    }

    /// <summary>The largest number of session intervals covering one instant in [dayStart, dayEnd).</summary>
    public static long MaxConcurrent(IReadOnlyList<MetricsSession> sessions, DateTime dayStart, DateTime dayEnd, DateTime extractTime)
    {
        long count = 0;
        var changes = new List<(DateTime At, int Delta)>();
        foreach (var s in sessions)
        {
            var start = s.Started;
            var end = s.Ended ?? extractTime;
            if (end <= dayStart || start >= dayEnd) continue;
            if (start <= dayStart) count++;
            else changes.Add((start, 1));
            if (end < dayEnd) changes.Add((end, -1));
        }
        var best = count;
        foreach (var (_, delta) in changes.OrderBy(c => c.At).ThenBy(c => c.Delta))
        {
            count += delta;
            best = Math.Max(best, count);
        }
        return best;
    }

    // ---------------------------------------------------------------- group B

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeArc(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var endings = new Dictionary<string, long>(StringComparer.Ordinal);
        var byAgent = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var s in week.Sessions)
        {
            var kind = s.Ending ?? "open";
            endings[kind] = endings.GetValueOrDefault(kind) + 1;
            var agent = string.IsNullOrEmpty(s.Agent) ? "unknown" : s.Agent;
            if (!byAgent.TryGetValue(agent, out var counts)) byAgent[agent] = counts = new Dictionary<string, long>(StringComparer.Ordinal);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        var value = new Dictionary<string, object?>();
        foreach (var kind in MentorReaders.EndingKinds.OrderBy(k => k, StringComparer.Ordinal).Concat(new[] { "open" }))
            value[kind] = endings.GetValueOrDefault(kind);
        var byAgentValue = new Dictionary<string, object?>();
        foreach (var (agent, counts) in byAgent.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            byAgentValue[agent] = counts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        value["by_agent"] = byAgentValue;
        output["endings"] = value;
        cov["endings"] = week.SessionCov(week.Sessions);

        var turned = week.Sessions.Where(s => s.AgentTurns is not null).ToList();
        var turns = turned.Select(s => s.AgentTurns!.Value).ToList();
        var p90 = NearestRank(turns, 90);
        output["turns_per_session_median"] = NearestRank(turns, 50);
        output["turns_per_session_p90"] = p90;
        output["long_tail_session_ids"] = turned.Where(s => p90 is long threshold && s.AgentTurns!.Value >= threshold)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).Cast<object?>().ToList();
        var turnCov = week.SessionCov(turned);
        turnCov["sessions_without_agent_turn_count"] = (long)(week.Sessions.Count - turned.Count);
        foreach (var key in new[] { "turns_per_session_median", "turns_per_session_p90", "long_tail_session_ids" })
            cov[key] = turnCov;

        var reported = week.Sessions.Where(s => s.PeakContext is not null).ToList();
        var peaks = reported.Select(s => s.PeakContext!.Value).ToList();
        output["peak_context_tokens_median"] = NearestRank(peaks, 50);
        output["peak_context_tokens_p90"] = NearestRank(peaks, 90);
        output["share_over_400k"] = Share(peaks.Count(p => p > Context400K), peaks.Count);
        output["share_over_700k"] = Share(peaks.Count(p => p > Context700K), peaks.Count);
        var histogram = new Dictionary<string, object?>();
        for (var i = 0; i < ContextBuckets; i++)
        {
            var low = i * ContextBucket;
            var high = low + ContextBucket;
            var key = (low / 1000).ToString(CultureInfo.InvariantCulture) + "k-" + (high / 1000).ToString(CultureInfo.InvariantCulture) + "k";
            histogram[key] = (long)peaks.Count(p => low <= p && p < high);
        }
        histogram[(ContextBuckets * ContextBucket / 1000).ToString(CultureInfo.InvariantCulture) + "k+"] = (long)peaks.Count(p => p >= ContextBuckets * ContextBucket);
        output["peak_context_histogram"] = histogram;
        var peakCov = week.SessionCov(reported);
        peakCov["claude_only"] = true;
        peakCov["share_of_week_sessions_reporting"] = Share(reported.Count, week.Sessions.Count);
        foreach (var key in new[] { "peak_context_tokens_median", "peak_context_tokens_p90", "share_over_400k", "share_over_700k", "peak_context_histogram" })
            cov[key] = peakCov;

        var contexts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in week.UserPrompts)
        {
            if (p.Context is null) continue;
            if (!contexts.TryGetValue(p.Session, out var set)) contexts[p.Session] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(p.Context);
        }
        var counts2 = week.PromptSessions().Select(s => (long)(contexts.TryGetValue(s, out var set) ? set.Count : 0)).ToList();
        output["contexts_per_session_median"] = NearestRank(counts2, 50);
        cov["contexts_per_session_median"] = week.PromptCov(week.UserPrompts);
        return (output, cov);
    }

    // ---------------------------------------------------------------- group C

    /// <summary>
    /// Per session: [(start, end, state)] from its first state-changing event, clipped to the week. A segment's
    /// state is the NewState of the event that opened it; it runs to the next state-changing event of the
    /// same session, or to the session's EndedAtUtc, or to the week's horizon. A session with events but no
    /// session_history row is left out and counted beside the segments. The sessions come back in the order
    /// their first event has in the world, which is the order the reference sums them in.
    /// </summary>
    public static (List<(string Session, List<(DateTime Start, DateTime End, string State)> Rows)> Segments, long Orphans) SessionSegments(MetricsWorld world, MetricsWeek week)
    {
        var bySession = new Dictionary<string, List<MentorEvent>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in world.Events)
        {
            if (!MentorReaders.StateEventTypes.Contains(e.Type)) continue;
            if (!bySession.TryGetValue(e.Session, out var list))
            {
                bySession[e.Session] = list = new List<MentorEvent>();
                order.Add(e.Session);
            }
            list.Add(e);
        }
        var known = world.Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var endedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var s in world.Sessions)
            if (s.Ended is not null) endedAt[s.Id] = s.Ended.Value;
        var segments = new List<(string, List<(DateTime, DateTime, string)>)>();
        var orphans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in order)
        {
            var events = bySession[session];
            if (!known.Contains(session))
            {
                var beforeEnd = events.Where(e => e.Ts < week.End).ToList();
                if (beforeEnd.Count > 0 && (beforeEnd[^1].New != "Exited" || beforeEnd[^1].Ts >= week.Start))
                    orphans.Add(session);
                continue;
            }
            var rows = new List<(DateTime, DateTime, string)>();
            var limit = week.Horizon;
            if (endedAt.TryGetValue(session, out var ended) && ended < limit) limit = ended;
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var start = e.Ts;
                var end = i + 1 < events.Count ? events[i + 1].Ts : limit;
                if (start < week.Start) start = week.Start;
                if (end > limit) end = limit;
                if (end <= start) continue;
                rows.Add((start, end, e.New ?? throw new MentorDataException("A state event without NewState at " + e.Where + ".")));
            }
            if (rows.Count > 0) segments.Add((session, rows));
        }
        return (segments, orphans.Count);
    }

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeWaiting(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var world = week.World;
        var (segments, orphans) = SessionSegments(world, week);

        var waitingSeconds = 0.0;
        var boundaries = new HashSet<DateTime>();
        var perSession = new List<(DateTime Start, DateTime End, bool Waiting)>();
        foreach (var (_, rows) in segments)
        {
            foreach (var (start, end, state) in rows)
            {
                if (state == "Exited") continue;
                var waiting = WaitingStates.Contains(state);
                if (waiting) waitingSeconds += PyNumbers.TotalSeconds(end - start);
                perSession.Add((start, end, waiting));
                boundaries.Add(start);
                boundaries.Add(end);
            }
        }
        output["agent_quiet_hours"] = Hours(waitingSeconds);

        var idleSeconds = 0.0;
        var ordered = boundaries.OrderBy(b => b).ToList();
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var lo = ordered[i];
            var hi = ordered[i + 1];
            var openCount = 0;
            var waitingCount = 0;
            foreach (var (start, end, waiting) in perSession)
            {
                if (start <= lo && end >= hi)
                {
                    openCount++;
                    if (waiting) waitingCount++;
                }
            }
            if (openCount > 0 && waitingCount == openCount) idleSeconds += PyNumbers.TotalSeconds(hi - lo);
        }
        output["all_sessions_quiet_hours"] = Hours(idleSeconds);

        var eventSessions = segments.Select(s => s.Session).ToHashSet(StringComparer.Ordinal);
        var eventAgents = world.Sessions.Where(s => eventSessions.Contains(s.Id) && !string.IsNullOrEmpty(s.Agent)).Select(s => s.Agent!)
            .Distinct().OrderBy(a => a, StringComparer.Ordinal).Cast<object?>().ToList();
        var eventsCov = week.Coverage(eventAgents, eventSessions.Count);
        eventsCov["sessions_with_state_events_in_week"] = (long)eventSessions.Count;
        eventsCov["sessions_with_events_but_no_history_row"] = orphans;
        cov["agent_quiet_hours"] = eventsCov;
        cov["all_sessions_quiet_hours"] = eventsCov;

        output["human_response_minutes_median"] = NearestRank(HumanResponseMinutes(week), 50);
        cov["human_response_minutes_median"] = eventsCov;

        var stretched = week.Sessions.Where(s => s.WaitingStretches is not null).ToList();
        output["waiting_stretches_per_session_median"] = NearestRank(stretched.Select(s => s.WaitingStretches!.Value).ToList(), 50);
        cov["waiting_stretches_per_session_median"] = week.SessionCov(stretched);
        var idleRows = week.Sessions.Where(s => s.IdleSeconds is not null).ToList();
        var idleSum = 0.0;
        foreach (var s in idleRows) idleSum += s.IdleSeconds!.Value;
        output["session_row_lifetime_idle_hours"] = Hours(idleSum);
        cov["session_row_lifetime_idle_hours"] = week.SessionCov(idleRows);
        return (output, cov);
    }

    public static bool InWakingHours(DateTime local) => WakingStartHour <= local.Hour && local.Hour < WakingEndHour;

    /// <summary>Minutes from each in-week transition into waiting to the next user input, counted only when
    /// the wait's start and the reply both fall inside 08:00-22:00 local on the same day.</summary>
    public static List<double> HumanResponseMinutes(MetricsWeek week)
    {
        var world = week.World;
        var promptsBySession = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        foreach (var p in world.Prompts)
        {
            if (p.Role != "user") continue;
            if (!promptsBySession.TryGetValue(p.Session, out var list)) promptsBySession[p.Session] = list = new List<DateTime>();
            list.Add(p.Ts);
        }
        var turnsBySession = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        foreach (var e in world.Events)
        {
            if (e.Type != MentorReaders.TurnEventType) continue;
            if (!turnsBySession.TryGetValue(e.Session, out var list)) turnsBySession[e.Session] = list = new List<DateTime>();
            list.Add(e.Ts);
        }
        var minutes = new List<double>();
        foreach (var e in week.EventsInWeek)
        {
            if (!MentorReaders.StateEventTypes.Contains(e.Type) || e.New is null || !WaitingStates.Contains(e.New)) continue;
            if (e.Prev is not null && WaitingStates.Contains(e.Prev)) continue;
            var startedLocal = week.Local(e.Ts);
            if (!InWakingHours(startedLocal)) continue;
            var candidates = new List<DateTime>();
            if (promptsBySession.TryGetValue(e.Session, out var prompts)) candidates.AddRange(prompts.Where(t => t > e.Ts));
            if (turnsBySession.TryGetValue(e.Session, out var turns)) candidates.AddRange(turns.Where(t => t > e.Ts));
            if (candidates.Count == 0) continue;
            var reply = candidates.Min();
            var replyLocal = week.Local(reply);
            if (replyLocal.Date != startedLocal.Date || !InWakingHours(replyLocal)) continue;
            minutes.Add(PyNumbers.Round(PyNumbers.TotalSeconds(reply - e.Ts) / 60.0, 1));
        }
        return minutes;
    }

    // ---------------------------------------------------------------- group D

    public static string NormaliseText(string text)
    {
        var lowered = NonWordRe.Replace(PyText.Lower(text), " ");
        return PyText.Strip(SpaceRe.Replace(lowered, " "));
    }

    public static HashSet<string> Shingles(IReadOnlyList<string> words, int size)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= words.Count - size; i++) set.Add(string.Join(" ", words.Skip(i).Take(size)));
        return set;
    }

    /// <summary>Python's text[:40].lower().strip() starts with one of the markers.</summary>
    public static bool IsCorrectionCandidate(string text)
    {
        var head = PyText.Strip(PyText.Lower(Head(text, 40)));
        return CorrectionMarkers.Any(marker => head.StartsWith(marker, StringComparison.Ordinal));
    }

    /// <summary>Python's text[:n], by code points.</summary>
    private static string Head(string text, int n)
    {
        var taken = 0;
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken == n) break;
            end += rune.Utf16SequenceLength;
            taken++;
        }
        return text.Substring(0, end);
    }

    public static bool HasSpecificityMarker(string text)
        => PathRe.IsMatch(text) || IssueRe.IsMatch(text) || UrlRe.IsMatch(text) || QuoteRe.IsMatch(text);

    public static bool HasDoneCriteria(string text, long words)
    {
        if (words < DoneCriteriaMinWords) return false;
        var lowered = PyText.Lower(text);
        return DoneCriteriaWords.Any(word => lowered.Contains(word, StringComparison.Ordinal));
    }

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputePromptShape(MetricsWeek week, bool withClusters = true)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var prompts = week.HumanPrompts;
        var words = prompts.Select(p => (long)p.Words).ToList();
        var n = prompts.Count;
        var pcov = week.PromptCov(prompts);
        output["prompt_words"] = new Dictionary<string, object?>
        {
            ["median"] = NearestRank(words, 50),
            ["p10"] = NearestRank(words, 10),
            ["p90"] = NearestRank(words, 90),
            ["share_under_8_words"] = Share(words.Count(w => w < ShortPromptWords), n),
            ["share_over_300_words"] = Share(words.Count(w => w > LongPromptWords), n),
            ["human_prompts"] = (long)n,
        };
        cov["prompt_words"] = pcov;

        // origin_modality and origin_surface, not the raw record fields: for a ledger-origin prompt the
        // record's own modality and surface are null and the stamp is on the event.
        var modalityShare = new Dictionary<string, object?>();
        foreach (var k in Origin.Modalities) modalityShare[k] = Share(prompts.Count(p => p.OriginModality == k), n);
        var surfaceShare = new Dictionary<string, object?>();
        foreach (var k in new[] { "desktop", "cockpit", "phone", "unknown" }) surfaceShare[k] = Share(prompts.Count(p => p.OriginSurface == k), n);
        output["modality_share"] = modalityShare;
        output["surface_share"] = surfaceShare;
        cov["modality_share"] = pcov;
        cov["surface_share"] = pcov;

        var corrections = prompts.Where(p => IsCorrectionCandidate(p.Text)).ToList();
        output["correction_candidates_share"] = Share(corrections.Count, n);
        cov["correction_candidates_share"] = With(pcov, "heuristic", true);
        var perSessionTotal = new Dictionary<string, long>(StringComparer.Ordinal);
        var sessionOrder = new List<string>();
        foreach (var p in prompts)
        {
            if (!perSessionTotal.ContainsKey(p.Session)) { perSessionTotal[p.Session] = 0; sessionOrder.Add(p.Session); }
            perSessionTotal[p.Session]++;
        }
        var perSessionCorr = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in corrections) perSessionCorr[p.Session] = perSessionCorr.GetValueOrDefault(p.Session) + 1;
        var rows = new List<Dictionary<string, object?>>();
        foreach (var session in sessionOrder)
        {
            var total = perSessionTotal[session];
            if (total < CorrectionSessionMinPrompts) continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["session_id"] = session,
                ["user_prompts"] = total,
                ["share"] = Share(perSessionCorr.GetValueOrDefault(session), total),
            });
        }
        rows = rows.OrderByDescending(r => (double)r["share"]!).ThenBy(r => (string)r["session_id"]!, StringComparer.Ordinal).ToList();
        output["correction_candidate_session_ids"] = rows.Cast<object?>().ToList();
        cov["correction_candidate_session_ids"] = With(pcov, "heuristic", true);

        output["specificity_markers_share"] = Share(prompts.Count(p => HasSpecificityMarker(p.Text)), n);
        cov["specificity_markers_share"] = With(pcov, "heuristic", true);
        var eligible = prompts.Where(p => p.Words >= DoneCriteriaMinWords).ToList();
        output["done_criteria_share"] = Share(eligible.Count(p => HasDoneCriteria(p.Text, p.Words)), eligible.Count);
        cov["done_criteria_share"] = With(With(week.PromptCov(eligible), "heuristic", true), "prompts_of_20_words_or_more", (long)eligible.Count);

        output["repeated_instruction_clusters"] = withClusters ? RepeatedClusters(prompts).Cast<object?>().ToList() : new List<object?>();
        cov["repeated_instruction_clusters"] = With(pcov, "heuristic", true);
        return (output, cov);
    }

    /// <summary>Single-link clusters over 5-word shingles, linking only across sessions.</summary>
    public static List<Dictionary<string, object?>> RepeatedClusters(IReadOnlyList<MentorRecord> prompts)
    {
        var items = new List<(string Pid, string Session, int Words, HashSet<string> Shingles)>();
        foreach (var p in prompts)
        {
            var tokens = PyText.Split(NormaliseText(p.Text));
            if (tokens.Count < ClusterMinWords) continue;
            items.Add((p.Pid, p.Session, tokens.Count, Shingles(tokens, ClusterShingle)));
        }
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var sh in items[i].Shingles)
            {
                if (!index.TryGetValue(sh, out var list)) index[sh] = list = new List<int>();
                list.Add(i);
            }
        }
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var shared = new Dictionary<int, int>();
            var sharedOrder = new List<int>();
            foreach (var sh in item.Shingles)
            {
                foreach (var j in index[sh])
                {
                    if (j <= i) continue;
                    if (!shared.ContainsKey(j)) { shared[j] = 0; sharedOrder.Add(j); }
                    shared[j]++;
                }
            }
            foreach (var j in sharedOrder)
            {
                var commonCount = shared[j];
                var other = items[j];
                if (other.Session == item.Session) continue;
                var union = item.Shingles.Count + other.Shingles.Count - commonCount;
                if (union > 0 && (double)commonCount / union >= ClusterJaccard)
                    parent[Find(i)] = Find(j);
            }
        }
        var groups = new Dictionary<int, List<int>>();
        var groupOrder = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var members)) { groups[root] = members = new List<int>(); groupOrder.Add(root); }
            members.Add(i);
        }
        var clusters = new List<Dictionary<string, object?>>();
        foreach (var root in groupOrder)
        {
            var members = groups[root];
            var sessions = members.Select(i => items[i].Session).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (members.Count < ClusterMinSize || sessions.Count < ClusterMinSessions) continue;
            clusters.Add(new Dictionary<string, object?>
            {
                ["size"] = (long)members.Count,
                ["sessions"] = sessions.Cast<object?>().ToList(),
                ["prompt_ids"] = members.Select(i => items[i].Pid).OrderBy(p => p, StringComparer.Ordinal).Cast<object?>().ToList(),
                ["example_words"] = (long)members.Min(i => items[i].Words),
            });
        }
        return clusters.OrderByDescending(c => (long)c["size"]!).ThenBy(c => (string)((List<object?>)c["prompt_ids"]!)[0]!, StringComparer.Ordinal).Take(ClusterTop).ToList();
    }

    // ---------------------------------------------------------------- group E

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeOutcomes(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var n = week.Sessions.Count;
        var scov = week.SessionCov(week.Sessions);
        output["sessions_with_pull_requests_share"] = Share(week.Sessions.Count(s => s.PullRequests is { Count: > 0 }), n);
        output["sessions_with_commits_share"] = Share(week.Sessions.Count(s => s.Commits is { Count: > 0 }), n);
        cov["sessions_with_pull_requests_share"] = scov;
        cov["sessions_with_commits_share"] = scov;
        var summarised = week.Sessions.Where(s => s.SummaryKind is not null).ToList();
        output["left_unverified_items_per_session_mean"] = Mean(summarised.Select(s => (long)(s.LeftUnverified?.Count ?? 0)).ToList());
        cov["left_unverified_items_per_session_mean"] = week.SessionCov(summarised);
        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in week.Sessions)
        {
            var kind = s.SummaryKind ?? "null";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        var summaryCoverage = new Dictionary<string, object?>();
        foreach (var (k, v) in kinds.OrderBy(kv => kv.Key, StringComparer.Ordinal)) summaryCoverage[k] = Share(v, n);
        output["summary_coverage"] = summaryCoverage;
        cov["summary_coverage"] = scov;
        return (output, cov);
    }

    // ---------------------------------------------------------------- group F

    /// <summary>0.9091 -> '90.91'; 0.5 -> '50'; up to two decimals, trailing zeros stripped.</summary>
    public static string PercentText(double wordShare)
        => PyNumbers.FormatFixed(wordShare * 100.0, 2).TrimEnd('0').TrimEnd('.');

    /// <summary>The #2639 sentence for the coverage block: count unresolved user prompts and their share of the
    /// week's user-prompt words (null when the week has no user-prompt words at all, rendered as 0%).</summary>
    public static string UnresolvedSentence(long count, object? wordShare)
    {
        var percent = PercentText(wordShare is double d ? d : 0.0);
        return UnresolvedSentenceTemplate.Replace("{count}", count.ToString(CultureInfo.InvariantCulture)).Replace("{percent}", percent);
    }

    public static string UnresolvedNote(long count, object? wordShare) => UnresolvedSentence(count, wordShare);

    public static int WordDiffCount(string raw, string cleaned) => PyDifflib.ChangedWordCount(raw, cleaned);

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeVoice(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var transcripts = week.Transcripts;
        long spoken = 0;
        foreach (var t in transcripts) spoken += PyText.Split(t.Raw).Count;
        var typedPrompts = week.HumanPrompts.Where(p => p.OriginModality == "typed").ToList();
        long typed = typedPrompts.Sum(p => (long)p.Words);
        var unresolvedPrompts = week.UserPrompts.Where(p => p.Origin == "unresolved").ToList();
        long unresolvedWords = unresolvedPrompts.Sum(p => (long)p.Words);
        long allWords = week.UserPrompts.Sum(p => (long)p.Words);
        var unresolvedWordShare = Share(unresolvedWords, allWords);
        output["voice_words"] = new Dictionary<string, object?>
        {
            ["transcripts"] = (long)transcripts.Count,
            ["spoken_words"] = spoken,
            ["typed_words"] = typed,
            ["voice_share_of_known_modality_words"] = Share(spoken, spoken + typed),
            ["unresolved_prompt_share"] = Share(unresolvedPrompts.Count, week.UserPrompts.Count),
            ["unresolved_word_share"] = unresolvedWordShare,
        };
        var vcov = week.Coverage(week.PromptAgents(), week.PromptSessions().Count);
        vcov["transcripts"] = (long)transcripts.Count;
        vcov["unresolved_prompts"] = (long)unresolvedPrompts.Count;
        vcov["unresolved_words"] = unresolvedWords;
        vcov["unresolved_word_share"] = unresolvedWordShare;
        vcov["unresolved_note"] = UnresolvedNote(unresolvedPrompts.Count, unresolvedWordShare);
        cov["voice_words"] = vcov;
        output["cleanup_applied_share"] = Share(transcripts.Count(t => t.Cleanup), transcripts.Count);
        cov["cleanup_applied_share"] = vcov;
        var changed = transcripts.Where(t => !string.Equals(t.Cleaned, t.Raw, StringComparison.Ordinal)).Select(t => (long)WordDiffCount(t.Raw, t.Cleaned)).ToList();
        output["changed_words_per_transcript_mean"] = Mean(changed);
        cov["changed_words_per_transcript_mean"] = With(vcov, "transcripts_changed", (long)changed.Count);
        return (output, cov);
    }

    // ---------------------------------------------------------------- group G (origin)

    public static Dictionary<string, object?> CountAndWords(IReadOnlyList<MentorRecord> prompts) => new()
    {
        ["count"] = (long)prompts.Count,
        ["words"] = prompts.Sum(p => (long)p.Words),
    };

    public static double SessionLifetimeSeconds(MetricsSession s, DateTime extractTime)
        => PyNumbers.TotalSeconds((s.Ended ?? extractTime) - s.Started);

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeOrigin(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        List<MentorRecord> ByOrigin(string origin) => week.UserPrompts.Where(p => p.Origin == origin).ToList();
        var human = ByOrigin("human");
        var humanValue = CountAndWords(human);
        var byModality = new Dictionary<string, object?>();
        foreach (var m in Origin.Modalities) byModality[m] = CountAndWords(human.Where(p => p.OriginModality == m).ToList());
        humanValue["by_modality"] = byModality;
        var bySurface = new Dictionary<string, object?>();
        foreach (var s in Origin.Surfaces) bySurface[s] = CountAndWords(human.Where(p => p.OriginSurface == s).ToList());
        humanValue["by_surface"] = bySurface;
        var value = new Dictionary<string, object?> { ["human"] = humanValue };
        foreach (var origin in new[] { "agent", "framework", "unresolved" }) value[origin] = CountAndWords(ByOrigin(origin));
        var rules = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in week.UserPrompts)
        {
            var rule = p.OriginRule ?? throw new MentorDataException("The user record at " + p.Pid + " has no origin rule.");
            rules[rule] = rules.GetValueOrDefault(rule) + 1;
        }
        var byRule = new Dictionary<string, object?>();
        foreach (var rule in rules.Keys.OrderBy(r => r, StringComparer.Ordinal)) byRule[rule] = rules[rule];
        value["by_rule"] = byRule;
        output["prompts_by_origin"] = value;
        long allWords = week.UserPrompts.Sum(p => (long)p.Words);
        var unresolved = ByOrigin("unresolved");
        var pcov = week.PromptCov(week.UserPrompts);
        pcov["unresolved_sentence"] = UnresolvedSentence(unresolved.Count, Share(unresolved.Sum(p => (long)p.Words), allWords));
        pcov["chat_relay_not_counted"] = true;
        pcov["chat_relay_sentence"] = ChatRelaySentence;
        cov["prompts_by_origin"] = pcov;

        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in week.Sessions)
        {
            var kind = s.OriginKind ?? "unknown";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        var humanSessions = week.Sessions.Where(s => s.OriginKind == "human").ToList();
        var surfaces = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in humanSessions)
        {
            var surface = s.OriginSurface ?? "unknown";
            surfaces[surface] = surfaces.GetValueOrDefault(surface) + 1;
        }
        var bySurfaceCount = new Dictionary<string, object?>();
        foreach (var s in OriginSurfaces) bySurfaceCount[s] = surfaces.GetValueOrDefault(s);
        output["sessions_by_origin"] = new Dictionary<string, object?>
        {
            ["human"] = new Dictionary<string, object?> { ["count"] = (long)humanSessions.Count, ["by_surface"] = bySurfaceCount },
            ["agent"] = kinds.GetValueOrDefault("agent"),
            ["schedule"] = kinds.GetValueOrDefault("schedule"),
            ["unknown"] = kinds.GetValueOrDefault("unknown"),
        };
        cov["sessions_by_origin"] = week.SessionCov(week.Sessions);

        var standing = week.Sessions
            .Where(s => s.OriginKind is "agent" or "schedule" && SessionLifetimeSeconds(s, week.World.ExtractTime) > StandingSessionHours * 3600)
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        output["standing_sessions"] = new Dictionary<string, object?> { ["count"] = (long)standing.Count, ["session_ids"] = standing.Cast<object?>().ToList() };
        cov["standing_sessions"] = week.SessionCov(week.Sessions.Where(s => s.OriginKind is "agent" or "schedule").ToList());

        var drivers = new Dictionary<string, object?>();
        foreach (var origin in Origin.Classes) drivers[origin] = (long)ByOrigin(origin).Count;
        output["turns_by_driver"] = drivers;
        cov["turns_by_driver"] = week.PromptCov(week.UserPrompts);
        return (output, cov);
    }

    // ---------------------------------------------------------------- group H (repos)

    /// <summary>{name: count} sorted by count descending then name.</summary>
    public static Dictionary<string, object?> CountsByName(IEnumerable<string> names)
    {
        var counter = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names) counter[name] = counter.GetValueOrDefault(name) + 1;
        var result = new Dictionary<string, object?>();
        foreach (var name in counter.Keys.OrderByDescending(k => counter[k]).ThenBy(k => k, StringComparer.Ordinal)) result[name] = counter[name];
        return result;
    }

    public static (Dictionary<string, object?> Out, Dictionary<string, object?> Cov) ComputeRepos(MetricsWeek week)
    {
        var output = new Dictionary<string, object?>();
        var cov = new Dictionary<string, object?>();
        var repoOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in week.World.Sessions) repoOf[s.Id] = s.RepoName;
        var names = new List<string>();
        long withoutRow = 0;
        foreach (var p in week.HumanPrompts)
        {
            if (!repoOf.TryGetValue(p.Session, out var repo))
            {
                names.Add(NoSessionRow);
                withoutRow++;
            }
            else
            {
                names.Add(repo ?? NoRepositoryName);
            }
        }
        output["human_prompts_by_repo"] = CountsByName(names);
        var pcov = week.PromptCov(week.HumanPrompts);
        pcov["prompts_without_session_row"] = withoutRow;
        cov["human_prompts_by_repo"] = pcov;

        var humanSessions = week.Sessions.Where(s => s.OriginKind == "human").ToList();
        output["human_sessions_by_repo"] = CountsByName(humanSessions.Select(s => s.RepoName ?? NoRepositoryName));
        cov["human_sessions_by_repo"] = week.SessionCov(humanSessions);

        output["human_prompts_by_session"] = CountsByName(week.HumanPrompts.Select(p => p.Session));
        cov["human_prompts_by_session"] = week.PromptCov(week.HumanPrompts);
        return (output, cov);
    }

    // ------------------------------------------------------------------ assembly

    public static (Dictionary<string, object?> Values, Dictionary<string, object?> Coverage) ComputeAll(MetricsWeek week, bool forBaseline = false, Action<string, Dictionary<string, object?>>? log = null)
    {
        var values = new Dictionary<string, object?>();
        var coverage = new Dictionary<string, object?>();
        var steps = new (string Group, Func<(Dictionary<string, object?> Out, Dictionary<string, object?> Cov)> Compute)[]
        {
            ("rhythm", () => ComputeRhythm(week)),
            ("arc", () => ComputeArc(week)),
            ("waiting", () => ComputeWaiting(week)),
            ("prompt_shape", () => ComputePromptShape(week, withClusters: !forBaseline)),
            ("outcomes", () => ComputeOutcomes(week)),
            ("voice", () => ComputeVoice(week)),
            ("origin", () => ComputeOrigin(week)),
            ("repos", () => ComputeRepos(week)),
        };
        foreach (var (group, compute) in steps)
        {
            var (output, cov) = compute();
            foreach (var (k, v) in output) values[k] = v;
            foreach (var (k, v) in cov) coverage[k] = v;
            log?.Invoke(group, output);
        }
        return (values, coverage);
    }

    /// <summary>Median over prior weeks, recursively over dicts; lists and strings get no baseline.</summary>
    public static object? BaselineOf(object? current, IReadOnlyList<object?> priors)
    {
        switch (current)
        {
            case null:
            case bool:
                return null;
            case long:
            case int:
            case double:
            {
                var numbers = priors.Where(p => p is long or int or double).ToList();
                if (numbers.Count == 0) return null;
                var median = Median(numbers);
                if (median is double d)
                {
                    var rounded = PyNumbers.Round(d, 4);
                    // int(median) if float(median).is_integer() else median - boxed on each side, because a
                    // conditional expression over a long and a double would widen the long back to a double.
                    if (rounded == Math.Floor(rounded)) return (long)rounded;
                    return rounded;
                }
                return median;
            }
            case Dictionary<string, object?> dictionary:
            {
                var result = new Dictionary<string, object?>();
                foreach (var (key, value) in dictionary)
                {
                    var inner = priors.Select(p => p is Dictionary<string, object?> d ? (d.TryGetValue(key, out var v) ? v : 0L) : null).ToList();
                    result[key] = BaselineOf(value, inner);
                }
                return result;
            }
            default:
                return null;
        }
    }

    private static double AsDouble(object number) => number switch
    {
        long l => l,
        int i => i,
        double d => d,
        _ => throw new MentorDataException("Not a number: " + number.GetType().Name),
    };

    /// <summary>statistics.median: the middle value as it is when the count is odd, the mean of the two middle
    /// values (a float) when it is even.</summary>
    private static object Median(List<object?> numbers)
    {
        var data = numbers.Select(n => n ?? throw new MentorDataException("A null reached the median.")).OrderBy(AsDouble).ToList();
        var n = data.Count;
        var i = n / 2;
        if (n % 2 == 1) return data[i] is int small ? (long)small : data[i];
        var a = data[i - 1];
        var b = data[i];
        if (a is long la && b is long lb) return (double)(la + lb) / 2.0;
        return (AsDouble(a) + AsDouble(b)) / 2.0;
    }

    public static string BaselineNote(int eligibleCount, IReadOnlyList<string> sources)
        => BaselineNotePrefix + eligibleCount.ToString(CultureInfo.InvariantCulture) + " complete prior weeks in " + string.Join("+", sources);

    /// <summary>The document: <c>metrics.build_metrics(world, iso_week, generated_utc, business_hours)</c>.</summary>
    public static Dictionary<string, object?> BuildMetrics(MetricsWorld world, string isoWeek, string generatedUtc, BusinessHours hours)
    {
        ArgumentNullException.ThrowIfNull(world);
        FileLog.Write($"[Metrics] BuildMetrics: {isoWeek} for {world.Label} ({world.Zone.Name})");
        var week = new MetricsWeek(world, isoWeek, hours);
        var (values, coverage) = ComputeAll(week, log: (group, output) =>
            FileLog.Write("[Metrics]   " + group + ": " + string.Join(", ", output.Keys.OrderBy(k => k, StringComparer.Ordinal))));
        var weekOriginCounts = new Origin.Counts();
        foreach (var p in week.UserPrompts)
            weekOriginCounts.Add(p.Origin ?? throw new MentorDataException("The user record at " + p.Pid + " has no origin."),
                p.OriginRule ?? throw new MentorDataException("The user record at " + p.Pid + " has no origin rule."));
        FileLog.Write("[Metrics]   " + Origin.SummaryLine(world.Label + " " + isoWeek, weekOriginCounts));

        // Every prior week is computed; which of them feed a metric's baseline is decided per metric by
        // source coverage. A covered week with no rows is a real zero.
        var priorLabels = MentorReaders.PriorWeeks(isoWeek, BaselineWeeks);
        var priors = new Dictionary<string, MetricsWeek>(StringComparer.Ordinal);
        var priorValues = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var label in priorLabels)
        {
            var prior = new MetricsWeek(world, label, hours);
            priors[label] = prior;
            priorValues[label] = ComputeAll(prior, forBaseline: true).Values;
            var covered = SourceNames.Where(prior.SourceCovers).ToList();
            FileLog.Write("[Metrics]   prior " + label + ": " + prior.Sessions.Count + " sessions, " + prior.UserPrompts.Count + " user prompts, "
                + prior.Transcripts.Count + " transcripts; fully covered by: " + (covered.Count > 0 ? string.Join("+", covered) : "no source"));
        }
        var weeksBySource = new Dictionary<string, object?>();
        foreach (var source in SourceNames)
            weeksBySource[source] = priorLabels.Where(label => priors[label].SourceCovers(source)).Cast<object?>().ToList();
        var sourceCoverage = new Dictionary<string, object?>();
        foreach (var source in SourceNames)
        {
            var start = world.SourceStart(source);
            sourceCoverage[source] = new Dictionary<string, object?>
            {
                ["earliest_record_utc"] = start is null ? null : MentorReaders.StampUtc(start.Value),
                ["extract_time_utc"] = MentorReaders.StampUtc(world.SourceEnd.For(source)),
                ["complete_prior_weeks"] = weeksBySource[source],
            };
        }

        var groups = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var group in GroupOrder)
        {
            groups[group] = new Dictionary<string, object?>();
            foreach (var key in GroupIds[group])
            {
                var definition = DefinitionOf(key);
                var sources = MetricSources(key);
                var eligible = priorLabels.Where(label => priors[label].CoveredBy(sources)).ToList();
                var value = values[key];
                object? baseline = null;
                var cov = (Dictionary<string, object?>)coverage[key]!;
                if (NoBaseline.Contains(key))
                {
                    eligible = new List<string>();
                    cov = With(cov, "baseline_note", NoBaselineNote);
                }
                else if (eligible.Count >= MinBaselineWeeks)
                {
                    baseline = BaselineOf(value, eligible.Select(label => priorValues[label][key]).ToList());
                }
                else
                {
                    cov = With(cov, "baseline_note", BaselineNote(eligible.Count, sources));
                }
                groups[group][key] = new Dictionary<string, object?>
                {
                    ["value"] = value,
                    ["baseline"] = baseline,
                    ["baseline_weeks"] = eligible.Cast<object?>().ToList(),
                    ["unit"] = definition.Unit,
                    ["coverage"] = cov,
                    ["source"] = definition.Source,
                    ["definition"] = definition.Text,
                };
            }
        }

        var stamps = week.UserPrompts.Select(p => p.Ts)
            .Concat(week.Sessions.Select(s => s.Started))
            .Concat(week.EventsInWeek.Select(e => e.Ts))
            .Concat(week.Transcripts.Select(t => t.Ts));
        var daysWithData = stamps.Select(x => week.Local(x).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal).Cast<object?>().ToList();
        var agents = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in week.Sessions)
        {
            var agent = string.IsNullOrEmpty(s.Agent) ? "unknown" : s.Agent;
            agents[agent] = agents.GetValueOrDefault(agent) + 1;
        }
        var agentsValue = new Dictionary<string, object?>();
        foreach (var agent in agents.Keys.OrderBy(a => a, StringComparer.Ordinal)) agentsValue[agent] = agents[agent];
        var claudeReporting = (long)week.Sessions.Count(s => s.Agent == "ClaudeCode" && s.PeakContext is not null);
        var unresolved = week.UserPrompts.Where(p => p.Origin == "unresolved").ToList();
        long allWords = week.UserPrompts.Sum(p => (long)p.Words);
        var originCounts = new Dictionary<string, object?>();
        foreach (var (origin, n) in Origin.ClassCounts(weekOriginCounts)) originCounts[origin] = (long)n;
        var originRules = new Dictionary<string, object?>();
        foreach (var (key, n) in weekOriginCounts.Items.OrderBy(kv => kv.Key.Origin, StringComparer.Ordinal).ThenBy(kv => kv.Key.Rule, StringComparer.Ordinal))
            originRules[key.Rule] = (long)n;
        var document = new Dictionary<string, object?>
        {
            ["account_label"] = world.Label,
            ["week"] = isoWeek,
            ["time_zone"] = world.Zone.Name,
            ["generated_utc"] = generatedUtc,
            ["coverage"] = new Dictionary<string, object?>
            {
                ["days_with_data"] = daysWithData,
                ["agents"] = agentsValue,
                ["sessions_in_week"] = (long)week.Sessions.Count,
                ["open_sessions_in_week"] = (long)week.Sessions.Count(s => s.Ended is null),
                ["user_prompts_in_week"] = (long)week.UserPrompts.Count,
                ["human_prompts_in_week"] = (long)week.HumanPrompts.Count,
                ["origin_counts"] = originCounts,
                ["origin_rules"] = originRules,
                ["unresolved_sentence"] = UnresolvedSentence(unresolved.Count, Share(unresolved.Sum(p => (long)p.Words), allWords)),
                ["chat_relay_not_counted"] = true,
                ["chat_relay_sentence"] = ChatRelaySentence,
                ["hours_caution"] = HoursCautionSentence,
                ["transcripts_in_week"] = (long)week.Transcripts.Count,
                ["claude_only_metrics_apply"] = claudeReporting > 0,
                ["claude_sessions_reporting_context"] = claudeReporting,
                ["torn_prompt_log_lines"] = (long)world.Torn.Count,
                ["recovered_prompt_log_records"] = (long)world.Recovered,
                ["lost_prompt_log_records"] = (long)world.Lost,
                ["source_coverage"] = sourceCoverage,
                ["baseline_weeks_by_source"] = weeksBySource,
            },
        };
        foreach (var group in GroupOrder) document[group] = groups[group];
        FileLog.Write($"[Metrics] BuildMetrics: {isoWeek} for {world.Label} done, {week.Sessions.Count} sessions, {week.HumanPrompts.Count} human prompts");
        return document;
    }
}
