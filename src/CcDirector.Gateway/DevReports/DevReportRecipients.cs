using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// "SENT TO A NAMED MEMBER" - THE ONE RECORD OF WHICH MEMBERS OF A TEAM A DEV REPORT WAS SENT TO (devthrottle_internal#2309).
/// A recipient reads exactly the reports this record names them on, and nothing else; the Collaborator's Questions
/// (devthrottle_internal#2307) asks the same record whether a question was put to someone.
///
/// Every operation takes the team's tenant and reads through a context scoped to it, so another team's rows are never
/// found. This class records; it does not rule. Who may send a report (its author) and who may receive one (a member
/// whose role may read reports sent to them) are decided by the team gate and <see cref="Teams.TeamAccess"/> before
/// anything is written here. Account subjects are personally identifying and are never logged.
/// </summary>
internal sealed class DevReportRecipients
{
    private readonly GatewayDatabase _db;

    public DevReportRecipients(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Record that <paramref name="senderSubject"/> sent <paramref name="version"/> of the report to each of
    /// <paramref name="recipientSubjects"/> (Tech Lead ruling F1: a send covers the VERSION SENT) - but only while that
    /// version is still the report's newest (delta review D2, round-3 review R2).
    ///
    /// ONE DATABASE DECISION. The first statement of one transaction is a conditional write on the report's own row,
    /// <c>update dev_reports set Version = Version where Id = report and Version = version</c>. It matches no row when a
    /// publish has already committed a newer version - from this process or the other one during a deploy, which no
    /// in-memory lock spans - and then nothing is written and the answer is <see cref="DevReportSendOutcome.Sent"/> false,
    /// which the route answers 409. When it matches, it holds the report's row until the recipient rows are committed
    /// with it, so a publish that lands meanwhile waits and becomes the NEXT version: the send took effect while its
    /// version was the newest. A check read before the write could not say that (R2).
    ///
    /// A member not sent it before gets a row holding that version. A member holding an EARLIER version is moved to this
    /// one: sent again now, and unread again, because what they hold changed. A member already holding this version or a
    /// later one is left exactly as they are - a send never moves anyone backwards - by a conditional update on their row
    /// (<c>where SentVersion &lt; version</c>, delta review D7). Two sends of the same report to the same new member at
    /// once collide on the unique index; the loser's transaction is rolled back and run once more, when that member is
    /// found and moved instead. A second failure is a fault and surfaces.
    /// </summary>
    public DevReportSendOutcome Send(TenantId team, Guid reportId, string senderSubject,
        IReadOnlyCollection<string> recipientSubjects, int version, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderSubject);
        ArgumentNullException.ThrowIfNull(recipientSubjects);
        if (recipientSubjects.Count == 0)
            throw new ArgumentException("A send names at least one recipient.", nameof(recipientSubjects));
        if (recipientSubjects.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A recipient is an account subject, never empty.", nameof(recipientSubjects));
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), version, "A report version starts at 1.");

