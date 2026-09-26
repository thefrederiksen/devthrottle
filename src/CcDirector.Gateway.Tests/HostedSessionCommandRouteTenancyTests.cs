using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1869: on hosted you could SEE sessions and do NOTHING with them.
///
/// The read path was made tenant-aware and resolves the request's tenant from the authenticated device key.
/// The COMMAND path was not: twenty-three per-session routes passed a literal <c>TenantId.Local</c> into the
/// session locator. On hosted, where a request's tenant is a real account, that read the empty Local
/// partition, so prompt, interrupt, escape, buffer, summary, git, wingman, role, hold and delete all answered
/// 404 "session not found across any director" for a correctly enrolled Director whose sessions the roster
/// was listing perfectly. Because /buffer was among them the terminal view was dead too.
///
/// It was INVISIBLE on self-host, because there the request's tenant genuinely IS Local, so every existing
/// test agreed with the bug. Only a real Director driven against the real hosted box found it. These tests
/// are the hosted case that was missing.
///
/// WHAT IS ASSERTED, and why it is asserted this way. Every route is checked in BOTH directions:
///   - the owner's own key must NOT get the locator's not-found body - the session was found;
///   - the OTHER tenant's key on the same session MUST get exactly that 404 - isolation still holds.
/// The second half is what stops this being a test that would pass if the fix had simply made every route
/// locate every session regardless of tenant. Matching the locator's exact error body, rather than a bare
/// status code, is deliberate: these routes can legitimately answer non-200 for reasons that have nothing to
/// do with tenancy (a verb the fake Director does not implement, a session in the wrong state), and a
/// status-code assertion would confuse those with the defect under test. The ONE exception since Voice
/// Delivery phase 5 is POST /prompt: a session that cannot be located now is HELD 202 "waiting-for-director"
/// for every caller (contract section 8), so for that row the cross-tenant proof is that the held answer is
/// the one B gets for an id no tenant ever had, and that A's Director receives no prompt verb at all.
///
/// Revert-prove: put a literal TenantId.Local back into any one route's locate call - or change
/// LocateSessionForRequestAsync to ignore the request and pass TenantId.Local - and that route's owner-side
/// assertion goes RED with the not-found body, while its cross-tenant assertion stays green.
///
/// This drives a REAL GatewayHost over REAL HTTP through the REAL auth middleware, with two REAL tunnel
/// Directors on two different tenants - the same wire path a production Director uses.
/// </summary>
public sealed class HostedSessionCommandRouteTenancyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "sess-a";
    private const string SessB = "sess-b";

    /// <summary>
    /// The signature of "the locator found nothing". Issue #2188 replaced the old prose body
    /// ("session not found across any director") with a plain sentence plus a MACHINE-READABLE code, and this
    /// anchors on the code rather than the prose: the sentence is user-facing copy and may be reworded, the
    /// code is the contract. Anchoring on the prose is also what made the negative assertions below fragile -
    /// reword the sentence and a "does not contain" check passes for the wrong reason.
    /// </summary>
    private const string NotLocated = "session_not_found";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;
    private string _keyA = "";
    private string _keyB = "";

    /// <summary>Every command tenant A's tunnel Director received in this test case, so the cross-tenant prompt row can
    /// prove behaviourally that B's words never reached A's session (Voice Delivery phase 5).</summary>
    private readonly List<string> _verbsReceivedByA = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cmd-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private bool _cleanedUp;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        try
        {
            await InitializeHostedAsync();
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    private async Task InitializeHostedAsync()
    {
        await StartGatewayWithBindRetryAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB").DeviceKey;

        // Both fake Directors answer every verb, so a route that reaches its Director gets a real answer and
        // any not-found body can only have come from the locate step - the step under test.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA", dispatch: RecordAThenAnswerAnything);
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB", dispatch: AnswerAnything);
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    private async Task StartGatewayWithBindRetryAsync()
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
                instancesDirectory: _instancesDir,
                workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
                snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
                streamMode: true);
            try
            {
                await _gateway.StartAsync();
                return;
            }
            catch (Exception ex) when (attempt < maximumAttempts && IsAddressAlreadyInUse(ex))
            {
                await _gateway.StopAsync();
            }
        }
    }

    private static bool IsAddressAlreadyInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
        }

        return false;
    }

    private static DirectorCommandResult AnswerAnything(DirectorCommand cmd) =>
        FakeTunnelDirector.Ok(new { ok = true, lines = Array.Empty<string>(), items = Array.Empty<object>() });

    private DirectorCommandResult RecordAThenAnswerAnything(DirectorCommand cmd)
    {
        lock (_verbsReceivedByA) _verbsReceivedByA.Add(cmd.Verb);
        return AnswerAnything(cmd);
    }

    public Task DisposeAsync() => CleanupAsync();

    private async Task CleanupAsync()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;

        try
        {
            _http?.Dispose();
            if (_dirA is not null)
                await _dirA.DisposeAsync();
            if (_dirB is not null)
                await _dirB.DisposeAsync();
            if (_gateway is not null)
                await _gateway.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
            try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The per-session routes the live hosted run reported dead, as (method, path-after-the-session-id, body).
    /// /buffer leads because it is the terminal view: if that one is dead the feature is dead whatever else works.
    /// </summary>
    public static IEnumerable<object[]> SessionRoutes() => new List<object[]>
    {
        new object[] { "GET",    "/buffer",         null! },
        new object[] { "GET",    "/summary",        null! },
        new object[] { "GET",    "/git",            null! },
        new object[] { "GET",    "/wingman",        null! },
        new object[] { "GET",    "/handover",       null! },
        new object[] { "GET",    "/recap",          null! },
        new object[] { "POST",   "/interrupt",      "{}" },
        new object[] { "POST",   "/escape",         "{}" },
        new object[] { "POST",   "/prompt",         "{\"text\":\"hello\"}" },
        new object[] { "POST",   "/role",           "{\"role\":\"Worker\"}" },
        new object[] { "POST",   "/hold",           "{\"onHold\":true}" },
        new object[] { "POST",   "/transcribing",   "{\"transcribing\":true}" },
        new object[] { "POST",   "/wingman/ask",    "{\"question\":\"why\"}" },
        new object[] { "POST",   "/wingman/goal",   "{\"goal\":\"ship\"}" },
        new object[] { "POST",   "/recap",          "{}" },
        new object[] { "POST",   "/request-deletion", "{\"reason\":\"done\"}" },
        // Added after review PROVED the gap: the reviewer reverted PATCH /sessions/{sid} to a hardcoded
        // local tenant and all 35 tests still passed, so a partial conversion demonstrably COULD hide. These
        // are the remaining converted locate sites that the table did not reach.
        new object[] { "DELETE", "/request-deletion", null! },
        new object[] { "PATCH",  "",                "{\"name\":\"renamed\"}" },
        new object[] { "POST",   "/upload-image",   "{\"dataUrl\":\"data:image/png;base64,iVBORw0KGgo=\"}" },
        new object[] { "DELETE", "",                null! },
    };

    [Theory]
    [MemberData(nameof(SessionRoutes))]
    public async Task The_owning_tenant_can_reach_its_own_session(string method, string suffix, string? body)
    {
        // The defect, directly: on hosted this used to be the locator's 404 for EVERY one of these, because the
        // route asked the Local partition while the caller's tenant was a real account.
        var resp = await Send(method, $"sessions/{SessA}{suffix}", _keyA, body);
        var text = await resp.Content.ReadAsStringAsync();

        // Assert the POSITIVE fact, not merely the absence of a string: the route must not have refused this
        // as unlocatable at all. A bare "does not contain" would also hold if the body were empty, or if the
        // refusal were reworded - which is exactly how an absence-only assertion stops proving anything.
        Assert.False(resp.StatusCode == HttpStatusCode.NotFound,
            $"{method} /sessions/{{sid}}{suffix} could not locate its OWN tenant's session - " +
            $"status {(int)resp.StatusCode}, body: {text}");
        Assert.False(text.Contains(NotLocated, StringComparison.Ordinal),
            $"{method} /sessions/{{sid}}{suffix} could not locate its OWN tenant's session - " +
            $"status {(int)resp.StatusCode}, body: {text}");
    }

    [Theory]
    [MemberData(nameof(SessionRoutes))]
    public async Task Another_tenant_cannot_reach_it(string method, string suffix, string? body)
    {
        // The other direction, and the reason the test above is not satisfied by "locate everything". Tenant B
        // naming tenant A's session must still get the locator's not-found answer.
        //
        // THE PROMPT ROUTE SINCE VOICE DELIVERY PHASE 5 (contract section 8): a prompt to a session that cannot
        // be located now is HELD 202 "waiting-for-director", never 404 - for an owning tenant whose Director is
        // stale, and for this cross-tenant call exactly the same way, because the held record is written to the
        // CALLER'S partition and the driver can only ever locate the session there. The answer is the one B gets
        // for an id no tenant ever had, so it reveals nothing about A's session - and the proof is behavioural,
        // not just the status: A's tunnel Director receives no prompt verb at all.
        if (method == "POST" && suffix == "/prompt")
        {
            var resp = await Send(method, $"sessions/{SessA}{suffix}", _keyB, body);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.Contains("waiting-for-director", text, StringComparison.Ordinal);
            Assert.DoesNotContain(NotLocated, text, StringComparison.Ordinal);
            Assert.DoesNotContain("director_stale", text, StringComparison.Ordinal);

            // Tenant-blind: B naming A's session and B naming an id that never existed in any tenant get the
            // SAME held answer, so nothing about A's session can be read out of it.
            var ghost = await Send(method, $"sessions/no-session-any-tenant-had{suffix}", _keyB, body);
            var ghostText = await ghost.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Accepted, ghost.StatusCode);
            Assert.Contains("waiting-for-director", ghostText, StringComparison.Ordinal);

            // The isolation itself: A's Director was asked nothing for B's words.
            lock (_verbsReceivedByA) Assert.DoesNotContain("prompt", _verbsReceivedByA);
            return;
        }

        var respPlain = await Send(method, $"sessions/{SessA}{suffix}", _keyB, body);
        var textPlain = await respPlain.Content.ReadAsStringAsync();

        // Still a 404 carrying the not-found CODE - never the retryable 503 that issue #2188 introduced for a
        // stale Director. Tenant B must not be told "that machine is catching up, try again", because that
        // would confirm the session exists and invite retries against another tenant's id.
        Assert.Equal(HttpStatusCode.NotFound, respPlain.StatusCode);
        Assert.Contains(NotLocated, textPlain, StringComparison.Ordinal);
        Assert.DoesNotContain("director_stale", textPlain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_and_the_commands_agree()
    {
        // The shape of the bug in one assertion: the roster listing a session while the commands cannot find
        // it is precisely "you can see it and do nothing with it". They must now answer about the same world.
        var roster = await (await Send("GET", "sessions", _keyA, null)).Content.ReadAsStringAsync();
        Assert.Contains(SessA, roster, StringComparison.Ordinal);

        var bufferResp = await Send("GET", $"sessions/{SessA}/buffer", _keyA, null);
        var buffer = await bufferResp.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.NotFound, bufferResp.StatusCode);
        Assert.False(buffer.Contains(NotLocated, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handover_locates_its_source_session_in_the_requesting_tenant()
    {
        // Not reachable from the route table: /handover names its session in the BODY, not the path. It was
        // one of the six converted sites the first version of this proof did not cover.
        // CONTRACT CHANGE (issue #2188): this route used to answer with its own prose,
        // "source session not found across any director". It now goes through the one shared responder every
        // other per-session route uses, so the body carries the same machine-readable code as the rest -
        // which is what this now anchors on. The tenancy property under test is unchanged.
        var own = await Send("POST", "handover", _keyA, $"{{\"fromSessionId\":\"{SessA}\",\"toRepoPath\":\"/repo\"}}");
        Assert.NotEqual(HttpStatusCode.NotFound, own.StatusCode);
        Assert.DoesNotContain(NotLocated, await own.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var cross = await Send("POST", "handover", _keyB, $"{{\"fromSessionId\":\"{SessA}\",\"toRepoPath\":\"/repo\"}}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        var crossBody = await cross.Content.ReadAsStringAsync();
        Assert.Contains(NotLocated, crossBody, StringComparison.Ordinal);
        // Never the retryable "that machine is catching up" answer: telling tenant B to try again would
        // confirm tenant A's session exists and invite retries against another tenant's id.
        Assert.DoesNotContain("director_stale", crossBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handover_resolves_its_target_director_in_the_requesting_tenant()
    {
        // Review blocker: fixing the source locate made this route REACHABLE, and its target Director was
        // still resolved through a fleet-global lookup - so a caller could name another account's Director and
        // have its existence decide the answer. Activating a route onto a fleet-global lookup would have
        // opened a cross-tenant path in the act of closing one.
        //
        // Alice naming her OWN Director gets past the target lookup...
        var ownTarget = await Send("POST", "handover", _keyA,
            $"{{\"fromSessionId\":\"{SessA}\",\"toRepoPath\":\"/repo\",\"toDirectorId\":\"dir-a\"}}");
        Assert.DoesNotContain("target director not found", await ownTarget.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and naming BOB'S Director does not, even though that Director certainly exists in the fleet.
        // The positive control above is what makes this meaningful: the route can find a target, just not his.
        var crossTarget = await Send("POST", "handover", _keyA,
            $"{{\"fromSessionId\":\"{SessA}\",\"toRepoPath\":\"/repo\",\"toDirectorId\":\"dir-b\"}}");
        Assert.Equal(HttpStatusCode.NotFound, crossTarget.StatusCode);
        Assert.Contains("target director not found", await crossTarget.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fanout_locates_no_target_outside_the_requesting_tenant()
    {
        // The last two converted sites: /fanout locates each TARGET session and, separately, the SENDING
        // session named by fromSessionId. Neither is reachable from the route table.
        //
        // SCOPE, stated rather than implied: only the cross-tenant direction is asserted here. The owner-side
        // path cannot be driven to completion in this harness - once a target IS located, /fanout goes on to
        // send a broadcast prompt and waits on a reply the fake Director does not satisfy, so the request
        // hangs rather than answering. Asserting the isolation direction is honest; claiming an owner-side
        // fanout proof would not be. The owner-side conversion of these two sites rests on their being the
        // same single helper the twenty other routes prove, not on this test.
        //
        // Alice fanning out to BOB's session must locate NOTHING - and because nothing is located, no send is
        // attempted, so this returns promptly instead of hanging. That asymmetry is itself the evidence.
        var cross = await Send("POST", "fanout", _keyA,
            $"{{\"sessionIds\":[\"{SessB}\"],\"text\":\"hello\",\"fromSessionId\":\"{SessB}\"}}");
        var body = await cross.Content.ReadAsStringAsync();

        // The response ECHOES every requested session id back as a result row whether or not it was located,
        // so the id's presence proves nothing - the first version of this assertion got that wrong. What a
        // located target would put in the body is its OWNING DIRECTOR, and that is the leak signature.
        Assert.DoesNotContain("dir-b", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MB\"", body, StringComparison.Ordinal);

        // NO owner-side positive control here, and that is a stated limitation rather than an oversight. The
        // broadcast governor and the delivery wait dominate this route: with a sender the request blocks on a
        // reply the fake Director does not satisfy, and without one the governor's scope rules decide the
        // outcome before delivery is reached. Neither shape isolates the locate step. So this test asserts the
        // isolation direction only, and the owner-side conversion of fanout's two sites rests on their being
        // the same single helper the twenty routes in the theory above prove - not on this test.
    }

    private Task<HttpResponseMessage> Send(string method, string path, string deviceKey, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Idle",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
