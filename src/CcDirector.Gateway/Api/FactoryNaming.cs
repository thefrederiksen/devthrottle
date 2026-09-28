using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHO MAY PUT A FACTORY ON A TRIGGER OR A SCHEDULE (Factory Memory mission, phase 1; review finding 1 - the
/// highest of the three high findings).
///
/// Why this gate has to exist at all. A trigger's or a schedule's factory is not an ordinary setting: the
/// GATEWAY ITSELF stamps the session it starts into that factory, and by the inheritance rule every session
/// that session spawns as well. But any session key may create and change triggers and schedules
/// (<see cref="SessionKeyGuard"/> lists both route families). So without this gate, a session in no factory -
/// or in a different one - could create a trigger naming <c>website-factory</c>, run it, and be handed
/// membership BY THE PRODUCT, one hop away from the forged-claim refusal that
/// <see cref="SpawnFactory"/> makes at the spawn door. It could also PUT a new factory onto an EXISTING
/// trigger and aim the next real run at a factory of its choosing. The spawn-door check would pass every
/// test and the goal "a session cannot put itself into a factory by claiming it in a request" would still
/// fail.
///
/// THE RULE: a person, or a session already in that factory. Everyone else may still create and edit
/// triggers and schedules freely - with no factory on them. Nothing that works today stops working, because
/// nothing today names a factory on a schedule at all and a trigger's factory is a label nothing reads for
/// access yet.
///
/// AN ORDINARY EDIT NEVER STRIPS MEMBERSHIP. A request that says nothing about the factory KEEPS the stored
/// one, rather than clearing it: changing an interval must not quietly take the Website Factory's Scout out
/// of its factory. TAKING A FACTORY OFF ALTOGETHER IS NOT SUPPORTED, by anyone, in this phase - a blank value
/// means "said nothing", so there is no way to spell removal (review finding 6, which caught this file claiming
/// otherwise). Moving a row to another factory works, and so does deleting and recreating it; a spelling for
/// removal belongs with the Cockpit's Memory tab, not invented here.
///
/// AND THE FACTORY FIELD IS NOT THE ONLY WAY IN (review finding 2, and the owner's decision of 28 September).
/// Every session key may also rewrite what a trigger or a schedule RUNS - its prompt, its seed, its repository,
/// its check command - and then run it. The Gateway stamps the session it starts into that row's factory, so an
/// outsider who could edit the Website Factory's Scout could have a factory member run its own instructions and,
/// from phase 2, write that factory's memory. So <see cref="TryAct"/> guards the whole definition: acting at all
/// on a row that HAS a factory is limited to a person or a session of that factory. Rows with no factory are
/// unaffected, which is every trigger and schedule that is not a factory's.
/// </summary>
internal static class FactoryNaming
{
    /// <summary>
    /// Decide the factory a create or update may store. Returns false when the request must be refused, with
    /// <paramref name="error"/> holding the answer; on true, <paramref name="settled"/> is what to store
    /// (null meaning no factory).
    ///
    /// <paramref name="requested"/> is what the body said (null = said nothing), <paramref name="existing"/>
    /// the stored value (null on a create). <paramref name="what"/> names the thing in the refusal - "trigger"
    /// or "schedule" - so the sentence reads like the route the caller called.
    /// </summary>
    internal static bool TrySettle(
        HttpContext ctx,
        Func<string, SessionFactoryLookup>? factoryOf,
        string? requested,
        string? existing,
        string what,
        string route,
        out IResult? error,
        out string? settled)
    {
        error = null;
        var want = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        var have = string.IsNullOrWhiteSpace(existing) ? null : existing.Trim();

        // Said nothing about the factory: keep what is stored. This is the ordinary edit, and it is the common case.
        if (want is null)
        {
            settled = have;
            return true;
        }

        // Named exactly what is already there: nothing is changing, so there is nothing to authorize.
        if (have is not null && string.Equals(want, have, StringComparison.OrdinalIgnoreCase))
        {
            settled = have;
            return true;
        }

        settled = want;

        // A PERSON'S DEVICE - the Cockpit or the phone. A person may put a schedule or trigger into any of their
        // own factories; this is the path by which a factory gets its first scheduled agent.
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
        {
            FileLog.Write($"[FactoryNaming] {route}: a person named factory '{want}' on a {what}");
            return true;
        }

        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            && si is Pairing.SessionCredentialIdentity caller)
        {
            var lookup = factoryOf is null ? SessionFactoryLookup.NotKnown : factoryOf(caller.SessionId.ToString());
            if (!lookup.IsKnown)
            {
                FileLog.Write($"[FactoryNaming] {route}: REFUSED - the calling session {caller.SessionId} is not yet known to the Gateway");
                error = Results.Json(new { error = SpawnFactory.NotYetKnown, detail = SpawnFactory.NotYetKnownDetail },
                    statusCode: StatusCodes.Status409Conflict);
                return false;
            }

            var mine = lookup.Factory;
            var inWanted = mine is not null && string.Equals(mine, want, StringComparison.OrdinalIgnoreCase);
            // Moving a factory OFF something also needs the right to the factory it is leaving: otherwise an
            // outside session could not add itself, but could still take the Website Factory's schedule away
            // from it, which is the same tampering with a different sign.
            var inExisting = have is null || (mine is not null && string.Equals(mine, have, StringComparison.OrdinalIgnoreCase));

            if (inWanted && inExisting)
            {
                FileLog.Write($"[FactoryNaming] {route}: session {caller.SessionId} named its own factory '{want}' on a {what}");
                return true;
            }

            FileLog.Write($"[FactoryNaming] {route}: REFUSED - session {caller.SessionId} (factory {mine ?? "none"}) named '{want}' on a {what}" +
                          (have is null ? "" : $" that belongs to '{have}'"));
            error = Results.Json(new
            {
                error = mine is null
                    ? $"this session is in no factory, so it may not put a {what} into '{want}'"
                    : $"a session of factory '{mine}' may not put a {what} into '{want}'",
                detail = Detail(what),
            }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        // NEITHER A PERSON NOR A SESSION. Refused, and this is the fail-closed choice on purpose: this field
        // hands out membership, so a credential the Gateway cannot place must not be able to set it. A caller
        // that cannot be identified can still write the same trigger or schedule with no factory on it.
        FileLog.Write($"[FactoryNaming] {route}: REFUSED - an unidentified caller named factory '{want}' on a {what}");
        error = Results.Json(new
        {
            error = $"only a person, or a session already in '{want}', may put a {what} into it",
            detail = Detail(what),
        }, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    /// <summary>
    /// MAY THIS CALLER ACT ON THIS ROW AT ALL - write it, delete it, pause it, resume it, or run it now (review
    /// finding 2; the owner's decision of 28 September). Returns false with <paramref name="error"/> when not.
    ///
    /// A row with NO factory is open to anyone, exactly as before: this closes a door into a factory, it does not
    /// put triggers and schedules behind a new permission in general. A row WITH one is that factory's business,
    /// because everything on it decides what a session of that factory will be told to do.
    /// </summary>
    internal static bool TryAct(
        HttpContext ctx,
        Func<string, SessionFactoryLookup>? factoryOf,
        string? rowFactory,
        string what,
        string route,
        out IResult? error)
    {
        error = null;
        var owner = string.IsNullOrWhiteSpace(rowFactory) ? null : rowFactory.Trim();
        if (owner is null) return true;

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
            return true;

        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            && si is Pairing.SessionCredentialIdentity caller)
        {
            var lookup = factoryOf is null ? SessionFactoryLookup.NotKnown : factoryOf(caller.SessionId.ToString());
            if (!lookup.IsKnown)
            {
                error = Results.Json(new { error = SpawnFactory.NotYetKnown, detail = SpawnFactory.NotYetKnownDetail },
                    statusCode: StatusCodes.Status409Conflict);
                return false;
            }
            if (lookup.Factory is { } mine && string.Equals(mine, owner, StringComparison.OrdinalIgnoreCase))
                return true;

            FileLog.Write($"[FactoryNaming] {route}: REFUSED - session {caller.SessionId} (factory {lookup.Factory ?? "none"}) acted on a {what} of '{owner}'");
            error = Refuse(what, owner);
            return false;
        }

        FileLog.Write($"[FactoryNaming] {route}: REFUSED - an unidentified caller acted on a {what} of '{owner}'");
        error = Refuse(what, owner);
        return false;
    }

    private static IResult Refuse(string what, string owner) => Results.Json(new
    {
        error = $"this {what} belongs to factory '{owner}', so only a person or a session of '{owner}' may change or run it",
        detail = ActDetail(what, owner),
    }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Why acting on another factory's row is refused. Held here so tests can pin it.</summary>
    internal static string ActDetail(string what, string factory) =>
        $"Everything on a {what} decides what a session of '{factory}' will be told to do - its prompt, where it " +
        $"runs, and when - and that session may read and write that factory's memory. Changing or running it from " +
        $"outside the factory would therefore direct one of its members. A {what} with no factory on it is open to " +
        $"any session, as before.";

    /// <summary>Why the refusal happened and what to do instead. Held here so tests can pin it.</summary>
    internal static string Detail(string what) =>
        $"The sessions a {what} starts are born into its factory, and a factory's memory may be changed only by " +
        $"its own sessions - so naming the factory here is joining it, not labelling it. Create the {what} " +
        $"without a factory, or have the owner name the factory in the Cockpit.";
}
