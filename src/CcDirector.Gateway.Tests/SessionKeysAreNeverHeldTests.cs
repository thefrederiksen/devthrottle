using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A PROMPT SENT WITH A SESSION KEY IS NEVER HELD AND NEVER HANDED TO THE GATEWAY'S DRIVER (the Delivery Lead's ruling
/// on merging Voice Delivery phase 5 with #3435, "A session may type into the sessions it owns", 26 September 2026).
/// Holding and the driver are for the OWNER'S OWN words, typed on the owner's own devices: an agent's words are its own
/// to retry, its send goes only through #3435's guarded send, and the call answers what happened - delivered, not
/// delivered with its reason, or could not be reached now. Before the ruling, a session key's prompt to a session whose
/// Director was unreachable was held as "still delivering" and later typed by the driver with no ownership check and no
/// composer guard - over the owner's unsent words, which #3435 says are never typed over.
///
/// Every request goes over HTTP through the real <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted
/// session keys on a real HOSTED host, and the Director is a real SignalR connection whose answers the tests play - so
/// the guard, the route, the hold and the driver are the production pieces, run together.
/// </summary>
public sealed class SessionKeysAreNeverHeldTests : IAsyncLifetime
{
    private const string SharedToken = "session-keys-never-held-token";
    private const string DirectorId = "director-never-held";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenant = default;
    private HttpClient _owner = null!;
    private HttpClient _parent = null!;
    private HttpClient _raised = null!;
    private HubConnection _conn = null!;
    private long _pushSequence;

    private readonly string _parentId = Guid.NewGuid().ToString();
    private readonly string _childId = Guid.NewGuid().ToString();
    private readonly string _raisedId = Guid.NewGuid().ToString();
    private readonly string _workerId = Guid.NewGuid().ToString();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-session-keys-never-held-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public SessionKeysAreNeverHeldTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        // Only a test's own wake-ups drive anything, so a hold the test makes by hand is never raced by the tick.
        _gateway.HeldDeliveryTickInterval = TimeSpan.FromHours(1);
        await _gateway.StartAsync();

