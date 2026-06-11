using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #176 defense #1: the self-update test harness must write its FAKE versions into an ISOLATED
/// install root (via CC_DIRECTOR_ROOT), never the production %LOCALAPPDATA%\cc-director setup state.
///
/// These tests prove the engine-side mechanism the script relies on: InstallLayout.Default() honors
/// CC_DIRECTOR_ROOT, so the version-recording (InstalledManifest) and rollback-pin (PinStore) writes
/// that GatewaySelfUpdate performs land under the isolated root - leaving the production
/// installed.json / update-pins.json byte-identical.
/// </summary>
public class SelfUpdateIsolationTests
{
    [Fact]
    public void Default_HonorsCcDirectorRootOverride()
    {
        var iso = Path.Combine(Path.GetTempPath(), "cc-iso-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", iso);
            var layout = InstallLayout.Default();
            Assert.Equal(iso, layout.LocalRoot);
            Assert.StartsWith(iso, layout.InstalledManifestPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", previous);
        }
    }

    [Fact]
    public void RecordingFakeVersions_UnderIsolatedRoot_LeavesProductionSetupStateUntouched()
    {
        // Snapshot the production setup files (whatever their current state) so we can prove they do
        // not change while we record fake 9.9.x versions exactly like GatewaySelfUpdate would.
        var prod = InstallLayout.Default(); // production layout (no override active in this assertion)
        var prodInstalledBefore = SnapshotBytes(prod.InstalledManifestPath);
        var prodPinsBefore = SnapshotBytes(Path.Combine(prod.SetupStateDir, "update-pins.json"));

        var iso = Path.Combine(Path.GetTempPath(), "cc-iso-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", iso);
            var layout = InstallLayout.Default();
            Assert.Equal(iso, layout.LocalRoot);

            // Mirror GatewaySelfUpdate.RecordInstalled + Pin: write the FAKE versions the harness stages.
            var manifest = InstalledManifest.Load(layout);
            manifest.Set(ComponentRegistry.Gateway.Id, "9.9.9");
            manifest.Save(layout);

            var pins = PinStore.Load(layout);
            pins.Pin(ComponentRegistry.Gateway.Id, "9.9.8");
            PinStore.Save(layout, pins);

            // The fake versions landed under the ISOLATED root...
            Assert.True(File.Exists(layout.InstalledManifestPath), "isolated installed.json should exist");
            Assert.Contains("9.9.9", File.ReadAllText(layout.InstalledManifestPath));
            Assert.True(File.Exists(Path.Combine(layout.SetupStateDir, "update-pins.json")), "isolated update-pins.json should exist");

            // ...and the isolated root is NOT the production root.
            Assert.NotEqual(prod.LocalRoot, layout.LocalRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", previous);
            try { if (Directory.Exists(iso)) Directory.Delete(iso, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        // The production setup files are byte-identical to before the isolated write.
        var prodInstalledAfter = SnapshotBytes(prod.InstalledManifestPath);
        var prodPinsAfter = SnapshotBytes(Path.Combine(prod.SetupStateDir, "update-pins.json"));
        Assert.Equal(prodInstalledBefore, prodInstalledAfter);
        Assert.Equal(prodPinsBefore, prodPinsAfter);
    }

    private static byte[] SnapshotBytes(string path) =>
        File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
}
