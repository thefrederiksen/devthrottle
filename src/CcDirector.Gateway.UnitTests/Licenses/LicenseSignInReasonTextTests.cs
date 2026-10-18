using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The sentence a sign-in row shows for its reason word. A word this Gateway has no sentence for is shown as the bare
/// word, so every word the contract names must have a sentence of its own.
/// </summary>
public sealed class LicenseSignInReasonTextTests
{
    [Fact]
    public void ReasonText_EveryRenewalWordTheContractNames_HasItsOwnSentence()
    {
        var sentences = LicenseRenewalErrors.All.Select(word => (word, text: LicenseSignInFold.ReasonText(word)!)).ToList();

        Assert.All(sentences, s => Assert.NotEqual(s.word, s.text));
        Assert.All(sentences, s => Assert.EndsWith(".", s.text));
        Assert.Equal(sentences.Count, sentences.Select(s => s.text).Distinct().Count());
    }

    [Fact]
    public void ReasonText_NeedsFreshSignIn_SaysTheApprovalPageAskedForOne()
    {
        Assert.Equal("The approving browser profile needs a fresh claude.ai sign-in: the approval page asked for one.",
            LicenseSignInFold.ReasonText(LicenseRenewalErrors.NeedsFreshSignIn));
    }
}
