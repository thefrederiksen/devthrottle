using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.UnitTests.Instances;

/// <summary>
/// Which instance a Director runs as (issue devthrottle_internal#2488). The defect these pin: an
/// <c>--instance</c> name that was not registered fell back to the DEFAULT instance, so a test build ran on the
/// owner's real data. A named instance that is not registered must never resolve to the default.
/// </summary>
public sealed class InstanceSelectionTests
{
    private static readonly IReadOnlyList<NamedInstance> Registered = new[]
    {
        new NamedInstance { Name = "default", DisplayName = "devlinux" },
        new NamedInstance { Name = "linuxtest", DisplayName = "Linux test" },
    };

    [Fact]
    public void Choose_UnknownExplicitName_IsRefusedAndNeverTheDefault()
    {
        var selection = InstanceSelection.Choose("toolhealth2474", Registered);

        Assert.False(selection.MayStart);
        Assert.NotEqual("default", selection.Slug);
        Assert.Contains("no Director instance named 'toolhealth2474'", selection.Refusal);
        Assert.Contains("Registered instances: default, linuxtest", selection.Refusal);
    }

    [Fact]
    public void Choose_RegisteredName_StartsAsIt()
    {
        var selection = InstanceSelection.Choose("linuxtest", Registered);

        Assert.True(selection.MayStart);
        Assert.Equal("linuxtest", selection.Slug);
        Assert.True(selection.WasExplicit);
        Assert.Equal("Linux test", selection.DisplayName);
    }

    [Fact]
    public void Choose_RegisteredNameInOtherCase_StartsAsTheRegisteredName()
    {
        var selection = InstanceSelection.Choose("  LinuxTest ", Registered);

        Assert.True(selection.MayStart);
        Assert.Equal("linuxtest", selection.Slug);
    }

    [Fact]
    public void Choose_NoInstanceGiven_IsTheDefault()
    {
        var selection = InstanceSelection.Choose(null, Registered);

        Assert.True(selection.MayStart);
        Assert.Equal("default", selection.Slug);
        Assert.False(selection.WasExplicit);
    }

    [Fact]
    public void Choose_DefaultNotYetInTheRegistry_StillStarts()
    {
        var selection = InstanceSelection.Choose("default", Array.Empty<NamedInstance>());

        Assert.True(selection.MayStart);
        Assert.Equal("default", selection.Slug);
    }

    [Fact]
    public void Choose_UnknownNameWithAnEmptyRegistry_SaysNoneAreRegistered()
    {
        var selection = InstanceSelection.Choose("ghost", Array.Empty<NamedInstance>());

        Assert.False(selection.MayStart);
        Assert.Contains("Registered instances: none", selection.Refusal);
    }

    [Fact]
    public void Unreadable_IsARefusal()
    {
        var selection = InstanceSelection.Unreadable("linuxtest", "file is locked");

        Assert.False(selection.MayStart);
        Assert.Contains("could not be read: file is locked", selection.Refusal);
    }

    [Fact]
    public void Unreadable_ErrorEndingInAFullStop_IsNotDoubled()
    {
        var selection = InstanceSelection.Unreadable("linuxtest", "Access to the path is denied.");

        Assert.DoesNotContain("..", selection.Refusal);
    }
}
