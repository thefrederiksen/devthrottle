using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Communications.Services;
using CcDirector.Gateway.Api;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// CR-6, comm-queue half: the /comm-queue surface must be REFUSED IN WHOLE on the hosted Gateway.
///
/// The store behind the route is ONE process-global SQLite file (config/comm-queue/communications.db)
/// with no tenant anywhere - not in the file, not in the store, not in the route - and the route sat
/// behind only the host-wide authentication gate, which admits ANY enrolled device key from ANY
/// account. So on shared hosted infrastructure every subscriber could read the operator's outbound
/// communications queue: draft emails, posts, recipients, personas. The Communication Manager app the
/// surface backed was retired 2026-07-06 and the comm queue is not part of the hosted launch, so the
/// family is refused on hosted rather than partitioned, through the shared refusal primitive
/// (<see cref="Gateway.Tenancy.HostedRouteDeny.ExclusiveGroup"/>) - the same boundary /vault/keys and
/// /shutdown adopted. On hosted the handler is NEVER MAPPED; one verb-less catch-all refusal claims
/// everything under /comm-queue, so every request shape - including a body-bound POST, a verb the
/// family never mapped, and a route added under the prefix later - meets the refusal.
///
/// This is the SOURCE-side half of the Wave-2 hostile two-tenant matrix's comm-queue row: the row
/// turns FAIL to PASS because an enrolled tenant's read is REFUSED, not because the route vanished -
/// the refusal is asserted as an exact payload, and the self-host control
/// (<see cref="SelfHostCommQueueControlTests"/>) proves the same route still serves the real queue
/// off hosted.
///
/// A REAL DRAFT IS SEEDED FIRST. A deny tested against an empty queue proves nothing - the refused
/// read here would otherwise have had nothing to disclose. The seed goes through the production
/// schema (<see cref="DatabaseService.InitializeAsync"/>) plus one raw insert, because the write API
/// died with the desktop app.
///
/// REVERT-PROOF - the recipe to RUN, not to describe. In
/// <c>src/CcDirector.Gateway/Api/CommQueueEndpoints.cs</c> change <c>Map</c> to map the route on the
/// ungrouped builder (<c>outer.MapGet("/comm-queue", ...)</c>) instead of through
/// <c>HostedRouteDeny.ExclusiveGroup</c>. Rebuild, confirm zero errors, run this class: every hosted
/// test reddens with the symptom - the enrolled tenant SERVED the operator's queue (200 with the
/// seeded draft) instead of the refusal - while <see cref="SelfHostCommQueueControlTests"/> stays
/// green. Restore, rebuild, rerun: all green.
///
/// Sets CC_DIRECTOR_ROOT (the storage root the handler resolves the SQLite path through), so it joins the
/// DirectorRoot collection to serialize with every other test that redirects the root.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedCommQueueDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hosted-commq-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;

    public HostedCommQueueDenyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-commq-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // The operator's queue holds a real pending draft, so a read that (wrongly) got through would
        // have something to hand back.
        await CommQueueTestSeed.SeedAsync(_root);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, entitled, tenant-bound device key - the strongest caller hosted has. The
        // point is that even this one is refused: no credential reads the operator's queue on hosted.
        _key = HostedTestEnrollment.Enroll(_gateway, "sub-commq-a", "a@example.com", "dev-cq-a", "MCQA").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every request shape in one theory: the production route in both its query forms, a body-bound
    /// POST (the shape the primitive's own record says a deny-family probe MUST include, because it is
    /// precisely the shape a request-time filter cannot answer uniformly), a verb-and-path the family
    /// never mapped, and a path under the prefix that no route has ever served (the future-route
    /// property of the exclusive catch-all).
    /// </summary>
    [Theory]
    [InlineData("GET", "comm-queue", null)]                                  // the production read
    [InlineData("GET", "comm-queue?status=all", null)]                        // every-status form of the read
    [InlineData("POST", "comm-queue", "{\"status\":\"all\"}")]                // body-bound POST, never mapped
    [InlineData("DELETE", "comm-queue/42", null)]                             // verb and path never mapped
    [InlineData("GET", "comm-queue/added/later", null)]                       // a route nobody has written yet
    public async Task Every_comm_queue_request_shape_is_refused_to_an_enrolled_tenant(
        string method, string path, string? body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_read_did_not_leak_the_seeded_draft_anywhere_in_the_body()
    {
        var resp = await Send(HttpMethod.Get, "comm-queue");

        // The refusal is asserted FIRST; on its own, "the body does not contain the draft" is an
        // absence-only claim that any body which simply is not the queue would satisfy. Pinning the
        // exact refusal makes this a statement about the guard, not about the string.
        await AssertBodyIsNothingButTheRefusal(resp);
        Assert.DoesNotContain(CommQueueTestSeed.SecretDraftContent, await resp.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the family up as a side effect of mapping before the
        // host-wide authentication gate. GREEN in both directions of the revert on purpose - a control
        // that moves with the change under test is not a control.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("comm-queue")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and format facts before parsing - same reasoning as
    /// <see cref="HostedVaultDenyTests.AssertBodyIsNothingButTheRefusal"/>: the property set is
    /// EXACTLY one error field, so any queue payload key (status, count, stats, items) and anything
    /// added to the projection later reddens automatically.
    /// </summary>
    internal static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(CommQueueEndpoints.RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }
}

/// <summary>
/// Seeds the operator's comm queue at the redirected storage root with ONE pending draft, through the
/// production schema (<see cref="DatabaseService.InitializeAsync"/>) plus a raw insert - the desktop
/// Communication Manager that owned the write path was retired, so there is no write API to seed
/// through. The columns supplied are exactly the NOT NULL set plus the fields the Gateway's slim
/// projection surfaces (ticket number, status, recipient, content).
/// </summary>
internal static class CommQueueTestSeed
{
    /// <summary>Distinctive enough that finding it in ANY response body is a disclosure, not a coincidence.</summary>
    public const string SecretDraftContent =
        "SECRET outbound draft b7c2: the acquisition closes Friday, hold all announcements";

    public static async Task SeedAsync(string root)
    {
        var contentPath = Path.Combine(root, "config", "comm-queue");
        using (var db = new DatabaseService(contentPath))
            await db.InitializeAsync();

        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(contentPath, "communications.db")}");
        await connection.OpenAsync();
        var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO communications (id, ticket_number, platform, type, persona, content, created_at, status, recipient) " +
            "VALUES ($id, 42, 'email', 'email', 'operator', $content, $createdAt, 'pending_review', 'board@example.com')";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$content", SecretDraftContent);
        insert.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        await insert.ExecuteNonQueryAsync();
    }
}
