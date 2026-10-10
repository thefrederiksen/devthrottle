using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The fingerprint rules, pinned (the Error Logging mission, step 1 review). A fingerprint is stored on every report and
/// keys the summaries kept for good, so ANY change to what goes into it - the basis or a single pass - splits every
/// existing problem into an old and a new one. <see cref="ErrorFingerprint.RulesVersion"/> exists to make that fork
/// visible, and until now only a comment said to raise it. These fixed inputs and their exact fingerprints make the
/// rule bite: change the rules and this fails, and the message says what to do.
/// </summary>
public sealed class ErrorFingerprintGoldenTests
{
    private const string RaiseTheVersion =
        "the rules changed: raise ErrorFingerprint.RulesVersion and update these values";

    public static TheoryData<string, string?, string?, string?, string?, int?, string?> Golden => new()
    {
        { "ca083dab2ec899cf", "director", "SessionManager", "System.IO.IOException", "Save FAILED: disk full", null, null },
        { "1ce1a114ef8a58ad", "director", "Session", null, "WAIT ENDED session=2c3c4215 verb=prompt", null, null },
        { "5463adc5fbeed9c5", "gateway", "GatewayEndpoints", null, "POST /sessions/{sid}/prompt answered 503 (director_stale)", 503, "director_stale" },
        { "ef350bd820ea043a", "cockpit", "SessionComposer", null, "Gateway answered 403 for the prompt", 403, "forbidden" },
        { "3828724d7784ff8d", "install", "launcher", null, "can't open 'x.txt', user's file at C:\\Users\\a\\b on 2026-10-09T10:00:00Z", null, null },
        { "d521480bdb2874ec", "tool", "cc-devthrottle", null, "GET /gateway/skills FAILED: no answer after 30s from \"https://example\"", null, null },
        { "690210ceac670d02", "gateway", "ErrorIntakeLimit", null, "POST /install-reports dropped 7 report(s) over its limit in the hour from 2026-10-09T10:00Z", 429, "rate_limited" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void A_fixed_input_has_its_exact_fingerprint(string expected, string? component, string? source, string? exceptionType,
        string? message, int? httpStatus, string? errorCode)
    {
        var actual = ErrorFingerprint.Of(component, source, exceptionType, message, httpStatus, errorCode);
        Assert.True(expected == actual, $"{RaiseTheVersion}. Expected {expected}, got {actual} for \"{message}\".");
    }

    [Fact]
    public void The_rules_version_is_the_one_these_values_were_made_with()
        => Assert.True(ErrorFingerprint.RulesVersion == 1, RaiseTheVersion + " (they were made with version 1)");

    [Fact]
    public void A_normalised_message_has_its_exact_text()
    {
        const string expected = "POST /sessions/<hex>/prompt FAILED at <time> for <q> in <path> with <hex> id <id> <hex> <n> retries";
        var actual = ErrorFingerprint.Normalise(
            "POST /sessions/2c3c4215/prompt FAILED at 10:15:02 for \"hello\" in /var/lib/x/y with 0xFF id cmd-a1b2c3 deadbeefcafe 42 retries");
        Assert.True(expected == actual, $"{RaiseTheVersion}. Expected \"{expected}\", got \"{actual}\".");
    }
}
