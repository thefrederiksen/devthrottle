using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #497, #887: the transcription mode parse/format helpers and the endpoint resolver. The
/// default is now DevThrottle's hosted service (we dogfood our own, issue #887); removed legacy
/// provider values migrate forward to DevThrottle; a typo never silently picks a mode
/// (no-fallback rule); and the resolver pairs every mode with exactly one Gateway-owned DevThrottle
/// base URL + key name.
/// </summary>
public sealed class TranscriptionModeTests
{
    [Theory]
    [InlineData("byo", TranscriptionMode.DevThrottle)]
    [InlineData("BYO", TranscriptionMode.DevThrottle)]
    [InlineData("openai", TranscriptionMode.DevThrottle)]
    [InlineData("  DevThrottle  ", TranscriptionMode.DevThrottle)]
    [InlineData("devthrottle", TranscriptionMode.DevThrottle)]
    public void Parse_RecognizedValues(string value, TranscriptionMode expected)
        => Assert.Equal(expected, TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData("local")]   // issue #887 removed local; it migrates forward, not throws
    [InlineData("LOCAL")]
    [InlineData("  Local  ")]
    public void Parse_Local_MigratesForwardToDevThrottle(string value)
        => Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingValue_DefaultsToDevThrottle(string? value)   // issue #887: hosted is the default
        => Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeExtensions.Parse(value));

    [Fact]
    public void Parse_Byo_MigratesForwardToDevThrottle()
        => Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeExtensions.Parse("byo"));

    [Theory]
    [InlineData("groq")]
    [InlineData("whisper")]
    public void Parse_UnknownValue_Throws(string value)
        => Assert.Throws<ArgumentException>(() => TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData(TranscriptionMode.Byo, "devthrottle")]
    [InlineData(TranscriptionMode.DevThrottle, "devthrottle")]
    public void ToConfigString_SerializesForwardToDevThrottle(TranscriptionMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToConfigString());
        Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeExtensions.Parse(expected));
    }

    [Theory]
    [InlineData("local", true)]   // recognized legacy alias (migrates forward), so still valid
    [InlineData("byo", true)]
    [InlineData("openai", true)]
    [InlineData("devthrottle", true)]
    [InlineData("", true)]      // empty is valid (means default)
    [InlineData("nope", false)]
    public void IsValid_ClassifiesInput(string value, bool expected)
        => Assert.Equal(expected, TranscriptionModeExtensions.IsValid(value));

    // ===== Endpoint resolver: every mode is Gateway-owned DevThrottle hosted AI =====

    [Fact]
    public void Resolve_Byo_MigratesForwardToDevThrottle()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.Byo);

        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("DEVTHROTTLE_API_KEY", ep.KeyName);
        Assert.True(ep.IsDevThrottle);
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal("whisper-large-v3", ep.Model);
        Assert.Equal(TranscriptionMode.DevThrottle, ep.Mode);
    }

    [Fact]
    public void Resolve_DevThrottle_UsesDevThrottleBaseUrlAndDevThrottleKeyName()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.DevThrottle);

        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("DEVTHROTTLE_API_KEY", ep.KeyName);
        Assert.True(ep.IsDevThrottle);
        // The requested model names the one the hosted proxy really serves: whisper-large-v3, never
        // the generative gpt-4o-transcribe.
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal("whisper-large-v3", ep.Model);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleModel, ep.Model);
        // DevThrottle mode must never present the user's own legacy provider key name.
        Assert.NotEqual("OPENAI_API_KEY", ep.KeyName);
    }

    [Theory]
    [InlineData("  batch  ", TranscriptionTransport.Batch)]
    [InlineData("batch", TranscriptionTransport.Batch)]
    public void Transport_Parse_RecognizedValues(string value, TranscriptionTransport expected)
        => Assert.Equal(expected, TranscriptionTransportExtensions.Parse(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("realtime")]
    [InlineData("websocket")]
    public void Transport_Parse_UnknownOrMissing_Throws(string? value)
        => Assert.Throws<ArgumentException>(() => TranscriptionTransportExtensions.Parse(value));

    [Theory]
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

}
