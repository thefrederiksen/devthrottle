using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Memory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE GATEWAY CALLS A FACTORY AGENT USES TO READ AND WRITE ITS FACTORY'S MEMORY (Factory Memory mission,
/// phase 2, section 5.3).
///
///   GET    /factory-memory/notes                  the factory's notes as they stand
///   GET    /factory-memory/notes/{name}           one note, including a deleted one
///   PUT    /factory-memory/notes/{name}           write it, stating the version last read
///   DELETE /factory-memory/notes/{name}           delete it, stating the version last read
///   GET    /factory-memory/notes/{name}/history   every kept version
///   POST   /factory-memory/notes/{name}/restore   put an old version back - A PERSON ONLY
///
/// A SESSION NEVER NAMES THE FACTORY IT IS WRITING TO. The Gateway works it out from the session's own record,
/// which is what phase 1 built and the whole reason that phase came first: a field a caller could state is a
/// field a caller could choose, and this one decides whose memory is being written. A PERSON names the factory
/// in the query string, because a person is not in a factory and there is nothing to read them from.
///
/// RESTORE IS THE ONE CALL A SESSION CANNOT MAKE. The owner allowed factory sessions to delete, against the
/// Architect's recommendation, on the condition that a delete is undoable; letting the same session undo its own
/// delete would not add anything, and restoring is the act that decides which version of the truth stands. So it
/// belongs to the person, as section 5.3 says.
///
/// A DIRECTOR MAY READ THE LIST, AND NOTHING ELSE (phase 3a, section 5.4). The Director puts a factory session's
/// memory in place BEFORE the agent starts, and at that moment the session has no key of its own yet - the key is
/// minted a few lines later, and the Gateway has no history row for the session either. So the download is made
/// with the Director's own credential, naming the factory the Gateway itself settled on the create request. That
/// credential already has authority over the whole account; reading a factory's notes adds nothing a person could
/// not already see. Writing, deleting, history and restore stay closed to it: a Director writes nobody's memory.
/// </summary>
internal static class FactoryMemoryEndpoints
{
    internal const string Prefix = "/factory-memory";

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryMemoryStore store,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapGet($"{Prefix}/notes", (HttpContext ctx, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error, directorMayRead: true)) return error!;
            var notes = store.List(tenant, caller.Factory);
            return Results.Json(new FactoryMemoryListResponse
            {
                Factory = caller.Factory,
                Notes = notes.Select(ToDto).ToList(),
                Bytes = notes.Sum(n => (long)System.Text.Encoding.UTF8.GetByteCount(n.Text ?? "")),
                MaxBytes = FactoryMemoryStore.MaxFactoryBytes,
                MaxNotes = FactoryMemoryStore.MaxNotes,
            });
        });

        app.MapGet($"{Prefix}/notes/{{name}}", (HttpContext ctx, string name, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error)) return error!;
            var note = store.Get(tenant, caller.Factory, name);
            if (note is null)
                return Results.NotFound(new { error = $"this factory has no note called '{name}'", factory = caller.Factory, name });
            // A DELETED note answers 200 with the delete on it, not 404. "It was deleted in version 4 by that
            // session, and a person can restore it" is a different and more useful fact than "there is no such
            // note", and an agent that gets 404 would write a fresh note and lose what the history holds.
            return Results.Json(ToDto(note));
        });

        app.MapPut($"{Prefix}/notes/{{name}}", async (HttpContext ctx, string name, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error)) return error!;
            var (body, bad) = await ReadBody<SetFactoryMemoryNoteRequest>(ctx);
            if (bad is not null) return bad;
            var write = store.Set(tenant, caller.Factory, name, body!.Text ?? "", body.ExpectedVersion,
                caller.AuthorKind, caller.AuthorId, nowUtc());
            return Answer(write);
        });

        app.MapDelete($"{Prefix}/notes/{{name}}", async (HttpContext ctx, string name, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error)) return error!;
            var (body, bad) = await ReadBody<DeleteFactoryMemoryNoteRequest>(ctx);
            if (bad is not null) return bad;
            var write = store.Delete(tenant, caller.Factory, name, body!.ExpectedVersion,
                caller.AuthorKind, caller.AuthorId, nowUtc());
            return Answer(write);
        });

        app.MapGet($"{Prefix}/notes/{{name}}/history", (HttpContext ctx, string name, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error)) return error!;
            return Results.Json(new FactoryMemoryHistoryResponse
            {
                Factory = caller.Factory,
                Name = name,
                Versions = store.History(tenant, caller.Factory, name).Select(ToDto).ToList(),
            });
        });

        app.MapPost($"{Prefix}/notes/{{name}}/restore", async (HttpContext ctx, string name, string? factory) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!TryCaller(ctx, sessionFactoryOf, factory, out var caller, out var error)) return error!;
            if (caller.AuthorKind != FactoryMemoryAuthorKinds.Person)
            {
                FileLog.Write($"[FactoryMemoryEndpoints] restore REFUSED for a session: {caller.Factory}/{name}");
                return Results.Json(new
                {
                    error = "only a person can restore a note",
                    detail =
                        "A factory session may write and delete its factory's notes; putting an old version back " +
                        "is the owner's call, because it decides which version of the truth stands. Ask the owner " +
                        "to restore it in the Cockpit, or write the text you want as a new version.",
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            var (body, bad) = await ReadBody<RestoreFactoryMemoryNoteRequest>(ctx);
            if (bad is not null) return bad;
            var write = store.Restore(tenant, caller.Factory, name, body!.Version,
                caller.AuthorKind, caller.AuthorId, nowUtc(), body.ExpectedVersion);
            return Answer(write);
        });

        FileLog.Write($"[FactoryMemoryEndpoints] mapped {Prefix}/notes and its note, history and restore routes");
    }

    /// <summary>The caller kind of a Director's download. Deliberately NOT one of <see cref="FactoryMemoryAuthorKinds"/>:
    /// a Director only ever reads, so it can never be the author of a version.</summary>
    private const string DirectorReader = "director";

    /// <summary>Who is asking, and whose memory that means.</summary>
    private readonly record struct Caller(string Factory, string AuthorKind, string? AuthorId);

    /// <summary>
    /// WHOSE MEMORY THIS CALL IS ABOUT, settled from the credential (Factory Memory mission, phase 1's whole
    /// purpose). A session gets its OWN factory and may not name one; a person names one because nothing can be
    /// read from them; anything else is refused.
    /// </summary>
    private static bool TryCaller(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf,
        string? statedFactory, out Caller caller, out IResult? error, bool directorMayRead = false)
    {
        caller = default;
        error = null;
        var stated = string.IsNullOrWhiteSpace(statedFactory) ? null : statedFactory.Trim();

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
        {
            if (stated is null)
            {
                error = Results.BadRequest(new
                {
                    error = "name the factory whose memory you mean",
                    detail = "A person is not in a factory, so there is nothing to read it from: pass ?factory=<id>.",
                });
                return false;
            }
            // WHICH person, as far as this Gateway can tell: the device that authenticated. It is recorded as the
            // author so "who changed the memory" has an answer for a person's edit as well as a session's.
            // Folded and checked (phase 2 review, finding 1): a person reading 'Website-Factory' in the Cockpit
            // must be shown the same memory an agent of website-factory writes, not an empty second one.
            if (!Factory.FactoryNames.TryFactory(stated, out var folded, out var refusal))
            {
                error = Results.BadRequest(new { error = refusal, detail = SpawnFactory.OneSpelling });
                return false;
            }
            caller = new Caller(folded, FactoryMemoryAuthorKinds.Person, FleetManagerOwnerDevice.Caller(ctx)?.Actor);
            return true;
        }

        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            && si is Pairing.SessionCredentialIdentity session)
        {
            var lookup = factoryOf(session.SessionId.ToString());
            if (!lookup.IsKnown)
            {
                error = Results.Json(new { error = SpawnFactory.NotYetKnown, detail = SpawnFactory.NotYetKnownDetail },
                    statusCode: StatusCodes.Status409Conflict);
                return false;
            }
            if (!lookup.IsInAFactory)
            {
                error = Results.Json(new
                {
                    error = "this session is in no factory, so it has no memory to read or write",
                    detail =
                        "A factory's memory belongs to the factory, and a session belongs to one because a " +
                        "factory's trigger or schedule started it, or because a person put it there. A session " +
                        "outside every factory has nothing to read here and nothing it may write.",
                }, statusCode: StatusCodes.Status403Forbidden);
                return false;
            }
            if (stated is not null && !string.Equals(stated, lookup.Factory, StringComparison.OrdinalIgnoreCase))
            {
                // REFUSED RATHER THAN IGNORED. A session that named another factory meant it, and answering with
                // its own factory's notes instead would look like the other factory's memory had been read.
                FileLog.Write($"[FactoryMemoryEndpoints] REFUSED - session {session.SessionId} of '{lookup.Factory}' named '{stated}'");
                error = Results.Json(new
                {
                    error = $"a session of factory '{lookup.Factory}' may not read or write the memory of '{stated}'",
                    detail = "Leave the factory out: a session's calls are always about its own factory's memory.",
                }, statusCode: StatusCodes.Status403Forbidden);
                return false;
            }
            caller = new Caller(lookup.Factory!, FactoryMemoryAuthorKinds.Session, session.SessionId.ToString());
            return true;
        }

        // A DIRECTOR, downloading a factory session's memory before that session's agent starts (see the class
        // summary). Only a VERIFIED credential counts - a request that carried nothing is not a Director - and only
        // on the list route; every other route falls through to the refusal below.
        if (directorMayRead
            && ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey)
            && WorkspaceEndpoints.IsDirectorCredential(ctx))
        {
            if (stated is null)
            {
                error = Results.BadRequest(new
                {
                    error = "name the factory whose memory you are downloading",
                    detail = "A Director downloads a factory session's memory for the factory on that session's create: pass ?factory=<id>.",
                });
                return false;
            }
            FileLog.Write($"[FactoryMemoryEndpoints] a Director is downloading the memory of '{stated}'");
            caller = new Caller(stated, DirectorReader, null);
            return true;
        }

        // Neither a person nor a session, or a Director on a route other than the list. Refused: a factory's memory
        // is not readable by a credential the Gateway cannot place, and a Director writes nobody's memory.
        error = Results.Json(new
        {
            error = "a factory's memory is read and written by that factory's own sessions, or by a person",
            detail = "This request carried neither a session's key nor a person's device key.",
        }, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    private static IResult Answer(FactoryMemoryWrite write)
    {
        if (write.Ok) return Results.Json(ToDto(write.Note!));
        var status = write.Outcome switch
        {
            FactoryMemoryOutcome.Stale => StatusCodes.Status409Conflict,
            FactoryMemoryOutcome.NoSuchNote or FactoryMemoryOutcome.NoSuchVersion => StatusCodes.Status404NotFound,
            FactoryMemoryOutcome.BadName => StatusCodes.Status400BadRequest,
            // A cap is a refusal of this content, not a fault in the request's shape: 409 says "not as things
            // stand", which is the truth - delete a note and the same write succeeds.
            _ => StatusCodes.Status409Conflict,
        };
        return Results.Json(new
        {
            error = write.Refusal,
            outcome = write.Outcome.ToString(),
            // THE CURRENT NOTE RIDES THE REFUSAL. Without it a refused writer must read, merge and race again;
            // with it, it already holds what it needs to merge.
            current = write.Note is null ? null : ToDto(write.Note),
        }, statusCode: status);
    }

    private static FactoryMemoryNoteDto ToDto(FactoryMemoryNoteEntity n) => new()
    {
        DeletedNotice = !n.Deleted ? null
            : $"'{n.Name}' was deleted in version {n.Version} by " +
              $"{(n.AuthorKind == FactoryMemoryAuthorKinds.Person ? "a person" : $"session {n.AuthorId}")}" +
              $" on {n.WrittenAtUtc:yyyy-MM-dd HH:mm} and can be restored by a person in the Cockpit. " +
              "Writing this name again continues the same history rather than starting a new note.",
        Factory = n.Factory,
        Name = n.Name,
        Version = n.Version,
        Text = n.Text,
        Deleted = n.Deleted,
        AuthorKind = n.AuthorKind,
        AuthorId = n.AuthorId,
        WrittenAtUtc = n.WrittenAtUtc,
    };

    private static async Task<(T? body, IResult? error)> ReadBody<T>(HttpContext ctx) where T : class
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted);
            return body is null ? (null, Results.BadRequest(new { error = "a body is required" })) : (body, null);
        }
        catch (Exception ex)
        {
            return (null, Results.BadRequest(new { error = "the body could not be read as JSON", detail = ex.Message }));
        }
    }

    private static IResult NoAccount() =>
        Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
}
