using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>A report's status in words: what the viewer shows beside the title, never the code (rule 7). A contributor
/// reads no word about agents (Team implementation 4.1).</summary>
public sealed class DevReportStatusLabelTests
{
    [Theory]
    [InlineData("waiting-on-you", false, "Waiting on you")]
    [InlineData("agent-working", false, "Agent working")]
    [InlineData("done", false, "Done")]
    [InlineData("waiting-on-you", true, "Waiting on you")]
    [InlineData("agent-working", true, "Being worked on")]
    [InlineData("done", true, "Done")]
    public void For_EveryStatusAReportCanCarry_HasItsWords(string status, bool contributor, string expected)
    {
        Assert.Equal(expected, DevReportStatusLabel.For(status, contributor));
    }

    [Fact]
    public void For_EveryStatusTheShapeCheckAccepts_HasWords_AndAContributorsNeverNameTheAgent()
    {
        foreach (var status in DevReportShapeCheck.Statuses)
        {
            Assert.False(string.IsNullOrWhiteSpace(DevReportStatusLabel.For(status, contributor: false)));
            Assert.DoesNotContain("agent", DevReportStatusLabel.For(status, contributor: true), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void For_AStatusNoReportCanCarry_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DevReportStatusLabel.For("draft", contributor: false));
    }
}
