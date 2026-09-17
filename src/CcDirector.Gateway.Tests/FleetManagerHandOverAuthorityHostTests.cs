using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// WHO MAY HAND A SESSION OVER (the Fleet Manager mission, step 8, the Architect's ruling), on a REAL booted hosted
/// Gateway, through its real middleware and session-key guard, with real credentials and a tunnel-connected Director
/// that carries out <c>set-controller</c>:
///
///  - the account's marked Fleet Manager, with its own session key, takes to itself a session that answers to the
///    owner, and hands a session it owns back to the owner;
///  - it never takes a session another running session owns, and never reaches another account's session;
///  - every other session key of the account is refused with <c>not_fleet_manager</c> and the reason;
///  - the owner's own browser still hands over as before.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public sealed class FleetManagerHandOverAuthorityHostTests : IAsyncLifetime
{
    private const string SharedToken = "fleet-manager-hand-over-authority-token";
    private const string DirectorIdA = "director-hand-over-a";
    private const string DirectorIdB = "director-hand-over-b";

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hand-over-authority-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _directorA = null!;
    private FakeTunnelDirector _directorB = null!;
    private HttpClient _ownerA = null!;
    private HttpClient _fleetManager = null!;
    private HttpClient _plainKey = null!;
    private HttpClient _fleetManagerB = null!;

    private readonly string _fleetManagerId = Guid.NewGuid().ToString();
    private readonly string _plainId = Guid.NewGuid().ToString();
    private readonly string _secondPlainId = Guid.NewGuid().ToString();
    private readonly string _ownedId = Guid.NewGuid().ToString();
    private readonly string _architectId = Guid.NewGuid().ToString();
    private readonly string _workerId = Guid.NewGuid().ToString();
    private readonly string _fleetManagerBId = Guid.NewGuid().ToString();
    private readonly string _foreignId = Guid.NewGuid().ToString();

    /// <summary>What Director A holds: its sessions, whose owner <c>set-controller</c> changes.</summary>
    private readonly Dictionary<string, SessionDto> _rowsA = new();

    public FleetManagerHandOverAuthorityHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-ho-a-{_runId}";
        var subjectB = $"sub-ho-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"ho-a-{_runId}@example.com", $"dev-ho-dir-a-{_runId}", "MHOA");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"ho-b-{_runId}@example.com", $"dev-ho-dir-b-{_runId}", "MHOB");
        Assert.NotEqual(a.Tenant.Value, b.Tenant.Value);

        var ownerA = _gateway.Devices.RegisterForTenant(a.Tenant, subjectA, $"dev-ho-owner-a-{_runId}", "OWNER-A", deviceType: "browser");
        var ownerB = _gateway.Devices.RegisterForTenant(b.Tenant, subjectB, $"dev-ho-owner-b-{_runId}", "OWNER-B", deviceType: "browser");
        _ownerA = Client(ownerA.DeviceKey);
        using var ownerBClient = Client(ownerB.DeviceKey);

        _fleetManager = Client(SessionKey(a.Tenant, DirectorIdA, _fleetManagerId));
        _plainKey = Client(SessionKey(a.Tenant, DirectorIdA, _plainId));
        _fleetManagerB = Client(SessionKey(b.Tenant, DirectorIdB, _fleetManagerBId));

        var now = DateTime.UtcNow;
        foreach (var row in new[]
                 {
                     Row(_fleetManagerId, "Fleet Manager", null, now.AddHours(-3)),
                     Row(_plainId, "Plain work", null, now.AddHours(-2)),
                     Row(_secondPlainId, "More plain work", null, now.AddHours(-2)),
                     Row(_ownedId, "Already the Fleet Manager's", _fleetManagerId, now.AddHours(-1)),
                     Row(_architectId, "An Architect", null, now.AddHours(-1)),
                     Row(_workerId, "The Architect's Worker", _architectId, now.AddMinutes(-30)),
                 })
            _rowsA[row.SessionId] = row;

        _directorA = await FakeTunnelDirector.StartAsync(_gateway, a.DeviceKey, DirectorIdA, "MHOA",
            dispatch: SetControllerOnA, changesOwner: true);
        _directorB = await FakeTunnelDirector.StartAsync(_gateway, b.DeviceKey, DirectorIdB, "MHOB",
            dispatch: cmd =>
            {
                if (cmd.Verb == "set-controller") lock (_ownerChangesB) _ownerChangesB.Add(cmd);
                return FakeTunnelDirector.Ok(new { ok = true });
            },
            changesOwner: true);
        await PushAAsync();
        await _directorB.PushSnapshotAsync(
            Row(_fleetManagerBId, "Another account's Fleet Manager", null, now.AddHours(-1)),
            Row(_foreignId, "Another account's work", null, now.AddHours(-1)));

        await MarkAsync(_ownerA, _fleetManagerId);
        await MarkAsync(ownerBClient, _fleetManagerBId);
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _fleetManager, _plainKey, _fleetManagerB })
            http?.Dispose();
        if (_directorA is not null) await _directorA.DisposeAsync();
        if (_directorB is not null) await _directorB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static SessionDto Row(string id, string name, string? owner, DateTime created) => new()
    {
        SessionId = id, Name = name, ActivityState = "WaitingForInput", MachineName = "MHOA",
        IsControlled = owner is not null, ControllerSessionId = owner,
        CreatedAt = created, LastActivityAt = DateTime.UtcNow,
    };

    /// <summary>Every <c>set-controller</c> each Director received. The Gateway sends other verbs too (display state),
    /// which are not a change of owner.</summary>
    private readonly List<DirectorCommand> _ownerChangesA = new();
    private readonly List<DirectorCommand> _ownerChangesB = new();

    private DirectorCommandResult SetControllerOnA(DirectorCommand cmd)
    {
        if (cmd.Verb != "set-controller")
            return FakeTunnelDirector.Ok(new { ok = true });
        lock (_ownerChangesA) _ownerChangesA.Add(cmd);
        var request = JsonSerializer.Deserialize<SetControllerRequest>(cmd.PayloadJson!, FakeTunnelDirector.WebJson)!;
        var row = _rowsA[cmd.SessionId!];
        row.ControllerSessionId = string.IsNullOrWhiteSpace(request.ControllerSessionId) ? null : request.ControllerSessionId;
        row.IsControlled = row.ControllerSessionId is not null;
        return FakeTunnelDirector.Ok(row);
    }

    private Task PushAAsync() => _directorA.PushSnapshotAsync(_rowsA.Values.ToArray());

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(TenantId tenant, string directorId, string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(tenant, directorId, sessionId, GatewaySessionKey.Hash(key),
            DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private async Task MarkAsync(HttpClient owner, string sessionId)
    {
        var (status, _) = await SendAsync(owner, "PUT", "gateway/fleet-manager", new { sessionId });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> HandOverAsync(HttpClient http, string session, string to)
    {
        var (status, text) = await SendAsync(http, "POST", "gateway/fleet-manager/hand-over", new { session, to });
        return (status, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, string verb, string path, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{verb} {path} -> {(int)resp.StatusCode} {text}");
        return (resp.StatusCode, text);
    }

    // ---- the Fleet Manager's own key -----------------------------------------------------------------------

    [Fact]
    public async Task The_Fleet_Manager_takes_a_session_that_answers_to_the_owner_to_itself()
    {
        var (status, body) = await HandOverAsync(_fleetManager, _plainId, "fleet-manager");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_fleetManagerId, body.GetProperty("ownerSessionId").GetString());
        Assert.Equal(_plainId, Assert.Single(_ownerChangesA).SessionId);
        Assert.Equal(_fleetManagerId, _rowsA[_plainId].ControllerSessionId);
    }

    [Fact]
    public async Task The_Fleet_Manager_hands_a_session_it_owns_back_to_the_owner()
    {
        var (status, body) = await HandOverAsync(_fleetManager, _ownedId, "owner");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("ownerSessionId").ValueKind);
        Assert.Null(_rowsA[_ownedId].ControllerSessionId);
    }

    [Fact]
    public async Task The_Fleet_Manager_never_takes_a_session_another_running_session_owns()
    {
        var (status, body) = await HandOverAsync(_fleetManager, _workerId, "fleet-manager");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("which is still running, so it was not handed over", body.GetProperty("error").GetString());
        Assert.Empty(_ownerChangesA);
        Assert.Equal(_architectId, _rowsA[_workerId].ControllerSessionId);
    }

    [Fact]
    public async Task The_Fleet_Manager_never_hands_back_a_session_it_does_not_own()
    {
        var (status, _) = await HandOverAsync(_fleetManager, _workerId, "owner");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Empty(_ownerChangesA);
    }

    [Fact]
    public async Task The_Fleet_Manager_never_reaches_another_accounts_session()
    {
        var (status, body) = await HandOverAsync(_fleetManager, _foreignId, "fleet-manager");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.StartsWith($"No session {_foreignId} is running in this account", body.GetProperty("error").GetString());
        Assert.Empty(_ownerChangesB);
    }

    [Fact]
    public async Task Another_accounts_Fleet_Manager_never_reaches_this_accounts_session()
    {
        var (status, _) = await HandOverAsync(_fleetManagerB, _plainId, "fleet-manager");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Empty(_ownerChangesA);
        Assert.Null(_rowsA[_plainId].ControllerSessionId);
    }

    // ---- every other session key ---------------------------------------------------------------------------

    [Theory]
    [InlineData("fleet-manager")]
    [InlineData("owner")]
    public async Task Any_other_session_key_is_refused_with_the_reason(string to)
    {
        var target = to == "owner" ? _ownedId : _secondPlainId;

        var (status, body) = await HandOverAsync(_plainKey, target, to);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_fleet_manager", body.GetProperty("code").GetString());
        Assert.Equal($"Only this account's Fleet Manager session ({_fleetManagerId}) may hand a session over; " +
                     $"session {_plainId} is not it. The owner hands sessions over from the Cockpit or the phone.",
            body.GetProperty("error").GetString());
        Assert.Empty(_ownerChangesA);
    }

    [Fact]
    public async Task A_Fleet_Manager_that_is_itself_owned_is_refused_with_the_reason()
    {
        _rowsA[_fleetManagerId].ControllerSessionId = _architectId;
        _rowsA[_fleetManagerId].IsControlled = true;
        await PushAAsync();

        var (status, body) = await HandOverAsync(_fleetManager, _secondPlainId, "fleet-manager");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_fleet_manager", body.GetProperty("code").GetString());
        Assert.Contains("is not running as the Fleet Manager", body.GetProperty("error").GetString());
        Assert.Empty(_ownerChangesA);
    }

    // ---- the owner, as today -------------------------------------------------------------------------------

    [Fact]
    public async Task The_owners_own_browser_still_hands_over_both_ways()
    {
        var (to, _) = await HandOverAsync(_ownerA, _secondPlainId, "fleet-manager");
        Assert.Equal(HttpStatusCode.OK, to);
        Assert.Equal(_fleetManagerId, _rowsA[_secondPlainId].ControllerSessionId);

        await PushAAsync();
        var (back, _) = await HandOverAsync(_ownerA, _secondPlainId, "owner");
        Assert.Equal(HttpStatusCode.OK, back);
        Assert.Null(_rowsA[_secondPlainId].ControllerSessionId);
    }
}
