using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway relay routes for cross-machine Director lifecycle management via cc-launcher.
///
/// Issue #331: the cc-launcher process on each machine registers with the Gateway
/// (POST /launchers/register) and heartbeats so the Gateway knows it is live. The
/// Gateway then exposes relay routes that push lifecycle verbs DOWN the persistent
/// stream that machine's launcher holds open to this Gateway:
///
///   POST /launchers/register                        launcher self-registers
///   POST /launchers/{machine}/heartbeat             launcher heartbeat
///   DELETE /launchers/{machine}                     graceful launcher unregister
///   GET  /launchers                                 list registered launchers
///
///   POST /machines/{machine}/director/restart       push -> launcher verb director/restart
///        body {"onlyIfEmpty": true} restarts ONLY while that Director holds no live sessions, and
///        answers 409 with the launcher's own sentence - naming how many are live - when it holds some
///   POST /machines/{machine}/director/start         push -> launcher verb director/start
///   POST /machines/{machine}/director/stop          push -> launcher verb director/stop
///   POST /machines/{machine}/launch                 push -> launcher verb launch
///   GET  /machines/{machine}/apps                   push -> launcher verb apps
///   GET  /machines/{machine}/files                  push -> launcher verb files
///
/// Relay calls are token-gated (Gateway Bearer) and audit-logged. A slot guard in the
/// relay refuses restart/stop targeting the main Director build or slots 1-4 unless the
/// request carries <c>"confirmProtected": true</c>.
///
/// HOW A COMMAND CROSSES MACHINES (remove-the-network-port mission, phase 6). The launcher LISTENS ON
/// NOTHING - it has no REST interface, no port and no bearer token. It DIALS OUT to this Gateway's
/// /launcher-stream hub and keeps that connection open, and every verb above is delivered down that
/// connection (<see cref="LauncherLifecycleRelay"/>). Local and remote machines are therefore the same
/// case: whichever machine the launcher is on, IT opened the connection, so there is no dial-back
/// address, no loopback shortcut, and no second path when the stream is down - an unconnected launcher
/// is refused loudly, never reached another way. The registration surface above is presence-and-identity
/// metadata for listings; delivery is decided solely by the stream connection existing.
///
/// TENANT-SCOPED ON HOSTED, NOT DENIED (issue #1917 closed the leak by DENY; this replaces that deny with
/// the authorization it was always a placeholder for). A tenant has FULL control of every device registered
/// to its Gateway - hosted or self-hosted, no difference - including starting and stopping sessions and the
/// launcher on any of them, and including when an agent does it on the owner's behalf. Cross-machine control
/// IS the product, not a feature of it - a fleet you cannot start work on is not a fleet. (The principle is
/// recorded in full in the PRIVATE architecture record. It is restated here rather than linked, because the
/// comment this replaced was itself an authoritative-sounding rationale that outlived the code it described,
/// and a link out of the file is exactly how that survives a second time.)
///
/// WHAT THE DENY WAS FOR, AND WHY IT IS NO LONGER THE ANSWER. This family used to be TENANT-BLIND BY
/// CONSTRUCTION - no tenant dimension anywhere in the path - so on hosted any authenticated device key could
/// enumerate every tenant's machines through GET /launchers and then drive the machine routes AGAINST
/// ANOTHER TENANT'S MACHINE: cross-machine CODE EXECUTION via POST /machines/{machine}/launch, and
/// OUTBOUND-REQUEST FORGERY via POST /launchers/register, which - when the relay still dialed launchers over
/// HTTP - could overwrite a machine's stored token, port and network address and so re-point the relay at an
/// arbitrary host. That hole was real and it had to close.
/// The deny closed it by removing the capability from every hosted tenant on their OWN machines, which is the
/// one thing the product must never do. The correct close - stated in the deny's own un-deny instruction and
/// now implemented - is to bind machine identity to a tenant at REGISTRATION and authorize every call against
/// the CALLING tenant.
///
/// THE TENANT IS NEVER TAKEN FROM THE REQUEST. Every route resolves the calling tenant from the AUTHENTICATED
/// DEVICE KEY (<see cref="GatewayEndpoints.ResolveReadTenant"/> over
/// <see cref="Tenancy.HostedTenantBoundary"/>), never from the machine name in the path or from anything in
/// the body. A machine name is client-supplied and is unique only WITHIN a tenant; the owning tenant is
/// proven, not claimed. Deny-by-default: a hosted key that resolves to no tenant gets 403 and reaches
/// nothing - it never falls back to the Local or System partition.
///
/// SO THE KEY IS COMPOSITE, EVERYWHERE. <see cref="LauncherRegistry"/> keys on (tenant, machine) and
/// <see cref="Streaming.LauncherConnectionRegistry"/> keys on (tenant, machine), so one tenant naming a
/// machine can only ever reach its OWN entry - another tenant's launcher registered under the same bare name
/// is a DIFFERENT row and is not consulted, not listed, not overwritten and not removable.
/// <see cref="Streaming.LauncherHub"/>.Hello - the /launcher-stream half, which is not an HTTP route at all -
/// binds its connection to the tenant of its own authenticated device key and ABORTS when that key resolves
/// to none, exactly as <see cref="Streaming.DirectorHub"/>.Hello does. There is no writer left that can
/// deposit a tenant-blind row.
///
/// BOTH DISPATCH ARMS ARE SCOPED. (There used to be a third - an HTTP relay that dialed the launcher's
/// loopback REST interface as the fallback when the stream arm returned null, and the history matters
/// because the old deny called out precisely that an ungated failure path is a gate that opens exactly when
/// something is already going wrong. The remove-the-network-port mission deleted that arm outright in phase
/// 6: the launcher no longer listens, so there is nothing to dial and no fallback to gate.) The two that
/// remain both scope on the calling tenant:
///   * the Director tunnel - the session spawn enters the caller's tenant scope, so the transport's own
///     fail-closed resolves the caller's Director and no one else's;
///   * the launcher stream - behind <see cref="LauncherLifecycleRelay"/>, which takes the tenant as an
///     ARGUMENT and resolves the connection and the registry row as (tenant, machine), refusing loudly when
///     the caller's launcher is not stream-connected.
///
/// THE PURGE THE UN-DENY INSTRUCTION REQUIRED IS DISCHARGED BY CONSTRUCTION. It asked for the launcher and
/// launcher-connection registries to be purged of rows accumulated behind the deny, deny-closed on the safe
/// side until write coverage was proven complete. Write coverage is now complete (above), and both registries
/// are process-lifetime IN-MEMORY dictionaries: <c>GatewayHost.Launchers</c> is a plain <c>new()</c>, there is
/// no launcher entity in the database, no snapshot, and nothing reloads either registry at startup. A Gateway
/// restart is therefore a full purge, and shipping this performs one. Nothing survives to be re-served.
///
/// SELF-HOST IS UNCHANGED AND IS STILL THE CONTROL. There the single tenant is Local, every authenticated
/// caller resolves to it, and the composite key degenerates to the machine name it always was - the same
/// routes, the same relay, the same behaviour, byte for byte.
///
/// WHAT THIS DELIBERATELY DOES NOT DO: reach across tenants, ever. The capability restored here is a tenant's
/// control of ITS OWN registered devices. There is no route, argument or credential in this file by which one
/// subscriber can see, start, stop or enumerate another subscriber's machines.
/// </summary>
internal static class MachineEndpoints
{
    /// <summary>
    /// Slots 1-4 and the main slot (0) are protected from unconfirmed restart/stop.
    /// Agents run on slots >= 5 (issue #331 spec: "slots 5+" relay freely; the slot-guard
    /// refuses main + 1-4 without explicit confirm).
    /// </summary>
    private static readonly Regex SlotFromPath =
        new(@"cc-director(\d*)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The launcher self-registration prefix this family owns.</summary>
    internal const string LauncherPrefix = "/launchers";

    /// <summary>The machine relay prefix this family owns.</summary>
    internal const string MachinePrefix = "/machines";

    /// <summary>
    /// Maps both surfaces - <see cref="LauncherPrefix"/> and <see cref="MachinePrefix"/> - IDENTICALLY on
    /// hosted and on self-host. There is no hosted branch here any more: the difference between the two
    /// deployments is not which routes exist, it is which TENANT the calling device key resolves to, and that
    /// is decided per request inside the handlers rather than per route at startup.
    ///
    /// This replaced a pair of <c>HostedRouteDeny.ExclusiveGroup</c> claims that refused both prefixes
    /// outright on hosted. The refusal was a placeholder for the authorization that is now in place; keeping
    /// it would keep hosted tenants locked out of their own machines, which the tenant-device-control
    /// principle forbids.
    /// </summary>
    public static void Map(IEndpointRouteBuilder outer, LauncherRegistry launchers,
        MachineSessionSpawner spawner,
        // The tenant boundary. Every launcher-registry read/write and every relay is scoped to the CALLING
        // tenant, resolved from the authenticated device key (never the machine name in the path or body).
        // REQUIRED, not defaulted (tenant-boundary hardening, release 2026-07-31, finding CR-7): the boundary
        // is a security argument, and when it was optional a forgotten argument silently served the Local
        // partition on hosted. A self-host-only caller must state the absence with an explicit null.
        HostedTenantBoundary? boundary,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand = null,
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store. When non-null, a
        // mission-scoped spawn (req.MissionId set) is validated against it here - the Gateway is the source
        // of truth - and the resolved mission NAME is stamped onto the create request so the Director stamps
        // the attachment without any local lookup. Null (old callers, tests) leaves MissionId to flow through
        // to the Director's transitional local-store bridge unchanged.
        Core.Sessions.MissionStore? missions = null,
        // Workflows mission (phase 5b, issue #1771): the workflow-run store. When non-null, a spawn is
        // SEATED on a run: an explicit req.WorkflowRunId is validated here and the run's workflow id +
        // pinned version are stamped onto the create request (the MissionName pattern); a mission-scoped
        // spawn with no explicit run auto-seats onto the mission's run. After a successful spawn the new
        // session is recorded as a run PARTICIPANT - the persisted run-to-session membership governance
        // reads. Null (old callers, tests) seats nothing and changes nothing.
        Workflows.WorkflowRunStore? workflowRuns = null)
    {
        if (spawner is null) throw new ArgumentNullException(nameof(spawner));

        FileLog.Write($"[MachineEndpoints] mapping {LauncherPrefix} + {MachinePrefix}; hosted={GatewayHostedMode.IsHosted} - every route authorizes against the CALLING tenant, resolved from the authenticated device key");

        MapLauncherRoutes(outer.MapGroup(LauncherPrefix), launchers, boundary);
        MapMachineRoutes(outer.MapGroup(MachinePrefix), launchers, spawner, sendLauncherCommand, missions, workflowRuns, boundary);
    }

    /// <summary>The calling tenant, resolved from the authenticated device key. Null means no tenant is
    /// bound (hosted with an unbound key) - the caller must refuse with 403.</summary>
    private static TenantId? ReqTenant(HttpContext ctx, HostedTenantBoundary? boundary)
        => GatewayEndpoints.ResolveReadTenant(ctx, boundary);

    private static IResult NoTenant() =>
        Results.Json(new { error = "no tenant is bound to this request" }, statusCode: 403);

    /// <summary>
    /// Session origin (devthrottle_internal issue #982), stamped GATEWAY-AUTHORITATIVELY on the spawn
    /// relay - the same rule <c>PromptRequest.Surface</c> already follows for turns.
    ///
    /// This route is the one spawn path a caller outside the owner's machines can reach, so what a
    /// client CLAIMS about its own origin cannot be the record. When the verified per-device key says
    /// the caller is a signed-in phone or browser, we know two things by construction - a person is
    /// holding it, and which surface it is - and both are OVERWRITTEN here, along with any parent
    /// session the client named (a phone is nobody's child).
    ///
    /// Every OTHER caller is left exactly as it arrived, and that is the important half. A remote spawn
    /// from an agent reaches this route relayed by its own Director over the tunnel, carrying that
    /// Director's key rather than a device key; the Director already stamped the truth on the loopback
    /// floor, and overwriting it here would erase the agent lineage on precisely the cross-machine
    /// spawns - one session driving work on another computer - that make the lineage worth having.
    /// </summary>
    private static void StampOriginFromDeviceKey(NewSessionRequest req, HttpContext ctx)
    {
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        var surface = Core.Sessions.SessionOriginSurfaces.FromDeviceType(deviceType);
        if (surface == Core.Sessions.SessionOriginSurfaces.Unknown)
            return; // not a person's device - keep what the relaying Director stated

        req.Origin = Core.Sessions.SessionOriginKinds.Human;
        req.OriginSurface = surface;
        req.ParentSessionId = null;
        FileLog.Write($"[MachineEndpoints] spawn origin stamped from the verified device key: human/{surface} (deviceType={deviceType})");
    }

    /// <summary>
    /// The four launcher self-registration routes, mapped relative to <see cref="LauncherPrefix"/> so the full
    /// paths are <c>/launchers</c> and <c>/launchers/{machine}/...</c>. All three
    /// <see cref="LauncherRegistry"/> writers - Upsert (register), Heartbeat, Remove (unregister) - live here,
    /// and every one of them takes the CALLING tenant, so there is no way to write a row into a partition the
    /// caller does not own.
    /// </summary>
    private static void MapLauncherRoutes(IEndpointRouteBuilder app, LauncherRegistry launchers, HostedTenantBoundary? boundary)
    {
        // ===== Launcher self-registration surface =====
        // The launcher's own machine name is client-supplied; the OWNING TENANT is not - it is resolved from
        // the launcher's authenticated device key, so a launcher registers only into its own tenant partition.

        // POST /launchers/register - the launcher POSTs this on startup and after reconnects.
        // Phase 6 of the remove-the-network-port mission: no port and no token any more. The machine name
        // is the only required field, because the registration is presence metadata - command delivery is
        // the stream connection, which authenticates and binds its tenant on its own (LauncherHub.Hello).
        app.MapPost("/register", (LauncherRegistrationRequest req, HttpContext ctx) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.MachineName))
                return Results.BadRequest(new { error = "machineName is required" });
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            FileLog.Write($"[MachineEndpoints] POST /launchers/register: tenant={tenant.Value}, machine={req.MachineName}, pid={req.Pid}, version={req.Version}");
            var dto = launchers.Upsert(tenant, req);
            return Results.Json(dto, statusCode: 201);
        });

        // POST /launchers/{machine}/heartbeat - keep-alive from the launcher every 30 s.
        app.MapPost("/{machine}/heartbeat", (string machine, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var ok = launchers.Heartbeat(tenant, machine);
            if (!ok)
            {
                FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: unknown -> 410");
                return Results.StatusCode(410);
            }
            FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: ok");
            return Results.Json(new { ok = true });
        });

        // DELETE /launchers/{machine} - graceful unregister on launcher shutdown.
        app.MapDelete("/{machine}", (string machine, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            launchers.Remove(tenant, machine);
            FileLog.Write($"[MachineEndpoints] DELETE /launchers/{machine}: removed");
            return Results.Json(new { ok = true });
        });

        // GET /launchers - list the CALLING TENANT's registered launchers (never another tenant's).
        app.MapGet("", (HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var list = launchers.ListLaunchers(tenant);
            FileLog.Write($"[MachineEndpoints] GET /launchers: tenant={tenant.Value}, count={list.Count}");
            return Results.Json(list);
        });
    }

    /// <summary>
    /// The five machine relay routes, mapped relative to <see cref="MachinePrefix"/> so the full paths are
    /// <c>/machines/{machine}/...</c>. Every one of them resolves the calling tenant first and refuses with
    /// 403 when none is bound.
    /// </summary>
    private static void MapMachineRoutes(IEndpointRouteBuilder app, LauncherRegistry launchers,
        MachineSessionSpawner spawner,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        Core.Sessions.MissionStore? missions,
        Workflows.WorkflowRunStore? workflowRuns,
        HostedTenantBoundary? boundary)
    {
        // ===== Machine relay surface =====
        // The target machine name is in the path; the caller's TENANT comes from the authenticated key, and
        // the launcher/connection is resolved as (callerTenant, machine) - so a caller can only ever reach a
        // launcher its OWN tenant registered, never another tenant's machine of the same bare name.

        // POST /machines/{machine}/director/restart
        app.MapPost("/{machine}/director/restart", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/restart: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "restart", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/director/start
        app.MapPost("/{machine}/director/start", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/start: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "start", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/sessions - "start a session on another computer". Resolve the machine
        // to a Director (auto-launching one via the launcher if none is running) and create the session
        // there through the SAME resolve-then-create path the cron firing engine uses. Fail loud: an
        // off/unreachable machine or a create failure returns 502 with the error - NEVER a local spawn.
        app.MapPost("/{machine}/sessions", async (string machine, NewSessionRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: repo={req?.RepoPath}, agent={req?.Agent}");
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            // Re-enter the caller tenant scope for the spawn, and hold it across the await. Everything
            // downstream resolves the tenant AMBIENTLY rather than by argument: the target resolver lists the
            // machine Directors within the current tenant, the auto-launch reaches only this tenant launcher,
            // and the create rides GatewayHost.SendCommandAsync, which resolves the Director tunnel connection
            // in the current tenant and DROPS the command when no scope is in effect.
            //
            // THIS IS DEFENCE IN DEPTH, NOT A FIX - AND THE FIX READING WAS TESTED AND DISPROVED, so do not
            // restore it. On hosted the request is ALREADY inside this exact scope: GatewayHost.cs registers a
            // hosted-only middleware that resolves ResolveRequestTenant(ctx) and wraps the whole pipeline in
            // EnterScope. ReqTenant above calls that same resolver, so whenever it yields a tenant the
            // middleware has already entered it. An earlier reading held that the mission and workflow-run
            // reads above ran unscoped and would throw deny-by-default on hosted; that was checked by putting
            // this line back BELOW those reads and running the hosted workflow-run tests, which stayed green.
            // What this line buys is that the route does not depend on an outer middleware for a fail-closed
            // property. Scopes nest and restore on dispose, so re-entering the same tenant costs nothing.
            using var tenantScope = boundary is null ? null : boundary.EnterScope(tenant);

            StampOriginFromDeviceKey(req, ctx);

            // The mission NAME and the workflow SEAT, resolved in the ONE place both spawn doors share
            // (issue #2629 - this route and POST /directors/{id}/sessions had drifted, and the Director
            // door's missing name took mission-scoped spawning down completely).
            var route = $"POST /machines/{machine}/sessions";
            if (!SpawnMissionAndSeat.TryResolve(req, tenant, missions, workflowRuns, route, out var seatRun, out var resolveError))
                return resolveError!;

            var (ok, dto, error, _) = await spawner.SpawnOnMachineAsync(machine, req, ct);
            if (!ok || dto is null)
            {
                FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions FAILED: {error}");
                return Results.Json(new { error = error ?? $"could not start a session on '{machine}'", machine }, statusCode: 502);
            }

            // The run-participant record, in the same shared place the resolution lives.
            SpawnMissionAndSeat.RecordParticipant(seatRun, workflowRuns, req, dto, machine, route);

            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: started sid={dto.SessionId}" +
                          (seatRun is null ? "" : $", seated on run {seatRun.Id} ({seatRun.WorkflowId} v{seatRun.WorkflowVersion})"));
            return Results.Json(dto, statusCode: 201);
        });

        // POST /machines/{machine}/director/stop
        app.MapPost("/{machine}/director/stop", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/stop: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "stop", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/launch - relay a generic launch request to the launcher.
        app.MapPost("/{machine}/launch", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/launch: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            // Forward the original request body verbatim to the launcher.
            LaunchRelayBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<LaunchRelayBody>(ct); }
            catch { /* treat as null -> launcher will 400 */ }

            // Tenant-boundary hardening (release 2026-07-31, finding CR-5): starting a program on a machine is
            // always a protected action, so EVERY launch requires the same explicit confirmation the sibling
            // restart/stop slot guard demands - it used to relay on key possession alone, which made a stolen
            // key remote code execution across the account. This is the accident guard; the authorization half
            // is the launcher's installed-applications allowlist (AppCatalog.ResolveLaunchPath), enforced in
            // the launcher process itself so no relay arm can bypass it.
            if (body?.ConfirmProtected != true)
            {
                var reason = "launch guard: refusing to start a program without confirmProtected=true";
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb=launch reason={reason}");
                return Results.Json(new
                {
                    error = "launch_guard",
                    detail = reason,
                    machine,
                    verb = "launch",
                    hint = "Set confirmProtected=true to confirm starting a program on this machine.",
                }, statusCode: 403);
            }

            // Tenant-boundary hardening (release 2026-07-31, inspection finding I1-03). On a HOSTED
            // process the launch verb accepts NO caller-supplied arguments and no caller-supplied working
            // directory: the catalogue entry ALONE determines what runs.
            //
            // Why: the catalogue allowlist on its own was never containment. Every ordinary machine has a
            // command interpreter or script host in its installed applications, so a caller could select
            // that catalogued entry and put the real command in the argument string - the launcher
            // interpolates it into the command line it starts. That is arbitrary code execution wearing an
            // installed application's name, which is the capability this mission exists to remove.
            //
            // Self-host is deliberately UNCHANGED - the desktop and the local agent keep passing arguments,
            // so no capability is deleted, only narrowed on the surface a stolen tenant credential can reach.
            // This is reversible when the credential-authority tiers land (the named gap from ruling 6).
            //
            // The refusal is EXPLICIT rather than a silent drop: quietly discarding the arguments would
            // start a DIFFERENT program than the caller asked for and report success, which is its own
            // failure mode and a worse one to debug.
            // The rule is about the FIELD BEING PRESENT, not about what is in it (inspection finding
            // M03-I2B-02). The first version of this guard tested IsNullOrWhiteSpace and then
            // forwarded the original object, so an empty or whitespace-only value passed a rule that
            // says no caller-supplied arguments and no caller-supplied working directory are
            // accepted. That is not merely untidy: an EMPTY working directory is not "no working
            // directory" downstream - it moves the launcher's choice from the application's own
            // directory to the process default, which is a caller-influenced change to how the
            // program starts. A JSON null is treated as absent because it is indistinguishable from
            // an omitted property and carries nothing from the caller either way.
            var suppliedArgs = body?.Args is not null;
            var suppliedCwd = body?.Cwd is not null;
            if (GatewayHostedMode.IsHosted && (suppliedArgs || suppliedCwd))
            {
                var supplied = suppliedArgs && suppliedCwd
                    ? "arguments and a working directory"
                    : suppliedArgs ? "arguments" : "a working directory";
                var reason = $"launch guard: this hosted service does not accept {supplied} on a launch request";
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb=launch reason={reason}");
                return Results.Json(new
                {
                    error = "launch_arguments_not_allowed",
                    detail = reason,
                    machine,
                    verb = "launch",
                    hint = "Start the application by its catalogue name or path with no args and no cwd. "
                         + "The catalogue entry determines what runs.",
                }, statusCode: 403);
            }

            var outcome = await LauncherLifecycleRelay.SendLaunchAsync(
                tenant, machine, body, launchers, sendLauncherCommand, ct);
            return ToResult(machine, "launch", outcome);
        });

        // GET /machines/{machine}/apps - what is installed on that machine, so a caller can find something to
        // start without knowing any of its paths.
        app.MapGet("/{machine}/apps", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] GET /machines/{machine}/apps: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            var query = ctx.Request.Query["q"].ToString();
            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);

            var outcome = await LauncherLifecycleRelay.SendQueryAsync(
                tenant, machine, "apps", query, limit, timeoutMilliseconds: 0, launchers, sendLauncherCommand, ct);
            return ToQueryResult(machine, "apps", outcome);
        });

        // GET /machines/{machine}/files - a filename search across that machine's drives.
        app.MapGet("/{machine}/files", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] GET /machines/{machine}/files: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            var query = ctx.Request.Query["q"].ToString();
            if (string.IsNullOrWhiteSpace(query))
                return Results.Json(new { error = "q is required for a file search", machine }, statusCode: 400);

            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            _ = int.TryParse(ctx.Request.Query["timeoutMilliseconds"].ToString(), out var timeout);

            var outcome = await LauncherLifecycleRelay.SendQueryAsync(
                tenant, machine, "files", query, limit, timeout, launchers, sendLauncherCommand, ct);
            return ToQueryResult(machine, "files", outcome);
        });
    }

    /// <summary>
    /// Render a QUERY outcome as the HTTP answer.
    ///
    /// It differs from <see cref="ToResult"/> in one way that matters to every caller: a successful query
    /// returns the launcher's own document AS the response body, rather than wrapping it in a relay envelope
    /// with the answer buried in a string field. A caller asking a machine what it has installed should get
    /// the catalogue, not a description of a relay hop that happens to contain one.
    ///
    /// Every failure shape is left identical to the action verbs, including the deliberate merging of
    /// "no such machine" with "that machine belongs to another tenant".
    /// </summary>
    private static IResult ToQueryResult(string machine, string verb, LauncherLifecycleRelay.LauncherRelayOutcome outcome)
    {
        if (outcome.Kind != LauncherLifecycleRelay.RelayOutcomeKind.Relayed)
            return ToResult(machine, verb, outcome);

        // The launcher's body is already JavaScript Object Notation - its own answer on success, its own error
        // document on failure - so it is passed through untouched at the status the launcher chose. An empty
        // body would mean the launcher answered with nothing at all, which is a relay fault rather than an
        // answer, and is reported as one instead of being served as an empty document.
        if (string.IsNullOrWhiteSpace(outcome.Payload))
        {
            FileLog.Write($"[MachineEndpoints] {verb} on {machine}: relayed {outcome.RelayStatus} with an EMPTY body");
            return Results.Json(new { error = $"the launcher on '{machine}' returned no answer for '{verb}'", machine },
                statusCode: 502);
        }

        return Results.Content(outcome.Payload, "application/json; charset=utf-8", statusCode: outcome.RelayStatus);
    }

    /// <summary>
    /// Render a relay outcome as the HTTP answer.
    ///
    /// The not-registered case is 404 and says so in the caller's terms. It is reached both when nobody has
    /// registered that machine and when ANOTHER TENANT has registered a machine of that name - those are the
    /// same answer on purpose, because a distinguishable "exists, but not yours" would let one subscriber
    /// enumerate another's machines one name at a time.
    ///
    /// The undeliverable cases are 502 and there are TWO of them, deliberately, because they have two
    /// different fixes. A launcher that has gone quiet has crashed or lost the network; a launcher that is
    /// still heartbeating while holding no stream is TOO OLD to accept stream commands - it expected the
    /// Gateway to dial its own listener, and that relay is deleted. One message for both would tell the
    /// second user to check a connection that is provably working. Each carries the registered version and
    /// how long since the last heartbeat, so the answer shows its evidence rather than asserting a cause.
    /// A machine-readable `reason` travels with them for a client that wants to act on the difference.
    /// </summary>
    private static IResult ToResult(string machine, string verb, LauncherLifecycleRelay.LauncherRelayOutcome outcome)
        => outcome.Kind switch
        {
            LauncherLifecycleRelay.RelayOutcomeKind.Relayed => Results.Json(new RelayResult
            {
                Machine = machine,
                Verb = verb,
                RelayStatus = outcome.RelayStatus,
                Payload = outcome.Payload ?? "",
            }, statusCode: outcome.RelayStatus),

            LauncherLifecycleRelay.RelayOutcomeKind.NoLauncher => Results.Json(
                new { error = $"no launcher registered for machine '{machine}'", machine }, statusCode: 404),

            // STILL HEARTBEATING, STILL UNREACHABLE - a different condition with a different fix, and it
            // used to share the message below. A launcher predating the stream reaches this Gateway
            // perfectly and opens no stream, so telling its owner to check the network sends them to
            // examine the one thing that is demonstrably working.
            LauncherLifecycleRelay.RelayOutcomeKind.NotStreamCapable => Results.Json(
                new
                {
                    // THE SHARED TRUTH STAYS IN THE SENTENCE. Both undeliverable cases mean the launcher
                    // is not connected for commands, and that phrase is as true here as it is below - the
                    // split ADDS the reason, it does not make the shared fact false. Two tests assert that
                    // phrase and they were right to: dropping it to make room for the new detail would have
                    // narrowed what the message promises while looking like an improvement.
                    error = $"the launcher on '{machine}' is registered but not connected for commands: it is "
                          + $"reaching this Gateway (it heartbeated {outcome.QuietForSeconds}s ago, version "
                          + $"'{outcome.LauncherVersion}') and yet holds no command stream. A launcher that "
                          + $"talks to this Gateway while opening no stream is too old to accept commands from "
                          + $"it: update the launcher on that machine. Its network connection is not the problem.",
                    machine,
                    verb,
                    launcherVersion = outcome.LauncherVersion,
                    reason = "launcher-too-old",
                }, statusCode: 502),

            _ => Results.Json(
                new
                {
                    error = $"the launcher on '{machine}' is registered but not connected to this Gateway - it "
                          + $"has stopped talking to it altogether (last heartbeat {outcome.QuietForSeconds}s "
                          + $"ago), so the command could not be delivered. Commands reach a launcher only over "
                          + $"the connection it opens to the Gateway; check that machine's launcher is running "
                          + $"and can reach this Gateway.",
                    machine,
                    verb,
                    launcherVersion = outcome.LauncherVersion,
                    reason = "launcher-not-connected",
                }, statusCode: 502),
        };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Relay a director lifecycle verb (restart/start/stop) to the target machine's launcher.
    /// Enforces the slot guard for restart/stop verbs.
    /// </summary>
    private static async Task<IResult> RelayDirectorLifecycleAsync(
        string machine, string verb, HttpContext ctx, LauncherRegistry launchers,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand, HostedTenantBoundary? boundary, CancellationToken ct)
    {
        if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

        // Parse optional body for confirmProtected flag and target exe path.
        // Use JsonDocument to avoid internal-class reflection issues with System.Text.Json.
        // Do NOT gate on ContentLength - transfer-encoded bodies may have no explicit length.
        string? exePathFromBody = null;
        bool? confirmProtectedFromBody = null;
        var onlyIfEmptyFromBody = false;

        // THE BODY IS READ, NOT GUESSED AT. This used to decide whether a body existed from the content
        // type or a declared length, and that heuristic was wrong in both directions: an empty body
        // labelled as JavaScript Object Notation was parsed and failed, while a chunked body - which
        // declares no length - carrying "onlyIfEmpty" was treated as no body at all and quietly became an
        // unconditional restart. Reading the bytes answers both: nothing sent is absent, anything sent
        // must parse.
        ctx.Request.EnableBuffering();
        using var bodyReader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var bodyText = await bodyReader.ReadToEndAsync(ct);
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(bodyText);
            }
            catch (JsonException ex)
            {
                // A BODY THAT WAS SENT AND WILL NOT PARSE IS A 400, NOT AN EMPTY BODY. No body at all is
                // fine - every field here is optional - but silently discarding a body the caller did
                // send discards whatever safety flag was in it, and this route's flags decide whether
                // somebody's live sessions survive.
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb={verb} reason=unparseable body: {ex.Message}");
                return Results.Json(new
                {
                    error = "bad_request_body",
                    detail = "the request body was sent but could not be read as JavaScript Object Notation, "
                           + "so nothing was done. It is not treated as an empty body: a flag that was meant "
                           + "to be in it would have been dropped in silence.",
                    machine,
                    verb,
                }, statusCode: 400);
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.Json(new
                    {
                        error = "bad_request_body",
                        detail = $"the request body must be a JavaScript Object Notation object; this one is "
                               + $"{doc.RootElement.ValueKind}. Nothing was done.",
                        machine,
                        verb,
                    }, statusCode: 400);

                if (doc.RootElement.TryGetProperty("exePath", out var ep))
                    exePathFromBody = ep.GetString();
                if (doc.RootElement.TryGetProperty("confirmProtected", out var cp) && cp.ValueKind == JsonValueKind.True)
                    confirmProtectedFromBody = true;

                if (ReadOnlyIfEmpty(doc.RootElement, machine, verb, out onlyIfEmptyFromBody) is { } flagRefusal)
                    return flagRefusal;
            }
        }

        // ONLY-IF-EMPTY BELONGS TO RESTART, AND ASKING FOR IT ANYWHERE ELSE IS REFUSED RATHER THAN IGNORED.
        // A caller sending it to stop is asking not to interrupt live work; quietly dropping the flag and
        // stopping the Director anyway would answer that request with the exact outcome it was trying to
        // prevent, and report success. A stop that must not interrupt anything has no implementation here
        // yet - so the honest answer is to say so.
        if (onlyIfEmptyFromBody && verb != "restart")
            return OnlyIfEmptyNotUnderstood(machine, verb,
                $"onlyIfEmpty is understood by 'restart' alone, and this is '{verb}'");

        // Slot guard: refuse restart/stop targeting the main build or slots 1-4 without confirm.
        if ((verb == "restart" || verb == "stop") && exePathFromBody is { } exePath)
        {
            var (isProtected, slotDesc) = IsProtectedSlot(exePath);
            if (isProtected && confirmProtectedFromBody != true)
            {
                var reason = $"slot guard: refusing {verb} of protected Director ({slotDesc}) without confirmProtected=true";
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb={verb} reason={reason}");
                return Results.Json(new
                {
                    error = "slot_guard",
                    detail = reason,
                    machine,
                    verb,
                    exePath,
                    hint = "Set confirmProtected=true to override (human-confirmed action only).",
                }, statusCode: 403);
            }
        }

        // Dispatch lives in LauncherLifecycleRelay: the persistent launcher stream, resolved on
        // (tenant, machine), and nothing else - an unconnected launcher is refused, never dialed. The slot
        // guard above has already run before anything is delivered.
        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(
            tenant, machine, verb, exePathFromBody, confirmProtectedFromBody == true,
            launchers, sendLauncherCommand, ct, onlyIfEmptyFromBody);
        return ToResult(machine, verb, outcome);
    }

    /// <summary>
    /// Read the onlyIfEmpty flag out of a lifecycle request body. Returns null when the body is fine (and
    /// <paramref name="onlyIfEmpty"/> holds the answer), or the refusal to return when it is not.
    ///
    /// IT IS THE ONE FIELD ON THIS ROUTE WHOSE ABSENCE IS THE DANGEROUS READING, and every rule here comes
    /// from that. A malformed confirmProtected is safely read as absent, because absent is its careful
    /// side - a missing confirmProtected REFUSES a protected slot. (A missing exePath is NOT the careful
    /// side: it skips the slot guard entirely. That is this route's long-standing behaviour, pinned by its
    /// own test, and it is neither changed nor endorsed here.) Absent HERE means "restart regardless of
    /// what the Director is holding", so anything the caller might have MEANT as the flag and this route
    /// cannot be sure of has to be refused instead of ignored:
    ///
    ///   * a value that is not a boolean - "true", 1, null - is refused rather than read as false;
    ///   * the NAME is matched without regard to case, because {"OnlyIfEmpty": true} is unmistakably a
    ///     caller asking for the guard, and a case-sensitive lookup answered it with an unconditional
    ///     restart and a success;
    ///   * two spellings of the name in one object are refused, because which one wins is a detail of the
    ///     parser and not something the caller can have intended.
    ///
    /// WHAT IT STILL CANNOT CATCH, stated rather than implied: a plain misspelling ("onlyIfEmtpy") is
    /// indistinguishable from a field this route has never heard of, and is ignored. Closing that needs
    /// the guard to be something other than an optional field - a separate route, or a required mode on
    /// every lifecycle call - which is a bigger change than this one and is not smuggled in here.
    /// </summary>
    private static IResult? ReadOnlyIfEmpty(JsonElement body, string machine, string verb, out bool onlyIfEmpty)
    {
        onlyIfEmpty = false;

        JsonElement? found = null;
        foreach (var property in body.EnumerateObject())
        {
            if (!property.NameEquals("onlyIfEmpty")
                && !string.Equals(property.Name, "onlyIfEmpty", StringComparison.OrdinalIgnoreCase))
                continue;

            if (found is not null)
                return OnlyIfEmptyNotUnderstood(machine, verb,
                    "the request body names onlyIfEmpty more than once, and which spelling would win is a "
                    + "detail of the parser rather than anything you asked for");

            found = property.Value;
        }

        if (found is not { } value)
            return null; // absent: an ordinary, unconditional lifecycle call

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return OnlyIfEmptyNotUnderstood(machine, verb,
                $"onlyIfEmpty must be true or false; this request sent {value.ValueKind}");

        onlyIfEmpty = value.ValueKind == JsonValueKind.True;
        return null;
    }

    /// <summary>
    /// The one refusal for every way a caller can ask for onlyIfEmpty and not get it: on a verb that
    /// cannot honour it, or written as something that is not a boolean. Both mean the same thing to the
    /// reader - the condition you attached was NOT applied and nothing was done - and they are refused
    /// rather than ignored because the whole point of the flag is that live work is not interrupted.
    /// </summary>
    private static IResult OnlyIfEmptyNotUnderstood(string machine, string verb, string reason)
    {
        FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb={verb} reason={reason}");
        return Results.Json(new
        {
            error = "only_if_empty_not_supported",
            detail = reason + ". It was NOT applied and nothing was done: a flag that asks not to "
                   + "interrupt live work must never be dropped in silence.",
            machine,
            verb,
            hint = "Send \"onlyIfEmpty\": true (a boolean) on POST /machines/{machine}/director/restart.",
        }, statusCode: 400);
    }

    /// <summary>
    /// Returns (isProtected=true, description) when the exe path refers to the main build
    /// or a protected slot (1-4). Agent slots (5+) are NOT protected.
    /// </summary>
    private static (bool isProtected, string description) IsProtectedSlot(string exePath)
    {
        var m = SlotFromPath.Match(Path.GetFileName(exePath));
        if (!m.Success)
        {
            // Path does not match cc-director*.exe pattern - not a slot we can classify.
            return (false, "unknown");
        }

        var slotStr = m.Groups[1].Value;
        if (string.IsNullOrEmpty(slotStr))
        {
            // cc-director.exe - the main production build.
            return (true, "main build (cc-director.exe)");
        }

        if (int.TryParse(slotStr, out var slot) && slot >= 1 && slot <= 4)
        {
            return (true, $"protected slot cc-director{slot}.exe");
        }

        // Slot 5+ - not protected.
        return (false, $"agent slot {slotStr}");
    }
}

/// <summary>Request body for POST /machines/{machine}/launch, carried to the launcher as a stream command.</summary>
internal sealed class LaunchRelayBody
{
    public string? Path { get; init; }

    /// <summary>
    /// An application display name from that machine's catalogue, used instead of <see cref="Path"/>. This is
    /// the form a remote caller can actually use: it has no way to know where a program lives on a machine it
    /// cannot see, and the launcher resolves the name locally where the catalogue is.
    /// </summary>
    public string? App { get; init; }

    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }

    /// <summary>
    /// Tenant-boundary hardening (CR-5): the explicit confirmation every launch requires, the same flag the
    /// restart/stop slot guard reads. Without it the route refuses with 403 before any relay arm runs.
    /// </summary>
    public bool ConfirmProtected { get; init; }
}

/// <summary>Response body returned by the Gateway for relay calls.</summary>
internal sealed class RelayResult
{
    public required string Machine { get; init; }
    public required string Verb { get; init; }
    public required int RelayStatus { get; init; }
    public required string Payload { get; init; }
}
