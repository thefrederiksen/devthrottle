using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole road the capability answer travels - issue #2720: a launcher declares itself over a REAL
/// SignalR stream, the Gateway stores that beside the connection, and GET
/// /machines/{machine}/restart-capability folds it into a verdict.
///
/// WHY IT IS TESTED AT THIS LEVEL AND NOT ONLY AS A FOLD. The fold's own tests prove the verdict from
/// facts handed to it; the hub's tests prove a connection is bound. Neither can see the seam between
/// them, and the seam is where this fails silently: a Hello that carried a declaration the hub then
/// dropped would leave every machine answering DeclaredNothing for ever, which is a PLAUSIBLE answer
/// (it is the true answer for every launcher shipped so far) and would pass every unit test in the
/// change. So the launcher here is a real client on /launcher-stream, sending a real Hello, because
/// since the remove-the-network-port mission that is the only way a launcher reaches the Gateway at all.
///
/// NOTHING IS RESTARTED AND NOTHING IS COMMANDED. The route under test is a read; the stub launcher
/// below answers no commands because none are sent to it.
/// </summary>
public sealed class MachineRestartCapabilityRouteTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Machine = "RESTART-CAPABILITY-MACHINE";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection? _launcher;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-restart-capability-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        if (_launcher is not null) await _launcher.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    /// <summary>Open a real launcher stream and send a real Hello, with or without a declaration.</summary>
    private async Task JoinAsLauncherAsync(LauncherCapabilityDeclaration? declaration, string version = "2.1.0")
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream",
                options => options.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .Build();

        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello
        {
            MachineName = Machine,
            Version = version,
            Capability = declaration,
        });
        _launcher = conn;
    }

    /// <summary>The presence row the real launcher's registration client posts alongside its stream.</summary>
    private void Register(string version, TimeSpan? quietFor = null)
    {
        _gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Pid = 4242,
            Version = version,
        });
    }

    private static LauncherCapabilityDeclaration Declaration(
        bool? signalArmed = true, bool instanceHome = false, params string[] commands) => new()
    {
        Commands = commands.ToList(),
        RestartSignalArmed = signalArmed,
        ServingRootIsInstanceHome = instanceHome,
        ServingRootKey = "b1706c7af60c",
    };

    private async Task<JsonElement> AskAsync()
    {
        var response = await _http.GetAsync($"machines/{Machine}/restart-capability");

        // ALWAYS 200, INCLUDING WHEN THE ANSWER IS NO. A cannot-restart verdict is a successful answer
        // to the question that was asked. A 404 or 502 here would be read by a caller's error handling
        // as "the query broke", which is exactly the reading that lets a drain carry on regardless.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    // =========================================================================================
    // The declaration survives the whole road
    // =========================================================================================

    /// <summary>
    /// THE SEAM. A declaration sent on a real Hello comes back out of the route, fact for fact. Every
    /// one of these is a fact only the machine could know, and every one of them would be silently
    /// missing - with a plausible answer in its place - if the hub dropped the declaration.
    /// </summary>
    [Fact]
    public async Task A_declaration_sent_on_Hello_comes_back_out_of_the_route()
    {
        Register("2.1.0");
        await JoinAsLauncherAsync(Declaration(
            signalArmed: true,
            commands: new[] { LauncherCapabilities.DirectorRestart, LauncherCapabilities.Apps }));

        var answer = await AskAsync();

        Assert.Equal("CanRestart", S(answer, "verdict"));
        Assert.Equal("Connected", S(answer, "reach"));
        Assert.Equal("Declared", S(answer, "declaration"));
        Assert.Equal("Listening", S(answer, "restartSignal"));
        Assert.Equal("b1706c7af60c", S(answer, "servingRootKey"));
        Assert.False(answer.GetProperty("servingRootIsInstanceHome").GetBoolean());
        Assert.Contains(LauncherCapabilities.DirectorRestart,
            answer.GetProperty("declaredCommands").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("declares director/restart", S(answer, "reason"));
    }

    /// <summary>
    /// A launcher that streams and declares nothing - every launcher in the fleet today. Reachable, and
    /// silent about itself. It can still be restarted, and the reason says the capability is INFERRED
    /// from the stream rather than taken from the launcher's own word.
    /// </summary>
    [Fact]
    public async Task A_launcher_that_streams_and_declares_nothing_is_DeclaredNothing_and_can_still_restart()
    {
        Register("2.0.6");
        await JoinAsLauncherAsync(declaration: null, version: "2.0.6");

        var answer = await AskAsync();

        Assert.Equal("CanRestart", S(answer, "verdict"));
        Assert.Equal("DeclaredNothing", S(answer, "declaration"));
        Assert.Equal("NotDeclared", S(answer, "restartSignal"));
        Assert.Contains("inferred from the stream", S(answer, "reason"));

        // And the guard is UNKNOWN, not available. That is the whole prevention-versus-detection point:
        // a drain must not send a guarded restart to a launcher that cannot promise to honour it.
        Assert.Equal("Unknown", S(answer, "guardedRestart"));
    }

    /// <summary>
    /// THE 2026-09-06 MACHINE, END TO END. Registered and heartbeating with no stream at all - so no
    /// Hello was ever received and nothing was ever declared. It must be a plain NO naming the fix, and
    /// it must NOT read like a launcher that answered and said no.
    /// </summary>
    [Fact]
    public async Task A_registered_launcher_that_never_joined_the_stream_cannot_restart()
    {
        Register("1.9.8");

        var answer = await AskAsync();

        Assert.Equal("CannotRestart", S(answer, "verdict"));
        Assert.Equal("NotStreamCapable", S(answer, "reach"));
        Assert.Equal("NotDeclared", S(answer, "declaration"));
        Assert.Contains("1.9.8", S(answer, "reason"));
        Assert.Contains("predates the command stream", S(answer, "reason"));
        Assert.Contains("network connection is not the problem", S(answer, "reason"));
        Assert.Contains("it holds no command stream, so it has declared nothing", S(answer, "guardedRestartReason"));
    }

    /// <summary>No launcher at all: a third fix again, and a 200 carrying a no rather than a 404.</summary>
    [Fact]
    public async Task A_machine_with_no_launcher_answers_a_no_rather_than_a_not_found()
    {
        var answer = await AskAsync();

        Assert.Equal("CannotRestart", S(answer, "verdict"));
        Assert.Equal("NoLauncher", S(answer, "reach"));
        Assert.Contains("Install and start cc-launcher", S(answer, "reason"));
    }

    /// <summary>
    /// THE WRONG-ROOT MACHINE, END TO END. Streaming, declaring everything, signal armed - and serving a
    /// Director's instance home. Every other fact reads healthy and the verdict is still no.
    /// </summary>
    [Fact]
    public async Task A_launcher_serving_an_instance_home_answers_no_with_every_other_fact_healthy()
    {
        Register("2.1.0");
        await JoinAsLauncherAsync(Declaration(
            signalArmed: true, instanceHome: true,
            commands: new[] { LauncherCapabilities.DirectorRestart }));

        var answer = await AskAsync();

        Assert.Equal("Connected", S(answer, "reach"));
        Assert.Equal("Declared", S(answer, "declaration"));
        Assert.Equal("Listening", S(answer, "restartSignal"));
        Assert.Equal("CannotRestart", S(answer, "verdict"));
        Assert.True(answer.GetProperty("servingRootIsInstanceHome").GetBoolean());
        Assert.Contains("instance home", S(answer, "reason"));
        Assert.Contains("no CC_DIRECTOR_ROOT", S(answer, "reason"));
    }

    /// <summary>
    /// A launcher that declares the guarded restart gets an Available guard through the whole road. This
    /// is the state Phase 2's flag reaches once a launcher build honours it, and it is asserted here so
    /// that the wiring is proved before any launcher makes the promise.
    /// </summary>
    [Fact]
    public async Task A_launcher_that_declares_the_guarded_restart_reports_it_as_available()
    {
        Register("2.2.0");
        await JoinAsLauncherAsync(Declaration(commands: new[]
        {
            LauncherCapabilities.DirectorRestart, LauncherCapabilities.DirectorRestartOnlyIfEmpty,
        }));

        var answer = await AskAsync();

        Assert.Equal("Available", S(answer, "guardedRestart"));
        Assert.Contains("say how many", S(answer, "guardedRestartReason"));
    }

    /// <summary>
    /// WHEN THE LAUNCHER GOES, ITS PROMISES GO WITH IT. A declaration that outlived its connection would
    /// answer CanRestart about a machine that is no longer there - and it would answer confidently,
    /// because every fact in that record was true when it was written.
    /// </summary>
    [Fact]
    public async Task A_launcher_that_disconnects_stops_answering_for_itself()
    {
        Register("2.1.0");
        await JoinAsLauncherAsync(Declaration(commands: new[] { LauncherCapabilities.DirectorRestart }));
        Assert.Equal("Declared", S(await AskAsync(), "declaration"));   // control: it WAS answering

        await _launcher!.DisposeAsync();
        _launcher = null;

        // The hub's disconnect is asynchronous, so this waits for the state rather than assuming it.
        var declaration = "";
        for (var i = 0; i < 100 && declaration != "NotDeclared"; i++)
        {
            declaration = S(await AskAsync(), "declaration");
            if (declaration != "NotDeclared") await Task.Delay(50);
        }

        Assert.Equal("NotDeclared", declaration);
    }

    /// <summary>
    /// The route is a READ and touches nothing. Asked twenty times in a row it gives the same answer -
    /// which is the observable form of "a capability check may not have consequences", and matters
    /// because the fact it reports about the restart signal is one that the OTHER way of asking (raising
    /// the signal) would have acted on.
    /// </summary>
    [Fact]
    public async Task Asking_repeatedly_changes_nothing()
    {
        Register("2.1.0");
        await JoinAsLauncherAsync(Declaration(commands: new[] { LauncherCapabilities.DirectorRestart }));

        var first = await AskAsync();
        for (var i = 0; i < 20; i++)
        {
            var again = await AskAsync();
            Assert.Equal(S(first, "verdict"), S(again, "verdict"));
            Assert.Equal(S(first, "reason"), S(again, "reason"));
            Assert.Equal(S(first, "declaration"), S(again, "declaration"));
        }
    }

    /// <summary>The machine name is echoed as the caller wrote it, so an answer can be matched to its
    /// question when several machines are asked in one pass.</summary>
    [Fact]
    public async Task The_answer_names_the_machine_the_caller_asked_about()
    {
        var answer = await AskAsync();
        Assert.Equal(Machine, S(answer, "machine"));
    }
}
