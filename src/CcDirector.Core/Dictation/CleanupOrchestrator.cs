using System.Diagnostics;
using System.Text;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Runs a final transcript through Claude Haiku to repair mistranscriptions
/// and apply per-profile style. The vocabulary and known mistranscription
/// patterns from the dictionary are passed to Haiku in natural language so
/// the model can both fix listed cases and generalize to near-misses.
///
/// Mirrors the side-call pattern in SupervisorService: spawn `claude --print
/// --model haiku --no-session-persistence --tools "" --dangerously-skip-permissions
/// --output-format text` with the prompt as a positional argument.
///
/// Fails open: on any error (no claude CLI, timeout, non-zero exit) the
/// returned <see cref="CleanupOutcome"/> carries the raw transcript verbatim
/// and a failure reason. Callers should ship the raw text rather than block.
/// </summary>
public sealed class CleanupOrchestrator
{
    public const string DefaultModel = "haiku";
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly string _claudeExePath;

    public CleanupOrchestrator(string claudeExePath)
    {
        if (string.IsNullOrWhiteSpace(claudeExePath))
            throw new ArgumentException("claudeExePath is required", nameof(claudeExePath));
        _claudeExePath = claudeExePath;
    }

    /// <summary>
    /// Clean a raw transcript using the dictionary and the specified profile.
    /// Returns the original text unchanged when cleanup is disabled or fails.
    /// </summary>
    public async Task<CleanupOutcome> CleanAsync(
        string rawTranscript,
        DictationDictionary dictionary,
        string profileName,
        CancellationToken ct = default)
    {
        FileLog.Write($"[CleanupOrchestrator] CleanAsync: profile={profileName}, len={rawTranscript?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");

        var profile = ResolveProfile(dictionary, profileName);
        if (!profile.CleanupEnabled)
        {
            FileLog.Write($"[CleanupOrchestrator] CleanAsync: cleanup disabled for profile '{profile.Name}', returning verbatim");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: $"profile '{profile.Name}' has cleanup disabled");
        }

        var prompt = BuildPrompt(rawTranscript, dictionary, profile);

        try
        {
            var cleaned = await RunHaikuAsync(prompt, ct);
            // Defensive: never let the cleanup pass produce an empty string
            // when the input was non-empty. That would silently swallow user
            // dictation, which is far worse than a slightly-imperfect cleanup.
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                FileLog.Write("[CleanupOrchestrator] CleanAsync: Haiku returned empty output, falling back to raw");
                return new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup returned empty");
            }
            return new CleanupOutcome(cleaned, Applied: true, Reason: null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CleanupOrchestrator] CleanAsync FAILED: {ex.Message}");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Build the cleanup prompt. Exposed internally so tests can inspect it
    /// without invoking the model.
    /// </summary>
    internal static string BuildPrompt(
        string rawTranscript,
        DictationDictionary dictionary,
        DictationProfile profile)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are cleaning up a voice dictation transcript from a software engineer.");
        sb.AppendLine();

        if (dictionary.Vocabulary.Count > 0)
        {
            sb.AppendLine(
                "The speaker uses these technical terms and proper nouns, which MUST appear "
                + "correctly in the output (exact capitalization and punctuation):");
            foreach (var term in dictionary.Vocabulary)
                sb.AppendLine($"  - {term}");
            sb.AppendLine();
        }

        if (dictionary.CommonMistranscriptions.Count > 0)
        {
            sb.AppendLine(
                "Speech-to-text often mishears these terms. Here are mistranscription "
                + "patterns observed in real use. When you see one of these in the "
                + "transcript, replace it with the canonical term on the left:");
            foreach (var kv in dictionary.CommonMistranscriptions)
            {
                var quoted = string.Join(", ", kv.Value.Select(v => $"\"{v}\""));
                sb.AppendLine($"  - {kv.Key} : {quoted}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(
            "This list is not exhaustive. If you see a word that is not a standard "
            + "English word AND is a plausible near-miss for one of the listed terms, "
            + "also replace it. When unsure between two possible matches, pick the one "
            + "that fits the sentence context. If a word is truly ambiguous and you have "
            + "no way to decide, leave it alone rather than guessing.");
        sb.AppendLine();

        sb.AppendLine("Also fix obvious filler words (uh, um, like). Preserve all other words exactly as they appear.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(profile.StylePrompt))
        {
            sb.AppendLine($"Style guidance for this profile: {profile.StylePrompt}");
            sb.AppendLine();
        }

        sb.AppendLine("Return ONLY the cleaned transcript text on a single line. No commentary, no quotes, no preamble.");
        sb.AppendLine();
        sb.AppendLine("Transcript to clean:");
        sb.Append(rawTranscript);

        return sb.ToString();
    }

    private static DictationProfile ResolveProfile(DictationDictionary dictionary, string profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName)
            && dictionary.Profiles.TryGetValue(profileName, out var found))
            return found;
        if (dictionary.Profiles.TryGetValue("default", out var def))
            return def;
        // The loader always inserts a "default" profile, so this branch is a
        // belt-and-braces fallback for callers that construct dictionaries
        // by hand.
        return new DictationProfile("default", CleanupEnabled: true, StylePrompt: null);
    }

    private async Task<string> RunHaikuAsync(string prompt, CancellationToken ct)
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
        psi.ArgumentList.Add(DefaultModel);
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

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for claude --print");
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
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"claude --print did not finish within {ProcessTimeout.TotalSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        if (proc.ExitCode != 0)
        {
            FileLog.Write($"[CleanupOrchestrator] claude --print exit={proc.ExitCode} in {sw.ElapsedMilliseconds}ms, "
                          + $"stderr={Truncate(stderr, 400)}");
            throw new InvalidOperationException($"claude --print exited {proc.ExitCode}: {stderr.Trim()}");
        }

        FileLog.Write($"[CleanupOrchestrator] cleanup done in {sw.ElapsedMilliseconds}ms, output chars={stdout.Length}");
        return stdout.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Outcome of a single cleanup pass. <see cref="Text"/> always carries
/// something safe to ship: cleaned text on success, raw transcript on
/// failure or when cleanup is disabled for the profile.
/// </summary>
public sealed record CleanupOutcome(string Text, bool Applied, string? Reason);
