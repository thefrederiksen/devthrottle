using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Marks an endpoint as a hosted refusal, so the FINALISED route space can be checked once everything has
/// been mapped. Carries the family and the pattern the family asked for, so a failure message names the
/// two routes a human has to go and look at.
/// </summary>
internal sealed record HostedRefusalMarker(HostedDenial Denial, string SourcePattern);

/// <summary>
/// Marks a group as claiming a prefix EXCLUSIVELY on hosted - nothing outside the denied family may serve
/// a path underneath it. Recorded as metadata so the claim is checked against the finalised route space
/// rather than trusted.
/// </summary>
internal sealed record HostedExclusivePrefixMarker(HostedDenial Denial, string Prefix);

/// <summary>
/// Validates the route space AFTER every endpoint has been mapped, and fails the Gateway at startup rather
/// than letting a conflict surface as a 500 on a denied route the first time somebody calls it.
///
/// WHAT THIS CHECKS, AND HOW IT AVOIDS RE-MODELLING THE MATCHER.
///
/// A previous version of this file tried to detect whether two route patterns would TIE in the matcher, by
/// comparing a hand-rolled "shape key" and then only ever testing that key for EQUALITY. It was wrong, and
/// it was wrong in the way that matters: three independent route pairs escaped it and returned HTTP 500 at
/// request time - a standard parameter against an optional one on a present segment, literal case variants,
/// and equal-precedence complex segments differing only by their separator character. Equal keys are a
/// SOUND witness of a tie, but they are not the ONLY tie: two patterns can tie with DIFFERENT keys, and the
/// method-removal this primitive performs is exactly what manufactures those different-key ties.
///
/// The lesson from that miss is kept, because it is the second time this primitive made the same mistake:
/// <b>the matcher's FULL ambiguity relation is not reachable from public API, so any hand-written check for
/// the WHOLE of it is a MODEL of framework semantics rather than the semantics themselves.</b> The first
/// instance was a hand-written scan standing in for the route parser; the second was a hand-rolled key
/// standing in for the matcher. Both were correct on the cases their author thought of and wrong on cases
/// the framework already knew about.
///
/// So this does NOT reimplement the ambiguity relation. It asks a strictly smaller, DECIDABLE question -
/// "could a single request path be matched by both of these patterns at EQUAL precedence?" - and it answers
/// the precedence half by reading each pattern's OWN <see cref="RoutePattern.InboundPrecedence"/> - the exact
/// value the endpoint comparer uses - rather than by re-deriving precedence digits. That is the opposite of
/// modelling: the specificity ranking that decides which of two matches wins is the framework's, computed by
/// the framework, and it is read from the FINALISED pattern so a parameter carrying a required value counts
/// as the literal it actually matches. What is left for this file to decide is only whether a common concrete
/// path EXISTS, which is structural and conservative:
///
///   - Two patterns of DIFFERENT segment count, or of DIFFERENT inbound precedence, cannot tie - a
///     more-specific match always wins, so a literal never ties with the parameter that could cover it, and
///     a constrained parameter never ties with an unconstrained one. These are dropped.
///   - At EQUAL precedence, a position where BOTH sides are a single literal shares a path only if the two
///     literals are equal (case-insensitively, as the matcher compares them); unequal literals mean no
///     shared path and no tie. Every other position - a parameter, a catch-all, a complex segment - is
///     treated as ABLE to share a value (a parameter matches anything; two complex segments differing only
///     by separator still share values such as "a-b.c"). That is the fail-CLOSED direction: it can
///     over-reject an ambiguous DEVELOPER DECLARATION at startup with a clear error, which is acceptable
///     because it runs on the denied-route declarations, never on user traffic.
///
/// This is COMPLETE for the ties method-removal introduces. Two routes a family held apart by HTTP METHOD,
/// or two families denying one path under different verbs, become verb-less refusals with identical
/// precedence and intersecting value sets - Standard-vs-Optional, or separator-only complex differences
/// included - and every such pair fails the start, whether or not their old shape keys were equal.
///
///   - an EXCLUSIVE-PREFIX family maps ONE catch-all refusal under a prefix nothing else may serve, and
///     maps NO per-route refusal (the handle discards each handler). One refusal cannot tie with itself.
///     Exclusivity is checked by PREFIX CONTAINMENT against the finalised route patterns STRUCTURALLY - the
///     prefix is required to be literal at construction, and a live route's path is read from its parsed
///     segments, never from RawText, which a model-built pattern leaves null.
///
/// What this still does NOT claim to catch is a tie that would ALREADY exist in a family's own production
/// route table before any deny - an optional parameter against a shorter path under a DIFFERENT segment
/// count, say. That tie is not introduced by substitution; it is the family's own, it would surface off
/// hosted too, and deciding the general variable-length case is the unreachable relation. Deciding the
/// introduced subset - a shared path at equal precedence among same-length patterns - is what this checks.
/// </summary>
internal static class HostedRefusalRouteSpace
{
    /// <summary>
    /// Reads every endpoint the application has mapped and throws on the first violation. Call ONCE, after
    /// all mapping is done and before the host serves. Inert when nothing is refused, so it does nothing on
    /// self-host, where no refusal endpoint exists.
    /// </summary>
    /// <summary>
    /// Selects the finalised endpoint set the way it MUST be read before StartAsync, then validates it. This
    /// is the ONE place the pre-start data source is chosen, and both GatewayHost (immediately before its own
    /// StartAsync) and the pre-start test harness call it - so the test drives the exact selection production
    /// uses, and a revert of the source back to the DI composite reddens the tie tests instead of passing
    /// unnoticed. See <see cref="SelectFinalisedEndpoints"/> for why the app's own data sources are the only
    /// correct source here.
    /// </summary>
    /// <returns>The finalised endpoints it validated. Every read of the app's data sources builds every endpoint
    /// again, so the caller keeps this one read rather than selecting a second time.</returns>
    public static IReadOnlyList<Endpoint> ValidateBeforeStart(IEndpointRouteBuilder app)
    {
        var endpoints = SelectFinalisedEndpoints(app);
        Validate(endpoints);
        return endpoints;
    }

