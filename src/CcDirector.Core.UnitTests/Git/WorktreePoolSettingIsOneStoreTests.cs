using System.Diagnostics;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// THE SETTING HAS ONE STORE, AND TWO READERS THAT MUST AGREE.
///
/// The Director reads the pooled-worktree setting through <see cref="WorktreePoolSettings"/>. The only
/// way a person turns it on is `cc-devthrottle worktree pool on`, which writes it from Python. Both
/// write `config.json` under `worktreePool.repoDefaults[&lt;repository key&gt;]`, and if their key rules
/// ever differ by so much as a trailing backslash the command prints exactly what a working one prints
/// and the Director reads OFF forever. Nothing fails; the feature simply does not exist.
///
/// A test on either side alone cannot catch that, because each is self-consistent. So this one runs the
/// command line's own settings module and then reads the result through the DIRECTOR'S OWN CLASS - and
/// the other way round as well, because a reader that agrees in one direction can still disagree in the
/// other.
///
/// It drives `tools/cc-devthrottle/src/worktree_pool_settings.py` rather than the whole command line
/// deliberately. That module is the one the three commands call - the Python suite
/// (`tools/cc-devthrottle/tests/test_worktree_pool_settings.py`) holds them to it - and it imports
/// nothing but the standard library, so this test needs a Python 3 and no installed packages. Reaching
/// through typer and rich would prove the same key rule while making a green gate depend on a
/// particular virtual environment.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class WorktreePoolSettingIsOneStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    private const string Repo = @"D:\Repos\PooledRepo";

    public WorktreePoolSettingIsOneStoreTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-onestore-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

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
    /// A Python 3 on this machine, or a failure that names the fix. NOT a skip: a skipped test reads
    /// exactly like a passing one in every report this repository produces, and what it would be
    /// hiding is the two halves of one setting having drifted apart.
    /// </summary>
    private static string Python()
    {
        var named = Environment.GetEnvironmentVariable("CC_DEVTHROTTLE_TEST_PYTHON");
        if (!string.IsNullOrWhiteSpace(named))
        {
            return ExecutableResolver.Resolve(named)
                ?? throw new InvalidOperationException(
                    $"CC_DEVTHROTTLE_TEST_PYTHON names \"{named}\", which does not resolve to an executable.");
        }

        foreach (var candidate in new[] { "python3", "python" })
        {
            var resolved = ExecutableResolver.Resolve(candidate);
            if (resolved is not null)
                return resolved;
        }

        throw new InvalidOperationException(
            "No Python 3 was found on PATH, so the command line's half of the pooled-worktree setting " +
            "could not be run. Install Python 3, or set CC_DEVTHROTTLE_TEST_PYTHON to one.");
    }

    /// <summary>
    /// A Python string literal for <paramref name="text"/>. Composed rather than written as a raw
    /// literal because a Python raw string CANNOT end in a backslash, and a trailing separator is one
    /// of the very spellings these tests exist to try.
    /// </summary>
    private static string PythonString(string text)
        => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Run a snippet against the command line's settings module and return what it printed.</summary>
    private static string RunTheCommandLinesSettingsModule(string snippet)
    {
        var toolsDirectory = Path.Combine(RepoRoot(), "tools");
        var program =
            "import sys\n" +
            $"sys.path.insert(0, r'{Path.Combine(toolsDirectory, "cc-devthrottle")}')\n" +
            $"sys.path.insert(0, r'{toolsDirectory}')\n" +
            "from src import worktree_pool_settings as s\n" +
            snippet;

        var start = new ProcessStartInfo(Python())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(program);
        // The child must see the same throwaway root this test redirected the Director's own reader to.
        start.Environment["CC_DIRECTOR_ROOT"] = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT") ?? "";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Python did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The command line's settings module exited {process.ExitCode}.\n{output}\n{errors}");
        return output.Trim();
    }

    // ---- the command line writes what the Director reads ----------------------------------------

    [Fact]
    public void TheCommandLineTurnsItOn_AndTheDirectorReadsItOn()
    {
        Assert.False(WorktreePoolSettings.For(Repo).Enabled);

        RunTheCommandLinesSettingsModule($"s.save({PythonString(Repo)}, True, 7)\n");

        // Read through the DIRECTOR'S OWN class, which is the only reader whose answer matters: it is
        // the one consulted on the create path when a session is opened.
        var setting = WorktreePoolSettings.For(Repo);
        Assert.True(setting.Enabled);
        Assert.Equal(7, setting.PoolSize);
    }

    [Fact]
    public void TheCommandLinesDefaultSize_IsTheDirectorsDefaultSize()
    {
        var fromTheCommandLine = RunTheCommandLinesSettingsModule("print(s.DEFAULT_POOL_SIZE)\n");

        Assert.Equal(WorktreePoolSettings.DefaultPoolSize.ToString(), fromTheCommandLine);
        Assert.Equal("4", fromTheCommandLine);
    }

    [Fact]
    public void TheCommandLineTurnsItOff_AndTheDirectorReadsItOff()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 5));
        Assert.True(WorktreePoolSettings.For(Repo).Enabled);

        RunTheCommandLinesSettingsModule($"s.clear({PythonString(Repo)})\n");

        var setting = WorktreePoolSettings.For(Repo);
        Assert.False(setting.Enabled);
        // Back to the DEFAULT size, not the five that was stored: off means never configured.
        Assert.Equal(4, setting.PoolSize);
    }

    [Fact]
    public void TheDirectorTurnsItOn_AndTheCommandLineReadsItOn()
    {
        // The other direction, which a one-way test would miss: `worktree pool status` has to report
        // what a Director wrote, or a person is told the pool is off for a repository that is using one.
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 6));

        var answer = RunTheCommandLinesSettingsModule($"print(s.read({PythonString(Repo)}))\n");

        Assert.Equal("(True, 6)", answer);
    }

    // ---- and they key a repository the same way --------------------------------------------------

    [Theory]
    // The spellings that would silently create a SECOND entry if the two key rules differed: a
    // trailing separator, forward slashes, surrounding spaces, and (on Windows) a different case.
    [InlineData(@"D:\Repos\PooledRepo\")]
    [InlineData("D:/Repos/PooledRepo")]
    [InlineData(@"  D:\Repos\PooledRepo  ")]
    public void HowThePathWasTyped_DoesNotMakeASecondEntry(string typedDifferently)
    {
        RunTheCommandLinesSettingsModule($"s.save({PythonString(typedDifferently)}, True, 3)\n");

        var setting = WorktreePoolSettings.For(Repo);
        Assert.True(setting.Enabled);
        Assert.Equal(3, setting.PoolSize);
    }

    [Fact]
    public void OnWindowsTheCaseOfThePathDoesNotMatterEither()
    {
        if (!OperatingSystem.IsWindows())
            return; // On a case-sensitive filesystem two spellings ARE two repositories, by both rules.

        RunTheCommandLinesSettingsModule($"s.save({PythonString(@"D:\REPOS\POOLEDREPO")}, True, 2)\n");

        Assert.True(WorktreePoolSettings.For(Repo).Enabled);
        Assert.Equal(2, WorktreePoolSettings.For(Repo).PoolSize);
    }

    // ---- neither writer destroys what the other left -------------------------------------------

    [Fact]
    public void TheCommandLineWriting_LeavesEverySectionTheDirectorWrote()
    {
        // config.json is shared with the Director, with `cc-devthrottle settings`, and with the
        // Gateway settings route. A writer that serialised its own model over the top would drop
        // every section it did not know about - which is why both sides deep-merge.
        Configuration.CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            ["gateway"] = new System.Text.Json.Nodes.JsonObject { ["url"] = "https://example.invalid" },
        });

        RunTheCommandLinesSettingsModule($"s.save({PythonString(Repo)}, True, 4)\n");

        var document = Configuration.CcDirectorConfigService.ReadRaw();
        Assert.Equal("https://example.invalid", document["gateway"]?["url"]?.GetValue<string>());
        Assert.True(WorktreePoolSettings.For(Repo).Enabled);
    }

    [Fact]
    public void TheDirectorWriting_LeavesTheCommandLinesOtherRepositoriesAlone()
    {
        const string other = @"D:\Repos\AnotherRepo";
        RunTheCommandLinesSettingsModule($"s.save({PythonString(other)}, True, 8)\n");

        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 2));

        Assert.Equal("(True, 8)", RunTheCommandLinesSettingsModule($"print(s.read({PythonString(other)}))\n"));
        Assert.Equal(2, WorktreePoolSettings.For(Repo).PoolSize);
    }
}
