using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The ONE hosted-refusal boundary every deny family adopts. It exists because a refusal attached as a
/// route-group endpoint filter does NOT answer uniformly across request shapes, and the shapes it misses
/// are not the ones an obvious test exercises. The mechanism, the shapes, and the measurements behind that
/// are recorded in the PRIVATE architecture record; this file carries what a maintainer needs in order not
/// to undo the design, and no more.
///
/// The consequence that matters to anyone writing a proof: a future-route probe on a deny family MUST
/// include a BODY-BOUND POST and not only a parameterless GET, because a parameterless GET is precisely the
/// shape through which this class of defect cannot be seen.
///
/// HOW THIS CLOSES IT. On hosted the family's handler is NEVER MAPPED. In its place goes a verb-less
/// refusal route on the same pattern. So there is no binding step to get ahead of, no body parameter, no
/// inferred media-type constraint, and no method constraint - which means no request shape can be answered
/// by the framework ahead of the refusal:
///
/// | Request shape           | Answered by |
/// |-------------------------|-------------|
/// | valid body              | the refusal |
/// | malformed body          | the refusal |
/// | wrong media type        | the refusal |
/// | a verb never mapped     | the refusal |
/// | a route added LATER     | the refusal |
///
/// The wrong-verb row is why this is a verb-less route rather than a convention that replaces the endpoint's
/// request delegate. That convention closes the three body shapes but still lets endpoint SELECTION answer
/// 405 for a verb the group never mapped - which discloses that a route exists on a Gateway whose refusal
/// says it does not. A wrong verb IS a request shape, and the standard being enforced is that the refusal is
/// uniform across shapes.
///
/// WHY A TYPED HANDLE, AND WHAT IT DOES *NOT* GIVE US - corrected after review disproved the stronger claim.
///
/// Routes are mapped through <see cref="HostedDenyGroup"/>, a distinct type obtainable only from this class.
/// It was claimed that this made unguarded mapping structurally UNEXPRESSIBLE, and that claim was FALSE:
/// review changed one mapping receiver from the guarded group to the outer builder, it compiled with zero
/// errors, and the hosted assertions reddened. So the handle is a CANARY - the bypass compiles and tests
/// catch it - not a boundary.
///
/// The correction matters because a one-proof discharge for every adopting family rested on it. What
/// survives is narrower and is stated as such: a family that maps its routes in a private STATIC method
/// taking only the handle cannot see an outer builder at all, so its routes are not INDIVIDUALLY movable -
/// redirecting one means changing the signature, which moves all of them together. That earns a family ONE
/// attachment arm rather than one per route. It does not earn an exemption from arms altogether.
///
/// Do not restore the stronger claim without a mechanism that actually enforces the shape - an analyzer,
/// not a convention. The bypass was measured; it is not hypothetical.
///
/// SELF-HOST IS UNTOUCHED, AND THAT IS THE CONTROL. Off hosted every <c>Map</c> below maps the family's real
/// handler on the group exactly as an unguarded builder would, and no refusal route is created at all.
///
/// FAIL DIRECTION. The refusal payload is validated when it is CONSTRUCTED, so a family supplying a blank
/// message fails the Gateway at STARTUP - loudly, before serving - rather than serving an empty refusal that
/// reads like a working route. The hosted decision is a REQUIRED argument (the host's
/// <c>GatewayHostOptions.Hosted</c>, read once at startup), never an optional one a caller can omit: a security
/// branch that depends on an optional argument fails OPEN the moment somebody forgets it.
/// </summary>
public static class HostedRouteDeny
{
    /// <summary>
    /// Opens an EXCLUSIVE-PREFIX denied group: on hosted the family's handlers are not mapped and ONE
    /// catch-all refusal claims everything under the prefix, including paths that do not exist. Use this
    /// wherever the family owns its prefix outright - it is the stronger shape, because one refusal cannot
    /// tie with anything and the exclusivity claim is checked by simple prefix containment at startup
    /// rather than by reasoning about which patterns compete.
    ///
    /// It also gives the family FUTURE-ROUTE coverage for free: a path added under the prefix later is
    /// already refused, because the catch-all never needed to know the route existed.
    ///
    /// The claim is CHECKED, not trusted: if any live route serves under this prefix, the Gateway refuses
    /// to start, because the catch-all would take that route off the air.
    /// </summary>
    public static HostedDenyGroup ExclusiveGroup(IEndpointRouteBuilder outer, string prefix, HostedDenial denial, bool hosted)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(denial);

        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
            throw new ArgumentException(
                $"The hosted denial for '{denial.Family}' asked to claim a prefix exclusively, but gave an empty " +
                "prefix - which would claim the entire Gateway. An exclusive claim needs a real prefix.",
                nameof(prefix));

