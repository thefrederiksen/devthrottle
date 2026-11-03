using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public class GitSyncStatus
{
    public string BranchName { get; init; } = "";
    public bool IsDetachedHead { get; init; }
    public bool HasUpstream { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public int BehindMainCount { get; init; } // -1 if on main already
    public string MainBranchName { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public class GitSyncStatusProvider
{
    public async Task<GitSyncStatus> GetSyncStatusAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            var read = await RunGitAsync(repoPath, new[] { "status", "--branch", "--porcelain=v2" }, ct);
            if (read.Output == null)
                return new GitSyncStatus { Success = false, Error = read.Problem };

            var status = ParseBranchHeaders(read.Output);

            // Determine behind-main count if not on main
            if (!status.IsDetachedHead && !IsMainBranch(status.BranchName))
            {
                var mainBranch = await DetectMainBranchAsync(repoPath, ct);
                if (mainBranch != null)
                {
                    var countRead = await RunGitAsync(repoPath, new[] { "rev-list", "--count", $"HEAD..origin/{mainBranch}" }, ct);
                    if (countRead.Output != null && int.TryParse(countRead.Output.Trim(), out var count))
                    {
                        return new GitSyncStatus
                        {
                            BranchName = status.BranchName,
                            IsDetachedHead = status.IsDetachedHead,
                            HasUpstream = status.HasUpstream,
                            AheadCount = status.AheadCount,
                            BehindCount = status.BehindCount,
                            BehindMainCount = count,
                            MainBranchName = mainBranch,
                            Success = true
                        };
                    }

                    // The main branch was detected but the behind-main count could not be read (a
                    // transient object-store failure, malformed output). The sync status is
                    // INCOMPLETE, so fail closed rather than publish a false BehindMainCount of zero
                    // as verified (inspection).
                    return new GitSyncStatus { Success = false, Error = $"could not read behind-main count for {status.BranchName}" };
                }
            }

            return status;
        }
        catch (OperationCanceledException)
        {
            // A superseded scan cancelled this compute - propagate, never fold into a failed status.
            throw;
        }
        catch (Exception ex)
        {
            return new GitSyncStatus { Success = false, Error = ex.Message };
        }
    }

    public static GitSyncStatus ParseBranchHeaders(string output)
    {
        string branchName = "";
        bool isDetached = false;
        bool hasUpstream = false;
        int ahead = 0;
        int behind = 0;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("# branch.head "))
            {
                branchName = line["# branch.head ".Length..];
                if (branchName == "(detached)")
                    isDetached = true;
            }
            else if (line.StartsWith("# branch.upstream "))
            {
                hasUpstream = true;
            }
            else if (line.StartsWith("# branch.ab "))
            {
                // Format: # branch.ab +3 -1
                var parts = line["# branch.ab ".Length..].Split(' ');
                if (parts.Length >= 2)
                {
                    if (parts[0].StartsWith('+'))
                        int.TryParse(parts[0][1..], out ahead);
                    if (parts[1].StartsWith('-'))
                        int.TryParse(parts[1][1..], out behind);
                }
            }
        }

        return new GitSyncStatus
        {
            BranchName = branchName,
            IsDetachedHead = isDetached,
            HasUpstream = hasUpstream,
            AheadCount = ahead,
            BehindCount = behind,
            BehindMainCount = IsMainBranch(branchName) ? -1 : 0,
            MainBranchName = "",
            Success = true
        };
    }

    public async Task FetchAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            await RunGitAsync(repoPath, new[] { "fetch", "--quiet" }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Swallow — invalid paths, git not found, etc.
        }
    }

    private async Task<string?> DetectMainBranchAsync(string repoPath, CancellationToken ct)
    {
        var result = await RunGitAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (result.Output != null)
            return "main";

        result = await RunGitAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (result.Output != null)
            return "master";

        return null;
    }

    private static bool IsMainBranch(string branchName) =>
        branchName is "main" or "master";

    /// <summary>
    /// One git read. <c>Output</c> is null unless the command both launched and exited zero;
    /// <c>Problem</c> then says which of those two it was. Keeping them apart is the point: the
    /// caller used to report "Failed to start git process" for BOTH, so a git that ran perfectly
    /// well and exited non-zero was described as never having started (issue #1048).
    /// </summary>
    private readonly record struct GitRead(string? Output, string Problem);

    private async Task<GitRead> RunGitAsync(string repoPath, string[] args, CancellationToken ct)
    {
        // ProcessRunner drains BOTH pipes (the old code never drained stderr, so a git command that
        // filled its error pipe could deadlock) and honors cancellation by killing the child so a
        // superseded scan does not leave git processes behind (issue 516).
        // Optional locks off: a killed status must not strand index.lock (issue #2872).
        var r = await ProcessRunner.RunAsync("git", args, repoPath, standardInput: null, GitOptionalLocks.Off, ct);
        if (!r.Started)
            return new GitRead(null, GitLaunchFailure.Describe(r.StartErrorCode, r.StandardError));
        if (r.ExitCode != 0)
            return new GitRead(null, $"git {string.Join(' ', args)} exited {r.ExitCode}: {r.StandardError.Trim()}");
        return new GitRead(r.StandardOutput, "");
    }
}
