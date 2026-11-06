using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Missions;

/// <summary>
/// THE MISSION BUTTON'S ONE FOLD (the Missions First-Class mission). Every client - the Director's tab bar and its
/// corner button first - renders what this decides, verbatim (repository rule 7). Three verdicts, from the session's
/// Mission attachment and the Gateway's mission store:
///
///   a Mission with a document  -> the Mission's name, colour "mission", tooltip = name and goal, address /r/&lt;id&gt;
///   a Mission, no document yet -> the Mission's name, colour "mission-outline", tooltip says there is no document
///   no Mission                 -> "No mission", colour "warning", tooltip says the session has no Mission
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
    /// <summary>The words on the button of a session attached to no Mission.</summary>
    public const string NoMissionLabel = "No mission";

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
                Label = NoMissionLabel,
                ColorKey = MissionButtonDisplay.ColorWarning,
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
                Label = name,
                ColorKey = MissionButtonDisplay.ColorMission,
                Tooltip = goal.Length > 0
                    ? $"Mission: {name}\nGoal: {goal}"
                    : $"Mission: {name}\nGoal: not set - the mission document has no goal section.",
                // The one source of the address is the route that serves it, so the two cannot drift apart.
                DocumentAddress = $"{Api.DevReportLinkRoute.Prefix}{reportId:D}",
            };
        }

        return new MissionButtonDisplay
        {
            Verdict = MissionButtonDisplay.VerdictMissionWithoutDocument,
            Label = name,
            ColorKey = MissionButtonDisplay.ColorMissionOutline,
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
