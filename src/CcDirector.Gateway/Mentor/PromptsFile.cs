using System.Globalization;
using System.Text;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of the reference's <c>prompts_file.py</c>: the full-prompt file, <c>prompts-human.md</c> - every
/// prompt the product stamped as the developer's own that week (origin class human), one section per session
/// in start order, each prompt in time order with its local time, modality, surface and word count, and its
/// full text. No assistant text. The first block is followed by a session index: id8 | repo name | human
/// prompts | started by | session name, one line per session in the file's order - the ids the mentor's
/// topic lines cite. ASCII only, through the packet's transliteration table.
/// </summary>
public static class PromptsFile
{
    public const string FileName = "prompts-human.md";
    public const string OwnPromptsSentence =
        "This file holds every prompt the product stamped as yours - your own typed or spoken words - and "
        + "nothing another account wrote.";
    public const string NoSessionRow = "no session row";
    public const string NoRepoName = "no repository name";
    public const string OpenAtExtract = "open at extract time";
    public const string Indent = "    ";

    /// <summary>The week's user records the classifier called human, in time order.</summary>
    public static List<MentorRecord> HumanPrompts(WeekData week)
        => week.Records.Where(r => r.Role == "user" && r.Origin == "human").ToList();

    /// <summary>SessionName from the session row, else the prompt log's sessionName, else unnamed - the
    /// packet's resolution, applied to the row whether or not the session started in the week.</summary>
    public static string SessionName(MentorSession? row, IReadOnlyList<MentorRecord> records)
    {
        if (row is not null && !string.IsNullOrEmpty(row.Name)) return row.Name;
        foreach (var record in records)
            if (!string.IsNullOrEmpty(record.Name)) return record.Name;
        return "(unnamed session)";
    }

    /// <summary>One session of the ordered list: its id, its row (or null), its human prompts.</summary>
    public sealed record OrderedSession(string Id, MentorSession? Row, List<MentorRecord> Records);

