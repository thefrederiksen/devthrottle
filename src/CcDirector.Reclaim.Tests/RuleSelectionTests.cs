using CcDirector.Reclaim.Rules;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// Which rules apply to the folder a caller asked about.
///
/// A rule looks at one known place, so asking about a small folder must not be answered with
/// gigabytes found somewhere else on the machine. The rules left out are named, never merely absent:
/// a tool that quietly ran fewer rules than it has reports less to remove and reads exactly like a
/// cleaner disk.
/// </summary>
public class RuleSelectionTests
{
    [Fact]
    public void For_ARuleThatLooksInsideTheFolderAskedAbout_IsRun()
    {
        var inside = Rule("inside", Path.Combine(Root(), "Windows", "Installer"));

        var selection = RuleSelection.For([inside], Root());

        Assert.Same(inside, Assert.Single(selection.ToRun));
        Assert.Empty(selection.NotRun);
    }

    [Fact]
    public void For_ARuleThatLooksSomewhereElse_IsNamedRatherThanQuietlyDropped()
    {
        var elsewhere = Rule("elsewhere", Path.Combine(Root(), "Users", "someone", "cache"));

        var selection = RuleSelection.For([elsewhere], Path.Combine(Root(), "Windows"));

        Assert.Empty(selection.ToRun);
        var named = Assert.Single(selection.NotRun);
        Assert.Equal("elsewhere", named.RuleId);
        Assert.Equal(Path.Combine(Root(), "Users", "someone", "cache"), named.LooksIn);
    }

    [Fact]
    public void For_ARuleThatLooksAtExactlyTheFolderAskedAbout_IsRun()
    {
        var same = Rule("same", Path.Combine(Root(), "Windows"));

        var selection = RuleSelection.For([same], Path.Combine(Root(), "Windows"));

        Assert.Single(selection.ToRun);
    }

    /// <summary>
    /// The trap in every path comparison written by hand. Without a separator forced onto the end of
    /// the folder being compared against, one folder is read as sitting inside another purely because
    /// its name starts with the same letters.
    /// </summary>
    [Fact]
    public void IsInside_AFolderWhoseNameMerelyStartsWithTheOtherName_IsNotInside()
    {
        var bob = Path.Combine(Root(), "Users", "bob");
        var bobby = Path.Combine(Root(), "Users", "bobby");

        Assert.False(RuleSelection.IsInside(bobby, bob));
        Assert.True(RuleSelection.IsInside(Path.Combine(bob, "cache"), bob));
    }

    [Fact]
    public void IsInside_AFolderWithATrailingSeparator_IsJudgedTheSameAsOneWithout()
    {
        var withSeparator = Path.Combine(Root(), "Windows") + Path.DirectorySeparatorChar;
        var without = Path.Combine(Root(), "Windows");

        Assert.True(RuleSelection.IsInside(without, withSeparator));
        Assert.True(RuleSelection.IsInside(withSeparator, without));
    }

    /// <summary>Everything on a volume is inside that volume's own root.</summary>
    [Fact]
    public void IsInside_AnythingAtAll_IsInsideTheRootOfItsVolume()
    {
        Assert.True(RuleSelection.IsInside(Path.Combine(Root(), "Windows", "Installer"), Root()));
    }

    // The root of the volume the tests are running on, so the paths compared here are real paths on
    // this platform rather than strings that only look like paths on one of them.
    private static string Root() => Path.GetPathRoot(Path.GetTempPath())!;

    private static IReclaimRule Rule(string id, string looksIn) => new StubRule(id, looksIn);

    private sealed class StubRule(string id, string looksIn) : IReclaimRule
    {
        public string Id => id;
        public string Name => $"A rule called {id}";
        public ProofKind Proof => ProofKind.WeMadeIt;
        public string WhatItRemoves => "nothing, it exists to be sorted";
        public string WhyItIsSafe => "it is never run";
        public string WhatIsLost => "nothing";
        public string HowToGetItBack => "there is nothing to get back";
        public int AgeGateDays => 1;
        public bool NeedsAdministrator => false;
        public string? CommandToRun => null;
        public string LooksIn => looksIn;

        public RuleAnswer Examine(RuleContext context) =>
            throw new InvalidOperationException("A rule sorted by where it looks is never examined here.");
    }
}
