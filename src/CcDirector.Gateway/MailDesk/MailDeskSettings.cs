using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Settings;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE NAMES OF THE MAIL DESK SETTINGS (Mail Desk phase 3 contract, section 7): the word a change names in
/// <c>POST /gateway/mail-desk/settings</c>, in <c>maildesk set</c>, and as the subject of its recorded change.
/// </summary>
public static class MailDeskSettingNames
{
    public const string RouteThreshold = "route-threshold";
    public const string SpamThreshold = "spam-threshold";
    public const string SpendLimit = "spend-limit";
    public static readonly IReadOnlyList<string> All = new[] { RouteThreshold, SpamThreshold, SpendLimit };
}

/// <summary>
/// ONE ACCOUNT'S MAIL DESK SETTINGS, as the screening reads them: the two thresholds and the spend limit. An account
/// that never set one has its default (<see cref="Defaults"/>).
/// </summary>
/// <param name="RouteThreshold">The confidence at or above which the route question's answer routes an email.</param>
/// <param name="SpamThreshold">The confidence at or above which an unknown sender's "spam" answer junks an email.</param>
/// <param name="SpendLimitUsd">The model spend, in US dollars, at or over which no model question is asked.</param>
public sealed record MailDeskSettings(decimal RouteThreshold, decimal SpamThreshold, decimal SpendLimitUsd)
{
    /// <summary>The defaults of section 1: route 0.90, spam 0.95, a spend limit of 5.00 dollars.</summary>
    public static readonly MailDeskSettings Defaults = new(0.90m, 0.95m, 5.00m);

    /// <summary>The lowest and highest a threshold may be.</summary>
    public const decimal MinThreshold = 0.5m;

    /// <inheritdoc cref="MinThreshold"/>
    public const decimal MaxThreshold = 1.0m;

    /// <summary>The lowest and highest the spend limit may be, in US dollars.</summary>
    public const decimal MinSpendLimitUsd = 0m;

    /// <inheritdoc cref="MinSpendLimitUsd"/>
    public const decimal MaxSpendLimitUsd = 100m;

    /// <summary>The value of the setting named <paramref name="name"/>.</summary>
    public decimal Get(string name) => name switch
    {
        MailDeskSettingNames.RouteThreshold => RouteThreshold,
        MailDeskSettingNames.SpamThreshold => SpamThreshold,
        MailDeskSettingNames.SpendLimit => SpendLimitUsd,
        _ => throw new ArgumentException($"'{name}' is not a Mail Desk setting; it is one of {MailDeskWords.InWords(MailDeskSettingNames.All)}.", nameof(name)),
    };

    /// <summary>These settings with the one named <paramref name="name"/> set to <paramref name="value"/>.</summary>
    public MailDeskSettings With(string name, decimal value) => name switch
    {
        MailDeskSettingNames.RouteThreshold => this with { RouteThreshold = value },
        MailDeskSettingNames.SpamThreshold => this with { SpamThreshold = value },
        MailDeskSettingNames.SpendLimit => this with { SpendLimitUsd = value },
        _ => throw new ArgumentException($"'{name}' is not a Mail Desk setting; it is one of {MailDeskWords.InWords(MailDeskSettingNames.All)}.", nameof(name)),
    };

    /// <summary>The <c>tenant_settings</c> key a setting is stored under.</summary>
    internal static string KeyOf(string name) => name switch
    {
        MailDeskSettingNames.RouteThreshold => TenantSettingKeys.MailDeskRouteThreshold,
        MailDeskSettingNames.SpamThreshold => TenantSettingKeys.MailDeskSpamThreshold,
        MailDeskSettingNames.SpendLimit => TenantSettingKeys.MailDeskSpendLimitUsd,
        _ => throw new ArgumentException($"'{name}' is not a Mail Desk setting.", nameof(name)),
    };

    /// <summary>Why <paramref name="value"/> cannot be the setting <paramref name="name"/>, or null when it can.</summary>
    public static string? RangeRefusal(string name, decimal value)
    {
        var (min, max, what) = name == MailDeskSettingNames.SpendLimit
            ? (MinSpendLimitUsd, MaxSpendLimitUsd, "a number of US dollars")
            : (MinThreshold, MaxThreshold, "a confidence");
        return value < min || value > max
            ? $"{name} is {what} from {Number(min)} to {Number(max)}; {Number(value)} is outside that"
            : null;
    }

