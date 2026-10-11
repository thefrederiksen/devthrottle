using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// VERIFICATION, BY EXECUTION, of a claim about the hosted Gateway's context-less routes.
///
/// THE CLAIM UNDER TEST. A census found sixty-six routes that take a path parameter but no
/// <c>HttpContext</c>, and INFERRED that the database-backed ones must hit the deny-by-default throw in
/// <c>AsyncLocalTenantContext.Current</c> - broken, but not leaking - because a handler with no
/// <c>HttpContext</c> cannot resolve the caller's tenant. That inference had never been executed.
///
/// WHAT THIS FILE DOES. It seeds real rows for TWO tenants over real HTTP through the real auth middleware
/// on a real hosted <see cref="GatewayHost"/>, then has tenant A name tenant B's object id on context-less
/// database-backed routes, and records exactly what comes back.
///
/// SCOPE - READ THIS BEFORE QUOTING THE RESULT. This file CROSS-PROBES EIGHT of the sixty-six context-less
/// routes, across four stores: two work-list operations, two cron operations, one session-spend read, and
/// three workflow operations. A ninth context-less route, POST /gateway/workflows/{id}/publish, is exercised
/// on the OWNER path only, as a seed, and is NOT cross-probed - it is not part of the claim.
///
/// It does NOT retire the other fifty-eight by inference; that was the census's error and repeating it here
/// would be the same mistake in the opposite direction. The full code-derived inventory, and exactly what
/// remains unproven, is stated in the pull request. Eight samples cannot stand for sixty-six.
///
/// HOW A POSITIVE CONTROL IS BUILT HERE. Asserting a status code does not prove the intended handler
/// returned the seeded row - an unrelated 200, a catch-all, or a failed seed answering empty would all pass.
/// So every owner control asserts the exact SEEDED FINGERPRINT (the values this test itself wrote), and
/// every write or delete is judged by an independent RE-READ of the resulting state, never by the absence of
/// a substring. Status and media type are asserted BEFORE any parse, because parsing is itself an assertion
/// about format: parse-first would turn a wrong response into a parser crash, which proves nothing.
///
/// WHAT EXECUTION FOUND. Nothing threw and nothing leaked. Every route answered its owner with the exact
/// seeded row and answered the other tenant with an ordinary not-found; tenant B's rows survived tenant A's
/// delete attempts byte-for-byte, and tenant B could then perform those same deletes itself - so the refusals
/// are refusals, not inert operations. The claim's conclusion about the database half is wrong in BOTH
/// halves: these routes are neither broken nor leaking.
///
/// THE MECHANISM, AND WHY IT IS PROVEN ELSEWHERE. The tenant scope is not entered by the handler, so a
/// handler's signature cannot decide whether one exists. It is entered by a host-wide middleware registered
/// before routing that binds the AUTHENTICATED device key's tenant for the whole pipeline
/// (<c>GatewayHost.cs</c>, the device-key HTTP boundary). A context-less handler therefore runs inside the
/// caller's scope, and the entity global query filter answers about the caller's tenant only.
///
/// Those are TWO SEPARATE production mechanisms, and an end-to-end test like this one cannot tell them
/// apart - it would pass if either did all the work. They are therefore isolated from each other, without
/// HTTP and without the middleware, in <see cref="ContextLessRouteTenancyMechanismProofTests"/>, which is
/// the companion to this file and is where the causal claims are actually earned.
/// </summary>
public sealed class ContextLessDatabaseRouteTenancyTests : IAsyncLifetime
{
    private const string SharedToken = "test-token";
    private string TenantA { get; set; } = "";
    private string TenantB { get; set; } = "";

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ctxless-" + Guid.NewGuid().ToString("N"));

    public ContextLessDatabaseRouteTenancyTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            cronJobsPath: Path.Combine(_instancesDir, "cron", "cronjobs.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions", "missions.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        var deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        TenantA = deviceA.Tenant.Value;
        TenantB = deviceB.Tenant.Value;
        _keyA = deviceA.DeviceKey;
        _keyB = deviceB.DeviceKey;

        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ================================================================= work lists (worklists table)

    /// <summary>
    /// GET /lists/{name} - context-less (WorkListEndpoints.cs:56, handler <c>(string name)</c>).
    /// The owner control asserts the exact seeded list AND its exact seeded item, so an empty or unrelated
    /// 200 cannot pass for a served row.
    /// </summary>
    [Fact]
    public async Task WorkListGetByName_ServesTheOwnerTheExactSeededList_AndIsAPlainNotFoundForTheOtherTenant()
    {
        const string list = "bobs-backlog";

        var seed = await Json(await Send("POST", "lists", _keyB, "{\"name\":\"" + list + "\"}"),
            HttpStatusCode.OK, "SEED    POST /lists (tenant B)");
        Assert.Equal(list, Str(seed, "name"));

        var seedItem = await Json(
            await Send("POST", $"lists/{list}/items", _keyB,
                "{\"source\":\"github\",\"id\":\"77\",\"area\":\"alpha\"}"),
            HttpStatusCode.OK, "SEED    POST /lists/{name}/items (tenant B)");
        Assert.Equal(list, Str(seedItem, "name"));

        // CONTROL: the exact seeded fingerprint, not merely a 200.
        var control = await Json(await Send("GET", $"lists/{list}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /lists/{name} (owner B)");
        AssertIsBobsBacklogWithTheOneSeededItem(control, list);

        // CROSS: tenant A naming tenant B's list.
        var cross = await Json(await Send("GET", $"lists/{list}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   GET /lists/{name} (tenant A naming B's list)");
        Assert.Equal("no such list", Str(cross, "error"));
        Assert.Equal(list, Str(cross, "name"));
    }

    /// <summary>
    /// DELETE /lists/{name}/items/{source}/{id} - context-less (WorkListEndpoints.cs:110, handler
    /// <c>(string name, string source, string id)</c>). A destructive context-less route.
    ///
    /// The refusal is judged by an independent RE-READ of tenant B's exact list state, never by the absence
    /// of a substring in tenant A's response - absence can pass on an empty or wrong body. And the owner then
    /// performs the SAME delete successfully, which is what makes tenant A's 404 a refusal rather than an
    /// operation that was inert for everyone.
    /// </summary>
    [Fact]
    public async Task WorkListDeleteItem_ContextLess_LeavesTheOtherTenantsListExactlyAsItWas_AndTheOwnerCanStillDeleteIt()
    {
        const string list = "bobs-delete-target";

        Assert.Equal(list, Str(await Json(await Send("POST", "lists", _keyB, "{\"name\":\"" + list + "\"}"),
            HttpStatusCode.OK, "SEED    POST /lists (tenant B)"), "name"));
        await Json(await Send("POST", $"lists/{list}/items", _keyB,
                "{\"source\":\"github\",\"id\":\"77\",\"area\":\"alpha\"}"),
            HttpStatusCode.OK, "SEED    POST /lists/{name}/items (tenant B)");

        // CROSS: tenant A tries to delete tenant B's item by name, source and id.
        var cross = await Json(await Send("DELETE", $"lists/{list}/items/github/77", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   DELETE /lists/{name}/items/{source}/{id} (tenant A)");
        Assert.Equal("no such list", Str(cross, "error"));

        // INDEPENDENT RE-READ: B's list must still hold exactly the seeded item, unchanged.
        var after = await Json(await Send("GET", $"lists/{list}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /lists/{name} (owner B, after A's delete attempt)");
        AssertIsBobsBacklogWithTheOneSeededItem(after, list);

        // CONTROL: the owner performs the SAME operation - so the refusal above is a refusal, not inertia.
        var ownerDelete = await Json(await Send("DELETE", $"lists/{list}/items/github/77", _keyB, null),
            HttpStatusCode.OK, "CONTROL DELETE /lists/{name}/items/{source}/{id} (owner B)");
        Assert.Equal(list, Str(ownerDelete, "name"));
        Assert.Equal("github", Str(ownerDelete, "source"));
        Assert.Equal("77", Str(ownerDelete, "id"));
        Assert.True(Bool(ownerDelete, "removed"), "the owner's own delete reported nothing removed");

        // And the effect of the owner's delete is independently re-read.
        var emptied = await Json(await Send("GET", $"lists/{list}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /lists/{name} (owner B, after its OWN delete)");
        Assert.Equal(list, Str(emptied, "name"));
        Assert.Empty(Arr(emptied, "items").EnumerateArray());
    }

    private static void AssertIsBobsBacklogWithTheOneSeededItem(JsonElement list, string expectedName)
    {
        Assert.Equal(expectedName, Str(list, "name"));
        var items = Arr(list, "items");
        Assert.Equal(1, items.GetArrayLength());
        var only = items[0];
        Assert.Equal("github", Str(only, "source"));
        Assert.Equal("77", Str(only, "id"));
        Assert.Equal("alpha", Str(only, "area"));
    }

    // ================================================================= cron jobs (cron_jobs table)

    /// <summary>
    /// GET /cron/jobs/{id} (CronJobEndpoints.cs:56) and DELETE /cron/jobs/{id} (CronJobEndpoints.cs:91) -
    /// both context-less, handlers <c>(string id)</c>.
    /// </summary>
    [Fact]
    public async Task CronJobGetAndDelete_ContextLess_ServeTheOwnerTheExactSeededJob_AndTheOtherTenantCanNeitherReadNorDeleteIt()
    {
        var seed = await Json(await Send("POST", "cron/jobs", _keyB, CronBody("bobs-nightly")),
            HttpStatusCode.Created, "SEED    POST /cron/jobs (tenant B)");
        var jobId = Str(seed, "id");
        Assert.NotEmpty(jobId);

        var control = await Json(await Send("GET", $"cron/jobs/{jobId}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /cron/jobs/{id} (owner B)");
        AssertIsBobsNightly(control, jobId);

        var cross = await Json(await Send("GET", $"cron/jobs/{jobId}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   GET /cron/jobs/{id} (tenant A naming B's job)");
        Assert.Equal("no such cron job", Str(cross, "error"));
        Assert.Equal(jobId, Str(cross, "id"));

        var crossDelete = await Json(await Send("DELETE", $"cron/jobs/{jobId}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   DELETE /cron/jobs/{id} (tenant A deleting B's job)");
        Assert.Equal("no such cron job", Str(crossDelete, "error"));

        // INDEPENDENT RE-READ: the job is still there and still byte-for-byte the seeded job.
        var survives = await Json(await Send("GET", $"cron/jobs/{jobId}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /cron/jobs/{id} (owner B, after A's delete attempt)");
        AssertIsBobsNightly(survives, jobId);

        // CONTROL: the owner performs the same delete, so A's 404 is a refusal and not an inert route.
        var ownerDelete = await Json(await Send("DELETE", $"cron/jobs/{jobId}", _keyB, null),
            HttpStatusCode.OK, "CONTROL DELETE /cron/jobs/{id} (owner B)");
        Assert.Equal(jobId, Str(ownerDelete, "id"));
        Assert.True(Bool(ownerDelete, "deleted"), "the owner's own delete reported nothing deleted");

        var gone = await Json(await Send("GET", $"cron/jobs/{jobId}", _keyB, null),
            HttpStatusCode.NotFound, "AFTER   GET /cron/jobs/{id} (owner B, after its OWN delete)");
        Assert.Equal("no such cron job", Str(gone, "error"));
    }

    private static void AssertIsBobsNightly(JsonElement job, string expectedId)
    {
        Assert.Equal(expectedId, Str(job, "id"));
        Assert.Equal("bobs-nightly", Str(job, "name"));
        Assert.Equal("recurring", Str(job, "scheduleKind"));
        Assert.Equal("0 0 * * *", Str(job, "cronExpression"));
        Assert.Equal("America/Chicago", Str(job, "timeZoneId"));
        Assert.Equal("MB", Str(Obj(job, "target"), "machine"));
        Assert.Equal("/help", Str(Obj(job, "action"), "seed"));
    }

    // ================================================================= session spend (session_spend table)

    /// <summary>
    /// GET /gateway/governance/session-spend/{sessionId} - context-less
    /// (GovernanceSpendEndpoints.cs:62, handler <c>(string sessionId)</c>). The owner control asserts the
    /// exact seeded token counts, so a zeroed or unrelated record cannot pass for the seeded one.
    /// </summary>
    [Fact]
    public async Task SessionSpendGetBySessionId_ContextLess_ServesTheOwnerTheExactSeededTokens_AndIsANotFoundForTheOtherTenant()
    {
        const string sid = "bobs-session-0001";

        var seed = await Json(await Send("POST", "gateway/governance/session-spend", _keyB,
                "{\"sessionId\":\"" + sid + "\",\"agentKind\":\"claude\",\"billingMode\":\"subscription-included\"," +
                "\"tokensCaptured\":true,\"inputTokens\":111,\"outputTokens\":222}"),
            HttpStatusCode.OK, "SEED    POST /gateway/governance/session-spend (tenant B)");
        Assert.Equal(sid, Str(seed, "sessionId"));

        var control = await Json(await Send("GET", $"gateway/governance/session-spend/{sid}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/governance/session-spend/{sessionId} (owner B)");
        Assert.Equal(sid, Str(control, "sessionId"));
        Assert.Equal("claude", Str(control, "agentKind"));
        Assert.Equal("subscription-included", Str(control, "billingMode"));
        Assert.True(Bool(control, "tokensCaptured"), "the seeded record did not come back with tokensCaptured");
        Assert.Equal(111, Int(control, "inputTokens"));
        Assert.Equal(222, Int(control, "outputTokens"));

        var cross = await Json(await Send("GET", $"gateway/governance/session-spend/{sid}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   GET /gateway/governance/session-spend/{sessionId} (tenant A)");
        Assert.Contains(sid, Str(cross, "error"), StringComparison.Ordinal);
    }

    // ================================================================= workflows (workflows table)

    /// <summary>
    /// GET /gateway/workflows/{id} (WorkflowEndpoints.cs:57), GET /gateway/workflows/{id}/versions
    /// (WorkflowEndpoints.cs:131), DELETE /gateway/workflows/{id} (WorkflowEndpoints.cs:123) and
    /// POST /gateway/workflows/{id}/publish (WorkflowEndpoints.cs:105) - all context-less.
    /// </summary>
    [Fact]
    public async Task WorkflowReadVersionsAndArchive_ContextLess_ServeTheOwnerTheExactSeededWorkflow_AndTheOtherTenantCanNeitherReadNorArchiveIt()
    {
        const string wid = "bobs-flow";

        var draft = await Json(await Send("POST", "gateway/workflows", _keyB, WorkflowBody(wid)),
            HttpStatusCode.Created, "SEED    POST /gateway/workflows (tenant B)");
        Assert.Equal(wid, Str(draft, "workflowId"));
        Assert.Equal(1, Int(draft, "version"));

        var published = await Json(await Send("POST", $"gateway/workflows/{wid}/publish", _keyB, "{}"),
            HttpStatusCode.OK, "SEED    POST /gateway/workflows/{id}/publish (tenant B, context-less)");
        Assert.Equal(wid, Str(published, "id"));
        Assert.Equal(1, Int(published, "version"));

        var control = await Json(await Send("GET", $"gateway/workflows/{wid}", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/workflows/{id} (owner B)");
        AssertIsBobsFlow(control, wid);

        var controlVersions = await Json(await Send("GET", $"gateway/workflows/{wid}/versions", _keyB, null),
            HttpStatusCode.OK, "CONTROL GET /gateway/workflows/{id}/versions (owner B)");
        var versions = Arr(controlVersions, "versions");
        Assert.Equal(1, versions.GetArrayLength());
        Assert.Equal(1, Int(versions[0], "version"));
        Assert.Equal("test-session", Str(versions[0], "authoredBy"));

        var cross = await Json(await Send("GET", $"gateway/workflows/{wid}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   GET /gateway/workflows/{id} (tenant A)");
        Assert.Contains(wid, Str(cross, "error"), StringComparison.Ordinal);

        var crossVersions = await Json(await Send("GET", $"gateway/workflows/{wid}/versions", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   GET /gateway/workflows/{id}/versions (tenant A)");
        Assert.Contains(wid, Str(crossVersions, "error"), StringComparison.Ordinal);

        var crossDelete = await Json(await Send("DELETE", $"gateway/workflows/{wid}", _keyA, null),
            HttpStatusCode.NotFound, "CROSS   DELETE /gateway/workflows/{id} (tenant A archiving B's workflow)");
        Assert.Contains(wid, Str(crossDelete, "error"), StringComparison.Ordinal);

        // INDEPENDENT RE-READ: still there, still exactly the seeded workflow.
        var stillThere = await Json(await Send("GET", $"gateway/workflows/{wid}", _keyB, null),
            HttpStatusCode.OK, "AFTER   GET /gateway/workflows/{id} (owner B, after A's archive attempt)");
        AssertIsBobsFlow(stillThere, wid);

        // CONTROL: the owner archives it itself - the refusal above was a refusal, not an inert route.
        var ownerArchive = await Json(await Send("DELETE", $"gateway/workflows/{wid}", _keyB, null),
            HttpStatusCode.OK, "CONTROL DELETE /gateway/workflows/{id} (owner B)");
        Assert.Equal(wid, Str(ownerArchive, "id"));
        Assert.True(Bool(ownerArchive, "archived"), "the owner's own archive reported nothing archived");

        var goneForOwner = await Json(await Send("GET", $"gateway/workflows/{wid}", _keyB, null),
            HttpStatusCode.NotFound, "AFTER   GET /gateway/workflows/{id} (owner B, after its OWN archive)");
        Assert.Contains(wid, Str(goneForOwner, "error"), StringComparison.Ordinal);
    }

    private static void AssertIsBobsFlow(JsonElement workflow, string expectedId)
    {
        Assert.Equal(expectedId, Str(workflow, "id"));
        Assert.Equal("Bobs flow", Str(workflow, "name"));
        Assert.Equal("A workflow owned by tenant B.", Str(workflow, "summary"));
        Assert.Equal("Never - it exists only to be named by the wrong tenant.", Str(workflow, "whenToUse"));
        Assert.Equal(1, Int(workflow, "version"));
        var steps = Arr(workflow, "steps");
        Assert.Equal(1, steps.GetArrayLength());
        Assert.Equal("Step", Str(steps[0], "name"));
        Assert.Equal("Do the thing.", Str(steps[0], "description"));
    }

    // ================================================= the credential is the only variable

    /// <summary>
    /// THE CREDENTIAL PROBE, on ONE route, with nothing else varied.
    ///
    /// The claim's premise was that the HANDLER's signature decides whether a tenant is in scope. It does
    /// not - the CREDENTIAL does. This holds the route, the store and the payload fixed and varies only the
    /// bearer token:
    ///
    ///   device key BOUND to a tenant   -> 200, a served list  (a tenant resolved, the middleware entered its scope)
    ///   active device row with NO tenant -> 401                (invalid hosted binding, rejected by authentication)
    ///   shared machine token           -> 401                  (on hosted it is not a credential at all)
    ///   garbage                        -> 401                  (never authenticated at all)
    ///
    /// The "no tenant" arm proves row status alone is insufficient: an active device row without a canonical
    /// tenant/account mapping is an invalid hosted credential. Authentication rejects it before any request
    /// scope or database route can be reached, and the typed revocation body is asserted exactly.
    ///
    /// The shared machine token USED to occupy that arm - it authenticated but carried no device, so it also
    /// reached the 500. Production-readiness MH-2 (commit 76e7b49e) closed that on hosted: the shared token no
    /// longer authenticates at all on a multi-tenant Gateway, because a credential with no device has no
    /// tenant and would reach every tenant-blind route unscoped. So on hosted it is now a 401 - refused at the
    /// door, exactly as an unknown credential is - and that rejection is asserted here to keep this route
    /// honest about the current invariant. (Self-host still accepts the shared token; this file runs hosted,
    /// and <see cref="AuthMiddlewareTests"/> pins both halves without HTTP.)
    ///
    /// The separate scope and database-filter mechanisms remain pinned without HTTP in
    /// <see cref="ContextLessRouteTenancyMechanismProofTests"/>.
    /// </summary>
    [Fact]
    public async Task OnOneRoute_TheCredentialAloneDecidesWhetherATenantScopeExists()
    {
        // A device key BOUND to a tenant: a scope is resolved and entered, so the route reaches the store.
        var withBoundDeviceKey = await Json(await Send("GET", "cron/jobs", _keyB, null),
            HttpStatusCode.OK, "BOUND DEVICE KEY   GET /cron/jobs (a scope IS entered)");
        Assert.Equal(JsonValueKind.Array, Arr(withBoundDeviceKey, "jobs").ValueKind);

        // A device row that is active but bound to NO canonical tenant. Hosted authentication treats that
        // inconsistent binding as revoked, so it cannot enter any scope or reach the context-less handler.
        var unboundKey = _gateway.Devices.Register("dev-unbound", "MC").DeviceKey; // registered, never account-bound
        var withUnboundKey = await Send("GET", "cron/jobs", unboundKey, null);
        var unboundBody = await withUnboundKey.Content.ReadAsStringAsync();
        _out.WriteLine($"UNBOUND DEVICE KEY GET /cron/jobs -> {(int)withUnboundKey.StatusCode} " +
                       $"[{withUnboundKey.Content.Headers.ContentType?.MediaType}] {unboundBody}");
        Assert.Equal(HttpStatusCode.Unauthorized, withUnboundKey.StatusCode);
        Assert.Equal("application/json", withUnboundKey.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}", unboundBody);

        // The shared machine token: on hosted (MH-2) it is not a credential at all, so it never reaches the
        // database - it is a 401, exactly as an unknown credential is.
        var withSharedToken = await Send("GET", "cron/jobs", SharedToken, null);
        _out.WriteLine($"SHARED TOKEN       GET /cron/jobs -> {(int)withSharedToken.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, withSharedToken.StatusCode);

        // A credential that never authenticated at all.
        var withGarbage = await Send("GET", "cron/jobs", "not-a-real-credential", null);
        _out.WriteLine($"GARBAGE            GET /cron/jobs -> {(int)withGarbage.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, withGarbage.StatusCode);
    }

    /// <summary>
    /// MECHANISM 1's RESOLUTION STEP, PROBED DIRECTLY. The middleware asks the tenant boundary what tenant an
    /// authenticated device key belongs to, and enters that tenant's scope. This asks the boundary the same
    /// question the middleware asks, on the same running Gateway, and pins the exact answers:
    ///
    ///   device key A -> tenant-alice        device key B -> tenant-bob        shared token -> nothing
    ///
    /// The two keys resolving to DIFFERENT tenants is the fact the cross-tenant tests above depend on, and
    /// the shared token resolving to NOTHING is why the credential probe gets a 500 rather than a served row.
    /// Both are asserted as exact values, not as a difference.
    ///
    /// The remaining half of mechanism 1 - that the middleware actually ENTERS the scope it resolved - is not
    /// provable by inspection from out here, and is established causally by mutating the resolution in
    /// production and watching the cross-tenant tests in this file turn red. See the pull request.
    /// </summary>
    [Fact]
    public void TheAuthBoundary_ResolvesEachDeviceKeyToItsOwnTenant_AndResolvesNothingForANonDeviceKey()
    {
        var forA = _gateway.TenantBoundary.ResolveForDeviceKey(_keyA);
        var forB = _gateway.TenantBoundary.ResolveForDeviceKey(_keyB);
        var forSharedToken = _gateway.TenantBoundary.ResolveForDeviceKey(SharedToken);

        _out.WriteLine($"device key A -> {forA?.Value ?? "(nothing)"}");
        _out.WriteLine($"device key B -> {forB?.Value ?? "(nothing)"}");
        _out.WriteLine($"shared token -> {forSharedToken?.Value ?? "(nothing)"}");

        Assert.Equal(TenantA, forA?.Value);
        Assert.Equal(TenantB, forB?.Value);
        Assert.Null(forSharedToken);
    }

    // ================================================================= helpers

    private static string CronBody(string name) => JsonSerializer.Serialize(new
    {
        name,
        scheduleKind = "recurring",
        cronExpression = "0 0 * * *",
        timeZoneId = "America/Chicago",
        target = new { machine = "MB" },
        action = new { repoPath = @"D:\repo", seed = "/help" },
    });

    private static string WorkflowBody(string id) => JsonSerializer.Serialize(new
    {
        id,
        name = "Bobs flow",
        summary = "A workflow owned by tenant B.",
        whenToUse = "Never - it exists only to be named by the wrong tenant.",
        humanCheckpoint = "None.",
        steps = new[] { new { name = "Step", description = "Do the thing.", doer = "Worker", done = "It is done." } },
        instructionsMarkdown = "# Bobs flow\n\nDo the thing.",
        authoredBy = "test-session",
    });

    /// <summary>
    /// Assert STATUS and MEDIA TYPE first, and only then parse.
    ///
    /// Parsing is itself an assertion about format. The Gateway serves a single-page-application catch-all
    /// and a generic 500 body, so a route that answered wrongly could hand back HTML or nothing at all -
    /// and a parse attempted first would throw <see cref="JsonException"/>, laundering a wrong response into
    /// a crash that says nothing about isolation. Asserting the format first means a wrong response fails as
    /// an assertion this test raised, which is the only kind of red that counts.
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

    // Typed accessors. Every one of these is an ASSERTION, so a missing or wrongly-typed property fails as a
    // red this test raised rather than as a KeyNotFoundException or an InvalidOperationException from the
    // JSON reader - a crash would be unclassifiable evidence.

    private static JsonElement Member(JsonElement el, string prop, JsonValueKind expected)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"expected a JSON object to read '{prop}' from, but the value was {el.ValueKind}");
        Assert.True(el.TryGetProperty(prop, out var v), $"the response has no '{prop}' property: {el}");
        Assert.True(v.ValueKind == expected,
            $"'{prop}' was {v.ValueKind}, expected {expected}: {el}");
        return v;
    }

    private static string Str(JsonElement el, string prop) => Member(el, prop, JsonValueKind.String).GetString()!;

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

    private Task<HttpResponseMessage> Send(string method, string path, string bearer, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

}