        BeforeSendWriteForTests?.Invoke();
        try { return SendOnce(team, reportId, senderSubject, recipientSubjects, version, nowUtc); }
        catch (DbUpdateException ex)
        {
            FileLog.Write($"[DevReportRecipients] Send: report={reportId} lost a race with another send " +
                          $"({ex.InnerException?.Message ?? ex.Message}); rolled back, running once more");
            return SendOnce(team, reportId, senderSubject, recipientSubjects, version, nowUtc);
        }
    }

    /// <summary>Runs after the caller has checked the version and before the send's transaction begins - so a test can
    /// stand in for a publish landing in between (round-3 review R2). Null outside tests.</summary>
    internal Action? BeforeSendWriteForTests { get; set; }

    private DevReportSendOutcome SendOnce(TenantId team, Guid reportId, string senderSubject,
        IReadOnlyCollection<string> recipientSubjects, int version, DateTime nowUtc)
    {
        using var ctx = _db.CreateContext(team);
        using var tx = ctx.Database.BeginTransaction();
        var stillNewest = ctx.DevReports
            .Where(r => r.Id == reportId && r.Version == version)
            .ExecuteUpdate(set => set.SetProperty(r => r.Version, r => r.Version));
        if (stillNewest != 1)
        {
            FileLog.Write($"[DevReportRecipients] Send: tenant={team.ToLogString()} report={reportId} version={version} is not the newest - nothing sent");
            return new DevReportSendOutcome(false, RecipientsOf(team, reportId));
        }

        var existing = ctx.DevReportRecipients.AsNoTracking()
            .Where(r => r.ReportId == reportId)
            .Select(r => r.RecipientSubject)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var moved = 0;
        foreach (var subject in recipientSubjects.Distinct(StringComparer.Ordinal))
        {
            if (existing.Contains(subject))
            {
                moved += ctx.DevReportRecipients
                    .Where(r => r.ReportId == reportId && r.RecipientSubject == subject && r.SentVersion < version)
                    .ExecuteUpdate(set => set
                        .SetProperty(r => r.SentVersion, version)
                        .SetProperty(r => r.SentBySubject, senderSubject)
                        .SetProperty(r => r.SentAtUtc, nowUtc)
                        .SetProperty(r => r.ReadAtUtc, (DateTime?)null));
                continue;
            }
            ctx.DevReportRecipients.Add(new DevReportRecipientEntity
            {
                TenantId = team.Value,
                ReportId = reportId,
                RecipientSubject = subject,
                SentBySubject = senderSubject,
                SentVersion = version,
                SentAtUtc = nowUtc,
            });
            added++;
        }
        ctx.SaveChanges();
        tx.Commit();
        FileLog.Write($"[DevReportRecipients] Send: tenant={team.ToLogString()} report={reportId} version={version} added={added} moved={moved} of {recipientSubjects.Count}");
        return new DevReportSendOutcome(true, RecipientsOf(team, reportId));
    }

    /// <summary>The recipient's row for the report - which version they hold - or null when it was not sent to them.</summary>
    public DevReportRecipientEntity? RowFor(TenantId team, Guid reportId, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        using var ctx = _db.CreateContext(team);
        return ctx.DevReportRecipients.AsNoTracking().FirstOrDefault(r => r.ReportId == reportId && r.RecipientSubject == subject);
    }

    /// <summary>Whether the report was sent to <paramref name="subject"/>.</summary>
    public bool IsSentTo(TenantId team, Guid reportId, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        using var ctx = _db.CreateContext(team);
        return ctx.DevReportRecipients.AsNoTracking().Any(r => r.ReportId == reportId && r.RecipientSubject == subject);
    }

    /// <summary>
    /// The reports sent to <paramref name="recipientSubject"/> in the team, newest send first, each with the title and
    /// status of the VERSION they were sent - read in one query, joined to that version. A row whose version is gone is
    /// absent.
    /// </summary>
    public IReadOnlyList<DevReportReceived> SentTo(TenantId team, string recipientSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientSubject);
        using var ctx = _db.CreateContext(team);
        var rows = (from r in ctx.DevReportRecipients.AsNoTracking()
                    where r.RecipientSubject == recipientSubject
                    join v in ctx.DevReportVersions.AsNoTracking()
                        on new { r.ReportId, Version = r.SentVersion } equals new { v.ReportId, v.Version }
                    select new { Row = r, v.Title, v.Status })
            .ToList()
            .OrderByDescending(x => x.Row.SentAtUtc)
            .ThenBy(x => x.Row.ReportId)
            .Select(x => new DevReportReceived(x.Row, x.Title, x.Status))
            .ToList();
        FileLog.Write($"[DevReportRecipients] SentTo: tenant={team.ToLogString()} count={rows.Count}");
        return rows;
    }

    /// <summary>Everyone the report was sent to, oldest send first.</summary>
    public IReadOnlyList<DevReportRecipientEntity> RecipientsOf(TenantId team, Guid reportId)
    {
        using var ctx = _db.CreateContext(team);
        return ctx.DevReportRecipients.AsNoTracking()
            .Where(r => r.ReportId == reportId)
            .ToList()
            .OrderBy(r => r.SentAtUtc)
            .ThenBy(r => r.RecipientSubject, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>How many members each report was sent to, for a list. A report sent to nobody is absent.</summary>
    public IReadOnlyDictionary<Guid, int> RecipientCounts(TenantId team, IReadOnlyCollection<Guid> reportIds)
    {
        ArgumentNullException.ThrowIfNull(reportIds);
        if (reportIds.Count == 0) return new Dictionary<Guid, int>();
        using var ctx = _db.CreateContext(team);
        return ctx.DevReportRecipients.AsNoTracking()
            .Where(r => reportIds.Contains(r.ReportId))
            .GroupBy(r => r.ReportId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
    }

    /// <summary>
    /// Mark <paramref name="version"/> of the report read by <paramref name="recipientSubject"/> - only when that is the
    /// version they hold, and the first time only (delta review D5). The page names the version it showed, so a read
    /// posted for version 1 that lands after the row moved to version 2 marks nothing: version 2 has not been seen. One
    /// conditional update, so a send moving the row at the same moment cannot be overwritten.
    /// </summary>
    public DevReportReadMark MarkRead(TenantId team, Guid reportId, string recipientSubject, int version, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientSubject);
        using var ctx = _db.CreateContext(team);
        var marked = ctx.DevReportRecipients
            .Where(r => r.ReportId == reportId && r.RecipientSubject == recipientSubject && r.SentVersion == version && r.ReadAtUtc == null)
            .ExecuteUpdate(set => set.SetProperty(r => r.ReadAtUtc, (DateTime?)nowUtc));
        if (marked == 1)
        {
            FileLog.Write($"[DevReportRecipients] MarkRead: tenant={team.ToLogString()} report={reportId} version={version} - read for the first time");
            return DevReportReadMark.Read;
        }
        var row = ctx.DevReportRecipients.AsNoTracking().FirstOrDefault(r => r.ReportId == reportId && r.RecipientSubject == recipientSubject);
        if (row is null) return DevReportReadMark.NotSent;
        if (row.SentVersion != version)
        {
            FileLog.Write($"[DevReportRecipients] MarkRead: tenant={team.ToLogString()} report={reportId} version={version} - they hold version {row.SentVersion}, NOT marked");
            return DevReportReadMark.VersionNotHeld;
        }
        return DevReportReadMark.Read;
    }
}

/// <summary>
/// What a send did. <paramref name="Sent"/> is false when the version named was no longer the report's newest at the
/// moment of the write, and then nothing was written. <paramref name="Recipients"/> is everyone the report was sent to
/// afterwards, oldest send first.
/// </summary>
internal sealed record DevReportSendOutcome(bool Sent, IReadOnlyList<DevReportRecipientEntity> Recipients);

/// <summary>What marking a report read did.</summary>
internal enum DevReportReadMark
{
    /// <summary>The version named is the one held, and it is now read (or already was).</summary>
    Read,
    /// <summary>The report was not sent to this person.</summary>
    NotSent,
    /// <summary>The person holds a different version from the one named, so nothing was marked.</summary>
    VersionNotHeld,
}

/// <summary>One report sent to a member, as their Reports page lists it: their row, and the title and status of the version
/// they hold.</summary>
internal sealed record DevReportReceived(DevReportRecipientEntity Row, string Title, string Status);
