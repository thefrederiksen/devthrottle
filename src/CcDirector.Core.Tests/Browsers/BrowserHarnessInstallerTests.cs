using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Tests for the browser-harness installer (issue #1012). What is asserted here is everything that can
/// be settled without reaching PyPI: where the harness is placed, that its own environment is kept apart
/// from the shared cc-* tools venv, the shim bodies, how the shim directory reaches this process's PATH,
/// and - the one that matters most - that a machine which CANNOT install says so instead of continuing
/// as though it had worked (CLAUDE.md rule 3, no fallbacks).
///
/// These redirect CcStorage with CC_DIRECTOR_ROOT and also take over PATH, because the production code's
/// first question is "is the harness already on PATH?" and this machine may well have one. They therefore
/// join the serialized config-environment collection - two of them running at once would trade PATHs.
/// </summary>
[Collection("ConfigEnvSerial")]
public class BrowserHarnessInstallerTests
{
    /// <summary>
    /// Run <paramref name="body"/> with the storage root pointed at a fresh temp directory and PATH set
    /// to exactly <paramref name="path"/> (empty by default, so no harness on the machine can resolve and
    /// leak into the result). Both are restored afterwards, whatever happens.
    /// </summary>
    private static void WithRootAndPath(Action<string> body, string path = "")
    {
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-harness-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable("PATH", path);
        try
        {
            Directory.CreateDirectory(root);
            body(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a temp dir we could not remove must not fail the test */ }
        }
    }

    // --- Where things go ---------------------------------------------------------------------------

    [Fact]
    public void EnvDir_IsItsOwn_NotTheSharedToolsVenv()
    {
        WithRootAndPath(root =>
        {
            // The harness must NOT land in pyenv: that venv is deleted and rebuilt by the tools-bundle
            // installer on every release, which would silently remove the harness again, and the
            // harness pins dependency versions the cc-* tools do not share.
            var sharedVenv = Path.Combine(root, "pyenv");
            Assert.NotEqual(sharedVenv, BrowserHarnessInstaller.EnvDir);
            Assert.False(
                BrowserHarnessInstaller.EnvDir.StartsWith(sharedVenv + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "the harness environment must not live inside the shared tools venv");

            Assert.Equal(Path.Combine(root, "harness-env"), BrowserHarnessInstaller.EnvDir);
        });
    }

    [Fact]
    public void ConsoleScript_LivesInsideTheHarnessEnvironment()
    {
        WithRootAndPath(_ =>
            Assert.StartsWith(
                BrowserHarnessInstaller.EnvDir + Path.DirectorySeparatorChar,
                BrowserHarnessInstaller.ConsoleScript,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShimDir_OnWindows_IsTheManagedBinEveryToolShimAlreadyUses()
    {
        if (!OperatingSystem.IsWindows()) return;

        // This is the whole PATH argument: the shim goes where cc-devthrottle.cmd already lives, a
        // directory the product itself put on PATH. Anywhere else and a successful install could still
        // fail detection.
        WithRootAndPath(root => Assert.Equal(Path.Combine(root, "bin"), BrowserHarnessInstaller.ShimDir));
    }

    [Fact]
    public void ShimDir_OnARootThatIsNotTheMachinesDefault_IsThatRootsOwnBin_OnEveryPlatform()
    {
        // A scratch root (a test Director's) must never link the user's global ~/.local/bin at a harness
        // environment that is deleted along with the scratch root (mission #4070).
        WithRootAndPath(root => Assert.Equal(Path.Combine(root, "bin"), BrowserHarnessInstaller.ShimDir));
    }

    [Fact]
    public void BundledPython_IsTheOneDevThrottleShips()
    {
        WithRootAndPath(root =>
        {
            var expected = OperatingSystem.IsWindows()
                ? Path.Combine(root, "python", "python.exe")
                : Path.Combine(root, "python", "bin", "python3");
            Assert.Equal(expected, BrowserHarnessInstaller.BundledPython);
        });
    }

    // --- The failure that must never be silent -----------------------------------------------------

    [Fact]
    public async Task InstallAsync_WithNoBundledPython_FailsLoudly_AndCreatesNothing()
    {
        await WithRootAndPathAsync(async _ =>
        {
            var result = await BrowserHarnessInstaller.InstallAsync();

            Assert.False(result.Success);
            // The message has to name the missing thing and what to do about it - this is the text the
            // wizard shows, and "something went wrong" would leave the user with nowhere to go.
            Assert.Contains("bundled Python is missing", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BrowserHarnessInstaller.BundledPython, result.Message);
            Assert.Null(result.Version);

            // Nothing half-built is left behind: no environment, and above all no shim, since a shim
            // pointing at an absent console script is the "installed" lie this whole design avoids.
            Assert.False(Directory.Exists(BrowserHarnessInstaller.EnvDir));
            Assert.False(File.Exists(Path.Combine(BrowserHarnessInstaller.ShimDir, "browser-harness.cmd")));
        });
    }

    [Fact]
    public async Task InstallAsync_WhenAlreadyOnPath_LeavesTheExistingInstallAlone()
    {
        // A user who installed browser-harness themselves (uv, or their own Python) must not have it
        // rebuilt underneath them, and we must not claim to have installed what was already there.
        var borrowedDir = Path.Combine(Path.GetTempPath(), "cc-director-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(borrowedDir);
        var fakeExe = Path.Combine(borrowedDir, OperatingSystem.IsWindows() ? "browser-harness.cmd" : "browser-harness");
        File.WriteAllText(fakeExe, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeExe, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            await WithRootAndPathAsync(async _ =>
            {
                var result = await BrowserHarnessInstaller.InstallAsync();

                Assert.True(result.Success);
                Assert.Contains("already installed", result.Message, StringComparison.OrdinalIgnoreCase);
                // Untouched: no environment of ours was built over the top of theirs.
                Assert.False(Directory.Exists(BrowserHarnessInstaller.EnvDir));
            }, path: borrowedDir);
        }
        finally
        {
            try { Directory.Delete(borrowedDir, recursive: true); } catch (IOException) { }
        }
    }

    // --- The shims ---------------------------------------------------------------------------------

    [Fact]
    public void WindowsShimBody_RunsTheConsoleScript_AndRefusesWhenItIsMissing()
    {
        const string target = @"C:\root\harness-env\Scripts\browser-harness.exe";
        var body = BrowserHarnessInstaller.BuildWindowsShimBody(target);

        Assert.Contains($"\"{target}\" %*", body);
        // The guard is what turns a removed environment into a sentence the user can act on instead of
        // cmd.exe's "is not recognized".
        Assert.Contains($"if not exist \"{target}\"", body);
        Assert.Contains("exit /b 1", body);
        Assert.Contains("\r\n", body);
    }

    [Fact]
    public void BashShimBody_UsesForwardSlashes_SoGitBashCanExecIt()
    {
        var body = BrowserHarnessInstaller.BuildWindowsBashShimBody(@"C:\root\harness-env\Scripts\browser-harness.exe");

        Assert.StartsWith("#!/bin/sh\n", body);
        Assert.Contains("C:/root/harness-env/Scripts/browser-harness.exe", body);
        Assert.DoesNotContain("\r\n", body);
        Assert.Contains("\"$@\"", body);
    }

    // --- PATH handling -----------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\a;C:\b", @"C:\b", true)]
    [InlineData(@"C:\a;C:\b\", @"C:\b", true)]      // a trailing separator is the same directory
    [InlineData(@"C:\a; C:\b ", @"C:\b", true)]      // padded entries are the same directory
    [InlineData(@"C:\a;C:\bc", @"C:\b", false)]      // a prefix is NOT the same directory
    [InlineData("", @"C:\b", false)]
    public void PathContains_RecognisesTheSameDirectory_AndOnlyThat(string searchPath, string dir, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, BrowserHarnessInstaller.PathContains(searchPath, dir));
    }

    [Fact]
    public void PathContains_OnWindows_IgnoresCase()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(BrowserHarnessInstaller.PathContains(@"C:\Program Files;C:\Users\me\bin", @"c:\users\me\BIN"));
    }

    [Fact]
    public void EnsureShimDirOnProcessPath_AddsItOnce_AndKeepsWhatWasThere()
    {
        WithRootAndPath(_ =>
        {
            var existing = Environment.GetEnvironmentVariable("PATH");
            Assert.False(BrowserHarnessInstaller.PathContains(existing, BrowserHarnessInstaller.ShimDir));

            BrowserHarnessInstaller.EnsureShimDirOnProcessPath();
            var after = Environment.GetEnvironmentVariable("PATH");
            Assert.True(BrowserHarnessInstaller.PathContains(after, BrowserHarnessInstaller.ShimDir));
            Assert.Contains(@"C:\already\here", after);

            // Idempotent: a second install (or a repair) must not keep stacking the same entry.
            BrowserHarnessInstaller.EnsureShimDirOnProcessPath();
            Assert.Equal(after, Environment.GetEnvironmentVariable("PATH"));
        }, path: @"C:\already\here");
    }

    /// <summary>The async form of <see cref="WithRootAndPath"/>.</summary>
    private static async Task WithRootAndPathAsync(Func<string, Task> body, string path = "")
    {
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-harness-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable("PATH", path);
        try
        {
            Directory.CreateDirectory(root);
            await body(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a temp dir we could not remove must not fail the test */ }
        }
    }
}
