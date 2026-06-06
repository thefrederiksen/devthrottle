using System.Diagnostics;
using System.Text;
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
            // Carried forward, never invented: a failed wingman read must not amnesia the
            // session's standing chapter title, and never starts a chapter (NewChapter=false).
            Headline = package.CurrentHeadline ?? "",
            NewChapter = false,
            TurnTitle = "",
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
/// persistence). The model returns JSON matching the frozen contract; the validation layer
/// (D5) verifies the evidence is verbatim and the options are sane, REJECTING rather than
/// rendering garbage. No prose-mining anywhere (D6).
///
/// The prompt and the validation are <see cref="TurnBriefContract"/> (issue #185): ONE
/// frozen v2.3 contract shared with the Gateway's warm-brain brief agent. This cold-spawn
/// path survives until issue #187 deletes the Director-side pipeline.
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

        var prompt = TurnBriefContract.BuildPrompt(package);
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

        var brief = TurnBriefContract.ParseAndValidate(stdout, package, Id);
        FileLog.Write($"[TurnBriefGenerator] done in {sw.ElapsedMilliseconds}ms: {(brief is null ? "REJECTED by validation" : $"needsYou={(brief.NeedsYou is null ? "null" : brief.NeedsYou.Urgency)}")}");
        return brief;
    }

    // Thin wrappers so existing tests (and callers) keep their entry points until #187.
    internal static string BuildPrompt(TurnPackage p) => TurnBriefContract.BuildPrompt(p);

    internal static TurnBriefDto? ParseAndValidate(string raw, TurnPackage package, string generatorId)
        => TurnBriefContract.ParseAndValidate(raw, package, generatorId);

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
