using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Attribution;

/// <summary>
/// THE DELIVERY ID IS THE GATEWAY'S, PROVEN ACROSS THE REAL ROUTE (Voice Delivery mission, phase 1). The Director
/// refuses a second copy of a delivery id it has typed, so a client that could set the id freely could block words it
/// does not own, or pass them off as delivered. "Send anyway" may only CLAIM its recording; the prompt route turns the
/// claim into the delivery id only for an upload of the caller's own tenant for that session.
///
/// A hostile body is posted at the MAPPED ROUTE of a real <see cref="GatewayHost"/>, rides the REAL tunnel (a real
/// SignalR Director connection), and what arrives is read and run through the REAL prompt core - with a delivery
/// record of this test's own, so nothing touches the machine's record.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DeliveryIdIsGatewayAuthoritativeTests : IAsyncLifetime
{
    private const string Token = "test-token-delivery-id";
    private const string DirectorId = "dir-delivery-id";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-delid-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";
    private DeliveryRecord _directorRecord = null!;
    private VoiceUploadStore _uploads = null!;
    private readonly List<PromptRequest> _arrived = new();
    private readonly ExecuteActionTestBackend _backend = new();

    /// <summary>When set, the Director answers the prompt verb with this instead of running the real prompt core - how
    /// a send that runs out of time is played (phase 2). Null: the real core.</summary>
    private Func<DirectorCommand, DirectorCommandResult>? _promptAnswer;

    /// <summary>When set, the Director answers "what became of delivery id X?" with this. Null: the real verb over this
    /// test's own delivery record.</summary>
    private Func<DirectorCommand, DirectorCommandResult>? _deliveryStateAnswer;

    public DeliveryIdIsGatewayAuthoritativeTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-delid-" + Guid.NewGuid().ToString("N"));
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
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // The same staging root the Gateway's own dictation store uses (it resolves under CC_DIRECTOR_ROOT).
        _uploads = new VoiceUploadStore(CcStorage.DictationUploads(), TenantId.Local);
        _directorRecord = new DeliveryRecord(Path.Combine(_root, "director-delivery-records"));

        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, _backend);
        _sid = _session.Id.ToString();

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59918/",
            MachineName = "delid-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        // THE DIRECTOR SIDE IS THE REAL PROMPT CORE, over this test's own delivery record.
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb == DeliveryStateRequest.Verb)
            {
                var stateAnswer = _deliveryStateAnswer?.Invoke(cmd) ?? SessionReadExecutor.DeliveryStateOf(cmd, _directorRecord);
                stateAnswer.CommandId = cmd.CommandId;
                return stateAnswer;
            }
            if (cmd.Verb == "prompt" && _promptAnswer is { } played)
            {
                var playedAnswer = played(cmd);
                playedAnswer.CommandId = cmd.CommandId;
                return playedAnswer;
            }
            if (cmd.Verb != "prompt") return await SessionCommandExecutor.DispatchAsync(_sm, DirectorId, cmd);
            var body = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            lock (_arrived) _arrived.Add(body);
            var result = await SessionCommandExecutor.SendPromptAsync(_session, body, SendSource.UserInput, _directorRecord);
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

    private async Task<JsonElement> PostPrompt(object body)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt", body);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private PromptRequest LastArrived()
    {
        lock (_arrived) return _arrived[^1];
    }

    /// <summary>An upload record for <paramref name="sessionId"/> in <paramref name="store"/>'s tenant, as the
    /// dictation upload leg leaves it.</summary>
    private static string Upload(VoiceUploadStore store, string sessionId)
    {
        var id = Guid.NewGuid().ToString();
        store.Register(id);
        store.MarkPending(id, sessionId);
        return id;
    }

    [Fact]
    public async Task A_claim_for_this_accounts_recording_of_this_session_becomes_the_delivery_id()
    {
        // Proves "Send anyway" naming its own recording reaches the Director with that recording as the delivery id.
        var uploadId = Upload(_uploads, _sid);

        await PostPrompt(new { text = "typed before the spoken words", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), LastArrived().DeliveryId);
        Assert.Null(LastArrived().DeliveryIdClaim);
    }

    [Fact]
    public async Task A_claim_for_another_sessions_recording_is_dropped_and_the_text_still_goes()
    {
        // Proves a recording of a DIFFERENT session cannot be named: the claim is dropped, the words are still sent.
        var uploadId = Upload(_uploads, Guid.NewGuid().ToString());

        var answer = await PostPrompt(new { text = "hello", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.True(answer.GetProperty("accepted").GetBoolean());
        Assert.Null(LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_claim_for_another_tenants_recording_is_dropped()
    {
        // Proves a recording in ANOTHER account's partition - even for this very session id - cannot be named.
        var otherTenant = _uploads.ForTenant(new TenantId("22222222-2222-2222-2222-222222222222"));
        var uploadId = Upload(otherTenant, _sid);

        await PostPrompt(new { text = "hello", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Null(LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_delivery_id_set_directly_in_the_body_is_overwritten()
    {
        // Proves the body cannot set the delivery id itself: without a claim it arrives empty, and with a verified
        // claim it arrives as the Gateway's own verified id, never the body's.
        var uploadId = Upload(_uploads, _sid);

        await PostPrompt(new { text = "first", appendEnter = true, deliveryId = "made-up-id" });
        Assert.Null(LastArrived().DeliveryId);

        await PostPrompt(new { text = "second", appendEnter = true, deliveryId = "made-up-id", deliveryIdClaim = uploadId });
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_second_send_anyway_of_a_delivered_recording_is_refused_and_the_caller_is_told()
    {
        // Proves the Director's delivery state reaches the caller unchanged: the second copy is refused as delivered.
        var uploadId = Upload(_uploads, _sid);

        var first = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });
        var second = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.True(first.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", first.GetProperty("deliveryState").GetString());
        Assert.False(second.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", second.GetProperty("deliveryState").GetString());
    }

    /// <summary>
    /// The recording as the client leaves it before "Send anyway" can be pressed: resolved as moved-on - a terminal
    /// outcome - and then ACKNOWLEDGED through the real acknowledge route, which the client does for every terminal
    /// outcome before it shows the button (client-core <c>uploadDictationToSession</c>).
    /// </summary>
    private async Task<string> MovedOnAndAcknowledged()
    {
        var uploadId = Upload(_uploads, _sid);
        _uploads.MarkDelivered(uploadId, submitted: false, movedOn: true, transcript: "send me once", reason: DeliveryDecisions.TooOld);
        var ack = await _http.PostAsync($"dictation/{uploadId}/ack", content: null);
        var ackText = await ack.Content.ReadAsStringAsync();
        Assert.True(ack.StatusCode == HttpStatusCode.OK, $"the acknowledgement answered {(int)ack.StatusCode}: {ackText}");
        Assert.True(JsonDocument.Parse(ackText).RootElement.GetProperty("retired").GetBoolean(), $"the acknowledgement retired nothing: {ackText}");
        return uploadId;
    }

    [Fact]
    public async Task Send_anyway_of_an_acknowledged_recording_that_already_landed_is_refused_and_types_nothing()
    {
        // Proves the mission's 09:05 case through the caller that exists (review finding 1): the first copy LANDED -
        // the Director's record says delivered - but the Gateway gave up waiting and judged the recording moved on, the
        // client acknowledged that outcome and showed the words back, and the owner pressed "Send anyway". The claim
        // must still become the delivery id, so the Director refuses the second copy, types nothing, and says so.
        var uploadId = await MovedOnAndAcknowledged();
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, canonical).Began);
        _directorRecord.MarkDelivered(_session.Id, canonical);
        var typedBefore = _backend.TextsSent;

        var answer = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(canonical, LastArrived().DeliveryId);
        Assert.False(answer.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", answer.GetProperty("deliveryState").GetString());
        Assert.Equal(typedBefore, _backend.TextsSent);
    }

    [Fact]
    public async Task Send_anyway_of_an_acknowledged_recording_leaves_the_claim_and_the_refusal_in_its_decision_record()
    {
        // Proves review finding 2: after the recording's own "acknowledged" line, its decision record (read through the
        // real read route) says the claim was believed and the Director refused the second copy as delivered - so "why
        // did my words go in once?" is answered without the container's log.
        var uploadId = await MovedOnAndAcknowledged();
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, canonical).Began);
        _directorRecord.MarkDelivered(_session.Id, canonical);

        await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        var read = await _http.GetFromJsonAsync<JsonElement>($"dictation/{uploadId}/decisions");
        var lines = read.GetProperty("decisions").EnumerateArray().ToList();
        var names = lines.Select(l => l.GetProperty("decision").GetString()).ToList();
        var acknowledged = names.IndexOf(DeliveryDecisions.Acknowledged);
        Assert.True(acknowledged >= 0, $"no acknowledged line: {string.Join(", ", names)}");
        Assert.Equal(new[] { DeliveryDecisions.ClaimVerified, DeliveryDecisions.ClaimDirectorAnswer }, names.Skip(acknowledged + 1));
        var answer = lines[^1].GetProperty("facts");
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.True(answer.GetProperty("refusedDuplicate").GetBoolean());
        Assert.Equal("delivered", answer.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Send_anyway_of_an_acknowledged_recording_the_Director_never_received_is_typed()
    {
        // Proves the other half: when the words really were dropped - the Director never saw this delivery id - a
        // "Send anyway" of the acknowledged recording is typed, once, under its delivery id. This is also the control
        // for the test above: the same backend counter moves when something IS typed.
        var uploadId = await MovedOnAndAcknowledged();
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        var typedBefore = _backend.TextsSent;

        var answer = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(canonical, LastArrived().DeliveryId);
        Assert.True(answer.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", answer.GetProperty("deliveryState").GetString());
        Assert.True(_backend.TextsSent > typedBefore, "nothing was typed for a recording the Director never received");
        Assert.Equal(DeliveryState.Delivered, _directorRecord.Read(_session.Id, canonical).State);
    }

    [Fact]
    public async Task A_verified_claim_whose_session_cannot_be_found_writes_where_it_stopped()
    {
        // Proves the phase 1 review's first note is closed: a verified "Send anyway" whose prompt stops at the session
        // lookup, before the Director, no longer leaves the recording's decision record ending at the claim. The line
        // after the verified claim says it stopped before the Director and why, and nothing reached the Director.
        var elsewhere = Guid.NewGuid().ToString();
        var uploadId = Upload(_uploads, elsewhere);
        int arrivedBefore;
        lock (_arrived) arrivedBefore = _arrived.Count;

        var resp = await _http.PostAsJsonAsync($"sessions/{elsewhere}/prompt",
            new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        lock (_arrived) Assert.Equal(arrivedBefore, _arrived.Count);
        var read = await _http.GetFromJsonAsync<JsonElement>($"dictation/{uploadId}/decisions");
        var lines = read.GetProperty("decisions").EnumerateArray().ToList();
        var names = lines.Select(l => l.GetProperty("decision").GetString()).ToList();
        Assert.Equal(new[] { DeliveryDecisions.ClaimVerified, DeliveryDecisions.ClaimStoppedBeforeDirector }, names.TakeLast(2));
        var stopped = lines[^1].GetProperty("facts");
        Assert.Equal(GatewayEndpoints.ClaimStopSessionNotFound, stopped.GetProperty("reason").GetString());
        Assert.Equal(elsewhere, stopped.GetProperty("sessionId").GetString());
    }

    // ===== phase 2: a "Send anyway" that runs out of time asks, through the same piece as dictation =============

    private static DirectorCommandResult RanOutOfTime(DirectorCommand _)
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostPromptRaw(object body)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt", body);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<List<string?>> DecisionsAfterTheClaim(string uploadId)
    {
        var read = await _http.GetFromJsonAsync<JsonElement>($"dictation/{uploadId}/decisions");
        var names = read.GetProperty("decisions").EnumerateArray().Select(l => l.GetProperty("decision").GetString()).ToList();
        return names.Skip(names.LastIndexOf(DeliveryDecisions.ClaimVerified)).ToList();
    }

    [Fact]
    public async Task Send_anyway_that_runs_out_of_time_asks_and_answers_200_when_the_Director_says_delivered()
    {
        // Proves the "Send anyway" prompt route no longer calls a slow send a failure: the prompt verb runs out of time,
        // the Gateway asks the Director through the same piece dictation uses, and the answer "delivered" is today's
        // 200 prompt answer. The log reads claim, answer (unanswered), question, answer.
        var uploadId = Upload(_uploads, _sid);
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, canonical).Began);
        _directorRecord.MarkDelivered(_session.Id, canonical);
        _promptAnswer = RanOutOfTime;

        var (status, body) = await PostPromptRaw(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", body.GetProperty("deliveryState").GetString());
        Assert.Equal(new[]
        {
            DeliveryDecisions.ClaimVerified, DeliveryDecisions.ClaimDirectorAnswer, DeliveryDecisions.AskedDirector,
            DeliveryDecisions.DeliveryStateAnswer,
        }, await DecisionsAfterTheClaim(uploadId));
    }

    [Fact]
    public async Task Send_anyway_that_runs_out_of_time_answers_202_while_the_Director_is_still_delivering()
    {
        // Proves the held answer on the prompt route: the Director says it is still typing this delivery id, so the
        // caller is told 202 "still delivering" - not a failure, and not an offer to send it yet again.
        var uploadId = Upload(_uploads, _sid);
        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, VoiceUploadStore.NormalizeUploadId(uploadId)!).Began);
        _promptAnswer = RanOutOfTime;

        var (status, body) = await PostPromptRaw(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.True(body.GetProperty("delivering").GetBoolean());
        Assert.Equal("delivering", body.GetProperty("directorState").GetString());
        Assert.Equal(DeliveryDecisions.StillDelivering, (await DecisionsAfterTheClaim(uploadId)).Last());
    }

    [Fact]
    public async Task Send_anyway_that_runs_out_of_time_answers_202_no_answer_when_the_question_gets_none()
    {
        // Proves a question that gets no answer holds the words too: 202 with directorState "no-answer".
        var uploadId = Upload(_uploads, _sid);
        _promptAnswer = RanOutOfTime;
        _deliveryStateAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer to the question");

        var (status, body) = await PostPromptRaw(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("no-answer", body.GetProperty("directorState").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_anyway_that_runs_out_of_time_answers_502_when_the_words_are_not_in(bool markedNotDelivered)
    {
        // Proves the 502 on the prompt route: the question says the words are not in - the Director marked the id not
        // delivered (the end of its fifteen-minute watch, contract section 5), or never saw it - so the caller is told
        // it failed and can show the words back again.
        var uploadId = Upload(_uploads, _sid);
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        if (markedNotDelivered)
        {
            Assert.True(_directorRecord.TryBeginDelivery(_session.Id, canonical).Began);
            _directorRecord.MarkNotDelivered(_session.Id, canonical, "never appeared in the agent's records within 15 minutes");
        }
        _promptAnswer = RanOutOfTime;

        var (status, body) = await PostPromptRaw(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        var answer = (await DecisionsAfterTheClaim(uploadId)).Last();
        Assert.Equal(DeliveryDecisions.DeliveryStateAnswer, answer);
    }

    [Fact]
    public async Task Send_anyway_of_a_recording_the_Director_marked_not_delivered_is_typed_without_asking_first()
    {
        // Proves the Tech Lead's correction to contract section 5: a "Send anyway" re-press does not ask first. It is a
        // plain send with the claim, the Director accepts a not-delivered id again, and the words are typed once - 200.
        var uploadId = Upload(_uploads, _sid);
        var canonical = VoiceUploadStore.NormalizeUploadId(uploadId)!;
        Assert.True(_directorRecord.TryBeginDelivery(_session.Id, canonical).Began);
        _directorRecord.MarkNotDelivered(_session.Id, canonical, "never appeared in the agent's records within 15 minutes");
        var typedBefore = _backend.TextsSent;

        var answer = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.True(answer.GetProperty("accepted").GetBoolean());
        Assert.True(_backend.TextsSent > typedBefore, "nothing was typed for a recording the Director marked not delivered");
        Assert.DoesNotContain(DeliveryDecisions.AskedDirector, await DecisionsAfterTheClaim(uploadId));
    }
}
