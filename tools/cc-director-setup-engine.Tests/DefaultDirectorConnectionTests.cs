using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3506: the setup command line's <c>enroll</c> must connect the Director this install runs, so the
/// Director starts connected and as ONE device. Two things decide that, and both are pinned here against the
/// DIRECTOR'S rules, not against the class under test:
///
///  - WHERE the connection lands. The Director runs in <c>&lt;machine root&gt;\instances\default</c> (its
///    Program points CC_DIRECTOR_ROOT there) and reads its connection with <see cref="GatewayConfig.Load"/>.
///    The tests read the saved connection exactly that way - CC_DIRECTOR_ROOT on the instance home, then the
///    Director's own <c>Load()</c> - and assert the machine root got nothing. Before the fix the command line
///    wrote to the machine root, where that read finds no gateway: "No Gateway", local-only mode.
///
///  - WHICH device is enrolled. The Director's id lives in its home under a slot named by
///    SHA256(lowercased "&lt;exe&gt;|instance=default"). The expected file name is computed here with a
///    separate copy of that hash, so a change to the shared rule that moved the file away from where the
///    Director looks would fail here. Before the fix the command line minted a throwaway GUID, the Director
///    minted its own, and the account would have held two workstations for one machine.
/// </summary>
[Collection(MachineRootCollection.Name)] // points the process-wide CC_DIRECTOR_ROOT setting at the instance home
public sealed class DefaultDirectorConnectionTests : IDisposable
{
    private const string RootVariable = "CC_DIRECTOR_ROOT";

    private readonly string _machineRoot;
    private readonly string? _previousRoot;

