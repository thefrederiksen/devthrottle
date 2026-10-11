using System;
using System.Linq;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The route-pattern normaliser: what the refusal is mapped on, and the grammar it claims to cover.
///
/// WHY THIS CLASS EXISTS AS ITS OWN THING. The normaliser was originally hand-written against the pattern
/// TEXT and validated against simple inline policies only - and that is a narrower question set than the
/// grammar it has to survive. The route grammar has optional parameters, default values, catch-alls with
/// two sigils, escaped braces, and policy arguments that themselves contain the delimiters a text scan is
/// looking for. A scan that is right on simple shapes and silently wrong on exotic ones does not produce a
/// visible bug: it produces a refusal that does not cover its own route, while the family's author believes
/// it does. That is manufactured coverage, which is the exact defect the boundary exists to remove.
///
/// So the normaliser now rebuilds the PARSED MODEL and this class states the grammar it covers, one test
/// per element, plus the behaviour on something it does not model: it THROWS, and does not pass through.
/// </summary>
public sealed class HostedRefusalPatternTests
{
    /// <summary>
    /// The ONE transformation. Everything else in these tests is a preservation claim; this is the change,
    /// and it is the reason the whole class exists - a refusal carrying the real route's constraint is never
    /// selected for the request that fails that constraint, which is the hole this closes.
    /// </summary>
    [Theory]
    [InlineData("/x/{id:int}", "id")]
    [InlineData("/x/{id:int:min(1)}", "id")]                    // several policies on one parameter
    [InlineData("/x/{name:regex(^[[a-z]]+$)}", "name")]          // a policy argument containing delimiters
    [InlineData("/x/{a:int}/y/{b:alpha}", "a")]                  // several constrained parameters
    public void Every_parameter_policy_is_removed(string pattern, string firstParameterName)
    {
        var normalised = HostedRefusalPattern.WithoutPolicies(pattern, "probe");

        Assert.All(AllParameters(normalised), p => Assert.Empty(p.ParameterPolicies));
        Assert.Equal(firstParameterName, AllParameters(normalised).First().Name);
    }

    /// <summary>
    /// Preservation, element by element. Each row is a piece of grammar the normaliser CLAIMS to cover, and
    /// the claim is worth exactly as much as the row that tests it - which is why they are enumerated rather
    /// than sampled. Losing any of these would change which requests the refusal matches.
    /// </summary>
    [Theory]
    [InlineData("/x/{id?}")]                       // optional
    [InlineData("/x/{id:int?}")]                   // optional AND constrained
    [InlineData("/x/{id=7}")]                      // default value
    [InlineData("/x/{id:int=7}")]                  // default AND constrained
    [InlineData("/x/{**rest}")]                    // catch-all, slash-preserving
    [InlineData("/x/{*rest}")]                     // catch-all, the other sigil
    [InlineData("/x/y")]                           // pure literals
    [InlineData("/x/{a}-{b}")]                     // two parameters in one complex segment
    [InlineData("/x/file.{ext}")]                  // literal and parameter sharing a segment
    [InlineData("/{a}/{b}/{c}")]                   // several segments
    public void The_shape_of_the_route_survives_normalisation(string pattern)
    {
        var original = RoutePatternFactory.Parse(pattern);
        var normalised = HostedRefusalPattern.WithoutPolicies(pattern, "probe");

        // Same segment structure, part for part.
        Assert.Equal(original.PathSegments.Count, normalised.PathSegments.Count);
        foreach (var (before, after) in original.PathSegments.Zip(normalised.PathSegments))
            Assert.Equal(before.Parts.Count, after.Parts.Count);

        // Same parameters, with kind and default intact - optionality and catch-all semantics are the real
        // route's, not something the normaliser decided.
        var originalParameters = AllParameters(original).ToList();
        var normalisedParameters = AllParameters(normalised).ToList();

        Assert.Equal(originalParameters.Count, normalisedParameters.Count);
        foreach (var (before, after) in originalParameters.Zip(normalisedParameters))
        {
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.ParameterKind, after.ParameterKind);
            Assert.Equal(before.Default, after.Default);
        }

