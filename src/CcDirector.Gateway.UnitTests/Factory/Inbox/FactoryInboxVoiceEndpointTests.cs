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
    private VoiceUploadStore _uploads = null!;
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
            _store, Resolve, _ => Factories(), (_, _) => SessionFactoryLookup.NotKnown, (_, _) => null, (_, _) => false, () => _now));
        FactoryInboxVoiceEndpoints.Map(app, new FactoryInboxVoiceEndpoints.Parts(
            _store, Resolve, _ => Factories(), _ => _uploads,
            (_, audio, fileName, mime, _) =>
            {
                _transcriptions++;
                _transcribed.Add((audio, fileName, mime));
                return Task.FromResult(_heard);
            },
            () => _now));
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
        string caller = Phone, string note = "bad")
        => Call("POST", $"/gateway/factory/{factory}/inbox/voice/{uploadId}/complete", caller,
            new { totalChunks = chunks, mime = "audio/webm", ext = "webm", note, sentAtUtc = _now });

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