    /// <summary>
    /// The pre-start source SELECTION, factored to a single definition. The finalised endpoints are read from
    /// the app's OWN <see cref="IEndpointRouteBuilder.DataSources"/>, which carry the group / minimal-API
    /// endpoints (prefix and metadata conventions applied) the moment they are mapped. The DI-resolved
    /// <see cref="EndpointDataSource"/> composite is deliberately NOT read here: it is not populated with those
    /// endpoints until StartAsync builds the endpoint middleware, so before start it is EMPTY and the whole
    /// validation would silently do nothing. Because this selection has exactly one definition shared by
    /// production and the harness, reverting it to the DI composite fails the pre-start tie tests rather than
    /// only regressing production.
    /// </summary>
    public static IReadOnlyList<Endpoint> SelectFinalisedEndpoints(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routes = endpoints.OfType<RouteEndpoint>().ToList();

        var exclusive = routes
            .Select(e => (Endpoint: e, Marker: e.Metadata.GetMetadata<HostedExclusivePrefixMarker>()))
            .Where(x => x.Marker is not null)
            .ToList();

        var refusalEndpoints = routes
            .Where(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is not null)
            .ToList();

        if (refusalEndpoints.Count == 0 && exclusive.Count == 0)
        {
            FileLog.Write("[HostedRefusalRouteSpace] no hosted refusals mapped - nothing to validate (self-host)");
            return;
        }

        ValidateNoRefusalTies(routes, refusalEndpoints);
        ValidateExclusiveContainment(routes, exclusive);

        FileLog.Write($"[HostedRefusalRouteSpace] validated {exclusive.Count} exclusive-prefix claim(s) and " +
                      $"{refusalEndpoints.Count} refusal route(s) against {routes.Count} mapped route(s)");
    }

