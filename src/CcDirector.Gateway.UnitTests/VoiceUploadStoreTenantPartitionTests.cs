using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884: <see cref="VoiceUploadStore"/> is PARTITIONED BY TENANT - the on-disk root and the record -
/// so a caller-supplied upload id is only meaningful inside its own tenant.
///
/// This is the store half. The end-to-end proof that no LEG of the dictation front door can cross an account
/// boundary lives in <c>DictationTenantIsolationTests</c>; what is pinned here is the partition those legs
/// stand on: the directory that makes a cross-tenant read physically impossible, the second (record-borne)
/// check behind it, and the two path aliases that would quietly re-merge two accounts into one folder.
///
/// THE PRODUCTION LINES THESE TESTS PROTECT (each revert-provable - revert it, watch these go red):
///   - <c>VoiceUploadStore.PartitionRootFor</c> - return <c>_partitionBase</c> for every tenant and the
///     isolation assertions go RED (two accounts share one folder again).
///   - <c>VoiceUploadStore.IsMintedAccountTenant</c> - loosen it to a character allow-list and the
///     traversal / casing-alias refusals go RED.
///   - <c>VoiceUploadStore.BelongsHere</c> - the independent DISCLOSURE guard. Neuter it (<c>=&gt; true</c>)
///     and a record physically present in the wrong partition is handed over; the foreign-record refusal
///     goes RED.
///   - <c>VoiceUploadStore.WriteRecordMarker</c>'s tenant stamp - a separate line with a DIFFERENT failure
///     mode, and the distinction is worth keeping crisp. Drop the stamp while <c>BelongsHere</c> stays
///     strict and nothing is disclosed: with the primary partition intact, account records simply become
///     unattributed and are therefore REFUSED - a correctness and availability failure, not a disclosure
///     one. It still earns its own proof, because the accepted design requires the persisted ownership
///     stamp; it just must not be described as demonstrating cross-tenant disclosure.
///   - The partition-container skip in <c>SweepAbandoned</c> - remove it and the sweep deletes every other
///     tenant's staging in one pass, which that test goes RED on.
///
/// IF YOU ARE ADDING A PARTITION TEST HERE, READ THIS FIRST: A RED MUST BE AN ASSERTION, NEVER A CRASH.
///
/// Every test in this file is a canary for a specific mutation, and a canary only pays for itself if its
/// red can NAME what it caught. A NullReferenceException or an input/output error means the mutation broke
/// something before the test could ask its question. It still shows up as a red, which is why it is so
/// easy to accept - but it is evidence that SOMETHING is wrong, not evidence about the line the mutation
/// was aimed at. Counting it launders the mutation into a proof it did not earn.
///
/// Two ways that happens in a PARTITION test specifically, both of which have already bitten this file:
///
///  1. DEREFERENCING A RECORD THAT THE MUTATION DELETED. <c>store.ReadRecord(id)!.State</c> reads fine
///     until a mutation makes the read return null, and then the test dies on the <c>!</c> instead of
///     failing on the claim. Use <see cref="MustStillExist"/>: it asserts presence, with the sentence the
///     test is actually making, before anything touches the record.
///
///  2. THE SETUP ITSELF THROWING - the subtler one, and the one that looks like an environment fault.
///     Several tests here copy one tenant's record file into another tenant's partition to stage the
///     mis-computed-root scenario. When a mutation COLLAPSES the partitions, those two paths become THE
///     SAME PATH, and copying a file onto itself throws a file-in-use error. The test crashes during
///     ARRANGE, before a single assertion runs, and the crash tells you nothing about the record check the
///     test exists for. So ASK THE CROSS-BOUNDARY QUESTION FIRST: assert the two partition paths differ
///     BEFORE the copy. The same mutation then reddens as a plain assertion that says the partitions
///     collapsed - which is exactly what happened. That is why those <c>Assert.NotEqual</c> calls are
///     ordered ahead of the <c>File.Copy</c> calls, and they must stay ahead of them.
///
/// The general rule both cases are instances of: put the assertion that states the boundary claim BEFORE
/// any control, setup, or dereference that a broken boundary could make throw.
/// </summary>
public sealed class VoiceUploadStoreTenantPartitionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-upload-partition-" + Guid.NewGuid().ToString("N"));

    private readonly VoiceUploadStore _base;

    // Two tenants in exactly the form the real registry mints (a canonical lowercase GUID), because the
    // store now enforces that shape - a made-up id would be testing a partition production cannot create.
    private readonly TenantId _tenantA = new(Guid.NewGuid().ToString());
    private readonly TenantId _tenantB = new(Guid.NewGuid().ToString());

    public VoiceUploadStoreTenantPartitionTests() => _base = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Self_host_keeps_the_exact_root_it_has_always_used()
    {
        // Self-host is the CONTROL for this whole change: the local tenant must resolve to the same directory
        // as before, so nothing migrates and no upload moves.
        Assert.Equal(_root, _base.Root);
        Assert.Equal(_root, _base.ForTenant(TenantId.Local).Root);
        Assert.True(_base.ForTenant(TenantId.Local).Tenant.IsLocal);

        var id = Guid.NewGuid().ToString();
        _base.Register(id);
        Assert.True(Directory.Exists(Path.Combine(_root, Guid.Parse(id).ToString("N"))));
        // And the local partition does not sprout a container it never had.
        Assert.False(Directory.Exists(Path.Combine(_root, VoiceUploadStore.TenantPartitionDirectoryName)));
    }

    [Fact]
    public async Task The_same_upload_id_is_two_different_uploads_in_two_tenants()
    {
        // The heart of the fix: an upload id is only meaningful INSIDE its tenant, so the SAME id is legal in
        // both accounts at once and neither can see the other's staging.
        var id = Guid.NewGuid().ToString();
        var a = _base.ForTenant(_tenantA);
        var b = _base.ForTenant(_tenantB);

        a.Register(id);
        await a.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("alpha-audio"), null);

        // Positive control in front of the absence claim: A really did stage bytes under this id.
        Assert.True(a.Exists(id));
        Assert.True(a.StagedBytes(id) > 0);

        // Both directions. B does not see A's upload at all - not its bytes, not even its existence.
        Assert.False(b.Exists(id));
        Assert.Equal(0, b.StagedBytes(id));

        b.Register(id);
        await b.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("bravo-audio"), null);

        var assembledA = await a.AssembleAsync(id, 1);
        var assembledB = await b.AssembleAsync(id, 1);
        Assert.Equal("alpha-audio", Encoding.UTF8.GetString(assembledA.Audio!));
        Assert.Equal("bravo-audio", Encoding.UTF8.GetString(assembledB.Audio!));

        // ...and neither is the LOCAL partition's, which stays empty of this id entirely.
        Assert.False(_base.Exists(id));
    }

    [Fact]
    public void A_terminal_record_is_invisible_and_unresolvable_from_another_tenant()
    {
        // The concrete disclosure from the issue: after A completes upload id X, B posting the same id was
        // handed A's terminal record - and therefore A's TRANSCRIPT.
        var id = Guid.NewGuid().ToString();
        var a = _base.ForTenant(_tenantA);
        var b = _base.ForTenant(_tenantB);

        a.MarkDelivered(id, submitted: true, movedOn: false, transcript: "alpha-secret-transcript");

        // Positive control: A reads its own terminal record back, so the absence claim below is not passing
        // because nothing was ever written.
        var mine = a.ReadRecord(id);
        Assert.NotNull(mine);
        Assert.Equal("alpha-secret-transcript", mine!.Transcript);
        Assert.Equal(_tenantA.Value, mine.Tenant);

        Assert.Null(b.ReadRecord(id));
        Assert.Null(_base.ReadRecord(id));

        // The mirror direction, on the same id: B's own terminal record is equally invisible to A.
        //
        // Read through MustStillExist rather than dereferencing with `!`. Under a mutation that collapses the
        // partitions, B's write lands on A's record and A's read then returns null - and a bare `!` turns that
        // into a NullReferenceException, which is a CRASH, not a proof. A crash says "something upstream
        // broke"; it does not say "A's transcript survived B writing", which is the claim on this line. Asked
        // by assertion, the same mutation reddens here with that sentence.
        b.MarkDelivered(id, submitted: true, movedOn: false, transcript: "bravo-secret-transcript");
        Assert.Equal("bravo-secret-transcript",
            MustStillExist(b, id, "B's own terminal record must be readable in B's partition").Transcript);
        Assert.Equal("alpha-secret-transcript",
            MustStillExist(a, id, "A's transcript must survive B writing the same upload id").Transcript);
    }

    [Fact]
    public void A_record_carrying_another_tenant_is_refused_even_inside_this_partition()
    {
        // The second, independent check. The directory already makes this unreachable, which is exactly why
        // it must be tested directly: if a partition root were ever mis-computed, the record itself still
        // refuses to be handed to the wrong account rather than silently disclosing a transcript.
        var id = Guid.NewGuid().ToString();
        var a = _base.ForTenant(_tenantA);
        var b = _base.ForTenant(_tenantB);

        a.MarkDelivered(id, submitted: true, movedOn: false, transcript: "alpha-secret-transcript");
        b.Register(id);

        // Physically place A's record file inside B's partition - the mis-computed-root scenario.
        var aFile = Path.Combine(a.Root, Guid.Parse(id).ToString("N"), "record.json");
        var bFile = Path.Combine(b.Root, Guid.Parse(id).ToString("N"), "record.json");

        // ASK THE CROSS-BOUNDARY QUESTION FIRST - see the class comment. Under a mutation that collapses
        // the partitions these are the SAME path, and File.Copy of a file onto itself throws during ARRANGE,
        // before any assertion runs, which proves nothing about the record check this test exists for.
        // Asserting the paths differ turns that mutation into a red that says the partitions collapsed.
        // This line must stay AHEAD of the copy.
        Assert.NotEqual(aFile, bFile);
        File.Copy(aFile, bFile, overwrite: true);

        // Positive control: the file really is there and really is A's, so the refusal below is a refusal and
        // not a missing file.
        Assert.Contains("alpha-secret-transcript", File.ReadAllText(bFile));

        // Refused under its OWN name (issue #2745), not reported as absent: "absent" is the one answer that
        // lets a caller open the upload, and opening here would write B's PENDING marker over A's record.
        var refused = b.Read(id);
        Assert.Equal(DictationRecordReadKind.ForeignTenant, refused.Kind);
        Assert.Null(refused.Record);
        Assert.True(refused.Refuses);
        // And the strict read will not quietly hand back null for it either.
        Assert.Throws<UnreadableDictationRecordException>(() => b.ReadRecord(id));

        // The same check on the PENDING projection, which reads records by enumeration rather than by id: a
        // foreign pending record planted in this partition must not lock a session here either.
        var pendingId = Guid.NewGuid().ToString();
        var sessionA = Guid.NewGuid().ToString();
        a.Register(pendingId);
        a.MarkPending(pendingId, sessionA);
        Assert.True(a.IsSessionLocked(sessionA));  // positive control
        b.Register(pendingId);
        var aPending = Path.Combine(a.Root, Guid.Parse(pendingId).ToString("N"), "record.json");
        var bPending = Path.Combine(b.Root, Guid.Parse(pendingId).ToString("N"), "record.json");
        // Ahead of the copy for the reason in the class comment: under a collapse these are the same path
        // and File.Copy would throw during setup, laundering the mutation into a crash-red.
        Assert.NotEqual(aPending, bPending);
        File.Copy(aPending, bPending, overwrite: true);
        Assert.False(b.IsSessionLocked(sessionA));
    }

    [Fact]
    public void The_session_lock_projection_never_crosses_tenants()
    {
        var idA = Guid.NewGuid().ToString();
        var idB = Guid.NewGuid().ToString();
        var sessionA = Guid.NewGuid().ToString();
        var sessionB = Guid.NewGuid().ToString();
        var a = _base.ForTenant(_tenantA);
        var b = _base.ForTenant(_tenantB);

        a.Register(idA);
        a.MarkPending(idA, sessionA);
        b.Register(idB);
        b.MarkPending(idB, sessionB);

        // Positive controls first: each account's own pending upload does lock its own session.
        Assert.True(a.IsSessionLocked(sessionA));
        Assert.True(b.IsSessionLocked(sessionB));

        // Both directions: neither account's pending upload can lock (or be seen by) the other.
        Assert.False(a.IsSessionLocked(sessionB));
        Assert.False(b.IsSessionLocked(sessionA));
        Assert.DoesNotContain(sessionB, a.LockedSessionIds());
        Assert.DoesNotContain(sessionA, b.LockedSessionIds());
        // The local partition sees neither, which is the self-host control.
        Assert.Empty(_base.LockedSessionIds());
    }

    [Theory]
    // The traversal alias: combining the base root with "tenants" and ".." canonicalizes to exactly the base
    // root - the LOCAL partition - so a character allow-list that accepts ".." merges an account into it.
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    // The reserved system identity: no recorded audio belongs to it, so the safe answer is no partition.
    [InlineData("system")]
    public void A_tenant_id_that_is_not_a_minted_account_is_refused_not_normalised(string value)
    {
        // A blank value cannot even construct a TenantId, which is the same refusal one layer earlier.
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Throws<ArgumentException>(() => new TenantId(value));
            return;
        }
        Assert.Throws<ArgumentException>(() => _base.ForTenant(new TenantId(value)));
    }

    [Fact]
    public void An_uppercase_spelling_of_a_minted_tenant_is_refused_rather_than_folded()
    {
        // The casing alias, and the reason refusing beats normalising: Windows and Azure Files name the SAME
        // directory for both spellings, while the tenants table treats them as DIFFERENT identities. Folding
        // the case would hand two identities one folder full of audio; refusing keeps them apart.
        var upper = new TenantId(_tenantA.Value.ToUpperInvariant());
        Assert.Throws<ArgumentException>(() => _base.ForTenant(upper));
    }

    [Fact]
    public void Sweeping_the_local_partition_never_touches_another_tenants_staging()
    {
        // The container holding the other partitions is not an upload. Age-sweeping it would delete every
        // other account's staged audio in one pass - the loudest possible cross-tenant write.
        var id = Guid.NewGuid().ToString();
        var a = _base.ForTenant(_tenantA);
        a.Register(id);
        a.MarkPending(id, Guid.NewGuid().ToString());

        // Positive control: the sweep really does run and really does remove an aged LOCAL upload, so the
        // survival assertion below is not passing because the sweep did nothing at all.
        // A cutoff in the FUTURE (a negative max age), so "aged out" is deterministic without sleeping.
        var localId = Guid.NewGuid().ToString();
        _base.Register(localId);
        Assert.Equal(1, _base.SweepAbandoned(TimeSpan.FromDays(-1)));
        Assert.False(_base.Exists(localId));

        Assert.True(a.Exists(id));
        Assert.Equal(DictationDeliveryState.Pending,
            MustStillExist(a, id, "the sweep must not have destroyed another tenant's pending record").State);
    }

    [Fact]
    public void A_tenant_partition_sweep_removes_that_tenants_abandoned_upload_and_no_others()
    {
        // Issue #1884, finding 2: the voice-turn retention sweep now runs PER TENANT (GatewayHost wraps it in
        // _tenantPass.ForEachTenant and sweeps ForTenant(tenant)). This proves the mechanism that wiring drives:
        // ForTenant(A).SweepAbandoned removes A's OWN aged staging - which the base/Local sweep deliberately
        // never descends into - while leaving B's staging untouched. Without the per-tenant sweep, a hosted
        // tenant's interrupted utterance audio would be retained forever (only the base root was ever swept).
        var a = _base.ForTenant(_tenantA);
        var b = _base.ForTenant(_tenantB);
        var agedA = Guid.NewGuid().ToString();
        var liveB = Guid.NewGuid().ToString();
        a.Register(agedA);
        b.Register(liveB);

        // A cutoff in the FUTURE (a negative max age) so "aged out" is deterministic without sleeping. Sweep
        // A's partition: A's staging is removed; B's partition is a different root and is untouched.
        Assert.Equal(1, a.SweepAbandoned(TimeSpan.FromDays(-1)));
        Assert.False(a.Exists(agedA), "A's own partition sweep must remove A's aged staging");
        Assert.True(b.Exists(liveB), "sweeping A's partition must not touch B's staging");

        // Positive control that the per-tenant sweep is what removes B's too - the base sweep would not.
        Assert.Equal(1, b.SweepAbandoned(TimeSpan.FromDays(-1)));
        Assert.False(b.Exists(liveB));
    }

    /// <summary>
    /// Read a record that the claim under test says MUST still be there, asserting its presence before
    /// touching it.
    ///
    /// This exists so a mutation reddens by ASSERTION rather than by CRASH. Dereferencing with <c>!</c> turns
    /// an absent record into a NullReferenceException, and a NullReferenceException is not evidence about the
    /// line the test was aimed at - it only says something upstream broke before the test could ask its
    /// question. Every red has to be able to name what it caught.
    /// </summary>
    private static DictationDeliveryRecord MustStillExist(VoiceUploadStore store, string uploadId, string claim)
    {
        var record = store.ReadRecord(uploadId);
        Assert.True(record is not null, claim + " (the record was absent)");
        return record!;
    }
}
