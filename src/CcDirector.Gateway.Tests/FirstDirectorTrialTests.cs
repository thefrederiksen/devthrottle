using System;
using System.IO;
using System.Linq;
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
/// The 7-day Pro trial that starts at an account's FIRST DIRECTOR (Website Black mission, owner decisions D3, D4 and
/// D20 of 5 November 2026; <see cref="FirstDirectorTrial"/>). Driven through the one mint,
/// <see cref="HostedEnrollmentEndpoint.Enroll"/>, against the Gateway's test database and a fixed clock.
///
/// WHAT MUST HOLD, each with its own test:
///   1. THE SETTING OFF CHANGES NOTHING. With the setting off (or not passed), a first Director enrols on the free
///      plan and the ledger holds no row - exactly what the Gateway did before this change. This is the check that
///      the release, which ships with the setting off, granted no one anything.
///   2. NOT AT SIGN-UP. An account the website has made but whose Director has never enrolled has no trial: there
///      is nothing in the ledger until the Director door is used.
///   3. AT THE FIRST DIRECTOR, 7 days of Pro, started at that moment.
///   4. A SECOND DIRECTOR DOES NOT RESTART IT, and enrolling again after the 7 days grants nothing.
///   5. PRO UNTIL THE 7 DAYS END, FREE AFTER, exactly at the end instant.
///   6. NOTHING through the Cockpit and phone door, nothing to a paying account, nothing to an account the Gateway
///      already knew, and a ledger that cannot be read is a retry that mints nothing.
/// </summary>
public sealed class FirstDirectorTrialTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Subject = "sub-first-director";

    /// <summary>The moment the first Director enrols. Fixed, so the 7-day window is judged exactly.</summary>
    private static readonly DateTime FirstConnect = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"first-director-trial-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    private static EnrollSignedInRequest Director(string deviceId = "director-1") => new()
    {
        DeviceId = deviceId,
        MachineName = "M",
        Platform = "windows",
        DeviceType = "workstation",
    };

    private static EnrollSignedInRequest Browser() => new()
    {
        DeviceId = "browser-1",
        MachineName = "Cockpit",
        Platform = "web",
        DeviceType = "browser",
    };

    /// <summary>The payment-side table the Gateway only reads; optionally one paid row for the account.</summary>
    private GatewayDatabase Open(string? paidStatus = null, string? paidTier = null)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        if (paidStatus is not null)
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                Subject, paidStatus, FirstConnect.AddDays(30), true, paidTier);
        }
        return db;
    }

    private sealed record Wired(GatewayDatabase Db, DeviceRegistry Devices, TenantRegistry Tenants,
        JwtAccessTokenValidator Validator, TrialRegistry Trials, EntitlementRegistry Entitlements);

    private Wired Wire(GatewayDatabase db)
    {
        var trials = new TrialRegistry(db);
        return new Wired(db, new DeviceRegistry(_devPath), new TenantRegistry(db),
            new JwtAccessTokenValidator("test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
                expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false),
            trials, new EntitlementRegistry(db, requireLivemode: false, trials: trials));
    }

    private string Token() => _key.Token(Subject, "first@example.invalid", Audience, Issuer);

    private HostedEnrollmentEndpoint.EnrollResult EnrollDirector(Wired w, DateTime at, FirstDirectorTrial? setting,
        string deviceId = "director-1")
        => HostedEnrollmentEndpoint.Enroll(Token(), Director(deviceId), w.Devices, w.Tenants, w.Validator,
            w.Entitlements, at, trials: w.Trials, firstDirectorTrial: setting);

    private static FirstDirectorTrial On => FirstDirectorTrial.Parse("1");

    private static int TrialRows(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        return ctx.AccountTrials.Count();
    }

    // ------------------------------------------------------------------------------------------- 1. off changes nothing

    [Fact]
    public void Enroll_SettingOff_FirstDirectorIsGrantedNoTrial()
    {
        var w = Wire(Open());

        var result = EnrollDirector(w, FirstConnect, FirstDirectorTrial.Off);

        Assert.Equal(200, result.Status);
        Assert.NotNull(w.Tenants.LookupBySubject(Subject));
        Assert.Equal(0, TrialRows(w.Db));
        Assert.Equal(EntitlementRegistry.TierFree, w.Entitlements.Evaluate(Subject, FirstConnect).Tier);
    }

    [Fact]
    public void Enroll_SettingNotPassed_FirstDirectorIsGrantedNoTrial()
    {
        // How every existing caller (and the Cockpit and phone door) calls the mint: no setting at all.
        var w = Wire(Open());

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Director(), w.Devices, w.Tenants, w.Validator,
            w.Entitlements, FirstConnect);

        Assert.Equal(200, result.Status);
        Assert.Equal(0, TrialRows(w.Db));
        Assert.Equal(EntitlementRegistry.TierFree, w.Entitlements.Evaluate(Subject, FirstConnect).Tier);
    }

    // --------------------------------------------------------------------------- 2 and 3. not at sign-up; at the Director

    [Fact]
    public void Enroll_SettingOn_TrialStartsAtTheFirstDirectorNotBefore()
    {
        var w = Wire(Open());

        // Signed up on the website, no Director yet: the Gateway holds nothing for the account, and it is not Pro.
        Assert.Equal(TrialOutcome.None, w.Trials.Evaluate(Subject, FirstConnect.AddDays(-3)).Outcome);
        Assert.Equal(0, TrialRows(w.Db));

        var result = EnrollDirector(w, FirstConnect, On);

        Assert.Equal(200, result.Status);
        Assert.Equal(1, TrialRows(w.Db));
        var status = w.Trials.ReadStatus(Subject, FirstConnect);
        Assert.Equal(TrialStatusKind.Active, status.Kind);
        Assert.Equal(FirstConnect, status.StartedAtUtc);
        Assert.Equal(FirstConnect.AddDays(7), status.ExpiresAtUtc);

        var entitlement = w.Entitlements.Evaluate(Subject, FirstConnect);
        Assert.Equal(EntitlementOutcome.Entitled, entitlement.Outcome);
        Assert.Equal(EntitlementRegistry.TierPro, entitlement.Tier);
    }

    // ------------------------------------------------------------------------------------- 4. never restarted

    [Fact]
    public void Enroll_SecondDirectorThreeDaysLater_DoesNotRestartTheTrial()
    {
        var w = Wire(Open());
        Assert.Equal(200, EnrollDirector(w, FirstConnect, On).Status);

        var second = EnrollDirector(w, FirstConnect.AddDays(3), On, deviceId: "director-2");

        Assert.Equal(200, second.Status);
        Assert.Equal(1, TrialRows(w.Db));
        Assert.Equal(FirstConnect.AddDays(7), w.Trials.ReadStatus(Subject, FirstConnect.AddDays(3)).ExpiresAtUtc);
    }

    [Fact]
    public void Enroll_AfterTheSevenDays_GrantsNothingNew_AndEnrolsOnFree()
    {
        var w = Wire(Open());
        Assert.Equal(200, EnrollDirector(w, FirstConnect, On).Status);

        var later = FirstConnect.AddDays(20);
        var again = EnrollDirector(w, later, On, deviceId: "director-3");

        Assert.Equal(200, again.Status);
        Assert.Equal(1, TrialRows(w.Db));
        Assert.Equal(TrialStatusKind.Expired, w.Trials.ReadStatus(Subject, later).Kind);
        Assert.Equal(EntitlementRegistry.TierFree, w.Entitlements.Evaluate(Subject, later).Tier);
    }

    // ------------------------------------------------------------------------------ 5. Pro until day 7, free after

    [Fact]
    public void Evaluate_ProUntilTheSevenDaysEnd_FreeFromTheEndInstant()
    {
        var w = Wire(Open());
        Assert.Equal(200, EnrollDirector(w, FirstConnect, On).Status);
        var end = FirstConnect.AddDays(7);

        Assert.Equal(EntitlementRegistry.TierPro, w.Entitlements.Evaluate(Subject, end.AddTicks(-1)).Tier);
        Assert.Equal(EntitlementRegistry.TierFree, w.Entitlements.Evaluate(Subject, end).Tier);
    }

    // ------------------------------------------------------------------------------------- 6. who never gets one

    [Fact]
    public void Enroll_CockpitOrPhoneDoor_SettingOn_GrantsNothing()
    {
        // Pre-launch grace not set: every account has remote access, so the door would enrol - and still grants nothing.
        var w = Wire(Open());

        var result = HostedEnrollmentEndpoint.Enroll(Token(), Browser(), w.Devices, w.Tenants, w.Validator,
            w.Entitlements, FirstConnect, door: HostedEnrollmentEndpoint.EnrollDoor.CockpitOrPhone,
            remoteAccess: new RemoteAccessRule(PreLaunchGrace.NotSet), trials: w.Trials, firstDirectorTrial: On);

        Assert.Equal(200, result.Status);
        Assert.Equal(0, TrialRows(w.Db));
    }

    [Fact]
    public void Enroll_PayingAccount_SettingOn_GrantsNothing()
    {
        var w = Wire(Open(paidStatus: "active", paidTier: EntitlementRegistry.TierPro));

        var result = EnrollDirector(w, FirstConnect, On);

        Assert.Equal(200, result.Status);
        Assert.Equal(0, TrialRows(w.Db));
        Assert.Equal(EntitlementRegistry.TierPro, w.Entitlements.Evaluate(Subject, FirstConnect).Tier);
    }

    [Fact]
    public void Enroll_AccountTheGatewayAlreadyKnew_SettingOn_GrantsNothing()
    {
        // An account that reached the hosted Gateway before (it holds a tenant) has already had its first Director.
        var w = Wire(Open());
        w.Tenants.MintOrLookupBySubject(Subject, "first@example.invalid");

        var result = EnrollDirector(w, FirstConnect, On);

        Assert.Equal(200, result.Status);
        Assert.Equal(0, TrialRows(w.Db));
        Assert.Equal(EntitlementRegistry.TierFree, w.Entitlements.Evaluate(Subject, FirstConnect).Tier);
    }

    [Fact]
    public void Enroll_TrialLedgerUnreadable_SettingOn_IsARetryThatMintsNothing()
    {
        var w = Wire(Open());
        using (var ctx = w.Db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE account_trials");

        var result = EnrollDirector(w, FirstConnect, On);

        Assert.Equal(503, result.Status);
        Assert.Null(w.Tenants.LookupBySubject(Subject));
    }

    [Fact]
    public void Enroll_SettingOnWithNoLedger_IsAWiringDefect()
    {
        var w = Wire(Open());

        Assert.Throws<InvalidOperationException>(() => HostedEnrollmentEndpoint.Enroll(Token(), Director(), w.Devices,
            w.Tenants, w.Validator, w.Entitlements, FirstConnect, trials: null, firstDirectorTrial: On));
    }
}
