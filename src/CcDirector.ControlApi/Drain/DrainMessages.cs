using System.Text;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// The words the drain says to a session. They are here, in one place, because three phrases in them are
/// load-bearing and were learned by running this on a real fleet:
///
///  - "START NOTHING NEW" - without it a session finishes its turn and picks up the next thing, and the
///    drain never converges.
///  - "YOU WILL NOT BE KILLED" - a session told a restart is coming will otherwise rush, and a rushed
///    handover is the failure this whole exercise exists to prevent.
///  - "NO SECRETS" - the first handover ever produced this way came back carrying a virtual machine's
///    administrator password. It is an instruction, not a check, which is why the sweep exists too.
///
/// And one that is load-bearing for a reason nobody would guess: the handover skill is named EXPLICITLY,
/// because two skills both promise "seven headings" and they are not the same seven. The local handover
/// skill's third heading is "Files Modified"; move-session's is "the exact next action". A session that
/// reaches for the wrong one puts the owner questions where nobody is reading.
/// </summary>
public static class DrainMessages
{
    /// <summary>
    /// The drain message sent to one senior or standalone seat.
    /// </summary>
    /// <param name="directorName">The Director being restarted, named so the seat knows which machine.</param>
    /// <param name="handoverPath">The exact file this seat writes - the same path the drain watches.</param>
    /// <param name="directory">The drain's directory, where a senior's subordinates write theirs.</param>
    /// <param name="hasSubordinates">True when this seat has seats reporting to it, which changes what it
    /// is asked to do: collect first, then report for the whole subtree.</param>
    /// <param name="reason">Why the restart is happening, in the owner's words.</param>
    public static string Drain(
        string? directorName, string handoverPath, string directory, bool hasSubordinates, string? reason)
    {
        var sb = new StringBuilder();
        sb.Append("This is your Director. ");
        sb.Append(string.IsNullOrWhiteSpace(directorName) ? "This Director" : directorName);
        sb.Append(" is being restarted");
        if (!string.IsNullOrWhiteSpace(reason)) sb.Append(" (").Append(reason.Trim()).Append(')');
        sb.Append(", so every session on it will be destroyed - including yours. ");
        sb.Append("Finish the turn you are on and START NOTHING NEW. ");
        sb.Append("Then write a handover document to \"").Append(handoverPath).Append("\" using the seven ");
        sb.Append("headings from the move-session skill - fetch it with 'cc-devthrottle skill get ");
        sb.Append("move-session', it is the authority on what a good handover contains, and it is NOT the ");
        sb.Append("local /handover skill, whose seven headings are different. ");

        if (hasSubordinates)
        {
            sb.Append("You have seats reporting to you. Tell each of them to write its own document into \"");
            sb.Append(directory).Append("\" named \"<its own short session id> - <its own name>.md\" FIRST, ");
            sb.Append("and wait for them before you finish yours. A seat with nothing of its own to hand ");
            sb.Append("over should NOT write a thin document - it reports up to you, and you name it on a ");
            sb.Append("'covered:' line in your block below. ");
        }

        sb.Append("End your document with this block, exactly, on its own lines: ");
        sb.Append("an HTML comment opening with 'drain-report', then 'state: drained' (or 'blocked' or ");
        sb.Append("'declined'), then 'restore: yes' or 'restore: no' saying whether this seat should be ");
        sb.Append("brought back after the restart, then 'why: <one line>' saying why - you know your own ");
        sb.Append("work better than anyone reading it afterwards. Add one 'covered: <session id> | <why ");
        sb.Append("it reported up>' line for each seat your document accounts for, one 'question: <the ");
        sb.Append("question, word for word>' line for every question you are leaving on the owner, and ");
        sb.Append("'blocked-reason: <what you are blocked on>' if you cannot reach a clean stop. ");
        sb.Append("Close the comment. ");
        sb.Append("NO SECRETS in the document - no passwords, no keys, no tokens; it is swept before the ");
        sb.Append("restart. ");
        sb.Append("If you cannot reach a clean stop, say so plainly in that block and say what you are ");
        sb.Append("blocked on: YOU WILL NOT BE KILLED, nothing will be forced, and the restart simply will ");
        sb.Append("not happen. You do not need to reply to this message - the document IS the reply.");

        return sb.ToString();
    }

    /// <summary>
    /// The message sent to a seat immediately before it is flagged for deletion, so a session that is
    /// still awake does not start something new in the gap between the drain reading its document and the
    /// reaper removing it.
    /// </summary>
    /// <param name="reReadNote">Anything the drain noticed at close time, notably that the document
    /// changed after it was first read.</param>
    public static string Closing(string? reReadNote = null)
    {
        var sb = new StringBuilder();
        sb.Append("Handover received and read. Do nothing further; you are being closed for the Director ");
        sb.Append("restart. Your document is on disk and the record of this drain is on the Gateway.");
        if (!string.IsNullOrWhiteSpace(reReadNote)) sb.Append(' ').Append(reReadNote.Trim());
        return sb.ToString();
    }

    /// <summary>The name a drained seat carries while it waits to be reaped, so every screen shows which
    /// seats are already done without anybody having to open the record.</summary>
    /// <param name="originalName">The seat's name.</param>
    public static string DrainedName(string originalName) => $"[DRAINED] {originalName}";
}
