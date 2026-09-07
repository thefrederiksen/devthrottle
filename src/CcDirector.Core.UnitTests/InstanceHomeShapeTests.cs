using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.UnitTests;

/// <summary>
/// <see cref="InstanceContext.LooksLikeAnInstanceHome"/> - issue #2720.
///
/// WHY ANYTHING ASKS. A launcher is machine-wide and must serve the machine's SHARED root. On
/// 2026-09-06 one was started from a shell carrying a Director's CC_DIRECTOR_ROOT, inherited it, and
/// took that Director's instance home for the machine root. It registered, heartbeated, opened its
/// command stream and armed both lifecycle signals - all of it filed under a root nothing else
/// computes, so nothing could see it or reach it. From outside it was a perfectly healthy launcher
/// that could restart nothing.
///
/// WHAT THE TEST IS FOR, BEYOND THE OBVIOUS. The check must be NARROW. A launcher serving a throwaway
/// root of its own - a test rig, which is the only safe way to exercise any of this - is a legitimate
/// thing to serve and must answer false. So the shape being detected is the one that is wrong by
/// construction, and the tests below pin both sides of that line rather than only the positive.
/// </summary>
public sealed class InstanceHomeShapeTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    /// <summary>The real shape: InstanceHome composes SharedRoot/instances/slug, so a parent directory
    /// named "instances" is the fault.</summary>
    [Theory]
    [InlineData("root", "instances", "default")]
    [InlineData("root", "instances", "work")]
    [InlineData("a", "b", "cc-director", "instances", "default")]
    public void A_path_whose_parent_is_named_instances_is_an_instance_home(params string[] parts)
        => Assert.True(InstanceContext.LooksLikeAnInstanceHome(P(parts)));

    /// <summary>A trailing separator is the same path. A launcher's root can arrive either way, and a
    /// check that answered differently would report the same machine two ways on two runs.</summary>
    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        var home = P("root", "instances", "default");
        Assert.True(InstanceContext.LooksLikeAnInstanceHome(home));
        Assert.True(InstanceContext.LooksLikeAnInstanceHome(home + Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// THE SIDE THAT MATTERS MOST. Every one of these is a legitimate root and must answer false -
    /// including a test rig's throwaway directory, which is the only way this whole area can be
    /// exercised without touching the machine's real launcher. A check that condemned it would refuse
    /// the rig that proves it.
    /// </summary>
    [Theory]
    [InlineData("root")]
    [InlineData("root", "cc-director")]
    [InlineData("Temp", "cc-rig-9f3a2b")]
    [InlineData("root", "instances")]          // the CONTAINER, not a home inside it
    [InlineData("root", "instance", "default")] // singular: a different directory entirely
    public void An_ordinary_or_isolated_root_is_not_an_instance_home(params string[] parts)
        => Assert.False(InstanceContext.LooksLikeAnInstanceHome(P(parts)));

    /// <summary>Windows path comparison is case-insensitive in practice, and a root that differed only
    /// in case would otherwise be reported healthy.</summary>
    [Fact]
    public void The_instances_segment_is_matched_regardless_of_case()
        => Assert.True(InstanceContext.LooksLikeAnInstanceHome(P("root", "Instances", "default")));

    /// <summary>Nothing is not an instance home, and asking must not throw - this runs inside a launcher
    /// describing itself, where an exception would take the whole declaration with it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_an_instance_home(string? root)
        => Assert.False(InstanceContext.LooksLikeAnInstanceHome(root));

    /// <summary>
    /// The check agrees with the composition it is checking. Asserted against
    /// <see cref="InstanceContext.InstanceHome"/> itself rather than against a hand-written path, so
    /// that changing how an instance home is composed cannot leave this check quietly looking for the
    /// old shape - which is how a detector goes silently blind.
    /// </summary>
    [Fact]
    public void The_check_agrees_with_how_an_instance_home_is_actually_composed()
    {
        Assert.True(InstanceContext.LooksLikeAnInstanceHome(InstanceContext.InstanceHome));
        Assert.False(InstanceContext.LooksLikeAnInstanceHome(InstanceContext.SharedRoot));
    }
}
