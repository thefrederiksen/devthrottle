using System.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Summarizes Claude responses using the Claude CLI with haiku model.
/// Produces short, conversational summaries suitable for TTS.
/// </summary>
public class ClaudeSummarizer : IResponseSummarizer
{
    private const string ClaudeExecutable = "claude";
    private const string Model = "haiku";
    private const int TimeoutSeconds = 30;

    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// The prompt template for summarization.
    /// </summary>
    private const string SummarizationPrompt = """
        You are turning a coding agent's written reply into words a person will
        hear out loud, probably while driving. Rewrite it as two to four short,
        casual sentences, like telling a friend what happened or what the answer
        is. Speak in concepts only. Do not read code, commands, file paths,
        function names, or symbols out loud. If code matters, say in plain words
        what it does or would do. Output ONLY the spoken version, nothing else.
        """;

    /// <summary>
    /// The prompt template for periodic progress notes during a long turn. The
    /// input is a raw, noisy terminal tail; the job is to extract intent.
    /// </summary>
    private const string ProgressPrompt = """
        Below is the recent terminal output of a coding agent that is STILL working
        on a task. In one or two short, calm spoken sentences, tell a person who is
        listening while driving what the agent appears to be doing right now. Speak
        in plain concepts only: no code, commands, file paths, function names, or
        symbols. Begin as if continuing to wait, for example "Still going" or
        "Still working". If you genuinely cannot tell what it is doing, say only
        that it is still working. Output ONLY the spoken update, nothing else.
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
            FileLog.Write($"[ClaudeSummarizer] SummarizeAsync FAILED: {ex.Message}");
            // Fallback: return truncated original
            return TruncateForSpeech(response);
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
            FileLog.Write("[ClaudeSummarizer] Empty output from Claude");
            return TruncateForSpeech(response);
        }
        return CleanupForSpeech(summary);
    }

    /// <summary>
    /// Run <c>claude -p &lt;prompt&gt;</c> with <paramref name="input"/> piped to
    /// stdin and return the trimmed stdout. Throws when the CLI exits non-zero.
    /// </summary>
    private static async Task<string> RunClaudeAsync(string prompt, string input, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ClaudeExecutable,
            Arguments = $"-p \"{prompt}\" --model {Model} --output-format text",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(timeoutCts.Token);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            FileLog.Write($"[ClaudeSummarizer] Claude exited with code {process.ExitCode}: {error}");
            throw new InvalidOperationException($"Claude CLI failed: {error}");
        }

        return output.Trim();
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
    /// Removes markdown formatting, code blocks, etc.
    /// </summary>
    private static string CleanupForSpeech(string text)
    {
        // Remove code blocks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");

        // Remove inline code
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`[^`]+`", "");

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

    /// <summary>
    /// Truncate text for speech when summarization fails.
    /// </summary>
    private static string TruncateForSpeech(string text)
    {
        text = CleanupForSpeech(text);

        if (text.Length <= 300)
            return text;

        // Find a sentence break near the cutoff
        var cutoff = 300;
        var sentenceEnd = text.LastIndexOf('.', cutoff);
        if (sentenceEnd > 100)
            return text[..(sentenceEnd + 1)];

        // No good sentence break, just truncate
        return text[..cutoff] + "...";
    }
}
