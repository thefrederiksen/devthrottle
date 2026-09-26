using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Attribution;

/// <summary>
/// A TYPED PROMPT GETS A DELIVERY ID AND THE GATEWAY RESOLVES IT, PROVEN ACROSS THE REAL ROUTE (Voice Delivery mission,
/// phase 5, contract section 7). Phase 4 case 2f: a typed prompt was answered 200 "delivering", the Director in the end
/// refused it and typed nothing, and with no delivery id nobody could ask.
///
/// The same rig as <see cref="DeliveryIdIsGatewayAuthoritativeTests"/>: a real <see cref="GatewayHost"/>, its mapped
/// routes, a real SignalR Director connection, and on the Director side the REAL prompt core and the REAL
/// <c>delivery-state</c> verb over this test's own delivery record. The Gateway's driver is the host's own
/// (<see cref="GatewayHost.TypedPromptDriver"/>), called directly as its wake-ups will call it.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TypedPromptsAreResolvedByTheGatewayTests : IAsyncLifetime
{
    private const string Token = "test-token-typed-prompts";
    private const string DirectorId = "dir-typed-prompts";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-typed-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";
    private DeliveryRecord _directorRecord = null!;
    private readonly List<PromptRequest> _arrived = new();
    private readonly ExecuteActionTestBackend _backend = new();
    private int _asks;
    private readonly AheadClock _clock = new();

    /// <summary>When set, the Director answers the prompt verb with this instead of the real prompt core. Null: the real core.</summary>
    private Func<PromptRequest, DirectorCommandResult>? _promptAnswer;

    public TypedPromptsAreResolvedByTheGatewayTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-typed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: Path.Combine(_root, "keyvault.json"),
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        _gateway.DeliveryClock = _clock;
        // Only a test's own TickAsync ticks the Gateway's driver, so an attempt a test makes by hand never races it.
        _gateway.HeldDeliveryTickInterval = TimeSpan.FromHours(1);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        _directorRecord = new DeliveryRecord(Path.Combine(_root, "director-delivery-records"));

        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, _backend);
        _sid = _session.Id.ToString();

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/",
            MachineName = "typed-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb == DeliveryStateRequest.Verb)
            {
                Interlocked.Increment(ref _asks);
                var stateAnswer = SessionReadExecutor.DeliveryStateOf(cmd, _directorRecord);
                stateAnswer.CommandId = cmd.CommandId;
                return stateAnswer;
            }
            if (cmd.Verb != "prompt") return await SessionCommandExecutor.DispatchAsync(_sm, DirectorId, cmd);
            var body = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            lock (_arrived) _arrived.Add(body);
            var result = _promptAnswer is { } played
                ? played(body)
                : await SessionCommandExecutor.SendPromptAsync(_session, body, SendSource.UserInput, _directorRecord);
            result.CommandId = cmd.CommandId;
            return result;
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        await _conn.InvokeAsync("PushSnapshot", 1L, new[] { new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" } });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        var resp = await _http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> PostPrompt(string text)
        => Send(HttpMethod.Post, $"sessions/{_sid}/prompt", new { text, appendEnter = true });

    private int Arrived()
    {
        lock (_arrived) return _arrived.Count;
    }

    [Fact]
    public async Task A_typed_prompt_carries_a_minted_delivery_id_to_the_Director_and_back_to_the_caller()
    {
        // Proves T1 as the prompt leaves for the Director: a fresh id in the N spelling on every typed prompt, a
        // different one each time, and the same id on the answer body the caller reads.
        var (firstStatus, first) = await PostPrompt("the first typed prompt");
        var (_, second) = await PostPrompt("the second typed prompt");

        Assert.Equal(HttpStatusCode.OK, firstStatus);
        PromptRequest[] arrived;
        lock (_arrived) arrived = _arrived.ToArray();
        Assert.Equal(2, arrived.Length);
        Assert.Matches("^[0-9a-f]{32}$", arrived[0].DeliveryId);
        Assert.Matches("^[0-9a-f]{32}$", arrived[1].DeliveryId);
        Assert.NotEqual(arrived[0].DeliveryId, arrived[1].DeliveryId);
        Assert.Equal(arrived[0].DeliveryId, first.GetProperty("deliveryId").GetString());
        Assert.Equal(arrived[1].DeliveryId, second.GetProperty("deliveryId").GetString());
        Assert.True(first.GetProperty("accepted").GetBoolean());
        // Delivered at once: no record is written.
        Assert.Equal(TypedPromptReadKind.Absent, _gateway.TypedPrompts.Read(arrived[0].DeliveryId!).Kind);
    }

    [Fact]
    public async Task The_phase_4_case_2f_shape_is_held_asked_and_shown_back_and_the_prompt_is_never_sent_twice()
    {
        // Proves the case that started this: the Director accepts and answers "delivering", then refuses in the end and
        // types nothing (its record becomes not-delivered). The route answers 202 with the id; the driver asks and hears
        // not-delivered; the outcome route shows the words back with "Send anyway"; and exactly ONE prompt reached the
        // Director - the Gateway never re-sent the typed text.
        _promptAnswer = body =>
        {
            Assert.True(_directorRecord.TryBeginDelivery(_session.Id, body.DeliveryId!).Began);
            _directorRecord.MarkNotDelivered(_session.Id, body.DeliveryId!, "the composer never echoed the text");
            return DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse
            {
                Accepted = true, DeliveryState = DeliveryState.Delivering, ActivityState = "Working",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        };

        var (status, held) = await PostPrompt("Reply with only the word OK. Marker, choral lemur 24");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.True(held.GetProperty("delivering").GetBoolean());
        Assert.Equal("delivering", held.GetProperty("directorState").GetString());
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        Assert.Equal(0, _asks);

        var (heldStatus, heldOutcome) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.Accepted, heldStatus);
        Assert.Equal("delivering", heldOutcome.GetProperty("directorState").GetString());

        var result = await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId,
            TypedPromptDecisions.DriveDirectorConnected);
        Assert.Equal(TypedDriveResult.Finished, result);
        Assert.Equal(1, _asks);

        var (outcomeStatus, outcome) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.OK, outcomeStatus);
        Assert.False(outcome.GetProperty("submitted").GetBoolean());
        Assert.True(outcome.GetProperty("movedOn").GetBoolean());
        Assert.Equal("not-delivered", outcome.GetProperty("reason").GetString());
        Assert.True(outcome.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal("Reply with only the word OK. Marker, choral lemur 24", outcome.GetProperty("transcript").GetString());
        Assert.Equal(1, Arrived());
        Assert.Equal(0, _backend.TextsSent);
    }

    [Fact]
    public async Task An_unanswered_typed_prompt_is_held_at_once_and_the_route_does_not_ask()
    {
        // Proves the route holds an unanswered typed prompt at once - no inline question - and the driver then resolves
        // it from the Director's own record.
        _promptAnswer = body =>
        {
            Assert.True(_directorRecord.TryBeginDelivery(_session.Id, body.DeliveryId!).Began);
            return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        };

        var (status, held) = await PostPrompt("typed while the Director is starved");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("no-answer", held.GetProperty("directorState").GetString());
        Assert.Equal(0, _asks);
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        var names = _gateway.TypedPrompts.ReadDecisions(deliveryId).Select(l => l.Decision).ToArray();
        Assert.Equal(new[] { "sent-to-director", "director-answer", "still-delivering" }, names);

        _directorRecord.MarkDelivered(_session.Id, deliveryId);
        await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId, TypedPromptDecisions.DriveTick);

        var (outcomeStatus, outcome) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.OK, outcomeStatus);
        Assert.True(outcome.GetProperty("submitted").GetBoolean());
        Assert.Null(_gateway.TypedPrompts.Read(deliveryId).Record!.Text);
        Assert.Equal(1, Arrived());
    }

    [Fact]
    public async Task The_route_sets_the_send_time_and_hands_a_held_prompt_to_the_Gateways_own_driver()
    {
        // Proves F6 and the wiring into the Gateway's driver on the real host. The send time the Director judges the age
        // limit by is the moment the Gateway received the prompt - a body's own value is overwritten. And a held typed
        // prompt is handed to the SAME driver that drives dictations: its own tick asks the Director and finishes it,
        // with no hand-made attempt and nothing sent twice.
        _promptAnswer = body =>
        {
            Assert.True(_directorRecord.TryBeginDelivery(_session.Id, body.DeliveryId!).Began);
            return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        };
        var before = _clock.GetUtcNow().UtcDateTime;

        var (status, held) = await Send(HttpMethod.Post, $"sessions/{_sid}/prompt",
            new { text = "typed with a forged send time", appendEnter = true, sentAtUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Equal(HttpStatusCode.Accepted, status);
        PromptRequest arrived;
        lock (_arrived) arrived = Assert.Single(_arrived);
        Assert.InRange(arrived.SentAtUtc!.Value, before.AddSeconds(-1), _clock.GetUtcNow().UtcDateTime.AddSeconds(1));
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        var driver = _gateway.HeldDeliveries!;
        Assert.Equal(1, driver.HeldCount);

        _directorRecord.MarkDelivered(_session.Id, deliveryId);
        _clock.Ahead = HeldDeliveryDriver.ShortestWait + TimeSpan.FromSeconds(1);
        await driver.TickAsync();

        Assert.Equal(TypedPromptState.Delivered, _gateway.TypedPrompts.Read(deliveryId).Record!.State);
        Assert.Equal(0, driver.HeldCount);
        Assert.Equal(1, _asks);
        Assert.Equal(1, Arrived());
    }

    [Fact]
    public async Task The_outcome_route_never_sends_or_asks_and_refuses_another_account_and_another_session()
    {
        // Proves T5's gate: reading the outcome sends nothing and asks nothing; a delivery id held in ANOTHER account's
        // partition is not found, and so is this account's id asked for under a different session.
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var (_, held) = await PostPrompt("mine");
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        var otherAccountsId = TypedPromptDelivery.MintDeliveryId();
        _gateway.TypedPrompts.ForTenant(new TenantId("55555555-5555-5555-5555-555555555555"))
            .Hold(otherAccountsId, _sid, "theirs", DateTime.UtcNow, "no-answer", new TypedPromptDecisionFacts());
        var arrivedBefore = Arrived();

        var mine = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        var theirs = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{otherAccountsId}/outcome");
        var wrongSession = await Send(HttpMethod.Get, $"sessions/{Guid.NewGuid()}/prompts/{deliveryId}/outcome");
        var nonsense = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/not-an-id/outcome");

        Assert.Equal(HttpStatusCode.Accepted, mine.Status);
        Assert.Equal(HttpStatusCode.NotFound, theirs.Status);
        Assert.Equal(HttpStatusCode.NotFound, wrongSession.Status);
        Assert.Equal(HttpStatusCode.NotFound, nonsense.Status);
        Assert.Equal(arrivedBefore, Arrived());
        Assert.Equal(0, _asks);
        Assert.Equal(1, _gateway.TypedPrompts.ReadDecisions(deliveryId).Count(l => l.Decision == "still-delivering"));
    }

    [Fact]
    public async Task Ack_refuses_a_held_prompt_then_deletes_the_text_of_a_shown_back_one_and_is_idempotent()
    {
        // Proves T5's acknowledgement: a held prompt cannot be acknowledged (409); once shown back, the acknowledgement
        // deletes the words, answers the same the second time, and writes "acknowledged" once.
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var (_, held) = await PostPrompt("show me back");
        var deliveryId = held.GetProperty("deliveryId").GetString()!;

        var whileHeld = await Send(HttpMethod.Post, $"sessions/{_sid}/prompts/{deliveryId}/ack");
        Assert.Equal(HttpStatusCode.Conflict, whileHeld.Status);

        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, deliveryId).Began);
        _directorRecord.MarkNotDelivered(_session.Id, deliveryId, "refused");
        await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId, TypedPromptDecisions.DriveTick);

        var first = await Send(HttpMethod.Post, $"sessions/{_sid}/prompts/{deliveryId}/ack");
        var second = await Send(HttpMethod.Post, $"sessions/{_sid}/prompts/{deliveryId}/ack");

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Null(_gateway.TypedPrompts.Read(deliveryId).Record!.Text);
        Assert.Equal(1, _gateway.TypedPrompts.ReadDecisions(deliveryId).Count(l => l.Decision == "acknowledged"));
        var (_, after) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal("not-delivered", after.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("transcript").ValueKind);
    }

    [Fact]
    public async Task A_prompt_refused_before_the_session_was_touched_is_still_502_with_its_delivery_id()
    {
        // Proves the known-not-in row of T2 keeps today's 502, now with the delivery id, and holds nothing.
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");

        var (status, body) = await PostPrompt("to an ended session");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.False(body.GetProperty("accepted").GetBoolean());
        var deliveryId = body.GetProperty("deliveryId").GetString()!;
        Assert.Equal(TypedPromptReadKind.Absent, _gateway.TypedPrompts.Read(deliveryId).Kind);
    }

    [Fact]
    public async Task A_typed_prompt_to_a_session_that_cannot_be_located_now_is_held_waiting_for_its_Director_never_404()
    {
        // Proves contract section 8 on the route: a session no connected Director lists now (a stale or frozen Director)
        // is not "gone". The typed prompt is held 202 "waiting-for-director" with its delivery id, nothing reaches any
        // Director, and its outcome reads held.
        var unlocated = Guid.NewGuid().ToString();

        var (status, held) = await Send(HttpMethod.Post, $"sessions/{unlocated}/prompt", new { text = "for a frozen machine", appendEnter = true });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("waiting-for-director", held.GetProperty("directorState").GetString());
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        Assert.Equal(0, Arrived());
        var (outcomeStatus, outcome) = await Send(HttpMethod.Get, $"sessions/{unlocated}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.Accepted, outcomeStatus);
        Assert.Equal("waiting-for-director", outcome.GetProperty("directorState").GetString());
        Assert.True(_gateway.TypedPrompts.Read(deliveryId).Record!.NeverSent);

        // Its Director comes back and lists the session: the driver sends it ONCE, under its minted id (contract
        // section 10), and the outcome reads delivered.
        _promptAnswer = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse
        {
            Accepted = true, DeliveryState = DeliveryState.Delivered, ActivityState = "Working",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await _conn.InvokeAsync("PushSnapshot", 2L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
            new SessionDto { SessionId = unlocated, ActivityState = "WaitingForInput" },
        });
        var result = await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId,
            TypedPromptDecisions.DriveDirectorConnected);
        var again = await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId,
            TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.Finished, result);
        Assert.Equal(TypedDriveResult.NotHeld, again);
        PromptRequest sent;
        lock (_arrived) sent = Assert.Single(_arrived);
        Assert.Equal(deliveryId, sent.DeliveryId);
        Assert.Equal("for a frozen machine", sent.Text);
        var (deliveredStatus, delivered) = await Send(HttpMethod.Get, $"sessions/{unlocated}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.OK, deliveredStatus);
        Assert.True(delivered.GetProperty("submitted").GetBoolean());
    }

    [Fact]
    public async Task QA_case_9_typed_unanswered_then_frozen_is_held_on_every_wake_up_and_unconfirmed_with_the_words_at_5_01()
    {
        // Proves QA's case 9 for a typed prompt: the prompt goes out and is unanswered, then the Director freezes (its
        // tunnel is gone). Every driver wake-up holds it - 202 on the outcome route, never "gone", never a second send -
        // and at 5:01 from the sent time it is "could not confirm it arrived", with the words and no "Send anyway".
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var (status, held) = await PostPrompt("typed just before the freeze");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        await _conn.StopAsync();

        for (var wake = 1; wake <= 12; wake++)
        {
            _clock.Ahead = TimeSpan.FromSeconds(20 * wake);
            var result = await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId, TypedPromptDecisions.DriveTick);
            Assert.Equal(TypedDriveResult.Held, result);
            var (wakeStatus, wakeOutcome) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
            Assert.Equal(HttpStatusCode.Accepted, wakeStatus);
            Assert.Equal("waiting-for-director", wakeOutcome.GetProperty("directorState").GetString());
        }

        _clock.Ahead = TimeSpan.FromSeconds(301);
        await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId, TypedPromptDecisions.DriveTick);

        var (finalStatus, final) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.OK, finalStatus);
        Assert.Equal("unconfirmed", final.GetProperty("reason").GetString());
        Assert.False(final.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal("typed just before the freeze", final.GetProperty("transcript").GetString());
        Assert.Equal(1, Arrived());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> PostClaimedPrompt(string text, string deliveryIdClaim)
        => Send(HttpMethod.Post, $"sessions/{_sid}/prompt", new { text, appendEnter = true, deliveryIdClaim });

    /// <summary>
    /// A typed prompt held by the real route and then shown back not-delivered - the record a "Send anyway" claims - with
    /// the Director's own delivery record saying the id is not in.
    /// </summary>
    private async Task<string> ShownBack(string text)
    {
        _promptAnswer = body =>
        {
            Assert.True(_directorRecord.TryBeginDelivery(_session.Id, body.DeliveryId!).Began);
            _directorRecord.MarkNotDelivered(_session.Id, body.DeliveryId!, "the composer never echoed the text");
            return DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse
            {
                Accepted = true, DeliveryState = DeliveryState.Delivering, ActivityState = "Working",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        };
        var (_, held) = await PostPrompt(text);
        var deliveryId = held.GetProperty("deliveryId").GetString()!;
        var driven = await _gateway.TypedPromptDriver.DriveOnceAsync(TenantId.Local, _gateway.TypedPrompts, deliveryId,
            TypedPromptDecisions.DriveTick);
        Assert.Equal(TypedDriveResult.Finished, driven);
        Assert.Equal(TypedPromptState.NotDelivered, _gateway.TypedPrompts.Read(deliveryId).Record!.State);
        _promptAnswer = null; // from here the REAL prompt core answers, as it will for the claim's press
        return deliveryId;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never held");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Send_anyway_on_a_shown_back_typed_prompt_claims_the_original_id_and_sends_once()
    {
        // Proves the Delivery Lead's ruling on the review's finding 2 across the real route: "Send anyway" on a
        // shown-back typed prompt CLAIMS the ORIGINAL delivery id through the same request field a recording's does,
        // the Gateway verifies it against the typed prompt store, and the prompt goes out under the ORIGINAL id and
        // the PRESS TIME. The Director's record holds that id as not-delivered, which may begin again, so the claim's
        // send types the words once, and the outcome route answers on the SAME id.
        const string words = "Reply with only the word OK. Marker, choral lemur 25";
        var deliveryId = await ShownBack(words);
        var before = _clock.GetUtcNow().UtcDateTime;

        var (status, body) = await PostClaimedPrompt(words, deliveryId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(deliveryId, body.GetProperty("deliveryId").GetString());
        PromptRequest[] arrived;
        lock (_arrived) arrived = _arrived.ToArray();
        Assert.Equal(2, arrived.Length); // the original send, and the claim's press - never a second copy
        Assert.All(arrived, p => Assert.Equal(deliveryId, p.DeliveryId)); // the claim carried the ORIGINAL id
        var claimed = arrived[1];
        Assert.Equal(words, claimed.Text);
        Assert.InRange(claimed.SentAtUtc!.Value, before.AddSeconds(-1), _clock.GetUtcNow().UtcDateTime.AddSeconds(1));
        var record = _gateway.TypedPrompts.Read(deliveryId).Record!;
        Assert.Equal(TypedPromptState.Delivered, record.State);
        Assert.Null(record.Text);
        var (outcomeStatus, outcome) = await Send(HttpMethod.Get, $"sessions/{_sid}/prompts/{deliveryId}/outcome");
        Assert.Equal(HttpStatusCode.OK, outcomeStatus);
        Assert.True(outcome.GetProperty("submitted").GetBoolean());
        Assert.Equal(1, _backend.TextsSent); // the claim's press typed the words exactly once
    }

    [Fact]
    public async Task Two_tabs_pressing_Send_anyway_at_the_same_moment_make_exactly_one_send()
    {
        // Proves the atomicity the ruling demands, across the real route: two claims of the same typed delivery id, as
        // two tabs would send them, at the same moment - exactly ONE prompt reaches the Director, the other claim is
        // refused without sending and answered with the record's current state.
        const string words = "only one of us may arrive";
        var deliveryId = await ShownBack(words);
        // The claim's press is slow on the Director, so the first tab's send is still in flight when the second tab's
        // claim arrives - the exact race two tabs run.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _promptAnswer = body =>
        {
            gate.Task.Wait();
            return SessionCommandExecutor.SendPromptAsync(_session, body, SendSource.UserInput, _directorRecord)
                .GetAwaiter().GetResult();
        };

        var first = PostClaimedPrompt(words, deliveryId);
        await WaitUntil(() => Arrived() == 2); // the claim's press reached the Director
        var (secondStatus, second) = await PostClaimedPrompt(words, deliveryId);

        // The second claim was refused while the record was held by the first: answered its current state, nothing sent.
        Assert.Equal(HttpStatusCode.Accepted, secondStatus);
        Assert.True(second.GetProperty("delivering").GetBoolean());
        Assert.Equal(deliveryId, second.GetProperty("deliveryId").GetString());
        gate.SetResult();
        var (firstStatus, firstBody) = await first;
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.True(firstBody.GetProperty("accepted").GetBoolean());
        Assert.Equal(2, Arrived()); // the original send and ONE claim press - the second tab sent nothing
        Assert.Equal(1, _backend.TextsSent);
    }

    [Fact]
    public async Task A_claim_of_another_sessions_typed_id_is_dropped_and_the_words_go_as_an_ordinary_prompt()
    {
        // Proves the claim is gated to THIS session, as the recording claim is: an id held for another session is
        // dropped, and the words go out as an ordinary prompt under a FRESH minted id - the claimed record untouched.
        var otherSid = Guid.NewGuid().ToString();
        var otherId = TypedPromptDelivery.MintDeliveryId();
        _gateway.TypedPrompts.Hold(otherId, otherSid, "another session's words", _clock.GetUtcNow().UtcDateTime,
            "no-answer", new TypedPromptDecisionFacts());
        _gateway.TypedPrompts.ResolveNotDelivered(otherId, TypedPromptDecisions.ReasonDirectorSaidNotDelivered, "not-delivered");

        var (status, body) = await PostClaimedPrompt("words pressed at this session", otherId);

        Assert.Equal(HttpStatusCode.OK, status);
        var freshId = body.GetProperty("deliveryId").GetString()!;
        Assert.NotEqual(otherId, freshId); // dropped: the Gateway minted a fresh id, exactly as a dropped recording claim
        PromptRequest[] arrived;
        lock (_arrived) arrived = _arrived.ToArray();
        var sent = Assert.Single(arrived);
        Assert.Equal(freshId, sent.DeliveryId);
        Assert.Equal(TypedPromptState.NotDelivered, _gateway.TypedPrompts.Read(otherId).Record!.State); // untouched
        var lines = _gateway.TypedPrompts.ReadDecisions(otherId).Select(l => l.Decision).ToArray();
        Assert.Contains("send-anyway-claim-dropped", lines);
    }

    /// <summary>The real clock moved ahead by <see cref="Ahead"/>: the route and the driver judge the limit by it.</summary>
    private sealed class AheadClock : TimeProvider
    {
        public TimeSpan Ahead;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Ahead;
    }
}
