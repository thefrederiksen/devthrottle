using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.UnitTests.DevReports;

/// <summary>
/// The words a report uses for the session it came from (phase 3b, issue #3025). The fold is pure - a number
/// and a name in, two finished strings out - so every rule is provable here with no host.
///
/// THE RULE UNDER TEST ABOVE ALL OTHERS: no internal identifier ever reaches these strings. The fold is never
/// handed a session id, so it cannot emit one; what it does when it knows nothing is say a plain true sentence
/// instead, and that is asserted rather than assumed.
/// </summary>
public sealed class DevReportSessionLabelTests
{
    private const string SessionId = "9ee22395-13ef-4157-be4b-4975311135bd";

    [Fact]
    public void Session_NumberAndName_ReadsAsTheOwnerWouldSayIt()
    {
        Assert.Equal("121 devthrottle - tool not working on linux",
            DevReportSessionLabel.Session(121, "devthrottle - tool not working on linux"));
    }

    [Fact]
    public void Back_NumberAndName_IsTheSameWordsWithTheWayBackInFront()
    {
        Assert.Equal("back to 121 devthrottle - tool not working on linux",
            DevReportSessionLabel.Back(121, "devthrottle - tool not working on linux"));
    }

    [Fact]
    public void Session_NameUnknown_IsTheNumberAlone()
    {
        Assert.Equal("121", DevReportSessionLabel.Session(121, null));
        Assert.Equal("121", DevReportSessionLabel.Session(121, "   "));
        Assert.Equal("back to 121", DevReportSessionLabel.Back(121, null));
    }

    [Fact]
    public void Session_NumberUnknown_IsTheNameAlone()
    {
        // Say what you honestly know. A missing number is not a reason to hide the name the owner recognises.
        Assert.Equal("the gateway worker", DevReportSessionLabel.Session(null, "the gateway worker"));
        Assert.Equal("back to the gateway worker", DevReportSessionLabel.Back(null, "the gateway worker"));
    }

    [Fact]
    public void Session_NeitherKnown_IsOnePlainTrueSentence()
    {
        Assert.Equal("the session", DevReportSessionLabel.Session(null, null));
        Assert.Equal("back to the session", DevReportSessionLabel.Back(null, null));
        Assert.Equal("back to the session", DevReportSessionLabel.Back(null, ""));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(121, null)]
    [InlineData(null, "the gateway worker")]
    [InlineData(121, "the gateway worker")]
    public void Session_WhateverIsKnown_NeverContainsASessionIdentifier(int? number, string? name)
    {
        // The fold is never given the id, so this asserts the shape the owner sees rather than the plumbing:
        // no label, in any state of knowledge, carries a hexadecimal identifier.
        var label = DevReportSessionLabel.Session(number, name);
        var back = DevReportSessionLabel.Back(number, name);

        Assert.DoesNotContain(SessionId, label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionId, back, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionId[..8], label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionId[..8], back, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_NameWithNewlinesAndRuns_IsOneLineWithSingleSpaces()
    {
        Assert.Equal("121 a name that wrapped", DevReportSessionLabel.Session(121, "  a name that \n\t wrapped  "));
    }

    [Fact]
    public void Session_NameLongerThanTheCeiling_IsCutAtTheTitleCeiling()
    {
        // The same ceiling a report title uses - one convention on this server, not two.
        var name = new string('x', DevReportTitle.MaxLength + 50);
        var label = DevReportSessionLabel.Session(121, name);

        Assert.Equal("121 " + new string('x', DevReportTitle.MaxLength), label);
    }

    [Fact]
    public void Session_NumberOutsideTheAllocatorsBand_IsRenderedAsTheGatewayHoldsIt()
    {
        // The band is 100-999, so a number is normally three digits already. One outside it is printed as it
        // is - never zero-padded into a shape the allocator never issued.
        Assert.Equal("7 a session", DevReportSessionLabel.Session(7, "a session"));
        Assert.Equal("1042 a session", DevReportSessionLabel.Session(1042, "a session"));
    }
}
