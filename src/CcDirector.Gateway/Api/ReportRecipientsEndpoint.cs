using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Who the daily report should go to.
///
///   GET /gateway/reports/recipients  ->  { recipients: [ { account, email, timeZone? } ] }
///
/// The report endpoint answers "what does THIS account's day look like" and has always required the
/// caller to already know the account. The sender therefore could only ever mail a hard-coded
/// allowlist. This is the missing half: the list itself, so the daily email reaches every account on
/// the Gateway without anyone maintaining a list of addresses by hand in a deployment variable.
///
/// SAME GATE AS THE REPORT. It presents the same service token, is public in AuthMiddleware for the
/// same reason, and fails the same way: no token configured is 503 and never an open door; a wrong
/// token is 401. It is deliberately NOT a second, weaker way in - a list of every account's email
/// address is more sensitive than any single report, not less.
///
/// AN ACCOUNT WITH NO EMAIL IS OMITTED, not returned blank. The tenants table records the email as it
/// was at mint time and it is nullable; a recipient with nothing to send to is not a recipient, and
/// returning an empty address would invite the sender to try anyway.
///
/// WHETHER AN ACCOUNT WANTS THE EMAIL IS DECIDED HERE (issue #1000). An account that set its report
/// cadence to Off is not on this list, so it is not mailed. The filtering is in THIS one place - the
/// single answer to "who should be mailed" - and not in the sender, so every future channel inherits the
/// same answer instead of each one re-deriving it. An account that has never touched the setting is
/// included: the default is daily, which is what everyone received before the setting existed.
///
/// The preference is per ACCOUNT and lives on the Gateway because that is the only scope it is true at.
/// It was once asked by the first-run wizard, which runs once per director per machine - so it was asked
/// N times for one email address and the answers never reconciled. That step was removed (issue #996);
/// this is where the question ended up.
///
/// WHOSE SEVEN O'CLOCK (#3124). The owner ruled that 7am means 7am where the reader is. The sender wakes
/// hourly and needs each account's zone to know whose morning it is, so <c>timeZone</c> rides beside the
/// address - an IANA id, and ONLY when the account chose one. An account that never chose is sent without
/// the key, never with the operator default filled in: the sender keeps those on the send time everybody
/// had before, and a default dressed up as a choice would silently move them.
/// </summary>
internal static class ReportRecipientsEndpoint
{
    /// <summary>One row of the list. <c>TimeZone</c> is absent from the wire when the account chose none.</summary>
    internal sealed record Recipient(
        string Account,
        string Email,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZone);

    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; this endpoint carries its own gate.</summary>
    public const string Path = "/gateway/reports/recipients";

    public static void Map(IEndpointRouteBuilder app, TenantRegistry tenants, TenantSettingsResolver settings)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(settings);

        app.MapGet(Path, (HttpContext ctx) => Handle(ctx, tenants, settings));

        FileLog.Write($"[ReportRecipientsEndpoint] mapped {Path} (service-token authorized, read-only)");
    }

    internal static IResult Handle(HttpContext ctx, TenantRegistry tenants, TenantSettingsResolver settings)
    {
        // Reuses the report endpoint's gate rather than repeating it: one token, one rule, and no
        // chance of the two drifting so that the recipient list is guarded less well than the reports.
        if (MorningReportEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        var addressed = tenants.ListAll()
            .Where(t => !string.IsNullOrWhiteSpace(t.Email))
            .ToList();

        // The account's own answer to "do you want this mail" (issue #1000), asked per tenant. An account
        // that never chose is daily, so this removes nobody until somebody deliberately turns it off.
        var wanted = addressed
            .Where(t => settings.DailyReportCadence(new TenantId(t.TenantId)) != ReportCadence.Off)
            .ToList();

        var recipients = wanted
            .Select(t => new Recipient(t.Email!.Trim(), t.Email!.Trim(), settings.ChosenTimeZoneIana(new TenantId(t.TenantId))))
            // One address on two accounts is mailed once (below), so it can only have one morning. The
            // account that CHOSE a zone wins over the one that said nothing - a stated preference beats
            // a default. Stable sort: among equals the registry's own order is kept.
            .OrderBy(r => r.TimeZone is null)
            // Two accounts may legitimately carry one address, and they may disagree about wanting the
            // mail. Dropping the duplicate AFTER the filter is what makes "one account still wants it"
            // win over "the other turned it off" - the address is mailed once, which is the only answer
            // that serves both, and silence would be an opt-out one account never asked for.
            .DistinctBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Counts only - the addresses themselves are personally identifying and this log is not the
        // place for them (the tenants table says the email is never logged). The opted-out count is
        // logged beside the served count so a shrinking list has a stated reason and is never mistaken
        // for accounts going missing.
        FileLog.Write($"[ReportRecipientsEndpoint] served {recipients.Count} recipient(s), " +
                      $"{recipients.Count(r => r.TimeZone is not null)} on a time zone of their own; " +
                      $"{addressed.Count - wanted.Count} account(s) have the report turned off");
        return Results.Json(new { recipients });
    }
}
