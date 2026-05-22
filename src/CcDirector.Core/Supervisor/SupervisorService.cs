using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Supervisor;

/// <summary>
/// Generalised Session-Supervisor for cc-director.
///
/// Every method on this class is a one-shot side-call to `claude --print --bare
/// --model haiku --tools ""` carrying a focused prompt - exactly the pattern
/// <see cref="RecapGenerator"/> uses for the existing recap feature. The
/// "Session Supervisor" from the PRD is the conceptual sum of these short
/// fresh-context calls; we do NOT spawn a long-running shadow process per
/// session.
///
/// Methods correspond 1:1 to the PRD's Session Supervisor responsibilities.
/// </summary>
public static class SupervisorService
{
    /// <summary>Cheap fast model we run the Supervisor on. Haiku family.</summary>
    public const string DefaultModel = "haiku";

    /// <summary>Hard timeout per Supervisor call.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

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
            FileLog.Write($"[SupervisorService] CleanVoiceTranscriptAsync FAILED: {ex.Message}");
            return new VoiceCleanupResult(rawTranscript, "supervisor call failed: " + ex.Message);
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
            return new VoiceCleanupResult(fallbackRaw, "supervisor returned empty output");

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
                return new VoiceCleanupResult(fallbackRaw, "supervisor returned empty 'cleaned' field");
            return new VoiceCleanupResult(cleaned.Trim(), string.IsNullOrEmpty(reason) ? "no changes needed" : reason.Trim());
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SupervisorService] cleanup JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            return new VoiceCleanupResult(fallbackRaw, "supervisor JSON parse failed");
        }
    }

    // ====================================================================
    // Phase 2: Per-turn structured summary  (feeds Agent View AND voice TTS)
    // ====================================================================

    /// <summary>
    /// Summarise one completed turn for both screen readers (Agent View) and
    /// ear listeners (voice TTS).  Returns a populated <see cref="TurnSummary"/>
    /// even on Supervisor failure (status field reflects what happened).
    /// </summary>
    public static async Task<TurnSummary> SummarizeTurnAsync(
        TurnData turn,
        string? lastAssistantText,
        string repoPath,
        string claudeExePath,
        CancellationToken ct = default)
    {
        var summary = new TurnSummary
        {
            GeneratedAt = DateTime.UtcNow,
            TurnStartedAt = turn.Timestamp.UtcDateTime,
        };

        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            summary.Status = "supervisor_failed";
            summary.Error = "no claude CLI configured";
            // Provide at least a rule-based headline so the UI is not empty.
            summary.Headline = BuildFallbackHeadline(turn);
            summary.SpokenText = "Agent finished. Check the screen for details.";
            return summary;
        }

        var prompt = BuildTurnSummaryPrompt(turn, lastAssistantText ?? "", repoPath ?? "");

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            ParseTurnSummaryJsonInto(stdout, summary, turn);
            return summary;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SupervisorService] SummarizeTurnAsync FAILED: {ex.Message}");
            summary.Status = "supervisor_failed";
            summary.Error = ex.Message;
            summary.Headline = BuildFallbackHeadline(turn);
            summary.SpokenText = "Agent finished. Check the screen for details.";
            return summary;
        }
    }

    private static string BuildFallbackHeadline(TurnData turn)
    {
        if (turn.FilesTouched.Count > 0 && turn.BashCommands.Count > 0)
            return $"Edited {Path.GetFileName(turn.FilesTouched[0])} and ran {turn.BashCommands.Count} command(s).";
        if (turn.FilesTouched.Count > 0)
            return $"Touched {turn.FilesTouched.Count} file(s); first: {Path.GetFileName(turn.FilesTouched[0])}.";
        if (turn.BashCommands.Count > 0)
            return $"Ran {turn.BashCommands.Count} shell command(s).";
        if (turn.ToolsUsed.Count > 0)
            return $"Used tools: {string.Join(", ", turn.ToolsUsed.Take(3))}.";
        return "Turn completed.";
    }

    private static string BuildTurnSummaryPrompt(TurnData turn, string lastAssistantText, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are summarising one turn of a Claude Code session for a user who does not want to read the raw output, and who may be listening to a voice playback while driving.");
        sb.AppendLine();
        sb.AppendLine("INPUT: the user's prompt, the tools the agent used, the files it touched, the commands it ran, the last assistant text.");
        sb.AppendLine();
        sb.AppendLine("Output ONE JSON object, no markdown fence, exactly this shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"headline\": \"<one short sentence describing what the agent did this turn>\",");
        sb.AppendLine("  \"files_touched\": [\"<list of distinct file paths touched, max 5>\"],");
        sb.AppendLine("  \"commands_run\": [\"<list of distinct shell commands, max 3>\"],");
        sb.AppendLine("  \"decisions\": [\"<key decisions or findings, max 3 bullets>\"],");
        sb.AppendLine("  \"needs_user\": \"<one of: 'no' | 'question' | 'error' | 'permission' | 'idle'>\",");
        sb.AppendLine("  \"needs_user_detail\": \"<short sentence if needs_user != 'no', empty otherwise>\",");
        sb.AppendLine("  \"needs_user_short\": \"<see rules below>\",");
        sb.AppendLine("  \"spoken_text\": \"<see rules below>\"");
        sb.AppendLine("}");
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
        sb.AppendLine("TURN DATA:");
        sb.AppendLine($"- Repo: {repoPath}");
        sb.AppendLine($"- User prompt: {Truncate(turn.UserPrompt, 1500)}");
        sb.AppendLine($"- Tools used: {string.Join(", ", turn.ToolsUsed)}");
        sb.AppendLine($"- Files touched: {string.Join(" | ", turn.FilesTouched.Take(10))}");
        sb.AppendLine($"- Commands run: {string.Join(" | ", turn.BashCommands.Take(10))}");
        sb.AppendLine($"- Last assistant text (up to 8000 chars - the question, if any, is in here; quote it verbatim): {Truncate(lastAssistantText, 8000)}");
        return sb.ToString();
    }

    internal static void ParseTurnSummaryJsonInto(string raw, TurnSummary summary, TurnData turn)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            summary.Status = "parse_failed";
            summary.Error = "supervisor returned empty output";
            summary.Headline = BuildFallbackHeadline(turn);
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
            if (string.IsNullOrEmpty(summary.Headline)) summary.Headline = BuildFallbackHeadline(turn);
            if (string.IsNullOrEmpty(summary.SpokenText)) summary.SpokenText = summary.Headline;
            if (string.IsNullOrEmpty(summary.NeedsUser)) summary.NeedsUser = "no";

            // Trim spoken_text to a hard cap so TTS does not run forever.
            if (summary.SpokenText.Length > 320)
                summary.SpokenText = summary.SpokenText[..317] + "...";

            // Status was defaulted to "ok" in the ctor; leave it.
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SupervisorService] turn-summary JSON parse failed: {ex.Message}, raw='{Truncate(raw, 200)}'");
            summary.Status = "parse_failed";
            summary.Error = "supervisor JSON parse failed";
            summary.Headline = BuildFallbackHeadline(turn);
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
            resp.Status = "supervisor_failed";
            resp.Error = "no claude CLI configured";
            return resp;
        }

        var prompt = BuildRulesPrompt(rulesText, latestSummary, repoPath ?? "");
        string stdout;
        try { stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[SupervisorService] CheckRulesAsync FAILED: {ex.Message}");
            resp.Status = "supervisor_failed";
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
                FileLog.Write($"[SupervisorService] LoadRulesChain: walking parents failed: {ex.Message}");
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
            FileLog.Write($"[SupervisorService] LoadRulesChain: global CLAUDE.md failed: {ex.Message}");
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
            resp.Error = "supervisor returned empty output";
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
                resp.Error = "supervisor JSON missing 'violations' array";
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
            FileLog.Write($"[SupervisorService] rules JSON parse failed: {ex.Message}");
            resp.Status = "parse_failed";
            resp.Error = "supervisor JSON parse failed";
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
            FileLog.Write($"[SupervisorService] GitSnapshotAsync FAILED: {ex.Message}");
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
                    FileLog.Write($"[SupervisorService] git diff for recovery prompt failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SupervisorService] git snapshot for recovery prompt failed: {ex.Message}");
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
    // Phase 5: Supervisor Ask - interactive single-turn query about a session
    // ====================================================================

    /// <summary>
    /// Ask the supervisor a question about a specific session. One fresh
    /// <c>claude --print --model haiku</c> call with no session persistence.
    /// The session's recent state (supervisor decisions, turn summaries, buffer
    /// tail, metadata) is piped in as context. No conversation memory between
    /// calls - each ask is independent.
    ///
    /// Caller supplies the session state via the parameters rather than passing
    /// the Session object so this stays a pure function (testable, no UI thread).
    /// </summary>
    public static async Task<SupervisorAskResult> AskAboutSessionAsync(
        string question,
        SupervisorAskContext context,
        string claudeExePath,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(question))
            return new SupervisorAskResult { Status = "bad_request", Error = "empty question" };

        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            sw.Stop();
            return new SupervisorAskResult
            {
                Status = "no_claude",
                Error = "no claude CLI configured",
                Answer = "Supervisor is not configured (no claude CLI path). Set agents.claudePath in config.json.",
                ContextDigest = context.ToDigest(),
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }

        var prompt = BuildAskPrompt(question, context);

        try
        {
            var stdout = await RunSideClaudeAsync(prompt, claudeExePath, ct);
            sw.Stop();
            var answer = (stdout ?? "").Trim();
            // Cap response so a misbehaving model can't return a megabyte into the UI.
            if (answer.Length > 4000) answer = answer[..3997] + "...";
            return new SupervisorAskResult
            {
                Answer = answer,
                Model = DefaultModel,
                LatencyMs = sw.ElapsedMilliseconds,
                ContextDigest = context.ToDigest(),
                Status = "ok",
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[SupervisorService] AskAboutSessionAsync FAILED: {ex.Message}");
            return new SupervisorAskResult
            {
                Status = "supervisor_failed",
                Error = ex.Message,
                Answer = "Supervisor call failed: " + ex.Message,
                ContextDigest = context.ToDigest(),
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }
    }

    internal static string BuildAskPrompt(string question, SupervisorAskContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the supervisor for a CC Director session. The user has a question about THIS session.");
        sb.AppendLine();
        sb.AppendLine("Answer ONLY from the context below. If the context does not contain the answer, say:");
        sb.AppendLine("\"I don't have that in context.\"  Do NOT speculate. Do NOT invent file names, decisions, or activity.");
        sb.AppendLine("Respond in 1-3 short sentences, plain text, no code blocks, no markdown headings.");
        sb.AppendLine();
        sb.AppendLine("=== SESSION METADATA ===");
        sb.AppendLine($"- Repo: {context.RepoPath}");
        sb.AppendLine($"- Agent: {context.AgentKind}");
        sb.AppendLine($"- Activity state: {context.ActivityState}");
        sb.AppendLine($"- Supervisor color: {context.CurrentColor} ({context.CurrentReason})");
        sb.AppendLine($"- Git dirty: {context.GitDirty}");

        if (context.RecentSupervisorEvents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== SUPERVISOR DECISIONS (newest first) ===");
            foreach (var e in context.RecentSupervisorEvents.Take(20))
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
            sb.AppendLine(Truncate(context.BufferTailText, 4000));
        }

        sb.AppendLine();
        sb.AppendLine("=== USER QUESTION ===");
        sb.Append(Truncate(question, 1500));
        return sb.ToString();
    }

    // ====================================================================
    // Internals: side-claude invocation (mirrors RecapGenerator)
    // ====================================================================

    /// <summary>
    /// Spawn  claude --print --bare --model haiku --tools ""  with the given
    /// prompt as a positional arg.  Returns stdout text.  Throws on non-zero
    /// exit or timeout.
    /// </summary>
    private static async Task<string> RunSideClaudeAsync(string prompt, string claudeExePath, CancellationToken ct)
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
        psi.ArgumentList.Add(DefaultModel);
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
            FileLog.Write($"[SupervisorService] claude --print exit={proc.ExitCode} in {sw.ElapsedMilliseconds}ms, stderr={Truncate(stderr, 400)}");
            throw new InvalidOperationException($"claude --print exited {proc.ExitCode}: {stderr.Trim()}");
        }

        FileLog.Write($"[SupervisorService] side-call done in {sw.ElapsedMilliseconds}ms, output chars={stdout.Length}");
        return stdout.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Output of <see cref="SupervisorService.CleanVoiceTranscriptAsync"/>.
/// Always populated even on failure (Cleaned falls back to raw).
/// </summary>
public sealed record VoiceCleanupResult(string Cleaned, string Reason);
