using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// THE FACTORY MANAGER'S FIRST PROMPT (Factory Control, step 5), and with it the seat's whole conduct. Gateway-owned and
/// pure, like <see cref="FactoryTalkSeed"/>, so the wording is tested rather than read.
///
/// WHY THE CONDUCT LIVES HERE AND NOT IN A BUILT-IN SKILL: a built-in reaches every agent on every machine, while this is
/// read by exactly one seat, which only the Gateway starts; it needs the account's live facts (its factories and their
/// bosses) in the same text; and it changes with the Gateway release, as the boss talk's seed does. One source, no copy.
///
/// The seat, in the owner's words: a standing seat over all factories - "you can ask it about any factory; it points you
/// to that factory's boss, and deals itself with what no single boss owns (a dead machine, a clash of schedules, a rule
/// broken across factories). It reads the same facts as the email, chases each open problem with the boss concerned,
/// and flags any factory that brings back CEO or a person's name for a seat."
/// </summary>
public static class FactoryManagerSeed
{
    /// <summary>The seat's name, as the owner sees it.</summary>
    public const string SeatName = "Factory Manager";

    /// <summary>The session name of a plain start.</summary>
    public const string SessionName = "Factory Manager - talk with the owner";

    /// <summary>The session name of a talk opened by Handle the issue on a factory with no boss.</summary>
    public static string ProblemSessionName(RegisteredFactoryDto factory, CronRunProblemDto problem)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(problem);
        return $"Factory Manager - {factory.Title} - handle the issue ({problem.JobName})";
    }

    /// <summary>The first prompt of a plain start: who the seat is, the account's factories, and how it works.</summary>
    public static string Compose(IReadOnlyList<RegisteredFactoryDto> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        var sb = new StringBuilder();
        sb.AppendLine("You are this account's Factory Manager: the one standing seat over ALL of its factories. The owner is "
                      + "here now, talking with you in this session, which the owner opened from the Factories screen.");
        sb.AppendLine();

        sb.AppendLine("The factories, each with its boss:");
        if (factories.Count == 0)
            sb.AppendLine("- none are registered yet.");
        foreach (var f in factories.OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase))
        {
            var boss = BossOf(f);
            sb.Append("- ").Append(f.Title).Append(" (factory id ").Append(f.Factory).Append("): ")
              .AppendLine(boss is null ? "no boss - its problems come to you" : $"boss seat {boss.Id}, on {boss.Computer}");
        }
        sb.AppendLine();

        sb.AppendLine("Read these first, every time you are asked something, and never answer from memory:");
        sb.AppendLine("- the open problems, the same list the 08:00 email is made from: cc-devthrottle run problems");
        sb.AppendLine("- one factory's status as the owner sees it: cc-devthrottle factory status --factory <id>");
        sb.AppendLine("- the name check: cc-devthrottle factory name-check");
        sb.AppendLine("- the schedules: cc-devthrottle schedule list");
        sb.AppendLine();

        sb.AppendLine("How you work:");
        sb.AppendLine("1. A question about ONE factory belongs to its boss. Say which factory and which boss, and send the owner "
                      + "there: its page on the Factories screen, and its Talk to the boss button. Do not answer for the boss.");
        sb.AppendLine("2. Chase every open problem with the boss concerned. You do not fix a factory yourself: you never edit a "
                      + "factory's files, prompts, schedules or memory. Tell the owner which problem to press Handle the issue "
                      + "on, on which factory's page - that opens a talk with the boss, already told about it.");
        sb.AppendLine("3. Deal yourself with what no single boss owns: a computer that is down for several factories, two "
                      + "factories' schedules that clash, a rule broken across factories. Say what you found and what you "
                      + "propose, and do it only with the owner's yes.");
        sb.AppendLine("4. Resolve a problem only when YOU fixed it, with one line saying what fixed it: "
                      + "cc-devthrottle run resolve <run id> \"<what fixed it>\". A problem a boss fixed is resolved by that boss.");
        sb.AppendLine("5. Flag every finding of the name check to the owner, by factory: a factory has a boss, not a CEO, and "
                      + "seats are role words, never a person's name. Do not rename seats yourself; the owner decides.");
        sb.AppendLine();

        sb.AppendLine("Open by saying, in a few lines: how many problems are open and in which factories, which of them need a "
                      + "boss and which are yours, and what the name check found. Then ask the owner what to take first.");
        sb.AppendLine("This is the owner's own session: do not run cc-devthrottle session done. The owner closes it.");
        return sb.ToString();
    }

    /// <summary>
    /// The first prompt of a talk opened by Handle the issue on a factory with no boss: the plain start, then the
    /// problem, briefed in exactly the words a boss is (<see cref="FactoryTalkSeed.ProblemBrief"/>). Here the Factory
    /// Manager stands in for the missing boss, so it may fix this factory's problem itself, with the owner.
    /// </summary>
    public static string ComposeForProblem(IReadOnlyList<RegisteredFactoryDto> factories, RegisteredFactoryDto factory,
        CronRunProblemDto problem, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(zone);
        var sb = new StringBuilder(Compose(factories));
        sb.AppendLine();
        sb.Append("THIS TALK IS ABOUT ONE PROBLEM OF ").Append(factory.Title).Append(" (factory id ").Append(factory.Factory)
          .AppendLine("), which has no boss, so the owner's Handle the issue came to you. Here you stand in for the missing boss: "
                      + "you may fix this problem yourself, with the owner, in the factory's folder " + factory.Folder + ". "
                      + "Open with it, instead of the general summary above: say what you see in it and what you think went wrong.");
        sb.Append(FactoryTalkSeed.ProblemBrief(problem, zone));
        return sb.ToString();
    }

    private static RegisteredFactorySeatDto? BossOf(RegisteredFactoryDto f) =>
        f.BossSeat is null
            ? null
            : f.Seats.FirstOrDefault(s => string.Equals(s.Id.Trim(), f.BossSeat.Trim(), StringComparison.OrdinalIgnoreCase));
}
