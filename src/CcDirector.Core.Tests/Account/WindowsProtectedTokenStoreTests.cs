using System.Runtime.Versioning;
using System.Text;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the Windows Data Protection (DPAPI) credential store. Windows-only - the DPAPI is a
/// Windows API, so these facts no-op on other platforms (guarded by the OnWindows check) and the
/// class is annotated [SupportedOSPlatform("windows")] so the platform-compatibility analyzer is
/// satisfied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProtectedTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;

    public WindowsProtectedTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-dpapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _blobPath = Path.Combine(_tempDir, "devthrottle-credential.bin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public void SaveThenLoad_RoundTripsTheTokenPair()
    {
        if (!OnWindows) return;

        var store = new WindowsProtectedTokenStore(_blobPath);
        var tokens = new DevThrottleTokens("access-abc", "refresh-xyz");

        store.Save(tokens);
        var loaded = store.Load();

        Assert.True(store.HasTokens);
        Assert.NotNull(loaded);
        Assert.Equal("access-abc", loaded.AccessToken);
        Assert.Equal("refresh-xyz", loaded.RefreshToken);
    }

    // Acceptance criterion: a search of the stored blob does not find the raw token string in plain
    // text - the blob is DPAPI-encrypted at rest.
    [Fact]
    public void Save_DoesNotWriteRawTokenInPlainText()
    {
        if (!OnWindows) return;

        var store = new WindowsProtectedTokenStore(_blobPath);
        const string rawAccess = "RAW-ACCESS-TOKEN-PLAINTEXT-MARKER-583";
        const string rawRefresh = "RAW-REFRESH-TOKEN-PLAINTEXT-MARKER-583";
        store.Save(new DevThrottleTokens(rawAccess, rawRefresh));

        var bytesOnDisk = File.ReadAllBytes(_blobPath);
        var textOnDisk = Encoding.UTF8.GetString(bytesOnDisk);

        Assert.DoesNotContain(rawAccess, textOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(rawRefresh, textOnDisk, StringComparison.Ordinal);
        // The ASCII bytes of the token must not appear anywhere in the encrypted blob either.
        Assert.False(ContainsBytes(bytesOnDisk, Encoding.ASCII.GetBytes(rawAccess)));
    }

    [Fact]
    public void Clear_RemovesTheStoredEntry()
    {
        if (!OnWindows) return;

        var store = new WindowsProtectedTokenStore(_blobPath);
        store.Save(new DevThrottleTokens("access", "refresh"));
        Assert.True(store.HasTokens);

        store.Clear();

        Assert.False(store.HasTokens);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_NoStoredEntry_ReturnsNull()
    {
        if (!OnWindows) return;

        var store = new WindowsProtectedTokenStore(_blobPath);

        Assert.False(store.HasTokens);
        Assert.Null(store.Load());
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
