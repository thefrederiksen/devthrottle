using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>Pluggable brief generation so the orchestrator's lifecycle is testable without
/// the model (phase 1 stub) and the model is swappable (DT1).</summary>
public interface ITurnBriefGenerator
{
    /// <summary>Generator identity recorded on briefs ("wingman:opus", "stub").</summary>
    string Id { get; }

    /// <summary>Interpret one turn. Null = generation failed; the orchestrator degrades.</summary>
    Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct);
}

/// <summary>Phase-1 stub: proves the lifecycle without spending tokens. Also the last-resort
/// degrade tier - an honest "turn N completed" marker, never invented content.</summary>
public sealed class StubTurnBriefGenerator : ITurnBriefGenerator
{
    public string Id => "stub";

    public Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Task.FromResult<TurnBriefDto?>(new TurnBriefDto
        {
            SessionId = package.SessionId.ToString(),
            TurnNumber = package.TurnCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Model = Id,
            Degraded = true,
            DegradeTier = "stub",
            Intent = package.RollingIntent ?? "(no brief yet - wingman unavailable)",
            Did = new List<string>(),
            NeedsYou = null,
        });
    }
}

/// <summary>
/// The wingman call (TURN_BRIEFING.md section 3 box [2], plan DT1): one structured
/// strong-model reading of the turn package via a side `claude --print` spawn (the
/// WingmanService pattern - runs on the Max subscription, no --bare, no tools, no session
/// persistence). The model returns JSON matching the v2.1 contract; the validation layer
/// (D5) verifies the evidence is verbatim and the options are sane, REJECTING rather than
/// rendering garbage. No prose-mining anywhere (D6).
/// </summary>
public sealed class WingmanTurnBriefGenerator : ITurnBriefGenerator
{
    /// <summary>Strong model only (charter). WingmanService.Model is the audited source of truth.</summary>
    public static readonly string Model = WingmanService.Model;

    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(150);

    private readonly string _claudeExePath;

    public string Id => $"wingman:{Model}";

