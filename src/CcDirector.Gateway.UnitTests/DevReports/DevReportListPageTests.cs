using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// The owner's Reports list, one page at a time (the Reports page timed out on a long-lived account because the list
/// read every report and settled every session behind them on each refresh). The database cuts the page: newest
/// update first, then session, then key, so walking the pages returns every report exactly once even when several
/// share an update time. Over a real, throwaway, fully migrated Gateway database.
/// </summary>
public sealed class DevReportListPageTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-report-pages");
    private static readonly TenantId Other = new("tenant-report-pages-other");
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly DevReportStore _store;

    public DevReportListPageTests()
    {
        _store = new DevReportStore(_h.Open());
    }

    public void Dispose() => _h.Dispose();

    private Guid Publish(string sid, string key, DateTime at, TenantId? tenant = null) =>
        _store.Publish(tenant ?? Tenant, sid, key, "<p>x</p>", "ready", key, at).Report.Id;

    private List<Guid> WalkAll(string? sessionId, int pageSize)
    {
        var seen = new List<Guid>();
        DevReportListPosition? after = null;
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = _store.ListPage(Tenant, sessionId, pageSize, after);
            Assert.True(page.Reports.Count <= pageSize);
            seen.AddRange(page.Reports.Select(r => r.Id));
            if (!page.More) return seen;
            Assert.NotEmpty(page.Reports);
            after = DevReportListPosition.FromMarker(DevReportListPosition.Of(page.Reports[^1]).ToMarker());
            Assert.NotNull(after);
        }
        throw new InvalidOperationException("the list never ended");
    }

    [Fact]
    public void ListPage_FirstPage_IsTheNewestReports_AndSaysThereIsMore()
    {
        for (var i = 0; i < 30; i++) Publish("sid-" + (i % 7), "r" + i, T0.AddMinutes(i));

        var page = _store.ListPage(Tenant, null, 20, null);

        Assert.Equal(20, page.Reports.Count);
        Assert.True(page.More);
        Assert.Equal(Enumerable.Range(10, 20).Reverse().Select(i => "r" + i), page.Reports.Select(r => r.Key));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(100)]
    public void ListPage_WalkingEveryPage_ReturnsEachReportOnce_InTheWholeListsOrder(int pageSize)
    {
        // Ties on the update time across sessions and keys: the order must still be total.
        for (var i = 0; i < 25; i++) Publish("sid-" + (i % 4), "key-" + (i % 6) + "-" + i, T0.AddMinutes(i / 5));

        var walked = WalkAll(null, pageSize);

        var whole = _store.List(Tenant, null)
            .OrderByDescending(r => r.UpdatedAtUtc).ThenBy(r => r.SessionId, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => r.Id).ToList();
        Assert.Equal(25, walked.Count);
        Assert.Equal(whole, walked);
    }

    [Fact]
    public void ListPage_ExactlyAFullPage_SaysThereIsNoMore()
    {
        for (var i = 0; i < 20; i++) Publish("sid", "r" + i, T0.AddMinutes(i));

        Assert.False(_store.ListPage(Tenant, null, 20, null).More);
    }

    [Fact]
    public void ListPage_ForOneSession_PagesThatSessionsReportsOnly()
    {
        for (var i = 0; i < 9; i++) Publish(i % 3 == 0 ? "mine" : "other", "r" + i, T0.AddMinutes(i));

        var walked = WalkAll("mine", 2);

        Assert.Equal(new[] { "r6", "r3", "r0" }, walked.Select(id => _store.Get(Tenant, id)!.Key));
    }

    [Fact]
    public void ListPage_NeverReadsAnotherAccountsReports()
    {
        Publish("sid", "mine", T0);
        Publish("sid", "theirs", T0.AddMinutes(1), Other);

        var page = _store.ListPage(Tenant, null, 20, null);

        Assert.Equal("mine", Assert.Single(page.Reports).Key);
    }

    [Fact]
    public void ListPage_ANewVersionMovesTheReportToTheTop()
    {
        Publish("sid", "old", T0);
        Publish("sid", "new", T0.AddMinutes(1));
        Publish("sid", "old", T0.AddMinutes(2));

        Assert.Equal(new[] { "old", "new" }, _store.ListPage(Tenant, null, 20, null).Reports.Select(r => r.Key));
    }

    [Fact]
    public void Marker_RoundTrips_KeysWithAnyCharacter()
    {
        var position = new DevReportListPosition(T0.AddTicks(1234567), "6f1d2c9e-0000-4000-8000-000000000001", @"C:\work\a b+c/d=.html");

        Assert.Equal(position, DevReportListPosition.FromMarker(position.ToMarker()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a marker")]
    [InlineData("!!!")]
    [InlineData("MTIzNDU")] // "12345": one field, not three
    public void Marker_ThatTheListDidNotMake_IsRefused(string marker)
    {
        Assert.Null(DevReportListPosition.FromMarker(marker));
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    public void ReadPageSize_AcceptsAWholeNumberInRange(string? raw, int expected)
    {
        Assert.Equal(expected, DevReportEndpoints.ReadPageSize(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    [InlineData("2.5")]
    [InlineData("ten")]
    public void ReadPageSize_RefusesAnythingElse(string raw)
    {
        Assert.Null(DevReportEndpoints.ReadPageSize(raw));
    }
}
