using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHOSE IS WHAT A TEAM REQUEST TOUCHES (devthrottle_internal#2311). Inside a team's tenant a person's sessions and
/// computers are private to them, so <see cref="TeamEndpointGate"/> asks, for a rule whose target is
/// <see cref="TeamTarget.CallersOwn"/>, whether the request touches only the caller's own. This answers it from the
/// request's own credential and route, never from anything a client writes:
///
/// <list type="bullet">
/// <item>A Director's tunnel (<c>/director-stream</c>) is the calling key's own connection: Hello binds it to that
/// key's tenant, and a Director id stays with the key that first registered it.</item>
/// <item>A Director (<c>/directors/{id}/...</c>) is its owner's: the person the key it said Hello on was issued to.</item>
/// <item>A session - any route naming one by <c>{sid}</c>: <c>/sessions/{sid}/...</c>, its transcript, its prompts - is
/// its Director's owner's. A Director's own sessions are its owner's own.</item>
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
        // session.
        var sessionId = routeValue("sid");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var located = _sessions.TryGetLastKnownSession(tenant, sessionId);
            if (located is not { } found)
            {
                FileLog.Write($"[TeamCallerOwnership] Whose: session {sessionId} is not known in tenant {tenant.ToLogString()} - unknown");
                return TeamOwnership.Unknown;
            }
            return OwnerOfDirector(tenant, found.DirectorId, callerSubject, "session");
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
