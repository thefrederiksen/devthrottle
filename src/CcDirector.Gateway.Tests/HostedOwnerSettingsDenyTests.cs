using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// One place naming EVERY route in the owner-settings group, with the verb each one actually answers on.
/// Both sides of the proof read this table: the hosted side asserts every row is REFUSED, the self-host
/// side asserts every row is SERVED. Writing the table once is what makes "every route, both directions"
/// a checkable claim rather than a sentence in a pull request.
/// </summary>
public static class OwnerSettingsRoutes
{
    /// <summary>The refusal a route in <c>SettingsEndpoints</c> answers with on hosted.</summary>
    public const string SettingsRefusal = "gateway settings are not available on the hosted gateway";

    /// <summary>The refusal a route in <c>AiModelsEndpoint</c> answers with on hosted.</summary>
    public const string ModelsRefusal = "the model settings are not available on the hosted gateway";

    /// <summary>A route in the group: the verb it answers, its path, and a well-formed body for writes.</summary>
    public sealed record Route(string Verb, string Path, string? Body, string Refusal)
    {
        public override string ToString() => Verb + " /" + Path;
    }

    /// <summary>
    /// The routes mapped by <c>SettingsEndpoints</c> that STAY DENIED on hosted. Bodies are WELL FORMED and
    /// would be accepted on self-host, so a refusal here can never be mistaken for a validation rejection.
    ///
    /// Issue #2022 part 1 removed five machine-scoped routes (brain restart/config, addressing GET+PUT,
    /// autostart) - they do not exist any more. Issue #2022 part 2 RETIRED the deny for the per-account routes
    /// (the settings snapshot, snooze-default, snooze-presets, time-zone, ai-provider, tts-voice), which now
    /// SERVE on hosted (proved by <see cref="HostedPerAccountSettingsServeTests"/>). Injected text was un-denied
    /// too and now serves per-tenant (issue #2057). What remains DENIED here is the one process-global route
    /// with no tenant dimension: transcription mode (a single-valued provider fact).
    /// </summary>
    public static readonly Route[] Settings =
    {
        new("GET",  "gateway/transcription-mode",       null, SettingsRefusal),
        new("PUT",  "gateway/transcription-mode",       "{\"mode\":\"devthrottle\"}", SettingsRefusal),
    };

    /// <summary>
    /// The <c>AiModelsEndpoint</c> routes that STAY DENIED on hosted (issue #2022 part 2): the catalog and
    /// test-chat, which spend the SHARED deployment provider credential with no per-caller scoping. The five
    /// per-account model/voice setters were un-denied and now SERVE on hosted (see
    /// <see cref="HostedPerAccountSettingsServeTests"/>).
    /// </summary>
    public static readonly Route[] Models =
    {
        new("GET",  "gateway/ai/models",              null, ModelsRefusal),
        new("POST", "gateway/ai/test-chat",           "{\"model\":\"some-model\"}", ModelsRefusal),
    };

    /// <summary>All 4 route-and-verb pairs that STILL refuse on hosted after the issue #2022 + #2057 deny retirements.</summary>
    public static IEnumerable<Route> All => Settings.Concat(Models);

