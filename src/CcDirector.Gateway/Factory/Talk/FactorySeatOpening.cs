using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// The brief the Gateway gives a talk or a scheduled run: the seat's PUBLISHED version, its text and which version it
/// is (Factory Seats mission, phase 2c). Never a draft - a draft is a proposal nobody has approved.
/// </summary>
/// <param name="Version">The published version, counting from 1.</param>
/// <param name="Text">The brief's whole text at that version, exactly as it was published.</param>
/// <param name="Kind">Which kind of version <paramref name="Version"/> is (<see cref="FactoryBriefVersionKinds"/>): the
/// seat's own brief version, or - for a defined factory - the definition version whose seat file holds the brief
/// (Factory Definition, phase 4).</param>
public sealed record FactoryGivenBrief(int Version, string Text, string Kind = FactoryBriefVersionKinds.Brief)
{
    /// <summary>True when <see cref="Version"/> is a definition version.</summary>
    public bool FromDefinition => Kind == FactoryBriefVersionKinds.Definition;

    /// <summary>
    /// The version as it is written down and read back - on a talk's activity line (<c>--version</c>) and on the
    /// activity row a run leaves: "3" for a brief version, as always, and "definition v3" for a definition version,
    /// so the two can never be mistaken for each other.
    /// </summary>
    public string Recorded => FromDefinition
        ? "definition v" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"version 3", or "definition version 3": the version as a sentence says it.</summary>
    public string VersionWords => (FromDefinition ? "definition version " : "version ")
                                  + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// HOW THE GATEWAY OPENS A SEAT'S SESSION (Factory Seats mission, phase 2c): who the seat is, from the registration,
/// and the seat's brief, from the Gateway. A talk and a scheduled run are opened in the same words, so both are
/// written here, once, and pure so the wording is tested rather than read.
///
/// The seat is named from the registration at the moment the session is started. That is what makes renaming a seat
/// one registration and nothing else: no schedule's prompt and no file has to say the name again.
/// </summary>
public static class FactorySeatOpening
{
    /// <summary>
    /// The sentence that tells the session who it is. The boss is "the boss of &lt;factory&gt;" and never a person's
    /// name (the owner's ruling of 8 October 2026), and its role is Boss (13 October 2026), so no role is said beside
    /// it. Every other seat is introduced by its registered name and role.
    /// </summary>
    public static string WhoYouAre(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        var sb = new StringBuilder();
        if (FactoryTalkSeed.IsBoss(factory, seat))
            sb.Append("You are the boss of ").Append(factory.Title);
        else
            sb.Append("You are ").Append(seat.Name).Append(", ").Append(seat.Role).Append(" of ").Append(factory.Title);
        sb.Append(" (factory id: ").Append(factory.Factory).Append("; your agent id: ").Append(seat.Id).Append(").");
        return sb.ToString();
    }

    /// <summary>The environment variable that names a seat's kit folder on its machine (Factory Definition mission,
    /// phase 5): the Director sets it on a session of a defined factory's seat.</summary>
    public const string KitDirVariable = "CC_FACTORY_KIT_DIR";

    /// <summary>
    /// Where a seat of a DEFINED factory finds its kit (Factory Definition mission, phase 5; phase 4 QA report, finding
    /// 2): the folder <see cref="KitDirVariable"/> names, with <c>brief.md</c> and <c>grants.md</c> in it. Said by name of
    /// the variable, never as a path: the folder is on the seat's machine and only the Director that started it knows it.
    /// </summary>
    public const string KitSentence =
        "Your kit is in the folder the environment variable " + KitDirVariable + " names: brief.md in it holds this same "
        + "brief, and grants.md what you are allowed to do, with its limits. Read grants.md before you act on anything it covers.";

    /// <summary>
    /// How a seat of a DEFINED factory changes the factory's goal (Factory Definition mission, phase 5): the goal is part
    /// of the definition, so a goal change is a definition change the owner publishes - never a goal file, never a
    /// registration (a defined factory's registration is written by a publish, and any other is refused).
    /// </summary>
    public const string GoalChange =
        "save a draft of the definition with the new goal (cc-devthrottle factory definition save); the owner publishes it.";

    /// <summary>
    /// The brief as the session reads it: its text between two lines that say which version it is. The text is given
    /// whole and unchanged; only line ends at its very end are dropped, so the closing line always starts a line.
    /// </summary>
    /// <param name="newLine">The line end the surrounding prompt is written with.</param>
    public static string BriefBlock(FactoryGivenBrief brief, string newLine)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(newLine);
        return $"--- your brief, {brief.VersionWords} ---" + newLine
               + brief.Text.TrimEnd('\r', '\n') + newLine
               + $"--- end of your brief, {brief.VersionWords} ---";
    }
}
