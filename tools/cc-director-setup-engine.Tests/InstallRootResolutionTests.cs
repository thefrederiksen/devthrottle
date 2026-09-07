using CcDirector.Core.Storage;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Where the installer decides to put everything.
///
/// <c>InstallLayout.Default</c> used to compute the root itself:
///
/// <code>
/// var localAppData = Environment.GetFolderPath(SpecialFolder.LocalApplicationData);
/// localRoot = Path.Combine(localAppData, "cc-director");
/// </code>
///
/// On Linux that folder call returns an EMPTY STRING when <c>~/.local/share</c> does not exist -
/// the state of every fresh account. <c>Path.Combine</c> does not fail on an empty first segment;
/// it returns the RELATIVE path <c>"cc-director"</c>, which resolves against the process working
/// directory. So a first install on a clean desktop wrote the user's install tree beside the setup
/// executable, and nothing complained.
///
/// The same defect was fixed in <c>CcStorage</c> by Phase 1 of this mission. This half was missed,
/// and the comment on this method claimed parity with <c>CcStorage</c> the whole time - a stale
/// claim of correctness being worse than no claim, because it spends the next reader's scepticism
/// somewhere else.
/// </summary>
public sealed class InstallRootResolutionTests
{
    /// <summary>
    /// One per-user root, one implementation of it. The installer asks <see cref="CcStorage"/>
    /// rather than deriving its own answer - two implementations of one path cannot stay equal, and
    /// the proof of that is that these two DID diverge, for exactly as long as it took Phase 1 to
    /// fix one of them.
    /// </summary>
    [Fact]
    public void Default_UsesTheSameRootAsTheDirectorItself()
        => Assert.Equal(CcStorage.Root(), InstallLayout.Default().LocalRoot);

    /// <summary>
    /// The root the old code produced on a fresh Linux account, rejected by name. This is the
    /// literal string <c>Path.Combine("", "cc-director")</c> returns.
    /// </summary>
    [Fact]
    public void Constructor_RejectsTheRelativeRootTheOldCodeProduced()
    {
        var ex = Assert.Throws<ArgumentException>(() => new InstallLayout("cc-director"));
        Assert.Contains("relative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cc-director")]
    [InlineData("some/nested/relative/path")]
    [InlineData("./cc-director")]
    [InlineData("..")]
    public void Constructor_RejectsAnyRelativeRoot(string relative)
        => Assert.Throws<ArgumentException>(() => new InstallLayout(relative));

    /// <summary>
    /// The guard that already existed, kept. It was not wrong - it was insufficient: the defect
    /// produced "cc-director", which is neither null nor whitespace, so it walked straight past.
    /// </summary>
    [Fact]
    public void Constructor_StillRejectsBlankRoots()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout(""));
        Assert.Throws<ArgumentException>(() => new InstallLayout("   "));
        Assert.Throws<ArgumentException>(() => new InstallLayout("\t"));
    }

    [Fact]
    public void Constructor_AcceptsAnAbsoluteRoot()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "cc-install-root-test");
        Assert.Equal(absolute, new InstallLayout(absolute).LocalRoot);
    }

    /// <summary>
    /// Every path the installer writes hangs off the root, so an absolute root makes all of them
    /// absolute. This is the property the defect broke wholesale rather than in one place.
    /// </summary>
    [Fact]
    public void EveryDirectoryUnderAnAbsoluteRoot_IsItselfAbsolute()
    {
        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), "cc-install-root-test"));

        foreach (var dir in new[] { layout.AppDir, layout.BinDir, layout.PythonDir, layout.PyenvDir, layout.StateDir, layout.LogsDir })
            Assert.True(Path.IsPathRooted(dir), $"{dir} is not absolute");
    }
}

