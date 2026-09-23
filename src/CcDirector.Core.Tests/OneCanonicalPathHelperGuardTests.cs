using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// ONE canonical-path helper for the whole test tree, and a guard that keeps it that way.
///
/// WHAT WENT WRONG. <see cref="TestTempRoot"/> exists because macOS answers
/// <c>Path.GetTempPath()</c> with <c>/var/folders/...</c> while <c>/var</c> is a symbolic link to
/// <c>/private/var</c>, so a fixture built from the raw temporary path and compared against what git
/// or the product reports is comparing two spellings of the same directory. Three suites needed that
/// and none of them used it. Each wrote its own private <c>RealPath</c> instead, and all three wrote
/// the SAME wrong thing: a recursion that walks UP the path calling <c>ResolveLinkTarget</c> on every
/// ancestor, all the way to the volume root.
///
/// Windows throws <c>DirectoryNotFoundException</c> when asked to resolve <c>"C:\"</c>. So every test
/// that went through one of those helpers passed on macOS, where the walk reaches <c>/</c> harmlessly,
/// and failed on the Windows build machine - seventeen tests across three suites, in three separate
/// places, from one idea copied three times. They were part of a continuous integration run that had
/// been red for long enough that nobody read it any more.
///
/// WHY A GUARD AND NOT JUST THE FIX. The fix is three deletions, and nothing stops a fourth suite
/// writing the same helper next month - the reasoning that produced it was sound, only the ending was
/// wrong. So the rule is stated as something the build can check.
///
/// WHAT THE RULE ACTUALLY FORBIDS, and this took a correction to get right. The first version of this
/// guard banned every mention of <c>ResolveLinkTarget</c> in test code, and it named five files: the
/// three real offenders, its own prose, and <c>RulePrimitivesTests</c> - which calls it perfectly
/// legitimately, to assert that a link it has just created really is a link before testing the
/// product against it. A guard that calls correct code an offence gets suppressed, and then it is
/// worth nothing.
///
/// The hazard is not the call. It is the DIRECTION OF THE WALK. Resolving a component and then
/// stepping UP to its parent arrives, eventually, at the volume root - and that is the one argument
/// Windows refuses. <see cref="TestTempRoot"/> walks the other way: it splits the path and builds
/// DOWN from the root, so the root is its starting point and never something it asks about. So the
/// rule is: a test file may resolve links, and may take a path apart, but may not do both - because
/// together they are the walk that ends at the root.
///
/// Compare <see cref="TestProjectPath"/>, which exists for the same reason after four copies of one
/// predicate were all wrong at once.
/// </summary>
public class OneCanonicalPathHelperGuardTests
{
    /// <summary>The one file allowed to walk a path apart AND resolve links, relative to the
    /// repository root and spelled with forward slashes.</summary>
    private const string TheSharedHelper = "src/CcDirector.Core.Tests/TestTempRoot.cs";

    /// <summary>This guard names both of the constructions it searches for, so it matches itself.
    /// It is excluded by path rather than by making the search cleverer, because a guard whose
    /// needles are assembled at runtime to dodge its own scan is a guard nobody can read.</summary>
    private const string ThisGuard = "src/CcDirector.Core.Tests/OneCanonicalPathHelperGuardTests.cs";

    [Fact]
    public void NoTestFile_ResolvesLinksWhileWalkingUpThePath()
    {
        var root = GetRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Relative(root, file);
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            if (!TestProjectPath.IsTestProject(rel)) continue;   // production code is not this rule's business
            if (rel == TheSharedHelper || rel == ThisGuard) continue;

            var text = File.ReadAllText(file);
            bool resolvesLinks = text.Contains("ResolveLinkTarget(", StringComparison.Ordinal);
            bool stepsUpThePath = text.Contains("Path.GetDirectoryName(", StringComparison.Ordinal);
            if (resolvesLinks && stepsUpThePath)
                offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "These test files resolve symbolic links AND step up the path, which is the walk that ends "
            + "at the volume root - and Windows throws DirectoryNotFoundException when asked to resolve "
            + "one. Three copies of exactly that shape passed on macOS and failed every one of their "
            + "tests on the build machine. Call TestTempRoot.Canonical, which builds DOWN from the root "
            + "instead:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The volume root is the shape that crashed the three copies, so the shared helper is held to it
    /// directly. <c>Canonical</c> must answer for the root of the drive the tests actually run on -
    /// <c>/</c> on macOS and Linux, <c>C:\</c> or whatever the checkout sits on, on Windows.
    /// </summary>
    [Fact]
    public void Canonical_GivenTheVolumeRoot_AnswersWithoutThrowing()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory));
        Assert.False(string.IsNullOrEmpty(volumeRoot));

        var answer = TestTempRoot.Canonical(volumeRoot!);

        Assert.False(string.IsNullOrEmpty(answer));
    }

    /// <summary>
    /// A path whose ancestors exist but whose leaf does not. Several of the suites that used the
    /// hand-rolled helpers delete a repository and then ask what its path resolves to, so a helper
    /// that throws on a missing folder would have failed them too.
    /// </summary>
    [Fact]
    public void Canonical_GivenAPathThatDoesNotExist_AnswersWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "nor-this");

        var answer = TestTempRoot.Canonical(missing);

        Assert.False(string.IsNullOrEmpty(answer));
    }

    /// <summary>
    /// The helper's whole purpose: the temporary root it hands out must already be the spelling other
    /// tools report back, so a fixture built on it compares equal to what git answers. Asking it to
    /// canonicalise its own answer must change nothing - if it does, the answer it gave out was not
    /// canonical.
    /// </summary>
    [Fact]
    public void For_AnswersAPathThatIsAlreadyCanonical()
    {
        var root = TestTempRoot.For("one-canonical-path-helper-guard-");

        Assert.Equal(root, TestTempRoot.Canonical(root));
    }

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