    /// <summary>
    /// Fails the start on a tie INTRODUCED by refusal substitution: two verb-less refusals that a single
    /// request path could match at equal precedence, or a refusal that could be matched at equal precedence
    /// with a LIVE route. Both compete in the matcher and answer 500/ambiguous at request time - on a DENIED
    /// route, the one nobody exercises until a caller does.
    ///
    /// This is the introduced subset of the ambiguity relation, not the whole of it, and it is COMPLETE for
    /// that subset: it does not rely on two patterns having EQUAL shape keys (the round-1 miss), it asks
    /// whether they could match a common path at equal precedence - which is true for Standard-vs-Optional
    /// parameters and for separator-only complex differences, exactly the different-key ties method-removal
    /// manufactures. The precedence half is the framework's own (see <see cref="CouldMatchSamePath"/>), so
    /// this file never re-derives the specificity ranking it was twice burned re-deriving.
    /// </summary>
    private static void ValidateNoRefusalTies(List<RouteEndpoint> routes, List<RouteEndpoint> refusalEndpoints)
    {
        // The live route space. Refusals are excluded here - they are checked against each other and against
        // these lives below.
        var lives = routes
            .Where(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is null)
            .ToList();

        // Refusal against refusal. Every unordered pair, so a tie is caught whichever family declared first.
        for (var i = 0; i < refusalEndpoints.Count; i++)
        {
            for (var j = i + 1; j < refusalEndpoints.Count; j++)
            {
                var a = refusalEndpoints[i];
                var b = refusalEndpoints[j];
                if (!CouldMatchSamePath(a.RoutePattern, b.RoutePattern)) continue;

                var familyA = a.Metadata.GetMetadata<HostedRefusalMarker>()!.Denial.Family;
                var familyB = b.Metadata.GetMetadata<HostedRefusalMarker>()!.Denial.Family;
                throw new InvalidOperationException(
                    $"Two hosted refusals compete for the same route shape: '{PatternText(a)}' (family " +
                    $"'{familyA}') and '{PatternText(b)}' (family '{familyB}'). Refusal substitution drops the " +
                    "HTTP method and folds literal case, so two routes held apart by verb - even ones whose " +
                    "parameter kinds or separators differ - become verb-less refusals a single path can match " +
                    "at equal precedence, and they TIE - a 500 on a denied route at request time. Refusing to " +
                    "start instead. These two denied shapes must be reconciled to a single refusal.");
            }
        }

        // Refusal against live. A verb-less refusal matches every method, so it ties with a live route
        // wherever a single path could match both at equal precedence.
        foreach (var refusal in refusalEndpoints)
        {
            var family = refusal.Metadata.GetMetadata<HostedRefusalMarker>()!.Denial.Family;
            foreach (var live in lives)
            {
                if (!CouldMatchSamePath(refusal.RoutePattern, live.RoutePattern)) continue;

                throw new InvalidOperationException(
                    $"The hosted refusal '{PatternText(refusal)}' (family '{family}') competes for the same route " +
                    $"shape as the live route '{PatternText(live)}'. The verb-less refusal matches every method, so " +
                    "it ties with the live route wherever a single path can match both at equal precedence - a 500 " +
                    "on the denied route rather than a clean answer. Refusing to start: either that live route " +
                    "belongs to the denied family, or the family is denying a shape it does not own.");
            }
        }
    }

    /// <summary>
    /// True when a SINGLE request path could be matched by both patterns at EQUAL precedence - the condition
    /// under which the matcher ties and answers an ambiguous 500. This is the whole tie test, and it is
    /// deliberately conservative (fail-CLOSED): it answers "could they tie", over-rejecting an ambiguous
    /// declaration rather than under-reporting a real tie.
    ///
    /// The precedence half is NOT re-derived here. Each pattern's own <see cref="RoutePattern.InboundPrecedence"/>
    /// is the framework's inbound-precedence value - the same ranking the matcher uses to decide which of two
    /// candidate matches wins - so a literal never counts as tying with the parameter that could cover it, and
    /// a constrained parameter never counts as tying with an unconstrained one, WITHOUT this file re-implementing
    /// any of that ordering. Reading the FINALISED pattern's own value (rather than reconstructing it through a
    /// RouteTemplate, which drops the pattern's required values) means a parameter carrying a required value is
    /// scored as the literal it actually matches. Unequal precedence, or a different segment count, means no tie.
    ///
    /// What is left is only whether a common concrete path EXISTS at that shared precedence, decided
    /// per-segment by <see cref="SegmentsCanShareValue"/>.
    /// </summary>
    private static bool CouldMatchSamePath(RoutePattern a, RoutePattern b)
    {
        if (a.PathSegments.Count != b.PathSegments.Count) return false;
        if (a.InboundPrecedence != b.InboundPrecedence) return false;

        for (var i = 0; i < a.PathSegments.Count; i++)
            if (!SegmentsCanShareValue(a.PathSegments[i], b.PathSegments[i]))
                return false;

        return true;
    }

