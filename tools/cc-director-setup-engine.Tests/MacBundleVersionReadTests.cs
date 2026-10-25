using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Reading a version out of a REAL macOS .app bundle, with nothing stubbed.
///
/// Every other test of this reader injects its own readVersion - including the one named for reporting
/// "the bundle's Info.plist version" (issue #1736), which hands in a fixed string and never creates a
/// plist. So the branch that actually shells out to plutil had no coverage at all, on a reader whose
/// macOS answer depends entirely on it.
///
/// That matters because of the compensation for issue #1740. The Director self-updates in place and
/// never writes installed.json, so on any Mac that has self-updated the record is older than the
/// bundle on disk. The reader detects that by comparing the record against the version it reads from
/// the bundle - so if this read ever returns null, the comparison cannot fire, status silently
/// under-reports, and plan proposes a redundant re-download of a build the machine already runs. The
/// logic of that comparison is tested with a stub; this is the part underneath it.
/// </summary>
public class MacBundleVersionReadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _appsDir;

    public MacBundleVersionReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-bundle-" + Guid.NewGuid().ToString("N"));
        _appsDir = Path.Combine(_dir, "Applications");
        Directory.CreateDirectory(_appsDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private InstallLayout Layout() =>
        new(Path.Combine(_dir, "local"), defaultRoot: null, homeDir: _dir, macAppsDir: _appsDir);

    /// <summary>Write a Director.app whose Info.plist carries <paramref name="version"/>.</summary>
    private void WriteBundle(string version)
    {
        var contents = Path.Combine(_appsDir, "Director.app", "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleShortVersionString</key>
                <string>{version}</string>
                <key>CFBundleVersion</key>
                <string>{version}</string>
            </dict>
            </plist>
            """);
    }

    [MacOnlyFact("reads a real .app bundle with plutil, which only exists on macOS")]
    public void TheVersionIsReadFromTheBundlesInfoPlist_WithNothingStubbed()
    {
        WriteBundle("2.28.0");
        var reader = new InstalledStateReader(Layout(), installed: InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Equal("2.28.0", state.Version);
        Assert.Equal("2.28.0", state.FileVersion);
    }

    [MacOnlyFact("the #1740 compensation end to end, through the real plutil read")]
    public void AStaleRecordLosesToTheBundleOnDisk_WhichIsTheSelfUpdateCase()
    {
        // Exactly the state of a Mac that has self-updated: the record says what the installer placed,
        // the bundle on disk is newer. Observed on devthrottle-mac-mini on 21 Oct 2026 - installed.json
        // said 2.26.0 while Info.plist said 2.28.0.
        WriteBundle("2.28.0");
        var manifest = InstalledManifest.Empty();
        manifest.Set(ComponentRegistry.Director.Id, "2.26.0");
        var reader = new InstalledStateReader(Layout(), installed: manifest);

        var state = reader.Read(ComponentRegistry.Director);

        Assert.Equal("2.28.0", state.Version);
    }

    [MacOnlyFact("a bundle with no readable version must not silently become a version")]
    public void ABundleWhoseInfoPlistHasNoVersionKey_ReadsAsPresentWithNoVersion()
    {
        // The failure mode this whole file is about: if the read comes back null, the #1740 comparison
        // cannot fire. It must report no version rather than invent one, and presence must survive -
        // the bundle IS there.
        var contents = Path.Combine(_appsDir, "Director.app", "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>CFBundleName</key><string>Director</string></dict></plist>
            """);
        var reader = new InstalledStateReader(Layout(), installed: InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Null(state.Version);
    }

    [MacOnlyFact("a stale record must still be reported when the bundle version cannot be read")]
    public void WhenTheBundleVersionCannotBeRead_TheRecordIsStillReported()
    {
        // And the other half: an unreadable bundle must not erase what we recorded. This is the state
        // in which the drift DOES bite - the record is all there is, and it may be stale.
        var contents = Path.Combine(_appsDir, "Director.app", "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "not a plist at all");
        var manifest = InstalledManifest.Empty();
        manifest.Set(ComponentRegistry.Director.Id, "2.26.0");
        var reader = new InstalledStateReader(Layout(), installed: manifest);

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Equal("2.26.0", state.Version);
    }
}
