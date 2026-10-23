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

    /// <summary>Claude Code's configuration folder, which holds its conversations: CLAUDE_CONFIG_DIR when that is set
    /// (a scenario with a configuration of its own, <see cref="ScratchClaudeConfig"/>), else the home folder's.</summary>
    public static string ClaudeConfigFolder() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude");

    /// <summary>Where each agent keeps its records.</summary>
    public static IReadOnlyList<string> RootsFor(string agent) => agent switch
    {
        "claude" => [Path.Combine(ClaudeConfigFolder(), "projects")],
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
                                  || Leftover(hay, needle).Length > 0
                                  || TurnsHolding(agent, sinceUtc, needle) > 1;
                    return new(true, false, doubled, false, file);
                }
                if (FindPayload(s, needle, repoDirs) is { } p)
                {
                    // The file route's record must be the instruction line alone, or (Claude Code) the @-reference alone.
                    var record = s.Trim();
                    var merged = !Normalize(s).StartsWith("The user's message for this turn is in the file", StringComparison.Ordinal)
                                 && !(record.StartsWith('@') && !record.Any(char.IsWhiteSpace));
                    return new(true, true, merged, false, p);
                }
                if (partialWhere is null && head.Length > 0 && hay.Contains(head, StringComparison.Ordinal))
                    partialWhere = file;
            }
        }
        return new(false, false, false, partialWhere is not null, partialWhere);
    }

    /// <summary>
    /// How many USER TURNS hold the text, across every candidate file: one record kind per agent, so an agent that
    /// writes the same prompt into two kinds of record (Codex: an event and a response item) is not counted twice.
    /// Two turns is a prompt delivered twice - the double a resend can cause, which a check that stops at the first
    /// hit reads as ARRIVED (review finding 3).
    /// </summary>
    private static int TurnsHolding(string agent, DateTime sinceUtc, string needle)
    {
        var turns = 0;
        foreach (var file in CandidateFiles(agent, sinceUtc).Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            var raw = ReadShared(file);
            if (raw is null) continue;
            foreach (var line in Encoding.UTF8.GetString(raw).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!IsUserTurn(agent, doc.RootElement)) continue;
                    var strings = new List<string>();
                    Collect(doc.RootElement, strings);
                    if (strings.Any(x => Normalize(x).Contains(needle, StringComparison.Ordinal))) turns++;
                }
                catch (JsonException) { }
            }
        }
        return turns;
    }

    internal static bool IsUserTurn(string agent, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var type = Str(root, "type");
        return agent switch
        {
            // A prompt sent while Claude Code works is recorded as a queued_command attachment, not a user line, when it is
            // delivered inside the running turn (issue #4378, the mid-turn cells); it is the person's prompt all the same.
            "claude" => (type == "user" && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
                        || IsQueuedPrompt(root),
            // Older Codex wrote the prompt as an event_msg user_message; Codex 0.16x writes it as a response_item message
            // with role user (and no user_message event), measured 23 October 2026. Either is a user turn.
            "codex" => root.TryGetProperty("payload", out var p)
                       && ((type == "event_msg" && Str(p, "type") == "user_message")
                           || (type == "response_item" && Str(p, "type") == "message" && Str(p, "role") == "user")),
            "pi" => type == "message" && root.TryGetProperty("message", out var msg) && Str(msg, "role") == "user",
            _ => false,
        };
    }

    /// <summary>Claude Code's record of a prompt typed while it worked: an attachment of type queued_command in prompt mode.</summary>
    private static bool IsQueuedPrompt(JsonElement root) =>
        root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "attachment"
        && root.TryGetProperty("attachment", out var a) && a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty("type", out var at) && at.ValueKind == JsonValueKind.String && at.GetString() == "queued_command"
        && !(a.TryGetProperty("commandMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() != "prompt");

    /// <summary>
    /// Whether <paramref name="root"/> is the agent's own TOOL CALL - the record of it asking to run a tool, written before
    /// the tool runs and so before any approval is asked (issue #4378: the Codex approval ask read from its records):
    ///  - Claude Code: an assistant line with a tool_use part;
    ///  - Codex: a response_item of type function_call, custom_tool_call or local_shell_call, or an event naming an
    ///    approval request;
    ///  - Pi: an assistant message with a toolCall part.
    /// The prompt that asked for the tool also holds its words (Codex echoes it in an item_completed event), so a reader
    /// that only skipped user turns took the prompt for the ask.
    /// </summary>
    internal static bool IsToolCall(string agent, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool HasPart(JsonElement message, string partType) =>
            message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            && c.EnumerateArray().Any(p => Str(p, "type") == partType);
        var type = Str(root, "type");
        return agent switch
        {
            "claude" => type == "assistant" && root.TryGetProperty("message", out var m) && HasPart(m, "tool_use"),
            "codex" => root.TryGetProperty("payload", out var p)
                       && ((type == "response_item" && Str(p, "type") is "function_call" or "custom_tool_call" or "local_shell_call")
                           || (type == "event_msg" && Str(p, "type") is { } e && e.EndsWith("approval_request", StringComparison.Ordinal))),
            "pi" => type == "message" && root.TryGetProperty("message", out var pm) && Str(pm, "role") == "assistant" && HasPart(pm, "toolCall"),
            _ => false,
        };
    }

    /// <summary>
    /// THE STRICT WITNESS (issue #4378). <see cref="Check"/> folds whitespace and so cannot see a lost line break, a
    /// doubled space or a tab turned into a space. This finds the USER TURN that holds <paramref name="token"/> and
    /// returns its text as the agent recorded it, so the caller can compare it character for character. Null when no
    /// user turn holds the token. The agent's own paste wrapper is removed; nothing else is changed.
    /// </summary>
    public static string? UserTurnText(string agent, DateTime sinceUtc, string token)
    {
        foreach (var file in CandidateFiles(agent, sinceUtc).Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            var raw = ReadShared(file);
            if (raw is null) continue;
            foreach (var line in Encoding.UTF8.GetString(raw).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(token, StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!IsUserTurn(agent, doc.RootElement)) continue;
                    var text = TurnText(agent, doc.RootElement);
                    if (text is not null && text.Contains(token, StringComparison.Ordinal))
                        // Only the agent's paste wrapper is removed - never surrounding whitespace, which a route may
                        // have lost (review of #4390, finding 3).
                        return StripPasteWrapper(text);
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    /// <summary>
    /// How many user turns hold <paramref name="token"/> (issue #4378, the owner-draft cell, whose turn holds the draft as
    /// well as the text, so the folding witness reads it as merged). Codex writes one prompt in two record kinds in some
    /// versions (an event and a response item), so for Codex the larger of the two counts is taken; for the others every
    /// kind adds up, so a prompt written once as Claude Code's queued attachment and again as a user line is two.
    /// </summary>
    public static int UserTurnsHolding(string agent, DateTime sinceUtc, string token) => CountUserTurns(agent, RecentLines(agent, sinceUtc), token);

    internal static int CountUserTurns(string agent, IEnumerable<string> lines, string token)
    {
        var byKind = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            if (!line.Contains(token, StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!IsUserTurn(agent, root) || TurnText(agent, root) is not { } text || !text.Contains(token, StringComparison.Ordinal)) continue;
                var kind = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            }
            catch (JsonException) { }
        }
        return agent == "codex" ? byKind.Values.DefaultIfEmpty().Max() : byKind.Values.Sum();
    }

    /// <summary>Every JSON-lines record of <paramref name="agent"/> that may have been written since
    /// <paramref name="sinceUtc"/>, for the working-or-waiting matrix to read times from (issue #4378).</summary>
    public static IEnumerable<string> RecentLines(string agent, DateTime sinceUtc)
    {
        foreach (var file in CandidateFiles(agent, sinceUtc).Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            var raw = ReadShared(file);
            if (raw is null) continue;
            foreach (var line in Encoding.UTF8.GetString(raw).Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
        }
    }

    /// <summary>The text inside the agent's paste wrapper when the whole record is one, else the record unchanged.</summary>
    internal static string StripPasteWrapper(string text)
    {
        var m = Regex.Match(text, @"^<pasted_content id=""[^""]*"">(?<body>[\s\S]*)</pasted_content(?: id=""[^""]*"")?>$", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["body"].Value : text;
    }

    /// <summary>The prompt text of one user-turn record: Claude Code's message content, Codex's user message, Pi's
    /// message content - a string, or the text parts of a list, joined.</summary>
    internal static string? TurnText(string agent, JsonElement root)
    {
        static string? FromContent(JsonElement content) => content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                .Select(p => p.GetProperty("text").GetString())),
            _ => null,
        };
        return agent switch
        {
            "claude" => IsQueuedPrompt(root) ? root.GetProperty("attachment").TryGetProperty("prompt", out var qp) && qp.ValueKind == JsonValueKind.String ? qp.GetString() : null
                : root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) ? FromContent(c) : null,
            "codex" => !root.TryGetProperty("payload", out var p) ? null
                : p.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString()
                : p.TryGetProperty("content", out var pc0) ? FromContent(pc0) : null,
            "pi" => root.TryGetProperty("message", out var pm) && pm.TryGetProperty("content", out var pc) ? FromContent(pc) : null,
            _ => null,
        };
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