        // THE EXCLUSIVE PREFIX MUST BE PURELY LITERAL. Exclusivity is verified by simple PREFIX CONTAINMENT
        // in HostedRefusalRouteSpace, and containment is only well-defined against a literal prefix. A
        // parameterised prefix - /family/{tenant:int} - normalises on hosted to /family/{tenant}, so a live
        // route /family/{scope}/still-serving does not TEXTUALLY start with the original prefix and slips the
        // containment check, then serves BENEATH a prefix the family claimed exclusively: an outage on the
        // more-specific live route hidden behind a claim that reads as airtight. Rejecting the parameterised
        // prefix at CONSTRUCTION is the fail-loud fix: a prefix a family cannot claim literally is a prefix it
        // cannot claim exclusively, and it must use per-route Group instead.
        if (ContainsRouteParameter(prefix))
            throw new ArgumentException(
                $"The hosted denial for '{denial.Family}' asked to claim the prefix '{prefix}' exclusively, but it " +
                "contains a route parameter. An exclusive claim is verified by literal prefix containment, which a " +
                "parameterised prefix cannot support - a more-specific live route could serve beneath it unseen. " +
                "Use a literal prefix, or open a per-route Group so each declared route carries its own refusal.",
                nameof(prefix));

        var group = CreateGroup(outer, prefix, denial, exclusive: true, hosted);

        if (hosted)
            group.MapExclusiveCatchAll(prefix);

        return group;
    }

    /// <summary>
    /// True when <paramref name="prefix"/> carries a route parameter part - <c>{id}</c>, <c>{id:int}</c>,
    /// <c>{**rest}</c>. Parsed by the framework's own parser, never scanned by hand: a hand scan for a brace
    /// is exactly the kind of pattern-text model this primitive has already been burned by twice.
    /// </summary>
    private static bool ContainsRouteParameter(string prefix)
    {
        var parsed = Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(prefix);
        foreach (var segment in parsed.PathSegments)
            foreach (var part in segment.Parts)
                if (part is Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart)
                    return true;
        return false;
    }

    /// <summary>
    /// Opens a PER-ROUTE denied group: on hosted each route the family declares gets its own refusal in
    /// place of its handler. Use this where the family's paths sit under a prefix that also carries LIVE
    /// routes, so an exclusive claim would take undenied routes off the air.
    ///
    /// THE COST, STATED RATHER THAN DISCOVERED: a per-route family does NOT inherit future-route coverage.
    /// A route added to it later has no refusal unless somebody writes one - which is the very property the
    /// group mechanism exists to provide. A family using this mode therefore owes a test enumerating its
    /// own mapped routes and asserting each has a refusal, so that adding one without a refusal REDDENS.
    /// That converts the lost property from a thing to remember into a thing that fails.
    /// </summary>
    /// <param name="outer">The builder the group hangs off.</param>
    /// <param name="prefix">The group prefix, or <c>""</c> to keep route paths written out in full.</param>
    /// <param name="denial">This family's refusal payload - the only per-family configuration.</param>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>). REQUIRED, so a
    /// family cannot be mapped without saying which mode it is in; see <see cref="GatewayHostOptions"/>.</param>
    public static HostedDenyGroup Group(IEndpointRouteBuilder outer, string prefix, HostedDenial denial, bool hosted)
        => CreateGroup(outer, prefix, denial, exclusive: false, hosted);

    /// <summary>
    /// The shared group construction behind <see cref="Group"/> and <see cref="ExclusiveGroup"/>. The
    /// <paramref name="exclusive"/> flag rides onto the handle so its <c>Map</c> knows whether a per-route
    /// refusal is owed (per-route mode) or whether the ONE catch-all already covers the route and a per-route
    /// refusal would only manufacture a tie (exclusive mode).
    /// </summary>
    private static HostedDenyGroup CreateGroup(IEndpointRouteBuilder outer, string prefix, HostedDenial denial, bool exclusive, bool hosted)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(denial);

        // THE PREFIX IS NORMALISED TOO, on hosted. A group prefix can carry its own parameter policies -
        // /family/{scope:int} - and a route's full pattern is the prefix plus the local pattern. Normalising
        // only the local half leaves the constraint-miss hole one level up: a request whose PREFIX segment
        // fails its policy fails endpoint selection, so the refusal is never selected and the framework
        // answers instead. That was measured on the previous head, and it is the same defect the local
        // normalisation exists to close, which is exactly why it was easy to miss - the fix had already been
        // applied once and looked done.
        //
        // Off hosted the prefix is used verbatim, so self-host route matching is byte-identical to a group
        // created without this primitive at all. Normalising off hosted would WIDEN the family's real routes,
        // which would be a behaviour change on the control.
        // Handed to MapGroup as a PATTERN, never re-serialised to text. A text round-trip would need a
        // fallback for the case where the rebuilt pattern has no raw text - and that fallback would quietly
        // reinstate the constrained prefix, which is the hole this is closing, restored by the very line
        // meant to close it.
        FileLog.Write($"[HostedRouteDeny] group family={denial.Family} prefix='{prefix}' " +
                      $"hosted={hosted}" +
                      " - on hosted EVERY route in this group is refused on EVERY request shape, with no argument binding");

        var group = hosted
            ? outer.MapGroup(HostedRefusalPattern.WithoutPolicies(prefix, denial.Family))
            : outer.MapGroup(prefix);

        return new HostedDenyGroup(group, denial, exclusive, hosted);
    }
}

