using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Generalised Session-Wingman for cc-director.
///
/// Every method on this class is a one-shot side-call to `claude --print --bare
/// --model haiku --tools ""` carrying a focused prompt - exactly the pattern
/// <see cref="RecapGenerator"/> uses for the existing recap feature. The
/// "Session Wingman" from the PRD is the conceptual sum of these short
/// fresh-context calls; we do NOT spawn a long-running shadow process per
/// session.
///
/// Methods correspond 1:1 to the PRD's Session Wingman responsibilities.
/// </summary>
public static class WingmanService
{
    /// <summary>Cheap fast model we run the Wingman on. Haiku family.</summary>
    public const string DefaultModel = "haiku";

    /// <summary>
    /// Strong model used for on-demand, user-facing wingman work (the "Explain"
    /// briefing) where answer quality matters more than latency/cost. Matches the
    /// best model a real session would run on.
    /// </summary>
    public const string StrongModel = "opus";

    /// <summary>Hard timeout per Wingman call.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    // ====================================================================
    // Terminal state classification (agent-agnostic, terminal-only)
    // ====================================================================

    /// <summary>
    /// Classify what an agent's session is doing RIGHT NOW from the tail of its
    /// terminal, with no hooks and no agent-specific rules - the model reads the
    /// rendered screen the way a person would. Returns one of:
    /// working | waiting_for_input | waiting_for_permission | idle | cancelled | unknown,
    /// plus a one-line reason. Stateless: one fresh Haiku call, nothing persisted.
    ///
    /// This is the "judge" stage of the terminal state detector - invoked when the
    /// cheap byte-activity gate notices the terminal has gone quiet. Fails closed to
    /// ("unknown", reason) so a missing CLI or parse error never fabricates a state.
    /// </summary>
    public static async Task<(string state, string reason)> ClassifyTerminalStateAsync(
        string terminalTail, string agentName, string claudeExePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claudeExePath))
            return ("unknown", "no claude CLI configured");
        if (string.IsNullOrWhiteSpace(terminalTail))
            return ("unknown", "empty terminal");

        var prompt = BuildTerminalStatePrompt(terminalTail, agentName ?? "an AI coding agent");
        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            return ParseTerminalStateJson(stdout);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] ClassifyTerminalStateAsync FAILED: {ex.Message}");
            return ("unknown", "classify call failed: " + ex.Message);
        }
    }

    private static string BuildTerminalStatePrompt(string terminalTail, string agentName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are reading the tail of a terminal running {agentName} (an AI coding agent in an interactive TUI). Decide what the session is doing RIGHT NOW, the way a person glancing at the screen would.");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{\"state\": \"working|waiting_for_input|waiting_for_permission|idle|cancelled|unknown\", \"reason\": \"<one short sentence citing what on screen tells you>\"}");
        sb.AppendLine();
        sb.AppendLine("How to decide (read the BOTTOM of the output - that is the most recent):");
        sb.AppendLine("- working: the agent is actively producing output - a spinner or progress animation, an elapsed-time counter (e.g. \"Brewed for 12s\"), an \"esc to interrupt\" footer, or text still streaming in.");
        sb.AppendLine("- waiting_for_permission: the agent has drawn a confirmation it cannot pass on its own - a yes/no box, a numbered choice list (\"1. Yes  2. No\"), or a \"[y/n]\" style prompt - and is parked on it.");
        sb.AppendLine("- waiting_for_input: the agent has finished and is sitting at an EMPTY input prompt waiting for the user to type the next instruction. No spinner, no question pending.");
        sb.AppendLine("- cancelled: the screen shows the last action was interrupted/cancelled (e.g. an \"Interrupted\" or \"Cancelled\" notice) and the agent is now back at the prompt doing nothing.");
        sb.AppendLine("- idle: nothing is happening and none of the above fit (e.g. a bare shell prompt, or a blank settled screen).");
        sb.AppendLine("- unknown: the tail is too garbled or sparse to tell.");
        sb.AppendLine();
        sb.AppendLine("Do not assume 'working' just because there is a lot of text; only an ACTIVE indicator at the bottom means working. When the bottom shows a prompt box with no spinner, the agent is waiting, not working.");
        sb.AppendLine();
        sb.AppendLine("TERMINAL TAIL (ANSI stripped; the end is the most recent):");
        sb.AppendLine(TruncateKeepEnd(terminalTail, 4000));
        return sb.ToString();
    }

    internal static (string state, string reason) ParseTerminalStateJson(string raw)
    {
        var valid = new[] { "working", "waiting_for_input", "waiting_for_permission", "idle", "cancelled", "unknown" };
        if (string.IsNullOrWhiteSpace(raw)) return ("unknown", "classifier returned empty output");

        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence > 0) s = s[..endFence].Trim();
        }
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "").Trim().ToLowerInvariant() : "";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "").Trim() : "";
            if (!valid.Contains(state)) return ("unknown", $"classifier returned invalid state '{state}'");
            return (state, reason);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] terminal-state JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            return ("unknown", "classifier JSON parse failed");
        }
    }

    // ====================================================================
    // Phase 1: Voice transcript cleanup
    // ====================================================================

    /// <summary>
    /// Clean a raw Whisper transcript into a polished prompt for a Claude Code
    /// agent.  Strips filler words, fixes obvious mis-transcriptions, preserves
    /// intent.  Output is a JSON object  { cleaned, reason }; we parse it.
    ///
    /// On any failure (no claude CLI, parse error, timeout) returns a result
    /// whose <see cref="VoiceCleanupResult.Cleaned"/> is the raw transcript verbatim
    /// (fail open) and <see cref="VoiceCleanupResult.Reason"/> describes the failure.
    /// </summary>
    public static async Task<VoiceCleanupResult> CleanVoiceTranscriptAsync(
        string rawTranscript,
        string repoPath,
        string claudeExePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new VoiceCleanupResult(rawTranscript ?? "", "empty raw transcript");
        if (string.IsNullOrWhiteSpace(claudeExePath))
            return new VoiceCleanupResult(rawTranscript, "no claude CLI configured");

        var prompt = BuildVoiceCleanupPrompt(rawTranscript, repoPath ?? "");

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            return ParseVoiceCleanupJson(stdout, rawTranscript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] CleanVoiceTranscriptAsync FAILED: {ex.Message}");
            return new VoiceCleanupResult(rawTranscript, "wingman call failed: " + ex.Message);
        }
    }

    private static string BuildVoiceCleanupPrompt(string raw, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a transcription cleanup assistant for a hands-free voice interface to a Claude Code agent.");
        sb.AppendLine();
        sb.AppendLine("The user just dictated the text below into their phone while driving.  Whisper transcribed it.");
        sb.Append("The text is a request, question, or instruction the user wants sent to the Claude Code agent that is working in ");
        sb.AppendLine(string.IsNullOrEmpty(repoPath) ? "their repository." : $"the `{repoPath}` repository.");
        sb.AppendLine();
        sb.AppendLine("Your job: produce the CLEANED version of the user's message, ready to send to the agent.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Remove filler words (um, uh, like, you know, kind of, basically, sort of).");
        sb.AppendLine("- Fix obvious mis-transcriptions where the meaning is clear.");
        sb.AppendLine("- Keep the user's intent and tone.  Do NOT paraphrase or improve beyond cleanup.");
        sb.AppendLine("- Do NOT add greetings, sign-offs, or commentary.");
        sb.AppendLine("- Do NOT answer the question yourself.  Just clean the prompt.");
        sb.AppendLine("- If the message is so unclear you cannot confidently clean it, output it verbatim.");
        sb.AppendLine("- One paragraph.  No bullet lists, no headings, no quotation marks around the result.");
        sb.AppendLine();
        sb.AppendLine("Output JSON only, no markdown fence, no other text, this exact shape:");
        sb.AppendLine("{\"cleaned\": \"<the cleaned prompt>\", \"reason\": \"<one short sentence explaining what you changed, or 'no changes needed' if minimal>\"}");
        sb.AppendLine();
        sb.AppendLine("RAW TRANSCRIPT:");
        sb.Append(raw);
        return sb.ToString();
    }

    internal static VoiceCleanupResult ParseVoiceCleanupJson(string raw, string fallbackRaw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new VoiceCleanupResult(fallbackRaw, "wingman returned empty output");

        var s = raw.Trim();

        // Defensive: if the model wrapped the JSON in a fence despite the prompt, strip it.
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence > 0) s = s[..endFence].Trim();
        }

        // Also tolerate a leading "json" label or trailing chatter by extracting first {...} block.
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var cleaned = root.TryGetProperty("cleaned", out var c) ? (c.GetString() ?? "") : "";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(cleaned))
                return new VoiceCleanupResult(fallbackRaw, "wingman returned empty 'cleaned' field");
            return new VoiceCleanupResult(cleaned.Trim(), string.IsNullOrEmpty(reason) ? "no changes needed" : reason.Trim());
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] cleanup JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            return new VoiceCleanupResult(fallbackRaw, "wingman JSON parse failed");
        }
    }

    // ====================================================================
    // Phase 2: Per-turn structured summary  (feeds Agent View AND voice TTS)
    // ====================================================================

    /// <summary>
    /// Summarise one completed turn for both screen readers (Agent View) and
    /// ear listeners (voice TTS).  Returns a populated <see cref="TurnSummary"/>
    /// even on Wingman failure (status field reflects what happened).
    ///
    /// Source of truth is the session's OWN terminal transcript (ANSI stripped) -
    /// the bytes that actually appeared on this session's PTY. The Wingman never
    /// reads Claude Code's shared per-repo .jsonl files, so it cannot pick up
    /// another session's conversation. See <see cref="TurnSummaryCache"/>.
    /// </summary>
    public static async Task<TurnSummary> SummarizeTurnAsync(
        string terminalTranscript,
        DateTime turnStartedAt,
        string repoPath,
        string claudeExePath,
        CancellationToken ct = default)
    {
        var summary = new TurnSummary
        {
            GeneratedAt = DateTime.UtcNow,
            TurnStartedAt = turnStartedAt,
        };

        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            summary.Status = "wingman_failed";
            summary.Error = "no claude CLI configured";
            summary.Headline = BuildFallbackHeadline();
            summary.SpokenText = "Agent finished. Check the screen for details.";
            return summary;
        }

        var prompt = BuildTurnSummaryPrompt(terminalTranscript ?? "", repoPath ?? "");

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            ParseTurnSummaryJsonInto(stdout, summary);
            return summary;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] SummarizeTurnAsync FAILED: {ex.Message}");
            summary.Status = "wingman_failed";
            summary.Error = ex.Message;
            summary.Headline = BuildFallbackHeadline();
            summary.SpokenText = "Agent finished. Check the screen for details.";
            return summary;
        }
    }

    /// <summary>
    /// Generic fallback headline used only when the Wingman side-call is unavailable
    /// or fails. We deliberately do NOT derive it from hook-reported tools/files: the
    /// Wingman's only content source is this session's terminal, and a failed call
    /// has no terminal-derived summary to show.
    /// </summary>
    private static string BuildFallbackHeadline() => "Agent completed a turn.";

    private static string BuildTurnSummaryPrompt(string terminalTranscript, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are summarising one turn of a Claude Code session for a user who does not want to read the raw terminal, and who may be listening to a voice playback while driving.");
        sb.AppendLine();
        sb.AppendLine("INPUT: the terminal output of the turn that just finished (the agent's rendered output with ANSI escape codes stripped). Read it and report what the agent did and whether it needs the user. Anything the agent is asking lives at the END of the output.");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"headline\": \"<one short sentence describing what the agent did this turn>\",");
        sb.AppendLine("  \"files_touched\": [\"<distinct file paths the output shows were touched, max 5; empty if none visible>\"],");
        sb.AppendLine("  \"commands_run\": [\"<distinct shell commands the output shows were run, max 3; empty if none visible>\"],");
        sb.AppendLine("  \"decisions\": [\"<key decisions or findings, max 3 bullets>\"],");
        sb.AppendLine("  \"needs_user\": \"<one of: 'no' | 'question' | 'error' | 'permission' | 'idle'>\",");
        sb.AppendLine("  \"needs_user_detail\": \"<short sentence if needs_user != 'no', empty otherwise>\",");
        sb.AppendLine("  \"needs_user_short\": \"<see rules below>\",");
        sb.AppendLine("  \"spoken_text\": \"<see rules below>\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules for needs_user:");
        sb.AppendLine("- 'question': pick this when the agent's output ends with an explicit question that the user must answer before the agent continues. A question mark at the END of the terminal output is a STRONG signal, even when most of the turn was technical work (analysis, code, diagrams). A polite \"Want me to ...?\", \"Should I ...?\", \"Would you like ...?\", \"OK to ...?\" at the end of an otherwise informational reply STILL counts as 'question'.");
        sb.AppendLine("- 'error': agent reports an error it cannot resolve on its own.");
        sb.AppendLine("- 'permission': agent paused for an OS-level permission prompt (Yes/No, [y/n]).");
        sb.AppendLine("- 'idle': agent finished cleanly and has nothing pending.");
        sb.AppendLine("- 'no': agent is mid-flow or returned a non-question informational reply with no trailing question.");
        sb.AppendLine();
        sb.AppendLine("Rules for needs_user_short:");
        sb.AppendLine("- Restate the agent's question VERBATIM. Copy the words the agent used.");
        sb.AppendLine("- DO NOT paraphrase, summarise, shorten, soften, or rewrite. The user trusts the agent's phrasing over yours.");
        sb.AppendLine("- If the agent's question spans multiple sentences, keep them all. Up to 500 characters.");
        sb.AppendLine("- Only trim trailing pleasantries (\"Let me know!\", \"Sound good?\"). Keep the substance.");
        sb.AppendLine("- If the agent asked multiple separate questions, include them all, separated by a space. Do NOT pick one.");
        sb.AppendLine("- Empty string when needs_user == 'no'.");
        sb.AppendLine();
        sb.AppendLine("Rules for spoken_text:");
        sb.AppendLine("- One to three short sentences.  Maximum about 280 characters.");
        sb.AppendLine("- Written for the ear: a human listening in a car.  Plain language.  No code, no symbols, no file paths, no commands.");
        sb.AppendLine("- Reads the FINDING or OUTCOME, not the process.  Example: \"Tests passed.  Three files were updated.  The login bug is fixed.\" NOT \"I ran dotnet test and got exit code zero...\"");
        sb.AppendLine("- If needs_user != \"no\", start with: \"I need you to <decide / answer / approve>.  <question>.\"");
        sb.AppendLine("- If the agent did nothing meaningful, spoken_text can be: \"Acknowledged, nothing to report.\"");
        sb.AppendLine();
        sb.AppendLine($"Repo: {repoPath}");
        sb.AppendLine();
        sb.AppendLine("TURN OUTPUT (this session's terminal, ANSI stripped; the END is the most recent text and is where any question lives - quote it verbatim):");
        sb.AppendLine(TruncateKeepEnd(terminalTranscript, 8000));
        return sb.ToString();
    }

    /// <summary>
    /// Keep the LAST <paramref name="max"/> characters of the input, with a short
    /// marker prefix when truncated. Used for fields where the trailing content
    /// matters (e.g. an agent reply whose question is at the end) — the
    /// front-truncating <see cref="Truncate"/> would cut that off.
    /// </summary>
    internal static string TruncateKeepEnd(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return "... [earlier text omitted] ..." + s[^max..];
    }

    internal static void ParseTurnSummaryJsonInto(string raw, TurnSummary summary)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            summary.Status = "parse_failed";
            summary.Error = "wingman returned empty output";
            summary.Headline = BuildFallbackHeadline();
            summary.SpokenText = "Agent finished. Check the screen for details.";
            return;
        }

        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence > 0) s = s[..endFence].Trim();
        }
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            if (root.TryGetProperty("headline", out var h)) summary.Headline = (h.GetString() ?? "").Trim();
            if (root.TryGetProperty("needs_user", out var n)) summary.NeedsUser = (n.GetString() ?? "no").Trim().ToLowerInvariant();
            if (root.TryGetProperty("needs_user_detail", out var nd)) summary.NeedsUserDetail = (nd.GetString() ?? "").Trim();
            if (root.TryGetProperty("needs_user_short", out var ns))
            {
                summary.NeedsUserShort = (ns.GetString() ?? "").Trim();
                if (summary.NeedsUserShort.Length > 500)
                    summary.NeedsUserShort = summary.NeedsUserShort[..497] + "...";
            }
            if (root.TryGetProperty("spoken_text", out var sp)) summary.SpokenText = (sp.GetString() ?? "").Trim();

            if (root.TryGetProperty("files_touched", out var f) && f.ValueKind == JsonValueKind.Array)
                summary.FilesTouched = f.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).Take(5).ToList();
            if (root.TryGetProperty("commands_run", out var c) && c.ValueKind == JsonValueKind.Array)
                summary.CommandsRun = c.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).Take(3).ToList();
            if (root.TryGetProperty("decisions", out var d) && d.ValueKind == JsonValueKind.Array)
                summary.Decisions = d.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).Take(3).ToList();

            // Defensive fill-ins so callers never get empty fields.
            if (string.IsNullOrEmpty(summary.Headline)) summary.Headline = BuildFallbackHeadline();
            if (string.IsNullOrEmpty(summary.SpokenText)) summary.SpokenText = summary.Headline;
            if (string.IsNullOrEmpty(summary.NeedsUser)) summary.NeedsUser = "no";

            // Trim spoken_text to a hard cap so TTS does not run forever.
            if (summary.SpokenText.Length > 320)
                summary.SpokenText = summary.SpokenText[..317] + "...";

            // Status was defaulted to "ok" in the ctor; leave it.
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] turn-summary JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            summary.Status = "parse_failed";
            summary.Error = "wingman JSON parse failed";
            summary.Headline = BuildFallbackHeadline();
            summary.SpokenText = "Agent finished. Check the screen for details.";
        }
    }

    // ====================================================================
    // Phase 5: Rules / memory enforcement (CLAUDE.md violations)
    // ====================================================================

    public static async Task<RuleViolationsResponse> CheckRulesAsync(
        TurnSummary latestSummary,
        string repoPath,
        string claudeExePath,
        CancellationToken ct = default)
    {
        var resp = new RuleViolationsResponse();
        if (latestSummary is null) { resp.Status = "no_summary"; return resp; }

        var rulesText = LoadRulesChain(repoPath, out var sources);
        if (string.IsNullOrWhiteSpace(rulesText)) { resp.Status = "no_rules"; return resp; }
        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            resp.Status = "wingman_failed";
            resp.Error = "no claude CLI configured";
            return resp;
        }

        var prompt = BuildRulesPrompt(rulesText, latestSummary, repoPath ?? "");
        string stdout;
        try { stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] CheckRulesAsync FAILED: {ex.Message}");
            resp.Status = "wingman_failed";
            resp.Error = ex.Message;
            return resp;
        }
        ParseRulesJsonInto(stdout, resp, sources.FirstOrDefault());
        return resp;
    }

    internal static string LoadRulesChain(string repoPath, out List<string> sourcesFound)
    {
        sourcesFound = new List<string>();
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(repoPath))
        {
            try
            {
                var dir = new DirectoryInfo(repoPath);
                while (dir is not null && dir.Exists)
                {
                    var candidate = Path.Combine(dir.FullName, "CLAUDE.md");
                    if (File.Exists(candidate))
                    {
                        sourcesFound.Add(candidate);
                        sb.AppendLine($"### From {candidate}");
                        sb.AppendLine(File.ReadAllText(candidate));
                        sb.AppendLine();
                    }
                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WingmanService] LoadRulesChain: walking parents failed: {ex.Message}");
            }
        }

        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                var globalClaude = Path.Combine(userProfile, ".claude", "CLAUDE.md");
                if (File.Exists(globalClaude) && !sourcesFound.Contains(globalClaude))
                {
                    sourcesFound.Add(globalClaude);
                    sb.AppendLine($"### From {globalClaude}");
                    sb.AppendLine(File.ReadAllText(globalClaude));
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] LoadRulesChain: global CLAUDE.md failed: {ex.Message}");
        }

        return sb.ToString();
    }

    private static string BuildRulesPrompt(string rulesText, TurnSummary summary, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a rules / memory enforcement assistant for a Claude Code agent.");
        sb.AppendLine();
        sb.AppendLine("Below are the user's CLAUDE.md files - rules and conventions the agent is supposed to follow.");
        sb.AppendLine("After the rules is a STRUCTURED SUMMARY of what the agent just did in its most recent turn.");
        sb.AppendLine();
        sb.AppendLine("Your job: identify CONCRETE violations of the rules by the agent's actions.  Do not invent violations.");
        sb.AppendLine("Be conservative.  When in doubt, return an empty list.");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{ \"violations\": [ { \"rule\": \"<short rule excerpt>\", \"what\": \"<what the agent did that broke it>\", \"severity\": \"info|warn|block\" } ] }");
        sb.AppendLine();
        sb.AppendLine("If there are no violations: { \"violations\": [] }");
        sb.AppendLine();
        sb.AppendLine("=== CLAUDE.md rule chain ===");
        sb.AppendLine(Truncate(rulesText, 12000));
        sb.AppendLine();
        sb.AppendLine("=== Recent turn summary ===");
        sb.AppendLine($"Repo: {repoPath}");
        sb.AppendLine($"Headline: {summary.Headline}");
        sb.AppendLine($"Files touched: {string.Join(", ", summary.FilesTouched)}");
        sb.AppendLine($"Commands run: {string.Join(" | ", summary.CommandsRun)}");
        sb.AppendLine($"Decisions: {string.Join(" | ", summary.Decisions)}");
        sb.AppendLine($"NeedsUser: {summary.NeedsUser}");
        sb.AppendLine($"NeedsUserDetail: {summary.NeedsUserDetail}");
        return sb.ToString();
    }

    internal static void ParseRulesJsonInto(string raw, RuleViolationsResponse resp, string? defaultSource)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            resp.Status = "parse_failed";
            resp.Error = "wingman returned empty output";
            return;
        }
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence > 0) s = s[..endFence].Trim();
        }
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("violations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                resp.Status = "parse_failed";
                resp.Error = "wingman JSON missing 'violations' array";
                return;
            }
            foreach (var v in arr.EnumerateArray())
            {
                var rule = v.TryGetProperty("rule", out var r) ? (r.GetString() ?? "").Trim() : "";
                var what = v.TryGetProperty("what", out var w) ? (w.GetString() ?? "").Trim() : "";
                var sev  = v.TryGetProperty("severity", out var sv) ? (sv.GetString() ?? "warn").Trim().ToLowerInvariant() : "warn";
                if (sev is not ("info" or "warn" or "block")) sev = "warn";
                if (string.IsNullOrWhiteSpace(rule) && string.IsNullOrWhiteSpace(what)) continue;
                resp.Violations.Add(new RuleViolation { Rule = rule, What = what, Severity = sev, Source = defaultSource });
            }
            resp.Status = "ok";
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] rules JSON parse failed: {ex.Message}");
            resp.Status = "parse_failed";
            resp.Error = "wingman JSON parse failed";
        }
    }

    // ====================================================================
    // Phase 6: Git awareness (no Haiku - run git locally)
    // ====================================================================

    public static async Task<GitSnapshot> GitSnapshotAsync(string repoPath, CancellationToken ct = default)
    {
        var snap = new GitSnapshot();
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            snap.Status = "not_a_repo"; snap.Error = "repo path missing or not a directory";
            return snap;
        }
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            snap.Status = "not_a_repo"; snap.Error = "no .git directory";
            return snap;
        }

        try
        {
            snap.Branch = (await RunGitAsync(repoPath, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

            var status = await RunGitAsync(repoPath, ct, "status", "--porcelain=v2", "--branch");
            snap.Dirty = status.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));

            var ab = status.Split('\n').FirstOrDefault(l => l.StartsWith("# branch.ab"));
            if (ab is not null)
            {
                foreach (var p in ab.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (p.StartsWith("+") && int.TryParse(p[1..], out var a)) snap.Ahead = a;
                    else if (p.StartsWith("-") && int.TryParse(p[1..], out var b)) snap.Behind = b;
                }
            }

            snap.LastCommit = (await RunGitAsync(repoPath, ct, "log", "-1", "--pretty=format:%h %s")).Trim();
            snap.Status = "ok";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] GitSnapshotAsync FAILED: {ex.Message}");
            snap.Status = "git_failed";
            snap.Error = ex.Message;
        }
        return snap;
    }

    private static async Task<string> RunGitAsync(string repoPath, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoPath,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git Process.Start returned null");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await proc.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} -> exit {proc.ExitCode}: {Truncate(stderr, 300)}");
        return stdout;
    }

    // ====================================================================
    // Phase 7: Crash resilience - recovery prompt builder
    // ====================================================================

    public static async Task<RecoveryPrompt> BuildRecoveryPromptAsync(
        string sessionId, string repoPath, TurnSummary? lastSummary, CancellationToken ct = default)
    {
        var rp = new RecoveryPrompt { SessionId = sessionId };
        var sb = new StringBuilder();
        sb.AppendLine("# Recovery: previous Claude Code session exited unexpectedly");
        sb.AppendLine();
        sb.AppendLine($"- Repo: `{repoPath}`");
        sb.AppendLine($"- Session ID: `{sessionId}`");
        sb.AppendLine();

        if (lastSummary is not null)
        {
            sb.AppendLine("## What the previous session was doing");
            sb.AppendLine();
            sb.AppendLine($"- {lastSummary.Headline}");
            if (lastSummary.Decisions.Count > 0)
            {
                sb.AppendLine("- Decisions / findings:");
                foreach (var d in lastSummary.Decisions) sb.AppendLine($"  - {d}");
            }
            if (lastSummary.FilesTouched.Count > 0)
                sb.AppendLine($"- Files touched: {string.Join(", ", lastSummary.FilesTouched)}");
            if (lastSummary.CommandsRun.Count > 0)
                sb.AppendLine($"- Commands run: {string.Join(" | ", lastSummary.CommandsRun)}");
            sb.AppendLine();
        }
        else
        {
            rp.Status = "no_data";
            sb.AppendLine("(No structured turn summary was available for the dead session.)");
            sb.AppendLine();
        }

        try
        {
            var git = await GitSnapshotAsync(repoPath, ct);
            sb.AppendLine("## Git state when it crashed");
            sb.AppendLine();
            sb.AppendLine($"- Branch: `{git.Branch}`");
            sb.AppendLine($"- Dirty:  {git.Dirty}");
            sb.AppendLine($"- Last commit: `{git.LastCommit}`");
            if (git.Dirty)
            {
                try
                {
                    var diff = await RunGitAsync(repoPath, ct, "diff", "--stat");
                    if (!string.IsNullOrWhiteSpace(diff))
                    {
                        sb.AppendLine();
                        sb.AppendLine("### Uncommitted changes (--stat)");
                        sb.AppendLine();
                        sb.AppendLine("```");
                        sb.AppendLine(Truncate(diff, 4000));
                        sb.AppendLine("```");
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[WingmanService] git diff for recovery prompt failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] git snapshot for recovery prompt failed: {ex.Message}");
            rp.Status = "generated_with_warnings";
            rp.Error = ex.Message;
        }

        sb.AppendLine();
        sb.AppendLine("## Suggested next prompt");
        sb.AppendLine();
        sb.AppendLine("Pick up where I left off.  Above is a snapshot of what the previous session was doing.  Read the git diff if there is one, then continue the work.");

        rp.MarkdownBlob = sb.ToString();
        return rp;
    }

    // ====================================================================
    // Phase 8: Code review enforcement (pure local check)
    // ====================================================================

    public static List<RuleViolation> CheckCodeReviewDiscipline(IReadOnlyList<TurnData> recentTurns)
    {
        var violations = new List<RuleViolation>();
        if (recentTurns is null || recentTurns.Count == 0) return violations;

        bool reviewedSinceLastCommit = false;
        foreach (var t in recentTurns)
        {
            if (TurnUsedReviewSkill(t)) reviewedSinceLastCommit = true;
            if (TurnHasGitCommit(t))
            {
                if (!reviewedSinceLastCommit)
                {
                    violations.Add(new RuleViolation
                    {
                        Rule = "Run review-code skill before any git commit.",
                        What = $"Turn at {t.Timestamp:HH:mm:ss} ran `git commit` without a prior /review-code skill in this session.",
                        Severity = "warn",
                    });
                }
                reviewedSinceLastCommit = false;
            }
        }
        return violations;
    }

    private static bool TurnHasGitCommit(TurnData t)
    {
        if (t.BashCommands is null) return false;
        foreach (var c in t.BashCommands)
        {
            var cmd = (c ?? "").Trim().ToLowerInvariant();
            if (cmd.StartsWith("git commit") || cmd.Contains(" git commit ")) return true;
        }
        return false;
    }

    private static bool TurnUsedReviewSkill(TurnData t)
    {
        if (t.ToolsUsed is not null)
        {
            foreach (var tool in t.ToolsUsed)
            {
                if (tool is null) continue;
                if (tool.Equals("review-code", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return (t.UserPrompt ?? "").Contains("/review-code", StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // Phase 5: Wingman Ask - interactive single-turn query about a session
    // ====================================================================

    /// <summary>
    /// Ask the wingman a question about a specific session. One fresh
    /// <c>claude --print --model haiku</c> call with no session persistence.
    /// The session's recent state (wingman decisions, turn summaries, buffer
    /// tail, metadata) is piped in as context. No conversation memory between
    /// calls - each ask is independent.
    ///
    /// Caller supplies the session state via the parameters rather than passing
    /// the Session object so this stays a pure function (testable, no UI thread).
    /// </summary>
    public static async Task<WingmanAskResult> AskAboutSessionAsync(
        string question,
        WingmanAskContext context,
        string claudeExePath,
        CancellationToken ct = default,
        bool explain = false)
    {
        var sw = Stopwatch.StartNew();

        // Explain mode does not need a user question (it briefs the whole session);
        // the free-text ask path still requires one.
        if (!explain && string.IsNullOrWhiteSpace(question))
            return new WingmanAskResult { Status = "bad_request", Error = "empty question" };

        var model = explain ? StrongModel : DefaultModel;

        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            sw.Stop();
            return new WingmanAskResult
            {
                Status = "no_claude",
                Error = "no claude CLI configured",
                Answer = "Wingman is not configured (no claude CLI path). Set agents.claudePath in config.json.",
                ContextDigest = context.ToDigest(),
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }

        var prompt = explain ? BuildExplainPrompt(context) : BuildAskPrompt(question, context);

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct, model);
            sw.Stop();
            var answer = (stdout ?? "").Trim();
            // Explain mode appends a "QUICK REPLIES:" JSON line; lift it off and clean the
            // displayed briefing so the UI can render tap-to-answer buttons.
            var quickReplies = new List<string>();
            if (explain)
            {
                (answer, quickReplies) = ExtractQuickReplies(answer);
            }
            // Cap response so a misbehaving model can't return a megabyte into the UI.
            if (answer.Length > 4000) answer = answer[..3997] + "...";
            return new WingmanAskResult
            {
                Answer = answer,
                Model = model,
                LatencyMs = sw.ElapsedMilliseconds,
                ContextDigest = context.ToDigest(),
                Status = "ok",
                QuickReplies = quickReplies,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[WingmanService] AskAboutSessionAsync FAILED: {ex.Message}");
            return new WingmanAskResult
            {
                Status = "wingman_failed",
                Error = ex.Message,
                Answer = "Wingman call failed: " + ex.Message,
                ContextDigest = context.ToDigest(),
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }
    }

    /// <summary>
    /// Lift the trailing "QUICK REPLIES: [...]" line off an explain answer: returns the
    /// briefing with that section removed, plus the parsed options (the model produced them
    /// as JSON; we only parse our own structured trailer, not free prose). Capped at 4.
    /// </summary>
    internal static (string cleaned, List<string> replies) ExtractQuickReplies(string answer)
    {
        var replies = new List<string>();
        if (string.IsNullOrEmpty(answer)) return (answer, replies);

        var idx = answer.IndexOf("QUICK REPLIES:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (answer, replies);

        var after = answer[(idx + "QUICK REPLIES:".Length)..];
        var cleaned = answer[..idx].TrimEnd();

        var lb = after.IndexOf('[');
        var rb = lb >= 0 ? after.IndexOf(']', lb + 1) : -1;
        if (lb >= 0 && rb > lb)
        {
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(after.Substring(lb, rb - lb + 1));
                if (arr is not null)
                    foreach (var s in arr)
                        if (!string.IsNullOrWhiteSpace(s)) replies.Add(s.Trim());
            }
            catch { /* model didn't emit valid JSON; just no quick replies */ }
        }
        if (replies.Count > 4) replies = replies.GetRange(0, 4);
        return (cleaned, replies);
    }

    internal static string BuildAskPrompt(string question, WingmanAskContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the wingman for a CC Director session. The user has a question about THIS session.");
        sb.AppendLine();
        sb.AppendLine("Answer ONLY from the context below. If the context does not contain the answer, say:");
        sb.AppendLine("\"I don't have that in context.\"  Do NOT speculate. Do NOT invent file names, decisions, or activity.");
        sb.AppendLine("Respond in 1-3 short sentences, plain text, no code blocks, no markdown headings.");
        sb.AppendLine();
        AppendSessionContext(sb, context);
        sb.AppendLine();
        sb.AppendLine("=== USER QUESTION ===");
        sb.Append(Truncate(question, 1500));
        return sb.ToString();
    }

    /// <summary>
    /// Prompt for the on-demand "Explain" briefing. Produces a short, plain-language
    /// account of what the session has done and what the agent is waiting on, while
    /// preserving the agent's actual question verbatim (clarifying only when the bare
    /// question is ambiguous out of context).
    /// </summary>
    internal static string BuildExplainPrompt(WingmanAskContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the wingman for a CC Director session. The user is away from their computer and wants a quick, clear briefing on THIS session so they can decide what to do next.");
        sb.AppendLine();
        sb.AppendLine("Write exactly TWO labeled sections, plain text, no code blocks, no bullet symbols. The ONLY markdown allowed is a table (see below); otherwise no markdown:");
        sb.AppendLine();
        sb.AppendLine("WHAT'S HAPPENED:");
        sb.AppendLine("Be terse. 1 to 3 short sentences, fewer is better. No padding, no preamble, no restating the obvious, no narrating an empty session. Plain language a non-expert can follow. No file paths or commands unless they are essential to understanding.");
        sb.AppendLine("- If no real work has happened yet (the session is fresh or idle and the agent has not done anything), say so in ONE short sentence and stop. Example: \"Nothing yet; the session is idle and waiting for a task.\" Do NOT speculate about what it might do.");
        sb.AppendLine("- The terminal's input box may show faint placeholder or example text (such as a suggested command or a sample file name) when it is empty. That is NOT something the agent did or the user typed. Never describe placeholder text as activity.");
        sb.AppendLine("- If the agent presented a TABLE the user needs to SEE to make sense of things (a comparison, a set of options, a plan laid out in rows, or data with columns), reproduce that table as a GitHub-style markdown table right here, after the sentences. Keep the agent's row and column content; do not invent rows. Example:");
        sb.AppendLine("    | Item | Today |");
        sb.AppendLine("    | --- | --- |");
        sb.AppendLine("    | Session name | header |");
        sb.AppendLine("- Only include a table when the agent actually presented tabular content the user must see. Do NOT turn ordinary prose, lists, or a single value into a table. At most one table.");
        sb.AppendLine();
        sb.AppendLine("WHAT CLAUDE WANTS:");
        sb.AppendLine("State the question, request, or decision the agent is waiting on, in the AGENT'S OWN WORDS.");
        sb.AppendLine("- Preserve the agent's phrasing. Do NOT reword, soften, summarize, or improve the actual question. The user trusts the agent's words over yours.");
        sb.AppendLine("- Only add a few words of clarification IN PARENTHESES when the bare question is ambiguous without context. Example: \"Want me to implement it?\" -> \"Want me to implement it (the Tailscale auto-provisioning)?\"");
        sb.AppendLine("- If the agent asked multiple questions, include them all.");
        sb.AppendLine("- If the session is idle and nothing is pending (it finished, or it never started), write exactly: \"Nothing pending. Waiting for you to give it a task.\" and nothing else.");
        sb.AppendLine("- If the agent is mid-flow and actively working but not waiting on anything, write: \"Claude is still working; nothing is needed from you right now.\"");
        sb.AppendLine("- Do NOT combine these two; an idle session is not \"still working\".");
        sb.AppendLine();
        sb.AppendLine("QUICK REPLIES:");
        sb.AppendLine("If \"WHAT CLAUDE WANTS\" is a decision the user can answer in a few words (yes/no, this-or-that, pick from a short menu), output the tappable answer options as a JSON array on ONE line, e.g.: [\"Yes, go ahead\", \"No, stop\"]");
        sb.AppendLine("- 2 to 4 options. Each is the literal text the user would send back to the agent, phrased as the user's own reply (not a description).");
        sb.AppendLine("- Cover the real choices the agent offered; do not invent options the agent did not imply.");
        sb.AppendLine("- If there is no clear short answer (the agent is just working, or the reply needs real typing), output an empty array: []");
        sb.AppendLine();
        sb.AppendLine("Answer ONLY from the context below. Do NOT invent file names, decisions, or questions. If the context does not show what the agent is asking, say so plainly.");
        sb.AppendLine();
        AppendSessionContext(sb, context);
        return sb.ToString();
    }

    /// <summary>Appends the shared session-state sections (metadata, wingman decisions,
    /// recent turn summaries, terminal buffer tail) used by both the ask and explain prompts.</summary>
    private static void AppendSessionContext(StringBuilder sb, WingmanAskContext context)
    {
        sb.AppendLine("=== SESSION METADATA ===");
        sb.AppendLine($"- Repo: {context.RepoPath}");
        sb.AppendLine($"- Agent: {context.AgentKind}");
        sb.AppendLine($"- Activity state: {context.ActivityState}");
        sb.AppendLine($"- Wingman color: {context.CurrentColor} ({context.CurrentReason})");
        sb.AppendLine($"- Git dirty: {context.GitDirty}");

        if (context.RecentWingmanEvents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== WINGMAN DECISIONS (newest first) ===");
            foreach (var e in context.RecentWingmanEvents.Take(20))
                sb.AppendLine($"- {e.At:HH:mm:ss}  {e.OldColor} -> {e.NewColor}  \"{e.Reason}\"");
        }

        if (context.RecentTurnSummaries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== RECENT TURN SUMMARIES (oldest first) ===");
            foreach (var t in context.RecentTurnSummaries.TakeLast(5))
            {
                sb.AppendLine($"- {t.TurnStartedAt:HH:mm:ss}  headline: {t.Headline}");
                if (!string.IsNullOrEmpty(t.NeedsUser) && t.NeedsUser != "no")
                    sb.AppendLine($"    needs_user: {t.NeedsUser} - {t.NeedsUserShort}");
                if (t.Decisions != null && t.Decisions.Count > 0)
                    sb.AppendLine($"    decisions: {string.Join(" | ", t.Decisions)}");
                if (t.FilesTouched != null && t.FilesTouched.Count > 0)
                    sb.AppendLine($"    files: {string.Join(", ", t.FilesTouched)}");
            }
        }

        if (!string.IsNullOrEmpty(context.BufferTailText))
        {
            sb.AppendLine();
            sb.AppendLine("=== TERMINAL BUFFER (tail, ANSI stripped) ===");
            sb.AppendLine("This is the raw terminal display, not a log of what the user did. Read it with care:");
            sb.AppendLine("When the agent is waiting on the user, Claude Code draws a bordered box (horizontal lines above and below) holding one or more choices it is offering, usually with a cursor or arrow marker (such as '>' or a highlighted line) sitting in front of one of them. Text that appears inside that box is the AGENT'S OWN suggestion to the user, not something the user has done. The marker only shows which choice is highlighted by default; it does NOT mean the user selected, typed, or approved it. So a line like \"Yes, go ahead\" sitting inside the box between the lines with the cursor in front of it means the agent is asking that question, not that the user answered yes.");
            sb.AppendLine("Use your judgment over the whole context to tell the agent's suggested choices apart from text the user actually entered, and treat the session as still waiting until there is real evidence the user responded. This is guidance for reading the screen, not a rigid rule to apply blindly.");
            sb.AppendLine();
            sb.AppendLine(Truncate(context.BufferTailText, 4000));
        }
    }

    // ====================================================================
    // Goal management: is the session still working toward its stated goal?
    // ====================================================================

    /// <summary>
    /// Judge whether a session is still on track toward its stated goal, has
    /// drifted, or has completed it. One fresh Haiku call over the goal plus the
    /// session's recent turn summaries.
    ///
    /// Observational only - the caller decides what to do with the verdict. On any
    /// failure (no claude CLI, parse error, timeout) returns
    /// <see cref="GoalStates.Unknown"/> with a reason; we never fabricate a verdict.
    /// </summary>
    public static async Task<GoalAssessment> AssessGoalAsync(
        string goal,
        IReadOnlyList<TurnSummary> recentSummaries,
        string repoPath,
        string claudeExePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return new GoalAssessment { State = GoalStates.Unknown, Reason = "no goal set" };
        if (string.IsNullOrWhiteSpace(claudeExePath))
            return new GoalAssessment { State = GoalStates.Unknown, Reason = "no claude CLI configured" };

        var prompt = BuildGoalAssessmentPrompt(goal, recentSummaries ?? Array.Empty<TurnSummary>(), repoPath ?? "");

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            return ParseGoalAssessmentJson(stdout);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanService] AssessGoalAsync FAILED: {ex.Message}");
            return new GoalAssessment { State = GoalStates.Unknown, Reason = "wingman call failed: " + ex.Message };
        }
    }

    internal static string BuildGoalAssessmentPrompt(string goal, IReadOnlyList<TurnSummary> recentSummaries, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the goal-tracking wingman for a Claude Code session. The user set a GOAL for this session. Below the goal is a list of recent turn summaries describing what the agent has actually done, oldest first.");
        sb.AppendLine();
        sb.AppendLine("Your job: judge whether the session is still working toward the goal.");
        sb.AppendLine();
        sb.AppendLine("Pick exactly one state:");
        sb.AppendLine("- \"on_track\": the recent work is plausibly in service of the goal, or the session just started and nothing contradicts it.");
        sb.AppendLine("- \"drifting\": the recent work has clearly moved onto something unrelated to the goal, OR the agent is stuck/looping without progressing the goal.");
        sb.AppendLine("- \"complete\": the goal appears to have been accomplished based on the summaries.");
        sb.AppendLine();
        sb.AppendLine("Be conservative. Prefer \"on_track\" unless there is clear evidence of drift or completion. A single tangential step is not drift; a sustained move away from the goal is.");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{\"state\": \"on_track|drifting|complete\", \"reason\": \"<one short plain-language sentence; name the goal and what the work shows>\"}");
        sb.AppendLine();
        sb.AppendLine($"=== GOAL ===");
        sb.AppendLine(Truncate(goal, 1000));
        sb.AppendLine();
        sb.AppendLine($"=== RECENT TURN SUMMARIES (oldest first), repo {repoPath} ===");
        if (recentSummaries.Count == 0)
        {
            sb.AppendLine("(no turns completed yet)");
        }
        else
        {
            foreach (var t in recentSummaries.TakeLast(8))
            {
                sb.AppendLine($"- {t.Headline}");
                if (!string.IsNullOrEmpty(t.NeedsUser) && t.NeedsUser != "no")
                    sb.AppendLine($"    needs_user: {t.NeedsUser}");
                if (t.Decisions is { Count: > 0 })
                    sb.AppendLine($"    decisions: {string.Join(" | ", t.Decisions)}");
            }
        }
        return sb.ToString();
    }

    internal static GoalAssessment ParseGoalAssessmentJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new GoalAssessment { State = GoalStates.Unknown, Reason = "wingman returned empty output" };

        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence > 0) s = s[..endFence].Trim();
        }
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "").Trim().ToLowerInvariant() : "";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "").Trim() : "";
            if (!GoalStates.IsValid(state) || state == GoalStates.Unknown)
                return new GoalAssessment { State = GoalStates.Unknown, Reason = "wingman returned an invalid goal state" };
            return new GoalAssessment { State = state, Reason = reason };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] goal-assessment JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            return new GoalAssessment { State = GoalStates.Unknown, Reason = "wingman JSON parse failed" };
        }
    }

    // ====================================================================
    // Internals: side-claude invocation (mirrors RecapGenerator)
    // ====================================================================

    /// <summary>
    /// Spawn  claude --print --bare --model haiku --tools ""  with the given
    /// prompt as a positional arg.  Returns stdout text.  Throws on non-zero
    /// exit or timeout.
    /// </summary>
    private static async Task<string> RunSideClaudeAsync(string prompt, string claudeExePath, CancellationToken ct, string model = DefaultModel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = claudeExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(model) ? DefaultModel : model);
        // NOTE: --bare is intentionally NOT passed.  --bare disables keychain reads,
        // which prevents the side-call from picking up the user's OAuth credentials
        // from ~/.claude/.credentials.json and fails with "Not logged in".
        // --tools "" below already prevents tool use (the main safety reason for
        // --bare).  The cost of dropping --bare is some extra auto-context (CLAUDE.md
        // auto-discovery, auto-memory) - acceptable for a Haiku call.
        psi.ArgumentList.Add("--no-session-persistence");
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        psi.ArgumentList.Add(prompt);

        psi.WorkingDirectory = Path.GetTempPath();

        // Strip nested-Claude-Code env vars so the side call isn't detected as a child session.
        foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CC_SESSION_ID", "GIT_EDITOR" })
            psi.Environment.Remove(k);

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for claude --print");
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProcessTimeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"claude --print did not finish within {ProcessTimeout.TotalSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        if (proc.ExitCode != 0)
        {
            FileLog.Write($"[WingmanService] claude --print exit={proc.ExitCode} in {sw.ElapsedMilliseconds}ms, stderr={Truncate(stderr, 400)}");
            throw new InvalidOperationException($"claude --print exited {proc.ExitCode}: {stderr.Trim()}");
        }

        FileLog.Write($"[WingmanService] side-call done in {sw.ElapsedMilliseconds}ms, output chars={stdout.Length}");
        return stdout.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Output of <see cref="WingmanService.CleanVoiceTranscriptAsync"/>.
/// Always populated even on failure (Cleaned falls back to raw).
/// </summary>
public sealed record VoiceCleanupResult(string Cleaned, string Reason);
