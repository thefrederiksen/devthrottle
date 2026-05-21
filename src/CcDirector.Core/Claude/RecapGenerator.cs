using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Generates a "what was done / what is next" recap by spawning a side-claude
/// process in --print mode against a digest of the session. Does NOT touch the
/// live session that is being recapped. Designed to use the cheapest model
/// available (Haiku) since recap quality only needs to be good enough.
/// </summary>
public static class RecapGenerator
{
    /// <summary>Default cheap model for recap generation. Haiku 4.5 family.</summary>
    public const string DefaultModel = "haiku";

    /// <summary>How long to wait for the side-claude process before killing it.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Spawn <c>claude --print --model &lt;model&gt; ...</c> with the digest as the prompt
    /// and capture its stdout. Returns the markdown recap text on success.
    /// </summary>
    /// <param name="digestText">
    /// The session digest, ideally pre-formatted by
    /// <see cref="SummaryBuilder.FormatAsHandoverPrompt"/>. We treat it as opaque
    /// context to feed claude.
    /// </param>
    /// <param name="claudeExePath">Absolute path to claude.exe (from AgentOptions.ClaudePath).</param>
    /// <param name="model">Model alias or full name. Defaults to "haiku".</param>
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
        psi.ArgumentList.Add(model);
        // --bare strips hooks, LSP, plugin sync, attribution, auto-memory, CLAUDE.md
        // auto-discovery, background prefetches, keychain reads. Perfect for a side-call
        // that should have zero side effects and minimal latency.
        psi.ArgumentList.Add("--bare");
        // Don't save the recap to the resume picker / session history.
        psi.ArgumentList.Add("--no-session-persistence");
        // Disable all tools. "" means "no tools enabled" per claude --help.
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");
        // No file or shell access needed, but we still need to bypass permission
        // dialogs that would otherwise block --print.
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        // The prompt goes as the positional argument to --print.
        psi.ArgumentList.Add(prompt);

        // Run in a neutral working directory (system temp). The side-claude has no
        // tools enabled so cwd does not matter, but using temp avoids polluting any
        // repo with .claude state.
        psi.WorkingDirectory = Path.GetTempPath();

        // Strip the same Claude-Code-related env vars the ConPty backend strips,
        // so the side-claude is not detected as a nested session.
        foreach (var k in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CC_SESSION_ID", "GIT_EDITOR" })
        {
            psi.Environment.Remove(k);
        }

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for claude --print");

        // Close stdin -- we're passing the prompt via argv, not via stdin.
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
            FileLog.Write($"[RecapGenerator] claude --print failed: exit={proc.ExitCode}, stderr={stderr.Substring(0, Math.Min(400, stderr.Length))}");
            throw new InvalidOperationException(
                $"claude --print exited {proc.ExitCode}: {stderr.Trim()}");
        }

        FileLog.Write($"[RecapGenerator] done in {sw.ElapsedMilliseconds}ms, output chars={stdout.Length}");
        return stdout.Trim();
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
