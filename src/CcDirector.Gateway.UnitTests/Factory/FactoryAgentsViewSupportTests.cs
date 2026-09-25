using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Factory;

/// <summary>
/// The pieces around the Factory Agents fold: reading the record page by page (a cut list must say it was cut), and
/// the saved reports ("Make a report from this") held in the account's settings.
/// </summary>
public sealed class FactoryAgentsViewSupportTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

    private static FactoryActivityDto Row(int i) => new()
    {
        Id = Guid.NewGuid(), Factory = "f", FactoryAgent = "a", What = $"row {i}", Outcome = "done", Actor = "x",
        OccurredUtc = Now.AddSeconds(-i), RecordedUtc = Now,
    };

    private static FactoryAgentsSources Sources(IReadOnlyList<FactoryActivityDto> all, FactoryReportStore? reports = null) => new(
        Query: (_, q) =>
        {
            var page = all.Skip(q.Offset).Take(q.Limit).ToList();
            return new FactoryActivityPage { Rows = page, Offset = q.Offset, Limit = q.Limit, HasMore = q.Offset + page.Count < all.Count };
        },
        Append: (_, _, _) => throw new InvalidOperationException("not used"),
        Triggers: _ => Array.Empty<FactoryTriggerFacts>(),
        SetTriggerPaused: (_, _, _, _, _) => Task.FromResult(false),
        LiveSessionIds: _ => new HashSet<string>(),
        TimeZone: _ => TimeZoneInfo.Utc,
        NowUtc: () => Now,
        Reports: reports!,
        Maps: null!);

    [Fact]
    public void ReadAll_PagesThroughEveryRow_AndSaysItWasNotCut()
    {
        var all = Enumerable.Range(0, 2500).Select(Row).ToList();

        var (rows, truncated) = FactoryAgentsViewEndpoints.ReadAll(Sources(all), TenantA,
            new FactoryRecordQuery(null, null, null, null, null, true, 0, 1000));

        Assert.Equal(2500, rows.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadAll_MoreThanTheCeiling_StopsAndSaysItWasCut()
    {
        var all = Enumerable.Range(0, FactoryAgentsViewEndpoints.MaxRowsPerRead + 10).Select(Row).ToList();

        var (rows, truncated) = FactoryAgentsViewEndpoints.ReadAll(Sources(all), TenantA,
            new FactoryRecordQuery(null, null, null, null, null, true, 0, 1000));

        Assert.True(truncated);
        Assert.Equal(FactoryAgentsViewEndpoints.MaxRowsPerRead, rows.Count);
    }

    [Fact]
    public void ReadAll_ARecordThatClaimsMoreAndReturnsNothing_FailsLoud()
    {
        var sources = Sources(Array.Empty<FactoryActivityDto>()) with
        {
            Query = (_, q) => new FactoryActivityPage { Rows = new(), Offset = q.Offset, Limit = q.Limit, HasMore = true },
        };

        Assert.Throws<InvalidOperationException>(() => FactoryAgentsViewEndpoints.ReadAll(sources, TenantA,
            new FactoryRecordQuery(null, null, null, null, null, true, 0, 1000)));
    }

    [Fact]
    public void Inputs_FeedsTheFoldAnEscalationAndItsCorrection_SoWaitingClears()
    {
        var escalation = new FactoryActivityDto
        {
            Id = Guid.NewGuid(), Factory = "f", FactoryAgent = "a", What = "money", Outcome = "escalated", Actor = "x",
            OccurredUtc = Now.AddHours(-3), RecordedUtc = Now.AddHours(-3),
        };
        var fix = new FactoryActivityDto
        {
            Id = Guid.NewGuid(), Factory = "f", FactoryAgent = "a", What = "handled", Outcome = "done", Actor = "owner",
            CorrectsId = escalation.Id, OccurredUtc = Now.AddDays(-2).AddHours(1), RecordedUtc = Now,
        };
        var all = new List<FactoryActivityDto> { escalation, fix };
        var sources = Sources(all) with
        {
            Query = (_, q) =>
            {
                var rows = all.Where(r => (q.Outcome is null || r.Outcome == q.Outcome)
                                          && (q.FromUtc is null || r.OccurredUtc >= q.FromUtc)
                                          && (q.ToUtc is null || r.OccurredUtc < q.ToUtc)).ToList();
                return new FactoryActivityPage { Rows = rows, HasMore = false };
            },
        };

        // Escalated 3 hours ago; the correction carries a time two days EARLIER (a caller sets a row's own time).
        // It must still clear the escalation, so corrections are read with no time bound.
        var window = FactoryAgentsFold.ResolveWindow("last-24h", null, null, Now, "last-24h");
        var inputs = FactoryAgentsViewEndpoints.Inputs(sources, TenantA, window, null, Now);

        Assert.Single(inputs.WaitingCandidates);
        Assert.Contains(inputs.Corrections, c => c.Id == fix.Id);
        Assert.Empty(FactoryAgentsFold.Waiting(inputs, null).Items);
    }

    [Fact]
    public void Inputs_ACutCorrectionsRead_WarnsOnWaitingAndNotOnTheWindow()
    {
        // One open escalation, and more correcting rows of one outcome than one read returns. The correction for the
        // escalation sits past the ceiling, so the list the fold sees cannot clear it - it must say so, not look whole.
        var escalation = new FactoryActivityDto
        {
            Id = Guid.NewGuid(), Factory = "f", FactoryAgent = "a", What = "money", Outcome = "escalated", Actor = "x",
            OccurredUtc = Now.AddHours(-3), RecordedUtc = Now.AddHours(-3),
        };
        var corrections = Enumerable.Range(0, FactoryAgentsViewEndpoints.MaxRowsPerRead + 5)
            .Select(i => new FactoryActivityDto
            {
                Id = Guid.NewGuid(), Factory = "f", FactoryAgent = "a", What = $"fix {i}", Outcome = "done", Actor = "owner",
                CorrectsId = Guid.NewGuid(), OccurredUtc = Now.AddDays(-30).AddSeconds(i), RecordedUtc = Now,
            })
            .ToList();
        var all = new List<FactoryActivityDto> { escalation };
        all.AddRange(corrections);
        var sources = Sources(all) with
        {
            Query = (_, q) =>
            {
                var rows = all.Where(r => (q.Outcome is null || r.Outcome == q.Outcome)
                                          && (q.FromUtc is null || r.OccurredUtc >= q.FromUtc)
                                          && (q.ToUtc is null || r.OccurredUtc < q.ToUtc)).ToList();
                var page = rows.Skip(q.Offset).Take(q.Limit).ToList();
                return new FactoryActivityPage { Rows = page, Offset = q.Offset, Limit = q.Limit, HasMore = q.Offset + page.Count < rows.Count };
            },
        };

        var window = FactoryAgentsFold.ResolveWindow("last-24h", null, null, Now, "last-24h");
        var inputs = FactoryAgentsViewEndpoints.Inputs(sources, TenantA, window, null, Now);

        Assert.True(inputs.WaitingTruncated);
        Assert.False(inputs.WindowTruncated);
        Assert.Equal(FactoryAgentsViewEndpoints.MaxRowsPerRead, inputs.Corrections.Count);
        var waiting = FactoryAgentsFold.Waiting(inputs, null);
        Assert.NotNull(waiting.TruncatedText);
        Assert.Contains("may already be handled", waiting.TruncatedText);
        Assert.Null(FactoryAgentsFold.Activity(inputs, FactoryFilter.None, "/csv").TruncatedText);
    }

    [Fact]
    public void Reports_SaveThenList_RoundTripsForItsOwnAccountOnly()
    {
        using var h = new GatewayDbTestHarness();
        var store = new FactoryReportStore(new TenantSettingsStore(h.Open()));

        var saved = store.Save(TenantA, new SaveFactoryReportRequest { Name = "Weekly blocks", Factory = "website-business", Outcome = "BLOCKED", Window = "last-7d" },
            "owner (device:1)", Now, TimeZoneInfo.Utc);

        var listed = Assert.Single(store.List(TenantA));
        Assert.Equal(saved.Id, listed.Id);
        Assert.Equal("blocked", listed.Outcome);
        Assert.Equal("last-7d", listed.Window);
        Assert.Null(listed.FromUtc);
        Assert.Empty(store.List(TenantB));
        Assert.Equal(saved.Id, store.Find(TenantA, saved.Id)!.Id);
        Assert.Null(store.Find(TenantB, saved.Id));
    }

    [Fact]
    public void Reports_ACustomWindowTypedInTheAccountsZone_IsStoredInUtc()
    {
        using var h = new GatewayDbTestHarness();
        var store = new FactoryReportStore(new TenantSettingsStore(h.Open()));
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus-two", "plus-two");

        var saved = store.Save(TenantA, new SaveFactoryReportRequest
        {
            Name = "Night", Window = "custom",
            FromUtc = new DateTime(2026, 9, 21, 18, 0, 0, DateTimeKind.Unspecified),
            ToUtc = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Unspecified),
        }, "owner", Now, zone);

        Assert.Equal(new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc), saved.FromUtc);
        Assert.Equal(new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc), saved.ToUtc);
    }

    [Theory]
    [InlineData("", "last-7d", null)]
    [InlineData("Name", "last-year", null)]
    [InlineData("Name", "last-7d", "bogus")]
    public void Reports_ARefusedFilterIsNotSaved(string name, string window, string? outcome)
    {
        using var h = new GatewayDbTestHarness();
        var store = new FactoryReportStore(new TenantSettingsStore(h.Open()));

        Assert.Throws<FactoryViewValidationException>(() => store.Save(TenantA,
            new SaveFactoryReportRequest { Name = name, Window = window, Outcome = outcome }, "owner", Now, TimeZoneInfo.Utc));
        Assert.Empty(store.List(TenantA));
    }
}
