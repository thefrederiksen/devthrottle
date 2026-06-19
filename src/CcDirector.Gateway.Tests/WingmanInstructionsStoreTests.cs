using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Editable/versioned wingman instructions (issue #537): the wingman uses the active version
/// (custom else deployed default); a non-customized user auto-tracks the latest default, while a
/// customized user is told when the dev team ships a new default and can switch to it.
/// </summary>
public sealed class WingmanInstructionsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "wmi-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    private WingmanInstructionsStore New(string def, string ver = "1")
        => new(defaultContent: def, defaultVersion: ver, path: _path);

    [Fact]
    public void Fresh_UsesDeployedDefault_NotCustomized_NoUpdate()
    {
        var s = New("DEFAULT v1");
        Assert.False(s.IsCustomized);
        Assert.Equal("DEFAULT v1", s.ActiveContent);
        Assert.False(s.UpdateAvailable);
    }

    [Fact]
    public void Save_BecomesActiveAndCustomized()
    {
        var s = New("DEFAULT v1");
        var v = s.Save("MY custom instructions", "first try");
        Assert.True(s.IsCustomized);
        Assert.Equal("MY custom instructions", s.ActiveContent);
        Assert.Equal("user", v.Source);
        Assert.Single(s.Versions());
        Assert.False(s.UpdateAvailable);   // editing acknowledges the current default
    }

    [Fact]
    public void Save_EmptyOrOversized_Throws()
    {
        var s = New("DEFAULT v1");
        Assert.Throws<ArgumentException>(() => s.Save("   ", null));
        Assert.Throws<ArgumentException>(() => s.Save(new string('x', WingmanInstructionsStore.MaxContentChars + 1), null));
    }

    [Fact]
    public void CustomizedUser_NewDefaultShips_UpdateAvailable_WithOldDefaultForDiff()
    {
        New("DEFAULT v1").Save("MY custom", null);          // customize against v1
        var s2 = New("DEFAULT v2 changed", "2");            // dev team ships a new default (same path)
        Assert.True(s2.IsCustomized);
        Assert.Equal("MY custom", s2.ActiveContent);        // still on the user's version
        Assert.True(s2.UpdateAvailable);                    // but told a new default exists
        var (ackVer, ackContent) = s2.AcknowledgedDefault();
        Assert.Equal("DEFAULT v1", ackContent);             // the diff's left side = what they based on
    }

    [Fact]
    public void NonCustomizedUser_NewDefaultShips_AutoTracks_NoBanner()
    {
        New("DEFAULT v1");                                  // never customized
        var s2 = New("DEFAULT v2 changed", "2");
        Assert.False(s2.IsCustomized);
        Assert.Equal("DEFAULT v2 changed", s2.ActiveContent);   // rides the latest default
        Assert.False(s2.UpdateAvailable);                       // no stale banner
    }

    [Fact]
    public void SwitchToDefault_AdoptsLatest_ClearsUpdate()
    {
        New("DEFAULT v1").Save("MY custom", null);
        var s2 = New("DEFAULT v2 changed", "2");
        Assert.True(s2.UpdateAvailable);
        s2.SwitchToDefault();
        Assert.False(s2.IsCustomized);
        Assert.Equal("DEFAULT v2 changed", s2.ActiveContent);
        Assert.False(s2.UpdateAvailable);
    }

    [Fact]
    public void Revert_MakesAnOlderVersionActiveAgain()
    {
        var s = New("DEFAULT v1");
        var v1 = s.Save("version one", "v1");
        s.Save("version two", "v2");
        Assert.Equal("version two", s.ActiveContent);
        Assert.True(s.Revert(v1.Id));
        Assert.Equal("version one", s.ActiveContent);
        Assert.False(s.Revert("does-not-exist"));
    }

    [Fact]
    public void State_PersistsAcrossReload()
    {
        New("DEFAULT v1").Save("persisted custom", "keep");
        var s2 = New("DEFAULT v1");                          // reload from the same file
        Assert.True(s2.IsCustomized);
        Assert.Equal("persisted custom", s2.ActiveContent);
        Assert.Single(s2.Versions());
    }
}
