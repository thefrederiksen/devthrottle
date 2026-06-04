using System.IO;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Builds GitHub web URLs for a local repository by resolving its origin remote.
/// Used by the "New GitHub Issue" session menu item and the screenshot Issue button.
/// </summary>
internal static class GitHubUrls
{
    private static readonly TimeSpan RemoteUrlRegexTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Resolves the repo's origin remote via git and converts it to the GitHub
    /// "new issue" URL. Throws with a clear message when the directory is not a
    /// git repo, has no origin remote, or the origin is not on github.com.
    /// </summary>
    public static string BuildNewIssueUrl(string repoPath)
    {
        FileLog.Write($"[GitHubUrls] BuildNewIssueUrl: repoPath={repoPath}");
        if (!Directory.Exists(repoPath))
            throw new InvalidOperationException($"Directory not found: {repoPath}");

        var origin = GetOriginRemoteUrl(repoPath);
        var url = ParseNewIssueUrl(origin);
        FileLog.Write($"[GitHubUrls] BuildNewIssueUrl: origin={origin} -> {url}");
        return url;
    }

    /// <summary>
    /// Converts an origin remote URL to the GitHub "new issue" URL. Pure string
    /// logic so it is unit-testable without git. Accepts the three remote shapes:
    ///   git@github.com:owner/repo.git
    ///   ssh://git@github.com/owner/repo.git
    ///   https://github.com/owner/repo(.git)
    /// Throws when the remote is not on github.com.
    /// </summary>
    internal static string ParseNewIssueUrl(string originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            throw new ArgumentException("Origin remote URL is required", nameof(originUrl));

        var match = System.Text.RegularExpressions.Regex.Match(
            originUrl.Trim(), @"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        if (!match.Success)
            throw new InvalidOperationException($"Origin is not a GitHub remote: {originUrl}");

        return $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/issues/new";
    }

    /// <summary>
    /// Runs "git remote get-url origin" in the repo and returns the trimmed URL.
    /// Throws when the repo has no origin remote (or is not a git repo).
    /// </summary>
    private static string GetOriginRemoteUrl(string repoPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "remote get-url origin",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length == 0)
            throw new InvalidOperationException($"No 'origin' remote in {repoPath}");
        return output;
    }
}
