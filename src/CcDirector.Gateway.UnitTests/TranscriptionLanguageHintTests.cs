using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the spoken-language hint actually reaches the provider.
///
/// This is the property the whole multi-language transcription check rests on, and it is exactly the
/// kind that is easy to plumb and never verify: the field can be added to a record, threaded through
/// five methods, and silently dropped at the wire, and every screen above would still look right while
/// every non-English result quietly came back worse. So the assertion is made where it matters - on
/// the outgoing HTTP request - not on the intermediate types.
///
/// Auto-detection is the default and must stay the default: every ordinary dictation path passes no
/// language, and sending an empty or guessed one would be worse than sending none.
/// </summary>
public sealed class TranscriptionLanguageHintTests
{
    /// <summary>Captures the outgoing request body so the multipart parts can be inspected.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hello"}"""),
            };
        }
    }

    private static ResolvedTranscription Routing(string? language) => new()
    {
        BaseUrl = "https://example.invalid/api/v1",
        ApiKey = "dt_test",
        Transport = TranscriptionTransport.Batch,
        Model = "gpt-4o-transcribe",
        Mode = TranscriptionMode.DevThrottle,
        Language = language,
    };

    private static async Task<string> PostAndCaptureAsync(string? language)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http);

        await pipeline.TranscribeUncorrectedAsync(new byte[] { 1, 2, 3, 4 }, "voice-test.wav", Routing(language), CancellationToken.None);
        return handler.LastBody;
    }

    [Fact]
    public async Task TheLanguageHintIsSentToTheProvider()
    {
        var body = await PostAndCaptureAsync("da");

        Assert.Contains("name=language", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("da", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("da")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task EveryOfficiallySupportedLanguageIsCarriedThrough(string code)
    {
        // One per SUPPORTED language, so adding a language to the picker without plumbing it is caught
        // here rather than by a user in that language wondering why their results are poor.
        var body = await PostAndCaptureAsync(code);

        Assert.Contains("name=language", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\r\n\r\n{code}\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoHint_NoLanguageFieldIsSent_SoTheProviderStillDetects()
    {
        // The behaviour every existing dictation caller depends on. A "language" part carrying an
        // empty value would tell the provider to detect nothing rather than to detect for itself.
        var body = await PostAndCaptureAsync(null);

        Assert.DoesNotContain("name=language", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AWhitespaceHintIsTreatedAsNoHint()
    {
        var body = await PostAndCaptureAsync("   ");

        Assert.DoesNotContain("name=language", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheModelAndResponseFormatAreStillSent()
    {
        // Guards against a change to the form construction that adds the hint and drops something the
        // provider requires - the test would otherwise pass on a request that no longer works at all.
        var body = await PostAndCaptureAsync("da");

        Assert.Contains("name=model", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=response_format", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=file", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToResolved_NormalizesTheHint_SoBlankNeverReachesTheWire()
    {
        var endpoint = TranscriptionEndpointResolver.Resolve(TranscriptionMode.DevThrottle);
        var routing = new GatewayTranscriptionRouting { Endpoint = endpoint, Key = "dt_test" };

        Assert.Equal("da", routing.ToResolved("  da  ").Language);
        Assert.Null(routing.ToResolved("   ").Language);
        Assert.Null(routing.ToResolved(null).Language);
    }
}
