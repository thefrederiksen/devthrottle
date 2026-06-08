using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the central <see cref="KeyVault"/> store. Each test points the vault at an
/// isolated temp file so it never touches the real %LOCALAPPDATA% store.
/// </summary>
public sealed class KeyVaultTests : IDisposable
{
    private readonly string _path;

    public KeyVaultTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "cc-keyvault-test-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_MissingFile_ReturnsNull()
    {
        var vault = new KeyVault(_path);
        Assert.Null(vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void ListNames_MissingFile_ReturnsEmpty()
    {
        var vault = new KeyVault(_path);
        Assert.Empty(vault.ListNames());
    }

    [Fact]
    public void SetThenGet_RoundTripsValue()
    {
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "sk-abc123");
        Assert.Equal("sk-abc123", vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void Set_Overwrites_ExistingValue()
    {
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "sk-old");
        vault.Set("OPENAI_API_KEY", "sk-new");
        Assert.Equal("sk-new", vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void Set_PersistsAcrossInstances()
    {
        new KeyVault(_path).Set("OPENAI_API_KEY", "sk-persist");
        // A fresh instance (e.g. the recording transcriber reading the same file) sees it.
        Assert.Equal("sk-persist", new KeyVault(_path).Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void Delete_ExistingKey_ReturnsTrueAndRemoves()
    {
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "sk-x");
        Assert.True(vault.Delete("OPENAI_API_KEY"));
        Assert.Null(vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var vault = new KeyVault(_path);
        Assert.False(vault.Delete("NOPE"));
    }

    [Fact]
    public void ListNames_ReturnsAllNamesSorted()
    {
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "1");
        vault.Set("ANTHROPIC_API_KEY", "2");
        Assert.Equal(new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY" }, vault.ListNames());
    }

    [Fact]
    public void Set_EmptyName_Throws()
    {
        var vault = new KeyVault(_path);
        Assert.Throws<ArgumentException>(() => vault.Set("  ", "v"));
    }

    [Fact]
    public void SetIfAbsent_WhenMissing_WritesAndReturnsTrue()
    {
        var vault = new KeyVault(_path);
        Assert.True(vault.SetIfAbsent("OPENAI_API_KEY", "sk-seed"));
        Assert.Equal("sk-seed", vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void SetIfAbsent_WhenPresent_KeepsExistingAndReturnsFalse()
    {
        // The bootstrap must never clobber a key an operator rotated in via the Cockpit.
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "sk-rotated");
        Assert.False(vault.SetIfAbsent("OPENAI_API_KEY", "sk-seed"));
        Assert.Equal("sk-rotated", vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void SetIfAbsent_WhenExistingValueEmpty_SeedsAndReturnsTrue()
    {
        // An empty stored value is treated as absent, so a real key can still seed in.
        var vault = new KeyVault(_path);
        vault.Set("OPENAI_API_KEY", "");
        Assert.True(vault.SetIfAbsent("OPENAI_API_KEY", "sk-seed"));
        Assert.Equal("sk-seed", vault.Get("OPENAI_API_KEY"));
    }

    [Fact]
    public void SetIfAbsent_EmptyName_Throws()
    {
        var vault = new KeyVault(_path);
        Assert.Throws<ArgumentException>(() => vault.SetIfAbsent("  ", "v"));
    }
}
