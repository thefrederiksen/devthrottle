using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A BOSS STARTS A SEAT OF ITS OWN FACTORY (the owner, 1 November 2026: "The boss's main job is to delegate and make sure
/// the right resources are in place to get things done."). The rule and the first prompt, pure, so every line of the rule
/// is pinned by a test of this class alone. The design is <c>docs/factories/boss-delegation-design.md</c>.
///
/// A delegation adds no authority: it starts exactly what the owner's published definition already says. The Boss names a
/// seat, a name and a job, and never an agent, a model, a machine, a kit, a grant or a version.
/// </summary>
public static class FactorySeatDelegation
{
    /// <summary>The refusal for a caller that is not a Boss started as a Boss, or null when it is. The caller's recorded
    /// seat is read from its history row, never from a request. A Boss's child carries the Boss seat with a rules-only
    /// kit and is refused here: that is what stops authority leaking down a team.</summary>
    public static string? RefuseCaller(SessionFactoryLookup caller, PublishedFactoryDefinition? published)
    {
        if (!caller.IsInAFactory) return "this session is in no factory, so it has no seat to start";
        if (published is null)
            return $"'{caller.Factory}' has no published definition, so it has no seat that can be started";
        var own = caller.HasASeat ? published.Seat(caller.Seat) : null;
        if (own is null || !FactorySeatStart.IsABoss(published.Definition, own))
            return "only the Boss of a factory may start another seat of it, and this session is not the Boss. " +
                   "Finish your work and hand it back; the Boss decides who does the next piece.";
        if (caller.SeatIsInherited)
            return "this session has the Boss's rules but was not started as the Boss, so it may not start a seat. " +
                   "Only a Boss started by its schedule, its trigger, the owner's Talk button or a session in no factory may.";
        return null;
    }

    /// <summary>The refusal for a seat a Boss may not start, or null when it may. A Boss seat is refused: a Boss holds the
    /// right to publish, so a Boss that started a Boss would have made a second holder of it on its own word.</summary>
    public static string? RefuseTarget(PublishedFactoryDefinition published, string seat)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (published.Seat(seat) is not { } target) return FactorySeatStart.SeatNotInVersion(published, seat);
        return FactorySeatStart.IsABoss(published.Definition, target)
            ? $"the seat '{target.Seat}' is a Boss, and a Boss starts only the other seats of its factory. No session was started."
            : null;
    }

    /// <summary>The name of the session: the fleet's convention of factory, seat and what this session does. A Boss
    /// that passes a whole name ("WarmForward - Scout - find ten") already wrote the factory and the seat, so that
    /// prefix is not added a second time; the name only ever carries it once.</summary>
    public static string SessionName(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat, string name)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var prefix = $"{factory.Title} - {seat.Name} - ";
        var what = name.Trim();
        while (what.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && what[prefix.Length..].Trim() is { Length: > 0 } rest)
            what = rest;
        return prefix + what;
    }

    /// <summary>
    /// What tells the seat who it answers to and how the work gets back. A seat's brief was written for a schedule, so it
    /// may say to report a run or close the session; this is the paragraph that says neither applies here.
    /// </summary>
    public const string StartedByTheBoss =
        "THE BOSS STARTED THIS SESSION. The job below is your whole task, and the boss owns this session: the owner is not "
        + "here and is not reading it. Do the job as your brief and grants allow. Nothing here reports a scheduled run, so do "
        + "not run cc-devthrottle run result. When the job is finished, run cc-devthrottle session handback \"<what you did, "
        + "and what the boss needs to know>\": that is how the work comes back, so never end without it. Then run "
        + "cc-devthrottle session done, so a finished session is not left waiting. If you are stuck on a decision, run cc-devthrottle session raise \"<the decision>\" and end "
        + "your turn.";

    /// <summary>The first prompt: who the seat is, its brief, where its kit is, the paragraph above, and the Boss's job.
    /// The job is last and given whole, so the Boss's words reach the seat unchanged.</summary>
    public static string FirstPrompt(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat, FactoryGivenBrief brief, string job)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(job);
        const string nl = "\n";
        var sb = new StringBuilder();
        sb.Append(FactorySeatOpening.WhoYouAre(factory, seat)).Append(nl).Append(nl);
        sb.Append(FactorySeatOpening.BriefBlock(brief, nl)).Append(nl).Append(nl);
        sb.Append(FactorySeatOpening.KitSentence).Append(nl).Append(nl);
        sb.Append(StartedByTheBoss).Append(nl).Append(nl);
        sb.Append("--- the job from the boss ---").Append(nl);
        sb.Append(job.Trim()).Append(nl);
        sb.Append("--- end of the job from the boss ---");
        return sb.ToString();
    }

    /// <summary>The row the factory's activity record keeps for one delegation: which Boss session started which seat, in
    /// which session. Written by the Gateway, so a Boss cannot start a seat and leave it out.</summary>
    public static AppendFactoryActivityRequest ActivityRow(string factory, string bossSeat, string bossSession, string seat,
        int version, string startedSession, string sessionName)
    {
        var what = $"The boss session {bossSession} started the seat {seat} in session {startedSession}: {sessionName}";
        if (what.Length > FactoryActivityRecord.MaxWhatChars) what = what[..FactoryActivityRecord.MaxWhatChars];
        FileLog.Write($"[FactorySeatDelegation] ActivityRow: factory={factory}, seat={seat}, session={startedSession}");
        return new AppendFactoryActivityRequest
        {
            Factory = factory,
            FactoryAgent = bossSeat,
            FactoryAgentVersion = "definition v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SessionId = startedSession,
            What = what,
            Outcome = FactoryActivityOutcome.Started,
            Subject = seat,
        };
    }
}
