namespace CcDirector.Core.Sessions;

/// <summary>
/// Whether the fleet preamble - the text that tells a session the rules it runs under - actually
/// reached a session being launched.
///
/// WHY THIS IS A REFUSAL AND NOT A LOG LINE. The preamble is not a convenience. It carries the
/// standing rules, including the one forbidding an assistant's name in the owner's repositories and
/// his clients' deliverables. A session that starts without it is not slightly degraded: it does not
/// know the rules, and nothing about it looks any different from one that does.
///
/// The code this replaces got the ANALYSIS right and the ANSWER wrong, which is why it is worth
/// spelling out. Both installers documented the fold and its consequence in their own doc comments -
/// "in which case the caller launches the session without hook-based pointer tracking (the session
/// still starts)" and "false if anything failed, in which case the session still launches, just
/// without the preamble hook". The third state was seen, understood precisely enough to be written
/// down, written down, and shipped anyway. So this is not a case of an unconsidered branch: it is a
/// warning in a document, and a warning in a document is a design defect wearing a note.
///
/// The test that settles it: VISIBLE TO THE PERSON THE CONSEQUENCE HAPPENS TO, AT THE MOMENT IT
/// HAPPENS. A file-log line fails that test twice over - the log is not read at launch and it is not
/// read by the person. A badge on the session fails it too, more subtly: a session launched into a
/// fleet of twenty may never be looked at, so a mark that must be LOOKED AT is not a mark that
/// reaches anyone at the moment it matters. A refusal at launch is unmissable, arrives exactly when
/// the person can act, and says what to fix - the same shape the unresolvable-executable refusal in
/// <c>SessionManager.CreateSession</c> already uses, which is the proven-visible path in this code.
/// </summary>
public enum PreambleDelivery
{
    /// <summary>
    /// The preamble file was written and the hook that reads it is installed. The session will be
    /// told the rules within moments of starting.
    /// </summary>
    Delivered,

    /// <summary>
    /// This agent family has no <c>SessionStart</c> hook and is not expected to have one - its rules
    /// arrive by another route, or not at all. NOT a failure, and deliberately its own value rather
    /// than folded into <see cref="Delivered"/>: "we did not need to" and "we did it" are different
    /// facts, and a future reader counting delivered sessions must not be handed the wrong one.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// The preamble could NOT be delivered. The session must not start: it would run without the
    /// standing rules, and nobody would be told.
    /// </summary>
    Failed,
}
