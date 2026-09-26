using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Transcription;
using CcDirector.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Attribution;

/// <summary>
/// THE TWO ATTRIBUTION MARKERS ARE THE GATEWAY'S, PROVEN ACROSS THE REAL ROUTE (independent inspection of
/// phase two, "Clean up Your Throttle", 2026-09-05 - findings I2-01, I2-02 and I2-03).
///
/// Every earlier test of these markers constructed a <see cref="PromptRequest"/> by hand and handed it to
/// the Director's executor: it proved the consumer obeys when told, and nothing about who is allowed to
/// tell it. The inspection showed each defect below would have stayed green through those tests. So this
/// file does what they did not: it deserializes a HOSTILE BODY at the MAPPED ROUTE of a real
/// <see cref="GatewayHost"/>, follows the request over the REAL tunnel (a real SignalR Director connection),
/// runs the REAL <see cref="SessionCommandExecutor"/> on what arrives, and reads what the REAL
/// <see cref="Session"/> recorded - the tally bucket and the submission-ledger event. A body's lie has to
/// survive all of that to count, and it does not.
///
/// The Director is registered with an unreachable control endpoint, so anything that arrives can only have
/// ridden the tunnel (the same construction as <see cref="TunnelMechanismProofTests"/>).
/// </summary>
[Collection("DirectorRoot")]
public sealed class PromptAttributionIsGatewayAuthoritativeTests : IAsyncLifetime
{
    private const string Token = "test-token-attribution";
    private const string DirectorId = "dir-attribution";
    private const string Transcript = "deploy the gateway and tell me when it is up";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly TranscriptionMode _prevMode;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-attr-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";

    /// <summary>What reached the Director: every prompt verb's deserialized body, in order.</summary>
    private readonly List<PromptRequest> _arrived = new();

    /// <summary>When set, the Director answers a prompt with this instead of running it - to stand in for an answer the
    /// real session cannot be put into here (an owner's unsent draft needs a real terminal, which an embedded one is not).</summary>
    private Func<PromptRequest, PromptResponse>? _promptAnswer;

    /// <summary>What the session's submission ledger recorded, in order.</summary>
    private readonly List<(SendSource? Source, InputOrigin? Origin, SubmissionEvidence Evidence)> _ledger = new();

