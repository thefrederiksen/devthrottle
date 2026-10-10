namespace CcDirector.Core.Utilities;

/// <summary>
/// Resolves a local repository's origin remote to its repo name and to GitHub web URLs. The GitHub
/// "new issue" helpers (used by the desktop "New GitHub Issue" menu item, the screenshot Issue button, and
/// the Director's GET /sessions/{sid}/github-urls endpoint) are GitHub-only. The repo-name helpers
/// (<see cref="ResolveRepoNameCached"/> / <see cref="ParseRepoName"/>, used by the DevThrottle Stats repo
/// grouping) understand BOTH github.com and Azure DevOps (dev.azure.com and the legacy visualstudio.com), so
/// a repo on either host folds its worktrees and per-machine clones into one Repos row.
/// </summary>
public static class GitHubUrls
{
    private static readonly TimeSpan RemoteUrlRegexTimeout = TimeSpan.FromMilliseconds(50);

    // repoPath -> resolved "owner/repo" repo name, or "" when the checkout is on no host we recognize. A
    // local clone's origin does not change over a process lifetime, and resolving it spawns a git subprocess,
    // so this is cached: ResolveRepoNameCached runs on the Director's roster path (every session, every poll)
    // and must not fork git each time. Misses ("" - no origin, not a git repo, unrecognized remote) are
    // cached too, so a checkout with no recognized remote is probed once, not on every poll.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> RepoNameCache =
        new(StringComparer.Ordinal);

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
    /// The "owner/repo" (GitHub) or "org/repo" (Azure DevOps) repo name for a local checkout, or "" when the
    /// checkout's origin is on neither host (no origin remote, not a git repo, or an unrecognized remote).
    /// Best-effort and NEVER throws: an empty result is a legitimate answer - not every checkout is on a host
    /// we recognize - and the caller (the DevThrottle Stats repo grouping) falls back to the folder name for
    /// those. Cached by path; see <see cref="RepoNameCache"/>.
    ///
    /// This is the key that makes every worktree and every per-machine clone of one repository roll up into
    /// a single row on the Repos page: they all share one origin remote, so they all resolve to one repo name.
    /// </summary>
    public static string ResolveRepoNameCached(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return "";
        return RepoNameCache.GetOrAdd(repoPath, ResolveRepoNameUncached);
    }

    private static string ResolveRepoNameUncached(string repoPath)
    {
        try
        {
            if (!Directory.Exists(repoPath)) return "";
            var repoName = ParseRepoName(GetOriginRemoteUrl(repoPath));
            FileLog.Write($"[GitHubUrls] ResolveRepoNameCached: {repoPath} -> {repoName}");
            return repoName;
        }
        catch (Exception ex)
        {
            // A checkout with no origin, or one on a host we do not recognize, is an expected state, not a
            // failure to hide: it simply has no repo name and groups by its folder name instead. Recorded, "".
            FileLog.Write($"[GitHubUrls] ResolveRepoNameCached FAILED: {repoPath} has no recognized remote repo name: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Converts an origin remote URL to the GitHub "new issue" URL. Pure string logic so it is unit-testable
    /// without git. Accepts the three GitHub remote shapes:
    ///   git@github.com:owner/repo.git
    ///   ssh://git@github.com/owner/repo.git
    ///   https://github.com/owner/repo(.git)
    /// Throws when the remote is not on github.com - "new issue" is a GitHub concept, so this stays
    /// GitHub-only even though <see cref="ParseRepoName"/> also understands Azure DevOps.
    /// </summary>
    internal static string ParseNewIssueUrl(string originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            throw new ArgumentException("Origin remote URL is required", nameof(originUrl));
        var gh = TryMatchGitHub(originUrl.Trim());
        if (gh is null)
            throw new InvalidOperationException($"Origin is not a GitHub remote: {originUrl}");
        return $"https://github.com/{gh.Value.owner}/{gh.Value.repo}/issues/new";
    }

    /// <summary>
    /// Extracts the "owner/repo" (GitHub) or "org/repo" (Azure DevOps) repo name from an origin remote URL.
    /// Pure string logic so it is unit-testable without git. The repo name is host-neutral on purpose: every
    /// worktree and every per-machine clone of one repository shares one origin, so they all resolve to the
    /// identical repo name and fold into a single Repos row. Throws when the remote is empty or on neither host.
    ///
    /// Azure DevOps identifies a repo as org/project/repo; the repo name keeps org/repo (dropping the project)
    /// to match GitHub's two-part shape and how the owner refers to it. The bounded cost: two repos of the
    /// same name in different projects of one org would share a repo name - accepted, and vanishingly rare here.
    /// </summary>
    internal static string ParseRepoName(string originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            throw new ArgumentException("Origin remote URL is required", nameof(originUrl));

        var url = originUrl.Trim();
        var gh = TryMatchGitHub(url);
        if (gh is not null)
            return $"{gh.Value.owner}/{gh.Value.repo}";

        var az = TryMatchAzureDevOps(url);
        if (az is not null)
            return $"{az.Value.org}/{az.Value.repo}";

        throw new InvalidOperationException($"Origin is not a recognized GitHub or Azure DevOps remote: {originUrl}");
    }

    /// <summary>GitHub (owner, repo) from a remote URL, or null when it is not a github.com remote. Non-throwing.</summary>
    public static (string Owner, string Repo)? TryGitHubOwnerRepo(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : TryMatchGitHub(url.Trim());

    /// <summary>Azure DevOps (org, repo) from a remote URL, or null when it is not an Azure DevOps remote. Non-throwing.</summary>
    public static (string Org, string Repo)? TryAzureDevOpsOrgRepo(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : TryMatchAzureDevOps(url.Trim());

    // GitHub remote -> (owner, repo), or null when the URL is not a github.com remote. Accepts the SSH
    // (git@github.com:owner/repo), ssh:// and https:// shapes; the ".git" suffix is optional.
    private static (string owner, string repo)? TryMatchGitHub(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            url, @"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        return m.Success ? (m.Groups["owner"].Value, m.Groups["repo"].Value) : null;
    }

    // Azure DevOps remote -> (org, repo), or null. Covers the three shapes the tooling emits:
    //   https://[user@]dev.azure.com/{org}/{project}/_git/{repo}   (modern HTTPS)
    //   git@ssh.dev.azure.com:v3/{org}/{project}/{repo}            (modern SSH)
    //   https://{org}.visualstudio.com/[collection/]{project}/_git/{repo}  (legacy)
    // The project segment is matched but discarded; the repo name is org/repo. The ".git" suffix is optional.
    private static (string org, string repo)? TryMatchAzureDevOps(string url)
    {
        var https = System.Text.RegularExpressions.Regex.Match(
            url, @"dev\.azure\.com/(?<org>[^/\s]+)/[^/\s]+/_git/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        if (https.Success) return (https.Groups["org"].Value, https.Groups["repo"].Value);

        var ssh = System.Text.RegularExpressions.Regex.Match(
            url, @"ssh\.dev\.azure\.com:v3/(?<org>[^/\s]+)/[^/\s]+/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        if (ssh.Success) return (ssh.Groups["org"].Value, ssh.Groups["repo"].Value);

        var legacy = System.Text.RegularExpressions.Regex.Match(
            url, @"(?<org>[^/@\s.]+)\.visualstudio\.com/(?:[^\s]+/)?_git/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        if (legacy.Success) return (legacy.Groups["org"].Value, legacy.Groups["repo"].Value);

        return null;
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