        // Literal text is untouched, which is what keeps a refusal from widening across a path boundary.
        Assert.Equal(LiteralText(original), LiteralText(normalised));
    }

    /// <summary>
    /// A pattern the framework itself rejects is not the normaliser's to reinterpret. It is parsed by the
    /// framework's parser, so a malformed pattern fails there with the framework's own message rather than
    /// being quietly accepted by a lenient hand-written scan.
    /// </summary>
    [Theory]
    [InlineData("/x/{unclosed")]
    [InlineData("/x/{}")]
    public void A_malformed_pattern_is_refused_by_the_frameworks_own_parser(string pattern)
        => Assert.ThrowsAny<Exception>(() => HostedRefusalPattern.WithoutPolicies(pattern, "probe"));

    /// <summary>
    /// THE EXHAUSTIVENESS CLAIM, which is what the fail-loud rule actually rests on here.
    ///
    /// The normaliser handles three route part kinds and THROWS on anything else. That throw is
    /// unreachable today - and I would rather say so than imply it is covered: <c>RoutePatternPart</c> has
    /// an internal constructor, so the framework's three subclasses are the entire hierarchy and no test in
    /// this repository can manufacture a fourth. The defensive arm therefore has no test, deliberately, and
    /// this is the test that guards the assumption UNDERNEATH it instead.
    ///
    /// If a framework upgrade ever adds a fourth part kind, this reddens - which is the moment somebody
    /// needs to decide whether the normaliser handles it or refuses it. Without this, that upgrade would
    /// land silently and the first sign would be a startup failure on somebody's exotic route, or worse, a
    /// refusal that quietly did not cover it.
    /// </summary>
    [Fact]
    public void The_route_part_kinds_the_normaliser_handles_are_still_the_only_ones_that_exist()
    {
        var partKinds = typeof(RoutePatternPart).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(RoutePatternPart)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "RoutePatternLiteralPart", "RoutePatternParameterPart", "RoutePatternSeparatorPart" },
            partKinds);
    }

    private static System.Collections.Generic.IEnumerable<RoutePatternParameterPart> AllParameters(RoutePattern pattern)
        => pattern.PathSegments.SelectMany(s => s.Parts).OfType<RoutePatternParameterPart>();

    private static string LiteralText(RoutePattern pattern)
        => string.Join("/", pattern.PathSegments
            .SelectMany(s => s.Parts)
            .OfType<RoutePatternLiteralPart>()
            .Select(p => p.Content));

}

/// <summary>
/// The finalised route-space check, in the form it took AFTER review disproved the previous one.
///
/// The previous version tried to detect whether two patterns would TIE in the matcher, using a hand-rolled
/// shape key. Three independent route pairs escaped it and returned HTTP 500 at request time. The reason is
/// worth keeping: the matcher's ambiguity relation is not reachable from public API, so any check for it is
/// a MODEL of framework semantics rather than the semantics themselves - and that was the SECOND time this
/// primitive modelled a framework semantic and got it wrong.
///
/// So this no longer detects ambiguity. It checks the one thing that removes the conditions for it and that
/// can be computed correctly: an EXCLUSIVE-PREFIX family claims a prefix nothing else may serve under, and
/// that claim is verified by simple PREFIX CONTAINMENT.
/// </summary>
public sealed class HostedRefusalRouteSpaceTests
{
    private static readonly HostedDenial Denial = new(
        family: "probe",
        message: "not available on the hosted gateway",
        reason: "the probe family exists to prove the route-space check",
        unDenyInstruction: "nothing to un-deny: a test fixture");

