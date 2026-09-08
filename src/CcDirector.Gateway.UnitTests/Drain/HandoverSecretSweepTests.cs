using System.Text.RegularExpressions;
using CcDirector.ControlApi.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// THE INSTRUMENT IS RUN AGAINST KNOWN-BAD INPUT BEFORE ANY CLEAN RESULT IS BELIEVED.
///
/// A zero from a broken sweep reads exactly like a zero from clean documents. These tests are what make
/// the difference visible: a deliberately dead pattern must be reported as dead, a deliberately greedy
/// one must be reported as greedy, and the sweep must REFUSE to answer at all on either.
/// </summary>
public class HandoverSecretSweepTests
{
    [Fact]
    public void Prove_EveryShippedPatternFiresOnItsOwnKnownBadControl()
    {
        var proof = HandoverSecretSweep.Prove();

        Assert.True(proof.Valid,
            "the shipped sweep failed its own proof: " + string.Join("; ", proof.Failures));
        Assert.Equal(proof.PatternsTotal, proof.PatternsProved);
        Assert.True(proof.PatternsTotal >= 8);
    }

    [Fact]
    public void Prove_ShippedPatternsAreQuietOnCarefulProse()
    {
        // The clean control deliberately contains the WORDS - password, token, key, secret - because that
        // is exactly what a careful handover says. A pattern that fires here would bury real findings.
        Assert.Empty(HandoverSecretSweep.Sweep("clean.md", HandoverSecretSweep.CleanControl));
    }

    [Fact]
    public void Prove_ReportsAPatternThatHasSTOPPEDFIRING()
    {
        // The failure this whole design exists to catch: a pattern that no longer matches anything. It
        // reports zero findings on every document, for ever, and looks exactly like good news.
        var dead = new List<SecretPattern>
        {
            new("dead-on-arrival",
                new Regex("this string appears in no document ever written"),
                "password: NotARealOne123!"),
        };

        var proof = HandoverSecretSweep.Prove(dead, HandoverSecretSweep.CleanControl);

        Assert.False(proof.Valid);
        Assert.Equal(0, proof.PatternsProved);
        Assert.Contains(proof.Failures, f => f.Contains("dead-on-arrival") && f.Contains("did not fire"));
    }

    [Fact]
    public void Prove_ReportsAPatternThatFiresOnEVERYTHING()
    {
        // The mirror failure, and the reason firing-on-bad-input alone is not enough: a pattern that
        // matches anything passes that test and then makes every document look full of secrets.
        var greedy = new List<SecretPattern>
        {
            new("matches-everything", new Regex("."), "password: NotARealOne123!"),
        };

        var proof = HandoverSecretSweep.Prove(greedy, HandoverSecretSweep.CleanControl);

        Assert.False(proof.Valid);
        Assert.Equal(1, proof.PatternsProved);
        Assert.Contains(proof.Failures, f => f.Contains("matches-everything") && f.Contains("clean prose"));
    }

    [Fact]
    public void Prove_AnEmptyPatternSetIsNOTAValidInstrument()
    {
        // Zero patterns prove zero and sweep nothing, and every document comes back clean. That must be a
        // refusal, not a pass.
        var proof = HandoverSecretSweep.Prove(new List<SecretPattern>(), HandoverSecretSweep.CleanControl);
        Assert.False(proof.Valid);
    }

    [Fact]
    public void Sweep_RefusesToAnswerWhenTheInstrumentFailedItsProof()
    {
        var dead = new List<SecretPattern>
        {
            new("dead", new Regex("nothing matches this"), "password: NotARealOne123!"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HandoverSecretSweep.Sweep("doc.md", "password: NotARealOne123!", dead, HandoverSecretSweep.CleanControl));

        Assert.Contains("failed its own proof", ex.Message);
    }

    [Theory]
    [InlineData("The virtual machine password: Zx9!kkQQmm44 and it is on the wiki.", "assigned-password")]
    [InlineData("Set api_key = abcd1234efgh5678ijklmnop before running it.", "assigned-token-or-key")]
    [InlineData("curl -H \"Authorization: Bearer abcdefghijklmnop0123456\" ...", "bearer-header")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private-key-block")]
    [InlineData("Use AKIAQQQQWWWWEEEERRRR for the upload.", "aws-access-key-id")]
    [InlineData("token ghp_aaaabbbbccccddddeeeeffffgggghhhh", "github-token")]
    [InlineData("The bot is xoxb-1111111111-2222222222-abcdefghijkl", "slack-token")]
    [InlineData("key sk-aaaabbbbccccddddeeeeffffgggg", "openai-style-key")]
    public void Sweep_FindsASeededSecretInARealSentence(string line, string expectedPattern)
    {
        var findings = HandoverSecretSweep.Sweep("seeded.md", "## What I did\n\n" + line + "\n");

        Assert.Contains(findings, f => f.PatternName == expectedPattern);
        Assert.All(findings, f => Assert.Equal(3, f.Line));
    }

    [Fact]
    public void Sweep_AFindingDoesNOTCarryTheSecret()
    {
        // The record is stored on the Gateway. A finding that quoted the credential would have copied it
        // off the machine, which is the opposite of what the sweep is for.
        const string Secret = "Zx9kkQQmm44rrSS";
        var findings = HandoverSecretSweep.Sweep("seeded.md", $"The virtual machine password: {Secret}");

        var finding = Assert.Single(findings);
        Assert.DoesNotContain(Secret, finding.RedactedExcerpt);
        Assert.Contains("[REDACTED", finding.RedactedExcerpt);
        Assert.Contains("password", finding.RedactedExcerpt);
    }

    [Fact]
    public void Sweep_ASecondSecretOnTheSameLineIsHiddenToo()
    {
        // Redacting only the match that FIRED left the context either side of it in the clear, so on a
        // line carrying two credentials each finding published the other one - verbatim, into a record
        // that is stored off this machine. Every match from every pattern is hidden before any finding
        // quotes the line.
        const string Password = "Zx9kkQQmm44rrSS";
        const string ApiKey = "abcd1234efgh5678ijklmnop";

        var findings = HandoverSecretSweep.Sweep(
            "seeded.md", $"password: {Password} api_key={ApiKey}");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.DoesNotContain(Password, f.RedactedExcerpt);
            Assert.DoesNotContain(ApiKey, f.RedactedExcerpt);
        });
    }

