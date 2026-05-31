using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Every method on this class is a one-shot side-call to `claude --print` on the
/// Wingman's strong <see cref="Model"/> carrying a focused prompt - exactly the pattern
/// <see cref="RecapGenerator"/> uses for the existing recap feature. The
/// "Session Wingman" from the PRD is the conceptual sum of these short
/// fresh-context calls; we do NOT spawn a long-running shadow process per
/// session.
///
/// Methods correspond 1:1 to the PRD's Session Wingman responsibilities.
/// </summary>
public static class WingmanService
{
    /// <summary>
    /// The single model the Wingman runs on - ALWAYS a strong model, NEVER a cheap one.
    /// The Wingman's whole job is to genuinely help the user across every session; a
    /// weak model cannot read a screen faithfully, answer without summarizing, or judge
    /// state reliably, so the Wingman does not get a cheap tier. This is a hard invariant
    /// of the Wingman invariants (docs/wingman/WINGMAN.md) and is enforced by the charter
    /// audit (WingmanCharterAuditTests), which fails the build if a cheap-model call ever
    /// reappears in Wingman code.
    /// </summary>
    public const string Model = "opus";

    /// <summary>Back-compat aliases. Both now resolve to the single strong <see cref="Model"/>;
    /// new code should reference <see cref="Model"/>. Kept so existing call sites compile.</summary>
    public const string DefaultModel = Model;
    public const string StrongModel = Model;

    /// <summary>Hard timeout per Wingman call.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The single shared "explain this session" question. This is the one prompt every
    /// entry point that asks the Wingman to plainly read a session and say what it wants
    /// should use - the FIFO conveyor briefing, the desktop Terminal "Explain" button, and
    /// any future caller - so improving how the Wingman explains a session improves all of
    /// them at once. Feed it to <see cref="AnswerViaSessionAsync"/> with the session's
    /// full cleaned terminal. Read-only and verbatim: it states what happened and what the
    /// agent is waiting on, inventing nothing.
    /// </summary>
    public const string BriefingQuestion =
        "Give me a short briefing on this session, reading ONLY from the terminal. " +
        "First, WHAT'S HAPPENING: 1 to 3 short sentences on what the agent just did. " +
        "Then, WHAT IT WANTS: the exact question or decision it is waiting on, in its own " +
        "words, or 'Nothing pending' if it is just working. No file paths unless essential. " +
        "Do not invent anything.";

    /// <summary>
    /// Shared briefing on how a Claude Code session actually LOOKS on screen, so any
    /// Wingman prompt reads the terminal the way someone who knows Claude Code would -
    /// not the way a stranger guessing from keywords would. Trained once, reused by
    /// every prompt builder.
    ///
    /// This exists because the field bug it fixes is precisely a keyword trap: the
    /// persistent mode footer "bypass permissions on (shift+tab to cycle)" contains the
    /// word "permission", and an uninformed reader flags it as a permission prompt - the
    /// exact OPPOSITE of the truth (that mode means the agent never stops to ask).
    /// </summary>
    public const string ClaudeCodeScreenReference = """
        HOW A CLAUDE CODE SESSION LOOKS (read this before judging):

        1. PERSISTENT MODE FOOTER (NOT a prompt - it is ALWAYS on screen).
           Near the bottom Claude Code shows its current permission MODE, one of:
             - "bypass permissions on"   (often with "(shift+tab to cycle)")
             - "accept edits on"         (often with "(shift+tab to cycle)")
             - "plan mode on"            (often with "(shift+tab to cycle)")
           This line is a STATUS INDICATOR, not a question. It is present whether the
           agent is working, waiting, or idle. It NEVER by itself means the session is
           waiting for permission. In particular "bypass permissions on" means the
           agent will auto-approve everything and will NOT stop to ask - so seeing it
           is evidence AGAINST waiting_for_permission, never for it. The word
           "permission" in this footer is part of the mode name, not a request to you.

        2. A REAL PERMISSION PROMPT looks completely different: a bordered box with a
           question like "Do you want to proceed?" or "Do you want to make this edit to
           <file>?", followed by a NUMBERED choice list, e.g.
             "1. Yes   2. Yes, and don't ask again   3. No, and tell Claude what to do
              differently"
           with a selector arrow on one option. The session is parked on that box and
           cannot continue until a number is chosen. THAT is waiting_for_permission.

        3. THE INPUT BOX: a rounded/bordered box containing "> " (often a blinking
           cursor) with a hint like "? for shortcuts" beneath it, and NO spinner and NO
           pending question. That is the agent finished and waiting for the next
           instruction => waiting_for_input.

        4. ACTIVE / WORKING indicators: a spinner or animated glyph, an elapsed-time
           counter (e.g. "Brewed for 12s"), an "esc to interrupt" footer, or text still
           streaming in. Any of these at the bottom => working.

        5. DECIDING THE STATE (apply in this order; this overrides the descriptions above):
           a. WORKING check FIRST, mechanically: find the LAST non-empty line on screen -
              the bottom-most footer line (it carries the mode indicator). Read it
              carefully. If that line literally contains "esc to interrupt", the state is
              WORKING - stop here. An empty input box, a "Read ... file" result line, or a
              finished-looking layout ABOVE it does NOT change this: while working the agent
              shows an empty box AND "esc to interrupt" at the same time. A stale "esc to
              interrupt" higher up in the scrollback (left over from a finished step) does
              NOT count - only the bottom-most footer line decides working.
           b. If that bottom footer has NO "esc to interrupt", the turn is OVER:
              - waiting_for_permission ONLY if parked on a real blocking gate: a bordered
                NUMBERED-choice box ("1. Yes  2. No ..."), a "[y/n]" prompt, or an
                interactive menu showing "Enter to select ... Esc to cancel". The mode
                footer alone is NOT a gate.
              - otherwise waiting_for_input - INCLUDING when the agent's last message is a
                prose question or offer ("OK to proceed?", "Want me to ...?", "Which do you
                prefer?") with no numbered box. A prose question at an empty input box is
                waiting_for_input, not permission.
           c. cancelled: an "Interrupted"/"Cancelled" notice with the agent back at the prompt.
           d. unknown: the screen is blank, garbled, or only a startup banner with no input
              box, no spinner, and no footer. When you cannot positively identify the state,
              say unknown - NEVER fabricate "working" from garbage with no active indicator.
        """;

