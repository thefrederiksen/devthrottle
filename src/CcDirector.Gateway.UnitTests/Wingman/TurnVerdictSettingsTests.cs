using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The two turn-judging switches as stored settings (the Wingman-on-every-turn mission, slice B): that both
/// default OFF, that the two are genuinely independent, that a corrupt stored value reads as OFF rather than
/// ON, and that the closed key set still refuses a key nobody has classified.
///
/// WHY THE DIRECTION OF EVERY DEFAULT IS ASSERTED. These two switches are the only place in this build where
/// a wrong reading is silent: judging ON that nobody asked for spends a model call at every stop, and
/// colouring ON that nobody asked for paints a session calm - which is to say it stops waking somebody who
/// was needed. Both failures show up as nothing happening, which is why they are pinned here rather than
/// left to the resolver's own comments.
/// </summary>
public sealed class TurnVerdictSettingsTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");
    private static readonly DateTime Now = new(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);

    private (TenantSettingsStore Store, TenantSettingsResolver Resolver) NewSettings()
    {
        var store = new TenantSettingsStore(_harness.Open());
        return (store, new TenantSettingsResolver(store));
    }

    [Fact]
    public void An_account_that_has_never_chosen_is_judging_nothing_and_colouring_nothing()
    {
        var (_, resolver) = NewSettings();

        var settings = resolver.TurnVerdict(TenantA);

        Assert.False(settings.JudgeEnabled);
        Assert.False(settings.ColourEnabled);
        // The three numbers are the design's, not a tenant's, and the seat reads them from here so there is
        // one place they live rather than three constants scattered through it.
        Assert.Equal(TurnVerdictSettings.DefaultMaxInFlight, settings.MaxInFlight);
        Assert.Equal(TurnVerdictSettings.DefaultSettleMs, settings.SettleMs);
        Assert.Equal(TurnVerdictSettings.DefaultJudgeTimeoutSeconds, settings.JudgeTimeoutSeconds);
    }

    [Fact]
    public void The_two_switches_are_independent_so_the_shadow_state_is_reachable()
    {
        var (_, resolver) = NewSettings();

        resolver.SetTurnVerdictJudgeEnabled(TenantA, true, Now);

        // Judging on, colouring off: verdicts are stored and gradeable and no row's colour moves. This is
        // the state every tenant but the owner's runs in until the judge has earned its calm verdicts, so a
        // resolver that coupled the two would remove the whole rollout plan rather than one setting.
        var settings = resolver.TurnVerdict(TenantA);
        Assert.True(settings.JudgeEnabled);
        Assert.False(settings.ColourEnabled);
    }

    [Fact]
    public void Turning_a_switch_off_is_stored_as_a_decision_rather_than_as_an_absence()
    {
        var (store, resolver) = NewSettings();
        resolver.SetTurnVerdictJudgeEnabled(TenantA, true, Now);
        resolver.SetTurnVerdictJudgeEnabled(TenantA, false, Now);

        Assert.False(resolver.TurnVerdict(TenantA).JudgeEnabled);
        // The row is PRESENT and says false, rather than being removed. "I turned this off" and "I never
        // touched it" happen to behave the same here, but they are different facts and the store keeps them
        // apart - the same call the voice-mode switch makes.
        Assert.Equal("false", store.Get(TenantA, TenantSettingKeys.TurnVerdictJudgeEnabled));
    }

    [Fact]
    public void A_stored_value_that_is_not_a_boolean_reads_as_off_and_never_as_on()
    {
        var (store, resolver) = NewSettings();
        // The shape a rollback, a hand edit, or a newer Gateway could leave behind.
        store.Set(TenantA, TenantSettingKeys.TurnVerdictColourEnabled, "yes-please", Now);
        store.Set(TenantA, TenantSettingKeys.TurnVerdictJudgeEnabled, "", Now);

        var settings = resolver.TurnVerdict(TenantA);

        // Degrading toward ON would be a corrupt row widening the product's licence: spending on every stop,
        // and painting sessions calm on the strength of a value nobody wrote.
        Assert.False(settings.ColourEnabled);
        Assert.False(settings.JudgeEnabled);
    }

    [Fact]
    public void One_accounts_switches_are_never_read_for_another()
    {
        var (_, resolver) = NewSettings();
        resolver.SetTurnVerdictJudgeEnabled(TenantA, true, Now);
        resolver.SetTurnVerdictColourEnabled(TenantA, true, Now);

        var other = resolver.TurnVerdict(TenantB);

        Assert.False(other.JudgeEnabled);
        Assert.False(other.ColourEnabled);
    }

    [Fact]
    public void Both_keys_are_in_the_closed_set_and_a_key_nobody_classified_is_still_refused()
    {
        var (store, _) = NewSettings();

        Assert.Contains(TenantSettingKeys.TurnVerdictJudgeEnabled, TenantSettingKeys.All);
        Assert.Contains(TenantSettingKeys.TurnVerdictColourEnabled, TenantSettingKeys.All);

        // The set stays closed. A key added to the store without being classified here would be a setting
        // the typed resolver never reads and nothing can turn off - which is the failure the closed set
        // exists to prevent, and it must not have been loosened by adding two keys to it.
        Assert.Throws<ArgumentException>(
            () => store.Set(TenantA, "turn_verdict_invented_next_year", "true", Now));
    }
}
