using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The workspace surface (issue #2722): a named set of seats, authored by hand or captured from a running
/// Director, stored on the Gateway so it is readable when the machine it describes is down.
///
///   GET    /gateway/workspaces        -> { workspaces: [ summary, ... ] }
///   GET    /gateway/workspaces/{id}   -> the whole document | 404
///   POST   /gateway/workspaces        -> CAPTURE: fold a Director's live sessions into a new one
///                                       | 400 | 404 | 409
///   PUT    /gateway/workspaces/{id}   -> store a document | 400
///                                     with "If-None-Match: *", create only | 412 if it exists
///   DELETE /gateway/workspaces/{id}   -> 200 | 404
///   POST   /gateway/workspaces/{id}/restore
///                                     -> RESTORE: the named Director brings the seats back | 202 | 400 | 404
///                                        | 409 | 502 (the Message Load mission, slice 6)
///   POST   /gateway/workspaces/{id}/restore/marks
///                                     -> what a restore did to one seat, written by the Director holding the
///                                        restore lease | 200 | 400 | 403 | 409 (inspection 7, ruling 1)
///   POST   /gateway/workspaces/{id}/restore/dev-reports
///                                     -> a restored seat's dev reports pass to the session it came back as, asked
///                                        by the Director holding the restore lease | 200 | 400 | 403 | 404 | 409
///
/// The routes sit under /gateway for the same reason the workflow routes do: the Gateway serves the
/// single-page app at "/" and falls unknown page paths back to index.html, so an API at a bare
/// /workspaces would win a path a page may later want.
///
/// WHY CAPTURE IS A SEPARATE VERB rather than the caller assembling a document and PUTting it: the facts
/// on a seat - the agent, the resolved role, the controller, the model, the transcript path - are ones
/// the Gateway already holds firsthand. A caller that assembles them is a caller that can get them wrong,
/// and this document is read after the sessions are gone, when nobody can check. So the capture reads
/// them, and the caller supplies only what the Gateway cannot know: what to call it and why it is being
/// taken. The store enforces the other half of that: a PUT can never mint or rewrite a capture header.
///
/// The capture CREATES and never replaces. Overwriting an existing workspace would destroy a drain that
/// somebody is halfway through, and it would do it silently.
///
/// AND IT REFUSES A DIRECTOR IT CANNOT SEE. The registry knows a Director for a while after it stops
/// talking, and the live roster comes from the push stream - so a Director that is registered but not
/// stream-connected folds to ZERO seats and would be recorded as an empty fleet. Empty is exactly what a
/// finished drain looks like, so that record would say "nothing was running" about a machine nobody could
/// reach. A capture whose emptiness cannot be distinguished from unreachability is refused.
///
/// AUTH: these are device-authed client routes carrying no per-route auth of their own. They sit under
/// the "/gateway/..." prefix, so the host-wide device-key middleware gates them exactly like every other
/// client data endpoint. An agent's SESSION key reaches them too - a session drives a drain, so a session
/// must be able to write the record of one - which is a route registration in SessionKeyGuard, not an
/// absence of one.
/// </summary>
internal static class WorkspaceEndpoints
{
    /// <summary>
    /// Map the workspace routes.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="store">The persisted workspace store.</param>
    /// <param name="connectedFleet">Whether one Director is stream-connected AND what it is running, as
    /// ONE atomic read. A delegate rather than the store itself, so the tenant resolution stays in the one
    /// place that owns it - and one delegate rather than two, so the two facts cannot come from either
    /// side of a disconnection.</param>
    /// <param name="lookupDirector">Resolve a Director's display name and version, for the capture header.
    /// Returns null when the Gateway does not know that Director.</param>
    /// <param name="sendRestore">Send a restore order down one Director's stream and return its answer, or null
    /// when that Director is not connected (the Message Load mission, slice 6).</param>
    /// <param name="callerIsDirector">Whether the request's verified credential is the one the named Director said
    /// Hello on, in the request's own account (inspection 11, ruling 1).</param>
    /// <param name="passDevReports">Pass a restored seat's dev reports to the session it came back as, in the request's
    /// own account, and say what passed. A delegate so the account resolution stays in the one place that owns it.
    /// Throws the workspace exceptions with the reason when the pass is refused.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        WorkspaceStore store,
        Func<string, (Streaming.FleetObservation Observation, IReadOnlyList<SessionDto> Sessions)> connectedFleet,
        Func<string, DirectorDto?> lookupDirector,
        Func<string, WorkspaceRestoreOrder, CancellationToken, Task<DirectorCommandResult?>> sendRestore,
        Func<HttpContext, string, bool> callerIsDirector,
        Func<WorkspaceDocument, WorkspaceDevReportPassRequest, DateTime, WorkspaceDevReportPassResult> passDevReports)
    {
        // RESTORE (the Message Load mission, slice 6; owner decision 2, 17 September 2026). A drained fleet is
        // brought back by the DIRECTOR, not by a session running spawn lines: a session key may name only itself
        // or the user as the owner of what it starts, and a restored Worker is owned by somebody else. So a
        // session (or the owner) asks here, the Gateway relays the order to the named Director, and that Director
        // starts every seat through POST /directors/{its id}/sessions on its own credential, naming the owner the
        // seat had - read from the seat facts this Gateway captured, which no caller can rewrite.
        //
        // WHO ASKED is stamped from the verified credential, never read from the body. The Director checks the
        // workspace and the seats before it answers, so a restore that cannot start is refused here, with its
        // reason, rather than failing later in a log.
        app.MapPost("/gateway/workspaces/{id}/restore", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            WorkspaceRestoreRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<WorkspaceRestoreRequest>(
                    ctx.Request.Body, WorkspaceStore.DocumentJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"the restore body could not be read: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (req is null || string.IsNullOrWhiteSpace(req.DirectorId))
                return Results.Json(new { error = "directorId is required - a restore names the Director that brings the seats back (after a restart, the NEW one)" },
                    statusCode: StatusCodes.Status400BadRequest);

            var doc = store.Get(id);
            if (doc is null)
                return Results.Json(new { error = $"no workspace with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);

            var director = lookupDirector(req.DirectorId);
            if (director is null)
                return Results.Json(
                    new { error = $"no Director with id '{req.DirectorId}' - this Gateway has never seen it, or it belongs to another account" },
                    statusCode: StatusCodes.Status404NotFound);

            // The seats were captured on one machine, and their repositories are paths on that machine. Starting
            // them on another would be a guess that the same paths exist there.
            if (!string.IsNullOrWhiteSpace(doc.Machine) && !string.IsNullOrWhiteSpace(director.MachineName)
                && !string.Equals(doc.Machine, director.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[WorkspaceEndpoints] restore REFUSED: id={id}, director={req.DirectorId}: machine {director.MachineName} is not {doc.Machine}");
                return Results.Json(
                    new { error = $"workspace '{id}' was captured on {doc.Machine}, and Director '{req.DirectorId}' runs on {director.MachineName}. " +
                                  "A seat is restored on the machine its repository paths belong to." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var askedBy = ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedSessionItemKey, out var si)
                          && si is Pairing.SessionCredentialIdentity caller
                ? caller.SessionId.ToString()
                : null;
            var order = new WorkspaceRestoreOrder
            {
                WorkspaceId = doc.Id,
                Seats = req.Seats,
                Seeds = req.Seeds,
                ForceSeats = req.ForceSeats,
                RequestedBySessionId = askedBy,
            };

            // ONE DIRECTOR PER WORKSPACE (inspection 7, ruling 4). The Director's own gate is per process, and two
            // Directors on the captured machine both pass the machine check above - each would read the same
            // un-restored seats and start them. The lease is taken HERE, before anything is relayed, and only the
            // holder may write what a restore did.
            if (!string.Equals(doc.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal))
                return Results.Json(
                    new { error = $"workspace '{id}' is {doc.Origin}, not captured. A Director restores only a workspace the Gateway captured, because only there are the owners facts the Gateway observed." },
                    statusCode: StatusCodes.Status409Conflict);

            bool newlyLeased;
            try
            {
                newlyLeased = store.TakeRestoreLease(doc.Id, req.DirectorId, askedBy, DateTime.UtcNow);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] restore REFUSED: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] restore REFUSED: id={id}, director={req.DirectorId}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            FileLog.Write($"[WorkspaceEndpoints] restore: id={id}, director={req.DirectorId}, askedBy={askedBy ?? "owner"}, " +
                          $"seats={(req.Seats is null ? "all owed" : string.Join(",", req.Seats))}, newlyLeased={newlyLeased}");
            var result = await sendRestore(req.DirectorId, order, ct);

            // A lease this request granted is given back when nothing will run under it. A lease the Director
            // already held is left alone: a run of that Director may be the reason this one was refused. A
            // timeout keeps it too - the Director may have taken the restore - and it lapses on its own.
            var nothingRuns = result is null || (!result.Ok && result.Status != DirectorCommandStatus.Timeout);
            if (newlyLeased && nothingRuns) store.ReleaseRestoreLease(doc.Id, req.DirectorId);

            if (result is null)
                return Results.Json(new { error = $"Director '{req.DirectorId}' is not connected to this Gateway, so nothing was restored." },
                    statusCode: StatusCodes.Status502BadGateway);
            if (!result.Ok)
            {
                FileLog.Write($"[WorkspaceEndpoints] restore REFUSED by the Director: id={id}, status={result.Status}: {result.Error}");
                var status = result.Status switch
                {
                    DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
                    DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
                    DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
                    DirectorCommandStatus.Timeout => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway,
                };
                return Results.Json(new { error = result.Error ?? $"the Director refused the restore ({result.Status})" }, statusCode: status);
            }

            return Results.Content(result.BodyJson ?? "{}", "application/json", statusCode: StatusCodes.Status202Accepted);
        });
        // WHAT A RESTORE DID (inspection 7, ruling 1). The only way the restored id, the failure, the attempt time
        // and the start token reach a workspace: written by the Director that holds the restore lease, on its own
        // credential. A session key never reaches this route (SessionKeyGuard lists only the restore route
        // itself), and a person's device is refused here - neither runs a restore.
        //
        // AND THE CREDENTIAL MUST BE THAT DIRECTOR'S (inspection 11, ruling 1). The mark names a Director; the
        // Gateway already knows which credential that Director said Hello on, because the hub bound the id to the
        // authenticated key. Any other key of the same account - another workstation - is refused 403 before the
        // store is asked, so it can neither write a mark under the lease holder's name nor release its lease.
        // What this does not separate: several Directors on ONE machine share that machine's key, so one of them
        // can still name another. The lease and the start token are what stop those.
        app.MapPost("/gateway/workspaces/{id}/restore/marks", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsDirectorCredential(ctx))
            {
                FileLog.Write($"[WorkspaceEndpoints] restore mark REFUSED: id={id}: not a Director's credential");
                return Results.Json(new { error = "only the Director running a restore writes what the restore did; a person or a session sets the restore decision and the handover path, and asks for the restore." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            WorkspaceRestoreMark? mark;
            try
            {
                mark = await JsonSerializer.DeserializeAsync<WorkspaceRestoreMark>(ctx.Request.Body, WorkspaceStore.DocumentJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"the restore mark could not be read: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (mark is null)
                return Results.Json(new { error = "a restore mark body is required" }, statusCode: StatusCodes.Status400BadRequest);

            if (string.IsNullOrWhiteSpace(mark.DirectorId) || !callerIsDirector(ctx, mark.DirectorId))
            {
                FileLog.Write($"[WorkspaceEndpoints] restore mark REFUSED: id={id}, director={mark.DirectorId}, kind={mark.Kind}: the credential is not the one that Director is connected on");
                return Results.Json(new { error = $"this credential is not the one Director '{mark.DirectorId}' is connected to this Gateway on, so it may not write what that Director's restore did." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var saved = store.RecordRestoreMark(id, mark, DateTime.UtcNow);
                return Results.Json(saved, WorkspaceStore.DocumentJsonOptions);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] restore mark REFUSED: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] restore mark CONFLICT: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // A RESTORED SEAT'S DEV REPORTS PASS TO THE SESSION IT CAME BACK AS (the Smart Director Restart mission,
        // section 5.3 item 13). Asked by the Director running the restore, authorised exactly as its marks are: a
        // Director's credential, and the one the named Director is connected on. A session key never reaches this
        // route - SessionKeyGuard is an allow list that does not name it - and it is refused here as well, so a
        // later edit to that list is not enough to let a session take over another session's reports. The body
        // names a seat only; both session ids come from the stored workspace (see DevReportInheritance).
        app.MapPost("/gateway/workspaces/{id}/restore/dev-reports", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsDirectorCredential(ctx))
            {
                FileLog.Write($"[WorkspaceEndpoints] dev report pass REFUSED: id={id}: not a Director's credential");
                return Results.Json(new { error = "only the Director running a restore passes a seat's dev reports to the session it came back as; a person or a session may not move a report from one session to another." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            WorkspaceDevReportPassRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<WorkspaceDevReportPassRequest>(ctx.Request.Body, WorkspaceStore.DocumentJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"the dev report pass could not be read: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (req is null)
                return Results.Json(new { error = "a dev report pass body is required" }, statusCode: StatusCodes.Status400BadRequest);

            if (string.IsNullOrWhiteSpace(req.DirectorId) || !callerIsDirector(ctx, req.DirectorId))
            {
                FileLog.Write($"[WorkspaceEndpoints] dev report pass REFUSED: id={id}, director={req.DirectorId}: the credential is not the one that Director is connected on");
                return Results.Json(new { error = $"this credential is not the one Director '{req.DirectorId}' is connected to this Gateway on, so it may not pass dev reports in that Director's name." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var doc = store.Get(id);
            if (doc is null)
                return Results.Json(new { error = $"no workspace with id '{id}'" }, statusCode: StatusCodes.Status404NotFound);

            try
            {
                var result = passDevReports(doc, req, DateTime.UtcNow);
                return Results.Json(result, WorkspaceStore.DocumentJsonOptions);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] dev report pass REFUSED: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] dev report pass CONFLICT: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/gateway/workspaces", () =>
        {
            var workspaces = store.List();
            FileLog.Write($"[WorkspaceEndpoints] list: count={workspaces.Count}");
            return Results.Json(new { workspaces });
        });

        app.MapGet("/gateway/workspaces/{id}", (string id) =>
        {
            var doc = store.Get(id);
            if (doc is null)
            {
                FileLog.Write($"[WorkspaceEndpoints] get: id={id}, result=not found");
                return Results.Json(new { error = $"no workspace with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            FileLog.Write($"[WorkspaceEndpoints] get: id={id}, seats={doc.Seats.Count}");
            return Results.Json(doc, WorkspaceStore.DocumentJsonOptions);
        });

        // Create or replace. This is the write path for BOTH halves of the object's life: authoring a
        // workspace by hand, and writing the drain's judgments - the handover paths, the drain states, the
        // restore decisions, and afterwards what the restart actually produced - back onto a captured one.
        // The id in the route wins over the id in the body, so a body copied from another workspace cannot
        // quietly overwrite that one.
        app.MapPut("/gateway/workspaces/{id}", async (string id, HttpContext ctx) =>
        {
            WorkspaceDocument? doc;
            try
            {
                doc = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(
                    ctx.Request.Body, WorkspaceStore.DocumentJsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"the workspace body could not be read: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (doc is null)
                return Results.Json(new { error = "a workspace body is required" },
                    statusCode: StatusCodes.Status400BadRequest);

            doc.Id = id;

            // CREATE-ONLY, when the caller asks for it. "If-None-Match: *" is the standard way to say
            // "write this only if it does not exist", and a caller who has to overwrite must first read
            // what is there. Without it the only way to avoid clobbering is to list, check, and then PUT
            // - three operations with two gaps, which is how the legacy import could overwrite a
            // workspace somebody else created a moment earlier.
            var createOnly = ctx.Request.Headers.IfNoneMatch.Any(v => v == "*");

            try
            {
                var saved = createOnly
                    ? store.CreateAuthored(doc, DateTime.UtcNow)
                    : store.Save(doc, DateTime.UtcNow);
                FileLog.Write($"[WorkspaceEndpoints] PUT: id={id}, seats={saved.Seats.Count}");
                return Results.Json(saved, WorkspaceStore.DocumentJsonOptions);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] PUT REFUSED: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] PUT CONFLICT: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
        });

        // CAPTURE. POST creates a workspace by folding a Director's live sessions - the only way a
        // workspace is created from something that is running, and the reason there is no POST that takes a
        // hand-assembled document (PUT is that path, and it names its own id).
        app.MapPost("/gateway/workspaces", (WorkspaceCaptureRequest? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.DirectorId))
                return Results.Json(new { error = "directorId is required - a capture names the Director it folds" },
                    statusCode: StatusCodes.Status400BadRequest);

            var director = lookupDirector(req.DirectorId);
            if (director is null)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture: directorId={req.DirectorId}, result=unknown Director");
                return Results.Json(
                    new { error = $"no Director with id '{req.DirectorId}' - this Gateway has never seen it, " +
                                  "or it belongs to another account" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            // ONE read for both facts, and FOUR answers out of it. Asking "is it connected?" and then
            // "what is it running?" as two calls leaves a gap a disconnection fits through; reducing the
            // answer to a boolean leaves a Director that has connected and not yet spoken looking exactly
            // like one running nothing. Either way what comes out is a capture recording an EMPTY fleet,
            // and that record is a restart that restores nothing, silently.
            //
            // Only Observed captures - INCLUDING an observed empty snapshot, which is a real answer and
            // is what a finished Director genuinely looks like.
            var (observation, sessions) = connectedFleet(req.DirectorId);

            // THE PERMISSIVE ARM IS THE POSITIVE ONE, and this is the whole shape of it. The first
            // version named the three unsafe states and used the default arm as permission to capture -
            // so ONE observation was allowed by name and every other value, including every state added
            // after today, was allowed by falling through. Naming four states instead of three does not
            // fix that; it buys the fifth. What fixes it is that the allow is a single positive test
            // against the one state known to be safe, and everything else - named or not - refuses.
            var refusal = observation == Streaming.FleetObservation.Observed
                ? null
                : observation switch
                {
                    Streaming.FleetObservation.Unknown =>
                        $"This Gateway has no live record of Director '{req.DirectorId}' at all, so there " +
                        "is nothing to read. It may have been restarted or evicted since it registered.",
                    Streaming.FleetObservation.NotConnected =>
                        $"Director '{req.DirectorId}' ({director.DisplayName} on {director.MachineName}) " +
                        "is registered but not connected to this Gateway, so its live sessions cannot be read.",
                    Streaming.FleetObservation.ConnectedButSilent =>
                        $"Director '{req.DirectorId}' ({director.DisplayName} on {director.MachineName}) " +
                        "has just connected and has not yet said what it is running. That is not the same " +
                        "as running nothing - wait for its first push and capture again.",

                    // Anything this build does not recognise. It cannot be reached today, and that is the
                    // point: when a later build adds an observation, this is where it lands until somebody
                    // decides it is safe to capture, rather than being captured because nobody decided.
                    _ => $"This Gateway read Director '{req.DirectorId}' and got an observation this build " +
                         $"does not recognise ({observation}). Nothing is captured on an answer nobody " +
                         "here can interpret.",
                };

            if (refusal is not null)
            {
                FileLog.Write(
                    $"[WorkspaceEndpoints] capture REFUSED: directorId={req.DirectorId}, observation={observation}");
                return Results.Json(
                    new
                    {
                        error = refusal + " Capturing it now would record an EMPTY fleet, which is " +
                                "indistinguishable from a Director that had genuinely finished.",
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var doc = WorkspaceCapture.Capture(
                req, sessions, director.DisplayName, director.Version, director.MachineName, DateTime.UtcNow);

            try
            {
                var saved = store.Create(doc, DateTime.UtcNow);
                FileLog.Write(
                    $"[WorkspaceEndpoints] capture: id={saved.Id}, directorId={req.DirectorId}, " +
                    $"seats={saved.Seats.Count}");
                return Results.Json(saved, WorkspaceStore.DocumentJsonOptions,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture REFUSED: id={req.Id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture CONFLICT: id={req.Id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapDelete("/gateway/workspaces/{id}", (string id) =>
        {
            var deleted = store.Delete(id);
            FileLog.Write($"[WorkspaceEndpoints] DELETE: id={id}, deleted={deleted}");
            return deleted
                ? Results.Json(new { deleted = true, id })
                : Results.Json(new { error = $"no workspace with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
        });
    }

    /// <summary>
    /// True when the verified credential is a DIRECTOR's: not a session key and not a person's phone or browser.
    /// That leaves a workstation key (what a Director enrols with) or the shared machine token of a self-hosted
    /// Gateway. Read from the identity the authentication gate resolved, never from the request.
    /// </summary>
    internal static bool IsDirectorCredential(HttpContext ctx)
    {
        if (Util.AuthMiddleware.CallingSession(ctx) is not null) return false;
        var deviceType = ctx.Items.TryGetValue(Util.AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        return Core.Sessions.SessionOriginSurfaces.FromDeviceType(deviceType) == Core.Sessions.SessionOriginSurfaces.Unknown;
    }
}
