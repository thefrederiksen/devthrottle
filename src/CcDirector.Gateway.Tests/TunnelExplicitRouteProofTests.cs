using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR C): the EXPLICITLY-registered session routes in
/// <c>GatewayEndpoints</c> that used to dial the owning Director over HTTP now ride the tunnel under stream
/// mode. This boots a REAL streamMode <see cref="GatewayHost"/>, dials the REAL DirectorHub with a REAL
/// MessagePack SignalR client, and drives each route end to end.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered with a DELIBERATELY UNREACHABLE control endpoint, so an
/// HTTP dial cannot succeed - a 200 with the expected body can ONLY have ridden the tunnel. Each test also
/// asserts the exact verb (and, where it matters, the payload) the Gateway sent DOWN the tunnel, so a route
/// that silently mapped to the wrong verb or dropped its query parameters fails loudly.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelExplicitRouteProofTests : IAsyncLifetime
{
    private const string Token = "test-token-explicit-route-proof";
    private const string DirectorId = "dir-explicit";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-explicit-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;
    private SessionManager _sm = null!;
    private Session _session = null!;
    private string _sid = "";

    // The last command the Director saw over the tunnel, so a test can assert the verb + payload the route sent.
    private DirectorCommand? _lastCommand;

    public TunnelExplicitRouteProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-explicit-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // THE SESSION SUPERVISOR IS SWITCHED OFF. Pushing a session that has stopped is a turn end, and the
        // supervisor answers every turn end with one "screen-grid" read of that session's screen, looking for a
        // dropped connection. It runs on its own task, so its read reaches the tunnel at an unpredictable moment
        // and was captured here as the command a route sent: the build failed with "expected handover, actual
        // screen-grid", on a different route each run. Every route proof below asserts which verb rode the
        // tunnel, so none of them can share the tunnel with a read they did not make.
        _gateway.TenantSettingsResolver.SetSessionSupervisorEnabled(
            CcDirector.Core.Tenancy.TenantId.Local, false, DateTime.UtcNow);

        // A REAL session so the Director-side handlers (which validate the session exists) run the real code.
        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        _sid = _session.Id.ToString();

        // The Director registers UNREACHABLE, so any working route proves it rode the tunnel, never an HTTP dial.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59918/", // nothing listens here
            MachineName = "explicit-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        // Push the session so the Gateway resolves this Director as its owner (TryLocate) and targets the tunnel.
        await _conn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
        });
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

    // The Director-side Command handler: records the command, then returns a canned body per verb so the test
    // can assert the Gateway route surfaced exactly that body as its HTTP response.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _lastCommand = cmd;
        return cmd.Verb switch
        {
            "buffer" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, buffer = "hello", newCursor = 5 })),
            "summary" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, directorId = DirectorId, title = "a summary" })),
            "git-status" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { branch = "main", ahead = 0, behind = 0 })),
            "handover" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, directorId = DirectorId, displayName = "a session" })),
            "recap" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, recap = "a recap", cached = true })),
            "wingman-view" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, color = "green" })),
            "request-deletion" => DirectorCommandResult.Success(),
            "cancel-deletion" => DirectorCommandResult.Success(),
            "wingman-ask" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { status = "ok", answer = "an answer" })),
            "recap-generate" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, recap = "a generated recap", model = "opus" })),
            // The chunked upload verbs run the REAL Director reassembly + save so the proof exercises the real
            // begin/chunk/complete path end to end (a real file is written under the test's screenshots folder).
            "upload-image-begin" => SessionByteExecutor.UploadImageBegin(_sm, cmd),
            "upload-image-chunk" => SessionByteExecutor.UploadImageChunk(cmd),
            "upload-image-complete" => SessionByteExecutor.UploadImageComplete(cmd),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };
    }

    // ------------------------------------------------------------------------------------- reads ----

    [Fact]
    public async Task Buffer_ridesTheTunnel_andCarriesItsQueryParamsInThePayload()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/buffer?lines=25&raw=true&since=7");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("hello", node?["buffer"]?.GetValue<string>());

        Assert.Equal("buffer", _lastCommand!.Verb);
        var payload = JsonNode.Parse(_lastCommand.PayloadJson)!.AsObject();
        Assert.Equal(25, (int?)payload["lines"]);
        Assert.Equal(true, (bool?)payload["raw"]);
        Assert.Equal(7, (long?)payload["since"]);
    }

    [Fact]
    public async Task Summary_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("a summary", node?["title"]?.GetValue<string>());
        Assert.Equal(DirectorId, node?["directorId"]?.GetValue<string>()); // the Director core sets DirectorId in its body
        Assert.Equal("summary", _lastCommand!.Verb);
    }

    [Fact]
    public async Task Git_ridesTheTunnel_asTheGitStatusVerb()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/git");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("main", node?["branch"]?.GetValue<string>());
        Assert.Equal("git-status", _lastCommand!.Verb); // the /git route maps to the git-status tunnel verb
    }

    [Fact]
    public async Task Handover_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/handover");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("a session", node?["displayName"]?.GetValue<string>());
        Assert.Equal(DirectorId, node?["directorId"]?.GetValue<string>());
        Assert.Equal("handover", _lastCommand!.Verb);
    }

    [Fact]
    public async Task RecapRead_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/recap");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("a recap", node?["recap"]?.GetValue<string>());
        Assert.Equal("recap", _lastCommand!.Verb);
    }

    [Fact]
    public async Task WingmanView_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/wingman");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("green", node?["color"]?.GetValue<string>());
        Assert.Equal("wingman-view", _lastCommand!.Verb);
    }

    // ------------------------------------------------------------------------------------ writes ----

    [Fact]
    public async Task RequestDeletion_ridesTheTunnel_andSynthesizesPendingDeletionTrue()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/request-deletion", new { reason = "done" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["pendingDeletion"]?.GetValue<bool>());

        Assert.Equal("request-deletion", _lastCommand!.Verb);
        var payload = JsonNode.Parse(_lastCommand.PayloadJson)!.AsObject();
        Assert.Equal("done", (string?)payload["reason"]); // the deletion reason rode the payload
    }

    [Fact]
    public async Task CancelDeletion_ridesTheTunnel_andSynthesizesPendingDeletionFalse()
    {
        var resp = await _http.DeleteAsync($"sessions/{_sid}/request-deletion");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(node?["pendingDeletion"]?.GetValue<bool>());
        Assert.Equal("cancel-deletion", _lastCommand!.Verb);
    }

    // ---------------------------------------------------------------------------- slow LLM (unary) ----

    [Fact]
    public async Task WingmanAsk_ridesTheTunnel_asASynchronousUnaryVerb()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/wingman/ask", new { question = "what is happening" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("an answer", node?["answer"]?.GetValue<string>());

        Assert.Equal("wingman-ask", _lastCommand!.Verb);
        var payload = JsonNode.Parse(_lastCommand.PayloadJson)!.AsObject();
        Assert.Equal("what is happening", (string?)payload["question"]); // the question rode the payload
    }

    [Fact]
    public async Task RecapGenerate_ridesTheTunnel_andPreservesThe201AndModel()
    {
        var resp = await _http.PostAsync($"sessions/{_sid}/recap?model=opus", content: null);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode); // the HTTP path returned 201; the tunnel path preserves it
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("a generated recap", node?["recap"]?.GetValue<string>());

        Assert.Equal("recap-generate", _lastCommand!.Verb);
        var payload = JsonNode.Parse(_lastCommand.PayloadJson)!.AsObject();
        Assert.Equal("opus", (string?)payload["model"]); // the model query param rode the payload
    }

    // ------------------------------------------------------------------------- chunked upload-image ----

    [Fact]
    public async Task UploadImage_ridesTheTunnel_chunked_andReassemblesByteForByte()
    {
        // A 50 KB image spans multiple chunks (UploadChunkRawBytes = 20 KB -> 3 chunks: 20 + 20 + 10).
        var image = new byte[(DirectorStreamLimits.UploadChunkRawBytes * 2) + 10 * 1024];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)((i * 31 + 7) % 251);

        using var form = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(image);
        filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(filePart, "file", "photo.png");

        var resp = await _http.PostAsync($"sessions/{_sid}/upload-image", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        var savedPath = node?["path"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(savedPath));

        // The chunked upload rode the tunnel: begin, three chunks, then complete were the commands seen.
        Assert.Equal("upload-image-complete", _lastCommand!.Verb);

        // The Director reassembled the chunks byte-for-byte and saved the real file (same machine, in-process).
        Assert.True(File.Exists(savedPath), $"expected the reassembled image at {savedPath}");
        var saved = await File.ReadAllBytesAsync(savedPath!);
        Assert.Equal(image, saved);
        Assert.EndsWith(".png", savedPath);

        // The save landed INSIDE this test's throwaway root, not in the user's real Pictures\Screenshots.
        // Asserted, not assumed: this test set CC_DIRECTOR_ROOT and trusted that to sandbox the write, but
        // CcStorage.Screenshots() ignored the root and fell through to the user's own screenshots folder, so
        // every run of this suite left a 50 KB undrawable "photo.png" in their gallery.
        Assert.StartsWith(_root, savedPath!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------- helpers ----

}
