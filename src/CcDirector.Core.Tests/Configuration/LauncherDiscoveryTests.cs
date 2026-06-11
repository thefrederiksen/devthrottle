using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The launcher presence/port fact (issue #330): absent file = NOT INSTALLED (a valid
/// fact, never an error), present file = installed + its declared port, and a present
/// but unreadable file must say so honestly instead of masquerading as "not installed".
/// </summary>
public sealed class LauncherDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-fact-tests", Guid.NewGuid().ToString("N"));

    public LauncherDiscoveryTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string content)
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Read_FileAbsent_ReportsNotInstalled_NoError()
    {
        var fact = LauncherDiscovery.Read(Path.Combine(_dir, "does-not-exist.json"));

        Assert.False(fact.Installed);
        Assert.Null(fact.Port);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Read_FilePresentWithPort_ReportsInstalledAndPort()
    {
        var path = PathFor("""{ "launcherId": "abc", "port": 7900, "pid": 1234 }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Equal(7900, fact.Port);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Read_PortKeyIsCaseInsensitive()
    {
        var path = PathFor("""{ "Port": 7901 }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Equal(7901, fact.Port);
    }

    [Fact]
    public void Read_CorruptJson_ReportsInstalledWithError_NeverNotInstalled()
    {
        var path = PathFor("{ torn-by-power-loss");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed); // the file existing IS the presence fact
        Assert.Null(fact.Port);
        Assert.NotNull(fact.Error);
        Assert.Contains("unparsable", fact.Error);
    }

    [Fact]
    public void Read_NoPortField_ReportsInstalledWithError()
    {
        var path = PathFor("""{ "launcherId": "abc" }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Null(fact.Port);
        Assert.Contains("no port field", fact.Error);
    }

    [Fact]
    public void Read_NonIntegerPort_ReportsInstalledWithError()
    {
        var path = PathFor("""{ "port": "seventy-nine hundred" }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Null(fact.Port);
        Assert.Contains("not an integer", fact.Error);
    }
}