    [Fact]
    public void Sweep_TwoSecretsWhoseMATCHESOVERLAPAreBothHidden()
    {
        // The residual the second review round found in the fix for the first. Two patterns can match
        // OVERLAPPING regions - the password pattern's value swallows the token that follows it - and
        // editing them independently right to left replaced the inner one first, shortened the string,
        // and pushed the outer one past the end. Its bounds check then failed, it was skipped, and the
        // first secret survived into every finding.
        const string Password = "FIRSTSECRETVALUE";
        const string Token = "ghp_aaaabbbbccccddddeeeeffffgggg";

        var findings = HandoverSecretSweep.Sweep("seeded.md", $"password: {Password},{Token}");

        Assert.NotEmpty(findings);
        Assert.All(findings, f =>
        {
            Assert.DoesNotContain(Password, f.RedactedExcerpt);
            Assert.DoesNotContain(Token, f.RedactedExcerpt);
        });
    }

    [Fact]
    public void RedactLine_DoesNotTRUNCATEWhatItIsAskedToProtect()
    {
        // REDACTION AND SHORTENING ARE DIFFERENT JOBS, and they were the same method. The 300-character
        // cap belongs to a FINDING EXCERPT, which is read in a report; applying it to a value being
        // STORED silently chopped the tail off everything the boundary guard touched - a restore command
        // with its seed path at the end, a problem sentence, a seat's own words. A guard against losing
        // information that loses information is worse than the leak it closed, because the leak was at
        // least visible. Three drain tests caught it and this one pins it.
        var long_ = "cc-devthrottle session spawn " + new string('x', 400) + " --prompt END-OF-COMMAND";

        var redacted = HandoverSecretSweep.RedactLine(long_);

        Assert.Equal(long_, redacted);
        Assert.EndsWith("END-OF-COMMAND", redacted);
        Assert.DoesNotContain("...", redacted);
    }

    [Fact]
    public void RedactLine_StillHidesASecretInALongValue()
    {
        // And the other half: no truncation must not mean no redaction.
        const string Secret = "Zx9kkQQmm44rrSS";
        var long_ = new string('x', 400) + $" password: {Secret} " + new string('y', 400);

        var redacted = HandoverSecretSweep.RedactLine(long_);

        Assert.DoesNotContain(Secret, redacted);
        Assert.Contains("[REDACTED", redacted);
        Assert.EndsWith("yyy", redacted);
    }

    [Fact]
    public void Sweep_AFindingExcerptIsStillCapped()
    {
        // The cap did not go away, it went where it belongs: a report excerpt is read by a person and is
        // shortened; a stored value is not.
        var text = new string('x', 400) + " password: NotARealOne123! " + new string('y', 400);

        var finding = Assert.Single(HandoverSecretSweep.Sweep("d.md", text));

        Assert.True(finding.RedactedExcerpt.Length < text.Length);
        Assert.EndsWith("...", finding.RedactedExcerpt);
    }

    [Fact]
    public void Sweep_ReportsTheLineNumberSoSomebodyCanGoAndFixIt()
    {
        var text = "one\ntwo\nthree\npassword: NotARealOne123!\nfive";
        var finding = Assert.Single(HandoverSecretSweep.Sweep("d.md", text));
        Assert.Equal(4, finding.Line);
        Assert.Equal("d.md", finding.File);
    }

    [Fact]
    public void Sweep_EmptyAndNullDocumentsAreCleanRatherThanAnError()
    {
        Assert.Empty(HandoverSecretSweep.Sweep("d.md", null));
        Assert.Empty(HandoverSecretSweep.Sweep("d.md", ""));
    }
}
