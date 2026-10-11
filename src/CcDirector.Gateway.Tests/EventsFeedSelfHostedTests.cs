using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the tenant-scoped events feed preserves the single Local tenant behavior of a self-hosted Gateway.
/// The test uses the real route and the local registration seam that publishes Director arrivals.
/// </summary>
public sealed class EventsFeedSelfHostedTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-events-self-hosted-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Events_feed_on_self_host_announces_local_director()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var subscription = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await subscription.Content.ReadAsStreamAsync());

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-local-feed",
            TailnetEndpoint = "http://127.0.0.1:9/",
            MachineName = "local",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventData = await ReadNextEventDataAsync(reader, timeout.Token);
        Assert.Contains("dir-local-feed", eventData);
    }

    private static async Task<string> ReadNextEventDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line[6..];
        }

        throw new EndOfStreamException("The events feed closed before publishing an event.");
    }

}
