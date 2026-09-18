using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The typed per-tenant resolver (issue #2017): tenant override when set and valid, otherwise the operator
/// global default - and NEVER another tenant's value. These tests assert the resolver's own contract; the
/// global-default arm is asserted by comparing against the same global helper the resolver falls back to (so
/// the test is robust to whatever operator default the environment holds), while the override arm is exact.
/// </summary>
public sealed class TenantSettingsResolverTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
    private const TranscriptionMode Mode = TranscriptionMode.DevThrottle;

    private static TenantSettingsResolver NewResolver(GatewayDbTestHarness h)
        => new(new TenantSettingsStore(h.Open()));

    // Override values in these tests are DevThrottle internal included ids: since the Included AI
    // mission (issue #1360) a non-devthrottle override is not honored - it would bill credits on an
    // internal feature - so resolution falls forward to the operator default. The wingman "thinking"
    // override is deliberately set to the FAST id (and vice versa) where two distinct values are
    // needed, which still proves the cells are stored separately.

    [Fact]
    public void OneTenantsOverride_DoesNotLeakToAnother_WhoGetsTheGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // The thinking id on the FAST role: distinct from the fast-id operator default, so a leak is visible.
        r.SetWingmanModel(TenantA, WingmanModelRole.Fast, "devthrottle/wingman", Now);

        // Tenant B never set an override: it must get the OPERATOR global default, never tenant A's value.
        Assert.Equal("devthrottle/wingman", r.WingmanModel(TenantA, Mode, WingmanModelRole.Fast).Value);
        Assert.Equal(WingmanModelConfig.Resolve(Mode, WingmanModelRole.Fast), r.WingmanModel(TenantB, Mode, WingmanModelRole.Fast));
        Assert.NotEqual("devthrottle/wingman", r.WingmanModel(TenantB, Mode, WingmanModelRole.Fast).Value);
    }

    [Fact]
    public void WingmanModel_RolesAreStoredSeparately()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // Crossed on purpose: each role must read its OWN stored cell.
        r.SetWingmanModel(TenantA, WingmanModelRole.Thinking, "devthrottle/wingman-fast", Now);
        r.SetWingmanModel(TenantA, WingmanModelRole.Fast, "devthrottle/wingman", Now);

        Assert.Equal("devthrottle/wingman-fast", r.WingmanModel(TenantA, Mode, WingmanModelRole.Thinking).Value);
        Assert.Equal("devthrottle/wingman", r.WingmanModel(TenantA, Mode, WingmanModelRole.Fast).Value);
    }

    [Fact]
    public void WingmanModel_CatalogIdOverride_FallsForwardToIncludedDefault()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // The Included AI revert-proof on the per-tenant wingman path (issue #1360).
        r.SetWingmanModel(TenantA, WingmanModelRole.Thinking, "Qwen/Qwen2.5-72B-Instruct", Now);

        Assert.Equal(WingmanModelConfig.Resolve(Mode, WingmanModelRole.Thinking),
            r.WingmanModel(TenantA, Mode, WingmanModelRole.Thinking));
    }

    [Fact]
    public void TimeZone_InvalidOverrideStored_FallsBackToGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        // A corrupt override (e.g. a value that later fails validation) must degrade to the operator default,
        // not crash a turn and not leak another tenant's value. Write an invalid id straight through the store.
        store.Set(TenantA, TenantSettingKeys.TimeZone, "Not/AZone", Now);

        Assert.Equal(TimeZoneConfig.Get(), r.TimeZone(TenantA));
    }

    [Fact]
    public void TimeZone_ValidOverride_ReturnsOverride()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetTimeZone(TenantA, "Asia/Tokyo", Now);

        Assert.Equal("Asia/Tokyo", r.TimeZone(TenantA));
    }

    [Fact]
    public void SetTimeZone_InvalidId_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Throws<ArgumentException>(() => r.SetTimeZone(TenantA, "Not/AZone", Now));
    }

    [Fact]
    public void SetTtsModel_Empty_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Throws<ArgumentException>(() => r.SetTtsModel(TenantA, "   ", Now));
    }

    [Fact]
    public void SnoozePresets_ValidSet_RoundTripsAndDefaultPersists()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSnoozePresets(TenantA, new[] { 240, 15, 60 }, 60, Now);

        Assert.Equal(new[] { 15, 60, 240 }, r.SnoozePresets(TenantA)); // sorted ascending
        Assert.Equal(60, r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SnoozePresets_NoOverride_ReturnsGlobalDefaults()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Equal(SnoozePresetsConfig.Get(), r.SnoozePresets(TenantA));
        Assert.Equal(SnoozeDefaultConfig.Get(), r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SnoozeDefault_CorruptOverride_FallsBackToGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        store.Set(TenantA, TenantSettingKeys.SnoozeDefaultMinutes, "not-a-number", Now);

        Assert.Equal(SnoozeDefaultConfig.Get(), r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SetSnoozePresets_DefaultNotInList_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // The default must be one of the presets - the same invariant the global setter enforces.
        Assert.Throws<ArgumentException>(() => r.SetSnoozePresets(TenantA, new[] { 15, 60 }, 999, Now));
    }

    // ---- the daily report cadence (issue #1000) ------------------------------------------------------

    [Fact]
    public void DailyReportCadence_NoChoice_IsDaily()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // What every account received before this setting existed. A different answer here would silently
        // stop the report for everybody the moment the setting shipped.
        Assert.Equal(ReportCadence.Daily, r.DailyReportCadence(TenantA));
    }

    [Fact]
    public void DailyReportCadence_Off_RoundTrips()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetDailyReportCadence(TenantA, ReportCadence.Off, Now);

        Assert.Equal(ReportCadence.Off, r.DailyReportCadence(TenantA));
    }

    [Fact]
    public void DailyReportCadence_OneTenantsChoice_DoesNotSilenceAnother()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetDailyReportCadence(TenantA, ReportCadence.Off, Now);

        Assert.Equal(ReportCadence.Off, r.DailyReportCadence(TenantA));
        Assert.Equal(ReportCadence.Daily, r.DailyReportCadence(TenantB));
    }

    [Fact]
    public void DailyReportCadence_UnreadableValue_ReadsAsDaily_NotAsSilence()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        // Includes the value a NEWER Gateway would write for weekly, read by one that does not know it yet.
        // Degrading to mail is recoverable and visible; degrading to silence is neither.
        foreach (var junk in new[] { "", "   ", "true", "never", "weekly" })
        {
            store.Set(TenantA, TenantSettingKeys.DailyReportCadence, junk, Now);
            Assert.Equal(ReportCadence.Daily, r.DailyReportCadence(TenantA));
        }
    }

    [Fact]
    public void DailyReportCadence_IsStoredUnderTheDocumentedKey_AsTheCadenceName()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        r.SetDailyReportCadence(TenantA, ReportCadence.Off, Now);

        // The stored form is the wire name, so the value the recipients endpoint reads and the value the
        // settings page sends are the same string, not two spellings that could drift apart.
        Assert.Equal(ReportCadences.OffName, store.Get(TenantA, TenantSettingKeys.DailyReportCadence));
    }

    [Fact]
    public void DailyReportCadence_ChoosingDaily_IsStoredRatherThanLeftAbsent()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        r.SetDailyReportCadence(TenantA, ReportCadence.Daily, Now);

        Assert.Equal(ReportCadences.DailyName, store.Get(TenantA, TenantSettingKeys.DailyReportCadence));
    }

    // ---- the mentor report switch (devthrottle_internal#1661) ----------------------------------------
    //
    // This Gateway does not SEND the mentor report - the harness does, reading this same row out of the
    // database. So what these prove is that the row an account writes here is the row that harness will
    // read: stored under the documented key, in the documented "true"/"false" spelling, per tenant.

    [Fact]
    public void MentorReportEnabled_NoChoice_IsOn()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // What every account received before this setting existed. Defaulting to OFF would silently stop
        // the report for everybody the moment the setting shipped, and nobody would have asked for that.
        Assert.True(r.MentorReportEnabled(TenantA));
    }

    [Fact]
    public void MentorReportEnabled_Off_RoundTrips()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetMentorReportEnabled(TenantA, false, Now);

        Assert.False(r.MentorReportEnabled(TenantA));
    }

    [Fact]
    public void MentorReportEnabled_OneTenantsOptOut_DoesNotSilenceAnother()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetMentorReportEnabled(TenantA, false, Now);

        Assert.False(r.MentorReportEnabled(TenantA));
        Assert.True(r.MentorReportEnabled(TenantB));
    }

    [Fact]
    public void MentorReportEnabled_UnreadableValue_ReadsAsOn_NotAsSilence()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        foreach (var junk in new[] { "", "   ", "off", "no", "0" })
        {
            store.Set(TenantA, TenantSettingKeys.MentorReportEnabled, junk, Now);
            Assert.True(r.MentorReportEnabled(TenantA));
        }
    }

    [Fact]
    public void MentorReportEnabled_IsStoredUnderTheDocumentedKey_InTheSpellingTheHarnessReads()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        // The harness parses this row itself, in Python, out of gateway.tenant_settings. It accepts exactly
        // "true" and "false" and REFUSES anything else rather than guessing, so the spelling written here is
        // not a detail: a value this assertion let drift would stop that account's report with an error
        // instead of sending it.
        r.SetMentorReportEnabled(TenantA, false, Now);
        Assert.Equal("false", store.Get(TenantA, TenantSettingKeys.MentorReportEnabled));

        r.SetMentorReportEnabled(TenantA, true, Now);
        Assert.Equal("true", store.Get(TenantA, TenantSettingKeys.MentorReportEnabled));
    }

    [Fact]
    public void MentorReportEnabled_TurningItBackOn_IsStoredRatherThanLeftAbsent()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        r.SetMentorReportEnabled(TenantA, false, Now);
        r.SetMentorReportEnabled(TenantA, true, Now);

        // "Back on" is a choice this account made, and a later look at the store can tell it apart from an
        // account that never touched the setting.
        Assert.Equal("true", store.Get(TenantA, TenantSettingKeys.MentorReportEnabled));
    }
}
