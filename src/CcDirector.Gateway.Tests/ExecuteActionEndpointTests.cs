using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// In-process stub backend for execute-action endpoint tests: provides a real
/// CircularTerminalBuffer so every byte the executor injects is observable, never spawns
/// a process, and can simulate a process exit on demand (drives Session.Status to Exited
/// through the same backend event a real ConPty exit uses).
/// </summary>
internal sealed class ExecuteActionTestBackend : ISessionBackend
{
    private bool _hasExited;

    public int ProcessId => 0;
    public string Status => "Buffer-only";
    public bool IsRunning => !_hasExited;
    public bool HasExited => _hasExited;
    public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(65536);

#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
#pragma warning restore CS0067
    public event Action<int>? ProcessExited;

    /// <summary>Simulate the agent process dying (non-zero code so the manager keeps the row).</summary>
    public void RaiseProcessExited(int exitCode)
    {
        _hasExited = true;
        ProcessExited?.Invoke(exitCode);
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
    public void Write(byte[] data) => Buffer?.Write(data);
    public Task SendTextAsync(string text) => Task.CompletedTask;
    public Task SendEnterAsync() => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// End-to-end tests for <c>POST /sessions/{sid}/execute-action</c> (issue #327) - the
/// Phase-1B mechanical verb: the caller supplies the complete structured WingmanAction
/// and the Director executes it verbatim through WingmanActionExecutor with zero decision
/// logic. Runs against a real ControlApiHost (auth ENABLED, so the token gate is exercised
/// on every request) with embedded buffer-only sessions, so the exact bytes written to the
/// PTY are asserted, not inferred.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ExecuteActionEndpointTests : IAsyncLifetime
{
    private readonly string _instancesDir;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public ExecuteActionEndpointTests()
    {
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-exec-action-test-" + Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: true, instancesDirectory: _instancesDir);
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
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch { /* best effort */ }
    }

    private (Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var backend = new ExecuteActionTestBackend();
        var session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (session, backend);
    }

    private static byte[] BufferBytes(ExecuteActionTestBackend backend)
    {
        if (backend.Buffer is null)
            throw new InvalidOperationException("test backend has no buffer");
        return backend.Buffer.DumpAll();
    }

    // ---------- The verb executes exactly what the caller passed ----------

    [Fact]
    public async Task ExecuteAction_Submit_WritesTextThenEnterAndEchoesActionVerbatim()
    {
        var (session, backend) = NewSession();

        var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "hello from execute-action", Reason = "caller decided" };
        var resp = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action", action);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(result);
        Assert.True(result.Performed);
        Assert.Equal(WingmanActResult.StatusOk, result.Status);
        // Untransformed mapping: the result echoes the exact action/text/reason passed in.
        Assert.Equal(WingmanAction.ActSubmit, result.Action);
        Assert.Equal("hello from execute-action", result.Text);
        Assert.Equal("caller decided", result.Reason);
        // No LLM in the path: Model stays empty (wingman/act stamps it; this verb never does).
        Assert.Equal("", result.Model);

        // The exact bytes a human keystroke path would produce: the text, then Enter.
        var written = Encoding.UTF8.GetString(BufferBytes(backend));
        Assert.Contains("hello from execute-action", written);
        Assert.EndsWith("\r", written);

        // Executor invariant preserved: the audit trail recorded the action.
        Assert.Single(session.RecentWingmanActions);
        Assert.Equal(WingmanAction.ActSubmit, session.RecentWingmanActions[0].Action);
    }

    [Fact]
    public async Task ExecuteAction_SendKeys_WritesExactMappedBytes()
    {
        var (session, backend) = NewSession();

        var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
        action.Keys.AddRange(new[] { "Down", "Enter" });
        var resp = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action", action);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(result);
        Assert.True(result.Performed);
        Assert.Equal(new[] { "Down", "Enter" }, result.Keys);

        // ESC [ B (Down) then CR (Enter) - byte-for-byte what KeyChords maps, nothing else.
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x42, 0x0D }, BufferBytes(backend));
    }

    // ---------- Executor invariants surface through the endpoint ----------

    [Fact]
    public async Task ExecuteAction_RepeatWithinCooldownOnUnchangedScreen_ReportsSuppressed()
    {
        var (session, backend) = NewSession();

        // Ctrl+C is a C0 control the terminal grid drops, so the screen hash is identical
        // across both calls - exactly the idempotency case the 3s cooldown guards.
        var action = new WingmanAction { Action = WingmanAction.ActSendKeys };
        action.Keys.Add("Ctrl+C");

        var first = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action", action);
        var firstResult = await first.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(firstResult);
        Assert.True(firstResult.Performed);

        var second = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action", action);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondResult = await second.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(secondResult);
        Assert.False(secondResult.Performed);
        Assert.Equal(WingmanActResult.StatusSuppressed, secondResult.Status);

        // Exactly one Ctrl+C byte reached the PTY.
        Assert.Equal(new byte[] { 0x03 }, BufferBytes(backend));
    }

    [Fact]
    public async Task ExecuteAction_OnExitedSession_Returns410AndInjectsNothing()
    {
        var (session, backend) = NewSession();
        backend.RaiseProcessExited(1); // drives Session.Status -> Exited via the real backend event

        var action = new WingmanAction { Action = WingmanAction.ActSubmit, Text = "must not land" };
        var resp = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action", action);

        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(result);
        Assert.False(result.Performed);
        Assert.Equal(WingmanActResult.StatusSessionGone, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("nothing was injected", result.Error);

        Assert.Empty(BufferBytes(backend));
    }

    [Fact]
    public async Task ExecuteAction_None_IsAcceptedAsNoOp()
    {
        var (session, backend) = NewSession();

        var resp = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action",
            new WingmanAction { Action = WingmanAction.ActNone });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(result);
        Assert.False(result.Performed);
        Assert.Equal(WingmanActResult.StatusOk, result.Status);

        Assert.Empty(BufferBytes(backend));
        Assert.Empty(session.RecentWingmanActions);
    }

    // ---------- Caller errors are 4xx and inject nothing ----------

    [Fact]
    public async Task ExecuteAction_UnknownActionName_Returns400()
    {
        var (session, backend) = NewSession();

        var resp = await _client.PostAsJsonAsync($"sessions/{session.Id}/execute-action",
            new WingmanAction { Action = "frobnicate", Text = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<WingmanActResult>();
        Assert.NotNull(result);
        Assert.Equal(WingmanActResult.StatusBadRequest, result.Status);
        Assert.Empty(BufferBytes(backend));
    }

    [Fact]
    public async Task ExecuteAction_NullBody_Returns400()
    {
        var (session, _) = NewSession();

        var content = new StringContent("null", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync($"sessions/{session.Id}/execute-action", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ExecuteAction_UnknownSession_Returns404()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/execute-action",
            new WingmanAction { Action = WingmanAction.ActNone });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ExecuteAction_InvalidSessionIdFormat_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("sessions/not-a-guid/execute-action",
            new WingmanAction { Action = WingmanAction.ActNone });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---------- The verb is token-gated like every other verb ----------

    [Fact]
    public async Task ExecuteAction_WithoutBearerToken_Returns401()
    {
        var (session, backend) = NewSession();

        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        var resp = await anonymous.PostAsJsonAsync($"sessions/{session.Id}/execute-action",
            new WingmanAction { Action = WingmanAction.ActSubmit, Text = "should be rejected" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(BufferBytes(backend));
    }
}
