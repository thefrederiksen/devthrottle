using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Implements the pull-request-merged signal (C1) by shelling the GitHub CLI (<c>gh</c>).
/// If <c>gh</c> is not installed, not authenticated, or errors for any reason, both probes
/// return false so the local git signals (C2 origin-branch-gone, C3 contained-in-main) decide.
/// </summary>
public sealed class GhMergedPullRequestProbe : IMergedPullRequestProbe
{
    public Task<bool> IsBranchMergedAsync(string repoPath, string branch, CancellationToken ct = default) =>
        AnyPullRequestAsync(repoPath, branch, "merged", ct);

    public Task<bool> HasOpenPullRequestAsync(string repoPath, string branch, CancellationToken ct = default) =>
        AnyPullRequestAsync(repoPath, branch, "open", ct);

    private static async Task<bool> AnyPullRequestAsync(string repoPath, string branch, string state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "pr", "list", "--head", branch, "--state", state, "--json", "number", "--limit", "1" })
            psi.ArgumentList.Add(a);

        FileLog.Write($"[GhMergedPullRequestProbe] gh pr list --head {branch} --state {state}");

        var stdout = new StringBuilder();
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                FileLog.Write($"[GhMergedPullRequestProbe] gh unavailable/failed (exit={proc.ExitCode}) - relying on local git signals");
                return false;
            }
        }
        catch (Exception ex)
        {
            // gh not installed, or the process could not start - fall through to local git signals.
            // not-an-error: the gh command line is optional; without it the local git signals decide
            FileLog.Write($"[GhMergedPullRequestProbe] gh could not run ({ex.Message}) - relying on local git signals");
            return false;
        }

        // gh returns a JSON array; a non-empty array means at least one matching pull request.
        var text = stdout.ToString().Trim();
        return text.Length > 0 && text != "[]" && text.Contains("\"number\"", StringComparison.Ordinal);
    }
}
