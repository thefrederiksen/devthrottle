using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Issue #3675: the fingerprint says which PROBLEM a report is. The Director's own reporter merged repeats by
/// ignoring digits, and session and command ids carry letters, so one failure on two sessions became two rows.
/// These tests hold both halves: the same failure with different ids is ONE problem, and two genuinely different
/// failures are never merged.
/// </summary>
public sealed class ErrorFingerprintTests
{
    private const string Component = "director";
    private const string Source = "SessionManager";
    private const string Exception = "System.InvalidOperationException";

    private static string Of(string message) => ErrorFingerprint.Of(Component, Source, Exception, message);

    [Fact]
    public void Of_TwoReportsDifferingOnlyByAGuid_AreTheSameProblem()
    {
        var a = Of("Prompt delivery FAILED for session 2c3c4215-3a02-43f0-9cf6-f8ca4d872347: no answer");
        var b = Of("Prompt delivery FAILED for session 9b1d0e77-15aa-4c1e-8f2e-0c3d5a6b7e8f: no answer");

        Assert.Equal(a, b);
    }

    [Theory]
    // The case the digit-only grouping missed: short session ids made of hex letters and digits.
    [InlineData("WAIT ENDED session=2c3c4215 verb=prompt", "WAIT ENDED session=9f8eab7d verb=prompt")]
    // A hex id with no digit-only run long enough to be a number.
    [InlineData("handle 0x7ffa3b2c closed twice", "handle 0x1c2d3e4f closed twice")]
    // A bare hex id, including one that happens to hold no digit at all.
    [InlineData("object 7ffa3b2c9d0e released", "object deadbeefcafe released")]
    // Command ids that mix letters, digits and a dash.
    [InlineData("Command cmd-a1b2c3 FAILED: timed out", "Command cmd-zz9y8x FAILED: timed out")]
    [InlineData("Command sess_9f8e7d FAILED", "Command sess_1a2b3c FAILED")]
    public void Of_TwoReportsDifferingOnlyByALetteredId_AreTheSameProblem(string first, string second)
    {
        Assert.Equal(Of(first), Of(second));
    }

    [Theory]
    [InlineData("retry 3 of 5 FAILED", "retry 4 of 5 FAILED")]
    [InlineData(@"open C:\repos\alpha\file.cs FAILED", @"open D:\work\beta\other.cs FAILED")]
    [InlineData("open ~/repo/a.txt FAILED", "open ~/other/b.txt FAILED")]
    [InlineData("open /var/lib/one/x FAILED", "open /opt/two/y/z FAILED")]
    [InlineData("expired at 2026-10-08T10:15:30Z", "expired at 2026-09-01T23:59:01.123+02:00")]
    [InlineData("expired at 10:15:30", "expired at 23:59")]
    [InlineData("unknown name \"alpha\" in settings", "unknown name \"beta gamma\" in settings")]
    [InlineData("unknown name 'alpha' in settings", "unknown name 'beta' in settings")]
    public void Of_TwoReportsDifferingOnlyByWhatVariesBetweenOccurrences_AreTheSameProblem(string first, string second)
    {
        Assert.Equal(Of(first), Of(second));
    }

    [Theory]
    [InlineData("Prompt delivery FAILED: no answer", "Prompt delivery FAILED: session not found")]
    [InlineData("Save FAILED: disk full", "Load FAILED: disk full")]
    [InlineData("Command cmd-a1b2c3 FAILED: timed out", "Command cmd-a1b2c3 FAILED: refused")]
    public void Of_TwoGenuinelyDifferentMessages_AreDifferentProblems(string first, string second)
    {
        Assert.NotEqual(Of(first), Of(second));
    }

    [Fact]
    public void Of_TheSameMessageFromADifferentComponentSourceOrException_IsADifferentProblem()
    {
        const string message = "Save FAILED: disk full";
        var baseline = ErrorFingerprint.Of("director", "SessionManager", "System.IO.IOException", message);

        Assert.NotEqual(baseline, ErrorFingerprint.Of("launcher", "SessionManager", "System.IO.IOException", message));
        Assert.NotEqual(baseline, ErrorFingerprint.Of("director", "RepoManager", "System.IO.IOException", message));
        Assert.NotEqual(baseline, ErrorFingerprint.Of("director", "SessionManager", "System.UnauthorizedAccessException", message));
    }

    [Fact]
    public void Of_IsSixteenLowerCaseHexCharacters_AndStable()
    {
        var a = Of("Save FAILED: disk full");

        Assert.True(ErrorFingerprint.IsFingerprint(a));
        Assert.Equal(a, Of("Save FAILED: disk full"));
    }

    [Fact]
    public void Normalise_AnApostropheInsideAWord_IsNotReadAsAQuotation()
    {
        // Were the two apostrophes read as quotes, everything between them would vanish, and "can't open, don't
        // retry" and "can't save, don't retry" would be one problem.
        Assert.NotEqual(Of("can't open the file, don't retry"), Of("can't save the file, don't retry"));
        Assert.Equal("can't open the file, don't retry", ErrorFingerprint.Normalise("can't open the file, don't retry"));
    }

    [Fact]
    public void Normalise_AWordWithNoDigit_IsNeverTakenForAnId()
    {
        Assert.Equal("Directory facade decade FAILED", ErrorFingerprint.Normalise("Directory facade decade FAILED"));
    }

    [Fact]
    public void Normalise_AVeryLongMessage_IsCut()
    {
        var normalised = ErrorFingerprint.Normalise(new string('x', 5000));

        Assert.Equal(ErrorFingerprint.MaxNormalisedLength, normalised.Length);
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcde", false)]
    [InlineData("../../etc/passwd", false)]
    public void IsFingerprint_OnlyTheShapeOfProducesIsAccepted(string value, bool expected)
    {
        Assert.Equal(expected, ErrorFingerprint.IsFingerprint(value));
    }
}
