using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// The first prompt of a talk: the session the Factories screen's Talk button opens so the owner can talk to one
/// seat of a factory (Factories screen mission, phase C). Gateway-owned, like every word the product puts in front
/// of an agent, and pure so the wording is tested rather than read.
///
/// What it must achieve, in the owner's rulings: the agent is SEATED as that agent (its brief, the factory's goal,
/// memory and recent activity); it runs under THE SAME RULES AS ITS SCHEDULED RUNS, minus the steps that only make
/// sense when nobody is there; it opens by saying what happened and what it needs; and before the talk ends it
/// leaves a <c>talked</c> line in the activity record and what was decided in the factory's memory, so the factory's
/// page can say "Talked with you". A goal change is allowed here, because the owner is present to approve it.
/// </summary>
public static class FactoryTalkSeed
{
    /// <summary>The goal file a factory that names none is told to write, in its folder.</summary>
    public const string DefaultGoalFile = "GOAL.md";

    /// <summary>The session name a talk carries: <c>&lt;Factory title&gt; - &lt;Seat name&gt; - talk with the owner</c>,
    /// and for the boss <c>&lt;Factory title&gt; - Boss - talk with the owner</c>: the boss has no name of its own.</summary>
    public static string SessionName(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        return $"{factory.Title} - {(IsBoss(factory, seat) ? FactoriesScreenFold.BossRoleWord : seat.Name)} - talk with the owner";
    }

    /// <summary>Whether this seat is the factory's boss (the registry's <c>bossSeat</c>).</summary>
    public static bool IsBoss(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat) =>
        factory.BossSeat is not null && string.Equals(factory.BossSeat.Trim(), seat.Id.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <param name="factory">The registered factory.</param>
    /// <param name="seat">The seat the owner pressed Talk on; one of <paramref name="factory"/>'s seats.</param>
    /// <param name="scheduleId">The first schedule the registry names for the seat, or null when it names none.</param>
    /// <param name="scheduleSeed">That schedule's seed text, or null when the seat has no schedule or the schedule is
    /// not on the Gateway (any more).</param>
    public static string Compose(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat, string? scheduleId, string? scheduleSeed)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);

        var goalFile = string.IsNullOrWhiteSpace(factory.GoalFile) ? DefaultGoalFile : factory.GoalFile!.Trim();
        var goalPath = InFolder(factory.Folder, goalFile);
        var sb = new StringBuilder();

        // The boss is "the boss of <factory>", never a person's name (the owner's ruling of 8 October 2026). A seat
        // whose registered role is a distinct word (CFO) is still introduced as the boss, with that role beside it.
        if (IsBoss(factory, seat))
        {
            sb.Append("You are the boss of ").Append(factory.Title);
            if (!string.IsNullOrWhiteSpace(seat.Role) && !string.Equals(seat.Role.Trim(), FactoriesScreenFold.BossRoleWord, StringComparison.OrdinalIgnoreCase))
                sb.Append(" (its ").Append(seat.Role.Trim()).Append(')');
            sb.Append(" (factory id: ").Append(factory.Factory).Append("; your agent id: ").Append(seat.Id).AppendLine(").");
        }
        else
        {
            sb.Append("You are ").Append(seat.Name).Append(", ").Append(seat.Role).Append(" of ").Append(factory.Title)
              .Append(" (factory id: ").Append(factory.Factory).Append("; your agent id: ").Append(seat.Id).AppendLine(").");
        }
        sb.AppendLine();
        sb.AppendLine("The owner is here now and is talking with you. This is the owner's own session, opened from the "
                      + "Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a "
                      + "report from it - the owner is reading your answers as you write them.");
        sb.AppendLine();

