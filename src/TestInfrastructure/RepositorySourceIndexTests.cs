#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// The shared source index lists what the repository holds - tracked files and new files - and nothing that
/// git ignores. Compiled into one assembly: it builds a real git repository, and running that in every test
/// assembly buys no coverage.
/// </summary>
public sealed class RepositorySourceIndexTests
{
    [Fact]
    public void Root_is_the_directory_holding_the_solution_file()
    {
        Assert.True(File.Exists(Path.Combine(RepositorySourceIndex.Root, "cc-director.sln")),
            "the root should hold cc-director.sln, found " + RepositorySourceIndex.Root);
    }

    [Fact]
    public void ListFiles_holds_tracked_and_new_files_and_leaves_out_ignored_and_deleted_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-source-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Git(root, "init", "-q");
            Git(root, "config", "user.email", "test@example.com");
            Git(root, "config", "user.name", "Test");
            Git(root, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\n");
            Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
            File.WriteAllText(Path.Combine(root, "src", "tracked.cs"), "// tracked and committed");
            File.WriteAllText(Path.Combine(root, "src", "deleted.cs"), "// committed, then removed from disk");
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", "fixture");

            File.WriteAllText(Path.Combine(root, "src", "new.cs"), "// written, never added");
            File.WriteAllText(Path.Combine(root, "src", "bin", "ignored.cs"), "// a build output");
            File.Delete(Path.Combine(root, "src", "deleted.cs"));

            var files = RepositorySourceIndex.ListFiles(root)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { ".gitignore", "src/new.cs", "src/tracked.cs" }, files);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void ListFiles_returns_absolute_paths_in_the_platforms_separators()
    {
        var file = RepositorySourceIndex.Files.First();
        Assert.True(Path.IsPathRooted(file), file);
        Assert.StartsWith(RepositorySourceIndex.Root, file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, file.Substring(RepositorySourceIndex.Root.Length));
    }

    [Fact]
    public void Under_returns_the_files_below_that_directory_with_that_extension_and_nothing_else()
    {
        var files = RepositorySourceIndex.Under("src/CcDirector.Core", ".cs").ToList();

        Assert.Contains(files, f => RepositorySourceIndex.Relative(f) == "src/CcDirector.Core/Sessions/InheritedSessionEnvironment.cs");
        Assert.All(files, f => Assert.StartsWith("src/CcDirector.Core/", RepositorySourceIndex.Relative(f), StringComparison.Ordinal));
        Assert.All(files, f => Assert.EndsWith(".cs", f, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => RepositorySourceIndex.Relative(f).StartsWith("src/CcDirector.Core.Tests/", StringComparison.Ordinal));

        var absolute = RepositorySourceIndex.Under(Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.Core"), ".cs").ToList();
        Assert.Equal(files, absolute);
    }

    [Fact]
    public void Under_a_directory_that_does_not_exist_throws_so_a_guard_cannot_pass_by_scanning_nothing()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => RepositorySourceIndex.Under("src/No.Such.Project", ".cs").ToList());
        Assert.Contains("No.Such.Project", ex.Message);
    }

    [Fact]
    public void The_list_is_built_once_per_process()
    {
        Assert.Same(RepositorySourceIndex.Files, RepositorySourceIndex.Files);
    }

    /// <summary>git writes its object files read-only, which a plain recursive delete refuses on Windows.</summary>
    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var git = Process.Start(startInfo)!;
        var error = git.StandardError.ReadToEnd();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
