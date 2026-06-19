using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Summarizes Claude responses by asking a REAL driver-backed session
/// (<see cref="SessionAskRunner"/>, issue #509) instead of the metered
/// <c>claude -p</c> one-shot path, so the work bills against the user's
/// subscription (issue #511). Produces short, conversational summaries
/// suitable for text-to-speech.
/// </summary>
public class ClaudeSummarizer : IResponseSummarizer
{
    private const string ClaudeExecutable = "claude";
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(30);

    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// The prompt template for summarization. Internal so tests can assert the
    /// prompt allows non-Latin input (issue #367).
    /// </summary>
    internal const string SummarizationPrompt = """
        You are turning a coding agent's written reply into words a person will
        hear out loud, probably while driving. Your job is FIDELITY, not brevity:
        the listener must hear the agent's actual answer, not a looser version of
        it. Rules:
        - Preserve the actual answer and every concrete fact: names, numbers,
          yes/no, the decision or result. Never drop the facts that ARE the answer.
        - Do not add, embellish, reframe, or change the topic. If the agent did
          not actually answer the question, say that plainly; never invent an answer.
        - Make it sound natural to hear, but completeness wins over shortness.
          Use as many sentences as the answer needs; do not force it into a
          fixed length.
        - Speak for the ear: do not read code, commands, file paths, function
          names, or symbols out loud. When code matters, say in plain words what
          it does or would do.
        - The reply may be in ANY language or script (Korean, Japanese, Arabic,
          and so on). Non-Latin and other Unicode characters are valid content,
          never encoding corruption. Summarize faithfully in the same language
          the reply is written in; never refuse or say the text cannot be read.
        Output ONLY the spoken version, nothing else.
        """;

