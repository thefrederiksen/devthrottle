using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two account tenants these tests partition between, and - the point of this type existing at all -
/// the NON-CANONICAL SPELLINGS of one, WITH THE FIXTURE'S OWN PREMISE ASSERTED.
///
/// WHY THIS IS A TYPE AND NOT TWO CONSTANTS. The first version of these tests built an "uppercase alias" by
/// calling <c>ToUpperInvariant()</c> on an ALL-NUMERIC identifier. Uppercasing a string with no letters in
/// it changes nothing, so the alias was byte-for-byte the canonical value: the tests asserted that a
/// non-canonical spelling is refused while never once presenting a non-canonical spelling. They were not
/// weak tests - their premise never occurred, so they could not fail in the direction they were written to
/// catch, and they went red for the opposite reason. A fixture is a CLAIM ABOUT THE INPUT, and an unasserted
/// claim about the input is exactly as unreliable as an unasserted claim about the output.
///
/// TWO THINGS FIX IT, AND THE SECOND IS THE ONE THAT LASTS. First, the identifiers now contain hexadecimal
/// LETTERS, so case actually varies. But choosing better characters is a fact about today's constants that
/// the next edit can quietly undo, so second - and this is the durable half - <see cref="AliasesOf"/>
/// ASSERTS ITS OWN PREMISE before returning: every spelling it hands back must differ from the canonical
/// value, and must still denote the SAME underlying identifier (otherwise the test would be presenting a
/// different tenant, which the store should refuse for an entirely different reason and would prove nothing
/// about spelling). If someone later changes these constants back to all-digit values, the fixture fails
/// LOUDLY at the claim rather than silently ceasing to test anything.
///
/// The alias set deliberately includes two forms that differ for ANY identifier - the dash-less "N" form and
/// the braced "B" form - so the premise no longer depends on the fixture happening to contain letters. That
/// is the design half of the same fix: make the condition impossible to miss rather than remembering to
/// choose inputs that hit it.
/// </summary>
internal static class NetDiagTenantFixture
{
    /// <summary>A canonical minted account tenant, in the EXACT form the registry mints (lowercase "D").
    /// Contains hexadecimal letters, so case conversion genuinely produces a different string.</summary>
    public static readonly TenantId TenantA = new("aaaaaaaa-1111-4c1a-8b1a-aaaaaaaaaaaa");

    /// <summary>A second canonical minted account tenant, the one the first must never reach.</summary>
    public static readonly TenantId TenantB = new("bbbbbbbb-2222-4c2b-8b2b-bbbbbbbbbbbb");

    /// <summary>
    /// Every non-canonical SPELLING of <paramref name="tenant"/> a test should present to a partition key -
    /// the same identifier, written a way this system does not mint. The premise is asserted here, once, for
    /// every caller: each spelling differs from the canonical text (or it is not an alias at all) and each
    /// parses back to the same identifier (or it is a different tenant, not a different spelling).
    /// </summary>
    public static IEnumerable<string> AliasesOf(TenantId tenant)
    {
        var canonical = tenant.Value;
        var parsed = Guid.Parse(canonical);

        var aliases = new[]
        {
            canonical.ToUpperInvariant(),  // same identifier, upper case
            parsed.ToString("N"),          // same identifier, no dashes
            parsed.ToString("B"),          // same identifier, braced
        };

        foreach (var alias in aliases)
        {
            Assert.NotEqual(canonical, alias);          // it really is a DIFFERENT spelling...
            Assert.Equal(parsed, Guid.Parse(alias));    // ...of the SAME identifier.
        }

        return aliases;
    }
}

