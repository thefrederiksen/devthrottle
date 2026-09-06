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
/// WHAT THIS PROVES AND WHAT IT DOES NOT. It reads one file and asserts a call, so it catches the edit
/// that is actually likely: somebody with a launcher that will not die flipping the flag to make it die.
/// It does NOT catch the same kill moved into a helper elsewhere, which is the known weakness of every
/// source-text guard (see NoListenerDependencyGuardTests, which chose a dependency assertion for exactly
/// that reason). A dependency assertion cannot help here, because what has to be asserted is the VALUE
/// OF AN ARGUMENT, and both values come from the same method on the same type. Proved against a
/// known-bad input before it was trusted: with entireProcessTree flipped to true, this test fails and
/// every other test in this project still passes.
/// </summary>
public class LauncherSwapNeverKillsATreeTests
{
    [Fact]
    public void TheDirectorsLauncherSwap_KillsOneProcess_NeverATree()
    {
        var source = File.ReadAllText(OwnerSourcePath());

        Assert.Contains("entireProcessTree: false", source);
        Assert.DoesNotContain("entireProcessTree: true", source);
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
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, marker)),
                $"Resolved the repository root to {root}, but it does not contain {marker}. This guard was "
                + "built from a tree it can no longer find, so it is not reading the product.");
        }

        return root;
    }
}
