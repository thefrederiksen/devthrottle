using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PROVES THE PROPERTY THAT MADE THE GROUP FILTER THE RIGHT SHAPE, WHICH NOTHING ELSE HERE CAN SEE.
///
/// Each of the four denied families is denied through the shared refusal primitive
/// <see cref="CcDirector.Gateway.Tenancy.HostedRouteDeny"/> - three via <c>ExclusiveGroup</c> (one catch-all
/// refusal claims the whole prefix) and transcription via the per-route <c>Group</c> (a refusal mirrors each
/// route mapped through the returned handle) - rather than a <c>DenyOnHosted()</c> call repeated in every
/// handler. The stated reason is that a per-handler guard ROTS - the moment someone adds a route to that
/// file it is undefended and nothing fails - whereas mapping through the primitive's group handle covers
/// every route mapped through it INCLUDING ROUTES THAT DO NOT EXIST YET (an exclusive family's catch-all
/// covers even paths never declared; a per-route family covers every route it maps through the handle).
///
/// That difference is completely invisible to any test that only drives the routes existing today: a
/// per-handler guard and the primitive behave identically on those. Which is exactly what makes the
/// per-handler shape dangerous, and exactly why the claim needs its own proof rather than an assurance in a
/// pull request body. Until this file existed, the pull request asserted the future-route property and
/// nothing tested it.
///
/// So each family maps a BRAND-NEW route onto its returned group handle, with NO deny written for it
/// anywhere. The probe route is a BODY-BOUND POST, not a parameterless GET, because a parameterless GET is
/// the ONE shape the underlying endpoint-filter defect could never be seen through: a filter that answered
/// AFTER model binding still refused a GET correctly while leaking every bound body, a malformed body to the
/// framework's own 400, and a wrong media type to its own 415. The GET probe this file first shipped proved
/// the future-route property but was blind to a regression back to that defect. A body-bound POST is not, so
/// each family asserts:
///
///   1. HOSTED           - the future route is refused across the shapes that regression hides in: a
///                         MALFORMED body (answered by the framework's own 400 under the old defect), a
///                         WRONG MEDIA TYPE (answered by its own 415), and - the load-bearing one - a
///                         NO-BINDING sentinel proving NO handler-bound code ran behind the refusal on any
///                         shape. Each carries that family's refusal and nothing else.
///   2. SELF-HOST        - the SAME body-bound route still BINDS and SERVES, over BOTH non-hosted forms that
///                         occur in practice (the variable absent, and the variable present but "0"), and the
///                         custom binder is proven to have actually run.
///   3. SCOPING CONTROL  - a route mapped OUTSIDE the group still SERVES on hosted.
///
/// All three are needed and none is redundant. Without (2) a filter that refused everything unconditionally -
/// or one that bricked binding on every deployment - would pass every hosted assertion in this file while
/// having silently killed the route for self-host too; a brick is indistinguishable from a working gate if
/// you only push on it from one side. Without (3) the hosted passes could be the host refusing everything
/// rather than the primitive doing its job.
///
/// The self-host legs also assert <see cref="GatewayHostedMode.IsHosted"/> is genuinely false rather than
/// trusting the environment variable to have taken effect, so no leg can silently run in the mode it
/// believes it is not in.
/// </summary>
[Collection("DirectorRoot")]   // serialized: every leg mutates the process-wide CC_GATEWAY_HOSTED and
                              // CC_DIRECTOR_ROOT, so running beside another collection would let one
                              // test's mode leak into another's - the exact silent-wrong-mode failure
                              // the IsHosted assertions above exist to catch.
public sealed class HostedContentDenyGroupFilterTests
{
    private const string ProbeBody = "probe-served-zqxjv";

