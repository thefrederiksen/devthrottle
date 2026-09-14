using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one place a spawn's ORIGIN, PARENT and OWNER are settled before the create leaves the Gateway.
///
/// It is a sibling of <see cref="SpawnMissionAndSeat"/> and exists for the same stated reason: a session can
/// be started through two doors - POST /machines/{machine}/sessions and POST /directors/{id}/sessions, the
/// latter being what an unqualified <c>cc-devthrottle session spawn</c> uses - and anything a door must do
/// to a create before dispatching it belongs in ONE place. Two copies of a rule drift apart by default.
///
/// THE RULE: WHO IS ASKING IS ESTABLISHED FROM THE CREDENTIAL, NEVER FROM THE BODY. The authentication gate
/// has already resolved who the caller is; this reads that answer. <see cref="AuthMiddleware"/> says so in
/// as many words - anything downstream "must read the identity this gate resolved, never re-read the raw
/// request and reach its own conclusion. Two parsers of one request eventually disagree, and the gap between
/// them is a session id the caller can choose."
///
/// WHAT WAS WRONG BEFORE (issue #2838). Three things, one shape - the body was trusted for facts the caller
/// should not be able to pick:
///  1. Only the MACHINE door stamped anything, and only for a person's device. The Director door - the
///     common one - forwarded the body verbatim, so a caller stated its own origin and its own parent.
///  2. Nothing required an agent-initiated spawn to say who would own the result. The refusal the owner
///     approved on 2026-09-13 lived only in the command-line tool, which made it a convention rather than a
///     rule: any other caller got an unowned session, which then went red at him.
///  3. A malformed owner id was DROPPED rather than refused, so a caller that declared ownership and
///     mistyped the id got an unowned session and no error at all. That is the dead <c>--type</c> flag
///     again: sent in good faith, discarded in silence.
///
/// WHY A PERSON IS NEVER ASKED, and why that is not an exemption. A session a person opens from the desktop,
/// the Cockpit or the phone is the user's: there is no second candidate, so there is nothing to state and
/// asking would be ceremony. The requirement lands exactly where the ambiguity is - an agent starting an
/// agent is the ONLY path by which work can stop being the user's.
/// </summary>
internal static class SpawnOrigin
{
    /// <summary>The literal a caller sends to say "the USER owns this", spelled as the command line spells
    /// it (<c>--controlled-by none</c>). It exists because <c>null</c> used to mean BOTH "the user's" and
    /// "nobody said", and a gate cannot tell those apart.</summary>
    internal const string UserOwned = "none";

    /// <summary>
    /// Settle origin, parent and owner on <paramref name="req"/>. Returns false when the spawn must be
    /// refused, with <paramref name="error"/> holding the answer to return.
    ///
    /// <paramref name="route"/> names the calling door in the log, so a refusal says which one it came
    /// through.
    /// </summary>
    internal static bool TryEstablish(NewSessionRequest req, HttpContext ctx, string route, out IResult? error)
    {
        error = null;
        if (req is null) return true;

        // A PERSON'S DEVICE. The verified device type decides, and it overwrites whatever the body said -
        // including the parent, because a session a person opened has no parent and a caller must not be
        // able to invent one. Nothing is asked of them.
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        var surface = SessionOriginSurfaces.FromDeviceType(deviceType);
        if (surface != SessionOriginSurfaces.Unknown)
        {
            req.Origin = SessionOriginKinds.Human;
            req.OriginSurface = surface;
            req.ParentSessionId = null;
            req.ControllerSessionId = null;
            FileLog.Write($"[SpawnOrigin] {route}: origin established from the verified device key: human/{surface}");
            return true;
        }

        // A SESSION. Its own key authenticated this request, so it IS the parent - read from the identity the
        // auth gate resolved rather than from the body, which is the whole point.
        //
        // THIS IS ALSO THE ONLY ARM THAT REFUSES, and the narrowness is deliberate. A spawn RELAYED by a
        // Director carries that Director's own key (it enrols as "workstation"), and its body was written by
        // a command line that was already gated before it got there. Refusing on a stated origin rather than
        // an established one would break cross-machine spawning to close a door that needs a credential the
        // product only issues to its own components. Enforcement goes exactly as far as identification does,
        // and no further - see THE_BOUNDARY test.
        if (!ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            || si is not Pairing.SessionCredentialIdentity caller)
            return true;

        req.Origin = SessionOriginKinds.Agent;
        if (string.IsNullOrWhiteSpace(req.OriginSurface) || !SessionOriginSurfaces.IsValid(req.OriginSurface))
            req.OriginSurface = SessionOriginSurfaces.Api;
        req.ParentSessionId = caller.SessionId.ToString();
        FileLog.Write($"[SpawnOrigin] {route}: origin established from the verified session key: agent, parent={caller.SessionId}");

        var stated = req.ControllerSessionId?.Trim();

        if (string.IsNullOrEmpty(stated))
        {
            FileLog.Write($"[SpawnOrigin] {route}: REFUSED - an agent-initiated spawn did not say who will own the result");
            error = Results.BadRequest(new
            {
                error = "this spawn has to say who will OWN the new session",
                detail =
                    "A session is starting a session, so there are two possible owners and no safe default " +
                    "between them. Set controllerSessionId to the id of the session that will own it - your " +
                    "own id if you will collect the work - or to the literal \"none\" if the USER owns it, in " +
                    "which case it goes red and asks him when it finishes and you will not hear from it. " +
                    "A session a PERSON opens needs none of this: it is the user's, and it is never asked.",
                controllerSessionId = "<session id> | \"none\"",
            });
            return false;
        }

        if (string.Equals(stated, UserOwned, StringComparison.OrdinalIgnoreCase))
        {
            // Stated plainly: the USER owns it. Null on the wire from here on means the same thing, but it
            // now means it BECAUSE SOMEBODY SAID SO rather than because nobody did.
            req.ControllerSessionId = null;
            FileLog.Write($"[SpawnOrigin] {route}: ownership declared - the USER owns the new session");
            return true;
        }

        if (!Guid.TryParse(stated, out _))
        {
            // REFUSED, NOT DROPPED. Silently discarding this is how a caller that declared ownership and
            // mistyped the id ended up with an unowned session and no error.
            FileLog.Write($"[SpawnOrigin] {route}: REFUSED - controllerSessionId '{stated}' is not a session id");
            error = Results.BadRequest(new
            {
                error = $"controllerSessionId '{stated}' is not a session id",
                detail =
                    "It must be a session id, or the literal \"none\" meaning the USER owns the new session. " +
                    "It is refused rather than ignored: a declaration that is dropped silently produces an " +
                    "unowned session, which is exactly what declaring ownership was meant to prevent.",
            });
            return false;
        }

        FileLog.Write($"[SpawnOrigin] {route}: ownership declared - session {stated} owns the new session");
        return true;
    }
}
