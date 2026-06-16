using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Feedback;

/// <summary>
/// One user correction of the terminal state detector: "the status said X, it should
/// have been Y, here's why." This is the human-in-the-loop ground truth that replaces
/// automated hook/terminal agreement measurement - the person watching the terminal is
/// the authority, and every correction is captured with full context so we can improve
/// the detector.
/// </summary>
public sealed record StateVote(
    string SessionId,
    string RepoPath,
    string Agent,
    string DetectedState,
    string DetectedReason,
    string CorrectState,
    string Note,
    string TerminalTail,
    DateTime At);

/// <summary>Outcome of submitting a vote: it is always persisted locally; GitHub is best-effort.</summary>
public sealed record StateVoteResult(bool Saved, bool PostedToGitHub, string Detail);

/// <summary>
/// Persists state-detector corrections locally (never lose feedback) and appends them to
/// one ongoing GitHub tracking issue via the <c>gh</c> CLI. No auth/gating - single-user.
/// </summary>
public static class StateVoteService
{
    private const string Repo = "thefrederiksen/devthrottle";
    private const string IssueTitle = "Wingman terminal-state detector: misclassification reports";

    private static string VotesDir => Path.Combine(CcStorage.Root(), "state-votes");
    private static string VotesFile => Path.Combine(VotesDir, "votes.jsonl");
    private static string IssueNumberFile => Path.Combine(VotesDir, "github-issue.txt");

    public static async Task<StateVoteResult> SubmitAsync(StateVote vote, CancellationToken ct = default)
    {
        FileLog.Write($"[StateVoteService] vote: sid={vote.SessionId}, detected={vote.DetectedState}, correct={vote.CorrectState}");

        // 1. Persist locally first - this must never be lost, regardless of GitHub.
        Directory.CreateDirectory(VotesDir);
        await File.AppendAllTextAsync(VotesFile,
            JsonSerializer.Serialize(vote) + Environment.NewLine, ct);

        // 2. Best-effort: append to the ongoing GitHub tracking issue.
        try
        {
            var (posted, detail) = await PostToGitHubAsync(vote, ct);
            return new StateVoteResult(Saved: true, PostedToGitHub: posted, Detail: detail);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StateVoteService] GitHub post FAILED: {ex.Message}");
            return new StateVoteResult(Saved: true, PostedToGitHub: false, Detail: "saved locally; GitHub post failed: " + ex.Message);
        }
    }

    private static async Task<(bool posted, string detail)> PostToGitHubAsync(StateVote vote, CancellationToken ct)
    {
        var issue = await GetOrCreateIssueAsync(ct);
        if (issue is null)
            return (false, "saved locally; could not find or create the GitHub tracking issue (is gh installed and authed?)");

        var body = BuildCommentBody(vote);
        var (exit, _, stderr) = await RunGhAsync(ct, "issue", "comment", issue, "--repo", Repo, "--body", body);
        if (exit != 0)
            return (false, $"saved locally; gh comment failed (exit {exit}): {Truncate(stderr, 300)}");

        FileLog.Write($"[StateVoteService] appended vote to issue #{issue}");
        return (true, $"posted to GitHub issue #{issue}");
    }

    private static async Task<string?> GetOrCreateIssueAsync(CancellationToken ct)
    {
        // Reuse the remembered issue number if we have one.
        try
        {
            if (File.Exists(IssueNumberFile))
            {
                var saved = (await File.ReadAllTextAsync(IssueNumberFile, ct)).Trim();
                if (!string.IsNullOrEmpty(saved)) return saved;
            }
        }
        catch (Exception ex) { FileLog.Write($"[StateVoteService] read issue file failed: {ex.Message}"); }

        // Create the tracking issue once.
        var initialBody =
            "Auto-created tracking issue. Each comment is a user correction of the Wingman terminal-state detector " +
            "(detected state vs. what it should have been, with terminal context), submitted from the mobile session view.";
        var (exit, stdout, stderr) = await RunGhAsync(ct,
            "issue", "create", "--repo", Repo, "--title", IssueTitle, "--body", initialBody);
        if (exit != 0)
        {
            FileLog.Write($"[StateVoteService] gh issue create failed (exit {exit}): {Truncate(stderr, 300)}");
            return null;
        }

        // gh prints the issue URL on success; the number is its last path segment.
        var url = (stdout ?? "").Trim();
        var number = url.Length > 0 ? url[(url.LastIndexOf('/') + 1)..].Trim() : "";
        if (string.IsNullOrEmpty(number)) return null;

        try { await File.WriteAllTextAsync(IssueNumberFile, number, ct); }
        catch (Exception ex) { FileLog.Write($"[StateVoteService] persist issue number failed: {ex.Message}"); }
        return number;
    }

    private static string BuildCommentBody(StateVote v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### State correction - {v.At:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"- **Detected:** `{v.DetectedState}`" + (string.IsNullOrWhiteSpace(v.DetectedReason) ? "" : $" ({v.DetectedReason})"));
        sb.AppendLine($"- **Should have been:** `{v.CorrectState}`");
        sb.AppendLine($"- **Agent:** {v.Agent}");
        sb.AppendLine($"- **Repo:** `{v.RepoPath}`");
        sb.AppendLine($"- **Session:** `{v.SessionId}`");
        if (!string.IsNullOrWhiteSpace(v.Note))
        {
            sb.AppendLine();
            sb.AppendLine($"**Why it's wrong:** {v.Note.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(v.TerminalTail))
        {
            sb.AppendLine();
            sb.AppendLine("<details><summary>Terminal tail (ANSI stripped)</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(Truncate(v.TerminalTail, 3000));
            sb.AppendLine("```");
            sb.AppendLine("</details>");
        }
        return sb.ToString();
    }

    private static async Task<(int exit, string stdout, string stderr)> RunGhAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("gh Process.Start returned null (is gh installed and on PATH?)");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await proc.WaitForExitAsync(cts.Token);
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