    /// <summary>
    /// The URL prefix of each family's denied group, because they are NOT all the same and assuming they
    /// were is what broke this rig on its first run. After the move to the shared refusal primitive every
    /// family maps its group at its OWN real prefix and writes its routes relative to that, so a probe mapped
    /// onto the group is reachable only under that prefix - and the prefix has to come from the production
    /// code rather than from an assumption about it. Three families claim their prefix EXCLUSIVELY
    /// (instructions, utterance, dictation); transcription uses the PER-ROUTE group because <c>/transcription</c>
    /// also carries the live batch + cleanup routes, but its group prefix is <c>/transcription</c> all the
    /// same, so its probe sits one segment deeper than before.
    /// </summary>
    // The /dictation and /wingman/utterance upload families are NO LONGER denied (issue #1884, un-deny): they
    // are served tenant-partitioned on hosted, so they are not deny groups and are not probed here. The
    // remaining denied content-read families keep their group-filter proof.
    private static string PrefixFor(string family) => family switch
    {
        "instructions" => "gateway/wingman/instructions/",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static string ProbePath(string family) => PrefixFor(family) + "probe-added-later";

    /// <summary>The second future route, whose parameter is a custom binder we can watch run (or not run).</summary>
    private static string BindingProbePath(string family) => PrefixFor(family) + "probe-binding";

    private const string InstructionsRefusal = "the wingman instructions surface is not available on the hosted gateway";

    /// <summary>Which family, its group-mapper, and the refusal its filter must produce.</summary>
    public static TheoryData<string> Families() => new() { "instructions" };

    private static string RefusalFor(string family) => family switch
    {
        "instructions" => InstructionsRefusal,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>
    /// Asserts the response is EXACTLY this family's refusal and nothing else - the family's error string, a
    /// one-name property set, application/json, and a 404. The media type and property set are checked
    /// through <see cref="ContentFingerprint"/>, so a masking route (the Cockpit single-page-app fallback, or
    /// any other handler) reddens as "expected application/json, got &lt;that&gt;" rather than as a parser
    /// crash - a red that arrives as a crash proves a route moved, not that the guard held.
    /// </summary>
    private static async Task AssertIsExactlyTheRefusal(HttpResponseMessage resp, string family)
    {
        var root = await ContentFingerprint.AsJsonObjectAsync(resp, $"{family} probe on hosted");
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(RefusalFor(family), ContentFingerprint.Text(root, "error", family));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== 1. HOSTED: a BODY-BOUND route added to the group later is refused across the shapes a =====
    // =====    parameterless GET is blind to, and NO handler-bound code runs behind the refusal. =====

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_malformed_body_on_a_route_added_later_meets_the_refusal_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        await using var rig = await Rig.StartAsync(family);

        // Malformed JSON is a shape an earlier boundary let the framework answer with its own 400 instead of
        // the refusal - and a parameterless GET, having no body to be malformed, could never surface it.
        var resp = await rig.PostAsync(ProbePath(family), "{ not json", "application/json");

        await AssertIsExactlyTheRefusal(resp, family);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_wrong_media_type_on_a_route_added_later_meets_the_refusal_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        await using var rig = await Rig.StartAsync(family);

        // A body parameter makes the framework infer a media-type constraint that endpoint SELECTION enforces
        // ahead of any handler; mapping no handler at all on hosted is what strips the constraint with it.
        // The shape that survived longest across candidate designs, and one a GET cannot exercise.
        var resp = await rig.PostAsync(ProbePath(family), "hello", "text/plain");

        await AssertIsExactlyTheRefusal(resp, family);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task No_argument_binder_runs_behind_the_refusal_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        ProbeBinding.Reset();
        await using var rig = await Rig.StartAsync(family);

        // The claim the GET probe could not make: not that the answer was right, but that NO handler-bound
        // code executed - the property that separates a refusal placed BEFORE binding from one placed after
        // it, and the one that was false under the original endpoint-filter defect on every shape, a valid
        // body included.
        await rig.PostAsync(BindingProbePath(family), "{\"value\":\"x\"}", "application/json");
        await rig.PostAsync(BindingProbePath(family), "{ not json", "application/json");
        await rig.PostAsync(BindingProbePath(family), "hello", "text/plain");

        Assert.Equal(0, ProbeBinding.Count);
    }

    // ===== 2. SELF-HOST: the same body-bound route still BINDS and SERVES, over BOTH non-hosted forms =====

    [Theory]
    [InlineData("instructions", null)]
    [InlineData("instructions", "0")]
    public async Task A_route_added_to_the_group_still_binds_and_serves_on_self_host(string family, string? hostedValue)
    {
        using var env = new HostedEnv(hostedValue);
        Assert.False(GatewayHostedMode.IsHosted,
            $"the self-host leg must actually be in self-host mode (CC_GATEWAY_HOSTED={hostedValue ?? "absent"})");

        ProbeBinding.Reset();
        await using var rig = await Rig.StartAsync(family);

        // The JSON body binds and the handler serves it back: the positive twin of the hosted no-binding
        // claim, and proof the substitution did not brick the route for self-host.
        var echo = await rig.PostAsync(ProbePath(family), $"{{\"text\":\"{ProbeBody}\"}}", "application/json");
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal(ProbeBody, await echo.Content.ReadAsStringAsync());

        // And the custom binder ACTUALLY RAN. Without this a primitive that broke binding on every deployment
        // would satisfy the hosted no-binding assertion and nothing here would notice.
        var bound = await rig.PostAsync(BindingProbePath(family), "{\"value\":\"x\"}", "application/json");
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        Assert.Equal(1, ProbeBinding.Count);
    }

    // ===== 3. SCOPING CONTROL: a route OUTSIDE the group still serves on hosted =====

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_outside_the_group_still_serves_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        await using var rig = await Rig.StartAsync(family);

        // Mapped on the application, NOT on the guarded group. If this were refused, the hosted passes
        // above would be the host refusing everything rather than the primitive being correctly scoped.
        var resp = await rig.Http.GetAsync("outside-the-group");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(ProbeBody, body);
    }

    /// <summary>Sets CC_GATEWAY_HOSTED for one test and restores whatever was there before.</summary>
    private sealed class HostedEnv : IDisposable
    {
        private readonly string? _prior;

        public HostedEnv(string? value)
        {
            _prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _prior);
    }

    /// <summary>
    /// A minimal application carrying ONE family's guarded group, plus a probe route mapped onto that
    /// group and a control route mapped outside it. Deliberately not a whole GatewayHost: the point is to
    /// hold the returned group and map onto it, which only the endpoint's own Map can hand back.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _root;
        private readonly string? _priorRoot;
        private readonly GatewayDbTestHarness? _db;
        public HttpClient Http { get; }

        private Rig(WebApplication app, HttpClient http, string root, string? priorRoot,
            GatewayDbTestHarness? db)
        {
            _app = app;
            Http = http;
            _root = root;
            _priorRoot = priorRoot;
            _db = db;
        }

        /// <summary>A POST with an explicit body and media type - the shapes the body-bound probes exercise.</summary>
        public Task<HttpResponseMessage> PostAsync(string path, string content, string mediaType)
            => Http.PostAsync(path, new StringContent(content, Encoding.UTF8, mediaType));

        public static async Task<Rig> StartAsync(string family)
        {
            var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            var root = Path.Combine(Path.GetTempPath(), "ccd-probe-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");

            GatewayDbTestHarness? db = null;
            var group = MapFamily(app, family, root, ref db);

            // The brand-new routes, on the guarded group, with NO deny of their own - and BODY-BOUND, so they
            // exercise the shape a parameterless GET is blind to. One carries a JSON body (a malformed body
            // and a wrong media type are answerable here); the other a custom binder whose execution is
            // observable, so "no handler-bound code ran" is a fact and not an inference.
            group.MapPost("/probe-added-later", (EchoBody body) => Results.Text(body.Text));
            group.MapPost("/probe-binding", (ProbeBinding probe) => Results.Text(probe.Value));
            // The control, deliberately OUTSIDE the group.
            app.MapGet("/outside-the-group", () => Results.Text(ProbeBody));

            await app.StartAsync();
            return new Rig(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
                root, priorRoot, db);
        }

        private static HostedDenyGroup MapFamily(WebApplication app, string family, string root,
            ref GatewayDbTestHarness? db)
        {
            var brain = (TenantId _, WingmanModelRole _, string _, CancellationToken _) =>
                Task.FromException<IAgentBrain>(
                    new InvalidOperationException("the brain must not be reached by a routing probe"));

            switch (family)
            {
                case "instructions":
                    db = new GatewayDbTestHarness();
                    return WingmanInstructionsEndpoint.Map(
                        app,
                        new WingmanInstructionsStore(db.Open(), db.LegacyPath("probe-legacy.json")),
                        brain);

                // NOTE: /wingman/utterance and /dictation are deliberately absent - they are un-denied and
                // tenant-partitioned (issue #1884), not deny groups, so they have no group filter to probe.

                default:
                    throw new ArgumentOutOfRangeException(nameof(family), family, "unknown family");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
            _db?.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>The JSON body the echo probe binds. A malformed body and a wrong media type are shapes that
    /// only exist because this parameter does - which is the whole reason the probe is no longer a GET.</summary>
    private sealed record EchoBody(string Text);

    /// <summary>
    /// A parameter whose BINDING IS OBSERVABLE. Argument binding leaves no trace of its own, so proving that
    /// nothing bound on hosted requires a parameter that records the fact it was bound. This is the instrument
    /// for the load-bearing claim - not that the response was right, but that no handler-bound code ran at all.
    /// Its counter is private to this class and its tests are serialized by the collection, so a Reset-then-
    /// assert cannot be contaminated by another class.
    /// </summary>
    private sealed class ProbeBinding
    {
        private static int _count;

        public string Value { get; init; } = "";

        public static int Count => Volatile.Read(ref _count);

        public static void Reset() => Interlocked.Exchange(ref _count, 0);

        public static ValueTask<ProbeBinding?> BindAsync(HttpContext context)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult<ProbeBinding?>(new ProbeBinding { Value = ProbeBody });
        }
    }
}
