using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The retired-launcher purge on macOS and Linux, where a shim is a SYMLINK and the proof is where it
/// points (#3147).
///
/// Before this, the purge was structurally inert off Windows: the only candidate per retired name is a
/// <c>~/.local/bin/&lt;name&gt;</c> symlink, the safety rule demanded the product's own forwarding line
/// in the file's CONTENTS, and a symlink has no contents of its own to carry one. Every retired name was
/// therefore kept on every Mac, and nothing said so.
///
/// These tests pin both halves of the replacement: a link into THIS install's own pyenv bin is removed,
/// and everything else in that shared directory survives. The directory really is shared - on the Mac
/// this was confirmed on, <c>~/.local/bin</c> also held hand-installed links to a language runtime and a
/// shell - so "it is a symlink" and "it is in our shim directory" are both true of files no install
/// could ever put back. Only the target is specific to us.
/// </summary>
public sealed class MacLegacyAliasSymlinkPurgeTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly PythonToolsInstaller _installer;

    /// <summary>A name the product has retired, so the purge is allowed to LOOK at it.</summary>
    private const string RetiredName = "cc-playwright";

    public MacLegacyAliasSymlinkPurgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        _installer = new PythonToolsInstaller(_layout);
        Directory.CreateDirectory(_layout.ToolShimDir);
        Directory.CreateDirectory(_layout.PyenvBinDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string ShimPath(string name) => Path.Combine(_layout.ToolShimDir, name);

    /// <summary>Link a shim name at this install's own pyenv bin, exactly as WriteUnixShims does.</summary>
    private string LinkToOurPyenv(string name, bool targetExists)
    {
        var target = Path.Combine(_layout.PyenvBinDir, name);
        if (targetExists) File.WriteAllText(target, "fake console script");
        var link = ShimPath(name);
        File.CreateSymbolicLink(link, target);
        return link;
    }

    [UnixOnlyFact("the purge proves a Unix shim by its link target; Windows takes the text-shim branch")]
    public void ALinkIntoThisInstallsOwnPyenv_IsRemoved()
    {
        var link = LinkToOurPyenv(RetiredName, targetExists: true);

        _installer.RemoveLegacyAliasShims();

        Assert.False(Path.Exists(link), "a retired name linked into this install's own pyenv bin is ours to remove");
    }

    [UnixOnlyFact("a dangling link is the fault being cleaned up and only exists off Windows as a symlink")]
    public void ADanglingLinkIntoOurPyenv_IsStillRemoved_BecauseThatIsTheWholePoint()
    {
        // The venv exe is already gone - which is exactly the state that makes a retired shim print
        // "cc-* tools are not fully installed" on a healthy machine. A missing target must not read as
        // a reason to keep the shim.
        var link = LinkToOurPyenv(RetiredName, targetExists: false);
        Assert.False(File.Exists(Path.Combine(_layout.PyenvBinDir, RetiredName)), "the target must be absent for this test to mean anything");

        _installer.RemoveLegacyAliasShims();

        Assert.False(Path.Exists(link), "a dangling link is judged by its target PATH, not by whether the target exists");
    }

    [UnixOnlyFact("the shim directory is shared, so a link the product did not write must survive")]
    public void ALinkPointingAnywhereElse_IsKept_AndTheKeepIsReported()
    {
        // The shape of a launcher a person made for a tool they still run: same name, same directory,
        // a link - but pointing at their own build, which no install could put back.
        var theirBuild = Path.Combine(_dir, "their-own-build", RetiredName);
        Directory.CreateDirectory(Path.GetDirectoryName(theirBuild)!);
        File.WriteAllText(theirBuild, "a build the owner made");
        var link = ShimPath(RetiredName);
        File.CreateSymbolicLink(link, theirBuild);

        _installer.RemoveLegacyAliasShims();

        Assert.True(Path.Exists(link), "a link that does not point into this install's pyenv bin is not ours to delete");

        // Said out loud, so a survivor is distinguishable from a file the purge never looked at.
        var kept = _installer.FindHandWrittenLegacyAliasLaunchers();
        var entry = Assert.Single(kept, k => k.Path == link);
        Assert.Contains("not a symlink to this install's own pyenv bin", entry.Reason);
    }

    [UnixOnlyFact("a link to the right directory but the wrong name is not this name's shim")]
    public void ALinkIntoOurPyenvUnderADifferentName_IsKept()
    {
        var target = Path.Combine(_layout.PyenvBinDir, "cc-devthrottle"); // a live tool, not the retired name
        File.WriteAllText(target, "fake console script");
        var link = ShimPath(RetiredName);
        File.CreateSymbolicLink(link, target);

        _installer.RemoveLegacyAliasShims();

        Assert.True(Path.Exists(link), "the target must be this install's pyenv bin entry FOR THIS NAME");
    }

    [UnixOnlyFact("off Windows the product only ever writes a symlink in the shim directory")]
    public void ARealFileAtARetiredName_IsKept()
    {
        var file = ShimPath(RetiredName);
        File.WriteAllText(file, "#!/bin/sh\nexec /somewhere/else \"$@\"\n");

        _installer.RemoveLegacyAliasShims();

        Assert.True(File.Exists(file), "a real file is not a link the product wrote, so it is kept");
    }

    [UnixOnlyFact("the retired-name list is what buys permission to look at a file at all")]
    public void ALiveToolsShim_IsNeverACandidate()
    {
        // cc-devthrottle ships, so it is on no retired list - its shim must survive a purge even though
        // it is a link into our own pyenv bin, which is the one shape the purge removes.
        var link = LinkToOurPyenv("cc-devthrottle", targetExists: true);

        _installer.RemoveLegacyAliasShims();

        Assert.True(Path.Exists(link), "a shipping tool's shim is not a retired name and must never be touched");
    }
}
