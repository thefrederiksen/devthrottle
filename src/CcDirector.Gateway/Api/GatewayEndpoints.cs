using System.Diagnostics;
using System.Net.Http.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

internal static class GatewayEndpoints
{
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client, string version, string token, bool authEnabled = false)
    {
        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";

        // ===== HTML pages =====
        app.MapGet("/", (HttpContext ctx) =>
        {
            var accept = ctx.Request.Headers["Accept"].ToString();
            if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new
                {
                    name = "CC Director Gateway",
                    version,
                    directors = registry.ListDirectors().Count,
                });
            }
            var html = EmbeddedResources.Load("manager.html")
                .Replace("__LOGOUT_VISIBILITY__", logoutVisibility);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/login", (HttpContext ctx) =>
        {
            var next = ctx.Request.Query["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";
            var html = EmbeddedResources.Load("login.html")
                .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                .Replace("__ERROR__", "");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var submitted = (form["token"].ToString() ?? "").Trim();
            var next = form["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";

            if (!string.Equals(submitted, token, StringComparison.Ordinal))
            {
                var html = EmbeddedResources.Load("login.html")
                    .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                    .Replace("__ERROR__", "Wrong token. Check gateway-token.txt and try again.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
                return;
            }

            ctx.Response.Cookies.Append(AuthMiddleware.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true,
            });
            ctx.Response.Redirect(IsSafeRedirect(next) ? next : "/");
        });

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(AuthMiddleware.CookieName);
            return Results.Redirect("/login");
        });

        // ===== REST =====
        app.MapGet("/healthz", async () =>
        {
            var directors = registry.ListDirectors();
            int totalSessions = 0;
            foreach (var d in directors)
            {
                var sessions = await client.ListSessionsAsync(d.ControlEndpoint);
                totalSessions += sessions?.Count ?? 0;
            }

            return Results.Json(new HealthDto
            {
                Status = "ok",
                Directors = directors.Count,
                Sessions = totalSessions,
                Version = version,
                ServerTime = DateTime.UtcNow,
            });
        });

        app.MapGet("/directors", () =>
        {
            return Results.Json(registry.ListDirectors());
        });

        app.MapGet("/sessions", async (string? director, string? agent, string? state) =>
        {
            var all = new List<SessionDto>();
            var directors = registry.ListDirectors();
            foreach (var d in directors)
            {
                if (!string.IsNullOrEmpty(director) && !string.Equals(d.DirectorId, director, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sessions = await client.ListSessionsAsync(d.ControlEndpoint);
                if (sessions is null) continue;

                foreach (var s in sessions)
                {
                    if (!string.IsNullOrEmpty(agent) && !string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(state) && !string.Equals(s.ActivityState, state, StringComparison.OrdinalIgnoreCase))
                        continue;
                    s.DirectorId = d.DirectorId;
                    all.Add(s);
                }
            }
            return Results.Json(all);
        });

        app.MapGet("/sessions/{sid}", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });
            session.DirectorId = director!.DirectorId;
            return Results.Json(session);
        });

        app.MapGet("/sessions/{sid}/buffer", async (string sid, int? lines, bool? raw, long? since) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });

            var buffer = await client.GetBufferAsync(director!.ControlEndpoint, sid, lines, raw == true, since);
            if (buffer is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            return Results.Json(buffer);
        });

        app.MapPost("/sessions/{sid}/prompt", async (string sid, PromptRequest req) =>
        {
            if (req is null || string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid}, director={director.DirectorId}, waitForIdle={req.WaitForIdle}");

            var (ok, body, err) = await client.PostPromptAsync(director.ControlEndpoint, sid, req);
            if (!ok || body is null)
                return Results.Json(new PromptResponse
                {
                    Accepted = false,
                    Error = err,
                    ActivityState = session.ActivityState,
                }, statusCode: StatusCodes.Status502BadGateway);

            if (!req.WaitForIdle)
                return Results.Json(body);

            var sw = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
            string finalState = body.ActivityState;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(750);
                var cur = await client.GetSessionAsync(director.ControlEndpoint, sid);
                if (cur is null) { finalState = "Exited"; break; }
                finalState = cur.ActivityState;
                if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
            }
            sw.Stop();

            // Fetch new output since prompt was sent
            string output = "";
            var buf = await client.GetBufferAsync(director.ControlEndpoint, sid, lines: 500, raw: false, since: body.BufferCursor);
            if (buf is not null) output = buf.Text;

            body.WaitStatus = finalState switch
            {
                "Idle" or "WaitingForInput" => "idle",
                "Exited" or "Failed" => "failed",
                _ => "timeout",
            };
            body.Output = output;
            body.ActivityState = finalState;
            return Results.Json(body);
        });

        app.MapPost("/sessions/{sid}/interrupt", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            var ok = await client.PostInterruptAsync(director.ControlEndpoint, sid);
            return ok
                ? Results.Json(new { accepted = true })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/repos", async (string id) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            var repos = await client.ListReposAsync(d.ControlEndpoint);
            if (repos is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(repos);
        });

        app.MapPost("/directors/{id}/sessions", async (string id, NewSessionRequest req) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions: repo={req.RepoPath}, agent={req.Agent}");
            var (ok, body, err) = await client.CreateSessionAsync(d.ControlEndpoint, req);
            if (!ok)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        app.MapDelete("/directors/{id}", async (string id, [FromBody] ShutdownDirectorRequest? body) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id}");
            var director = registry.Get(id);
            if (director is null)
                return Results.NotFound(new { error = "director not found" });

            body ??= new ShutdownDirectorRequest();
            var ok = await client.PostShutdownAsync(director.ControlEndpoint);
            if (ok) return Results.Json(new { accepted = true });

            if (body.Force)
            {
                try
                {
                    var proc = Process.GetProcessById(director.Pid);
                    proc.Kill(entireProcessTree: true);
                    return Results.Json(new { accepted = true, killed = true });
                }
                catch (Exception ex)
                {
                    return Results.Problem("could not kill process: " + ex.Message, statusCode: 500);
                }
            }

            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/fanout", async (FanoutRequest req) =>
        {
            if (req is null || req.SessionIds is null || req.SessionIds.Count == 0)
                return Results.BadRequest(new { error = "sessionIds is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            FileLog.Write($"[GatewayEndpoints] POST fanout: count={req.SessionIds.Count}, len={req.Text.Length}");

            var startedAt = DateTime.UtcNow;

            // Resolve all directors once up-front
            var directorBySession = new Dictionary<string, DirectorDto>();
            foreach (var sid in req.SessionIds)
            {
                var (d, s) = await LocateSessionAsync(registry, client, sid);
                if (d is not null && s is not null) directorBySession[sid] = d;
            }

            // Send to all in parallel
            var sendTasks = req.SessionIds.Select(async sid =>
            {
                var sw = Stopwatch.StartNew();
                if (!directorBySession.TryGetValue(sid, out var director))
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        Status = "not_found",
                        Error = "session not found",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                var promptReq = new PromptRequest { Text = req.Text, AppendEnter = req.AppendEnter };
                var (ok, body, err) = await client.PostPromptAsync(director.ControlEndpoint, sid, promptReq);
                if (!ok || body is null)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "failed",
                        Error = err,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                if (!req.WaitForIdle)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "idle",
                        Output = "",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                // Poll for idle
                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                string finalState = body.ActivityState;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(750);
                    var cur = await client.GetSessionAsync(director.ControlEndpoint, sid);
                    if (cur is null) { finalState = "Exited"; break; }
                    finalState = cur.ActivityState;
                    if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
                }

                // Get the diff
                var buf = await client.GetBufferAsync(director.ControlEndpoint, sid, lines: 500, raw: false, since: body.BufferCursor);
                var output = buf?.Text ?? "";

                sw.Stop();
                return new FanoutResult
                {
                    SessionId = sid,
                    DirectorId = director.DirectorId,
                    Status = finalState switch
                    {
                        "Idle" or "WaitingForInput" => "idle",
                        "Exited" or "Failed" => "failed",
                        _ => "timeout",
                    },
                    Output = output,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }).ToList();

            var results = await Task.WhenAll(sendTasks);

            return Results.Json(new FanoutResponse
            {
                Results = results.ToList(),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
            });
        });

        app.MapGet("/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            var ct = ctx.RequestAborted;
            var queue = System.Threading.Channels.Channel.CreateUnbounded<GatewayEvent>();

            void OnAdded(DirectorDto d) => queue.Writer.TryWrite(new GatewayEvent("director.added", d.DirectorId));
            void OnRemoved(string id) => queue.Writer.TryWrite(new GatewayEvent("director.removed", id));

            registry.OnDirectorAdded += OnAdded;
            registry.OnDirectorRemoved += OnRemoved;

            try
            {
                await foreach (var ev in queue.Reader.ReadAllAsync(ct))
                {
                    var line = $"event: {ev.Type}\ndata: {{\"id\":\"{ev.Id}\"}}\n\n";
                    await ctx.Response.WriteAsync(line, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                registry.OnDirectorAdded -= OnAdded;
                registry.OnDirectorRemoved -= OnRemoved;
            }
        });

        app.MapPost("/directors", async (LaunchDirectorRequest? body) =>
        {
            body ??= new LaunchDirectorRequest();
            FileLog.Write($"[GatewayEndpoints] POST director: launch new instance");

            var exePath = ResolveDirectorExe();
            if (exePath is null)
                return Results.Problem("cc-director.exe not found on PATH or in standard install location", statusCode: 500);

            var beforeIds = registry.ListDirectors().Select(d => d.DirectorId).ToHashSet();

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem("failed to start director: " + ex.Message, statusCode: 500);
            }

            // Poll for new director registration
            var deadline = DateTime.UtcNow.AddMilliseconds(body.TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var newId = registry.ListDirectors().Select(d => d.DirectorId).FirstOrDefault(id => !beforeIds.Contains(id));
                if (newId is not null)
                {
                    var d = registry.Get(newId)!;
                    return Results.Json(new { directorId = d.DirectorId, pid = d.Pid });
                }
            }

            return Results.Problem("director did not register within timeout", statusCode: 504);
        });
    }

    private static async Task<(DirectorDto? director, SessionDto? session)> LocateSessionAsync(DirectorRegistry registry, DirectorEndpointClient client, string sid)
    {
        foreach (var d in registry.ListDirectors())
        {
            var s = await client.GetSessionAsync(d.ControlEndpoint, sid);
            if (s is not null) return (d, s);
        }
        return (null, null);
    }

    private static string? ResolveDirectorExe()
    {
        // 1) Same directory as the running gateway
        var gatewayDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "cc-director.exe", "cc-director" })
        {
            var candidate = Path.Combine(gatewayDir, name);
            if (File.Exists(candidate)) return candidate;
        }

        // 2) Standard install location
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bin = Path.Combine(localAppData, "cc-director", "bin");
        foreach (var name in new[] { "cc-director.exe", "cc-director" })
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal sealed record GatewayEvent(string Type, string Id);

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }
}
