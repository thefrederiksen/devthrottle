using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Team invitations over real HTTP on a hosted Gateway with Teams released (devthrottle_internal#2301): the caller is
/// the account behind their own device key, the email is asked of a recording mailer (nothing leaves the process), and
/// the team's bill is a team_entitlements row written here the way the website's webhook writes it.
/// </summary>
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
[Collection("GatewayHostedMode")]
public sealed class HostedTeamInvitationEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _owner = "sub-inv-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-inv-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-inv-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _newcomer = "sub-inv-new-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-inv-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingMailer _mailer = new();
    private readonly ITestOutputHelper _output;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyOwner = "";
    private string _keyManager = "";
    private string _keyBob = "";
    private string? _priorHosted;
    private string? _priorRoot;

    public HostedTeamInvitationEndpointsTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        _gateway.TeamInvitationMailer = _mailer;
        await _gateway.StartAsync();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        using (var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");

        _keyOwner = Enroll("dev-inv-owner", _owner, "owner@acme.example");
        _keyManager = Enroll("dev-inv-manager", _manager, "manager@acme.example");
        _keyBob = Enroll("dev-inv-bob", _bob, "bob@gmail.example");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task OwnerInvites_InviteeAcceptsOverTheWire_AndIsAMemberInTheInvitedRole_Once()
    {
        var team = await CreateTeamWithBill();

        var (created, body) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyOwner, new { email = "bob@gmail.example", role = "Developer" });
        Assert.Equal(HttpStatusCode.Created, created);
        var id = body.GetProperty("invitation").GetProperty("id").GetString()!;
        Assert.Equal(id, Assert.Single(_mailer.Sent).Id);
        var token = TokenOf(id);

        var (opened, page) = await Send(HttpMethod.Post, "team-invitations/open", _keyBob, new { token });
        Assert.Equal(HttpStatusCode.OK, opened);
        Assert.Equal("owner@acme.example", page.GetProperty("invitation").GetProperty("invitedBy").GetString());
        Assert.Equal("bob@gmail.example", page.GetProperty("invitation").GetProperty("signedInAs").GetString());
        Assert.True(page.GetProperty("invitation").GetProperty("canRespond").GetBoolean());

        var (accepted, _) = await Send(HttpMethod.Post, "team-invitations/accept", _keyBob, new { token });
        Assert.Equal(HttpStatusCode.OK, accepted);
        var (_, members) = await Send(HttpMethod.Get, $"teams/{team}/members", _keyBob);
        var me = members.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isYou").GetBoolean());
        Assert.Equal("Developer", me.GetProperty("role").GetString());

        var (again, refusal) = await Send(HttpMethod.Post, "team-invitations/accept", _keyBob, new { token });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal(TeamInvitationRefusals.AlreadyAccepted, refusal.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ManagerInvitingAManager_IsForbidden_AndADeveloperInvitingAnyone_IsForbidden()
    {
        var team = await CreateTeamWithBill();
        Assert.True(_gateway.TeamRegistry.AddMember(team, _manager, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(team, _bob, TeamRole.Developer).IsDone);

        var (m, mBody) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyManager, new { email = "x@y.example", role = "Manager" });
        Assert.Equal(HttpStatusCode.Forbidden, m);
        Assert.Equal("Only the Owner can invite a Manager.", mBody.GetProperty("error").GetString());

        // A Developer is refused by the team gate (#2302) before the invitation code runs, in the role table's own words.
        var (d, dBody) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyBob, new { email = "x@y.example", role = "Collaborator" });
        Assert.Equal(HttpStatusCode.Forbidden, d);
        Assert.Equal(TeamEndpointGate.RefusalCode, dBody.GetProperty("code").GetString());
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Developer, TeamPermissions.Row(TeamAction.InviteOrRemoveDevelopersAndCollaborators)),
            dBody.GetProperty("error").GetString());
        using (var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext())
            Assert.Equal(0, ctx.TeamInvitations.Count(i => i.TeamId == team));

        var (ok, _) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyManager, new { email = "x@y.example", role = "Developer" });
        Assert.Equal(HttpStatusCode.Created, ok);
    }

    [Fact]
    public async Task TeamWithNoBill_InvitationIsRefusedWithThePlainWordsReason_AndNoEmailIsAsked()
    {
        var (_, created) = await Send(HttpMethod.Post, "teams", _keyOwner, new { name = "Unbilled" });
        var team = created.GetProperty("team").GetProperty("id").GetString()!;

        var (status, body) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyOwner, new { email = "a@b.example", role = "Developer" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("The team's bill has not started - the Owner finishes billing first.", body.GetProperty("error").GetString());
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task SignedOutBrowserAtTheAcceptPage_IsSentToSignInAndBackToTheSamePage_AndTheLogNeverHoldsTheSecret()
    {
        var team = await CreateTeamWithBill();
        var (_, body) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyOwner, new { email = "nobody.yet@new.example", role = "Collaborator" });
        var token = TokenOf(body.GetProperty("invitation").GetProperty("id").GetString()!);

        using var log = FileLog.RedirectForTests();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"invite/{token}");
        req.Headers.Accept.ParseAdd("text/html");
        using var resp = await _http.SendAsync(req);

        // No account and no device key yet: the browser goes to sign-in (where a new person signs up) carrying the
        // accept page as next=, so the round trip ends on the same accept page with the invitation intact.
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/signin?next={Uri.EscapeDataString("/invite/" + token)}", resp.Headers.Location!.OriginalString);

        // The access log recorded the request - with the secret cut out.
        var lines = log.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains("GET /invite/[redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.Ordinal));

        // After signing up, the same token opens and accepts.
        _gateway.TenantRegistry.MintOrLookupBySubject(_newcomer, "nobody.yet@new.example");
        var keyNew = Enroll("dev-inv-new", _newcomer, "nobody.yet@new.example");
        var (accepted, _) = await Send(HttpMethod.Post, "team-invitations/accept", keyNew, new { token });
        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal(TeamRole.Collaborator, _gateway.TeamRegistry.RoleOf(team, _newcomer));
    }

    private const string PhoneUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    /// <summary>
    /// Review F1: the link in the email is very often opened on a phone. A phone navigation is normally sent to the
    /// mobile app, which has no invitation page, so the invitation was dropped - and that redirect logged the raw
    /// address, secret included. Signed in or signed out, the phone must reach the accept page and its sign-in round
    /// trip, and no log line may hold the secret.
    /// </summary>
    [Fact]
    public async Task PhoneAtTheAcceptPage_ReachesItSignedInAndThroughSignIn_NeverTheMobileApp_AndNoLogLineHoldsTheSecret()
    {
        var team = await CreateTeamWithBill();
        var (_, body) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyOwner, new { email = "bob@gmail.example", role = "Developer" });
        var token = TokenOf(body.GetProperty("invitation").GetProperty("id").GetString()!);

        using var log = FileLog.RedirectForTests();

        // Signed in on the phone (a signed-in browser carries its device key in the cookie): the accept page itself.
        var signedIn = await PhoneGet($"invite/{token}", _keyBob);
        AssertReachedTheCockpit(signedIn, $"/invite/{token}");

        // Signed out on the phone: sent to sign in carrying the accept page, and the sign-in page is the Cockpit's,
        // not the mobile app's front page - so the round trip can end on the same invitation.
        var signedOut = await PhoneGet($"invite/{token}", deviceKey: null);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.Status);
        var signIn = signedOut.Location!;
        Assert.Equal($"/signin?next={Uri.EscapeDataString("/invite/" + token)}", signIn);
        AssertReachedTheCockpit(await PhoneGet(signIn.TrimStart('/'), deviceKey: null), signIn);
        // Where that sign-in returns, on the phone.
        AssertReachedTheCockpit(await PhoneGet("device-callback", deviceKey: null), "/device-callback");

        // Every other phone navigation still goes to the mobile app, signed in or not.
        var elsewhere = await PhoneGet("sessions", _keyBob);
        Assert.Equal(HttpStatusCode.Redirect, elsewhere.Status);
        Assert.Equal("/mobile/", elsewhere.Location);
        var plainSignIn = await PhoneGet("signin?next=%2Fsessions", deviceKey: null);
        Assert.Equal(HttpStatusCode.Redirect, plainSignIn.Status);
        Assert.Equal("/mobile/", plainSignIn.Location);

        var lines = log.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains("GET /invite/[redacted]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("[MobileRedirect] phone navigation /sessions -> /mobile/", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>The phone was not redirected: the Cockpit answered - with the page when the Cockpit is built into this
    /// host, or its own "not built" answer in a test host that has none. Either way, never a 302 to the mobile app.</summary>
    private static void AssertReachedTheCockpit((HttpStatusCode Status, string? Location, string Body) answer, string what)
    {
        Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"a phone at {what} answered {(int)answer.Status} {answer.Location}");
        Assert.True(answer.Status == HttpStatusCode.OK
                ? answer.Body.Contains("<html", StringComparison.OrdinalIgnoreCase)
                : answer.Body.Contains("React Cockpit not built", StringComparison.Ordinal),
            $"a phone at {what} was answered by something other than the Cockpit: {answer.Body}");
    }

    private async Task<(HttpStatusCode Status, string? Location, string Body)> PhoneGet(string path, string? deviceKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/html");
        req.Headers.TryAddWithoutValidation("User-Agent", PhoneUserAgent);
        if (deviceKey is not null)
            req.Headers.Add("Cookie", $"cc-gateway-token={deviceKey}");
        using var resp = await _http.SendAsync(req);
        return (resp.StatusCode, resp.Headers.Location?.OriginalString, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResendAndCancel_OverTheWire_RenewTheLinkAndThenStopIt()
    {
        var team = await CreateTeamWithBill();
        var (_, body) = await Send(HttpMethod.Post, $"teams/{team}/invitations", _keyOwner, new { email = "bob@gmail.example", role = "Developer" });
        var id = body.GetProperty("invitation").GetProperty("id").GetString()!;
        var first = TokenOf(id);

        var (resent, _) = await Send(HttpMethod.Post, $"teams/{team}/invitations/{id}/resend", _keyOwner);
        Assert.Equal(HttpStatusCode.OK, resent);
        Assert.Equal(2, _mailer.Sent.Count);
        var (oldLink, _) = await Send(HttpMethod.Post, "team-invitations/open", _keyBob, new { token = first });
        Assert.Equal(HttpStatusCode.NotFound, oldLink);

        var (cancelled, _) = await Send(HttpMethod.Post, $"teams/{team}/invitations/{id}/cancel", _keyOwner);
        Assert.Equal(HttpStatusCode.OK, cancelled);
        var (accept, refusal) = await Send(HttpMethod.Post, "team-invitations/accept", _keyBob, new { token = TokenOf(id) });
        Assert.Equal(HttpStatusCode.Conflict, accept);
        Assert.StartsWith("This invitation was cancelled by the team", refusal.GetProperty("error").GetString());

        var (_, list) = await Send(HttpMethod.Get, $"teams/{team}/invitations", _keyOwner);
        Assert.Equal("cancelled", Assert.Single(list.GetProperty("invitations").EnumerateArray()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task UnboundDevice_IsRefused_AndAnUnknownTokenIsNotFound()
    {
        var unbound = _gateway.Devices.Register("dev-inv-unbound", "M9").DeviceKey;

        // Refused at the auth layer or at the caller resolution - either way, before any invitation is looked at.
        var (denied, _) = await Send(HttpMethod.Post, "team-invitations/open", unbound, new { token = "anything" });
        Assert.True(denied is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"answered {denied}");
        var (missing, body) = await Send(HttpMethod.Post, "team-invitations/open", _keyBob, new { token = "not-a-real-token" });
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal(TeamInvitationRefusals.NoSuchInvitation, body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Written to the test output, which docs/proof/teams-2301/api-transcript.txt is captured from. Device keys and
    /// the link's secret are not printed.
    /// </summary>
    [Fact]
    public async Task Transcript_TheWholeInvitationFlow()
    {
        var (_, created) = await Send(HttpMethod.Post, "teams", _keyOwner, new { name = "Acme" });
        var team = created.GetProperty("team").GetProperty("id").GetString()!;
        _output.WriteLine("POST /teams {\"name\":\"Acme\"} as owner@acme.example -> 201");
        await Show(HttpMethod.Post, $"teams/{team}/invitations", "teams/{teamId}/invitations", _keyOwner, "owner@acme.example (no bill yet)", new { email = "bob@gmail.example", role = "Developer" });
        StartBill(team);
        _output.WriteLine("(website webhook) team_entitlements row written: active, 1 seat");
        await Show(HttpMethod.Get, $"teams/{team}/invitations/options", "teams/{teamId}/invitations/options", _keyOwner, "owner@acme.example");
        var invitation = await Show(HttpMethod.Post, $"teams/{team}/invitations", "teams/{teamId}/invitations", _keyOwner, "owner@acme.example", new { email = "bob@gmail.example", role = "Developer" });
        var token = TokenOf(invitation.GetProperty("invitation").GetProperty("id").GetString()!);
        await Show(HttpMethod.Post, "team-invitations/open", "team-invitations/open", _keyBob, "bob@gmail.example", new { token = "<the secret from the email link>" }, new { token });
        await Show(HttpMethod.Post, "team-invitations/accept", "team-invitations/accept", _keyBob, "bob@gmail.example", new { token = "<the secret from the email link>" }, new { token });
        await Show(HttpMethod.Post, "team-invitations/accept", "team-invitations/accept", _keyBob, "bob@gmail.example (second time)", new { token = "<the secret from the email link>" }, new { token });
        await Show(HttpMethod.Get, $"teams/{team}/members", "teams/{teamId}/members", _keyOwner, "owner@acme.example");
        await Show(HttpMethod.Get, $"teams/{team}/invitations", "teams/{teamId}/invitations", _keyOwner, "owner@acme.example");
    }

    private async Task<JsonElement> Show(HttpMethod method, string path, string shownPath, string key, string who, object? shown = null, object? sent = null)
    {
        var (status, body) = await Send(method, path, key, sent ?? shown);
        _output.WriteLine("");
        _output.WriteLine($"{method} /{shownPath}{(shown is null ? "" : " " + JsonSerializer.Serialize(shown))} as {who} -> {(int)status} {status}");
        _output.WriteLine(JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));
        return body;
    }

    private async Task<string> CreateTeamWithBill()
    {
        var (status, body) = await Send(HttpMethod.Post, "teams", _keyOwner, new { name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, status);
        var team = body.GetProperty("team").GetProperty("id").GetString()!;
        StartBill(team);
        return team;
    }

    private void StartBill(string team)
    {
        using var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, 'active', 1, 1)", team);
    }

    // The link's secret as the email would carry it: the mailer is the only place it is ever handed (only its hash is
    // stored). The newest send for the invitation wins, as a resend replaces the link.
    private string TokenOf(string invitationId)
    {
        lock (_mailer.Sent) return _mailer.Sent.Last(s => s.Id == invitationId).Token;
    }

    private string Enroll(string deviceId, string subject, string email)
    {
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        var key = _gateway.Devices.Register(deviceId, "M-" + deviceId).DeviceKey;
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string key, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        else if (method == HttpMethod.Post) req.Content = JsonContent.Create(new { });
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }

    private sealed class RecordingMailer : ITeamInvitationMailer
    {
        public List<(string Id, string Token)> Sent { get; } = new();

        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add((invitationId, acceptToken));
            return Task.FromResult(new TeamInvitationMailResult(true, null, 200, null));
        }
    }
}
