using System.Net.Http;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #4744: before the launcher swaps a Director in, the cc-* tools are brought to THAT Director's version, from that
/// release's own folder - never "the latest". Tools already at or past it are left alone without asking the download
/// server anything; tools behind it that cannot be installed say why, and never report that a refresh happened.
/// </summary>
public sealed class ToolsForDirectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-tools-for-director-" + Guid.NewGuid().ToString("N"));
    private readonly InstallLayout _layout;
    private readonly FakeDownloadServer _server = new();

    public ToolsForDirectorTests()
    {
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { /* scratch folder */ }
    }

    private void ToolsInstalledAt(string version)
    {
        var m = InstalledManifest.Load(_layout);
        m.Set(PythonToolsInstaller.ComponentId, version);
        m.Save(_layout);
    }

    private ReleaseSource Source() => new(new HttpClient(_server));

    [Theory]
    [InlineData("2.37.0")]
    [InlineData("2.38.1")]
    public async Task BringPythonToolsToDirectorAsync_ToolsAtOrPastTheDirector_ReadyWithoutAskingTheServer(string installed)
    {
        ToolsInstalledAt(installed);

        var answer = await new ToolUpdater(_layout).BringPythonToolsToDirectorAsync("2.37.0+9ac50e5", Source());

        Assert.True(answer.IsReady);
        Assert.Contains(installed, answer.Message);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task BringPythonToolsToDirectorAsync_ToolsBehind_ReadsThatReleasesOwnFolder_AndAReleaseWithNoBundleIsNotReady()
    {
        // The 7 November 2026 machine: tools 2.35.0 beside a Director going to 2.37.0. Here 2.37.0 carries no tools
        // bundle, so the tools cannot reach it - and the answer says so instead of reporting a refresh.
        ToolsInstalledAt("2.35.0");
        _server.Channel("stable.json", "v2.38.0").Release("v2.37.0", "cc-director-win-x64.exe");

        var answer = await new ToolUpdater(_layout).BringPythonToolsToDirectorAsync("2.37.0", Source());

        Assert.False(answer.IsReady);
        Assert.False(answer.HeldBecauseInUse);
        Assert.Contains("release 2.37.0 carries no cc-* tools bundle", answer.Message);
        // Its own folder, never the stable channel's newest release.
        Assert.Contains(_server.Requests, r => r.EndsWith("/v2.37.0/release-manifest.json", StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Requests, r => r.EndsWith("stable.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BringPythonToolsToDirectorAsync_ToolsAtTheVersionButMissingConsoleScripts_NotReady()
    {
        // The version alone is not enough: a venv missing its console scripts cannot run the Director's commands.
        ToolsInstalledAt("2.37.0");
        PythonToolsState.SaveScripts(_layout, ["cc-devthrottle"]);

        var answer = await new ToolUpdater(_layout).BringPythonToolsToDirectorAsync("2.37.0", Source());

        Assert.False(answer.IsReady);
        Assert.Contains("missing console scripts", answer.Message);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task BringPythonToolsToDirectorAsync_RecordedVersionIsNotAVersion_NotReadyWithTheReason()
    {
        ToolsInstalledAt("garbage");

        var answer = await new ToolUpdater(_layout).BringPythonToolsToDirectorAsync("2.37.0", Source());

        Assert.False(answer.IsReady);
        Assert.Contains("'garbage'", answer.Message);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task BringPythonToolsToDirectorAsync_NoSuchRelease_Throws()
    {
        ToolsInstalledAt("2.35.0");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new ToolUpdater(_layout).BringPythonToolsToDirectorAsync("2.37.0", Source()));
    }
}