/// <summary>
/// Architecture fitness function: the installer does not work out the per-user root for itself.
///
/// This is the mutation guard for the fix above. On Windows the old code and the new code agree -
/// <c>%LOCALAPPDATA%\cc-director</c> either way - so no equality assertion can tell them apart from
/// a Windows test run. What CAN be told apart is whether the derivation is present in the source at
/// all. Putting <c>GetFolderPath(LocalApplicationData)</c> back into the installer fails this test
/// immediately, on any platform.
///
/// It is the same shape as <c>PlatformAssetSelectionGuardTests</c>, and for the same reason: this
/// mission has now found the identical mechanism in four files, and three of those were found only
/// because somebody went looking after the first was reported.
/// </summary>
public sealed class InstallRootDerivationGuardTests
{
    private static readonly string[] ScannedDirectories =
    {
        "tools/cc-director-setup-engine",
        "tools/cc-director-setup-avalonia",
        "tools/cc-director-setup-cli",
    };

    private const string ForbiddenDerivation = "SpecialFolder.LocalApplicationData";

    /// <summary>
    /// KNOWN AND DELIBERATELY NOT FIXED. <c>SetupLog</c>'s static constructor derives its own log
    /// directory the same way - <c>Path.Combine(localAppData, "cc-director", "logs", "setup")</c>,
    /// then <c>Directory.CreateDirectory</c> - so on a fresh Linux account the wizard writes its own
    /// log to a RELATIVE <c>cc-director/logs/setup</c> beside the setup executable, and honours no
    /// CC_DIRECTOR_ROOT override at all. It does not crash; it just logs where nobody will look,
    /// which matters precisely when somebody is trying to work out why an install went wrong.
    ///
    /// It is listed here rather than quietly excluded from the sweep, so that this exemption is the
    /// record of it. It is a named Phase B item; deleting this entry is what closing it looks like.
    /// </summary>
    private static readonly string[] KnownUnfixed =
    {
        "tools/cc-director-setup-avalonia/Services/SetupLog.cs",
    };

    [Fact]
    public void No_setup_file_derives_the_per_user_root_for_itself()
    {
        var root = GetRepoRoot();
        var offenders = new List<string>();

        foreach (var file in ScannedFiles(root))
        {
            var rel = Relative(root, file);
            if (KnownUnfixed.Contains(rel, StringComparer.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;   // prose, including the explanations above
                if (!lines[i].Contains(ForbiddenDerivation, StringComparison.Ordinal)) continue;
                offenders.Add($"{rel}:{i + 1}: {code}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The installer is deriving the per-user root itself instead of asking CcStorage.Root(). "
            + "On Linux that call returns an empty string on a fresh account, Path.Combine turns it "
            + "into a relative path, and the install lands beside the setup executable:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The exemption above must name a file that EXISTS and that still carries the derivation. An
    /// exemption for a file that has since been fixed, renamed or deleted is a hole in the sweep
    /// that reads exactly like a clean result.
    /// </summary>
    [Fact]
    public void The_known_unfixed_exemption_still_describes_something_real()
    {
        var root = GetRepoRoot();

        foreach (var rel in KnownUnfixed)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"exempted file {rel} no longer exists - remove the exemption");
            Assert.Contains(ForbiddenDerivation, File.ReadAllText(full), StringComparison.Ordinal);
        }
    }

    /// <summary>The sweep must actually be reading files; an empty sweep passes trivially.</summary>
    [Fact]
    public void The_guard_actually_reads_the_setup_surface()
    {
        var root = GetRepoRoot();
        var files = ScannedFiles(root).Select(f => Relative(root, f)).ToList();

        Assert.Contains("tools/cc-director-setup-engine/InstallLayout.cs", files);
        Assert.Contains("tools/cc-director-setup-avalonia/Services/SetupLog.cs", files);
        Assert.True(files.Count > 20, $"only {files.Count} files scanned; the sweep is not reaching the setup surface");
    }

    private static IEnumerable<string> ScannedFiles(string root) =>
        ScannedDirectories
            .Select(d => Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f =>
            {
                var rel = Relative(root, f);
                return !rel.Contains("/bin/", StringComparison.Ordinal)
                       && !rel.Contains("/obj/", StringComparison.Ordinal);
            })
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
