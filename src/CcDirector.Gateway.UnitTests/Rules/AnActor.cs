using CcDirector.Gateway.Rules;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Who made a change to a rule, for the tests that write one. Every write to the rule store names its caller now
/// (Session Rules part 2, phase 2), and these are the two kinds of caller the history has to tell apart.
/// </summary>
internal static class AnActor
{
    /// <summary>The owner's phone, as the device-key middleware names it.</summary>
    public static RuleChangeActor Owner { get; } = new("device:device-9f2c", null, null);

    /// <summary>An agent session calling with its own key.</summary>
    public static RuleChangeActor Session(Guid sessionId, string agent = "ClaudeCode") =>
        new("session:" + sessionId, sessionId.ToString(), agent);
}
