using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class UninstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public UninstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = Sandboxed(Path.Combine(_dir, "local"));
    }

    /// <summary>
    /// A layout whose root, HOME FOLDER and macOS Applications folder are all inside this test's own
    /// temporary directory.
    ///
    /// Every destructive test in this file must use one. A layout built from a root alone asks the
    /// MACHINE for the other two, and the uninstall plan then names the real ones: on a Mac that is
    /// ~/Applications/Director.app and /Applications/Director.app - the developer's own installed
    /// DevThrottle. RemoveDirectories deleted it. This was not hypothetical: running these tests on a Mac
    /// removed the installed application from this machine, twice, and the only reason it was noticed was
    /// that someone looked. The temp root was never the whole sandbox; the home folder is the other half
    /// of it, and <see cref="PlanNeverNamesAPathOutsideTheLayoutItWasGiven"/> is the guard that says so.
    /// </summary>
    private static InstallLayout Sandboxed(string root)
    {
        var home = Path.Combine(Path.GetDirectoryName(root)!, "home");
        return new InstallLayout(root, defaultRoot: null, homeDir: home,
            macAppsDir: Path.Combine(home, "Applications"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Plan_Workstation_TargetsAppBinLauncherPathShortcut_NotGatewayDirsOrGatewayAutostart()
    {
        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        Assert.Contains(_layout.AppDir, dirs);
        Assert.Contains(_layout.BinDir, dirs);
        // The Launcher ships to both roles, so a Workstation uninstall removes its binaries too.
        Assert.Contains(_layout.LauncherDir, dirs);
        Assert.DoesNotContain(_layout.GatewayDir, dirs);
        Assert.DoesNotContain(_layout.CockpitDir, dirs);
        // The Gateway autostart Run key is never in a Workstation plan...
        Assert.DoesNotContain(plan, t => t.Kind == UninstallKind.Autostart && t.Path == GatewayAutostart.ValueName);
        // ...but the Launcher autostart Run key is (Windows), because the launcher is a Workstation component.
        if (OperatingSystem.IsWindows())
            Assert.Contains(plan, t => t.Kind == UninstallKind.Autostart && t.Path == LauncherAutostart.ValueName);
        Assert.Contains(plan, t => t.Kind == UninstallKind.PathEntry);
        Assert.Contains(plan, t => t.Kind == UninstallKind.Shortcut);
    }

    [Fact]
    public void Plan_Gateway_AddsAutostartAndGatewayDirs()
    {
        var plan = new Uninstaller(_layout).Plan(InstallRole.Gateway);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        if (OperatingSystem.IsWindows())
            Assert.Contains(plan, t => t.Kind == UninstallKind.Autostart);
        Assert.Contains(_layout.GatewayDir, dirs);
        Assert.Contains(_layout.CockpitDir, dirs);
        Assert.Contains(_layout.StateDir, dirs);
        // Logs stay: they live with the user's other data under the root.
        Assert.DoesNotContain(_layout.LogsDir, dirs);
    }

    [Fact]
    public void RemoveDirectories_DeletesInstallDirs_PreservesUserData()
    {
        // Install-owned dirs:
        Directory.CreateDirectory(_layout.AppDir);
        File.WriteAllText(Path.Combine(_layout.AppDir, "cc-director.exe"), "app");
        Directory.CreateDirectory(_layout.BinDir);
        File.WriteAllText(Path.Combine(_layout.BinDir, "cc-pdf.exe"), "tool");

        // User data living under the SAME per-user root (must NOT be touched):
        var vault = Path.Combine(_layout.LocalRoot, "vault");
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(vault, "contacts.db"), "precious");
        var connections = Path.Combine(_layout.LocalRoot, "connections");
        Directory.CreateDirectory(connections);
        File.WriteAllText(Path.Combine(connections, "linkedin.json"), "session");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveDirectories(InstallRole.Workstation, steps, errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(_layout.AppDir));
        Assert.False(Directory.Exists(_layout.BinDir));
        // The per-user root and the user's data survive.
        Assert.True(Directory.Exists(_layout.LocalRoot));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(vault, "contacts.db")));
        Assert.Equal("session", File.ReadAllText(Path.Combine(connections, "linkedin.json")));
    }

    [Fact]
    public void RemoveDirectories_NeverDeletesPerUserRoot()
    {
        // A pathological layout where bin == the root; the guard must refuse.
        var bad = Sandboxed(_layout.LocalRoot);
        Directory.CreateDirectory(bad.LocalRoot);
        // Force a target equal to the root by checking the guard via AppDir? AppDir != root, so simulate
        // by asserting the guard logic indirectly: deleting AppDir leaves root intact.
        Directory.CreateDirectory(bad.AppDir);
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(bad).RemoveDirectories(InstallRole.Workstation, steps, errors);
        Assert.True(Directory.Exists(bad.LocalRoot));
    }

    /// <summary>
    /// The uninstall plan must name ONLY paths inside the layout it was given - its root, its home
    /// folder, and the Applications folder it was told about.
    ///
    /// This is the guard for a defect that was live and silent: these tests built their layout from a
    /// temporary ROOT alone, so the layout asked the machine for the home folder and (on macOS) for the
    /// Applications folder. The plan then named the real ~/Applications/Director.app, RemoveDirectories
    /// deleted it, and the test passed - it only ever asserted about the temp root, so the one path it
    /// did not look at was the only one that was real. Running the suite on a Mac uninstalled DevThrottle
    /// from the developer's own machine.
    ///
    /// The assertion is a PRESENCE: every directory in the plan is under the sandbox. "I found nothing
    /// outside it" would certify a plan that was empty, and an empty plan is a broken instrument - so the
    /// count is checked first.
    ///
    /// This covers every target the plan names as a FILESYSTEM PATH - the directories, and the macOS
    /// launch agent property list. The property list was the second instance of this same defect:
    /// LauncherLaunchdAutostart.PlistPath and GatewayLaunchdAutostart.PlistPath read the real home
    /// through a static property no layout could redirect, so an uninstall handed a sandbox still named
    /// the user's own ~/Library/LaunchAgents file - and on 19 October 2026 an uninstall run against a
    /// scratch root deleted the real launch agent on this machine. Both now answer from the layout's own
    /// home folder (PlistPathIn), and this test is what keeps them there.
    ///
    /// The Autostart target on WINDOWS is a registry value name, not a path, so it is excluded by kind
    /// rather than by name - there is no sandbox a registry key could be under, and asserting a path
    /// shape against "DevThrottle" would fail for the wrong reason.
    /// </summary>
    [Fact]
    public void PlanNeverNamesAPathOutsideTheLayoutItWasGiven()
    {
        var sandbox = Path.Combine(_dir, "sandbox");
        var layout = Sandboxed(Path.Combine(sandbox, "local"));

        foreach (var role in new[] { InstallRole.Workstation, InstallRole.Gateway })
        {
            // Directories on every platform; the launch agent property list only where it is a path.
            var paths = new Uninstaller(layout).Plan(role)
                .Where(t => t.Kind == UninstallKind.Directory
                            || (t.Kind == UninstallKind.Autostart && OperatingSystem.IsMacOS()))
                .ToList();

            Assert.NotEmpty(paths);
            if (OperatingSystem.IsMacOS())
            {
                // A PRESENCE, so this cannot pass by the launch agent having quietly left the plan: the
                // path it used to name wrongly must be in here, and must be in the sandbox.
                Assert.Contains(paths, t => t.Kind == UninstallKind.Autostart);
            }

            foreach (var target in paths)
            {
                Assert.True(
                    target.Path.StartsWith(sandbox + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    $"the {role} uninstall plan names '{target.Path}' ({target.Kind}), which is outside the "
                    + $"sandbox '{sandbox}' this layout was given. On a real machine that is someone's own "
                    + "file, and the uninstall deletes what the plan names.");
            }
        }
    }

    [Fact]
    public void TheDirectorBundleAndItsEarlierLocationsAreRemovedByThePlanAlone()
    {
        // Regression. RemoveMacArtifacts used to delete the bundle a SECOND time, after
        // RemoveDirectories had already deleted it from the plan. Two costs: the uninstall reported
        // "Director app: not present" for bundles it had itself removed a moment earlier, which reads
        // like a failed uninstall; and when the launcher could not be stopped - the one case where the
        // plan is skipped ON PURPOSE, because deleting a running launcher's files is what creates an
        // unstoppable launcher - the second pass deleted the app anyway, so the uninstall contradicted
        // what it had just reported. The second pass is gone, which is only safe while the plan covers
        // every bundle on its own. That is what this test holds.
        if (!OperatingSystem.IsMacOS())
        {
            // Off macOS the bundle aliases are a macOS history and there are none, so there is nothing
            // a second pass could have covered either.
            Assert.Empty(_layout.LegacyAliasesFor(ComponentRegistry.Director));
            return;
        }

        var bundles = new[] { _layout.PathFor(ComponentRegistry.Director) }
            .Concat(_layout.LegacyAliasesFor(ComponentRegistry.Director)).ToArray();
        Assert.True(bundles.Length > 1, "the macOS plan should name the bundle and at least one earlier location");
        foreach (var bundle in bundles)
            Directory.CreateDirectory(Path.Combine(bundle, "Contents"));

        // Each one is named by the plan exactly once - a path listed twice would delete fine and report
        // twice, which is the reporting defect in a different place.
        var planned = new Uninstaller(_layout).Plan(InstallRole.Workstation)
            .Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();
        foreach (var bundle in bundles)
            Assert.Single(planned, p => string.Equals(p, bundle, StringComparison.Ordinal));

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveDirectories(InstallRole.Workstation, steps, errors);

        Assert.Empty(errors);
        foreach (var bundle in bundles)
            Assert.False(Directory.Exists(bundle), $"the plan alone should have removed '{bundle}'");
    }

    [Theory]
    [InlineData(@"C:\a;C:\Users\me\AppData\Local\cc-director\bin;C:\b", @"C:\Users\me\AppData\Local\cc-director\bin", @"C:\a;C:\b")]
    [InlineData(@"C:\a;C:\b", @"C:\Users\me\AppData\Local\cc-director\bin", @"C:\a;C:\b")]
    [InlineData(@"C:\CC\BIN\;c:\cc\bin", @"C:\CC\BIN", "")]
    public void ComputePathWithout_RemovesEntryCaseInsensitive(string input, string dir, string expected)
    {
        Assert.Equal(expected, Uninstaller.ComputePathWithout(input, dir));
    }

    // ===== Full data wipe (issue #261) - the "Also delete my data" opt-in =====

    [Fact]
    public void WipeUserData_DeletesEntireRoot_WhenLeafIsCcDirector()
    {
        // A root whose leaf folder IS "cc-director" - the only shape the guard allows.
        var root = Path.Combine(_dir, "cc-director");
        var layout = Sandboxed(root);
        Directory.CreateDirectory(Path.Combine(root, "vault"));
        File.WriteAllText(Path.Combine(root, "vault", "contacts.db"), "secrets");
        Directory.CreateDirectory(Path.Combine(root, "connections", "linkedin"));
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(layout).WipeUserData(steps, errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(root));
        Assert.Contains(steps, s => s.Contains("removed all data"));
    }

    [Fact]
    public void WipeUserData_RefusesRoot_NotNamedCcDirector()
    {
        // The default layout's root leaf is "local", not "cc-director" - the guard must refuse.
        Directory.CreateDirectory(_layout.LocalRoot);
        File.WriteAllText(Path.Combine(_layout.LocalRoot, "precious.txt"), "do not delete");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).WipeUserData(steps, errors);

        Assert.Contains(errors, e => e.Contains("not a cc-director root"));
        // The directory and its contents survive the refusal.
        Assert.True(Directory.Exists(_layout.LocalRoot));
        Assert.Equal("do not delete", File.ReadAllText(Path.Combine(_layout.LocalRoot, "precious.txt")));
    }

    [Fact]
    public void WipeUserData_RootAbsent_ReportsSkipped_NotError()
    {
        var root = Path.Combine(_dir, "cc-director"); // never created
        var layout = Sandboxed(root);

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(layout).WipeUserData(steps, errors);

        Assert.Empty(errors);
        Assert.Contains(steps, s => s.Contains("not present"));
    }

    // ===== Skills are not ours to remove (issue 995) =====
    //
    // The installer no longer writes skill files onto anyone's machine - skills live on the Gateway
    // and are fetched - so the uninstaller has nothing of ours in the user's skills folder and must
    // never reach into it. Any skill file already sitting on an existing machine is the user's file
    // now, and an uninstall leaves it exactly where it is. These two guards red the moment skill
    // removal is put back: the first on the engine's public surface, the second on what a real plan
    // would actually delete.

    [Fact]
    public void Engine_ExposesNoSkillRemovalSurface()
    {
        var engine = typeof(Uninstaller).Assembly;

        Assert.DoesNotContain(engine.GetTypes(), t => t.Name.Contains("Skill", StringComparison.Ordinal));
        Assert.Null(typeof(Uninstaller).GetMethod("RemoveSkills"));
        Assert.DoesNotContain(Enum.GetNames<UninstallKind>(), n => n.Contains("Skill", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ListsNothingInsideTheUsersSkillsFolder()
    {
        // Every path an uninstall would delete, for both roles. None may sit under the per-user
        // skills tree (%USERPROFILE%\.claude\skills), which is where the retired installer wrote.
        var skillsTree = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

        foreach (var role in new[] { InstallRole.Workstation, InstallRole.Gateway })
        {
            var plan = new Uninstaller(_layout).Plan(role);
            Assert.NotEmpty(plan);
            Assert.DoesNotContain(plan, (UninstallTarget t) =>
                t.Path.StartsWith(skillsTree, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ===== Scheduled-task + Tailscale removal route through the report (seam-driven) =====

    [Fact]
    public void RemoveScheduledTasks_ReportsRemovedAndSkipped()
    {
        var steps = new List<string>();
        var errors = new List<string>();
        // Present "launch", absent "gateway-launch".
        new Uninstaller(_layout).RemoveScheduledTasks(steps, errors, name =>
            new ScheduledTaskResult(name, Present: name == "cc-director-launch",
                Removed: name == "cc-director-launch", Error: null));

        Assert.Empty(errors);
        Assert.Contains(steps, s => s.Contains("removed scheduled task 'cc-director-launch'"));
        Assert.Contains(steps, s => s.Contains("cc-director-gateway-launch") && s.Contains("not present"));
    }

    [Fact]
    public void RemoveTailscaleServe_CliAbsent_IsNoOp_NotError()
    {
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveTailscaleServe(steps, errors,
            _ => (Available: false, ExitCode: -1, Error: ""));

        Assert.Empty(errors);
        Assert.Contains(steps, s => s.Contains("tailscale CLI not present"));
    }

    [Fact]
    public void RemoveTailscaleServe_Removes443()
    {
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveTailscaleServe(steps, errors,
            _ => (Available: true, ExitCode: 0, Error: ""));

        Assert.Empty(errors);
        Assert.Contains(steps, s => s.Contains("removed Tailscale Serve front-door mapping"));
    }

    [WindowsOnlyTheory("persisting a PATH change is Windows-only - elsewhere the shell profile owns PATH - "
                       + "and these expectations are drive-letter, semicolon and case-insensitive shaped")]
    [InlineData(@"C:\a;C:\b", @"C:\cc\bin", @"C:\a;C:\b;C:\cc\bin")]   // appended
    [InlineData(@"C:\a;C:\cc\bin;C:\b", @"C:\cc\bin", @"C:\a;C:\cc\bin;C:\b")] // already present -> unchanged
    [InlineData(@"C:\a;c:\CC\BIN\", @"C:\cc\bin", @"C:\a;c:\CC\BIN\")] // case/trailing-slash insensitive -> unchanged
    [InlineData("", @"C:\cc\bin", @"C:\cc\bin")]                       // empty -> just the dir
    public void TheInstallersPathStep_AppendsTheMasterUnlessItIsAlreadyThere(string input, string dir, string expected)
    {
        // The install's path step used to be a plain append and nothing else. It is now the shared
        // rewrite rule, so these four cases moved with it rather than being deleted: appending the
        // master, and leaving a path that already carries it alone whatever its spelling, are still
        // exactly what an ordinary install must do.
        Assert.Equal(expected, CcDirector.Core.Setup.FleetToolPathRepair.RewriteForInstall(input, dir).Path);
    }

    [Fact]
    public void Plan_OnMacOS_TargetsTheAppBundle_ItsPreRenameAlias_AndTheLaunchAgent()
    {
        if (OperatingSystem.IsWindows()) return; // macOS-shaped plan rows exist only off Windows.

        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        // The Director on macOS is the .app bundle in ~/Applications, not the Windows AppDir -
        // without these rows an uninstall would leave the actual application behind.
        Assert.Contains(_layout.PathFor(ComponentRegistry.Director), dirs);
        foreach (var alias in _layout.LegacyAliasesFor(ComponentRegistry.Director))
            Assert.Contains(alias, dirs);

        // The launch agent comes from THIS layout's home folder, not the machine's. Asserting the real
        // LauncherLaunchdAutostart.PlistPath here is what let the plan name the user's own
        // ~/Library/LaunchAgents file while under a sandbox - the test agreed with the defect.
        if (OperatingSystem.IsMacOS())
            Assert.Contains(plan, t => t.Kind == UninstallKind.Autostart
                && t.Path == LauncherLaunchdAutostart.PlistPathIn(_layout.HomeDir));
    }

    [Fact]
    public void Plan_OnWindows_DoesNotListMacBundleRows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();
        Assert.DoesNotContain(dirs, d => d.EndsWith(".app", StringComparison.Ordinal));
    }
}
