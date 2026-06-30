using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the local store for the cloud-issued per-device key (issue #857): a save/get round-trip keyed by
/// install id, that a different install id yields no key, that clearing removes it, and - DT-05 - that the
/// raw key value is never written to the log even though it is persisted to disk.
/// </summary>
public sealed class GatewayDeviceKeyStoreTests
{
    private const string Install = "install-857";
    private const string Key = "DEVICEKEY-PLAINTEXT-MARKER-857-store";

    private static GatewayDeviceKeyStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-device-key-store-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public void Save_Then_Get_RoundTrips_ForMatchingInstall()
    {
        var store = TempStore();
        try
        {
            store.Save(Install, Key);
            Assert.True(store.HasKeyForInstall(Install));
            Assert.Equal(Key, store.GetKeyForInstall(Install));
        }
        finally
        {
            TryDelete(store.StorePath);
        }
    }

    [Fact]
    public void GetKeyForInstall_DifferentInstall_ReturnsNull()
    {
        var store = TempStore();
        try
        {
            store.Save(Install, Key);
            Assert.Null(store.GetKeyForInstall("some-other-install"));
            Assert.False(store.HasKeyForInstall("some-other-install"));
        }
        finally
        {
            TryDelete(store.StorePath);
        }
    }

    [Fact]
    public void Clear_RemovesTheStoredKey()
    {
        var store = TempStore();
        try
        {
            store.Save(Install, Key);
            store.Clear();
            Assert.False(store.HasKeyForInstall(Install));
            Assert.False(File.Exists(store.StorePath));
        }
        finally
        {
            TryDelete(store.StorePath);
        }
    }

    [Fact]
    public void Save_PersistsKeyToDisk_ButNeverLogsIt()
    {
        var store = TempStore();
        try
        {
            IReadOnlyList<string> lines;
            using (var scope = FileLog.RedirectForTests())
            {
                store.Save(Install, Key);
                lines = scope.DrainAndReadLines();
            }

            Assert.Contains(Key, File.ReadAllText(store.StorePath), StringComparison.Ordinal);
            Assert.DoesNotContain(lines, line => line.Contains(Key, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("stored per-device key for install_id=", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(store.StorePath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
