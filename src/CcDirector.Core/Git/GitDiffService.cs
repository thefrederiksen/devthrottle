using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Reads diffs for the diff viewer: unstaged (working tree vs index) and staged (index vs HEAD),
/// whole-repo or one file. Read-only; the write actions stay on <see cref="GitWriteService"/>.
/// </summary>
public sealed class GitDiffService
{
    private readonly GitCommandRunner _git;

    public GitDiffService(GitCommandRunner? git = null)
    {
        _git = git ?? new GitCommandRunner();
    }

    /// <summary>Unstaged changes (working tree vs index), parsed. Optionally one file.</summary>
    public async Task<IReadOnlyList<FileDiff>> UnstagedAsync(string repoPath, string? file = null, CancellationToken ct = default)
        => await RunAndParseAsync(repoPath, BuildArgs(cached: false, file), ct);

    /// <summary>Staged changes (index vs HEAD), parsed. Optionally one file.</summary>
    public async Task<IReadOnlyList<FileDiff>> StagedAsync(string repoPath, string? file = null, CancellationToken ct = default)
        => await RunAndParseAsync(repoPath, BuildArgs(cached: true, file), ct);

    /// <summary>
    /// The diff of one UNTRACKED file rendered as an all-added diff, so a brand-new file is
    /// reviewable like any other change (git diff shows nothing for untracked paths).
    /// </summary>
    public async Task<FileDiff?> UntrackedAsync(string repoPath, string file, CancellationToken ct = default)
    {
        try
        {
            var full = Path.Combine(repoPath, file);
            if (!File.Exists(full))
                return null;
            var info = new FileInfo(full);
            if (info.Length > 2_000_000)
                return new FileDiff { NewPath = file, IsBinary = true }; // too big to render as text

            var lines = await File.ReadAllLinesAsync(full, ct);
            if (LooksBinary(full))
                return new FileDiff { NewPath = file, IsBinary = true };

            var diffLines = lines.Select((text, i) => new DiffLine
            {
                Kind = DiffLineKind.Added,
                Text = text,
                NewNumber = i + 1,
            }).ToList();

            return new FileDiff
            {
                NewPath = file,
                Hunks = new[] { new DiffHunk { Header = $"@@ -0,0 +1,{lines.Length} @@ (new file)", Lines = diffLines } },
                Added = lines.Length,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitDiffService] UntrackedAsync failed for {file} FAILED: {ex.Message}");
            return null;
        }
    }

    private static string[] BuildArgs(bool cached, string? file)
    {
        var args = new List<string> { "diff", "--no-color" };
        if (cached)
            args.Add("--cached");
        if (!string.IsNullOrWhiteSpace(file))
        {
            args.Add("--");
            args.Add(file);
        }
        return args.ToArray();
    }

    private async Task<IReadOnlyList<FileDiff>> RunAndParseAsync(string repoPath, string[] args, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, args, ct);
        if (!result.Success)
        {
            FileLog.Write($"[GitDiffService] git {string.Join(' ', args)} failed: {result.Error}");
            return Array.Empty<FileDiff>();
        }
        return DiffParser.Parse(result.Output);
    }

    private static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> probe = stackalloc byte[8000];
        int read = stream.Read(probe);
        for (int i = 0; i < read; i++)
            if (probe[i] == 0)
                return true;
        return false;
    }
}