/// <summary>
/// A family's refusal payload - the ONLY thing that differs between adopting families. Validated on
/// construction so a malformed one fails the Gateway at startup rather than serving a refusal that says
/// nothing.
/// </summary>
public sealed record HostedDenial
{
    /// <summary>The family name, for the log line and for telling refusals apart in a proof run.</summary>
    public string Family { get; }

    /// <summary>
    /// The single error string the caller receives. The refusal body carries this and nothing else, so the
    /// adopting family's test can assert an EXACT property set rather than the absence of today's payload
    /// keys - an absence-only assertion passes on a framework error too, and therefore cannot fail for the
    /// right reason.
    /// </summary>
    public string Message { get; }

    /// <summary>Why this family has no per-tenant answer to serve. Logged with every refusal.</summary>
    public string Reason { get; }

    /// <summary>
    /// What must happen before this deny is ever lifted. A deny stops the READ but not necessarily the
    /// WRITE, so data can keep accumulating behind it and be there in full on the day the deny lifts.
    /// Un-denying therefore means REMOVE the deny PLUS purge or migrate whatever accumulated - and that
    /// instruction is written here, AT the deny, rather than held as a general principle somebody has to
    /// remember at the moment they are least likely to.
    /// </summary>
    public string UnDenyInstruction { get; }

    /// <summary>
    /// The refusal status. 404 where the route does not exist as a concept on hosted: 403 would imply some
    /// credential could reach it, and none can.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Fails LOUDLY at startup on a payload that would produce a meaningless refusal. This is the
    /// no-fallback rule applied to a security primitive: a blank message would serve a refusal a caller
    /// cannot act on and a proof cannot assert, which is worse than not booting.
    /// </summary>
    public HostedDenial(
        string family,
        string message,
        string reason,
        string unDenyInstruction,
        int statusCode = StatusCodes.Status404NotFound)
    {
        Family = family;
        Message = message;
        Reason = reason;
        UnDenyInstruction = unDenyInstruction;
        StatusCode = statusCode;

        if (string.IsNullOrWhiteSpace(Family))
            throw new ArgumentException("A hosted denial must name its family.", nameof(Family));
        if (string.IsNullOrWhiteSpace(Message))
            throw new ArgumentException($"The hosted denial for '{Family}' must carry a message; a blank refusal tells a caller nothing.", nameof(Message));
        if (string.IsNullOrWhiteSpace(Reason))
            throw new ArgumentException($"The hosted denial for '{Family}' must state why the family has no per-tenant answer.", nameof(Reason));
        if (string.IsNullOrWhiteSpace(UnDenyInstruction))
            throw new ArgumentException($"The hosted denial for '{Family}' must state what un-denying requires, including what to purge or migrate.", nameof(UnDenyInstruction));
        if (StatusCode is < 400 or > 599)
            throw new ArgumentException($"The hosted denial for '{Family}' must refuse with a 4xx or 5xx status, not {StatusCode}.", nameof(StatusCode));
    }
}

