using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #497, #541: the transcription mode parse/format helpers and the endpoint resolver. The
/// default is now local Whisper.net (works offline, no key); a typo never silently picks a mode
/// (no-fallback rule); and the resolver pairs each remote mode with exactly one base URL + key name
/// - the security-critical routing - while local mode has no URL and no key (in-process).
/// </summary>
public sealed class TranscriptionModeTests
{
    [Theory]
    [InlineData("local", TranscriptionMode.Local)]
    [InlineData("LOCAL", TranscriptionMode.Local)]
    [InlineData("  Local  ", TranscriptionMode.Local)]
    [InlineData("byo", TranscriptionMode.Byo)]
    [InlineData("BYO", TranscriptionMode.Byo)]
    [InlineData("  DevThrottle  ", TranscriptionMode.DevThrottle)]
    [InlineData("devthrottle", TranscriptionMode.DevThrottle)]
    public void Parse_RecognizedValues(string value, TranscriptionMode expected)
        => Assert.Equal(expected, TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingValue_DefaultsToLocal(string? value)   // issue #541: was Byo before
        => Assert.Equal(TranscriptionMode.Local, TranscriptionModeExtensions.Parse(value));

    [Fact]
    public void Parse_Byo_StillReturnsByo()   // regression: opt-in BYO must still parse (issue #541)
        => Assert.Equal(TranscriptionMode.Byo, TranscriptionModeExtensions.Parse("byo"));

    [Theory]
    [InlineData("openai")]
    [InlineData("groq")]
    [InlineData("whisper")]
    public void Parse_UnknownValue_Throws(string value)
        => Assert.Throws<ArgumentException>(() => TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData(TranscriptionMode.Local, "local")]
    [InlineData(TranscriptionMode.Byo, "byo")]
    [InlineData(TranscriptionMode.DevThrottle, "devthrottle")]
    public void ToConfigString_RoundTrips(TranscriptionMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToConfigString());
        Assert.Equal(mode, TranscriptionModeExtensions.Parse(expected));
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("byo", true)]
    [InlineData("devthrottle", true)]
    [InlineData("", true)]      // empty is valid (means default)
    [InlineData("nope", false)]
    public void IsValid_ClassifiesInput(string value, bool expected)
        => Assert.Equal(expected, TranscriptionModeExtensions.IsValid(value));

    // ===== Endpoint resolver: local is in-process; the remote routing keeps BYO off devthrottle.com =====

    [Fact]
    public void Resolve_Local_HasNoUrlNoKey_AndIsLocal()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.Local);

        // Local is in-process (issue #541): no base URL and no vault key name.
        Assert.Null(ep.BaseUrl);
        Assert.Null(ep.KeyName);
        Assert.True(ep.IsLocal);
        Assert.False(ep.IsDevThrottle);
        // Local records then transcribes in-process: a batch transport, the local ggml model.
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal(TranscriptionEndpointResolver.LocalModel, ep.Model);
    }

    [Fact]
    public void Resolve_Byo_UsesOpenAiBaseUrlAndOpenAiKeyName()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.Byo);

        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("OPENAI_API_KEY", ep.KeyName);
        Assert.False(ep.IsDevThrottle);
        Assert.False(ep.IsLocal);
        // Issue #513: BYO is the realtime transport with the OpenAI model.
        Assert.Equal(TranscriptionTransport.Realtime, ep.Transport);
        Assert.Equal("gpt-4o-transcribe", ep.Model);
        // The bring-your-own key must NEVER be paired with a devthrottle.com URL.
        Assert.NotNull(ep.BaseUrl);
        Assert.DoesNotContain("devthrottle.com", ep.BaseUrl);
    }

    [Fact]
    public void Resolve_DevThrottle_UsesDevThrottleBaseUrlAndDevThrottleKeyName()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.DevThrottle);

        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("DEVTHROTTLE_API_KEY", ep.KeyName);
        Assert.True(ep.IsDevThrottle);
        // Issue #513: DevThrottle is the batch transport with the provider-correct Groq model -
        // never the shared OpenAI default (the proxy 404s on gpt-4o-transcribe).
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal("whisper-large-v3", ep.Model);
        Assert.NotEqual(TranscriptionEndpointResolver.OpenAiModel, ep.Model);
        // DevThrottle mode must never present the user's own OpenAI provider key.
        Assert.NotEqual("OPENAI_API_KEY", ep.KeyName);
    }

    [Theory]
    [InlineData("realtime", TranscriptionTransport.Realtime)]
    [InlineData("REALTIME", TranscriptionTransport.Realtime)]
    [InlineData("  batch  ", TranscriptionTransport.Batch)]
    [InlineData("batch", TranscriptionTransport.Batch)]
    public void Transport_Parse_RecognizedValues(string value, TranscriptionTransport expected)
        => Assert.Equal(expected, TranscriptionTransportExtensions.Parse(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("websocket")]
    public void Transport_Parse_UnknownOrMissing_Throws(string? value)
        => Assert.Throws<ArgumentException>(() => TranscriptionTransportExtensions.Parse(value));

    [Theory]
    [InlineData(TranscriptionTransport.Realtime, "realtime")]
    [InlineData(TranscriptionTransport.Batch, "batch")]
    public void Transport_ToConfigString_RoundTrips(TranscriptionTransport transport, string expected)
    {
        Assert.Equal(expected, transport.ToConfigString());
        Assert.Equal(transport, TranscriptionTransportExtensions.Parse(expected));
    }

    [Theory]
    [InlineData("dt_live_abc123", true)]
    [InlineData("dt_test_abc123", true)]
    [InlineData("  dt_live_padded  ", true)]
    [InlineData("sk-abc123", false)]
    [InlineData("dt_unknown_abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidDevThrottleKey_ChecksPrefix(string? key, bool expected)
        => Assert.Equal(expected, TranscriptionEndpointResolver.IsValidDevThrottleKey(key));

    [Theory]
    [InlineData("sk-abc123", true)]
    [InlineData("  sk-padded  ", true)]
    [InlineData("dt_live_abc", false)]
    [InlineData("abc123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidOpenAiKey_ChecksPrefix(string? key, bool expected)
        => Assert.Equal(expected, TranscriptionEndpointResolver.IsValidOpenAiKey(key));
}
