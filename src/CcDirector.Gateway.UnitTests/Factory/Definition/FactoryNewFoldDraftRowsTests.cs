using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// The "Not published yet" rows under the Factories list: each draft is drawn as a card beside the published factories,
/// so it carries the pill word and tone, and its seat count, from the Gateway (Factories screen cleanup, 17 Oct 2026).
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoryNewFoldDraftRowsTests
{
    [Fact]
    public void DraftRows_ADraft_CarriesItsPillAndSeatCount()
    {
        var rows = FactoryNewFold.DraftRows(new[] { new UnpublishedFactory("erik", "Erik Laursen", 1, 4, 3, "devlinux") });

        var row = Assert.Single(rows);
        Assert.Equal("DRAFT", row.StateWord);
        Assert.Equal(FactoryTone.Grey, row.StateTone);
        Assert.Equal("3 seats", row.SeatCountText);
        Assert.Equal("Erik Laursen - draft, not published yet", row.Text);
        Assert.Equal("Version 1, a draft, 4 files. Open it to read what changes and publish it.", row.LineText);
    }

    [Fact]
    public void DraftRows_OneSeat_SaysSeatOnce()
    {
        var rows = FactoryNewFold.DraftRows(new[] { new UnpublishedFactory("eric", "Eric", 2, 3, 1, null) });
        Assert.Equal("1 seat", Assert.Single(rows).SeatCountText);
    }
}
