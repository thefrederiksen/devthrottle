using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole road a guarded restart travels: an HTTP body on POST /machines/{machine}/director/restart,
/// through the relay, down the launcher's own stream, and back again with the launcher's answer.
///
/// WHY IT IS TESTED AT THIS LEVEL AND NOT ONLY IN PIECES. The relay tests prove the relay copies the flag
/// onto the command, and the launcher tests prove the launcher's guard refuses a busy Director. Neither
/// can see the seam between them: a route that parses "onlyIfEmpty" and then forwards a hard-coded false
/// passes every one of those tests while silently restarting Directors mid-drain. An adversarial review
/// named exactly that mutation as one nothing would catch, so the stub launcher here records the command
/// it actually received and the assertions read it.
///
/// The stub is a REAL SignalR client on /launcher-stream, because since the remove-the-network-port
/// mission that is the only way a command reaches a launcher at all.
/// </summary>
public sealed class RestartOnlyIfEmptyRouteTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Machine = "ONLY-IF-EMPTY-MACHINE";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection? _launcher;
    private readonly List<LauncherCommand> _received = new();

    /// <summary>How many sessions the stub launcher's Director is holding.</summary>
    private int _liveSessions;

    /// <summary>Whether the stub launcher understands onlyIfEmpty at all. False stands in for a launcher
    /// older than the flag: it ignores the condition, restarts anyway, and answers a bare success.</summary>
    private bool _launcherHonoursTheFlag = true;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-only-if-empty-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream",
                options => options.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .Build();

        conn.On<LauncherCommand, LauncherCommandResult>("Command", cmd =>
        {
            lock (_received) _received.Add(cmd);
            return Task.FromResult(Answer(cmd));
        });

        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Version = "9.9.9" });
        _launcher = conn;

        // The presence row the real launcher's registration client posts alongside its stream.
        _gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Pid = 4242,
            Version = "9.9.9",
        });
    }

    /// <summary>What the stub launcher answers - the real launcher's behaviour, in miniature.</summary>
    private LauncherCommandResult Answer(LauncherCommand cmd)
    {
        if (cmd.Verb != "director/restart")
            return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, $"unexpected verb: {cmd.Verb}");

        // A launcher older than the flag cannot see it. It restarts and says so, and says nothing about a
        // condition it has never heard of.
        if (!_launcherHonoursTheFlag)
            return LauncherCommandResult.Ok();

        if (cmd.OnlyIfEmpty && _liveSessions > 0)
            return LauncherCommandResult.Refuse(
                $"refusing to restart the Director on {Machine}: it is holding {_liveSessions} live "
                + "sessions. Drain it first - every session writes a handover and is closed - then ask again.");

        return cmd.OnlyIfEmpty
            ? LauncherCommandResult.OkWithPayload(JsonSerializer.Serialize(
                new { ok = true, restarted = true, onlyIfEmpty = true, sessions = 0 }, WebJson))
            : LauncherCommandResult.Ok();
    }

    public async Task DisposeAsync()
    {
        if (_launcher is not null) await _launcher.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    private LauncherCommand[] Received()
    {
        lock (_received) return _received.ToArray();
    }

    // =========================================================================================
    // The flag reaches the launcher
    // =========================================================================================

    [Fact]
    public async Task OnlyIfEmpty_InTheBody_ReachesTheLauncherAsTheFlagOnTheCommand()
    {
        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var command = Assert.Single(Received());
        Assert.Equal("director/restart", command.Verb);
        Assert.True(command.OnlyIfEmpty,
            "the caller asked for a restart only if the Director was empty and the launcher was sent an "
            + "ordinary restart. Every other test in this feature would still pass.");
    }

    [Fact]
    public async Task AnOrdinaryRestart_SendsTheFlagOff_AndKeepsTheAnswerItAlwaysHad()
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(Assert.Single(Received()).OnlyIfEmpty);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("via", body);
        Assert.Contains("stream", body);
    }

    // =========================================================================================
    // The refusal, with its count, reaches the caller
    // =========================================================================================

    [Fact]
    public async Task ABusyDirector_AnswersTheCallerWith409_AndTheLiveSessionCount()
    {
        _liveSessions = 3;

        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("3 live sessions", body);
    }

    // =========================================================================================
    // A launcher that cannot honour the condition must not be reported as having honoured it
    // =========================================================================================

    [Fact]
    public async Task ALauncherThatIgnoresTheFlag_IsReportedAsAFailure_NotAsAGuardedRestart()
    {
        _launcherHonoursTheFlag = false;

        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });

        // The command WAS carried out by that launcher - there is no taking it back - so the honest answer
        // is a failure that says the condition was not applied, never a success.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("launcher-did-not-honour-only-if-empty", body);
    }

    // =========================================================================================
    // A flag that cannot be understood is refused before anything is done
    // =========================================================================================

    [Theory]
    [InlineData("{\"onlyIfEmpty\":\"true\"}")]   // a string, not a boolean
    [InlineData("{\"onlyIfEmpty\":1}")]           // a number
    [InlineData("{\"onlyIfEmpty\":null}")]        // explicitly nothing
    public async Task ANonBooleanFlag_IsRefused_AndNothingReachesTheLauncher(string json)
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("only_if_empty_not_supported", await resp.Content.ReadAsStringAsync());

        // The point of refusing is that nothing happened. A 400 that had already restarted the machine
        // would be the worst of both answers.
        Assert.Empty(Received());
    }

    /// <summary>
    /// A caller who capitalises the flag is unmistakably asking for the guard. A case-sensitive lookup
    /// answered that request with an unconditional restart and a success, which is the same fail-open as
    /// a non-boolean value and was found by the same review.
    /// </summary>
    [Theory]
    [InlineData("{\"OnlyIfEmpty\":true}")]
    [InlineData("{\"onlyifempty\":true}")]
    public async Task TheFlagIsRecognisedWhateverItsCasing(string json)
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(Assert.Single(Received()).OnlyIfEmpty,
            "a differently-cased onlyIfEmpty was read as absent and became an unconditional restart.");
    }

    [Fact]
    public async Task TheFlagSpelledTwice_IsRefused_BecauseNobodyCanSayWhichOneWasMeant()
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart",
            new StringContent("{\"onlyIfEmpty\":true,\"OnlyIfEmpty\":false}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("only_if_empty_not_supported", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Received());
    }

    /// <summary>
    /// An EMPTY body is not a malformed one. Callers have always been allowed to send nothing here, and
    /// some of them label the nothing as JavaScript Object Notation; refusing those would break a caller
    /// that is doing nothing wrong.
    /// </summary>
    [Fact]
    public async Task AnEmptyBodyLabelledAsJson_IsStillAnOrdinaryRestart()
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(Assert.Single(Received()).OnlyIfEmpty);
    }

    /// <summary>
    /// A body sent without a declared length - what a streamed or chunked request looks like - carries the
    /// flag just as well, and the route used to decide there was no body at all and restart regardless.
    /// </summary>
    [Fact]
    public async Task AFlagInABodyWithNoDeclaredLength_StillReachesTheLauncher()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"onlyIfEmpty\":true}");
        var content = new StreamContent(new MemoryStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); // not JSON, and no length
        content.Headers.ContentLength = null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"machines/{Machine}/director/restart")
        {
            Content = content,
        };
        request.Headers.TransferEncodingChunked = true;

        var resp = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(Assert.Single(Received()).OnlyIfEmpty,
            "a body with no declared length was treated as no body, and the guard was dropped.");
    }

    [Fact]
    public async Task ABodyThatWillNotParse_IsRefused_AndNothingReachesTheLauncher()
    {
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("bad_request_body", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Received());
    }

    [Fact]
    public async Task OnlyIfEmptyFalse_IsAnOrdinaryRestart_NotARefusal()
    {
        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = false });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(Assert.Single(Received()).OnlyIfEmpty);
    }
}
