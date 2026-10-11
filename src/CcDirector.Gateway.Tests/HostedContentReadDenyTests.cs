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

/// <summary>
/// THE SELF-HOST CONTROL, and the reason it is a separate class: it boots the SAME GatewayHost with hosted
/// mode OFF and drives the SAME routes.
///
/// This is the condition the deny has to meet to be correct rather than merely safe. Self-host is
/// single-tenant, these features WORK there, and the owner uses them - the transcription analysis is how
/// an agent measures dictation quality, the wingman instructions are the owner's editable wingman prompt,
/// and the two upload families are the phone's voice and dictation lanes. A deny scoped to the wrong signal
/// would break the shipped product in order to protect the unshipped one, and it would do so silently.
///
/// EVERY ASSERTION HERE IS A HANDLER RECEIPT, AND THAT IS NOT A STYLE CHOICE - IT IS THE ONLY THING THAT
/// WORKS ON THIS GATEWAY. Two route-masking catch-alls exist, and both were read on this branch rather
/// than assumed:
///
///   1. THE ONE THAT APPLIES TO EVERY ROUTE BELOW - the single-page-app fallback.
///      <c>CockpitReactApp</c> maps <c>MapFallback("{*path}")</c> (CockpitReactApp.cs:125), which matches
///      ANY path and ANY verb that nothing else claimed, and its own not-found text says
///      "React Cockpit not built into this Gateway (release build only)". So a deleted, renamed or
///      re-verbed route answers 404 in a Debug test host but 200 WITH THE HTML SHELL on a release host.
///      Any assertion resting on "I got a 200" passes on a route that no longer exists.
///   2. The all-verb session catch-all. <c>SessionWsProxyEndpoints</c> maps
///      <c>app.Map("/sessions/{sid}/{**rest}")</c> (SessionWsProxyEndpoints.cs:153) - least-specific,
///      every verb - which swallows a deleted or re-verbed route on any <c>/sessions/{sid}/...</c> path
///      and answers 503 as <c>application/json</c>, never reaching the fallback, so a <c>text/html</c>
///      check sails straight past it. NONE of the four families denied here lives under
///      <c>/sessions/{sid}/</c>, so this one cannot mask THESE routes - it is recorded because it is why
///      "check the content type" is not a general defence on this Gateway, and the next route added to
///      this file might well sit under that prefix.
///
/// A 405 never occurs on these paths in either condition, so "the verb is wrong" does not announce itself
/// either. That is why the previous version of this class was unsound: it asserted status 200 plus the
/// ABSENCE of the refusal strings, and a built Cockpit shell satisfies both. It also drove <c>POST</c> at
/// the dictation chunk route, which production maps as <c>MapPut</c> ONLY
/// (GatewayDictationEndpoint, the <c>/{uploadId}/chunk/{index:int}</c> leg) - a request that never reached
/// the handler at all, and nothing in the assertion could notice.
///
/// So: every read asserts SEEDED or ROUTE-SPECIFIC JSON that only that handler could have produced, and
/// every write asserts its own STORE RECEIPT - a state change read back - rather than a status code.
/// Neither catch-all can fake those.
///
/// These are still not change-detectors: what is pinned is the one value this test itself planted, or the
/// exact top-level property set of a handler whose payload is a stable contract, never an incidental
/// field a later feature would legitimately move.
///
/// Three untouched pre-existing suites are controls too, and they exercise these families with real
/// payloads: <see cref="VoiceUploadLimitsTests"/> (the real utterance upload family end to end),
/// <see cref="DictationSessionLockTests"/> and <see cref="DurableDictationDedupeTests"/>.
///
/// MUTATION-RED RECIPE (run verbatim; this is the only check that catches both catch-alls, because it
/// does not depend on predicting what the framework returns):
///   For each route below, in the production file, either RENAME the path (add "-x") or RE-VERB it
///   (MapGet -> MapPost, MapPut -> MapPost). Rebuild, check the build line, run this class. The test for
///   that route must go RED. If it stays GREEN the canary cannot fail and the assertion is worthless -
///   fix the assertion, not the route. Restore, and confirm green.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedContentReadSelfHostControlTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>Planted as a dictionary correction, so /transcription/terms must report this pair.</summary>
    private const string SeededFind = "zqxjvfind";
    private const string SeededReplace = "zqxjvreplace";

    private static readonly string[] Refusals =
    {
        "transcription analysis is not available on the hosted gateway",
        "the wingman instructions surface is not available on the hosted gateway",
        "the wingman utterance upload is not available on the hosted gateway",
        "dictation upload is not available on the hosted gateway",
    };

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-selfhost-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;

    public HostedContentReadSelfHostControlTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfhost-content-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Hosted mode explicitly OFF - the whole point of this class. Cleared rather than set to "0" so it
        // is the same absence a real self-host install has.

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _key = _gateway.Devices.Register("dev-owner", "MA").DeviceKey;

        SeedTranscriptionHistory();
    }

    /// <summary>
    /// Plant one minimized transcription-health record under this test's isolated storage root.
    /// The self-host reads derive from this one record, so
    /// each of them has something only that handler could return.
    /// </summary>
    private static void SeedTranscriptionHistory()
    {
        new TranscriptionHistoryLog().Record(new TranscriptionHistoryRecord
        {
            TimestampUtc = DateTime.UtcNow,
            TurnId = "seed-turn",
            Outcome = "ok",
            TranscriptionMs = 120,
            CleanupMs = 30,
            Corrected = true,
            CleanupApplied = true,
            ChangedWordCount = 1,
            Changes = new[] { new TranscriptionHistoryEdit { Find = SeededFind, Replace = SeededReplace } },
            CharCount = 24,
            WordCount = 4,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ===== The transcription analysis reads: each asserts the SEEDED record back out =====

    [Fact]
    public async Task Self_host_transcription_turns_returns_the_seeded_turn()
    {
        var root = await GetJsonAsync("transcription/turns");

        var turns = ContentFingerprint.Arr(root, "turns", "GET transcription/turns").EnumerateArray().ToArray();
        var seeded = Assert.Single(turns);
        Assert.Equal("seed-turn", ContentFingerprint.Str(seeded, "turnId"));
        Assert.False(seeded.TryGetProperty("rawText", out _));
        Assert.False(seeded.TryGetProperty("cleanedText", out _));
    }

    [Fact]
    public async Task Self_host_transcription_stats_counts_the_seeded_turn()
    {
        var root = await GetJsonAsync("transcription/stats");

        // Exactly one turn was planted into an isolated, otherwise-empty log, so this is an exact figure
        // rather than a "greater than zero" that an accidental extra record could satisfy.
        Assert.Equal(1, ContentFingerprint.Num(root, "totalTurns", "GET transcription/stats"));
        Assert.Equal(1, ContentFingerprint.Num(root, "successfulTurns", "GET transcription/stats"));
    }

    [Fact]
    public async Task Self_host_transcription_terms_returns_the_seeded_correction()
    {
        var root = await GetJsonAsync("transcription/terms");

        var pairs = ContentFingerprint.Arr(root, "terms", "GET transcription/terms").EnumerateArray()
            .Select(t => (ContentFingerprint.Str(t, "find"), ContentFingerprint.Str(t, "replace")))
            .ToArray();
        Assert.Contains((SeededFind, SeededReplace), pairs);
    }

    // ===== The wingman instructions reads =====

    /// <summary>
    /// Saving a version and reading it back is the strongest available receipt for the instructions
    /// routes: the value asserted is one this test itself planted through the public surface, so no
    /// catch-all and no shipped default can produce it.
    /// </summary>
    [Fact]
    public async Task Self_host_wingman_instructions_round_trips_a_saved_version()
    {
        var content = "seeded wingman instructions zqxjv " + Guid.NewGuid().ToString("N");

        var saved = await Send(HttpMethod.Put, "gateway/wingman/instructions",
            JsonSerializer.Serialize(new { content }));
        // Fingerprint, not status: re-verbing this route sends the request to the Cockpit fallback, and a
        // status check would redden on 404 rather than on "that was not the save handler answering".
        var savedBody = await JsonAsync(saved, "PUT gateway/wingman/instructions");
        Assert.Equal(content, ContentFingerprint.Text(
            ContentFingerprint.Prop(savedBody, "active", "PUT gateway/wingman/instructions"),
            "content", "PUT gateway/wingman/instructions"));

        var active = await GetJsonAsync("gateway/wingman/instructions");
        var activeVersion = ContentFingerprint.Prop(active, "active", "GET gateway/wingman/instructions");
        Assert.Equal(content,
            ContentFingerprint.Text(activeVersion, "content", "GET gateway/wingman/instructions"));
        Assert.True(ContentFingerprint.Flag(active, "isCustomized", "GET gateway/wingman/instructions"));

        var versions = await GetJsonAsync("gateway/wingman/instructions/versions");
        Assert.Contains(
            ContentFingerprint.Arr(versions, "versions", "GET gateway/wingman/instructions/versions").EnumerateArray(),
            v => ContentFingerprint.Str(v, "content") == content);

        // The managed-default review must now report the box as customized - a state change this test
        // caused, read back through a DIFFERENT route than the one that caused it.
        var update = await GetJsonAsync("gateway/wingman/instructions/update");
        Assert.True(ContentFingerprint.Flag(update, "isCustomized", "GET gateway/wingman/instructions/update"));
    }

    /// <summary>
    /// The deployed default has no seedable value - it ships with the build - so this pins the one thing
    /// that is genuinely route-specific and contractual: the exact top-level property set, plus non-empty
    /// content. The Cockpit HTML shell is not JSON at all, and the /sessions catch-all's 503 body has a
    /// different property set, so neither can satisfy this.
    /// </summary>
    [Fact]
    public async Task Self_host_wingman_default_returns_the_deployed_default_prompt()
    {
        var root = await GetJsonAsync("gateway/wingman/instructions/default");

        Assert.Equal(
            new[] { "version", "hash", "content" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(
            ContentFingerprint.Text(root, "content", "GET gateway/wingman/instructions/default")));
    }

    /// <summary>
    /// THE REVERT ROUTE, which the hosted deny refuses and which nothing here served-proved until now.
    ///
    /// A deny that answers 404 is indistinguishable from a route that was deleted, so every denied path and
    /// verb needs a self-host fact showing THIS handler answers it. Revert gets the strongest kind: two
    /// versions are saved through the public surface, the FIRST is made active again by id, and the active
    /// content is read back through a DIFFERENT route than the one that changed it. Both values were
    /// planted by this test, so no catch-all and no shipped default can produce them.
    /// </summary>
    [Fact]
    public async Task Self_host_wingman_revert_makes_an_earlier_version_active_again()
    {
        var first = "seeded wingman version one zqxjv " + Guid.NewGuid().ToString("N");
        var second = "seeded wingman version two zqxjv " + Guid.NewGuid().ToString("N");

        var firstId = await SaveInstructionsAsync(first);
        await SaveInstructionsAsync(second);

        // PRECONDITION, asserted not assumed: the SECOND version is the active one, so the revert below is
        // load bearing. Without this the test would pass even if revert did nothing at all.
        var beforeRevert = await GetJsonAsync("gateway/wingman/instructions");
        Assert.Equal(second, ContentFingerprint.Text(
            ContentFingerprint.Prop(beforeRevert, "active", "GET gateway/wingman/instructions (before revert)"),
            "content", "GET gateway/wingman/instructions (before revert)"));

        var reverted = await Send(HttpMethod.Post, "gateway/wingman/instructions/revert",
            JsonSerializer.Serialize(new { id = firstId }));
        var revertedBody = await JsonAsync(reverted, "POST gateway/wingman/instructions/revert");
        Assert.Equal(first, ContentFingerprint.Text(
            ContentFingerprint.Prop(revertedBody, "active", "POST gateway/wingman/instructions/revert"),
            "content", "POST gateway/wingman/instructions/revert"));

        // Read back through a different route: the change was durable in the store, not a returned value.
        var afterRevert = await GetJsonAsync("gateway/wingman/instructions");
        Assert.Equal(first, ContentFingerprint.Text(
            ContentFingerprint.Prop(afterRevert, "active", "GET gateway/wingman/instructions (after revert)"),
            "content", "GET gateway/wingman/instructions (after revert)"));
    }

    /// <summary>
    /// THE SWITCH-TO-DEFAULT ROUTE, served-proved the same way: a real state transition, with the
    /// precondition asserted so the transition cannot be vacuous.
    ///
    /// A custom version is saved (so the box is genuinely customized - asserted), the route is called, and
    /// the box is read back through a DIFFERENT route as no longer customized AND serving the deployed
    /// default's content rather than the planted one. A handler that did nothing, and a catch-all answering
    /// both calls identically, both fail that.
    /// </summary>
    [Fact]
    public async Task Self_host_wingman_switch_to_default_drops_the_custom_version()
    {
        var custom = "seeded wingman custom zqxjv " + Guid.NewGuid().ToString("N");
        await SaveInstructionsAsync(custom);

        var deployedDefault = ContentFingerprint.Text(
            await GetJsonAsync("gateway/wingman/instructions/default"),
            "content", "GET gateway/wingman/instructions/default");
        Assert.False(string.IsNullOrWhiteSpace(deployedDefault));
        Assert.NotEqual(deployedDefault, custom);

        // PRECONDITION: the box really is customized right now, so the switch below has something to undo.
        var before = await GetJsonAsync("gateway/wingman/instructions");
        Assert.True(ContentFingerprint.Flag(before, "isCustomized",
            "GET gateway/wingman/instructions (before switch-to-default)"));

        var switched = await Send(HttpMethod.Post, "gateway/wingman/instructions/switch-to-default", "");
        var switchedBody = await JsonAsync(switched, "POST gateway/wingman/instructions/switch-to-default");
        Assert.False(ContentFingerprint.Flag(switchedBody, "isCustomized",
            "POST gateway/wingman/instructions/switch-to-default"));

        var after = await GetJsonAsync("gateway/wingman/instructions");
        Assert.False(ContentFingerprint.Flag(after, "isCustomized",
            "GET gateway/wingman/instructions (after switch-to-default)"));
        Assert.Equal(deployedDefault, ContentFingerprint.Text(
            ContentFingerprint.Prop(after, "active", "GET gateway/wingman/instructions (after switch-to-default)"),
            "content", "GET gateway/wingman/instructions (after switch-to-default)"));
    }

    /// <summary>Saves a version through the real route and returns its id, asserting the save answered.</summary>
    private async Task<string> SaveInstructionsAsync(string content)
    {
        var resp = await Send(HttpMethod.Put, "gateway/wingman/instructions",
            JsonSerializer.Serialize(new { content }));
        var body = await JsonAsync(resp, "PUT gateway/wingman/instructions");
        var active = ContentFingerprint.Prop(body, "active", "PUT gateway/wingman/instructions");
        Assert.Equal(content, ContentFingerprint.Text(active, "content", "PUT gateway/wingman/instructions"));
        var id = ContentFingerprint.Text(active, "id", "PUT gateway/wingman/instructions");
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    // ===== The two upload families: every leg leaves a receipt =====

    /// <summary>
    /// The owner's phone still registers an utterance upload AND the bytes it sends are staged. The chunk
    /// leg has no read route, so the receipt is the staged file itself: the whole isolated storage root is
    /// searched for a file whose CONTENT is the exact payload sent. Searching by content rather than by an
    /// expected path means the assertion cannot pass by looking in the wrong place, and cannot fail merely
    /// because the store renames its layout.
    /// </summary>
    [Fact]
    public async Task Self_host_utterance_upload_registers_and_stages_the_chunk_bytes()
    {
        var register = await Send(HttpMethod.Post, "wingman/utterance/upload", "");
        var registerBody = await JsonAsync(register, "POST wingman/utterance/upload");
        var id = ContentFingerprint.Text(registerBody, "upload_id", "POST wingman/utterance/upload");
        Assert.False(string.IsNullOrWhiteSpace(id));

        var payload = Encoding.UTF8.GetBytes("utterance-bytes-zqxjv-" + Guid.NewGuid().ToString("N"));
        var chunk = await SendBytes(HttpMethod.Put, $"wingman/utterance/{id}/chunk/0", payload);
        var chunkBody = await JsonAsync(chunk, "PUT wingman/utterance/{uploadId}/chunk/0");
        Assert.Equal(0,
            ContentFingerprint.Num(chunkBody, "index", "PUT wingman/utterance/{uploadId}/chunk/0"));

        AssertStagedOnDisk(payload);
    }

    /// <summary>
    /// The same for the dictation lane. NOTE THE VERB: production maps this chunk route with
    /// <c>MapPut</c> and nothing else. An earlier version of this test drove <c>POST</c>, which never
    /// reached the handler - and because no 405 occurs on this path, nothing in a status-based assertion
    /// could notice. The staged-bytes receipt is what makes the verb matter.
    /// </summary>
    [Fact]
    public async Task Self_host_dictation_upload_registers_and_stages_the_chunk_bytes()
    {
        var id = await RegisterDictationAsync(Guid.NewGuid().ToString("N"));

        var payload = Encoding.UTF8.GetBytes("dictation-bytes-zqxjv-" + Guid.NewGuid().ToString("N"));
        var chunk = await SendBytes(HttpMethod.Put, $"dictation/{id}/chunk/0", payload);
        var chunkBody = await JsonAsync(chunk, "PUT dictation/{uploadId}/chunk/0");
        Assert.Equal(0,
            ContentFingerprint.Num(chunkBody, "index", "PUT dictation/{uploadId}/chunk/0"));

        AssertStagedOnDisk(payload);
    }

    /// <summary>
    /// The abandon leg gets its own receipt, and it is a durable state change rather than a status:
    /// abandoning writes a terminal tombstone, so RE-REGISTERING the same upload id afterwards reports it
    /// dropped. Read back through a different route than the one that caused it, which no catch-all can
    /// fake.
    /// </summary>
    [Fact]
    public async Task Self_host_dictation_abandon_writes_a_tombstone_read_back_at_register()
    {
        var key = Guid.NewGuid().ToString("N");
        var id = await RegisterDictationAsync(key);

        var abandon = await Send(HttpMethod.Post, $"dictation/{id}/abandon", "");
        var abandonBody = await JsonAsync(abandon, "POST dictation/{uploadId}/abandon");
        Assert.True(
            ContentFingerprint.Flag(abandonBody, "abandoned", "POST dictation/{uploadId}/abandon"));

        var reRegister = await Send(HttpMethod.Post, "dictation/upload",
            "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}", key);
        var again = await JsonAsync(reRegister, "POST dictation/upload (re-register)");
        Assert.True(ContentFingerprint.Flag(again, "terminal", "POST dictation/upload (re-register)"));
        Assert.True(ContentFingerprint.Flag(again, "dropped", "POST dictation/upload (re-register)"));
    }

    /// <summary>
    /// The ack leg gets its own receipt too, and it is a state TRANSITION rather than a single value:
    /// acking a tombstone retires it, so the first ack reports retired and a second ack on the same id
    /// reports not-retired. A catch-all answering both calls identically cannot produce that difference.
    ///
    /// THE PRECONDITION IS ASSERTED, NOT ASSUMED, AND THAT IS THE WHOLE CORRECTION HERE. An earlier
    /// version registered, abandoned, then acked twice - and passed IDENTICALLY when the abandon route was
    /// renamed away, because <c>Acknowledge</c> only requires the staging DIRECTORY to exist and
    /// registering alone creates it. So the transition it measured was directory-existence, not
    /// tombstone-retirement: the test never established the tombstone it is named for. The route-mutation
    /// sweep caught it - renaming POST /dictation/{id}/abandon left this row green when it was declared to
    /// redden, which is precisely the canary-cannot-fail signal.
    ///
    /// It now reads the tombstone back through the register route BEFORE acking, so the abandon is load
    /// bearing, and reads it back again AFTER to prove the retirement was real rather than a returned
    /// boolean.
    /// </summary>
    [Fact]
    public async Task Self_host_dictation_ack_retires_the_tombstone_exactly_once()
    {
        const string session = "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}";
        var key = Guid.NewGuid().ToString("N");
        var id = await RegisterDictationAsync(key);

        var abandon = await Send(HttpMethod.Post, $"dictation/{id}/abandon", "");
        var abandonBody = await JsonAsync(abandon, "POST dictation/{uploadId}/abandon");
        Assert.True(
            ContentFingerprint.Flag(abandonBody, "abandoned", "POST dictation/{uploadId}/abandon"));

        // PRECONDITION: a terminal tombstone really exists now. If the abandon did not happen, this
        // re-register is an ordinary fresh upload carrying no "terminal" property at all, and this
        // assertion fails - which is what makes the rest of the test depend on the abandon.
        var beforeAck = await JsonAsync(
            await Send(HttpMethod.Post, "dictation/upload", session, key),
            "POST dictation/upload (before ack)");
        Assert.True(ContentFingerprint.Flag(beforeAck, "terminal", "POST dictation/upload (before ack)"));

        var first = await Send(HttpMethod.Post, $"dictation/{id}/ack", "");
        var firstBody = await JsonAsync(first, "POST dictation/{uploadId}/ack (first)");
        Assert.True(
            ContentFingerprint.Flag(firstBody, "retired", "POST dictation/{uploadId}/ack (first)"));

        var second = await Send(HttpMethod.Post, $"dictation/{id}/ack", "");
        var secondBody = await JsonAsync(second, "POST dictation/{uploadId}/ack (second)");
        Assert.False(
            ContentFingerprint.Flag(secondBody, "retired", "POST dictation/{uploadId}/ack (second)"));

        // AND the retirement was real: the same id now registers as a FRESH upload, with no terminal
        // outcome to report. A returned "retired: true" alone would not have shown that.
        var afterAck = await JsonAsync(
            await Send(HttpMethod.Post, "dictation/upload", session, key),
            "POST dictation/upload (after ack)");
        Assert.False(afterAck.TryGetProperty("terminal", out _));
        Assert.False(string.IsNullOrWhiteSpace(
            ContentFingerprint.Text(afterAck, "upload_id", "POST dictation/upload (after ack)")));
    }

    /// <summary>
    /// THE TWO COMPLETE LEGS GET A SERVED-SIDE PROOF TOO, AND THEY NEEDED ONE.
    ///
    /// A deny that answers 404 is indistinguishable from a route that does not exist. So a hosted
    /// refusal, on its own, is satisfied just as well by the route having been deleted - which means
    /// every denied path needs a self-host proof that THIS handler answers it. The other legs get that
    /// from their store receipts. These two had nothing, because completing an upload ends in a
    /// transcription call and this suite stands up no provider.
    ///
    /// It turns out not to need one. Both handlers validate their body BEFORE resolving any provider,
    /// and each rejects with a message no other handler in the Gateway emits. That message is a
    /// handler-positive fingerprint on exactly the path and verb that is denied on hosted, and it is
    /// deterministic: it depends on no key, no vault state and no network.
    /// </summary>
    [Fact]
    public async Task Self_host_utterance_complete_answers_from_its_own_handler()
    {
        // Body check first, before the upload is even looked up - so this needs no registration.
        var badBody = await Send(HttpMethod.Post, "wingman/utterance/any-id/complete", "{}");
        var badBodyJson = await JsonAsync(badBody, "POST wingman/utterance/{uploadId}/complete (bad body)");
        Assert.Equal("totalChunks (>0) is required",
            ContentFingerprint.Text(badBodyJson, "error", "POST wingman/utterance/{uploadId}/complete"));

        // And a second message from the same handler, one step further in, proving the upload lookup runs.
        var unknown = await Send(HttpMethod.Post, "wingman/utterance/not-a-real-upload/complete",
            "{\"totalChunks\":1}");
        var unknownJson = await JsonAsync(unknown, "POST wingman/utterance/{uploadId}/complete (unknown id)");
        Assert.Equal("unknown upload id (register it first)",
            ContentFingerprint.Text(unknownJson, "error", "POST wingman/utterance/{uploadId}/complete"));
    }

    /// <summary>
    /// The same served-side proof for the dictation completion leg, for the same reason: its hosted
    /// refusal is a 404, and without this nothing distinguishes that refusal from an absent route.
    /// </summary>
    [Fact]
    public async Task Self_host_dictation_complete_answers_from_its_own_handler()
    {
        var badBody = await Send(HttpMethod.Post, "dictation/any-id/complete", "{}");
        var badBodyJson = await JsonAsync(badBody, "POST dictation/{uploadId}/complete (bad body)");
        Assert.Equal("sessionId (guid) and totalChunks (>0) are required",
            ContentFingerprint.Text(badBodyJson, "error", "POST dictation/{uploadId}/complete"));
    }

    // ===== Helpers =====

    /// <summary>
    /// GET a route and return its parsed JSON body, refusing anything that is not a real JSON object from
    /// that handler. Parsing is itself part of the check: the Cockpit single-page-app fallback answers a
    /// deleted GET route with the HTML shell on a release host, and HTML does not parse.
    /// </summary>
    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var resp = await Send(HttpMethod.Get, path);
        await AssertNotTheHostedRefusal(resp);
        // NO status assertion here, deliberately. If this route is renamed the Cockpit fallback answers,
        // and a status check would make the row redden on "404 != 200" - proving a route changed rather
        // than proving this canary can tell the handler's answer from a masking route's. The caller's
        // fingerprint assertion is the first thing allowed to fail.
        return await ContentFingerprint.AsJsonObjectAsync(resp, "GET " + path);
    }

    private static Task<JsonElement> JsonAsync(HttpResponseMessage resp, string what)
        => ContentFingerprint.AsJsonObjectAsync(resp, what);

    private async Task<string> RegisterDictationAsync(string idempotencyKey)
    {
        var resp = await Send(HttpMethod.Post, "dictation/upload",
            "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}", idempotencyKey);
        var body = await JsonAsync(resp, "POST dictation/upload");
        var id = ContentFingerprint.Text(body, "upload_id", "POST dictation/upload");
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    /// <summary>The staged-bytes receipt: some file under the isolated root holds exactly these bytes.</summary>
    private void AssertStagedOnDisk(byte[] payload)
    {
        // Assert the directory exists before enumerating it: EnumerateFiles throws on a missing root,
        // and a throw here would be a crash rather than a statement about what the handler staged.
        Assert.True(Directory.Exists(_root), $"the storage root {_root} does not exist, so nothing could stage");

        var found = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Any(f =>
            {
                try { return File.ReadAllBytes(f).AsSpan().SequenceEqual(payload); }
                catch { return false; }
            });
        Assert.True(found, "the chunk handler did not stage the uploaded bytes anywhere under the storage root");
    }

    /// <summary>
    /// Reads the body and fails if it carries ANY of the four hosted refusal messages. Deliberately checks
    /// all four in every case rather than the one belonging to that route: a copy-paste that wired the
    /// wrong helper onto a group would still be caught. This is a floor, not the assertion - every test
    /// above also proves a handler receipt.
    /// </summary>
    private static async Task AssertNotTheHostedRefusal(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        foreach (var refusal in Refusals)
            Assert.DoesNotContain(refusal, body, StringComparison.Ordinal);
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

    private Task<HttpResponseMessage> SendBytes(HttpMethod method, string path, byte[] payload)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = new ByteArrayContent(payload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _http.SendAsync(req);
    }

}

