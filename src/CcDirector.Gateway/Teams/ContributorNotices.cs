using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>What kind of thing arrived for a contributor. For the log and the tests only: the email says the same words
/// whatever it was.</summary>
public static class ContributorNoticeKinds
{
    /// <summary>A dev report (with any questions in it) was sent to them.</summary>
    public const string Report = "report";

    /// <summary>A whole factory was shared with them.</summary>
    public const string Factory = "factory";

    /// <summary>The team's Owner (or a Manager) decided on something they wrote.</summary>
    public const string Reply = "reply";
}

/// <summary>How a person wants to be told that something is waiting (design.html, "Notifications").</summary>
public static class ContributorNoticeModes
{
    /// <summary>One email for each thing, as it arrives. The default.</summary>
    public const string Now = "now";

    /// <summary>One email a day, at the hour they chose, only when something is waiting.</summary>
    public const string Daily = "daily";

    /// <summary>No email; they check for themselves.</summary>
    public const string Never = "never";

    /// <summary>The three, in the order the card shows them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Now, Daily, Never };

    /// <summary>Whether <paramref name="value"/> is exactly one of the three.</summary>
    public static bool IsMode(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>How a waiting notice was settled, as <see cref="ContributorNoticeEntity.Outcome"/> stores it.</summary>
public static class ContributorNoticeOutcomes
{
    /// <summary>Told in its own email ("right away").</summary>
    public const string Emailed = "emailed";

    /// <summary>Told in the day's one email ("once a day").</summary>
    public const string InDailyEmail = "in-daily-email";

    /// <summary>Not emailed: the person chose never.</summary>
    public const string Never = "never";

    /// <summary>Not emailed: the person is no longer a contributor of the team.</summary>
    public const string NotAContributor = "not-a-contributor";

    /// <summary>Not emailed: the person has no account on this Gateway to address.</summary>
    public const string NoAccount = "no-account";
}

/// <summary>One person's choice. <paramref name="Chosen"/> is false when they never chose, and the defaults apply.</summary>
public sealed record ContributorNoticeChoice(string Mode, int DailyHour, string? DailyHandledLocalDate, bool Chosen);

/// <summary>The outcome of changing a person's choice: the stored choice, or the sentence saying why it was refused.</summary>
public sealed record ContributorNoticeChoiceResult(ContributorNoticeChoice? Choice, string? Refusal);

/// <summary>
/// THE RECORD OF WHAT A CONTRIBUTOR HAS NOT BEEN TOLD ABOUT YET, AND HOW THEY WANT TO BE TOLD (Team implementation piece
/// 4.4, devthrottle_internal#2801). The places that send something to a contributor call <see cref="Tell"/>; the notice
/// sweep (<see cref="ContributorNoticeSweep"/>) reads what waits, emails the person by their choice, and settles it.
///
/// WHO IS TOLD. Only a person who is a contributor (a Collaborator) of the team at the moment something is sent to them:
/// the people with the whole Cockpit see their work there. A row names the team and the person and nothing else that was
/// sent (security review Risk 8), so no content can reach an email from here.
///
/// THE CHOICE is one for the person, kept in their own tenant (<see cref="ContributorNoticeChoiceEntity"/>). No row means
/// right away, the default.
/// </summary>
public sealed class ContributorNotices
{
    /// <summary>The hour of the day the daily email goes out until the person picks another.</summary>
    public const int DefaultDailyHour = 8;

    /// <summary>The choice of a person who never chose.</summary>
    public static readonly ContributorNoticeChoice Default = new(ContributorNoticeModes.Now, DefaultDailyHour, null, false);

    internal const string NotAModeRefusal = "Choose one of: email me right away, once a day, or never.";
    internal const string NotAnHourRefusal = "Choose the hour for the daily email: a whole hour from 00:00 to 23:00.";

    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly Func<DateTime> _utcNow;

    public ContributorNotices(GatewayDatabase db, TeamRegistry teams, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ---------------------------------------------------------------- what arrived

    /// <summary>
    /// Something of <paramref name="kind"/> was just sent to <paramref name="recipientSubject"/> in
    /// <paramref name="teamId"/>. Records one waiting notice when that person is a contributor of the team; returns
    /// whether it did. The send itself has already taken effect, so this never refuses it.
    /// </summary>
    public bool Tell(string teamId, string recipientSubject, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientSubject);
        if (kind is not (ContributorNoticeKinds.Report or ContributorNoticeKinds.Factory or ContributorNoticeKinds.Reply))
            throw new ArgumentException($"Not a contributor notice kind: '{kind}'.", nameof(kind));

        var team = new TenantId(teamId);
        if (_teams.RoleOf(teamId, recipientSubject) != TeamRole.Collaborator)
        {
            FileLog.Write($"[ContributorNotices] Tell: team {team.ToLogString()} kind={kind} - the person is not a contributor of the team, nothing to tell");
            return false;
        }

        using var ctx = _db.CreateContext(team);
        ctx.ContributorNotices.Add(new ContributorNoticeEntity
        {
            TenantId = teamId,
            RecipientSubject = recipientSubject,
            Kind = kind,
            CreatedAtUtc = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc),
        });
        ctx.SaveChanges();
        FileLog.Write($"[ContributorNotices] Tell: team {team.ToLogString()} kind={kind} - recorded, waiting to be told");
        return true;
    }

    /// <summary>What still waits in one team, oldest first.</summary>
    internal IReadOnlyList<ContributorNoticeEntity> Waiting(string teamId)
    {
        using var ctx = _db.CreateContext(new TenantId(teamId));
        return ctx.ContributorNotices.AsNoTracking()
            .Where(n => n.SettledAtUtc == null)
            .OrderBy(n => n.CreatedAtUtc)
            .ToList();
    }

    /// <summary>Settle the notices <paramref name="ids"/> of one team as <paramref name="outcome"/>, at
    /// <paramref name="atUtc"/>. A notice already settled is left as it is.</summary>
    internal int Settle(string teamId, IReadOnlyCollection<Guid> ids, string outcome, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (ids.Count == 0)
            return 0;
        var wanted = ids.ToList();
        using var ctx = _db.CreateContext(new TenantId(teamId));
        var rows = ctx.ContributorNotices.Where(n => wanted.Contains(n.Id) && n.SettledAtUtc == null).ToList();
        foreach (var row in rows)
        {
            row.SettledAtUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc);
            row.Outcome = outcome;
            row.NextTryAtUtc = null;
        }
        ctx.SaveChanges();
        return rows.Count;
    }

