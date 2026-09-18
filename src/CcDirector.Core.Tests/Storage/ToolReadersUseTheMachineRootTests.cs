using CcDirector.Core.Browsers;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Git;
using CcDirector.Core.Storage;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Every reader in the Director that looks for an installed tool finds the MACHINE's copy, even when
/// it is asked from inside a Director or one of its sessions.
///
/// Each test here puts the real thing on disk under the machine root, pins CC_DIRECTOR_ROOT at a
/// Director's folder the way a running Director does, and then asks the reader its own question. That
/// is the difference between watching a reader and watching a path helper: a test that only compared
/// two composed strings would stay green if a reader stopped calling the helper at all.
///
/// The matching half - that a rig root of its own still keeps its own tools - is asserted beside each
/// one, because a fix that took a rig's tools away from it would pass every test above and quietly
/// make every isolated proof run against the real machine.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class ToolReadersUseTheMachineRootTests
{
    private static string ToolFileName(string tool)
        => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    // ---------------------------------------------------------------- the Tools page catalog

    [Fact]
    public void The_tool_catalog_finds_a_tool_installed_at_the_machine_root()
    {
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");
        root.MachineFile("not a real binary", "bin", ToolFileName("cc-pdf"));

        var catalog = new ToolCatalogService();
        var pdf = catalog.GetCatalog().Single(t => t.Name == "cc-pdf");

        Assert.True(pdf.IsBuilt,
            "The Tools page asked this Director's own folder for the tools instead of the machine's, so "
            + "a machine with every tool installed reads as a machine with none.");
        Assert.Equal(Path.Combine(root.Machine, "bin", ToolFileName("cc-pdf")), pdf.BinaryPath);
    }

    [Fact]
    public void The_tool_catalog_does_not_find_a_copy_left_inside_a_Directors_folder()
    {
        // The copies this mission deletes are still on disk while it lands. A reader that preferred
        // them would keep reporting a Director's own ageing copy as the installed product.
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");
        root.DirectorFile("a stale copy", "bin", ToolFileName("cc-pdf"));

        var pdf = new ToolCatalogService().GetCatalog().Single(t => t.Name == "cc-pdf");

        Assert.False(pdf.IsBuilt);
    }

    [Fact]
    public void The_tool_catalog_in_a_rig_root_reads_that_rigs_own_tools()
    {
        using var root = new PinnedRoot(rigRootName: "rig-9f2c");
        root.MachineFile("not a real binary", "bin", ToolFileName("cc-pdf"));

        var pdf = new ToolCatalogService().GetCatalog().Single(t => t.Name == "cc-pdf");

        Assert.True(pdf.IsBuilt);
        Assert.Equal(Path.Combine(root.Pinned, "bin", ToolFileName("cc-pdf")), pdf.BinaryPath);
    }

    // ---------------------------------------------------------------- the worktree pool

    [Fact]
    public void The_worktree_pool_resolves_cc_worktrees_at_the_machine_root()
    {
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");
        using var noExplicitPath = new ClearedVariable(CcWorktreesPool.ExecutableEnvVar);
        var installed = root.MachineFile(
            "not a real binary", "bin", ToolFileName(CcWorktreesPool.ToolName));

        // Compared without regard to case: the resolver re-attaches the extension from PATHEXT, which
        // Windows publishes in upper case, so the file it found and the file we wrote differ in spelling
        // and name the same file.
        Assert.Equal(installed, CcWorktreesPool.ResolveExecutable(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_worktree_pool_in_a_rig_root_resolves_that_rigs_own_copy()
    {
        using var root = new PinnedRoot(rigRootName: "rig-9f2c");
        using var noExplicitPath = new ClearedVariable(CcWorktreesPool.ExecutableEnvVar);
        var installed = root.MachineFile(
            "not a real binary", "bin", ToolFileName(CcWorktreesPool.ToolName));

        Assert.Equal(installed, CcWorktreesPool.ResolveExecutable(), StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the browser harness installer

    [Fact]
    public void The_browser_harness_installs_beside_the_machines_tools()
    {
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");

        // Its own environment, the interpreter it is built from, and the shim that puts it on PATH all
        // have to land in the same place. A shim written into the machine's bin that pointed at an
        // environment inside one Director's folder would leave every other Director on the machine
        // running a harness from a folder it does not own.
        Assert.Equal(Path.Combine(root.Machine, "harness-env"), BrowserHarnessInstaller.EnvDir);
        Assert.StartsWith(
            Path.Combine(root.Machine, "python") + Path.DirectorySeparatorChar,
            BrowserHarnessInstaller.BundledPython,
            StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(root.Machine, "bin"), BrowserHarnessInstaller.ShimDir);
    }

    [Fact]
    public void The_browser_harness_in_a_rig_root_stays_inside_that_rig()
    {
        using var root = new PinnedRoot(rigRootName: "rig-9f2c");

        Assert.Equal(Path.Combine(root.Pinned, "harness-env"), BrowserHarnessInstaller.EnvDir);
        Assert.StartsWith(
            Path.Combine(root.Pinned, "python") + Path.DirectorySeparatorChar,
            BrowserHarnessInstaller.BundledPython,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what the About boxes report

    [Fact]
    public void About_reports_the_machines_install_root_and_reads_its_manifest()
    {
        using var root = new PinnedRoot(machineRootName: "cc-director", directorSlug: "slot-5");
        root.MachineFile("""{"python-tools":"2.7.0"}""", "config", "setup", "installed.json");

        Assert.Equal(root.Machine, AboutInfo.InstallRoot);

        var components = AboutInfo.InstalledComponents();
        Assert.True(components.TryGetValue("python-tools", out var version),
            "About read this Director's own folder for the install manifest, so a named Director reports "
            + "every component as missing and every Python tool's version as unknown.");
        Assert.Equal("2.7.0", version);
    }

    [Fact]
    public void About_in_a_rig_root_reports_that_rigs_own_install()
    {
        using var root = new PinnedRoot(rigRootName: "rig-9f2c");
        root.MachineFile("""{"python-tools":"9.9.9"}""", "config", "setup", "installed.json");

        Assert.Equal(root.Pinned, AboutInfo.InstallRoot);
        Assert.Equal("9.9.9", AboutInfo.InstalledComponents()["python-tools"]);
    }
}

/// <summary>
/// Clears one environment variable for the life of a test and puts it back.
///
/// Used for the explicit overrides a reader checks BEFORE it looks at the installed layout: with one
/// of those set on the machine running the suite, the reader answers from it and never reaches the
/// code under test - a pass that means nothing, on a machine nobody thought to check.
/// </summary>
internal sealed class ClearedVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public ClearedVariable(string name)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
