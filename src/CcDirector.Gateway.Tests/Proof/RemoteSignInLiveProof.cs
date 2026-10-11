using System.Net;
using System.Net.Sockets;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tests.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Proof;

/// <summary>
/// Issue #1080 LIVE PROOF: boots the REAL <see cref="GatewayHost"/> (auth gate ON) on loopback and drives
/// the remote-capable cloud sign-in end to end THROUGH the real request pipeline (forwarded headers, auth
/// middleware, the access logger, the real endpoints), simulating a Tailscale Serve front door with
/// X-Forwarded-* headers from the trusted loopback proxy. It proves every acceptance criterion and writes a
/// human-readable HTTP transcript + the captured gateway log to the proof directory (env
/// <c>CC1080_PROOF_DIR</c>, or the temp directory when unset), which the committed HTML report embeds.
///
/// Only the REMOTE path is exercised live (it opens no browser); the same-machine host-local branch is proven
/// by the unit tests, since triggering it would open a real browser on the host.
/// </summary>
public sealed class RemoteSignInLiveProof
{
    private const string FrontDoorHost = "gw.example-tailnet.ts.net";
    private const string TailnetClientIp = "100.86.144.11";
    private const string GatewayToken = "proof-token-1080";

    [Fact]
    public async Task Remote_browser_completes_sign_in_through_the_front_door_and_no_host_browser_opens()
    {
        var transcript = new StringBuilder();
        transcript.AppendLine("Issue #1080 - Remote-capable cloud sign-in: complete sign-in in the user's own browser");
        transcript.AppendLine("Live proof against the REAL GatewayHost (auth gate ON), loopback Kestrel.");
        transcript.AppendLine("A remote browser is simulated by X-Forwarded-For/Host/Proto from the trusted loopback proxy");
        transcript.AppendLine("(exactly how a Tailscale Serve front door presents a tailnet client to the Gateway).");
        transcript.AppendLine($"Date: {DateTime.UtcNow:O}");
        transcript.AppendLine();

        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-proof-1080-" + Guid.NewGuid().ToString("N"));

        // The access/refresh pair the cloud sign-in completion hands back. The access token is a real
        // Gateway-signed JWT so /account/status validates it locally after the callback stores it.
        var accessJwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "owner@example.com", "google");
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-1080-PROOF";

