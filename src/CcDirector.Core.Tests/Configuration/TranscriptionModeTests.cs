using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #497: the transcription mode parse/format helpers and the endpoint resolver. The default
/// is bring-your-own (byo); a typo never silently picks a mode (no-fallback rule); and the
/// resolver pairs each mode with exactly one base URL + key name - the security-critical routing.
/// </summary>
public sealed class TranscriptionModeTests
{
    [Theory]
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
    public void Parse_MissingValue_DefaultsToByo(string? value)
        => Assert.Equal(TranscriptionMode.Byo, TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData("openai")]
    [InlineData("groq")]
    [InlineData("local")]
    public void Parse_UnknownValue_Throws(string value)
        => Assert.Throws<ArgumentException>(() => TranscriptionModeExtensions.Parse(value));

    [Theory]
    [InlineData(TranscriptionMode.Byo, "byo")]
    [InlineData(TranscriptionMode.DevThrottle, "devthrottle")]
    public void ToConfigString_RoundTrips(TranscriptionMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToConfigString());
        Assert.Equal(mode, TranscriptionModeExtensions.Parse(expected));
    }

    [Theory]
    [InlineData("byo", true)]
    [InlineData("devthrottle", true)]
    [InlineData("", true)]      // empty is valid (means default)
    [InlineData("nope", false)]
    public void IsValid_ClassifiesInput(string value, bool expected)
        => Assert.Equal(expected, TranscriptionModeExtensions.IsValid(value));

    // ===== Endpoint resolver: the routing that keeps the BYO key off devthrottle.com =====

    [Fact]
    public void Resolve_Byo_UsesOpenAiBaseUrlAndOpenAiKeyName()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.Byo);

        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("OPENAI_API_KEY", ep.KeyName);
        Assert.False(ep.IsDevThrottle);
        // The bring-your-own key must NEVER be paired with a devthrottle.com URL.
        Assert.DoesNotContain("devthrottle.com", ep.BaseUrl);
    }

    [Fact]
    public void Resolve_DevThrottle_UsesDevThrottleBaseUrlAndDevThrottleKeyName()
    {
        var ep = TranscriptionEndpointResolver.Resolve(TranscriptionMode.DevThrottle);

        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("DEVTHROTTLE_API_KEY", ep.KeyName);
        Assert.True(ep.IsDevThrottle);
        // DevThrottle mode must never present the user's own OpenAI provider key.
        Assert.NotEqual("OPENAI_API_KEY", ep.KeyName);
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
