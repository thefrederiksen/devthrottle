using System.Text.RegularExpressions;
using CcDirector.Core.Git;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Architecture;

/// <summary>
/// EVERY GIT THE DIRECTOR STARTS RUNS WITHOUT OPTIONAL LOCKS, EXCEPT A NAMED WRITER (issue #2872).
///
/// A plain <c>git status</c> writes the refreshed index back and holds <c>index.lock</c> while it does. The
/// Director kills a superseded read's git process tree, a killed git cannot remove its lock, and the 0-byte
/// lock it leaves stops every later commit, pull, checkout, rebase and worktree removal in that checkout with
/// "File exists" - while status keeps working, so nothing shows it. On the owner's machine that froze a main
/// checkout for six weeks and left 26 more locks in session worktrees. <see cref="GitOptionalLocks"/> sets
/// <c>GIT_OPTIONAL_LOCKS=0</c>, which removes the write and so the lock.
///
/// This guard reads the production SOURCE, so it runs in the default gate and needs no git: any file that
/// starts git must also use <see cref="GitOptionalLocks"/>, or be on the short list below with its reason. A
/// git launch added later in a new file fails here until it does one or the other. The behaviour itself - that
/// the readers really leave the index untouched - is proved against real git in CcDirector.Core.GitTests,
/// which is outside the gate because it starts processes.
///
/// WHAT IT CANNOT SEE. It is file-granular and shape-limited: a file with one covered launch passes even if a
/// second launch in it lacks the setting, and it does not recognise <c>Process.Start("git", ...)</c>, a file name
/// held in a variable, <c>"git.exe"</c>, or git started by another tool. That last case is real -
/// <c>CcWorktreesPool</c> runs cc-worktrees, which runs git status in slot worktrees - so files like it are named
/// in <see cref="KnownIndirectLaunchers"/> and must carry the setting too. "Passes" means every launch shape it
/// knows is covered, not that every git on the machine is.
///
/// It is a guard against an ABSENCE (a launch without the setting), so it also demands presence: it must find
/// the launch sites it knows about, and every allowed file must still exist and still start git. A scan that
/// found nothing because the files moved would otherwise pass.
/// </summary>
public sealed class BackgroundGitLaunchesTakeNoOptionalLocksTests
{
    /// <summary>The ways production code here starts git: by name, or by a runner whose default executable is git.</summary>
    private static readonly Regex GitLaunch = new(
        @"FileName\s*=\s*""git""|ProcessStartInfo\s*\(\s*""git""|ProcessRunner\.Run\w*\s*\(\s*""git""|:\s*this\s*\(\s*""git""\s*\)",
        RegexOptions.Compiled);

    /// <summary>Files that start git and deliberately do NOT turn optional locks off, each with why.</summary>
    private static readonly Dictionary<string, string> AllowedWithoutTheSetting = new(StringComparer.Ordinal)
    {
        // Every command it runs is a write the user asked for (add, reset, checkout, commit). Optional locks
        // do not apply to a write, and the issue was explicit that commands meant to write are not changed.
        ["src/CcDirector.Core/Git/GitWriteService.cs"] = "user-requested writes",
        // The delivery-qualification rig builds its own throwaway repositories and worktrees; it is not a
        // background read and never runs inside a session's checkout.
        ["src/CcDirector.DeliveryQualification/Rig.cs"] = "test rig that creates its own repositories",
    };

    /// <summary>The background readers named in issue #2872. Each must be found AND covered.</summary>
    private static readonly string[] KnownReaders =
    {
        "src/CcDirector.Core/Git/GitCommandRunner.cs",      // worktree inventory, worktree reaper, branch/diff/history services
        "src/CcDirector.Core/Git/GitStatusProvider.cs",
        "src/CcDirector.Core/Git/GitSyncStatusProvider.cs",
        "src/CcDirector.Core/Wingman/WingmanService.cs",
    };

    /// <summary>Files that start a TOOL which itself runs git reads, inheriting this process's environment.</summary>
    private static readonly string[] KnownIndirectLaunchers =
    {
        "src/CcDirector.Core/Git/CcWorktreesPool.cs",       // cc-worktrees: git status in slot worktrees, killed on timeout
    };

    [Fact]
    public void ToolsThatRunGit_AreStartedWithOptionalLocksOff()
    {
        foreach (var rel in KnownIndirectLaunchers)
        {
            var path = Path.Combine(RepositorySourceIndex.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{rel} is listed as starting a tool that runs git, but it is gone. Update the list.");
            Assert.True(StripComments(File.ReadAllText(path)).Contains("GitOptionalLocks.Off", StringComparison.Ordinal),
                $"{rel} starts a tool that runs git status and can be killed, without GitOptionalLocks.Off (issue #2872).");
        }
    }

    [Fact]
    public void EveryGitLaunch_TurnsOptionalLocksOff_OrIsANamedWriter()
    {
        var launchers = ProductionSources()
            .Where(f => GitLaunch.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(RepositorySourceIndex.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var reader in KnownReaders)
            Assert.Contains(reader, launchers);
        foreach (var allowed in AllowedWithoutTheSetting.Keys)
            Assert.True(launchers.Contains(allowed), $"{allowed} is allowed to start git without GitOptionalLocks but no longer starts git (or moved). Remove it from the list.");

        var uncovered = launchers
            .Where(p => !AllowedWithoutTheSetting.ContainsKey(p))
            .Where(p => !StripComments(File.ReadAllText(Path.Combine(RepositorySourceIndex.Root, p))).Contains("GitOptionalLocks.", StringComparison.Ordinal))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These files start git without GitOptionalLocks (issue #2872). A killed git status leaves index.lock behind " +
            "for good and blocks every write in that checkout. Call GitOptionalLocks.ApplyTo(startInfo), or pass " +
            "GitOptionalLocks.Off to ProcessRunner; if the file only WRITES, add it to AllowedWithoutTheSetting with why:\n  " +
            string.Join("\n  ", uncovered));
    }

    [Fact]
    public void Off_IsTheVariableGitReads_SetToZero()
    {
        Assert.Equal("GIT_OPTIONAL_LOCKS", GitOptionalLocks.VariableName);
        Assert.Equal("0", GitOptionalLocks.Off[GitOptionalLocks.VariableName]);
        Assert.Single(GitOptionalLocks.Off);
    }

    /// <summary>Production C# under src, excluding test projects and test infrastructure.</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var files = RepositorySourceIndex.Under("src", ".cs")
            .Where(f =>
            {
                var rel = RepositorySourceIndex.Relative(f);
                var project = rel.Split('/')[1];
                return !project.EndsWith("Tests", StringComparison.Ordinal)
                       && !project.Contains(".Tests.", StringComparison.Ordinal)
                       && project != "TestInfrastructure";
            })
            .ToList();
        Assert.True(files.Count > 500, $"Read only {files.Count} production source files under src. A guard that reads an empty tree has checked nothing.");
        return files;
    }

    private static string StripComments(string source)
        => Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
}
