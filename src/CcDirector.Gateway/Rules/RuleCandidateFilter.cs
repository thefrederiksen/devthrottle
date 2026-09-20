namespace CcDirector.Gateway.Rules;

/// <summary>
/// What a rule may decide, as stored on a firing. A closed set, because the record is read by people and by
/// the free checks: the cooldown and the daily cap count <see cref="Act"/> and nothing else.
/// </summary>
public static class RuleDecisions
{
    /// <summary>The instruction applied and the agent composed something to type. In dry run nothing is
    /// typed, but the decision is still an act and still counts against the ceiling.</summary>
    public const string Act = "act";

    /// <summary>The agent read the screen against the instruction and decided the instruction does not
    /// reach it. A first-class outcome, always written down.</summary>
    public const string Decline = "decline";

    /// <summary>The act was given up before the keystroke - the screen moved on, or a check the agent
    /// staked its decision on did not hold.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The reply named something it was not offered, or was not an answer at all. Never read as
    /// permission, and never swallowed.</summary>
    public const string Refused = "refused";
}

/// <summary>The facts about one session a rule's scope is matched against, read from the pushed roster
/// snapshot rather than by dialing the session.</summary>
public sealed record RuleSessionFacts(
    string SessionId,
    string Agent,
    string RepositoryPath,
    string Machine,
    string Mission,
    string ActivityState);

/// <summary>One rule that was considered and passed over, with the reason it was passed over.</summary>
public sealed record RuleSkipped(Guid RuleId, string Reason);

/// <summary>
/// The answer of the free checks: the rules worth paying a model call for, the ones that were passed over
/// with their reasons, and - when the pass stopped before any rule was considered at all - why it stopped.
/// </summary>
public sealed record RuleCandidates(
    IReadOnlyList<SessionRule> Chosen,
    IReadOnlyList<RuleSkipped> Skipped,
    string? StoppedBecause);

/// <summary>
/// THE FREE CHECKS. Cheap pure code that runs on every idle transition and decides whether any rule could
/// possibly apply, so the common case - a session that finished a turn normally - costs one screen read and
/// nothing else. No model is reached from here.
///
/// Everything it turns down it turns down OUT LOUD. A filter that answered with an empty list would read
/// exactly the same whether it had considered every rule and rejected each one or thrown on the first, so
/// each rule leaves either as a candidate or as a <see cref="RuleSkipped"/> carrying its reason.
/// </summary>
public static class RuleCandidateFilter
{
    /// <summary>The activity state a session reports while it is running a turn.</summary>
    public const string WorkingState = "Working";

    /// <summary>Stated when a session is still working, which is before any rule is looked at.</summary>
    public const string SessionIsWorking =
        "the session is working, so no rule is evaluated - a rule only ever looks at an idle session";

    /// <summary>Stated when the screen is the same one already evaluated for this session.</summary>
    public const string ScreenUnchanged =
        "the screen has not changed since this session was last looked at";

    /// <summary>Stated when there is nothing on the screen to read.</summary>
    public const string ScreenIsEmpty =
        "there is nothing on the screen to read, and an empty screen is not evidence";

    /// <summary>
    /// Choose the rules worth a model call for this screen. The session-level checks come first, because a
    /// working session or an unchanged screen makes every per-rule question moot; then each rule is asked
    /// about in the order that gets rid of it soonest.
    /// </summary>
    /// <exception cref="ArgumentNullException">The facts or the firing reader are missing.</exception>
    public static RuleCandidates Choose(
        IReadOnlyList<SessionRule> rules,
        RuleSessionFacts facts,
        string screenText,
        string? previousScreenText,
        Func<Guid, IReadOnlyList<SessionRuleFiring>> firingsFor,
        DateTime nowUtc)
    {
        if (facts is null) throw new ArgumentNullException(nameof(facts));
        if (firingsFor is null) throw new ArgumentNullException(nameof(firingsFor));

        var none = Array.Empty<SessionRule>();
        var noSkips = Array.Empty<RuleSkipped>();

        if (string.Equals(facts.ActivityState, WorkingState, StringComparison.OrdinalIgnoreCase))
            return new RuleCandidates(none, noSkips, SessionIsWorking);

        if (string.IsNullOrWhiteSpace(screenText))
            return new RuleCandidates(none, noSkips, ScreenIsEmpty);

        // An unseen screen is CHANGED, not unchanged: a Gateway that has just started has never looked at
        // this session, and a session parked on a notice since before it started is exactly the case the
        // feature exists for.
        if (previousScreenText is not null && string.Equals(previousScreenText, screenText, StringComparison.Ordinal))
            return new RuleCandidates(none, noSkips, ScreenUnchanged);

        var chosen = new List<SessionRule>();
        var skipped = new List<RuleSkipped>();
        var today = DateOnly.FromDateTime(nowUtc.ToUniversalTime());

        foreach (var rule in rules ?? none)
        {
            var scopeProblem = OutOfScope(rule.Scope, facts);
            if (scopeProblem is not null)
            {
                skipped.Add(new RuleSkipped(rule.Id, scopeProblem));
                continue;
            }

            if (!RulePrimitives.MatchesAny(screenText, rule.TriggerWords))
            {
                skipped.Add(new RuleSkipped(rule.Id,
                    "none of the words this rule watches for are on the screen: " +
                    string.Join(", ", rule.TriggerWords) + "."));
                continue;
            }

            // The ceiling counts ACTS on THIS session. A decline, an abandonment and a refusal all did
            // nothing, so none of them starts a cooldown or eats a day's allowance - a rule that declined a
            // second ago must still be free to look at the next screen.
            var acts = firingsFor(rule.Id)
                .Where(f => string.Equals(f.SessionId, facts.SessionId, StringComparison.Ordinal))
                .Where(f => string.Equals(f.Decision, RuleDecisions.Act, StringComparison.Ordinal))
                .ToList();

            var lastAct = acts.Count == 0 ? (DateTime?)null : acts.Max(f => f.OccurredUtc);
            if (lastAct is not null)
            {
                var since = (int)(nowUtc.ToUniversalTime() - lastAct.Value.ToUniversalTime()).TotalSeconds;
                if (since < rule.CooldownSeconds)
                {
                    skipped.Add(new RuleSkipped(rule.Id,
                        $"this rule acted on this session {since} seconds ago and waits " +
                        $"{rule.CooldownSeconds} seconds between acts on one session."));
                    continue;
                }
            }

            var actsToday = acts.Count(f => DateOnly.FromDateTime(f.OccurredUtc.ToUniversalTime()) == today);
            if (actsToday >= rule.DailyCap)
            {
                skipped.Add(new RuleSkipped(rule.Id,
                    $"this rule has already acted on this session {actsToday} times today and its daily " +
                    $"cap is {rule.DailyCap}."));
                continue;
            }

            chosen.Add(rule);
        }

        return new RuleCandidates(chosen, skipped, null);
    }

