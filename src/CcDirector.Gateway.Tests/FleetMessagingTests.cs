using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the pure message-framing helper used by fleet session-to-session
/// messaging (issue #705). Pure and machine-independent.
/// </summary>
public sealed class FleetMessagingFramingTests
{
    [Fact]
    public void ShortId_truncates_to_eight_characters()
    {
        Assert.Equal("4c810000", FleetMessaging.ShortId("4c810000-1111-2222"));
        Assert.Equal("abc", FleetMessaging.ShortId("abc"));
        Assert.Equal("", FleetMessaging.ShortId(null));
    }

    [Fact]
    public void BuildFramedMessage_WithName_includes_name_machine_id_and_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "4c810000-1111-2222-3333-444444444444", "feature-work", "machine-A", "run the tests");

        Assert.Contains("[message from feature-work (machine-A), id 4c810000]", framed);
        Assert.Contains("run the tests", framed);
        Assert.Contains("(to reply: cc-send 4c810000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithIdButNoName_uses_generic_session_header_with_reply()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "9b2f0000-aaaa-bbbb-cccc-dddddddddddd", null, "machine-B", "hello");

        Assert.Contains("[message from session 9b2f0000 (machine-B)]", framed);
        Assert.Contains("(to reply: cc-send 9b2f0000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithNoSender_is_anonymous_and_has_no_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(null, null, "machine-C", "broadcast text");

        Assert.Contains("[message from another session]", framed);
        Assert.Contains("broadcast text", framed);
        Assert.DoesNotContain("to reply:", framed);
    }
}

/// <summary>
/// Endpoint validation tests for the /fleet/* relay routes (issue #705). These assert the
/// deterministic request-validation behavior that runs before any Gateway interaction, so they
/// pass whether or not this machine has a Gateway configured. The richer relay / no-Gateway
/// delivery behavior is verified by live proof against a running Director.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetMessagingEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task Fleet_send_missing_target_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send", new FleetSendRequest { ToSessionId = "", Text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_send_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send", new FleetSendRequest { ToSessionId = "not-a-guid", Text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_send_empty_text_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/send",
            new FleetSendRequest { ToSessionId = Guid.NewGuid().ToString(), Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_broadcast_empty_text_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/broadcast", new FleetBroadcastRequest { Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_missing_target_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest { ToSessionId = "", Question = "q?" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_bad_guid_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask", new FleetAskRequest { ToSessionId = "not-a-guid", Question = "q?" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fleet_ask_empty_question_returns_400()
    {
        var resp = await _client.PostAsJsonAsync("fleet/ask",
            new FleetAskRequest { ToSessionId = Guid.NewGuid().ToString(), Question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
