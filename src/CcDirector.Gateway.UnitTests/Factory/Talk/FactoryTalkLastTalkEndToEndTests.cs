using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// A TALK REACHES THE FACTORY'S PAGE (Factories screen mission, phase C, end to end on the Gateway side). The talk seed
/// tells the agent to record one <c>talked</c> line; this writes exactly that line through the real activity record -
/// what <c>cc-devthrottle factory record</c> calls - and reads the factory's page through the page route's own input
/// reader and fold, so the "Last talk with you" line is proven from the row, not from a hand-built fold input.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactoryTalkLastTalkEndToEndTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
    private const string TalkSession = "7d2c0e4e-0000-4000-8000-00000000c0de";

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static RegisterFactoryRequest WarmForward() => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        Computer = "SOREN_NORTH",
        BossSeat = "nora-hale",
        Seats = { new FactorySeatManifest { Id = "nora-hale", Name = "Boss", Role = "Boss", BriefFile = "agents/ceo.yaml" } },
    };

    [Fact]
    public void TheTalkedLineTheSeedAsksFor_ShowsOnThePageAsLastTalkWithYou()
    {
        var db = _h.Open();
        var record = new FactoryActivityRecord(db);
        var registry = new FactoryRegistryStore(db);
        var registered = registry.Register(TenantId.Local, WarmForward(), "the owner (test)", Now);
        var settings = new TenantSettingsStore(db);
        var sources = new FactoriesScreenSources(
            new FactoryAgentsSources(
                Query: (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
                Append: (tenant, request, actor) => record.Append(tenant, request, actor),
                Triggers: _ => Array.Empty<FactoryTriggerFacts>(),
                SetTriggerPaused: (_, _, _, _, _) => Task.FromResult(false),
                LiveSessionIds: _ => new HashSet<string>(),
                TimeZone: _ => TimeZoneInfo.Utc,
                NowUtc: () => Now,
                Reports: new FactoryReportStore(settings),
                Maps: new FactoryMapStore(settings)),
            registry,
            _ => Array.Empty<CronJobDto>(),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>());

        FactoryPageViewDto Page() => FactoriesScreenFold.Page(registered,
            FactoriesScreenEndpoints.Inputs(sources, TenantId.Local, FactoryAgentsFold.WindowLast7d, "warmforward"));

        Assert.Equal("None yet.", Page().LastTalk.Text);

        // The line the seed tells the talk to record, for this factory and this seat.
        var seed = FactoryTalkSeed.Compose(registered, registered.Seats[0], scheduleId: null, scheduleSeed: null);
        Assert.Contains("cc-devthrottle factory record --factory warmforward --agent nora-hale --outcome talked", seed);
        record.Append(TenantId.Local, new AppendFactoryActivityRequest
        {
            Factory = "warmforward",
            FactoryAgent = "nora-hale",
            Outcome = FactoryActivityOutcome.Talked,
            What = "Decided: bunkie back to 13 C.",
            SessionId = TalkSession,
            OccurredUtc = Now.AddMinutes(-50),
        }, "session " + TalkSession);

        Assert.Equal("Talked with you, today 07:10 - Decided: bunkie back to 13 C.", Page().LastTalk.Text);
    }
}