    // ====================================================================
    // Phase 1: Voice transcript cleanup
    // ====================================================================

    /// <summary>
    /// Cleanup model. A small instruction-following model is the right tool here:
    /// the cleanup prompt asks for verbatim text + a JSON wrapper + an agent/wingman
    /// routing decision, none of which need a reasoning-tier model. Using
    /// <c>claude --print</c> on the Wingman's strong model meant a cold subprocess
    /// spawn per turn and 60s timeouts on a third of turns (issue #142); a direct
    /// HTTP call to OpenAI's nano-tier finishes in ~1s and never spawns a process.
    /// Dictation's <c>CleanupOrchestrator</c> already runs the same model in prod.
    /// </summary>
    private const string VoiceCleanupModel = "gpt-4.1-nano";
    private const string OpenAiChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    private static readonly TimeSpan VoiceCleanupHttpTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient _voiceCleanupHttp = new() { Timeout = VoiceCleanupHttpTimeout };

    /// <summary>
    /// Clean a raw Whisper transcript and decide its routing target. Output is a JSON
    /// object <c>{ cleaned, reason, target }</c>; we parse it.
    ///
    /// Backed by a direct OpenAI chat call (<see cref="VoiceCleanupModel"/>) rather than
    /// a side <c>claude --print</c> spawn - issue #142, where cold-spawn cost timed the
    /// cleanup out on a third of turns including very short ones.
    ///
    /// On any failure (no key, parse error, HTTP error, timeout) returns a result whose
    /// <see cref="VoiceCleanupResult.Cleaned"/> is the raw transcript verbatim (fail open)
    /// and <see cref="VoiceCleanupResult.Reason"/> describes the failure.
    /// </summary>
    public static async Task<VoiceCleanupResult> CleanVoiceTranscriptAsync(
        string rawTranscript,
        string repoPath,
        string openAiApiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new VoiceCleanupResult(rawTranscript ?? "", "empty raw transcript");
        if (string.IsNullOrWhiteSpace(openAiApiKey))
            return new VoiceCleanupResult(rawTranscript, "no OpenAI key configured");

        var prompt = BuildVoiceCleanupPrompt(rawTranscript, repoPath ?? "");

        var sw = Stopwatch.StartNew();
        try
        {
            var stdout = await CallOpenAiChatAsync(prompt, VoiceCleanupModel, openAiApiKey, ct);
            sw.Stop();
            FileLog.Write($"[WingmanService] CleanVoiceTranscriptAsync OK in {sw.ElapsedMilliseconds}ms");
            return ParseVoiceCleanupJson(stdout, rawTranscript);
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[WingmanService] CleanVoiceTranscriptAsync FAILED in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new VoiceCleanupResult(rawTranscript, "voice cleanup failed: " + ex.Message);
        }
    }

