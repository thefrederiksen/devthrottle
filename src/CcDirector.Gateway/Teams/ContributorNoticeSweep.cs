using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>What one pass of the notice sweep did, for the log and the tests.</summary>
public sealed record ContributorNoticeSweepResult(int EmailsSent, int Settled, int Failed);

/// <summary>
/// TELLS CONTRIBUTORS THAT SOMETHING IS WAITING, BY THEIR OWN CHOICE (Team implementation piece 4.4,
/// devthrottle_internal#2801). Runs once a minute on the Gateway's timers, over every team, and for each person with
/// something waiting (<see cref="ContributorNotices"/>):
///
/// <list type="bullet">
/// <item><b>Right away</b> (the default): one email for each thing, at the next pass after it arrived.</item>
/// <item><b>Once a day</b>: during the hour the person chose, in their own time zone (the time zone of their own account),
/// one email naming every team something waits in - and none when nothing waits. A day is handled once: the mark is
/// stored, so a restart inside the hour sends nothing again. A Gateway down for that whole hour rolls what waits into the
/// next day's email.</item>
/// <item><b>Never</b>: what waits is settled without an email.</item>
/// </list>
///
/// ONLY THE PERSON IT WAS SENT TO, AND ONLY WHILE THEY ARE A CONTRIBUTOR. Each notice names its one recipient, the email is
/// addressed to that person's own account and nobody else (<see cref="IPersonEmailSender"/>), and a person who is no
/// longer a contributor of the team is not emailed: their notice is settled as such. (Removing someone from a team also
/// deletes what waits for them - <see cref="TeamRegistry"/>.)
///
/// THE EMAIL CARRIES NO CONTENT (<see cref="ContributorNoticeEmail"/>): the team's name and the link to the Inbox.
///
/// A send that fails is tried again, the tries moving apart (one minute, two, four, up to an hour, or longer when the email
/// service asks), and a notice is settled only when the email service confirmed the send. A refusal that trying again
/// cannot change (no account here, or an answer from 400 to 499 other than 429) is settled as undeliverable and logged,
/// so retries end. The clock is injected; nothing here waits.
///
/// TWO GATEWAYS NEVER SEND ONE NOTICE TWICE. During a deploy the new Gateway runs beside the old one over the same
/// database, and both sweeps fire. Before each send the sweep CLAIMS what it is about to tell - a right-away notice by one
/// conditional update of its next try (<see cref="ContributorNotices.Claim"/>), a day's daily email by one conditional
/// update of the day's mark (<see cref="ContributorNotices.ClaimDay"/>) - and sends only what it claimed.
/// </summary>
public sealed class ContributorNoticeSweep
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>The longest the sweep itself waits between two tries of one email.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>How long a claim holds a notice for the send in hand. Well beyond one call to the email service (30 seconds
    /// at most), so a claim never lapses under a send still running; a Gateway that stops mid-send leaves the notice to be
    /// tried again after it.</summary>
    internal static readonly TimeSpan ClaimHold = TimeSpan.FromMinutes(2);

    /// <summary>The Inbox page of the contributor's app (<see cref="TeamApp"/>), under the Gateway's public address.</summary>
    internal const string InboxPath = "/contributor/inbox";

    private readonly GatewayDatabase _db;
    private readonly ContributorNotices _notices;
    private readonly TeamRegistry _teams;
    private readonly TenantRegistry _tenants;
    private readonly IPersonEmailSender _mail;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly Func<string?> _publicBase;
    private readonly Func<DateTime> _utcNow;
    private int _running;

    /// <param name="timeZoneOf">The time zone of an account's own tenant (the per-account setting).</param>
    /// <param name="publicBase">This Gateway's public address (<see cref="GatewayPublicUrl.ResolveBase"/>).</param>
    /// <param name="utcNow">The clock.</param>
    public ContributorNoticeSweep(GatewayDatabase db, ContributorNotices notices, TeamRegistry teams, TenantRegistry tenants,
        IPersonEmailSender mail, Func<TenantId, string> timeZoneOf, Func<string?> publicBase, Func<DateTime> utcNow)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _timeZoneOf = timeZoneOf ?? throw new ArgumentNullException(nameof(timeZoneOf));
        _publicBase = publicBase ?? throw new ArgumentNullException(nameof(publicBase));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>One pass over every team. A pass still running when the next is due is not overlapped: the second returns
    /// at once having done nothing.</summary>
    public async Task<ContributorNoticeSweepResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            FileLog.Write("[ContributorNoticeSweep] RunOnceAsync: the previous pass is still running - skipped");
            return new ContributorNoticeSweepResult(0, 0, 0);
        }

        try
        {
            var waiting = AllWaiting();
            if (waiting.Count == 0)
                return new ContributorNoticeSweepResult(0, 0, 0);

            var root = _publicBase();
            if (string.IsNullOrWhiteSpace(root))
            {
                FileLog.Write($"[ContributorNoticeSweep] RunOnceAsync: {waiting.Count} notice(s) wait, but this Gateway has no public address, so the Inbox link cannot be written - nothing sent, tried again next pass");
                return new ContributorNoticeSweepResult(0, 0, 0);
            }
            var link = root.Trim().TrimEnd('/') + InboxPath;

            var tally = new Tally();
            foreach (var person in waiting.GroupBy(w => w.Notice.RecipientSubject, StringComparer.Ordinal))
                await TellPersonAsync(person.Key, person.ToList(), link, tally, ct).ConfigureAwait(false);

            FileLog.Write($"[ContributorNoticeSweep] RunOnceAsync: {waiting.Count} waiting notice(s) read, {tally.Sent} email(s) sent, {tally.Settled} notice(s) settled, {tally.Failed} send(s) failed (tried again later)");
            return new ContributorNoticeSweepResult(tally.Sent, tally.Settled, tally.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The timer's entry point: a pass whose failure is logged and left to the next pass, never thrown into the
    /// timer thread.</summary>
    public void RunSafe() => _ = RunSafeAsync();

    private async Task RunSafeAsync()
    {
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ContributorNoticeSweep] RunSafe FAILED ({ex.GetType().Name}): {ex.Message} - the next pass tries again");
        }
    }

    private sealed class Tally
    {
        public int Sent;
        public int Settled;
        public int Failed;
    }

    private sealed record Waiting(string TeamId, ContributorNoticeEntity Notice);

    private List<Waiting> AllWaiting()
    {
        List<string> teamIds;
        using (var ctx = _db.CreateUnscopedContext())
            teamIds = ctx.Teams.AsNoTracking().Select(t => t.Id).ToList();
        return teamIds.SelectMany(teamId => _notices.Waiting(teamId).Select(n => new Waiting(teamId, n))).ToList();
    }

    private async Task TellPersonAsync(string subject, List<Waiting> waiting, string link, Tally tally, CancellationToken ct)
    {
        var now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);

        // Only while they are a contributor of the team. Read once per person: the teams they are in, by name.
        var teams = _teams.ListTeamsFor(subject).ToDictionary(t => t.TeamId, StringComparer.Ordinal);
        var gone = waiting.Where(w => !teams.TryGetValue(w.TeamId, out var t) || t.Role != TeamRole.Collaborator).ToList();
        foreach (var team in gone.GroupBy(w => w.TeamId, StringComparer.Ordinal))
        {
            tally.Settled += _notices.Settle(team.Key, team.Select(w => w.Notice.Id).ToList(), ContributorNoticeOutcomes.NotAContributor, now);
            FileLog.Write($"[ContributorNoticeSweep] TellPersonAsync: team {new TenantId(team.Key).ToLogString()} - {team.Count()} notice(s) for a person no longer a contributor there, settled without an email");
        }
        var mine = waiting.Except(gone).ToList();
        if (mine.Count == 0)
            return;

        var own = _tenants.LookupBySubject(subject);
        var choice = own is { } t ? _notices.ChoiceOf(t) : ContributorNotices.Default;
        switch (choice.Mode)
        {
            case ContributorNoticeModes.Never:
                foreach (var team in mine.GroupBy(w => w.TeamId, StringComparer.Ordinal))
                    tally.Settled += _notices.Settle(team.Key, team.Select(w => w.Notice.Id).ToList(), ContributorNoticeOutcomes.Never, now);
                return;

            case ContributorNoticeModes.Now:
                foreach (var w in mine.Where(w => w.Notice.NextTryAtUtc is null || w.Notice.NextTryAtUtc <= now))
                {
                    if (!_notices.Claim(w.TeamId, w.Notice.Id, now, now + ClaimHold))
                    {
                        FileLog.Write($"[ContributorNoticeSweep] TellPersonAsync: team {new TenantId(w.TeamId).ToLogString()} - a notice is already claimed by another pass, not sent here");
                        continue;
                    }
                    var email = ContributorNoticeEmail.RightAway(teams[w.TeamId].Name, link);
                    var ok = await SendAsync(subject, email, new[] { w }, ContributorNoticeOutcomes.Emailed, now, tally, ct).ConfigureAwait(false);
                    if (!ok)
                        return; // the rest wait for the next try rather than meet the same refusal now
                }
                return;

            case ContributorNoticeModes.Daily:
                await TellDailyAsync(subject, own!.Value, choice, mine, teams, link, now, tally, ct).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"A notice choice this sweep does not know: '{choice.Mode}'.");
        }
    }

    private async Task TellDailyAsync(string subject, TenantId own, ContributorNoticeChoice choice, List<Waiting> mine,
        Dictionary<string, TeamSummary> teams, string link, DateTime now, Tally tally, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(own));
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (local.Hour != choice.DailyHour || string.Equals(choice.DailyHandledLocalDate, today, StringComparison.Ordinal))
            return;
        if (mine.Any(w => w.Notice.NextTryAtUtc is { } next && next > now))
            return;

        if (!_notices.ClaimDay(own, today))
        {
            FileLog.Write($"[ContributorNoticeSweep] TellDailyAsync: tenant {own.ToLogString()} - today's daily email is already claimed by another pass, not sent here");
            return;
        }

        var names = mine.Select(w => teams[w.TeamId].Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var email = ContributorNoticeEmail.Daily(names, link);
        if (!await SendAsync(subject, email, mine, ContributorNoticeOutcomes.InDailyEmail, now, tally, ct).ConfigureAwait(false))
            _notices.ReleaseDay(own, today, choice.DailyHandledLocalDate);
    }

    /// <summary>Send one email for <paramref name="notices"/> and settle them as <paramref name="outcome"/> when it was
    /// confirmed sent. True when it was sent (or can never be, and they were settled); false when it failed and will be
    /// tried again.</summary>
    private async Task<bool> SendAsync(string subject, PersonEmail email, IReadOnlyCollection<Waiting> notices, string outcome,
        DateTime now, Tally tally, CancellationToken ct)
    {
        var result = await _mail.SendAsync(subject, email, ct).ConfigureAwait(false);
        if (result.Sent)
        {
            tally.Sent++;
            foreach (var team in notices.GroupBy(w => w.TeamId, StringComparer.Ordinal))
                tally.Settled += _notices.Settle(team.Key, team.Select(w => w.Notice.Id).ToList(), outcome, now);
            return true;
        }

        if (result.Permanent)
        {
            foreach (var team in notices.GroupBy(w => w.TeamId, StringComparer.Ordinal))
                tally.Settled += _notices.Settle(team.Key, team.Select(w => w.Notice.Id).ToList(), ContributorNoticeOutcomes.Undeliverable, now);
            FileLog.Write($"[ContributorNoticeSweep] SendAsync: {notices.Count} notice(s) settled as undeliverable, not tried again: {result.Detail}");
            return true;
        }

        tally.Failed++;
        var tries = notices.Max(w => w.Notice.FailedTries) + 1;
        var wait = Backoff(tries, result.RetryAfter);
        foreach (var team in notices.GroupBy(w => w.TeamId, StringComparer.Ordinal))
            _notices.Failed(team.Key, team.Select(w => w.Notice.Id).ToList(), now + wait);
        FileLog.Write($"[ContributorNoticeSweep] SendAsync: NOT sent ({result.Detail}) - try {tries}, the next in {wait.TotalMinutes:0} minute(s)");
        return false;
    }

    /// <summary>The wait after the <paramref name="failedTries"/>th failed try in a row: one minute, doubling, at most
    /// <see cref="LongestWait"/> - or the email service's own wait when that is longer.</summary>
    internal static TimeSpan Backoff(int failedTries, TimeSpan? retryAfter)
    {
        var minutes = Math.Min(LongestWait.TotalMinutes, Math.Pow(2, Math.Max(0, failedTries - 1)));
        var own = TimeSpan.FromMinutes(minutes);
        return retryAfter is { } asked && asked > own ? asked : own;
    }
}
