using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one place a spawn's FACTORY is settled before the create leaves the Gateway (Factory Memory
/// mission, phase 1). A sibling of <see cref="SpawnOrigin"/> and <see cref="SpawnMissionAndSeat"/>, and it
/// exists for the same stated reason: a session can be started through three doors - POST
/// /machines/{machine}/sessions, POST /directors/{id}/sessions, and the Fleet Manager's placement - and
/// anything a door must do to a create before dispatching it belongs in ONE place. Two copies of a rule
/// drift apart by default.
///
/// THE RULE: MEMBERSHIP IS ESTABLISHED FROM THE CREDENTIAL, NEVER FROM THE BODY. A factory's memory may be
/// read and written only by that factory's own sessions, so the field that decides it is exactly the field a
/// caller must not be able to choose. This is the same rule <see cref="SpawnOrigin"/> enforces for origin,
/// parent and owner, for the same reason, and it reads the identity the auth gate already resolved rather
/// than re-reading the request.
///
/// WHY MEMBERSHIP IS A BIRTH FACT AND NOT A LOOKUP THROUGH THE PARENT. The obvious design - "your factory is
/// your parent's factory" - fails twice. A parent's history row is pruned 90 days after it ends, so a
/// long-lived grandchild would lose its factory when an ancestor aged out; and three product paths build a
/// NEW session for an existing seat (a Director restart, a drain/restore, a Smart Restart reopen) where the
/// parent is either absent or is the restoring session rather than the original. So the factory is stamped
/// once, at birth, travels with the seat through all of those, and the check reads the session's OWN record
/// and never walks the chain.
///
/// WHERE THE CALLER'S OWN FACTORY IS READ FROM, and why it matters (review finding 4): the HISTORY ROW, via
/// <paramref name="factoryOf"/>, never the in-memory pushed roster. The roster is empty for a moment after a
/// Gateway restart, and a check reading it would refuse every factory session's write in that window and -
/// far worse - copy an EMPTY factory onto any child spawned in it, putting that child outside its factory
/// for the rest of its life. An absent row is therefore its own answer: "not yet known", which is a retry,
/// not a judgement about membership, and the spawn is refused rather than given nothing to inherit.
/// </summary>
internal static class SpawnFactory
{
    /// <summary>
    /// Settle <see cref="NewSessionRequest.Factory"/> on <paramref name="req"/>. Returns false when the
    /// spawn must be refused, with <paramref name="error"/> holding the answer to return.
    ///
    /// <paramref name="factoryOf"/> reads a session's recorded factory from its history row. A NULL reader
    /// is treated as "nothing can be read", which FAILS CLOSED: a session key may then state no factory and
    /// inherit none. It is never treated as "in no factory", because a Gateway that cannot read membership
    /// must not be a Gateway that hands it out.
    ///
    /// <paramref name="route"/> names the calling door in the log, so a refusal says which one it came through.
    /// </summary>
    internal static bool TryEstablish(
        NewSessionRequest req,
        HttpContext ctx,
        string route,
        Func<string, SessionFactoryLookup>? factoryOf,
        out IResult? error)
    {
        error = null;
        if (req is null) return true;

        var stated = req.Factory?.Trim();
        req.Factory = string.IsNullOrEmpty(stated) ? null : stated;

        // A PERSON'S DEVICE. The stated factory is KEPT: a person putting a session into a factory by hand is
        // the intended path (the Cockpit's and the phone's spawn), and there is no second candidate for who is
        // asking. This is the arm that makes a hand spawn into a factory possible at all - the command line
        // cannot reach it, because it holds a session key and nothing else (review finding 5).
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
        {
            // ONE SPELLING (phase 2 review, finding 1). A person's hand spawn is one of the two places a factory
            // id first comes into being, so it is folded here and refused if it is not one: membership compares
            // ids without regard to case while the memory is keyed on the exact string, so 'Website-Factory'
            // typed here would be the same factory for access and a SECOND, silently separate memory.
            if (req.Factory is not null)
            {
                if (!Factory.FactoryNames.TryFactory(req.Factory, out var folded, out var refusal))
                {
                    FileLog.Write($"[SpawnFactory] {route}: REFUSED - a person named '{req.Factory}': {refusal}");
                    error = Results.BadRequest(new { error = refusal, detail = OneSpelling });
                    return false;
                }
                req.Factory = folded;
            }
            FileLog.Write($"[SpawnFactory] {route}: factory kept from a person's device key: {req.Factory ?? "(none)"}");
            return true;
        }

        // A SESSION. Its own key authenticated this request, so its OWN recorded factory is the answer, and a
        // stated one is checked against it rather than believed. This is how a child, a grandchild and every
        // level below inherit membership - and the reason a session cannot put itself into a factory by asking.
        if (!ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            || si is not Pairing.SessionCredentialIdentity caller)
        {
            // ANY OTHER CREDENTIAL - a Director relaying, a drain/restore, a Smart Restart reopen, the cron
            // starter - passes through as it arrived, the same line SpawnOrigin draws for the parent. A restore
            // MUST be able to hand a seat its factory back, and these are internal hops whose own callers were
            // already gated. A create reaches a Director only over the Gateway tunnel or in-process from the
            // desktop (a person, who sets no factory), so the Director only ever stamps what the Gateway settled.
            FileLog.Write($"[SpawnFactory] {route}: no session or device credential - factory passed through as sent: {req.Factory ?? "(none)"}");
            return true;
        }

        // NO READER AT ALL is a different thing from "this session has no row yet", and conflating the two was a
        // mistake worth naming: a Gateway built without the reader (a harness, an older wiring) cannot read
        // membership for ANY session, so refusing every spawn would take spawning down entirely to protect a
        // field none of those callers are using. The fail-closed part that matters is kept - nothing can be
        // GRANTED without a reader, so a stated factory is still refused - while a spawn that names no factory
        // goes through with none, exactly as it did before this mission.
        if (factoryOf is null)
        {
            if (req.Factory is not null)
            {
                FileLog.Write($"[SpawnFactory] {route}: REFUSED - this Gateway cannot read a session's factory, so '{req.Factory}' cannot be verified");
                error = Results.Json(new { error = CannotVerify, detail = CannotVerifyDetail },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
                return false;
            }
            return true;
        }

        var lookup = factoryOf(caller.SessionId.ToString());

        if (!lookup.IsKnown)
        {
            // NOT KNOWN IS NOT "IN NO FACTORY". The caller is younger than its first push, or this Gateway
            // cannot read the row at all. Either way nothing can be concluded, so nothing is copied onto the
            // child: a spawn in this window is refused with a sentence that says to try again, never handed an
            // empty factory that would silently exile the child from its parent's factory for life.
            FileLog.Write($"[SpawnFactory] {route}: REFUSED - the calling session {caller.SessionId} is not yet known to the Gateway");
            error = Results.Json(new { error = NotYetKnown, detail = NotYetKnownDetail }, statusCode: StatusCodes.Status409Conflict);
            return false;
        }

        if (!lookup.IsInAFactory)
        {
            // A settled answer: the caller belongs to no factory, so it has none to give.
            if (req.Factory is not null)
            {
                FileLog.Write($"[SpawnFactory] {route}: REFUSED - session {caller.SessionId} is in no factory and named '{req.Factory}'");
                error = Results.Json(new
                {
                    error = $"this session is in no factory, so it cannot start a session in '{req.Factory}'",
                    detail = OutsideNamedAFactory,
                }, statusCode: StatusCodes.Status403Forbidden);
                return false;
            }
            req.Factory = null;
            return true;
        }

        if (req.Factory is not null && !string.Equals(req.Factory, lookup.Factory, StringComparison.OrdinalIgnoreCase))
        {
            // REFUSED, NOT QUIETLY CORRECTED. A caller that named a factory meant it; if we overwrote the name
            // with the caller's own, a session aiming a child at another factory would look like it had
            // succeeded. That is the dropped-declaration failure SpawnOrigin already refuses for a mistyped
            // owner id.
            FileLog.Write($"[SpawnFactory] {route}: REFUSED - session {caller.SessionId} of factory '{lookup.Factory}' named '{req.Factory}'");
            error = Results.Json(new
            {
                error = $"a session of factory '{lookup.Factory}' may not start a session in '{req.Factory}'",
                detail = NamedAnotherFactory,
            }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        // Inherited, in the Gateway's own spelling of the factory id rather than the caller's.
        req.Factory = lookup.Factory;
        FileLog.Write($"[SpawnFactory] {route}: factory established from the verified session key: {req.Factory} (parent {caller.SessionId})");
        return true;
    }

    /// <summary>
    /// WHICH FACTORY A HANDOVER'S TARGET IS BORN INTO: the source's, when the handover was asked for by a
    /// PERSON or by the SOURCE SESSION ITSELF, and otherwise none.
    ///
    /// A handover is the work moving on, so membership moves with it when the session hands ITSELF over. Any
    /// other session moving somebody else's work gets a target in no factory - it could otherwise mint a
    /// member of a factory it does not belong to and read that factory's memory through it.
    ///
    /// It lives here, beside the spawn-door rule, because a handover's target is a session being born and this
    /// is the one place that decides what a newborn session belongs to. It is a function of the credential and
    /// two strings so that it can be tested as one, rather than read out of an endpoint by eye.
    /// </summary>
    internal static string? ForHandover(HttpContext ctx, string fromSessionId, string? sourceFactory)
    {
        if (ctx is null) return null;

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        var byPerson = SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown;

        var bySourceItself = ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            && si is Pairing.SessionCredentialIdentity caller
            && string.Equals(caller.SessionId.ToString(), fromSessionId, StringComparison.OrdinalIgnoreCase);

        var inherited = (byPerson || bySourceItself) && !string.IsNullOrWhiteSpace(sourceFactory)
            ? sourceFactory!.Trim()
            : null;
        FileLog.Write($"[SpawnFactory] handover: factory={inherited ?? "(none)"} (byPerson={byPerson}, bySourceItself={bySourceItself}, source={sourceFactory ?? "(none)"})");
        return inherited;
    }

    /// <summary>Why one spelling matters, in words a person can act on.</summary>
    internal const string OneSpelling =
        "A factory is one name, spelled one way: lower-case letters and digits joined by hyphens. Membership " +
        "ignores capitals but the memory does not, so a second spelling would be the same factory for access and " +
        "a separate memory nobody could see from the other side.";

    /// <summary>The refusal when this Gateway holds no way to read a session's factory, so a stated one cannot be
    /// checked against the caller's own. Membership is never granted on trust.</summary>
    internal const string CannotVerify =
        "this Gateway cannot read which factory a session belongs to, so it cannot start a session in one";

    internal const string CannotVerifyDetail =
        "Naming a factory here is only ever allowed when the Gateway can confirm the calling session is already " +
        "in it, and this Gateway has no way to read that. A spawn that names no factory is unaffected and starts " +
        "as it always did.";

    /// <summary>The refusal when the calling session has no history row yet. Held once so a test can pin the
    /// wording, and phrased as a retry because that is what it is.</summary>
    internal const string NotYetKnown = "this session is not yet known to the Gateway; try again in a moment";

    internal const string NotYetKnownDetail =
        "Whether a session belongs to a factory is read from the record the Gateway keeps for it, and that " +
        "record is written from the first roster push after the session starts. Until it is there, nothing can " +
        "be concluded about this session's factory - so a session it starts is refused rather than started " +
        "outside the factory this one may belong to, which could not be corrected afterwards. Wait a moment " +
        "and send it again.";

    /// <summary>Why a session in no factory may not name one. Held once so the test can pin the wording.</summary>
    internal const string OutsideNamedAFactory =
        "A factory's memory may be changed only by that factory's own sessions, so joining a factory is not " +
        "something a caller can ask for. A session belongs to a factory because a factory's trigger or " +
        "schedule started it, or because a person put it there. If this session should be in a factory, the " +
        "trigger, the schedule or the person that starts it names the factory - it is never claimed in a " +
        "spawn.";

    /// <summary>Why a factory session may not aim a child at a different factory.</summary>
    internal const string NamedAnotherFactory =
        "A session starts sessions in its OWN factory and nowhere else: naming another factory here would let " +
        "one factory's agents write another factory's memory, which is the whole thing membership decides. " +
        "Leave the factory out and the new session inherits this one's.";
}
