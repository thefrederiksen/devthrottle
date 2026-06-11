using System.Net;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CcDirector.Launcher;

/// <summary>
/// Hosts the Launcher's loopback REST API on 127.0.0.1:<see cref="Port"/>.
///
/// Endpoints (all require Bearer token except /healthz):
///   GET  /healthz         -> {ok, version, pid, uptimeS}
///   GET  /status          -> launcher info + director status + launched pids
///   POST /launch          -> {path, args?, cwd?, headless?} -> {ok, pid}
///   POST /director/start  -> start installed Director
///   POST /director/stop   -> stop installed Director
///   POST /director/restart -> restart installed Director
///   POST /shutdown        -> quit the launcher
///
/// Discovery: writes {port, token, pid} to
///   %LOCALAPPDATA%/cc-director/config/launcher/launcher.json
/// on startup so an agent/CLI can find it.
/// </summary>
public sealed class LauncherHost : IAsyncDisposable
{
    private readonly int _port;
    private readonly LaunchService _launchService;
    private readonly DirectorSupervisor _directorSupervisor;
    private readonly Func<Task> _requestShutdownAsync;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly string _version;

    private WebApplication? _app;
    private string? _token;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public int Port => _port;

    public LauncherHost(int port, LaunchService launchService, DirectorSupervisor directorSupervisor, Func<Task> requestShutdownAsync, string version = "0.0.0")
    {
        _port = port;
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _directorSupervisor = directorSupervisor ?? throw new ArgumentNullException(nameof(directorSupervisor));
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _version = version;
    }

    /// <summary>Start Kestrel, load/generate the token, write the discovery file.</summary>
    public async Task StartAsync()
    {
        FileLog.Write($"[LauncherHost] StartAsync: port={_port}");

        _token = LauncherAuth.LoadOrCreateToken();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.Launcher",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Access log + error envelope for mutating requests.
        _app.Use(async (ctx, next) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await next(); }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherHost] pipeline exception: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                }
            }
            finally
            {
                sw.Stop();
                var method = ctx.Request.Method;
                if (method is "POST" or "PUT" or "PATCH" or "DELETE" || ctx.Response.StatusCode >= 400)
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[LauncherHost] {method} {ctx.Request.Path} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client}");
                }
            }
        });

        // Token auth middleware.
        var token = _token;
        _app.Use((ctx, next) => LauncherAuth.Run(ctx, token, next));

        _app.UseRouting();

        MapEndpoints(_app);

        await _app.StartAsync();

        WriteDiscoveryFile();

        FileLog.Write($"[LauncherHost] Kestrel listening on http://127.0.0.1:{_port} (loopback only)");
    }

    private void MapEndpoints(WebApplication app)
    {
        // GET /healthz - public, no auth.
        app.MapGet("/healthz", () =>
        {
            var uptimeS = (long)(DateTime.UtcNow - _startedAt).TotalSeconds;
            return Results.Json(new
            {
                ok = true,
                version = _version,
                pid = Environment.ProcessId,
                uptimeS,
            }, JsonOpts);
        });

        // GET /status - launcher info + director running state + launched pids.
        app.MapGet("/status", () =>
        {
            var uptimeS = (long)(DateTime.UtcNow - _startedAt).TotalSeconds;
            return Results.Json(new
            {
                launcher = new
                {
                    pid = Environment.ProcessId,
                    port = _port,
                    version = _version,
                    uptimeS,
                    startedAtUtc = _startedAt,
                },
                director = new
                {
                    running = _directorSupervisor.IsRunning,
                    exeExists = _directorSupervisor.DirectorExeExists,
                    exePath = _directorSupervisor.DirectorExePath,
                },
                launchedPids = _launchService.LaunchedPids,
            }, JsonOpts);
        });

        // POST /launch - launch arbitrary app with clean parentage.
        app.MapPost("/launch", async (HttpContext ctx) =>
        {
            LaunchRequestDto? dto;
            try
            {
                dto = await ctx.Request.ReadFromJsonAsync<LaunchRequestDto>(JsonOpts, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync($"{{\"error\":\"invalid JSON: {ex.Message}\"}}");
                return;
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("{\"error\":\"path is required\"}");
                return;
            }

            var request = new LaunchRequest
            {
                Path = dto.Path,
                Args = dto.Args,
                Cwd = dto.Cwd,
                Headless = dto.Headless,
            };

            var pid = _launchService.Launch(request, caller: $"POST /launch from {ctx.Connection.RemoteIpAddress}");
            await ctx.Response.WriteAsJsonAsync(new { ok = true, pid }, JsonOpts);
        });

        // POST /director/start
        app.MapPost("/director/start", async (HttpContext ctx) =>
        {
            _directorSupervisor.Start();
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "started" }, JsonOpts);
        });

        // POST /director/stop
        app.MapPost("/director/stop", async (HttpContext ctx) =>
        {
            await _directorSupervisor.StopAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "stopped" }, JsonOpts);
        });

        // POST /director/restart
        app.MapPost("/director/restart", async (HttpContext ctx) =>
        {
            await _directorSupervisor.RestartAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "restarted" }, JsonOpts);
        });

        // POST /shutdown - quit the launcher.
        app.MapPost("/shutdown", async (HttpContext ctx) =>
        {
            FileLog.Write("[LauncherHost] /shutdown requested");
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, JsonOpts);
            _ = Task.Run(async () =>
            {
                await Task.Delay(100); // Let the response flush.
                await _requestShutdownAsync();
            });
        });
    }

    /// <summary>
    /// Write the discovery file so agents/CLIs can find the port and token.
    /// Path: %LOCALAPPDATA%/cc-director/config/launcher/launcher.json
    /// </summary>
    private void WriteDiscoveryFile()
    {
        try
        {
            var dir = CcStorage.ToolConfig("launcher");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "launcher.json");
            var json = JsonSerializer.Serialize(new
            {
                port = _port,
                token = _token,
                pid = Environment.ProcessId,
            }, JsonOpts);
            File.WriteAllText(path, json);
            FileLog.Write($"[LauncherHost] Discovery file written: {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherHost] WriteDiscoveryFile FAILED: {ex.Message}");
        }
    }

    /// <summary>Remove the discovery file on shutdown.</summary>
    private void DeleteDiscoveryFile()
    {
        try
        {
            var path = Path.Combine(CcStorage.ToolConfig("launcher"), "launcher.json");
            if (File.Exists(path)) File.Delete(path);
            FileLog.Write("[LauncherHost] Discovery file removed");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherHost] DeleteDiscoveryFile FAILED: {ex.Message}");
        }
    }

    /// <summary>Stop Kestrel and remove the discovery file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        _disposed = true;
        FileLog.Write("[LauncherHost] StopAsync");

        DeleteDiscoveryFile();

        if (_app is not null)
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _app.StopAsync(cts.Token); }
            catch (Exception ex) { FileLog.Write($"[LauncherHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>JSON body for POST /launch.</summary>
internal sealed class LaunchRequestDto
{
    public string? Path { get; init; }
    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }
}