    /// <summary>A value as the history and the refusals write it: invariant, with no trailing zeros past two places.</summary>
    internal static string Number(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);
}

/// <summary>What a settings change came to.</summary>
public enum MailDeskSettingOutcome
{
    /// <summary>The setting was changed and the change recorded.</summary>
    Set,

    /// <summary>The name is not a Mail Desk setting. Nothing was recorded.</summary>
    UnknownName,

    /// <summary>The value is outside the setting's range. Nothing was recorded.</summary>
    OutOfRange,
}

/// <summary>The result of <see cref="MailDeskSettingsStore.Set"/>: the settings after it, and the change it recorded.</summary>
public sealed record MailDeskSettingWrite(
    MailDeskSettingOutcome Outcome,
    MailDeskSettings? Settings,
    MailDeskListChangeEntity? Change,
    string? Refusal);

/// <summary>
/// WHERE THE MAIL DESK SETTINGS ARE KEPT (Mail Desk phase 3 contract, section 7): one <c>tenant_settings</c> row per
/// setting the account has changed, and for every change one <c>mail_desk_list_changes</c> row of kind
/// <c>setting</c> saying from what, to what, who and why. The setting and its record are written in ONE save, so a
/// setting can never change without its record, nor a record stand for a change that was not made.
///
/// A stored value that cannot be read, or one outside its range, is a damaged row and throws: answering the default
/// instead would screen an account's mail by a threshold nobody chose, with no word of why.
/// </summary>
public sealed class MailDeskSettingsStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskSettingsStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The account's settings: each one as stored, or its default when the account never changed it.</summary>
    public MailDeskSettings Read(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ReadIn(ctx, tenant);
        }
    }

    /// <summary>
    /// Change one setting and record the change. A name that is not a setting, or a value outside its range, is
    /// answered with its refusal and nothing is written. The why is required: a route refuses a blank one first, so
    /// a blank one here throws.
    /// </summary>
    public MailDeskSettingWrite Set(TenantId tenant, string? name, decimal value, MailDeskWho who, string? why, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(who);
        var reason = MailDeskWho.RequireWhy(why);
        var folded = (name ?? "").Trim().ToLowerInvariant();
        if (!MailDeskWords.IsOneOf(folded, MailDeskSettingNames.All))
            return new MailDeskSettingWrite(MailDeskSettingOutcome.UnknownName, null, null,
                $"'{name}' is not a Mail Desk setting; it is one of {MailDeskWords.InWords(MailDeskSettingNames.All)}");
        if (MailDeskSettings.RangeRefusal(folded, value) is { } outOfRange)
            return new MailDeskSettingWrite(MailDeskSettingOutcome.OutOfRange, null, null, outOfRange);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var before = ReadIn(ctx, tenant);
            var from = before.Get(folded);
            var key = MailDeskSettings.KeyOf(folded);
            var stored = MailDeskSettings.Number(value);
            var row = ctx.TenantSettings.FirstOrDefault(e => e.Key == key);
            if (row is null)
                ctx.TenantSettings.Add(new TenantSettingEntity { TenantId = tenant.Value, Key = key, Value = stored, UpdatedAtUtc = nowUtc });
            else
            {
                row.Value = stored;
                row.UpdatedAtUtc = nowUtc;
            }
            // A change of the spend limit ends the stop the old limit made: the next stop is recorded afresh, so the
            // spend problem is open only while the limit set now is the one that stopped a call.
            if (folded == MailDeskSettingNames.SpendLimit
                && ctx.TenantSettings.FirstOrDefault(e => e.Key == TenantSettingKeys.MailDeskSpendStoppedUtc) is { } stoppedRow)
                ctx.TenantSettings.Remove(stoppedRow);
            var detail = JsonSerializer.Serialize(new { from, to = value });
            var change = new MailDeskChange(MailDeskWords.ChangeKinds.Setting, MailDeskWords.ChangeActions.Set, folded, detail, who, reason)
                .ToEntity(tenant.Value, nowUtc, null, null);
            ctx.MailDeskListChanges.Add(change);
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskSettingsStore] Set: account={tenant.ToLogString()} {folded} {MailDeskSettings.Number(from)} -> {stored} by {who.Kind} {who.Id}");
            return new MailDeskSettingWrite(MailDeskSettingOutcome.Set, before.With(folded, value), change, null);
        }
    }

    /// <summary>
    /// When the spend limit first stopped a model question since it was last set, or null when it has stopped none.
    /// A stored time that cannot be read is a damaged row and throws.
    /// </summary>
    public DateTime? SpendStoppedUtc(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var text = ctx.TenantSettings.AsNoTracking().Where(e => e.Key == TenantSettingKeys.MailDeskSpendStoppedUtc)
                .Select(e => e.Value).FirstOrDefault();
            if (text is null) return null;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                throw new InvalidOperationException(
                    $"The Mail Desk spend stop stored for account {tenant.ToLogString()} cannot be read as a time: '{text}'.");
            return DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Record that the spend limit stopped a model question at <paramref name="nowUtc"/> - only the FIRST stop since the
    /// limit was last set is kept, so the spend problem names when the Mail Desk stopped. Answers whether it wrote.
    /// </summary>
    public bool MarkSpendStopped(TenantId tenant, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (ctx.TenantSettings.Any(e => e.Key == TenantSettingKeys.MailDeskSpendStoppedUtc)) return false;
            var at = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            ctx.TenantSettings.Add(new TenantSettingEntity
            {
                TenantId = tenant.Value,
                Key = TenantSettingKeys.MailDeskSpendStoppedUtc,
                Value = at.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAtUtc = at,
            });
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskSettingsStore] MarkSpendStopped: account={tenant.ToLogString()} the spend limit stopped a model question at {at:O}");
            return true;
        }
    }

    private static MailDeskSettings ReadIn(GatewayDbContext ctx, TenantId tenant)
    {
        var keys = MailDeskSettingNames.All.Select(MailDeskSettings.KeyOf).ToList();
        var stored = ctx.TenantSettings.AsNoTracking().Where(e => keys.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var settings = MailDeskSettings.Defaults;
        foreach (var name in MailDeskSettingNames.All)
        {
            if (!stored.TryGetValue(MailDeskSettings.KeyOf(name), out var text)) continue;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                || MailDeskSettings.RangeRefusal(name, value) is not null)
                throw new InvalidOperationException(
                    $"The Mail Desk setting {name} stored for account {tenant.ToLogString()} cannot be read as one: '{text}'.");
            settings = settings.With(name, value);
        }
        return settings;
    }
}

