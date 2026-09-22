using System.Net;
using System.Net.Http.Headers;
using CcDirector.Core;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the voice upload front doors bound what they will hold in memory.
///
/// The defect these cover: both routes copied the whole request body into a <c>MemoryStream</c> with no
/// ceiling - <c>await ctx.Request.Body.CopyToAsync(ms, ct)</c> - so the SENDER decided how much of this
/// machine's memory to use. The Gateway shares a machine with the user's editors and agents, and these
/// are the mobile paths clients retry automatically, so a bad recording arrives repeatedly rather than
/// once. The cloud audio endpoint has had a 4 MB cap since day one; the local leg had none.
///
/// These boot the REAL <see cref="GatewayWingmanVoiceEndpoint"/> on an ephemeral port and drive it over
/// real HTTP, with storage redirected to a temp root (CC_DIRECTOR_ROOT) so the suite never touches the
/// developer's own voice-upload staging.
/// </summary>
[Collection("VoiceUploadLimits")]   // serial: these mutate the CC_DIRECTOR_ROOT process env var
public sealed class VoiceUploadLimitsTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public VoiceUploadLimitsTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-voice-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Boot the real voice endpoint. No auth middleware here on purpose: the trust boundary is
    /// covered by its own tests, and what is under test is the SIZE guard, which must hold on its own.</summary>
    private static async Task<(WebApplication App, HttpClient Http)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var vaultPath = Path.Combine(Path.GetTempPath(), "cc-voice-limits-" + Guid.NewGuid().ToString("N") + ".vault");
        var vault = new KeyVault(vaultPath);
        var persistPath = Path.Combine(Path.GetTempPath(), "cc-voice-limits-" + Guid.NewGuid().ToString("N") + ".json");
        var settingsData = new GatewayDbTestHarness();
        app.Lifetime.ApplicationStopped.Register(settingsData.Dispose);
        var tenantSettings = new TenantSettingsResolver(new TenantSettingsStore(settingsData.Open()));

        var voice = new WingmanVoiceService(
            (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by an upload-size test"),
            vault, tenantSettings, persistPath);

        GatewayWingmanVoiceEndpoint.Map(
            app,
            new DirectorRegistry(Path.Combine(Path.GetTempPath(), "cc-voice-limits-inst-" + Guid.NewGuid().ToString("N"))),
            (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by an upload-size test"),
            vault,
            voice,
            tenantSettings,
            // The boundary is required and non-nullable now (finding I1-01). Self-host harness, so the REAL
            // self-host boundary: built over the SingleTenantContext, it always resolves Local.
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()));

        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static async Task<string> RegisterUploadAsync(HttpClient http)
    {
        var res = await http.PostAsync("/wingman/utterance/upload", new StringContent(""));
        res.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("upload_id").GetString();
        // Explicit check rather than the null-forgiving operator, which the coding standard forbids:
        // if registration ever stopped returning an id, this says so instead of a NullReference later.
        if (id is null) throw new InvalidOperationException("upload registration returned no upload_id");
        return id;
    }

    private static ByteArrayContent Body(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    // A normal chunk still works. Without this, the tests below would pass on an endpoint that rejected
    // everything, which would prove nothing.
    [Fact]
    public async Task PutChunk_WithinLimit_IsAccepted()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;

        var id = await RegisterUploadAsync(http);
        var res = await http.PutAsync($"/wingman/utterance/{id}/chunk/0", Body(new byte[64 * 1024]));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // The declared-size door: an honest client that says how big the chunk is gets refused up front.
    [Fact]
    public async Task PutChunk_OverLimitWithDeclaredLength_Returns413()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;

        var id = await RegisterUploadAsync(http);
        var oversize = new byte[VoiceUploadLimits.MaxChunkBytes + 1];
        var res = await http.PutAsync($"/wingman/utterance/{id}/chunk/0", Body(oversize));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    // The real guard. Content-Length is the client's CLAIM; a chunked body makes no claim at all, so the
    // read itself has to stop. This sends an over-limit body with NO Content-Length - if the endpoint
    // trusted the header alone, this is the request that would sail through and be buffered whole.
    [Fact]
    public async Task PutChunk_OverLimitWithNoDeclaredLength_Returns413()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;

        var id = await RegisterUploadAsync(http);

        // A StreamContent over a non-seekable stream has no Content-Length: HttpClient sends it
        // chunked, exactly like a client that streams a recording as it captures it.
        var oversize = new MemoryStream(new byte[VoiceUploadLimits.MaxChunkBytes + 1024]);
        var content = new StreamContent(new NonSeekableStream(oversize));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var res = await http.PutAsync($"/wingman/utterance/{id}/chunk/0", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    // The upload total is bounded across chunks, not just per chunk: many legal chunks must not add up
    // to an unbounded upload.
    [Fact]
    public async Task PutChunk_ExceedingTheUploadTotal_Returns413()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;

        var id = await RegisterUploadAsync(http);
        var chunk = new byte[VoiceUploadLimits.MaxChunkBytes];              // 8 MB each
        var allowed = (int)(VoiceUploadLimits.MaxTotalUploadBytes / VoiceUploadLimits.MaxChunkBytes);

        for (var i = 0; i < allowed; i++)
        {
            var ok = await http.PutAsync($"/wingman/utterance/{id}/chunk/{i}", Body(chunk));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var overflow = await http.PutAsync($"/wingman/utterance/{id}/chunk/{allowed}", Body(chunk));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overflow.StatusCode);
    }

    // Re-sending a chunk must stay free. The total guard counts what is on disk, and a retry REPLACES a
    // chunk rather than adding one - if the guard forgot that, a client retrying its last chunk at the
    // ceiling (the normal case on a flaky phone connection) would be refused forever.
    [Fact]
    public async Task PutChunk_ResentAtTheCeiling_IsNotCountedTwice()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;

        var id = await RegisterUploadAsync(http);
        var chunk = new byte[VoiceUploadLimits.MaxChunkBytes];
        var allowed = (int)(VoiceUploadLimits.MaxTotalUploadBytes / VoiceUploadLimits.MaxChunkBytes);

        for (var i = 0; i < allowed; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.PutAsync($"/wingman/utterance/{id}/chunk/{i}", Body(chunk))).StatusCode);

        // The upload is now exactly at the ceiling. Re-send the last chunk, as a retrying client does.
        var resend = await http.PutAsync($"/wingman/utterance/{id}/chunk/{allowed - 1}", Body(chunk));
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
    }

    /// <summary>A stream that cannot report its length, so HttpClient must send it chunked.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