    [Fact]
    public void A_live_route_under_an_exclusively_claimed_prefix_fails_the_start()
    {
        // The over-refusal direction, which is an OUTAGE rather than a leak: the catch-all would swallow a
        // route nobody denied. No leak-shaped test in this repository would notice that.
        var endpoints = new[]
        {
            ExclusiveRefusal("/family"),
            LiveEndpoint("/family/still-serving"),
        };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));

        Assert.Contains("EXCLUSIVELY", error.Message);
        Assert.Contains("still-serving", error.Message);
    }

    [Fact]
    public void A_prefix_with_nothing_else_underneath_it_is_allowed()
    {
        var endpoints = new[]
        {
            ExclusiveRefusal("/family"),
            LiveEndpoint("/elsewhere/route"),
            LiveEndpoint("/familyish/route"),   // shares a text prefix but not a PATH prefix
        };

        HostedRefusalRouteSpace.Validate(endpoints);
    }

    [Fact]
    public void The_check_is_case_insensitive_about_the_prefix()
    {
        // Literal route matching is case-insensitive, so a containment check that was ordinal would miss a
        // live route differing only in case - and would then be swallowed at runtime by the catch-all.
        var endpoints = new[] { ExclusiveRefusal("/family"), LiveEndpoint("/FAMILY/still-serving") };

        Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));
    }

    [Fact]
    public void A_route_space_with_no_refusals_is_inert()
        => HostedRefusalRouteSpace.Validate(new[] { LiveEndpoint("/x/{id}"), LiveEndpoint("/y") });

    // ---- CHANGE 1: a tie INTRODUCED by refusal substitution fails the start, rather than surfacing as a
    //      request-time 500 on a denied route. These drive the production validator directly, over a
    //      finalised endpoint set, which is where the promise at GatewayHost.cs was previously not kept. ----

    [Fact]
    public void Two_refusals_that_tie_after_case_folding_fail_the_start()
    {
        // The Codex counterexample, at the route-space level: GET /x/{id} and POST /X/{name} become verb-less
        // refusals whose ONLY differences - method, literal case, parameter name - are exactly what
        // substitution erases. They TIE, and an unvalidated route space would let that become a 500 on the
        // denied route the first time a caller hit it. The validator is the backstop that fails the start.
        var endpoints = new[] { Refusal("/x/{id}"), Refusal("/X/{name}") };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));
        Assert.Contains("compete for the same route shape", error.Message);
    }

    [Fact]
    public void A_refusal_that_ties_with_a_live_route_fails_the_start()
    {
        // The verb-less refusal matches every method, so it ties with any live route of the same shape - a
        // denied family reaching over a route it does not own. Over-refusal, and a 500 rather than a clean
        // answer; the start fails instead.
        var endpoints = new[] { Refusal("/x/{id}"), LiveEndpoint("/x/{other}") };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));
        Assert.Contains("live route", error.Message);
    }

    [Fact]
    public void A_required_value_live_route_ties_with_a_literal_refusal_and_fails_the_start()
    {
        // The precedence source matters. The framework's RouteTemplate(RoutePattern) conversion DROPS a
        // pattern's RequiredValues, but the pattern's OWN InboundPrecedence treats a parameter carrying a
        // non-empty required value as a LITERAL. So a live /x/{slug} whose required value is slug=fixed
        // matches ONLY /x/fixed, at the SAME precedence as the literal refusal /x/fixed - the two TIE. Method
        // removal manufactures exactly this: a method-specific denied /x/fixed beside a verb-less
        // required-value live endpoint. Reconstructing precedence through a RouteTemplate would score the live
        // pattern as a plain parameter and miss the tie; reading the pattern's own InboundPrecedence sees it.
        var live = RoutePatternFactory.Parse(
            "/x/{slug}", defaults: null, parameterPolicies: null, requiredValues: new { slug = "fixed" });

        // The tie is a precedence equality on the FINALISED patterns - documented here so the reason the
        // start must fail is explicit, not just the throw below.
        Assert.Equal(RoutePatternFactory.Parse("/x/fixed").InboundPrecedence, live.InboundPrecedence);

        var endpoints = new[] { Refusal("/x/fixed"), LiveFromPattern(live) };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));
        Assert.Contains("live route", error.Message);
    }

    [Fact]
    public void Refusals_on_distinct_shapes_beside_a_non_colliding_live_route_are_allowed()
    {
        // The other direction: distinct shapes do NOT tie, and a literal sibling is a different shape from a
        // parameter segment - the neighbour must keep serving. A validator that reddened here would be an
        // over-refusal in its own right.
        HostedRefusalRouteSpace.Validate(new[] { Refusal("/x/{id}"), Refusal("/y/{id}"), LiveEndpoint("/x/summary") });
    }

    // ---- CHANGE 3: exclusive containment compares finalised patterns STRUCTURALLY, and a non-literal
    //      exclusive prefix is refused at construction. ----

    [Theory]
    [InlineData("/family/{tenant:int}")]   // a parameter carrying a policy
    [InlineData("/family/{tenant}")]       // a bare parameter, different name from any live route
    [InlineData("/family/{**rest}")]       // a catch-all
    public void A_non_literal_exclusive_prefix_is_refused_at_construction(string prefix)
    {
        // Exclusivity is verified by LITERAL prefix containment, which a parameterised prefix cannot support:
        // /family/{tenant:int} normalises to /family/{tenant}, and a live /family/{scope}/still-serving would
        // not textually start with the original prefix and would serve beneath the claim unseen. The prefix
        // is rejected where it is declared, not left to slip the route-space check.
        var outer = NewBuilder();

        var error = Assert.Throws<ArgumentException>(() => HostedRouteDeny.ExclusiveGroup(outer, prefix, Denial, hosted: true));
        Assert.Contains("route parameter", error.Message);
    }

    [Fact]
    public void A_model_built_live_route_with_null_raw_text_under_the_prefix_fails_the_start()
    {
        // A pattern rebuilt from the route MODEL - exactly how the refusal patterns are built - has a null
        // RawText. Reading a null RawText as the root "/" is what let a model-built live route pass
        // containment while it served beneath the exclusively claimed prefix. The path is read from the
        // parsed segments instead, so this fails the start.
        var modelLive = RoutePatternFactory.Pattern(
            RoutePatternFactory.Segment(RoutePatternFactory.LiteralPart("family")),
            RoutePatternFactory.Segment(RoutePatternFactory.LiteralPart("still-serving")));
        Assert.Null(modelLive.RawText);

        var endpoints = new[] { ExclusiveRefusal("/family"), LiveFromPattern(modelLive) };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));
        Assert.Contains("EXCLUSIVELY", error.Message);
    }

    private static IEndpointRouteBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        return builder.Build();
    }

    private static RouteEndpoint Refusal(string pattern)
        => new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(new HostedRefusalMarker(Denial, pattern)),
            displayName: pattern);

    private static RouteEndpoint LiveFromPattern(RoutePattern pattern)
        => new(
            _ => Task.CompletedTask,
            pattern,
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: pattern.RawText ?? "model-built");

    private static RouteEndpoint ExclusiveRefusal(string prefix)
        => new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(prefix + "/{**rest}"),
            order: 0,
            new EndpointMetadataCollection(
                new HostedRefusalMarker(Denial, prefix),
                new HostedExclusivePrefixMarker(Denial, prefix)),
            displayName: prefix);

    private static RouteEndpoint LiveEndpoint(string pattern)
        => new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: pattern);
}