    public DefaultDirectorConnectionTests()
    {
        _machineRoot = Path.Combine(Path.GetTempPath(), "cli-enroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_machineRoot);
        _previousRoot = Environment.GetEnvironmentVariable(RootVariable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _previousRoot);
        try { Directory.Delete(_machineRoot, recursive: true); } catch { /* temp cleanup only */ }
    }

    private DefaultDirectorConnection Windows() =>
        DefaultDirectorConnection.For(new InstallLayout(_machineRoot), OSPlatform.Windows);

    // The Director's identity slot, written out separately from DirectorIdentitySlot on purpose.
    private static string ExpectedIdFileName(string exe, string slug)
    {
        var key = $"{exe}|instance={slug}".Replace('/', '\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"director-id-{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}.txt";
    }

    // Read the connection exactly as the default Director does: storage pinned to its instance home.
    private GatewayConfig LoadAsTheDirector(DefaultDirectorConnection director)
    {
        Environment.SetEnvironmentVariable(RootVariable, director.StorageRoot);
        try { return GatewayConfig.Load(); }
        finally { Environment.SetEnvironmentVariable(RootVariable, _previousRoot); }
    }

    [Fact]
    public void For_StorageRootIsTheDefaultInstanceHome()
    {
        var director = Windows();

        Assert.Equal(Path.Combine(_machineRoot, "instances", "default"), director.StorageRoot);
    }

    [Fact]
    public void For_Windows_DirectorExecutableIsTheInstalledAppExe()
    {
        Assert.Equal(Path.Combine(_machineRoot, "app", "cc-director.exe"), Windows().DirectorExecutable);
    }

    [Fact]
    public void For_Mac_DirectorExecutableIsTheBinaryInsideTheBundle()
    {
        // On macOS the process path is the binary inside Director.app, not the bundle, so the slot is keyed
        // by Contents/MacOS/cc-director (scripts/package-mac-app.sh BIN_NAME).
        var director = DefaultDirectorConnection.For(new InstallLayout(_machineRoot), OSPlatform.OSX);

        Assert.EndsWith(Path.Combine("Director.app", "Contents", "MacOS", "cc-director"), director.DirectorExecutable);
    }

    [Fact]
    public void LoadOrCreateDirectorId_WritesTheSlotTheDefaultDirectorReads()
    {
        var director = Windows();

        var id = director.LoadOrCreateDirectorId();

        var expected = Path.Combine(_machineRoot, "instances", "default", "config", "director",
            ExpectedIdFileName(Path.Combine(_machineRoot, "app", "cc-director.exe"), "default"));
        Assert.True(File.Exists(expected), $"the id was not written where the Director reads it: {expected}");
        Assert.Equal(id, File.ReadAllText(expected).Trim());
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void LoadOrCreateDirectorId_ReusesTheIdTheDirectorAlreadyHas()
    {
        // A machine whose Director has already run keeps its id: enrolling must not mint a second device.
        var director = Windows();
        var existing = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(director.DirectorIdFile)!);
        File.WriteAllText(director.DirectorIdFile, existing);

        Assert.Equal(existing, director.LoadOrCreateDirectorId());
        Assert.Equal(existing, director.LoadOrCreateDirectorId());
    }

    [Fact]
    public void SaveEnrolledKey_TheDefaultDirectorReadsItAsConnected()
    {
        var director = Windows();

        director.SaveEnrolledKey("https://gateway.example.test", "per-device-key-0123456789");

        var seen = LoadAsTheDirector(director);
        Assert.True(seen.IsEnabled, "the Director reads no gateway url - it would start local-only (issue #3506)");
        Assert.Equal("https://gateway.example.test", seen.Url);
        Assert.Equal("per-device-key-0123456789", seen.Token);
        Assert.True(seen.StreamMode);
        Assert.Equal("per-device-key-0123456789",
            File.ReadAllText(Path.Combine(director.StorageRoot, "config", "director", "gateway-token.txt")));
    }

    [Fact]
    public void SaveEnrolledKey_WritesNothingIntoTheMachineRoot()
    {
        // No "write both places just in case": the machine root's config is not a Director's, and a second
        // copy there is a second answer that can disagree with the first.
        Windows().SaveEnrolledKey("https://gateway.example.test", "per-device-key-0123456789");

        Assert.False(File.Exists(Path.Combine(_machineRoot, "config", "config.json")));
        Assert.False(File.Exists(Path.Combine(_machineRoot, "config", "director", "gateway-token.txt")));
    }

    [Fact]
    public void LoadGateway_ReadsTheDefaultDirectorsHome_NotTheMachineRoot()
    {
        // "Already connected" must mean the Director is connected. A gateway block in the machine root (what
        // a pre-fix enroll left behind) says nothing about the Director, which never reads it.
        var machineConfig = Path.Combine(_machineRoot, "config");
        Directory.CreateDirectory(machineConfig);
        File.WriteAllText(Path.Combine(machineConfig, "config.json"),
            """{ "gateway": { "url": "https://stale.example.test", "token": "k" } }""");
        var director = Windows();

        Assert.False(director.LoadGateway().IsEnabled);

        director.SaveEnrolledKey("https://gateway.example.test", "per-device-key-0123456789");
        Assert.Equal("https://gateway.example.test", director.LoadGateway().Url);
    }
}

/// <summary>
/// The command line's hosted enroll, end to end through the real runner with only the network and the browser
/// faked: the device it enrolls is the default Director's id and the key lands where that Director reads it.
/// In the hosted-gateway-url collection because the hosted address is read from a process-wide variable
/// that other classes point at stub hosts. It reads the connection with <see cref="GatewayConfig.LoadFrom"/>
/// rather than moving CC_DIRECTOR_ROOT; that LoadFrom and the Director's own Load agree is pinned above.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public sealed class DefaultDirectorHostedEnrollTests : IDisposable
{
    private readonly string _machineRoot;

    public DefaultDirectorHostedEnrollTests()
    {
        _machineRoot = Path.Combine(Path.GetTempPath(), "cli-enroll-hosted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_machineRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_machineRoot, recursive: true); } catch { /* temp cleanup only */ }
    }

    private sealed class CapturingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { deviceKey = "hosted-key-abc", deviceCount = 1 }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task HostedEnroll_EnrollsTheDirectorsIdAndConnectsTheDirector()
    {
        using var hosted = new HostedGatewayUrlOverride(null);
        var director = DefaultDirectorConnection.For(new InstallLayout(_machineRoot), OSPlatform.Windows);
        var bodies = new List<string>();
        var runner = new GatewayAccountEnrollRunner(
            signIn: _ => Task.FromResult(new DevThrottleTokens("account-token", "refresh-token")),
            handlerFactory: () => new CapturingHandler(bodies),
            persist: director.SaveEnrolledKey);

        var deviceId = director.LoadOrCreateDirectorId();
        var result = await runner.SignInAndEnrollHostedAsync(deviceId, "WORKSTATION-1", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        // The enrolled device is the id the Director will present - read back from the Director's own slot.
        var body = Assert.Single(bodies);
        Assert.Contains(File.ReadAllText(director.DirectorIdFile).Trim(), body);

        // And the Director's home holds the hosted connection.
        var seen = GatewayConfig.LoadFrom(director.StorageRoot);
        Assert.Equal(HostedGateway.DefaultUrl, seen.Url);
        Assert.Equal("hosted-key-abc", seen.Token);
    }
}
