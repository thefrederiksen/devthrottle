namespace CcDirector.RuleHook;

/// <summary>
/// The limits of the decision record (W3) that BOTH ends must agree on, in one file. The evaluator may
/// reference no project (ruling P11), so the Gateway compiles this same source file in
/// (<c>CcDirector.Gateway.csproj</c>, linked) rather than keeping a second literal that could drift.
/// </summary>
internal static class HookWireLimits
{
    /// <summary>The longest command text a decision records; the evaluator cuts to it and the Gateway's
    /// subject column and write gate hold exactly this much.</summary>
    public const int SubjectMaxLength = 4000;
}
