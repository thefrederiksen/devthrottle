using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Setup.Cli;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// Issue #3506, through the command's OWN wiring: <c>enroll</c> must enroll the default Director's id and
/// land the connection in that Director's home (<c>&lt;root&gt;\instances\default</c>), the only place it
/// reads one. Each test drives <see cref="Commands.EnrollAsync(CliArgs, InstallLayout, bool, Func{Action{string, string}, GatewayAccountEnrollRunner})"/>
/// with the browser sign-in and the network faked, so a regression fails in a second instead of opening a
/// browser and waiting five minutes for nobody.
///
/// The process's own storage root (CC_DIRECTOR_ROOT) is pinned to a SEPARATE empty folder for the life of
/// each test. That is what the pre-fix command read and wrote, so a regression back to it is observable
/// here - and it lands in a temp folder, never in the real machine's configuration.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class EnrollTargetsTheDefaultDirectorTests : IDisposable
{
    private const string Key = "hosted-key-0123456789";

    private readonly string _installRoot;
    private readonly string _processRoot;
    private readonly string? _previousRoot;
    private readonly string? _previousHosted;
    private readonly List<string> _requests = new();
    private int _signIns;

    public EnrollTargetsTheDefaultDirectorTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cli-enroll-target-" + Guid.NewGuid().ToString("N"));
        _installRoot = Path.Combine(tmp, "install");
        _processRoot = Path.Combine(tmp, "process-root");
        Directory.CreateDirectory(_installRoot);
        Directory.CreateDirectory(_processRoot);
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousHosted = Environment.GetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _processRoot);
        Environment.SetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable, _previousHosted);
        try { Directory.Delete(Path.GetDirectoryName(_installRoot)!, recursive: true); } catch { /* temp cleanup only */ }
    }

    private sealed class FakeHostedGateway(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { deviceKey = Key, deviceCount = 1 }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    // The runner the command builds, with only the browser and the network replaced. The persist step is
    // the one the COMMAND chose - that choice is half of what is under test.
    private GatewayAccountEnrollRunner FakeRunner(Action<string, string> persist) => new(
        signIn: _ =>
        {
            Interlocked.Increment(ref _signIns);
            return Task.FromResult(new DevThrottleTokens("account-token", "refresh-token"));
        },
        handlerFactory: () => new FakeHostedGateway(_requests),
        persist: persist,
        httpTimeout: TimeSpan.FromSeconds(5));

    private async Task<(int code, string output)> RunAsync(params string[] argv)
    {
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try
        {
            var code = await Commands.EnrollAsync(CliArgs.Parse(argv), new InstallLayout(_installRoot), json: true, FakeRunner);
            return (code, output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Fact]
    public async Task EnrollHosted_EnrollsTheDefaultDirectorsIdAndConnectsThatDirector()
    {
        var (code, output) = await RunAsync("enroll", "--hosted", "--json");

        Assert.True(code == ExitCodes.Ok, output);
        var director = DefaultDirectorConnection.For(new InstallLayout(_installRoot));

        // The device enrolled is the id the default Director will present: the one in its own slot.
        Assert.True(File.Exists(director.DirectorIdFile), $"no id in the default Director's slot: {director.DirectorIdFile}");
        var body = Assert.Single(_requests);
        Assert.Contains(File.ReadAllText(director.DirectorIdFile).Trim(), body);

        // The connection is in the default Director's home, and nowhere in the process's own root.
        var seen = GatewayConfig.LoadFrom(director.StorageRoot);
        Assert.Equal(HostedGateway.DefaultUrl, seen.Url);
        Assert.Equal(Key, seen.Token);
        Assert.False(File.Exists(Path.Combine(_processRoot, "config", "config.json")),
            "enroll wrote the connection into the process's own root, where the Director never reads it");
    }

    [Fact]
    public async Task Enroll_DefaultDirectorAlreadyConnected_ReportsItAndSignsNothingIn()
    {
        DefaultDirectorConnection.For(new InstallLayout(_installRoot))
            .SaveEnrolledKey("https://gateway.example.test", "per-device-key-0123456789");

        var (code, output) = await RunAsync("enroll", "--json");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("\"alreadyConnected\":true", output.Replace(" ", ""));
        Assert.Contains("https://gateway.example.test", output);
        Assert.Equal(0, _signIns);
        Assert.Empty(_requests);
    }
}

/// <summary>Serializes the tests that change process-wide state: Console.Out and environment variables.</summary>
[CollectionDefinition(Name)]
public sealed class ProcessWideStateCollection
{
    public const string Name = "ProcessWideState";
}