/// <summary>
/// THE SPEND, FOLDED ONCE (repository rule 7; contract section 7): the settings, what the Mail Desk has spent on model
/// questions for the account, whether the limit has stopped it, and the one line every client shows. <c>GET
/// /settings</c> answers it and <c>GET /status</c> carries it as its <c>spend</c> block.
/// </summary>
public sealed record MailDeskSpendView(
    decimal RouteThreshold,
    decimal SpamThreshold,
    decimal SpendLimitUsd,
    decimal SpentUsd,
    bool Stopped,
    string Label)
{
    /// <summary>
    /// Fold the thresholds of <paramref name="settings"/> and the account's <paramref name="spend"/>. Everything about
    /// the money - the limit, what was spent, whether the Mail Desk has stopped and the sentence saying so - comes from
    /// <see cref="MailDeskSpend"/>, the one fold the spend problem reads too, so the settings and the Open problems card
    /// can never disagree (phase 3, rule 7).
    /// </summary>
    public static MailDeskSpendView Fold(MailDeskSettings settings, MailDeskSpend spend)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spend);
        var spent = spend.SpentMicros / 1_000_000m;
        var limit = spend.LimitMicros / 1_000_000m;
        var label = spend.Stopped
            ? spend.StoppedSentence
            : $"{MailDeskMoney.Dollars(spent)} spent of the {MailDeskMoney.Dollars(limit)} spend limit";
        return new MailDeskSpendView(settings.RouteThreshold, settings.SpamThreshold, limit, spent, spend.Stopped, label);
    }
}

/// <summary>Money as the Mail Desk writes it: dollars, at least two places, up to six (a call costs about $0.0001).</summary>
public static class MailDeskMoney
{
    public static string Dollars(decimal usd) => "$" + usd.ToString("0.00####", CultureInfo.InvariantCulture);

    /// <summary>Dollars as the millionths a decision row keeps.</summary>
    public static long Micros(decimal usd) => (long)Math.Round(usd * 1_000_000m, MidpointRounding.AwayFromZero);
}
