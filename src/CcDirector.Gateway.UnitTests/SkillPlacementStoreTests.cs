using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's skill-placement store - the feed that tells this Gateway whether the skills it serves can
/// actually be READ on the machines it serves them to.
///
/// The claims that matter:
///
///  - THE BROKEN CASE IS DETECTED AND SAID OUT LOUD. Serving a skill and an agent being able to read it are
///    different facts; the whole feature exists because they came apart silently on a real machine.
///  - THE VERDICT IS FOLDED ON THE GATEWAY. Status and message are decided here and rendered verbatim, so a
///    client cannot invent a plausible-but-wrong meaning for a row it did not expect.
///  - OVERWRITE, NOT APPEND: a re-report replaces that agent's row, so a problem that has been FIXED stops
///    being reported. A feed that keeps showing a solved problem gets ignored, and then the next real one
///    is ignored with it.
///  - TENANT ISOLATION IN BOTH DIRECTIONS, even when two accounts run identically-named Directors.
/// </summary>
public sealed class SkillPlacementStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime T0 = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private SkillPlacementStore NewStore() => new(_h.Open(new AsyncLocalTenantContext()));

    private static SkillPlacementReportDto Report(
        string agentKind = "ClaudeCode", int held = 3, int reachable = 3,
        bool storeMissing = false, IEnumerable<SkillPlacementProblemDto>? problems = null) =>
        new()
        {
            AgentKind = agentKind,
            Held = held,
            Reachable = reachable,
            StoreMissing = storeMissing,
            Problems = (problems ?? Enumerable.Empty<SkillPlacementProblemDto>()).ToList(),
            ObservedAtUtc = T0,
        };

    private static SkillPlacementProblemDto Shadowed(string id, string target = @"C:\Users\x\.claude\skills") =>
        new() { SkillId = id, Target = target, Fault = "Shadowed" };

    [Fact]
    public void A_machine_where_nothing_landed_is_reported_as_broken_with_a_usable_sentence()
    {
        // THE FAILURE THIS FEED EXISTS FOR, measured on a real machine: a retired installer's leftovers
        // occupied every built-in name, so NOTHING was placed for Claude Code and the agent went on reading
        // a two-month-old copy. The Gateway served everything correctly and had no idea.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SOREN_NORTH", new[]
        {
            Report(held: 3, reachable: 0, problems: new[]
            {
                Shadowed("dev-throttle"), Shadowed("fleet-comms"), Shadowed("move-session"),
            }),
        }, T0);

        var view = store.ReadAll(Alice);

        Assert.True(view.AnyBroken);
        var row = Assert.Single(view.Rows);
        Assert.Equal("broken", row.Status);
        // The sentence has to name the machine, the agent, the count and where to look - a warning that
        // does not say what to do is read once and ignored after that.
        Assert.Contains("0 of 3", row.Message);
        Assert.Contains("ClaudeCode", row.Message);
        Assert.Contains("SOREN_NORTH", row.Message);
        Assert.Contains("dev-throttle", row.Message);
        Assert.Contains(@"C:\Users\x\.claude\skills", row.Message);
    }

    [Fact]
    public void A_skill_kept_by_another_Directors_library_says_so_and_not_could_not_be_linked()
    {
        // Two Directors on one computer share its skill folders (devthrottle_internal#2311). When a team
        // Director yields a name to the person's own library, the sentence must say THAT - "could not be
        // linked" would send somebody hunting for a broken link that does not exist.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SOREN_NORTH", new[]
        {
            Report(held: 2, reachable: 1, problems: new[]
            {
                new SkillPlacementProblemDto
                {
                    SkillId = "fleet-comms", Target = @"C:\Users\x\.agents\skills", Fault = "HeldByAnotherSource",
                },
            }),
        }, T0);

        var row = Assert.Single(store.ReadAll(Alice).Rows);

        Assert.Equal("broken", row.Status);
        Assert.Contains("kept by another Director's library", row.Message);
        Assert.Contains("fleet-comms", row.Message);
        Assert.DoesNotContain("could not be linked", row.Message);
    }

    [Fact]
    public void A_healthy_machine_is_ok_and_does_not_raise_the_badge()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SOREN_NORTH", new[] { Report() }, T0);

        var view = store.ReadAll(Alice);

        Assert.False(view.AnyBroken);
        Assert.Equal("ok", Assert.Single(view.Rows).Status);
    }

    [Fact]
    public void A_Director_that_never_reached_the_Gateway_is_stale_not_broken()
    {
        // These want completely different responses: "your machine cannot read the skills" is a problem to
        // fix, "this Director has not connected yet" is a problem that fixes itself. Collapsing them into
        // one red state would train people to ignore red.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SOREN_NORTH",
            new[] { Report(held: 0, reachable: 0, storeMissing: true) }, T0);

        var view = store.ReadAll(Alice);

        Assert.False(view.AnyBroken);
        Assert.Equal("stale", Assert.Single(view.Rows).Status);
    }

    [Fact]
    public void A_fixed_machine_stops_being_reported_as_broken()
    {
        // OVERWRITE, NOT APPEND. A feed that keeps showing a problem after it is fixed gets ignored, and
        // then the next real problem is ignored with it.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SOREN_NORTH",
            new[] { Report(held: 3, reachable: 0, problems: new[] { Shadowed("move-session") }) }, T0);
        Assert.True(store.ReadAll(Alice).AnyBroken);

        store.StoreBatch(Alice, "director-1", "SOREN_NORTH", new[] { Report(held: 3, reachable: 3) },
            T0.AddMinutes(1));

        var view = store.ReadAll(Alice);
        Assert.False(view.AnyBroken);
        Assert.Equal("ok", Assert.Single(view.Rows).Status);
    }

    [Fact]
    public void Broken_rows_sort_above_healthy_ones()
    {
        // Part of the ruling, not decoration: the one row that needs attention must not be the twentieth
        // thing on the page.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "MACHINE-A", new[]
        {
            Report("Codex", held: 3, reachable: 3),
            Report("ClaudeCode", held: 3, reachable: 0, problems: new[] { Shadowed("move-session") }),
        }, T0);

        var rows = store.ReadAll(Alice).Rows;

        Assert.Equal("ClaudeCode", rows[0].AgentKind);
        Assert.Equal("broken", rows[0].Status);
    }

    [Fact]
    public void One_account_cannot_read_or_overwrite_another_accounts_machine()
    {
        // Both non-tenant key parts are CALLER-supplied, so two accounts can genuinely run identically
        // named Directors. Without the tenant in the key one would overwrite the other.
        var store = NewStore();
        store.StoreBatch(Alice, "director-1", "SHARED-NAME",
            new[] { Report(held: 3, reachable: 0, problems: new[] { Shadowed("move-session") }) }, T0);
        store.StoreBatch(Bob, "director-1", "SHARED-NAME", new[] { Report(held: 3, reachable: 3) }, T0);

        Assert.True(store.ReadAll(Alice).AnyBroken);
        Assert.False(store.ReadAll(Bob).AnyBroken);
        Assert.Single(store.ReadAll(Alice).Rows);
        Assert.Single(store.ReadAll(Bob).Rows);
    }

    [Fact]
    public void A_malformed_report_rejects_the_WHOLE_push()
    {
        // A half-landed batch would show one agent's new answer beside another's old one and call it a
        // single moment in time.
        var store = NewStore();
        var ex = Assert.Throws<SkillPlacementValidationException>(() =>
            store.StoreBatch(Alice, "director-1", "SOREN_NORTH", new[]
            {
                Report("Codex"),
                Report("ClaudeCode", held: 1, reachable: 5),
            }, T0));

        Assert.Contains("cannot exceed", ex.Message);
        Assert.Empty(store.ReadAll(Alice).Rows);
    }

    [Fact]
    public void The_same_agent_named_twice_in_one_push_is_a_producer_bug_and_is_refused()
    {
        // Two rows with the same key would make the stored answer depend on iteration order.
        var store = NewStore();
        Assert.Throws<SkillPlacementValidationException>(() =>
            store.StoreBatch(Alice, "director-1", "SOREN_NORTH",
                new[] { Report("ClaudeCode"), Report("ClaudeCode") }, T0));
    }

    [Fact]
    public void A_push_without_a_director_is_refused()
    {
        var store = NewStore();
        Assert.Throws<SkillPlacementValidationException>(() =>
            store.StoreBatch(Alice, "  ", "SOREN_NORTH", new[] { Report() }, T0));
    }
}
