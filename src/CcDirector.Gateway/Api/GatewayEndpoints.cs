using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
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
    /// <param name="onSessionState">Issue #186: receives every session-state observation
    /// (doorbell ping or heartbeat snapshot entry) as (directorId, sessionId, newState).
    /// The host feeds these to the turn-brief tracker; null when briefing is disabled.</param>
    /// <param name="assessedStateFor">Issue #186: the Gateway-owned assessedState for a
    /// session id, stamped onto the /sessions aggregation; null when briefing is disabled.</param>
    /// <param name="briefStampFor">Issue #187: the Gateway-owned briefing state + latest
    /// rail line per session, stamped onto the aggregation now that the Director-side
    /// pipeline (the previous writer of those SessionDto fields) is deleted.</param>
    /// <param name="interruptedBriefFor">Issue #212 W3: the Gateway's last-known rail line +
    /// headline for a session id, used to enrich the Interrupted sessions list so a dead
    /// session is triageable. Reads the durable brief store, so it works even for a session
    /// whose Director has died.</param>
    /// <param name="briefHistoryFor">Issue #212 W4: the full turn-brief history for a session
    /// id, oldest first - the raw material the restore endpoint builds its continuation
    /// context from. Reads the durable brief store, so it serves dead sessions too.</param>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client, string version, string token, bool authEnabled = false, Func<bool>? requestShutdown = null,
        Action<string, string, string>? onSessionState = null, Func<string, string?>? assessedStateFor = null,
        Func<string, (string BriefingState, string? RailLine)>? briefStampFor = null,
        Func<string, (string? RailLine, string? Headline)>? interruptedBriefFor = null,
        Func<string, List<TurnBriefDto>>? briefHistoryFor = null,
        SessionOwnerCache? owners = null)
    {
        // Graceful exit for the self-update helper: answer first (so the caller gets its 200),
        // then hand off to the host's shutdown handler shortly after. 501 when the hosting
        // process wired no handler - this endpoint never half-stops the host on its own.
        app.MapPost("/shutdown", () =>
        {
            FileLog.Write("[GatewayEndpoints] POST /shutdown");
            if (requestShutdown is null)
                return Results.Json(new { error = "shutdown not supported by this host" }, statusCode: StatusCodes.Status501NotImplemented);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250); // let the 200 flush before the host starts tearing down
                if (!requestShutdown())
                    FileLog.Write("[GatewayEndpoints] /shutdown: no handler registered; nothing stopped");
            });
            return Results.Json(new { shuttingDown = true });
        });

        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";

        // Phone recorder ingest (offline-recorded audio -> transcription -> vault).
        RecordingEndpoints.Map(app);

        // Read-only view of the Communication Manager approval queue (see the phone's
        // pending drafts remotely). Step 1 of centralizing the comm queue on the Gateway.
        CommQueueEndpoints.Map(app);

        // Local-machine exe/slot management (the "Exes" page).
        ExesEndpoints.Map(app, registry, client);

        // ===== HTML pages =====
        // The Gateway serves NO UI pages anymore (docs/plans/one-url-cockpit.md): "/" and every
        // other UI path fall through to the Cockpit via the fallback proxy. Only the token
        // login/logout pair remains (it guards the Gateway itself when auth is enabled).
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
            // Fan out in parallel: /healthz is the most-polled endpoint, so it must not
            // pay one client timeout per Director sequentially.
            var counts = await Task.WhenAll(directors.Select(async d =>
            {
                var sessions = await client.ListSessionsAsync(d.ControlEndpoint);
                return sessions?.Count ?? 0;
            }));
            int totalSessions = counts.Sum();

            return Results.Json(new HealthDto
            {
                Status = "ok",
                Directors = directors.Count,
                Sessions = totalSessions,
                Version = version,
                ServerTime = DateTime.UtcNow,
            });
        });

        // About / diagnostics: product, version, build date, install root, the one Cockpit URL, and
        // the installed component versions (from installed.json on this box). Feeds the Cockpit About
        // page; loopback-reachable like the rest of the read API.
        app.MapGet("/about", () => Results.Json(new AboutDto
        {
            Product = AboutInfo.ProductName,
            Version = AboutInfo.VersionFull,
            BuildDate = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm:ss"),
            MachineName = Environment.MachineName,
            InstallRoot = AboutInfo.InstallRoot,
            CockpitUrl = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } fd ? fd + "/" : null,
            InstalledComponents = new Dictionary<string, string>(AboutInfo.InstalledComponents()),
            ServerTime = DateTime.UtcNow,
        }));

        // Where is this machine's Cockpit? ONE URL: the Cockpit is served through the Gateway
        // front door (fallback proxy), so the answer is the front-door base URL itself - never
        // a :7470 URL. Url is null when Tailscale is unavailable; the caller surfaces that.
        // Port/Up still describe the loopback child the supervisor runs (diagnostics).
        app.MapGet("/cockpit", async () =>
        {
            var port = CockpitSupervisor.ResolvePort();
            return Results.Json(new CockpitInfoDto
            {
                Url = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } b ? b + "/" : null,
                Port = port,
                Up = await IsLoopbackPortOpenAsync(port),
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

        app.MapPost("/directors/{id}/heartbeat", async (string id, HttpContext ctx) =>
        {
            var ok = registry.Heartbeat(id);
            if (!ok)
            {
                FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/heartbeat: unknown id (caller should re-register)");
                // 410 Gone tells the Director "you're not in the registry anymore" so its
                // client can re-POST /directors/register instead of just retrying heartbeats.
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            // Issue #186: a new Director's heartbeat carries a per-session state snapshot -
            // the reconcile channel for lost doorbell pings. Old Directors POST no body.
            if (onSessionState is not null && ctx.Request.ContentLength > 0)
            {
                DirectorHeartbeatRequest? body = null;
                try { body = await ctx.Request.ReadFromJsonAsync<DirectorHeartbeatRequest>(ctx.RequestAborted); }
                catch (System.Text.Json.JsonException ex)
                {
                    FileLog.Write($"[GatewayEndpoints] heartbeat body unparsable from {id}: {ex.Message}");
                }
                if (body?.Sessions is { } sessions)
                {
                    // A state-carrying heartbeat (even with zero sessions) proves this
                    // Director pushes its own signals - the reconcile poll skips it.
                    registry.MarkStateReporting(id);
                    foreach (var s in sessions)
                        onSessionState(id, s.SessionId, s.ActivityState);
                }
            }
            return Results.Json(new { ok = true });
        });

        // Issue #186: the turn-end doorbell. The Director announces THAT a session's
        // mechanical state changed; the Gateway pulls the truth afterwards. Always 200 for
        // a known Director (a dropped observation costs nothing - the heartbeat reconciles);
        // 410 tells an unregistered Director to re-register first.
        app.MapPost("/directors/{id}/doorbell", (string id, DoorbellRequest req) =>
        {
            if (registry.Get(id) is null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (req is null || string.IsNullOrEmpty(req.SessionId) || string.IsNullOrEmpty(req.NewState))
                return Results.BadRequest(new { error = "sessionId and newState are required" });

            registry.MarkStateReporting(id);
            onSessionState?.Invoke(id, req.SessionId, req.NewState);
            return Results.Json(new { ok = true });
        });

        // Two-way connectivity handshake (issues #223/#224). The Director POSTs a fresh
        // nonce - this request ARRIVING proves Director->Gateway. The Gateway then proves
        // Gateway->Director by dialing the registered endpoint back with that nonce. PASS
        // requires both legs; the per-leg detail in the verdict IS the diagnosis ("you can
        // reach me but I cannot reach you at <url>: <error>") and feeds the Director's
        // troubleshooting ladder. A passing handshake stamps TwoWayVerifiedAt on the
        // registration so the Cockpit shows the identical, protocol-backed truth.
        app.MapPost("/directors/{id}/verify", async (string id, DirectorVerifyRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrEmpty(req.Nonce))
                return Results.BadRequest(new { error = "nonce is required" });
            var d = registry.Get(id);
            if (d is null)
            {
                // Same contract as heartbeat: 410 tells the Director to re-register first.
                FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/verify: unknown id (caller should re-register)");
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            var endpoint = (d.TailnetEndpoint ?? d.ControlEndpoint ?? "").TrimEnd('/');
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, error) = await client.VerifyCallbackAsync(endpoint, id, req.Nonce, ct);
            sw.Stop();

            if (ok)
            {
                registry.MarkTwoWayVerified(id);
                // A callback that answered is also a probe that answered: feed the
                // reachability circuit so an UNREACHABLE banner clears without waiting
                // for the next fleet poll to coincide with a closed breaker.
                registry.RecordReachable(id);
            }
            FileLog.Write($"[GatewayEndpoints] verify {id}: callbackOk={ok}, endpoint={endpoint}, {sw.ElapsedMilliseconds}ms{(ok ? "" : $", error={error}")}");

            return Results.Json(new DirectorVerifyResultDto
            {
                Verified = ok,
                Nonce = req.Nonce,
                CallbackOk = ok,
                CallbackError = error,
                CallbackEndpoint = endpoint,
                CallbackLatencyMs = sw.ElapsedMilliseconds,
                VerifiedAt = DateTime.UtcNow,
            });
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
                // Reachability circuit-breaker: a Director that has failed recent probes is skipped while
                // its breaker is open, so it stops costing a per-poll timeout. Still surfaced as an error
                // so the UI shows it as unreachable - with an ACTIONABLE message (issue #197): an endpoint
                // that never answered since registration is a provisioning problem on the Director's
                // machine (no tailscale serve mapping), not a transient outage. See DIRECTOR_LIVENESS_PLAN.md.
                if (!registry.ShouldProbe(d.DirectorId))
                {
                    var detail = registry.WasEverReachable(d.DirectorId)
                        ? $"unreachable ({registry.LastUnreachableError(d.DirectorId)}; cooling down)"
                        : $"endpoint never answered since registration ({registry.LastUnreachableError(d.DirectorId)}) - check Tailscale Serve / the Director log on {d.MachineName ?? "its machine"}";
                    return (Director: d, Sessions: (List<SessionDto>?)null, Error: detail);
                }

                var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
                var (sessions, error) = await client.ListSessionsWithStatusAsync(ep, includeExitedActual);
                if (error is null)
                    registry.RecordReachable(d.DirectorId);
                else
                    registry.RecordUnreachable(d.DirectorId, error);
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

                // Issue #291: this Director just answered (reachable), so its returned list is the
                // authoritative live set for it. Prune any session the cache still attributes to this
                // Director that is no longer live here - it exited or disappeared - so the per-session
                // WS proxy reverts to 404 instead of #288's 503 "owner offline". Computed from the raw
                // returned list (before the per-session view filters below) and excluding Exited rows
                // (a Director may include them when includeExited=true). Owners on OTHER Directors are
                // untouched, so an offline owner's sessions stay cached -> still 503 (#288 unchanged).
                var liveIds = new HashSet<string>(
                    sessions
                        .Where(x => !string.IsNullOrEmpty(x.SessionId)
                                 && !string.Equals(x.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.SessionId),
                    StringComparer.Ordinal);
                owners?.RetainForDirector(d.DirectorId, liveIds);

                var baseUrl = DeriveDirectorBaseUrl(ctx, d);
                var gatewayBaseUrl = DeriveGatewayBaseUrl(ctx);
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
                    // Issue #288: remember who owns this session so the WS proxy answers 503 (owner
                    // offline) instead of 404 once this Director goes dark.
                    owners?.Remember(s.SessionId, d.DirectorId);
                    // Issue #186: stamp the GATEWAY-owned assessedState. Suppressed while
                    // the session is mechanically working (raw activity always wins there);
                    // the Director's own annotation (if any) is overwritten - the Gateway
                    // is the one writer of this field on the aggregated view.
                    s.AssessedState = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : assessedStateFor?.Invoke(s.SessionId) ?? s.AssessedState;
                    // Issue #187: the Director-side pipeline that used to write these is
                    // deleted; the Gateway's brief agent is the one writer now (the yellow
                    // "wingman reading..." chip and the rail line both ride these fields).
                    if (briefStampFor is not null)
                    {
                        var (briefingState, railLine) = briefStampFor(s.SessionId);
                        s.BriefingState = briefingState;
                        s.RailLine = railLine;
                    }
                    // Stamp the deep link with the Gateway's own address (as this caller
                    // reached it) so the session view can offer a "back to Gateway" menu
                    // item. The session view is served by the Director on a different
                    // origin/port, so it cannot otherwise know where the Gateway lives.
                    s.ViewUrl = $"{baseUrl}/sessions/{s.SessionId}/view?gw={Uri.EscapeDataString(gatewayBaseUrl)}";
                    all.Add(s);
                }
            }

            if (envelope == true)
            {
                return Results.Json(new { sessions = all, machineErrors });
            }
            return Results.Json(all);
        });

        // Interrupted sessions (issue #212 W3): fan out to every Director for the crash
        // journals left on its machine by Directors that died abnormally, flatten to one row
        // per recoverable session, and enrich each with the Gateway's last-known brief so the
        // Cockpit Interrupted sessions list is triageable. Directors on one machine share the journal dir, so the
        // same dead journal can be reported by several live Directors - dedupe by directorId+pid.
        app.MapGet("/interrupted", async () =>
        {
            var directors = registry.ListDirectors();
            var fanout = directors.Select(async d =>
            {
                if (!registry.ShouldProbe(d.DirectorId)) return (Director: d, Journals: (List<CrashJournalDto>?)null);
                var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
                return (Director: d, Journals: await client.GetInterruptedAsync(ep));
            }).ToList();
            var results = await Task.WhenAll(fanout);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<InterruptedSessionDto>();
            foreach (var (d, journals) in results)
            {
                if (journals is null) continue;
                foreach (var j in journals)
                {
                    if (!seen.Add($"{j.DirectorId}.{j.Pid}")) continue; // already reported by a sibling Director
                    foreach (var s in j.Sessions)
                    {
                        var (railLine, headline) = interruptedBriefFor?.Invoke(s.SessionId) ?? (null, null);
                        outList.Add(new InterruptedSessionDto
                        {
                            SessionId = s.SessionId,
                            Name = s.Name,
                            RepoPath = s.RepoPath,
                            Agent = s.Agent,
                            ClaudeSessionId = s.ClaudeSessionId,
                            CreatedAtUtc = s.CreatedAtUtc,
                            DeadDirectorId = j.DirectorId,
                            DeadPid = j.Pid,
                            MachineName = j.MachineName,
                            User = j.User,
                            DiedAtUtc = j.LastUpdatedUtc,
                            ReportedByDirectorId = d.DirectorId,
                            RailLine = railLine,
                            Headline = headline,
                        });
                    }
                }
            }
            return Results.Json(outList.OrderByDescending(x => x.DiedAtUtc).ToList());
        });

        // Dismiss one interrupted journal once recovered or unwanted. Routed to the live
        // Director that surfaced it (via=reportedByDirectorId), which owns its machine's dir.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", async (string deadDirectorId, int deadPid, string? via) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            var d = registry.Get(via);
            if (d is null)
                return Results.NotFound(new { error = "reporting director not found" });
            var ok = await client.DismissInterruptedAsync(d.ControlEndpoint, deadDirectorId, deadPid);
            return ok ? Results.Json(new { dismissed = true }) : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Dismiss ONE session from an interrupted journal (issue #212 W4): the rest of the
        // journal stays in the Interrupted sessions list. Routed like the journal-level dismiss above.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            async (string deadDirectorId, int deadPid, string sessionId, string? via) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            var d = registry.Get(via);
            if (d is null)
                return Results.NotFound(new { error = "reporting director not found" });
            var ok = await client.RemoveInterruptedSessionAsync(d.ControlEndpoint, deadDirectorId, deadPid, sessionId);
            return ok ? Results.Json(new { removed = true }) : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Restore one interrupted session (issue #212 W4): create a CONTINUATION session -
        // a fresh session in the dead session's repo, seeded with a context document built
        // from the Gateway's surviving turn-brief history. Never `claude --resume`. The
        // continuation is created on req.ToDirectorId when given, else on the reporting
        // Director (req.Via) - the reporter shares the dead Director's machine, so the repo
        // path is valid there. After a successful create the restored session is removed
        // from the dirty journal so the Interrupted sessions list reflects what is still unrecovered.
        app.MapPost("/interrupted/{deadDirectorId}/{deadPid:int}/restore",
            async (string deadDirectorId, int deadPid, RestoreInterruptedRequest req) =>
        {
            FileLog.Write($"[GatewayEndpoints] POST /interrupted/{deadDirectorId}/{deadPid}/restore: sid={req?.SessionId} via={req?.Via} toDir={req?.ToDirectorId}");
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });

            var reporter = registry.Get(req.Via);
            if (reporter is null)
                return Results.NotFound(new { error = "reporting director not found" });
            var target = string.IsNullOrWhiteSpace(req.ToDirectorId) ? reporter : registry.Get(req.ToDirectorId);
            if (target is null)
                return Results.NotFound(new { error = "target director not found" });

            // The journal is the source of truth for what is restorable - never trust the
            // caller for repo/name. Re-read it from the reporting Director.
            var journals = await client.GetInterruptedAsync((reporter.ControlEndpoint ?? "").TrimEnd('/'));
            if (journals is null)
                return Results.Problem("reporting director did not serve its crash journals", statusCode: StatusCodes.Status502BadGateway);
            var journal = journals.FirstOrDefault(j =>
                string.Equals(j.DirectorId, deadDirectorId, StringComparison.OrdinalIgnoreCase) && j.Pid == deadPid);
            var row = journal?.Sessions.FirstOrDefault(s =>
                string.Equals(s.SessionId, req.SessionId, StringComparison.OrdinalIgnoreCase));
            if (journal is null || row is null)
                return Results.NotFound(new { error = "interrupted session not found in that journal (already restored or dismissed?)" });

            var briefs = briefHistoryFor?.Invoke(row.SessionId) ?? new List<TurnBriefDto>();
            var context = Recovery.RestoreContextBuilder.Build(
                row.Name, row.SessionId, row.RepoPath, row.ClaudeSessionId, journal.LastUpdatedUtc, briefs);

            // Spawning claude.exe takes seconds; the shared DirectorEndpointClient's 2s
            // aggregate timeout is too short here, and a timed-out create leaves an ORPHAN
            // (the Director finishes the spawn after the client gave up, so the session
            // exists but never gets renamed or journal-cleaned). Dedicated 20s client,
            // same as the cross-director handover's spawn leg above.
            var targetEp = (target.ControlEndpoint ?? "").TrimEnd('/');
            SessionDto? created;
            using (var spawnHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                var spawnResp = await spawnHttp.PostAsJsonAsync($"{targetEp}/sessions", new NewSessionRequest
                {
                    RepoPath = row.RepoPath,
                    Agent = row.Agent,
                    PrePrompt = context,
                });
                if (!spawnResp.IsSuccessStatusCode)
                {
                    var body = await spawnResp.Content.ReadAsStringAsync();
                    return Results.Problem(
                        $"target director failed to create the continuation session: HTTP {(int)spawnResp.StatusCode} - {body}",
                        statusCode: StatusCodes.Status502BadGateway);
                }
                created = await spawnResp.Content.ReadFromJsonAsync<SessionDto>();
            }
            if (created is null)
                return Results.Problem("target director returned an empty session body", statusCode: StatusCodes.Status502BadGateway);
            created.DirectorId = target.DirectorId;
            FileLog.Write($"[GatewayEndpoints] restore: created continuation {created.SessionId} on {target.DirectorId} for dead {row.SessionId}");

            // Give the continuation the dead session's name. Best-effort: a failed rename
            // does not undo a successful restore.
            var restoredName = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name;
            if (restoredName is not null)
            {
                var (patched, body, patchErr) = await client.PatchSessionAsync(targetEp, created.SessionId,
                    new SessionUpdateRequest { Name = restoredName });
                if (patched && body is not null) { body.DirectorId = target.DirectorId; created = body; }
                else FileLog.Write($"[GatewayEndpoints] restore: rename failed (continuing): {patchErr}");
            }

            // Pull the restored session out of the Interrupted sessions list. Best-effort too - the
            // user can still Dismiss the row by hand if this leg fails.
            var cleaned = await client.RemoveInterruptedSessionAsync(
                (reporter.ControlEndpoint ?? "").TrimEnd('/'), deadDirectorId, deadPid, row.SessionId);
            if (!cleaned)
                FileLog.Write($"[GatewayEndpoints] restore: journal cleanup failed for {row.SessionId} (row stays in the Interrupted sessions list)");

            return Results.Json(new RestoreInterruptedResponse
            {
                Restored = true,
                TargetSession = created,
                ContextSent = context,
                JournalCleaned = cleaned,
            }, statusCode: StatusCodes.Status201Created);
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
            session.ViewUrl = $"{baseUrl}/sessions/{session.SessionId}/view?gw={Uri.EscapeDataString(DeriveGatewayBaseUrl(ctx))}";
            return Results.Json(session);
        });

        // Forward "kill this session" to the owning Director so a remote client (the
        // phone) can shut a session down. Without this, DELETE only worked on the
        // Director's own Control API, never through the Gateway.
        app.MapDelete("/sessions/{sid}", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var ok = await client.KillSessionAsync(ep, sid);
            if (!ok)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(new { killed = true });
        });

        // Phase 4b: forward wingman observability through the Gateway so the merged
        // Session View on the gateway side can render WHY a dot is the color it is.
        app.MapGet("/sessions/{sid}/wingman", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var view = await client.GetWingmanAsync(ep, sid);
            if (view is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(view);
        });

        // Phase 5: forward "ask the wingman" calls. Each is one fresh side-call
        // (Haiku for free-text asks; Opus when Mode=="explain"). Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/ask", async (string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
            if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
                return Results.BadRequest(new WingmanAskResult { Status = "bad_request", Error = "question is required" });
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var result = await client.AskWingmanAsync(ep, sid, req, ct);
            if (result is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(result);
        });

        // Forward "set the session goal" to the owning Director. Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/goal", async (string sid, WingmanGoalRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var body = await client.SetWingmanGoalAsync(ep, sid, req ?? new WingmanGoalRequest(), ct);
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Forward the FIFO "park / un-park this session" (hold) call to the owning Director.
        app.MapPost("/sessions/{sid}/hold", async (string sid, HoldRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var ep = (director.ControlEndpoint ?? "").TrimEnd('/');
            var body = await client.SetHoldAsync(ep, sid, req ?? new HoldRequest(), ct);
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
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

        app.MapPost("/sessions/{sid}/escape", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            var ok = await client.PostEscapeAsync(director.ControlEndpoint, sid);
            return ok
                ? Results.Json(new { accepted = true })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Phone image upload: the browser POSTs the image to the Gateway (its origin); we
        // forward the bytes to the owning Director, which files it into its screenshots
        // folder (same machine as the session) and returns the saved absolute path.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionAsync(registry, client, sid);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an image file field 'file'" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no image uploaded; use form field 'file'" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);

            FileLog.Write($"[GatewayEndpoints] POST upload-image: sid={sid}, director={director.DirectorId}, bytes={ms.Length}");

            var (ok, path, fileName, err) = await client.UploadImageAsync(
                director.ControlEndpoint, sid, ms.ToArray(), file.FileName, file.ContentType, ctx.RequestAborted);
            if (!ok)
                return Results.Json(new { error = err }, statusCode: StatusCodes.Status502BadGateway);

            return Results.Json(new { path, fileName });
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

        app.MapDelete("/directors/{id}/repos", async (string id, string? path) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
            var removed = await client.DeleteRepoAsync(d.ControlEndpoint, path);
            return Results.Json(new { removed });
        });

        app.MapGet("/directors/{id}/coaching/categories", async (string id) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            var cats = await client.ListCoachingCategoriesAsync(d.ControlEndpoint);
            if (cats is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(cats);
        });

        app.MapGet("/directors/{id}/claude-sessions", async (string id) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            var sessions = await client.ListClaudeSessionsAsync(d.ControlEndpoint);
            if (sessions is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(sessions);
        });

        app.MapGet("/directors/{id}/handovers", async (string id) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            var handovers = await client.ListHandoversAsync(d.ControlEndpoint);
            if (handovers is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(handovers);
        });

        app.MapGet("/directors/{id}/handovers/content", async (string id, string? path) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
            var content = await client.GetHandoverContentAsync(d.ControlEndpoint, path);
            if (content is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(content);
        });

        app.MapGet("/directors/{id}/fs/list", async (string id, string? path) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            var listing = await client.ListDirectoryAsync(d.ControlEndpoint, path);
            if (listing is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(listing);
        });

        app.MapPost("/directors/{id}/sessions/github", async (string id, GitHubSessionRequest req) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
                return Results.BadRequest(new { error = "owner and repo are required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions/github: {req.Owner}/{req.Repo} mode={req.TriggerMode}");
            var (ok, body, err) = await client.CreateGitHubSessionAsync(d.ControlEndpoint, req);
            if (!ok)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        // Destructive-call gate (issue #212 W6/L4). A Director shutdown takes down every
        // claude.exe under it, so the request must (a) state a reason, and (b) when the
        // Director is reachable and has live sessions, confirm their count - a caller may
        // not kill sessions it did not know existed. Every branch logs loudly: the 2026-06-06
        // post-mortem found the force-kill path left no trace at all.
        app.MapDelete("/directors/{id}", async (HttpContext ctx, string id) =>
        {
            // Body read by hand instead of [FromBody]: an Accepts(application/json)
            // constraint would bounce body-less DELETEs off route matching, and a
            // body-less DELETE of an unknown id must still 404.
            ShutdownDirectorRequest body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ShutdownDirectorRequest>() ?? new ShutdownDirectorRequest();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "invalid JSON body" });
            }
            catch (InvalidOperationException)
            {
                // Not a JSON request (typically a body-less DELETE): empty request.
                body = new ShutdownDirectorRequest();
            }

            // Identify the caller: the tailnet IP for remote callers (phone), and additionally
            // the owning process for loopback callers like the Cockpit (issue #212 L3).
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var localPeer = Core.Network.LoopbackPeerResolver.Resolve(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            var caller = localPeer is null ? ip : $"{ip} [{localPeer}]";
            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} force={body.Force} " +
                $"confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} reason=\"{Truncate(body.Reason)}\" client={caller}");

            var director = registry.Get(id);
            if (director is null)
                return Results.NotFound(new { error = "director not found" });

            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director REJECTED (no reason): id={id} client={caller}");
                return Results.BadRequest(new { error = "reason is required: state why this Director is being shut down" });
            }

            var sessions = await client.ListSessionsAsync(director.ControlEndpoint);
            if (sessions is not null)
            {
                var live = sessions
                    .Where(s => !string.Equals(s.Status, "Exited", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (live.Count > 0 && body.ConfirmSessions != live.Count)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director BLOCKED by session gate: id={id} " +
                        $"liveSessions={live.Count} confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} client={caller}");
                    return Results.Json(new
                    {
                        error = $"director has {live.Count} live session(s); re-send with confirmSessions={live.Count} to proceed",
                        liveSessionCount = live.Count,
                        sessions = live.Select(s => new { s.SessionId, s.Name, s.RepoPath }).ToList(),
                    }, statusCode: StatusCodes.Status409Conflict);
                }
            }
            else
            {
                // Unreachable Director: the live count is unknowable, and an unreachable
                // Director is exactly the one an operator must still be able to stop.
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} live-session count UNKNOWN (director unreachable); session gate skipped");
            }

            var ok = await client.PostShutdownAsync(director.ControlEndpoint);
            if (ok)
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} pid={director.Pid} graceful shutdown accepted");
                return Results.Json(new { accepted = true });
            }

            if (body.Force)
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL: id={id} pid={director.Pid} " +
                    $"tree=true reason=\"{Truncate(body.Reason)}\" client={caller}");
                try
                {
                    var proc = Process.GetProcessById(director.Pid);
                    proc.Kill(entireProcessTree: true);
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL done: id={id} pid={director.Pid}");
                    return Results.Json(new { accepted = true, killed = true });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL FAILED: id={id} pid={director.Pid} error={ex.Message}");
                    return Results.Problem("could not kill process: " + ex.Message, statusCode: 500);
                }
            }

            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} graceful shutdown failed and force=false; nothing stopped");
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

            // Flush the response start NOW (SSE convention): events are not replayed,
            // so a subscriber must be able to treat "headers received" as "attached".
            // Without this Kestrel holds the headers until the first event is written.
            await ctx.Response.Body.FlushAsync(ct);

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

    // Locate the Director that owns a session. Every session endpoint calls this first,
    // so it fans out to all Directors in parallel rather than scanning them one-by-one:
    // total latency is bounded by the slowest single lookup (~the client timeout) instead
    // of summing one timeout per Director. Exactly one Director should own a given sid.
    private static async Task<(DirectorDto? director, SessionDto? session)> LocateSessionAsync(DirectorRegistry registry, DirectorEndpointClient client, string sid)
    {
        var lookups = registry.ListDirectors().Select(async d =>
        {
            var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
            var s = await client.GetSessionAsync(ep, sid);
            return (director: d, session: s);
        }).ToList();

        var results = await Task.WhenAll(lookups);
        foreach (var (director, session) in results)
            if (session is not null) return (director, session);
        return (null, null);
    }

    // Build the externally-reachable base URL for a Director's web UI.
    //
    // Priority:
    //   1. If the Director registered a TailnetEndpoint that is actually routable
    //      for THIS caller, trust it. A same-machine Director registers a loopback
    //      endpoint (http://127.0.0.1:<port>) which IS its control endpoint but is
    //      useless to a remote caller, so a loopback endpoint is honored only when
    //      the caller is itself on loopback.
    //   2. Else if the caller reached the Gateway over a non-loopback host
    //      (e.g. https://<host>.<tailnet>.ts.net/), mirror that host
    //      and the request scheme onto the Director's own Control API port.
    //      Tailscale Serve maps each Director port to the same number under
    //      HTTPS, so https://<tailnet>:<port>/ resolves correctly.
    //   3. Else fall back to the raw ControlEndpoint (loopback case).
    //
    // Without (2), ViewUrl returns http://127.0.0.1:<port>/... which is
    // unreachable from a phone or any non-loopback client.
    internal static string DeriveDirectorBaseUrl(HttpContext ctx, DirectorDto d)
    {
        var requestHost = ctx.Request.Host.Host;
        var callerIsLoopback = string.IsNullOrEmpty(requestHost)
                         || requestHost == "localhost"
                         || requestHost == "127.0.0.1"
                         || requestHost == "::1";

        // 1. Honor an explicitly registered endpoint, but never feed a loopback
        //    endpoint to a non-loopback caller (that is the phone-gets-127.0.0.1 bug).
        if (!string.IsNullOrEmpty(d.TailnetEndpoint)
            && Uri.TryCreate(d.TailnetEndpoint, UriKind.Absolute, out var tailnetUri)
            && (callerIsLoopback || !tailnetUri.IsLoopback))
        {
            return d.TailnetEndpoint.TrimEnd('/');
        }

        // 2. Remote caller: mirror the public host + scheme onto the Director's port.
        if (!callerIsLoopback
            && Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var controlUri)
            && controlUri.Port > 0)
        {
            return $"{ctx.Request.Scheme}://{requestHost}:{controlUri.Port}";
        }

        return (d.ControlEndpoint ?? "").TrimEnd('/');
    }

    // The Gateway's own externally-reachable base URL, exactly as THIS caller reached
    // it (scheme + host + optional port). Stamped onto session deep links so the
    // Director-served session view can link back to the Gateway directory it came from.
    internal static string DeriveGatewayBaseUrl(HttpContext ctx)
    {
        return $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";
    }

    // Quick liveness check: can we open a TCP connection to a loopback port? Used by
    // /cockpit to report whether the Cockpit process is actually accepting connections,
    // without a full HTTP round-trip. A 500ms ceiling keeps the endpoint snappy.
    private static async Task<bool> IsLoopbackPortOpenAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            return tcp.Connected;
        }
        catch (Exception)
        {
            // A refused or timed-out connect IS the answer this probe exists to give
            // (the Cockpit is not up); it is not an error to propagate.
            return false;
        }
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

    /// <summary>One-line-safe log form of a caller-supplied string (reason fields etc.).</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
    }
}
