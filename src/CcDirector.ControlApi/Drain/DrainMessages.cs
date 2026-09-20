using System.Text;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// The words the drain says to a session. They are here, in one place, because three phrases in them are
/// load-bearing and were learned by running this on a real fleet:
///
///  - "START NOTHING NEW" - without it a session finishes its turn and picks up the next thing, and the
///    drain never converges.
///  - "YOU WILL NOT BE KILLED" - a session told a restart is coming will otherwise rush, and a rushed
///    handover is the failure this whole exercise exists to prevent. THE OLDER DRAIN ONLY. The smart
///    shutdown has a time limit and does end sessions, so its message (<see cref="SmartShutdown"/>)
///    does not say this; it says how long there is and to write the exact next action first.
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
    /// <param name="subordinates">The seats reporting to this one, each with the EXACT path the drain is
    /// watching for its document. Not a naming rule for the senior to apply: the rule sanitizes characters
    /// a file cannot carry and truncates a long name, and a senior that applies it differently produces a
    /// document at a path nothing is watching - the file exists on disk and the seat is declared
    /// unreachable. Empty for a seat with nobody reporting to it.</param>
    /// <param name="reason">Why the restart is happening, in the owner's words.</param>
    public static string Drain(
        string? directorName,
        string handoverPath,
        string directory,
        IReadOnlyList<(string Name, string Path)> subordinates,
        string? reason)
    {
        var hasSubordinates = subordinates is { Count: > 0 };
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
            sb.Append("You have seats reporting to you. Tell each of them to write its own document to the ");
            sb.Append("EXACT path named here - do not compose one, these are the paths being watched: ");
            foreach (var (name, path) in subordinates)
                sb.Append('"').Append(name).Append("\" -> \"").Append(path).Append("\"; ");
            sb.Append("collect them FIRST and wait for them before you finish yours. A seat with nothing ");
            sb.Append("of its own to hand over should NOT write a thin document - it reports up to you, ");
            sb.Append("and you name it on a 'covered:' line in your block below. ");
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

    // ===================================================================================================
    // THE SMART SHUTDOWN'S WORDS. Three messages, used by the smart shutdown only. Drain() above is the
    // older path's and is not touched: a session on that path is still told it will not be killed,
    // because on that path it will not be.
    // ===================================================================================================

    /// <summary>
    /// The smart shutdown's request to one lead or standalone session.
    ///
    /// It differs from <see cref="Drain"/> in the one place the two features differ. The older drain
    /// never forces, so it says "YOU WILL NOT BE KILLED". A smart shutdown has a limit the owner chose
    /// and ends whatever is still present when it is reached, so that sentence would be a lie here and
    /// it is gone. In its place: how long there is, and the instruction that makes a limit survivable -
    /// write the exact next action FIRST and the rest after, so that whatever is on disk when time is up
    /// is already useful.
    ///
    /// "START NOTHING NEW" and "NO SECRETS" stay, for the reasons given at the top of this class. The
    /// paragraph about the seats reporting to a lead and the paragraph describing the closing block are
    /// the same words as in <see cref="Drain"/>, and a test holds them equal: the block is read by one
    /// parser (<see cref="DrainReportBlock"/>), so there must be one description of it.
    /// </summary>
    /// <param name="directorName">The Director shutting down, named so the session knows which.</param>
    /// <param name="handoverPath">The exact file this session writes - the same path that is watched.</param>
    /// <param name="subordinates">The sessions under this one, each with the EXACT path watched for its
    /// document. Empty for a session with nobody under it.</param>
    /// <param name="reason">Why, in the owner's words. Optional.</param>
    /// <param name="timeAllowed">How long the sessions have in all, from this message.</param>
    public static string SmartShutdown(
        string? directorName,
        string handoverPath,
        IReadOnlyList<(string Name, string Path)> subordinates,
        string? reason,
        TimeSpan timeAllowed)
    {
        var hasSubordinates = subordinates is { Count: > 0 };
        var minutes = WholeMinutes(timeAllowed);
        var sb = new StringBuilder();
        sb.Append("This is your Director. ");
        sb.Append(string.IsNullOrWhiteSpace(directorName) ? "This Director" : directorName);
        sb.Append(" is shutting down its sessions");
        if (!string.IsNullOrWhiteSpace(reason)) sb.Append(" (").Append(reason.Trim()).Append(')');
        sb.Append(", so every session on it will be closed - including yours. ");
        sb.Append("Finish the step you are on and START NOTHING NEW. ");
        sb.Append("YOU HAVE ").Append(minutes).Append(minutes == 1 ? " MINUTE" : " MINUTES");
        sb.Append(" from this message. A session still working part way through that time is interrupted ");
        sb.Append("and asked again, and whatever is still running at the end of it is shut down. ");
        sb.Append("Write a handover document to \"").Append(handoverPath).Append("\" using the seven ");
        sb.Append("headings from the move-session skill - fetch it with 'cc-devthrottle skill get ");
        sb.Append("move-session', it is the authority on what a good handover contains, and it is NOT the ");
        sb.Append("local /handover skill, whose seven headings are different. ");
        sb.Append("WRITE THE EXACT NEXT ACTION FIRST and save the file, then write the rest after it, so ");
        sb.Append("that whatever is on disk when time is up is already useful. When the Director comes ");
        sb.Append("back, a new session with a clean context may be started from this document, and it ");
        sb.Append("will know only what the document says. ");

        if (hasSubordinates)
        {
            sb.Append("You have seats reporting to you. Tell each of them to write its own document to the ");
            sb.Append("EXACT path named here - do not compose one, these are the paths being watched: ");
            foreach (var (name, path) in subordinates)
                sb.Append('"').Append(name).Append("\" -> \"").Append(path).Append("\"; ");
            sb.Append("collect them FIRST and wait for them before you finish yours. A seat with nothing ");
            sb.Append("of its own to hand over should NOT write a thin document - it reports up to you, ");
            sb.Append("and you name it on a 'covered:' line in your block below. ");
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
        sb.Append("If you cannot reach a clean stop in that time, say so plainly in that block and say ");
        sb.Append("what you are blocked on - the document is still read, and it is what whoever picks ");
        sb.Append("this work up starts from. You do not need to reply to this message - the document IS ");
        sb.Append("the reply.");

        return sb.ToString();
    }

    /// <summary>
    /// The short second message, sent to a session that was still mid-turn at two thirds of the time
    /// allowed and has just been interrupted: "hand over now: the exact next action first".
    ///
    /// It names the path and the closing block again, briefly, because the session it reaches may never
    /// have had either from the Director: a session under a lead is asked by its lead, not by us.
    /// </summary>
    /// <param name="handoverPath">The exact file this session writes - the same path that is watched.</param>
    /// <param name="timeLeft">How long is left before whatever is still running is shut down.</param>
    public static string HandOverNow(string handoverPath, TimeSpan timeLeft)
    {
        var minutes = WholeMinutes(timeLeft);
        var sb = new StringBuilder();
        sb.Append("This is your Director. You were interrupted because the time for this shutdown is ");
        sb.Append("nearly up: about ").Append(minutes).Append(minutes == 1 ? " minute is" : " minutes are");
        sb.Append(" left, and then this session is shut down whatever it is doing. ");
        sb.Append("HAND OVER NOW: THE EXACT NEXT ACTION FIRST. Do not go back to the work you were ");
        sb.Append("interrupted in. Write the exact next action to \"").Append(handoverPath);
        sb.Append("\" and save the file before anything else; then add what is proven, what is only ");
        sb.Append("believed, and what is uncommitted. End the document with the drain-report block: an ");
        sb.Append("HTML comment opening with 'drain-report', then 'state: drained', then 'restore: yes' ");
        sb.Append("or 'restore: no', then 'why: <one line>'. Close the comment. NO SECRETS in the ");
        sb.Append("document. You do not need to reply to this message - the document IS the reply.");
        return sb.ToString();
    }

    /// <summary>
    /// What a session still open is told when the owner chooses "Cancel and keep working": the restart
    /// is off, carry on.
    ///
    /// Unlike a closing message - see below for why there is none - this one is meant to start a turn. A
    /// session that was told to start nothing new, and perhaps interrupted, stays stopped for ever unless
    /// somebody tells it otherwise, and the owner has just said he wants it working.
    /// </summary>
    /// <param name="directorName">The Director, named so the session knows which.</param>
    public static string RestartIsOff(string? directorName)
    {
        var sb = new StringBuilder();
        sb.Append("This is your Director. The shutdown of ");
        sb.Append(string.IsNullOrWhiteSpace(directorName) ? "this Director" : directorName);
        sb.Append(" was CANCELLED by the owner: THE RESTART IS OFF and you will not be closed. Carry on ");
        sb.Append("with your work from where you were. If you were interrupted, pick that work up again. ");
        sb.Append("If you started or finished a handover document, leave it where it is - nothing will ");
        sb.Append("act on it, and you do not need to finish it.");
        return sb.ToString();
    }

    /// <summary>A time as whole minutes for a sentence, never below one: "0 minutes left" tells a session
    /// nothing it can act on, and the limit is what ends it, not this number.</summary>
    private static int WholeMinutes(TimeSpan time) => Math.Max(1, (int)Math.Round(time.TotalMinutes));

    // THERE IS NO CLOSING MESSAGE, and its absence is deliberate. There used to be one - "handover
    // received and read; do nothing further; you are being closed". A message DELIVERS A PROMPT and a
    // prompt starts a turn; the Director's reaper waits out a running turn; so the sequence was: ask for
    // the close, provoke one last turn in which the seat can amend its document to say it is blocked,
    // wait for that turn to end, delete the seat - and never read what it said. Nothing is lost by
    // removing it: the seat was already told in the drain message that the Director is restarting and to
    // start nothing new, and a seat is only closed once it has DECLARED it finished. Anything the drain
    // noticed goes into the record, where it survives the session.

    /// <summary>The name a drained seat carries while it waits to be reaped, so every screen shows which
    /// seats are already done without anybody having to open the record.</summary>
    /// <param name="originalName">The seat's name.</param>
    public static string DrainedName(string originalName) => $"[DRAINED] {originalName}";
}