    /// <summary>
    /// The prompt template for periodic progress notes during a long turn. The
    /// input is a raw, noisy terminal tail; the job is to extract intent.
    /// Internal so tests can assert the prompt allows non-Latin input (issue #367).
    /// </summary>
    internal const string ProgressPrompt = """
        Below is the recent terminal output of a coding agent that is STILL working
        on a task. In one or two short, calm spoken sentences, tell a person who is
        listening while driving what the agent appears to be doing right now. Speak
        in plain concepts only: no code, commands, file paths, function names, or
        symbols. The output may be in any language or script; non-Latin Unicode
        characters are valid content, never encoding corruption - describe the work
        in the same language, and never refuse. Begin as if continuing to wait, for
        example "Still going" or "Still working". If you genuinely cannot tell what
        it is doing, say only that it is still working. Output ONLY the spoken
        update, nothing else.
        """;

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _isAvailable!.Value;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _unavailableReason;
        }
    }

    /// <inheritdoc />
    public async Task<string> SummarizeAsync(string response, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[ClaudeSummarizer] SummarizeAsync: response length={response.Length}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Claude CLI not available: {UnavailableReason}");
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return "No response to summarize.";
        }

        // If the response is already short, return it as-is
        if (response.Length < 200)
        {
            FileLog.Write("[ClaudeSummarizer] Response already short, returning as-is");
            return CleanupForSpeech(response);
        }

        try
        {
            var summary = await RunClaudeSummarizationAsync(response, cancellationToken);
            FileLog.Write($"[ClaudeSummarizer] Summary: {summary.Length} chars");
            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No fallback paraphrase: a silently truncated original would change
            // the meaning, which is exactly the fidelity bug we are fixing. Return
            // empty so ChatService leaves Summary empty and the client speaks the
            // genuine, complete reply (ChatTurnResult.SpokenText falls back to
            // DisplayText). The failure is logged, not hidden.
            FileLog.Write($"[ClaudeSummarizer] SummarizeAsync FAILED: {ex.Message}");
            return "";
        }
    }

    /// <inheritdoc />
    public async Task<string> SummarizeProgressAsync(string recentActivity, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[ClaudeSummarizer] SummarizeProgressAsync: activity length={recentActivity?.Length ?? 0}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Claude CLI not available: {UnavailableReason}");
        }

        if (string.IsNullOrWhiteSpace(recentActivity))
        {
            return "";
        }

        try
        {
            var note = CleanupForSpeech(await RunClaudeAsync(ProgressPrompt, recentActivity, cancellationToken));
            FileLog.Write($"[ClaudeSummarizer] Progress note: {note.Length} chars");
            return note;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A progress note is a best-effort, throwaway courtesy spoken every
            // couple of minutes. If Haiku fails we say nothing this window rather
            // than read raw terminal text aloud; the failure is logged, not hidden.
            FileLog.Write($"[ClaudeSummarizer] SummarizeProgressAsync FAILED: {ex.Message}");
            return "";
        }
    }

    private async Task<string> RunClaudeSummarizationAsync(string response, CancellationToken cancellationToken)
    {
        var summary = await RunClaudeAsync(SummarizationPrompt, response, cancellationToken);
        if (string.IsNullOrEmpty(summary))
        {
            // Empty output: return empty (caller speaks the genuine reply) rather
            // than a truncated paraphrase that would change the meaning.
            FileLog.Write("[ClaudeSummarizer] Empty output from Claude");
            return "";
        }
        return CleanupForSpeech(summary);
    }

    /// <summary>
    /// Ask a REAL ClaudeCode session (<see cref="SessionAskRunner"/>) to apply
    /// <paramref name="prompt"/> to <paramref name="input"/> and return the trimmed
    /// answer. Issue #511: this replaced the metered <c>claude -p</c> one-shot path,
    /// so the summary work bills against the user's subscription. Throws when the
    /// session fails or no answer block comes back.
    /// </summary>
    private static async Task<string> RunClaudeAsync(string prompt, string input, CancellationToken cancellationToken)
    {
        // The instruction template and the reply-to-summarize are joined into one
        // prompt for the session (the old -p path piped the reply on stdin).
        var combinedPrompt = prompt + "\n\n=== REPLY TO SUMMARIZE ===\n" + input;

        // Issue #511: open a real session instead of `claude -p`. A neutral temp
        // working directory keeps any session state out of the user's repos; the
        // summary reads only the text in the prompt, so it needs no repo on disk.
        var workDir = Path.Combine(Path.GetTempPath(), $"cc-voice-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        FileLog.Write($"[ClaudeSummarizer] RunClaudeAsync: opening a real session (no -p), workDir={workDir}");

        try
        {
            var runner = new SessionAskRunner();
            var result = await runner.AskAsync(
                AgentKind.ClaudeCode,
                executablePath: ClaudeExecutable,
                agentArgs: null,
                workingDirectory: workDir,
                prompt: combinedPrompt,
                timeout: AskTimeout,
                ct: cancellationToken);
            return result.Answer.Trim();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    private void CheckAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClaudeExecutable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _isAvailable = false;
                _unavailableReason = "Failed to start Claude CLI";
                return;
            }

            process.WaitForExit(5000);

            if (process.ExitCode == 0)
            {
                _isAvailable = true;
                _unavailableReason = null;
                FileLog.Write("[ClaudeSummarizer] Claude CLI is available");
            }
            else
            {
                _isAvailable = false;
                _unavailableReason = "Claude CLI returned error";
            }
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _unavailableReason = $"Claude CLI not found: {ex.Message}";
            FileLog.Write($"[ClaudeSummarizer] CheckAvailability FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Clean up text for speech synthesis.
    /// Removes markdown formatting, code blocks, etc. Operates only on markdown
    /// syntax characters - all scripts (Latin or not) pass through untouched.
    /// Internal so tests can prove non-Latin input is not dropped (issue #367).
    /// </summary>
    internal static string CleanupForSpeech(string text)
    {
        // Remove code blocks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");

        // Strip inline-code backtick markers but KEEP the inner text (issue #368):
        // identifiers like `sessionName` are the answer's content and must be
        // spoken, not deleted. Code BLOCKS (``` fences) are removed entirely by
        // the rule above, which runs first.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");

        // Remove markdown links, keeping text
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        // Remove bullet points
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\s]*[-*+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove numbered lists
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\s]*\d+\.\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove markdown headers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove bold/italic
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_([^_]+)_", "$1");

        // Collapse multiple newlines
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");

        // Collapse multiple spaces
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ ]{2,}", " ");

        return text.Trim();
    }
}
