using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CcDirector.Launcher;

/// <summary>
/// launcher-persistent-join: the launcher's outbound client for the Gateway's LauncherHub. It dials the
/// Gateway (never the other way), authenticates with the configured Gateway credential, and keeps a
/// persistent stream open so the Gateway can PUSH a lifecycle command DOWN it - start/stop/restart the
/// installed Director, or a generic launch.
///
/// THIS CONNECTION IS THE ONLY WAY A COMMAND REACHES THIS LAUNCHER (remove-the-network-port mission,
/// phase 6). The launcher listens on nothing - there is no loopback REST interface for the Gateway to
/// relay to and no stream-mode switch to leave this off. If a Gateway is configured, this client runs;
/// a launcher without it is a launcher no machine route can drive, and the Gateway says so loudly
/// rather than reaching for a second path.
///
/// It is the launcher twin of the Director's <c>GatewayStreamClient</c>, but simpler: a launcher pushes no
/// session state, so on connect/reconnect it sends only <see cref="LauncherStreamHello"/>, then waits for
/// commands on the down-channel and executes them through the SAME <see cref="DirectorSupervisor"/> /
/// <see cref="LaunchService"/> actions.
///
/// It runs ALONGSIDE the existing <see cref="GatewayRegistrationClient"/> (which stays for presence and
/// heartbeat metadata). It is inert only when no Gateway is configured at all.
/// </summary>
public sealed class LauncherStreamClient : IAsyncDisposable
{
    private readonly GatewayConfig _config;
    private readonly string _version;
    private readonly DirectorSupervisor _supervisor;
    private readonly LaunchService _launchService;
    private readonly AppCatalog _appCatalog;
    private readonly FileSearchService _fileSearch;

    /// <summary>Query answers are serialised here before they ride back up the stream, using web-style
    /// naming so the documents agents read keep the shape they have always had.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>What to say when the supervisor reports an outcome and no reason for it. It cannot happen
    /// by construction; it is here so that if it ever does, the answer says whose fault it is.</summary>
    private const string UnexplainedOutcome =
        "the launcher did not restart the Director and did not say why, which is a fault in the launcher "
        + "rather than an answer about the Director";

    private HubConnection? _connection;
    private int _started;
    private volatile bool _disposed;