    public PromptAttributionIsGatewayAuthoritativeTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-attr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _prevMode = TranscriptionModeConfig.Get();
    }

    /// <summary>A transcription provider that answers every request with one fixed transcript, so the real
    /// utterance routes can run end to end with no network and no key of value.</summary>
    private sealed class FixedTranscriptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { text = Transcript }), Encoding.UTF8, "application/json"),
            });
    }

    public async Task InitializeAsync()
    {
        // The real transcription service, with its provider faked: the utterance completion route is the
        // thing under test, not the provider behind it.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        var vaultPath = Path.Combine(_root, "keyvault.json");
        new KeyVault(vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test");
        var transcription = new GatewayTranscriptionService(
            new KeyVault(vaultPath),
            http: new HttpClient(new FixedTranscriptHandler()),
            audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive")));

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            dictationTranscription: transcription);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A REAL session on a REAL session manager, with a test backend (no process).
        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        _sid = _session.Id.ToString();
        _session.OnTurnSubmitted += (source, origin, evidence) => { lock (_ledger) _ledger.Add((source, origin, evidence)); };

        // The Director registers UNREACHABLE, so a delivered prompt can only have ridden the tunnel.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/",
            MachineName = "attr-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        // THE DIRECTOR SIDE IS THE REAL EXECUTOR: what the Gateway forwards is deserialized and applied to
        // the real session exactly as a running Director would apply it.
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb == "prompt" && cmd.PayloadJson is { } json)
            {
                var body = JsonSerializer.Deserialize<PromptRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (body is not null) lock (_arrived) _arrived.Add(body);
                if (body is not null && _promptAnswer is { } answer)
                    return DirectorCommandResult.Success(JsonSerializer.Serialize(answer(body), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            return await SessionCommandExecutor.DispatchAsync(_sm, DirectorId, cmd);
        });
        await _conn.StartAsync();
        // This Director is the real executor, which checks a session is waiting before it types - and says so, as a
        // running Director does: a session types into a session it owns only through such a Director.
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test", ChecksIdleBeforeTyping = true });
        await _conn.InvokeAsync("PushSnapshot", 1L, new[] { new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" } });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        TranscriptionModeConfig.Set(_prevMode);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostPrompt(object body, string? bearer = null, string? sid = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid ?? _sid}/prompt") { Content = JsonContent.Create(body) };
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(req);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "the condition did not become true within " + within);
    }

    private static async Task AssertOk(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)resp.StatusCode}: {body}");
    }

    private PromptRequest LastArrived()
    {
        lock (_arrived) return Assert.Single(_arrived.TakeLast(1));
    }

    private InputStatBucketDto Bucket(string modality, string surface) =>
        _session.InputStats.Snapshot().Buckets.FirstOrDefault(b => b.Modality == modality && b.Surface == surface)
        ?? new InputStatBucketDto { Modality = modality, Surface = surface };

    /// <summary>Run the REAL utterance transcription round trip - register, one chunk, complete - and return
    /// the id the client would hand back as its spoken claim.</summary>
    private async Task<string> TranscribeAnUtterance()
    {
        var reg = await _http.PostAsync("wingman/utterance/upload", content: null);
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
        var id = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("upload_id").GetString()!;
        var chunk = await _http.PutAsync($"wingman/utterance/{id}/chunk/0", new ByteArrayContent(Enumerable.Repeat((byte)0x42, 2048).ToArray()));
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        var complete = await _http.PostAsJsonAsync($"wingman/utterance/{id}/complete", new { totalChunks = 1, mime = "audio/wav", ext = "wav" });
        await AssertOk(complete);
        var transcript = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transcript").GetString();
        Assert.Equal(Transcript, transcript);
        return id;
    }

    // ---- I2-02: the body cannot relabel the caller's own turn as another agent's ---------------------

    [Fact]
    public async Task A_hostile_operator_body_cannot_make_its_own_typed_prompt_agent_driven_or_spoken()
    {
        var resp = await PostPrompt(new { text = "hello", appendEnter = true, agentDriven = true, deliveryUploadId = "made-up-id" });
        await AssertOk(resp);

        // What the Director received: the Gateway's ruling, not the body's.
        var arrived = LastArrived();
        Assert.False(arrived.AgentDriven);
        Assert.Null(arrived.DeliveryUploadId);
        Assert.Equal("unknown", arrived.Surface);

        // What the real session recorded: one typed turn of the person's own, nothing on the agent lane.
        var snap = _session.InputStats.Snapshot();
        Assert.Equal(0, snap.AgentDrivenTurns);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        Assert.DoesNotContain(snap.Buckets, b => b.Modality == "voice");
        var entry = Assert.Single(_ledger);
        Assert.Equal(SendSource.UserInput, entry.Source);
        Assert.Equal(InputModality.Typed, entry.Origin!.Value.Modality);
        // WHAT THE DOOR KNEW (source logging): the prompt route, the shared machine token behind the call, no
        // transcript, and the digest and length of exactly the text it delivered - recorded, not inferred.
        Assert.Equal(SubmissionRoutes.GatewayPrompt, entry.Evidence.Provenance.Route);
        Assert.Equal(SubmissionIdentityKinds.MachineToken, entry.Evidence.Provenance.IdentityKind);
        Assert.Null(entry.Evidence.Provenance.TranscriptId);
        Assert.Empty(entry.Evidence.Provenance.SpokenSpans);
        Assert.Equal(SubmissionEvidence.Sha256Of("hello"), entry.Evidence.ContentSha256);
        Assert.Equal(5, entry.Evidence.ContentLength);
    }

    [Fact]
    public async Task A_session_key_caller_cannot_prompt_a_session_it_does_not_own_whatever_its_body_says()
    {
        // CHANGED BY THE MESSAGE LOAD MISSION (16 September 2026, ruling 17), and again by Parent Control, fix 1
        // (26 September 2026). A session key may type only into a session it owns; this one names a session that no
        // session owns, so it is refused by the route before anything reaches the Director - which is the strongest
        // form of the attribution guarantee: no agent turn can be counted as the person's, because none arrives.
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, Guid.NewGuid().ToString(),
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));

        var resp = await PostPrompt(new { text = "do the next task", appendEnter = true, agentDriven = false }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("An agent may type only into a session it owns", await resp.Content.ReadAsStringAsync());
        lock (_arrived) Assert.Empty(_arrived);
        Assert.Empty(_ledger);
        Assert.Equal(0, _session.InputStats.Snapshot().AgentDrivenTurns);
    }

    /// <summary>A key for a new session that OWNS this test's session, as <c>--controlled-by self</c> makes it.</summary>
    private async Task<string> KeyOfTheOwningSession()
    {
        var parent = Guid.NewGuid().ToString();
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, parent,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        await _conn.InvokeAsync("PushSnapshot", 2L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput", IsControlled = true, ControllerSessionId = parent },
        });
        return key;
    }

    [Fact]
    public async Task A_session_key_prompting_a_session_it_owns_is_sent_as_the_guarded_agent_send_whatever_its_body_says()
    {
        // Parent Control, fix 1. What reaches the Director is the Gateway's ruling, not the body's: an agent's turn,
        // submitted, and typed only when the session is waiting for a prompt with nothing of the owner's in its composer.
        var key = await KeyOfTheOwningSession();
        _session.ApplyTerminalActivityState(ActivityState.Working);
        _session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var resp = await PostPrompt(new { text = "carry on", appendEnter = true, agentDriven = false, onlyWhenWaitingForInput = false },
            bearer: key);

        var arrived = LastArrived();
        Assert.True(arrived.OnlyWhenWaitingForInput);
        Assert.True(arrived.AgentDriven);
        Assert.True(arrived.AppendEnter);
        Assert.Equal("carry on", arrived.Text);
        // The REAL executor ran the check. This test's session is an embedded one, whose terminal submits a whole turn
        // in one call, so the guarded send is refused before anything is typed - and the route says so, 409, in words.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Nothing was typed", body);
        Assert.Contains("one call", body);
        Assert.Empty(_ledger);
        Assert.Equal(0, _session.InputStats.Snapshot().AgentDrivenTurns);
    }

    [Fact]
    public async Task A_session_key_prompting_a_session_it_owns_whose_owner_has_unsent_words_types_nothing_and_says_why()
    {
        var key = await KeyOfTheOwningSession();
        _promptAnswer = _ => new PromptResponse
        {
            Accepted = false, RefusedBusy = true, IdleChecked = true, RefusedFor = PromptResponse.RefusedForOwnerDraft,
            ActivityState = "WaitingForInput", Error = "the owner has unsent text in the composer; nothing was typed",
        };

        var resp = await PostPrompt(new { text = "carry on" }, bearer: key);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var answer = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.NotNull(answer);
        Assert.False(answer!.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, answer.RefusedFor);
        Assert.Contains("never typed over", answer.Error);
        Assert.Contains("cc-devthrottle message send", answer.Error);
    }

    [Fact]
    public async Task A_session_key_prompting_a_session_it_owns_is_not_told_delivered_without_the_directors_check()
    {
        // A Director that answers accepted but does not say it made the check is not believed: the same reading the Fleet
        // Manager's events give that answer. The text may have been typed, so the answer never says it was not.
        var key = await KeyOfTheOwningSession();
        _promptAnswer = _ => new PromptResponse { Accepted = true, IdleChecked = false, ActivityState = "Working" };

        var resp = await PostPrompt(new { text = "carry on" }, bearer: key);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var answer = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.False(answer!.Accepted);
        Assert.Contains("not counted as delivered", answer.Error);
        Assert.DoesNotContain("Nothing was typed", answer.Error);
    }

    [Fact]
    public async Task A_session_key_prompting_a_session_it_owns_that_takes_it_gets_the_directors_answer()
    {
        var key = await KeyOfTheOwningSession();
        _promptAnswer = _ => new PromptResponse { Accepted = true, IdleChecked = true, ActivityState = "Working" };

        var resp = await PostPrompt(new { text = "carry on" }, bearer: key);

        await AssertOk(resp);
        var answer = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.True(answer!.Accepted);
        Assert.True(answer.IdleChecked);
    }

    [Fact]
    public async Task The_words_the_gateway_transcribed_sent_under_their_own_id_are_one_voice_turn()
    {
        var id = await TranscribeAnUtterance();

        var resp = await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id });
        await AssertOk(resp);

        Assert.Equal(id, LastArrived().DeliveryUploadId);
        Assert.Equal(1, Bucket("voice", "unknown").Turns);
        var entry = Assert.Single(_ledger);
        Assert.Equal(SendSource.Delivery, entry.Source);
        Assert.Equal(InputModality.Voice, entry.Origin!.Value.Modality);
        // The reserved claim IS the transcript: its id and its characters - the whole text - are on the row.
        Assert.Equal(id, entry.Evidence.Provenance.TranscriptId);
        Assert.Equal(new SpokenTurnRule.SpokenSpan(0, Transcript.Length), Assert.Single(entry.Evidence.Provenance.SpokenSpans));
        Assert.Equal(SubmissionEvidence.Sha256Of(Transcript), entry.Evidence.ContentSha256);
    }

    [Fact]
    public async Task A_replayed_id_is_typed_the_second_time()
    {
        var id = await TranscribeAnUtterance();
        await AssertOk(await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id }));
        await AssertOk(await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id }));

        lock (_arrived)
        {
            Assert.Equal(2, _arrived.Count);
            Assert.Equal(id, _arrived[0].DeliveryUploadId);
            Assert.Null(_arrived[1].DeliveryUploadId);
        }
        Assert.Equal(1, Bucket("voice", "unknown").Turns);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        Assert.Equal(2, _ledger.Count);
        Assert.Equal(SendSource.Delivery, _ledger[0].Source);
        Assert.Equal(SendSource.UserInput, _ledger[1].Source);
        // The replay carried no transcript at the door: no id, no spoken characters, the same words digested.
        Assert.Equal(id, _ledger[0].Evidence.Provenance.TranscriptId);
        Assert.Null(_ledger[1].Evidence.Provenance.TranscriptId);
        Assert.Empty(_ledger[1].Evidence.Provenance.SpokenSpans);
        Assert.Equal(_ledger[0].Evidence.ContentSha256, _ledger[1].Evidence.ContentSha256);
    }

    // ---- final inspection finding F-07: a claim is spent by a DELIVERED turn, not by an attempt -------

    [Fact]
    public async Task A_spoken_claim_whose_prompt_never_entered_a_session_is_still_spoken_on_the_retry()
    {
        // The words are transcribed once. The first send goes to a session id nobody has, so no turn enters
        // any session - the claim used to be spent right there. The retry, the same words under the same id,
        // is the person's one real spoken turn and must be filed as spoken.
        var id = await TranscribeAnUtterance();

        var failed = await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id }, sid: Guid.NewGuid().ToString());
        Assert.NotEqual(HttpStatusCode.OK, failed.StatusCode);
        lock (_arrived) Assert.Empty(_arrived);
        Assert.Empty(_ledger);

        var retry = await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id });
        await AssertOk(retry);

        Assert.Equal(id, LastArrived().DeliveryUploadId);
        Assert.Equal(1, Bucket("voice", "unknown").Turns);
        Assert.Equal(0, Bucket("typed", "unknown").Turns);
        var entry = Assert.Single(_ledger);
        Assert.Equal(SendSource.Delivery, entry.Source);
        Assert.Equal(InputModality.Voice, entry.Origin!.Value.Modality);

        // And having been delivered once, the claim is spent: a replay after the retry is typed.
        await AssertOk(await PostPrompt(new { text = Transcript, appendEnter = true, deliveryUploadId = id }));
        Assert.Null(LastArrived().DeliveryUploadId);
        Assert.Equal(1, Bucket("voice", "unknown").Turns);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
    }

    [Fact]
    public async Task A_real_id_on_different_words_is_typed()
    {
        var id = await TranscribeAnUtterance();
        var resp = await PostPrompt(new { text = Transcript + " and then restart it", appendEnter = true, deliveryUploadId = id });
        await AssertOk(resp);

        Assert.Null(LastArrived().DeliveryUploadId);
        Assert.Equal(0, Bucket("voice", "unknown").Turns);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        Assert.Equal(SendSource.UserInput, Assert.Single(_ledger).Source);
    }

    [Fact]
    public async Task A_made_up_id_is_typed_and_the_words_still_arrive()
    {
        var resp = await PostPrompt(new { text = "anything at all", appendEnter = true, deliveryUploadId = Guid.NewGuid().ToString("N") });
        await AssertOk(resp);
        var arrived = LastArrived();
        Assert.Null(arrived.DeliveryUploadId);
        Assert.Equal("anything at all", arrived.Text);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        Assert.Equal(0, Bucket("voice", "unknown").Turns);
    }

    // ---- I2-01: the durable recording-stage path labels a typed mixture as typed ---------------------

    private async Task<string> DeliverDurableDictation(string? before, string? prefix, string? after)
    {
        using var reg = new HttpRequestMessage(HttpMethod.Post, "dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = _sid }),
        };
        reg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var regResp = await _http.SendAsync(reg);
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);
        var id = (await regResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("upload_id").GetString()!;

        var chunk = await _http.PutAsync($"dictation/{id}/chunk/0", new ByteArrayContent(Enumerable.Repeat((byte)0x42, 2048).ToArray()));
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);

        var complete = await _http.PostAsJsonAsync($"dictation/{id}/complete",
            new { sessionId = _sid, totalChunks = 1, mime = "audio/wav", ext = "wav", before, prefix, after, sentAtUtc = DateTime.UtcNow, resumed = false });
        await AssertOk(complete);
        return id;
    }

    [Fact]
    public async Task A_recording_stage_send_with_typed_text_around_the_transcript_is_one_typed_turn()
    {
        // The Cockpit's and the phone's RECORDING-stage Send: the caret split the dictation around typed
        // text A and B, and an earlier paused segment had already turned to text.
        await DeliverDurableDictation(before: "A", prefix: "an earlier segment", after: "B");

        var arrived = LastArrived();
        Assert.Equal("A an earlier segment " + Transcript + " B", arrived.Text);
        Assert.Null(arrived.DeliveryUploadId);
        Assert.Equal("unknown", arrived.Surface);

        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        Assert.Equal(0, Bucket("voice", "unknown").Turns);
        var entry = Assert.Single(_ledger);
        Assert.Equal(SendSource.UserInput, entry.Source);
        Assert.Equal(InputModality.Typed, entry.Origin!.Value.Modality);
    }

    /// <summary>
    /// THE SAME MIXTURES THE DESKTOP IS FED (ruling R20). SpokenTurnRule.Examples is one table; the desktop's
    /// BackgroundDictationSendTests feed every row through the real background Send and read the origin it
    /// stamps, and this feeds every row through the REAL durable dictation route and reads the ledger. An
    /// identical mixture cannot classify differently on the two surfaces without one of the tests going red.
    /// </summary>
    [Fact]
    public async Task Every_example_mixture_is_classified_on_the_phone_exactly_as_the_shared_rule_says()
    {
        Assert.True(SpokenTurnRule.Examples.Count >= 6, "the shared table is too short to be a contract");
        var expectedVoice = 0;
        var expectedTyped = 0;
        foreach (var example in SpokenTurnRule.Examples)
        {
            // The fixed provider transcribes every clip to Transcript, so the example's own transcript is
            // what the route composes around; the typed halves and the earlier segment are the example's.
            var countBefore = _ledger.Count;
            var id = await DeliverDurableDictation(before: example.Before, prefix: example.Prefix, after: example.After);
            var arrived = LastArrived();
            var entry = _ledger[countBefore];
            Assert.Equal(_ledger.Count, countBefore + 1);
            var expected = example.Expected == InputModality.Voice ? SendSource.Delivery : SendSource.UserInput;
            Assert.True(expected == entry.Source && example.Expected == entry.Origin!.Value.Modality,
                $"'{example.Name}': the phone route recorded {entry.Origin!.Value.Modality} ({entry.Source}), the shared rule says {example.Expected}");
            if (example.Expected == InputModality.Voice) { Assert.Equal(id, arrived.DeliveryUploadId); expectedVoice++; }
            else { Assert.Null(arrived.DeliveryUploadId); expectedTyped++; }
            // WHAT THE DOOR KNEW (source logging): the dictation route, the transcript's id whether or not the turn
            // is spoken, and the spoken characters - the earlier segment and this transcript - exactly where they
            // stand in the delivered text, with the typed halves outside them.
            var p = entry.Evidence.Provenance;
            Assert.Equal(SubmissionRoutes.GatewayDictation, p.Route);
            Assert.Equal(SubmissionIdentityKinds.MachineToken, p.IdentityKind);
            Assert.Equal(id, p.TranscriptId);
            var spoken = p.SpokenSpans.Select(s => arrived.Text.Substring(s.Start, s.Length)).ToArray();
            var expectedSpoken = new[] { example.Prefix.Trim(), Transcript }.Where(x => x.Length > 0).ToArray();
            Assert.Equal(expectedSpoken, spoken);
            Assert.Equal(SubmissionEvidence.Sha256Of(arrived.Text), entry.Evidence.ContentSha256);
            Assert.Equal(arrived.Text.Length, entry.Evidence.ContentLength);
        }
        Assert.Equal(expectedVoice, Bucket("voice", "unknown").Turns);
        Assert.Equal(expectedTyped, Bucket("typed", "unknown").Turns);
    }

    [Fact]
    public async Task A_recording_stage_send_of_the_transcript_alone_is_one_voice_turn()
    {
        var id = await DeliverDurableDictation(before: null, prefix: null, after: "  ");

        var arrived = LastArrived();
        Assert.Equal(Transcript, arrived.Text);
        Assert.Equal(id, arrived.DeliveryUploadId);

        Assert.Equal(1, Bucket("voice", "unknown").Turns);
        Assert.Equal(0, Bucket("typed", "unknown").Turns);
        var entry = Assert.Single(_ledger);
        Assert.Equal(SendSource.Delivery, entry.Source);
        Assert.Equal(InputModality.Voice, entry.Origin!.Value.Modality);
    }

    // ---- source logging: a browser's per-character claims, verified before they are believed --------------

    /// <summary>
    /// The browser composer's case (owner's ruling, 2026-09-05): a person dictates INTO a typed sentence and
    /// sends it. The turn is typed - a mixture is not speech (ruling R20) - and it must still say WHICH
    /// characters came from the microphone. The composer claims the range; the Gateway verifies those
    /// characters ARE the transcript it registered, and only then records them.
    /// </summary>
    [Fact]
    public async Task A_verified_span_claim_records_which_characters_were_spoken_although_the_turn_is_typed()
    {
        var id = await TranscribeAnUtterance();
        var text = "please " + Transcript + " now";

        var resp = await PostPrompt(new
        {
            text,
            appendEnter = true,
            spokenSpans = new[] { new { start = 7, length = Transcript.Length, transcriptId = id } },
        });
        await AssertOk(resp);

        // The turn is TYPED: no whole-text claim was made, and a mixture is not spoken.
        var entry = Assert.Single(_ledger);
        Assert.Equal(InputModality.Typed, entry.Origin!.Value.Modality);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
        // And the ledger row says exactly which characters were spoken, and which transcript they came from.
        var p = entry.Evidence.Provenance;
        Assert.Equal(id, p.TranscriptId);
        var span = Assert.Single(p.SpokenSpans);
        Assert.Equal(Transcript, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public async Task A_span_claim_over_characters_that_are_not_the_transcript_is_refused_and_the_words_still_arrive()
    {
        var id = await TranscribeAnUtterance();
        // The id is real; the characters it names are the person's own typing. Nothing about them was spoken.
        var text = "I typed every word of this myself";

        var resp = await PostPrompt(new
        {
            text,
            appendEnter = true,
            spokenSpans = new[] { new { start = 0, length = 7, transcriptId = id } },
        });
        await AssertOk(resp);

        Assert.Equal(text, LastArrived().Text);
        var entry = Assert.Single(_ledger);
        Assert.Empty(entry.Evidence.Provenance.SpokenSpans);
        Assert.Null(entry.Evidence.Provenance.TranscriptId);
        Assert.Equal(1, Bucket("typed", "unknown").Turns);
    }

    [Fact]
    public async Task A_span_claim_naming_an_invented_transcript_or_lying_outside_the_text_is_refused()
    {
        var resp = await PostPrompt(new
        {
            text = Transcript,
            appendEnter = true,
            spokenSpans = new[]
            {
                new { start = 0, length = Transcript.Length, transcriptId = Guid.NewGuid().ToString("N") },
                new { start = 0, length = 9_000, transcriptId = "anything" },
                new { start = -4, length = 5, transcriptId = "anything" },
            },
        });
        await AssertOk(resp);

        var entry = Assert.Single(_ledger);
        Assert.Empty(entry.Evidence.Provenance.SpokenSpans);
        Assert.Null(entry.Evidence.Provenance.TranscriptId);
    }

    [Fact]
    public async Task A_session_credential_cannot_claim_that_any_of_its_characters_were_spoken()
    {
        // A session did not speak. Since the Message Load mission (ruling 17) a session key cannot reach the
        // prompt route at all, so the claim - even naming a real, unspent transcript - records nothing.
        var id = await TranscribeAnUtterance();
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, Guid.NewGuid().ToString(),
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));

        var resp = await PostPrompt(new
        {
            text = Transcript,
            appendEnter = true,
            spokenSpans = new[] { new { start = 0, length = Transcript.Length, transcriptId = id } },
        }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        lock (_arrived) Assert.Empty(_arrived);
        Assert.Empty(_ledger);
    }

    [Fact]
    public async Task A_client_cannot_set_the_route_or_the_credential_kind_by_sending_its_own_provenance()
    {
        // The body carries a whole provenance block claiming to be the desktop's local user. The route owns
        // both fields and overwrites them from what IT verified.
        var resp = await PostPrompt(new
        {
            text = "hello",
            appendEnter = true,
            provenance = new
            {
                route = SubmissionRoutes.DesktopComposer,
                identityKind = SubmissionIdentityKinds.LocalUser,
                transcriptId = "invented",
                spokenSpans = new[] { new { start = 0, length = 5 } },
            },
        });
        await AssertOk(resp);

        var p = Assert.Single(_ledger).Evidence.Provenance;
        Assert.Equal(SubmissionRoutes.GatewayPrompt, p.Route);
        Assert.Equal(SubmissionIdentityKinds.MachineToken, p.IdentityKind);
        Assert.Null(p.TranscriptId);
        Assert.Empty(p.SpokenSpans);
    }

    /// <summary>
    /// The browser terminal relay (source logging): the Gateway stamps the credential kind of the person
    /// typing when their socket opens, and every keystroke frame carries it to the Director. Driven through
    /// the REAL websocket route, with the real tunnel legs and the real Director executor.
    /// </summary>
    [Fact]
    public async Task The_browser_terminal_relay_carries_the_credential_kind_of_the_person_typing()
    {
        var arrived = new TaskCompletionSource<TerminalInputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _conn.Remove("Command");
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb == "terminal-input" && cmd.PayloadJson is { } json)
            {
                var body = JsonSerializer.Deserialize<TerminalInputRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (body is not null) arrived.TrySetResult(body);
            }
            if (cmd.Verb == "open-terminal-stream") return DirectorCommandResult.Success();
            return await SessionCommandExecutor.DispatchAsync(_sm, DirectorId, cmd);
        });

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_gateway.Port}/sessions/{_sid}/stream"), CancellationToken.None);
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("ls\r"), WebSocketMessageType.Binary, true, CancellationToken.None);

        var frame = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(frame.Provenance);
        Assert.Equal(SubmissionRoutes.GatewayTerminal, frame.Provenance!.Route);
        // The shared machine token authenticated this socket, and the relay says so rather than "unknown".
        Assert.Equal(SubmissionIdentityKinds.MachineToken, frame.Provenance.IdentityKind);

        // AND THE ROW THE SESSION RECORDED, which is the point: the keystrokes carried a carriage return, so
        // they are a submitted turn, and what the Director wrote on it is what the relay verified - not an
        // "unknown" it invented, and not something the wire said that the Director then ignored.
        await WaitUntil(() => _ledger.Count > 0, TimeSpan.FromSeconds(20));
        var recorded = Assert.Single(_ledger).Evidence.Provenance;
        Assert.Equal(SubmissionRoutes.GatewayTerminal, recorded.Route);
        Assert.Equal(SubmissionIdentityKinds.MachineToken, recorded.IdentityKind);
        Assert.Null(Assert.Single(_ledger).Evidence.ContentSha256);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
