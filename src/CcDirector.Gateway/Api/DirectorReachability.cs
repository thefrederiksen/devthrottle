using System.Collections.Concurrent;

namespace CcDirector.Gateway.Api;

/// <summary>How far a Director is from being able to RECEIVE a command. Three states, three different fixes.</summary>
internal enum DirectorReach
{
    /// <summary>No stream: nothing has dialled in, so there is nothing to send down. Start a Director.</summary>
    NoStream,

    /// <summary>A live stream, and it has answered something down it. Commands get through.</summary>
    Reachable,

    /// <summary>
    /// A live stream, reports arriving, and NOTHING sent down it has been answered. This is the state that
    /// had no name on 26 October 2026 (issue #4490): devthrottle-mac-mini held its connection open and
    /// reported all night while every command sent to it vanished, and the Gateway went on calling it
    /// healthy because health was judged on the reporting side alone.
    /// </summary>
    ReportingButNotReachable,
}

/// <summary>
/// What the Gateway KNOWS about a Director's downlink, from the sends it has already made.
///
/// WHY THIS IS KEPT AT ALL. The Gateway produced this evidence and threw it away. Every command it sends
/// either comes back with an answer or expires, and the pattern of those two facts is the only thing that
/// can tell a slow verb from a deaf machine - a distinction nothing in the Gateway could make. On
/// 26 October the licence switcher sent the same command four times, waited sixty seconds each time, and
/// said "the Director did not answer" four times, which is true and says nothing: the Director had not
/// answered ANYTHING down that connection since 22:02 the night before.
///
/// Facts only - no probe, no extra traffic. A reading is a summary of sends that already happened.
/// </summary>
internal static class DirectorDownlinkFacts
{
    private static readonly ConcurrentDictionary<string, Facts> ByDirector = new(StringComparer.Ordinal);

    /// <summary>What the Gateway has seen of one Director's downlink.</summary>
    internal sealed record Facts(DateTime? LastAnsweredUtc, DateTime? LastSilentUtc, int SilencesSinceAnAnswer);

    /// <summary>Nothing has been sent to this Director yet.</summary>
    internal static readonly Facts Nothing = new(null, null, 0);

    /// <summary>A command came back - an answer, success or a typed failure. The downlink is carrying.</summary>
    internal static void Answered(string directorId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return;
        ByDirector[directorId] = new Facts(nowUtc, Read(directorId).LastSilentUtc, 0);
    }

    /// <summary>A command expired with no answer at all.</summary>
    internal static void DidNotAnswer(string directorId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return;
        var before = Read(directorId);
        ByDirector[directorId] = before with
        {
            LastSilentUtc = nowUtc,
            SilencesSinceAnAnswer = before.SilencesSinceAnAnswer + 1,
        };
    }

    /// <summary>What is known about this Director's downlink, or <see cref="Nothing"/>.</summary>
    internal static Facts Read(string directorId) =>
        string.IsNullOrWhiteSpace(directorId) ? Nothing
        : ByDirector.TryGetValue(directorId, out var facts) ? facts
        : Nothing;

    /// <summary>Forget a Director - its stream closed, or a test is starting. Keeps the map from growing for ever.</summary>
    internal static void Forget(string directorId)
    {
        if (!string.IsNullOrWhiteSpace(directorId)) ByDirector.TryRemove(directorId, out _);
    }
}

/// <summary>
/// The ONE definition of how far a Director is from receiving a command, from the two facts the Gateway
/// holds: whether it holds a stream, and what came back the last times something was sent down it. The
/// Director twin of <see cref="LauncherReachability"/>, and it exists for the same reason: the rule was
/// nowhere, so "the Director did not answer" was the only sentence available for three different problems.
/// </summary>
internal static class DirectorReachability
{
    /// <summary>
    /// How many expiries with no answer in between make a silence rather than a slow verb. Two, not one: a
    /// single verb can legitimately outrun its wait - that is why the wait has an override for the
    /// language-model verbs - and calling one expiry a deaf machine would cry wolf on the first slow recap.
    /// Two in a row with nothing answered between them is not a slow verb any more.
    /// </summary>
    internal const int SilencesThatMeanDeaf = 2;

    /// <summary>Classify from the facts. <paramref name="streamConnected"/> is the Gateway's own connection registry.</summary>
    internal static DirectorReach Classify(bool streamConnected, DirectorDownlinkFacts.Facts facts)
    {
        if (!streamConnected) return DirectorReach.NoStream;
        return facts.SilencesSinceAnAnswer >= SilencesThatMeanDeaf
            ? DirectorReach.ReportingButNotReachable
            : DirectorReach.Reachable;
    }

    /// <summary>
    /// The sentence that goes on a failure when the facts say the machine is deaf rather than slow, or null
    /// when they do not. Written for whoever reads it on a phone: it says what was observed and when the
    /// downlink last worked, because that time is the one thing that tells the reader this is not their
    /// command's fault.
    /// </summary>
    internal static string? Describe(DirectorReach reach, DirectorDownlinkFacts.Facts facts)
    {
        if (reach != DirectorReach.ReportingButNotReachable) return null;

        var since = facts.LastAnsweredUtc is { } answered
            ? $"since {answered:HH:mm} UTC"
            : "at any point since this Gateway started";
        return $"This Director has not answered anything sent down its connection {since}, "
               + $"though the connection is open and its reports are arriving ({facts.SilencesSinceAnAnswer} commands "
               + "have expired with no answer). Commands are not reaching it; restarting it is what clears this.";
    }
}
