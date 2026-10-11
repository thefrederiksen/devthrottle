using System.Net;
using System.Net.Http.Json;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Activity;

/// <summary>
/// End-to-end tests for the activity ledger's front door over a real HTTP pipeline: a producer POSTs
/// observed events (idempotent by producer-minted id), and diagnosis GETs them back. Mapped without a
/// tenant boundary, exactly like the prompt endpoint tests - the self-host Local path; the hosted
/// two-tenant behavior is proven separately over a full GatewayHost.
/// </summary>
public sealed class ActivityEventEndpointsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private GatewayDbTestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = new GatewayDbTestHarness();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        // Self-host harness: the REAL self-host boundary, built over the SingleTenantContext, which always
        // resolves Local. A null boundary is a refusal in every mode now that the mode is a value the host
        // hands over rather than the process environment, so the absence is no longer a self-host form.
        ActivityEventEndpoints.Map(_app, new ActivityEventStore(_harness.Open()),
            tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false));
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _harness?.Dispose();
    }

    private static ActivityEventRecord Rec(Guid? id = null, DateTime? occurredUtc = null) => new()
    {
        EventId = id ?? Guid.NewGuid(),
        DirectorSequence = 7,
        OccurredUtc = occurredUtc ?? DateTime.UtcNow,
        DirectorId = "dir-1",
        SessionId = "s1",
        AgentKind = "Claude",
        EventType = ActivityEventTypes.TerminalOutputWhileSettled,
        Cause = ActivityCauses.TerminalOutputOnly,
        OutputByteCount = 96,
    };

    [Fact]
    public async Task A_producer_pushes_and_diagnosis_reads_it_back()
    {
        var post = await _client.PostAsJsonAsync("/activity-events/batch",
            new ActivityEventIngestRequest { Events = new[] { Rec(), Rec() } });

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var ack = await post.Content.ReadFromJsonAsync<ActivityEventIngestResponse>();
        Assert.Equal(2, ack!.Written);
        Assert.Equal(0, ack.Duplicates);

        var body = await _client.GetFromJsonAsync<EventsResponse>("/activity-events?sessionId=s1");
        Assert.Equal(2, body!.Count);
        Assert.All(body.Events, e => Assert.Equal(ActivityCauses.TerminalOutputOnly, e.Cause));
    }

    [Fact]
    public async Task A_retried_batch_is_acknowledged_as_duplicates_not_an_error()
    {
        var batch = new ActivityEventIngestRequest { Events = new[] { Rec() } };

        await _client.PostAsJsonAsync("/activity-events/batch", batch);
        var retry = await _client.PostAsJsonAsync("/activity-events/batch", batch);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var ack = await retry.Content.ReadFromJsonAsync<ActivityEventIngestResponse>();
        Assert.Equal(0, ack!.Written);
        Assert.Equal(1, ack.Duplicates);

        var body = await _client.GetFromJsonAsync<EventsResponse>("/activity-events?sessionId=s1");
        Assert.Equal(1, body!.Count);
    }

    [Fact]
    public async Task An_empty_push_is_rejected()
    {
        var resp = await _client.PostAsJsonAsync("/activity-events/batch",
            new ActivityEventIngestRequest { Events = Array.Empty<ActivityEventRecord>() });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_malformed_event_is_rejected_with_the_validation_message()
    {
        var resp = await _client.PostAsJsonAsync("/activity-events/batch",
            new ActivityEventIngestRequest { Events = new[] { Rec() with { EventType = "nonsense" } } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("not an activity event type", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_backwards_range_is_rejected()
    {
        var resp = await _client.GetAsync(
            $"/activity-events?from={DateTime.UtcNow:O}&to={DateTime.UtcNow.AddHours(-1):O}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>The GET /activity-events body shape.</summary>
    private sealed record EventsResponse(int Count, IReadOnlyList<ActivityEventRecord> Events);
}
