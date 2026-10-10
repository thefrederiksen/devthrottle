using System;
using System.IO;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted PAID-ENTITLEMENT gate at enrollment. Without it, enrolling against the hosted Gateway is free
/// and the billing side sells what anyone can take for nothing.
///
/// THE THING THESE TESTS EXIST TO HOLD, and it is the whole correctness of the gate: the entitlement read
/// has THREE outcomes, not two, and they must never share a code path.
///
///   Entitled     -> enroll.
///   NotEntitled  -> 402. We LOOKED and there is no valid entitlement. That is KNOWLEDGE.
///   Unknown      -> 503, retry. The read FAILED. We do not know.
///
/// The two failures are not symmetric and both are covered below by their own independent control:
///   - A false 402 locks out a customer who has PAID.
///   - A false MINT gives the product away for NOTHING - and that one is SILENT, because a successful
///     enrollment looks exactly like a correct one.
/// So the rule the tests enforce is: the mint happens ONLY on a confirmed entitlement. Ignorance mints
/// nothing and denies nothing.
///
/// Every deny path is checked for BOTH halves of the refusal - the status AND that no tenant was minted and
/// no device key issued. A 402 that still minted a tenant would satisfy a status-only assertion while having
/// given away the thing the gate exists to protect. That is the shape-versus-thing failure this mission keeps
/// paying for, so it is asserted directly.
///
/// Revert-prove: delete the entitlement block in HostedEnrollmentEndpoint.Enroll and the absence and
/// ignorance tests go RED - both wrongly enrolling - while the entitled test and the self-host control stay
/// green.
///
/// The table is created here with raw SQL rather than by a migration, deliberately and for the same reason
/// production does not migrate it: it belongs to the payment side, which creates and writes it as the
/// service role, while this Gateway holds SELECT and nothing more. Creating it here also states the schema
/// this code reads, so a contract drift shows up as a failing test rather than as a runtime surprise.
/// </summary>
public sealed class HostedEntitlementGateTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Subject = "sub-paying-account";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TestEs256Key _key = new();

    /// <summary>The legacy JSON store the registry would import from. Nothing writes it: the registry has kept its
    /// devices in the database since the cutover, so a check on this file says nothing about enrollment.</summary>
    private string DevPath => _harness.LegacyPath("devices.json");

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
    }

    private static EnrollSignedInRequest Req() => new()
    {
        DeviceId = "device-1",
        MachineName = "M",
        Platform = "linux",
        DeviceType = "workstation",
    };

    /// <summary>Creates the payment-side table this Gateway only reads, and optionally seeds one row. The
    /// table carries the two-tier <c>tier</c> column exactly as the payment side's schema does, so the
    /// Gateway's read (which selects it) resolves.</summary>
    private GatewayDatabase OpenWithEntitlements(string? status = null, DateTime? periodEnd = null, bool? livemode = true, string? tier = null)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");

        if (status is not null)
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                Subject, status, periodEnd, livemode, tier);
        }
        return db;
    }

    private (DeviceRegistry devices, TenantRegistry tenants, JwtAccessTokenValidator validator) Wire(GatewayDatabase db)
    {
        var devices = new DeviceRegistry(db, DevPath);
        var tenants = new TenantRegistry(db);
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        return (devices, tenants, validator);
    }

    private string Token() => _key.Token(Subject, "payer@example.com", Audience, Issuer);

    /// <summary>Did anything get given away? Both halves of what the gate protects.</summary>
    private void AssertNothingWasGivenAway(TenantRegistry tenants)
    {
        Assert.Null(tenants.LookupBySubject(Subject));                       // no tenant minted
        Assert.Empty(_harness.OpenDevices().List());                         // no device enrolled
    }

    [Fact]
    public void An_active_subscription_enrolls()
    {
        // The positive control for every refusal below. Without it, "unpaid accounts are refused" would also
        // hold if the gate refused EVERYONE - which would pass every deny test and ship a dead product.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
        Assert.False(string.IsNullOrWhiteSpace(result.Response!.DeviceKey));
        Assert.NotNull(tenants.LookupBySubject(Subject));
    }

    [Fact]
    public void ABSENCE_denies_with_402_and_mints_nothing()
    {
        // CONTROL ONE: the read SUCCEEDS and finds nothing. That is knowledge, so it denies - and it must
        // leave no trace, because minting a tenant is itself giving something away.
        var db = OpenWithEntitlements();   // table exists, no row for this subject
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        Assert.Null(result.Response);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void IGNORANCE_does_not_deny_and_does_not_mint()
    {
        // CONTROL TWO, and the one that matters most. The read FAILS - here for real, because the table the
        // payment side owns does not exist at all, which is exactly what a lost SELECT grant or an
        // un-migrated database looks like from this side.
        //
        // It must answer RETRY: not 402, because we have not established that the account is unpaid and a
        // false 402 locks out a payer; and above all not a MINT, because a false mint gives the product away
        // silently. This is the assertion that stops a database hiccup from turning a paid product free.
        var db = _harness.Open();          // no entitlements table created
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(503, result.Status);
        Assert.NotEqual(402, result.Status);   // stated separately: ignorance must not masquerade as refusal
        Assert.Null(result.Response);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_payment_being_retried_is_entitled_until_the_paid_period_ends()
    {
        // The dunning window. The customer has paid for this period and their card merely failed on renewal;
        // cutting them off mid-period would refuse someone who has paid.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now.AddDays(3));
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(200, result.Status);
    }

    [Fact]
    public void A_payment_being_retried_is_refused_once_the_paid_period_has_ended()
    {
        // ...and the window is FINITE. Without this, past_due would be an open-ended free pass, which is the
        // same defect as no gate at all wearing a policy's clothes.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now.AddSeconds(-1));
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_payment_being_retried_is_refused_AT_the_exact_instant_the_paid_period_ends()
    {
        // THE BOUNDARY ITSELF, which the two tests above deliberately straddle without ever landing on. One
        // probes three days inside the window and the other one second past it, so an inclusive comparison
        // at the end instant passes both of them while granting access on an entitlement that has expired.
        // At exactly CurrentPeriodEnd the paid period has ended - that is what the field means - so this is
        // a refusal. Small in duration, but it is a grant in the deny-OPEN direction, and an untested
        // boundary is where a policy quietly becomes the opposite of what it says.
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusPastDue, now);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), now);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("incomplete_expired")]
    [InlineData("")]
    [InlineData("ACTIVE_BUT_NOT_REALLY")]
    public void Any_state_that_is_not_a_granting_one_is_refused(string status)
    {
        // An unrecognised state is NOT entitled. We do not guess in the paying direction, and a state this
        // code has never heard of is the case where guessing is most tempting and least justified.
        var db = OpenWithEntitlements(status);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_subscription_that_is_not_live_money_is_refused_on_hosted(bool? livemode)
    {
        // A payment-provider TEST-mode subscription costs nothing to create, so honouring one is a paywall
        // bypass - and in the deny-OPEN direction, which is the silent one: the enrollment succeeds and looks
        // exactly like a paying customer.
        //
        // NULL IS ITS OWN CASE AND IT IS THE ONE MOST LIKELY TO BE WRITTEN AS A PASS. A row written before
        // this column existed, or by a webhook that forgot to set it, arrives null - and "we did not record
        // whether this was real money" is not evidence that it was. Both are refused, which is why this is a
        // Theory over false AND null rather than a single false case.
        //
        // Note this is ABSENCE, not ignorance: the read succeeded and returned a row that is not a valid
        // entitlement. So it earns the 402 and mints nothing - it does not become a retry.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: livemode);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: true), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void The_same_row_enrolls_when_live_money_is_not_required()
    {
        // The control that stops the test above passing for the wrong reason. An identical active row, with
        // livemode null, DOES enroll when the requirement is off - so the refusals above are caused by the
        // livemode rule specifically and not by the row being rejected for some unrelated defect.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: null);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(200, result.Status);
    }

    [Fact]
    public void Self_host_has_no_gate_at_all()
    {
        // THE CONTROL. Self-host has no billing and no boundary, so passing no entitlement registry must
        // leave enrollment exactly as it was. If this ever reddens, the gate has leaked into the unpaid,
        // un-billed deployment where there is nothing to sell.
        var db = _harness.Open();          // no entitlements table, and none needed
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            entitlements: null);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("pro")]
    [InlineData(null)]
    public void Enrollment_grants_on_any_active_entitlement_regardless_of_tier(string? tier)
    {
        // The gate is TIER-AGNOSTIC. Enrolling is "may this account reach a hosted tenant at all", which BOTH
        // plans grant - so a live active row enrolls whether its tier is hosted, pro, or (on an older row that
        // predates the column) null. The tier decides a capability INSIDE the tenant, read separately; it must
        // never become a second, silent paywall on enrollment. If this ever reddens for one tier, the gate has
        // started refusing a plan that paid.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, tier: tier);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), DateTime.UtcNow);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("pro")]
    public void Evaluate_exposes_the_tier_alongside_an_entitled_outcome(string tier)
    {
        // The reader SURFACES the plan for the capability that cares (the wingman): Evaluate returns the tier
        // paired with the entitled outcome, from the same single read the gate uses - so the two can never
        // disagree about the same row.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, tier: tier);

        var decision = new EntitlementRegistry(db, requireLivemode: false).Evaluate(Subject, DateTime.UtcNow);

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Equal(tier, decision.Tier);
    }

    [Fact]
    public void Evaluate_exposes_a_null_tier_on_an_older_row_that_predates_the_column()
    {
        // A row written before the tier column existed reads back as a null tier - not an error, just an older
        // row - and it still ENTITLES (the gate is tier-agnostic). Null tier is the capability side's problem
        // to interpret, not the gate's.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, tier: null);

        var decision = new EntitlementRegistry(db, requireLivemode: false).Evaluate(Subject, DateTime.UtcNow);

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Null(decision.Tier);
    }

    [Fact]
    public void Evaluate_carries_no_tier_when_the_read_finds_nothing()
    {
        // Absence: the read succeeds and finds no row. No outcome to grant, and no tier to expose.
        var db = OpenWithEntitlements();   // table exists, no row for this subject

        var decision = new EntitlementRegistry(db, requireLivemode: false).Evaluate(Subject, DateTime.UtcNow);

        Assert.Equal(EntitlementOutcome.NotEntitled, decision.Outcome);
        Assert.Null(decision.Tier);
    }

    [Fact]
    public void A_self_host_plan_is_refused_hosted_gateway_provisioning()
    {
        // THE NEGATIVE CONTROL for issue #2147, and it runs through the REAL provisioning path - the same
        // Enroll call a self-host subscriber's Director would make - not through the scope table in isolation.
        // A table that merely lacks an entry proves nothing about what the Gateway hands out; only refusing at
        // the door does.
        //
        // The row here is as GOOD as a row gets: active, live money, in period. It pays. What it does not buy
        // is hosted capacity, and this is the one line between a self-host subscriber and a tenant plus a
        // device key - the tunnel, the cockpit, the mobile application - on a plan priced to exclude them.
        //
        // HOW IT FAILS ON PURPOSE: give pro_selfhost the hosted_gateway scope in EntitlementScopes (or delete
        // the plan gate from HostedEnrollmentEndpoint.Enroll) and this test goes RED on both halves - the
        // status becomes 200 and a tenant is minted. The paired positive below stops it passing for the wrong
        // reason: if the gate simply refused everything, that one would red instead.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: true,
            tier: EntitlementRegistry.TierProSelfHost);
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: true), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_self_host_plan_still_grants_the_artificial_intelligence_features()
    {
        // The POSITIVE half of the same plan, and the reason the refusal above is a plan boundary rather than
        // a lockout. A self-host subscriber pays for exactly three things and must keep all three: dictation,
        // text to speech, and the wingman. If a later edit "fixes" the refusal above by denying pro_selfhost
        // everything, this reddens.
        var granted = EntitlementScopes.ForTier(EntitlementRegistry.TierProSelfHost);

        Assert.Contains(EntitlementScopes.Dictation, granted);
        Assert.Contains(EntitlementScopes.Tts, granted);
        Assert.Contains(EntitlementScopes.Wingman, granted);
        Assert.DoesNotContain(EntitlementScopes.HostedGateway, granted);
        Assert.Equal(3, granted.Count);   // EXACTLY those three - no fourth scope crept in
    }

    [Fact]
    public void An_unknown_plan_is_refused_hosted_gateway_provisioning()
    {
        // The default direction. A tier string the Gateway has never been taught - a new plan the website ships
        // before this table learns it, or a typo in the payment side - grants NOTHING, so it cannot provision
        // hosted capacity. Refusing a plan we do not know about is visible and reversible in one line here;
        // granting hosted capacity to one is silent and is the bypass this whole file exists to prevent.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, livemode: true, tier: "enterprise_platinum");
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: true), DateTime.UtcNow);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void Evaluate_carries_no_tier_when_the_read_fails()
    {
        // Ignorance: the read FAILS (the payment-side table is absent, as a lost SELECT grant looks from here).
        // Unknown, and a null tier - a failed read establishes nothing, so it exposes nothing.
        var db = _harness.Open();          // no entitlements table created

        var decision = new EntitlementRegistry(db, requireLivemode: false).Evaluate(Subject, DateTime.UtcNow);

        Assert.Equal(EntitlementOutcome.Unknown, decision.Outcome);
        Assert.Null(decision.Tier);
    }
}
