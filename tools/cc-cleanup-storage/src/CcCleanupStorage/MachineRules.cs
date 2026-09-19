using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;

namespace CcCleanupStorage;

/// <summary>
/// Which rules run on the machine this tool is running on.
///
/// This is the one place the platform is asked. It decides nothing about what any rule MEANS - a
/// rule's own answer and the engine's fold do all of that - it only answers which rule sets exist
/// here. The Windows rules are the only set that exists today; macOS and Linux arrive later as
/// sibling sets, which is one more entry here and no change anywhere else.
///
/// A platform with no rule set gets an empty list, and the engine turns an empty list into a broken
/// report rather than into an answer of nothing to remove.
/// </summary>
public static class MachineRules
{
    /// <summary>Every rule that runs on this machine, in order of measured value.</summary>
    public static IReadOnlyList<IReclaimRule> ForThisMachine()
    {
        var rules = OperatingSystem.IsWindows() ? WindowsRuleSet.ForThisMachine() : [];
        FileLog.Write($"[MachineRules] ForThisMachine: windows={OperatingSystem.IsWindows()}, rules={rules.Count}");
        return rules;
    }
}
