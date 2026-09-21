using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The Factory Agents pages wired to the real activity record (Website Business Factory, product track): the
/// record read and written in the account the ROUTE resolved, the "started" rows for exactly the sessions on
/// screen, and the "factory agent" chip stamped by the roster fold from them - and by nothing else.
/// </summary>
public sealed class FactoryRecordWiringTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly TenantId Alpha = new("alpha");
    private static readonly TenantId Beta = new("beta");

    private static AppendFactoryActivityRequest Row(string outcome, string? sessionId, string agent = "front-desk",
        DateTime? occurred = null) => new()
    {
        Factory = "website-business",
        FactoryAgent = agent,
        Outcome = outcome,
        What = "Answered a new customer's question.",
        SessionId = sessionId,
        Actor = "trigger:new-mail",
        OccurredUtc = occurred,
    };

    // ---------------------------------------------------------------------------------------------------------
    // The record, in the account the route resolved.

    [Fact]
    public void Append_ToAnExplicitAccount_IsReadInThatAccountOnly_WhateverTheAmbientAccount()
    {
        // The ambient account is beta; the route resolved alpha. The row belongs to alpha.
        var record = new FactoryActivityRecord(_h.Open(new FixedTenantContext(Beta)));

        var written = record.Append(Alpha, Row(FactoryActivityOutcome.Done, "s-1"), "owner (device)");

        Assert.Equal(written.Id, Assert.Single(record.Query(Alpha).Rows).Id);
        Assert.Empty(record.Query(Beta).Rows);
        Assert.Empty(record.Query().Rows);
    }

    [Fact]
    public void Query_WithSessionIds_ReturnsOnlyRowsNamingThoseSessions()
    {
        var record = new FactoryActivityRecord(_h.Open());
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-1"), null);
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-2"), null);
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, null), null);

        var rows = record.Query(TenantId.Local, sessionIds: new[] { "s-2", "s-9" }).Rows;

        Assert.Equal("s-2", Assert.Single(rows).SessionId);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The "started" rows for the sessions on screen.

    [Fact]
    public void Read_KeepsTheFirstStartedRowPerSession_AndIgnoresEveryOtherOutcome()
    {
        var record = new FactoryActivityRecord(_h.Open());
        var t0 = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc);
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Done, "s-1", agent: "bookkeeper", occurred: t0.AddMinutes(-5)), null);
        var first = record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-1", agent: "front-desk", occurred: t0), null);
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-1", agent: "other", occurred: t0.AddMinutes(5)), null);
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-off-screen", occurred: t0), null);

        var started = FactorySessionStarts.Read(record, TenantId.Local, new[] { "s-1", "s-plain" });

        var row = Assert.Single(started).Value;
        Assert.Equal(first.Id, row.Id);
        Assert.Equal("front-desk", row.FactoryAgent);
    }

    [Fact]
    public void Read_NoSessionsOnScreen_ReadsNothing()
    {
        var record = new FactoryActivityRecord(_h.Open());
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-1"), null);

        Assert.Empty(FactorySessionStarts.Read(record, TenantId.Local, Array.Empty<string>()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The chip, stamped by the roster fold.

    [Fact]
    public void RosterFold_StampsTheChipOnTheSessionAFactoryAgentStarted_AndNullOnEveryOther()
    {
        var record = new FactoryActivityRecord(_h.Open());
        record.Append(TenantId.Local, Row(FactoryActivityOutcome.Started, "s-factory"), null);
        var factorySession = new SessionDto { SessionId = "s-factory", ActivityState = "Working", Agent = "ClaudeCode" };
        var plain = new SessionDto { SessionId = "s-plain", ActivityState = "Working", Agent = "ClaudeCode" };
        var sessions = new List<SessionDto> { factorySession, plain };
        IReadOnlyCollection<string>? asked = null;

        GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, tenant: TenantId.Local,
            factoryStarts: (tenant, ids) => { asked = ids; return FactorySessionStarts.Read(record, tenant, ids); });

        Assert.NotNull(factorySession.FactoryAgent);
        Assert.Equal("Factory agent", factorySession.FactoryAgent!.Label);
        Assert.Null(plain.FactoryAgent);
        Assert.Equal(new[] { "s-factory", "s-plain" }, asked);
    }

    [Fact]
    public void RosterFold_SwitchOff_ClearsAnEchoedChip()
    {
        var echoed = new SessionDto
        {
            SessionId = "s-1", ActivityState = "Working", Agent = "ClaudeCode",
            FactoryAgent = new SessionFactoryAgentDto { Label = "Factory agent", Text = "echo" },
        };
        var sessions = new List<SessionDto> { echoed };

        GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, tenant: TenantId.Local, factoryStarts: null);

        Assert.Null(echoed.FactoryAgent);
    }

    [Fact]
    public void PushedSessionStore_StripsAChipADirectorSent()
    {
        const string director = "director-factory-chip";
        var store = new PushedSessionStore();
        store.RegisterConnection(TenantId.Local, director, "conn-1");
        var pushed = new SessionDto
        {
            SessionId = "s-1", ActivityState = "Working", Agent = "ClaudeCode",
            FactoryAgent = new SessionFactoryAgentDto { Label = "Factory agent", Text = "made up by a Director" },
        };

        Assert.True(store.ApplySnapshot(TenantId.Local, director, "conn-1", 1, new[] { pushed }));

        Assert.Null(Assert.Single(store.GetLastKnown(TenantId.Local, director).Sessions).FactoryAgent);
    }
}