        sb.AppendLine("Before you say anything, read:");
        sb.Append("- your brief: ").AppendLine(InFolder(factory.Folder, seat.BriefFile));
        sb.Append("- the factory's goal: ").AppendLine(string.IsNullOrWhiteSpace(factory.GoalText)
            ? $"{goalPath} (the registry holds no goal for this factory yet, so there may be none)"
            : goalPath);
        sb.AppendLine("- the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters");
        sb.Append("- the factory's recent activity: cc-devthrottle factory activity --factory ").Append(factory.Factory).AppendLine(" -n 30");
        sb.Append("- the factory's status as the owner sees it on the Factories screen: ").AppendLine(StatusCommand(factory));
        sb.AppendLine("  It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED or RUNNING) and every failing and "
                      + "waiting item with its row id. Say that word to the owner when you open.");
        sb.AppendLine();

        sb.AppendLine("You work under the same rules as your scheduled runs.");
        if (scheduleId is null)
        {
            sb.AppendLine("No schedule runs this seat yet, so your brief is the whole of your rules.");
        }
        else if (string.IsNullOrWhiteSpace(scheduleSeed))
        {
            sb.Append("The schedule that runs you (").Append(scheduleId)
              .AppendLine(") is not on the Gateway, so its rules could not be read here. Follow your brief, and tell the owner the schedule is missing.");
        }
        else
        {
            sb.Append("These are the rules of your scheduled runs, exactly as your schedule (").Append(scheduleId).AppendLine(") starts them:");
            sb.AppendLine("--- the rules of your scheduled runs ---");
            sb.AppendLine(scheduleSeed.Trim());
            sb.AppendLine("--- end of the rules of your scheduled runs ---");
        }
        sb.AppendLine("The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: "
                      + "do not rename this session to a dated name, do not send the morning email, and do not run "
                      + "cc-devthrottle session done. The owner closes this session.");
        sb.AppendLine();

        sb.AppendLine("Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest "
                      + "activity line with the outcome talked, if there is one) and what you need from the owner.");
        sb.AppendLine();

        sb.AppendLine("In a talk the owner and you may change anything about the factory, the goal included - the owner is here "
                      + "to approve it. If the goal changes:");
        sb.Append("- write the new goal into ").Append(goalPath)
          .AppendLine(", with today's date and this session's id (cc-devthrottle session whoami) beside it;");
        sb.Append("- register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, "
                  + "with goalFile ").Append(goalFile).AppendLine(" and goalApprovedOn set to today. If you do not have the manifest, "
                  + "write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, "
                  + "Factory registry and goal number).");
        sb.AppendLine();

        sb.AppendLine("Before the talk ends you MUST do all three of these:");
        sb.Append("1. Record one activity line: cc-devthrottle factory record --factory ").Append(factory.Factory)
          .Append(" --agent ").Append(seat.Id).AppendLine(" --outcome talked --what \"<one line of what was decided>\"");
        sb.AppendLine("2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> \"<what was decided>\"");
        sb.Append("3. Run ").Append(StatusCommand(factory)).AppendLine(" again. For every failing or waiting item that is "
                  + "now resolved, mark it handled with its evidence - and only if it is resolved:");
        sb.Append("   cc-devthrottle factory record --factory ").Append(factory.Factory)
          .AppendLine(" --agent <the item's seat> --outcome done --corrects <the item's row id> --what \"Handled: <the evidence>\"");
        sb.AppendLine("   Then tell the owner the word the factory ends on.");

        return sb.ToString();
    }

    /// <summary>What a factory's boss runs to read its own Factories screen (issue #3685).</summary>
    public static string StatusCommand(RegisteredFactoryDto factory) =>
        $"cc-devthrottle factory status --factory {factory.Factory}";

    // The folder is a path on the factory's own computer, which may not be this one, so it is joined with that
    // folder's own separator rather than this machine's.
    private static string InFolder(string folder, string relative)
    {
        var f = (folder ?? "").TrimEnd('\\', '/');
        var sep = f.Contains('\\') ? '\\' : '/';
        var r = (relative ?? "").Trim().TrimStart('\\', '/');
        return f.Length == 0 ? r : f + sep + r;
    }
}
