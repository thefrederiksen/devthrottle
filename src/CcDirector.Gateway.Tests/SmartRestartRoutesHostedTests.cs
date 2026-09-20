using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE THREE COMMAND LINE ROUTES ONTO THE SMART SHUTDOWN, ON A REAL HOSTED GATEWAY (mission "Smart Director
/// Restart", section 5.3 item 12; review of phase 4, finding 1).
///
/// Everything else about this door is proven without a server: the guard is a pure function with its own
/// tests, the Director's dispatch is tested on a real ControlApiHost, the translation over what the real
/// engine raised, and the command line against a stubbed Gateway. NOTHING reached these three route
/// handlers. So a route mapped one word off, a broken relay, a wrong status code or a miswritten tenant
/// resolution would have shipped green through every one of those suites - and the owner would meet it in
/// the one situation this door exists for: the window is broken, he reaches for the command line, and the
/// route is not where the guard, the contract and the documentation all say it is.
///
/// WHAT ONLY A BOOTED HOST PROVES, and it is the whole reason this file exists:
///
///  - THE ROUTES ARE MAPPED AT THE SHAPES THE GUARD REFUSES AND THE DOCUMENTATION PROMISES. A guard test
///    pins a refused shape against itself; only a real request proves a handler is listening at it.
///  - THE REFUSAL IS REAL. A session key POSTing the start is refused THROUGH THE AUTHENTICATION with the
///    named sentence, and the Director is never asked - the command does not reach the tunnel at all.
///  - THE RELAY CARRIES THE RIGHT VERB AND THE RIGHT BODY. The owner's device key reaches the engine over
///    the tunnel; the order it asked for arrives; the answer comes back with its status.
///  - THE TENANT BOUNDARY HOLDS. Another account's device key finds none of the three.
///  - AN UNREADABLE ANSWER IS A 502 THAT SAYS SO, never an empty success.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public sealed class SmartRestartRoutesHostedTests : IAsyncLifetime
{
    private const string SharedToken = "smart-restart-routes-token";

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-smart-restart-routes-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorId;
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly ConcurrentQueue<DirectorCommand> _asked = new();

    private string? _priorHosted;
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _director = null!;
    private string _deviceKeyOwner = "";
    private string _deviceKeyStranger = "";
    private string _sessionKey = "";

    public SmartRestartRoutesHostedTests(ITestOutputHelper output)
    {
        _out = output;
        _directorId = "director-sr-" + _runId;
    }

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var owner = HostedTestEnrollment.Enroll(_gateway, $"sub-sr-a-{_runId}", $"sr-a-{_runId}@example.com", $"dev-sra-{_runId}", "MSRA");
        var stranger = HostedTestEnrollment.Enroll(_gateway, $"sub-sr-b-{_runId}", $"sr-b-{_runId}@example.com", $"dev-srb-{_runId}", "MSRB");
        Assert.NotEqual(owner.Tenant.Value, stranger.Tenant.Value);
        _deviceKeyOwner = owner.DeviceKey;
        _deviceKeyStranger = stranger.DeviceKey;

        // A session key of the owner's own account, on this very Director - the closest an agent can get.
        _sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(owner.Tenant, _directorId, _sessionId,
            GatewaySessionKey.Hash(_sessionKey), DateTime.UtcNow.AddHours(1)));

        // The Director on the tunnel, bound to the owner's account. It records every verb it is asked and
        // answers the three of this door; anything else is refused, so a relay that sent the wrong verb
        // fails rather than passing quietly.
        _director = await FakeTunnelDirector.StartAsync(_gateway, _deviceKeyOwner, _directorId, "MSRA", Answer);
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ===== What the Director on the other end of the tunnel answers =====

    private static readonly SmartRestartStartAccepted Accepted = new()
    {
        Taken = true,
        Minutes = 15,
        Detail = "The smart restart of DevThrottle_1 has started. Every session is asked to hand over.",
    };

    private static readonly SmartRestartProgressDto Progress = new()
    {
        Running = true,
        Started = true,
        Phase = "Collecting",
        PhaseLabel = "Waiting for the handovers",
        CountLabel = "1 of 2 shut down",
        Total = 2,
        Gone = 1,
        Sessions = { new SmartRestartSessionDto { SessionId = "s1", Name = "A lead", State = "Asked", StateLabel = "Asked to hand over" } },
    };

    private static readonly SmartRestartHistoryDto History = new()
    {
        Refused = false,
        Message = "This Director has one restart record.",
        Entries = { new SmartRestartHistoryEntryDto { WorkspaceId = "restart-2026-09-20-0800", WhenLabel = "Today at 08:00" } },
    };

    private DirectorCommandResult Answer(DirectorCommand cmd)
    {
        _asked.Enqueue(cmd);
        if (cmd.Verb == SmartRestartVerbs.Start)
        {
            var order = JsonSerializer.Deserialize<SmartRestartStartOrder>(cmd.PayloadJson, FakeTunnelDirector.WebJson)!;
            return FakeTunnelDirector.Ok(new SmartRestartStartAccepted
            {
                Taken = true,
                DirectorId = _directorId,
                Minutes = order.Minutes,
                Detail = Accepted.Detail,
            });
        }
        if (cmd.Verb == SmartRestartVerbs.Progress) return FakeTunnelDirector.Ok(Progress);
        if (cmd.Verb == SmartRestartVerbs.History) return FakeTunnelDirector.Ok(History);
        return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"not served in this test: {cmd.Verb}");
    }

    // ===== The door =====

    /// <summary>
    /// EMPTYING A DIRECTOR IS THE OWNER'S, and the refusal happens inside the authentication - so it is
    /// terminal, and the Director is never asked. The sentence is the one the guard holds, which names what
    /// a session MAY do instead: an agent told only "no" goes looking for a way round.
    /// </summary>
    [Fact]
    public async Task ASessionKey_IsRefusedTheStart_WithTheSentenceNamingWhatItMayDoInstead()
    {
        using var agent = Client(_sessionKey);

        var (status, body) = await Send(agent, HttpMethod.Post, $"directors/{_directorId}/smart-restart", new { minutes = 10 });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_out_of_scope", body.GetProperty("code").GetString());
        Assert.Equal(SmartRestartRefusal.Start, body.GetProperty("error").GetString());
        Assert.Empty(_asked);
    }

    /// <summary>
    /// The owner's own key reaches the engine over the tunnel: the route is mapped where the guard, the
    /// contract and the documentation say, the start verb is the one that travels, the minutes and the
    /// reason arrive as asked, and the acceptance comes back as a 202 - taken, not finished.
    /// </summary>
    [Fact]
    public async Task TheOwnersDeviceKey_ReachesTheEngineOverTheTunnel_AndTheAcceptanceComesBack()
    {
        using var owner = Client(_deviceKeyOwner);

        var (status, body) = await Send(owner, HttpMethod.Post, $"directors/{_directorId}/smart-restart",
            new { minutes = 15, reason = "update to 2.9.0" });

        Assert.Equal(HttpStatusCode.Accepted, status);
        var asked = Assert.Single(_asked);
        Assert.Equal(SmartRestartVerbs.Start, asked.Verb);
        var order = JsonSerializer.Deserialize<SmartRestartStartOrder>(asked.PayloadJson, FakeTunnelDirector.WebJson)!;
        Assert.Equal(15, order.Minutes);
        Assert.Equal("update to 2.9.0", order.Reason);
        Assert.True(body.GetProperty("taken").GetBoolean());
        Assert.Equal(15, body.GetProperty("minutes").GetInt32());
        Assert.Equal(Accepted.Detail, body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// The two READS are open to a session key, and they are answered BY THE ROUTE rather than by the guard:
    /// asking how an emptying is going, or what was emptied last week, is not asking to empty anything. Each
    /// carries the Director's own words down untouched.
    /// </summary>
    [Fact]
    public async Task ASessionKeyMayReadTheProgressAndTheHistory_AndTheRoutesRelayTheDirectorsOwnWords()
    {
        using var agent = Client(_sessionKey);

        var (progressStatus, progress) = await Send(agent, HttpMethod.Get, $"directors/{_directorId}/smart-restart");
        var (historyStatus, history) = await Send(agent, HttpMethod.Get, $"directors/{_directorId}/restart-history");

        Assert.Equal(HttpStatusCode.OK, progressStatus);
        Assert.Equal(HttpStatusCode.OK, historyStatus);
        Assert.Equal(new[] { SmartRestartVerbs.Progress, SmartRestartVerbs.History },
            _asked.Select(c => c.Verb).ToArray());
        Assert.Equal(Progress.PhaseLabel, progress.GetProperty("phaseLabel").GetString());
        Assert.Equal(Progress.CountLabel, progress.GetProperty("countLabel").GetString());
        Assert.Equal("Asked to hand over", progress.GetProperty("sessions")[0].GetProperty("stateLabel").GetString());
        Assert.Equal(History.Message, history.GetProperty("message").GetString());
        Assert.Equal("restart-2026-09-20-0800", history.GetProperty("entries")[0].GetProperty("workspaceId").GetString());
    }

    /// <summary>
    /// The owner's own key reads them too - the same routes, answered the same way. Read beside the test
    /// above, this is what says the reads are open to BOTH and not accidentally session-only.
    /// </summary>
    [Fact]
    public async Task TheOwnersDeviceKey_ReadsTheProgressAndTheHistoryToo()
    {
        using var owner = Client(_deviceKeyOwner);

        var (progressStatus, _) = await Send(owner, HttpMethod.Get, $"directors/{_directorId}/smart-restart");
        var (historyStatus, _) = await Send(owner, HttpMethod.Get, $"directors/{_directorId}/restart-history");

        Assert.Equal(HttpStatusCode.OK, progressStatus);
        Assert.Equal(HttpStatusCode.OK, historyStatus);
    }

    /// <summary>
    /// ANOTHER ACCOUNT'S DIRECTOR IS NOT FOUND, on all three. The resolver is the one every other
    /// <c>/directors</c> route uses, and the answer is a not-found rather than a refusal, so a stranger
    /// cannot learn that this Director exists. Nothing is asked of the Director either way.
    /// </summary>
    [Fact]
    public async Task AnotherAccountsDeviceKey_FindsNoneOfTheThree_AndTheDirectorIsNeverAsked()
    {
        using var stranger = Client(_deviceKeyStranger);

        var start = await Send(stranger, HttpMethod.Post, $"directors/{_directorId}/smart-restart", new { minutes = 10 });
        var progress = await Send(stranger, HttpMethod.Get, $"directors/{_directorId}/smart-restart");
        var history = await Send(stranger, HttpMethod.Get, $"directors/{_directorId}/restart-history");

        Assert.Equal(HttpStatusCode.NotFound, start.Status);
        Assert.Equal(HttpStatusCode.NotFound, progress.Status);
        Assert.Equal(HttpStatusCode.NotFound, history.Status);
        Assert.Empty(_asked);
    }

    /// <summary>
    /// A DIRECTOR THAT ANSWERS WITH NOTHING READABLE IS A FAILURE THAT SAYS SO. An empty success here would
    /// tell the owner a Director was emptied when nothing is known to have happened at all.
    /// </summary>
    [Fact]
    public async Task ADirectorThatAnswersWithNothingReadable_IsRefusedWithTheReason_NotAnEmptySuccess()
    {
        _director.OnCommand(cmd => { _asked.Enqueue(cmd); return DirectorCommandResult.Success("null"); });
        using var owner = Client(_deviceKeyOwner);

        var start = await Send(owner, HttpMethod.Post, $"directors/{_directorId}/smart-restart", new { minutes = 10 });
        var progress = await Send(owner, HttpMethod.Get, $"directors/{_directorId}/smart-restart");
        var history = await Send(owner, HttpMethod.Get, $"directors/{_directorId}/restart-history");

        Assert.Equal(HttpStatusCode.BadGateway, start.Status);
        Assert.Equal(HttpStatusCode.BadGateway, progress.Status);
        Assert.Equal(HttpStatusCode.BadGateway, history.Status);
        Assert.Contains("whether it started is unknown", start.Body.GetProperty("error").GetString());
        Assert.Contains("where the smart restart stands is unknown", progress.Body.GetProperty("error").GetString());
        Assert.Contains("An empty history is never reported as one that could not be read",
            history.Body.GetProperty("error").GetString());
    }

    /// <summary>
    /// A body that is not an order is refused as exactly that, and the Director is never asked - a start
    /// that could not be read is never begun on a guessed default.
    /// </summary>
    [Fact]
    public async Task AStartWhoseOrderCannotBeRead_IsRefused_AndNothingIsAsked()
    {
        using var owner = Client(_deviceKeyOwner);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"directors/{_directorId}/smart-restart")
        {
            Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await owner.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_asked);
    }

    // ===== Plumbing =====

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{method} {path} -> {(int)resp.StatusCode}: {(text.Length > 400 ? text[..400] + "..." : text)}");
        var json = text.Length > 0 && (text[0] == '{' || text[0] == '[')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : JsonDocument.Parse("null").RootElement.Clone();
        return (resp.StatusCode, json);
    }
}
