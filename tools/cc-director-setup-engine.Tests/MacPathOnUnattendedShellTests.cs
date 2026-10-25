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
/// Every test takes the home folder as a parameter and starts no program, so it runs on any platform - the
/// discipline the rest of this engine's macOS branches follow.
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

    // ---- Which files zsh reads, and so whether the block reaches each kind of shell ---------------------------------
    //
    // THESE USED TO RUN A REAL ZSH, and so they ran only where there was one (skipped everywhere else) and let a real
    // process decide a gate test, which the test standard forbids. What zsh reads for which kind of shell is a fact
    // about zsh, documented in zsh(1) under STARTUP/SHUTDOWN FILES - not something this code decides - so it is stated
    // here once, as data, and the install's choice of files is checked against it for every kind of shell. The
    // defect (#4292) was exactly a write list that no unattended shell reads, and the negative control below shows
    // that the old list fails this check.

    /// <summary>The start-up files zsh reads, by kind of shell (zsh(1), STARTUP/SHUTDOWN FILES; .zshenv always).</summary>
    private static readonly (string Kind, string[] Reads)[] ZshReads =
    {
        ("unattended: a script, cron, launchd, a spawned tool (zsh -c)", new[] { ".zshenv" }),
        ("interactive (zsh -i)", new[] { ".zshenv", ".zshrc" }),
        ("interactive login (zsh -i -l, a terminal window)", new[] { ".zshenv", ".zprofile", ".zshrc", ".zlogin" }),
    };

    /// <summary>The kinds of shell that read NONE of <paramref name="written"/> - each one a shell with no cc-* tool on PATH.</summary>
    private static IReadOnlyList<string> ShellsThatMissTheBlock(IEnumerable<string> written)
    {
        var names = written.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        return ZshReads.Where(shell => !shell.Reads.Any(names.Contains)).Select(shell => shell.Kind).ToList();
    }

    [Fact]
    public void EveryKindOfZshReadsAFileTheBlockIsWrittenTo_IncludingTheUnattendedOnesCronAndLaunchdGet()
    {
        Assert.Empty(ShellsThatMissTheBlock(InstallFinalizer.MacPathWriteFiles(Home)));
    }

    [Fact]
    public void TheOldWriteListLeavesTheUnattendedShellWithoutTheBlock_WhichIsTheDefectThisFixIsFor()
    {
        // The negative control: the block in .zshrc alone - the behaviour before #4292 - reaches an interactive shell
        // and no unattended one. Without this, the check above could pass for a reason that has nothing to do with
        // .zshenv.
        var missed = ShellsThatMissTheBlock(new[] { Path.Combine(Home, ".zshrc") });

        Assert.Equal(new[] { ZshReads[0].Kind }, missed);
    }

    [Fact]
    public void TheBlockPutsTheToolFolderFirstOnPath_OnceOnly()
    {
        // What the block does once a shell reads it: put ~/.local/bin (where every cc-* shim lives) at the front of
        // PATH, and only when it is not already there, so a shell that reads more than one start-up file does not
        // repeat it.
        var block = InstallFinalizer.MacPathBlock();

        Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", block);
        Assert.Contains("case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;;", block);
    }
}
