using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// TTS fallback (mission Phase 2): when the cloud speech proxy quietly fails the primary voice provider
/// over to the backup, it sets the out-of-band <c>X-DevThrottle-TTS-Fallback</c> response header on the
/// SUCCESS. The Gateway must read that header, record it on the ready clip as a success-with-a-note, and
/// surface it through <see cref="WingmanVoiceService.ServedViaFallbackFor"/> for the VoiceDisplay fold -
/// WITHOUT ever treating it as an outage (no VoiceUnavailable state). The header's presence is the whole
/// signal; its value is never shown to a user.
/// </summary>
public sealed class WingmanVoiceFallbackTests : IDisposable
{
    private readonly GatewayDbTestHarness _settingsData = new();
    private TenantSettingsResolver? _settings;

    private TenantSettingsResolver Settings =>
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));

    public void Dispose() => _settingsData.Dispose();

    /// <summary>A speech upstream that returns 200 + audio bytes, optionally with the fallback header.
    /// The header VALUE is configurable so a test can prove the Gateway keys on the header's presence,
    /// never on its value (the cloud proxy sends a generic "1", never the provider name).</summary>
    private sealed class SpeechStub(byte[] audio, bool withFallbackHeader, string headerValue = "1") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            if (withFallbackHeader)
                resp.Headers.TryAddWithoutValidation("X-DevThrottle-TTS-Fallback", headerValue);
            return Task.FromResult(resp);
        }
    }

    private WingmanVoiceService ServiceWith(HttpMessageHandler handler, string persistPath)
    {
        var vault = new KeyVault(Path.Combine(Path.GetTempPath(), "wmvfb-" + Guid.NewGuid().ToString("N") + ".vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        // StoreSpokenAsync takes the spoken text directly, so the brain is never reached here.
        Func<TenantId, Core.Configuration.WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by a fallback header test");
        return Warmed(new WingmanVoiceService(brain, vault, Settings, persistPath, ttsHttpClient: new HttpClient(handler)));
    }

    // A unique SUBDIRECTORY per test, not a bare temp filename. The service derives its durable audio
    // cache dir from the persist path's DIRECTORY (".../voice-audio"), so tests that drop the persist file
    // straight in Path.GetTempPath() all share one TEMP/voice-audio - and a session id written by one test
    // is then loaded by another test's fresh service (LoadReadyAudio runs in the constructor), which breaks
    // them when classes run in parallel. An isolated directory keeps each test's cache to itself.
    private static string TempPersist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmvfb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "voice-sessions.json");
    }

    [Fact]
    public async Task Synthesis_WithFallbackHeader_MarksTheClipServedViaFallback_AndIsNotAnOutage()
    {
        // The generic marker "1" is what the cloud proxy now sends (never the provider name).
        var svc = ServiceWith(new SpeechStub(new byte[] { 1, 2, 3 }, withFallbackHeader: true, headerValue: "1"), TempPersist());

        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));                       // a fallback is a real, playable clip
        Assert.True(svc.ServedViaFallbackFor(TenantId.Local, "sid-1"));           // ...noted as backup-served
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));            // ...and NEVER an outage/unavailable state
    }

    // The Gateway must detect the fallback by the header's PRESENCE alone, never by its value. This
    // locks that contract: the notice fires for the generic marker the cloud sends today ("1"), for the
    // legacy value from before the value was scrubbed ("openai" - proving backward-compatibility across
    // the deploy window), and for any other opaque value. A regression that starts comparing the value
    // against a provider name would break exactly one of these and be caught.
    [Theory]
    [InlineData("1")]         // the generic marker the cloud sends now
    [InlineData("backup")]    // any other opaque value must work identically
    [InlineData("openai")]    // the legacy value: old cloud + new Gateway stays compatible
    public async Task Synthesis_FallbackDetection_IsByHeaderPresence_NotValue(string headerValue)
    {
        var svc = ServiceWith(new SpeechStub(new byte[] { 1, 2, 3 }, withFallbackHeader: true, headerValue), TempPersist());

        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.True(svc.ServedViaFallbackFor(TenantId.Local, "sid-1"));           // fires regardless of the header value
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    [Fact]
    public async Task Synthesis_WithoutFallbackHeader_IsANormalReadyClip_NoFallbackNote()
    {
        var svc = ServiceWith(new SpeechStub(new byte[] { 1, 2, 3 }, withFallbackHeader: false), TempPersist());

        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.False(svc.ServedViaFallbackFor(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void ServedViaFallback_SurvivesAGatewayRestart()
    {
        // The fallback fact is a property of the specific clip, persisted next to its audio so the notice
        // does not vanish on a gateway restart while that clip is still the current one.
        var persist = TempPersist();
        var first = ServiceWith(new SpeechStub(Array.Empty<byte>(), withFallbackHeader: false), persist);
        first.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 9, 9, 9 }, "audio/mpeg", servedViaFallback: true);
        Assert.True(first.ServedViaFallbackFor(TenantId.Local, "sid-1"));

        // A fresh instance from the SAME persist path reloads the durable cache.
        var reloaded = ServiceWith(new SpeechStub(Array.Empty<byte>(), withFallbackHeader: false), persist);
        Assert.True(reloaded.HasVoice(TenantId.Local, "sid-1"));
        Assert.True(reloaded.ServedViaFallbackFor(TenantId.Local, "sid-1"));
    }

    /// <summary>A speech upstream that STALLS on its first call (the primary goes silent - a
    /// TimeoutException, exactly what TtsSynthesis throws when its per-attempt deadline fires with no
    /// answer), then on every later call returns 200 + audio with the fallback header, recording the
    /// last request so a test can assert what headers the Gateway sent.</summary>
    private sealed class HangThenBackupStub : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            if (Calls == 1)
                // A silent primary: the request never gets an answer, which TtsSynthesis surfaces as a
                // TimeoutException. Returning a faulted task is how a stub reproduces that give-up.
                return Task.FromException<HttpResponseMessage>(new TimeoutException("primary went silent"));
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            resp.Headers.TryAddWithoutValidation("X-DevThrottle-TTS-Fallback", "1");   // the proxy served via the backup
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task PrimaryHang_ArmsBackupRoute_SoTheNextCallAsksTheProxyForTheBackup_AndIsServedByIt()
    {
        // Issue devthrottle_internal#405 (Option B). The cloud proxy's failover only reacts to an ERROR the primary returns;
        // a silent hang gives it nothing to react to, so the Gateway - which DID see the hang via its own
        // deadline - must route the session's next narration past the stalling primary by asking the proxy
        // for the backup (the X-DevThrottle-TTS-Prefer-Backup header).
        var stub = new HangThenBackupStub();
        var svc = ServiceWith(stub, TempPersist());

        // First narration: the primary goes silent. No audio, Retrying, and the request did NOT yet ask
        // for the backup (a fresh session has no reason to skip the primary).
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "first summary", "reply one");
        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
        Assert.Equal(1, stub.Calls);
        Assert.False(stub.LastRequest!.Headers.Contains("X-DevThrottle-TTS-Prefer-Backup"));

        // Second narration (same session, inside the armed window): the Gateway routes past the hung
        // primary - the request carries the prefer-backup header, the proxy serves the backup, and the
        // session becomes playable with the backup-voice note.
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "second summary", "reply two");
        Assert.Equal(2, stub.Calls);
        Assert.True(stub.LastRequest!.Headers.Contains("X-DevThrottle-TTS-Prefer-Backup"));
        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.True(svc.ServedViaFallbackFor(TenantId.Local, "sid-1"));
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));   // a served backup clears the Retrying state
    }

    [Fact]
    public async Task PrimaryHang_OnOneSession_DoesNotRouteAnotherSessionToTheBackup()
    {
        // The backup-routing window is strictly per session (keyed by sid): one session's hang must never
        // make a different, healthy session skip its primary.
        var stub = new HangThenBackupStub();
        var svc = ServiceWith(stub, TempPersist());

        await svc.StoreSpokenAsync(TenantId.Local, "sid-hang", "summary", "reply");   // sid-hang hits the silent primary
        Assert.Equal(1, stub.Calls);

        await svc.StoreSpokenAsync(TenantId.Local, "sid-other", "summary", "reply");  // a DIFFERENT session
        Assert.Equal(2, stub.Calls);
        Assert.False(stub.LastRequest!.Headers.Contains("X-DevThrottle-TTS-Prefer-Backup"));   // not armed for sid-other
    }

    /// <summary>
    /// Wait for the ready-audio cache to finish loading before handing the service to a test. The cache is
    /// read in the BACKGROUND in production so its cost cannot sit in front of the port bind (issue #2203);
    /// a test that asserts on reloaded audio would otherwise be racing that read. Nothing in the serving
    /// path waits like this - a cache still loading behaves as a miss and regenerates.
    /// </summary>
    private static WingmanVoiceService Warmed(WingmanVoiceService svc)
    {
        Assert.True(svc.ReadyAudioWarmup.Wait(TimeSpan.FromSeconds(30)),
            "the ready-audio warm load did not finish within 30 seconds");
        return svc;
    }
}