/// <summary>
/// Unsafe-collection census rows 21 and 22: the diagnostic RESULT store and the hourly quality ROLLUP.
///
/// THE DEFECT. <c>POST /diag/result</c> wrote into one process-global result list and folded into one
/// process-global hour bucket; <c>GET /diag/results</c> and <c>GET /diag/rollup</c> read those same globals.
/// Every authenticated caller, on any tenant, saw every tenant's diagnostic history and could add to every
/// tenant's aggregate. There is NO caller-supplied identifier anywhere in this shape to namespace - the hour
/// comes from server time and the result carries no id - so the remedy is a PARTITION, not a prefix.
///
/// TWO HALVES, AND EITHER ALONE IS WORTH NOTHING. These tests deliberately prove the WRITE STAMP and the
/// READ FILTER separately, because a fix to one masks a hole in the other. A write-only fix still leaks on
/// the reads. A read-only filter is worse than it looks: it is a DEFERRED leak, because cross-tenant data
/// keeps accumulating behind it, so the day the filter is lifted or bypassed it exposes a contaminated
/// history that was written wrong all along. The store-level tests below state the write half directly
/// (does the record land in - and ONLY in - the writing tenant's partition), and the endpoint tests state
/// the read half through real authenticated HTTP.
///
/// OVERLAPPING TIME BUCKETS ARE THE POINT for the rollup, and they come for free: both tenants POST within
/// the same test, so the server-derived UTC hour is the SAME hour for both. That is exactly the collision
/// the census row describes - a shared aggregate that nobody can attribute afterwards, so a fold by one
/// tenant silently poisons the other's numbers.
///
/// A POSITIVE CONTROL ON EVERY CROSS-TENANT ASSERTION. Each "B cannot see A's data" assertion is paired with
/// "A CAN see A's data" on the same route in the same test. Without that pairing an empty answer passes for
/// isolation when what really happened is a failed seed - the strongest possible isolation result and the
/// weakest possible evidence, indistinguishable from the outside.
///
/// STATUS AND MEDIA TYPE ARE ASSERTED BEFORE ANY PARSE. Parsing is itself an assertion about format: a
/// mutation that makes a route serve HTML, or 403, or 404 would otherwise redden these tests as a
/// JsonReaderException, which proves only that something upstream broke. An assertion says WHAT was served.
///
/// PRE-REGISTERED MUTATIONS AND PREDICTED REDS (one primitive per full-suite run, never combined - a
/// combined revert mis-attributes). Each of these can be individually wrong while everything else stays
/// correct and isolation still breaks, so each earns its own run; overlap does not exempt any of them.
///
///   M1  POST /diag/result passes <c>TenantId.Local</c> instead of the resolved request tenant (write half).
///       PREDICT RED: Two_tenants_writing_in_the_same_hour_do_not_see_each_others_results,
///       Two_tenants_writing_in_the_same_hour_do_not_share_a_rollup_bucket,
///       A_result_lands_only_in_the_writing_tenants_partition (endpoint form).
///       PREDICT GREEN (controls): every self-host control below, and the store-level tests, which do not
///       go through the route.
///   M2  GET /diag/results passes <c>TenantId.Local</c> instead of the resolved request tenant (read half).
///       PREDICT RED: Two_tenants_writing_in_the_same_hour_do_not_see_each_others_results.
///   M3  GET /diag/rollup passes <c>TenantId.Local</c> instead of the resolved request tenant (read half).
///       PREDICT RED: Two_tenants_writing_in_the_same_hour_do_not_share_a_rollup_bucket.
///   M4  Delete the three <c>reqTenant is null</c> deny blocks, letting a tenant-less key fall through.
///       PREDICT RED: A_key_with_no_bound_tenant_is_denied_on_every_diagnostic_route (both cases) and
///       A_key_with_no_bound_tenant_cannot_write_a_diagnostic_result.
///   M5  In both stores, delete the <c>parsed.Tenants is null -> DeletePrePartitionFile()</c> branch.
///       PREDICT RED: A_pre_partition_result_file_is_deleted_from_the_live_store_not_migrated,
///       A_pre_partition_rollup_file_is_deleted_from_the_live_store_not_migrated.
///   M6  In <see cref="NetDiagResultStore"/>, prune against the TOTAL record count across partitions rather
///       than the writing tenant's own list.
///       PREDICT RED: One_tenants_flood_does_not_evict_another_tenants_results.
///   M7  In both stores' <c>CanonicalTenantKey</c>, drop the ordinal round-trip comparison from
///       <c>IsMintedAccountTenant</c> so any parseable identifier is accepted in any spelling.
///       PREDICT RED: An_alternate_spelling_of_a_minted_tenant_is_refused_on_the_write_and_the_read,
///       An_alternate_spelling_of_a_minted_tenant_is_refused_on_the_fold_and_the_read.
///
/// A predicted red that does not appear is a FINDING, not something to explain away: it means the test does
/// not actually reach the line the mutation changed. That is not hypothetical here - the FIRST version of the
/// two alias tests could not reach their own condition at all (see <see cref="NetDiagTenantFixture"/>), which
/// is why the fixture now asserts its own premise.
/// </summary>
public sealed class NetDiagResultStoreTenantPartitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cc-netdiag-part-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "diagnostics-results.json");

    private static readonly TenantId TenantA = NetDiagTenantFixture.TenantA;
    private static readonly TenantId TenantB = NetDiagTenantFixture.TenantB;

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static NetDiagResultDto Result(string tag) => new() { Verdict = tag };

    [Fact]
    public void A_result_lands_only_in_the_writing_tenants_partition()
    {
        // THE WRITE HALF, stated on its own. Nothing here reads through a filter that could be covering for
        // an unstamped write: the record is placed by tenant, and the other tenant's partition is a
        // different list entirely.
        var store = new NetDiagResultStore(Path_);
        store.Add(TenantA, Result("a-only"));

        // Positive control FIRST - if this is empty the seed failed and the next assertion is meaningless.
        Assert.Equal(new[] { "a-only" }, store.Recent(TenantA).Select(r => r.Verdict));
        Assert.Empty(store.Recent(TenantB));
    }

    [Fact]
    public void Neither_tenant_can_mutate_or_suppress_the_others_results()
    {
        // The census's completion predicate names read, mutate, suppress and contend. Writing is the only
        // mutation this store offers, so "B writes a lot" is the whole attack, and A's list must be
        // untouched in content AND in order.
        var store = new NetDiagResultStore(Path_);
        store.Add(TenantA, Result("a1"));
        store.Add(TenantA, Result("a2"));
        for (int i = 0; i < 25; i++) store.Add(TenantB, Result($"b{i}"));

        Assert.Equal(new[] { "a2", "a1" }, store.Recent(TenantA).Select(r => r.Verdict));
        Assert.DoesNotContain(store.Recent(TenantA), r => r.Verdict!.StartsWith("b", StringComparison.Ordinal));
        Assert.DoesNotContain(store.Recent(TenantB), r => r.Verdict!.StartsWith("a", StringComparison.Ordinal));
    }

    [Fact]
    public void One_tenants_flood_does_not_evict_another_tenants_results()
    {
        // CONTENTION, which a partition alone does not close. If the MaxRecords cap were still counted
        // across all partitions, a noisy tenant would push a quiet tenant's history out of the store just by
        // writing - suppression achieved without ever reading anything. The cap is per tenant.
        var store = new NetDiagResultStore(Path_);
        store.Add(TenantA, Result("a-survivor"));
        for (int i = 0; i < NetDiagResultStore.MaxRecords + 50; i++)
            store.Add(TenantB, Result($"b{i}"));

        var a = store.Recent(TenantA);
        Assert.Equal(new[] { "a-survivor" }, a.Select(r => r.Verdict));
        Assert.Equal(NetDiagResultStore.MaxRecords, store.Recent(TenantB).Count);
    }

    [Fact]
    public void Partitions_survive_a_reload_intact()
    {
        var store = new NetDiagResultStore(Path_);
        store.Add(TenantA, Result("a-persisted"));
        store.Add(TenantB, Result("b-persisted"));

        var reopened = new NetDiagResultStore(Path_);
        Assert.Equal(new[] { "a-persisted" }, reopened.Recent(TenantA).Select(r => r.Verdict));
        Assert.Equal(new[] { "b-persisted" }, reopened.Recent(TenantB).Select(r => r.Verdict));
    }

    [Fact]
    public void A_pre_partition_result_file_is_deleted_from_the_live_store_not_migrated()
    {
        // THE ARCHITECT'S RULING, made executable. The old file is one flat list with NO per-tenant
        // attribution recorded anywhere in it. Migrating it would INVENT an attribution that was never
        // recorded - the forbidden half-partition - and would hand one tenant another tenant's data.
        // Quarantining it would merely park a live liability on disk that nothing will ever be able to
        // attribute. Diagnostics are ephemeral operational data with no durability contract, so the
        // purge costs nothing real.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
        {
          "results": [
            { "verdict": "pre-partition-1", "route": "tailscale" },
            { "verdict": "pre-partition-2", "route": "lan" }
          ]
        }
        """);

        var store = new NetDiagResultStore(Path_);

        // Not migrated into ANY partition - not the writing tenant's, not Local, not System.
        Assert.Empty(store.Recent(TenantA));
        Assert.Empty(store.Recent(TenantB));
        Assert.Empty(store.Recent(TenantId.Local));
        Assert.Empty(store.Recent(TenantId.System));

        // Not quarantined either: the live file is REMOVED, not renamed aside. This states deletion from
        // the LIVE STORE and nothing wider - a test that writes and deletes a file cannot speak for a share
        // snapshot, a soft-deleted copy or a backup of the same path, which are outside this process.
        Assert.False(File.Exists(Path_), "the pre-partition file must be deleted, not left in place");
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));

        // And the store is usable afterward, writing the new partitioned shape.
        store.Add(TenantA, Result("after-purge"));
        Assert.Equal(new[] { "after-purge" }, store.Recent(TenantA).Select(r => r.Verdict));
        Assert.Empty(store.Recent(TenantB));
    }

    [Fact]
    public void An_unreadable_file_is_still_quarantined_not_deleted()
    {
        // CONTROL for the purge: the two paths must stay distinct. A file we could not READ is evidence of a
        // bug and is preserved; a file we read perfectly whose CONTENTS are a cross-tenant mixture is
        // deleted. Collapsing either into the other loses something - evidence in one direction, or the
        // ruling in the other.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not valid json");

        var store = new NetDiagResultStore(Path_);

        Assert.Empty(store.Recent(TenantId.Local));
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void A_tenant_shape_this_system_does_not_mint_is_refused_not_coerced()
    {
        // A partition key that is not canonicalized is not a partition (one account reachable under two
        // spellings, or two accounts colliding on one). The store accepts the well-known Local and System
        // identities and the exact minted account form, and refuses anything else rather than storing it.
        var store = new NetDiagResultStore(Path_);

        Assert.Throws<ArgumentException>(() => store.Add(new TenantId("not-a-guid"), Result("x")));
        Assert.Throws<ArgumentException>(() => store.Recent(new TenantId("../escape")));
        Assert.Throws<ArgumentException>(() => store.Add(default, Result("x")));
    }

    [Fact]
    public void An_alternate_spelling_of_a_minted_tenant_is_refused_on_the_write_and_the_read()
    {
        // The SAME account written a way this system does not mint must not open a second partition, and
        // must not reach the first one either. The fixture asserts its own premise - see
        // NetDiagTenantFixture.AliasesOf - so an alias that is not actually a different spelling fails there
        // rather than passing here for the wrong reason.
        var store = new NetDiagResultStore(Path_);
        store.Add(TenantA, Result("canonical-only"));

        foreach (var alias in NetDiagTenantFixture.AliasesOf(TenantA))
        {
            Assert.Throws<ArgumentException>(() => store.Add(new TenantId(alias), Result("x")));
            Assert.Throws<ArgumentException>(() => store.Recent(new TenantId(alias)));
        }

        // Positive control: the canonical spelling still works and still holds exactly what was written, so
        // the refusals above are about the SPELLING and not about the store having become unusable.
        Assert.Equal(new[] { "canonical-only" }, store.Recent(TenantA).Select(r => r.Verdict));
    }
}

/// <summary>
/// The rollup half of census row 22, at the store level. See
/// <see cref="NetDiagResultStoreTenantPartitionTests"/> for the mutation register that covers both.
/// </summary>
public sealed class NetDiagRollupStoreTenantPartitionTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 20, 12, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime T0SameHour = new(2026, 7, 20, 12, 55, 0, DateTimeKind.Utc);

    private static readonly TenantId TenantA = NetDiagTenantFixture.TenantA;
    private static readonly TenantId TenantB = NetDiagTenantFixture.TenantB;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cc-netdiagroll-part-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "netdiag-rollup.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Folds_in_the_same_hour_do_not_share_a_bucket()
    {
        // THE OVERLAPPING TIME BUCKET the map demands. T0 and T0SameHour are the SAME UTC hour, which is what
        // every concurrent tenant gets from server time - so before the partition these three folds were one
        // bucket and one tenant's numbers were the sum of everybody's.
        var store = new NetDiagRollupStore(Path_);
        store.Fold(TenantA, T0, latencyMs: 40, direct: true, isLanPath: true, downMbps: 100, upMbps: 10);
        store.Fold(TenantA, T0SameHour, latencyMs: 44, direct: true, isLanPath: true, downMbps: 80, upMbps: 8);
        store.Fold(TenantB, T0SameHour, latencyMs: 900, direct: false, isLanPath: false, downMbps: 1, upMbps: 1);

        var a = Assert.Single(store.All(TenantA));
        var b = Assert.Single(store.All(TenantB));

        Assert.Equal(NetDiagRollupStore.HourKey(T0), a.Hour);
        Assert.Equal(NetDiagRollupStore.HourKey(T0), b.Hour); // same hour, different partition

        // Positive control on both sides: each tenant's own folds ARE there...
        Assert.Equal(2, a.Count);
        Assert.Equal(1, b.Count);

        // ...and neither carries a trace of the other. B's 900ms relay sample must not touch A's numbers.
        Assert.Equal(2, a.DirectCount);
        Assert.Equal(0, a.RelayCount);
        Assert.Equal(84, a.SumLatencyLan);
        Assert.Equal(180, a.SumDownLan);
        Assert.Equal(0, a.AwayCount);

        Assert.Equal(0, b.DirectCount);
        Assert.Equal(1, b.RelayCount);
        Assert.Equal(900, b.SumLatencyAway);
        Assert.Equal(0, b.LanCount);
    }

    [Fact]
    public void One_tenants_retention_prune_does_not_reach_into_anothers_history()
    {
        // Pruning is driven by the CLOCK OF THE FOLDING TENANT. Sharing one bucket map would let a tenant
        // folding "now" delete another tenant's older-than-retention history as a side effect - deletion, the
        // fourth verb in the census's completion predicate, achieved without touching that tenant at all.
        var store = new NetDiagRollupStore(Path_);
        store.Fold(TenantA, T0 - TimeSpan.FromDays(30), 44, true, true, null, null);
        store.Fold(TenantB, T0, 44, true, true, null, null); // B's prune runs against B's partition only

        Assert.Single(store.All(TenantA));
        Assert.Equal(NetDiagRollupStore.HourKey(T0 - TimeSpan.FromDays(30)), store.All(TenantA)[0].Hour);
    }

    [Fact]
    public void Partitions_survive_a_reload_intact()
    {
        var store = new NetDiagRollupStore(Path_);
        store.Fold(TenantA, T0, 40, true, true, null, null);
        store.Fold(TenantB, T0, 900, false, false, null, null);

        var reopened = new NetDiagRollupStore(Path_);
        Assert.Equal(1, Assert.Single(reopened.All(TenantA)).LanCount);
        Assert.Equal(1, Assert.Single(reopened.All(TenantB)).AwayCount);
    }

    [Fact]
    public void A_pre_partition_rollup_file_is_deleted_from_the_live_store_not_migrated()
    {
        // Worse than the raw-results case, and the same ruling for a stronger reason: each old bucket is a
        // SUM over every tenant that folded into that hour. The addends cannot be separated after the fact,
        // so attributing a bucket to a tenant hands that tenant a number that is provably not its own.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
        {
          "buckets": {
            "2026-07-20T12": { "hour": "2026-07-20T12", "count": 7, "sumLatencyMs": 700, "directCount": 7 }
          }
        }
        """);

        var store = new NetDiagRollupStore(Path_);

        Assert.Empty(store.All(TenantA));
        Assert.Empty(store.All(TenantB));
        Assert.Empty(store.All(TenantId.Local));
        Assert.Empty(store.All(TenantId.System));

        Assert.False(File.Exists(Path_), "the pre-partition rollup file must be deleted, not left in place");
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));

        store.Fold(TenantA, T0, 40, true, true, null, null);
        Assert.Equal(1, Assert.Single(store.All(TenantA)).Count);
        Assert.Empty(store.All(TenantB));
    }

    [Fact]
    public void An_unreadable_rollup_file_is_still_quarantined_not_deleted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ not json at all");

        var store = new NetDiagRollupStore(Path_);

        Assert.Empty(store.All(TenantId.Local));
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void A_tenant_shape_this_system_does_not_mint_is_refused_not_coerced()
    {
        var store = new NetDiagRollupStore(Path_);

        Assert.Throws<ArgumentException>(() => store.Fold(new TenantId("not-a-guid"), T0, 40, true, true, null, null));
        Assert.Throws<ArgumentException>(() => store.All(new TenantId("../escape")));
        Assert.Throws<ArgumentException>(() => store.Fold(default, T0, 40, true, true, null, null));
    }

    [Fact]
    public void An_alternate_spelling_of_a_minted_tenant_is_refused_on_the_fold_and_the_read()
    {
        var store = new NetDiagRollupStore(Path_);
        store.Fold(TenantA, T0, 40, true, true, null, null);

        foreach (var alias in NetDiagTenantFixture.AliasesOf(TenantA))
        {
            Assert.Throws<ArgumentException>(() => store.Fold(new TenantId(alias), T0, 40, true, true, null, null));
            Assert.Throws<ArgumentException>(() => store.All(new TenantId(alias)));
        }

        // Positive control: the canonical spelling still reaches its own single bucket, so the refusals are
        // about the SPELLING rather than about the store having become unusable.
        Assert.Equal(1, Assert.Single(store.All(TenantA)).Count);
    }
}