    /// <summary>
    /// Why this rule's scope does not cover this session, or null when it does. Each part is a filter:
    /// nothing stored means "any".
    ///
    /// Internal rather than private because the evaluator asks it again immediately before the keystroke:
    /// the facts are re-read across the model call, and a scope answered once at the start of a pass is an
    /// answer about a moment that has passed. One implementation, asked twice - never a second copy.
    /// </summary>
    internal static string? WhyOutOfScope(RuleScope scope, RuleSessionFacts facts) => OutOfScope(scope, facts);

    /// <summary>Why this rule's scope does not cover this session, or null when it does. Each part is a
    /// filter: nothing stored means "any".</summary>
    private static string? OutOfScope(RuleScope scope, RuleSessionFacts facts)
    {
        var problem = PartOutOfScope("agent", scope.Agent, facts.Agent)
            ?? PartOutOfScope("machine", scope.Machine, facts.Machine)
            ?? PartOutOfScope("mission", scope.Mission, facts.Mission);
        if (problem is not null) return problem;

        if (string.IsNullOrWhiteSpace(scope.Repository)) return null;
        return PathsAreTheSamePlace(scope.Repository, facts.RepositoryPath)
            ? null
            : $"this rule only watches sessions in '{scope.Repository}', and this session is in " +
              $"'{Show(facts.RepositoryPath)}'.";
    }

    private static string? PartOutOfScope(string what, string? wanted, string actual)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return null;
        return $"this rule only watches sessions whose {what} is '{wanted}', and this session's {what} is " +
               $"'{Show(actual)}'.";
    }

    /// <summary>
    /// Two written paths naming the same place, compared the way THE MACHINE THE PATH CAME FROM does -
    /// never the way the machine running this code does.
    ///
    /// THE GATEWAY IS NOT THE MACHINE THE PATH DESCRIBES, and that is the whole reason this is not one
    /// line. The Gateway runs in a Linux container and is handed repository paths pushed up from every
    /// Director in the fleet, Windows and macOS alike. Deciding case sensitivity from
    /// <c>OperatingSystem.IsWindows()</c> therefore asked the wrong machine: a rule scoped to
    /// <c>D:\ReposFred\scratch</c> matched a session reported as <c>d:\reposfred\scratch</c> while the code
    /// ran on a Windows desktop and stopped matching it the moment the same code ran on the hosted Gateway.
    /// A scope that does not match is a rule that silently never fires, which is the worst shape a defect
    /// can take: nothing fails, something merely stops happening.
    ///
    /// So the path's own shape decides. A Windows path - a drive letter, or a share - is compared without
    /// regard to case and with its separators unified, because that is what Windows does with it. Anything
    /// else is a POSIX path and is compared exactly, because that is what Linux does with it, and two names
    /// there that differ in case really are two different directories.
    /// </summary>
    private static bool PathsAreTheSamePlace(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(right)) return false;

        var writtenLeft = left.Trim();
        var writtenRight = right.Trim();

        if (IsAWindowsPath(writtenLeft) || IsAWindowsPath(writtenRight))
            return string.Equals(AsWindows(writtenLeft), AsWindows(writtenRight), StringComparison.OrdinalIgnoreCase);

        return string.Equals(AsPosix(writtenLeft), AsPosix(writtenRight), StringComparison.Ordinal);
    }

    /// <summary>A drive-letter path (<c>D:\repos</c>) or a share (<c>\\server\share</c>). These are the two
    /// shapes only Windows writes, and they are matched the way Windows matches them wherever this code
    /// happens to be running.</summary>
    private static bool IsAWindowsPath(string written) =>
        (written.Length >= 2 && written[1] == ':' && char.IsAsciiLetter(written[0]))
        || written.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Separators unified to a backslash and a trailing one dropped, so one Windows place written
    /// <c>D:/repos/x</c>, <c>D:\repos\x</c> and <c>D:\repos\x\</c> is one string.</summary>
    private static string AsWindows(string written) => written.Replace('/', '\\').TrimEnd('\\');

    /// <summary>A trailing separator dropped, and nothing else. A backslash is a legal character in a POSIX
    /// file name, so it is left exactly as written rather than treated as a separator.</summary>
    private static string AsPosix(string written) => written.Length > 1 ? written.TrimEnd('/') : written;

    /// <summary>An empty fact reads as nothing at all rather than as an empty pair of quotes.</summary>
    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "not set" : value;
}