    /// <summary>A send of the notices <paramref name="ids"/> of one team failed: count it and hold the next try until
    /// <paramref name="nextTryUtc"/>.</summary>
    internal void Failed(string teamId, IReadOnlyCollection<Guid> ids, DateTime nextTryUtc)
    {
        if (ids.Count == 0)
            return;
        var wanted = ids.ToList();
        using var ctx = _db.CreateContext(new TenantId(teamId));
        foreach (var row in ctx.ContributorNotices.Where(n => wanted.Contains(n.Id) && n.SettledAtUtc == null).ToList())
        {
            row.FailedTries++;
            row.NextTryAtUtc = DateTime.SpecifyKind(nextTryUtc, DateTimeKind.Utc);
        }
        ctx.SaveChanges();
    }

    // ---------------------------------------------------------------- the person's choice

    /// <summary>The choice of the person whose own tenant is <paramref name="own"/>; <see cref="Default"/> when they never
    /// chose.</summary>
    public ContributorNoticeChoice ChoiceOf(TenantId own)
    {
        using var ctx = _db.CreateContext(own);
        var row = ctx.ContributorNoticeChoices.AsNoTracking().FirstOrDefault();
        if (row is null)
            return Default;
        if (!ContributorNoticeModes.IsMode(row.Mode))
            throw new InvalidOperationException($"contributor_notice_choices.mode holds '{row.Mode}', which is not one of now, daily or never.");
        return new ContributorNoticeChoice(row.Mode, row.DailyHour, row.DailyHandledLocalDate, true);
    }

    /// <summary>Store the choice of the person whose own tenant is <paramref name="own"/>: <paramref name="mode"/> and,
    /// for the daily email, <paramref name="dailyHour"/> (the stored hour is kept when it is not given). Refused, with the
    /// sentence, when either is not one the card offers.</summary>
    public ContributorNoticeChoiceResult SetChoice(TenantId own, string? mode, int? dailyHour)
    {
        if (!ContributorNoticeModes.IsMode(mode))
        {
            FileLog.Write($"[ContributorNotices] SetChoice: tenant {own.ToLogString()} - not a mode, REFUSED");
            return new ContributorNoticeChoiceResult(null, NotAModeRefusal);
        }
        if (dailyHour is { } h && (h < 0 || h > 23))
        {
            FileLog.Write($"[ContributorNotices] SetChoice: tenant {own.ToLogString()} - not an hour, REFUSED");
            return new ContributorNoticeChoiceResult(null, NotAnHourRefusal);
        }

        using (var ctx = _db.CreateContext(own))
        {
            var row = ctx.ContributorNoticeChoices.FirstOrDefault();
            if (row is null)
            {
                row = new ContributorNoticeChoiceEntity { TenantId = own.Value, DailyHour = DefaultDailyHour };
                ctx.ContributorNoticeChoices.Add(row);
            }
            row.Mode = mode!;
            // The day already handled stays handled: moving the hour never sends a second daily email the same day.
            if (dailyHour is { } hour)
                row.DailyHour = hour;
            row.UpdatedAtUtc = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
            ctx.SaveChanges();
        }
        var stored = ChoiceOf(own);
        FileLog.Write($"[ContributorNotices] SetChoice: tenant {own.ToLogString()} mode={stored.Mode} hour={stored.DailyHour}");
        return new ContributorNoticeChoiceResult(stored, null);
    }

    /// <summary>Mark today's daily email time of the person whose own tenant is <paramref name="own"/> as handled.</summary>
    internal void MarkDailyHandled(TenantId own, string localDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDate);
        using var ctx = _db.CreateContext(own);
        var row = ctx.ContributorNoticeChoices.FirstOrDefault()
                  ?? throw new InvalidOperationException("A daily email time was handled for a person who never chose the daily email.");
        row.DailyHandledLocalDate = localDate;
        ctx.SaveChanges();
    }
}
