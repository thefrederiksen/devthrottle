using CcDirector.Core.Secrets;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.UnitTests.Secrets;

/// <summary>
/// The Director and the Gateway read the GitHub token from the cc-secrets store and nowhere else. Every test
/// writes its own store file in a temporary folder and passes that path, so none of them can reach the
/// owner's real store and none of them touch the environment.
/// </summary>
public sealed class SecretStoreReaderTests : IDisposable
{
    private readonly string _dir;

    public SecretStoreReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "secret-store-reader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteStore(string json)
    {
        var path = Path.Combine(_dir, "secrets.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoEntries = """
        {
          "version": 1,
          "entries": [
            { "name": "posthog-host", "kind": "setting", "secret": "https://example.invalid", "envName": "POSTHOG_HOST" },
            { "name": "github-token", "kind": "secret", "secret": "invented-token-value-1234", "envName": "GITHUB_TOKEN" }
          ]
        }
        """;

    [Fact]
    public void ReadSecret_EntryPresent_ReturnsItsValue()
    {
        var store = WriteStore(TwoEntries);

        var value = SecretStoreReader.ReadSecret(store, "github-token");

        Assert.Equal("invented-token-value-1234", value);
    }

    [Fact]
    public void ReadSecret_StoreMissing_ThrowsNamingTheStoreAndHowToAddTheEntry()
    {
        var store = Path.Combine(_dir, "secrets.json");

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "github-token"));

        Assert.Contains(store, ex.Message);
        Assert.Contains("cc-secrets add github-token", ex.Message);
    }

    [Fact]
    public void ReadSecret_EntryMissing_ThrowsWithHowToAddIt()
    {
        var store = WriteStore("""{ "version": 1, "entries": [ { "name": "other", "kind": "secret", "secret": "x-value-123456" } ] }""");

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "github-token"));

        Assert.Contains("no entry named 'github-token'", ex.Message);
        Assert.Contains("cc-secrets add github-token", ex.Message);
    }

    [Fact]
    public void ReadSecret_EntryHasNoValue_ThrowsWithHowToReplaceIt()
    {
        var store = WriteStore("""{ "version": 1, "entries": [ { "name": "github-token", "kind": "secret", "secret": "" } ] }""");

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "github-token"));

        Assert.Contains("--replace", ex.Message);
    }

    [Fact]
    public void ReadSecret_UnknownStoreVersion_Throws()
    {
        var store = WriteStore("""{ "version": 2, "entries": [ { "name": "github-token", "kind": "secret", "secret": "invented-token-value-1234" } ] }""");

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "github-token"));

        Assert.Contains("format version 1", ex.Message);
    }

    [Fact]
    public void ReadSecret_StoreNotJson_ThrowsNamingTheStore()
    {
        var store = WriteStore("GITHUB_TOKEN=invented-token-value-1234");

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "github-token"));

        Assert.Contains(store, ex.Message);
    }

    [Fact]
    public void ReadSecret_FailureMessage_NeverContainsAnotherEntrysValue()
    {
        var store = WriteStore(TwoEntries);

        var ex = Assert.Throws<SecretNotAvailableException>(() => SecretStoreReader.ReadSecret(store, "missing-entry"));

        Assert.DoesNotContain("invented-token-value-1234", ex.Message);
    }

    [Fact]
    public void ResolveSecretsStore_SecretsHomeSet_UsesItOnEveryPlatform()
    {
        var home = Path.Combine(_dir, "override");

        Assert.Equal(Path.Combine(home, "secrets.json"),
            CcStorage.ResolveSecretsStore(home, @"C:\Users\u\AppData\Local", "/home/u", isWindows: true, isMacOS: false));
        Assert.Equal(Path.Combine(home, "secrets.json"),
            CcStorage.ResolveSecretsStore(home, null, "/home/u", isWindows: false, isMacOS: false));
    }

    [Fact]
    public void ResolveSecretsStore_Windows_IsUnderLocalAppDataNotTheDirectorRoot()
    {
        var local = Path.Combine(_dir, "Local");

        var path = CcStorage.ResolveSecretsStore(null, local, "ignored", isWindows: true, isMacOS: false);

        Assert.Equal(Path.Combine(local, "cc-director", "secrets", "secrets.json"), path);
    }

    [Fact]
    public void ResolveSecretsStore_WindowsWithoutLocalAppData_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => CcStorage.ResolveSecretsStore(null, "", "ignored", isWindows: true, isMacOS: false));
    }

    [Fact]
    public void ResolveSecretsStore_MacAndLinux_MatchTheCcSecretsTool()
    {
        var home = Path.Combine(_dir, "home");

        Assert.Equal(Path.Combine(home, "Library", "Application Support", "cc-director", "secrets", "secrets.json"),
            CcStorage.ResolveSecretsStore(null, null, home, isWindows: false, isMacOS: true));
        Assert.Equal(Path.Combine(home, ".cc-director", "secrets", "secrets.json"),
            CcStorage.ResolveSecretsStore(null, null, home, isWindows: false, isMacOS: false));
    }
}
