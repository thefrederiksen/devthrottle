using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Missions;

/// <summary>
/// THE MISSION BUTTON'S ONE FOLD (the Missions First-Class mission). Every client - the Director's tab bar and its
/// corner button first - renders what this decides, verbatim (repository rule 7). Three verdicts, from the session's
/// Mission attachment and the Gateway's mission store:
///
///   a Mission with a document  -> "Mission", colour "mission", icon "document", tooltip = name and goal, address /r/&lt;id&gt;
///   a Mission, no document yet -> "Mission", colour "mission-outline", icon "none", tooltip = name, says no document
///   no Mission                 -> "Mission", colour "warning", icon "warning", tooltip says how to attach one
///
/// THE BUTTON ALWAYS SAYS "Mission" (the owner, 7 November 2026). It used to print the Mission's name, which made it
/// wide and cut off in every header; the name and goal now live only in the tooltip, and the icon and colour tell the
/// three states apart.
///
/// THE MISSION RECORD WINS OVER THE SESSION'S CACHED NAME. A session carries the name it was attached under, and a
/// rename since then is only on the record, so the record's name is shown whenever the record is found. A session
/// whose Mission is not in this account's store (a record written elsewhere, or one never synced) is still attached to
/// a Mission - it reads "no document yet" under the name the session carries, because there is no document this
/// Gateway can open for it, and a Mission it cannot name at all is named by its id rather than shown blank.
///
/// It reads no clock and no I/O: given the same session and missions it answers the same, so the display push's change
/// gate sees an unchanged verdict as unchanged.
/// </summary>
internal static class MissionButtonFold
{
    /// <summary>The words on the button, in every state.</summary>
    public const string ButtonLabel = "Mission";

    /// <summary>The last line of the tooltip of a Mission with a document: what a click does.</summary>
    public const string OpenDocumentHint = "Click to open the mission document.";

    /// <summary>The tooltip of a session attached to no Mission.</summary>
    public const string NoMissionTooltip = "This session has no Mission. Attach it to one with: cc-devthrottle mission attach <session> <mission>";

    /// <summary>The verdict for one session, given the account's missions keyed by id.</summary>
    public static MissionButtonDisplay For(SessionDto session, IReadOnlyDictionary<Guid, Mission> missions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(missions);

        if (session.MissionId is not { } missionId || missionId == Guid.Empty)
        {
            return new MissionButtonDisplay
            {
                Verdict = MissionButtonDisplay.VerdictNoMission,
                Label = ButtonLabel,
                ColorKey = MissionButtonDisplay.ColorWarning,
                IconKey = MissionButtonDisplay.IconWarning,
                Tooltip = NoMissionTooltip,
                DocumentAddress = null,
            };
        }

        missions.TryGetValue(missionId, out var mission);
        var name = NameOf(mission, session, missionId);

        if (mission?.DocumentReportId is { } reportId && reportId != Guid.Empty)
        {
            var goal = (mission.Goal ?? "").Trim();
            return new MissionButtonDisplay
            {
                Verdict = MissionButtonDisplay.VerdictMissionWithDocument,
                Label = ButtonLabel,
                ColorKey = MissionButtonDisplay.ColorMission,
                IconKey = MissionButtonDisplay.IconDocument,
                Tooltip = goal.Length > 0
                    ? $"Mission: {name}\nGoal: {goal}\n{OpenDocumentHint}"
                    : $"Mission: {name}\nGoal: not set - the mission document has no goal section.\n{OpenDocumentHint}",
                // The one source of the address is the route that serves it, so the two cannot drift apart.
                DocumentAddress = $"{Api.DevReportLinkRoute.Prefix}{reportId:D}",
            };
        }

        return new MissionButtonDisplay
        {
            Verdict = MissionButtonDisplay.VerdictMissionWithoutDocument,
            Label = ButtonLabel,
            ColorKey = MissionButtonDisplay.ColorMissionOutline,
            IconKey = MissionButtonDisplay.IconNone,
            Tooltip = $"Mission: {name}\nThis Mission has no document yet.",
            DocumentAddress = null,
        };
    }

    /// <summary>
    /// Stamp the verdict on every session. <paramref name="missions"/> null means no missions were read, and then every
    /// row is ASSIGNED null - a Director's echo never survives, and nothing is claimed that was not folded.
    /// </summary>
    public static void Stamp(IEnumerable<SessionDto> sessions, IReadOnlyList<Mission>? missions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (missions is null)
        {
            foreach (var s in sessions) s.MissionButton = null;
            return;
        }

        var byId = new Dictionary<Guid, Mission>();
        foreach (var m in missions)
            byId[m.MissionId] = m;
        foreach (var s in sessions)
            s.MissionButton = For(s, byId);
    }

    private static string NameOf(Mission? mission, SessionDto session, Guid missionId)
    {
        if (mission is not null && !string.IsNullOrWhiteSpace(mission.MissionName))
            return mission.MissionName.Trim();
        if (!string.IsNullOrWhiteSpace(session.MissionName))
            return session.MissionName.Trim();
        return $"Mission {missionId:D}";
    }
}
