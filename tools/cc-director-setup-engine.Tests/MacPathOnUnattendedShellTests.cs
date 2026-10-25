using System.Diagnostics;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Which shell start-up file the macOS install writes its PATH block to, and the behaviour that choice
/// decides: whether a cc-* tool is on PATH for a shell nobody is sitting in front of.
///
/// This is #4292. The install appended the block to ~/.zshrc, and zsh reads .zshrc only for INTERACTIVE
/// shells - so on a healthy Mac no cc-* tool was on PATH for a script, cron, launchd, continuous
/// integration, or a tool spawned by another program. Every tool was installed, every tool ran, and an
/// unattended shell could not find one of them.
///
/// The file-name tests take the home folder as a parameter so they run on any platform, which is the
/// discipline the rest of this engine's macOS branches follow. The behavioural test runs a real zsh,
/// because no assertion about a list of names could have caught this defect.
/// </summary>
public class MacPathOnUnattendedShellTests
{
    private const string Home = "/Users/someone";

    [Fact]
    public void TheBlockIsWrittenToZshenv_BecauseItIsTheOnlyFileEveryKindOfShellReads()
    {
        var written = InstallFinalizer.MacPathWriteFiles(Home);

        Assert.Contains(Path.Combine(Home, ".zshenv"), written);
    }

    [Fact]
    public void TheBlockIsNoLongerWrittenToZshrc_SoNoSecondRedundantBlockIsEverAppended()
    {
        // .zshenv covers every shell .zshrc covered, so writing both would append a second block that
        // does nothing, for ever. This is about what the install WRITES - not what an uninstall cleans.
        var written = InstallFinalizer.MacPathWriteFiles(Home);

        Assert.DoesNotContain(Path.Combine(Home, ".zshrc"), written);
    }

    [Fact]
    public void AnUninstallStillCleansZshrc_SoAMachineInstalledBeforeThisIsNotLeftWithAnOrphanedBlock()
    {
        // Every machine installed before #4292 carries the block in .zshrc. The uninstaller walks this
        // list, so .zshrc has to stay on it for ever even though nothing writes there any more.
        var cleaned = InstallFinalizer.MacShellRcFiles(Home);

        Assert.Contains(Path.Combine(Home, ".zshrc"), cleaned);
        Assert.Contains(Path.Combine(Home, ".zshenv"), cleaned);
    }

    [Fact]
    public void EverythingWrittenIsAlsoCleaned()
    {
        var written = InstallFinalizer.MacPathWriteFiles(Home);
        var cleaned = InstallFinalizer.MacShellRcFiles(Home);

        foreach (var file in written)
            Assert.Contains(file, cleaned);
    }

    [ZshOnlyFactAttribute("the whole defect is about which start-up file zsh reads for which shell")]
    public void TheToolFolderIsOnPathForANonInteractiveShell_WhichIsWhatCronAndLaunchdGet()
    {
        var home = Path.Combine(Path.GetTempPath(), "ccd-zshenv-" + Guid.NewGuid().ToString("N"));
        var localBin = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(localBin);
        try
        {
            // A stand-in for a tool shim, so the assertion is that a TOOL is reachable - not merely that
            // a string appears in PATH.
            var shim = WriteProbe(localBin);

            // Exactly what the install appends, in the file the install now appends it to.
            File.WriteAllText(Path.Combine(home, ".zshenv"), InstallFinalizer.MacPathBlock());

            // -c with no -i and no -l: a shell nobody is sitting in front of. This is cron, launchd, a
            // script, and a tool spawned by another program.
            var found = RunZsh(home, NonInteractive, "command -v cc-probe");
            Assert.Equal(shim, found.Trim());

            // And it is genuinely runnable, not just a path that resolved.
            Assert.Equal("probe-ran", RunZsh(home, NonInteractive, "cc-probe").Trim());
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    [ZshOnlyFactAttribute("proves the test above would have caught the defect instead of passing anyway")]
    public void TheSameProbeFailsWhenTheBlockIsInZshrc_WhichIsTheDefectThisFixIsFor()
    {
        // The negative control. Without it the test above could pass for a reason that has nothing to do
        // with .zshenv - a tool already on the machine's PATH, or an inherited environment - and would
        // then certify the very defect it is meant to rule out.
        var home = Path.Combine(Path.GetTempPath(), "ccd-zshrc-" + Guid.NewGuid().ToString("N"));
        var localBin = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(localBin);
        try
        {
            var shim = WriteProbe(localBin);

            // The old behaviour: the block in .zshrc, and nothing in .zshenv.
            File.WriteAllText(Path.Combine(home, ".zshrc"), InstallFinalizer.MacPathBlock());

            Assert.Equal("", RunZsh(home, NonInteractive, "command -v cc-probe").Trim());

            // An interactive login shell DOES find it, which is why the defect looked like nothing was
            // wrong to anyone typing at a terminal.
            Assert.Equal(shim, RunZsh(home, InteractiveLogin, "command -v cc-probe").Trim());
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A stand-in tool shim, made executable with chmod rather than File.SetUnixFileMode: that call is
    /// annotated unsupported on Windows, and this file compiles for every platform even though these
    /// tests only RUN where there is a zsh.
    /// </summary>
    private static string WriteProbe(string localBin)
    {
        var shim = Path.Combine(localBin, "cc-probe");
        File.WriteAllText(shim, "#!/bin/sh\necho probe-ran\n");
        using var chmod = Process.Start("/bin/chmod", ["+x", shim])
                          ?? throw new InvalidOperationException("could not start chmod");
        chmod.WaitForExit(30_000);
        Assert.Equal(0, chmod.ExitCode);
        return shim;
    }

    /// <summary>
    /// Run zsh with an EMPTY environment but for HOME and TERM, so the machine's own PATH cannot leak in
    /// and answer for the start-up file under test.
    /// </summary>
    /// <summary>A shell nobody is sitting in front of: cron, launchd, a script, a spawned tool.</summary>
    private static readonly string[] NonInteractive = [];

    /// <summary>What a person at a terminal gets, and the only kind that ever read .zshrc.</summary>
    private static readonly string[] InteractiveLogin = ["-i", "-l"];

    private static string RunZsh(string home, string[] interactivityFlags, string command)
    {
        var psi = new ProcessStartInfo("/usr/bin/env")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("HOME=" + home);
        psi.ArgumentList.Add("TERM=xterm");
        psi.ArgumentList.Add("/bin/zsh");
        foreach (var flag in interactivityFlags) psi.ArgumentList.Add(flag);
        // -c LAST and always: with a bare operand instead, zsh reads the argument as the name of a
        // SCRIPT FILE and finds nothing, which looks exactly like a PATH that was never set.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start zsh");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return stdout;
    }
}
