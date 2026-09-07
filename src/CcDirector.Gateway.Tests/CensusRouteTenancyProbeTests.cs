using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The CENSUS CLOSE-OUT probes (tenant-boundary hardening, release 2026-07-31, the brief's item 5).
///
/// <see cref="ContextLessDatabaseRouteTenancyTests"/> cross-probed EIGHT context-less database routes and
/// explicitly refused to generalise to the rest. The phase-4 census re-derived the whole inventory from
/// the source and reached a written prove-or-deny verdict for every route (the table is in the phase
/// report). Two FAMILIES came out of that census as "protected by the same mechanism as the proven eight,
/// but never executed against two tenants":
///
///   1. <b>The mission notes pair</b> (<c>GET</c> and <c>PUT /gateway/missions/notes</c>) - Phase 3
///      verified them by reading the code and named a route-level probe as unfinished business. They are
///      scoped by the ambient request scope plus the entity global query filter (the architecture's good
///      seam), never by an explicit tenant argument, so only execution can say the seam is actually in
///      the path.
///   2. <b>The skills family</b> - ten context-less routes over <c>SkillStore</c>, the structural twin of
///      the workflow routes the eight-route file proved. It is the LARGEST unproven family in the census
///      and it carries an extra mechanism the workflow twin also has: the shared System library
///      partition, reachable read-only for BUILT-IN ids only (<c>SkillStore.OpenOwningContext</c>). A
///      probe that did not exercise the built-in arm would leave the interesting half untested.
///
/// MECHANICS, taken from <see cref="ContextLessDatabaseRouteTenancyTests"/>: two tenants minted through
/// the product's own hosted enrollment on ONE real hosted <see cref="GatewayHost"/>, every request over
/// real HTTP through the real auth middleware, status and media type asserted BEFORE any parse, owner
/// controls asserting the EXACT SEEDED FINGERPRINT (never a bare 200), and every refusal judged by an
/// independent RE-READ of the other tenant's state plus a destructibility control - the owner performing
/// the SAME operation successfully, so a refusal is a refusal and not an inert route.
///
/// REVERT-PROVE: drop <c>ApplyTenantScope&lt;MissionNoteEntity&gt;</c> or the three
/// <c>ApplyTenantScope&lt;Skill*Entity&gt;</c> registrations from <c>GatewayDbContext</c> and the matching
/// tests here go RED with a cross-tenant fact served.
/// </summary>
public sealed class CensusRouteTenancyProbeTests : IAsyncLifetime
{
    private const string SharedToken = "test-token";

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";
    private CcDirector.Core.Tenancy.TenantId _tenantA;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-census-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public CensusRouteTenancyProbeTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            cronJobsPath: Path.Combine(_instancesDir, "cron", "cronjobs.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions", "missions.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var deviceA = HostedTestEnrollment.Enroll(_gateway, "sub-census-a", "census-a@example.com", "dev-ca", "MCA");
        var deviceB = HostedTestEnrollment.Enroll(_gateway, "sub-census-b", "census-b@example.com", "dev-cb", "MCB");
        _keyA = deviceA.DeviceKey;
        _keyB = deviceB.DeviceKey;
        _tenantA = deviceA.Tenant;

        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(deviceA.Tenant.Value, deviceB.Tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ================================================================= mission notes (mission_notes table)

    /// <summary>
    /// GET + PUT /gateway/missions/notes - both context-less (MissionNotesEndpoint.cs:29 and :34,
    /// handlers <c>()</c> and <c>(MissionNoteBody? req)</c>), scoped ONLY by the ambient request scope and
    /// the entity global query filter.
    ///
    /// The sharpest available probe: BOTH tenants write a note under the SAME mission name (the entity's
    /// composite key (TenantId, Key) makes that legal), each with a distinctive why-text. If the seam were
    /// missing, the second write would find and overwrite the first tenant's row - so this catches both a
    /// cross-tenant READ and a cross-tenant OVERWRITE, and the store's own key lookup
    /// (<c>e.Key == key</c>, unqualified by tenant) is exactly what makes the overwrite the live hazard.
    /// There is no per-mission GET, so each tenant's list is asserted as an EXACT one-element set.
    /// </summary>
        // PORTED 2026-08-07 to the WHY's new home. This probe used to seed through
    // PUT /gateway/missions/notes and read GET /gateway/missions/notes - the name-keyed note store, now
    // retired. The PROPERTY it proves is unchanged and still matters: two tenants using the SAME mission
    // name must not see or overwrite each other's WHY. It is now proved against the mission record, where
    // the WHY lives, which is a stronger form of the same claim - the old routes keyed on the shared name,
    // these key on each tenant's own mission id.
    [Fact]
    public async Task MissionWhy_KeepEachTenantsWhyUnderTheSameMissionNameSeparate()
    {
        const string mission = "shared-mission-name";
        const string whyA = "tenant-A-why-the-census-probe";
        const string whyB = "tenant-B-why-the-census-probe";

        // Each tenant creates its OWN mission, under the identical name.
        var createdA = await Json(await Send("POST", "missions", _keyA, MissionBody(mission)),
            HttpStatusCode.Created, "SEED    POST /missions (tenant A)");
        var createdB = await Json(await Send("POST", "missions", _keyB, MissionBody(mission)),
            HttpStatusCode.Created, "SEED    POST /missions (tenant B, SAME mission name)");
        var idA = Str(createdA, "missionId");
        var idB = Str(createdB, "missionId");
        Assert.NotEqual(idA, idB);

        var setA = await Json(await Send("PATCH", $"missions/{idA}", _keyA, WhyBody(whyA)),
            HttpStatusCode.OK, "SEED    PATCH /missions/{A} (tenant A)");
        Assert.Equal(whyA, Str(Obj(setA, "mission"), "why"));

        var setB = await Json(await Send("PATCH", $"missions/{idB}", _keyB, WhyBody(whyB)),
            HttpStatusCode.OK, "SEED    PATCH /missions/{B} (tenant B)");
        Assert.Equal(whyB, Str(Obj(setB, "mission"), "why"));

        // Each tenant reads back EXACTLY its own why - not the other's, and not overwritten by it.
        var readA = await Json(await Send("GET", $"missions/{idA}", _keyA, null),
            HttpStatusCode.OK, "READ    GET /missions/{A} (tenant A)");
        Assert.Equal(whyA, Str(readA, "why"));

        var readB = await Json(await Send("GET", $"missions/{idB}", _keyB, null),
            HttpStatusCode.OK, "READ    GET /missions/{B} (tenant B)");
        Assert.Equal(whyB, Str(readB, "why"));

        // CROSS-TENANT CONTROL: A cannot reach B's mission even holding its exact id, and the refusal is a
        // 404 - the same answer as an id that does not exist, so the id cannot be probed for existence.
        var crossRead = await Send("GET", $"missions/{idB}", _keyA, null);
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);

        var crossWrite = await Send("PATCH", $"missions/{idB}", _keyA, WhyBody("overwritten-by-A"));
        Assert.Equal(HttpStatusCode.NotFound, crossWrite.StatusCode);

        // ...and the refused write changed nothing: B's why is byte-for-byte what B set.
        var survivingB = await Json(await Send("GET", $"missions/{idB}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /missions/{B} (after A tried to overwrite it)");
        Assert.Equal(whyB, Str(survivingB, "why"));

        // DESTRUCTIBILITY CONTROL: a blank why CLEARS it. A clears its OWN, which must empty A's and leave
        // B's untouched. Without this, the reads above could be an inert route rather than a partitioned one.
        var clearA = await Json(await Send("PATCH", $"missions/{idA}", _keyA, WhyBody("")),
            HttpStatusCode.OK, "CONTROL PATCH /missions/{A} (tenant A clears its OWN why)");
        Assert.Equal("", Str(Obj(clearA, "mission"), "why"));

        var survivingBAgain = await Json(await Send("GET", $"missions/{idB}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /missions/{B} (after A cleared its own)");
        Assert.Equal(whyB, Str(survivingBAgain, "why"));
    }

    private static string MissionBody(string missionName) =>
        System.Text.Json.JsonSerializer.Serialize(new { missionName });

    private static string WhyBody(string why) =>
        System.Text.Json.JsonSerializer.Serialize(new { why });

    // ================================================================= skills (skills/skill_versions/skill_files)

    /// <summary>
    /// The skills READ family, context-less: GET /gateway/skills/{id} (SkillEndpoints.cs:56), /body (:72),
    /// /files/{**filePath} (:83), /versions (:154), /versions/{version:int} (:160). Tenant B owns a
    /// published skill with a distinctive body and a distinctive supporting file; tenant A names its id on
    /// every one of the five routes.
    ///
    /// The file leg matters twice over: <c>{**filePath}</c> is a catch-all, and the census verdict is that
    /// it is an EF string comparison rather than a filesystem path - so this probe also pins that a
    /// traversal-shaped file name reaches no other tenant's content (it simply matches no row).
    /// </summary>
    [Fact]
    public async Task SkillReadFamily_ContextLess_ServesTheOwnerTheExactSeededSkill_AndIsANotFoundForTheOtherTenant()
    {
        const string sid = "bobs-skill";
        const string body = "# Bobs skill\n\nThe seeded body only tenant B may read.";
        const string fileName = "reference.md";
        const string fileContent = "tenant-B-supporting-file-content";

        await SeedPublishedSkill(_keyB, sid, body, fileName, fileContent);

        // OWNER CONTROLS: the exact seeded fingerprint on each of the five routes.
        var head = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id} (owner B)");
        Assert.Equal(sid, Str(head, "id"));
        Assert.Equal("Bobs skill", Str(head, "name"));

        var ownerBody = await Send("GET", $"gateway/skills/{sid}/body", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerBody.StatusCode);
        Assert.Contains(body, await ownerBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var ownerFile = await Send("GET", $"gateway/skills/{sid}/files/{fileName}", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerFile.StatusCode);
        Assert.Contains(fileContent, await ownerFile.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var versions = await Json(await Send("GET", $"gateway/skills/{sid}/versions", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id}/versions (owner B)");
        Assert.Equal(1, Arr(versions, "versions").GetArrayLength());

        var versionOne = await Json(await Send("GET", $"gateway/skills/{sid}/versions/1", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/skills/{id}/versions/1 (owner B)");
        Assert.Equal(1, Int(versionOne, "version"));

        // CROSS: tenant A naming tenant B's skill id on every read route.
        //
        // Inspection finding M03-I2-03. These five rows used to accept "NotFound or BadRequest", and a
        // union of statuses cannot say WHICH decision was reached. SkillEndpoints.Guard deliberately
        // turns any SkillValidationException into a 400, so a future required field, a model-binding
        // change, or handler-specific validation would keep these rows green with the tenant lookup
        // never reached - the exact defect Phase 5a found one row over, in enable and disable. Each row
        // now asserts the SINGLE outcome a cross-tenant miss actually produces: the ordinary not-found,
        // with the not-found contract that route really writes. A 400 now fails the test, which is the
        // point: it would mean the refusal came from somewhere other than the tenant partition.
        foreach (var (path, expectedError) in new[]
                 {
                     ($"gateway/skills/{sid}", $"no skill with id '{sid}'"),
                     ($"gateway/skills/{sid}/body", $"no skill with id '{sid}'"),
                     ($"gateway/skills/{sid}/files/{fileName}", $"no file '{fileName}' on skill '{sid}'"),
                     ($"gateway/skills/{sid}/versions", $"no skill with id '{sid}'"),
                     ($"gateway/skills/{sid}/versions/1", $"no skill with id '{sid}'"),
                 })
        {
            var resp = await Send("GET", path, _keyA, null);
            var text = await resp.Content.ReadAsStringAsync();
            _out.WriteLine($"CROSS   GET /{path} (tenant A) -> {(int)resp.StatusCode} {text}");
            Assert.True(resp.StatusCode == HttpStatusCode.NotFound,
                $"/{path}: tenant A must get the ordinary not-found and nothing else, " +
                $"got {(int)resp.StatusCode}; body was: {text}");
            Assert.Equal(expectedError, ErrorOf(text));
            Assert.DoesNotContain(body, text, StringComparison.Ordinal);
            Assert.DoesNotContain(fileContent, text, StringComparison.Ordinal);
        }

        // THE TRAVERSAL-SHAPED FILE NAME, ON THE CATCH-ALL LEG (inspection finding I1-04).
        //
        // This used to ride in the loop above as "gateway/skills/{id}/files/../../{name}" and it proved
        // NOTHING: HttpClient resolved the dot segments before sending, so the server saw
        // /gateway/skills/{name}, a different route answered, and its 404 satisfied the assertion. The
        // catch-all leg this row is named after never ran. It is now sent WITHOUT canonicalization, so
        // the escape reaches the handler under test, and the test asserts that it did.
        var traversal = $"gateway/skills/{sid}/files/../../{fileName}";
        var travResp = await SendRaw("GET", traversal, _keyA);
        var travText = await travResp.Content.ReadAsStringAsync();
        _out.WriteLine($"CROSS   GET /{traversal} (tenant A, uncanonicalized) -> {(int)travResp.StatusCode} {travText}");

        // The request went out with the dot segments intact - if this fails, the probe is back to
        // testing the wrong route and its refusal below would be meaningless.
        Assert.Contains("/files/../../", travResp.RequestMessage!.RequestUri!.OriginalString, StringComparison.Ordinal);

        // The census verdict for this row is that the file name is a DATABASE KEY and never a path, so a
        // traversal-shaped key is simply a key that matches nothing. Tightened from "404 or 400": the
        // refusal must be the ordinary not-found, and it must not disclose either seeded secret.
        Assert.Equal(HttpStatusCode.NotFound, travResp.StatusCode);
        Assert.DoesNotContain(body, travText, StringComparison.Ordinal);
        Assert.DoesNotContain(fileContent, travText, StringComparison.Ordinal);

        // INDEPENDENT RE-READ: B's skill is untouched by A's attempts.
        var stillThere = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/skills/{id} (owner B, after A's reads)");
        Assert.Equal("Bobs skill", Str(stillThere, "name"));
    }

    /// <summary>
    /// The skills WRITE family, context-less: POST /gateway/skills/{id}/publish (:126), /clone (:137),
    /// /enable (:168), /disable (:173) and DELETE /gateway/skills/{id} (:146). Tenant A names tenant B's
    /// skill id on each; every attempt must be refused, B's skill must survive each one by an independent
    /// re-read, and the OWNER must then perform the same archive successfully - the destructibility
    /// control that makes A's refusals refusals rather than an inert route.
    /// </summary>
    [Fact]
    public async Task SkillWriteFamily_ContextLess_CannotTouchTheOtherTenantsSkill_AndTheOwnerStillCan()
    {
        const string sid = "bobs-write-target";
        const string body = "# Bobs write target\n\nSeeded body.";

        await SeedPublishedSkill(_keyB, sid, body, "notes.md", "supporting");

        // CROSS: every write verb tenant A can name B's id with.
        //
        // Inspection finding I1-04, and the probe was weaker than even the inspection realised. The
        // enable and disable rows used to send an empty body and accept 400 as a refusal. Both routes
        // require a 'by' field naming who is making the change, so both were answering
        // 400 "Turning a skill on or off requires 'by'" - a VALIDATION error raised before any tenancy
        // decision was reached. Those two rows were passing without the tenant boundary ever being
        // consulted. Each row now sends a well formed body, so the only thing left that can refuse it is
        // the tenant partition, and the accepted status is tightened to the ordinary not-found that a
        // cross-tenant miss actually produces.
        foreach (var (method, path, reqBody) in new[]
                 {
                     ("POST", $"gateway/skills/{sid}/publish", "{}"),
                     ("POST", $"gateway/skills/{sid}/enable?by=census-probe", "{}"),
                     ("POST", $"gateway/skills/{sid}/disable?by=census-probe", "{}"),
                     ("DELETE", $"gateway/skills/{sid}", null),
                 })
        {
            var resp = await Send(method, path, _keyA, reqBody);
            var text = await resp.Content.ReadAsStringAsync();
            _out.WriteLine($"CROSS   {method} /{path} (tenant A) -> {(int)resp.StatusCode} {text}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.DoesNotContain(body, text, StringComparison.Ordinal);
        }

        // A's clone attempt of B's id - the clone path is the one write that READS the named skill.
        //
        // Inspection finding I1-04: the clone RESPONSE used to go unasserted entirely. Only the later
        // "no stolen copy" read was checked, which a missing or completely inert clone handler passes
        // just as happily as a working one that correctly refused. The response is now asserted.
        // Inspection finding M03-I2-03, second half: this row asserted only "not a success status",
        // which EVERY 4xx and 5xx satisfies - a validation refusal, a routing miss, a method mismatch,
        // a server fault. Clone has validation and conflict paths around the source lookup, so any of
        // them could have kept this green while the tenant lookup never ran. It now asserts the one
        // outcome a cross-tenant clone actually produces and the exact contract it writes.
        var clone = await Send("POST", $"gateway/skills/{sid}/clone?newId=stolen-copy", _keyA, "{}");
        var cloneText = await clone.Content.ReadAsStringAsync();
        _out.WriteLine($"CROSS   POST clone (tenant A) -> {(int)clone.StatusCode} {cloneText}");
        Assert.True(clone.StatusCode == HttpStatusCode.NotFound,
            $"clone of another tenant's skill must be the ordinary not-found and nothing else, " +
            $"got {(int)clone.StatusCode}; body was: {cloneText}");
        Assert.Equal($"no skill with id '{sid}'", ErrorOf(cloneText));
        Assert.DoesNotContain(body, cloneText, StringComparison.Ordinal);

        var stolen = await Send("GET", "gateway/skills/stolen-copy", _keyA, null);
        var stolenText = await stolen.Content.ReadAsStringAsync();
        Assert.DoesNotContain(body, stolenText, StringComparison.Ordinal);

        // THE LIVE CONTROL FOR CLONE (inspection finding I1-04). A refusal only means something if the
        // same operation demonstrably WORKS for the owner. B clones its own skill and the copy really
        // carries the body - so the handler is present, reachable and functional, and A's refusal above
        // is a refusal rather than an inert route answering everyone the same way.
        var ownerClone = await Send("POST", $"gateway/skills/{sid}/clone?newId=bobs-own-copy", _keyB, "{}");
        _out.WriteLine($"CONTROL POST clone (owner B) -> {(int)ownerClone.StatusCode} {await ownerClone.Content.ReadAsStringAsync()}");
        Assert.True(ownerClone.IsSuccessStatusCode,
            $"the owner's own clone must succeed or this control proves nothing, got {(int)ownerClone.StatusCode}");
        var ownerCopyBody = await Send("GET", "gateway/skills/bobs-own-copy/body", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerCopyBody.StatusCode);
        Assert.Contains(body, await ownerCopyBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // THE LIVE CONTROL FOR ENABLE AND DISABLE (inspection finding I1-04). These two verbs were in
        // the cross-tenant loop above but were never shown to do anything at all, so a route that had
        // been removed would have satisfied the security assertion. The owner drives both here.
        foreach (var verb in new[] { "disable", "enable" })
        {
            var ownerToggle = await Send("POST", $"gateway/skills/{sid}/{verb}?by=census-probe", _keyB, "{}");
            var toggleText = await ownerToggle.Content.ReadAsStringAsync();
            _out.WriteLine($"CONTROL POST /gateway/skills/{{id}}/{verb} (owner B) -> {(int)ownerToggle.StatusCode} {toggleText}");
            Assert.True(ownerToggle.IsSuccessStatusCode,
                $"the owner's own {verb} must succeed or A's refusal of it proves nothing, got {(int)ownerToggle.StatusCode}; body was: {toggleText}");
        }

        // INDEPENDENT RE-READ: B's skill survived every attempt, still published, still readable.
        var survivor = await Json(await Send("GET", $"gateway/skills/{sid}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/skills/{id} (owner B, after A's write attempts)");
        Assert.Equal(sid, Str(survivor, "id"));
        var survivorBody = await Send("GET", $"gateway/skills/{sid}/body", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, survivorBody.StatusCode);
        Assert.Contains(body, await survivorBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // CONTROL: the owner performs the archive A was refused, and the effect is re-read.
        var ownerArchive = await Send("DELETE", $"gateway/skills/{sid}", _keyB, null);
        Assert.Equal(HttpStatusCode.OK, ownerArchive.StatusCode);
        var gone = await Send("GET", $"gateway/skills/{sid}", _keyB, null);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>
    /// The SHARED LIBRARY arm, which the cross-tenant tests above cannot see: a BUILT-IN skill lives in
    /// the System partition and is deliberately readable by every tenant (<c>SkillStore.OpenOwningContext</c>,
    /// the named capability SharedSkillLibraryRead). This asserts the sanctioned sharing is real - both
    /// tenants read the SAME built-in - so a future change that broke it would be caught here rather than
    /// read as "isolation improved", and the census's verdict on the built-in arm is executed rather than
    /// argued.
    /// </summary>
    [Fact]
    public async Task ABuiltInSkill_IsReadableByBothTenants_TheSanctionedSharedLibrary()
    {
        var listA = await Json(await Send("GET", "gateway/skills", _keyA, null),
            HttpStatusCode.OK, "READ    GET /gateway/skills (tenant A)");
        var builtInIds = Arr(listA, "skills").EnumerateArray()
            .Where(s => s.TryGetProperty("isBuiltIn", out var b) && b.ValueKind == JsonValueKind.True)
            .Select(s => Str(s, "id"))
            .ToList();
        Assert.NotEmpty(builtInIds); // the seeded library must exist, or the arm below proves nothing
        var builtIn = builtInIds[0];

        foreach (var key in new[] { _keyA, _keyB })
        {
            var head = await Json(await Send("GET", $"gateway/skills/{builtIn}", key, null),
                HttpStatusCode.OK, $"SHARED  GET /gateway/skills/{builtIn}");
            Assert.Equal(builtIn, Str(head, "id"));

            var bodyResp = await Send("GET", $"gateway/skills/{builtIn}/body", key, null);
            Assert.Equal(HttpStatusCode.OK, bodyResp.StatusCode);
            Assert.NotEmpty(await bodyResp.Content.ReadAsStringAsync());
        }
    }

    private async Task SeedPublishedSkill(string key, string id, string body, string fileName, string fileContent)
    {
        var draft = JsonSerializer.Serialize(new
        {
            id,
            name = "Bobs skill",
            summary = "A skill owned by one tenant, named by the other.",
            triggers = new[] { "census probe" },
            bodyMarkdown = body,
            files = new[] { new { fileName, content = fileContent, encoding = "utf8" } },
            authoredBy = "census-test",
        });

        var created = await Send("POST", "gateway/skills", key, draft);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"SEED POST /gateway/skills failed: {(int)created.StatusCode} {createdText}");

        var published = await Send("POST", $"gateway/skills/{id}/publish", key, "{}");
        var publishedText = await published.Content.ReadAsStringAsync();
        Assert.True(published.StatusCode is HttpStatusCode.OK,
            $"SEED POST /gateway/skills/{id}/publish failed: {(int)published.StatusCode} {publishedText}");
    }

    // ================================================================= helpers (per ContextLessDatabaseRouteTenancyTests)

    /// <summary>
    /// Assert STATUS and MEDIA TYPE first, and only then parse - parsing is itself an assertion about
    /// format, and the Gateway serves a single-page-application catch-all, so a parse attempted first
    /// would launder a wrong response into a crash that says nothing about isolation.
    /// </summary>
    private async Task<JsonElement> Json(HttpResponseMessage resp, HttpStatusCode expectedStatus, string label)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        _out.WriteLine($"{label} -> {(int)resp.StatusCode} {resp.StatusCode} [{mediaType}]");
        _out.WriteLine("    " + (body.Length > 600 ? body[..600] + " ...(truncated)" : body));

        Assert.True(expectedStatus == resp.StatusCode,
            $"{label}: expected HTTP {(int)expectedStatus} but got {(int)resp.StatusCode}; body was: {body}");
        Assert.True(mediaType == "application/json",
            $"{label}: expected media type application/json but got '{mediaType}'; body was: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Member(JsonElement el, string prop, JsonValueKind expected)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"expected a JSON object to read '{prop}' from, but the value was {el.ValueKind}");
        Assert.True(el.TryGetProperty(prop, out var v), $"the response has no '{prop}' property: {el}");
        Assert.True(v.ValueKind == expected, $"'{prop}' was {v.ValueKind}, expected {expected}: {el}");
        return v;
    }

    private static string Str(JsonElement el, string prop) => Member(el, prop, JsonValueKind.String).GetString()!;

    /// <summary>
    /// The "error" string a refusal body carries. A security row asserts this as well as the status so
    /// it proves WHICH refusal it got (inspection finding M03-I2-03) - a bare status can be produced by
    /// a validation path, a routing miss or a method mismatch just as easily as by the tenant lookup
    /// the row is named for.
    /// </summary>
    private static string ErrorOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return Str(doc.RootElement, "error");
    }

    private static int Int(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Number).GetInt32();

    private static JsonElement Obj(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Object);

    private static JsonElement Arr(JsonElement el, string prop) => Member(el, prop, JsonValueKind.Array);

    private static bool Bool(JsonElement el, string prop)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"expected a JSON object to read '{prop}' from, but the value was {el.ValueKind}");
        Assert.True(el.TryGetProperty(prop, out var v), $"the response has no '{prop}' property: {el}");
        Assert.True(v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"'{prop}' was {v.ValueKind}, expected a boolean: {el}");
        return v.GetBoolean();
    }

    // ==================================================================== workspaces (workspaces table)

    /// <summary>
    /// GET + DELETE /gateway/workspaces/{id} - both context-less (WorkspaceEndpoints.cs, handlers
    /// <c>(string id)</c>), scoped ONLY by the ambient request scope and the entity global query filter.
    ///
    /// THIS PROBE EXISTS BECAUSE THE CENSUS CLAIMED IT. The census comment says every family it lists was
    /// executed cross-tenant, and for workspaces that was a claim about a test nobody had written - the
    /// structural guard covers the MODEL (tenant_id, the query filter, the composite key), and a model
    /// guard is not a statement about what a route hands to a caller. A comment asserting an execution
    /// that did not happen is worse than no comment: it retires the question.
    ///
    /// The sharpest available shape, and the same one the mission-WHY probe uses: BOTH tenants store a
    /// workspace under the SAME slug, which the composite key (TenantId, Id) makes legal, each with a
    /// distinctive name. If the seam were missing the second write would find and overwrite the first
    /// tenant's row - so this catches a cross-tenant READ, a cross-tenant DELETE, and a cross-tenant
    /// OVERWRITE in one. The positive controls matter as much as the refusals: each tenant must be able
    /// to read and delete its OWN row, or a store that refused everybody would pass.
    /// </summary>
    [Fact]
    public async Task Workspaces_ContextLess_KeepEachTenantsWorkspaceUnderTheSameSlugSeparate()
    {
        const string slug = "morning-fleet";

        var bodyA = WorkspaceBody("A's morning fleet", @"D:\A\repo");
        var bodyB = WorkspaceBody("B's morning fleet", @"D:\B\repo");

        Assert.Equal(HttpStatusCode.OK, (await Send("PUT", $"gateway/workspaces/{slug}", _keyA, bodyA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("PUT", $"gateway/workspaces/{slug}", _keyB, bodyB)).StatusCode);

        // POSITIVE CONTROL: each tenant reads its OWN row under the shared slug, and gets its own.
        var readA = await Json(await Send("GET", $"gateway/workspaces/{slug}", _keyA, null),
            HttpStatusCode.OK, "A reads its own workspace under the shared slug");
        Assert.Equal("A's morning fleet", readA.GetProperty("name").GetString());

        var readB = await Json(await Send("GET", $"gateway/workspaces/{slug}", _keyB, null),
            HttpStatusCode.OK, "B reads its own workspace under the shared slug");
        Assert.Equal("B's morning fleet", readB.GetProperty("name").GetString());

        // B's write did not overwrite A's row, which is what a missing tenant qualifier on the store's
        // own key lookup would have done.
        Assert.Equal(@"D:\A\repo",
            readA.GetProperty("seats")[0].GetProperty("repoPath").GetString());

        // Each tenant's LIST is its own row and nothing else.
        var listA = await Json(await Send("GET", "gateway/workspaces", _keyA, null), HttpStatusCode.OK,
            "A lists its own workspaces");
        var rowsA = listA.GetProperty("workspaces").EnumerateArray().ToList();
        Assert.Equal("A's morning fleet", Assert.Single(rowsA).GetProperty("name").GetString());

        // A DELETE by the other tenant finds nothing and destroys nothing.
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send("DELETE", "gateway/workspaces/only-in-a", _keyB, null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Send("PUT", "gateway/workspaces/only-in-a", _keyA, bodyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send("GET", "gateway/workspaces/only-in-a", _keyB, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send("DELETE", "gateway/workspaces/only-in-a", _keyB, null)).StatusCode);

        // ...and it is still there for its owner afterwards, which is the half that proves the refusal
        // was a refusal and not a deletion that answered 404 on its way out.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("GET", "gateway/workspaces/only-in-a", _keyA, null)).StatusCode);

        // POSITIVE CONTROL: the owner CAN delete its own, so a store refusing everybody fails here.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("DELETE", "gateway/workspaces/only-in-a", _keyA, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send("GET", "gateway/workspaces/only-in-a", _keyA, null)).StatusCode);

        // The shared slug is untouched by all of that, for both of them.
        Assert.Equal(HttpStatusCode.OK, (await Send("GET", $"gateway/workspaces/{slug}", _keyA, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("GET", $"gateway/workspaces/{slug}", _keyB, null)).StatusCode);
    }

    private static string WorkspaceBody(string name, string repoPath) =>
        $$"""
        {
          "name": {{System.Text.Json.JsonSerializer.Serialize(name)}},
          "origin": "authored",
          "seats": [ { "name": "a seat", "agent": "ClaudeCode",
                       "repoPath": {{System.Text.Json.JsonSerializer.Serialize(repoPath)}} } ]
        }
        """;

    private Task<HttpResponseMessage> Send(string method, string path, string bearer, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    /// <summary>
    /// Send <paramref name="path"/> WITHOUT client-side path canonicalization, so a traversal-shaped
    /// request arrives at the server in the shape it was written.
    ///
    /// Inspection finding I1-04 exists because of this exact trap. A probe built as
    /// <c>gateway/skills/{id}/files/../../{name}</c> and handed to <see cref="HttpClient"/> never tests
    /// what it looks like it tests: <see cref="Uri"/> resolves the dot segments BEFORE the request goes
    /// out, so the server receives <c>/gateway/skills/{name}</c> and a DIFFERENT route answers. The
    /// probe then passed on the wrong route's reply, and the catch-all file leg it named was never
    /// executed at all. Percent-encoding the dots is not enough on its own either, because
    /// <see cref="Uri"/> unescapes and then normalizes them.
    ///
    /// <see cref="UriCreationOptions.DangerousDisablePathAndQueryCanonicalization"/> is the supported way
    /// to stop that, and it is exactly what a probe of a traversal defence needs: the escape must reach
    /// the handler under test, or the green means nothing.
    /// </summary>
    private Task<HttpResponseMessage> SendRaw(string method, string path, string bearer)
    {
        var absolute = new Uri(
            _http.BaseAddress!.ToString().TrimEnd('/') + "/" + path.TrimStart('/'),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        var req = new HttpRequestMessage(new HttpMethod(method), absolute);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _http.SendAsync(req);
    }

    // ============================================================ the rules family (session_rules, session_rule_firings)

    /// <summary>
    /// The THREE context-less Session Rules routes - <c>GET /gateway/rules/{id:guid}</c>,
    /// <c>DELETE /gateway/rules/{id:guid}</c> and <c>GET /gateway/rules/{id:guid}/firings</c> - executed
    /// against two accounts.
    ///
    /// They arrived on main in the Session Rules mission and were never ruled on, so the census went red
    /// (issue #2679). The verdict the census now carries is that they are confined by the model rather than
    /// by anything in the route: both tables are <c>TenantScopedEntity</c>, so
    /// <c>TenantScopeGuardTests</c> - which reflects the real EF model - proves each carries
    /// <c>tenant_id</c> and the deny-by-default global query filter. That is a strong argument and it is
    /// still an argument. This is the execution.
    ///
    /// The id is the sharpest possible handle here: a rule id is a Gateway-minted GUID, so the other
    /// account cannot guess one - but this probe HANDS it to them, which is the only way to ask whether
    /// anything but the filter was stopping them.
    ///
    /// Every refusal is judged twice, because a route that refuses everybody proves nothing about tenancy:
    /// an independent RE-READ by the owner after the other account's attempt, and a destructibility control
    /// in which the owner performs the SAME operation successfully.
    /// </summary>
    [Fact]
    public async Task RuleReadFamily_ContextLess_ServesTheOwnerTheExactSeededRule_AndIsANotFoundForTheOtherTenant()
    {
        var id = await CreateRule(_keyA, "when the screen says it is rate limited, wait");

        // OWNER CONTROL, on the exact fingerprint rather than a bare 200.
        var mine = await Json(await Send("GET", $"gateway/rules/{id}", _keyA, null),
            HttpStatusCode.OK, "owner reads its own rule");
        Assert.Equal("when the screen says it is rate limited, wait",
            mine.GetProperty("rule").GetProperty("instruction").GetString());

        // THE OTHER ACCOUNT, handed the id.
        var theirs = await Send("GET", $"gateway/rules/{id}", _keyB, null);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);

        // ...and it is not in their list either, which is the read that would leak without the filter.
        var listB = await Json(await Send("GET", "gateway/rules", _keyB, null),
            HttpStatusCode.OK, "the other account's own rule list");
        Assert.Empty(listB.GetProperty("rules").EnumerateArray());

        // INDEPENDENT RE-READ: the owner's rule is untouched by the attempt.
        var again = await Json(await Send("GET", $"gateway/rules/{id}", _keyA, null),
            HttpStatusCode.OK, "owner re-reads after the other account's attempt");
        Assert.Equal("when the screen says it is rate limited, wait",
            again.GetProperty("rule").GetProperty("instruction").GetString());
    }

    /// <summary>
    /// DELETE is the destructive half, and the one where a missing filter costs a row rather than a
    /// secret. The other account is handed the id and asked to delete it; the owner's rule must survive,
    /// and the owner must then be able to delete it themselves - so the refusal is a refusal and not an
    /// inert route.
    /// </summary>
    [Fact]
    public async Task RuleDelete_ContextLess_CannotRemoveTheOtherTenantsRule_AndTheOwnerStillCan()
    {
        var id = await CreateRule(_keyA, "a rule the other account will try to delete");

        // The other account's delete must not remove it. The route answers with what it deleted, and the
        // honest answer for a rule it cannot see is "nothing".
        var attempt = await Json(await Send("DELETE", $"gateway/rules/{id}", _keyB, null),
            HttpStatusCode.OK, "the other account attempts the delete");
        Assert.False(attempt.GetProperty("deleted").GetBoolean());

        // INDEPENDENT RE-READ: still there, and still the owner's.
        var survived = await Json(await Send("GET", $"gateway/rules/{id}", _keyA, null),
            HttpStatusCode.OK, "owner re-reads after the other account's delete");
        Assert.Equal("a rule the other account will try to delete",
            survived.GetProperty("rule").GetProperty("instruction").GetString());

        // DESTRUCTIBILITY CONTROL: the owner CAN delete it, so the refusal above was about tenancy and not
        // about a route that refuses everyone.
        var owned = await Json(await Send("DELETE", $"gateway/rules/{id}", _keyA, null),
            HttpStatusCode.OK, "owner deletes its own rule");
        Assert.True(owned.GetProperty("deleted").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send("GET", $"gateway/rules/{id}", _keyA, null)).StatusCode);
    }

    /// <summary>
    /// THE FIRING RECORD, which is the product - and the row that cannot be probed honestly without
    /// seeding, because no route creates a firing. Two empty lists compared against each other pass with
    /// or without the filter, so the owner's firing is seeded through the store (inside its own tenant
    /// scope) and the owner's read asserts THAT fingerprint. Only then does the other account's empty list
    /// carry any weight.
    /// </summary>
    [Fact]
    public async Task RuleFirings_ContextLess_ServeOnlyTheOwningTenants_Record()
    {
        var id = await CreateRule(_keyA, "a rule whose record the other account will ask for");
        _gateway.SeedRuleFiringForTest(_tenantA, Guid.Parse(id), "sess-a", "the owner's own firing", DateTime.UtcNow);

        // OWNER CONTROL on the exact seeded fingerprint.
        var mine = await Json(await Send("GET", $"gateway/rules/{id}/firings", _keyA, null),
            HttpStatusCode.OK, "owner reads its own firing record");
        var firings = mine.GetProperty("firings").EnumerateArray().ToList();
        Assert.Single(firings);
        Assert.Equal("the owner's own firing", firings[0].GetProperty("reason").GetString());

        // THE OTHER ACCOUNT, handed the same rule id: an empty record, not the owner's.
        var theirs = await Json(await Send("GET", $"gateway/rules/{id}/firings", _keyB, null),
            HttpStatusCode.OK, "the other account asks for that rule's record");
        Assert.Empty(theirs.GetProperty("firings").EnumerateArray());

        // INDEPENDENT RE-READ: the owner's record is intact.
        var again = await Json(await Send("GET", $"gateway/rules/{id}/firings", _keyA, null),
            HttpStatusCode.OK, "owner re-reads its record");
        Assert.Single(again.GetProperty("firings").EnumerateArray());
    }

    /// <summary>Create a rule for one account over the real route, and hand back its id. Every field the
    /// write gate insists on is supplied, so a refusal here is a real defect rather than a malformed
    /// fixture - and the refusal reason is surfaced, because the store returns it verbatim.</summary>
    private async Task<string> CreateRule(string key, string instruction)
    {
        var body = JsonSerializer.Serialize(new
        {
            instruction,
            screenDescription = "the screen says it is rate limited",
            triggerWords = new[] { "rate limited" },
            checks = new object[]
            {
                new
                {
                    name = "matches_any",
                    arguments = new Dictionary<string, object>
                    {
                        ["text"] = "the screen says it is rate limited",
                        ["terms"] = new[] { "rate limited" },
                    },
                },
            },
            scope = "all-sessions",
            cooldownSeconds = 60,
            dailyCap = 5,
        });
        var created = await Json(await Send("POST", "gateway/rules", key, body),
            HttpStatusCode.OK, "create a rule");
        return created.GetProperty("rule").GetProperty("id").GetString()!;
    }
}