/// <summary>
/// The typed handle a denied family maps its routes through. Obtainable only from
/// <see cref="HostedRouteDeny.Group"/>, which is what makes attachment structurally impossible to get wrong:
/// a family's mapping method takes ONE of these and therefore cannot be handed an unguarded builder without
/// changing its signature.
///
/// On hosted every method here maps a verb-less refusal on the pattern and DISCARDS the handler - the
/// handler is never mapped, so nothing binds. Off hosted every method maps the handler exactly as an
/// unguarded builder would.
/// </summary>
public sealed class HostedDenyGroup
{
    private readonly RouteGroupBuilder _group;
    private readonly HostedDenial _denial;

    // WHETHER THIS FAMILY CLAIMED ITS PREFIX EXCLUSIVELY. It changes ONE thing, and it is the thing the
    // review found missing: an exclusive family already maps ONE catch-all refusal under a prefix nothing
    // else may serve, so it does NOT also owe a per-route refusal for each declared route. Mapping one would
    // not add coverage - the catch-all already refuses the path - but it WOULD re-introduce exactly the
    // per-route ties (case/optional/policy) the exclusive shape exists to avoid, because two verb-less
    // refusals under one prefix can compete. So on hosted an exclusive family DISCARDS each handler and maps
    // no per-route refusal at all; the catch-all is the whole mechanism. Off hosted the flag is inert and the
    // real handlers map exactly as any group's would.
    private readonly bool _exclusive;

    // The deployment signal this family was mapped under - the owning host's value, fixed at mapping time.
    private readonly bool _hosted;

    // On hosted, one refusal per route shape WITHIN THIS FAMILY - a de-duplication, and nothing more.
    //
    // WHAT IT IS FOR: a family mapping several verbs on one path needs ONE verb-less refusal, because a
    // second on the same path would tie with the first and the tie surfaces as a 500 at request time, on the
    // denied route, which is the one nobody exercises until a caller does.
    //
    // WHAT IT IS EXPLICITLY NOT: this key is NOT the matcher's ambiguity relation, and it must never be read
    // as one. An earlier version of this primitive treated it as one, and review found three route pairs it
    // called distinct that the live matcher ties - a standard parameter against an optional one on a present
    // segment, literal case variants, and equal-precedence complex segments differing only by their
    // separator. The matcher's relation is not reachable from public API, so any check for it here is a MODEL
    // of framework semantics rather than the semantics, which is the same mistake as hand-scanning pattern
    // text instead of using the parser.
    //
    // The safety of this mode therefore does NOT rest on this key being complete. It rests on refusals
    // MIRRORING routes the family already declares: nothing is synthesised, so no pattern exists here that
    // the family did not already have. Two of its routes that tie would already tie in its own production
    // route table, before any deny existed.
    private readonly Dictionary<string, RegisteredRefusal> _refusals = new(StringComparer.Ordinal);

    private sealed record RegisteredRefusal(string SourcePattern, IEndpointConventionBuilder Builder);

    internal HostedDenyGroup(RouteGroupBuilder group, HostedDenial denial, bool exclusive, bool hosted)
    {
        _hosted = hosted;
        _group = group;
        _denial = denial;
        _exclusive = exclusive;
    }

    /// <summary>This family's refusal payload, so a test can assert against the same strings that are served.</summary>
    public HostedDenial Denial => _denial;

    public IEndpointConventionBuilder MapGet(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapGet(pattern, handler));

    public IEndpointConventionBuilder MapPost(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapPost(pattern, handler));

    public IEndpointConventionBuilder MapPut(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapPut(pattern, handler));

    public IEndpointConventionBuilder MapDelete(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapDelete(pattern, handler));

    public IEndpointConventionBuilder MapMethods(string pattern, IEnumerable<string> methods, Delegate handler)
        => Map(pattern, handler, () => _group.MapMethods(pattern, methods, handler));

    /// <summary>
    /// Maps the single catch-all refusal for an exclusive-prefix family, and records the exclusivity claim
    /// as metadata so the finalised route space can CHECK it rather than take it on trust.
    /// </summary>
    internal void MapExclusiveCatchAll(string prefix)
    {
        var refusal = _group.Map("/{**hostedDeniedPath}", context => WriteRefusalAsync(context, _denial));
        refusal.WithMetadata(new HostedRefusalMarker(_denial, prefix + "/{**hostedDeniedPath}"));
        refusal.WithMetadata(new HostedExclusivePrefixMarker(_denial, prefix));

        // The prefix ITSELF, which the catch-all above does not cover - a request to exactly the prefix has
        // no remaining segment for the catch-all to match. Without this the family's own root answers from
        // the fallback rather than from the refusal.
        var root = _group.Map("", context => WriteRefusalAsync(context, _denial));
        root.WithMetadata(new HostedRefusalMarker(_denial, prefix));
    }

