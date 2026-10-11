using System.Diagnostics;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// A real git repository built ONCE per test class and copied per test.
///
/// WHY. Six classes in this suite test the Director against real git - the worktree reaper, the worktree
/// inventory, the branch service, the repo-state collector, the pool reaper and the upstream probe. Each
/// test built its own bare origin, clone, identity, first commit and push: ten real git processes and
/// three to five seconds before the test did anything, 90 tests in all, about seven minutes of a suite
/// whose budget is two. Real git is the right subject; rebuilding it per test is not.
///
/// HOW. A subclass describes the repository once in <see cref="Build"/>, exactly as the old constructor
/// did, under a template folder. xUnit constructs the subclass once per test class (IClassFixture) and
/// disposes it after the last test. Each test calls <see cref="CopyTo"/> and receives its own root, a
/// file-for-file copy of the template, which the test is free to mutate and deletes in its Dispose as it
/// always did. Tests therefore stay as isolated from each other as before: nothing is shared but the
/// template, and the template is never written after it is built.
///
/// THE ONE THING A COPY MUST FIX. A clone records its origin as an ABSOLUTE path, so a copied clone would
/// push to the TEMPLATE's origin and tests would see each other's branches. <see cref="CopyTo"/> points
/// every clone named in <see cref="Clones"/> at the origin inside its own copy. Worktrees share their
/// clone's configuration, so one call per clone covers them. A relative URL would not do: git resolves it
/// against the directory the command runs in, which for a worktree is not the clone.
/// </summary>
public abstract class GitRepositoryTemplate : IDisposable
{
    private readonly string _prefix;
    private readonly object _gate = new();
    private string? _templateRoot;

    protected GitRepositoryTemplate(string prefix)
    {
        _prefix = prefix;
    }

    /// <summary>Build the repository under <paramref name="root"/>: the bare origin, the clone(s), the first commit.</summary>
    protected abstract void Build(string root);

    /// <summary>The clone folder names under the root whose origin must be re-pointed after a copy.</summary>
    protected abstract IReadOnlyList<string> Clones { get; }

    /// <summary>The bare origin's folder name under the root.</summary>
    protected virtual string OriginName => "origin.git";

    /// <summary>A fresh root holding a copy of the template, with every clone's origin pointing inside it.</summary>
    public string CopyTo()
    {
        var template = TemplateRoot();
        var root = TestTempRoot.For(_prefix);
        CopyDirectory(template, root);
        var origin = Path.Combine(root, OriginName);
        foreach (var clone in Clones)
            RunGit(Path.Combine(root, clone), "remote", "set-url", "origin", origin);
        return root;
    }

    private string TemplateRoot()
    {
        lock (_gate)
        {
            if (_templateRoot is not null) return _templateRoot;
            var root = TestTempRoot.For(_prefix + "template-");
            Directory.CreateDirectory(root);
            Build(root);
            _templateRoot = root;
            return root;
        }
    }

    public void Dispose()
    {
        if (_templateRoot is not null && Directory.Exists(_templateRoot))
            TestTempRoot.DeleteTree(_templateRoot);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    /// <summary>Run git in <paramref name="workingDirectory"/> and fail loudly when it does.</summary>
    protected static string RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var git = Process.Start(startInfo)!;
        var error = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory} (exit {git.ExitCode}): {error.Result}");
        return output;
    }
}