    public WingmanTurnBriefGenerator(string claudeExePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeExePath);
        _claudeExePath = claudeExePath;
    }

    public async Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        FileLog.Write($"[TurnBriefGenerator] GenerateAsync: sid={package.SessionId}, turn={package.TurnCount}, replyPending={package.ReplyPending}");

        var prompt = BuildPrompt(package);
        var sw = Stopwatch.StartNew();
        string stdout;
        try
        {
            stdout = await RunSideClaudeAsync(prompt, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnBriefGenerator] side-claude FAILED: {ex.Message}");
            return null;
        }
        sw.Stop();

        var brief = ParseAndValidate(stdout, package, Id);
        FileLog.Write($"[TurnBriefGenerator] done in {sw.ElapsedMilliseconds}ms: {(brief is null ? "REJECTED by validation" : $"needsYou={(brief.NeedsYou is null ? "null" : brief.NeedsYou.Urgency)}")}");
        return brief;
    }

    // ====================================================================
    // Prompt - built from the captured examples (docs/architecture/wingman/examples/)
    // ====================================================================

    internal static string BuildPrompt(TurnPackage p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the WINGMAN: you brief a busy engineering lead the moment one of their");
        sb.AppendLine("AI coding agents finishes a turn. The lead runs many agents; your brief is the");
        sb.AppendLine("only thing they read. Your job is INTERPRETATION - reduce their cognitive load.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object (no fences, no prose) in exactly this shape:");
        sb.AppendLine("""
{
  "intent": "1-2 sentences: what the USER is trying to get done, carried/updated across turns. NEVER the literal last message (a 'yes' must become what was approved).",
  "did": ["3-6 bullets, past tense, specific, <=15 words each. Proportional: trivial turn -> 1-2 bullets."],
  "needsYou": null OR {
    "statement": "Crisp. Lead with whether anything is broken/blocking, then the concrete action(s).",
    "answerVia": "reply" | "keys",
    "selectionMode": "single" | "multiple",
    "submit": null | "\r",
    "options": [ { "key": "short label", "send": "exact text or key sequence", "note": null | "scope/risk flag" } ],
    "evidence": "EXACT verbatim quote from the AGENT REPLY or the SCREEN - copied character-for-character, never paraphrased",
    "urgency": "blocking" | "review" | "fyi",
    "confidence": "high" | "ambiguous",
    "railLine": "<= 8 words"
  }
}
""");
        sb.AppendLine();
        sb.AppendLine("Rules learned from real captures:");
        sb.AppendLine("- REPLY-PENDING + an on-screen menu (picker/permission/plan approval): the question");
        sb.AppendLine("  exists ONLY on the SCREEN. Read it there. answerVia=keys; options' send = the key");
        sb.AppendLine("  that selects (e.g. \"1\") - pickers confirm with Enter, so send \"1\\r\" for them.");
        sb.AppendLine("- Permission prompts: surface SCOPE. 'Yes, and don't ask again for: git *' is a");
        sb.AppendLine("  standing grant, not a yes - flag it in the option's note.");
        sb.AppendLine("- 'pick any that apply' checklists: selectionMode=multiple, option sends TOGGLE");
        sb.AppendLine("  (just the number), submit=\"\\r\" completes.");
        sb.AppendLine("- Plan approval: summarize THE PLAN in the statement - the decision is about the");
        sb.AppendLine("  plan's content, not menu mechanics.");
        sb.AppendLine("- Nothing blocking but a real decision exists: urgency=fyi, statement starts");
        sb.AppendLine("  'Nothing needed. FYI: ...'. No ask at all: needsYou=null.");
        sb.AppendLine("- Unclear what the agent wants: confidence=ambiguous and SAY SO in the statement");
        sb.AppendLine("  ('unclear; likely X or Y'). Never invent certainty. Never invent options.");
        sb.AppendLine("- The screen may contain rendering tears (overdrawn lines); read through them.");
        sb.AppendLine();
        sb.AppendLine("=== SESSION CONTEXT ===");
        sb.AppendLine($"Prior rolling intent: {p.RollingIntent ?? "(first brief of this session)"}");
        if (p.PriorRailLines.Count > 0)
            sb.AppendLine("Recent needs-you lines: " + string.Join(" | ", p.PriorRailLines));
        sb.AppendLine($"First user prompt (session goal seed): {Truncate(p.FirstUserPrompt, 600)}");
        sb.AppendLine();
        sb.AppendLine("=== THIS TURN'S USER PROMPT ===");
        sb.AppendLine(Truncate(p.LastUserPrompt, 2000));
        sb.AppendLine();
        sb.AppendLine($"=== TRANSCRIPT OF THE TURN (reply-pending: {p.ReplyPending}) ===");
        sb.AppendLine(p.TranscriptDelta);
        sb.AppendLine();
        sb.AppendLine("=== CURRENT SCREEN (bottom of the terminal) ===");
        sb.AppendLine(p.ScreenTail);
        return sb.ToString();
    }

    // ====================================================================
    // Parse + validate (D5/D6: mechanical VALIDATION of the model, never interpretation)
    // ====================================================================

    internal static TurnBriefDto? ParseAndValidate(string raw, TurnPackage package, string generatorId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Models sometimes wrap JSON in fences despite instructions; unwrap mechanically.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var first = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && lastFence > first) json = json[(first + 1)..lastFence].Trim();
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            FileLog.Write($"[TurnBriefGenerator] validation: not JSON ({ex.Message})");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var brief = new TurnBriefDto
            {
                SessionId = package.SessionId.ToString(),
                TurnNumber = package.TurnCount,
                GeneratedAtUtc = DateTime.UtcNow,
                Model = generatorId,
                Degraded = false,
                Intent = root.TryGetProperty("intent", out var i) ? (i.GetString() ?? "").Trim() : "",
            };
            if (string.IsNullOrWhiteSpace(brief.Intent))
            {
                FileLog.Write("[TurnBriefGenerator] validation: missing intent");
                return null;
            }

            if (root.TryGetProperty("did", out var did) && did.ValueKind == JsonValueKind.Array)
                brief.Did = did.EnumerateArray()
                    .Select(b => (b.GetString() ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .Take(8)
                    .ToList();

            if (root.TryGetProperty("needsYou", out var ny) && ny.ValueKind == JsonValueKind.Object)
            {
                var n = new TurnBriefNeedsYou
                {
                    Statement = Str(ny, "statement"),
                    AnswerVia = Str(ny, "answerVia") is "keys" ? "keys" : "reply",
                    SelectionMode = Str(ny, "selectionMode") is "multiple" ? "multiple" : "single",
                    Submit = ny.TryGetProperty("submit", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null,
                    Evidence = Str(ny, "evidence"),
                    Urgency = Str(ny, "urgency") switch { "blocking" => "blocking", "fyi" => "fyi", _ => "review" },
                    Confidence = Str(ny, "confidence") is "ambiguous" ? "ambiguous" : "high",
                    RailLine = Str(ny, "railLine"),
                };
                if (string.IsNullOrWhiteSpace(n.Statement) || string.IsNullOrWhiteSpace(n.RailLine))
                {
                    FileLog.Write("[TurnBriefGenerator] validation: needsYou missing statement/railLine");
                    return null;
                }
                if (n.RailLine.Length > 60) n.RailLine = n.RailLine[..60];

                if (ny.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var key = Str(o, "key");
                        var send = Str(o, "send");
                        if (key.Length == 0 || send.Length == 0) continue;
                        n.Options.Add(new TurnBriefOption
                        {
                            Key = key.Length > 60 ? key[..60] : key,
                            Send = send,
                            Note = o.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                        });
                        if (n.Options.Count == 6) break;
                    }
                }

                // Contract invariants (the captures' findings):
                // multiple REQUIRES a submit send - a checklist without submit is unanswerable.
                if (n.SelectionMode == "multiple" && string.IsNullOrEmpty(n.Submit))
                {
                    FileLog.Write("[TurnBriefGenerator] validation: multiple without submit");
                    return null;
                }

                // Evidence must be VERBATIM from the reply or the screen (whitespace-tolerant).
                // Failed validation does not kill the brief - it kills the RECEIPTS, visibly.
                if (!string.IsNullOrWhiteSpace(n.Evidence))
                {
                    var inReply = package.LastAssistantText is not null && BriefBuilder.FindVerbatim(package.LastAssistantText, n.Evidence) is not null;
                    var onScreen = BriefBuilder.FindVerbatim(package.ScreenTail, n.Evidence) is not null;
                    if (!inReply && !onScreen)
                    {
                        FileLog.Write("[TurnBriefGenerator] validation: evidence not verbatim; dropping receipts");
                        n.Evidence = "";
                    }
                }

                brief.NeedsYou = n;
            }

            return brief;
        }
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    // ====================================================================
    // The side-claude spawn (WingmanService pattern; #168 stdout-error lesson applied)
    // ====================================================================

    private async Task<string> RunSideClaudeAsync(string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _claudeExePath,
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
        psi.ArgumentList.Add(Model);
        // NOTE: --bare is NOT passed - it disables keychain reads and the side-call dies
        // with "Not logged in" (issue #168). --tools "" already prevents tool use.
        psi.ArgumentList.Add("--no-session-persistence");
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        psi.ArgumentList.Add(prompt);
        psi.WorkingDirectory = Path.GetTempPath();
        foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CC_SESSION_ID", "GIT_EDITOR" })
            psi.Environment.Remove(k);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for claude --print (turn brief)");
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
            if (ct.IsCancellationRequested) throw; // watch-cancel or shutdown
            throw new TimeoutException($"turn-brief claude --print did not finish within {ProcessTimeout.TotalSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            // claude --print writes some fatal errors (e.g. "Not logged in") to STDOUT.
            var error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new InvalidOperationException($"claude --print exited {proc.ExitCode}: {Truncate(error, 300)}");
        }
        return stdout.Trim();
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
