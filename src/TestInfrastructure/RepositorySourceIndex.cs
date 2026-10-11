#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// The repository's source files, listed ONCE per test process and shared by every guard that reads the tree.
///
/// WHY. About twenty tests scan the repository for a rule - no production file names the provider's usage
/// endpoint, every AI client is built with a tag, only the composition root mints a system scope, and so on.
/// Each one used to walk the tree itself with Directory.EnumerateFiles(..., AllDirectories) and drop bin and
/// obj AFTERWARDS, so each walked every build output tree under the repository: medians of a few hundred
/// milliseconds, worst cases of 36 to 118 seconds when the disk was busy, in guards that read a few thousand
/// files. The guards are right to exist - they catch real drift - so the walk is what changes, not the rule.
///
/// WHAT IT IS. The files git knows about - tracked, plus new files not yet added and not ignored - under the
/// repository root, as absolute paths with the platform's separators, listed by one `git ls-files` the first
/// time anything asks and held for the rest of the process. That is the definition the guards actually want:
/// a build output, a node_modules tree, a stale untracked project and another checkout parked under
/// .claude/worktrees are all ignored by git and are not the repository, while a file a developer wrote a
/// minute ago and has not yet committed IS, and a guard that could not see it would pass the one change it
/// exists to catch. A tracked file deleted from disk but not yet staged is left out, because there is nothing
/// to read.
///
/// THE ROOT is the directory holding cc-director.sln above the test assembly, found once. Every guard used to
/// carry its own copy of that walk too, in six spellings.
///
/// The list is built on first use and never refreshed: a test process runs against one tree, and a guard that
/// wants to see a file written during the run should not be reading the tree in the first place.
/// </summary>
public static class RepositorySourceIndex
{
    private static readonly Lazy<string> RootValue =
        new(FindRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<string>> FilesValue =
        new(() => ListFiles(Root), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The repository root: the directory holding cc-director.sln above the test assembly.</summary>
    public static string Root => RootValue.Value;

    /// <summary>Every file of the repository, as absolute paths with the platform's separators.</summary>
    public static IReadOnlyList<string> Files => FilesValue.Value;

    /// <summary>Every C# source file of the repository.</summary>
    public static IEnumerable<string> CSharpFiles => WithExtension(".cs");

    /// <summary>Every file whose extension is <paramref name="extension"/> (with its dot, any case).</summary>
    public static IEnumerable<string> WithExtension(string extension) =>
        Files.Where(f => HasExtension(f, extension));

    /// <summary>
    /// Every file in <paramref name="directory"/> or below it, optionally only those with
    /// <paramref name="extension"/>. The directory is repository-relative with either separator
    /// ("src/CcDirector.Gateway") or absolute. A directory that does not exist yields nothing.
    /// </summary>
    public static IEnumerable<string> Under(string directory, string? extension = null)
    {
        var full = Path.IsPathRooted(directory)
            ? Path.GetFullPath(directory)
            : Path.GetFullPath(Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && (extension is null || HasExtension(f, extension)));
    }

    /// <summary>The repository-relative path of <paramref name="absolutePath"/>, with forward slashes.</summary>
    public static string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');

    private static bool HasExtension(string file, string extension) =>
        Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find the repository root (the directory holding cc-director.sln) above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Lists the files git knows about under <paramref name="root"/>: tracked, plus new files that are neither
    /// added nor ignored, minus those no longer on disk. Absolute paths, platform separators.
    /// </summary>
    internal static IReadOnlyList<string> ListFiles(string root)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
            startInfo.ArgumentList.Add(argument);

        using var git = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git did not start; the repository source index needs git on the PATH");
        var errorText = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-files failed in {root} (exit code {git.ExitCode}): {errorText.Result.Trim()}");
        }

        var files = new List<string>();
        foreach (var relative in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) files.Add(full);
        }

        if (files.Count == 0)
            throw new InvalidOperationException($"git ls-files listed no files under {root}; is it a git working tree?");
        return files;
    }
}
