using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the pure fleet messaging helper (issue #705). Pure and machine-independent.
///
/// The framing helper and its four tests were deleted on 17 September 2026 (Message Load mission,
/// inspection 11): a queued message is read from the inbox, never framed and typed, so nothing called it.
/// </summary>
public sealed class FleetMessagingFramingTests
{
    [Fact]
    public void ShortId_truncates_to_eight_characters()
    {
        Assert.Equal("4c810000", FleetMessaging.ShortId("4c810000-1111-2222"));
        Assert.Equal("abc", FleetMessaging.ShortId("abc"));
        Assert.Equal("", FleetMessaging.ShortId(null));
    }
}
