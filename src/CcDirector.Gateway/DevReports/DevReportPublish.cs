using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// What a publish did. Exactly one shape is filled: a refusal (<see cref="RefusalCode"/> set, nothing written), or a
/// published report with the mission link it carries (<see cref="Link"/>, null when the report is no Mission's
/// document). <see cref="LinkLost"/> is the one case in between: the report WAS written, and its Mission was removed
/// before the link could be made.
/// </summary>
internal sealed record DevReportPublishOutcome(
    DevReportEntity? Report,
    bool Created,
    MissionDocumentLinkResult? Link,
    string? RefusalCode,
    string? Refusal,
    Guid? LinkLostMissionId)
{
    public bool Refused => RefusalCode is not null;
    public bool LinkLost => LinkLostMissionId is not null;
}

/// <summary>
/// The publish step of <c>POST /sessions/{sid}/dev-reports</c>, after the body has been read and the shape checked,
/// kept out of the route so its ORDER can be tested without hosting it (review finding 7):
///
///   1. A publish asked to be a mission document finds the session's own Mission FIRST, and a refusal returns before
///      the store is touched - so "no Mission" never leaves a report version behind.
///   2. The report is written.
///   3. With the flag, the report becomes the Mission's document. Without it, a report that already IS the session's
///      Mission's document refreshes the goal and why all the same (review finding 4, owner ruling).
/// </summary>
internal static class DevReportPublish
{
    public static DevReportPublishOutcome Run(DevReportStore store, MissionDocumentLinker missionDocuments,
        DevReportAuthorAnswer author, TenantId tenant, string sessionId, string key, string html, string status,
        string title, DateTime nowUtc, DevReportFactorySeatIdentity? factorySeat, bool asMissionDocument)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(missionDocuments);
        FileLog.Write($"[DevReportPublish] Run: sid={sessionId} missionDocument={asMissionDocument}");

        Mission? target = null;
        if (asMissionDocument)
        {
            var resolved = missionDocuments.Resolve(tenant, sessionId);
            if (resolved.Refused)
                return new DevReportPublishOutcome(null, false, null, resolved.RefusalCode, resolved.Refusal, null);
            target = resolved.Mission;
        }

        var (report, created) = DevReportAuthor.PublishAs(store, author, tenant, sessionId, key, html, status, title,
            nowUtc, factorySeat);
        var at = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), TimeSpan.Zero);

        if (target is null)
        {
            var refreshed = missionDocuments.RefreshIfDocument(tenant, sessionId, report.Id, html, at);
            return new DevReportPublishOutcome(report, created, refreshed, null, null, null);
        }

        var link = missionDocuments.Link(tenant, target, report.Id, html, at);
        if (link is null)
        {
            FileLog.Write($"[DevReportPublish] Run: report {report.Id} written, mission {target.MissionId} gone before the link");
            return new DevReportPublishOutcome(report, created, null, null, null, target.MissionId);
        }
        return new DevReportPublishOutcome(report, created, link, null, null, null);
    }
}