        var subject = $"sub-never-held-{_runId}";
        var enrolled = HostedTestEnrollment.Enroll(_gateway, subject, $"never-held-{_runId}@example.com",
            $"dev-never-held-dir-{_runId}", "MNH");
        _tenant = enrolled.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        _owner = Client(_gateway.Devices.RegisterForTenant(_tenant, subject, $"dev-never-held-owner-{_runId}", "OWNER",
            deviceType: "browser").DeviceKey);
        _parent = Client(SessionKey(_parentId));
        _raised = Client(SessionKey(_raisedId));

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/",
            MachineName = "never-held-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        // The Director says on its Hello-streamed capabilities that it checks the session is waiting before it
        // types, so a session key's guarded send is allowed at all (a Director that does not is refused, not held).
        _gateway.TurnPushCapabilities.Record(_tenant, DirectorId, pushesTurns: true, checksIdleBeforeTyping: true);

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream",
                o => o.AccessTokenProvider = () => Task.FromResult<string?>(enrolled.DeviceKey))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb == DeliveryStateRequest.Verb)
            {
                // The question "what became of delivery id X?" is answered with no answer of any kind - the
                // unanswered send the Held reading is built from.
                var noAnswer = DirectorCommandResult.Fail(DirectorCommandStatus.Timeout,
                    "the Director did not answer the question within 30 seconds");
                noAnswer.CommandId = cmd.CommandId;
                return noAnswer;
            }
            if (cmd.Verb != "prompt")
                return await Task.FromResult(DirectorCommandResult.Success("{}"));
            var body = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            _out.WriteLine($"the Director received a prompt: deliveryId={body.DeliveryId}");
            // The prompt verb is answered with no answer of any kind - the unanswered send the Held reading is
            // built from.
            var played = DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
            played.CommandId = cmd.CommandId;
            return played;
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        await PushSessions();
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _owner.Dispose();
        _parent.Dispose();
        _raised.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static SessionDto Session(string id, string? owner) => new()
    {
        SessionId = id,
        Name = "session " + id[..8],
        ActivityState = "WaitingForInput",
        IsControlled = owner is not null,
        ControllerSessionId = owner,
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        LastActivityAt = DateTime.UtcNow,
    };

    private async Task PushSessions()
    {
        await _conn.InvokeAsync("PushSnapshot", ++_pushSequence, new[]
        {
            Session(_parentId, owner: null),
            Session(_childId, owner: _parentId),
            Session(_raisedId, owner: null),
            Session(_workerId, owner: null),
        });
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenant, DirectorId, sessionId,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private async Task Raise(string sessionId)
    {
        using var resp = await _owner.PostAsync($"sessions/{sessionId}/raise", content: null);
        Assert.True(resp.IsSuccessStatusCode, "the owner could not raise the session: " + (int)resp.StatusCode);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostPrompt(HttpClient http, string sid, string text)
    {
        using var resp = await http.PostAsJsonAsync($"sessions/{sid}/prompt", new { text, appendEnter = true });
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST sessions/{sid}/prompt -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + (body.Length > 600 ? body[..600] + " ..." : body));
        return (resp.StatusCode, string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>Nothing new is held after the call: the tenant's held typed prompts and the driver's tracked
    /// deliveries are exactly what they were before it (the class's tests share the store, and the owner's own test
    /// holds one on purpose).</summary>
    private (int Held, int Driven) HeldBefore() =>
        (_gateway.TypedPrompts.ForTenant(_tenant).HeldDeliveries().Count, _gateway.HeldDeliveries!.HeldCount);

    /// <summary>Drop the Director's tunnel, so its sessions exist but cannot be located now - the frozen-Director
    /// state of QA's case 9, reached at once rather than by waiting out the staleness window.</summary>
    private async Task FreezeTheDirector()
    {
        await _conn.StopAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (_gateway.PushedSessions.TryLocate(_tenant, _childId, TimeSpan.FromMinutes(10)) is not null
               && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
        Assert.Null(_gateway.PushedSessions.TryLocate(_tenant, _childId, TimeSpan.FromMinutes(10)));
    }

    // ---- the ruling's tests -------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_SessionKeyToItsOwnChildWhoseDirectorIsUnreachable_IsNotHeld_AndAnswersNotReachedNow()
    {
        // The seam the ruling closes: the child is owned by the caller, but its Director cannot be located now, so
        // the ownership rule never ran and the hold used to take the prompt anyway - to be typed LATER, unguarded,
        // by the Gateway's driver. A session key is never held: it is answered "could not be reached now, try
        // again" (the pre-phase-5 answer), and the driver tracks nothing.
        await FreezeTheDirector();
        var before = HeldBefore();

        var (status, body) = await PostPrompt(_parent, _childId, "carry on");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("director_stale", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean(), "the caller must be told to retry, not that it is gone");
        Assert.Equal(before, HeldBefore());
    }

    [Fact]
    public async Task Prompt_ARaisedSessionKeyThatTimesOut_IsAnsweredWithItsDeliveryId_AndNothingIsDrivenLater()
    {
        // The one session caller that reaches the ordinary typed send is a RAISED session (the ownership rule
        // answered every other one). Its prompt is sent and unanswered - the exact reading that used to hold it
        // 202 and hand it to the driver. It is answered instead: not accepted, the reason, and its delivery id
        // (so the Director refuses a repeat), and nothing is held, so no later wake-up presses it.
        await Raise(_raisedId);
        var before = HeldBefore();

        var (status, body) = await PostPrompt(_raised, _workerId, "carry on");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("deliveryId").GetString());
        var deliveryId = body.GetProperty("deliveryId").GetString()!;
        // Nothing was written for this prompt, so no later wake-up can press it.
        Assert.Equal(TypedPromptReadKind.Absent, _gateway.TypedPrompts.ForTenant(_tenant).Read(deliveryId).Kind);
        Assert.Equal(before, HeldBefore());

        // Nothing is driven later: the wake-ups find nothing to press.
        await _gateway.TypedPromptDriver.DriveOnceAsync(_tenant, _gateway.TypedPrompts.ForTenant(_tenant), deliveryId,
            TypedPromptDecisions.DriveTick);
        Assert.Equal(before, HeldBefore());
    }

    [Fact]
    public async Task Prompt_TheOwnersOwnDeviceToAnUnreachableSession_IsStillHeldAsBefore()
    {
        // The ruling widens nothing and narrows nothing for the owner: the owner's own words to a session that
        // cannot be located now are still held 202 waiting-for-director and driven by the Gateway, exactly as
        // contract section 8 has it.
        await FreezeTheDirector();
        var before = HeldBefore();

        var (status, body) = await PostPrompt(_owner, _childId, "from the owner");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("waiting-for-director", body.GetProperty("directorState").GetString());
        var deliveryId = body.GetProperty("deliveryId").GetString()!;
        Assert.True(_gateway.TypedPrompts.ForTenant(_tenant).Read(deliveryId).Record!.NeverSent);
        Assert.Equal(before.Driven + 1, _gateway.HeldDeliveries!.HeldCount);
    }
}