        var previousSecret = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);

        using var logScope = FileLog.RedirectForTests();

        GatewayHost gateway;
        string signedInLandingHtml;
        string outboundSignInUrl;
        int statusCode;
        try
        {
            var account = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);

            gateway = new GatewayHost(
                port: GatewayHost.OperatingSystemAssignedPort,
                token: GatewayToken,
                authEnabled: true,
                instancesDirectory: instancesDir,
                workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
                account: account);
            await gateway.StartAsync();

            var baseUri = new Uri($"http://127.0.0.1:{gateway.Port}/");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = baseUri };

            // ---- Step A: remote browser hits the public sign-in START front door. Expect a 302 redirect to
            // the cloud sign-in page carrying the Gateway's REACHABLE front-door callback as redirect_uri. ----
            using (var startReq = new HttpRequestMessage(HttpMethod.Post, AccountSignInStartEndpoint.Path))
            {
                startReq.Headers.Add("X-Forwarded-For", TailnetClientIp);
                startReq.Headers.Add("X-Forwarded-Host", FrontDoorHost);
                startReq.Headers.Add("X-Forwarded-Proto", "https");
                using var startResp = await http.SendAsync(startReq);

                statusCode = (int)startResp.StatusCode;
                outboundSignInUrl = startResp.Headers.Location?.ToString() ?? "";

                transcript.AppendLine("=== A) POST /account/sign-in-start  (remote browser: X-Forwarded-For=" + TailnetClientIp + ", Host=" + FrontDoorHost + ", Proto=https; NO Gateway token) ===");
                transcript.AppendLine($"HTTP {statusCode} {startResp.StatusCode}");
                transcript.AppendLine($"Location: {outboundSignInUrl}");
                transcript.AppendLine("[AC 1] the remote browser is redirected to the cloud sign-in page (not handed a 'look at the host' page)");
                transcript.AppendLine("[AC 3] the redirect_uri is the reachable front-door callback on " + FrontDoorHost + " over https, NOT a loopback URL");
                transcript.AppendLine("[AC 2] no host browser is launched - the response is a redirect, the host-local loopback flow is never entered");
                transcript.AppendLine();

                Assert.Equal((int)HttpStatusCode.Found, statusCode);
                Assert.StartsWith(FirstRunLoginCoordinator.ResolveSignInBaseUrl(), outboundSignInUrl, StringComparison.Ordinal);
                var expectedCallback = Uri.EscapeDataString($"https://{FrontDoorHost}{RemoteSignInRouting.CallbackPath}");
                Assert.Contains($"redirect_uri={expectedCallback}", outboundSignInUrl, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", outboundSignInUrl, StringComparison.Ordinal);
            }

            // ---- Step B: the cloud sign-in completion redirects the user's browser back to the Gateway
            // front-door callback with the token pair. Expect a signed-in landing page. ----
            var callbackUrl = $"{RemoteSignInRouting.CallbackPath}?access_token={Uri.EscapeDataString(accessJwt)}&refresh_token={Uri.EscapeDataString(refreshToken)}";
            using (var cbResp = await http.GetAsync(callbackUrl))
            {
                signedInLandingHtml = await cbResp.Content.ReadAsStringAsync();
                transcript.AppendLine("=== B) GET /account/sign-in-callback?access_token=<jwt>&refresh_token=<...>  (public front-door callback; the cloud completion's redirect back) ===");
                transcript.AppendLine($"HTTP {(int)cbResp.StatusCode} {cbResp.StatusCode}");
                transcript.AppendLine("(body is the signed-in landing page; it echoes NO token)");
                transcript.AppendLine();

                Assert.Equal(HttpStatusCode.OK, cbResp.StatusCode);
                Assert.Contains("signed in", signedInLandingHtml, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(accessJwt, signedInLandingHtml, StringComparison.Ordinal);
                Assert.DoesNotContain(refreshToken, signedInLandingHtml, StringComparison.Ordinal);
            }

            // ---- Step C: after the remote sign-in, GET /account/status reports signed-in. ----
            using (var statusReq = new HttpRequestMessage(HttpMethod.Get, "/account/status"))
            {
                statusReq.Headers.Add("Authorization", $"Bearer {GatewayToken}");
                using var statusResp = await http.SendAsync(statusReq);
                var statusBody = await statusResp.Content.ReadAsStringAsync();

                transcript.AppendLine("=== C) GET /account/status  (Bearer Gateway token) ===");
                transcript.AppendLine($"HTTP {(int)statusResp.StatusCode} {statusResp.StatusCode}");
                transcript.AppendLine($"Body: {statusBody}");
                transcript.AppendLine("[AC 4] the Gateway reports signed-in after the remote sign-in");
                transcript.AppendLine();

                Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
                Assert.Contains("\"signedIn\":true", statusBody, StringComparison.Ordinal);
                Assert.DoesNotContain(accessJwt, statusBody, StringComparison.Ordinal);
            }

            await gateway.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previousSecret);
            try { if (Directory.Exists(instancesDir)) Directory.Delete(instancesDir, true); }
            catch { /* best-effort temp cleanup */ }
        }

        // ---- AC 2 (log) + AC 3 (log) + AC 6: inspect the captured gateway log. ----
        var logLines = logScope.DrainAndReadLines();
        var gatewayLog = string.Join(Environment.NewLine, logLines);

        // AC 6 (DT-05): neither token appears ANYWHERE in the gateway log.
        Assert.DoesNotContain(accessJwt, gatewayLog, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, gatewayLog, StringComparison.Ordinal);
        // AC 6: the callback's access-log line redacted the credential-bearing query.
        Assert.Contains("[redacted: sign-in callback credential, DT-05]", gatewayLog, StringComparison.Ordinal);
        // AC 3 (log): the token-free outbound sign-in URL (front-door callback) was logged.
        Assert.Contains("remote sign-in -> redirecting the browser to the cloud sign-in page", gatewayLog, StringComparison.Ordinal);
        // AC 2 (log): the host-local loopback flow was NEVER entered for the remote request.
        Assert.DoesNotContain("starting the host-local browser loopback flow", gatewayLog, StringComparison.Ordinal);

        // Trim the captured log to the sign-in-relevant lines for the transcript.
        var relevant = logLines.Where(l =>
            l.Contains("AccountSignInStartEndpoint", StringComparison.Ordinal)
            || l.Contains("AccountSignInCallbackEndpoint", StringComparison.Ordinal)
            || l.Contains("GatewaySignInService", StringComparison.Ordinal)
            || l.Contains("sign-in-callback", StringComparison.Ordinal)
            || l.Contains("sign-in-start", StringComparison.Ordinal)
            || l.Contains("/account/status", StringComparison.Ordinal));
        transcript.AppendLine("=== Gateway log (sign-in relevant lines) - NO credential material, callback query redacted [AC 6, AC 2, AC 3] ===");
        foreach (var l in relevant)
            transcript.AppendLine(l);

        // Write the artifacts. CC1080_PROOF_DIR names the PARENT; this run gets its own subdirectory beneath
        // it, so two concurrent runs cannot overwrite each other's evidence (issue #1156).
        var outDir = ProofOutputDirectory.ResolveOrNull("CC1080_PROOF_DIR");
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            File.WriteAllText(Path.Combine(outDir, "gateway-http-transcript.txt"), transcript.ToString());
            File.WriteAllText(Path.Combine(outDir, "signed-in-landing.html"), signedInLandingHtml);
        }
    }

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

}
