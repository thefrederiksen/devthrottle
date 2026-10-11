using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A HOST KEEPS ITS FILES UNDER THE ROOT IT WAS BUILT WITH, not under the process root at the moment a store happens to
/// resolve its path. Before this, every store the host built composed its path from <c>CcStorage.Root()</c>, which
/// re-reads <c>CC_DIRECTOR_ROOT</c> on every call: two hosts in one process could not keep their files apart, and a
/// test could only isolate a host by setting the process-wide variable, which is why this assembly runs one class at
/// a time (gateway-tests.md section 3.1, root cause 1).
///
/// The host here is given NO per-store path and NO token, so every path it uses is one it composed from
/// <see cref="GatewayHostOptions.StorageRoot"/>. The process root during the run is the assembly's redirected root
/// (<see cref="TestStorageRootRedirect"/>); this test writes no environment variable.
/// </summary>
public sealed class GatewayHostStorageRootTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-host-storage-root-" + Guid.NewGuid().ToString("N"));

    public GatewayHostStorageRootTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task A_host_built_with_a_stated_root_keeps_its_files_there_and_mints_nothing_under_the_process_root()
    {
        var processRoot = TestStorageRootRedirect.Root;
        Assert.NotEqual(processRoot, _root);

        await using var host = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            authEnabled: false,
            options: new GatewayHostOptions { Hosted = false, StorageRoot = _root });

        Assert.Equal(_root, host.StorageRoot);

        // The bearer token is minted at construction into the host's token file: under ITS root ...
        var tokenFile = Path.Combine(_root, "config", "director", "gateway-token.txt");
        Assert.True(File.Exists(tokenFile), $"the token file was not created under the host's root: {tokenFile}");
        Assert.Equal(host.Token, File.ReadAllText(tokenFile).Trim());

        // ... and not into the process root's. That file may exist from an earlier class in the run; what it must not
        // hold is THIS host's token, which only this host knows.
        var processTokenFile = Path.Combine(processRoot, "config", "director", "gateway-token.txt");
        if (File.Exists(processTokenFile))
            Assert.NotEqual(host.Token, File.ReadAllText(processTokenFile).Trim());

        // The upload stores create their directories when the host is built.
        Assert.True(Directory.Exists(Path.Combine(_root, "dictation-uploads")), "dictation-uploads is not under the host's root");
        Assert.True(Directory.Exists(Path.Combine(_root, "voice-turn-uploads")), "voice-turn-uploads is not under the host's root");

        // The Gateway database opens on start, under the host's root.
        await host.StartAsync();
        Assert.True(File.Exists(Path.Combine(_root, "gateway.db")), "gateway.db is not under the host's root");
    }

    [Fact]
    public void A_host_refuses_an_empty_root()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            authEnabled: false,
            options: new GatewayHostOptions { Hosted = false, StorageRoot = "" }));
        Assert.Contains("StorageRoot", ex.Message);
    }
}
