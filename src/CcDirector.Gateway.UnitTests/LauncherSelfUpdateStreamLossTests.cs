using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #4423: Update now worked - the launcher and the Director came back on the new version - and the
/// Directors page then said the launcher was too old to accept commands, and told the owner to update it.
///
/// An update restarts the launcher, so two things happen that look like "heartbeating with no stream": the
/// stream closes while the update request is still waiting for an answer, and the new process registers
/// before it opens its own stream. Neither is a launcher that is too old. These tests drive both through a
/// real launcher hub on an in-memory test server, with a client that hangs up mid-command the way a launcher
/// replacing itself does - nothing listens on a port and nothing waits on the clock.
/// </summary>
public sealed class LauncherSelfUpdateStreamLossTests : IAsyncLifetime
{
    private const string Machine = "studio-mac";

    private readonly LauncherRegistry _launchers = new();
    private readonly LauncherConnectionRegistry _connections = new();
    private readonly TaskCompletionSource _streamClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        // The same wiring the Gateway host does: a cleared stream is written onto the launcher's registration.
        _connections.StreamClosed = (tenant, machine, closedAt) =>
        {
            _launchers.NoteStreamClosed(tenant, machine, closedAt);
            _streamClosed.TrySetResult();
        };

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_connections);
        builder.Services.AddSingleton(new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false));
        _app = builder.Build();
        _app.MapHub<LauncherHub>("/launcher-stream");
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>The Gateway host's send hook, minus the host: find the connection, push the command down it.</summary>
    private LauncherCommandRouter.SendLauncherCommandAsync SendHook()
    {
        var hub = _app.Services.GetRequiredService<IHubContext<LauncherHub>>();
        return async (tenant, machine, command, ct) =>
            _connections.GetActiveConnection(tenant, machine) is { } connection
                ? await LauncherCommandRouter.InvokeDownStreamAsync(hub, connection.ConnectionId, machine, command, ct)
                : null;
    }

    /// <summary>A launcher that takes the update and goes before it answers - the old process, replaced.</summary>
    private async Task<HubConnection> ConnectLauncherThatRestartsOnUpdateAsync()
    {
        var server = _app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "launcher-stream"), HttpTransportType.LongPolling,
                o => o.HttpMessageHandlerFactory = _ => server.CreateHandler())
            .Build();
        var neverAnswers = new TaskCompletionSource<LauncherCommandResult>();
        Func<LauncherCommand, Task<LauncherCommandResult>> restartInsteadOfAnswering = command =>
        {
            _ = Task.Run(() => connection.StopAsync());
            return neverAnswers.Task;
        };
        connection.On("Command", restartInsteadOfAnswering);
        await connection.StartAsync();
        await connection.InvokeAsync("Hello", new LauncherStreamHello
        {
            MachineName = Machine,
            Version = "2.28.0",
            Capability = new LauncherCapabilityDeclaration { Commands = { LauncherCapabilities.DirectorUpdate } },
        });
        Assert.NotNull(_connections.GetActiveConnection(TenantId.Local, Machine));
        return connection;
    }

    private void RegisterLauncher(string version) => _launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
    {
        MachineName = Machine,
        Pid = version == "2.28.0" ? 101 : 202,
        Version = version,
        StartedAt = DateTime.UtcNow,
    });

    private Task<LauncherLifecycleRelay.LauncherRelayOutcome> SendUpdateAsync(LauncherCommandRouter.SendLauncherCommandAsync hook)
        => LauncherLifecycleRelay.SendQueryAsync(TenantId.Local, Machine, LauncherCapabilities.DirectorUpdate,
            query: null, limit: 0, timeoutMilliseconds: 0, _launchers, hook, CancellationToken.None);

    [Fact]
    public async Task SendQueryAsync_StreamClosesWhileTheUpdateIsInFlight_IsStreamLostNotTooOld()
    {
        // Arrange - the 2.28.0 launcher is registered and holding its stream.
        RegisterLauncher("2.28.0");
        await using var launcher = await ConnectLauncherThatRestartsOnUpdateAsync();

        // Act - Update now; the launcher replaces itself before it can answer.
        var outcome = await SendUpdateAsync(SendHook()).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.StreamLost, outcome.Kind);
        Assert.False(outcome.Accepted);
    }

    [Fact]
    public async Task SendQueryAsync_NewLauncherRegisteredButNotYetStreaming_IsReconnectingNotTooOld()
    {
        // Arrange - the update ran, the old stream is gone, and the new process has registered with its new
        // version but has not opened its stream yet. That is the window the owner's question landed in.
        RegisterLauncher("2.28.0");
        await using (var launcher = await ConnectLauncherThatRestartsOnUpdateAsync())
        {
            await SendUpdateAsync(SendHook()).WaitAsync(TimeSpan.FromSeconds(30));
            await _streamClosed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        // The old launcher quits cleanly: its registration client DELETEs its row (review finding 1). Then the new
        // process registers, before it opens its own stream.
        _launchers.Remove(TenantId.Local, Machine);
        RegisterLauncher("2.30.0");

        // Act - ask again before the new stream is up.
        var outcome = await SendUpdateAsync(SendHook());

        // Assert
        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.Reconnecting, outcome.Kind);
        Assert.Equal("2.30.0", outcome.LauncherVersion);
        Assert.NotNull(_launchers.Get(TenantId.Local, Machine)!.StreamClosedAt);
    }

    [Fact]
    public async Task SendQueryAsync_LauncherThatNeverStreamed_IsStillTooOld()
    {
        // Arrange - heartbeating, and this Gateway has never seen it hold a stream: the case "too old" is for.
        RegisterLauncher("1.9.8");

        // Act
        var outcome = await SendUpdateAsync(SendHook());

        // Assert
        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.NotStreamCapable, outcome.Kind);
    }
}

