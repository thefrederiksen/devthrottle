using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests.DevReports;

/// <summary>
/// The key that tells one session's reports tab from another's (issue #3019). The document-tab machinery
/// keys tabs on a path and re-uses a tab whose key already matches, so this key decides two things the owner
/// sees: pressing Reports twice returns to the tab already open, and two sessions get two tabs rather than
/// one showing the wrong reports.
///
/// It is deliberately not a file path. Nothing opens it, and the tests below hold it to that so a later edit
/// cannot quietly turn it into something the file viewers would try to read.
/// </summary>
public sealed class DevReportsPaneTabKeyTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public void TabKeyFor_TheSameSessionTwice_IsTheSameKey()
        => Assert.Equal(DevReportsPaneControl.TabKeyFor(A), DevReportsPaneControl.TabKeyFor(A));

    [Fact]
    public void TabKeyFor_TwoSessions_AreDifferentKeys()
        => Assert.NotEqual(DevReportsPaneControl.TabKeyFor(A), DevReportsPaneControl.TabKeyFor(B));

    [Fact]
    public void TabKeyFor_NamesTheSessionAndIsNotAFilePath()
    {
        var key = DevReportsPaneControl.TabKeyFor(A);

        Assert.Equal("dev-reports:" + A.ToString("D"), key);
        Assert.False(Path.IsPathRooted(key), "the reports tab key must not read as a file path");
        Assert.Equal("", Path.GetExtension(key));
    }
}
