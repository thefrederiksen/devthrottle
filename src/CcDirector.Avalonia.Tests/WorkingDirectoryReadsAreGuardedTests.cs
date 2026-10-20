using CcDirector.Core.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// A process is allowed to have no readable working directory, and on macOS the Director regularly
/// does: the updater replaces the application bundle this process is sitting in, the Director restarts
/// into a working directory that has just been deleted, and <c>getcwd</c> fails from then on.
///
/// Measured on 2026-10-19 on the owner's Mac: minutes after a self-update, clicking Settings threw
/// <c>FileNotFoundException at Interop.Sys.GetCwd()</c> out of
/// <c>SkillToolLinker.DefaultSkillRoots</c> - an UNHANDLED user-interface exception, so the Settings
/// dialog never opened and nothing was shown to say why. The owner reported it as "Settings is not
/// working on this Mac". Windows cannot reach the state at all, because it refuses to delete a running
/// process's working directory, so the unguarded read looks perfectly safe to write and to review.
///
/// Nothing in the test suite can see this: it needs a deleted working directory, which a gate test is
/// not allowed to depend on (and mutating the process's working directory mid-run would break whatever
/// else is running). So the guard is on the SOURCE instead - every read goes through
/// <see cref="ProcessWorkingDirectory"/>, which answers null rather than throwing, and this fails on a
/// new direct read anywhere in the Director's own code.
/// </summary>
public sealed class WorkingDirectoryReadsAreGuardedTests
{
    // The projects whose code runs inside the Director and the launcher - the processes that can be
    // left without a working directory by an update.
    private static readonly string[] ScannedProjects =
    {
        "src/CcDirector.Avalonia",
        "src/CcDirector.Core",
        "src/CcDirector.Launcher",
    };

    // The helper itself is the one place allowed to read it directly - that is its whole job.
    private const string Helper = "src/CcDirector.Core/Utilities/ProcessWorkingDirectory.cs";

    private static readonly string[] DirectReads = { "Environment.CurrentDirectory", "Directory.GetCurrentDirectory()" };

    private readonly ITestOutputHelper _output;

    public WorkingDirectoryReadsAreGuardedTests(ITestOutputHelper output) => _output = output;

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"could not find the repository root above {AppContext.BaseDirectory}; this test reads the source tree");
    }

    [Fact]
    public void NoDirectWorkingDirectoryRead_OutsideTheOneHelperThatGuardsIt()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        int filesRead = 0;

        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project);
            Assert.True(Directory.Exists(dir), $"scanned project not found: {dir}");

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative == Helper) continue;

                filesRead++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var read in DirectReads)
                    {
                        if (lines[i].Contains(read, StringComparison.Ordinal))
                            offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
                    }
                }
            }
        }

        // An empty scan would pass this test while proving nothing at all, so the scan itself is checked.
        _output.WriteLine($"read {filesRead} source files across {ScannedProjects.Length} projects");
        Assert.True(filesRead > 100, $"the scan read only {filesRead} files, so it cannot have covered the Director");

        Assert.True(offenders.Count == 0,
            "the working directory must be read through ProcessWorkingDirectory, which answers null instead of "
            + "throwing when the operating system will not name it (the Director loses its working directory on "
            + "every macOS self-update). Direct reads found:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void CurrentOrAppDirectory_AlwaysNamesADirectoryThatExists()
    {
        // Whatever the working directory is doing, a caller that must hand a directory to a child
        // process gets one that is really there.
        var directory = ProcessWorkingDirectory.CurrentOrAppDirectory();

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Directory.Exists(directory), $"CurrentOrAppDirectory returned a directory that does not exist: {directory}");
    }
}
