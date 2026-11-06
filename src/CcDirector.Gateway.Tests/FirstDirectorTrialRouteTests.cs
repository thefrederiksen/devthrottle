using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The 7-day Pro trial at an account's first Director, END TO END through a REAL hosted <see cref="GatewayHost"/>
/// over REAL HTTP (Website Black review, finding 2). <see cref="FirstDirectorTrialTests"/> calls
/// <see cref="HostedEnrollmentEndpoint.Enroll"/> directly; this proves the WIRING those tests cannot see: that
/// <see cref="GatewayHostOptions.FirstDirectorTrial"/> reaches the route, and that the host hands the route its trial
/// ledger. Without either, the first Director enrolment with the setting on would fail in production, at the flip.
///
/// The account token is a test ES256 token: the host's account-token validator reads its public key set, audience and
/// issuer from the environment variables a test may set (<see cref="GatewayAccountFactory.BuildAuthorizationValidator"/>).
/// Test parallelization is off for this assembly, so setting them for the life of one host is safe; they are put back
/// afterwards.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FirstDirectorTrialRouteTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly string _subject = "sub-fdt-route-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-fdt-route-" + Guid.NewGuid().ToString("N"));
    private readonly TestEs256Key _key = new();
    private readonly (string Name, string? Prior)[] _env =
    {
        ("CC_DIRECTOR_ROOT", null),
        (DevThrottleSigningKeys.PublicKeySetEnvVar, null),
        (GatewayAccountFactory.SupabaseAudienceEnvVar, null),
        (GatewayAccountFactory.SupabaseIssuerEnvVar, null),
    };
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        for (var i = 0; i < _env.Length; i++)
            _env[i].Prior = Environment.GetEnvironmentVariable(_env[i].Name);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);
        Environment.SetEnvironmentVariable(DevThrottleSigningKeys.PublicKeySetEnvVar, _key.PublicKeySetJson());
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SupabaseAudienceEnvVar, Audience);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SupabaseIssuerEnvVar, Issuer);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true,
            options: new GatewayHostOptions { Hosted = true, FirstDirectorTrial = FirstDirectorTrial.Parse("1") });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // The payment side's table must exist for the paid read to be KNOWLEDGE ("not paying"), not Unknown. Seeding
        // ANOTHER account creates it and leaves this test's account with no paid row: the free plan.
        _gateway.SeedEntitlementForTest("sub-fdt-someone-else-" + Guid.NewGuid().ToString("N"));

        // A test host (not a real hosted image) seeds a PAID "hosted" row for every account a device is bound to, so
        // the low-level test enrol paths pass the request-path cutoff (GatewayHost, OnAccountBoundForTest). A paid row
        // wins over a trial, so left on it would hide the very read this test makes. Production never wires it.
        _gateway.Devices.OnAccountBoundForTest = null;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        foreach (var (name, prior) in _env)
            Environment.SetEnvironmentVariable(name, prior);
        _key.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private async Task<HttpStatusCode> Post(string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            _key.Token(_subject, "route@example.invalid", Audience, Issuer));
        using var resp = await _http.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task FirstDirector_OverTheWire_WithTheSettingOn_StartsTheTrial_AndTheNextReadIsPro()
    {
        Assert.Equal(TrialStatusKind.NeverGranted, _gateway.TrialRegistry.ReadStatus(_subject, ReadAt).Kind);

        var status = await Post("devices/enroll-hosted",
            new { deviceId = "director-1", machineName = "M", platform = "windows", deviceType = "workstation" });

        Assert.Equal(HttpStatusCode.OK, status);
        // The route stamps the trial with the moment it ran. The row's own instants are read back, and every judgement
        // below is made at a moment taken from them, so nothing here reads a clock.
        var row = _gateway.TrialRegistry.ReadStatus(_subject, ReadAt);
        Assert.NotNull(row.StartedAtUtc);
        Assert.Equal(TimeSpan.FromDays(7), row.ExpiresAtUtc!.Value - row.StartedAtUtc!.Value);
        var duringTheTrial = row.StartedAtUtc.Value.AddHours(1);
        Assert.Equal(TrialOutcome.Active, _gateway.TrialRegistry.Evaluate(_subject, duringTheTrial).Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, _gateway.EntitlementRegistry.Evaluate(_subject, duringTheTrial).Tier);
    }

    /// <summary>A fixed instant for reads whose answer here does not depend on the moment (is there a row at all).</summary>
    private static readonly DateTime ReadAt = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MovingADirector_ToTheOwnAccount_OverTheWire_GrantsNoTrial()
    {
        // A Director first set up for a team, moved to the person's own account: a move is never a first arrival
        // (HostedEnrollmentEndpoint.Move passes no ledger), even for an account the Gateway mints a tenant for here.
        var team = _gateway.TeamRegistry.CreateTeam("sub-fdt-owner-" + Guid.NewGuid().ToString("N"), "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(team, _subject, TeamRole.Developer).IsDone);
        _gateway.Devices.RegisterForTenant(new TenantId(team), _subject,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(team, _subject, "director-1"), "M");

        var status = await Post("devices/enroll-hosted/move", new { deviceId = "director-1" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(_gateway.TenantRegistry.LookupBySubject(_subject));
        Assert.Equal(TrialStatusKind.NeverGranted, _gateway.TrialRegistry.ReadStatus(_subject, ReadAt).Kind);
    }
}
