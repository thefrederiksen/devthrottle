using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Reading a version out of a macOS .app bundle through the reader's own plist read - everything real except the
/// plutil process itself.
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
///
/// ON EVERY MACHINE, WITH NO REAL PROGRAM. These tests used to run the real /usr/bin/plutil, so they ran only on
/// macOS (skipped everywhere else) and let a real process decide a gate test. The reader now takes the platform and
/// the process runner as parameters (the engine's own pattern), so these tests read as macOS on any machine, and
/// <see cref="Plutil"/> answers exactly as plutil does for the three cases that matter - a version, a missing key, an
/// unreadable file - by reading the property list in this process. What is real: the bundle on disk, the plist path
/// the reader composes, the arguments it sends, how it treats each answer, and the #1740 comparison on top.
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

    /// <summary>Every plist the stand-in plutil was asked to read.</summary>
    private readonly List<string> _plutilReads = new();

    /// <summary>A reader laid out as macOS, whose plutil is <see cref="Plutil"/>.</summary>
    private InstalledStateReader MacReader(InstalledManifest installed) =>
        new(Layout(), OSPlatform.OSX, Plutil, installed: installed);

    private static readonly Regex ExtractArguments = new("^-extract (?<key>\\S+) raw \"(?<plist>[^\"]+)\"$");

    /// <summary>
    /// Answers as /usr/bin/plutil does for "-extract &lt;key&gt; raw &lt;plist&gt;": the value and a line break, exit 0;
    /// a missing key, exit 1; a file that is not a property list, exit 1. Any other program fails the test.
    /// </summary>
    private (int exit, string output) Plutil(string exe, string arguments, Action<string>? onLine, TimeSpan timeout)
    {
        Assert.Equal(InstalledStateReader.PlutilPath, exe);
        var match = ExtractArguments.Match(arguments);
        Assert.True(match.Success, $"plutil was asked something other than a raw extract: {arguments}");
        var plist = match.Groups["plist"].Value;
        _plutilReads.Add(plist);

        XDocument doc;
        try
        {
            doc = XDocument.Load(plist, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return (1, $"{plist}: Property List error: Unexpected character n at line 1 / JSON error: JSON text did not start with array or object\n");
        }
        var keys = doc.Root?.Element("dict")?.Elements().ToList() ?? new List<XElement>();
        var at = keys.FindIndex(e => e.Name == "key" && e.Value == match.Groups["key"].Value);
        return at >= 0 && at + 1 < keys.Count
            ? (0, keys[at + 1].Value + "\n")
            : (1, $"{plist}: Could not extract value, error: No value at that key path or invalid key path: {match.Groups["key"].Value}\n");
    }

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

    [Fact]
    public void TheVersionIsReadFromTheBundlesInfoPlist_WithNothingStubbed()
    {
        WriteBundle("2.28.0");
        var reader = MacReader(InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Equal("2.28.0", state.Version);
        Assert.Equal("2.28.0", state.FileVersion);
        // The version came from the bundle's own Info.plist, asked of plutil once.
        Assert.Equal(new[] { Path.Combine(_appsDir, "Director.app", "Contents", "Info.plist") }, _plutilReads);
    }

    [Fact]
    public void OffMacOS_ABundleIsNeverAskedOfPlutil()
    {
        // plutil exists only on macOS: a reader for any other system must not try it, whatever is on disk.
        WriteBundle("2.28.0");
        var bundle = Path.Combine(_appsDir, "Director.app");

        Assert.Null(InstalledStateReader.ReadVersionFromDisk(bundle, OSPlatform.Linux, Plutil));
        Assert.Empty(_plutilReads);
    }

    [Fact]
    public void AStaleRecordLosesToTheBundleOnDisk_WhichIsTheSelfUpdateCase()
    {
        // Exactly the state of a Mac that has self-updated: the record says what the installer placed,
        // the bundle on disk is newer. Observed on devthrottle-mac-mini on 21 Oct 2026 - installed.json
        // said 2.26.0 while Info.plist said 2.28.0.
        WriteBundle("2.28.0");
        var manifest = InstalledManifest.Empty();
        manifest.Set(ComponentRegistry.Director.Id, "2.26.0");
        var reader = MacReader(manifest);

        var state = reader.Read(ComponentRegistry.Director);

        Assert.Equal("2.28.0", state.Version);
    }

    [Fact]
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
        var reader = MacReader(InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Null(state.Version);
    }

    [Fact]
    public void WhenTheBundleVersionCannotBeRead_TheRecordIsStillReported()
    {
        // And the other half: an unreadable bundle must not erase what we recorded. This is the state
        // in which the drift DOES bite - the record is all there is, and it may be stale.
        var contents = Path.Combine(_appsDir, "Director.app", "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "not a plist at all");
        var manifest = InstalledManifest.Empty();
        manifest.Set(ComponentRegistry.Director.Id, "2.26.0");
        var reader = MacReader(manifest);

        var state = reader.Read(ComponentRegistry.Director);

        Assert.True(state.Present);
        Assert.Equal("2.26.0", state.Version);
    }
}

/// <summary>
/// The reader lays a machine out as the platform it was GIVEN, not the machine it runs on - its legacy aliases
/// included. The Gateway's pre-rename name differs by platform (".exe" only on Windows), so a reader for Windows on
/// any other machine finds the Windows alias only if it asks the layout for Windows's aliases.
/// </summary>
public class InstalledStateReaderPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-reader-platform-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void AReaderForWindows_FindsTheGatewayUnderItsWindowsPreRenameName_OnAnyMachine()
    {
        var layout = new InstallLayout(_root);
        var legacy = Path.Combine(layout.GatewayDir, "cc-director-gateway.exe");
        Directory.CreateDirectory(layout.GatewayDir);
        File.WriteAllText(legacy, "the pre-rename Gateway");
        var reader = new InstalledStateReader(layout, OSPlatform.Windows,
            (exe, arguments, _, _) => throw new InvalidOperationException($"no program may be started: {exe} {arguments}"),
            readVersion: _ => null, installed: InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Gateway);

        Assert.True(state.Present);
        Assert.Equal(layout.PathFor(ComponentRegistry.Gateway, OSPlatform.Windows), state.Path);
    }
}