    /// <summary>The sessions with human prompts in session start order: StartedAtUtc from the row, or the
    /// first human prompt's time when the session has no row; ties by id.</summary>
    public static List<OrderedSession> OrderedSessions(IReadOnlyList<MentorRecord> prompts, IReadOnlyDictionary<string, MentorSession> sessions)
    {
        var bySession = new Dictionary<string, List<MentorRecord>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var record in prompts)
        {
            if (!bySession.TryGetValue(record.Session, out var list))
            {
                bySession[record.Session] = list = new List<MentorRecord>();
                order.Add(record.Session);
            }
            list.Add(record);
        }
        var entries = new List<(DateTime Start, string Id, MentorSession? Row, List<MentorRecord> Records)>();
        foreach (var sid in order)
        {
            var records = bySession[sid];
            sessions.TryGetValue(sid, out var row);
            var start = row is not null ? row.Started : records[0].Ts;
            entries.Add((start, sid, row, records));
        }
        return entries.OrderBy(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new OrderedSession(e.Id, e.Row, e.Records)).ToList();
    }

    /// <summary>The first eight characters of a session id, as Python's <c>sid[:8]</c>: a shorter id is itself.</summary>
    public static string Id8(string sessionId) => sessionId.Length > 8 ? sessionId.Substring(0, 8) : sessionId;

    /// <summary>Every line of the text indented by <see cref="Indent"/>; an empty text gives one indented empty line.</summary>
    public static string IndentBlock(string text)
        => string.Join("\n", text.Split('\n').Select(line => Indent + line));

    public static List<string> RenderIndex(WeekData week, IReadOnlyList<OrderedSession> ordered, Ascii ascii)
    {
        var lines = new List<string>
        {
            "## Session index", "",
            "One line per session below, in the same order: the first eight characters of the session "
            + "id | repository name | human prompts | started by | session name.", "",
        };
        foreach (var entry in ordered)
        {
            string repo, startedBy;
            if (entry.Row is not null)
            {
                repo = !string.IsNullOrEmpty(entry.Row.RepoName) ? entry.Row.RepoName : NoRepoName;
                startedBy = entry.Row.OriginKind;
            }
            else
            {
                repo = NoSessionRow;
                startedBy = NoSessionRow;
            }
            lines.Add("- " + Id8(entry.Id) + " | " + ascii.Text(repo) + " | " + entry.Records.Count.ToString(CultureInfo.InvariantCulture) + " | "
                + startedBy + " | " + ascii.Text(SessionName(entry.Row, entry.Records)));
        }
        lines.Add("");
        return lines;
    }

    public static List<string> RenderFirstBlock(WeekData week, IReadOnlyList<MentorRecord> prompts, int sessionCount)
    {
        var lines = new List<string> { "# Your prompts - " + week.Label + " - " + week.Week, "" };
        lines.Add("- account: " + week.Label);
        var monday = week.Zone.ToLocal(week.Start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sunday = week.Zone.ToLocal(week.End).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lines.Add("- week: " + week.Week + ", Monday " + monday + " to Sunday " + sunday + ", local time");
        lines.Add("- time zone: " + week.Zone.Name);
        var words = prompts.Sum(r => (long)r.Words);
        lines.Add("- human prompts: " + prompts.Count.ToString(CultureInfo.InvariantCulture) + "; words: " + words.ToString(CultureInfo.InvariantCulture)
            + "; sessions they fall in: " + sessionCount.ToString(CultureInfo.InvariantCulture));
        lines.Add("- " + OwnPromptsSentence);
        lines.Add("");
        return lines;
    }

    public static List<string> RenderSession(WeekData week, string sid, MentorSession? row, IReadOnlyList<MentorRecord> records, Ascii ascii)
    {
        var lines = new List<string> { "#### Session " + sid + " - " + ascii.Text(SessionName(row, records)), "" };
        if (row is not null)
        {
            var repo = !string.IsNullOrEmpty(row.RepoName) ? row.RepoName : NoRepoName;
            var endText = row.Ended is not null ? week.Stamp(row.Ended.Value) : OpenAtExtract;
            lines.Add("repo: " + ascii.Text(repo));
            lines.Add("agent: " + (string.IsNullOrEmpty(row.Agent) ? "unknown" : row.Agent));
            lines.Add("start: " + week.Stamp(row.Started) + "; end: " + endText);
            lines.Add("started by: " + row.OriginKind);
        }
        else
        {
            var agents = records.Where(r => !string.IsNullOrEmpty(r.Agent)).Select(r => r.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
            lines.Add("repo: " + NoSessionRow);
            lines.Add("agent: " + (agents.Count > 0 ? string.Join("/", agents) : "unknown") + " (from the prompt log)");
            lines.Add("start: " + NoSessionRow + " (first prompt " + week.Stamp(records[0].Ts) + "); end: " + NoSessionRow);
            lines.Add("started by: " + NoSessionRow);
        }
        lines.Add("");
        foreach (var record in records)
        {
            lines.Add("[" + week.Stamp(record.Ts) + " local, " + (record.OriginModality ?? "None") + "/"
                + (record.OriginSurface ?? "None") + ", " + record.Words.ToString(CultureInfo.InvariantCulture) + " words]");
            lines.Add(IndentBlock(ascii.Text(record.Text)));
            lines.Add("");
        }
        return lines;
    }

    /// <summary>The whole file: (text, session count, replaced non-ASCII count).</summary>
    public static (string Text, int SessionCount, int Replaced) RenderFile(WeekData week, IReadOnlyList<MentorRecord> prompts, IReadOnlyDictionary<string, MentorSession> sessions)
    {
        var ascii = new Ascii();
        var ordered = OrderedSessions(prompts, sessions);
        var lines = RenderFirstBlock(week, prompts, ordered.Count);
        lines.AddRange(RenderIndex(week, ordered, ascii));
        foreach (var entry in ordered)
            lines.AddRange(RenderSession(week, entry.Id, entry.Row, entry.Records, ascii));
        lines.Add("End of prompts. " + prompts.Count.ToString(CultureInfo.InvariantCulture) + " human prompts.");
        var builder = new StringBuilder();
        foreach (var line in lines) builder.Append(line).Append('\n');
        return (builder.ToString(), ordered.Count, ascii.Replaced);
    }
}
