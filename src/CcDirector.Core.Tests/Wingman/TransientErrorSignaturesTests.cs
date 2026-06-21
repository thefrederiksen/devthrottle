using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Issue #476: the content classifier must recognize the TRANSIENT Anthropic server-error family
/// and act on nothing else. These tests pin both halves: transient signatures match (so the loop
/// arms), and terminal / non-error text does NOT (so the loop never auto-retries a real problem).
/// </summary>
public sealed class TransientErrorSignaturesTests
{
    // The verbatim field message (Screenshot 2026-06-16 133011.png), as it appears at the bottom
    // of a stalled session's terminal.
    private const string Verbatim500 =
        "API Error: 500 Internal server error. This is a server-side issue, usually temporary - " +
        "try again in a moment. If it persists, check https://status.claude.com";

    [Fact]
    public void IsRetryableTransient_Verbatim500Message_True()
        => Assert.True(TransientErrorSignatures.IsRetryableTransient(Verbatim500));

    [Theory]
    [InlineData("API Error: 529 Overloaded")]
    [InlineData("overloaded_error: Overloaded")]
    [InlineData("The server is overloaded. Please try again in a moment.")]
    [InlineData("500 Internal Server Error")] // case-insensitive
    public void IsRetryableTransient_TransientFamily_True(string screen)
        => Assert.True(TransientErrorSignatures.IsRetryableTransient(screen));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Done. Tests passed. 3 files changed.")]            // ordinary output
    [InlineData("bypass permissions on (shift+tab to cycle)")]      // mode footer, not an error
    [InlineData("> ")]                                              // idle input box
    public void IsRetryableTransient_NoError_False(string? screen)
        => Assert.False(TransientErrorSignatures.IsRetryableTransient(screen));

    [Theory]
    [InlineData("API Error: 401 Unauthorized. invalid api key")]
    [InlineData("authentication_error: invalid x-api-key")]
    [InlineData("Your credit balance is too low to access the Anthropic API.")]
    [InlineData("invalid_request_error: 400 Bad Request - malformed request")]
    [InlineData("403 Forbidden: permission_error")]
    public void IsRetryableTransient_TerminalError_False(string screen)
        => Assert.False(TransientErrorSignatures.IsRetryableTransient(screen));

    [Fact]
    public void IsRetryableTransient_TerminalSignatureVetoesTransient_False()
    {
        // A screen that somehow shows BOTH a transient and a terminal signature must fail closed
        // toward NOT acting - a wrong key never clears on a retry.
        var screen = "500 Internal server error\nAlso: invalid api key";
        Assert.True(TransientErrorSignatures.ContainsTransient(screen));
        Assert.True(TransientErrorSignatures.ContainsTerminal(screen));
        Assert.False(TransientErrorSignatures.IsRetryableTransient(screen));
    }
}
