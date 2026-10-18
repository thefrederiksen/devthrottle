namespace CcDirector.Core.Claude;

/// <summary>
/// EVERY QUESTION CLAUDE CODE WOULD ASK A SESSION THAT NOBODY WILL ANSWER, ASKED IN ONE PLACE (Command Line Agent Handling
/// mission). The Director and the delivery rig both give this to <see cref="Sessions.SessionManager.ClaudeQuestionCheck"/>,
/// so the rig refuses exactly what the Director refuses. Each question is the owner's to answer and none is answered here:
/// the one-time Bypass Permissions warning (<see cref="ClaudeBypassConsent"/>) and "Teach auto mode about your
/// environment?" (<see cref="ClaudeAutoModeSetupOffer"/>).
/// </summary>
public static class ClaudeStartQuestions
{
    /// <summary>The sentence that refuses the start, naming the first question that would stop it, or null to go ahead.</summary>
    public static string? WhyItWouldStop(string? userArgs, string effectiveArgs, string workingDirectory, string machine) =>
        ClaudeBypassConsent.WhyItWouldStop(userArgs, effectiveArgs, workingDirectory, machine)
        ?? ClaudeAutoModeSetupOffer.WhyItWouldStop(userArgs, effectiveArgs, workingDirectory, machine);
}
