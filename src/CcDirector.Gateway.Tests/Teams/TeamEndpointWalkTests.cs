using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// WHO CAN DO WHAT, ACROSS THE WHOLE ROUTE TABLE (devthrottle_internal#2302). A REAL hosted <see cref="GatewayHost"/>
/// with Teams released, its FINALISED route table read endpoint by endpoint, and every endpoint put to the gate the
/// host itself installed (<see cref="GatewayHost.TeamGate"/>) as a request inside a team's tenant:
///
/// <list type="bullet">
/// <item>an endpoint that states no action is refused inside a team - so one added later is refused until someone
/// writes down what it does (the endpoint walk);</item>
/// <item>every endpoint of the session, computer, transcript, prompt, Mentor, skills and team families states one;</item>
/// <item>a Collaborator is refused every session, computer, Mentor and skills endpoint (#2302 test 2);</item>
/// <item>no role reaches another person's live session or transcript (#2302 test 3), and a Manager is refused another
/// person's prompts (#2302 test 4);</item>
/// <item>someone who is not a member is refused everywhere.</item>
/// </list>
///
/// And over REAL HTTP: the one team endpoint a person reaches today, the member list, through the real pipeline for
/// each role and for a stranger; and the fact that a key bound to a team's tenant is not accepted by today's hosted
/// device registry at all, so no request yet runs inside a team (devthrottle_internal#2311 changes that, and the gate
/// is what it will then meet).
///
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TeamEndpointWalkTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The families #2302 names: every endpoint under these must state its action.</summary>
    private static readonly string[] DeclaredFamilies =
    {
        "/teams/{teamId}",
        "/sessions", "/interrupted", "/session-numbers", "/fanout", "/handover", "/worktrees", "/repositories", "/fleet",
        "/directors", "/machines", "/launchers", "/director-stream", "/launcher-stream",
        "/history", "/prompts", "/transcription", "/dictation", "/wingman",
        "/gateway/mentor-report",
        "/gateway/skills", "/gateway/workflows", "/gateway/workflow-runs",
    };

    private readonly string _owner = "sub-walk-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-walk-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _developer = "sub-walk-developer-" + Guid.NewGuid().ToString("N");
    private readonly string _collaborator = "sub-walk-collaborator-" + Guid.NewGuid().ToString("N");
    private readonly string _stranger = "sub-walk-stranger-" + Guid.NewGuid().ToString("N");

    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-teamwalk-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;
    private string _team = "";
    private (string Method, string Pattern)[] _routes = Array.Empty<(string, string)>();

    public TeamEndpointWalkTests(ITestOutputHelper output) => _output = output;

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
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        foreach (var (subject, email) in new[] { (_owner, "owner@example.com"), (_manager, "manager@example.com"),
                     (_developer, "developer@example.com"), (_collaborator, "collaborator@example.com"), (_stranger, "stranger@example.com") })
            _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _team = _gateway.TeamRegistry.CreateTeam(_owner, "Walk").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _developer, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _collaborator, TeamRole.Collaborator).IsDone);

        // The finalised route table, each endpoint once per method it answers. A method-less endpoint (a hub, a
        // fallback) is asked as both a read and a change. The hosted refusal catch-all claims denied families and
        // serves nothing, so it is not an endpoint a team could use.
        _routes = _gateway.MappedEndpoints.OfType<RouteEndpoint>()
            .Select(e => (Pattern: TeamEndpointRules.Normalize(e.RoutePattern.RawText),
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>()))
            .Where(r => !r.Pattern.Contains("hostedDeniedPath", StringComparison.Ordinal))
            .SelectMany(r => r.Methods.Count == 0 ? new[] { "GET", "POST" } : r.Methods.ToArray(), (r, m) => (Method: m, r.Pattern))
            .Distinct()
            .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToArray();
        Assert.True(_routes.Length > 300, $"The route table read only {_routes.Length} endpoints - the walk would prove nothing.");
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

    private string SubjectFor(TeamRole role) => role switch
    {
        TeamRole.Owner => _owner,
        TeamRole.Manager => _manager,
        TeamRole.Developer => _developer,
        _ => _collaborator,
    };

    /// <summary>One request to a real endpoint, inside the team: through its tenant, or through the route's
    /// {teamId} from the caller's own account for the team routes.</summary>
    private TeamGateVerdict Ask(string method, string pattern, string subject, TeamOwnership whose)
    {
        var rule = TeamEndpointRules.Find(method, pattern);
        if (rule?.TeamFrom == TeamFrom.RouteTeamId)
            return _gateway.TeamGate.Check(method, pattern, name => name == "teamId" ? _team : null,
                _gateway.TenantRegistry.LookupBySubject(subject), () => subject, _ => whose);
        return _gateway.TeamGate.Check(method, pattern, _ => null, new TenantId(_team), () => subject, _ => whose);
    }

    private static bool InFamily(string pattern, IEnumerable<string> families) =>
        families.Any(f => pattern == f || pattern.StartsWith(f.EndsWith('/') ? f : f + "/", StringComparison.Ordinal));

    [Fact]
    public void EveryEndpoint_InATeam_IsRefusedUnlessItStatesAnAction()
    {
        var undeclared = _routes.Where(r => TeamEndpointRules.Find(r.Method, r.Pattern) is null).ToArray();
        var declared = _routes.Length - undeclared.Length;
        _output.WriteLine($"{_routes.Length} endpoint-methods on the hosted route table: {declared} state an action, {undeclared.Length} state none and are refused inside a team.");

        foreach (var (method, pattern) in undeclared)
        {
            var verdict = Ask(method, pattern, _owner, TeamOwnership.Callers);
            Assert.True(verdict.Outcome == TeamGateOutcome.Refused && verdict.Message == TeamEndpointGate.UndeclaredRefusal,
                $"{method} {pattern} states no action, yet the gate answered {verdict.Outcome} for the team's Owner inside the team.");
        }
        Assert.NotEmpty(undeclared);
    }

    [Fact]
    public void EveryEndpointOfTheNamedFamilies_StatesAnAction()
    {
        var inFamilies = _routes.Where(r => InFamily(r.Pattern, DeclaredFamilies)).ToArray();
        _output.WriteLine($"{inFamilies.Length} endpoint-methods in the session, computer, transcript, prompt, Mentor, skills and team families:");
        foreach (var (method, pattern) in inFamilies)
        {
            var rule = TeamEndpointRules.Find(method, pattern);
            _output.WriteLine($"  {method} {pattern} -> {rule?.Action.ToString() ?? "NONE"}{(rule?.OthersAction is { } o ? $" (someone else's: {o})" : "")}");
            Assert.True(rule is not null, $"{method} {pattern} is in a family #2302 names but states no action.");
        }
        Assert.True(inFamilies.Length > 100, $"Only {inFamilies.Length} endpoints matched the families - the walk would prove nothing.");
    }

    [Fact]
    public void EveryRule_StatesTheActionOfAtLeastOneRealEndpoint()
    {
        foreach (var rule in TeamEndpointRules.All)
            Assert.True(_routes.Any(r => ReferenceEquals(TeamEndpointRules.Find(r.Method, r.Pattern), rule)),
                $"The rule for {rule.Methods} {rule.Prefix} matches no endpoint on the route table - it is stale.");
    }

    [Fact]
    public void Issue2302Test2_ACollaborator_EverySessionComputerMentorAndSkillsEndpoint_IsRefused()
    {
        var families = DeclaredFamilies.Where(f => f != "/teams/{teamId}").ToArray();
        var asked = 0;
        foreach (var (method, pattern) in _routes.Where(r => InFamily(r.Pattern, families)))
        {
            foreach (var whose in new[] { TeamOwnership.Callers, TeamOwnership.SomeoneElses })
            {
                var verdict = Ask(method, pattern, _collaborator, whose);
                Assert.True(verdict.Outcome == TeamGateOutcome.Refused, $"{method} {pattern} ({whose}) was not refused for a Collaborator.");
                asked++;
            }
        }
        _output.WriteLine($"{asked} Collaborator requests, every one refused.");
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public void Issue2302Test3_NoRole_ReadsAnotherPersonsLiveSessionOrTranscript(TeamRole role)
    {
        var sessionsAndTranscripts = _routes
            .Where(r => TeamEndpointRules.Find(r.Method, r.Pattern)?.OthersAction == TeamAction.JoinOrWatchSomeoneElsesSession)
            .ToArray();
        Assert.Contains(sessionsAndTranscripts, r => r.Pattern == "/sessions/{sid}/stream");
        Assert.Contains(sessionsAndTranscripts, r => r.Pattern == "/sessions/{sid}/buffer");
        Assert.Contains(sessionsAndTranscripts, r => r.Pattern == "/history/sessions/{sessionId}");

        foreach (var (method, pattern) in sessionsAndTranscripts)
        {
            var verdict = Ask(method, pattern, SubjectFor(role), TeamOwnership.SomeoneElses);
            Assert.True(verdict.Outcome == TeamGateOutcome.Refused, $"{method} {pattern}: {role} reached another person's session.");
        }
        _output.WriteLine($"{role}: {sessionsAndTranscripts.Length} session and transcript endpoint-methods, another person's refused on every one.");
    }

    [Fact]
    public void Issue2302Test4_AManager_AnotherPersonsPrompts_IsRefusedOnEveryPromptEndpoint()
    {
        var prompts = _routes
            .Where(r => TeamEndpointRules.Find(r.Method, r.Pattern)?.OthersAction == TeamAction.ReadAnotherPersonsPrompts)
            .ToArray();
        Assert.Contains(prompts, r => r.Pattern == "/prompts" && r.Method == "GET");

        foreach (var (method, pattern) in prompts)
        {
            var verdict = Ask(method, pattern, _manager, TeamOwnership.SomeoneElses);
            Assert.True(verdict.Outcome == TeamGateOutcome.Refused && verdict.Action == TeamAction.ReadAnotherPersonsPrompts,
                $"{method} {pattern}: a Manager reached another person's prompts.");
        }

        // The single exception is a named action, granted to a Manager, for the Mentor page (devthrottle_internal#2305).
        Assert.True(_gateway.TeamAccess.Decide(_team, _manager, TeamAction.ReadPromptsQuotedOnMentorPage).Allowed);
    }

    [Fact]
    public void NotAMember_EveryEndpoint_IsRefused()
    {
        foreach (var (method, pattern) in _routes)
        {
            var verdict = Ask(method, pattern, _stranger, TeamOwnership.Callers);
            Assert.True(verdict.Outcome is TeamGateOutcome.Refused or TeamGateOutcome.NoSuchTeam,
                $"{method} {pattern} let someone who is not a member through ({verdict.Outcome}).");
        }
    }

    [Fact]
    public void Gate_IsTheSameOneTheHostInstalled_OverTheSameRegistry()
    {
        Assert.NotNull(_gateway.TeamGate);
        Assert.True(_gateway.TeamRegistry.IsTeam(new TenantId(_team)));
        Assert.Equal(TeamRole.Developer, _gateway.TeamAccess.Decide(_team, _developer, TeamAction.SeeMembersAndRoles).Role);
    }

    // ---- Over the wire --------------------------------------------------------------------------------------------

    private string Enroll(string deviceId, string subject)
    {
        var tenant = _gateway.TenantRegistry.LookupBySubject(subject)!.Value;
        var key = _gateway.Devices.Register(deviceId, "M-" + deviceId).DeviceKey;
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public async Task OverTheWire_EveryRole_SeesTheMembersAndRoles(TeamRole role)
    {
        var key = Enroll("dev-walk-" + role, SubjectFor(role));

        var (status, body) = await Get($"teams/{_team}/members", key);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4, body.GetProperty("count").GetInt32());
        Assert.Equal(TeamRoles.Label(role), body.GetProperty("team").GetProperty("role").GetString());
    }

    [Fact]
    public async Task OverTheWire_SomeoneWhoIsNotAMember_IsToldThereIsNoSuchTeam()
    {
        var key = Enroll("dev-walk-stranger", _stranger);
        var (status, body) = await Get($"teams/{_team}/members", key);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(Api.TeamEndpoints.NoSuchTeamRefusal, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OverTheWire_AKeyBoundToATeamsTenant_IsNotAcceptedToday_SoNoRequestYetRunsInsideATeam()
    {
        // The binding devthrottle_internal#2311 will make for a Director set up for a team. Today's hosted device
        // registry refuses a key whose account does not own the tenant it is bound to, so such a key never
        // authenticates - and if #2311 lets it, the gate is what it meets next (TeamEndpointGateTests shows what the
        // gate does with it).
        var key = _gateway.Devices.Register("dev-walk-team-bound", "M-team").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-walk-team-bound", _developer, _team);

        var (status, _) = await Get("gateway/skills", key);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ---- The Collaborator's app (devthrottle_internal#2306) ---------------------------------------------------------

    /// <summary>
    /// Every Cockpit page a Collaborator cannot open, and the Gateway reads that page makes when it loads - read from
    /// the page's client in packages/client-core. Each must be a real endpoint on the hosted route table, so a renamed
    /// endpoint turns this red instead of leaving the proof about an address nothing serves.
    /// </summary>
    public static readonly (string Page, string[] Reads)[] PagesACollaboratorCannotOpen =
    {
        ("Fleet Manager", new[] { "/gateway/fleet-manager" }),
        ("Sessions", new[] { "/sessions" }),
        ("A session", new[] { "/sessions/{sid}/buffer", "/sessions/{sid}/turn-verdicts" }),
        ("Fleet Map", new[] { "/sessions", "/directors" }),
        ("History", new[] { "/history/report" }),
        ("Directors", new[] { "/directors" }),
        ("Schedule", new[] { "/cron/jobs" }),
        ("Workflows", new[] { "/gateway/workflows" }),
        ("Skills", new[] { "/gateway/skills" }),
        ("Dictionary", new[] { "/ingest/dictionary" }),
        ("Voice Recorder", new[] { "/ingest/recordings" }),
        ("Transcription", new[] { "/voice-quality/summary" }),
        ("Your Throttle", new[] { "/stats/data" }),
        ("Settings", new[] { "/gateway/settings", "/gateway/mentor-report" }),
        ("Account", new[] { "/account/status", "/account/devices" }),
        ("About", new[] { "/gateway/about" }),
    };

    [Fact]
    public void Issue2306_ACollaboratorInTheTeam_TheDataBehindEveryPageTheyCannotOpen_IsRefused()
    {
        var asked = 0;
        foreach (var (page, reads) in PagesACollaboratorCannotOpen)
        {
            foreach (var pattern in reads)
            {
                Assert.True(_routes.Contains(("GET", pattern)), $"{page}: GET {pattern} is not an endpoint on the hosted route table.");
                foreach (var whose in new[] { TeamOwnership.Callers, TeamOwnership.SomeoneElses })
                {
                    var verdict = Ask("GET", pattern, _collaborator, whose);
                    _output.WriteLine($"{page}: GET {pattern} ({whose}) -> {verdict.Outcome}: {verdict.Message}");
                    Assert.True(verdict.Outcome == TeamGateOutcome.Refused,
                        $"{page}: GET {pattern} ({whose}) was {verdict.Outcome} for a Collaborator in the team.");
                    asked++;
                }
            }
        }
        _output.WriteLine($"{asked} Collaborator reads behind {PagesACollaboratorCannotOpen.Length} pages, every one refused.");
    }

    [Fact]
    public async Task Issue2306_OverTheWire_AKeyBoundToTheTeam_GetsNoDataBehindAnyPageACollaboratorCannotOpen()
    {
        // A key bound to the team's tenant, held by its Collaborator: what a request from inside the team would carry.
        // Today the hosted device registry does not accept it at all (see the test above it), so every read is
        // refused before it reaches the gate; the gate's own answer for each read is the test before this one.
        var key = _gateway.Devices.Register("dev-walk-collab-team-bound", "M-collab").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-walk-collab-team-bound", _collaborator, _team);

        foreach (var pattern in PagesACollaboratorCannotOpen.SelectMany(p => p.Reads).Distinct())
        {
            var path = pattern.Replace("{sid}", "00000000-0000-0000-0000-000000000001", StringComparison.Ordinal).TrimStart('/');
            var (status, _) = await Get(path, key);
            _output.WriteLine($"GET /{path} with a team-bound Collaborator key -> {(int)status}");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
    }

    [Theory]
    [InlineData(TeamRole.Owner, true)]
    [InlineData(TeamRole.Manager, true)]
    [InlineData(TeamRole.Developer, true)]
    [InlineData(TeamRole.Collaborator, false)]
    public async Task Issue2306_OverTheWire_TheTeamListCarriesEachRolesPageVerdict(TeamRole role, bool fullApp)
    {
        var key = Enroll("dev-walk-app-" + role, SubjectFor(role));

        var (status, body) = await Get("teams", key);

        Assert.Equal(HttpStatusCode.OK, status);
        var team = body.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetString() == _team);
        var app = team.GetProperty("app");
        Assert.Equal(fullApp, app.GetProperty("full").GetBoolean());
        Assert.Equal(new[] { "/questions", "/requests", "/reports" },
            app.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("path").GetString()));
        if (fullApp)
        {
            Assert.Equal(JsonValueKind.Null, app.GetProperty("elsewhere").ValueKind);
        }
        else
        {
            Assert.Equal("/questions", app.GetProperty("landing").GetString());
            Assert.Equal("This page is not available to Collaborators.", app.GetProperty("elsewhere").GetString());
        }
    }

    [Theory]
    [InlineData("questions")]
    [InlineData("requests")]
    [InlineData("reports")]
    public async Task Issue2306_OverTheWire_EachTeamPageAddress_IsServedTheCockpit(string page)
    {
        var key = Enroll("dev-walk-page-" + page, _collaborator);
        using var req = new HttpRequestMessage(HttpMethod.Get, page);
        req.Headers.Accept.ParseAdd("text/html");
        req.Headers.Add("Cookie", $"cc-gateway-token={key}");

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        // The Cockpit answered: the page when it is built into this host, or its own "not built" answer in a test host
        // that has none - never a Gateway endpoint's JSON under the same address.
        _output.WriteLine($"GET /{page} (text/html) -> {(int)resp.StatusCode}");
        Assert.True(resp.StatusCode == HttpStatusCode.OK
                ? text.Contains("<html", StringComparison.OrdinalIgnoreCase)
                : resp.StatusCode == HttpStatusCode.NotFound && text.Contains("React Cockpit not built", StringComparison.Ordinal),
            $"/{page} was answered by something other than the Cockpit: {(int)resp.StatusCode} {text}");
    }

    /// <summary>
    /// The team's own routes - the team in the address, the person's own key - DO run inside a team today, so for them
    /// the Collaborator's refusal is proven over real HTTP: every /teams/{teamId}/... endpoint whose action the role
    /// table does not give a Collaborator is sent through the real pipeline with a Collaborator's own key, and the
    /// gate's 403 must come back. Today that is the invitation writes behind the invite page (/team/{teamId}/invite),
    /// which the Cockpit shows a Collaborator as "not available".
    /// </summary>
    [Fact]
    public async Task Issue2306_OverTheWire_ACollaborator_EveryTeamRouteTheTableRefusesThem_Is403()
    {
        var key = Enroll("dev-walk-collab-routes", _collaborator);
        var refusedForCollaborator = _routes
            .Where(r => r.Pattern.StartsWith("/teams/{teamId}/", StringComparison.Ordinal))
            .Where(r => TeamEndpointRules.Find(r.Method, r.Pattern) is not { } rule
                        || TeamPermissions.Grant(TeamRole.Collaborator, rule.Action) == TeamGrant.No)
            .ToArray();
        Assert.NotEmpty(refusedForCollaborator);

        foreach (var (method, pattern) in refusedForCollaborator)
        {
            var path = pattern.Replace("{teamId}", _team, StringComparison.Ordinal)
                .Replace("{invitationId}", Guid.NewGuid().ToString("N"), StringComparison.Ordinal)
                .TrimStart('/');
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (method != "GET")
                req.Content = new StringContent("{\"email\":\"someone@example.org\",\"role\":\"Collaborator\"}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            _output.WriteLine($"{method} {pattern} as the team's Collaborator -> {(int)resp.StatusCode} {text}");

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            var body = JsonDocument.Parse(text).RootElement;
            Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        }
    }
}
