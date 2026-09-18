using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE ONE RULE for the Fleet Manager routes that only the owner may call (the walkthrough, step 7, and hand over,
/// step 8): the request carries the owner's own signed-in phone or browser key, and nothing else. A session key is
/// refused - the Fleet Manager's own included - and so are a Director's key and the shared machine token. Each route
/// names what it refused and says, in its own words, what a session should do instead.
/// </summary>
internal static class FleetManagerOwnerDevice
{
    /// <summary>
    /// The owner's device when the request comes from it; otherwise null and <paramref name="refusal"/> is the 403
    /// answer, <c>{ code: "owner_only", error }</c>.
    /// </summary>
    /// <param name="what">What was asked, completing "may ...", for example "hand a session over".</param>
    /// <param name="sessionKeySentence">The whole sentence a session key is answered with.</param>
    /// <param name="logTag">The calling class, for the log line.</param>
    public static DeviceCredentialIdentity? Require(HttpContext ctx, string what, string sessionKeySentence,
        string logTag, out IResult? refusal)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            FileLog.Write($"[{logTag}] REFUSED: session {session.SessionId} asked to {what}");
            refusal = Refuse(sessionKeySentence);
            return null;
        }

        var device = ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedDeviceItemKey, out var d)
            ? d as DeviceCredentialIdentity
            : null;
        if (device is null || SessionOriginSurfaces.FromDeviceType(device.DeviceType) == SessionOriginSurfaces.Unknown)
        {
            var kind = AuthMiddleware.IdentityKind(ctx);
            var credential = device is null ? kind : $"{kind} ({device.DeviceType})";
            FileLog.Write($"[{logTag}] REFUSED: a {credential} credential asked to {what}");
            refusal = Refuse($"only the owner on their own signed-in phone or browser may {what}; "
                             + $"this request was made with a {credential} credential");
            return null;
        }

        refusal = null;
        return device;
    }

    private static IResult Refuse(string sentence)
        => Results.Json(new { code = "owner_only", error = sentence }, statusCode: StatusCodes.Status403Forbidden);
}
