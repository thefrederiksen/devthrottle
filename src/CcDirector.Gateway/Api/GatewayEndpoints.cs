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
        // Phase 1: the canonical "/" is the directory page. The legacy aggregator
        // manager UI is still reachable at "/legacy-manager" for the embedded
        // Avalonia ManagerView until it migrates to per-Director direct calls.
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
            var html = EmbeddedResources.Load("directory.html")
                .Replace("__LOGOUT_VISIBILITY__", logoutVisibility);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/legacy-manager", (HttpContext ctx) =>
        {
            var html = EmbeddedResources.Load("manager.html")
                .Replace("__LOGOUT_VISIBILITY__", logoutVisibility);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/api", () =>
        {
            var html = EmbeddedResources.Load("api.html");
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

        // ===== HTTP discovery (Phase 1) =====
        // The Director POSTs /directors/register on startup and heartbeats every 15 s.
        // On graceful shutdown it DELETEs its registration. Same-machine Directors that
        // don't have gateway.url configured continue to be discovered via the filesystem
        // watch path - both paths coexist permanently.

        app.MapPost("/directors/register", (DirectorRegistrationRequest req) =>
        {
            if (req is null || string.IsNullOrEmpty(req.DirectorId))
                return Results.BadRequest(new { error = "directorId is required" });
            if (string.IsNullOrEmpty(req.TailnetEndpoint))
                return Results.BadRequest(new { error = "tailnetEndpoint is required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/register: id={req.DirectorId}, endpoint={req.TailnetEndpoint}, machine={req.MachineName}");
            var dto = registry.Upsert(req);
            return Results.Json(dto, statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/directors/{id}/heartbeat", (string id) =>
        {
            var ok = registry.Heartbeat(id);
            if (!ok)
            {
                FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/heartbeat: unknown id (caller should re-register)");
                // 410 Gone tells the Director "you're not in the registry anymore" so its
                // client can re-POST /directors/register instead of just retrying heartbeats.
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            return Results.Json(new { ok = true });
        });

        app.MapDelete("/directors/{id}/registration", (string id) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /directors/{id}/registration");
            var removed = registry.Remove(id);
            return removed
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "director not found" });
        });

        // Fleet-wide read aggregator. Fans out in parallel to every registered Director,
        // stamps each returned SessionDto with the owning Director's machine name, user,
        // tailnet endpoint, and a full deep-link ViewUrl. Failed Directors do not poison
        // the response: by default they're silently skipped (backward-compat flat list);
        // with ?envelope=true they're surfaced in machineErrors so the UI can render an
        // inline "unreachable" placeholder.
        app.MapGet("/sessions", async (HttpContext ctx, string? director, string? agent, string? state,
                                       string? statusColor, string? machine,
                                       bool? includeExited, string? q, bool? envelope) =>
        {
            var directors = registry.ListDirectors()
                .Where(d => string.IsNullOrEmpty(director) || string.Equals(d.DirectorId, director, StringComparison.OrdinalIgnoreCase))
                .Where(d => string.IsNullOrEmpty(machine) || string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var includeExitedActual = includeExited ?? false;
            var fanoutTasks = directors.Select(async d =>
            {
                var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
                var (sessions, error) = await client.ListSessionsWithStatusAsync(ep, includeExitedActual);
                return (Director: d, Sessions: sessions, Error: error);
            }).ToList();

            var results = await Task.WhenAll(fanoutTasks);

            var all = new List<SessionDto>();
            var machineErrors = new List<MachineErrorDto>();

            foreach (var (d, sessions, error) in results)
            {
                if (error is not null)
                {
                    machineErrors.Add(new MachineErrorDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName,
                        Error = error,
                    });
                    continue;
                }
                if (sessions is null) continue;

                var baseUrl = DeriveDirectorBaseUrl(ctx, d);
                foreach (var s in sessions)
                {
                    if (!string.IsNullOrEmpty(agent) && !string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(state) && !string.Equals(s.ActivityState, state, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(statusColor) && !string.Equals(s.StatusColor, statusColor, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!includeExitedActual && string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(q))
                    {
                        var needle = q;
                        var nameHit = !string.IsNullOrEmpty(s.Name) && s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        var repoHit = !string.IsNullOrEmpty(s.RepoPath) && s.RepoPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        if (!nameHit && !repoHit) continue;
                    }

                    s.DirectorId = d.DirectorId;
                    s.MachineName = d.MachineName;
                    s.User = d.User;
                    s.TailnetEndpoint = baseUrl;
                    s.ViewUrl = $"{baseUrl}/sessions/{s.SessionId}/view";
                    all.Add(s);
                }
            }

            if (envelope == true)
            {
                return Results.Json(new { sessions = all, machineErrors });
            }
            return Results.Json(all);
        });

        app.MapGet("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var baseUrl = DeriveDirectorBaseUrl(ctx, director);
            session.DirectorId = director.DirectorId;
            session.MachineName = director.MachineName;
            session.User = director.User;
            session.TailnetEndpoint = baseUrl;
            session.ViewUrl = $"{baseUrl}/sessions/{session.SessionId}/view";
            return Results.Json(session);
        });

        // Phase 4b: forward supervisor observability through the Gateway so the merged
        // Session View on the gateway side can render WHY a dot is the color it is.
        app.MapGet("/sessions/{sid}/supervisor", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var view = await client.GetSupervisorAsync(ep, sid);
            if (view is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(view);
        });

        // Phase 5: forward "ask the supervisor" calls. Each is one fresh Haiku side-call.
        app.MapPost("/sessions/{sid}/supervisor/ask", async (string sid, SupervisorAskRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Question))
                return Results.BadRequest(new SupervisorAskResult { Status = "bad_request", Error = "question is required" });
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var result = await client.AskSupervisorAsync(ep, sid, req, ct);
            if (result is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(result);
        });

        app.MapPatch("/sessions/{sid}", async (string sid, SessionUpdateRequest req) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "request body is required" });

            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] PATCH /sessions/{sid}: name=\"{req.Name}\", director={director.DirectorId}");

            var (ok, body, err) = await client.PatchSessionAsync(director.ControlEndpoint, sid, req);
            if (!ok || body is null)
                return Results.Problem(err ?? "patch failed", statusCode: StatusCodes.Status502BadGateway);

            body.DirectorId = director.DirectorId;
            return Results.Json(body);
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

        app.MapGet("/sessions/{sid}/summary", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var summary = await client.GetSummaryAsync(director!.ControlEndpoint, sid);
            if (summary is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            summary.DirectorId = director.DirectorId;
            return Results.Json(summary);
        });

        // Recap proxy. Both endpoints transparently forward to whichever Director owns the
        // session. The Director side does the heavy lifting (claude --print + cache); this
        // is just routing.
        app.MapGet("/sessions/{sid}/recap", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var recap = await client.GetRecapAsync(director.ControlEndpoint, sid);
            if (recap is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(recap);
        });

        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var model = ctx.Request.Query["model"].ToString();
            FileLog.Write($"[GatewayEndpoints] POST /recap: sid={sid}, director={director.DirectorId}, model={model ?? "(default)"}");
            var (ok, body, err) = await client.PostRecapAsync(director.ControlEndpoint, sid, model, ctx.RequestAborted);
            if (!ok || body is null)
                return Results.Problem(err ?? "recap failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        app.MapPost("/handover", async (HandoverRequest req) =>
        {
            // Gateway-side /handover dispatches to whichever Director owns the source
            // session. Same-Director case: proxy the request to that Director. Cross-Director
            // case (toDirectorId set + different from source): read the prose context from
            // source-side, then spawn the target session on the target Director with the
            // context as PrePrompt.

            if (req is null || string.IsNullOrEmpty(req.FromSessionId))
                return Results.BadRequest(new { error = "fromSessionId is required" });
            if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "exactly one of toSessionId or toRepoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /handover: from={req.FromSessionId} toSid={req.ToSessionId} toRepo={req.ToRepoPath} toDir={req.ToDirectorId}");

            var (sourceDirector, sourceSession) = await LocateSessionAsync(registry, client, req.FromSessionId);
            if (sourceSession is null || sourceDirector is null)
                return Results.NotFound(new { error = "source session not found across any director" });

            DirectorDto? targetDirector = null;
            if (!string.IsNullOrEmpty(req.ToDirectorId)
                && !string.Equals(req.ToDirectorId, sourceDirector.DirectorId, StringComparison.OrdinalIgnoreCase))
            {
                targetDirector = registry.Get(req.ToDirectorId);
                if (targetDirector is null)
                    return Results.NotFound(new { error = "target director not found" });
            }

            if (targetDirector is null)
            {
                // Same-Director: proxy the entire request.
                var (ok, body, err) = await client.PostHandoverAsync(sourceDirector.ControlEndpoint, req);
                if (!ok || body is null)
                    return Results.Problem(err ?? "handover failed", statusCode: StatusCodes.Status502BadGateway);
                if (body.TargetSession is not null) body.TargetSession.DirectorId = sourceDirector.DirectorId;
                return Results.Json(body, statusCode: 201);
            }

            // Cross-Director path. Only the "new session in target Director" form is supported here.
            if (!string.IsNullOrEmpty(req.ToSessionId))
                return Results.BadRequest(new { error = "cross-director handover to an existing session is not supported in v1; use toRepoPath instead" });
            if (string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "toRepoPath is required for cross-director handover" });

            string contextText;
            try
            {
                var ctxUrl = $"{sourceDirector.ControlEndpoint}/sessions/{req.FromSessionId}/handover-context";
                if (!string.IsNullOrEmpty(req.ExtraContext))
                    ctxUrl += "?extraContext=" + Uri.EscapeDataString(req.ExtraContext);
                using var ctxHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                contextText = await ctxHttp.GetStringAsync(ctxUrl);
            }
            catch (Exception ex)
            {
                return Results.Problem("failed to read handover-context from source director: " + ex.Message, statusCode: 502);
            }

            var spawnReq = new NewSessionRequest
            {
                RepoPath = req.ToRepoPath,
                Agent = req.ToAgent,
                PrePrompt = contextText,
            };
            using var spawnHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var spawnResp = await spawnHttp.PostAsJsonAsync($"{targetDirector.ControlEndpoint}/sessions", spawnReq);
            if (!spawnResp.IsSuccessStatusCode)
            {
                var body = await spawnResp.Content.ReadAsStringAsync();
                return Results.Problem($"target director returned {(int)spawnResp.StatusCode}: {body}", statusCode: 502);
            }
            var newSession = await spawnResp.Content.ReadFromJsonAsync<SessionDto>();
            if (newSession is not null) newSession.DirectorId = targetDirector.DirectorId;

            return Results.Json(new HandoverResponse
            {
                Accepted = true,
                TargetSession = newSession,
                ContextSent = contextText,
                ArchivedAt = null, // archive is written only on the source side; cross-director skips
            }, statusCode: 201);
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
                // --skip-workspace-picker so the spawned Director never blocks on the
                // workspace-selection modal at startup (the whole point of a programmatic
                // spawn is to skip user interaction).
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                };
                psi.ArgumentList.Add("--skip-workspace-picker");
                Process.Start(psi);
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
            var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
            var s = await client.GetSessionAsync(ep, sid);
            if (s is not null) return (d, s);
        }
        return (null, null);
    }

    // Build the externally-reachable base URL for a Director's web UI.
    //
    // Priority:
    //   1. If the Director explicitly registered a TailnetEndpoint, trust that.
    //   2. Else if the caller reached the Gateway over a non-loopback host
    //      (e.g. https://<host>.<tailnet>.ts.net/), mirror that host
    //      and the request scheme onto the Director's own Control API port.
    //      Tailscale Serve maps each Director port to the same number under
    //      HTTPS, so https://<tailnet>:<port>/ resolves correctly.
    //   3. Else fall back to the raw ControlEndpoint (loopback case).
    //
    // Without (2), ViewUrl returns http://127.0.0.1:<port>/... which is
    // unreachable from a phone or any non-loopback client.
    private static string DeriveDirectorBaseUrl(HttpContext ctx, DirectorDto d)
    {
        if (!string.IsNullOrEmpty(d.TailnetEndpoint))
            return d.TailnetEndpoint.TrimEnd('/');

        var requestHost = ctx.Request.Host.Host;
        var isLoopback = string.IsNullOrEmpty(requestHost)
                         || requestHost == "localhost"
                         || requestHost == "127.0.0.1"
                         || requestHost == "::1";

        if (!isLoopback
            && Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var controlUri)
            && controlUri.Port > 0)
        {
            return $"{ctx.Request.Scheme}://{requestHost}:{controlUri.Port}";
        }

        return (d.ControlEndpoint ?? "").TrimEnd('/');
    }

    private static string? ResolveDirectorExe()
    {
        var names = new[] { "cc-director.exe", "cc-director" };

        // 1) Same directory as the running gateway (production: same install dir)
        var gatewayDir = AppContext.BaseDirectory;
        foreach (var name in names)
        {
            var candidate = Path.Combine(gatewayDir, name);
            if (File.Exists(candidate)) return candidate;
        }

        // 2) Dev-build layout: when the gateway is running from
        //    src/CcDirector.Gateway/bin/<config>/<tfm>/, the freshly-built director sits at
        //    src/CcDirector.Avalonia/bin/<config>/<tfm>/cc-director.exe . Walk up four
        //    levels to find a sibling Avalonia/bin/<config>/<tfm>/.
        var dir = new DirectoryInfo(gatewayDir);
        // gatewayDir = .../src/CcDirector.Gateway/bin/<config>/<tfm>/
        // parent[0]  = .../src/CcDirector.Gateway/bin/<config>/
        // parent[1]  = .../src/CcDirector.Gateway/bin/
        // parent[2]  = .../src/CcDirector.Gateway/
        // parent[3]  = .../src/
        if (dir.Parent?.Parent?.Parent?.Parent is { } srcRoot)
        {
            var tfm = dir.Name;
            var cfg = dir.Parent.Name;
            var avaloniaCandidate = Path.Combine(srcRoot.FullName, "CcDirector.Avalonia", "bin", cfg, tfm);
            foreach (var name in names)
            {
                var candidate = Path.Combine(avaloniaCandidate, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3) Standard install location (only used when nothing better was found)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bin = Path.Combine(localAppData, "cc-director", "bin");
        foreach (var name in names)
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
