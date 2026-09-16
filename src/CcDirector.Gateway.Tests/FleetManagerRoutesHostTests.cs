using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The eight Fleet Manager routes under <c>/gateway/fleet-manager</c> (the Fleet Manager mission, step 3) on a
/// REAL booted hosted Gateway, through its real middleware, with real credentials: an owner's browser device key,
/// a Director's own device key, and minted session keys. What ONLY a booted host proves:
///
///  - THE MAPPING. Each route answers on the path and verb the command line calls.
///  - THE GUARD. A session key reaches every route - the answer is the ROUTE's, never the guard's
///    <c>session_key_out_of_scope</c>.
///  - THE AUTHORITY, end to end. The account's marked Fleet Manager session is allowed on all eight; any other
///    session key of the account is refused by the route with <c>not_fleet_manager</c> and a reason; the owner's
///    own device may do everything but file; a Director's key may do nothing.
///  - THE MARK HISTORY, written by the real mark route: a replacement Fleet Manager's digest still carries the
///    sessions the earlier one started, with that owner's id.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public sealed class FleetManagerRoutesHostTests : IAsyncLifetime
{
    private const string SharedToken = "fleet-manager-routes-host-token";
    private const string DirectorId = "director-fm-a";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private HttpClient _ownerA = null!;
    private HttpClient _ownerB = null!;
    private HttpClient _directorA = null!;
    private HttpClient _fleetManager = null!;
    private HttpClient _otherSession = null!;
    private HttpClient _nextFleetManager = null!;

    private readonly string _fleetManagerId = Guid.NewGuid().ToString();
    private readonly string _otherSessionId = Guid.NewGuid().ToString();
    private readonly string _ownedId = Guid.NewGuid().ToString();
    private readonly string _nextFleetManagerId = Guid.NewGuid().ToString();
    private readonly string _ownedByNextId = Guid.NewGuid().ToString();

    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-fleet-manager-routes-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public FleetManagerRoutesHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-fm-a-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"fm-a-{_runId}@example.com", $"dev-fm-dir-{_runId}", "MFMA");
        var b = HostedTestEnrollment.Enroll(_gateway, $"sub-fm-b-{_runId}", $"fm-b-{_runId}@example.com", $"dev-fm-b-{_runId}", "MFMB");
        _tenantA = a.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(a.Tenant.Value, b.Tenant.Value);

        // The enrolment helper registers a Director's key ("workstation"). The owner's signed-in browser is a
        // second device of the same account, with the device type a Cockpit sign-in stamps.
        var ownerA = _gateway.Devices.RegisterForTenant(a.Tenant, subjectA, $"dev-fm-owner-{_runId}", "OWNER-A",
            deviceType: "browser");
        var ownerB = _gateway.Devices.RegisterForTenant(b.Tenant, $"sub-fm-b-{_runId}", $"dev-fm-owner-b-{_runId}",
            "OWNER-B", deviceType: "phone");
        _ownerA = Client(ownerA.DeviceKey);
        _ownerB = Client(ownerB.DeviceKey);
        _directorA = Client(a.DeviceKey);

        _fleetManager = Client(SessionKey(_fleetManagerId));
        _otherSession = Client(SessionKey(_otherSessionId));
        _nextFleetManager = Client(SessionKey(_nextFleetManagerId));

        var now = DateTime.UtcNow;
        Push(_tenantA,
            new SessionDto { SessionId = _fleetManagerId, Name = "Fleet Manager", ActivityState = "WaitingForInput",
                             CreatedAt = now.AddHours(-3), LastActivityAt = now },
            new SessionDto { SessionId = _otherSessionId, Name = "The owner's own session", ActivityState = "WaitingForInput",
                             CreatedAt = now.AddHours(-2), LastActivityAt = now },
            new SessionDto { SessionId = _ownedId, Name = "Started by the first Fleet Manager", ActivityState = "Working",
                             IsControlled = true, ControllerSessionId = _fleetManagerId,
                             CreatedAt = now.AddHours(-1), LastActivityAt = now },
            new SessionDto { SessionId = _nextFleetManagerId, Name = "Fleet Manager after the reset", ActivityState = "WaitingForInput",
                             CreatedAt = now.AddMinutes(-30), LastActivityAt = now },
            new SessionDto { SessionId = _ownedByNextId, Name = "Started by the second Fleet Manager", ActivityState = "WaitingForInput",
                             IsControlled = true, ControllerSessionId = _nextFleetManagerId,
                             CreatedAt = now.AddMinutes(-10), LastActivityAt = now });

        // The owner marks the Fleet Manager through the real mark route.
        await Mark(_fleetManagerId);
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _ownerB, _directorA, _fleetManager, _otherSession, _nextFleetManager })
            http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, DirectorId, sessionId,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private void Push(TenantId tenant, params SessionDto[] sessions)
    {
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-" + DirectorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, DirectorId, "conn-" + DirectorId);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(tenant, DirectorId, "conn-" + DirectorId, ++_pushSequence,
            sessions.ToList()));
    }

    private async Task Mark(string sessionId)
    {
        var (status, body) = await Send(_ownerA, "PUT", "gateway/fleet-manager", new { sessionId });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(sessionId, Root(body).GetProperty("sessionId").GetString());
    }

    private async Task<(HttpStatusCode Status, string Body)> Send(HttpClient http, string verb, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{verb} {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + text);
        return (resp.StatusCode, text);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static object ReadyBody(string title) => new
    {
        kind = "ready",
        title,
        ready = new
        {
            pullRequest = "https://github.com/example/repo/pull/1",
            risk = "low",
            checks = "passed",
            tested = "the unit suite",
            reviewedBy = "a second reviewer",
            change = "The roster sorts by name.",
        },
    };

    /// <summary>A record filed by the Fleet Manager, and a preference kept by the owner, to act on.</summary>
    private async Task<(string OutcomeId, string PreferenceId)> SeedAsync()
    {
        var filed = await Send(_fleetManager, "POST", "gateway/fleet-manager/outcomes", ReadyBody("Seeded record"));
        Assert.Equal(HttpStatusCode.Created, filed.Status);
        var kept = await Send(_ownerA, "POST", "gateway/fleet-manager/preferences", new { text = "merge docs on green" });
        Assert.Equal(HttpStatusCode.Created, kept.Status);
        return (Root(filed.Body).GetProperty("id").GetString()!, Root(kept.Body).GetProperty("id").GetString()!);
    }

    /// <summary>One call to one of the eight routes.</summary>
    private async Task<(HttpStatusCode Status, string Body)> CallAsync(HttpClient http, string route, string digestSession)
    {
        var (outcomeId, preferenceId) = await SeedAsync();
        return route switch
        {
            "file" => await Send(http, "POST", "gateway/fleet-manager/outcomes", ReadyBody("Filed in the test")),
            "list" => await Send(http, "GET", "gateway/fleet-manager/outcomes?status=all"),
            "read" => await Send(http, "GET", $"gateway/fleet-manager/outcomes/{outcomeId}"),
            "answer" => await Send(http, "POST", $"gateway/fleet-manager/outcomes/{outcomeId}/answer", new { answer = "Merge it." }),
            "preferences" => await Send(http, "GET", "gateway/fleet-manager/preferences"),
            "prefer" => await Send(http, "POST", "gateway/fleet-manager/preferences", new { text = "never merge on red" }),
            "forget" => await Send(http, "DELETE", $"gateway/fleet-manager/preferences/{preferenceId}"),
            "digest" => await Send(http, "GET", $"gateway/fleet-manager/digest?session={digestSession}"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    public static TheoryData<string> Routes => new() { "file", "list", "read", "answer", "preferences", "prefer", "forget", "digest" };

    private static void AssertTheRouteAnswered(string body)
        => Assert.DoesNotContain("session_key_out_of_scope", body);

    // ---- who may call, per route -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task The_marked_Fleet_Manager_session_key_is_allowed(string route)
    {
        var (status, body) = await CallAsync(_fleetManager, route, _fleetManagerId);

        AssertTheRouteAnswered(body);
        Assert.Equal(route is "file" or "prefer" ? HttpStatusCode.Created : HttpStatusCode.OK, status);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Another_session_key_of_the_account_is_refused_by_the_route_with_the_reason(string route)
    {
        var (status, body) = await CallAsync(_otherSession, route, _otherSessionId);

        AssertTheRouteAnswered(body);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_fleet_manager", Root(body).GetProperty("code").GetString());
        var error = Root(body).GetProperty("error").GetString()!;
        Assert.Contains($"only this account's Fleet Manager session ({_fleetManagerId}) may", error);
        Assert.Contains($"session {_otherSessionId} is not it", error);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("read")]
    [InlineData("answer")]
    [InlineData("preferences")]
    [InlineData("prefer")]
    [InlineData("forget")]
    [InlineData("digest")]
    public async Task The_owners_own_device_is_allowed(string route)
    {
        var (status, _) = await CallAsync(_ownerA, route, _otherSessionId);

        Assert.Equal(route is "prefer" ? HttpStatusCode.Created : HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task The_owners_own_device_may_not_file_a_record()
    {
        var (status, body) = await CallAsync(_ownerA, "file", _fleetManagerId);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.StartsWith("the owner does not file records", Root(body).GetProperty("error").GetString());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_Directors_own_key_is_refused(string route)
    {
        var (status, body) = await CallAsync(_directorA, route, _fleetManagerId);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_fleet_manager", Root(body).GetProperty("code").GetString());
    }

    // ---- what the allowed callers get ----------------------------------------------------------------------

    [Fact]
    public async Task An_answer_carries_who_gave_it_and_a_second_answer_is_a_conflict()
    {
        var (outcomeId, _) = await SeedAsync();

        var first = await Send(_ownerA, "POST", $"gateway/fleet-manager/outcomes/{outcomeId}/answer", new { answer = "Merge it." });
        var second = await Send(_fleetManager, "POST", $"gateway/fleet-manager/outcomes/{outcomeId}/answer", new { answer = "Wait." });

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("owner", Root(first.Body).GetProperty("answeredBy").GetString());
        Assert.Equal("owner", Root(first.Body).GetProperty("answeredByRole").GetString());
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("already_answered", Root(second.Body).GetProperty("code").GetString());

        var (_, filedByFm) = await Send(_fleetManager, "POST", "gateway/fleet-manager/outcomes", ReadyBody("Answered by the Fleet Manager"));
        var fmId = Root(filedByFm).GetProperty("id").GetString();
        var byFm = await Send(_fleetManager, "POST", $"gateway/fleet-manager/outcomes/{fmId}/answer", new { answer = "Merge it." });
        Assert.Equal(_fleetManagerId, Root(byFm.Body).GetProperty("answeredBy").GetString());
        Assert.Equal("fleet-manager", Root(byFm.Body).GetProperty("answeredByRole").GetString());
    }

    [Fact]
    public async Task Another_accounts_owner_cannot_read_this_accounts_record()
    {
        var (outcomeId, _) = await SeedAsync();

        var foreign = await Send(_ownerB, "GET", $"gateway/fleet-manager/outcomes/{outcomeId}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
    }

    [Fact]
    public async Task The_Fleet_Manager_may_not_read_another_sessions_digest()
    {
        var (status, body) = await Send(_fleetManager, "GET", $"gateway/fleet-manager/digest?session={_otherSessionId}");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.StartsWith("the Fleet Manager reads its own digest only", Root(body).GetProperty("error").GetString());
    }

    /// <summary>
    /// A REPLACEMENT: the owner marks a new Fleet Manager in the old one's place. The old session key is refused
    /// from then on, and the new one's digest carries what the old one started - still owned by the old id, since
    /// nothing has handed it over - beside its own.
    /// </summary>
    [Fact]
    public async Task After_a_new_Fleet_Manager_is_marked_its_digest_still_carries_the_earlier_ones_sessions()
    {
        await Mark(_nextFleetManagerId);

        var old = await Send(_fleetManager, "GET", "gateway/fleet-manager/outcomes");
        Assert.Equal(HttpStatusCode.Forbidden, old.Status);

        var (status, body) = await Send(_nextFleetManager, "GET", $"gateway/fleet-manager/digest?session={_nextFleetManagerId}");
        Assert.Equal(HttpStatusCode.OK, status);
        var digest = JsonSerializer.Deserialize<FleetDigestDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(digest.IsFleetManager);
        Assert.Equal(_nextFleetManagerId, digest.FleetManagerSessionId);
        Assert.Equal(new[] { _fleetManagerId, _nextFleetManagerId }, digest.FleetManagerSessionIds);
        Assert.Equal(new[] { (_ownedId, _fleetManagerId), (_ownedByNextId, _nextFleetManagerId) },
            digest.OwnedSessions.Select(s => (s.SessionId, s.OwnerSessionId)));
    }
}
