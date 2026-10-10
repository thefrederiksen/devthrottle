using System.Runtime.Versioning;
using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Puts DevThrottle in Windows "Settings > Apps", so it can be removed the way every other
/// Windows program is removed.
///
/// The capability to write that entry already existed (<see cref="AddRemovePrograms"/>), the
/// uninstaller already removed it, and the uninstall screen already listed it among the things it
/// would take away - but NOTHING EVER WROTE IT. A registry scan of both hives found fifteen
/// programs and not one of them was this one. So the product installed, added a Start menu
/// shortcut, and offered no advertised way off the machine: to remove it you had to still have the
/// setup executable you most likely deleted from Downloads. A Windows program that cannot be
/// uninstalled from Settings is behaviour people associate with malware, and it was the last
/// impression the product made.
///
/// Two things have to be true for the entry to work, which is why they are done together here:
///   1. The entry exists, with the values Windows needs to render the row.
///   2. Its UninstallString points at an executable that will STILL BE THERE later. The running
///      setup exe is wherever the user happened to download it, so a copy is kept inside the
///      install root and the entry points at the copy.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UninstallRegistration
{
    /// <summary>The switch that sends the setup executable straight to its uninstall screen.</summary>
    public const string UninstallSwitch = "/uninstall";

    /// <summary>Where the retained copy of the setup executable lives inside the install root.</summary>
    public static string SetupCopyPath(InstallLayout layout) =>
        Path.Combine(layout.LocalRoot, "setup", "cc-director-setup.exe");

    /// <summary>
    /// Register (or refresh) the Apps &amp; features entry. Runs on install AND update, so an
    /// existing machine that never had the entry gains it on the next update. Best-effort: a
    /// failure here must never fail the install, because everything the user actually asked for
    /// is already on disk by this point.
    /// </summary>
    public static void Register(InstallLayout layout, string appExePath)
    {
        try
        {
            var setupCopy = KeepACopyOfSetup(layout);
            if (setupCopy == null)
            {
                SetupLog.Write("[UninstallRegistration] no usable setup executable to point at - entry NOT written");
                return;
            }

            // Quoted: the path contains spaces on any normal machine (C:\Users\First Last\...).
            var uninstallCommand = $"\"{setupCopy}\" {UninstallSwitch}";

            // The version stamped on this setup exe is the version it just installed.
            var version = SetupExecutableVersion.Read();
            if (string.IsNullOrWhiteSpace(version))
                version = "0.0.0";

            // The icon is the APP's, not the installer's - the row should look like DevThrottle.
            var icon = File.Exists(appExePath) ? appExePath : setupCopy;

            AddRemovePrograms.Register(
                version: version,
                uninstallCommand: uninstallCommand,
                installLocation: layout.LocalRoot,
                displayIcon: icon,
                estimatedSizeKb: EstimatedSizeKb(layout.LocalRoot));

            SetupLog.Write($"[UninstallRegistration] registered: version={version}, uninstall={uninstallCommand}");
        }
        catch (Exception ex)
        {
            // Logged, not thrown. The install succeeded; only its discoverability in Settings did not.
            SetupLog.Write($"[UninstallRegistration] Register FAILED: {ex.Message}");
            _ = WizardProgressReport.Error("register-uninstall", $"Windows setup wizard could not register DevThrottle in Settings > Apps: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Is this process the retained copy, running from inside the install root, asked to uninstall?
    ///
    /// If so it must NOT do the uninstall from here: it would be holding a file inside the very
    /// directory tree it is about to delete, so "Also delete my data" - which removes the whole
    /// per-user root - would fail on a locked executable and report the uninstall as finished with
    /// problems. The answer is the one every real Windows uninstaller uses: run from somewhere else.
    /// </summary>
    public static bool ShouldRelaunchFromTemp(InstallLayout layout)
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(running))
            return false;

        var root = Path.GetFullPath(layout.LocalRoot);
        var me = Path.GetFullPath(running);

        return me.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy this executable to the temp directory and start it there with the same switch, so the
    /// install root is left entirely unlocked. Returns true when the replacement is running and
    /// this process should exit. The copy lands OUTSIDE the install root, so the relaunched process
    /// answers false to <see cref="ShouldRelaunchFromTemp"/> and there is no way to loop.
    /// </summary>
    public static bool RelaunchFromTemp()
    {
        try
        {
            var running = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(running) || !File.Exists(running))
                return false;

            var stagingDir = Path.Combine(Path.GetTempPath(), "devthrottle-uninstall");
            Directory.CreateDirectory(stagingDir);
            var staged = Path.Combine(stagingDir, "cc-director-setup.exe");
            File.Copy(running, staged, overwrite: true);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = staged,
                Arguments = UninstallSwitch,
                UseShellExecute = false,
            });

            SetupLog.Write($"[UninstallRegistration] relaunched the uninstaller from {staged} so the install root is unlocked");
            return true;
        }
        catch (Exception ex)
        {
            // Could not stage a copy: carry on in place. The uninstall still works; only the
            // "also delete my data" wipe may leave this one executable behind.
            SetupLog.Write($"[UninstallRegistration] could not relaunch from temp, continuing in place: {ex.Message}");
            _ = WizardProgressReport.Error("uninstall-relaunch", $"Windows uninstall could not relaunch from a temporary copy and continued in place: {ex.GetType().Name}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Copy the running setup executable into the install root and return the copy's path, or null
    /// if there is nothing to copy. Returns the existing copy's path when the file is locked -
    /// which is exactly what happens when setup is re-run FROM that copy to uninstall.
    /// </summary>
    private static string? KeepACopyOfSetup(InstallLayout layout)
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(running) || !File.Exists(running))
            return null;

        var destination = SetupCopyPath(layout);

        // Already running from the copy: nothing to do, and copying a file onto itself throws.
        if (string.Equals(Path.GetFullPath(running), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return destination;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(running, destination, overwrite: true);
            SetupLog.Write($"[UninstallRegistration] kept a copy of setup at {destination}");
            return destination;
        }
        catch (IOException ex)
        {
            // The old copy is locked (an earlier setup is still running from it). An existing copy
            // is still a working uninstaller, so point at it rather than abandoning the entry.
            SetupLog.Write($"[UninstallRegistration] could not refresh the setup copy: {ex.Message}");
            return File.Exists(destination) ? destination : null;
        }
    }

    /// <summary>
    /// Installed size in kilobytes for the Settings row. Best-effort by design: this is a cosmetic
    /// number, so an unreadable directory returns what was counted so far rather than failing the
    /// registration over it.
    /// </summary>
    private static int EstimatedSizeKb(string root)
    {
        try
        {
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; }
                catch { /* Not reported: a file that vanished mid-walk is not worth failing over */ }
            }

            // Windows stores EstimatedSize as a DWORD of kilobytes.
            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch (Exception ex)
        {
            // Not reported: the size is a hint Settings > Apps shows; the registration itself went ahead.
            SetupLog.Write($"[UninstallRegistration] could not measure the install size: {ex.Message}");
            return 0;
        }
    }
}
