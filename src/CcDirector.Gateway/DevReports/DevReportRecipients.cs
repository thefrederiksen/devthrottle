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
    /// <paramref name="recipientSubjects"/> (Tech Lead ruling F1: a send covers the VERSION SENT). A member not sent it
    /// before gets a row holding that version. A member holding an EARLIER version is moved to this one: sent again now,
    /// and unread again, because what they hold changed. A member already holding this version or a later one is left
    /// exactly as they are - a send never moves anyone backwards - so a send can be repeated safely. Returns every
    /// recipient of the report afterwards, oldest send first.
    /// </summary>
    public IReadOnlyList<DevReportRecipientEntity> Send(TenantId team, Guid reportId, string senderSubject,
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

        using var ctx = _db.CreateContext(team);
        var existing = ctx.DevReportRecipients
            .Where(r => r.ReportId == reportId)
            .ToList()
            .ToDictionary(r => r.RecipientSubject, StringComparer.Ordinal);
        var added = 0;
        var moved = 0;
        foreach (var subject in recipientSubjects.Distinct(StringComparer.Ordinal))
        {
            if (existing.TryGetValue(subject, out var row))
            {
                if (row.SentVersion >= version) continue;
                row.SentVersion = version;
                row.SentBySubject = senderSubject;
                row.SentAtUtc = nowUtc;
                row.ReadAtUtc = null;
                moved++;
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
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            // Two sends of the same report to the same member at once (two Gateway processes during a deploy): the unique
            // index refuses the second, and the row the first wrote is the answer when it holds this version or a later
            // one. Any other failure leaves a recipient without what was sent, and that is thrown below rather than
            // answered as sent.
            FileLog.Write($"[DevReportRecipients] Send: report={reportId} write refused ({ex.InnerException?.Message ?? ex.Message}); re-reading");
            var now = RecipientsOf(team, reportId).ToDictionary(r => r.RecipientSubject, r => r.SentVersion, StringComparer.Ordinal);
            if (!recipientSubjects.All(s => now.TryGetValue(s, out var held) && held >= version))
                throw;
        }
        FileLog.Write($"[DevReportRecipients] Send: tenant={team.ToLogString()} report={reportId} version={version} added={added} moved={moved} of {recipientSubjects.Count}");
        return RecipientsOf(team, reportId);
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
    /// Mark the report read by <paramref name="recipientSubject"/>, the first time only - a later open leaves the first
    /// time alone. False when the report was not sent to them.
    /// </summary>
    public bool MarkRead(TenantId team, Guid reportId, string recipientSubject, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientSubject);
        using var ctx = _db.CreateContext(team);
        var row = ctx.DevReportRecipients.FirstOrDefault(r => r.ReportId == reportId && r.RecipientSubject == recipientSubject);
        if (row is null) return false;
        if (row.ReadAtUtc is null)
        {
            row.ReadAtUtc = nowUtc;
            ctx.SaveChanges();
            FileLog.Write($"[DevReportRecipients] MarkRead: tenant={team.ToLogString()} report={reportId} - read for the first time");
        }
        return true;
    }
}

/// <summary>One report sent to a member, as their Reports page lists it: their row, and the title and status of the version
/// they hold.</summary>
internal sealed record DevReportReceived(DevReportRecipientEntity Row, string Title, string Status);
