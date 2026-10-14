using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// The brief the Gateway gives a talk or a scheduled run: the seat's PUBLISHED version, its text and which version it
/// is (Factory Seats mission, phase 2c). Never a draft - a draft is a proposal nobody has approved.
/// </summary>
/// <param name="Version">The published version, counting from 1.</param>
/// <param name="Text">The brief's whole text at that version, exactly as it was published.</param>
public sealed record FactoryGivenBrief(int Version, string Text);

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

    /// <summary>
    /// The brief as the session reads it: its text between two lines that say which version it is. The text is given
    /// whole and unchanged; only line ends at its very end are dropped, so the closing line always starts a line.
    /// </summary>
    /// <param name="newLine">The line end the surrounding prompt is written with.</param>
    public static string BriefBlock(FactoryGivenBrief brief, string newLine)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(newLine);
        return $"--- your brief, version {brief.Version} ---" + newLine
               + brief.Text.TrimEnd('\r', '\n') + newLine
               + $"--- end of your brief, version {brief.Version} ---";
    }
}
