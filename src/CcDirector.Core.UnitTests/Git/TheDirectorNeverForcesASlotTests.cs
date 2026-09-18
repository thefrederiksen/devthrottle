using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// A SLOT WHOSE HOLDER IS GONE IS NOT THE DIRECTOR'S TO TAKE.
///
/// After a Director has stopped, its slots are still in use in the pool under holders that no longer
/// exist. The obliging thing to do about that is to take them back - and cc-worktrees has the
/// commands for it: <c>lease --reclaim-held</c> takes a held slot as it is, <c>release</c> puts one
/// back and REMOVES ITS DIRECTORY, and <c>destroy --allow-held --allow-in-use</c> removes it outright.
/// Each of those is somebody deciding to give up what is in that directory, and it is never a restart
/// that decides it.
///
/// So the Director's whole vocabulary is <c>get</c>, <c>return</c> and reading the tool's records. It
/// never reclaims, never releases, never resets and never destroys, and a session restored after a
/// restart re-attaches the lease it already had rather than taking anything.
///
/// This is a SOURCE sweep rather than a behaviour test on purpose. The behaviour tests each watch one
/// path; a forcing flag added to a path nobody wrote a test for would pass all of them. What is
/// checked here is that the words do not appear at all in the two files that can run the tool.
/// </summary>
public sealed class TheDirectorNeverForcesASlotTests
{
    /// <summary>Every way of telling cc-worktrees to take a slot that is not being given up.</summary>
    public static TheoryData<string> ForcingArguments() => new()
    {
        "--reclaim-held",
        "release",
        "--confirm-abandon",
        "destroy",
        "--allow-held",
        "--allow-in-use",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The files that can reach cc-worktrees at all: the one that runs it, and the one that decides
    /// when to. Comments are stripped first, because this file's own subject has to be describable in
    /// prose right beside the code - and a sweep that reads the explanation as if it were an
    /// instruction would either fail on honest prose or have to be weakened until it caught nothing.
    /// </summary>
    private static IEnumerable<(string Name, string Code)> TheFilesThatCanRunTheTool()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("src", "CcDirector.Core", "Git", "CcWorktreesPool.cs"),
                     Path.Combine("src", "CcDirector.Core", "Sessions", "SessionManager.cs"),
                 })
        {
            var path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), $"{relative} is not where this sweep expects it; the sweep is now reading nothing.");
            yield return (relative, StripComments(File.ReadAllText(path)));
        }
    }

    /// <summary>Removes block and line comments, and the contents of string literals' neighbours be damned:
    /// a forcing flag is only dangerous where it is an argument, and an argument is a string literal.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*//.*$", "", RegexOptions.Multiline);
        return source;
    }

    [Theory]
    [MemberData(nameof(ForcingArguments))]
    public void TheDirectorNeverPassesAForcingArgumentToCcWorktrees(string forcing)
    {
        foreach (var (name, code) in TheFilesThatCanRunTheTool())
        {
            Assert.DoesNotContain($"\"{forcing}\"", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSweepIsReadingTheRealFilesAndCanTell()
    {
        // The control, because a sweep whose pass condition is an absence certifies a run that never
        // happened just as happily as a clean one. These two strings ARE in the code, as arguments,
        // so a sweep reading empty text would fail here.
        var files = TheFilesThatCanRunTheTool().ToList();
        Assert.Equal(2, files.Count);
        var pool = files.Single(f => f.Name.EndsWith("CcWorktreesPool.cs", StringComparison.Ordinal)).Code;
        Assert.Contains("\"get\"", pool, StringComparison.Ordinal);
        Assert.Contains("\"return\"", pool, StringComparison.Ordinal);
        Assert.Contains("\"--lease\"", pool, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommentsAreStrippedSoTheRuleCanBeExplainedBesideTheCode()
    {
        // And the control for THAT: the words must still be findable in the raw file, or this sweep is
        // passing because the explanation was deleted rather than because the code is clean.
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Core", "Sessions", "SessionManager.cs"));
        Assert.Contains("reclaim-held", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("reclaim-held", StripComments(raw), StringComparison.Ordinal);
    }
}
