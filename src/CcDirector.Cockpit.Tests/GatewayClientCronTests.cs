using System.Net;
using System.Text.Json;
using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Unit tests for the cron methods added to <see cref="GatewayClient"/> (issue #488): they target the
/// shipped <c>/cron/jobs</c> surface (#482-#484), parse the list/record envelopes, and surface the
/// Gateway's error body on a non-success status (so the Schedule form can show a 400/409 inline). The
/// HttpClient is stubbed by a routing handler - no network, no Gateway.
/// </summary>
public sealed class GatewayClientCronTests
{
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
        public RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_route(request));
    }

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Camel), System.Text.Encoding.UTF8, "application/json"),
    };

    private static GatewayClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var http = new HttpClient(new RouteHandler(route)) { BaseAddress = new Uri("http://gw.test/") };
        return new GatewayClient(http, NullLogger<GatewayClient>.Instance);
    }

    private static CronJobDto SampleJob(string id = "cj_abc123") => new()
    {
        Id = id, Name = "nightly", Enabled = true, ScheduleKind = "recurring", CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", WorkListName = "Tonight" },
    };

    [Fact]
    public async Task GetCronJobsAsync_ParsesJobsEnvelope()
    {
        var client = ClientFor(_ => Json(HttpStatusCode.OK, new { jobs = new[] { SampleJob("cj_1"), SampleJob("cj_2") } }));

        var jobs = await client.GetCronJobsAsync();

        Assert.Equal(2, jobs.Count);
        Assert.Equal("cj_1", jobs[0].Id);
        Assert.Equal("Tonight", jobs[0].Action.WorkListName);
    }

    [Fact]
    public async Task CreateCronJobAsync_PostsAndReturnsCreatedJob()
    {
        var client = ClientFor(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("cron/jobs", req.RequestUri!.ToString());
            return Json(HttpStatusCode.Created, SampleJob("cj_new"));
        });

        var created = await client.CreateCronJobAsync(SampleJob());

        Assert.NotNull(created);
        Assert.Equal("cj_new", created.Id);
    }

    [Fact]
    public async Task CreateCronJobAsync_OnBadRequest_ThrowsWithServerMessage()
    {
        var client = ClientFor(_ => Json(HttpStatusCode.BadRequest, new { error = "invalid cron expression: not a cron" }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateCronJobAsync(SampleJob()));
        Assert.Contains("invalid cron expression", ex.Message); // surfaced for inline display (AC4)
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task RunCronJobNowAsync_ReturnsRecord_AndSurfaces409Overlap()
    {
        var ok = ClientFor(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/run", req.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, new CronRunRecord
            {
                ScheduledUtc = DateTime.UtcNow, FiredUtc = DateTime.UtcNow,
                TargetDirectorId = "workstation-A", SessionId = "sid-1",
                InfraStatus = "started", TaskStatus = "unknown",
            });
        });
        var rec = await ok.RunCronJobNowAsync("cj_1");
        Assert.NotNull(rec);
        Assert.Equal("started", rec.InfraStatus);

        var busy = ClientFor(_ => Json(HttpStatusCode.Conflict, new { error = "a prior run of this job is still in flight" }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => busy.RunCronJobNowAsync("cj_1"));
        Assert.Contains("409", ex.Message);
    }

    [Fact]
    public async Task GetCronRunsAsync_ParsesRunsEnvelope()
    {
        var client = ClientFor(_ => Json(HttpStatusCode.OK, new
        {
            jobId = "cj_1",
            runs = new[]
            {
                new CronRunRecord { ScheduledUtc = DateTime.UtcNow, FiredUtc = DateTime.UtcNow, TargetDirectorId = "workstation-A", SessionId = "sid-1", InfraStatus = "started", TaskStatus = "unknown" },
            },
        }));

        var runs = await client.GetCronRunsAsync("cj_1");

        Assert.Single(runs);
        Assert.Equal("workstation-A", runs[0].TargetDirectorId);
    }

    [Fact]
    public async Task DeleteCronJobAsync_OnNotFound_Throws()
    {
        var client = ClientFor(_ => Json(HttpStatusCode.NotFound, new { error = "no such cron job" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteCronJobAsync("cj_nope"));
    }
}
