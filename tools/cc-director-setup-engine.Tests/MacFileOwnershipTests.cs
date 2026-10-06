using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The ownership check and repair behind #3411: a root-owned file in the install makes launchd refuse the
/// launcher with "78: EX_CONFIG". Every command is injected, so these run on any platform.
/// </summary>
public class MacFileOwnershipTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _agents;
    private readonly string _plist;

    public MacFileOwnershipTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mac-ownership-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "Application Support", "cc-director");
        Directory.CreateDirectory(Path.Combine(_root, "logs", "launcher"));
        _agents = Path.Combine(_dir, "LaunchAgents");
        _plist = Path.Combine(_agents, "com.devthrottle.cc-launcher.plist");
        Directory.CreateDirectory(_agents);
        File.WriteAllText(_plist, "<plist/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private MacFileOwnership.Targets Targets(params string[] trees) => new(trees, [_agents]);

    [Fact]
    public void FindNotOwned_SearchesTreesAllTheWayDown_AndSharedFoldersOnlyThemselves()
    {
        var asked = new List<string>();
        var missing = Path.Combine(_dir, "nope");

        var found = MacFileOwnership.FindNotOwned((exe, args) => { asked.Add($"{exe} {args}"); return (0, exe == "/usr/bin/stat" ? "501" : ""); },
            new MacFileOwnership.Targets([_root, _plist, missing], [_agents, Path.Combine(_dir, "no-folder")]), "501");

        Assert.Empty(found);
        Assert.Equal(
            [
                $"/usr/bin/find \"{_root}\" \"{_plist}\" ! -user 501 -print",
                $"/usr/bin/stat -f %u \"{_agents}\"",
            ],
            asked);
    }

    [Fact]
    public void FindNotOwned_ReturnsThePathsFindListed_AndIgnoresItsErrorLines()
    {
        var logDir = Path.Combine(_root, "logs", "launcher");
        var output = $"{logDir}\nfind: {logDir}: Permission denied\n";

        // find exits 1 because it could not descend into the root-owned folder; the folder is still listed.
        var found = MacFileOwnership.FindNotOwned(
            (exe, _) => exe == "/usr/bin/stat" ? (0, "501") : (1, output), Targets(_root, _plist), "501");

        Assert.Equal([logDir], found);
    }

    [Fact]
    public void FindNotOwned_RootOwnedSharedFolder_IsReported()
    {
        var found = MacFileOwnership.FindNotOwned(
            (exe, _) => exe == "/usr/bin/stat" ? (0, "0") : (0, ""), Targets(_root), "501");

        Assert.Equal([_agents], found);
    }

    [Fact]
    public void FindNotOwned_StatFails_Throws_NeverReadsAsAllOwned()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MacFileOwnership.FindNotOwned((exe, _) => exe == "/usr/bin/stat" ? (1, "stat: Permission denied") : (0, ""), Targets(_root), "501"));
    }

    [Fact]
    public void FindNotOwned_FailedSearchWithNothingFound_Throws_NeverReadsAsAllOwned()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MacFileOwnership.FindNotOwned((_, _) => (1, "find: illegal option"), Targets(_root), "501"));

        Assert.Contains("could not check who owns", ex.Message);
    }

    [Fact]
    public void FindNotOwned_NothingExists_DoesNotRunFind()
    {
        var ran = false;

        var found = MacFileOwnership.FindNotOwned((_, _) => { ran = true; return (0, ""); },
            new MacFileOwnership.Targets([Path.Combine(_dir, "a")], [Path.Combine(_dir, "b")]), "501");

        Assert.Empty(found);
        Assert.False(ran);
    }

    [Fact]
    public void AppleScript_ChownsFoldersFirstThenTreesRecursively_AsAdministrator()
    {
        var script = MacFileOwnership.AppleScript(
            new(["/Users/r/Library/Application Support/cc-director", "/Users/r/a\"b"], ["/Users/r/Applications"]), "501", "20");

        Assert.StartsWith("do shell script \"/bin/sh -c \" & quoted form of \"for p in \\\"$@\\\"; do if [ -e \\\"$p\\\" ]; then /usr/sbin/chown \\\"$0\\\"", script);
        Assert.Contains("\" & \" 501:20 \" & quoted form of \"/Users/r/Applications\" & \"; \" & \"/bin/sh -c \"", script);
        Assert.True(script.IndexOf("/Users/r/Applications", StringComparison.Ordinal) < script.IndexOf("cc-director", StringComparison.Ordinal), "folders are repaired before the trees inside them");
        Assert.Contains("/usr/sbin/chown -R ", script);
        Assert.Contains("quoted form of \"/Users/r/Library/Application Support/cc-director\"", script);
        Assert.Contains("quoted form of \"/Users/r/a\\\"b\"", script);
        Assert.Contains("with administrator privileges", script);
    }

    [Fact]
    public void AppleScript_RefusesNonNumericIds()
    {
        Assert.Throws<ArgumentException>(() => MacFileOwnership.AppleScript(new(["/x"], []), "501; rm -rf /", "20"));
        Assert.Throws<ArgumentException>(() => MacFileOwnership.AppleScript(new(["/x"], []), "", "20"));
    }

    [Fact]
    public void Repair_RunsTheScriptWithOsascript_AndRemovesIt()
    {
        string? scriptPath = null;
        string? scriptText = null;

        var failure = MacFileOwnership.Repair((exe, args) =>
        {
            Assert.Equal("/usr/bin/osascript", exe);
            scriptPath = args.Trim('"');
            scriptText = File.ReadAllText(scriptPath);
            return (0, "");
        }, Targets(_root), "501", "20");

        Assert.Null(failure);
        Assert.Contains("/usr/sbin/chown -R ", scriptText);
        Assert.Contains(" 501:20 ", scriptText);
        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void Repair_CancelledPrompt_SaysSo()
    {
        var failure = MacFileOwnership.Repair((_, _) => (1, "execution error: User canceled. (-128)"), Targets(_root), "501", "20");

        Assert.Equal("the password prompt was cancelled", failure);
    }

    [Fact]
    public void Repair_RunnerThrows_ReportsItAndDoesNotThrow()
    {
        var failure = MacFileOwnership.Repair((_, _) => throw new IOException("disk full"), Targets(_root), "501", "20");

        Assert.Contains("could not be started", failure);
        Assert.Contains("disk full", failure);
    }

    [Fact]
    public void ManualCommand_SingleQuotesEveryPath_SoNothingInAPathRunsUnderSudo()
    {
        var command = MacFileOwnership.ManualCommand(new(["/tmp/dev $(touch pwned) `id` \"x\"", "/tmp/it's"], ["/Users/r/Applications"]));

        Assert.Equal(
            "sudo /bin/sh -c 'for p in \"$@\"; do if [ -e \"$p\" ]; then /usr/sbin/chown \"$0\" \"$p\" || exit 1; fi; done' \"$(id -u):$(id -g)\" '/Users/r/Applications'"
            + " && sudo /bin/sh -c 'for p in \"$@\"; do if [ -e \"$p\" ]; then /usr/sbin/chown -R \"$0\" \"$p\" || exit 1; fi; done' \"$(id -u):$(id -g)\" '/tmp/dev $(touch pwned) `id` \"x\"' '/tmp/it'\\''s'",
            command);
    }

    private static MacFileOwnership.CommandRunner Fake(Func<int> findCalls, Func<int, string, (int, string)> find, Func<(int, string)> osascript) =>
        (exe, args) => exe switch
        {
            "/usr/bin/id" => (0, args == "-u" ? "501" : "20"),
            "/usr/bin/find" => find(findCalls(), args),
            "/usr/bin/stat" => (0, string.Join('\n', Enumerable.Repeat("501", args.Count(c => c == '"') / 2))),
            "/usr/bin/osascript" => osascript(),
            _ => throw new InvalidOperationException($"unexpected command {exe} {args}"),
        };

    [Fact]
    public void EnsureOwnedByUser_AllOwned_DoesNotPrompt()
    {
        var prompted = false;
        var steps = new List<string>();

        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => 0, (_, _) => (0, ""), () => { prompted = true; return (0, ""); }), Targets(_root, _plist), offerPrompt: true, steps.Add);

        Assert.Null(failure);
        Assert.False(prompted);
        Assert.Contains(steps, s => s.Contains("every install file belongs"));
    }

    [Fact]
    public void EnsureOwnedByUser_RootOwnedLogs_PromptsOnce_AndPassesWhenTheRepairTook()
    {
        var calls = 0;
        var logDir = Path.Combine(_root, "logs");
        var prompts = 0;

        // The first tree search finds the folder; every search after the repair finds nothing.
        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => ++calls, (n, args) => n == 1 ? (1, $"{logDir}\nfind: {logDir}: Permission denied") : (0, ""),
                () => { prompts++; return (0, ""); }),
            Targets(_root, _plist), offerPrompt: true, _ => { });

        Assert.Null(failure);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void EnsureOwnedByUser_NoPromptOffered_NeverPrompts_AndGivesTheManualCommand()
    {
        var prompted = false;
        var logDir = Path.Combine(_root, "logs");

        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => 0, (_, _) => (0, logDir), () => { prompted = true; return (0, ""); }),
            Targets(_root, _plist), offerPrompt: false, _ => { });

        Assert.False(prompted);
        Assert.NotNull(failure);
        Assert.Contains("this command does not ask for a password", failure);
        Assert.Contains($"\"$(id -u):$(id -g)\" '{_agents}' && sudo /bin/sh -c", failure);
        Assert.Contains($"\"$(id -u):$(id -g)\" '{_root}' '{_plist}'", failure);
    }

    [Fact]
    public void Repair_NamesEveryTarget_EvenOnesThatDoNotExistYet_BecauseAParentMayHideThem()
    {
        string? scriptText = null;
        var hidden = Path.Combine(_dir, "Applications-locked", "DevThrottle Setup.app");

        MacFileOwnership.Repair((_, args) => { scriptText = File.ReadAllText(args.Trim('"')); return (0, ""); },
            new([_root, hidden], [Path.GetDirectoryName(hidden)!]), "501", "20");

        Assert.Contains($"quoted form of \"{hidden.Replace("\\", "\\\\")}\"", scriptText);
    }

    [Fact]
    public void EnsureOwnedByUser_PromptCancelled_FailsWithTheManualCommand()
    {
        var logDir = Path.Combine(_root, "logs");

        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => 0, (_, _) => (0, logDir), () => (1, "User canceled. (-128)")),
            Targets(_root, _plist), offerPrompt: true, _ => { });

        Assert.NotNull(failure);
        Assert.Contains("the password prompt was cancelled", failure);
        Assert.Contains($"'{_root}'", failure);
    }

    [Fact]
    public void EnsureOwnedByUser_RepairRanButFilesStillNotOwned_Fails()
    {
        var logDir = Path.Combine(_root, "logs");

        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => 0, (_, _) => (0, logDir), () => (0, "")),
            Targets(_root), offerPrompt: true, _ => { });

        Assert.NotNull(failure);
        Assert.Contains("still do not belong to you", failure);
    }

    [Fact]
    public void EnsureOwnedByUser_CheckFails_NeverReadsAsAllOwned()
    {
        var failure = MacFileOwnership.EnsureOwnedByUser(
            Fake(() => 0, (_, _) => (1, "find: illegal option"), () => (0, "")), Targets(_root), offerPrompt: true, _ => { });

        Assert.NotNull(failure);
        Assert.Contains("Could not check who owns", failure);
    }

    [Fact]
    public void InstallTargets_CoverTheInstallTheAppsAndTheAgent_AndOnlyTheSharedParentsThemselves()
    {
        var layout = new InstallLayout(_root);

        var targets = MacFileOwnership.InstallTargets(layout, _plist);

        Assert.Contains(_root, targets.Trees);
        Assert.Contains(_plist, targets.Trees);
        Assert.Contains(Path.Combine(layout.MacAppsDir, "Director.app"), targets.Trees);
        Assert.Contains(Path.Combine(layout.MacAppsDir, MacFileOwnership.SetupAppName), targets.Trees);
        Assert.Contains(Path.Combine(Path.GetDirectoryName(layout.MacAppsDir)!, ".zshrc"), targets.Trees);
        Assert.Contains(Path.Combine(Path.GetDirectoryName(layout.MacAppsDir)!, ".bash_profile"), targets.Trees);
        Assert.Equal([layout.MacAppsDir, _agents, Path.GetDirectoryName(layout.MacUserBinDir)!, layout.MacUserBinDir], targets.Folders);
    }

    [Fact]
    public void Describe_NamesTheFirstFewAndCountsTheRest()
    {
        var paths = Enumerable.Range(1, 8).Select(i => $"/p{i}").ToList();

        Assert.Equal("/p1, /p2, /p3, /p4, /p5 and 3 more", MacFileOwnership.Describe(paths));
    }
}
