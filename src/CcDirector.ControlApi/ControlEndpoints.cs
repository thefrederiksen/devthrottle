using System.Diagnostics;
using System.Text;
using CcDirector.ControlApi.Chat;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's Control API endpoints onto the provided IEndpointRouteBuilder.
/// Now serves both REST JSON and a self-contained HTML Director UI.
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version, Func<Task> requestShutdownAsync, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, TurnSummaryCache? turnSummaryCache = null, string? gatewayUrl = null, ProactiveExplainService? proactiveExplain = null, GatewayConnectionMonitor? gatewayMonitor = null)
    {
        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";
        // URL of the Gateway this Director is registered with, for the "Gateway" nav
        // button in the served HTML. Empty when no gateway.url is configured -- the
        // pages hide the button rather than render a dead link.
        var gatewayUrlAttr = System.Net.WebUtility.HtmlEncode(gatewayUrl ?? "");
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

        // ===== Two-way handshake callback (issues #223/#224) =====
        // The Gateway dials this with the nonce the Director just POSTed to its
        // /directors/{id}/verify. Deliberately NOT in DirectorAuth.PublicPaths: the
        // handshake must prove the SAME authenticated channel real Gateway traffic uses -
        // a token mismatch should fail verification loudly, not be bypassed by it.
        // Echoing the Director id lets the Gateway catch an advertised URL that reaches
        // the wrong process; recording the receipt lets THIS side independently confirm
        // the callback landed here (the anti-impostor cross-check in GatewayClient).
        app.MapGet("/verify/{nonce}", (string nonce) => Results.Json(new VerifyCallbackDto
        {
            DirectorId = directorId,
            Nonce = nonce,
            Known = gatewayMonitor?.RecordCallback(nonce) ?? false,
        }));

        // ===== HTML pages =====
        // The cards-grid Director (manager.html) is the default UI at "/" -- the
        // multi-session directory the phone lands on. The old text-only "Director
        // chat" screen was removed; per-session messaging lives in the session view.
        app.MapGet("/", (HttpContext ctx) =>
        {
            // If browser asks for JSON, give them session list (handy for curl users)
            if (!DirectorAuth.PrefersHtml(ctx))
                return Results.Json(sessionManager.ListSessions().Select(s => Map(s, directorId, turnSummaryCache)).ToList());

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
            var shortSid = sid.Substring(0, Math.Min(8, sid.Length));
            var html = EmbeddedResources.Load("session-view.html")
                .Replace("__SID__", sid)
                .Replace("__SHORT_SID__", shortSid)
                .Replace("__GATEWAY_URL__", gatewayUrlAttr);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ===== REST: Sessions =====
        // Phase 3: Exited sessions are hidden by default. They aren't a "color" on
        // the directory map - if a session is gone, its card/row disappears. History
        // tooling can opt in via ?includeExited=true.
        app.MapGet("/sessions", (bool? includeExited) =>
        {
            var includeExitedActual = includeExited ?? false;
            var sessions = sessionManager.ListSessions()
                .Where(s => includeExitedActual || s.ActivityState != ActivityState.Exited)
                .Select(s => Map(s, directorId, turnSummaryCache))
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

            return Results.Json(Map(session, directorId, turnSummaryCache));
        });

        // Ask the wingman about this session. Two behaviors, both on the strong model:
        //   mode=explain -> terse "what's happening" briefing over pre-built context.
        //   free-text question -> the "Ask the Wingman" channel: a read-only full-power
        //   session (Read/Grep/Glob) over the whole terminal + repo that answers
        //   faithfully and reads content VERBATIM when asked, never summarizing.
        app.MapPost("/sessions/{sid}/wingman/ask", async (string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
            // Explain mode briefs the whole session and needs no user question; the
            // free-text ask path still requires one.
            if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
                return Results.BadRequest(new WingmanAskResult { Status = "bad_request", Error = "question is required" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            // Explain = the terse "what's happening" briefing (Opus over pre-built,
            // length-capped context). Unchanged.
            if (explain)
            {
                var explainCtx = await WingmanContextBuilder.BuildAsync(session, turnSummaryCache, ct);
                var explainResult = await Core.Wingman.WingmanService.AskAboutSessionAsync(
                    req.Question, explainCtx, sessionManager.Options.ClaudePath, ct, explain: true);
                return Results.Json(explainResult);
            }

            // Any free-text question = the faithful "Ask the Wingman" channel: a
            // read-only full-power session over the WHOLE terminal + repo, on the strong
            // model, that reads content VERBATIM instead of summarizing. This replaces the
            // old terse one-shot ask (no more 1-3 sentence cap, no Haiku, no 4000-char tail).
            var fullTerminal = ReadFullCleanedBuffer(session);
            var result = await Core.Wingman.WingmanService.AnswerViaSessionAsync(
                req.Question, fullTerminal, session.AgentKind.ToString(), session.RepoPath,
                sessionManager.Options.ClaudePath, ct);
            return Results.Json(result);
        });

        // Structured-intent actuation (Path A): the Wingman looks at the session's live
        // screen + state, decides ONE action (type / send_keys / submit / none), and the
        // Director executes it. The decision runs on a tool-less strong-model side-call; the
        // model never gets a write tool - WingmanActionExecutor is the only thing that writes
        // to the PTY. Pass ?decideOnly=true to get the decision WITHOUT executing it (dry run).
        app.MapPost("/sessions/{sid}/wingman/act", async (string sid, bool? decideOnly, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new WingmanActResult { Status = WingmanActResult.StatusBadRequest, Error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            if (string.IsNullOrWhiteSpace(sessionManager.Options.ClaudePath))
                return Results.Json(new WingmanActResult { Status = WingmanActResult.StatusNoClaude, Error = "no claude CLI configured" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var context = await WingmanContextBuilder.BuildAsync(session, turnSummaryCache, ct);
                var action = await Core.Wingman.WingmanService.DecideSessionActionAsync(
                    context, sessionManager.Options.ClaudePath, ct);
                sw.Stop();

                WingmanActResult result;
                if (decideOnly == true)
                {
                    result = new WingmanActResult { Action = action.Action, Text = action.Text, Reason = action.Reason };
                    result.Keys.AddRange(action.Keys);
                }
                else
                {
                    result = Core.Wingman.WingmanActionExecutor.Execute(session, action);
                }
                result.Model = Core.Wingman.WingmanService.Model;
                result.LatencyMs = sw.ElapsedMilliseconds;
                FileLog.Write($"[ControlEndpoints] POST /wingman/act: session={guid} decideOnly={decideOnly == true} action={result.Action} performed={result.Performed} status={result.Status}");
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                FileLog.Write($"[ControlEndpoints] POST /wingman/act FAILED: session={guid}: {ex.Message}");
                return Results.Json(new WingmanActResult
                {
                    Status = WingmanActResult.StatusWingmanFailed,
                    Error = ex.Message,
                    Model = Core.Wingman.WingmanService.Model,
                    LatencyMs = sw.ElapsedMilliseconds,
                });
            }
        });

        // Mobile experience: the proactively-cached wingman briefing for this session.
        // Returns instantly (no LLM call) so a phone shows it the moment the view opens.
        // text is null when nothing has been cached yet (mobile mode off, or first turn
        // not finished). The phone falls back to the on-demand /wingman/ask in that case.
        app.MapGet("/sessions/{sid}/wingman/explain", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            return Results.Json(new
            {
                mobileMode = session.MobileMode,
                text = session.CachedExplainText,
                at = session.CachedExplainAt,
                model = session.CachedExplainModel,
                quickReplies = session.CachedQuickReplies,
                headline = session.CachedExplainHeadline,
                whatHappened = session.CachedExplainWhatHappened,
                longDescription = session.CachedExplainLongDescription,
                whatClaudeWants = session.CachedExplainWhatClaudeWants,
                say = session.CachedExplainSay,
            });
        });

        // Toggle mobile mode for a session. When turned on, kick off an immediate background
        // briefing so the cache is warm right away instead of waiting for the next turn-end.
        app.MapPost("/sessions/{sid}/mobile-mode", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<MobileModeRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            // Session (text) tab: Text when watching, Off when the phone navigates away. This is
            // the same gate as before (MobileMode is now derived from ViewMode), so proactive
            // briefings behave identically; we only also distinguish Voice from Text now.
            session.ViewMode = enabled ? MobileViewMode.Text : MobileViewMode.Off;
            FileLog.Write($"[ControlEndpoints] /mobile-mode: session={guid} enabled={enabled} viewMode={session.ViewMode}");
            if (enabled) proactiveExplain?.TriggerBackgroundExplain(session);
            return Results.Json(new { mobileMode = session.MobileMode });
        });

        // Toggle voice (in-car) mode for a session. The mobile Voice tab calls this on tab switch:
        // enabled -> Voice (the wingman will write spoken-friendly remarks); disabled -> Text (the
        // user left the Voice tab but the phone is still on the mobile app). Like /mobile-mode this
        // warms the briefing cache immediately so the phone has something to speak right away.
        app.MapPost("/sessions/{sid}/voice-mode", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<VoiceModeRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            session.ViewMode = enabled ? MobileViewMode.Voice : MobileViewMode.Text;
            FileLog.Write($"[ControlEndpoints] /voice-mode: session={guid} enabled={enabled} viewMode={session.ViewMode}");
            proactiveExplain?.TriggerBackgroundExplain(session);
            return Results.Json(new { voiceMode = session.VoiceMode, mobileMode = session.MobileMode });
        });

        // Park / un-park a session in the FIFO voice queue. The phone's FIFO mode calls this
        // when the user says "put this on hold": held sessions stay reported with their true
        // state and color, but the FIFO conductor skips them until they are taken off hold.
        // Empty body defaults to onHold=true (the common case is "hold this one").
        app.MapPost("/sessions/{sid}/hold", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var onHold = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<HoldRequest>();
                if (body is not null) onHold = body.OnHold;
            }
            catch { /* empty body -> default to hold */ }

            session.OnHold = onHold;
            FileLog.Write($"[ControlEndpoints] /hold: session={guid} onHold={onHold}");
            return Results.Json(new { onHold = session.OnHold });
        });

        // Toggle the Wingman experience for a session. Default ON for every session; users
        // turn it OFF on the per-session settings UI when they want a plain terminal with
        // no auto-explain and no Voice/Wingman tabs. When the toggle is flipped back ON we
        // kick off an immediate background briefing so the cache is warm right away. When
        // flipped OFF mid-flight we also clear IsExplaining so the dot doesn't stick on
        // Yellow waiting for the in-flight briefing to finish.
        // Empty body defaults to enabled=true (the common case is "turn it on").
        app.MapPost("/sessions/{sid}/wingman-enabled", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<WingmanEnabledRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            session.WingmanEnabled = enabled;
            FileLog.Write($"[ControlEndpoints] /wingman-enabled: session={guid} enabled={enabled}");
            if (enabled)
            {
                // Warm the cache so the Wingman tab has something to show on first open.
                proactiveExplain?.TriggerBackgroundExplain(session);
            }
            else
            {
                // Don't let a Yellow dot stick around for a session the user just turned off.
                session.IsExplaining = false;
            }
            return Results.Json(new { wingmanEnabled = session.WingmanEnabled });
        });

        // Resolve the session repo's GitHub "new issue" URL from its origin remote. The
        // Cockpit's session menu (#191) calls this because the repo lives on THIS Director's
        // machine - the browser cannot read the git config itself. 409 when the repo has no
        // GitHub origin (the menu shows the message verbatim).
        app.MapGet("/sessions/{sid}/github-urls", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            FileLog.Write($"[ControlEndpoints] GET /sessions/{guid}/github-urls: repo={session.RepoPath}");
            try
            {
                var url = GitHubUrls.BuildNewIssueUrl(session.RepoPath);
                return Results.Json(new { newIssueUrl = url });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[ControlEndpoints] github-urls FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        });

        // Mobile view-links: serve a local file INLINE so a phone can tap a link and VIEW
        // the file (HTML/PDF/image/text) in the browser, instead of getting a useless file
        // path it cannot open. Browser Back returns to the session.
        //
        // Security: per the solo-tailnet decision (see remote-experience-plan.md) there is
        // NO sandbox/allowed-roots restriction - the tailnet boundary is the only gate, and
        // the tailnet is the owner's own devices. Revisit (add auth/signed links) the moment
        // a non-owner device or second user joins the tailnet.
        app.MapGet("/file", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (!System.IO.File.Exists(path))
                return Results.NotFound(new { error = "file not found: " + path });

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var ctype = ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".pdf"            => "application/pdf",
                ".png"            => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"            => "image/gif",
                ".svg"            => "image/svg+xml",
                ".css"            => "text/css; charset=utf-8",
                ".js"             => "text/javascript; charset=utf-8",
                ".json"           => "application/json; charset=utf-8",
                ".csv"            => "text/csv; charset=utf-8",
                ".md" or ".txt" or ".log" => "text/plain; charset=utf-8",
                _                 => "application/octet-stream",
            };
            FileLog.Write($"[ControlEndpoints] GET /file: {path} ({ctype})");
            // No fileDownloadName -> served inline, so the browser renders it (not a download).
            return Results.File(path, ctype);
        });

        // Phase 4b: observability into the wingman. Returns current color + reason,
        // a timestamped log of recent decisions, and the latest TurnSummary if any.
        app.MapGet("/sessions/{sid}/wingman", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var events = session.RecentWingmanEvents;
            var latestSummary = turnSummaryCache?.GetForSession(session.Id).LastOrDefault();

            return Results.Json(new WingmanViewDto
            {
                SessionId = session.Id.ToString(),
                Name = session.CustomName,
                CurrentColor = session.StatusColor,
                CurrentReason = session.LastStatusReason,
                Since = events.Count > 0 ? events[0].At : (DateTime?)null,
                Events = events.Select(e => new WingmanEventDto
                {
                    At = e.At,
                    OldColor = e.OldColor,
                    NewColor = e.NewColor,
                    Reason = e.Reason,
                    Llm = e.Llm,
                }).ToList(),
                Actions = session.RecentWingmanActions.Select(a => new WingmanActionDto
                {
                    At = a.At,
                    Action = a.Action,
                    Detail = a.Detail,
                    Reason = a.Reason,
                }).ToList(),
                LatestTurnSummary = latestSummary,
                Goal = session.WingmanGoal,
                GoalSetAt = session.WingmanGoalSetAt,
                GoalState = session.WingmanGoalState,
                GoalReason = session.WingmanGoalReason,
                GoalEvaluatedAt = session.WingmanGoalEvaluatedAt,
                LastUserPrompt = session.LastUserPrompt,
                LastUserPromptAt = session.LastUserPromptAt,
            });
        });

        // Goal management: set (or clear) the session's stated goal. Setting a goal
        // kicks off an immediate background assessment so the verdict is warm. Pass an
        // empty/null goal to clear it and stop goal-tracking.
        app.MapPost("/sessions/{sid}/wingman/goal", (string sid, WingmanGoalRequest req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            session.SetWingmanGoal(req?.Goal);
            FileLog.Write($"[ControlEndpoints] POST /wingman/goal: session={guid} goal=\"{req?.Goal}\"");

            if (!string.IsNullOrWhiteSpace(req?.Goal) && turnSummaryCache is not null)
                _ = turnSummaryCache.AssessGoalNowAsync(guid);

            return Results.Json(new
            {
                goal = session.WingmanGoal,
                goalSetAt = session.WingmanGoalSetAt,
                goalState = session.WingmanGoalState,
            });
        });

        app.MapPatch("/sessions/{sid}", (string sid, SessionUpdateRequest req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            FileLog.Write($"[ControlEndpoints] PATCH /sessions/{sid}: name=\"{req?.Name}\"");

            if (!sessionManager.RenameSession(guid, req?.Name))
                return Results.NotFound(new { error = "session not found" });

            var session = sessionManager.GetSession(guid);
            return session is null
                ? Results.NotFound(new { error = "session not found" })
                : Results.Json(Map(session, directorId, turnSummaryCache));
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

        // ===== HTML snapshot of the terminal grid =====
        // The Avalonia terminal renders cleanly because it pipes the raw PTY
        // bytes through a real xterm-compatible VT emulator. The HTML "Raw
        // terminal" tab needs the same treatment, otherwise CR-overwrites and
        // status-bar redraws stack as junk lines. We expose the per-session
        // parser snapshot here as styled HTML; the client just swaps innerHTML.
        app.MapGet("/sessions/{sid}/buffer/html", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var (scrollbackHtml, gridHtml, scrollbackCount) = session.GetHtmlSnapshotSplit();
            return Results.Json(new
            {
                sessionId = sid,
                totalBytes = session.Buffer?.TotalBytesWritten ?? 0,
                scrollbackHtml,
                gridHtml,
                scrollbackCount,
                // Backward-compat: existing callers (and integration tests) read
                // .html as the concatenated stream. Keep this so a partial
                // client update doesn't break.
                html = scrollbackHtml + gridHtml,
            });
        });

        // The Gateway pushing its assessed state DOWN (issue #186 two-owner model): stored
        // as a display annotation only. Never fed into the detector; any real activity
        // change wipes it (see Session.SetActivityState).
        app.MapPost("/sessions/{sid}/assessment", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var req = await ctx.Request.ReadFromJsonAsync<AssessmentRequest>(ctx.RequestAborted);
            session.SetAssessedStateAnnotation(req?.AssessedState);
            FileLog.Write($"[ControlEndpoints] assessment: sid={sid} -> {req?.AssessedState ?? "(cleared)"}");
            return Results.Json(new { ok = true });
        });

        app.MapGet("/sessions/{sid}/turns", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var resp = new TurnsResponse
            {
                SessionId = sid,
                ClaudeSessionId = session.ClaudeSessionId,
            };

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                resp.Status = "no_session_id";
                resp.Error = "Session has not been linked to a Claude session id yet.";
                return Results.Json(resp);
            }

            try
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                resp.JsonlPath = jsonl;

                if (!File.Exists(jsonl))
                {
                    resp.Status = "no_jsonl";
                    resp.Error = $"JSONL file not found at {jsonl}";
                    return Results.Json(resp);
                }

                var messages = StreamMessageParser.ParseFile(jsonl);
                resp.LineCount = messages.Count;
                resp.Widgets = WidgetBuilder.BuildFromMessages(messages);
                resp.Status = "ok";
                return Results.Json(resp);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /turns FAILED: {ex.Message}");
                resp.Status = "parse_error";
                resp.Error = ex.Message;
                return Results.Json(resp);
            }
        });

        app.MapGet("/sessions/{sid}/summary", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var dto = new SessionSummaryDto
            {
                SessionId = sid,
                DirectorId = directorId,
                Agent = session.AgentKind.ToString(),
                RepoPath = session.RepoPath,
                ActivityState = session.ActivityState.ToString(),
                CreatedAt = session.CreatedAt.UtcDateTime,
            };

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                dto.Status = "no_session_id";
                dto.Error = "Session has not been linked to a Claude session id yet.";
                return Results.Json(dto);
            }

            try
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                if (!File.Exists(jsonl))
                {
                    dto.Status = "no_jsonl";
                    dto.Error = $"JSONL file not found at {jsonl}";
                    return Results.Json(dto);
                }

                var messages = StreamMessageParser.ParseFile(jsonl);
                var summary = SummaryBuilder.Build(messages);
                // Fold the structural fields we already filled into the freshly built one.
                summary.SessionId = sid;
                summary.DirectorId = directorId;
                summary.Agent = dto.Agent;
                summary.RepoPath = dto.RepoPath;
                summary.ActivityState = dto.ActivityState;
                summary.CreatedAt = dto.CreatedAt;
                summary.Status = "ok";
                return Results.Json(summary);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /summary FAILED: {ex.Message}");
                dto.Status = "parse_error";
                dto.Error = ex.Message;
                return Results.Json(dto);
            }
        });

        // ===== REST: Brief - the Cockpit's full-page session view (ASK / DID / NEEDS YOU) =====
        // Sourced from the Claude JSONL transcript, never the terminal screen. The DID bullets
        // and verbatim NEEDS-YOU extraction come from a cached OpenAI condensation (one call
        // per completed turn); the raw blocks are served even when the condenser is
        // unavailable - explicit degrade, never a blank page.
        // Plan: docs/plans/cockpit-brief-view.md
        app.MapGet("/sessions/{sid}/brief", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var resp = new BriefResponse
            {
                SessionId = sid,
                ActivityState = session.ActivityState.ToString(),
                CreatedAt = session.CreatedAt.UtcDateTime,
            };

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                resp.Status = "no_session_id";
                resp.Error = "Session has not been linked to a Claude session id yet.";
                return Results.Json(resp);
            }

            try
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                if (!File.Exists(jsonl))
                {
                    resp.Status = "no_jsonl";
                    resp.Error = $"JSONL file not found at {jsonl}";
                    return Results.Json(resp);
                }

                var messages = StreamMessageParser.ParseFile(jsonl);
                var widgets = WidgetBuilder.BuildFromMessages(messages);
                var extract = BriefBuilder.Extract(widgets);

                resp.TurnCount = extract.TurnCount;
                resp.Goal = TruncateForDisplay(extract.FirstUserPrompt, BriefBuilder.GoalMaxChars);
                resp.LastAsk = TruncateForDisplay(extract.LastUserPrompt, BriefBuilder.LastAskMaxChars);
                resp.FullReply = extract.LastAssistantText;
                resp.ReplyPending = extract.ReplyPending;

                // While the current turn's reply is missing from the transcript (mid-reply, or
                // blocked in an on-screen interactive prompt), do NOT condense or fall back:
                // the condensation would describe the PREVIOUS turn against the NEW ask, and a
                // fallback needs-you would quote the wrong reply. The client routes the user
                // to the Terminal tab instead.
                if (extract.LastAssistantText is not null && !extract.ReplyPending)
                {
                    var cached = BriefCache.TryGetCurrent(guid, extract.TurnCount);
                    if (cached is null)
                    {
                        using var condenser = BriefBuilder.TryCreate();
                        if (condenser is not null)
                        {
                            var c = await condenser.CondenseAsync(
                                extract.LastUserPrompt, extract.LastAssistantText, ctx.RequestAborted);
                            if (c is not null)
                            {
                                cached = new BriefCache.Entry
                                {
                                    AtTurnCount = extract.TurnCount,
                                    DidBullets = c.Bullets,
                                    NeedsYouVerbatim = c.NeedsYouVerbatim,
                                    Condenser = condenser.CondenserId,
                                    GeneratedAt = DateTime.UtcNow,
                                };
                                BriefCache.Set(guid, cached);
                            }
                        }
                    }

                    if (cached is not null)
                    {
                        resp.DidBullets = cached.DidBullets;
                        resp.Condenser = cached.Condenser;
                        resp.GeneratedAt = cached.GeneratedAt;
                        resp.NeedsYou = cached.NeedsYouVerbatim;
                        resp.NeedsYouSource = cached.NeedsYouVerbatim is null ? null : "model";
                    }

                    // Verbatim-by-construction fallback: the reply's final paragraph. Applied
                    // both when the condenser is unavailable AND when its extraction failed
                    // validation but the session is visibly waiting on the user.
                    if (resp.NeedsYou is null &&
                        session.ActivityState == ActivityState.WaitingForInput)
                    {
                        resp.NeedsYou = BriefBuilder.FallbackNeedsYou(extract.LastAssistantText);
                        resp.NeedsYouSource = resp.NeedsYou is null ? null : "fallback";
                    }
                }

                resp.Status = "ok";
                return Results.Json(resp);
            }
            catch (OperationCanceledException)
            {
                // Client navigated away mid-condensation. Normal.
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /brief FAILED: {ex.Message}");
                resp.Status = "parse_error";
                resp.Error = ex.Message;
                return Results.Json(resp);
            }
        });

        app.MapGet("/sessions/{sid}/handover-context", (string sid, string? extraContext) =>
        {
            // Return the plain-text prompt that would be sent to a target session on
            // POST /handover. Useful for clients (skills, UI) that want to preview or
            // edit the context before dispatching.
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            SessionSummaryDto summary;
            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                summary = new SessionSummaryDto
                {
                    SessionId = sid, DirectorId = directorId,
                    Agent = session.AgentKind.ToString(),
                    RepoPath = session.RepoPath,
                    ActivityState = session.ActivityState.ToString(),
                    CreatedAt = session.CreatedAt.UtcDateTime,
                };
            }
            else
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                summary = File.Exists(jsonl)
                    ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                    : new SessionSummaryDto();
                summary.SessionId = sid;
                summary.DirectorId = directorId;
                summary.Agent = session.AgentKind.ToString();
                summary.RepoPath = session.RepoPath;
                summary.ActivityState = session.ActivityState.ToString();
                summary.CreatedAt = session.CreatedAt.UtcDateTime;
            }

            var text = SummaryBuilder.FormatAsHandoverPrompt(summary, extraContext);
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        // ===== REST: Recap (cheap claude --print side-call, cached) =====
        // Two endpoints: GET returns whatever is in the cache (or status=not_cached),
        // POST regenerates and writes to cache. We never start a generation on GET
        // because GET should always be cheap and never trigger an API spend.
        app.MapGet("/sessions/{sid}/recap", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var cached = RecapCache.TryGet(guid);
            var currentTurns = ComputeTurnCount(session);

            if (cached is null)
            {
                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    CurrentTurnCount = currentTurns,
                    Status = "not_cached",
                    Error = "No recap has been generated yet. POST to /sessions/{sid}/recap to create one.",
                });
            }

            return Results.Json(new RecapResponse
            {
                SessionId = sid,
                Recap = cached.Recap,
                GeneratedAt = cached.GeneratedAt,
                AtTurnCount = cached.AtTurnCount,
                CurrentTurnCount = currentTurns,
                IsStale = currentTurns > cached.AtTurnCount,
                Model = cached.Model,
                ElapsedMs = cached.ElapsedMs,
                Status = "ok",
            });
        });

        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions/{sid}/recap");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var model = ctx.Request.Query["model"].ToString();
            if (string.IsNullOrWhiteSpace(model))
                model = RecapGenerator.DefaultModel;

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    Model = model,
                    Status = "no_session_id",
                    Error = "Session has not been linked to a Claude session id yet.",
                });
            }

            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl))
            {
                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    Model = model,
                    Status = "no_jsonl",
                    Error = $"JSONL file not found at {jsonl}",
                });
            }

            SessionSummaryDto summary;
            string digest;
            int currentTurns;
            try
            {
                var messages = StreamMessageParser.ParseFile(jsonl);
                summary = SummaryBuilder.Build(messages);
                summary.SessionId = sid;
                summary.DirectorId = directorId;
                summary.Agent = session.AgentKind.ToString();
                summary.RepoPath = session.RepoPath;
                summary.ActivityState = session.ActivityState.ToString();
                summary.CreatedAt = session.CreatedAt.UtcDateTime;
                digest = SummaryBuilder.FormatAsHandoverPrompt(summary);
                currentTurns = summary.TurnCount;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /recap digest build FAILED: {ex.Message}");
                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    Model = model,
                    Status = "generation_failed",
                    Error = "Failed to build session digest: " + ex.Message,
                });
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var recapText = await RecapGenerator.GenerateAsync(
                    digest, sessionManager.Options.ClaudePath, model, ctx.RequestAborted);
                sw.Stop();

                var entry = new RecapCache.Entry
                {
                    Recap = recapText,
                    GeneratedAt = DateTime.UtcNow,
                    AtTurnCount = currentTurns,
                    Model = model,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
                RecapCache.Set(guid, entry);

                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    Recap = entry.Recap,
                    GeneratedAt = entry.GeneratedAt,
                    AtTurnCount = entry.AtTurnCount,
                    CurrentTurnCount = currentTurns,
                    IsStale = false,
                    Model = entry.Model,
                    ElapsedMs = entry.ElapsedMs,
                    Status = "ok",
                }, statusCode: 201);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499); // Client Closed Request
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /recap generation FAILED: {ex.Message}");
                return Results.Json(new RecapResponse
                {
                    SessionId = sid,
                    Model = model,
                    Status = "generation_failed",
                    Error = ex.Message,
                });
            }
        });

        // ===== REST: Voice (Whisper-backed Director UI voice mode) ==================
        // Accepts a multipart/form-data upload with one audio file field. Returns the
        // transcript + an executed reply. The OpenAI key lives in AgentOptions and is
        // never sent to the browser.
        app.MapPost("/voice/command", async (HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /voice/command");

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an audio file" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no audio file uploaded; use form field 'file'" });

            var svc = new VoiceService(sessionManager, sessionManager.Options);
            await using var stream = file.OpenReadStream();
            var resp = await svc.HandleAsync(stream, file.FileName, ctx.RequestAborted);
            return Results.Json(resp);
        });

        // GET /voice/status - reports whether voice mode is enabled (key configured).
        // The browser uses this on page load to hide / disable the Voice button when
        // no key is present, instead of letting the user try and get an error mid-record.
        app.MapGet("/voice/status", () =>
        {
            var svc = new VoiceService(sessionManager, sessionManager.Options);
            return Results.Json(new { available = svc.IsAvailable });
        });

        // ===== REST: Resumable voice utterance upload (spotty-network safe) =========
        // Same origin as the Voice tab. Flow: register -> chunk(idempotent) -> complete.
        // Built for the car: each chunk that lands stays landed, so a dropped connection
        // resumes at the next missing chunk instead of re-sending the whole clip.
        app.MapPost("/voice/utterance", (VoiceUtteranceRegisterRequest? req) =>
        {
            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            if (!svc.IsAvailable)
                return Results.Json(new { status = "no_key", error = "OpenAI API key missing" });
            var id = svc.Register(req?.UtteranceId);
            return Results.Json(new { utteranceId = id });
        });

        // Raw audio bytes in the body; X-Chunk-Sha256 header carries the hex digest so the
        // server can reject corruption and treat an identical retry as a no-op.
        app.MapPut("/voice/utterance/{id}/chunk/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();

            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            try
            {
                await svc.StoreChunkAsync(id, index, bytes, string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] PUT /voice/utterance chunk FAILED: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/voice/utterance/{id}/complete", async (string id, VoiceUtteranceCompleteRequest req, HttpContext ctx) =>
        {
            if (req is null || req.TotalChunks <= 0)
                return Results.BadRequest(new { error = "totalChunks (>0) is required" });

            var repoPath = "";
            var sessionName = "";
            if (!string.IsNullOrEmpty(req.SessionId) && Guid.TryParse(req.SessionId, out var sg))
            {
                var s = sessionManager.GetSession(sg);
                repoPath = s?.RepoPath ?? "";
                sessionName = s is null ? ""
                    : (!string.IsNullOrWhiteSpace(s.CustomName) ? s.CustomName!.Trim()
                       : Path.GetFileName(s.RepoPath.TrimEnd('\\', '/')));
            }

            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            var resp = await svc.CompleteAsync(id, req.TotalChunks, req.Mime ?? "audio/webm", repoPath,
                req.SessionId ?? "", sessionName, ctx.RequestAborted);
            // "incomplete" is a client-recoverable state (re-send missing chunks), so 409.
            return resp.Status == "incomplete"
                ? Results.Json(resp, statusCode: StatusCodes.Status409Conflict)
                : Results.Json(resp);
        });

        // ===== REST: Manager chat (Phase 1) =================================================
        // Relays one user message to the session configured by Chat.SessionRepoPath in
        // appsettings.json. Waits for the agent's turn to complete, returns the reply.
        // See docs/features/director/GOAL_VOICE_MANAGER.md Phase 1.
        app.MapPost("/chat", async (ChatRequest req, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /chat: textLen={req?.Text?.Length ?? 0}, pollOnly={req?.PollOnly ?? false}");
            // A poll request carries no new message (PollOnly): it only reads the
            // session's current state, so Text is not required in that mode.
            if (req is null || (!req.PollOnly && string.IsNullOrWhiteSpace(req.Text)))
                return Results.BadRequest(new { error = "text is required" });

            var svc = new ChatService(sessionManager, sessionManager.Options);
            var resp = await svc.HandleAsync(req, ctx.RequestAborted);

            // Map the service status to an HTTP code so the UI can branch cleanly.
            return resp.Status switch
            {
                "ok" or "timeout" or "working" => Results.Json(resp),
                "no_session_configured" => Results.Json(resp, statusCode: StatusCodes.Status503ServiceUnavailable),
                "session_not_found" => Results.Json(resp, statusCode: StatusCodes.Status404NotFound),
                "session_busy" => Results.Json(resp, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(resp, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        // ===== REST: Wingman rules / git / recovery (Phases 5-7) =========================
        // Each of these is a thin HTTP wrapper over the matching WingmanService method.
        app.MapPost("/sessions/{sid}/rule-violations", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });

            var latest = turnSummaryCache?.GetForSession(guid).LastOrDefault();
            if (latest is null)
                return Results.Json(new RuleViolationsResponse { SessionId = sid, Status = "no_summary" });

            var resp = await WingmanService.CheckRulesAsync(latest, session.RepoPath, sessionManager.Options.ClaudePath, ctx.RequestAborted);
            resp.SessionId = sid;
            return Results.Json(resp);
        });

        app.MapGet("/sessions/{sid}/git", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            var snap = await WingmanService.GitSnapshotAsync(session.RepoPath, ctx.RequestAborted);
            return Results.Json(snap);
        });

        // ===== Git WRITE actions (mirror the desktop Source Control view) =====
        // Reads stay on GET /git above; these mutate the working tree of the session's repo.
        var gitWrite = new Core.Git.GitWriteService();

        async Task<IResult> RunGitWrite(string sid, Func<string, Task<Core.Git.GitWriteResult>> op)
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            var r = await op(session.RepoPath);
            return r.Success
                ? Results.Json(new { accepted = true, output = r.Output })
                : Results.Json(new { accepted = false, error = r.Error, exitCode = r.ExitCode }, statusCode: StatusCodes.Status409Conflict);
        }

        app.MapPost("/sessions/{sid}/git/stage", (string sid, GitPathsRequest? req) =>
            RunGitWrite(sid, repo => gitWrite.StageAsync(repo, req?.Paths ?? new())));
        app.MapPost("/sessions/{sid}/git/unstage", (string sid, GitPathsRequest? req) =>
            RunGitWrite(sid, repo => gitWrite.UnstageAsync(repo, req?.Paths ?? new())));
        app.MapPost("/sessions/{sid}/git/discard", (string sid, GitPathsRequest? req) =>
            RunGitWrite(sid, repo => gitWrite.DiscardAsync(repo, req?.Paths ?? new())));
        app.MapPost("/sessions/{sid}/git/commit", (string sid, GitCommitRequest? req) =>
            RunGitWrite(sid, repo => gitWrite.CommitAsync(repo, req?.Message ?? "")));

        // Re-point a Director session at a different Claude session id (mirrors the desktop
        // Relink button - recover continuity when the underlying Claude session id changed).
        app.MapPost("/sessions/{sid}/relink", (string sid, RelinkRequest? req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            if (req is null || string.IsNullOrWhiteSpace(req.ClaudeSessionId))
                return Results.BadRequest(new { error = "claudeSessionId is required" });
            if (sessionManager.GetSession(guid) is null)
                return Results.NotFound(new { error = "session not found" });

            sessionManager.RelinkClaudeSession(guid, req.ClaudeSessionId);
            return Results.Json(new { accepted = true, claudeSessionId = req.ClaudeSessionId });
        });

        app.MapPost("/sessions/{sid}/recovery-prompt", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            var latest = turnSummaryCache?.GetForSession(guid).LastOrDefault();
            var rp = await WingmanService.BuildRecoveryPromptAsync(sid, session.RepoPath, latest, ctx.RequestAborted);
            return Results.Json(rp);
        });

        // ===== REST: OpenAI TTS (Phase 3) ===================================================
        // Voice mode posts spoken_text here, gets audio/mpeg back.  Falls back to
        // browser SpeechSynthesis on the client side if this fails.
        app.MapPost("/tts", async (TtsRequest req, HttpContext ctx) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new TtsErrorResponse { Status = "empty_text", Error = "text is required" });

            var svc = new TtsService(sessionManager.Options);
            var result = await svc.GenerateAsync(req.Text, req.Voice, req.Model, ctx.RequestAborted);
            if (!result.Success || result.AudioBytes is null)
            {
                var status = result.Status switch
                {
                    "no_key" => StatusCodes.Status503ServiceUnavailable,
                    "empty_text" => StatusCodes.Status400BadRequest,
                    "openai_failed" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status500InternalServerError,
                };
                return Results.Json(
                    new TtsErrorResponse { Status = result.Status, Error = result.ErrorMessage ?? "" },
                    statusCode: status);
            }
            return Results.File(result.AudioBytes, contentType: result.ContentType ?? "audio/mpeg");
        });

        app.MapGet("/tts/status", () =>
        {
            var svc = new TtsService(sessionManager.Options);
            return Results.Json(new
            {
                available = svc.IsAvailable,
                voice = sessionManager.Options.TtsVoice,
                model = sessionManager.Options.TtsModel,
            });
        });

        // ===== REST: Wingman turn summaries (Phase 2) ====================================
        // Per-completed-turn structured summary produced by the SessionWingman.
        // Feeds the Agent View AND the voice mode's TTS (via summary.spokenText).
        // See docs/goals/GOAL_CC_DIRECTOR_SUPERVISOR.md section 4.
        app.MapGet("/sessions/{sid}/turn-summaries", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            if (sessionManager.GetSession(guid) is null)
                return Results.NotFound(new { error = "session not found" });

            var list = turnSummaryCache?.GetForSession(guid).ToList() ?? new List<TurnSummary>();
            return Results.Json(new TurnSummariesResponse { SessionId = sid, Summaries = list });
        });

        // POST /sessions/{sid}/turn-summaries - generate a summary for the LATEST turn
        // on demand.  Used by the voice mode after a chat reply lands, so it can speak
        // the spoken_text version instead of the raw reply.  Synchronous: returns the
        // generated summary directly so the caller doesn't have to poll.
        app.MapPost("/sessions/{sid}/turn-summaries", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            if (turnSummaryCache is null)
                return Results.Problem("Wingman turn-summary cache not wired", statusCode: 500);

            var summary = await turnSummaryCache.GenerateForLatestTurnAsync(guid, ctx.RequestAborted);
            if (summary is null)
                return Results.NotFound(new { error = "session not found or has no terminal output yet" });
            return Results.Json(summary, statusCode: 201);
        });

        // POST /sessions/{sid}/state-vote - human correction of the terminal state detector.
        // The user says what the status SHOULD have been; we capture it with the terminal
        // tail and file it to the GitHub tracking issue (and always locally). This is the
        // ground-truth feedback loop that replaces automated hook-vs-terminal measurement.
        app.MapPost("/sessions/{sid}/state-vote", async (string sid, StateVoteRequest req, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST state-vote: sid={sid}, correct={req?.CorrectState}");
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.CorrectState))
                return Results.BadRequest(new { error = "correctState is required" });

            // Capture this session's own terminal tail (ANSI stripped) for context.
            var tail = "";
            try
            {
                var bytes = session.Buffer?.DumpAll();
                if (bytes is { Length: > 0 })
                {
                    const int TailBytes = 8192;
                    var start = Math.Max(0, bytes.Length - TailBytes);
                    tail = AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
                }
            }
            catch (Exception ex) { FileLog.Write($"[ControlEndpoints] state-vote tail FAILED: {ex.Message}"); }

            var vote = new Core.Feedback.StateVote(
                SessionId: sid,
                RepoPath: session.RepoPath,
                Agent: session.AgentKind.ToString(),
                DetectedState: string.IsNullOrWhiteSpace(req.DetectedState) ? session.ActivityState.ToString() : req.DetectedState!,
                DetectedReason: req.DetectedReason ?? session.LastStatusReason ?? "",
                CorrectState: req.CorrectState!,
                Note: req.Note ?? "",
                TerminalTail: tail,
                At: DateTime.UtcNow);

            var result = await Core.Feedback.StateVoteService.SubmitAsync(vote, ctx.RequestAborted);
            return Results.Json(result);
        });

        app.MapPost("/handover", async (HandoverRequest req) =>
        {
            // Director-local handover. Source AND target must both live on this Director.
            // Cross-Director handovers go via the Gateway proxy.
            FileLog.Write($"[ControlEndpoints] POST /handover: from={req?.FromSessionId} toSid={req?.ToSessionId} toRepo={req?.ToRepoPath}");

            if (req is null || string.IsNullOrEmpty(req.FromSessionId))
                return Results.BadRequest(new { error = "fromSessionId is required" });
            if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "exactly one of toSessionId or toRepoPath is required" });
            if (!string.IsNullOrEmpty(req.ToSessionId) && !string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "toSessionId and toRepoPath are mutually exclusive" });

            if (!Guid.TryParse(req.FromSessionId, out var fromGuid))
                return Results.BadRequest(new { error = "invalid fromSessionId format" });

            var source = sessionManager.GetSession(fromGuid);
            if (source is null)
                return Results.NotFound(new { error = "source session not found on this director" });

            // 1) Build the context text
            SessionSummaryDto summary;
            if (string.IsNullOrEmpty(source.ClaudeSessionId))
            {
                summary = new SessionSummaryDto
                {
                    SessionId = req.FromSessionId, DirectorId = directorId,
                    Agent = source.AgentKind.ToString(),
                    RepoPath = source.RepoPath,
                    ActivityState = source.ActivityState.ToString(),
                    CreatedAt = source.CreatedAt.UtcDateTime,
                };
            }
            else
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(source.ClaudeSessionId, source.RepoPath);
                summary = File.Exists(jsonl)
                    ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                    : new SessionSummaryDto();
                summary.SessionId = req.FromSessionId;
                summary.DirectorId = directorId;
                summary.Agent = source.AgentKind.ToString();
                summary.RepoPath = source.RepoPath;
                summary.ActivityState = source.ActivityState.ToString();
                summary.CreatedAt = source.CreatedAt.UtcDateTime;
            }
            var contextText = SummaryBuilder.FormatAsHandoverPrompt(summary, req.ExtraContext);

            // 2) Find or create the target session
            Session target;
            if (!string.IsNullOrEmpty(req.ToSessionId))
            {
                if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                    return Results.BadRequest(new { error = "invalid toSessionId format" });
                var existing = sessionManager.GetSession(toGuid);
                if (existing is null)
                    return Results.NotFound(new { error = "target session not found on this director" });
                if (existing.Status is SessionStatus.Exited or SessionStatus.Failed)
                    return Results.StatusCode(StatusCodes.Status409Conflict);
                target = existing;
                await target.SendTextAsync(contextText);
            }
            else
            {
                var repo = req.ToRepoPath!;
                if (!Directory.Exists(repo))
                    return Results.BadRequest(new { error = $"toRepoPath does not exist: {repo}" });
                if (!Enum.TryParse<AgentKind>(req.ToAgent, ignoreCase: true, out var kind))
                    return Results.BadRequest(new { error = $"unknown agent: {req.ToAgent}" });

                IAgent agent = kind switch
                {
                    AgentKind.ClaudeCode => new ClaudeAgent(sessionManager.Options),
                    AgentKind.Pi => new PiAgent(sessionManager.Options),
                    AgentKind.Codex => new CodexAgent(sessionManager.Options),
                    AgentKind.Gemini => new GeminiAgent(sessionManager.Options),
                    AgentKind.OpenCode => new OpenCodeAgent(sessionManager.Options),
                    _ => throw new InvalidOperationException("unreachable"),
                };

                try
                {
                    target = sessionManager.CreateSession(repo, agent, userArgs: null, SessionBackendType.ConPty, resumeSessionId: null);
                }
                catch (Exception ex)
                {
                    return Results.Problem("failed to create target session: " + ex.Message, statusCode: 500);
                }
                // SessionManager.CreateSession now fires OnSessionCreated itself, so no
                // explicit RaiseSessionCreated call is needed here.

                // Dispatch the context after the new session reaches Idle. Fire-and-forget;
                // we return the target DTO immediately so callers can navigate to it.
                var capturedTarget = target;
                var capturedText = contextText;
                _ = Task.Run(async () =>
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(30_000);
                    while (DateTime.UtcNow < deadline)
                    {
                        var st = capturedTarget.ActivityState;
                        if (st is ActivityState.Idle or ActivityState.WaitingForInput) break;
                        if (st is ActivityState.Exited) { FileLog.Write($"[ControlEndpoints] /handover target exited before idle, sid={capturedTarget.Id}"); return; }
                        await Task.Delay(500);
                    }
                    try { await capturedTarget.SendTextAsync(capturedText); }
                    catch (Exception ex) { FileLog.Write($"[ControlEndpoints] /handover dispatch FAILED: {ex.Message}"); }
                });
            }

            // 3) Optionally archive to vault
            string? archivedAt = null;
            if (req.ArchiveToVault)
            {
                try { archivedAt = HandoverArchive.Write(summary, contextText, target.Id.ToString()); }
                catch (Exception ex) { FileLog.Write($"[ControlEndpoints] /handover archive FAILED: {ex.Message}"); }
            }

            return Results.Json(new HandoverResponse
            {
                Accepted = true,
                TargetSession = Map(target, directorId, turnSummaryCache),
                ContextSent = contextText,
                ArchivedAt = archivedAt,
            }, statusCode: 201);
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

        // ===== Per-session prompt queue =====
        // Messages the user composed while the agent was busy. Stored on the session's
        // PromptQueue; the Cockpit's Queue button adds here, the queue panel lists/removes,
        // and "send" delivers an item to the PTY now. Mirrors the existing desktop queue.
        app.MapGet("/sessions/{sid}/queue", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });
            return Results.Json(new { items = ProjectQueue(session) });
        });

        app.MapPost("/sessions/{sid}/queue", (string sid, PromptRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue enqueue: sid={sid}, len={req?.Text?.Length ?? 0}");
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            session.PromptQueue.Enqueue(req.Text);
            return Results.Json(new { items = ProjectQueue(session) });
        });

        app.MapDelete("/sessions/{sid}/queue/{itemId}", (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE queue item: sid={sid}, item={itemId}");
            if (!Guid.TryParse(sid, out var guid) || !Guid.TryParse(itemId, out var itemGuid))
                return Results.BadRequest(new { error = "invalid id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            session.PromptQueue.Remove(itemGuid);
            return Results.Json(new { items = ProjectQueue(session) });
        });

        // Deliver one queued item to the PTY now (and drop it from the queue). Used by the
        // queue panel's per-item "send" and by a "send next" action.
        app.MapPost("/sessions/{sid}/queue/{itemId}/send", async (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue send: sid={sid}, item={itemId}");
            if (!Guid.TryParse(sid, out var guid) || !Guid.TryParse(itemId, out var itemGuid))
                return Results.BadRequest(new { error = "invalid id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });
            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
                return Results.StatusCode(StatusCodes.Status409Conflict);

            var item = session.PromptQueue.FindById(itemGuid);
            if (item is null)
                return Results.NotFound(new { error = "queue item not found" });

            var text = item.Text;
            session.PromptQueue.Remove(itemGuid);
            await session.SendTextAsync(text);
            return Results.Json(new { items = ProjectQueue(session) });
        });

        // Hard interrupt via the session's agent driver (Ctrl+C for Claude). Drivers
        // own the per-CLI keystrokes now: pi for example has NO safe hard interrupt
        // (Ctrl+C twice quits it) and its driver refuses with 409.
        app.MapPost("/sessions/{sid}/interrupt", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST interrupt: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            try
            {
                await session.InterruptAsync();
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(new { accepted = true });
        });

        // Soft-stop the current turn via the session's agent driver (Esc for Claude
        // AND pi - but the driver owns that knowledge, not this endpoint).
        app.MapPost("/sessions/{sid}/escape", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST escape: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            try
            {
                await session.CancelTurnAsync();
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(new { accepted = true });
        });

        // Open the tool's in-terminal history picker (Claude's double-Esc). A
        // visible-terminal feature: the desktop/Cockpit terminal must be on screen.
        app.MapPost("/sessions/{sid}/history-picker", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST history-picker: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            try
            {
                await session.ShowHistoryAsync();
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(new { accepted = true });
        });

        // Reset the conversation context in place (/clear for Claude, /new for pi) and,
        // for transcript-capable drivers, re-link the Director to the NEW agent session
        // id - closing the stale-relink gap after /clear (issue #172 spike finding).
        app.MapPost("/sessions/{sid}/clear-context", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[ControlEndpoints] POST clear-context: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var oldId = session.ClaudeSessionId;
            string? newId;
            try
            {
                newId = await session.ClearContextAsync(ct);
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            if (newId is not null)
                sessionManager.RelinkClaudeSession(guid, newId);

            return Results.Json(new { accepted = true, oldAgentSessionId = oldId, newAgentSessionId = newId });
        });

        // Resize the session's PTY grid so a remote terminal (the Cockpit) can use the full
        // window width. Session.Resize no-ops on an unchanged size, so a chatty client can't
        // hammer the PTY (the Wingman repaint-loop invariant).
        app.MapPost("/sessions/{sid}/resize", (string sid, ResizeRequest req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            if (req is null || req.Cols <= 0 || req.Rows <= 0)
                return Results.BadRequest(new { error = "cols and rows must be > 0" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            session.Resize((short)Math.Min(req.Cols, short.MaxValue), (short)Math.Min(req.Rows, short.MaxValue));
            return Results.Json(new { accepted = true, cols = (int)session.CurrentCols, rows = (int)session.CurrentRows });
        });

        // Upload an image (from the phone) and file it into the user's screenshots folder
        // on THIS Director's machine, where the owning Claude session can read it by
        // absolute path. Accepts multipart/form-data with one image field ("file"). Returns
        // the saved absolute path so the client can drop it into the composer for the user
        // to send. The session and the saved file live on the same machine by construction
        // (the session runs here), so the path is always valid for that session.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext httpCtx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST upload-image: sid={sid}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            if (!httpCtx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an image file field 'file'" });

            var form = await httpCtx.Request.ReadFormAsync(httpCtx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no image uploaded; use form field 'file'" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp" };
            if (!allowed.Contains(ext))
                return Results.BadRequest(new { error = $"unsupported image type '{ext}'. Allowed: {string.Join(", ", allowed)}" });

            var dir = CcStorage.Screenshots();
            var name = $"upload-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}";
            var fullPath = Path.Combine(dir, name);

            await using (var dest = File.Create(fullPath))
            await using (var src = file.OpenReadStream())
            {
                await src.CopyToAsync(dest, httpCtx.RequestAborted);
            }

            FileLog.Write($"[ControlEndpoints] upload-image saved: {fullPath} ({file.Length} bytes)");
            return Results.Json(new { path = fullPath, fileName = name });
        });

        // ===== REST: Screenshots gallery =====
        // The screenshots folder (CcStorage.Screenshots()) lives on THIS Director's machine.
        // The desktop UI reads it via a FileSystemWatcher; the remote Cockpit can't, so it reads
        // the gallery over these endpoints. The browser loads thumbnails by pointing <img src>
        // straight at GET /screenshots/file (the same browser-direct path the live terminal uses),
        // so that endpoint is permissively CORS-open for image GETs on this single-user tailnet.

        // List the screenshots, newest first, with a pre-formatted local time label so the
        // Cockpit renders the same "MMM d, h:mm tt" as the desktop without re-deriving it.
        // The response is ALWAYS capped: ?count=N for an explicit cap, otherwise the newest
        // DefaultScreenshotCount. The folder can hold thousands of images and no client ever
        // shows more than the newest few - older files are deliberately ignored. "total"
        // always reports the full folder count so clients can say "newest N of total".
        app.MapGet("/screenshots", (int? count) =>
        {
            var cap = count is > 0 ? count.Value : DefaultScreenshotCount;
            FileLog.Write($"[ControlEndpoints] GET /screenshots cap={cap}");
            var dir = CcStorage.Screenshots();
            var all = ScreenshotFiles(dir);
            var items = all
                .Take(cap)
                .Select(info => new
                {
                    fileName = info.Name,
                    // Absolute on-disk path on THIS Director's machine. The Cockpit injects it
                    // into the composer at the cursor (desktop parity: drop the path into the
                    // prompt) - the owning Claude session reads it directly, no upload needed.
                    path = info.FullName,
                    timeLabel = info.LastWriteTime.ToString("MMM d, h:mm tt"),
                    lastWriteUtc = info.LastWriteTimeUtc,
                    sizeBytes = info.Length,
                })
                .ToList();
            return Results.Json(new { directory = dir, total = all.Count, items });
        });

        // Serve one screenshot's bytes. Loaded browser-direct as an <img src> and fetched for the
        // "Copy" clipboard action, so it sets Access-Control-Allow-Origin:* (single-user tailnet,
        // no auth/security gating per the deployment model). Path-traversal safe: name must be a
        // bare file that resolves inside the screenshots folder with an image extension.
        app.MapGet("/screenshots/file", (HttpContext ctx, string name) =>
        {
            var full = ResolveScreenshot(name);
            if (full is null)
            {
                FileLog.Write($"[ControlEndpoints] GET /screenshots/file rejected: name={name}");
                return Results.NotFound(new { error = "screenshot not found" });
            }
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            // Screenshot files are written once and never modified, so let the browser keep
            // thumbnails for an hour - session switches and tab revisits stop re-downloading
            // the same bytes over the tailnet.
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Results.File(full, ScreenshotContentType(full), enableRangeProcessing: true);
        });

        // Delete one screenshot from disk (the per-card "Del" action). Mirrors the desktop, which
        // deletes the file off disk. Path-traversal safe via ResolveScreenshot.
        app.MapDelete("/screenshots/file", (string name) =>
        {
            var full = ResolveScreenshot(name);
            if (full is null)
                return Results.NotFound(new { error = "screenshot not found" });
            FileLog.Write($"[ControlEndpoints] DELETE /screenshots/file: {full}");
            File.Delete(full);
            return Results.Json(new { deleted = true, fileName = Path.GetFileName(full) });
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

        // ===== REST: Remove a repository from the recent list =====
        app.MapDelete("/repos", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE /repos: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (repositoryRegistry is null)
                return Results.Json(new { removed = false });

            var removed = repositoryRegistry.Remove(path);
            return Results.Json(new { removed });
        });

        // ===== REST: Register a repository explicitly (no session needed) =====
        app.MapPost("/repos", (RepoAddRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /repos: path={req?.Path}, name=\"{req?.Name}\"");
            if (string.IsNullOrWhiteSpace(req?.Path))
                return Results.BadRequest(new { error = "path is required" });
            if (repositoryRegistry is null)
                return Results.BadRequest(new { error = "repository registry not available" });
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = $"directory not found: {req.Path}" });

            var added = repositoryRegistry.TryAdd(req.Path);
            if (!string.IsNullOrWhiteSpace(req.Name))
                repositoryRegistry.Rename(req.Path, req.Name);

            var wanted = NormalizeRepoPath(req.Path);
            var repo = repositoryRegistry.Repositories.First(r => NormalizeRepoPath(r.Path) == wanted);
            var dto = new RepositoryDto
            {
                Name = string.IsNullOrEmpty(repo.Name) ? Path.GetFileName(repo.Path.TrimEnd('\\', '/')) : repo.Name,
                Path = repo.Path,
                LastUsed = repo.LastUsed,
            };
            return Results.Json(new { added, repo = dto },
                statusCode: added ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // ===== REST: Rename a registered repository =====
        app.MapPatch("/repos", (RepoRenameRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] PATCH /repos: path={req?.Path}, name=\"{req?.Name}\"");
            if (string.IsNullOrWhiteSpace(req?.Path))
                return Results.BadRequest(new { error = "path is required" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (repositoryRegistry is null)
                return Results.BadRequest(new { error = "repository registry not available" });

            if (!repositoryRegistry.Rename(req.Path, req.Name))
                return Results.NotFound(new { error = "repository not registered" });

            var wanted = NormalizeRepoPath(req.Path);
            var repo = repositoryRegistry.Repositories.First(r => NormalizeRepoPath(r.Path) == wanted);
            return Results.Json(new RepositoryDto
            {
                Name = repo.Name,
                Path = repo.Path,
                LastUsed = repo.LastUsed,
            });
        });

        // ===== REST: Enriched per-repo overview (repositories page) =====
        app.MapGet("/repos/overview", () =>
        {
            FileLog.Write("[ControlEndpoints] GET /repos/overview");
            if (repositoryRegistry is null)
                return Results.Json(Array.Empty<RepoOverviewDto>());

            // Aggregate every per-repo data source once, keyed by normalized path.
            var liveByRepo = sessionManager.ListSessions()
                .Where(s => s.ActivityState != ActivityState.Exited)
                .GroupBy(s => NormalizeRepoPath(s.RepoPath))
                .ToDictionary(g => g.Key, g => g.Select(s => s.CustomName ?? ProjectNameOf(s.RepoPath)).ToList());

            var historyByRepo = new SessionHistoryStore().LoadAll()
                .Where(h => !string.IsNullOrEmpty(h.RepoPath))
                .GroupBy(h => NormalizeRepoPath(h.RepoPath))
                .ToDictionary(g => g.Key, g => g.ToList());

            var claudeByRepo = ClaudeSessionReader.ScanAllProjects()
                .Where(m => !string.IsNullOrEmpty(m.ProjectPath))
                .GroupBy(m => NormalizeRepoPath(m.ProjectPath!))
                .ToDictionary(g => g.Key, g => g.ToList());

            var handoversByRepo = HandoverScanner.ScanAll()
                .SelectMany(h => h.RepoPaths.Select(p => (Repo: NormalizeRepoPath(p), Handover: h)))
                .GroupBy(x => x.Repo)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Handover).ToList());

            var overview = repositoryRegistry.Repositories.Select(r =>
            {
                var key = NormalizeRepoPath(r.Path);
                liveByRepo.TryGetValue(key, out var liveNames);
                historyByRepo.TryGetValue(key, out var history);
                claudeByRepo.TryGetValue(key, out var claude);
                handoversByRepo.TryGetValue(key, out var handovers);

                var latestHistory = history?.OrderByDescending(h => h.LastUsedAt).FirstOrDefault();
                var latestClaude = claude?.OrderByDescending(m => m.Modified).FirstOrDefault();

                var lastHistoryAt = latestHistory is null || latestHistory.LastUsedAt == default
                    ? (DateTime?)null
                    : latestHistory.LastUsedAt.UtcDateTime;
                var lastClaudeAt = latestClaude is null || latestClaude.Modified == DateTime.MinValue
                    ? (DateTime?)null
                    : latestClaude.Modified;

                // Most recent activity wins; its summary describes the last session.
                var lastSessionAt = (lastHistoryAt ?? DateTime.MinValue) >= (lastClaudeAt ?? DateTime.MinValue)
                    ? lastHistoryAt ?? lastClaudeAt
                    : lastClaudeAt;
                var lastSummary = lastSessionAt == lastClaudeAt
                    ? latestClaude?.Summary ?? latestClaude?.FirstPrompt ?? latestHistory?.FirstPromptSnippet
                    : latestHistory?.FirstPromptSnippet ?? latestClaude?.Summary ?? latestClaude?.FirstPrompt;

                return new RepoOverviewDto
                {
                    Name = string.IsNullOrEmpty(r.Name) ? Path.GetFileName(r.Path.TrimEnd('\\', '/')) : r.Name,
                    Path = r.Path,
                    LastUsed = r.LastUsed,
                    PathExists = Directory.Exists(r.Path),
                    LiveSessionCount = liveNames?.Count ?? 0,
                    LiveSessionNames = liveNames ?? new List<string>(),
                    ResumableSessionCount = claude?.Count ?? 0,
                    HistorySessionCount = history?.Count ?? 0,
                    LastSessionAtUtc = lastSessionAt,
                    LastSessionSummary = lastSummary,
                    GitBranch = claude?.OrderByDescending(m => m.Modified)
                        .FirstOrDefault(m => !string.IsNullOrEmpty(m.GitBranch))?.GitBranch,
                    HandoverCount = handovers?.Count ?? 0,
                    LastHandoverUtc = handovers?.Max(h => h.DateUtc),
                };
            })
            .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
            .ToList();

            return Results.Json(overview);
        });

        // ===== REST: Coaching quick-launch categories (Assistant / Coach cards) =====
        app.MapGet("/coaching/categories", () =>
        {
            FileLog.Write("[ControlEndpoints] GET /coaching/categories");
            var cats = new List<CoachingCategoryDto>
            {
                new()
                {
                    Key = "assistant",
                    Label = "Assistant",
                    Description = "Tasks, contacts, daily briefing",
                    Path = CcStorage.CoachingCategory("assistant"),
                },
                new()
                {
                    Key = "coach",
                    Label = "Coach",
                    Description = "Life coaching across all domains",
                    Path = CcStorage.CoachingCategory("coach"),
                },
            };
            return Results.Json(cats);
        });

        // ===== REST: Resumable Claude Code sessions (Resume Session tab) =====
        app.MapGet("/claude-sessions", (string? repo) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /claude-sessions: repo={repo}");

            var claudeMeta = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
            foreach (var cm in ClaudeSessionReader.ScanAllProjects())
                claudeMeta.TryAdd(cm.SessionId, cm);

            var history = new SessionHistoryStore().LoadAll();
            var dtos = new List<ClaudeSessionDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // History entries first (they carry custom name/color), enriched with Claude metadata.
            foreach (var entry in history)
            {
                if (string.IsNullOrEmpty(entry.ClaudeSessionId) || !seen.Add(entry.ClaudeSessionId))
                    continue;
                claudeMeta.TryGetValue(entry.ClaudeSessionId, out var meta);
                dtos.Add(new ClaudeSessionDto
                {
                    ClaudeSessionId = entry.ClaudeSessionId,
                    RepoPath = entry.RepoPath,
                    ProjectName = ProjectNameOf(entry.RepoPath),
                    CustomName = entry.CustomName,
                    CustomColor = entry.CustomColor,
                    MessageCount = meta?.MessageCount ?? 0,
                    Summary = meta?.Summary ?? meta?.FirstPrompt ?? entry.FirstPromptSnippet,
                    LastUsedUtc = entry.LastUsedAt == default ? meta?.Modified : entry.LastUsedAt.UtcDateTime,
                });
            }

            // Then any Claude sessions not tracked by a workspace history entry.
            foreach (var meta in claudeMeta.Values)
            {
                if (string.IsNullOrEmpty(meta.SessionId) || !seen.Add(meta.SessionId))
                    continue;
                dtos.Add(new ClaudeSessionDto
                {
                    ClaudeSessionId = meta.SessionId,
                    RepoPath = meta.ProjectPath ?? string.Empty,
                    ProjectName = ProjectNameOf(meta.ProjectPath ?? string.Empty),
                    MessageCount = meta.MessageCount,
                    Summary = meta.Summary ?? meta.FirstPrompt,
                    LastUsedUtc = meta.Modified == DateTime.MinValue ? null : meta.Modified,
                });
            }

            if (!string.IsNullOrWhiteSpace(repo))
            {
                var wanted = NormalizeRepoPath(repo);
                dtos = dtos.Where(d => !string.IsNullOrEmpty(d.RepoPath)
                                       && NormalizeRepoPath(d.RepoPath) == wanted).ToList();
            }

            var ordered = dtos
                .OrderByDescending(d => d.LastUsedUtc ?? DateTime.MinValue)
                .ToList();
            return Results.Json(ordered);
        });

        // ===== REST: Handover documents (Handovers tab) =====
        app.MapGet("/handovers", (string? repo) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /handovers: repo={repo}");
            var infos = HandoverScanner.ScanAll();
            if (!string.IsNullOrWhiteSpace(repo))
            {
                var wanted = NormalizeRepoPath(repo);
                infos = infos.Where(h => h.RepoPaths.Any(p => NormalizeRepoPath(p) == wanted)).ToList();
            }
            var dtos = infos.Select(h => new HandoverDto
            {
                Path = h.Path,
                Title = h.Title,
                DateDisplay = h.DateDisplay,
                DateUtc = h.DateUtc,
                RepoPath = h.RepoPath,
                RepoPaths = h.RepoPaths,
                SessionName = h.SessionName,
            }).ToList();
            return Results.Json(dtos);
        });

        app.MapPost("/handovers", (HandoverCreateRequest req) =>
        {
            // Standalone handover document: written to the vault handover folder so it
            // shows up in the Handovers tab and GET /handovers. Unlike POST /handover,
            // no target session is involved.
            FileLog.Write($"[ControlEndpoints] POST /handovers: title=\"{req?.Title}\"");
            if (string.IsNullOrWhiteSpace(req?.Title))
                return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "content is required" });

            var path = HandoverScanner.WriteNew(req.Title, req.Content, req.RepoPaths, req.SessionName);
            var h = HandoverScanner.Parse(path);
            return Results.Json(new HandoverDto
            {
                Path = h.Path,
                Title = h.Title,
                DateDisplay = h.DateDisplay,
                DateUtc = h.DateUtc,
                RepoPath = h.RepoPath,
                RepoPaths = h.RepoPaths,
                SessionName = h.SessionName,
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete("/handovers", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE /handovers: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            try
            {
                HandoverScanner.Delete(path);
                return Results.Json(new { removed = true });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "handover not found" });
            }
        });

        app.MapGet("/handovers/content", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /handovers/content: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            try
            {
                var content = HandoverScanner.ReadContent(path);
                return Results.Json(new HandoverContentDto { Path = path, Content = content });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "handover not found" });
            }
        });

        // ===== REST: Remote folder browser (Browse... button) =====
        app.MapGet("/fs/list", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /fs/list: path={path}");
            try
            {
                return Results.Json(ListDirectory(path));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] GET /fs/list FAILED: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
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
                AgentKind.OpenCode => new OpenCodeAgent(sessionManager.Options),
                _ => throw new InvalidOperationException("unreachable"),
            };

            // Session type (issue #211): identity chosen once at creation. Null/empty
            // means Implement so pre-#211 clients keep today's behavior exactly.
            var sessionType = SessionType.Implement;
            if (!string.IsNullOrWhiteSpace(req.Type)
                && !Enum.TryParse(req.Type, ignoreCase: true, out sessionType))
                return Results.BadRequest(new { error = $"unknown type: {req.Type}. Valid: Implement, Discuss, BugReport" });

            Session session;
            try
            {
                session = sessionManager.CreateSession(
                    req.RepoPath,
                    agent,
                    req.Args,
                    SessionBackendType.ConPty,
                    resumeSessionId: string.IsNullOrWhiteSpace(req.ResumeSessionId) ? null : req.ResumeSessionId,
                    sessionType: sessionType);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] POST /sessions FAILED: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }

            // Apply the per-session Wingman opt-in from the request. Default in the contract
            // is true (matching Session.WingmanEnabled's default), so a new session boots with
            // the auto-explain briefing on unless the caller explicitly disabled it.
            session.WingmanEnabled = req.WingmanEnabled;
            FileLog.Write($"[ControlEndpoints] POST /sessions: sid={session.Id} wingmanEnabled={session.WingmanEnabled}");

            // SessionManager.CreateSession now fires OnSessionCreated itself, so no
            // explicit RaiseSessionCreated call is needed here.

            // If a PrePrompt was supplied, dispatch it once the agent is actually READY.
            // Fire-and-forget on a background task so the POST returns 201 immediately.
            //
            // READINESS (issue #212): ActivityState alone is NOT a gate here - a fresh
            // session reads WaitingForInput from t=0, which dispatched seeds into a
            // still-BOOTING claude: the typed text reached the composer but the Enter
            // keypresses were dropped, parking the whole prompt unsubmitted (observed
            // live on the restore E2E, repeatedly). Claude is ready once its startup
            // burst (the welcome banner) has rendered AND the TUI has settled - so wait
            // for substantial output followed by a full quiet poll. If the deadline
            // passes without that, dispatch anyway; the backend's @-reference submit
            // watchdog is the last line of defense.
            // The type's playbook (issue #211) rides the SAME readiness gate, ahead of any
            // caller-supplied PrePrompt: one composed dispatch, so the agent reads its
            // ground rules before the seeded task (the #236 bug-session flow needs both).
            var seedText = SessionTypePlaybooks.ComposeSeed(sessionType, req.PrePrompt);

            if (!string.IsNullOrWhiteSpace(seedText))
            {
                var prePrompt = seedText;
                var waitMs = Math.Max(1000, req.PrePromptWaitMs);
                var capturedSession = session;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                        long lastBytes = -1;
                        while (DateTime.UtcNow < deadline)
                        {
                            var st = capturedSession.ActivityState;
                            if (st is ActivityState.Exited) { FileLog.Write($"[ControlEndpoints] PrePrompt: session exited before ready, sid={capturedSession.Id}"); return; }
                            var bytes = capturedSession.Buffer?.TotalBytesWritten ?? 0;
                            var settled = bytes > 1500 && bytes == lastBytes
                                && st is ActivityState.Idle or ActivityState.WaitingForInput;
                            if (settled)
                            {
                                FileLog.Write($"[ControlEndpoints] PrePrompt: agent ready (TUI rendered {bytes} bytes, then settled), sid={capturedSession.Id}");
                                break;
                            }
                            lastBytes = bytes;
                            await Task.Delay(750);
                        }
                        FileLog.Write($"[ControlEndpoints] PrePrompt: dispatching to sid={capturedSession.Id}, len={prePrompt.Length}");
                        await capturedSession.SendTextAsync(prePrompt);
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[ControlEndpoints] PrePrompt FAILED: {ex.Message}");
                    }
                });
            }

            return Results.Json(Map(session, directorId, turnSummaryCache), statusCode: 201);
        });

        // ===== REST: Create a GitHub Actions remote session =====
        app.MapPost("/sessions/github", (GitHubSessionRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions/github: {req?.Owner}/{req?.Repo} mode={req?.TriggerMode}");

            if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
                return Results.BadRequest(new { error = "owner and repo are required" });
            if (string.IsNullOrWhiteSpace(req.InitialPrompt))
                return Results.BadRequest(new { error = "initialPrompt is required" });
            if (!Enum.TryParse<RemoteTriggerMode>(req.TriggerMode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = $"unknown triggerMode: {req.TriggerMode}. Valid: NewIssue, ExistingThread, WorkflowDispatch" });
            if (mode == RemoteTriggerMode.ExistingThread && (req.ThreadNumber is null || req.ThreadNumber <= 0))
                return Results.BadRequest(new { error = "threadNumber is required (and must be positive) for ExistingThread mode" });
            if (mode == RemoteTriggerMode.WorkflowDispatch && string.IsNullOrWhiteSpace(req.WorkflowFile))
                return Results.BadRequest(new { error = "workflowFile is required for WorkflowDispatch mode" });

            var config = new RemoteSessionConfig
            {
                Owner = req.Owner.Trim(),
                Repo = req.Repo.Trim(),
                BaseBranch = string.IsNullOrWhiteSpace(req.BaseBranch) ? "main" : req.BaseBranch.Trim(),
                TriggerMode = mode,
                InitialPrompt = req.InitialPrompt.Trim(),
                ThreadNumber = req.ThreadNumber,
                IssueTitle = req.IssueTitle,
                WorkflowFile = req.WorkflowFile,
            };

            try
            {
                var session = sessionManager.CreateGitHubActionsSession(config);
                return Results.Json(Map(session, directorId, turnSummaryCache), statusCode: 201);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] POST /sessions/github FAILED: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ===== REST: Kill a session =====
        app.MapDelete("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            // Name the local caller (issue #212 L3): a loopback DELETE could be any agent
            // on this machine, and "127.0.0.1" alone is useless for forensics.
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /sessions/{sid} caller={caller}");

            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            try
            {
                await sessionManager.KillSessionAsync(guid);
                // Kill AND remove: without removal the dead session lingered as a zombie row
                // in every roster (status Exited but still listed), was persisted at Director
                // shutdown, and RESURRECTED on relaunch. DELETE means gone - same as the
                // desktop's close flow, which has always paired kill with removal.
                sessionManager.RemoveSession(guid);
                return Results.Json(new { killed = true, removed = true });
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

        // ===== Crash recovery (issue #212 W3) =====
        // This machine's claimed dirty crash journals - Directors that died abnormally here,
        // with their recoverable session rosters. The Gateway aggregates these across the fleet
        // for the Cockpit's Interrupted sessions list.
        app.MapGet("/interrupted", () =>
        {
            var pending = Core.Sessions.DirectorCrashJournal.ListPendingRecoveries();
            return Results.Json(pending);
        });

        // Dismiss one claimed dirty journal once its sessions are recovered or no longer wanted.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", (HttpContext ctx, string deadDirectorId, int deadPid) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} caller={caller}");
            var removed = Core.Sessions.DirectorCrashJournal.Dismiss(deadDirectorId, deadPid);
            return removed ? Results.Json(new { dismissed = true }) : Results.NotFound(new { error = "no such interrupted journal" });
        });

        // Remove ONE session from a claimed dirty journal after it has been restored
        // (issue #212 W4): the rest of the journal stays in the Interrupted sessions list.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            (HttpContext ctx, string deadDirectorId, int deadPid, string sessionId) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} caller={caller}");
            var removed = Core.Sessions.DirectorCrashJournal.RemoveSession(deadDirectorId, deadPid, sessionId);
            return removed ? Results.Json(new { removed = true }) : Results.NotFound(new { error = "no such interrupted session" });
        });

        // ===== Shutdown =====
        app.MapPost("/shutdown", (HttpContext ctx) =>
        {
            // Name the local caller (issue #212 L3). This endpoint stops the whole Director
            // and every claude.exe under it; the 2026-06-06 post-mortem could not tell whether
            // an agent had triggered a shutdown because the caller was never recorded.
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] POST shutdown requested caller={caller}");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlEndpoints] Shutdown FAILED: {ex.Message}"); }
            });
            return Results.Json(new { accepted = true });
        });
    }

    /// <summary>Folder name of a repo path, for display fallback. Empty path -> "Unknown Project".</summary>
    private static string ProjectNameOf(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath))
            return "Unknown Project";
        return Path.GetFileName(repoPath.TrimEnd('\\', '/'));
    }

    /// <summary>Display-cap for brief text blocks. Null-safe; marks the cut explicitly.</summary>
    private static string? TruncateForDisplay(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "... [truncated]";
    }

    /// <summary>
    /// Canonical form for repo-path comparison across endpoints (?repo= filters, overview
    /// grouping): full path, trailing separators trimmed, lowercased (Windows paths are
    /// case-insensitive). Callers must filter null/empty first; Path.GetFullPath throws
    /// only on empty or embedded-NUL input, which the global error envelope surfaces loudly.
    /// </summary>
    private static string NormalizeRepoPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// List sub-directories of <paramref name="path"/> for the remote folder browser. A null or
    /// empty path returns the drive roots. Solo-tailnet: no path sandboxing (see remote-experience-plan.md).
    /// </summary>
    private static DirectoryListingDto ListDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DirEntryDto
                {
                    Name = d.Name.TrimEnd('\\', '/'),
                    Path = d.RootDirectory.FullName,
                    IsDrive = true,
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DirectoryListingDto { CurrentPath = null, ParentPath = null, Entries = drives };
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"directory not found: {full}");

        var parent = Directory.GetParent(full.TrimEnd('\\', '/'))?.FullName;

        var entries = Directory.EnumerateDirectories(full)
            .Select(d =>
            {
                try
                {
                    // Skip hidden / system directories so the picker stays clean.
                    var attr = File.GetAttributes(d);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                        return null;
                }
                catch { return null; }
                return new DirEntryDto { Name = Path.GetFileName(d), Path = d, IsDrive = false };
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DirectoryListingDto { CurrentPath = full, ParentPath = parent, Entries = entries };
    }

    /// <summary>
    /// The session's ENTIRE terminal buffer, ANSI stripped, for the "Ask the Wingman"
    /// answer path. Unlike WingmanContextBuilder's tail (capped at 4000 chars for a
    /// one-shot prompt), this returns everything: the read-only session writes it to a
    /// snapshot file and reads only as much as it needs, so "read me the whole article"
    /// can reach content that scrolled past a tail. Read-only inspection; never mutates.
    /// </summary>
    private static string ReadFullCleanedBuffer(Session session)
    {
        try
        {
            var bytes = session.Buffer?.DumpAll();
            if (bytes is null || bytes.Length == 0) return "";
            return AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ReadFullCleanedBuffer FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>Project a session's prompt queue to the wire shape the Cockpit renders.</summary>
    private static IReadOnlyList<object> ProjectQueue(Session s) =>
        s.PromptQueue.Items
            .Select(i => (object)new { id = i.Id.ToString(), text = i.Text, createdAt = i.CreatedAt })
            .ToList();

    /// <summary>The session driver's capability flags as names, for UIs to render
    /// action buttons from (no per-agent special cases client-side).</summary>
    private static List<string> CapabilityNames(Session s)
    {
        var caps = s.Driver.Capabilities;
        return Enum.GetValues<Core.Drivers.DriverCapabilities>()
            .Where(f => f != Core.Drivers.DriverCapabilities.None && caps.HasFlag(f))
            .Select(f => f.ToString())
            .ToList();
    }

    private static SessionDto Map(Session s, string directorId, TurnSummaryCache? cache = null)
    {
        // Phase 3: StatusColor and LastStatusReason are owned by the SessionStatusWingman
        // and live on the Session itself. Map() reads them directly - no derivation, no
        // recomputation from TurnSummaryCache, no fallback. The `cache` argument is kept for
        // other endpoints that surface raw summaries; it is not consulted for color.
        var lastWrite = s.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
        var lastActivity = lastWrite == DateTime.MinValue ? s.CreatedAt.UtcDateTime : lastWrite;
        var idleSeconds = Math.Max(0, (DateTime.UtcNow - lastActivity).TotalSeconds);
        return new()
        {
            SessionId = s.Id.ToString(),
            DirectorId = directorId,
            Agent = s.AgentKind.ToString(),
            Type = s.SessionType.ToString(),
            RepoPath = s.RepoPath,
            Status = s.Status.ToString(),
            ActivityState = s.ActivityState.ToString(),
            // The Gateway-pushed display annotation (issue #186 two-owner model). Null
            // unless the Gateway's brain assessed the CURRENT quiet; wiped by any real
            // state change. Display only - never feeds the detector.
            AssessedState = s.AssessedStateAnnotation,
            CreatedAt = s.CreatedAt.UtcDateTime,
            TotalBufferBytes = s.Buffer?.TotalBytesWritten ?? 0,
            BackendType = s.BackendType.ToString(),
            DriverCapabilities = CapabilityNames(s),
            Name = s.CustomName,
            SortOrder = s.SortOrder,
            StatusColor = s.StatusColor,
            LastStatusReason = s.LastStatusReason,
            BriefingState = s.BriefingState.ToString(),
            RailLine = s.LatestBriefRailLine,
            LastActivityAt = lastActivity,
            IdleSeconds = idleSeconds,
            QuietThresholdSeconds = CcDirector.Core.Wingman.TerminalStateDetector.QuietThreshold.TotalSeconds,
            VoiceMode = s.VoiceMode,
            OnHold = s.OnHold,
            WingmanEnabled = s.WingmanEnabled,
            RemoteRepo = s.RemoteRepo ?? "",
            RemoteThreadUrl = s.RemoteThreadUrl ?? "",
            RemoteRunUrl = s.RemoteRunUrl ?? "",
            RemoteRunStatus = s.RemoteRunStatus ?? "",
        };
    }

    /// <summary>
    /// Compute the current turn count from the session's linked JSONL file.
    /// Returns 0 if the session isn't linked or the file isn't there yet.
    /// Used by the recap endpoints to compute the IsStale flag.
    /// </summary>
    private static int ComputeTurnCount(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return 0;
        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return 0;
            var messages = StreamMessageParser.ParseFile(jsonl);
            return WidgetBuilder.BuildFromMessages(messages).Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ComputeTurnCount FAILED: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }

    // ===== Screenshots gallery helpers =====

    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Newest-N cap applied to GET /screenshots when the client sends no ?count. The gallery
    /// only ever shows the newest few screenshots; older files are deliberately never listed.
    /// Internal (not private) so the endpoint tests assert against the same number.
    /// </summary>
    internal const int DefaultScreenshotCount = 100;

    /// <summary>Image files in the screenshots folder, newest first. Empty if the folder is missing.</summary>
    /// <summary>
    /// All image files in the screenshots folder, newest first, in ONE filesystem pass.
    /// DirectoryInfo.EnumerateFiles yields FileInfo objects pre-populated from the directory
    /// enumeration data, so sorting and reading LastWriteTime/Length costs no extra stat
    /// calls. The previous string-path version stat'ed every file twice (sort + FileInfo),
    /// which took ~100ms on a 1400-file folder; this takes ~1-2ms.
    /// </summary>
    private static List<FileInfo> ScreenshotFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<FileInfo>();
        return new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => ScreenshotExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Resolve a client-supplied screenshot name to an absolute path INSIDE the screenshots
    /// folder, or null if it escapes the folder, is not a bare file name, has a non-image
    /// extension, or does not exist. Defends GET/DELETE /screenshots/file against traversal.
    /// </summary>
    private static string? ResolveScreenshot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // Reject anything that isn't a bare file name (no separators, no "..").
        if (Path.GetFileName(name) != name)
            return null;
        if (!ScreenshotExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return null;

        var dir = CcStorage.Screenshots();
        var full = Path.GetFullPath(Path.Combine(dir, name));
        // Confirm the resolved path is still under the screenshots folder.
        var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    private static string ScreenshotContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
