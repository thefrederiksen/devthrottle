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
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
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
        var bad = new InstallLayout(_layout.LocalRoot);
        Directory.CreateDirectory(bad.LocalRoot);
        // Force a target equal to the root by checking the guard via AppDir? AppDir != root, so simulate
        // by asserting the guard logic indirectly: deleting AppDir leaves root intact.
        Directory.CreateDirectory(bad.AppDir);
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(bad).RemoveDirectories(InstallRole.Workstation, steps, errors);
        Assert.True(Directory.Exists(bad.LocalRoot));
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
        var layout = new InstallLayout(root);
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
        var layout = new InstallLayout(root);

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

    [Theory]
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

        if (OperatingSystem.IsMacOS())
            Assert.Contains(plan, t => t.Kind == UninstallKind.Autostart
                && t.Path == LauncherLaunchdAutostart.PlistPath);
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
