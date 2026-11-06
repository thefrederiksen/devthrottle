using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.DevReports;

/// <summary>The Mission a session may link its report to, or the refusal that stops it. Exactly one is set.</summary>
internal sealed record MissionDocumentTarget(Mission? Mission, string? RefusalCode, string? Refusal)
{
    public bool Refused => Mission is null;
}

/// <summary>What linking did, in the words the publisher is told.</summary>
internal sealed record MissionDocumentLinkResult(
    Guid MissionId,
    string MissionName,
    string Goal,
    string Why,
    bool GoalFound,
    bool WhyFound,
    Guid? ReplacedReportId,
    IReadOnlyList<string> Notes);

/// <summary>
/// Publishing a dev report AS A MISSION DOCUMENT (the Missions First-Class mission):
/// <c>cc-dev-reports open &lt;file&gt; --mission-document</c>.
///
/// THE PERMISSION RULE IS STRUCTURAL. A session links a report to ITS OWN Mission and nothing else: the Mission is
/// the one the Gateway's own records say the publishing session is attached to, and the request names no Mission at
/// all, so there is nothing a caller could put in it to reach another one. The Mission is looked up inside the
/// caller's tenant, so another account's Mission is not found.
///
/// NO MISSION IS A REFUSAL, NEVER A NO-OP. A session attached to no Mission that asks for a mission document is told
/// so, and NOTHING is published - <see cref="Resolve"/> is asked before the report is written, so a refused publish
/// leaves no half-done state behind (a new report version with no link).
///
/// ONE DOCUMENT PER MISSION: <see cref="Link"/> replaces whatever document the Mission held. A republish of the same
/// report is the same report id, so it keeps the same link; a different report takes the link over and the publisher
/// is told which report it replaced.
/// </summary>
internal sealed class MissionDocumentLinker
{
    private readonly MissionStore _missions;
    private readonly Func<TenantId, string, Guid?> _missionOf;

    /// <param name="missions">The Gateway's mission store.</param>
    /// <param name="missionOf">(tenant, session id) -> the Mission that session is attached to, from the Gateway's own
    /// records, or null when it is attached to none.</param>
    public MissionDocumentLinker(MissionStore missions, Func<TenantId, string, Guid?> missionOf)
    {
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _missionOf = missionOf ?? throw new ArgumentNullException(nameof(missionOf));
    }

    /// <summary>The Mission <paramref name="sessionId"/> may link a report to, or why it may not. Writes nothing.</summary>
    public MissionDocumentTarget Resolve(TenantId tenant, string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var missionId = _missionOf(tenant, sessionId);
        if (missionId is not { } id || id == Guid.Empty)
        {
            FileLog.Write($"[MissionDocumentLinker] Resolve REFUSED sid={sessionId}: the session has no Mission");
            return new MissionDocumentTarget(null, "no_mission",
                "This session is attached to no Mission, so its report cannot be a mission document. Nothing was " +
                "published. Attach this session to its Mission first (cc-devthrottle mission attach <session> <mission>), " +
                "or publish without --mission-document. If you attached it a moment ago, the attachment may not have " +
                "reached the Gateway yet: wait a few seconds and publish again.");
        }

        var mission = _missions.Get(tenant, id);
        if (mission is null)
        {
            FileLog.Write($"[MissionDocumentLinker] Resolve REFUSED sid={sessionId}: mission {id} is not on this Gateway");
            return new MissionDocumentTarget(null, "mission_unknown",
                $"This session is attached to Mission {id}, which this Gateway does not hold for this account, so the " +
                "report cannot be its document. Nothing was published. List the missions with: cc-devthrottle mission list");
        }

        FileLog.Write($"[MissionDocumentLinker] Resolve: sid={sessionId} mission={mission.MissionId}");
        return new MissionDocumentTarget(mission, null, null);
    }

    /// <summary>
    /// A republish WITHOUT the flag of the report that already IS the document of the publishing session's own Mission
    /// (owner ruling, review finding 4). The document is the one source of the goal and the why, so a new version of it
    /// refreshes them exactly as a publish with the flag does - otherwise the button's tooltip would state one goal while
    /// the document one click away states another. No new permission is involved: the Mission is the session's own,
    /// and the report is one this session published. Returns null, changing nothing, for any other report.
    /// </summary>
    public MissionDocumentLinkResult? RefreshIfDocument(TenantId tenant, string sessionId, Guid reportId, string html,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (_missionOf(tenant, sessionId) is not { } missionId || missionId == Guid.Empty)
            return null;
        var mission = _missions.Get(tenant, missionId);
        if (mission?.DocumentReportId != reportId)
            return null;
        FileLog.Write($"[MissionDocumentLinker] RefreshIfDocument: report={reportId} is mission {missionId}'s document; refreshing");
        return Link(tenant, mission, reportId, html, nowUtc);
    }

    /// <summary>
    /// Make report <paramref name="reportId"/> the document of <paramref name="mission"/>, and stamp the why and goal
    /// read from <paramref name="html"/>. Returns null when the Mission was removed from the store between
    /// <see cref="Resolve"/> and this call.
    /// </summary>
    public MissionDocumentLinkResult? Link(TenantId tenant, Mission mission, Guid reportId, string html, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(html);
        var sections = MissionDocumentSections.Read(html);
        var outcome = _missions.SetDocument(tenant, mission.MissionId, reportId, sections.Goal, sections.Why, nowUtc);
        if (outcome is null)
        {
            FileLog.Write($"[MissionDocumentLinker] Link FAILED: mission {mission.MissionId} vanished before the link");
            return null;
        }

        var notes = new List<string>();
        if (sections.Goal is null)
            notes.Add("The report has no goal section (data-dev-report=\"goal\"), so the Mission has no goal. Add one " +
                      "and publish again.");
        else if (sections.Goal.Length == 0)
            notes.Add("The report's goal section has no words in it, so the Mission has no goal.");
        if (sections.Why is null)
            notes.Add("The report has no why section (data-dev-report=\"why\"), so the Mission's why was left as it was.");
        if (outcome.ReplacedReportId is { } replaced)
            notes.Add($"This report replaces report {replaced:D} as the Mission's document.");

        var linked = outcome.Mission;
        FileLog.Write($"[MissionDocumentLinker] Link: mission={linked.MissionId} report={reportId} " +
                      $"replaced={(outcome.ReplacedReportId?.ToString("D") ?? "(none)")} notes={notes.Count}");
        return new MissionDocumentLinkResult(linked.MissionId, linked.MissionName, linked.Goal, linked.Why,
            GoalFound: sections.Goal is not null, WhyFound: sections.Why is not null, outcome.ReplacedReportId, notes);
    }
}
