using CcDirector.Gateway.Contracts;
using CcDirector.StateAgreementCheck;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE TOOL'S OWN VERDICT, NOT JUST ITS CHECKER - the composition in
/// <see cref="CcDirector.StateAgreementCheck.Program.RunAsync"/> that turns a tree disagreement into a failing process exit.
///
/// WHY THIS EXISTS. <see cref="TreeAgreementTests"/> exercises <see cref="TreeAgreement.Check"/> and
/// watches it report faults, and every one of those tests stayed green when an independent inspection
/// of this slice deleted the arm that ACTS on the answer - replacing the composed return with
/// `return exitCode;`. With that substitution the tool prints a tree failure in full and still exits
/// zero whenever the separate live-session comparison happens to be clean, so a script gating on the
/// exit code is gated on nothing. A checker that reports correctly into a process that ignores it is
/// the same defect as no checker.
///
/// The live Gateway is the one thing this tool cannot reach in a test, so it is passed in. Everything
/// else here is the real thing: the real shared fixture file, the real C# fold, the real
/// <see cref="AgreementCheck.Summarize"/> arithmetic.
/// </summary>
public sealed class AgreementCheckExitCodeTests
{
    /// <summary>A fleet that is clean by construction: no sessions, so no live finding can exist and the
    /// live half of the verdict is exactly zero. Any non-zero exit therefore came from the tree.</summary>
    private static Func<Task<IReadOnlyList<SessionDto>>> ACleanFleet() =>
        () => Task.FromResult<IReadOnlyList<SessionDto>>(Array.Empty<SessionDto>());

    private static readonly IReadOnlyDictionary<string, string> APalette =
        new Dictionary<string, string> { ["blue"] = "#000000" };

    [Fact]
    public async Task ATreeDisagreement_FailsTheRun_EvenWhenEveryLiveSessionAgrees()
    {
        using var repo = new RepoRootWithABrokenSharedFile();

        var exitCode = await CcDirector.StateAgreementCheck.Program.RunAsync(repo.Path, APalette, ACleanFleet());

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ACleanTreeAndACleanFleet_IsTheOnlyWayToZero()
    {
        var exitCode = await CcDirector.StateAgreementCheck.Program.RunAsync(TestRepoRoot.Path, APalette, ACleanFleet());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AFleetThatDisagrees_FailsTheRun_WhateverTheTreeSays()
    {
        // The control for the test above: the live half must still be able to fail on its own, so a
        // reader cannot mistake the tree arm for the whole verdict.
        var unstamped = new[] { new SessionDto { SessionId = "u", Name = "a session the Gateway did not stamp" } };

        var exitCode = await CcDirector.StateAgreementCheck.Program.RunAsync(
            TestRepoRoot.Path, APalette, () => Task.FromResult<IReadOnlyList<SessionDto>>(unstamped));

        Assert.Equal(1, exitCode);
    }

    /// <summary>
    /// A throwaway repository root carrying nothing but the shared tree answers, with one answer changed
    /// so the real C# fold must disagree with it. The real file is the source, so this cannot drift into
    /// a private fixture that agrees with itself.
    /// </summary>
    private sealed class RepoRootWithABrokenSharedFile : IDisposable
    {
        public string Path { get; }

        public RepoRootWithABrokenSharedFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tree-agreement-exit-" + Guid.NewGuid().ToString("N"));
            var file = System.IO.Path.Combine(Path, TreeAgreement.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

            var source = File.ReadAllText(System.IO.Path.Combine(TestRepoRoot.Path,
                TreeAgreement.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            const string find = "\"roots\": [\"100\", \"103\", \"108\", \"112\"]";
            const string replace = "\"roots\": [\"103\", \"100\", \"108\", \"112\"]";
            var occurrences = source.Split(find).Length - 1;
            Assert.True(occurrences == 1,
                $"The text to mutate appears {occurrences} times in the shared file, not once - this injection would prove nothing.");
            File.WriteAllText(file, source.Replace(find, replace));
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temporary directory that outlives the run is not a test failure */ }
        }
    }
}

/// <summary>The real repository root, found the same way the tool itself finds it.</summary>
internal static class TestRepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, "packages"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
