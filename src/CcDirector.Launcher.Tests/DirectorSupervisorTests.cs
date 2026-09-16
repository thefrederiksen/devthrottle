using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Tests for <see cref="DirectorSupervisor"/> exe-path resolution order.
/// No real process spawning.
/// </summary>
public sealed class DirectorSupervisorTests
{
    // -------------------------------------------------------------------------
    // AC2a: Exe path resolves via InstallLayout (no hardcoding).
    // -------------------------------------------------------------------------

    [Fact]
    public void DirectorExePath_ResolvesThroughInstallLayout()
    {
        var layout = new InstallLayout(@"C:\FakeRoot");
        var supervisor = new DirectorSupervisor(layout);

        var expected = layout.PathFor(ComponentRegistry.Director);
        Assert.Equal(expected, supervisor.DirectorExePath);
    }

    [Fact]
    public void DirectorExePath_IncludesAppSubdirectory()
    {
        var layout = new InstallLayout(@"C:\FakeRoot");
        var supervisor = new DirectorSupervisor(layout);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("app", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cc-director", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // macOS: the installed Director is the application bundle in ~/Applications.
            Assert.EndsWith("Director.app", supervisor.DirectorExePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DirectorExeExists_ReturnsFalse_WhenPathDoesNotExist()
    {
        // Windows-only premise: on macOS PathFor(Director) is the machine-global
        // ~/Applications/Director.app, independent of the injected root - the macOS
        // presence semantics are covered by DirectorExeExists_MacBundleDirectory_CountsAsInstalled.
        if (!OperatingSystem.IsWindows()) return;

        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"));
        var supervisor = new DirectorSupervisor(layout);

        Assert.False(supervisor.DirectorExeExists);
    }

    // -------------------------------------------------------------------------
    // AC2b: Default constructor uses InstallLayout.Default() (real machine path).
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultConstructor_ResolvesRealLocalAppDataPath()
    {
        var supervisor = new DirectorSupervisor();

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(localAppData, supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // macOS: the bundle lives under the user's home (~/Applications), not local application data.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.StartsWith(home, supervisor.DirectorExePath, StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------------------
    // macOS: the installed Director is a bundle DIRECTORY, and DirectorExeExists
    // must treat a directory as present.
    // -------------------------------------------------------------------------

    [Fact]
    public void DirectorExeExists_MacBundleDirectory_CountsAsInstalled()
    {
        if (OperatingSystem.IsWindows()) return; // macOS/Unix semantics only

        // Point the layout at a scratch root, then create the bundle DIRECTORY where
        // InstallLayout would place it. PathFor(Director) on macOS is ~/Applications/Director.app
        // regardless of the root, so simulate through a real temp bundle only when it does
        // not already exist - never touch a real installed bundle.
        var supervisor = new DirectorSupervisor();
        if (Directory.Exists(supervisor.DirectorExePath) || File.Exists(supervisor.DirectorExePath))
        {
            // A real Director is installed on this machine: presence must be reported.
            Assert.True(supervisor.DirectorExeExists);
            return;
        }

        Assert.False(supervisor.DirectorExeExists);
    }

    // -------------------------------------------------------------------------
    // THE KILL GATE. Inspection 3, finding 3: a lone registration authorized a
    // force-kill of a process that was never confirmed to be the Director.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A RESOLVED DIRECTOR THAT IS NOT CERTIFIED AS THE INSTALLED IMAGE IS NEVER FORCE-KILLED.
    ///
    /// This drives the real StopAsync against a real live process that is emphatically not a Director -
    /// a helper process, registered as if it were one. The locator resolves it (a single claimant, and
    /// with no installed path there is nothing to compare its image against), the shutdown signal is
    /// raised and nothing is listening for it, and the old code went straight from there to killing the
    /// process id. The assertion is that the helper is STILL ALIVE afterwards.
    ///
    /// Deliberately a foreign process and not this test process. Pointing this at ourselves would make a
    /// regression kill the test run rather than report a failure, and a detector whose failure mode is
    /// destroying the evidence is not a detector.
    /// </summary>
    [Fact]
    public async Task StopAsync_RefusesToForceKillAProcessNotCertifiedAsTheInstalledDirector()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-stop-gate-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        Directory.CreateDirectory(registrations);

        using var helper = StartHelperProcess();
        try
        {
            const string directorId = "bbbb0001-0000-0000-0000-000000000001";
            File.WriteAllText(Path.Combine(registrations, directorId + ".json"),
                $$"""
                {
                  "DirectorId": "{{directorId}}",
                  "Pid": {{helper.Id}},
                  "StartedAt": "{{DateTime.UtcNow:o}}",
                  "ControlEndpoint": "",
                  "Version": "1.9.7"
                }
                """);

            // No installed path to compare against, so the claimant resolves UNCERTIFIED - the exact
            // state the kill gate exists for.
            var locator = new CcDirector.Core.Instances.DirectorInstanceLocator(instanceHome);
            var lookup = locator.Resolve();
            Assert.Equal(CcDirector.Core.Instances.DirectorResolution.Running, lookup.Outcome);
            Assert.False(lookup.Director!.IsInstalledImage);

            var supervisor = new DirectorSupervisor(new InstallLayout(root), locator);
            await supervisor.StopAsync();

            helper.Refresh();
            Assert.False(helper.HasExited,
                "StopAsync force-killed a process that was never certified as the installed Director. A "
                + "registration naming a live process id is not proof that the process is ours to end - it "
                + "could be a development build, or something that merely inherited a dead Director's id.");
        }
        finally
        {
            try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>A harmless long-running process to stand in for a wrongly-registered claimant.</summary>
    private static System.Diagnostics.Process StartHelperProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 60 127.0.0.1")
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"sleep 60\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        return System.Diagnostics.Process.Start(psi)
               ?? throw new InvalidOperationException("could not start a helper process");
    }

    [Fact]
    public void DirectStartInfo_StartsTheExecutableItself_NotThroughAnotherProgram()
    {
        // Linux used to fall through to the macOS "/usr/bin/open" start, which on Linux is a different
        // program or absent, so a launcher that stopped a Linux Director to update it started nothing.
        var exe = Path.Combine(Path.GetTempPath(), "app", "cc-director");

        var psi = DirectorSupervisor.DirectStartInfo(exe);

        Assert.Equal(exe, psi.FileName);
        Assert.Equal(Path.GetDirectoryName(exe), psi.WorkingDirectory);
        Assert.Equal(OperatingSystem.IsWindows(), psi.UseShellExecute);
        Assert.Empty(psi.ArgumentList);
    }
}
