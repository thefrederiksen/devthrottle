using CcDirector.Core.Storage;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The categories the mission reports and never offers for removal.
///
/// The mission names them: DevThrottle's own session logs and history, recordings, application
/// data such as ProgramData\mindzie, Hugging Face models, Playwright browsers, Docker and virtual
/// machine disks, Android emulators, browser profiles. They are sized and dated by the scan and
/// never offered, and they are not rules: no code path may put one of them into the candidate
/// list, and a reader must not be able to mistake a report line for an offer.
///
/// This test holds that. It does not run any rule against the machine - no test in this suite
/// points at anything on the machine it runs on - it pins the structure instead: no rule in the
/// machine's rule set looks inside any of these places, no rule that produces candidates covers one
/// of them, and no rule is named for one. The one rule whose place is a whole volume - Windows'
/// own Disk Cleanup list, which looks at C:\ - offers nothing ever by design, and the test that
/// pins that emptiness is in the Disk Cleanup rule's own tests.
/// </summary>
public class MachineRulesDeclineTests
{
    /// <summary>
    /// The mission's reported-only categories, named as places on this machine. The list is typed
    /// here on purpose: it is the mission's own list, held as an alarm rather than read as
    /// product logic. A rule added for one of these places fails this test, and that is the test
    /// doing its job: the mission would have to be reopened before any of them is ever offered.
    /// </summary>
    private static readonly (string Category, string Place)[] ReportedOnlyCategories =
    [
        ("DevThrottle's own session logs and history", CcStorage.MachineRoot()),
        ("dictation recordings", CcStorage.DictationRecordings()),
        ("AgentEyes recordings", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEyes")),
        ("application data such as ProgramData\\mindzie", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "mindzie")),
        ("Hugging Face models", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface")),
        ("Playwright browsers", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")),
        ("Docker and virtual machine disks", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker")),
        ("Android emulators", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd")),
        ("browser profiles", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data")),
        ("browser profiles", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data")),
        ("browser profiles", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "User Data"))
    ];

    /// <summary>
    /// Words that name the reported-only categories, which no rule may carry in its identifier. A
    /// rule named for one of them is a rule built to offer one of them.
    /// </summary>
    private static readonly string[] CategoryWordsInRuleNames =
    [
        "recording", "mindzie", "hugging", "playwright", "docker", "android", "browser",
        "session-log", "profile", "history"
    ];

    [Fact]
    public void MachineRules_DeclineTheCategoriesTheMissionOnlyReports_NoRuleLooksInsideThem()
    {
        // The Windows rules and the places they decline are Windows things; on another platform
        // there is no Windows rule set to hold to them.
        if (!OperatingSystem.IsWindows()) return;

        foreach (var rule in WindowsRuleSet.ForThisMachine())
        {
            foreach (var (category, place) in ReportedOnlyCategories)
            {
                Assert.False(
                    RuleSelection.IsInside(rule.LooksIn, place),
                    $"the rule {rule.Id} looks in {rule.LooksIn}, which is inside {category} at {place}: " +
                    "that category is reported and never offered, and no rule may look inside it");
            }
        }
    }

    [Fact]
    public void MachineRules_DeclineTheCategoriesTheMissionOnlyReports_NoCandidateProducingRuleCoversThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var rule in WindowsRuleSet.ForThisMachine())
        {
            // The Disk Cleanup rules look at a whole volume, and the categories sit on it, so this
            // assertion cannot hold for them. They offer nothing ever instead, which is pinned by
            // DiskCleanupRuleTests, and that emptiness is why covering the volume costs nothing.
            if (IsAVolumeRoot(rule.LooksIn)) continue;

            foreach (var (category, place) in ReportedOnlyCategories)
            {
                Assert.False(
                    RuleSelection.IsInside(place, rule.LooksIn),
                    $"the rule {rule.Id} looks in {rule.LooksIn}, which covers {category} at {place}: " +
                    "that category is reported and never offered, and no rule that produces " +
                    "candidates may cover it");
            }
        }
    }

    [Fact]
    public void MachineRules_DeclineTheCategoriesTheMissionOnlyReports_NoRuleIsNamedForThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var rule in WindowsRuleSet.ForThisMachine())
        {
            foreach (var word in CategoryWordsInRuleNames)
            {
                Assert.DoesNotContain(word, rule.Id, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Every rule on the machine has its own identifier, because a machine matches on it: two
    /// rules sharing one would be one rule to everything that reads the answer.
    /// </summary>
    [Fact]
    public void WindowsRuleSet_TheMachineRules_HaveDistinctIdsAndPlaces()
    {
        var rules = WindowsRuleSet.ForThisMachine();

        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct().Count());
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.LooksIn)));

        // The rules this phase adds, named as this machine spells them. Asserted only where the
        // machine can be relied on to have the drive, which on this suite is Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(rules, rule => rule.Id == "windows-component-store");
            Assert.Contains(rules, rule => rule.Id == "windows-crash-dumps-and-error-reports");

            // One recycle bin rule and one Disk Cleanup rule per fixed volume.
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
            {
                var letter = char.ToLowerInvariant(drive.Name.TrimEnd(Path.DirectorySeparatorChar)[0]);
                Assert.Contains(rules, rule => rule.Id == $"windows-recycle-bin-on-{letter}");
                Assert.Contains(rules, rule => rule.Id == $"windows-disk-cleanup-on-{letter}");
            }
        }
    }

    private static bool IsAVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return string.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
