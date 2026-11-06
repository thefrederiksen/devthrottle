using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE FACTORY INBOX SCREENS' GATEWAY HALF (Factory Inbox mission, phase 4), on an in-memory test server: a SPOKEN message
/// through the durable dictation upload with a factory inbox as the target, and the words the list's rows print.
///
/// What these pin: a spoken message is the speech model's raw words plus ONLY the account's listed corrections, with the
/// raw words kept beside it (the verbatim rule); the client sends no words for a recording; a repeated complete leaves
/// nothing twice; a recording with chunks missing is answered with what is missing; a transcription that can work on a
/// retry keeps the recording; the spoken send is refused exactly where the typed one is (archived, session key); and the
/// list hands every row its line, waiting line, badge and tone.
/// </summary>
public sealed class FactoryInboxVoiceEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Browser = "browser";
    private const string ASession = "11111111-1111-4111-8111-111111111111";
    // A minted account id: only such an id names a partition of the dictation staging.
    private static readonly TenantId A = new("a1b2c3d4-0000-4000-8000-00000000000a");

    // The account's hand-listed corrections: "dev throttle" is its spelling of DevThrottle, and nothing else.
    private static DictationDictionary DictionaryOf(TenantId tenant) => tenant == A
        ? new DictationDictionary(["DevThrottle"],
            new Dictionary<string, IReadOnlyList<string>> { ["DevThrottle"] = ["dev throttle"] },
            new Dictionary<string, DictationProfile>())
        : DictationDictionary.Empty;

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _uploadRoot = Path.Combine(Path.GetTempPath(), "fi-voice-" + Guid.NewGuid().ToString("N"));
    private GatewayDatabase _db = null!;
    private FactoryInboxStore _store = null!;
    private FactoryInboxWakeStore _wakes = null!;
    private VoiceUploadStore _uploads = null!;
    // What each route told the wake: a message logged, with its factory and the time it is dated by.
    private readonly List<(TenantId Tenant, string Factory, DateTime SentAtUtc)> _sentToWake = new();
    private WebApplication? _app;
    private HttpClient _client = null!;
    private readonly DateTime _now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    // What the stand-in speech model answers, and how often it was asked.
    private GatewayTranscriptionResult _heard = GatewayTranscriptionResult.Ok("please ask dev throttle to rewrite the draft", "test", "test-model");
    private int _transcriptions;
    private readonly List<(byte[] Audio, string FileName, string Mime)> _transcribed = new();

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db, DictionaryOf);
        _wakes = new FactoryInboxWakeStore(_db);
        _uploads = new VoiceUploadStore(_uploadRoot, TenantId.Local).ForTenant(A);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            StandInForAuthentication(ctx);
            await next();
        });
        FactoryInboxEndpoints.Map(app, new FactoryInboxEndpoints.Parts(
            _store, Resolve, _ => Factories(), (_, _) => SessionFactoryLookup.NotKnown, (_, _) => null, (_, _) => false, () => _now,
            _wakes, (t, f, at) => _sentToWake.Add((t, f, at)), (_, _) => { },
            // The thread's held recordings come from the same staging partition the spoken send registers in.
            (_, factory) => _uploads.PendingFor(FactoryInboxVoiceEndpoints.TargetPrefix + factory)));
        FactoryInboxVoiceEndpoints.Map(app, new FactoryInboxVoiceEndpoints.Parts(
            _store, Resolve, _ => Factories(), _ => _uploads,
            (_, audio, fileName, mime, _) =>
            {
                _transcriptions++;
                _transcribed.Add((audio, fileName, mime));
                return Task.FromResult(_heard);
            },
            () => _now,
            _wakes, (t, f, at) => _sentToWake.Add((t, f, at))));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
        try { Directory.Delete(_uploadRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static List<RegisteredFactoryDto> Factories() =>
    [
        new() { Factory = "invoices", Title = "Invoices", BossSeat = "boss" },
        new() { Factory = "payroll", Title = "Payroll", BossSeat = "boss" },
        new() { Factory = "old-shop", Title = "Old shop", BossSeat = "boss", ArchivedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
    ];

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? new TenantId(v.ToString()) : null;

    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        var tenant = Resolve(ctx) ?? A;
        if (caller is Phone or Browser)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = caller;
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity($"owner-{caller}-1", tenant.Value, caller, "active");
        }
        else if (Guid.TryParse(caller, out var session))
        {
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, tenant, "dir-a");
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string caller, object? body = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, A.Value);
        request.Headers.Add(CallerHeader, caller);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Register(string uploadId, string factory = "invoices", string caller = Phone)
        => Call("POST", $"/gateway/factory/{factory}/inbox/voice", caller, new { }, uploadId);

    private Task<(HttpStatusCode Status, JsonElement Body)> Complete(string uploadId, int chunks, string factory = "invoices",
        string caller = Phone, string note = "bad", DateTime? sentAtUtc = null)
        => Call("POST", $"/gateway/factory/{factory}/inbox/voice/{uploadId}/complete", caller,
            new { totalChunks = chunks, mime = "audio/webm", ext = "webm", note, sentAtUtc = sentAtUtc ?? _now });

    /// <summary>Registers a recording and stages its chunks, as the client does through the unchanged chunk leg.</summary>
    private async Task<string> Recorded(params string[] chunks)
    {
        var id = Guid.NewGuid().ToString();
        var (status, _) = await Register(id);
        Assert.Equal(HttpStatusCode.OK, status);
        var uid = VoiceUploadStore.NormalizeUploadId(id)!;
        for (var i = 0; i < chunks.Length; i++)
            await _uploads.StoreChunkAsync(uid, i, Encoding.UTF8.GetBytes(chunks[i]), null);
        return id;
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---------- the spoken send ----------

    [Fact]
    public async Task Complete_SpokenMessage_IsTheRawWordsPlusOnlyListedCorrections()
    {
        var id = await Recorded("first-half ", "second-half");

        var (status, body) = await Complete(id, 2);

        Assert.Equal(HttpStatusCode.Created, status);
        var message = body.GetProperty("message");
        Assert.Equal("spoken", message.GetProperty("source").GetString());
        Assert.Equal("Bad", message.GetProperty("noteLabel").GetString());
        // "dev throttle" is the account's listed spelling of DevThrottle; nothing else changed.
        Assert.Equal("please ask DevThrottle to rewrite the draft", message.GetProperty("text").GetString());
        Assert.Equal("please ask dev throttle to rewrite the draft", message.GetProperty("rawText").GetString());
        Assert.Equal("amber", message.GetProperty("tone").GetString());
        // The whole recording, in chunk order, went to the speech model once.
        Assert.Equal(1, _transcriptions);
        Assert.Equal("first-half second-half", Encoding.UTF8.GetString(_transcribed[0].Audio));
        Assert.Equal("audio.webm", _transcribed[0].FileName);
    }

    [Fact]
    public async Task Complete_StoredRow_KeepsRawAndCorrectionsThatReproduceTheText()
    {
        var id = await Recorded("audio");
        await Complete(id, 1);

        var (messages, _) = _store.Thread(A, "invoices", 10);
        var row = Assert.Single(messages);
        Assert.Equal("please ask dev throttle to rewrite the draft", row.RawText);
        Assert.Equal("please ask DevThrottle to rewrite the draft", row.Text);
        Assert.Contains("DevThrottle", row.CorrectionsJson);
    }

    [Fact]
    public async Task Complete_SentAnHourBeforeItCompletes_IsDatedByTheSendPress()
    {
        var pressed = _now.AddHours(-1);
        var id = await Recorded("audio");

        var (status, _) = await Complete(id, 1, sentAtUtc: pressed);

        Assert.Equal(HttpStatusCode.Created, status);
        var row = Assert.Single(_store.Thread(A, "invoices", 10).Messages);
        Assert.Equal(pressed, row.SentAtUtc);
    }

    [Fact]
    public async Task Complete_SpokenMessage_OpensTheGatherWindowFromTheSendPress()
    {
        // The press was an hour before the complete: the wake must measure the gather window from the press.
        var pressed = _now.AddHours(-1);
        var id = await Recorded("audio");

        var (status, _) = await Complete(id, 1, sentAtUtc: pressed);

        Assert.Equal(HttpStatusCode.Created, status);
        var told = Assert.Single(_sentToWake);
        Assert.Equal(A, told.Tenant);
        Assert.Equal("invoices", told.Factory);
        Assert.Equal(pressed, told.SentAtUtc);
    }

    [Fact]
    public async Task Complete_Repeated_TellsTheWakeOnce()
    {
        var id = await Recorded("audio");
        await Complete(id, 1);

        await Complete(id, 1);

        Assert.Single(_sentToWake);
    }

    [Fact]
    public async Task Complete_FailedTranscription_TellsTheWakeNothing()
    {
        _heard = GatewayTranscriptionResult.ProviderError("test", "test-model", "the provider timed out");
        var id = await Recorded("audio");

        await Complete(id, 1);

        Assert.Empty(_sentToWake);
    }

    [Fact]
    public async Task Complete_FactoryWaiting_AnswersWithTheSameWaitingLabelAsATypedMessage()
    {
        var row = _wakes.Row(A, "invoices", _now);
        Assert.True(_wakes.TryUpdate(A, "invoices", row.Version, w =>
        {
            w.SecretaryWaiting = FactoryInboxWords.Waiting.NoDirector;
            w.SecretaryWaitingDetail = "SOREN_NORTH";
        }, _now));
        var id = await Recorded("audio");

        var (status, body) = await Complete(id, 1);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Waiting - no Director is running on the Boss's computer, SOREN_NORTH; it goes on when one connects there",
            body.GetProperty("message").GetProperty("stateLabel").GetString());
    }

    [Fact]
    public async Task Complete_WithoutASendTime_IsRefusedAndNothingIsTranscribed()
    {
        var id = await Recorded("audio");

        var (status, body) = await Call("POST", $"/gateway/factory/invoices/inbox/voice/{id}/complete", Phone,
            new { totalChunks = 1, mime = "audio/webm", ext = "webm", note = "bad" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("sent_at_required", Code(body));
        Assert.Equal(0, _transcriptions);
        Assert.Empty(_store.Thread(A, "invoices", 10).Messages);
    }

    [Fact]
    public async Task Complete_Repeated_LeavesNothingTwiceAndTranscribesOnce()
    {
        var id = await Recorded("audio");
        Assert.Equal(HttpStatusCode.Created, (await Complete(id, 1)).Status);

        var (status, body) = await Complete(id, 1);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("terminal").GetBoolean());
        Assert.True(body.GetProperty("delivered").GetBoolean());
        Assert.Equal(1, _transcriptions);
        Assert.Single(_store.Thread(A, "invoices", 10).Messages);
    }

    [Fact]
    public async Task Register_AfterItWasLeft_AnswersTerminalAndDoesNotReopen()
    {
        var id = await Recorded("audio");
        await Complete(id, 1);

        var (status, body) = await Register(id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("terminal").GetBoolean());
        Assert.Equal("This recording was already left in the factory inbox.", body.GetProperty("line").GetString());
    }

    [Fact]
    public async Task Complete_ChunksMissing_SaysWhichAndTranscribesNothing()
    {
        var id = await Recorded("only-the-first");

        var (status, body) = await Complete(id, 3);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(new[] { 1, 2 }, body.GetProperty("missing").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(0, _transcriptions);
    }

    [Fact]
    public async Task Complete_TranscriptionFailsForNow_KeepsTheRecordingAndCanBeRetried()
    {
        var id = await Recorded("audio");
        _heard = GatewayTranscriptionResult.ProviderError("test", "test-model", "the provider timed out");

        var (status, body) = await Complete(id, 1);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Contains("will retry by itself", body.GetProperty("error").GetString());
        Assert.Empty(_store.Thread(A, "invoices", 10).Messages);

        _heard = GatewayTranscriptionResult.Ok("now it works", "test", "test-model");
        Assert.Equal(HttpStatusCode.Created, (await Complete(id, 1)).Status);
        Assert.Single(_store.Thread(A, "invoices", 10).Messages);
    }

    // ---------- a held recording and the 24-hour sweep (phase 4 review, finding 2) ----------

    /// <summary>Ages a recording's staging past the stale-pending sweep's bound, as a night with the app closed does.</summary>
    private void AgedPastTheSweep(string id)
        => Directory.SetLastWriteTimeUtc(Path.Combine(_uploads.Root, VoiceUploadStore.NormalizeUploadId(id)!), DateTime.UtcNow.AddDays(-3));

    [Fact]
    public async Task Sweep_HeldRecordingOlderThanADay_IsNotDroppedAndIsLeftOnTheNextRetry()
    {
        // Arrange: the transcription fails for now, the Gateway promises the recording is kept, and nobody retries for
        // three days.
        var id = await Recorded("audio");
        _heard = GatewayTranscriptionResult.ProviderError("test", "test-model", "the provider timed out");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Complete(id, 1)).Status);
        AgedPastTheSweep(id);

        // Act: the Gateway's dictation sweep runs with its production bound, then the device retries.
        var expired = _uploads.ExpireStalePending(TimeSpan.FromHours(24));
        _heard = GatewayTranscriptionResult.Ok("please rewrite the draft", "test", "test-model");
        var (registered, registerBody) = await Register(id);
        var (completed, _) = await Complete(id, 1);

        // Assert: the message is in the inbox - the register reopened the recording rather than answering "already dealt
        // with; nothing was left", which is the answer that made the device delete its only copy - and the sweep abandoned
        // nothing.
        Assert.Equal("please rewrite the draft", Assert.Single(_store.Thread(A, "invoices", 10).Messages).Text);
        Assert.Equal(HttpStatusCode.OK, registered);
        Assert.False(registerBody.TryGetProperty("terminal", out _));
        Assert.Equal(HttpStatusCode.Created, completed);
        Assert.Equal(0, expired);
    }

    [Fact]
    public void Sweep_ASessionDictationOlderThanADay_IsStillAbandoned()
    {
        // The sweep's own job is unchanged: a session's dictation nobody came back for releases its session lock.
        var uid = VoiceUploadStore.NormalizeUploadId(Guid.NewGuid().ToString())!;
        _uploads.Register(uid);
        _uploads.MarkPending(uid, "22222222-2222-4222-8222-222222222222");
        AgedPastTheSweep(uid);

        Assert.Equal(1, _uploads.ExpireStalePending(TimeSpan.FromHours(24)));
        Assert.Equal(DictationDeliveryState.Abandoned, _uploads.Read(uid).Record!.State);
    }

    [Fact]
    public async Task Thread_HeldRecording_IsShownInTheGatewaysWordsUntilItIsLeft()
    {
        var id = await Recorded("audio");
        _heard = GatewayTranscriptionResult.ProviderError("test", "test-model", "the provider timed out");
        await Complete(id, 1);
        AgedPastTheSweep(id);
        _uploads.ExpireStalePending(TimeSpan.FromHours(24));

        var (status, body) = await Call("GET", "/gateway/factory/invoices/inbox", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        var held = Assert.Single(body.GetProperty("held").EnumerateArray());
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(id), held.GetProperty("uploadId").GetString());
        Assert.Equal(FactoryInboxWords.HeldLine, held.GetProperty("line").GetString());
        Assert.Equal("Last tried", held.GetProperty("atLabel").GetString());
        Assert.Equal("amber", held.GetProperty("tone").GetString());
        // Another factory's thread does not show it.
        Assert.Empty((await Call("GET", "/gateway/factory/payroll/inbox", Phone)).Body.GetProperty("held").EnumerateArray());

        _heard = GatewayTranscriptionResult.Ok("now it works", "test", "test-model");
        Assert.Equal(HttpStatusCode.Created, (await Complete(id, 1)).Status);
        Assert.Empty((await Call("GET", "/gateway/factory/invoices/inbox", Phone)).Body.GetProperty("held").EnumerateArray());
    }

    [Fact]
    public async Task Complete_TranscriptionRefusesForGood_IsResolvedAndNotShownAsHeld()
    {
        var id = await Recorded("audio");
        _heard = GatewayTranscriptionResult.PermanentError("test", "test-model", "unsupported_audio", "the audio format is not supported");

        var (status, _) = await Complete(id, 1);
        var (again, againBody) = await Register(id);
        var (_, thread) = await Call("GET", "/gateway/factory/invoices/inbox", Phone);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.True(againBody.GetProperty("terminal").GetBoolean());
        Assert.False(againBody.GetProperty("delivered").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Empty(thread.GetProperty("held").EnumerateArray());
        Assert.Equal(1, _transcriptions);
    }

    [Fact]
    public async Task Complete_NothingWasSaid_IsRefusedInWordsAndIsFinal()
    {
        var id = await Recorded("audio");
        _heard = GatewayTranscriptionResult.Ok("   ", "test", "test-model");

        var (status, body) = await Complete(id, 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("message_empty", Code(body));
        Assert.Empty(_store.Thread(A, "invoices", 10).Messages);
        var (again, againBody) = await Complete(id, 1);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.False(againBody.GetProperty("delivered").GetBoolean());
        Assert.Equal(1, _transcriptions);
    }

    [Fact]
    public async Task Complete_ForAnotherFactoryThanItWasRegisteredFor_IsRefused()
    {
        var id = await Recorded("audio");

        var (status, body) = await Complete(id, 1, factory: "payroll");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("upload_for_another_target", Code(body));
        Assert.Equal(0, _transcriptions);
    }

    [Fact]
    public async Task Register_ArchivedFactory_IsRefusedInTheTypedSendsWords()
    {
        var (status, body) = await Register(Guid.NewGuid().ToString(), factory: "old-shop");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("factory_archived", Code(body));
        Assert.Equal("'old-shop' is archived, so it takes no messages. Restore it from the Factories screen first.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_SessionKey_IsRefused()
    {
        var (status, body) = await Register(Guid.NewGuid().ToString(), caller: ASession);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_out_of_scope", Code(body));
    }

    [Fact]
    public async Task Register_WithoutARecordingId_IsRefused()
    {
        var (status, body) = await Call("POST", "/gateway/factory/invoices/inbox/voice", Phone, new { });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_upload_id", Code(body));
    }

    [Fact]
    public async Task Complete_FromTheCockpit_IsRecordedAsTheCockpit()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Register(id, caller: Browser)).Status);
        await _uploads.StoreChunkAsync(VoiceUploadStore.NormalizeUploadId(id)!, 0, Encoding.UTF8.GetBytes("audio"), null);

        var (status, body) = await Complete(id, 1, caller: Browser);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Sent from the Cockpit", body.GetProperty("message").GetProperty("trail")[0].GetProperty("label").GetString());
    }

    // ---------- the list's rows ----------

    [Fact]
    public async Task List_EachRow_CarriesItsWordsAndTone()
    {
        var (sent, _) = await Call("POST", "/gateway/factory/invoices/inbox", Phone, new { text = "Check the totals" });
        Assert.Equal(HttpStatusCode.Created, sent);

        var (status, body) = await Call("GET", "/gateway/factory/inbox", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = body.GetProperty("factories").EnumerateArray().ToDictionary(f => f.GetProperty("factory").GetString()!, f => f.GetProperty("row"));
        Assert.Equal("Check the totals", rows["invoices"].GetProperty("line").GetString());
        Assert.Equal("1 waiting for a decision", rows["invoices"].GetProperty("waitingLine").GetString());
        Assert.Equal("Sent", rows["invoices"].GetProperty("badge").GetString());
        Assert.Equal("amber", rows["invoices"].GetProperty("tone").GetString());
        Assert.Equal("No messages yet", rows["payroll"].GetProperty("line").GetString());
        Assert.Equal(JsonValueKind.Null, rows["payroll"].GetProperty("waitingLine").ValueKind);
        Assert.Equal("grey", rows["payroll"].GetProperty("tone").GetString());
        Assert.False(rows.ContainsKey("old-shop"));
    }
}
