using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHO IS ASKING, decided once for every route that records who changed something: the factory definition routes
/// (<see cref="FactoryDefinitionEndpoints"/>) and the Mail Desk routes (<see cref="MailDeskEndpoints"/>). Each route
/// then rules on what that caller may do; this decides only who it is.
///
/// A caller is a PERSON (a signed-in phone or browser key), a SESSION (a session's key) - in a factory or in none, read
/// from the session's history row - or NEITHER (the account's shared machine token, or a Director), which may read and
/// can never be the author of a change. A session the Gateway holds no history row for yet is refused with 409: it is
/// younger than its first push, and nothing can be concluded about which factory it is in.
/// </summary>
internal static class GatewayCaller
{
    /// <summary>The caller kind of a credential that is neither a person's device nor a session's key. Deliberately
    /// NOT one of <see cref="FactoryMemoryAuthorKinds"/>: such a caller may read and can never be the author of a change.</summary>
    public const string NeitherPersonNorSession = "machine";

    /// <summary>What kind of caller this is - a person's device, a session's key, or neither - without asking which
    /// factory it is in.</summary>
    public static string KindOf(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown) return FactoryMemoryAuthorKinds.Person;
        if (AuthMiddleware.CallingSession(ctx) is not null) return FactoryMemoryAuthorKinds.Session;
        return NeitherPersonNorSession;
    }

    /// <summary>
    /// Who is asking: the kind, the identifier a change records (a person's from their device, a session's id), a
    /// session's name, and the factory a session is in (null when it is in none, and for every other kind). False, with
    /// the 409 answer, only for a session the Gateway does not know yet. <paramref name="logTag"/> is the calling class
    /// and <paramref name="route"/> the route, for the log line.
    /// </summary>
    public static bool TryIdentify(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string logTag, string route,
        out Identity caller, out IResult? error)
    {
        ArgumentNullException.ThrowIfNull(factoryOf);
        caller = default;
        error = null;
        switch (KindOf(ctx))
        {
            case FactoryMemoryAuthorKinds.Person:
                caller = new Identity(FactoryMemoryAuthorKinds.Person, FleetManagerOwnerDevice.Caller(ctx)?.Actor);
                return true;
            case FactoryMemoryAuthorKinds.Session:
                var session = AuthMiddleware.CallingSession(ctx)!.SessionId.ToString();
                var lookup = factoryOf(session);
                if (!lookup.IsKnown)
                {
                    FileLog.Write($"[{logTag}] {route}: REFUSED - the calling session {session} is not yet known to the Gateway");
                    error = Results.Json(new { error = SpawnFactory.NotYetKnown, detail = SpawnFactory.NotYetKnownDetail },
                        statusCode: StatusCodes.Status409Conflict);
                    return false;
                }
                caller = new Identity(FactoryMemoryAuthorKinds.Session, session, lookup.Name,
                    lookup.IsInAFactory ? lookup.Factory!.Trim() : null,
                    lookup.IsInAFactory ? lookup.Seat?.Trim() : null,
                    lookup.IsInAFactory && lookup.SeatIsInherited);
                return true;
            default:
                caller = new Identity(NeitherPersonNorSession, null);
                return true;
        }
    }

    /// <summary>A caller: its kind, the identifier a change records, a session's name, the factory a session is in, and
    /// the seat of that factory it was started as or inherited (null when it is no seat), and whether it inherited it.</summary>
    public readonly record struct Identity(string Kind, string? Id, string? Name = null, string? Factory = null,
        string? Seat = null, bool SeatIsInherited = false)
    {
        /// <summary>A person, or a session's key: someone a change can record as its author.</summary>
        public bool IsAnAuthor => Kind is FactoryMemoryAuthorKinds.Person or FactoryMemoryAuthorKinds.Session;
    }
}