    /// <summary>
    /// Whether two segments AT EQUAL PRECEDENCE could be filled by a common value. Only when BOTH sides are a
    /// single literal is non-overlap structurally decidable: the two match a common path iff the literals are
    /// equal, case-insensitively, as the matcher compares route literals. Any other segment - a parameter, a
    /// catch-all, or a complex mix - is treated as ABLE to share a value (a parameter matches anything; two
    /// complex segments differing only by their separator still share values such as "a-b.c"), which is the
    /// fail-CLOSED direction: a false "yes" over-rejects an ambiguous declaration at startup, a false "no"
    /// would ship a request-time 500 on a denied route. Since the two segments are already known to be at
    /// equal precedence, "one side literal, the other a parameter" does not reach here as a shared-path case.
    /// </summary>
    private static bool SegmentsCanShareValue(RoutePatternPathSegment a, RoutePatternPathSegment b)
    {
        if (IsSingleLiteral(a, out var literalA) && IsSingleLiteral(b, out var literalB))
            return string.Equals(literalA, literalB, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    /// <summary>True when the segment is exactly one literal part, handing back its text.</summary>
    private static bool IsSingleLiteral(RoutePatternPathSegment segment, out string literal)
    {
        if (segment.Parts.Count == 1 && segment.Parts[0] is RoutePatternLiteralPart part)
        {
            literal = part.Content;
            return true;
        }

        literal = string.Empty;
        return false;
    }

    /// <summary>
    /// Fails the start when a live route serves BENEATH a prefix a family claimed exclusively - an OUTAGE on
    /// a route nobody denied, which no leak-shaped test would notice. Containment is computed from the
    /// finalised route patterns STRUCTURALLY: the exclusive prefix is literal (enforced at construction), and
    /// the live route's path is read from its parsed segments, so a model-built pattern with a null RawText is
    /// compared correctly rather than being read as the root "/".
    /// </summary>
    private static void ValidateExclusiveContainment(
        List<RouteEndpoint> routes,
        List<(RouteEndpoint Endpoint, HostedExclusivePrefixMarker? Marker)> exclusive)
    {
        foreach (var (endpoint, marker) in exclusive)
        {
            var prefix = marker!.Prefix.TrimEnd('/').ToLowerInvariant();

            foreach (var other in routes)
            {
                if (ReferenceEquals(other, endpoint)) continue;
                if (other.Metadata.GetMetadata<HostedRefusalMarker>() is not null) continue;
                if (other.Metadata.GetMetadata<HostedExclusivePrefixMarker>() is not null) continue;

                var otherPath = StructuralPath(other.RoutePattern);
                if (!otherPath.StartsWith(prefix + "/", StringComparison.Ordinal) &&
                    !string.Equals(otherPath, prefix, StringComparison.Ordinal))
                    continue;

                throw new InvalidOperationException(
                    $"The hosted denial for family '{marker.Denial.Family}' claims the prefix '{marker.Prefix}' " +
                    $"EXCLUSIVELY, but the live route '{PatternText(other)}' serves underneath it. The " +
                    "catch-all refusal would take that route off the air. Refusing to start: an outage on an " +
                    "undenied route is as much a defect as a leak on a denied one. Either that route belongs to " +
                    "the denied family, or this family cannot claim the prefix exclusively and owes per-route " +
                    "refusals instead.");
            }
        }
    }

    /// <summary>
    /// A route's path as a comparable, case-folded string, built from its PARSED SEGMENTS and never its
    /// RawText - a pattern rebuilt from the route model (as the refusal patterns are) has a null RawText, and
    /// reading that as the root "/" is precisely the containment miss this closes. Literal text is folded to
    /// lower case to match the matcher; a parameter part is emitted as a sentinel that cannot equal any
    /// literal prefix segment, so a parameter never satisfies containment against a literal prefix.
    /// </summary>
    private static string StructuralPath(RoutePattern pattern)
    {
        var sb = new StringBuilder();

        foreach (var segment in pattern.PathSegments)
        {
            sb.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        sb.Append(literal.Content.ToLowerInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        sb.Append(separator.Content.ToLowerInvariant());
                        break;
                    case RoutePatternParameterPart:
                        // A sentinel a literal path segment cannot contain - route literals never carry a
                        // brace, and the exclusive prefix is required literal - so a parameter segment can
                        // never be read as a literal match for an exclusive prefix segment.
                        sb.Append("{}");
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Cannot read a structural path for a route part of type '{part.GetType().Name}'.");
                }
            }
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    /// <summary>The pattern text for a human-facing message, falling back to the structural path when a
    /// model-built pattern has no RawText - the message never decides anything, so a readable fallback is
    /// fine here where it would be a bug in the containment check above.</summary>
    private static string PatternText(RouteEndpoint endpoint)
        => endpoint.RoutePattern.RawText ?? StructuralPath(endpoint.RoutePattern);
}
