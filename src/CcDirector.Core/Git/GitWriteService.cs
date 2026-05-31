using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Result of a git write command: the exit code plus captured stdout/stderr.
/// </summary>
public sealed class GitWriteResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// Runs the git WRITE actions the desktop Source Control view offers - stage, unstage,
/// discard, commit - by shelling <c>git</c> in the session's repo. Reads stay on
/// <see cref="GitStatusProvider"/> / WingmanService.GitSnapshotAsync; this is the
/// mutation half, exposed over REST so the Cockpit reaches parity with the desktop.
/// </summary>
public sealed class GitWriteService
{
    /// <summary>Stage paths (<c>git add -- &lt;paths&gt;</c>), or everything when paths is empty (<c>git add -A</c>).</summary>
    public Task<GitWriteResult> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        => paths.Count == 0
            ? RunGitAsync(repoPath, new[] { "add", "-A" }, ct)
            : RunGitAsync(repoPath, Prepend("add", "--", paths), ct);

    /// <summary>Unstage paths (<c>git reset HEAD -- &lt;paths&gt;</c>), or everything when paths is empty.</summary>
    public Task<GitWriteResult> UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        => paths.Count == 0
            ? RunGitAsync(repoPath, new[] { "reset", "HEAD" }, ct)
            : RunGitAsync(repoPath, Prepend2("reset", "HEAD", "--", paths), ct);

    /// <summary>
    /// Discard unstaged changes to tracked paths (<c>git checkout -- &lt;paths&gt;</c>). Does NOT
    /// delete untracked files - that would need <c>git clean</c>, which we deliberately do not do.
    /// </summary>
    public Task<GitWriteResult> DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        => paths.Count == 0
            ? Task.FromResult(new GitWriteResult { Success = false, Error = "discard requires at least one path" })
            : RunGitAsync(repoPath, Prepend("checkout", "--", paths), ct);

    /// <summary>Commit staged changes (<c>git commit -m &lt;message&gt;</c>).</summary>
    public Task<GitWriteResult> CommitAsync(string repoPath, string message, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(message)
            ? Task.FromResult(new GitWriteResult { Success = false, Error = "commit message is required" })
            : RunGitAsync(repoPath, new[] { "commit", "-m", message }, ct);

    private static string[] Prepend(string a, string b, IReadOnlyList<string> paths)
    {
        var args = new List<string>(paths.Count + 2) { a, b };
        args.AddRange(paths);
        return args.ToArray();
    }

    private static string[] Prepend2(string a, string b, string c, IReadOnlyList<string> paths)
    {
        var args = new List<string>(paths.Count + 3) { a, b, c };
        args.AddRange(paths);
        return args.ToArray();
    }

    private static async Task<GitWriteResult> RunGitAsync(string repoPath, string[] args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return new GitWriteResult { Success = false, Error = $"repo path not found: {repoPath}" };

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        FileLog.Write($"[GitWriteService] git {string.Join(' ', args)} (cwd={repoPath})");

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var result = new GitWriteResult
        {
            Success = proc.ExitCode == 0,
            ExitCode = proc.ExitCode,
            Output = stdout.ToString().TrimEnd(),
            Error = stderr.ToString().TrimEnd(),
        };
        if (!result.Success)
            FileLog.Write($"[GitWriteService] git failed exit={proc.ExitCode}: {result.Error}");
        return result;
    }
}
