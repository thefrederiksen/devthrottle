using System.Diagnostics;
using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's Control API endpoints onto the provided IEndpointRouteBuilder.
/// Now serves both REST JSON and a self-contained HTML Manager UI.
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version, Func<Task> requestShutdownAsync, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null)
    {
        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";
        // ===== Healthz =====
        app.MapGet("/healthz", () => Results.Json(new HealthDto
        {
            Status = "ok",
            Directors = 1,
            Sessions = sessionManager.ListSessions().Count,
            Version = version,
            ServerTime = DateTime.UtcNow,
            DirectorId = directorId,
            MachineName = Environment.MachineName,
        }));

        // ===== HTML pages =====
        app.MapGet("/", (HttpContext ctx) =>
        {
            // If browser asks for JSON, give them session list (handy for curl users)
            if (!DirectorAuth.PrefersHtml(ctx))
                return Results.Json(sessionManager.ListSessions().Select(s => Map(s, directorId)).ToList());

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

            var actual = DirectorAuth.LoadOrCreateToken();
            if (!string.Equals(submitted, actual, StringComparison.Ordinal))
            {
                var html = EmbeddedResources.Load("login.html")
                    .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                    .Replace("__ERROR__", "Wrong token. Check gateway-token.txt and try again.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
                return;
            }

            ctx.Response.Cookies.Append(DirectorAuth.CookieName, actual, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true,
                // No Secure flag - we're on plain HTTP (loopback/Tailscale).
            });
            ctx.Response.Redirect(IsSafeRedirect(next) ? next : "/");
        });

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(DirectorAuth.CookieName);
            return Results.Redirect("/login");
        });

        app.MapGet("/sessions/{sid}/view", (HttpContext ctx, string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });
            var html = EmbeddedResources.Load("session-view.html").Replace("__SID__", sid);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ===== REST: Sessions =====
        app.MapGet("/sessions", () =>
        {
            var sessions = sessionManager.ListSessions()
                .Select(s => Map(s, directorId))
                .ToList();
            return Results.Json(sessions);
        });

        app.MapGet("/sessions/{sid}", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            return Results.Json(Map(session, directorId));
        });

        app.MapGet("/sessions/{sid}/buffer", (string sid, int? lines, bool? raw, long? since) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var buffer = session.Buffer;
            if (buffer is null)
                return Results.Json(new BufferResponse { SessionId = sid });

            byte[] bytes;
            long newCursor;
            if (since is long pos && pos >= 0)
            {
                var (data, np) = buffer.GetWrittenSince(pos);
                bytes = data;
                newCursor = np;
            }
            else
            {
                bytes = buffer.DumpAll();
                newCursor = buffer.TotalBytesWritten;
            }

            string text;
            if (raw == true)
                text = Encoding.UTF8.GetString(bytes);
            else
            {
                text = AnsiCleaner.Clean(bytes);
                if (lines is int n && n > 0)
                    text = AnsiCleaner.LastLines(text, n);
            }

            return Results.Json(new BufferResponse
            {
                SessionId = sid,
                TotalBytes = buffer.TotalBytesWritten,
                NewCursor = newCursor,
                Text = text,
            });
        });

        app.MapPost("/sessions/{sid}/prompt", async (string sid, PromptRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST prompt: sid={sid}, len={req?.Text?.Length ?? 0}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            if (req is null || string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
                return Results.StatusCode(StatusCodes.Status409Conflict);

            var bufferCursor = session.Buffer?.TotalBytesWritten ?? 0;

            if (req.AppendEnter)
                await session.SendTextAsync(req.Text);
            else
                session.SendInput(Encoding.UTF8.GetBytes(req.Text));

            FileLog.Write($"[ControlEndpoints] POST prompt OK: sid={sid}, cursor={bufferCursor}");

            return Results.Json(new PromptResponse
            {
                Accepted = true,
                SentAt = DateTime.UtcNow,
                BufferCursor = bufferCursor,
                ActivityState = session.ActivityState.ToString(),
            });
        });

        app.MapPost("/sessions/{sid}/interrupt", (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST interrupt: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            session.SendInput(new byte[] { 0x03 });
            return Results.Json(new { accepted = true });
        });

        // ===== REST: Fan-out within this Director =====
        app.MapPost("/fanout-local", async (FanoutRequest req) =>
        {
            if (req is null || req.SessionIds is null || req.SessionIds.Count == 0)
                return Results.BadRequest(new { error = "sessionIds is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            FileLog.Write($"[ControlEndpoints] POST fanout-local: count={req.SessionIds.Count}, len={req.Text.Length}");

            var startedAt = DateTime.UtcNow;

            var tasks = req.SessionIds.Select(async sid =>
            {
                var sw = Stopwatch.StartNew();
                if (!Guid.TryParse(sid, out var guid))
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, Status = "not_found", Error = "invalid guid", ElapsedMs = sw.ElapsedMilliseconds };
                }
                var session = sessionManager.GetSession(guid);
                if (session is null)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, Status = "not_found", Error = "session not found", ElapsedMs = sw.ElapsedMilliseconds };
                }

                var cursor = session.Buffer?.TotalBytesWritten ?? 0;
                try
                {
                    if (req.AppendEnter)
                        await session.SendTextAsync(req.Text);
                    else
                        session.SendInput(Encoding.UTF8.GetBytes(req.Text));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = "failed", Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
                }

                if (!req.WaitForIdle)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = "idle", Output = "", ElapsedMs = sw.ElapsedMilliseconds };
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(750);
                    var st = session.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput or ActivityState.Exited) break;
                }

                string output = "";
                if (session.Buffer is not null)
                {
                    var (data, _) = session.Buffer.GetWrittenSince(cursor);
                    output = AnsiCleaner.LastLines(AnsiCleaner.Clean(data), 500);
                }

                sw.Stop();
                var final = session.ActivityState;
                var status = final switch
                {
                    ActivityState.Idle or ActivityState.WaitingForInput => "idle",
                    ActivityState.Exited => "failed",
                    _ => "timeout",
                };
                return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = status, Output = output, ElapsedMs = sw.ElapsedMilliseconds };
            }).ToList();

            var results = await Task.WhenAll(tasks);

            return Results.Json(new FanoutResponse
            {
                Results = results.ToList(),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
            });
        });

        // ===== REST: Repos (for the New Session picker) =====
        app.MapGet("/repos", () =>
        {
            if (repositoryRegistry is null)
                return Results.Json(Array.Empty<RepositoryDto>());

            var repos = repositoryRegistry.Repositories
                .Select(r => new RepositoryDto
                {
                    Name = string.IsNullOrEmpty(r.Name) ? Path.GetFileName(r.Path.TrimEnd('\\', '/')) : r.Name,
                    Path = r.Path,
                    LastUsed = r.LastUsed,
                })
                .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
                .ToList();
            return Results.Json(repos);
        });

        // ===== REST: Create a session =====
        app.MapPost("/sessions", (NewSessionRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions: repo={req?.RepoPath}, agent={req?.Agent}");

            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            if (!Directory.Exists(req.RepoPath))
                return Results.BadRequest(new { error = $"repoPath does not exist: {req.RepoPath}" });

            if (!Enum.TryParse<AgentKind>(req.Agent, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = $"unknown agent: {req.Agent}. Valid: ClaudeCode, Pi, Codex, Gemini" });

            IAgent agent = kind switch
            {
                AgentKind.ClaudeCode => new ClaudeAgent(sessionManager.Options),
                AgentKind.Pi => new PiAgent(sessionManager.Options),
                AgentKind.Codex => new CodexAgent(sessionManager.Options),
                AgentKind.Gemini => new GeminiAgent(sessionManager.Options),
                _ => throw new InvalidOperationException("unreachable"),
            };

            Session session;
            try
            {
                session = sessionManager.CreateSession(
                    req.RepoPath,
                    agent,
                    req.Args,
                    SessionBackendType.ConPty,
                    resumeSessionId: null);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] POST /sessions FAILED: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }

            // Notify any listeners (Avalonia UI subscribes to update its sidebar)
            sessionManager.RaiseSessionCreated(session);

            return Results.Json(Map(session, directorId), statusCode: 201);
        });

        // ===== REST: Kill a session =====
        app.MapDelete("/sessions/{sid}", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE /sessions/{sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            try
            {
                await sessionManager.KillSessionAsync(guid);
                return Results.Json(new { killed = true });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "session not found" });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] DELETE FAILED: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ===== Shutdown =====
        app.MapPost("/shutdown", () =>
        {
            FileLog.Write($"[ControlEndpoints] POST shutdown requested");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlEndpoints] Shutdown FAILED: {ex.Message}"); }
            });
            return Results.Json(new { accepted = true });
        });
    }

    private static SessionDto Map(Session s, string directorId) => new()
    {
        SessionId = s.Id.ToString(),
        DirectorId = directorId,
        Agent = s.AgentKind.ToString(),
        RepoPath = s.RepoPath,
        Status = s.Status.ToString(),
        ActivityState = s.ActivityState.ToString(),
        CreatedAt = s.CreatedAt.UtcDateTime,
        TotalBufferBytes = s.Buffer?.TotalBytesWritten ?? 0,
        BackendType = s.BackendType.ToString(),
        Name = null,
    };

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }
}