    /// <summary>Reconnect backoff between long-outage restart attempts once auto-reconnect has given up.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    public LauncherStreamClient(GatewayConfig config, string version, DirectorSupervisor supervisor,
        LaunchService launchService, AppCatalog? appCatalog = null, FileSearchService? fileSearch = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _version = version ?? "";
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _appCatalog = appCatalog ?? new AppCatalog();
        _fileSearch = fileSearch ?? new FileSearchService();
    }

    /// <summary>True when a Gateway is configured. When false, <see cref="Start"/> is a no-op. Deliberately
    /// NOT gated on <see cref="GatewayConfig.StreamMode"/>: the stream is the only command path to a
    /// launcher, so an off switch here would be an off switch for cross-machine lifecycle itself.</summary>
    public bool IsEnabled => _config.IsEnabled;

    /// <summary>Start dialing the Gateway. Idempotent; inert when <see cref="IsEnabled"/> is false.</summary>
    public void Start()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write($"[LauncherStreamClient] Start: dialing {_config.Url}/launcher-stream for machine {Environment.MachineName}");
        _ = SuperviseAsync();
    }

    // Owns the connection for its whole life: build once, keep it connected, and re-Hello on every
    // (re)connection. Auto-reconnect handles transient drops fast; when it gives up (Closed), this loop
    // restarts the connection so a long Gateway outage self-heals without a launcher restart.
    private async Task SuperviseAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_config.Url.TrimEnd('/') + "/launcher-stream", options =>
            {
                var token = _config.Token;
                if (!string.IsNullOrEmpty(token))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                // Local-name-friendly dialing (see GatewayHttp): a gateway named soren_north.local
                // resolves to a link-local IPv6 address first, and the default connect hangs on it.
                // The handler covers negotiate and the fallback transports; the websocket factory
                // covers the websocket itself, which dials outside that handler by default.
                options.HttpMessageHandlerFactory = _ => GatewayHttp.Handler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await GatewayHttp.ConnectWebSocketAsync(context.Uri, token, cancellationToken);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            })
            .Build();

        _connection.Reconnecting += ex => { FileLog.Write($"[LauncherStreamClient] reconnecting: {ex?.Message}"); return Task.CompletedTask; };
        _connection.Reconnected += async _ => await SayHelloAsync();

        // The production down-channel. The Gateway invokes "Command" with a LauncherCommand and awaits the
        // LauncherCommandResult over the same connection (SignalR client results). This handler is a
        // boundary, so it catches: a dispatch fault becomes a typed Error result the Gateway reports to
        // its caller, never a faulted hub invocation.
        _connection.On<LauncherCommand, LauncherCommandResult>("Command", cmd => DispatchAsync(cmd));

        while (!_disposed)
        {
            if (await TryConnectAsync())
            {
                await SayHelloAsync();
                await WaitUntilClosedAsync();      // returns when auto-reconnect has exhausted its attempts
            }
            if (_disposed) break;
            await Task.Delay(RestartDelay);        // long-outage restart
        }
    }

    /// <summary>
    /// Execute one command through the shared supervisor/launch actions. A boundary: any fault becomes an
    /// Error result.
    ///
    /// INTERNAL RATHER THAN PRIVATE so the tests can drive a real command through the real supervisor.
    /// Every decision this method makes - which verb honours onlyIfEmpty, what a refusal becomes on the
    /// wire - is invisible to a test that stops at the relay or starts at the supervisor, and a seam that
    /// nothing crosses is exactly where a flag goes missing without a single test turning red.
    /// </summary>
    internal async Task<LauncherCommandResult> DispatchAsync(LauncherCommand cmd)
    {
        try
        {
            if (cmd is null)
                return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "command is required");

            FileLog.Write($"[LauncherStreamClient] Command received: verb={cmd.Verb}, path={cmd.Path ?? "(none)"}, confirmProtected={cmd.ConfirmProtected}, onlyIfEmpty={cmd.OnlyIfEmpty}");

            // A CONDITION THIS VERB CANNOT HONOUR IS REFUSED HERE, AT THE BOUNDARY, rather than ignored.
            // onlyIfEmpty means "do not interrupt live work"; every verb below except the restart does
            // something else entirely with it, which is nothing. Dropping it silently would answer a
            // request to be careful with a success that was never careful, and this is the last place
            // that can tell - the Gateway route refuses it too, but it is not the only way a command can
            // arrive here.
            if (cmd.OnlyIfEmpty && cmd.Verb != "director/restart")
            {
                FileLog.Write($"[LauncherStreamClient] Command declined: onlyIfEmpty was sent with '{cmd.Verb}'");
                return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest,
                    $"onlyIfEmpty is understood by 'director/restart' alone, and this is '{cmd.Verb}'. "
                    + "Nothing was done: a condition that cannot be honoured is refused, never dropped.");
            }

            switch (cmd.Verb)
            {
                case "director/start":
                    _supervisor.Start();
                    return LauncherCommandResult.Ok();

                case "director/stop":
                    await _supervisor.StopAsync();
                    return LauncherCommandResult.Ok();

                case "director/restart":
                {
                    // ONLY-IF-EMPTY IS DECIDED HERE, IN THE LAUNCHER PROCESS, for the same reason the
                    // catalogue rule below is: it is the only place that can read how busy the Director
                    // actually is, and a guard the caller could route around is not a guard.
                    var outcome = await _supervisor.RestartAsync(cmd.OnlyIfEmpty);
                    switch (outcome.Verdict)
                    {
                        // NOTHING WAS DONE - the only outcome that may be called a refusal. It promises the
                        // machine is exactly as the caller left it, and the reason names the live count.
                        case DirectorRestartVerdict.Refused:
                            return LauncherCommandResult.Refuse(outcome.Reason ?? UnexplainedOutcome);

                        // SOMETHING WAS DONE AND IT DID NOT FINISH: the stop ran and nothing came back up.
                        // Reported as a fault, never as a refusal - a refusal would tell the caller their
                        // machine is untouched when it may now have no Director at all.
                        case DirectorRestartVerdict.NotStarted:
                            return LauncherCommandResult.Fail(LauncherCommandStatus.Error,
                                outcome.Reason ?? UnexplainedOutcome);

                        case DirectorRestartVerdict.Restarted:
                            // A GUARDED RESTART SAYS SO IN ITS ANSWER, in full: the condition it applied,
                            // the count it read, and that a Director really came back. An older launcher
                            // does not know this flag, ignores it, restarts a busy Director and answers
                            // with a bare OK - so a caller that needed the guarantee must be able to tell
                            // the two OKs apart, and the Gateway refuses to call a bare one guarded.
                            return cmd.OnlyIfEmpty
                                ? LauncherCommandResult.OkWithPayload(JsonSerializer.Serialize(
                                    new { ok = true, restarted = true, onlyIfEmpty = true, sessions = outcome.Sessions },
                                    JsonOptions))
                                : LauncherCommandResult.Ok();

                        default:
                            throw new InvalidOperationException(
                                $"the supervisor answered with a restart verdict this launcher does not "
                                + $"know how to report: {outcome.Verdict}.");
                    }
                }

                case "launch":
                {
                    // The catalogue resolution rule is enforced HERE, in the launcher process, so no relay
                    // arm or future caller can bypass what "start Chrome" is allowed to mean on this machine.
                    var (path, error) = _appCatalog.ResolveLaunchPath(cmd.Path, cmd.App);
                    if (error is not null)
                        return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, error);
                    _launchService.Launch(
                        new LaunchRequest { Path = path!, Args = cmd.Args, Cwd = cmd.Cwd, Headless = cmd.Headless },
                        caller: "launcher-stream");
                    return LauncherCommandResult.Ok();
                }

                // The query verbs. Their answers ride back in the result payload; see LauncherCommandResult.
                case "apps":
                    return LauncherCommandResult.OkWithPayload(
                        JsonSerializer.Serialize(_appCatalog.Search(cmd.Query, cmd.Limit), JsonOptions));

                case "files":
                    if (string.IsNullOrWhiteSpace(cmd.Query))
                        return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "query is required for a file search");
                    return LauncherCommandResult.OkWithPayload(
                        JsonSerializer.Serialize(
                            _fileSearch.Search(cmd.Query, cmd.Limit, cmd.TimeoutMilliseconds, CancellationToken.None),
                            JsonOptions));

                default:
                    FileLog.Write($"[LauncherStreamClient] Command declined (unknown verb): {cmd.Verb}");
                    return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, $"unknown verb: {cmd.Verb}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherStreamClient] Command FAILED: verb={cmd?.Verb}, error={ex.Message}");
            return LauncherCommandResult.Fail(LauncherCommandStatus.Error, ex.Message);
        }
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_connection is null) return false;
        try
        {
            await _connection.StartAsync();
            FileLog.Write($"[LauncherStreamClient] connected to {_config.Url}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherStreamClient] connect failed (will retry): {ex.Message}");
            return false;
        }
    }

    /// <summary>How long between Hello attempts while the connection is up. Short, because until Hello
    /// lands this launcher is invisible to every command, and long enough that a hub rejecting Hello
    /// repeatedly is not hammered.</summary>
    internal static readonly TimeSpan HelloRetryDelay = TimeSpan.FromSeconds(5);

    private Task SayHelloAsync() =>
        SendHelloUntilAcceptedAsync(
            () => _connection!.InvokeAsync("Hello", new LauncherStreamHello
            {
                MachineName = Environment.MachineName,
                Version = _version,
            }),
            () => _connection?.State ?? HubConnectionState.Disconnected,
            () => _disposed,
            HelloRetryDelay,
            CancellationToken.None);

    /// <summary>
    /// Send Hello, and KEEP SENDING IT for as long as the connection is up and it has not been accepted.
    ///
    /// WHY A RETRY HERE IS NOT A SECOND PATH. It is the same single path, tried again - nothing is dialed,
    /// nothing falls back, and there is no other way for this launcher to become reachable. The mission's
    /// no-fallback law forbids a SECOND mechanism behind a failing first one; this is the first mechanism
    /// insisting.
    ///
    /// WHY IT HAD TO CHANGE. Hello was sent once and its failure was swallowed with a note that
    /// auto-reconnect would retry - but retry only ever happened on Reconnected, and a hub or protocol
    /// error can fail Hello while SignalR stays CONNECTED. Nothing then fires Reconnected, so nothing
    /// retries: the launcher holds an open stream, is registered nowhere, and every command to it is
    /// undeliverable until some later disconnect that may never come. Independent inspection raised this
    /// as a hypothesis from the control flow; it was then reproduced against a real hub - see
    /// LauncherStreamIntegrationTests.A_failed_Hello_that_leaves_the_connection_up_registers_nothing_and_delivers_nothing.
    ///
    /// The worst outcome is now a delay rather than a permanent silence, and the loop cannot spin: it
    /// stops the moment the connection is no longer Connected, because the reconnect path owns that case
    /// and will call this again.
    /// </summary>
    internal static async Task SendHelloUntilAcceptedAsync(
        Func<Task> sendHello, Func<HubConnectionState> connectionState, Func<bool> disposed,
        TimeSpan retryDelay, CancellationToken ct)
    {
        var attempt = 0;
        while (!disposed() && !ct.IsCancellationRequested
               && connectionState() == HubConnectionState.Connected)
        {
            attempt++;
            try
            {
                await sendHello().ConfigureAwait(false);
                FileLog.Write($"[LauncherStreamClient] Hello sent: machine={Environment.MachineName}"
                              + (attempt > 1 ? $" (accepted on attempt {attempt})" : ""));
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherStreamClient] Hello attempt {attempt} FAILED: {ex.Message}. The "
                              + "connection is still up, so nothing else will retry this - until Hello is "
                              + $"accepted this launcher is registered nowhere and can receive no command. "
                              + $"Retrying in {retryDelay.TotalSeconds:0.#}s.");
            }

            try { await Task.Delay(retryDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task WaitUntilClosedAsync()
    {
        if (_connection is null) return;
        var closed = new TaskCompletionSource();
        Task OnClosed(Exception? _) { closed.TrySetResult(); return Task.CompletedTask; }
        _connection.Closed += OnClosed;
        try
        {
            if (_connection.State == HubConnectionState.Disconnected) return;
            await closed.Task;
        }
        finally
        {
            _connection.Closed -= OnClosed;
        }
    }

    public async Task StopAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[LauncherStreamClient] StopAsync error: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[LauncherStreamClient] DisposeAsync error: {ex.Message}"); }
            _connection = null;
        }
    }
}