    private static async Task<string> CallOpenAiChatAsync(
        string userPrompt, string model, string apiKey, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "user", content = userPrompt },
            },
            temperature = 0.0,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, OpenAiChatCompletionsEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _voiceCleanupHttp.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI chat completions HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return "";
        var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return content.Trim();
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
        sb.AppendLine("Your job: return the user's message VERBATIM. Do not classify it, do not route it - the UI button the user pressed already decided who hears it.");
        sb.AppendLine();
        sb.AppendLine("Cleanup rules - you are a strict transcriber, NOT an editor:");
        sb.AppendLine("- Return the user's words VERBATIM: same words, same order, same meaning.");
        sb.AppendLine("- Do NOT remove or alter filler words (um, uh, like, you know, so, right). Keep every one.");
        sb.AppendLine("- Do NOT reword, rephrase, shorten, summarize, expand, paraphrase, or 'improve' anything.");
        sb.AppendLine("- Do NOT add or delete words, and do NOT reorder words. This includes wake phrases like \"hey wingman\" - leave them in.");
        sb.AppendLine("- Do NOT fix grammar, spelling, or mis-transcribed terms. A separate dictionary step handles known term corrections.");
        sb.AppendLine("- Do NOT add greetings, sign-offs, or commentary. Do NOT answer the question yourself; just return the prompt text.");
        sb.AppendLine("- One paragraph. No bullet lists, no headings, no quotation marks around the result.");
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
        sb.AppendLine(ClaudeCodeScreenReference);
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
        sb.AppendLine("- 'permission': the agent is PARKED on a real bordered numbered-choice confirmation box (point 2 above), or a \"[y/n]\" prompt, and cannot continue until the user picks. The persistent mode footer (point 1: \"bypass permissions on\", \"accept edits on\", \"plan mode on\", \"shift+tab to cycle\") does NOT count and is evidence AGAINST permission.");
        sb.AppendLine("- 'idle': agent finished cleanly and has nothing pending (the input box from point 3 is empty, no spinner, no pending question).");
        sb.AppendLine("- 'no': agent is mid-flow or returned a non-question informational reply with no trailing question.");
        sb.AppendLine("- Do NOT report 'permission' or 'question' just because you see the word \"permission\" or a mention of committing/approving. The mode footer is a status line, not a request. An agent OFFERING to do something later (\"I can commit when you give the word\", \"say the word and I'll push\", \"let me know if you want X\") is NOT waiting on you: that is 'no'. Only a real on-screen gate (numbered box, [y/n], or a direct question the agent is parked on) counts.");
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
    // Phase 6: Git awareness (no LLM - run git locally)
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
    /// <c>claude --print</c> call (strong <see cref="Model"/>) with no session persistence.
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

        // One strong model for the whole Wingman (charter invariant); explain and the
        // free-text ask both run on it.
        var model = Model;

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
            var raw = (stdout ?? "").Trim();

            // Explain mode returns a single JSON object; parse it into structured fields and
            // synthesise the legacy `Answer` text so older clients (the desktop session view
            // before this change, plus anything that just shows the briefing as text) keep
            // working unchanged. Free-text ask mode keeps the plain-string contract.
            if (explain)
            {
                var parsed = ParseExplainJson(raw);
                if (parsed.Answer.Length > 4000) parsed.Answer = parsed.Answer[..3997] + "...";
                return new WingmanAskResult
                {
                    Answer = parsed.Answer,
                    Headline = parsed.Headline,
                    WhatHappened = parsed.WhatHappened,
                    LongDescription = parsed.LongDescription,
                    WhatClaudeWants = parsed.WhatClaudeWants,
                    Say = parsed.Say,
                    QuickReplies = parsed.Actions,
                    RunningInBackground = parsed.RunningInBackground,
                    Model = model,
                    LatencyMs = sw.ElapsedMilliseconds,
                    ContextDigest = context.ToDigest(),
                    Status = "ok",
                };
            }

            // Cap response so a misbehaving model can't return a megabyte into the UI.
            var answer = raw;
            if (answer.Length > 4000) answer = answer[..3997] + "...";
            return new WingmanAskResult
            {
                Answer = answer,
                Model = model,
                LatencyMs = sw.ElapsedMilliseconds,
                ContextDigest = context.ToDigest(),
                Status = "ok",
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
    /// Structured form of a parsed explain briefing. <see cref="Answer"/> is the synthesised
    /// human-readable text built from the show fields, so older callers that only know the
    /// flat-text shape keep working. <see cref="Headline"/>, <see cref="WhatHappened"/>,
    /// <see cref="WhatClaudeWants"/>, <see cref="Say"/>, and <see cref="Actions"/> carry the
    /// structured fields straight from the model.
    /// </summary>
    internal sealed class ExplainBriefing
    {
        public string Answer { get; set; } = "";
        public string Headline { get; set; } = "";
        public string WhatHappened { get; set; } = "";
        public string LongDescription { get; set; } = "";
        public string WhatClaudeWants { get; set; } = "";
        public string Say { get; set; } = "";
        public List<string> Actions { get; set; } = new();

        /// <summary>The model's running_in_background verdict: true when the session is parked
        /// on its own background task and is NOT waiting on the user. Drives the Purple badge.</summary>
        public bool RunningInBackground { get; set; }
    }

    /// <summary>
    /// Parse the model's explain output. The new prompt asks for a single JSON object with
    /// headline / what_happened / what_claude_wants / say / actions. We fall open if the
    /// model returns plain text instead - the legacy text is treated as the briefing body so
    /// the briefing is still useful even when the JSON wrapper goes missing.
    /// </summary>
    internal static ExplainBriefing ParseExplainJson(string raw)
    {
        var result = new ExplainBriefing();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            // Model didn't return JSON. Treat the entire response as the briefing body and
            // try the legacy QUICK REPLIES trailer extractor so older prompt outputs still
            // get tappable actions.
            var (legacyText, legacyReplies) = ExtractQuickReplies(raw);
            result.Answer = legacyText;
            result.WhatHappened = legacyText;
            result.Say = legacyText;
            result.Actions = legacyReplies;
            return result;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            result.Headline = ReadString(root, "headline");
            result.WhatHappened = ReadString(root, "what_happened");
            result.LongDescription = ReadString(root, "long_description");
            result.WhatClaudeWants = ReadString(root, "what_claude_wants");
            result.Say = ReadString(root, "say");
            result.Actions = ReadStringArray(root, "actions", maxCount: 4);
            result.RunningInBackground = ReadBool(root, "running_in_background");
        }
        catch (System.Text.Json.JsonException ex)
        {
            FileLog.Write($"[WingmanService] ParseExplainJson FAILED: {ex.Message}");
            result.Answer = raw;
            result.WhatHappened = raw;
            result.Say = raw;
            return result;
        }

        result.Answer = ComposeAnswerFromFields(result);
        return result;
    }

    private static string ReadString(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind != System.Text.Json.JsonValueKind.String) return "";
        return (el.GetString() ?? "").Trim();
    }

    /// <summary>Read a boolean field tolerantly: a real JSON true/false, or a "true"/"false"
    /// string (some models quote booleans despite the schema). Anything else is false.</summary>
    private static bool ReadBool(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String =>
                string.Equals((el.GetString() ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static List<string> ReadStringArray(System.Text.Json.JsonElement root, string name, int maxCount)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var el)) return list;
        if (el.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var s = (item.GetString() ?? "").Trim();
            if (s.Length == 0) continue;
            list.Add(s);
            if (list.Count >= maxCount) break;
        }
        return list;
    }

    /// <summary>
    /// Build the legacy two-section briefing text from the structured fields, so callers that
    /// only know <c>Answer</c> keep rendering the same shape that pre-#150 prompts produced.
    /// </summary>
    private static string ComposeAnswerFromFields(ExplainBriefing b)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(b.WhatHappened))
        {
            sb.AppendLine("WHAT'S HAPPENED:");
            sb.AppendLine(b.WhatHappened.Trim());
        }
        if (!string.IsNullOrWhiteSpace(b.LongDescription))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(b.LongDescription.Trim());
        }
        if (!string.IsNullOrWhiteSpace(b.WhatClaudeWants))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("WHAT CLAUDE WANTS YOU TO DO NEXT:");
            sb.AppendLine(b.WhatClaudeWants.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Pull the first balanced JSON object out of a model response. The model is told to
    /// return ONLY JSON but real models occasionally wrap it in ```json fences or prepend a
    /// stray "Here is the briefing:" line; this finds the {...} block tolerantly.
    /// </summary>
    private static string? ExtractJsonObject(string raw)
    {
        var open = raw.IndexOf('{');
        if (open < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = open; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (escape) { escape = false; continue; }
            if (ch == '\\') { escape = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return raw[open..(i + 1)];
            }
        }
        return null;
    }

    /// <summary>
    /// Lift the trailing "QUICK REPLIES: [...]" line off an explain answer: returns the
    /// briefing with that section removed, plus the parsed options (the model produced them
    /// as JSON; we only parse our own structured trailer, not free prose). Capped at 4.
    ///
    /// Kept as a fallback for older non-JSON model outputs and for the existing unit tests
    /// that exercise the legacy two-section format. New explain calls go through
    /// <see cref="ParseExplainJson"/> instead.
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

    /// <summary>
    /// Faithful, full-access answer to a free-text question about a session - the
    /// "Ask the Wingman" voice channel. Unlike <see cref="AskAboutSessionAsync"/> (a
    /// one-shot over a truncated, pre-built context), this runs a read-only FULL-POWER
    /// session (Read/Grep/Glob, MCP off, no writes, no PTY access) on the strong model,
    /// handed the WHOLE terminal as a snapshot file plus the session's repo as the
    /// working directory. It reads as much as it needs to answer, and when asked to read
    /// content (an article, a file, the agent's reply) it reproduces that content
    /// VERBATIM rather than summarizing. No length cap on the answer; the caller's TTS
    /// can be interrupted.
    ///
    /// Read-only by construction: the allowed tools cannot write, and no Send path to the
    /// partner PTY exists outside the Director process, so it can neither inject into nor
    /// resize the terminal it is reading. Stateless (fresh --no-session-persistence per
    /// call). Fails closed to a wingman_failed result.
    /// </summary>
    public static async Task<WingmanAskResult> AnswerViaSessionAsync(
        string question, string fullTerminalText, string agentName, string repoPath,
        string claudeExePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(question))
            return new WingmanAskResult { Status = "bad_request", Error = "empty question" };
        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            sw.Stop();
            return new WingmanAskResult
            {
                Status = "no_claude",
                Error = "no claude CLI configured",
                Answer = "Wingman is not configured (no claude CLI path). Set agents.claudePath in config.json.",
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }

        // Materialise the whole terminal as a snapshot the read-only session can Read on
        // its own, so the answer is not limited to a fixed paste.
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"cc-wingman-answer-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(snapshotPath, fullTerminalText ?? "", ct);
            var prompt = BuildWingmanAnswerSessionPrompt(question, snapshotPath, agentName ?? "an AI coding agent");
            var workDir = Directory.Exists(repoPath) ? repoPath : Path.GetTempPath();
            var stdout = await RunWingmanSessionAsync(
                prompt, claudeExePath, workDir, allowedTools: "Read Grep Glob", maxTurns: 12, ct: ct, model: StrongModel);
            sw.Stop();
            // No length cap: reading a whole article is the point. The caller interrupts TTS.
            return new WingmanAskResult
            {
                Answer = (stdout ?? "").Trim(),
                Model = StrongModel,
                LatencyMs = sw.ElapsedMilliseconds,
                ContextDigest = $"terminal:{(fullTerminalText?.Length ?? 0)}ch, repo:{System.IO.Path.GetFileName((repoPath ?? "").TrimEnd('\\', '/'))}, read-only session",
                Status = "ok",
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[WingmanService] AnswerViaSessionAsync FAILED: {ex.Message}");
            return new WingmanAskResult
            {
                Status = "wingman_failed",
                Error = ex.Message,
                Answer = "Wingman call failed: " + ex.Message,
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }
        finally
        {
            try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    internal static string BuildWingmanAnswerSessionPrompt(string question, string snapshotPath, string agentName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the read-only Wingman for a session running {agentName} (an AI coding agent in an interactive TUI). The user is talking to you hands-free, by voice, and wants you to answer their question about THIS session. Your answer will be read aloud to them.");
        sb.AppendLine();
        sb.AppendLine($"The session's full terminal has been captured (ANSI stripped) to this file: {snapshotPath}");
        sb.AppendLine("Use the Read tool to read it - the END of the file is the most recent output. Read as much as you need. Your working directory is the session's repo, so you may also Read/Grep/Glob it to find and open files the user refers to (for example an article or document the agent just wrote). When you Read a file, ignore the line-number prefixes the tool adds and use only the actual text content.");
        sb.AppendLine("You are READ-ONLY: never write, edit, run, or send anything. Only read and answer.");
        sb.AppendLine();
        sb.AppendLine("How to answer:");
        sb.AppendLine("- Answer the user's question directly and COMPLETELY from what you read. Do not drop detail the user asked for.");
        sb.AppendLine("- CRITICAL: if the user asks you to READ something to them - an article, a file, a document, the agent's last reply, \"the whole thing\" - reproduce that text VERBATIM, word for word. Do NOT summarize, shorten, paraphrase, condense, or add commentary. Read exactly what is written. This is the entire point: the user explicitly does not want a summary.");
        sb.AppendLine("- If the content is long, that is fine: output all of it. There is no length limit. The user can stop you.");
        sb.AppendLine("- For an ordinary question (\"what did it decide?\", \"is it done?\", \"what files changed?\"), answer in plain spoken language, as complete as the question needs.");
        sb.AppendLine("- Plain text only, suitable to be read aloud. No markdown fences, no headings, no bullet symbols. Do not narrate your tool use (no \"I read the file...\"); just give the answer.");
        sb.AppendLine("- If you genuinely cannot find what the user is asking about in the terminal or the repo, say so plainly in one sentence. Do not invent content.");
        sb.AppendLine();
        sb.AppendLine("=== USER QUESTION ===");
        sb.Append(Truncate(question, 2000));
        return sb.ToString();
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
    ///
    /// Output shape is a single JSON object so the same briefing can be rendered both
    /// as on-screen text (headline + sections + tap-to-answer actions) AND as a
    /// spoken-version field the phone TTSs on demand when the user enters voice mode.
    /// State is NOT decided here - <see cref="WhatClaudeWantsDirective"/> still binds
    /// the briefing to the badge color owned by SessionStatusWingman.
    /// </summary>
    internal static string BuildExplainPrompt(WingmanAskContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the wingman for a CC Director session. The user is away from their computer and wants a clear briefing on THIS session so they can step back in and decide what to do next.");
        sb.AppendLine();
        sb.AppendLine("The briefing is for someone returning to the session - it MUST make two things crystal clear, with no scanning required:");
        sb.AppendLine("  (a) what happened while they were away, and");
        sb.AppendLine("  (b) what Claude is asking them to do next (or that nothing is pending).");
        sb.AppendLine();
        sb.AppendLine("Return a single JSON object with EXACTLY these fields and no others. Output ONLY the JSON object, no markdown fence, no commentary before or after:");
        sb.AppendLine();
        sb.AppendLine("{");
        sb.AppendLine("  \"headline\": \"<one short line, ~6-10 words, what's the situation right now>\",");
        sb.AppendLine("  \"what_happened\": \"<the QUICK on-screen line - 1 short sentence, scan-friendly; rules below>\",");
        sb.AppendLine("  \"long_description\": \"<the LONGER on-screen detail - 1-2 short paragraphs; rules below>\",");
        sb.AppendLine("  \"what_claude_wants\": \"<the question Claude is waiting on, verbatim when possible; rules below>\",");
        sb.AppendLine("  \"say\": \"<the same content rewritten for the ear; smooth prose; rules below>\",");
        sb.AppendLine("  \"actions\": [\"<tap-to-answer option>\", \"...\"],");
        sb.AppendLine("  \"running_in_background\": <true or false - is the agent parked on its OWN background task and NOT waiting on you? rules below>");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules for headline:");
        sb.AppendLine("- One line. ~6-10 words. Sets the situation in a glance, like a notification subject.");
        sb.AppendLine("- The headline and the \"what_claude_wants\" section are shown TOGETHER. They MUST carry DIFFERENT information - never the same fact twice. The headline states WHAT THE TURN WAS ABOUT (the work Claude just did, or the topic); what_claude_wants states the actual ASK. Do NOT put the question in the headline.");
        sb.AppendLine("- When Claude is waiting on the user, DO NOT write a headline like \"Waiting on which X you prefer\" - that just restates the ask. Instead summarize what was accomplished or worked on, and let the ask live only in what_claude_wants. The colored status already tells the user a decision is pending, so the headline does not need to say \"waiting\".");
        sb.AppendLine("- Good pairing: headline \"Finished the login refactor; one decision left.\" + what_claude_wants \"Use JWT or session cookies?\". Bad pairing (duplicates): headline \"Waiting on which auth method you prefer.\" + what_claude_wants \"Use JWT or session cookies?\".");
        sb.AppendLine("- Examples of headlines: \"Finished the Wingman tab rewrite.\" / \"Refactored the parser; tests pass.\" / \"Hit a build error in the gateway.\" / \"Session is idle; nothing done yet.\"");
        sb.AppendLine();
        sb.AppendLine("Rules for what_happened (the QUICK on-screen line - what the user sees first):");
        sb.AppendLine("- ONE short sentence. Plain language a non-expert can follow. No padding, no preamble.");
        sb.AppendLine("- The user reads this in under a second when they return to the session, so make it carry weight: lead with the most important thing the agent actually did.");
        sb.AppendLine("- If no real work has happened yet (the session is fresh or idle and the agent has not done anything), say so in ONE short sentence. Example: \"Nothing yet; the session is idle and waiting for a task.\" Do NOT speculate about what it might do.");
        sb.AppendLine("- The terminal's input box may show faint placeholder or example text (such as a suggested command or a sample file name) when it is empty. That is NOT something the agent did or the user typed. Never describe placeholder text as activity.");
        sb.AppendLine("- No markdown of any kind in this field. Use the long_description for tables and detail.");
        sb.AppendLine();
        sb.AppendLine("Rules for long_description (the LONGER on-screen detail - what the user reads when they want more):");
        sb.AppendLine("- 1 to 2 short paragraphs (no more than ~6 sentences total). Fill in the specifics the QUICK line cannot carry: what the agent looked at or modified, which files or symbols were touched if relevant, decisions or findings, errors hit and how they were resolved.");
        sb.AppendLine("- Plain language; file paths and short symbol names are fine when they are part of what actually happened. NO bullets unless reproducing the agent's own list.");
        sb.AppendLine("- If the QUICK line already covers everything (truly nothing more to say, e.g. a brand-new idle session), repeat it in slightly different words rather than padding.");
        sb.AppendLine("- If the agent presented a TABLE the user needs to SEE to make sense of things (a comparison, a set of options, a plan laid out in rows, or data with columns), reproduce that table inside this field as a GitHub-style markdown table right after the prose (this field is the ONLY place markdown tables are allowed). Keep the agent's rows and columns; do not invent rows. Example:");
        sb.AppendLine("    | Item | Today |");
        sb.AppendLine("    | --- | --- |");
        sb.AppendLine("    | Session name | header |");
        sb.AppendLine("- At most one table.");
        sb.AppendLine();
        sb.AppendLine("Rules for what_claude_wants (the on-screen WHAT CLAUDE WANTS section):");
        // The session's working/waiting/idle verdict is ALREADY computed deterministically
        // by SessionStatusWingman (the colored badge the user sees). Anchor this section to
        // that verdict so the briefing can never contradict the badge -- the old prompt let
        // the model re-decide working-vs-waiting from the buffer, which produced summaries
        // like "Claude is still working" while the badge correctly read "NEEDS YOU".
        sb.AppendLine(WhatClaudeWantsDirective(context.CurrentColor));
        sb.AppendLine("- Preserve the agent's phrasing. Do NOT reword, soften, summarize, or improve the actual question. The user trusts the agent's words over yours.");
        sb.AppendLine("- Only add a few words of clarification IN PARENTHESES when the bare question is ambiguous without context. Example: \"Want me to implement it?\" -> \"Want me to implement it (the Tailscale auto-provisioning)?\"");
        sb.AppendLine("- If the agent asked multiple questions, include them all.");
        sb.AppendLine();
        sb.AppendLine("Rules for say (the spoken version, used by the phone's voice mode):");
        sb.AppendLine("- Same content as what_happened + what_claude_wants but optimized for the ear, not the screen. Smooth prose, one short paragraph.");
        sb.AppendLine("- NO markdown of any kind. No tables, no bullets, no asterisks, no backticks, no headings.");
        sb.AppendLine("- Read paths and commands as words only if essential; in general avoid them. Do not say file extensions like \"dot c-s\" or read URLs aloud.");
        sb.AppendLine("- Keep what_claude_wants verbatim in the say field too when there is a real question; reading the agent's own words is the point.");
        sb.AppendLine("- Aim for under ~30 seconds of speech (roughly 60-80 words). Tighter is better.");
        sb.AppendLine();
        sb.AppendLine("Rules for actions (tappable answer options - what was called QUICK REPLIES):");
        sb.AppendLine("- If what_claude_wants is a decision the user can answer in a few words (yes/no, this-or-that, pick from a short menu), populate this array with 2 to 4 options.");
        sb.AppendLine("- Each option is the LITERAL text the user would send back to the agent, phrased as the user's own reply (not a description).");
        sb.AppendLine("- Cover the real choices the agent offered; do not invent options the agent did not imply.");
        sb.AppendLine("- If there is no clear short answer (the agent is just working, or the reply needs real typing), return an empty array: []");
        sb.AppendLine();
        sb.AppendLine("Rules for running_in_background (this is the ONE case where you may override a NEEDS YOU determination):");
        sb.AppendLine("- The session may look idle to the system's dumb silence timer (no spinner, an empty input box, the badge already set to red NEEDS YOU) yet actually be BLOCKED ON ITS OWN BACKGROUND WORK that it kicked off and will resume from on its own. In that case it does NOT need the user.");
        sb.AppendLine("- Set running_in_background to true ONLY when BOTH are clearly true: (1) the agent is NOT asking you any question and is NOT parked on any prompt, permission box, or decision; AND (2) the screen shows it is waiting on its OWN background task that finishes on its own - for example a footer or line like \"N shell still running\", \"running in the background\", \"esc to interrupt\" alongside a long build, or the agent saying in its own words that it is waiting for a background build/task/command to finish before continuing (e.g. \"I'll wait for it to finish\", \"notified automatically when the background task completes\").");
        sb.AppendLine("- If there is ANY real question, prompt, numbered choice box, permission gate, or decision the user must make, set running_in_background to false. A pending ask ALWAYS wins over a background wait.");
        sb.AppendLine("- When unsure, set false. Do not guess a background task into existence; you must see real evidence of one on screen.");
        sb.AppendLine("- When you set running_in_background to true, what_claude_wants and say MUST say a background task is still running and nothing is needed from the user yet (this OVERRIDES the what_claude_wants directive above for this turn).");
        sb.AppendLine();
        sb.AppendLine("Answer ONLY from the context below. Do NOT invent file names, decisions, or questions. If the context does not show what the agent is asking, say so plainly in what_claude_wants and say.");
        sb.AppendLine();
        AppendSessionContext(sb, context);
        return sb.ToString();
    }

    /// <summary>
    /// Binds the briefing's "WHAT CLAUDE WANTS" section to the authoritative status color
    /// the wingman already computed, so the LLM cannot re-derive a contradicting verdict
    /// from the raw buffer. Red/blue/green/yellow map to the four real situations; the model
    /// still does the useful work of extracting the verbatim question when the state is red.
    /// </summary>
    internal static string WhatClaudeWantsDirective(string color)
    {
        return (color ?? "").Trim().ToLowerInvariant() switch
        {
            StatusColor.Red =>
                "The session's state has ALREADY been determined: the agent is WAITING ON THE USER. Treat this as fact. "
                + "State, in the agent's OWN WORDS, the exact question, request, or decision it is waiting on. "
                + "Do NOT write that Claude is still working or that nothing is needed -- that contradicts the determined state and is wrong. "
                + "If the buffer does not clearly show the question, say the agent is waiting on a response but the exact prompt is not visible.",
            StatusColor.Blue =>
                "The session's state has ALREADY been determined: the agent is actively WORKING and is not waiting on anything. "
                + "Write exactly: \"Claude is still working; nothing is needed from you right now.\" Do not state a question or invent a decision.",
            StatusColor.Green =>
                "The session's state has ALREADY been determined: the session is idle / ready and nothing is pending. "
                + "Write exactly: \"Nothing pending. Waiting for you to give it a task.\" and nothing else.",
            StatusColor.Yellow =>
                "The session's state has ALREADY been determined: the session is idle but needs attention (for example uncommitted work or a soft warning). "
                + "State what needs attention in ONE short sentence. Do not say Claude is still working.",
            _ =>
                "The session's current state could not be determined from the data source. "
                + "Say plainly that the state is unknown; do NOT guess whether the agent is working or waiting.",
        };
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
    /// drifted, or has completed it. One fresh strong-model call over the goal plus the
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
    // Structured-intent actuation (Path A): decide one action; the Director executes it.
    // The subprocess stays TOOL-LESS (RunSideClaudeAsync passes --tools ""); the model only
    // ever returns a WingmanAction, and WingmanActionExecutor is the sole thing that writes
    // to the PTY. See docs/wingman/WINGMAN.md, "Actuation (structured-intent)".
    // ====================================================================

    /// <summary>
    /// Decide a single action to take on a session, given its live screen + cursor and
    /// recent state. Returns a <see cref="WingmanAction"/>; the SAFE DEFAULT for anything
    /// ambiguous or unparseable is <see cref="WingmanAction.ActNone"/> (do nothing). This
    /// method runs a tool-less strong-model side-call and does NOT touch the terminal -
    /// execution is the caller's job (<c>WingmanActionExecutor.Execute</c>), so the
    /// read/decide and the write stay separated. Throws on subprocess failure/timeout; the
    /// HTTP boundary maps that to a wingman_failed result.
    /// </summary>
    public static async Task<WingmanAction> DecideSessionActionAsync(
        WingmanAskContext context, string claudeExePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claudeExePath))
            throw new InvalidOperationException("no claude CLI configured");

        var prompt = BuildActionDecisionPrompt(context);
        var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct, Model);
        return ParseActionDecisionJson(stdout);
    }

    internal static string BuildActionDecisionPrompt(WingmanAskContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the Wingman for a coding session. You may act ON THE USER'S BEHALF at the session's input prompt: type text, press named keys, or submit a line.");
        sb.AppendLine();
        sb.AppendLine(ClaudeCodeScreenReference);
        sb.AppendLine();
        sb.AppendLine("Choose EXACTLY ONE action:");
        sb.AppendLine("- \"none\": do nothing. THE SAFE DEFAULT. Choose this whenever the next step is not obvious, when there is a real decision only the user should make, when the agent is still working, or when you are unsure.");
        sb.AppendLine("- \"type\": put text in the input box WITHOUT submitting it (the user will review/submit). Provide \"text\".");
        sb.AppendLine("- \"send_keys\": press one or more named keys. Provide \"keys\" as an array. Allowed names: Enter, Esc, Tab, Space, Up, Down, Left, Right, Ctrl+C, Backspace.");
        sb.AppendLine("- \"submit\": type text and press Enter, sending it to the agent. Provide \"text\". Use this ONLY when the answer is unambiguous from the goal and the on-screen question.");
        sb.AppendLine();
        sb.AppendLine("Act only when it genuinely helps and the next step is unambiguous. If in doubt, choose \"none\". Never guess at a decision the user owns (e.g. approving a destructive command, picking between real alternatives).");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{\"action\": \"none|type|send_keys|submit\", \"text\": \"<for type/submit>\", \"keys\": [\"Enter\"], \"reason\": \"<one short sentence>\", \"confidence\": \"low|medium|high\"}");
        sb.AppendLine();
        sb.AppendLine($"=== SESSION ===  agent={context.AgentKind}, state={context.ActivityState}, color={context.CurrentColor} ({context.CurrentReason})");

        // The agent's pending question, if the latest turn summary captured one - the most
        // direct signal of what (if anything) the session is waiting on.
        var latest = context.RecentTurnSummaries.Count > 0 ? context.RecentTurnSummaries[^1] : null;
        if (latest is not null && !string.IsNullOrEmpty(latest.NeedsUser) && latest.NeedsUser != "no")
        {
            sb.AppendLine($"=== AGENT IS WAITING ===  kind={latest.NeedsUser}");
            if (!string.IsNullOrWhiteSpace(latest.NeedsUserShort))
                sb.AppendLine(Truncate(latest.NeedsUserShort, 500));
        }

        sb.AppendLine();
        sb.AppendLine("=== SCREEN (resolved grid; cursor marks where typing goes) ===");
        sb.AppendLine(RenderScreenForPrompt(context));

        if (!string.IsNullOrEmpty(context.BufferTailText))
        {
            sb.AppendLine();
            sb.AppendLine("=== TERMINAL BUFFER (tail, ANSI stripped) ===");
            sb.AppendLine(Truncate(context.BufferTailText, 2000));
        }
        return sb.ToString();
    }

    /// <summary>Render the grid rows for the prompt, noting the cursor cell so the model
    /// can tell where input would land. Drops blank rows above/below the content.</summary>
    private static string RenderScreenForPrompt(WingmanAskContext context)
    {
        if (context.ScreenRows.Count == 0)
            return "(no grid available)";

        var sb = new StringBuilder();
        if (context.CursorRow >= 0)
            sb.AppendLine($"[cursor at row {context.CursorRow}, col {context.CursorCol}]");
        for (int r = 0; r < context.ScreenRows.Count; r++)
        {
            var line = context.ScreenRows[r];
            if (string.IsNullOrWhiteSpace(line)) continue;
            sb.AppendLine($"{r,3}| {line}");
        }
        return sb.ToString().TrimEnd();
    }

    internal static WingmanAction ParseActionDecisionJson(string raw)
    {
        var none = new WingmanAction { Action = WingmanAction.ActNone, Reason = "no action" };
        if (string.IsNullOrWhiteSpace(raw)) return none;

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
            var action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "").Trim().ToLowerInvariant() : "";
            var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "").Trim() : "";
            var confidence = root.TryGetProperty("confidence", out var c) ? (c.GetString() ?? "low").Trim().ToLowerInvariant() : "low";

            var keys = new List<string>();
            if (root.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array)
                foreach (var item in k.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = (item.GetString() ?? "").Trim();
                        if (name.Length > 0) keys.Add(name);
                    }

            // Validate the intent so a half-formed action can never reach the executor:
            // type/submit need text; send_keys needs keys. Anything else collapses to none.
            switch (action)
            {
                case WingmanAction.ActType or WingmanAction.ActSubmit when !string.IsNullOrEmpty(text):
                    return new WingmanAction { Action = action, Text = text, Reason = reason, Confidence = confidence };
                case WingmanAction.ActSendKeys when keys.Count > 0:
                    var sendKeys = new WingmanAction { Action = action, Reason = reason, Confidence = confidence };
                    sendKeys.Keys.AddRange(keys);
                    return sendKeys;
                default:
                    return new WingmanAction { Action = WingmanAction.ActNone, Reason = string.IsNullOrEmpty(reason) ? "no action" : reason, Confidence = confidence };
            }
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WingmanService] action-decision JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            return none;
        }
    }

    // ====================================================================
    // Internals: side-claude invocation (mirrors RecapGenerator)
    // ====================================================================

    /// <summary>
    /// Spawn  claude --print --tools ""  on the Wingman's strong <see cref="Model"/> with
    /// the given prompt as a positional arg.  Returns stdout text.  Throws on non-zero
    /// exit or timeout.
    /// </summary>
    private static async Task<string> RunSideClaudeAsync(string prompt, string claudeExePath, CancellationToken ct, string model = Model)
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
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(model) ? Model : model);
        // NOTE: --bare is intentionally NOT passed.  --bare disables keychain reads,
        // which prevents the side-call from picking up the user's OAuth credentials
        // from ~/.claude/.credentials.json and fails with "Not logged in".
        // --tools "" below already prevents tool use (the main safety reason for
        // --bare).  The cost of dropping --bare is some extra auto-context (CLAUDE.md
        // auto-discovery, auto-memory) - acceptable for a short side-call.
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

    /// <summary>
    /// Spawn a FULL-POWER, fresh-per-call Claude Code session for the Wingman:
    /// "claude --print" with a scoped read-only tool allow-list, MCP disabled (lean,
    /// fast cold start), bounded turns, fresh context (--no-session-persistence).
    /// Returns the final stdout text. Throws on non-zero exit or timeout.
    ///
    /// This is the Phase 2 sibling of <see cref="RunSideClaudeAsync"/>: same fresh,
    /// stateless lifecycle, but with tools enabled so the session can read what it
    /// needs on its own instead of being handed a fixed paste. The caller is
    /// responsible for choosing a read-only <paramref name="allowedTools"/> set; this
    /// method never enables write/execute tools implicitly.
    /// </summary>
    private static async Task<string> RunWingmanSessionAsync(
        string prompt, string claudeExePath, string workingDirectory, string allowedTools, int maxTurns,
        CancellationToken ct, string model = Model)
    {
        // Empty MCP config + --strict-mcp-config => the session loads NO MCP servers
        // (not the user's globals), so cold start stays lean and the tool surface is
        // exactly what we allow below. Written fresh per call, deleted in finally.
        var mcpConfigPath = Path.Combine(Path.GetTempPath(), $"cc-wingman-mcp-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(mcpConfigPath, "{\"mcpServers\":{}}", ct);

        try
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
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(model) ? Model : model);
            psi.ArgumentList.Add("--no-session-persistence");
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(allowedTools);
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(mcpConfigPath);
            psi.ArgumentList.Add("--strict-mcp-config");
            psi.ArgumentList.Add("--max-turns");
            psi.ArgumentList.Add(maxTurns.ToString());
            psi.ArgumentList.Add("--dangerously-skip-permissions");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("text");
            psi.ArgumentList.Add(prompt);

            psi.WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Path.GetTempPath();

            foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CC_SESSION_ID", "GIT_EDITOR" })
                psi.Environment.Remove(k);

            var sw = Stopwatch.StartNew();
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for claude --print (wingman session)");
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
                throw new TimeoutException($"wingman session did not finish within {ProcessTimeout.TotalSeconds}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            if (proc.ExitCode != 0)
            {
                FileLog.Write($"[WingmanService] wingman session exit={proc.ExitCode} in {sw.ElapsedMilliseconds}ms, stderr={Truncate(stderr, 400)}");
                throw new InvalidOperationException($"wingman session exited {proc.ExitCode}: {stderr.Trim()}");
            }

            FileLog.Write($"[WingmanService] wingman session done in {sw.ElapsedMilliseconds}ms (tools={allowedTools}, maxTurns={maxTurns}), output chars={stdout.Length}");
            return stdout.Trim();
        }
        finally
        {
            try { if (File.Exists(mcpConfigPath)) File.Delete(mcpConfigPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Output of <see cref="WingmanService.CleanVoiceTranscriptAsync"/>.
/// Always populated even on failure (Cleaned falls back to raw, Target to "agent").
/// </summary>
/// <param name="Cleaned">The cleaned transcript, with any "Hey wingman" wake phrase stripped when <paramref name="Target"/> is "wingman".</param>
/// <param name="Reason">One-sentence explanation of what changed (or a failure reason).</param>
/// <param name="Target">Who the utterance is addressed to: "agent" (default, send to the session) or "wingman" (route to the read-only Ask-the-Wingman channel).</param>
public sealed record VoiceCleanupResult(string Cleaned, string Reason);
