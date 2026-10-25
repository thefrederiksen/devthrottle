using CcDirector.Gateway.Alerts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// HOW AN ALERT NAMES A SESSION (the Alerts mission, phase 5). The owner read "Let 121 Release - Manager message 125
/// Release - Release Manager?" on his phone and called it a machine string. Every session an alert mentions reads
/// "role (number)" now, from what the source's own record already holds.
/// </summary>
public sealed class AlertWordsTests
{
    [Theory]
    [InlineData("Release - Manager", "Release Manager")]
    [InlineData("Release - Release Manager", "Release Manager")]
    [InlineData("Alerts - Developer - Phase 5 phone screen redesign", "Alerts Developer")]
    [InlineData("Release - release manager - cut v2.31.0", "release manager")]
    [InlineData("Mac Tester", "Mac Tester")]
    [InlineData("  tests  ", "tests")]
    public void Role_TakesTheMissionAndRole_WithoutSayingTheMissionTwice(string name, string expected)
        => Assert.Equal(expected, AlertWords.Role(name));

    [Theory]
    [InlineData(121, "Release - Manager", "Release Manager (121)")]
    [InlineData(7, "Rig - Developer", "Rig Developer (007)")]
    [InlineData(125, null, "Session 125")]
    [InlineData(null, "Release - Manager", "Release Manager")]
    [InlineData(null, null, "A session this Gateway no longer knows")]
    public void SessionLabel_IsRoleThenNumber(int? number, string? name, string expected)
        => Assert.Equal(expected, AlertWords.SessionLabel(number, name));

    [Theory]
    [InlineData("Session 104 \"Mac - Tester - sudo checks\"", "Mac Tester (104)")]
    [InlineData("Session \"Mac - Tester\"", "Mac Tester")]
    [InlineData("You, on the phone", "You, on the phone")]
    [InlineData("The cc-secrets window or a terminal on mac-mini", "The cc-secrets window or a terminal on mac-mini")]
    public void FromSecretAskedBy_RewritesOnlyTheSessionForm(string askedBy, string expected)
        => Assert.Equal(expected, AlertWords.FromSecretAskedBy(askedBy));

    [Theory]
    [InlineData("Release - Release Manager (1512339f)", "Release Manager")]
    [InlineData("a device on this account", "A device on this account")]
    [InlineData("session 1512339f (not on the roster)", "Session 1512339f (not on the roster)")]
    public void FromRestartRequester_DropsTheShortId_AndStartsASentence(string requester, string expected)
        => Assert.Equal(expected, AlertWords.FromRestartRequester(requester));
}
