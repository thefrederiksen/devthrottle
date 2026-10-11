using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The central key-vault REST surface (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
/// Keys are set once here - from the Cockpit Keys page - and Directors GET them on demand.
/// Auth is the Gateway's host-wide token middleware, so these routes inherit it; the tailnet
/// plus that token is the trust boundary. Values leave the Gateway only via the single-key
/// GET a Director calls; the list route exposes names only, never values.
///
///   GET    /vault/keys           -> { "names": [...] }            (names only)
///   GET    /vault/keys/{name}    -> { "name", "value" } | 404
///   PUT    /vault/keys/{name}    body { "value": "..." } -> { "name", "set": true }
///   DELETE /vault/keys/{name}    -> { "name", "deleted": bool }
///
/// DENIED IN WHOLE ON HOSTED. Every route in this file is refused on the hosted Gateway - the reads,
/// the write, and the delete alike.
///
/// The store behind these routes is ONE global key vault file at the shared storage root. It carries no
/// tenant anywhere: not in the file format, not in the store, not in the routes. The routes sit behind
/// only the host-wide authentication gate, and that gate admits ANY enrolled device key from ANY account.
/// So on shared hosted infrastructure every subscriber could read every other subscriber's provider
/// credentials in cleartext (the single-key GET returns the raw value), overwrite them, and delete them.
/// That is credential THEFT and TAMPERING, not merely a disclosure, which is why the whole surface closes
/// rather than only the read.
///
/// It is a deny of the WHOLE GROUP rather than a guard on the value-returning read, because a caller who
/// can overwrite another account's key can redirect that account's paid usage, and a caller who can delete
/// it can silently disable someone else's service. A route-by-route fix also rots: the next route added to
/// this file would be open again by default and nobody would notice.
///
/// It is a deny rather than a per-tenant partition because there is nothing to partition by yet. The vault
/// file has no tenant column and no per-account namespace, so partitioning here would mean inventing
/// storage that does not exist - a half-partition, which is worse than an honest refusal. The debt is
/// written down on the pull request: when the vault is properly partitioned per account, these routes come
/// back one at a time.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE CHECK. This group is denied
/// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family
/// on this Gateway adopts (docs private architecture record; primitive at
/// <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>). An earlier revision of this file rolled its
/// own <c>AddEndpointFilter</c> deny before that primitive existed; it has been replaced so the release
/// ships ONE refusal boundary, not one per family. What the primitive buys here over the old ad-hoc filter:
///
///   * On hosted the four handlers are NEVER MAPPED. In their place the exclusive prefix maps ONE verb-less
///     catch-all refusal over everything under <c>/vault/keys</c> plus a root refusal at the prefix itself.
///     There is no binding step to get ahead of, no body parameter, no media-type constraint and no method
///     constraint, so EVERY request shape - a valid body, a malformed body, a wrong media type, a verb the
///     group never mapped, and a route added LATER - meets the refusal. The old request-time filter answered
///     only the shapes that reached it.
///   * The exclusivity claim is CHECKED at startup, not trusted:
///     <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/> refuses to start the Gateway if any live
///     route serves beneath <c>/vault/keys</c> (an outage the catch-all would cause) or if two refusals tie.
///   * The refusal payload is validated on CONSTRUCTION, so a blank message fails the Gateway at startup
///     rather than serving an empty refusal that reads like a working route.
///
/// The exclusive prefix is <c>/vault/keys</c>: the key-vault route group owns that prefix outright, nothing
/// else serves beneath it, and the exclusive shape is the stronger one - one catch-all cannot tie with
/// anything and it covers future routes under the prefix for free.
///
/// WHAT THIS DENY STOPS, NARROWLY: the HTTP ROUTE reads, the write and the delete. It does NOT stop every
/// write to this vault. The question that decides the un-deny condition is "does ANYTHING write?", and
/// separately "what is ALREADY SITTING THERE from before this deny existed?". A deny closes the ROUTE door.
/// It says nothing about the other doors, and nothing about what accumulated before it was hung.
///
/// The other doors, enumerated (every mutating call to this vault in the codebase - Set, SetIfAbsent, Delete):
///   * GatewayHost.SeedKeyVaultFromEnvironment - SetIfAbsent at host startup. HOSTED-GATED as part of this
///     change: it now no-ops on hosted (see GatewayHost).
///   * Account/TranscriptionKeyAutoProvisioner.EnsureAsync - SetIfAbsent + Set, fired on EVERY sign-in and
///     again at startup. HOSTED-GATED as part of this change: the provisioner is inert on hosted.
///   * Account/TranscriptionKeyAutoProvisioner.RevokeMintedKeyAsync - two Deletes, fired on EVERY sign-out.
///     HOSTED-GATED as part of this change (there is nothing auto-minted on hosted to revoke).
/// With the writers gated, no new key material arrives in the global vault on hosted while this deny is in
/// force. Material that PREDATES the deny is a separate concern the un-deny condition below still owns.
///
/// So the un-deny condition is NOT "add a per-account namespace". It is, in order: tenant-partition the
/// vault, THEN quarantine, purge or migrate the pre-existing global vault root, and only then restore a
/// tenant-scoped route. The raw-value GET is the LAST one back. Anyone lifting this deny needs the partition
/// AND the purge - the purge is REQUIRED, not optional, because material predating this deny is already in
/// the file and this change never touched it. The vault PARTITION (the tenant-scoped vault) is a SEPARATE
/// follow-up still owed; this pull request is the DENY half only.
///
/// Self-host is COMPLETELY unchanged, and that is the control. Off hosted the primitive maps the four real
/// handlers on the group exactly as an unguarded builder would and creates no refusal at all. Self-host is
/// single-tenant, the owner sets his own keys here from the Cockpit and his own Director reads them back, and
/// a deny scoped to the wrong signal would break the shipped product to protect the unshipped one.
/// </summary>
internal static class VaultEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The exclusive prefix the key-vault route group owns outright on hosted.</summary>
    internal const string Prefix = "/vault/keys";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the key vault is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole key-vault group. Validated on construction, so a blank field
    /// fails the Gateway at startup rather than serving a refusal a caller cannot act on. 404 rather than 403:
    /// on hosted there is no per-account key store, so this surface does not exist as a concept and "not here"
    /// is the truthful answer; 403 would imply some credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "key-vault",
        message: RefusalMessage,
        reason: "the key vault is one global store with no tenant in the file, the store or the routes, and the " +
                "host-wide auth gate admits any enrolled device key from any account - so one subscriber could read, " +
                "overwrite or delete every other subscriber's provider credentials",
        unDenyInstruction: "do NOT simply remove this deny: tenant-partition the vault, THEN purge or migrate the " +
                "pre-existing global vault root (material predating this deny is already in the file and this change " +
                "never touched it), and only then restore a tenant-scoped route - the raw-value GET last",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the key-vault routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny.ExclusiveGroup"/>, so
    /// a route mapped around the refusal is not expressible here without changing this method's plumbing - the
    /// bypass count is reduced by design, not by care. On hosted the handle DISCARDS each handler (the
    /// exclusive catch-all already refuses the path); off hosted it maps each handler as an unguarded builder
    /// would.
    ///
    /// The return value exists so the future-route property is statable from outside this file: a test can map
    /// a brand-new route through the returned handle and show the refusal already covers routes nobody has
    /// written yet - the one property that distinguishes an exclusive-prefix deny from a guard repeated in
    /// each handler.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, bool hosted, KeyVault vault)
    {
        FileLog.Write($"[VaultEndpoints] mapping {Prefix}; hosted={hosted} - on hosted the whole group is refused via the shared refusal primitive");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial(), hosted);
        MapRoutes(group, vault);
        return group;
    }

    /// <summary>
    /// The four key-vault routes, mapped relative to the <see cref="Prefix"/> so the full paths are
    /// <c>/vault/keys</c> and <c>/vault/keys/{name}</c> exactly as before. Takes the denied GROUP HANDLE and
    /// nothing else: the ungrouped route builder is deliberately out of scope here so no route can be mapped
    /// around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, KeyVault vault)
    {
        app.MapGet("", () => Results.Json(new { names = vault.ListNames() }));

        app.MapGet("/{name}", (string name) =>
        {
            var value = vault.Get(name);
            return value is null
                ? Results.NotFound(new { error = "no such key", name })
                : Results.Json(new { name, value });
        });

        app.MapPut("/{name}", async (string name, HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<VaultKeyBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Value is null)
                    return Results.BadRequest(new { error = "body { \"value\": \"...\" } is required" });

                vault.Set(name, body.Value);
                return Results.Json(new { name, set = true });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[VaultEndpoints] PUT {name} bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapDelete("/{name}", (string name) =>
            Results.Json(new { name, deleted = vault.Delete(name) }));
    }

    private sealed record VaultKeyBody(string? Value);
}
