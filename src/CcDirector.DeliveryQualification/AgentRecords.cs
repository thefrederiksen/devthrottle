using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcDirector.DeliveryQualification;

/// <summary>What the agent's own records say about one sent text.</summary>
/// <param name="Found">The whole text is in a record written after the send - typed, or in a payload file the
/// record points at.</param>
/// <param name="ViaFile">It arrived as a payload file the agent was told to read (the long-text route for Codex,
/// Copilot and OpenCode, and the @-reference route).</param>
/// <param name="Doubled">One record holds the text twice - the "typed twice, run together" failure.</param>
/// <param name="Partial">Not the whole text, but its start is there - a truncated delivery.</param>
/// <param name="Where">The file the evidence came from.</param>
public sealed record RecordsVerdict(bool Found, bool ViaFile, bool Doubled, bool Partial, string? Where);

/// <summary>
/// THE INDEPENDENT WITNESS. Reads each agent's own storage - never the Director's reader of it, so a fault in the
/// product's reader cannot certify itself - and answers whether a sent text reached the agent whole. Compared
/// after folding whitespace and invisible characters, and in Unicode normal form C, because agents re-wrap line
/// endings and strip invisible characters; every visible character must still be there, in order.
/// </summary>
public static class AgentRecords
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly Regex PayloadFile = new(@"input_\d{8}_\d{6}_\w+\.txt", RegexOptions.CultureInvariant);

    /// <summary>Where each agent keeps its records.</summary>
    public static IReadOnlyList<string> RootsFor(string agent) => agent switch
    {
        "claude" => [Path.Combine(Home, ".claude", "projects")],
        "codex" => [Path.Combine(Home, ".codex", "sessions")],
        "pi" => [Path.Combine(Home, ".pi", "agent", "sessions")],
        "gemini" => [Path.Combine(Home, ".gemini", "tmp")],
        "grok" => [Path.Combine(Home, ".grok", "sessions"), Path.Combine(Home, ".grok", "chats")],
        "opencode" => [Path.Combine(Home, ".local", "share", "opencode")],
        "copilot" => [Path.Combine(Home, ".copilot")],
        _ => throw new ArgumentException($"No records location is known for agent '{agent}'."),
    };

    /// <summary>Only directories whose name mentions the rig's repositories are searched under these roots, because
    /// they hold every conversation on the machine and are keyed by project folder.</summary>
    private static bool ScopedByProject(string agent) => agent is "claude" or "pi";

    public static RecordsVerdict Check(string agent, DateTime sinceUtc, string text, IReadOnlyList<string> repoDirs)
    {
        var needle = Normalize(text);
        var head = needle.Length > 40 ? needle[..40] : needle;
        var partialWhere = (string?)null;

        foreach (var file in CandidateFiles(agent, sinceUtc))
        {
            IEnumerable<string> strings;
            var isStore = file.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                          || file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                          || file.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);
            if (isStore)
            {
                var raw = ReadShared(file);
                if (raw is null) continue;
                var body = Encoding.UTF8.GetString(raw);
                if (AllLinesPresent(body, text)) return new(true, false, false, false, file);
                if (FindPayload(body, needle, repoDirs) is { } p) return new(true, true, false, false, p);
                continue;
            }

            strings = StringsOf(file);
            foreach (var s in strings)
            {
                var hay = Normalize(s);
                var at = hay.IndexOf(needle, StringComparison.Ordinal);
                if (at >= 0)
                {
                    // Anything else in the same record is a MERGE - another text run together with this one, the
                    // corruption of pull request #1513 - unless it is a wrapper the agent puts round a paste.
                    var doubled = hay.IndexOf(needle, at + Math.Max(1, needle.Length), StringComparison.Ordinal) >= 0
                                  || Leftover(hay, needle).Length > 0;
                    return new(true, false, doubled, false, file);
                }
                if (FindPayload(s, needle, repoDirs) is { } p)
                {
                    // The file route's record must be the instruction line alone.
                    var merged = !Normalize(s).StartsWith("Read file input_", StringComparison.Ordinal);
                    return new(true, true, merged, false, p);
                }
                if (partialWhere is null && head.Length > 0 && hay.Contains(head, StringComparison.Ordinal))
                    partialWhere = file;
            }
        }
        return new(false, false, false, partialWhere is not null, partialWhere);
    }

    private static readonly Regex PasteWrapper = new(@"^<pasted_content id=""[^""]*"">|</pasted_content(?: id=""[^""]*"")?>$", RegexOptions.CultureInvariant);

    /// <summary>What a record holds besides the text, once the agent's own paste wrapper is removed.</summary>
    private static string Leftover(string record, string needle)
    {
        var rest = record.Replace(needle, "", StringComparison.Ordinal).Trim();
        return PasteWrapper.Replace(rest, "").Trim();
    }

    /// <summary>A record that names a payload file whose content is the whole text.</summary>
    private static string? FindPayload(string record, string needle, IReadOnlyList<string> repoDirs)
    {
        foreach (Match m in PayloadFile.Matches(record))
        {
            foreach (var repo in repoDirs)
            {
                var path = Path.Combine(repo, ".temp", m.Value);
                if (!File.Exists(path)) continue;
                if (Normalize(File.ReadAllText(path)).Contains(needle, StringComparison.Ordinal)) return path;
            }
        }
        return null;
    }

    private static bool AllLinesPresent(string body, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return false;
        foreach (var line in lines)
        {
            var escaped = JsonSerializer.Serialize(line)[1..^1];
            if (!body.Contains(line, StringComparison.Ordinal) && !body.Contains(escaped, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>When the rig started; a record file created after it may belong to the rig.</summary>
    public static DateTime RigStartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The files that may hold the rig's records. NOT chosen by last-write time alone: Windows updates that time
    /// lazily for a file an agent holds open and keeps appending to (Codex does), so a record written a second ago
    /// can carry a time from before the send. That filter missed a delivered Codex prompt on 24 September. Files are
    /// chosen by where they are instead - the rig's own project folders, Codex's folder for today, a store file -
    /// and elsewhere by creation time, which is set once and is exact. Every text carries its own token, so reading
    /// an extra file cannot produce a false match.
    /// </summary>
    private static IEnumerable<string> CandidateFiles(string agent, DateTime sinceUtc)
    {
        var cutoff = sinceUtc.AddSeconds(-2);
        foreach (var root in RootsFor(agent))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs = ScopedByProject(agent)
                ? Directory.EnumerateDirectories(root).Where(d => d.Contains("delivery-qa", StringComparison.OrdinalIgnoreCase))
                : agent == "codex"
                    ? new[] { DateTime.Now, DateTime.Now.AddDays(-1) }
                        .Select(d => Path.Combine(root, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"))).Where(Directory.Exists)
                    : [root];
            var everyFile = ScopedByProject(agent) || agent == "codex";
            foreach (var dir in dirs)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    var wanted = ext is ".jsonl" or ".json" or ".db" or ".sqlite" || f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase);
                    if (!wanted) continue;
                    var isStore = ext is ".db" or ".sqlite" || f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase);
                    if (everyFile || isStore) { yield return f; continue; }
                    DateTime written, created;
                    try { written = File.GetLastWriteTimeUtc(f); created = File.GetCreationTimeUtc(f); } catch (IOException) { continue; }
                    if (written >= cutoff || created >= RigStartedUtc.AddSeconds(-2)) yield return f;
                }
            }
        }
    }

    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every string value in a JSON or JSON-lines file. A line that does not parse (half written) is
    /// skipped.</summary>
    private static List<string> StringsOf(string file)
    {
        var raw = ReadShared(file);
        var result = new List<string>();
        if (raw is null) return result;
        var text = Encoding.UTF8.GetString(raw);
        var chunks = file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? text.Split('\n') : [text];
        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            try
            {
                using var doc = JsonDocument.Parse(chunk);
                // A queue record repeats the prompt Claude Code also writes as a user line; reading both would
                // hide nothing but would make a doubled prompt harder to see, so it is skipped.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "queue-operation")
                    continue;
                Collect(doc.RootElement, result);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static void Collect(JsonElement e, List<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: into.Add(e.GetString() ?? ""); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Collect(p.Value, into); break;
            case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) Collect(i, into); break;
        }
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control) continue;
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
