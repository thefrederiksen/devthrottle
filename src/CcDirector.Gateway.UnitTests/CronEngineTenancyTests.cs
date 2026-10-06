using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Cross-tenant partition of the cron overlap guard (audit MED, gap audit-e). A cron job's id is
/// tenant-relative: <see cref="CronJobStore"/> mints a short <c>cj_</c> id whose uniqueness it checks only
/// THROUGH the tenant query filter, and the legacy import preserves caller-supplied ids, so two tenants can
/// hold a job with the SAME id (the database identity is (TenantId, Id)). Before the fix the engine's
/// in-flight set was keyed by the bare id alone, so tenant A's in-flight run made tenant B's run-now of B's
/// OWN same-id job be rejected as an "overlap". This test reproduces that cross-tenant denial on a single
/// engine (as in production, one <see cref="CronEngine"/> serves every tenant) and pins the fix; reverting
/// the key to the bare id reddens <see cref="Run_now_for_one_tenant_is_not_blocked_by_another_tenants_same_id_job"/>.
/// </summary>
public sealed class CronEngineTenancyTests : IDisposable
{
    private const string SharedId = "cj_shared";
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Run_now_for_one_tenant_is_not_blocked_by_another_tenants_same_id_job()
    {
        // One database, one AMBIENT tenant context the stores and the engine both read per-operation - exactly
        // the production shape (the store scopes by _tenantContext.Current, the engine keys by the same seam).
        var ambient = new AsyncLocalTenantContext();
        var db = _h.Open(ambient);

        // Seed the SAME job id under BOTH tenants via the legacy import (which preserves the supplied id),
        // each construction inside its own tenant scope so the row lands in that tenant's partition.
        CronJobStore store;
        CronRunHistoryStore history;
        using (ambient.Enter(TenantA))
        {
            WriteLegacyJob(_h.LegacyPath("a.json"), SharedId);
            store = new CronJobStore(db, _h.LegacyPath("a.json"));
            history = new CronRunHistoryStore(db, _h.LegacyPath("a.runs.json"));
        }
        using (ambient.Enter(TenantB))
        {
            WriteLegacyJob(_h.LegacyPath("b.json"), SharedId);
            _ = new CronJobStore(db, _h.LegacyPath("b.json"));   // throwaway: seeds B's row into the same db
        }

        var starter = new GatedStarter();
        var engine = new CronEngine(
            store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(),
            new FixedClock(DateTime.UtcNow), resolveTenant: () => ambient.CurrentOrNull);

        // Tenant A's run-now enters flight and parks inside the starter (its synchronous part - the store read,
        // the tenant resolve, and TryEnterFlight - all run under scope A before the first await).
        Task<CronRunNowResult> firstRun;
        using (ambient.Enter(TenantA))
            firstRun = engine.RunNowAsync(SharedId, CancellationToken.None);
        await starter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While A is still in flight, tenant B fires ITS OWN same-id job. With the guard partitioned by tenant
        // this is admitted and fires; with the bare-id guard it is wrongly rejected as an overlap.
        CronRunNowResult second;
        using (ambient.Enter(TenantB))
            second = await engine.RunNowAsync(SharedId, CancellationToken.None);
        Assert.Equal(CronFireOutcome.Fired, second.Outcome);

        // Let A finish; it fired too. Both tenants' sessions actually started - the two runs never collided.
        starter.Release.TrySetResult();
        var first = await firstRun;
        Assert.Equal(CronFireOutcome.Fired, first.Outcome);
        Assert.Equal(2, starter.StartCount);
    }

    [Fact]
    public async Task Same_tenant_overlap_is_still_skipped()
    {
        // The overlap guard is preserved WITHIN a tenant: a second run-now of the same job while the first is
        // in flight (same tenant) is still skipped.
        var ambient = new AsyncLocalTenantContext();
        var db = _h.Open(ambient);

        CronJobStore store;
        CronRunHistoryStore history;
        using (ambient.Enter(TenantA))
        {
            WriteLegacyJob(_h.LegacyPath("a.json"), SharedId);
            store = new CronJobStore(db, _h.LegacyPath("a.json"));
            history = new CronRunHistoryStore(db, _h.LegacyPath("a.runs.json"));
        }

        var starter = new GatedStarter();
        var engine = new CronEngine(
            store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(),
            new FixedClock(DateTime.UtcNow), resolveTenant: () => ambient.CurrentOrNull);

        Task<CronRunNowResult> firstRun;
        using (ambient.Enter(TenantA))
            firstRun = engine.RunNowAsync(SharedId, CancellationToken.None);
        await starter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        CronRunNowResult second;
        using (ambient.Enter(TenantA))
            second = await engine.RunNowAsync(SharedId, CancellationToken.None);
        Assert.Equal(CronFireOutcome.SkippedOverlap, second.Outcome);

        starter.Release.TrySetResult();
        Assert.Equal(CronFireOutcome.Fired, (await firstRun).Outcome);
        Assert.Equal(1, starter.StartCount);   // only the first run ever started a session
    }

    [Fact]
    public void Get_ForANamedAccount_ReadsThatAccountsJob_WhateverTheAmbientAccount()
    {
        // The Factories screen's Talk button reads a seat's schedule seed for the account its route resolved, not
        // whatever scope happens to be in effect: the same id in two accounts must answer with the named one's.
        var ambient = new AsyncLocalTenantContext();
        var db = _h.Open(ambient);
        CronJobStore store;
        using (ambient.Enter(TenantA))
        {
            WriteLegacyJob(_h.LegacyPath("a.json"), SharedId, seed: "seed of A");
            store = new CronJobStore(db, _h.LegacyPath("a.json"));
        }
        using (ambient.Enter(TenantB))
        {
            WriteLegacyJob(_h.LegacyPath("b.json"), SharedId, seed: "seed of B");
            _ = new CronJobStore(db, _h.LegacyPath("b.json"));
        }

        using (ambient.Enter(TenantA))
        {
            Assert.Equal("seed of B", store.Get(TenantB, SharedId)?.Action.Seed);
            Assert.Equal("seed of A", store.Get(TenantA, SharedId)?.Action.Seed);
            Assert.Null(store.Get(new TenantId("33333333-3333-3333-3333-333333333333"), SharedId));
            Assert.Null(store.Get(TenantA, " "));
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static void WriteLegacyJob(string path, string id, string seed = "/help")
    {
        var job = new CronJobDto
        {
            Id = id,
            Name = "shared",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = "0 0 * * *",
            TimeZoneId = "America/Chicago",
            Target = new CronJobTarget { Machine = "workstation-A" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = seed },
            PreventOverlap = true,
        };
        var json = JsonSerializer.Serialize(
            new { jobs = new[] { job } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json);
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    /// <summary>Blocks ONLY its first start (the parked in-flight run); every later start returns at once, so a
    /// second tenant's run is not itself held by the gate under test.</summary>
    private sealed class GatedStarter : ICronSessionStarter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int StartCount => _calls;

        public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            return ($"sid-{n}", "director-1", null);
        }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }
}
