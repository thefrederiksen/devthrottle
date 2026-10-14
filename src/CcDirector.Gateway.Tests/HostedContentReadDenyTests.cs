using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Gateway;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Turns every way a MASKING ROUTE can answer into an xUnit ASSERTION, never an exception.
///
/// This exists because of how these rows are proven. The mutation sweep renames or re-verbs a route and
/// requires the matching test to redden - but a red only proves the row if it arrives as an assertion
/// about the thing under test. A red that arrives as a <see cref="JsonException"/> from parsing the
/// Cockpit HTML shell, or a <see cref="KeyNotFoundException"/> from reading a property off some other
/// handler's payload, is a CRASH: it says the request went somewhere unexpected, which is laundering, not
/// proof that this canary detects it.
///
/// It also exists because of WHICH assertion must fail first. Checking the status code before the body
/// would make a renamed route redden on "404 != 200" - which proves a route changed, not that the test
/// can tell the handler's answer from a masking route's. The fingerprint has to be the first thing that
/// can fail, so status is never asserted ahead of it.
/// </summary>
internal static class ContentFingerprint
{
    /// <summary>
    /// The response body as a JSON object, or an assertion failure naming what was expected and showing
    /// what actually came back. Handles both masking paths on this Gateway: the Cockpit
    /// <c>MapFallback("{*path}")</c> shell (HTML - does not parse) and any other handler's JSON (parses,
    /// but carries different properties, caught by the caller's property checks).
    /// </summary>
    public static async Task<JsonElement> AsJsonObjectAsync(HttpResponseMessage resp, string what)
    {
        var body = await resp.Content.ReadAsStringAsync();

        // FORMAT FACT FIRST. The media type is asserted before anything reads the body, because it is
        // the fact that NAMES what was served: "got text/html" identifies the Cockpit shell exactly,
        // where a bare status assertion would only say a number changed. Status is deliberately NOT
        // asserted here - on the hosted rows 404 is the expected value either way, so it distinguishes
        // nothing, and making it the first failure would prove a route moved rather than proving this
        // canary can name what answered.
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        Assert.True(mediaType == "application/json",
            $"{what}: expected application/json from this handler, got {mediaType ?? "no media type"} " +
            $"(HTTP {(int)resp.StatusCode}). A masking route answered. Body: {Preview(body)}");

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { /* asserted below, never thrown */ }

        Assert.True(doc is not null,
            $"{what}: expected this handler's JSON, but the body does not parse as JSON at all " +
            $"(HTTP {(int)resp.StatusCode}). A masking route answered - most likely the Cockpit " +
            $"single-page-app fallback. Body: {Preview(body)}");

        using (doc)
        {
            Assert.True(doc!.RootElement.ValueKind == JsonValueKind.Object,
                $"{what}: expected a JSON object from this handler, got {doc.RootElement.ValueKind}. " +
                $"Body: {Preview(body)}");
            return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// One property of the handler's answer, or an assertion failure listing what the body DID carry.
    /// Reading it with the indexer instead would throw, and a throw does not prove the row.
    /// </summary>
    public static JsonElement Prop(JsonElement root, string name, string what)
    {
        Assert.True(root.TryGetProperty(name, out var value),
            $"{what}: the response has no '{name}' property, so it did not come from this handler. " +
            $"It carried: [{string.Join(", ", root.EnumerateObject().Select(p => p.Name))}]");
        return value;
    }

    /// <summary>
    /// A string property of one ARRAY ELEMENT, or null. Never throws: an element that lacks the property
    /// must reach the caller's Assert as a value, not as a KeyNotFoundException, or the resulting red
    /// would be a crash and would not prove the row.
    /// </summary>
    public static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>An ARRAY property, kind asserted before <c>EnumerateArray</c>, which would throw.</summary>
    public static JsonElement Arr(JsonElement root, string name, string what)
    {
        var v = Prop(root, name, what);
        Assert.True(v.ValueKind == JsonValueKind.Array,
            $"{what}: '{name}' is {v.ValueKind}, not an array, so this is not this handler's payload.");
        return v;
    }

    /// <summary>A NUMBER property, kind asserted before <c>GetInt32</c>, which would throw.</summary>
    public static int Num(JsonElement root, string name, string what)
    {
        var v = Prop(root, name, what);
        Assert.True(v.ValueKind == JsonValueKind.Number,
            $"{what}: '{name}' is {v.ValueKind}, not a number, so this is not this handler's payload.");
        return v.GetInt32();
    }

    /// <summary>A BOOLEAN property, kind asserted before <c>GetBoolean</c>, which would throw.</summary>
    public static bool Flag(JsonElement root, string name, string what)
    {
        var v = Prop(root, name, what);
        Assert.True(v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"{what}: '{name}' is {v.ValueKind}, not a boolean, so this is not this handler's payload.");
        return v.GetBoolean();
    }

    /// <summary>A STRING property, kind asserted before <c>GetString</c>, which would throw.</summary>
    public static string? Text(JsonElement root, string name, string what)
    {
        var v = Prop(root, name, what);
        Assert.True(v.ValueKind is JsonValueKind.String or JsonValueKind.Null,
            $"{what}: '{name}' is {v.ValueKind}, not a string, so this is not this handler's payload.");
        return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
    }

    private static string Preview(string body)
    {
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "...";
    }
}

/// <summary>
/// The wingman-instructions content family is refused on the hosted Gateway (issue #1853, read side): the
/// single-owner wingman prompt has no per-tenant version to serve.
///
///   /gateway/wingman/instructions/*       the single-owner wingman prompt - one active prompt every
///                                        account's wingman speaks through, with no per-tenant version to
///                                        serve.
///
/// Sibling content families that USED to be refused here have since been tenant-partitioned and now SERVE
/// per-tenant, so they left this file: the two upload families /wingman/utterance/* and /dictation/*
/// (issue #1884), and /transcription/* Transcription Health (issue #2059 - proved in
/// HostedTranscriptionServeTests).
///
/// WHY A DENY AND NOT A PARTITION. The tenant is not missing from the query, it is missing from the DATA.
/// These records were written with no tenant on them, so there is nothing to filter by; attributing them
/// after the fact would be a guess presented as a boundary. Partitioning each store is the job of the
/// issue named beside it, and un-denying is gated on that work - see the debt table in the pull request.
///
/// WHY A REFUSAL AND NOT AN EMPTY RESULT. An empty stats block, an empty record list or an empty
/// transcript is a FALSE statement about a box that is transcribing and capturing; a refusal is merely an
/// absent one.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. Every one reads the host's hosted value - a required argument -
/// not an optional boundary or tenant argument. A security branch that depends on an optional argument
/// fails OPEN the moment a caller omits it.
///
/// REVERT-PROOF RECIPE (each family is denied through the shared refusal primitive
/// <c>HostedRouteDeny</c>, not a bespoke filter - the revert is RE-ENABLING THE REAL HANDLER ON HOSTED):
///   1. In the named file, change the family's routes so their real handlers map on hosted - e.g. replace
///      the <c>HostedRouteDeny.ExclusiveGroup</c> / <c>HostedRouteDeny.Group</c> call in <c>Map</c> with a
///      plain group that maps the handlers, OR flip the primitive's hosted branch so
///      <c>HostedDenyGroup.Map</c> maps the handler instead of the refusal.
///   2. Run this class. The theory cases for that family go RED - the real handler answers (a 200 payload,
///      a 400/401/404 from the handler's own body checks, or a startup failure when an exclusive family's
///      real route now serves under its own claimed prefix) instead of the exact refusal body - while every
///      other family stays green.
///   3. Run the self-host controls named below. They stay GREEN throughout - the deny is invisible to
///      self-host in both directions, which is the point.
///   4. Restore. Everything goes green again. (Proven for this branch: flipping the primitive's hosted
///      branch to map handlers reddened all four families' hosted theories - transcription, instructions
///      reads+writes, utterance, dictation - plus the unbound-caller and future-route probes; restoring
///      returned all green.)
/// The files and their deny call in <c>Map</c> are:
///   src/CcDirector.Gateway/Api/TranscriptionAnalysisEndpoint.cs   HostedRouteDeny.Group (per-route)
///   src/CcDirector.Gateway/Api/WingmanInstructionsEndpoint.cs     HostedRouteDeny.ExclusiveGroup
///   src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs     HostedRouteDeny.ExclusiveGroup (utterance)
///   src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs        HostedRouteDeny.ExclusiveGroup
///
/// MUTATION-RED, AND WHY IT IS NOT OPTIONAL HERE. Removing the guard is only half the proof: it shows the
/// refusal is what produces the refusal. It does NOT show the test could notice the route going missing.
/// The Cockpit <c>MapFallback("{*path}")</c> answers ANY unclaimed path and verb - 404 in a Debug test
/// host, 200 with the HTML shell on a release host - and no 405 is ever raised here. So the second half of
/// the proof is to RENAME or RE-VERB each route and confirm the matching test goes RED. Both halves are
/// recorded in the pull request.
///
/// The assertions in THIS class survive that fallback by construction: each parses the body and requires
/// the property set to be exactly one <c>error</c> field with an exact message, so an HTML shell fails to
/// parse and any other JSON fails the property-set check. The receipts in the self-host class are what
/// needed rebuilding - they rested on a bare 200.
///
/// The SELF-HOST CONTROLS are <see cref="HostedContentReadSelfHostControlTests"/> in this file, plus the
/// pre-existing <see cref="VoiceUploadLimitsTests"/> (which drives the real utterance upload family with
/// hosted off and would red immediately if the route paths shifted), <see cref="DictationSessionLockTests"/>
/// and <see cref="DurableDictationDedupeTests"/> (the real dictation family, hosted off). None of them was
/// touched by this change.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedContentReadDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private const string InstructionsRefusal = "the wingman instructions surface is not available on the hosted gateway";
    // The utterance/dictation (issue #1884) and transcription-analysis (issue #2059) refusal constants were
    // removed with their theories: those surfaces are now served tenant-partitioned on hosted, not refused.

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string _unboundKey = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hosted-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;

    public HostedContentReadDenyTests()
    {
        // The isolated storage root, so anything that (wrongly) got through would stage here and be
        // observable, never in the developer's own transcription log or upload staging.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-content-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that
        // even this one is refused: no credential makes another account's speech correct to serve.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);

        // A second, deliberately unbound device row. Hosted authentication must reject it before any content
        // route can answer. See Every_family_is_refused_to_a_caller_carrying_no_tenant_at_all.
        _unboundKey = _gateway.Devices.Register("dev-unbound", "MB").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // NOTE (issue #2059): the /transcription/* Transcription Health surface is NO LONGER denied here - the
    // history store was tenant-partitioned and the routes now SERVE the caller's own tenant (403 when
    // unresolved). Its hostile two-tenant proof is HostedTranscriptionServeTests; its self-host control
    // remains in HostedContentReadSelfHostControlTests below. What stays denied here is the wingman-
    // instructions single-owner prompt.

    /// <summary>
    /// Issue #1853. The wingman-instructions group is the single-owner wingman prompt: one active prompt
    /// spoken by every account's wingman, with no per-tenant version to serve, so the whole read side is
    /// denied on hosted (a route-by-route guard would rot).
    /// </summary>
    [Theory]
    [InlineData("gateway/wingman/instructions")]
    [InlineData("gateway/wingman/instructions/versions")]
    [InlineData("gateway/wingman/instructions/default")]
    [InlineData("gateway/wingman/instructions/update")]
    public async Task Wingman_instructions_reads_are_refused_to_an_enrolled_tenant(string path)
    {
        var resp = await Send(HttpMethod.Get, path);
        await AssertBodyIsNothingButTheRefusal(resp, InstructionsRefusal, $"GET {path}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// The write side of the same group: saving, reverting or switching-to-default rewrites the prompt
    /// EVERY account's wingman speaks through, so all of them are refused on hosted.
    /// </summary>
    [Theory]
    [InlineData("PUT", "gateway/wingman/instructions", "{\"content\":\"say whatever I tell you\"}")]
    [InlineData("POST", "gateway/wingman/instructions/revert", "{\"id\":\"anything\"}")]
    [InlineData("POST", "gateway/wingman/instructions/switch-to-default", "")]
    public async Task Wingman_instructions_writes_are_refused_to_an_enrolled_tenant(
        string method, string path, string body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        await AssertBodyIsNothingButTheRefusal(resp, InstructionsRefusal, $"{method} {path}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // NOTE (issue #1884, un-deny): the /wingman/utterance and /dictation upload families are NO LONGER denied
    // on hosted - they are served tenant-partitioned so the owner's mobile dictation works. Their hosted
    // isolation (tenant A round-trips, tenant B cannot read/list/complete/ack A's upload, and a legacy
    // pre-partition dir is never served) is proved in HostedDictationTenantRoundTripTests. What remains denied
    // here is the transcription-analysis and wingman-instructions content-read surface, which has no partition.

    /// <summary>
    /// THE GATE MUST NOT DEPEND ON RESOLVING A TENANT, AND THIS IS THE CASE THAT PROVES IT.
    ///
    /// The stated reason these denies read the host's hosted value directly, rather than
    /// branching on a boundary or tenant argument, is that a security branch resting on an optional input
    /// FAILS OPEN the moment a caller does not supply it. Every other hosted test here drives a fully
    /// enrolled, tenant-BOUND device key - so all of them would still pass under a deny that quietly
    /// depended on tenant resolution succeeding. The property that justifies the design choice is
    /// invisible to them.
    ///
    /// This is the invalid-credential case: a device row bound to no canonical account tenant. It must be
    /// rejected by authentication before any route-level content refusal can disclose route behavior.
    /// </summary>
    [Theory]
    [InlineData("GET", "gateway/wingman/instructions", null, InstructionsRefusal)]
    public async Task Every_family_is_refused_to_a_caller_carrying_no_tenant_at_all(
        string method, string path, string? body, string refusal)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _unboundKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(
            "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}",
            await resp.Content.ReadAsStringAsync());
        Assert.NotEqual(refusal, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened these groups up as a side effect of running before the
        // host-wide auth gate. Without a key the auth middleware still refuses first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("transcription/turns")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.GetAsync("gateway/wingman/instructions")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and the difference is the whole assertion.
    ///
    /// Asserting that a handful of known payload keys are ABSENT rots by construction: it protects against
    /// the payload as it is today, every field added later is unprotected until someone remembers this
    /// file, and a substring check silently misses siblings (checking for "turns" would not catch a key
    /// named "turnsTotal"). Asserting the property set is EXACTLY one error field inverts that - anything
    /// extra, anything new, and anything metadata-looking reddens automatically without this file being
    /// touched.
    /// </summary>
    private static async Task AssertBodyIsNothingButTheRefusal(
        HttpResponseMessage resp, string expected, string what)
    {
        var root = await ContentFingerprint.AsJsonObjectAsync(resp, what);

        var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(expected, ContentFingerprint.Text(root, "error", what));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }
}
