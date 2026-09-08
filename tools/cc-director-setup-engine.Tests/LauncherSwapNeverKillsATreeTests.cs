using System.Runtime.CompilerServices;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// THE ONE RULE IN THE LAUNCHER SWAP THAT NO BEHAVIOURAL TEST CAN REACH: it must never kill a process
/// TREE.
///
/// On Windows the launcher is the Director's parent process, and the Director is the process performing
/// the swap. A tree kill aimed at the launcher would therefore end the swap halfway through, with the
/// launcher binary already renamed aside and nothing left running to put it back or to start what was
/// installed. The uninstaller kills trees on purpose - the whole install is going away there - so the
/// two stops look alike and the wrong one is one word away.
///
/// WHAT THIS PROVES AND WHAT IT DOES NOT. This began as a source-text scan of ONE file for ONE
/// spelling of the argument, which is the weakest kind of proof: it saw the edit it was written for and
/// nothing else, and in particular it could not see the same kill moved into a helper. The kill has
/// since been moved deliberately - it lives in SingleProcessStop, a type whose entire purpose is the
/// single-process form - which turns the weak assertion into a strong one. The file that performs the
/// swap now contains NO process kill at all, so what is asserted about it is a complete absence for
/// that file rather than the absence of one string, and there is no argument in it left to flip.
///
/// The residual limit, stated rather than hidden: this still cannot see a kill written into some third
/// file and called from the swap. What it can see is that the swap's own file expresses none, and that
/// the one type it delegates to expresses only the single-process form. Proved against known-bad
/// inputs before it was trusted: with entireProcessTree flipped to true in SingleProcessStop, and
/// separately with a Kill call put back into LauncherUpdateOwner, the matching test fails and every
/// other test in this project still passes.
/// </summary>
public class LauncherSwapNeverKillsATreeTests
{
    [Fact]
    public void TheFileThatPerformsTheSwap_ExpressesNoProcessKillAtAll()
    {
        // The strong form. Not "it does not say entireProcessTree: true" - it does not kill anything,
        // so the argument that could be got wrong is not written here to get wrong.
        var source = File.ReadAllText(OwnerSourcePath());

        Assert.DoesNotContain(".Kill(", source);
        Assert.DoesNotContain("entireProcessTree", source);
    }

    [Fact]
    public void TheOnlyKillInTheSwapPath_KillsOneProcess_NeverATree()
    {
        var source = File.ReadAllText(SingleProcessStopSourcePath());

        Assert.Contains("entireProcessTree: false", source);
        Assert.DoesNotContain("entireProcessTree: true", source);
        // One kill, not one correct kill beside another.
        Assert.Equal(1, CountOccurrences(source, ".Kill("));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void TheUninstallersStop_IsAllowedToKillATree_AndIsADifferentFile()
    {
        // Stated so the difference is deliberate rather than accidental: if these two ever end up in one
        // place, the reason the Director's swap must not do this is the thing that gets lost.
        var uninstall = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "cc-director-setup-engine", "LauncherStopper.cs"));

        Assert.Contains("entireProcessTree: true", uninstall);
    }

    private static string OwnerSourcePath()
        => Path.Combine(RepoRoot(), "tools", "cc-director-setup-engine", "LauncherUpdateOwner.cs");

    private static string SingleProcessStopSourcePath()
        => Path.Combine(RepoRoot(), "tools", "cc-director-setup-engine", "SingleProcessStop.cs");

    /// <summary>
    /// The repository this test file was compiled from, checked against markers so a path that exists
    /// but is not this repository fails loudly instead of being read as though it were the product.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/tools/cc-director-setup-engine.Tests/LauncherSwapNeverKillsATreeTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        var root = Path.GetFullPath(Path.Combine(dir, "..", ".."));

        foreach (var marker in new[]
                 {
                     Path.Combine("tools", "cc-director-setup-engine", "LauncherUpdateOwner.cs"),
                     Path.Combine("tools", "cc-director-setup-engine", "LauncherStopper.cs"),
                     Path.Combine("tools", "cc-director-setup-engine", "SingleProcessStop.cs"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, marker)),
                $"Resolved the repository root to {root}, but it does not contain {marker}. This guard was "
                + "built from a tree it can no longer find, so it is not reading the product.");
        }

        return root;
    }
}
