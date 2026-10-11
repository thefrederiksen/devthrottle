using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Activity;

/// <summary>
/// The activity ledger is TENANT-SCOPED on the hosted Gateway, proven over real HTTP with two accounts -
/// the hostile two-tenant proof the trustworthy-Working-start plan requires before any hosted deploy.
///
/// The ledger's <c>BoundedScreenDiff</c> can carry TERMINAL CONTENT, which can carry secrets, so this is a
/// content-disclosure surface exactly like the prompt log: every canary is BIDIRECTIONAL and every absence
/// claim has a POSITIVE CONTROL in front of it. The two accounts also push THE SAME producer-minted event
/// id: the composite (tenant, event id) key must store TWO rows - no cross-tenant squat, no existence
/// oracle, and no cross-tenant "duplicate" acknowledgement that would silently swallow one account's
/// evidence.
///
/// The assembly runs sequentially (TestParallelization), so toggling the storage root here is safe; it is
/// restored in DisposeAsync.
/// </summary>
public sealed class ActivityLedgerTenantIsolationTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private const string SecretDiffA = "alpha-account-secret-terminal-rows";
    private const string SecretDiffB = "bravo-account-secret-terminal-rows";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private string? _priorRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-activity-iso-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-activity-iso-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Isolate the storage root BEFORE the Gateway starts so its database binds the temp root, never the
        // developer's real one.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_instancesDir, "prompt-log"),
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two device keys, each bound to its OWN tenant minted by the real registry, plus one
        // registered-but-unbound key for the deny-by-default check.
        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        var tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* best-effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task One_account_never_reads_anothers_terminal_evidence_and_the_same_id_cannot_collide()
    {
        // Both accounts push THE SAME producer-minted event id, each carrying its own secret diff.
        var sharedId = Guid.NewGuid();
        var postA = await PostEvent(_keyA, sharedId, SecretDiffA);
        var postB = await PostEvent(_keyB, sharedId, SecretDiffB);
        Assert.Equal(HttpStatusCode.OK, postA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, postB.StatusCode);

        // B's push of A's id is NOT a duplicate: B owns its own id space. A "duplicates=1" here would mean
        // one account's evidence was silently swallowed by the other's - the cross-tenant squat.
        var ackB = await postB.Content.ReadFromJsonAsync<ActivityEventIngestResponse>();
        Assert.Equal(1, ackB!.Written);
        Assert.Equal(0, ackB.Duplicates);

        var bodyA = await ReadEventsBody(_keyA);
        var bodyB = await ReadEventsBody(_keyB);

        // Positive control in FRONT of each absence claim: each account reads its own evidence back.
        Assert.Contains(SecretDiffA, bodyA);
        Assert.Contains(SecretDiffB, bodyB);

        // The absence claims, both directions.
        Assert.DoesNotContain(SecretDiffB, bodyA);
        Assert.DoesNotContain(SecretDiffA, bodyB);

        // And neither read merely filtered a shared set down: each account sees exactly its one record.
        using var docA = JsonDocument.Parse(bodyA);
        using var docB = JsonDocument.Parse(bodyB);
        Assert.Equal(1, docA.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, docB.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Both_verbs_deny_a_device_key_with_no_bound_tenant()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadEvents(_keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostEvent(_keyUnbound, Guid.NewGuid(), "should never land")).StatusCode);
    }

    private Task<HttpResponseMessage> PostEvent(string deviceKey, Guid eventId, string diff)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "activity-events/batch")
        {
            Content = JsonContent.Create(new ActivityEventIngestRequest
            {
                Events = new List<ActivityEventRecord>
                {
                    new()
                    {
                        EventId = eventId,
                        DirectorSequence = 1,
                        OccurredUtc = DateTime.UtcNow,
                        DirectorId = "dir-1",
                        SessionId = "s1",
                        EventType = ActivityEventTypes.TerminalOutputWhileSettled,
                        Cause = ActivityCauses.TerminalOutputOnly,
                        BoundedScreenDiff = diff,
                    },
                },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> ReadEvents(string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "activity-events?sessionId=s1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<string> ReadEventsBody(string deviceKey)
    {
        var resp = await ReadEvents(deviceKey);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

}
