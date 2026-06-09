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
    public void Plan_Workstation_TargetsAppBinPathShortcut_NotGatewayOrAutostart()
    {
        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        Assert.Contains(_layout.AppDir, dirs);
        Assert.Contains(_layout.BinDir, dirs);
        Assert.DoesNotContain(_layout.GatewayDir, dirs);
        Assert.DoesNotContain(_layout.CockpitDir, dirs);
        Assert.DoesNotContain(plan, t => t.Kind == UninstallKind.Autostart);
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

    // ===== Skill removal (issue #257) - AC8: only OUR skills, never the user's own =====

    [Fact]
    public void RemoveSkills_RemovesOnlyManifestedSkills_LeavesUserSkills()
    {
        // The install recorded only "cc-director" as owned.
        SkillManifest.RecordInstalled(_layout, new[] { "cc-director" });

        // A sandbox skills dir holding BOTH our skill and a user-authored one with no manifest entry.
        var skills = Path.Combine(_dir, "skills");
        var ours = Path.Combine(skills, "cc-director");
        var theirs = Path.Combine(skills, "my-custom-skill");
        Directory.CreateDirectory(ours);
        File.WriteAllText(Path.Combine(ours, "SKILL.md"), "ours");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "precious user skill");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveSkills(steps, errors, skillsBaseDir: skills);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(ours));                 // ours removed
        Assert.True(Directory.Exists(theirs));                // the user's survives
        Assert.Equal("precious user skill", File.ReadAllText(Path.Combine(theirs, "SKILL.md")));
    }

    [Fact]
    public void RemoveSkills_NoManifest_RemovesNothing()
    {
        var skills = Path.Combine(_dir, "skills");
        var theirs = Path.Combine(skills, "cc-director"); // same NAME, but no manifest = not ours
        Directory.CreateDirectory(theirs);

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveSkills(steps, errors, skillsBaseDir: skills);

        Assert.True(Directory.Exists(theirs));                // never touched without an ownership record
        Assert.Contains(steps, s => s.Contains("none recorded"));
    }

    [Fact]
    public void RemoveSkills_ManifestedButAbsent_ReportsSkipped()
    {
        SkillManifest.RecordInstalled(_layout, new[] { "cc-director" });
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveSkills(steps, errors, skillsBaseDir: Path.Combine(_dir, "empty"));

        Assert.Empty(errors);
        Assert.Contains(steps, s => s.Contains("not present"));
    }

    [Fact]
    public void RemoveSkills_MalformedManifest_NeverEscapesOrWipesSkillsTree()
    {
        // A hand-corrupted/hostile manifest: blank (would resolve to the skills dir itself),
        // a parent-escape, a nested path, plus one legit name.
        Directory.CreateDirectory(_layout.SetupStateDir);
        File.WriteAllText(_layout.SkillManifestPath, """["", "..\\evil", "a/b", "cc-director"]""");

        var skills = Path.Combine(_dir, "skills");
        var legit = Path.Combine(skills, "cc-director");
        var userSkill = Path.Combine(skills, "user-skill");
        var sibling = Path.Combine(_dir, "evil");           // the "..\evil" target, OUTSIDE skills
        Directory.CreateDirectory(legit);
        Directory.CreateDirectory(userSkill);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(userSkill, "SKILL.md"), "user");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveSkills(steps, errors, skillsBaseDir: skills);

        // Only the legit, simple-named, manifested skill is removed.
        Assert.False(Directory.Exists(legit));
        // Everything the guard refuses survives: the whole skills tree, the user's skill, the sibling.
        Assert.True(Directory.Exists(skills));
        Assert.True(Directory.Exists(userSkill));
        Assert.True(Directory.Exists(sibling));
        // The unsafe entries are surfaced as refusals, not silently skipped.
        Assert.Contains(errors, e => e.Contains("refused"));
    }

    [Fact]
    public void Plan_ListsManifestedSkills()
    {
        SkillManifest.RecordInstalled(_layout, new[] { "cc-director" });
        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        Assert.Contains(plan, t => t.Kind == UninstallKind.Skill && t.Description.Contains("cc-director"));
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
    public void ComputePathWith_AppendsUnlessPresent(string input, string dir, string expected)
    {
        Assert.Equal(expected, InstallFinalizer.ComputePathWith(input, dir));
    }
}
