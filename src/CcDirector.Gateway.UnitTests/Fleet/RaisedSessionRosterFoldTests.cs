using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The finished values the raise and lower control renders (critical rule 7: the client is dumb). Each row carries
/// what it IS and the ONE thing it offers, in words the client shows verbatim - so the client never decides what
/// raised means. That the roster route really stamps them is proven in <c>RaisedSessionHostTests</c>.
/// </summary>
public sealed class RaisedSessionRosterFoldTests
{
    private const string A = "42000000-0000-4000-8000-000000000001";
    private const string B = "42000000-0000-4000-8000-000000000002";
    private const string Gone = "42000000-0000-4000-8000-000000000003";

    private static SessionDto Row(string id, string state = "WaitingForInput") => new() { SessionId = id, ActivityState = state };

    [Fact]
    public void Stamp_RaisedRow_SaysRaised_AndOffersOnlyLower()
    {
        var rows = new[] { Row(A), Row(B) };

        RaisedSessionRosterFold.Stamp(rows, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { A.ToUpperInvariant() });

        var raised = rows[0].Raise!;
        Assert.True(raised.Raised);
        Assert.Equal("Raised", raised.Mark);
        Assert.False(string.IsNullOrWhiteSpace(raised.MarkTitle));
        Assert.Equal(SessionRaiseDto.OfferLower, raised.Offer);
        Assert.Equal("Lower", raised.Label);
        Assert.False(string.IsNullOrWhiteSpace(raised.Title));
        Assert.False(string.IsNullOrWhiteSpace(raised.BusyLabel));
    }

    [Fact]
    public void Stamp_RowThatIsNotRaised_CarriesNoMark_OffersOnlyRaise_AndAsksFirst()
    {
        var rows = new[] { Row(A), Row(B) };

        RaisedSessionRosterFold.Stamp(rows, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { A });

        var plain = rows[1].Raise!;
        Assert.False(plain.Raised);
        Assert.True(string.IsNullOrEmpty(plain.Mark));
        Assert.Equal(SessionRaiseDto.OfferRaise, plain.Offer);
        Assert.Equal("Raise", plain.Label);
        // Raising is the dangerous direction, so the Gateway supplies the question the client must ask.
        Assert.Contains("Raise this session?", plain.Confirm);
    }

    [Fact]
    public void Stamp_SessionThatHasEnded_IsNotRaised_AndOffersNothing_EvenIfItsIdIsOnTheList()
    {
        var rows = new[] { Row(Gone, "Exited") };

        RaisedSessionRosterFold.Stamp(rows, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Gone });

        var ended = rows[0].Raise!;
        Assert.False(ended.Raised);
        Assert.True(string.IsNullOrEmpty(ended.Offer));
        Assert.True(string.IsNullOrEmpty(ended.Label));
    }

    [Fact]
    public void Stamp_OverwritesWhateverADirectorSent()
    {
        var forged = Row(A);
        forged.Raise = RaisedSessionRosterFold.RaisedRow();

        RaisedSessionRosterFold.Stamp(new[] { forged }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.False(forged.Raise!.Raised);
        Assert.Equal(SessionRaiseDto.OfferRaise, forged.Raise.Offer);
    }
}
