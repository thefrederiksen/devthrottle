using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstalledManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public InstalledManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-im-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_Absent_IsEmpty()
    {
        var m = InstalledManifest.Load(_layout);
        Assert.Null(m.Get("cc-pdf"));
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void SaveLoad_RoundTrips_CaseInsensitive()
    {
        var m = InstalledManifest.Load(_layout);
        m.Set("cc-pdf", "1.2.3");
        m.Set("director", "0.3.7");
        m.Save(_layout);

        Assert.True(File.Exists(_layout.InstalledManifestPath));
        var restored = InstalledManifest.Load(_layout);
        Assert.Equal("1.2.3", restored.Get("cc-pdf"));
        Assert.Equal("1.2.3", restored.Get("CC-PDF"));   // case-insensitive
        Assert.Equal("0.3.7", restored.Get("director"));
    }

    [Fact]
    public void Load_CorruptFile_IsEmpty_DoesNotThrow()
    {
        Directory.CreateDirectory(_layout.SetupStateDir);
        File.WriteAllText(_layout.InstalledManifestPath, "{ not valid json");
        var m = InstalledManifest.Load(_layout);
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void Remove_ForgetsEntry()
    {
        var m = InstalledManifest.Empty();
        m.Set("cc-pdf", "1.0.0");
        Assert.True(m.Remove("cc-pdf"));
        Assert.Null(m.Get("cc-pdf"));
    }

    [Fact]
    public void Reader_PrefersRecordedVersion_OverFileStamp()
    {
        // Manifest says cc-pdf is 2.0.0; the (fake) file stamp would say 9.9.9. Recorded wins.
        var manifest = InstalledManifest.Empty();
        manifest.Set("cc-pdf", "2.0.0");
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");

        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "9.9.9",
            installed: manifest);

        var state = reader.Read(pdf);
        Assert.True(state.Present);
        Assert.Equal("2.0.0", state.Version);
    }

    [Fact]
    public void Reader_FallsBackToFileStamp_WhenNotRecorded()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "9.9.9",
            installed: InstalledManifest.Empty());

        Assert.Equal("9.9.9", reader.Read(pdf).Version);
    }

    [Fact]
    public void Reader_AbsentFile_IsNotPresent_RegardlessOfManifest()
    {
        // Even if the manifest still lists a version (e.g. stale after a manual delete), an absent
        // file means not-present - the file-existence gate dominates.
        var manifest = InstalledManifest.Empty();
        manifest.Set("cc-pdf", "2.0.0");
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var reader = new InstalledStateReader(_layout, fileExists: _ => false, readVersion: _ => null, installed: manifest);

        var state = reader.Read(pdf);
        Assert.False(state.Present);
        Assert.Null(state.Version);
    }
}
