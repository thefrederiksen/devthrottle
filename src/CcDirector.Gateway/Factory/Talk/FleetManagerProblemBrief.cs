using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// WHAT THE FLEET MANAGER IS TOLD WHEN THE OWNER PRESSES HANDLE THE ISSUE ON A FACTORY WITH NO BOSS. Gateway-owned and
/// pure, like <see cref="FactoryTalkSeed"/>, so the wording is tested rather than read. The problem itself is briefed
/// in exactly the words a boss is (<see cref="FactoryTalkSeed.ProblemBrief"/>). The Fleet Manager stands in for the
/// missing boss, and its first rule still binds it: it never does the work itself, so the fix goes to a session it
/// starts in the factory's folder, and it resolves the problem once that session has fixed it.
/// </summary>
public static class FleetManagerProblemBrief
{
    /// <summary>The words carried on the <c>problem</c> event.</summary>
    public static string Compose(RegisteredFactoryDto factory, CronRunProblemDto problem, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(zone);
        var sb = new StringBuilder();
        sb.Append(factory.Title).Append(" (factory id ").Append(factory.Factory)
          .AppendLine(") has no boss, so the owner's Handle the issue came to you. You stand in for the missing boss on this "
                      + "one problem: say to the owner what you see in it and what you think went wrong, then start a session "
                      + "of your own in the factory's folder " + factory.Folder + " to fix it.");
        sb.Append(FactoryTalkSeed.ProblemBrief(problem, zone));
        return sb.ToString();
    }
}