/// <summary>
/// The READ half of census rows 21 and 22, proved through the real hosted Gateway over authenticated HTTP
/// with two separately enrolled, tenant-bound device keys. The store-level tests above cannot state this:
/// they prove the partition holds when it is asked correctly, and say nothing about whether the ROUTE asks
/// correctly. This class is the only thing that proves the routes resolve the tenant from the caller's own
/// credential rather than serving the whole store.
///
/// See <see cref="NetDiagResultStoreTenantPartitionTests"/> for the pre-registered mutation register.
/// </summary>
[Collection("DirectorRoot")]
public sealed class NetDiagEndpointTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";
    private string _keyNoTenant = "";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-netdiag-ep-" + Guid.NewGuid().ToString("N"));
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");

        // The diagnostic stores resolve their file from CcStorage.Root(), so the test gets its OWN root -
        // otherwise this test would read and WRITE the developer's real diagnostics files, and a stale one
        // from a previous run could seed a pass. [Collection("DirectorRoot")] serializes it against the
        // other tests that move this process-wide variable.
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);

        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        // A device row with no canonical tenant binding is not a valid hosted credential, so this caller must
        // be refused rather than quietly served the Local partition.
        _keyNoTenant = _gateway.Devices.Register("dev-none", "MC").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Two_tenants_writing_in_the_same_hour_do_not_see_each_others_results()
    {
        await PostResult(_keyA, "alice-result");
        await PostResult(_keyB, "bob-result");

        var aVerdicts = await ResultVerdicts(_keyA);
        var bVerdicts = await ResultVerdicts(_keyB);

        // POSITIVE CONTROL on each side FIRST. Without it, two empty lists would pass this test perfectly
        // while the truth was that neither POST ever landed.
        Assert.Contains("alice-result", aVerdicts);
        Assert.Contains("bob-result", bVerdicts);

        Assert.DoesNotContain("bob-result", aVerdicts);
        Assert.DoesNotContain("alice-result", bVerdicts);
    }

    [Fact]
    public async Task A_result_lands_only_in_the_writing_tenants_partition()
    {
        // THE WRITE HALF AT THE ROUTE. Only A ever writes; B only reads. If the route stamped the wrong
        // tenant on the write, A's own read would come back without it - which a read-filter fix alone
        // cannot rescue, and which the read-leak test above would not distinguish from a failed seed.
        await PostResult(_keyA, "written-by-alice");

        Assert.Contains("written-by-alice", await ResultVerdicts(_keyA));
        Assert.Empty(await ResultVerdicts(_keyB));
    }

    [Fact]
    public async Task Two_tenants_writing_in_the_same_hour_do_not_share_a_rollup_bucket()
    {
        // Both POSTs happen within this test, so the Gateway stamps them with the SAME server UTC hour -
        // the overlapping bucket the census row is about. A folds twice, B once.
        await PostResult(_keyA, "a1", latency: 40, direct: true, lan: true);
        await PostResult(_keyA, "a2", latency: 44, direct: true, lan: true);
        await PostResult(_keyB, "b1", latency: 900, direct: false, lan: false);

        var a = await Rollup(_keyA);
        var b = await Rollup(_keyB);

        // Positive control: each side sees its OWN folds.
        var aBucket = Assert.Single(a);
        var bBucket = Assert.Single(b);
        Assert.Equal(2, aBucket.Count);
        Assert.Equal(1, bBucket.Count);

        // Same hour on both sides - so this is a partition, not an accident of timing.
        Assert.Equal(aBucket.Hour, bBucket.Hour);

        // NO AGGREGATE POISONING: B's single 900ms relay sample is nowhere in A's numbers, and vice versa.
        Assert.Equal(2, aBucket.DirectCount);
        Assert.Equal(0, aBucket.RelayCount);
        Assert.Equal(84, aBucket.SumLatencyMs);

        Assert.Equal(0, bBucket.DirectCount);
        Assert.Equal(1, bBucket.RelayCount);
        Assert.Equal(900, bBucket.SumLatencyMs);
    }

    [Theory]
    [InlineData("diag/results")]
    [InlineData("diag/rollup")]
    public async Task A_key_with_no_bound_tenant_is_denied_on_every_diagnostic_route(string path)
    {
        // DENY BY DEFAULT. A hosted key with no tenant is rejected by authentication; serving it the Local
        // partition would be a wrong-tenant read.
        var resp = await Get(path, _keyNoTenant);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_key_with_no_bound_tenant_cannot_write_a_diagnostic_result()
    {
        // The write half of the same deny. A read-only refusal would be a DEFERRED leak: unattributable
        // records would keep accumulating behind it with nowhere correct to put them.
        var resp = await PostResultRaw(_keyNoTenant, "no-tenant");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the tenant work must not have opened these routes up as a side effect of running before
        // the host-wide auth gate.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("diag/results")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("diag/rollup")).StatusCode);
    }

    // ---- helpers ----

    private Task<HttpResponseMessage> PostResultRaw(string deviceKey, string verdict,
        double? latency = 40, bool direct = true, bool lan = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "diag/result")
        {
            Content = JsonContent.Create(new NetDiagResultDto
            {
                Verdict = verdict,
                LatencyMedianMs = latency,
                Direct = direct,
                IsLanPath = lan,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task PostResult(string deviceKey, string verdict,
        double? latency = 40, bool direct = true, bool lan = true)
    {
        var resp = await PostResultRaw(deviceKey, verdict, latency, direct, lan);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    /// <summary>Read GET /diag/results, asserting the status and media type BEFORE parsing - so a mutation
    /// that changes WHAT is served reddens as a statement rather than as a parser crash.</summary>
    private async Task<List<string>> ResultVerdicts(string deviceKey)
    {
        var resp = await Get("diag/results", deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("verdict", out var v) ? v.GetString() ?? "" : "")
            .ToList();
    }

    /// <summary>Read GET /diag/rollup with the same status-and-media-type-before-parse discipline.</summary>
    private async Task<List<NetDiagRollupStore.HourBucket>> Rollup(string deviceKey)
    {
        var resp = await Get("diag/rollup", deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(body))
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        return JsonSerializer.Deserialize<List<NetDiagRollupStore.HourBucket>>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

}

/// <summary>
/// SELF-HOST IS STATED, NOT INHERITED. With the Gateway built on <c>Hosted = false</c> there is one tenant -
/// Local - and all three diagnostic routes must behave exactly as they always did. Leaning on the older
/// endpoint tests would prove nothing here, because those never say which mode they run in.
///
/// This is a CONTROL for the mutation register in <see cref="NetDiagResultStoreTenantPartitionTests"/>: it
/// must stay GREEN under every one of M1 through M4, because on self-host the resolved tenant IS Local and
/// there is no second tenant to leak to. A control that moves with the change under test is not a control.
/// </summary>
[Collection("DirectorRoot")]
public sealed class NetDiagSelfHostControlTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-netdiag-self-" + Guid.NewGuid().ToString("N"));
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");

        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A device with NO account binding at all - on self-host that is still the one Local tenant, which
        // is the behaviour that must not have changed.
        _key = _gateway.Devices.Register("dev-self", "MSELF").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_result_posted_on_self_host_is_readable_back_and_folded_into_the_rollup()
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "diag/result")
        {
            Content = JsonContent.Create(new NetDiagResultDto
            {
                Verdict = "self-host", LatencyMedianMs = 40, Direct = true, IsLanPath = true,
            }),
        };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(post)).StatusCode);

        var results = await Get("diag/results");
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        Assert.Equal("application/json", results.Content.Headers.ContentType?.MediaType);
        Assert.Contains("self-host", await results.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var rollup = await Get("diag/rollup");
        Assert.Equal(HttpStatusCode.OK, rollup.StatusCode);
        Assert.Equal("application/json", rollup.Content.Headers.ContentType?.MediaType);

        var body = await rollup.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.EnumerateArray().Single().GetProperty("count").GetInt32());
    }

    private Task<HttpResponseMessage> Get(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return _http.SendAsync(req);
    }

}
