using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Recording;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression tests for issue #2482: hosted live dictation must read the REQUESTING TENANT's
/// glossary - the file the Cockpit dictionary editor writes via <see cref="TenantGlossary.PathFor"/> -
/// and never the global flat file. Before the fix, <c>POST /transcription</c> constructed
/// <see cref="GatewayTranscriptionService"/> without a tenant dictionary provider, so cleanup loaded
/// the flat <see cref="GatewayTranscriptionService.DictionaryPath"/> for every tenant and a hosted
/// user's dictionary edits did not affect their next dictation.
///
/// These tests build the service exactly as the endpoints do - with the DEFAULT dictionary provider,
/// nothing injected - so they prove the default itself is tenant-aware. The transcription provider is
/// a stubbed HttpClient. No network is needed for the correction here because these fixtures have no
/// judge credential, so only the deterministic listed-alias path runs - an UNLISTED correction would
/// need a hosted ruling, and with no judge none is applied. Uses CC_DIRECTOR_ROOT for the glossary locations - in the "DirectorRoot" collection
/// because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TenantGlossaryDictationCleanupTests : IDisposable
{
    private static readonly TenantId HostedTenant = new("11111111-2222-3333-4444-555555555555");

    private readonly string? _prevRoot;
    private readonly string _root;
    private readonly string _vaultPath;

    public TenantGlossaryDictationCleanupTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-glossary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _vaultPath = Path.Combine(_root, "keyvault.json");

        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The raw transcript the stubbed provider returns; both glossaries list "glotword"
    /// as a wrong form, each mapping it to a DIFFERENT canonical term, so the corrected output
    /// names exactly which file the cleanup read.</summary>
    private const string RawTranscript = "please spell glotword now";

    private static void WriteGlossary(string path, string canonicalTerm)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"common_mistranscriptions:\n  {canonicalTerm}: [glotword]\n");
    }

    private void WriteGlobalFlatFile() => WriteGlossary(GatewayTranscriptionService.DictionaryPath(), "GlobalCanonical");

    private void WriteTenantGlossary(TenantId tenant) => WriteGlossary(TenantGlossary.PathFor(tenant), "TenantCanonical");

    /// <summary>A genuinely corrupt glossary for this tenant - an unterminated YAML flow sequence, which
    /// makes the real loader's parse throw rather than quietly returning an empty dictionary. Written
    /// through the same <see cref="TenantGlossary.PathFor"/> the Cockpit dictionary editor writes, so the
    /// fault is raised by the PRODUCTION read path and not by a test stub standing in for it.</summary>
    private static void WriteMalformedTenantGlossary(TenantId tenant)
    {
        var path = TenantGlossary.PathFor(tenant);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "common_mistranscriptions:\n  TenantCanonical: [glotword\n");
    }

    /// <summary>A service built the way the endpoints build it: default dictionary provider, stubbed
    /// transcription POST, and a scratch archive/history under this test's own root so an omitted
    /// default can never write into the real user's folders.</summary>
    private GatewayTranscriptionService Service()
        => new(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"text\":\"" + RawTranscript + "\"}")),
            history: new TranscriptionHistoryLog(Path.Combine(_root, "history-scratch")),
            audioArchive: new TranscriptionAudioArchive(false, Path.Combine(_root, "archive-scratch")));

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StatusHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task TranscribeAsync_HostedTenant_ReadsThatTenantsGlossary_NeverTheGlobalFile()
    {
        // THE regression (issue #2482). The global flat file and the tenant's own glossary both list
        // the same wrong form with different corrections; the corrected text must carry the TENANT's
        // correction, proving the cleanup read the file the Cockpit dictionary editor writes.
        WriteGlobalFlatFile();
        WriteTenantGlossary(HostedTenant);

        var result = await Service().TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: true, CancellationToken.None,
            tenant: HostedTenant, source: "batch");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal("please spell TenantCanonical now", result.Text);
        Assert.DoesNotContain("GlobalCanonical", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_HostedTenantWithNoGlossary_DoesNotFallBackToTheGlobalFile()
    {
        // A tenant that has written no glossary gets NO correction - never the global file's terms.
        // A miss must stay a miss (tenancy law: no aggregate or global fallback that hides one).
        WriteGlobalFlatFile();

        var result = await Service().TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: true, CancellationToken.None,
            tenant: HostedTenant, source: "batch");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal(RawTranscript, result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_LocalTenant_StillReadsTheFlatFile()
    {
        // Self-host unchanged: the single Local tenant's glossary IS the flat file, exactly as before.
        WriteGlobalFlatFile();

        var result = await Service().TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: true, CancellationToken.None,
            tenant: TenantId.Local, source: "batch");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal("please spell GlobalCanonical now", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_NoTenant_StillReadsTheFlatFile()
    {
        // A caller that carries no tenant is the self-host single tenant - same flat file as before.
        WriteGlobalFlatFile();

        var result = await Service().TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: true, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal("please spell GlobalCanonical now", result.Text);
    }

    [Fact]
    public async Task CleanupAsync_TenantScoped_ReadsThatTenantsGlossary()
    {
        // The phone Notes assemble-then-clean path: GatewayServiceRecordingTranscriber passes its
        // tenant into CleanupAsync, so the assembled transcript is corrected against that tenant's
        // own glossary through the same one mechanism.
        WriteGlobalFlatFile();
        WriteTenantGlossary(HostedTenant);

        var outcome = await Service().CleanupAsync(RawTranscript, HostedTenant);

        Assert.True(outcome.Applied);
        Assert.Equal("please spell TenantCanonical now", outcome.Text);
    }

    // ---- The COMBINED contract: issues #2482 and #2483 together ----
    //
    // These two belong to neither issue alone, which is why neither branch carried them. 2482 makes the
    // read tenant-keyed; 2483 makes the read fail open. Only together do they answer the question a
    // hosted user actually poses: MY glossary is corrupt - what happens to my dictation? All three parts
    // of the answer are asserted, because each can regress on its own:
    //   1. the request still succeeds (2483 - a dictionary fault must not fail transcribed text),
    //   2. the text comes back RAW (2483 - fail open, not fail closed),
    //   3. and it is NOT corrected from the global file (2482 - a fault must not become a global fallback,
    //      which is the tenancy defect wearing a different hat).
    //
    // The fault is raised by the REAL loader on a REAL corrupt file through the DEFAULT provider - the
    // service is built exactly as the endpoints build it - so this proves the production path fails open,
    // not merely that the guard catches a stub that was told to throw.

    [Fact]
    public async Task TranscribeAsync_HostedTenantWithMalformedGlossary_FailsOpenToRawText_AndNeverTheGlobalFile()
    {
        WriteGlobalFlatFile();
        WriteMalformedTenantGlossary(HostedTenant);

        var result = await Service().TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: true, CancellationToken.None,
            tenant: HostedTenant, source: "batch");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal(RawTranscript, result.Text);
        Assert.DoesNotContain("GlobalCanonical", result.Text);
    }

    [Fact]
    public async Task RecordingTranscriber_CarriesItsOwnTenantIntoCleanup_NotTheGlobalFile()
    {
        // The wiring #2482 changed but nothing guarded. RecordingEndpoints builds ONE
        // GatewayServiceRecordingTranscriber per tenant and that adapter is the only thing that knows
        // which tenant the phone Notes assemble-then-clean path belongs to - it holds the tenant and
        // supplies it to CleanupAsync, because IRecordingTranscriber.CleanupAsync carries no tenant of
        // its own. Drop the tenant at that one call site and every hosted recording silently corrects
        // against the global file again, with no other test noticing.
        //
        // So this drives the ADAPTER, through its IRecordingTranscriber contract, rather than calling
        // the service directly - calling the service directly is what the test above already does, and
        // it is exactly the check that cannot see this regression.
        WriteGlobalFlatFile();
        WriteTenantGlossary(HostedTenant);

        IRecordingTranscriber transcriber = new GatewayServiceRecordingTranscriber(Service(), HostedTenant);

        var outcome = await transcriber.CleanupAsync(RawTranscript, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal("please spell TenantCanonical now", outcome.Text);
        Assert.DoesNotContain("GlobalCanonical", outcome.Text);
    }

    [Fact]
    public async Task CleanupAsync_HostedTenantWithMalformedGlossary_FailsOpenToRawText_AndNeverTheGlobalFile()
    {
        // The same combined contract on the phone Notes assemble-then-clean path.
        WriteGlobalFlatFile();
        WriteMalformedTenantGlossary(HostedTenant);

        var outcome = await Service().CleanupAsync(RawTranscript, HostedTenant);

        Assert.False(outcome.Applied);
        Assert.Equal(RawTranscript, outcome.Text);
        Assert.DoesNotContain("GlobalCanonical", outcome.Text);
        Assert.Contains("dictionary unavailable", outcome.Reason);
    }
}
