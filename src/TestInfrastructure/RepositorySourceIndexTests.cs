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
}