/// <summary>
/// Issue #4423 at the route the Directors page calls: what POST /machines/{machine}/director/update says when the
/// launcher's stream is lost mid-update, and when it is reconnecting. In-memory test server; no port, no waiting.
/// </summary>
public sealed class DirectorUpdateRouteStreamLossTests : IAsyncLifetime
{
    private const string Machine = "studio-mac";

    private readonly LauncherRegistry _launchers = new();
    private LauncherCommandRouter.SendLauncherCommandAsync _send = (_, _, _, _) => Task.FromResult<LauncherCommandResult?>(null);
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        _app = builder.Build();
        MachineEndpoints.Map(_app, false, _launchers,
            new MachineSessionSpawner(new NoDirectors(),
                (DirectorCommandRouter.SendDirectorCommandAsync)((_, _, _) => Task.FromResult<DirectorCommandResult?>(null))),
            boundary: new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false),
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            sendLauncherCommand: (tenant, machine, command, ct) => _send(tenant, machine, command, ct));
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private sealed class NoDirectors : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            throw new InvalidOperationException("no Director is resolved by these tests");
    }

    private void RegisterLauncher(string version) => _launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
    {
        MachineName = Machine,
        Pid = 202,
        Version = version,
        StartedAt = DateTime.UtcNow,
    });

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostUpdateAsync()
    {
        var res = await _http.PostAsync($"/machines/{Machine}/director/update", content: null);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        return (res.StatusCode, body);
    }

    // Review finding 3: the expected outcome of an update that restarts the launcher is answered as the update's view,
    // in the warn tone - not as an error the Cockpit shows in red and files as a fault.
    [Fact]
    public async Task PostUpdate_StreamLostMidUpdate_AnswersTheUpdateViewInTheWarnTone()
    {
        // Arrange
        RegisterLauncher("2.28.0");
        _send = (_, machine, command, _) => throw new LauncherStreamLostException(machine, command.Verb,
            new IOException("Connection 'abc' disconnected."));

        // Act
        var (status, body) = await PostUpdateAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(FleetTone.Warn, body.GetProperty("tone").GetString());
        var headline = body.GetProperty("headline").GetString()!;
        Assert.Contains("restarted", headline);
        Assert.Contains("version column", headline);
        Assert.DoesNotContain("too old", headline);
    }

    [Fact]
    public async Task PostUpdate_StreamDownLongerThanARestartTakes_SaysRestartTheLauncherNotTooOld()
    {
        // Arrange
        RegisterLauncher("2.30.0");
        _launchers.NoteStreamClosed(TenantId.Local, Machine, DateTime.UtcNow - LauncherReachability.ReconnectWindow - TimeSpan.FromMinutes(1));

        // Act
        var (status, body) = await PostUpdateAsync();

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("launcher-stream-down", body.GetProperty("reason").GetString());
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("Restart the launcher", error);
        Assert.DoesNotContain("too old", error);
    }

    [Fact]
    public async Task PostUpdate_LauncherReconnecting_SaysReconnectingAndNeverTooOld()
    {
        // Arrange - heartbeating, no stream now, and a stream was seen closing.
        RegisterLauncher("2.30.0");
        _launchers.NoteStreamClosed(TenantId.Local, Machine, DateTime.UtcNow);

        // Act
        var (status, body) = await PostUpdateAsync();

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("launcher-reconnecting", body.GetProperty("reason").GetString());
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("reconnecting", error);
        Assert.DoesNotContain("too old", error);
    }

    [Fact]
    public async Task PostUpdate_LauncherThatNeverStreamed_StillSaysTooOld()
    {
        // Arrange
        RegisterLauncher("1.9.8");

        // Act
        var (status, body) = await PostUpdateAsync();

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("launcher-too-old", body.GetProperty("reason").GetString());
    }
}
