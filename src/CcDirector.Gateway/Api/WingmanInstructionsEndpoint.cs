using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The editable, versioned wingman-instructions surface for the Cockpit settings page (issue #537).
/// The wingman uses the ACTIVE instructions (the user's custom version, else the deployed default);
/// this exposes viewing, editing (new version), version history + revert, and the managed-default
/// flow (see the dev team's changes, switch to the latest default). Backed by
/// <see cref="WingmanInstructionsStore"/>.
///
/// DENIED IN WHOLE ON HOSTED (issue #1853). Every route in this file is refused on the hosted Gateway.
///
/// THE OWNERSHIP. The instructions are a single-owner control over the whole box, the same class as the
/// /gateway settings group: a Gateway has ONE active wingman prompt, and it is the prompt every account's
/// wingman speaks through. A tenant that saves a version, reverts a version or switches to default is not
/// editing its own copy - there is no own copy - it is rewriting what the machine says to everybody. There
/// is nothing to partition by, so a per-tenant answer would mean inventing state the schema does not have.
///
/// It is a DENY OF THE WHOLE GROUP because a route-by-route guard rots - the next route added to this file
/// would be open by default.
///
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host has one tenant and one owner, so
/// the prompt is the owner's own prompt.
/// </summary>
internal static class WingmanInstructionsEndpoint
{
    /// <summary>
    /// The hosted refusal for the whole wingman-instructions group (issue #1853), or null on self-host
    /// where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT deployment signal - and NOT on a
    /// boundary or tenant argument being passed in. A security branch that depends on an optional argument
    /// fails OPEN when a caller omits it, which is exactly how the hosted account-status fix nearly shipped
    /// a hole: omit the argument and a hosted Gateway silently takes the self-host path. Asking hosted mode
    /// directly means this group cannot serve the single-owner wingman prompt on hosted however the host is wired.
    ///
    /// 404 rather than 403: on hosted these routes do not exist as a concept - there is no per-tenant wingman
    /// prompt - so "not here" is the truthful answer. 403 would imply the right credential could reach them,
    /// and none can.
    ///
    /// UN-DENY CONDITION. The wingman prompt is a single-owner control over the whole box: one active prompt,
    /// spoken by every account's wingman. Retiring this deny means giving the prompt a per-tenant model first
    /// (the schema has none today), so a tenant edits its own copy rather than rewriting what the machine says
    /// to everybody - only then can a tenant-scoped route come back.
    ///
    /// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This group is denied
    /// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny
    /// family on this Gateway adopts (reference implementation: the key-vault deny in pull request #1904).
    /// An earlier revision rolled its own <c>AddEndpointFilter</c> deny before the primitive existed; it has
    /// been replaced so the release ships ONE refusal boundary. The group owns the <c>/gateway/wingman/instructions</c>
    /// prefix OUTRIGHT - nothing else serves beneath it - so the exclusive shape fits: on hosted the seven
    /// handlers are NEVER MAPPED and ONE verb-less catch-all refuses everything under the prefix plus a root
    /// refusal at the prefix itself, covering every verb, every request shape, and every future sub-path for
    /// free. The exclusivity claim is CHECKED at startup by <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>,
    /// which fails the Gateway if any live route serves beneath the prefix. Off hosted the primitive maps the
    /// seven real handlers exactly as an unguarded builder would, with no refusal at all - self-host unchanged.
    /// </summary>
    /// <summary>The exclusive prefix the wingman-instructions route group owns outright on hosted.</summary>
    internal const string Prefix = "/gateway/wingman/instructions";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the wingman instructions surface is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole wingman-instructions group (issue #1853).
    /// Validated on construction, so a blank field fails the Gateway at startup rather than serving a refusal
    /// a caller cannot act on. 404 rather than 403: on hosted these routes do not exist as a concept - there
    /// is no per-tenant wingman prompt - so "not here" is the truthful answer; 403 would imply the right
    /// credential could reach them, and none can. Driven off <see cref="GatewayHostedMode.IsHosted"/> inside
    /// the primitive - the INDEPENDENT deployment signal, not an optional argument a caller can omit and
    /// thereby fail OPEN.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "wingman-instructions",
        message: RefusalMessage,
        reason: "the wingman prompt is a single-owner control over the whole box - one active prompt spoken by " +
                "every account's wingman - with no per-tenant version to serve",
        unDenyInstruction: "do NOT simply remove this deny: give the single-owner wingman prompt a per-tenant " +
                "model (the schema has none today) so a tenant edits its own copy rather than rewriting what the " +
                "machine says to everybody, and only then restore a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the wingman-instructions routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny"/>, so a route mapped
    /// around the refusal is not expressible in <see cref="MapRoutes"/> without changing its signature - the
    /// bypass count is reduced by design, not by care. On hosted the exclusive catch-all refuses the whole
    /// group and each handler is DISCARDED; off hosted the handle maps each handler as an unguarded builder
    /// would. The return value exists so the future-route property is statable from outside this file: a test
    /// maps a brand-new route through the returned handle and shows the refusal already covers routes nobody
    /// has written yet.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, WingmanInstructionsStore store,
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brainProvider)
    {
        FileLog.Write($"[WingmanInstructionsEndpoint] mapping {Prefix}; hosted={GatewayHostedMode.IsHosted} - on hosted the whole group is refused via the shared refusal primitive (issue #1853)");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial());
        MapRoutes(group, store);
        return group;
    }

    /// <summary>
    /// The seven wingman-instructions routes, mapped relative to the <see cref="Prefix"/> so the full paths
    /// are <c>/gateway/wingman/instructions</c> and its sub-paths exactly as before. Takes the denied GROUP
    /// HANDLE and nothing else: the ungrouped route builder is deliberately out of scope here so no route can
    /// be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, WingmanInstructionsStore store)
    {
        // Current state: the active instructions, whether they are customized, whether the dev team
        // has shipped a newer default, and the deployed-default identity.
        app.MapGet("", () =>
        {
            var active = store.Active();
            return Results.Json(new
            {
                active = Project(active),
                isCustomized = store.IsCustomized,
                updateAvailable = store.UpdateAvailable,
                defaultVersion = store.DefaultVersion,
                defaultHash = store.DefaultHash,
            });
        });

        // Save edited instructions as a new version and make them active.
        app.MapPut("", (WingmanInstructionsBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Content))
                return Results.Json(new { error = "content is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var v = store.Save(req.Content, req.Label);
                FileLog.Write($"[WingmanInstructionsEndpoint] saved version {v.Id}");
                return Results.Json(new { active = Project(v), isCustomized = store.IsCustomized });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Version history, newest first.
        app.MapGet("/versions", () =>
            Results.Json(new { versions = store.Versions().Select(Project).ToList() }));

        // Make an existing version active again.
        app.MapPost("/revert", (RevertBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return Results.Json(new { error = "id is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!store.Revert(req.Id))
                return Results.Json(new { error = "unknown version id" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized });
        });

        // The deployed default (the DevThrottle dev team's shipped instructions).
        app.MapGet("/default", () =>
        {
            var d = store.DefaultAsVersion();
            return Results.Json(new { version = store.DefaultVersion, hash = store.DefaultHash, content = d.Content });
        });

        // The managed-default review: is a newer default available, and what did the dev team change
        // (the acknowledged/based-on default -> the new default), so the page can show the diff.
        app.MapGet("/update", () =>
        {
            var (ackVersion, ackContent) = store.AcknowledgedDefault();
            return Results.Json(new
            {
                updateAvailable = store.UpdateAvailable,
                isCustomized = store.IsCustomized,
                acknowledgedDefaultVersion = ackVersion,
                acknowledgedDefaultContent = ackContent,
                newDefaultVersion = store.DefaultVersion,
                newDefaultContent = store.DefaultContent,
            });
        });

        // Adopt the deployed default (drop the custom version, acknowledge the latest default).
        app.MapPost("/switch-to-default", () =>
        {
            store.SwitchToDefault();
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized, updateAvailable = store.UpdateAvailable });
        });
    }

    private static object Project(WingmanInstructionsStore.InstructionVersion v) => new
    {
        id = v.Id,
        label = v.Label,
        source = v.Source,
        createdAt = v.CreatedAtUtc,
        hash = v.Hash,
        contentLength = v.Content.Length,
        content = v.Content,
    };
}

/// <summary>Body of the save route: the edited instructions and an optional label.</summary>
public sealed class WingmanInstructionsBody
{
    public string Content { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>Body of the revert route: the version id to make active.</summary>
public sealed class RevertBody
{
    public string Id { get; set; } = "";
}
