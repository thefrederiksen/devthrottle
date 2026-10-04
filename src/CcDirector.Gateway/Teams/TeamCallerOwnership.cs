using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHOSE IS WHAT A TEAM REQUEST TOUCHES (devthrottle_internal#2311). Inside a team's tenant a person's sessions and
/// computers are private to them, so <see cref="TeamEndpointGate"/> asks, for a rule whose target is
/// <see cref="TeamTarget.CallersOwn"/>, whether the request touches only the caller's own. HOW IT IS REALLY
/// ANSWERED, including the two things in it a member's own Director writes (review F2):
///
/// <list type="bullet">
/// <item>A Director's tunnel (<c>/director-stream</c>) is the calling key's own connection: Hello binds it to that
/// key's tenant, a Director id stays with the key that first registered it, and a TEAM key may say Hello only under the
/// Director id its own device row was enrolled with (<c>DirectorHub.Hello</c>).</item>
/// <item>A Director (<c>/directors/{id}/...</c>) is its owner's: the person the key it said Hello on was issued to, read
/// from the device registry. The Director id in Hello is client-written, but inside a team it cannot be another
/// member's: their id is their key's enrolled id, which a different person's key is refused under.</item>
/// <item>A session - any route naming one by <c>{sid}</c>: <c>/sessions/{sid}/...</c>, its transcript, its prompts - is
/// the caller's own only when EXACTLY ONE Director in the tenant holds that id in its roster and that Director is the
/// caller's. The session ids in a roster are client-written: the roster accepts any id from any Director, so two
/// Directors holding one id is not a session anyone can be said to own, and it is answered Unknown and refused - never
/// the first one found. The roster itself does not refuse the duplicate: a roster that kept the first writer would let a
/// Director that pushed a colleague's id first hide the colleague's own session from them.</item>
/// </list>
///
/// Anything else - a list across the whole team, a session or Director this Gateway does not know, one registered by
/// something other than a device key, or a request not made with a device key - is
/// <see cref="TeamOwnership.Unknown"/>, which the gate refuses. Refused, never guessed.
/// </summary>
public sealed class TeamCallerOwnership
{
    private const string DeviceCredentialPrefix = "device:";

    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions;
    private readonly DeviceRegistry _devices;

    public TeamCallerOwnership(DirectorRegistry directors, PushedSessionStore sessions, DeviceRegistry devices)
    {
        _directors = directors ?? throw new ArgumentNullException(nameof(directors));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>
    /// Whether the request to <paramref name="routePattern"/> in <paramref name="tenant"/>, made by
    /// <paramref name="callerSubject"/> with a device key, touches only the caller's own.
    /// </summary>
    public TeamOwnership Whose(TenantId tenant, string callerSubject, string? routePattern, Func<string, string?> routeValue)
    {
        ArgumentNullException.ThrowIfNull(routeValue);
        if (string.IsNullOrWhiteSpace(callerSubject) || routePattern is null)
            return TeamOwnership.Unknown;

        var pattern = TeamEndpointRules.Normalize(routePattern);
        if (IsUnder(pattern, "/director-stream"))
            return TeamOwnership.Callers;

        if (IsUnder(pattern, "/directors"))
        {
            var directorId = routeValue("directorId") ?? routeValue("id");
            return string.IsNullOrWhiteSpace(directorId)
                ? TeamOwnership.Unknown
                : OwnerOfDirector(tenant, directorId, callerSubject, "director");
        }

        // Any route that names a session by {sid} - the session family, its transcript, its prompts - touches that one
        // session. It is someone's only when exactly one Director in the tenant holds it.
        var sessionId = routeValue("sid");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var holders = _sessions.DirectorsHoldingSession(tenant, sessionId);
            if (holders.Count == 0)
            {
                FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} is not known in tenant {tenant.ToLogString()} - unknown");
                return TeamOwnership.Unknown;
            }
            if (holders.Count > 1)
            {
                FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} is held by {holders.Count} Directors in tenant {tenant.ToLogString()} - unknown");
                return TeamOwnership.Unknown;
            }
            return OwnerOfDirector(tenant, holders[0], callerSubject, "session");
        }

        return TeamOwnership.Unknown;
    }

    private TeamOwnership OwnerOfDirector(TenantId tenant, string directorId, string callerSubject, string what)
    {
        var credential = _directors.RegisteringCredentialOf(tenant, directorId);
        if (credential is null || !credential.StartsWith(DeviceCredentialPrefix, StringComparison.Ordinal))
        {
            FileLog.Write($"[TeamCallerOwnership] Whose: {what} of director={directorId} - the Director was not registered by a device key in tenant {tenant.ToLogString()}, unknown");
            return TeamOwnership.Unknown;
        }

        var owner = _devices.AccountSubjectOfDevice(credential[DeviceCredentialPrefix.Length..]);
        if (owner is null)
        {
            FileLog.Write($"[TeamCallerOwnership] Whose: {what} of director={directorId} - its key names nobody, unknown");
            return TeamOwnership.Unknown;
        }

        var mine = string.Equals(owner, callerSubject, StringComparison.Ordinal);
        FileLog.Write($"[TeamCallerOwnership] Whose: {what} of director={directorId} in tenant {tenant.ToLogString()} - {(mine ? "the caller's own" : "someone else's")}");
        return mine ? TeamOwnership.Callers : TeamOwnership.SomeoneElses;
    }

    private static bool IsUnder(string pattern, string prefix) =>
        string.Equals(pattern, prefix, StringComparison.Ordinal)
        || pattern.StartsWith(prefix + "/", StringComparison.Ordinal);
}