    /// <summary>
    /// The one decision, in one place: on hosted map the refusal and never the handler; off hosted map the
    /// handler and never a refusal.
    /// </summary>
    private IEndpointConventionBuilder Map(string pattern, Delegate handler, Func<IEndpointConventionBuilder> mapHandler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_hosted)
            return mapHandler();

        // EXCLUSIVE MODE: the ONE catch-all refusal under this prefix already refuses this route and every
        // path beneath it, including ones the family never declared. The handler is discarded - it is never
        // mapped, so nothing binds - and NO per-route refusal is added. Adding one would not extend coverage;
        // it would only give the family a second verb-less endpoint under the prefix that could tie with the
        // first. There is nothing to configure on a route that was never mapped, so a no-op handle is
        // returned rather than a builder pointing at some other endpoint.
        if (_exclusive)
        {
            FileLog.Write($"[HostedRouteDeny] exclusive family={_denial.Family} pattern='{pattern}' " +
                          "- handler discarded, covered by the catch-all refusal, no per-route refusal mapped");
            return NoOpConventionBuilder.Instance;
        }

        // The refusal is mapped on the family's pattern with its parameter POLICIES removed, rebuilt from the
        // parsed route MODEL rather than by editing the pattern text (see HostedRefusalPattern). Keeping a
        // policy would leave a measured hole: a segment that fails an inline constraint fails endpoint
        // SELECTION, so a refusal carrying that same constraint is never selected either and the framework
        // answers instead of the refusal. Everything else - literals, separators, parameter names, optionality,
        // catch-alls and defaults - is preserved exactly.
        //
        // A pattern containing a part the normaliser does not recognise THROWS here, at startup. That is
        // deliberate: passing it through would map a refusal that still carried a constraint while the
        // family's author believed the route was covered, which is the false coverage this boundary exists to
        // remove.
        var refusalPattern = HostedRefusalPattern.WithoutPolicies(pattern, _denial.Family);
        var shapeKey = HostedRefusalPattern.ShapeKey(refusalPattern);

        // Same route shape, already refused: a family mapping several verbs on one path needs exactly ONE
        // verb-less refusal, and mapping a second would tie with the first.
        if (_refusals.TryGetValue(shapeKey, out var existing))
            return existing.Builder;

        // Verb-less and handler-less: nothing constrains the match and nothing binds, so every request shape -
        // including a verb this family never mapped - meets the refusal below.
        var refusal = _group.Map(refusalPattern, context => WriteRefusalAsync(context, _denial));
        refusal.WithMetadata(new HostedRefusalMarker(_denial, pattern));

        _refusals[shapeKey] = new RegisteredRefusal(pattern, refusal);
        return refusal;
    }

    private static async Task WriteRefusalAsync(HttpContext context, HostedDenial denial)
    {
        FileLog.Write($"[HostedRouteDeny] DENIED on hosted: family={denial.Family} " +
                      $"method={context.Request.Method} path={context.Request.Path} reason={denial.Reason}");

        context.Response.StatusCode = denial.StatusCode;

        // The media type is set EXPLICITLY, with its charset parameter, because the proof asserts the whole
        // header value and not just the type: a refusal is a contract about what is served, and "close enough"
        // on a content type is how a caller ends up parsing something other than what it expected.
        context.Response.ContentType = "application/json; charset=utf-8";

        // A HEAD request is answered with the refusal's status and headers and no body - the framework
        // suppresses the body for HEAD. That is correct HTTP and it is stated here so the proof can assert it
        // deliberately rather than a reader assuming "every request shape" includes a body on HEAD.
        await context.Response.WriteAsJsonAsync(new HostedRefusalBody(denial.Message));
    }

    /// <summary>The refusal body: one property, so the assertion can be an exact property set.</summary>
    private sealed record HostedRefusalBody(string Error);

    /// <summary>
    /// The handle returned when an exclusive family maps a route on hosted: the handler was discarded and no
    /// endpoint was created, so there is nothing to apply a convention to. It accepts and ignores conventions
    /// rather than returning null, which would break a family that chains one onto the result.
    /// </summary>
    private sealed class NoOpConventionBuilder : IEndpointConventionBuilder
    {
        public static readonly NoOpConventionBuilder Instance = new();

        public void Add(Action<EndpointBuilder> convention) { }

        public void Finally(Action<EndpointBuilder> finalConvention) { }
    }
}
