using CcDirector.Core.Utilities;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening (Phase 5a, inspection finding I1-02): the file-containment decisions in
/// <see cref="ControlEndpoints"/> used to be LEXICAL - a normalized-string prefix test - so a symbolic
/// link or directory junction planted UNDER an allowed root kept a perfectly legal prefix while the
/// actual read followed the link anywhere on disk (Path.GetFullPath never touches the filesystem).
/// The comparisons were also unconditionally case-insensitive, which on a case-sensitive filesystem
/// accepts a case-variant path as though it were inside the root.
///
/// These tests plant REAL links on disk - directory junctions and symbolic links, never a simulation -
/// and prove the escape is refused at all three sites (<see cref="ControlEndpoints.ResolveSessionFile"/>,
/// <see cref="ControlEndpoints.ListDirectory"/>, <see cref="ControlEndpoints.ResolveScreenshot"/>),
/// prove that legal links which stay INSIDE the boundary keep working (a containment fix must not
/// refuse the legitimate namespace), and pin the platform-correct case comparison through the pure
/// decision function so both platform branches are testable on any one build machine.
///
/// A host that cannot create any real link FAILS these tests loudly instead of skipping them - a
/// silently skipped security regression test reads as coverage it does not provide. On Windows a
/// directory JUNCTION needs no privilege at all; a FILE symbolic link needs Developer Mode or
/// elevation, so the file-link test says exactly that if it cannot run.
///
/// In the "DirectorRoot" collection: the screenshot tests redirect CC_DIRECTOR_ROOT to an isolated
/// temp root, which is process-global state.
/// </summary>
[Collection("DirectorRoot")]
public sealed class PathContainmentLinkEscapeTests : IDisposable
{
    private readonly string _base;
    private readonly string? _prevRoot;
    private readonly string _root;     // the allowed root (a session working directory stand-in)
    private readonly string _outside;  // a sibling OUTSIDE the allowed root
    private readonly string _secret;   // an existing file outside the root (the stand-in for a token/key file)

    public PathContainmentLinkEscapeTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _base = Path.Combine(Path.GetTempPath(), "ccd-linkescape-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _base);

        _root = Path.Combine(_base, "session-repo");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _secret = Path.Combine(_outside, "gateway-token.txt");
        File.WriteAllText(_secret, "the-secret");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        // Directory.Delete(recursive) removes reparse points without following them, so the
        // outside targets survive until the whole base dir goes.
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------- real-link creation ----

    /// <summary>
    /// Create a REAL directory link on disk. A symbolic link is tried first (works unprivileged on
    /// Unix, and on Windows with Developer Mode); a Windows host without that privilege falls back
    /// to a directory JUNCTION, which never needs elevation. If no real link can be created the
    /// test FAILS LOUDLY - it must never silently skip into a false green.
    /// </summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail($"Could not create a directory symbolic link on this Unix host ({ex.Message}). " +
                            "This security regression test needs a REAL link and must not be skipped.");
                return;
            }
        }

        // No symbolic-link right on this Windows host: a junction, made in this process, which throws
        // rather than skip when it cannot be made.
        DirectoryJunction.Create(link, target);
    }

    /// <summary>
    /// Create a REAL file symbolic link. There is no unprivileged Windows fallback for a FILE link
    /// (junctions are directory-only), so a host without Developer Mode or elevation fails loudly
    /// with the reason, never skips.
    /// </summary>
    private static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"This host cannot create a FILE symbolic link ({ex.Message}). On Windows that needs " +
                        "Developer Mode or elevation. The link-escape regression cannot be proven without a real " +
                        "link, so this test fails loudly instead of silently skipping into a false green.");
        }
    }

    // ------------------------------------------------ ResolveSessionFile: links must not escape ----

    [Fact]
    public void ResolveSessionFile_linkedDirectoryUnderTheRootEscapingIt_isRefused()
    {
        // A directory link INSIDE the working directory pointing OUTSIDE it. The candidate path has a
        // perfectly legal lexical prefix; only the real filesystem namespace crosses the boundary.
        var link = Path.Combine(_root, "innocent-subdir");
        CreateDirectoryLink(link, _outside);
        var candidate = Path.Combine(link, "gateway-token.txt");
        // Prove the link REALLY escapes: the filesystem read reaches the outside secret.
        Assert.True(File.Exists(candidate), "test setup: the planted link must actually reach the outside file");

        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, candidate));
    }

    [Fact]
    public void ResolveSessionFile_pathThatIsItselfALinkOutOfTheRoot_isRefused()
    {
        // The requested path IS the link (the leaf component), not a path through one.
        var link = Path.Combine(_root, "leaf-link");
        CreateDirectoryLink(link, _outside);

        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, link));
    }

    [Fact]
    public void ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused()
    {
        // The most direct form of the reported attack: a FILE symbolic link inside the working
        // directory whose target is the secret outside it.
        var link = Path.Combine(_root, "alias.txt");
        CreateFileLink(link, _secret);
        Assert.True(File.Exists(link), "test setup: the planted file link must actually reach the outside file");

        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, link));
    }

    [Fact]
    public void ResolveSessionFile_linkInsideTheRootPointingInsideTheRoot_stillResolves()
    {
        // The other failure direction: a containment fix must not refuse the LEGITIMATE namespace.
        // A link that stays inside the root resolves and serves.
        var realSub = Path.Combine(_root, "real-sub");
        Directory.CreateDirectory(realSub);
        var inner = Path.Combine(realSub, "inner.txt");
        File.WriteAllText(inner, "inner-content");
        var alias = Path.Combine(_root, "alias-sub");
        CreateDirectoryLink(alias, realSub);

        var resolved = ControlEndpoints.ResolveSessionFile(_root, Path.Combine(alias, "inner.txt"));

        Assert.NotNull(resolved);
        Assert.Equal("inner-content", File.ReadAllText(resolved!));
    }

    [Fact]
    public void ResolveSessionFile_workingDirectoryItselfBehindALink_stillServesItsFiles()
    {
        // Root-side symmetry: when the session's working directory is itself reached through a link,
        // resolving only the candidate would make every legal file look out-of-root. Both sides are
        // resolved, so the real identities still nest.
        var actualRoot = Path.Combine(_base, "actual-root");
        Directory.CreateDirectory(actualRoot);
        File.WriteAllText(Path.Combine(actualRoot, "f.txt"), "through-the-alias");
        var rootAlias = Path.Combine(_base, "root-alias");
        CreateDirectoryLink(rootAlias, actualRoot);

        var resolved = ControlEndpoints.ResolveSessionFile(rootAlias, Path.Combine(rootAlias, "f.txt"));

        Assert.NotNull(resolved);
        Assert.Equal("through-the-alias", File.ReadAllText(resolved!));
    }

    // --------------------------------------------------- ListDirectory: links must not escape ----

    [Fact]
    public void ListDirectory_linkedDirectoryUnderTheRootEscapingIt_isRefused()
    {
        Directory.CreateDirectory(Path.Combine(_outside, "loot"));
        var link = Path.Combine(_root, "browse-me");
        CreateDirectoryLink(link, _outside);
        Assert.True(Directory.Exists(link), "test setup: the planted link must actually reach the outside directory");

        Assert.Throws<UnauthorizedAccessException>(() => ControlEndpoints.ListDirectory(link, new[] { _root }));
    }

    [Fact]
    public void ListDirectory_linkInsideTheRootPointingInsideTheRoot_stillLists()
    {
        var realSub = Path.Combine(_root, "list-real");
        Directory.CreateDirectory(Path.Combine(realSub, "child"));
        var alias = Path.Combine(_root, "list-alias");
        CreateDirectoryLink(alias, realSub);

        var listing = ControlEndpoints.ListDirectory(alias, new[] { _root });

        Assert.Contains(listing.Entries, e => e.Name == "child");
    }

    // ------------------------------------------------ ResolveScreenshot: links must not escape ----

    private void PinScreenshotsFolder(string shotsDir)
    {
        // Same pinning pattern as ScreenshotEndpointsTests: point CcStorage.Screenshots() into the
        // isolated temp root via config.json so nothing touches the user's real screenshots.
        Directory.CreateDirectory(shotsDir);
        Directory.CreateDirectory(CcStorage.Config());
        File.WriteAllText(CcStorage.ConfigJson(), JsonSerializer.Serialize(new
        {
            screenshots = new { source_directory = shotsDir },
        }));
    }

    [Fact]
    public void ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused()
    {
        // The one escape the bare-name gate cannot stop: a FILE symbolic link INSIDE the screenshots
        // folder, carrying an innocent image-extension name, targeting a secret elsewhere on disk.
        var shotsDir = Path.Combine(_base, "shots");
        PinScreenshotsFolder(shotsDir);
        var link = Path.Combine(shotsDir, "innocent.png");
        CreateFileLink(link, _secret);
        Assert.True(File.Exists(link), "test setup: the planted link must actually reach the outside file");

        Assert.Null(ControlEndpoints.ResolveScreenshot("innocent.png"));
    }

    [Fact]
    public void ResolveScreenshot_realFileInsideTheScreenshotsFolder_stillResolves()
    {
        var shotsDir = Path.Combine(_base, "shots-real");
        PinScreenshotsFolder(shotsDir);
        var real = Path.Combine(shotsDir, "real.png");
        File.WriteAllBytes(real, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var resolved = ControlEndpoints.ResolveScreenshot("real.png");

        Assert.NotNull(resolved);
        Assert.True(string.Equals(Path.GetFullPath(real), resolved, ControlEndpoints.PathContainmentComparison),
            $"expected {real}, got {resolved}");
    }

    // ------------------------------------------------------- case sensitivity is platform-correct ----

    [Fact]
    public void PathContainmentComparisonFor_windowsFileSystem_isCaseInsensitive()
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, ControlEndpoints.PathContainmentComparisonFor(windowsFileSystem: true));
    }

    [Fact]
    public void PathContainmentComparisonFor_nonWindowsFileSystem_isCaseSensitive()
    {
        // The I1-02 defect: both containment comparisons were unconditionally OrdinalIgnoreCase, so on
        // a case-sensitive filesystem a path differing only by case was accepted as inside the root.
        Assert.Equal(StringComparison.Ordinal, ControlEndpoints.PathContainmentComparisonFor(windowsFileSystem: false));
    }

    [Fact]
    public void PathContainmentComparison_isWiredToTheHostPlatform()
    {
        // The pure decision above covers both branches; this pins the seam - the property the
        // production sites actually read is fed by the real platform question.
        Assert.Equal(
            ControlEndpoints.PathContainmentComparisonFor(OperatingSystem.IsWindows()),
            ControlEndpoints.PathContainmentComparison);
    }

    [Fact]
    public void ResolveSessionFile_caseVariantOfTheRoot_followsThePlatformRule()
    {
        // Behavioral leg: a candidate spelled with a case-variant of the root's last segment. On a
        // Windows host the filesystem treats it as the same directory, so it resolves; anywhere else
        // it must be refused. (On a Windows build machine only the first branch executes - the
        // non-Windows branch is carried by PathContainmentComparisonFor_nonWindowsFileSystem above.)
        File.WriteAllText(Path.Combine(_root, "case-file.txt"), "case");
        var caseVariantRoot = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root)!.ToUpperInvariant());
        var candidate = Path.Combine(caseVariantRoot, "case-file.txt");

        var resolved = ControlEndpoints.ResolveSessionFile(_root, candidate);

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(resolved);
            Assert.Equal("case", File.ReadAllText(resolved!));
        }
        else
        {
            Assert.Null(resolved);
        }
    }
}
