using System.Diagnostics;
using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Generates a "what was done / what is next" recap by asking a REAL driver-backed
/// session (via <see cref="SessionAskRunner"/>, issue #509) over a digest of the
/// session. Does NOT touch the live session that is being recapped, and no longer
/// uses the metered <c>--print</c> one-shot path - a real session bills against the
/// user's subscription (issue #511).
/// </summary>
public static class RecapGenerator
{
    /// <summary>Default model for recap generation.</summary>
    public const string DefaultModel = "opus";

    /// <summary>How long to wait for the recap session before giving up.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Open a real, throwaway ClaudeCode session over the digest and read back the
    /// markdown recap text. Returns the recap on success. The model argument is
    /// accepted for signature compatibility but the real session runs on the user's
    /// configured default model; recap no longer pins a cheap one-shot model.
    /// </summary>
    /// <param name="digestText">
    /// The session digest, ideally pre-formatted by
    /// <see cref="SummaryBuilder.FormatAsHandoverPrompt"/>. We treat it as opaque
    /// context to feed the session.
    /// </param>
    /// <param name="claudeExePath">Absolute path to claude.exe (from AgentOptions.ClaudePath).</param>
    /// <param name="model">Model alias or full name (kept for signature compatibility).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> GenerateAsync(
        string digestText,
        string claudeExePath,
        string model = DefaultModel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(digestText))
            throw new ArgumentException("digestText is required", nameof(digestText));
        if (string.IsNullOrEmpty(claudeExePath))
            throw new ArgumentException("claudeExePath is required", nameof(claudeExePath));

        var prompt = BuildPrompt(digestText);
        FileLog.Write($"[RecapGenerator] GenerateAsync: model={model}, digestChars={digestText.Length}, promptChars={prompt.Length}");

        // Issue #511: the recap now opens a REAL session (SessionAskRunner) billed against
        // the user's subscription instead of the metered `claude --print` one-shot path.
        // A neutral temp working directory is used: the recap reads only the digest in the
        // prompt, so it needs no repo on disk, and temp keeps any session state out of the
        // user's repos.
        var workDir = CreateRecapWorkingDirectory();
        FileLog.Write($"[RecapGenerator] GenerateAsync: opening a real session (no --print), workDir={workDir}");

        var sw = Stopwatch.StartNew();
        try
        {
            var runner = new SessionAskRunner();
            var result = await runner.AskAsync(
                AgentKind.ClaudeCode,
                claudeExePath,
                agentArgs: null,
                workingDirectory: workDir,
                prompt: prompt,
                timeout: ProcessTimeout,
                ct: ct);
            sw.Stop();
            FileLog.Write($"[RecapGenerator] done in {sw.ElapsedMilliseconds}ms (real session), output chars={result.Answer.Length}");
            return result.Answer.Trim();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>Create the throwaway working directory the recap session runs in.</summary>
    private static string CreateRecapWorkingDirectory()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cc-recap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        return workDir;
    }

    private static string BuildPrompt(string digestText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are summarizing a Claude Code session that another agent ran.");
        sb.AppendLine("Below is the structured digest. Write a recap with exactly TWO markdown sections:");
        sb.AppendLine();
        sb.AppendLine("## What was done");
        sb.AppendLine("- Five to eight short bullets, one line each. Describe concrete accomplishments,");
        sb.AppendLine("  files touched, commands run, decisions made. No preamble.");
        sb.AppendLine();
        sb.AppendLine("## What is next");
        sb.AppendLine("- Three to six short bullets, one line each. Derive from outstanding TODOs,");
        sb.AppendLine("  the last user prompt, and the assistant's last reply. If nothing is pending,");
        sb.AppendLine("  say 'Session is at a clean stopping point.' and stop.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY those two markdown sections. No greetings, no closing remarks.");
        sb.AppendLine("- Do NOT speculate beyond what is in the digest.");
        sb.AppendLine("- Do NOT use emojis or special unicode characters.");
        sb.AppendLine();
        sb.AppendLine("=== SESSION DIGEST ===");
        sb.AppendLine();
        sb.Append(digestText);
        return sb.ToString();
    }
}
