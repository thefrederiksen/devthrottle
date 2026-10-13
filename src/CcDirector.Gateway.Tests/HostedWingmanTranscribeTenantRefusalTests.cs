using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE MAPPED ROUTE, DRIVEN. <c>POST /wingman/transcribe</c> on a hosted Gateway must REFUSE a caller whose
/// tenant does not resolve, never serve it - and this class is the only thing in the repository that drives
/// that route at all.
///
/// WHY THIS EXISTS RATHER THAN ANOTHER SERVICE-LEVEL TEST. The route used to pass the nullable result of
/// <c>GatewayEndpoints.ResolveReadTenant</c> straight into
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/>. That resolver's own documentation says null is a
/// REFUSAL; the service's contract says null means the self-host single tenant. Handing one to the other
/// turned the refusal into <see cref="TenantId.Local"/> three layers down, with three consequences on a
/// hosted request - the SHARED FLAT GLOSSARY biased the caller's words, the shared history took their turn,
/// and their transcript evidence landed in the Local partition.
///
/// Every existing test of that behaviour constructs <see cref="GatewayTranscriptionService"/> DIRECTLY and
/// never maps the route, so all of them passed with this route wide open. A guard written the same way would
/// be worth exactly as much. So the hosted leg here maps the REAL endpoint at the REAL path and posts real
/// multipart audio at it.
///
/// HOW THE UNRESOLVABLE TENANT IS PRODUCED, STATED EXACTLY, BECAUSE IT IS NOT THE OBVIOUS ONE. The route
/// resolves its tenant through the host's tenant boundary. On hosted the boundary answers null for an
/// authenticated identity that carries no tenant. That identity is NOT reachable through a hosted
/// <see cref="GatewayHost"/>'s front door: on hosted, the auth gate classifies a device row with no tenant
/// binding as an invalid credential and answers 401 before any route runs (that is what
/// <see cref="HostedContentReadDenyTests.Every_family_is_refused_to_a_caller_carrying_no_tenant_at_all"/>
/// pins). So the hosted leg stands in for the auth layer, exactly as <see cref="OmittedTenantBoundaryFailClosedTests"/>
/// does: a probe host maps the endpoint with <c>hosted: true</c> and a hosted boundary, and one middleware
/// stamps every request with an authenticated device identity that has NO tenant. The guard then has to
/// answer, and it is the only thing between that request and the transcription owner.
///
/// (An earlier revision reached the same null another way: it built a GatewayHost while the process-wide
/// hosted variable was off, so the host's boundary was the self-host one, and switched the variable on before
/// the request, so the resolver - which then read the variable live - refused without consulting the boundary.
/// The hosted mode is now a value fixed when the host is constructed, and the resolver asks the boundary,
/// so that state cannot be built any more. That is the point of the change, not a loss to this test.)
///
/// WHAT THIS PROVES AND WHAT IT DOES NOT. It proves that when the route's tenant resolution answers null for
/// an authenticated hosted caller, the route refuses and NOTHING downstream runs. It does not claim the
/// unbound-device-key path into that null is reachable on the production hosted Gateway - it is not, today,
/// because authentication refuses it first. The guard is the contract's own floor, and the self-host control
/// is what makes the floor load bearing.
///
/// THE STORE RECEIPT IS THE POINT, NOT THE STATUS CODE. A vault key IS configured here on purpose. Without
/// one the route answers 503 at its own key check before ever reaching the transcription owner, and every
/// "nothing was written" assertion below would be green whether the guard existed or not - decorative, not
/// load bearing. With the key set, removing the guard drives the request all the way into
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/>, and the shared transcription history takes a
/// record. That receipt changes colour when the guard is removed, which is the only thing that makes it
/// evidence rather than decoration.
///
/// WHICH ASSERTIONS ARE PROVEN ABLE TO FAIL, AND WHICH ARE NOT. Stated because the difference is the whole
/// value of the file:
///   PROVEN able to fail - the refusal body and status. Watched going red with the guard removed, on the probe
///   host, when this revision was written.
///   NOT proven able to fail - the SHARED/LOCAL TRANSCRIPTION HISTORY being empty, and the flat glossary's
///   terms being absent from the answer. The history receipt was watched red on an earlier revision that drove
///   a whole GatewayHost; on the probe host the request reaches the service but the provider call behind it
///   is a real one with a fake key, so what the history records after the guard is removed is the provider's
///   answer, not something this test controls. The glossary is only read on the success path, which no test
///   can produce here. Both are asserted because they are the right things to assert and they are cheap - but
///   this file does NOT claim to have watched them redden on this harness. What IS established is upstream of
///   both: the service is never entered.
///   DELIBERATELY ABSENT - "the clip was not archived". <see cref="TranscriptionAudioArchive.TrySave"/>
///   skips every write on hosted by its own gate, so that assertion could never fail in either direction. An
///   assertion that cannot fail reads as proof and is not; it was removed rather than left in. The transcript
///   store's Local row count is absent too: the probe host carries no transcript store, and on the earlier
///   whole-host revision that count was never proven able to fail either.
///
/// REVERT-PROOF RECIPE (run it; do not take the assertion on trust):
///   1. In <c>GatewayWingmanVoiceEndpoint.cs</c>, delete the <c>reqTenant is null</c> refusal at the top of
///      the <c>/wingman/transcribe</c> handler.
///   2. Run this class. The hosted leg goes RED on the refusal - the answer becomes the transcription
///      service's own provider error. The self-host control stays GREEN.
///   3. Restore. Green again.
/// Note that step 2 makes ONE outbound provider call with a fake key, for the reason given above. The
/// committed, guarded test makes no network call at all - it is refused before the service is entered.
///
/// The self-host CONTROL runs on a real self-host <see cref="GatewayHost"/>, authenticated with a real device
/// key through the real host-wide auth gate: the same request must get PAST the guard there. It joins the
/// DirectorRoot collection because it sets the process-wide storage root.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedWingmanTranscribeTenantRefusalTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The exact refusal every sibling route on this surface returns for an unresolved tenant.</summary>
    private const string Refusal = "no tenant is bound to this request";

    /// <summary>A term only the SHARED FLAT glossary carries, so its presence anywhere would name that file.</summary>
    private const string FlatGlossaryTerm = "zqxjvflatglossary";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string _vaultPath = "";

    /// <summary>The clip posted at the route - real bytes in a real multipart part, so the request is the
    /// shape the phone actually sends and not a body the handler rejects before the guard is reached.</summary>
    private readonly byte[] _clip = Encoding.UTF8.GetBytes("zqxjv-clip-" + Guid.NewGuid().ToString("N"));

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-transcribe-refusal-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;

    public HostedWingmanTranscribeTenantRefusalTests()
    {
        // The isolated storage root. Everything the refused request could have written - the glossary it
        // would have read, the audio archive, the transcription history - resolves under here, so a write
        // that should not happen is observable rather than lost in the developer's own folders.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-transcribe-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // A key for the configured transcription mode, so the route's own no-key check cannot be what
        // answers. Without this the receipts below prove nothing - see the class comment.
        _vaultPath = Path.Combine(_root, "keyvault.json");
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_notarealkey");

        // The SHARED FLAT glossary - the Local tenant's file, the one a null tenant reads. Seeded so it
        // exists and carries a term nothing else in this test could produce.
        var flat = GatewayTranscriptionService.DictionaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(flat)!);
        File.WriteAllText(flat, $"common_mistranscriptions:\n  {FlatGlossaryTerm}: [somethingelse]\n");

        // The self-host control's host: self-host STATED, so the real device key below authenticates and
        // resolves to Local through the real boundary.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;

        // PRECONDITION, asserted rather than assumed: nothing has been written yet, so a non-empty receipt
        // after a request can only have come from the request.
        Assert.Empty(HistoryFiles());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Hosted_transcribe_with_no_resolvable_tenant_is_refused_and_reaches_nothing()
    {
        await using var probe = await HostedProbe.StartAsync(_root, _vaultPath);
        var resp = await PostClipAsync(probe.Http, authorization: null);

        // 1. THE REFUSAL, body first. The status is asserted after the fingerprint deliberately: a status-first
        //    assertion would redden on "404 is not 403" if the route were renamed, which proves a route moved
        //    rather than proving this test can tell the guard's answer from a masking route's.
        var root = await ContentFingerprint.AsJsonObjectAsync(resp, "POST wingman/transcribe");
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Refusal, ContentFingerprint.Text(root, "error", "POST wingman/transcribe"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // 2. THE TRANSCRIPTION OWNER WAS NEVER ENTERED. RecordHistory runs on EVERY TranscribeAsync outcome,
        //    including a failed provider call, and with a null tenant it selects the shared history - which is
        //    the Local directory under this test's root. So an empty history is the receipt that the service
        //    was not reached at all, and therefore that the glossary read below could not have happened either.
        Assert.Empty(HistoryFiles());

        // 3. THE SHARED FLAT GLOSSARY WAS NOT APPLIED. A refusal carries no transcript at all, so there is no
        //    text for the flat file's terms to have altered - and the file is still exactly as seeded, so
        //    nothing rewrote it. Receipt 2 is what actually rules the read out, by proving the code that
        //    performs it never ran.
        Assert.DoesNotContain(FlatGlossaryTerm, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(FlatGlossaryTerm, File.ReadAllText(GatewayTranscriptionService.DictionaryPath()));
    }

    /// <summary>
    /// THE SELF-HOST CONTROL, and it is not optional: a guard that refused everybody would satisfy the test
    /// above perfectly. On a self-host host the boundary answers <see cref="TenantId.Local"/> for every
    /// authenticated caller, so the SAME request on a self-host host must get past the guard.
    ///
    /// It is proved by the answer that comes from FURTHER IN than the guard - the route's own bad-body
    /// message, which nothing else in the Gateway emits and which is reached only after the tenant check.
    /// A body-shape refusal is used rather than a full transcription because completing one would mean a
    /// real provider call: this route builds its own transcription service, so there is no client to stub.
    /// </summary>
    [Fact]
    public async Task Self_host_transcribe_is_not_refused_by_the_tenant_guard()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "wingman/transcribe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);

        var root = await ContentFingerprint.AsJsonObjectAsync(resp, "POST wingman/transcribe (self-host)");
        var error = ContentFingerprint.Text(root, "error", "POST wingman/transcribe (self-host)");
        Assert.Equal("send the recording as multipart form-data with an 'audio' file", error);
        Assert.NotEqual(Refusal, error);
    }

    private Task<HttpResponseMessage> PostClipAsync(HttpClient http, string? authorization)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(_clip);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "audio", "clip.webm");

        var req = new HttpRequestMessage(HttpMethod.Post, "wingman/transcribe")
        {
            Content = form,
        };
        if (authorization is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization);
        return http.SendAsync(req);
    }

    /// <summary>Every file the shared (Local) transcription history holds. Empty is the receipt.</summary>
    private static string[] HistoryFiles()
    {
        var dir = TranscriptionHistoryLog.DefaultDirectory();
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    /// <summary>
    /// A minimal HOSTED host carrying only the wingman voice endpoint, mapped with <c>hosted: true</c> and a
    /// hosted boundary over the ambient tenant context - the production wiring - behind a stand-in for the auth
    /// layer that stamps every request with an authenticated device identity carrying NO tenant. That is the
    /// one caller the real auth gate never lets through, and the one the handler's guard exists to answer.
    /// </summary>
    private sealed class HostedProbe : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Http { get; }

        private HostedProbe(WebApplication app, HttpClient http)
        {
            _app = app;
            Http = http;
        }

        internal static async Task<HostedProbe> StartAsync(string root, string vaultPath)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");

            app.Use(async (ctx, next) =>
            {
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
                    "dev-unbound", TenantId: null, DeviceRegistry.DefaultDeviceType, DeviceRegistry.StatusActive);
                await next();
            });

            var vault = new KeyVault(vaultPath);
            var settingsData = new GatewayDbTestHarness();
            app.Lifetime.ApplicationStopped.Register(settingsData.Dispose);
            var tenantSettings = new TenantSettingsResolver(new TenantSettingsStore(settingsData.Open()));
            Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> noBrain =
                (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by a refused transcribe");
            var voice = new WingmanVoiceService(noBrain, vault, tenantSettings, hosted: true, CcDirector.Gateway.Tenancy.TenantAiGate.SelfHost,
                Path.Combine(root, "probe-voice-sessions.json"));

            GatewayWingmanVoiceEndpoint.Map(
                app, hosted: true,
                new DirectorRegistry(Path.Combine(root, "probe-directors")),
                noBrain,
                vault,
                voice,
                tenantSettings,
                new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true), aiGate: CcDirector.Gateway.Tenancy.TenantAiGate.SelfHost);

            await app.StartAsync();
            return new HostedProbe(app, new HttpClient
            {
                BaseAddress = new Uri(app.Urls.First()),
                Timeout = TimeSpan.FromSeconds(30),
            });
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