    /// <summary>
    /// Every route as xUnit theory data, so no row can be silently left out of either side. Flattened to
    /// plain strings (an empty body string means "no body") DELIBERATELY: xUnit can only pre-enumerate and
    /// NAME theory cases whose arguments are serializable, and a run whose cases cannot be named
    /// individually cannot be checked against the declared method set by name.
    /// </summary>
    public static TheoryData<string, string, string, string> AllRoutes
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            foreach (var route in All) data.Add(route.Verb, route.Path, route.Body ?? "", route.Refusal);
            return data;
        }
    }

    /// <summary>
    /// Sends one row. Kept here so the hosted and self-host sides drive the routes IDENTICALLY and any
    /// difference between them is the Gateway's behaviour, not the client's.
    /// </summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient http, string verb, string path, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(request);
    }

    /// <summary>Overload for the non-theory call sites that already hold a <see cref="Route"/>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient http, Route route) =>
        SendAsync(http, route.Verb, route.Path, route.Body ?? "");

    /// <summary>
    /// Asserts a response is the hosted refusal and NOTHING ELSE.
    ///
    /// FORMAT FACTS FIRST, ON PURPOSE. The status and the media type are asserted BEFORE the body is
    /// parsed, because parsing is itself an unstated assertion about format. On this Gateway a 404 is not
    /// necessarily JSON - a single-page-application fallback answers unmatched paths and can reply with
    /// plain text, or on a release host with a 200 and HTML - so a test that parses first turns a real
    /// finding into a crash. A crash proves the mutation broke something upstream; it cannot say WHAT was
    /// served in place of the refusal, which is the entire claim a deny makes.
    ///
    /// The body is then compared as an ALLOW-LIST over the whole property set, not by substring. A
    /// substring match cannot see an extra leaked field, and a deny-list of today's keys rots the moment
    /// the payload grows.
    /// </summary>
    public static async Task AssertIsNothingButTheRefusal(HttpResponseMessage response, string expectedMessage)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// Issue #1863: the WHOLE owner-settings group is DENIED on the hosted Gateway.
///
/// THE DEFECT (as it stands after issues #2022 + #2057). FOUR routes across two endpoint classes read and
/// write PROCESS-GLOBAL configuration with no tenant dimension: config.json is one file for the whole
/// process, so a write to transcription mode is a FLEET-WIDE mutation performed by whichever authenticated
/// caller sent it. The AI catalog and test-chat spend the shared deployment credential with no per-caller
/// scoping. These STAY refused on hosted. The per-account routes (the settings snapshot, snooze, time zone,
/// ai-provider, tts-voice, the five model/voice setters, and injected text) were UN-DENIED once their runtime
/// consumers were tenant-threaded - they now serve on hosted, resolving the caller's tenant, and are proved by
/// <see cref="HostedPerAccountSettingsServeTests"/>. (Issue #2022 part 1 also removed five machine-scoped
/// routes - brain restart/config, network addressing, autostart - by taking them off the web page.)
///
/// WHY A DENY AND NOT A PARTITION. There is nothing to partition BY. These are not per-tenant records
/// whose storage unit could carry a tenant - they are single global values, one per key, and a
/// "per-tenant" answer would have to be invented from an attribution that was never recorded. That is a
/// half-partition, which is worse than an honest refusal because it looks like isolation. The concept
/// does not survive the move either: this is the OWNER'S control panel for HIS OWN Gateway, and on
/// shared hosted infrastructure "the owner" is not a thing.
///
/// SELF-HOST IS THE CONTROL AND IS UNCHANGED. Self-host is single-tenant and every one of these is
/// legitimate owner function there. <see cref="HostedOwnerSettingsSelfHostControlTests"/> proves that
/// explicitly, in both non-hosted forms, by asserting the REAL served result of every route.
///
/// IT REFUSES, IT DOES NOT SERVE EMPTY. An empty settings snapshot is indistinguishable from "you have
/// no settings", which is a false statement rather than an absent one. Every refusal is a 404 whose body
/// is EXACTLY one <c>error</c> property, asserted as an allow-list over the whole property set.
///
/// ONE SHARED REFUSAL PRIMITIVE PER FAMILY, NOT A GUARD PER ROUTE. Each of the two endpoint classes maps
/// its routes through <see cref="CcDirector.Gateway.Tenancy.HostedRouteDeny.Group"/>, which on hosted maps
/// a verb-less refusal in place of each handler - so a route added to the group later is refused too, with
/// no deny of its own. <see cref="HostedOwnerSettingsGroupFilterTests"/> is the test of that property: a
/// guard repeated per handler passes every other test in this file.
///
/// The primitive REPLACED an earlier bespoke <c>AddEndpointFilter</c> deny (a request-time filter that ran
/// inside the still-mapped handler). The upgrade shows up on the one shape the old filter could not answer -
/// a verb the route never served - proved by
/// <see cref="The_brain_restart_route_answers_the_refusal_on_a_verb_it_never_served_on_hosted"/>.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsDenyTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _deviceKey = "";

    public HostedOwnerSettingsDenyTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // EXPLICIT, never ambient. This class asserts hosted behaviour, so it states hosted mode itself
        // and proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that
        // even this one is refused: no credential makes an owner's control panel correct on shared
        // infrastructure, because there is no owner.
        _deviceKey = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceKey);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// EVERY route, on its OWN verb, refused to a fully enrolled tenant - and refused with a body carrying
    /// nothing but the refusal.
    /// </summary>
    [Theory]
    [MemberData(nameof(OwnerSettingsRoutes.AllRoutes), MemberType = typeof(OwnerSettingsRoutes))]
    public async Task Every_owner_settings_route_is_refused_on_hosted(
        string verb, string path, string body, string refusal)
    {
        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        await OwnerSettingsRoutes.AssertIsNothingButTheRefusal(response, refusal);
    }

    /// <summary>
    /// The refusal is a REFUSAL, not an empty snapshot. The exact-property-set assertion above is what
    /// enforces this; this states the intent in the terms the mistake would be made in - a denied route must
    /// not be handed a snapshot that reads "you have no custom text", which is a false statement where an
    /// absent one is merely absent. Uses the STILL-DENIED routes only (issue #2022 part 2 un-denied the
    /// per-account snapshot and snooze-presets, so those now serve - see HostedPerAccountSettingsServeTests).
    /// </summary>
    [Fact]
    public async Task The_refusal_is_not_an_empty_settings_snapshot()
    {
        foreach (var path in new[] { "gateway/transcription-mode" })
        {
            var response = await _http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("\"placeholders\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"enabled\"", body, StringComparison.Ordinal);
            Assert.Contains("not available on the hosted gateway", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A refused write CHANGED NOTHING. The refusal is asserted by an INDEPENDENT re-read of the
    /// configuration store rather than by the response body, because a filter that returned 404 after the
    /// handler had already run would look identical from outside.
    /// </summary>
    [Fact]
    public async Task A_refused_write_does_not_reach_the_configuration_store()
    {
        var timeZoneBefore = TimeZoneConfig.Get();
        var voiceBefore = TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get());

        foreach (var route in OwnerSettingsRoutes.All.Where(r => r.Body is not null))
            await OwnerSettingsRoutes.AssertIsNothingButTheRefusal(
                await OwnerSettingsRoutes.SendAsync(_http, route), route.Refusal);

        Assert.Equal(timeZoneBefore, TimeZoneConfig.Get());
        Assert.Equal(voiceBefore, TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()));
    }

    /// <summary>
    /// CONTROL, and it must stay green through the revert: the deny runs after the host-wide auth gate, so
    /// it must not have opened these routes up as a side effect.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("gateway/settings")).StatusCode);
    }

}

