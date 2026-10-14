using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The 14-day Pro trial (issue #2117) AFTER THE PRICING CHANGE MISSION (B3, owner decision D1 of 11 October 2026:
/// "There is no more 14 day trial"). Enrolment no longer grants a trial to anyone. A trial that was already running
/// is honoured to its end. These tests hold both halves.
///
/// WHAT MUST HOLD, each with its own test below:
///
///   1. NO NEW TRIAL. A brand-new account with no payment enrols its Director on the FREE plan, and the ledger
///      holds no row for it afterwards. If enrolment granted a trial again, this is the test that goes red.
///   2. A RUNNING TRIAL IS STILL PRO, and it ENDS. Not "is displayed as ended" - the entitlement read itself stops
///      returning the Pro tier at the expiry instant.
///   3. ONE trial per account, ever: the ledger's own writer never re-grants and never extends.
///   4. A paid subscription taken DURING a trial takes over with no gap and no trial expiry clipped onto it.
///
/// A trial under test is written into the ledger with <see cref="TrialRegistry.GrantIfFirstArrival"/> - the ledger's
/// one writer, which no part of the Gateway calls any more - because that is exactly the row a trial granted before
/// the change left behind.
///
/// Every deny path asserts BOTH halves of the refusal - the status AND that no tenant was minted and no
/// device key issued - because a 402 that still minted a tenant would satisfy a status-only assertion while
/// having given away the thing the gate protects.
/// </summary>
public sealed class HostedProTrialTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Subject = "sub-brand-new-account";

    /// <summary>A fixed instant so the fourteen-day window is judged exactly, not in whichever direction the
    /// clock happens to be pointing when the suite runs.</summary>
    private static readonly DateTime SignupMoment = new(2026, 7, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"trial-dev-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    private static EnrollSignedInRequest Req() => new()
    {
        DeviceId = "device-1",
        MachineName = "M",
        Platform = "linux",
        DeviceType = "workstation",
    };

    /// <summary>Creates the payment-side table this Gateway only reads, and optionally seeds one row. Without
    /// a seeded row the paid read succeeds and finds nothing - which is exactly a brand-new account that has
    /// never paid.</summary>
    private GatewayDatabase OpenWithEntitlements(string? status = null, DateTime? periodEnd = null,
        bool? livemode = true, string? tier = null)
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
        var devices = new DeviceRegistry(_devPath);
        var tenants = new TenantRegistry(db);
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        return (devices, tenants, validator);
    }

    private string Token() => _key.Token(Subject, "newcomer@example.com", Audience, Issuer);

    /// <summary>Did anything get given away? Both halves of what a refusal must withhold.</summary>
    private void AssertNothingWasGivenAway(TenantRegistry tenants)
    {
        Assert.Null(tenants.LookupBySubject(Subject));
        Assert.False(File.Exists(_devPath) && File.ReadAllText(_devPath).Contains("device-1", StringComparison.Ordinal));
    }

    /// <summary>How many rows the trial ledger holds, read from the table itself.</summary>
    private static int TrialRows(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        return ctx.AccountTrials.Count();
    }

    // ---------------------------------------------------------------------------------------------------
    // 1. No new trial: a brand-new account lands on the free plan.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_brand_new_account_with_no_payment_enrolls_its_Director_on_FREE_and_is_granted_NO_trial()
    {
        // The rule this test pinned was the opposite: a first arrival was handed fourteen days of Pro. Since the
        // Pricing Change mission nobody is. The paid read SUCCEEDS and finds nothing, the account has never been
        // seen by this Gateway (no tenant), and it still enrols its Director - on free, with the ledger untouched.
        //
        // HOW THIS FAILS ON PURPOSE: put the GrantIfFirstArrival call back into the enrolment gate and the row
        // count and the tier assertions both go red.
        var db = OpenWithEntitlements();
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);
        Assert.Null(tenants.LookupBySubject(Subject));

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            entitlements, SignupMoment);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
        Assert.False(string.IsNullOrWhiteSpace(result.Response!.DeviceKey));
        Assert.NotNull(tenants.LookupBySubject(Subject));

        Assert.Equal(0, TrialRows(db));
        Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment).Outcome);
        Assert.Equal(EntitlementRegistry.TierFree, entitlements.Evaluate(Subject, SignupMoment).Tier);
    }

    [Fact]
    public void The_trial_grants_PRO_tier_and_expires_exactly_fourteen_days_after_the_grant()
    {
        // "14 days of Pro" - both halves of that phrase, asserted. The tier must be pro (the trial grants the
        // same entitlement set a paying Pro member gets), and the period end must be the trial's own end, which
        // is what the hosted access lease clips to so caching cannot outlive the trial.
        var db = OpenWithEntitlements();
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var decision = entitlements.Evaluate(Subject, SignupMoment.AddDays(7));

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, decision.Tier);
        Assert.Equal(SignupMoment.AddDays(14), decision.CurrentPeriodEnd);
        Assert.Equal(TimeSpan.FromDays(14), TrialRegistry.TrialLength);
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. The trial ENDS - at the entitlement read, not in the user interface.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Fifteen_days_later_with_no_subscription_the_account_lands_on_FREE_and_keeps_running()
    {
        // The acceptance criterion for expiry, REWRITTEN for the owner decision of 2026-09-02. It used to read
        // NotEntitled, and that answer travelled all the way to a tombstone: the member devices were revoked
        // and their sessions dropped, so the end of a trial broke the machine instead of quietening it.
        //
        // Now the trial ENDS but the account does not. The assertions below are the whole policy and must be
        // read together - Entitled ALONE would be a permanent free Pro, which is the failure this test guarded
        // against before and still guards against now:
        //   - it is Entitled, so nothing revokes and the sessions keep running;
        //   - the tier is FREE, not pro, so it is a different plan and not a silent extension of the trial;
        //   - free grants hosted capacity and NONE of the three artificial-intelligence scopes, which is what
        //     actually makes dictation, spoken replies and the wingman go quiet.
        var db = OpenWithEntitlements();
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var decision = entitlements.Evaluate(Subject, SignupMoment.AddDays(15));

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Equal(EntitlementRegistry.TierFree, decision.Tier);
        Assert.NotEqual(EntitlementRegistry.TierPro, decision.Tier);

        // The plan, stated where plans are decided. This is the assertion that stops "the trial never ends".
        Assert.True(EntitlementScopes.GrantsHostedGateway(decision.Tier));
        Assert.False(EntitlementScopes.Grants(decision.Tier, EntitlementScopes.Dictation));
        Assert.False(EntitlementScopes.Grants(decision.Tier, EntitlementScopes.Tts));
        Assert.False(EntitlementScopes.Grants(decision.Tier, EntitlementScopes.Wingman));

        // A free plan has no paid period, so nothing may be clipped to one.
        Assert.Null(decision.CurrentPeriodEnd);
    }

    [Fact]
    public void The_trial_is_over_AT_the_exact_expiry_instant_not_a_tick_later()
    {
        // Boundaries are where a policy is decided, so this one is pinned by its own test. At exactly the
        // expiry the trial HAS ended - an inclusive comparison would grant on an expired trial, which is one
        // tick of access in the give-it-away direction.
        var db = OpenWithEntitlements();
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var oneTickBefore = entitlements.Evaluate(Subject, SignupMoment.AddDays(14).AddTicks(-1));
        var atExpiry = entitlements.Evaluate(Subject, SignupMoment.AddDays(14));

        // The boundary is now a TIER change rather than an outcome change - both instants are Entitled, because
        // the account survives its own trial. Asserting only the outcome would pass while the trial ran forever,
        // so the tier is what this test turns on, and the artificial-intelligence scope is what proves the tier
        // means something.
        Assert.Equal(EntitlementOutcome.Entitled, oneTickBefore.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, oneTickBefore.Tier);
        Assert.True(EntitlementScopes.Grants(oneTickBefore.Tier, EntitlementScopes.Dictation));

        Assert.Equal(EntitlementOutcome.Entitled, atExpiry.Outcome);
        Assert.Equal(EntitlementRegistry.TierFree, atExpiry.Tier);
        Assert.False(EntitlementScopes.Grants(atExpiry.Tier, EntitlementScopes.Dictation));
    }

    [Fact]
    public void Enrolling_again_after_the_trial_has_ended_SUCCEEDS_on_the_free_plan()
    {
        // The expiry proven end to end through the enrollment path, and this is the test that states the owner
        // decision most directly: a member whose fourteen days are up used to meet a 402 and get no device key,
        // which meant a new machine could not be connected and - through the request-path cutoff - the ones
        // already connected were revoked. Now they enroll, on free.
        //
        // The DEVICE KEY is the assertion that matters, exactly as it was when this test asserted the opposite.
        // The key is what actually unlocks hosted use, so proving it is ISSUED is proving the member still has
        // a working product. What they no longer have is the artificial intelligence, and that is asserted on
        // the plan by the tests above rather than re-derived here.
        var db = OpenWithEntitlements();
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        // The trial was granted before the change; enrolment itself grants none.
        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var during = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            entitlements, SignupMoment);
        Assert.Equal(200, during.Status);
        Assert.Equal(EntitlementRegistry.TierPro, entitlements.Evaluate(Subject, SignupMoment).Tier);

        var after = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            entitlements, SignupMoment.AddDays(15));

        Assert.Equal(200, after.Status);
        Assert.NotNull(after.Response);
        Assert.False(string.IsNullOrWhiteSpace(after.Response!.DeviceKey));

        // And enrolling on free must never look like a fresh trial: no trial is running afterwards, and the
        // ledger still holds the one row it held before either enrolment.
        Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment.AddDays(15)).Outcome);
        Assert.Equal(1, TrialRows(db));
        Assert.Equal(EntitlementRegistry.TierFree,
            entitlements.Evaluate(Subject, SignupMoment.AddDays(15)).Tier);
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. One trial per account, ever.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_second_grant_attempt_after_expiry_NEVER_restarts_the_fourteen_days()
    {
        // Without this, a member could restart the free window forever by re-enrolling, and the paywall would
        // exist only on paper. The expired row is the evidence that stops it - which is why the row is kept
        // rather than deleted when the trial ends.
        var db = OpenWithEntitlements();
        var trials = new TrialRegistry(db);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var retry = trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment.AddDays(30));

        Assert.Equal(TrialOutcome.None, retry.Outcome);
        Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment.AddDays(30)).Outcome);
    }

    [Fact]
    public void Granting_twice_inside_the_window_returns_the_SAME_expiry_and_never_extends_it()
    {
        // Idempotence stated as the property that matters: a second arrival during the trial (a second device,
        // a re-enroll) must not push the end date out. An extending grant would be a rolling free plan.
        var db = OpenWithEntitlements();
        var trials = new TrialRegistry(db);

        var first = trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);
        var second = trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment.AddDays(7));

        Assert.Equal(TrialOutcome.Active, second.Outcome);
        Assert.Equal(first.ExpiresAtUtc, second.ExpiresAtUtc);
        Assert.Equal(SignupMoment.AddDays(14), second.ExpiresAtUtc);
    }

    // ---------------------------------------------------------------------------------------------------
    // An account this Gateway already knew about gets no trial either - nobody does.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_account_the_gateway_already_knew_about_is_granted_NO_trial()
    {
        // The returning account: it already holds a tenant on this Gateway. It was never granted a trial under
        // the old rule, and it is not granted one now - the same answer a brand-new account gets (test 1), by the
        // one rule that there are no new trials.
        var db = OpenWithEntitlements();
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);

        tenants.MintOrLookupBySubject(Subject, "returning@example.com");

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false, trials: trials), SignupMoment);

        Assert.Equal(0, TrialRows(db));
        Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment).Outcome);

        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal(EntitlementRegistry.TierFree,
            new EntitlementRegistry(db, requireLivemode: false, trials: trials).Evaluate(Subject, SignupMoment).Tier);
    }

    // ---------------------------------------------------------------------------------------------------
    // 4. Subscribing during the trial.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Subscribing_on_day_seven_takes_over_with_no_gap_and_no_trial_expiry_clipped_onto_it()
    {
        // The paid row wins outright, so the member's access does not pause at the fourteen-day mark and does
        // not carry the trial's end date as its period end. Asserting the period end is the point: if the
        // trial's expiry leaked through, the hosted access lease would cut a PAYING member off on day fourteen.
        var paidPeriodEnd = SignupMoment.AddDays(37);
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, paidPeriodEnd,
            livemode: true, tier: EntitlementRegistry.TierPro);
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var onDayFifteen = entitlements.Evaluate(Subject, SignupMoment.AddDays(15));

        Assert.Equal(EntitlementOutcome.Entitled, onDayFifteen.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, onDayFifteen.Tier);
        Assert.Equal(paidPeriodEnd, onDayFifteen.CurrentPeriodEnd);
        Assert.NotEqual(SignupMoment.AddDays(14), onDayFifteen.CurrentPeriodEnd);
    }

    [Fact]
    public void A_paying_account_is_never_handed_a_trial_it_does_not_need()
    {
        // An account that pays from the start writes no trial row at all.
        var db = OpenWithEntitlements(EntitlementRegistry.StatusActive, tier: EntitlementRegistry.TierPro);
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false, trials: trials), SignupMoment);

        Assert.Equal(200, result.Status);
        Assert.Equal(0, TrialRows(db));
        Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment).Outcome);
    }

    // ---------------------------------------------------------------------------------------------------
    // Ignorance is still never a verdict - in EITHER direction, for EITHER read.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_live_trial_entitles_even_when_the_PAID_read_fails()
    {
        // The paid table is unreadable (no table at all - a lost SELECT grant, or an un-migrated database).
        // That is ignorance about the SUBSCRIPTION, but the trial we granted ourselves is knowledge, and it
        // entitles on its own. Denying here would lock a member out of their free window over a database fault
        // that has nothing to do with them.
        var db = _harness.Open();          // no entitlements table
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, SignupMoment);

        var decision = entitlements.Evaluate(Subject, SignupMoment.AddDays(3));

        Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, decision.Tier);
    }

    [Fact]
    public void A_failed_TRIAL_read_is_UNKNOWN_and_must_never_masquerade_as_a_refusal()
    {
        // The mirror of the entitlement gate's ignorance control, for the ledger this issue added. With the
        // trial table gone we cannot tell whether a trial covers this account - so this must answer retry, not
        // 402. A 402 here would refuse a member inside their free window because a table was unreadable, and a
        // grant would hand hosted to someone with no trial at all.
        var db = OpenWithEntitlements();
        using (var ctx = db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE account_trials");

        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);

        Assert.Equal(TrialOutcome.Unknown, trials.Evaluate(Subject, SignupMoment).Outcome);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false, trials: trials), SignupMoment);

        Assert.Equal(503, result.Status);
        Assert.NotEqual(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    [Fact]
    public void A_failed_PAID_read_with_no_trial_is_still_a_retry_never_a_free_grant()
    {
        // The trial must not weaken the original gate. The paid table is unreadable and this account has no
        // trial: the answer is still 503-retry, and above all NOT a mint. A trial feature that turned an
        // unreadable payment table into a free enrollment would be the silent give-away the gate exists to stop.
        var db = _harness.Open();          // no entitlements table
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false, trials: trials), SignupMoment);

        Assert.Equal(503, result.Status);
        AssertNothingWasGivenAway(tenants);
    }

    // ---------------------------------------------------------------------------------------------------
    // The refusal a member actually reads.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Over_the_wire_a_member_past_their_trial_is_enrolled_on_free_and_not_refused()
    {
        // THE OWNER DECISION, PROVEN ON THE WIRE. This test used to assert the opposite - that a member past
        // their trial met a 402 - and it drove the real endpoint over HTTP precisely because the refusal had to
        // be shown to be real rather than merely coded. The same standard applies to the grant that replaced
        // it: the member must actually receive a device key from a live endpoint, not just a 200 from a method.
        //
        // The account here holds no paid plan and no trial. That is exactly the member the old code turned away.
        // Under the free plan their Director is enrolled.
        var db = OpenWithEntitlements();
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        tenants.MintOrLookupBySubject(Subject, "returning@example.com");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        HostedEnrollmentEndpoint.Map(app, devices, tenants, validator, entitlements);
        await app.StartAsync();

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var msg = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
            {
                Content = JsonContent.Create(new { deviceId = "device-1", machineName = "M", platform = "linux", deviceType = "workstation" }),
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());

            var resp = await http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(200, (int)resp.StatusCode);
            Assert.Contains("deviceKey", body, StringComparison.OrdinalIgnoreCase);

            // And it is free, not a trial and not a paid plan.
            Assert.Equal(EntitlementRegistry.TierFree, entitlements.Evaluate(Subject, SignupMoment).Tier);
            Assert.Equal(TrialOutcome.None, trials.Evaluate(Subject, SignupMoment).Outcome);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task The_402_that_remains_still_says_what_to_do_about_it()
    {
        // THE COVERAGE THE TEST ABOVE GAVE UP, RETARGETED RATHER THAN DELETED. The wire assertion on
        // SubscribeMessage and SubscribeUrl existed to hold a real acceptance criterion - a refusal must point
        // at subscribing rather than being a raw error - and those two constants are asserted on the wire in
        // this file and nowhere else. The free plan removed the 402 that test was reading, so deleting it would
        // have quietly dropped the only proof that a refusal a member actually meets carries its message.
        //
        // A 402 still exists: a plan that pays for the artificial intelligence but NOT for hosted capacity.
        // That is the pro self-host plan, and it is refused hosted provisioning by the plan gate. Same endpoint,
        // same response shaping, same two constants - so the criterion is still proven, on the refusal that
        // still happens.
        var db = OpenWithEntitlements(status: "active", tier: EntitlementRegistry.TierProSelfHost);
        var (devices, tenants, validator) = Wire(db);
        var trials = new TrialRegistry(db);
        var entitlements = new EntitlementRegistry(db, requireLivemode: false, trials: trials);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        HostedEnrollmentEndpoint.Map(app, devices, tenants, validator, entitlements);
        await app.StartAsync();

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var msg = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
            {
                Content = JsonContent.Create(new { deviceId = "device-1", machineName = "M", platform = "linux", deviceType = "workstation" }),
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());

            var resp = await http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(402, (int)resp.StatusCode);
            Assert.Contains(EntitlementRegistry.SubscribeMessage, body, StringComparison.Ordinal);
            Assert.Contains(EntitlementRegistry.SubscribeUrl, body, StringComparison.Ordinal);
            AssertNothingWasGivenAway(tenants);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // The control: an entitlement registry with no trial ledger wired.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void With_no_trial_ledger_wired_the_gate_behaves_exactly_as_it_did_before()
    {
        // The control for the free plan. An entitlement registry built with no trial ledger has no free plan
        // either, so an account with no paid entitlement is refused, unchanged. If this ever passed WITH a trial
        // ledger, the tests above would be proving nothing about the free plan.
        var db = OpenWithEntitlements();
        var (devices, tenants, validator) = Wire(db);

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Req(), devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), SignupMoment);

        Assert.Equal(402, result.Status);
        AssertNothingWasGivenAway(tenants);
    }
}
