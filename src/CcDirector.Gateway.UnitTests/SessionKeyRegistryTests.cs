using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's record of per-SESSION credentials (Remove-the-network-port mission, phase 1b).
///
/// The property this whole registry exists to hold is that the Gateway never sees a key it could present
/// as a session - it registers a HASH, verifies a HASH, and stores a HASH. The rest is lifecycle: one live
/// key per session, revoked when the session is reaped, lapsed when nobody revoked it, and a database
/// failure that denies rather than grants.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class SessionKeyRegistryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private static readonly TenantId Account = new("tenant-a");
    private static readonly TenantId OtherAccount = new("tenant-b");

    public void Dispose() => _harness.Dispose();

    private SessionKeyRegistry Registry(Func<DateTime>? clock = null)
        => new(_harness.Open(), isHosted: false, clock);

    private static DateTime Later => DateTime.UtcNow.AddHours(12);

    [Fact]
    public void A_registered_key_authenticates_as_its_own_session()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();

        Assert.True(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), Later));

        var resolution = registry.ResolveCredential(key);
        Assert.Equal(SessionCredentialResolutionKind.Active, resolution.Kind);
        Assert.Equal(session, resolution.Identity!.SessionId);
        Assert.Equal(Account, resolution.Identity.Tenant);
        Assert.Equal("director-1", resolution.Identity.DirectorId);
    }

    [Fact]
    public void The_raw_key_is_never_stored()
    {
        // The claim is not "the registry does not return the key" - it is that the key is not IN it. Read
        // the row back through the database and prove the value appears nowhere in it. A registry that
        // held the key would let anyone who reached the database present themselves as any session.
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), Later);

        using var ctx = _harness.Open().CreateUnscopedContext();
        var row = ctx.SessionKeys.Single();

        Assert.DoesNotContain(key, row.KeyHash, StringComparison.Ordinal);
        Assert.DoesNotContain(key, row.SessionId, StringComparison.Ordinal);
        Assert.DoesNotContain(key, row.DirectorId, StringComparison.Ordinal);
        Assert.DoesNotContain(key, row.TenantId, StringComparison.Ordinal);
        Assert.Equal(GatewaySessionKey.Hash(key), row.KeyHash);
    }

    [Fact]
    public void An_unknown_key_is_unknown_not_a_grant()
    {
        var registry = Registry();
        registry.Register(Account, "director-1", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(GatewaySessionKey.Mint()), Later);

        var resolution = registry.ResolveCredential(GatewaySessionKey.Mint());

        Assert.Equal(SessionCredentialResolutionKind.Unknown, resolution.Kind);
        Assert.Null(resolution.Identity);
    }

    [Fact]
    public void A_blank_key_resolves_to_nothing()
    {
        var registry = Registry();
        Assert.Equal(SessionCredentialResolutionKind.Unknown, registry.ResolveCredential(null).Kind);
        Assert.Equal(SessionCredentialResolutionKind.Unknown, registry.ResolveCredential("").Kind);
    }

    [Fact]
    public void Re_registering_a_session_rotates_its_key_and_ends_the_previous_one()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var first = GatewaySessionKey.Mint();
        var second = GatewaySessionKey.Mint();

        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(first), Later);
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(second), Later);

        // ONE row, so a revocation ends the session's credential rather than one of several.
        Assert.Equal(1, registry.Count);
        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(second).Kind);
        Assert.Equal(SessionCredentialResolutionKind.Unknown, registry.ResolveCredential(first).Kind);
    }

    [Fact]
    public void A_revoked_key_is_refused_and_says_which_session_it_was()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), Later);

        Assert.True(registry.Revoke(Account, session.ToString(), SessionKeyRegistry.ReasonSessionReaped));

        var resolution = registry.ResolveCredential(key);
        Assert.Equal(SessionCredentialResolutionKind.Revoked, resolution.Kind);
        // The identity is still returned on a refusal, so the log can name the session that was refused.
        // A refusal that says only "no" is a refusal nobody can debug.
        Assert.Equal(session, resolution.Identity!.SessionId);
    }

    [Fact]
    public void Revoking_twice_is_not_an_error()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(GatewaySessionKey.Mint()), Later);

        Assert.True(registry.Revoke(Account, session.ToString(), SessionKeyRegistry.ReasonSessionReaped));
        Assert.False(registry.Revoke(Account, session.ToString(), SessionKeyRegistry.ReasonSessionReaped));
    }

    [Fact]
    public void A_revoked_session_is_not_revived_by_a_re_registration()
    {
        // THE RACE THIS EXISTS FOR: a session is reaped and its key revoked, and a tunnel reseed that was
        // already in flight re-registers it. If the re-registration won, a credential the Director
        // deliberately ended would come back to life for a session that no longer exists.
        var registry = Registry();
        var session = Guid.NewGuid();
        var original = GatewaySessionKey.Mint();
        var replacement = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(original), Later);
        registry.Revoke(Account, session.ToString(), SessionKeyRegistry.ReasonSessionReaped);

        Assert.False(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(replacement), Later));

        Assert.Equal(SessionCredentialResolutionKind.Revoked, registry.ResolveCredential(original).Kind);
        Assert.Equal(SessionCredentialResolutionKind.Unknown, registry.ResolveCredential(replacement).Kind);
    }

    [Fact]
    public void Another_account_cannot_revoke_this_accounts_session_key()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), Later);

        Assert.False(registry.Revoke(OtherAccount, session.ToString(), "someone-elses-idea"));
        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);
    }

    // ---------- Registration is where the takeover was ----------
    //
    // The suite already proved another account cannot REVOKE this account's row. It never tried to
    // REGISTER over it, and that was the hole: registration looked the row up by bare session id on an
    // unscoped context and then overwrote its tenant, its Director and its hash without comparing any of
    // them. Revocation was guarded; registration was the same takeover by another verb.

    [Fact]
    public void Another_account_cannot_register_over_this_accounts_session_key()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var mine = GatewaySessionKey.Mint();
        var theirs = GatewaySessionKey.Mint();
        Assert.True(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(mine), Later));

        // The other tenant registering the SAME session id must not touch my row. Whether its own
        // registration succeeds as a separate row is not the point - mine surviving intact is.
        registry.Register(OtherAccount, "director-2", session.ToString(), GatewaySessionKey.Hash(theirs), Later);

        var mineAfter = registry.ResolveCredential(mine);
        Assert.Equal(SessionCredentialResolutionKind.Active, mineAfter.Kind);
        Assert.Equal(Account.Value, mineAfter.Identity!.Tenant.Value);
        Assert.Equal("director-1", mineAfter.Identity!.DirectorId);
    }

    [Fact]
    public void Another_director_in_the_same_account_cannot_take_over_a_session_id()
    {
        // Inside ONE tenant the tenant check cannot help, and this is the likelier accident: a Director
        // reseeding a session id that is not its own. A session never migrates between Directors - moving
        // one creates a new session with a new id - so this is always a defect or an attempt.
        var registry = Registry();
        var session = Guid.NewGuid();
        var mine = GatewaySessionKey.Mint();
        var theirs = GatewaySessionKey.Mint();
        Assert.True(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(mine), Later));

        Assert.False(registry.Register(Account, "director-2", session.ToString(), GatewaySessionKey.Hash(theirs), Later));

        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(mine).Kind);
        Assert.Equal(SessionCredentialResolutionKind.Unknown, registry.ResolveCredential(theirs).Kind);
    }

    [Fact]
    public void A_directors_own_reseed_still_rotates_its_own_session_key()
    {
        // The guard above must not break the normal path it sits in front of: the SAME Director
        // re-registering the SAME session on a tunnel reseed is how a live key stays live.
        var registry = Registry();
        var session = Guid.NewGuid();
        var first = GatewaySessionKey.Mint();
        var second = GatewaySessionKey.Mint();
        Assert.True(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(first), Later));

        Assert.True(registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(second), Later));

        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(second).Kind);
    }

    [Fact]
    public void An_unchanged_roster_refresh_does_not_rewrite_a_long_lived_key()
    {
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var registry = Registry(() => now);
        var session = Guid.NewGuid();
        var hash = GatewaySessionKey.Hash(GatewaySessionKey.Mint());
        var initialExpiry = now + SessionKeyRegistry.MaxSessionKeyLifetime;

        IReadOnlyList<string> registrationLines;
        using (var log = FileLog.RedirectForTests())
        {
            Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, initialExpiry));
            now = now.AddSeconds(10);
            Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, now + TimeSpan.FromDays(1)));
            // Only this session's lines: the capture is process-wide, and a background task left by another class
            // can write into it while this test runs.
            registrationLines = log.DrainAndReadLines()
                .Where(l => l.Contains($"session={session}", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        using var ctx = _harness.Open().CreateUnscopedContext();
        var row = ctx.SessionKeys.Single();
        Assert.Equal(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc), row.IssuedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), row.ExpiresAtUtc);

        // Presence proves the capture worked; exactly one proves the ten-second refresh added no cap or
        // success line. Naming the first expiry also distinguishes that control from the later refresh.
        var recorded = Assert.Single(registrationLines);
        Assert.Contains($"expires={initialExpiry:O}", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_key_is_refreshed_before_half_its_lifetime_remains()
    {
        var started = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var now = started;
        var registry = Registry(() => now);
        var session = Guid.NewGuid();
        var hash = GatewaySessionKey.Hash(GatewaySessionKey.Mint());
        Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, now + SessionKeyRegistry.MaxSessionKeyLifetime));

        now = started + SessionKeyRegistry.RefreshWhenRemaining + TimeSpan.FromSeconds(1);
        Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, now + SessionKeyRegistry.MaxSessionKeyLifetime));

        using var ctx = _harness.Open().CreateUnscopedContext();
        var row = ctx.SessionKeys.Single();
        Assert.Equal(now, row.IssuedAtUtc);
        Assert.Equal(now + SessionKeyRegistry.MaxSessionKeyLifetime, row.ExpiresAtUtc);
    }

    [Fact]
    public void A_shorter_expiry_on_the_same_key_is_written_immediately()
    {
        var started = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var now = started;
        var registry = Registry(() => now);
        var session = Guid.NewGuid();
        var hash = GatewaySessionKey.Hash(GatewaySessionKey.Mint());
        Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, now + SessionKeyRegistry.MaxSessionKeyLifetime));

        now = started.AddSeconds(10);
        var shorterExpiry = started.AddHours(1);
        Assert.True(registry.Register(Account, "director-1", session.ToString(), hash, shorterExpiry));

        using var ctx = _harness.Open().CreateUnscopedContext();
        var row = ctx.SessionKeys.Single();
        Assert.Equal(now, row.IssuedAtUtc);
        Assert.Equal(shorterExpiry, row.ExpiresAtUtc);
    }

    [Fact]
    public void The_gateway_caps_an_expiry_computed_on_the_directors_clock()
    {
        // The expiry arrives from the Director, so it is only as trustworthy as the Director's clock. A
        // machine set years ahead would otherwise mint a key that outlives every backstop, refreshed on
        // every reseed. The Gateway caps it against its OWN clock.
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var registry = Registry(() => now);
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();

        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key),
            now.AddYears(5));

        // One second past the Gateway's own ceiling, the key must be gone - not still alive on the
        // Director's five-year claim.
        var justPastCeiling = Registry(() => now + SessionKeyRegistry.MaxSessionKeyLifetime + TimeSpan.FromSeconds(1));
        Assert.NotEqual(SessionCredentialResolutionKind.Active, justPastCeiling.ResolveCredential(key).Kind);
    }

    [Fact]
    public void A_shorter_expiry_than_the_cap_is_honoured()
    {
        // Capping must not become "always twelve hours". A Director asking for LESS authority than the
        // maximum is never a problem, and silently extending it would be the guard doing harm.
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var registry = Registry(() => now);
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();

        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key),
            now.AddMinutes(5));

        var afterTenMinutes = Registry(() => now.AddMinutes(10));
        Assert.NotEqual(SessionCredentialResolutionKind.Active, afterTenMinutes.ResolveCredential(key).Kind);
    }

    [Fact]
    public void A_lapsed_key_is_refused_even_though_nobody_revoked_it()
    {
        // The path where NO revocation is ever delivered: the Director was killed, the machine went away.
        // Without the expiry the key would be accepted forever, which is the one failure mode a revocation
        // model cannot cover on its own.
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var clockNow = now;
        var registry = Registry(() => clockNow);
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), now.AddHours(12));

        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);

        clockNow = now.AddHours(12).AddSeconds(1);
        Assert.Equal(SessionCredentialResolutionKind.Revoked, registry.ResolveCredential(key).Kind);
    }

    [Fact]
    public void The_expiry_sweep_tombstones_lapsed_rows_and_leaves_live_ones()
    {
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var clockNow = now;
        var registry = Registry(() => clockNow);
        var lapsing = Guid.NewGuid();
        var living = Guid.NewGuid();
        var livingKey = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", lapsing.ToString(), GatewaySessionKey.Hash(GatewaySessionKey.Mint()), now.AddHours(1));
        registry.Register(Account, "director-1", living.ToString(), GatewaySessionKey.Hash(livingKey), now.AddHours(24));

        clockNow = now.AddHours(2);
        Assert.Equal(1, registry.SweepExpired());

        Assert.Equal(SessionCredentialResolutionKind.Active, registry.ResolveCredential(livingKey).Kind);
        // Idempotent: the second sweep finds nothing left to end.
        Assert.Equal(0, registry.SweepExpired());
    }

    [Fact]
    public void A_key_hash_already_owned_by_another_session_is_refused()
    {
        var registry = Registry();
        var key = GatewaySessionKey.Mint();
        registry.Register(Account, "director-1", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(key), Later);

        Assert.False(registry.Register(Account, "director-1", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(key), Later));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void A_registration_survives_a_gateway_restart()
    {
        // The registry is the durable half of the pair. A Gateway restart must not silently invalidate every
        // live session's credential - the agents holding those keys have no way to be re-issued one.
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        Registry().Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key), Later);

        var afterRestart = new SessionKeyRegistry(_harness.Open());

        Assert.Equal(SessionCredentialResolutionKind.Active, afterRestart.ResolveCredential(key).Kind);
    }

    [Fact]
    public void On_hosted_a_local_tenant_key_is_denied_rather_than_collapsed_into_an_account()
    {
        // Unreachable through the hub - a hosted Hello resolves a real tenant or aborts the connection - so
        // this fires only on a wiring defect or an edited row. Which is exactly why it must DENY: honouring
        // the self-host single-tenant identity on a multi-tenant Gateway would put a session inside whatever
        // partition Local resolves to, and every account would share it.
        var selfHost = new SessionKeyRegistry(_harness.Open(), isHosted: false);
        var key = GatewaySessionKey.Mint();
        selfHost.Register(TenantId.Local, "director-1", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(key), Later);

        Assert.Equal(SessionCredentialResolutionKind.Active, selfHost.ResolveCredential(key).Kind);

        var hosted = new SessionKeyRegistry(_harness.Open(), isHosted: true);
        var resolution = hosted.ResolveCredential(key);

        Assert.Equal(SessionCredentialResolutionKind.Revoked, resolution.Kind);
        Assert.Null(resolution.Identity);
    }

    [Fact]
    public void A_registration_needs_a_session_and_a_hash()
    {
        var registry = Registry();
        Assert.Throws<ArgumentException>(() => registry.Register(Account, "director-1", "", GatewaySessionKey.Hash(GatewaySessionKey.Mint()), Later));
        Assert.Throws<ArgumentException>(() => registry.Register(Account, "director-1", Guid.NewGuid().ToString(), "", Later));
        Assert.Throws<ArgumentException>(() => registry.Register(default, "director-1", Guid.NewGuid().ToString(), "abc", Later));
    }
}