/// <summary>
/// Boots ONE owner-settings family onto a bare application with no authentication gate and NO
/// single-page-application fallback, and hands the caller the denied group HANDLE back so a test can map
/// routes through it. That return value is what makes the future-route proof possible at all: the handle is
/// created inside each <c>Map</c> method, so nothing outside those methods could otherwise state a property
/// about routes added to it later.
///
/// No fallback is deliberate as well. On the real Gateway an unmapped path is answered by the
/// single-page-application fallback, so "the route is not there" and "the route is there and refused" can
/// look alike. Here an UNDECLARED path is a bare 404 with no body, while a DECLARED route's path answers the
/// refusal on every verb (the primitive maps a verb-less refusal, so there is no 405) - which is what lets
/// <see cref="HostedOwnerSettingsGroupFilterTests"/> tell a refused route apart from an unmapped one.
/// </summary>
internal static class OwnerSettingsProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        Func<IEndpointRouteBuilder, HostedDenyGroup> mapFamily,
        Action<HostedDenyGroup>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = mapFamily(app);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }
}

/// <summary>
/// The well-formed JSON body the future-route canary binds THROUGH THE FRAMEWORK. A plain record parameter is
/// inferred as the JSON body (the same shape as the reference's <c>EchoBody</c>), so the framework infers the
/// media-type and parse constraints and a malformed body is the framework's own 400 on self-host - which is
/// what makes the hosted "malformed body meets the refusal" claim a real pre-emption of binding rather than a
/// custom binder side-stepping the bytes.
/// </summary>
internal sealed record CanaryBody(string Text);

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal is mapped through the shared primitive on each route
/// GROUP, so it covers routes that have not been written yet.
///
/// A guard line repeated in every handler passes EXACTLY the same tests as the group refusal for the routes
/// that exist today, which is precisely what makes it dangerous rather than merely untidy: the difference
/// only appears on the route somebody adds NEXT, when it is open on hosted by default and nothing fails.
/// That difference is not observable by driving the 31 routes that exist, so this class maps a BRAND-NEW
/// probe route THROUGH each denied handle and asserts it is refused with no deny written for it anywhere.
/// These are the tests that justify routing every handler through the primitive at all, and they are the
/// reason all three <c>Map</c> methods now return their denied handle.
///
/// The served half of the same probe - the SAME probe paths, with hosted mode explicitly OFF in both
/// non-hosted forms - is <see cref="HostedOwnerSettingsSelfHostProbeTests"/>. One direction alone cannot
/// tell a working gate apart from a brick: a refusal that fired everything unconditionally would pass every
/// assertion in this class while having silently killed the routes for self-host too.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsGroupFilterTests : IAsyncLifetime
{
    internal const string ProbeSentinel = "probe-payload-that-must-never-be-served-on-hosted";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-group-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;

    public HostedOwnerSettingsGroupFilterTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-group-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        // A real, started GatewayHost, because SettingsEndpoints.Map needs one. Its own routes are not
        // driven here; the probe application maps a second copy of the family and is what the tests call.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "probe-token",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>The two families, each mapped on its own so one refusal is the only thing in the way.</summary>
    internal Func<IEndpointRouteBuilder, HostedDenyGroup> Family(string name) => name switch
    {
        "settings" => routes => SettingsEndpoints.Map(routes, _gateway),
        "models" => routes => AiModelsEndpoint.Map(routes, new KeyVault(Path.Combine(_root, "vault.json")), _gateway.TenantSettingsResolver, _gateway.TenantBoundary),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown owner-settings family"),
    };

    public static TheoryData<string, string> Families => new()
    {
        { "settings", "/gateway/added-after-the-deny-was-written" },
        { "models", "/gateway/ai/added-after-the-deny-was-written" },
    };

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway - and it is proved through a
    /// BODY-BOUND POST canary, not a parameterless GET, because a parameterless GET is the one shape the
    /// original future-route defect could not be seen through (this is the shape set enumerated in
    /// <c>Tenancy/HostedRouteDenyTests</c>, mirrored here for the owner-settings families). NOTHING in the
    /// endpoint class mentions this path and no guard is written for it here - it is mapped THROUGH the denied
    /// handle, so on hosted the primitive maps a verb-less refusal in its place and the handler is never
    /// mapped at all.
    ///
    /// EVERY BODY SHAPE MEETS THE SAME REFUSAL. The canary binds its body through the FRAMEWORK - a plain
    /// <see cref="CanaryBody"/> record parameter, inferred as the JSON body exactly like the reference's
    /// <c>EchoBody</c>, NOT a custom binder that would ignore the bytes and prove nothing. That is what makes
    /// the malformed-body and wrong-media-type shapes real: on self-host a malformed body is the framework's
    /// own 400 (proved by the self-host half), so a hosted refusal that answers it with a 404 instead is
    /// genuinely pre-empting framework binding rather than side-stepping it. A well-formed body, a malformed
    /// one, and a wrong media type all meet the same 404 here, and the sentinel is served on none of them.
    ///
    /// Map the same canary on the ungrouped builder instead and it serves the sentinel with a 200, which is
    /// the future-route hole stated out loud. The served half - the SAME canary with hosted mode OFF - is
    /// <see cref="HostedOwnerSettingsSelfHostProbeTests"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own(
        string family, string probePath)
    {
        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            Family(family),
            mapIntoGroup: group => group.MapPost(probePath,
                (CanaryBody body) => Results.Json(new { probe = ProbeSentinel, echoed = body.Text })));
        try
        {
            // Valid body, malformed body, and a wrong media type - each meets the refusal, not a 400/415, and
            // the sentinel is served on none of them.
            await AssertIsNothingButTheRefusal(await http.PostAsync(probePath, JsonBody("{\"text\":\"hello\"}")));
            await AssertIsNothingButTheRefusal(await http.PostAsync(probePath, JsonBody("{ not json")));
            await AssertIsNothingButTheRefusal(
                await http.PostAsync(probePath, new StringContent("hello", Encoding.UTF8, "text/plain")));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Asserts the canary response is the family refusal and NOTHING ELSE: a 404, application/json, the
    /// sentinel absent, and exactly one <c>error</c> property. Format facts precede the parse for the same
    /// reason as <see cref="OwnerSettingsRoutes.AssertIsNothingButTheRefusal(HttpResponseMessage, string)"/> -
    /// a route that had gone and now served HTML must redden as a media-type mismatch, not a parser crash.
    /// </summary>
    private static async Task AssertIsNothingButTheRefusal(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(ProbeSentinel, body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(new[] { "error" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    /// <summary>
    /// CONTROL: the refusal is scoped to its own group, not a blanket refusal on the whole application. A
    /// route mapped OUTSIDE the denied handle still serves on hosted, so the refusals above are the primitive
    /// doing its job rather than the host refusing everything. This must stay GREEN through the revert.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_outside_the_group_still_serves_on_hosted(string family, string unusedProbePath)
    {
        _ = unusedProbePath;

        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            Family(family),
            mapOutsideGroup: routes => routes.MapGet("/not-an-owner-setting", () => Results.Json(new { served = "yes" })));
        try
        {
            var response = await http.GetAsync("/not-an-owner-setting");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"served\":\"yes\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// A VERB THE ROUTE NEVER SERVED meets the REFUSAL, not a 405 - the headline upgrade the shared refusal
    /// primitive buys over the old request-time group filter, and the reason this rework replaced the filter.
    ///
    /// <c>gateway/transcription-mode</c> serves GET and PUT and stays denied on hosted (a single-valued
    /// process-global provider fact with no per-tenant answer). Under the old
    /// <c>AddEndpointFilter</c> deny the route's handler was still MAPPED on hosted and the filter ran inside
    /// it, so a POST to that path would be answered by endpoint SELECTION with 405 <c>Allow: GET, PUT</c> -
    /// which discloses that a route exists on a Gateway whose refusal says it does not. The primitive maps a
    /// VERB-LESS refusal on the path and never maps the handler at all, so every verb - including one the route
    /// never served (POST) - meets the same 404 refusal. A wrong verb IS a request shape, and the refusal is
    /// uniform across shapes.
    ///
    /// THIS IS REVERT-PROOF. Restore the real handler on hosted (map it instead of the refusal) and this POST
    /// goes back to 405, reddening the refusal assertion. And it still answers the question the old 405 did -
    /// a refused path is distinguished from one that was never mapped: the refused path carries the error
    /// body, a never-mapped path is a bare 404 with no body and no <c>Allow</c> header on this fallback-free
    /// probe host.
    /// </summary>
    [Fact]
    public async Task A_route_answers_the_refusal_on_a_verb_it_never_served_on_hosted()
    {
        var (app, http) = await OwnerSettingsProbeHost.StartAsync(Family("settings"));
        try
        {
            // POST a GET/PUT-only route. NOT a 405 - the verb-less refusal answers it, uniformly across verbs.
            var wrongVerb = await http.PostAsync("/gateway/transcription-mode", new StringContent(""));
            await OwnerSettingsRoutes.AssertIsNothingButTheRefusal(wrongVerb, OwnerSettingsRoutes.SettingsRefusal);
            Assert.Empty(wrongVerb.Content.Headers.Allow);

            // The refusal above is this route's own, not a catch-all: a path NOT in the family answers a bare
            // 404 with no body on this fallback-free probe host. Per-route mode refuses only the paths the
            // family declares, so an undeclared sub-path is genuinely unmapped.
            var neverMapped = await http.GetAsync("/gateway/no-such-route");
            Assert.Equal(HttpStatusCode.NotFound, neverMapped.StatusCode);
            Assert.Empty(neverMapped.Content.Headers.Allow);
            Assert.Equal(string.Empty, await neverMapped.Content.ReadAsStringAsync());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
