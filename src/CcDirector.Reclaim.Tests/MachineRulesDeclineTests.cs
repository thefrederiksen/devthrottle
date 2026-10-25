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
    /// A Windows machine described rather than read, rooted wherever the running platform keeps its temporary
    /// folder: <c>C:</c> and <c>D:</c> as its fixed volumes, one user, and the folders Windows gives them. Nothing
    /// is created on disk - the three tests below only compare places.
    ///
    /// WHY A DESCRIBED MACHINE. These tests used to build the rule set from the machine running them and return
    /// early anywhere but Windows, so off Windows they passed having checked nothing. The structure they pin -
    /// which places the Windows rules look in, and what they are named - does not depend on the machine at all,
    /// so it is checked on every platform against a machine whose every folder is known.
    /// </summary>
    private static readonly string MachineRoot = Path.Combine(Path.GetTempPath(), "described-windows-machine");
    private static readonly string VolumeC = Path.Combine(MachineRoot, "C");
    private static readonly string VolumeD = Path.Combine(MachineRoot, "D");
    private static readonly string UserProfile = Path.Combine(VolumeC, "Users", "someone");
    private static readonly string LocalApplicationData = Path.Combine(UserProfile, "AppData", "Local");
    private static readonly string CommonApplicationData = Path.Combine(VolumeC, "ProgramData");

    private static readonly WindowsMachineFolders DescribedMachine = new(
        LocalApplicationData: LocalApplicationData,
        UserProfile: UserProfile,
        Windows: Path.Combine(VolumeC, "Windows"),
        CommonApplicationData: CommonApplicationData,
        Temp: Path.Combine(LocalApplicationData, "Temp"),
        FixedVolumeRoots: [VolumeC, VolumeD]);

    /// <summary>
    /// The mission's reported-only categories, named as places on the described machine - where each one lives on
    /// Windows. The list is typed here on purpose: it is the mission's own list, held as an alarm rather than read
    /// as product logic. A rule added for one of these places fails this test, and that is the test doing its job:
    /// the mission would have to be reopened before any of them is ever offered.
    /// </summary>
    private static readonly (string Category, string Place)[] ReportedOnlyCategories =
    [
        ("DevThrottle's own session logs and history", Path.Combine(LocalApplicationData, "cc-director")),
        ("dictation recordings", Path.Combine(LocalApplicationData, "cc-director", "dictation", "recordings")),
        ("AgentEyes recordings", Path.Combine(LocalApplicationData, "AgentEyes")),
        ("application data such as ProgramData\\mindzie", Path.Combine(CommonApplicationData, "mindzie")),
        ("Hugging Face models", Path.Combine(UserProfile, ".cache", "huggingface")),
        ("Playwright browsers", Path.Combine(LocalApplicationData, "ms-playwright")),
        ("Docker and virtual machine disks", Path.Combine(LocalApplicationData, "Docker")),
        ("Android emulators", Path.Combine(UserProfile, ".android", "avd")),
        ("browser profiles", Path.Combine(LocalApplicationData, "Google", "Chrome", "User Data")),
        ("browser profiles", Path.Combine(LocalApplicationData, "Microsoft", "Edge", "User Data")),
        ("browser profiles", Path.Combine(LocalApplicationData, "BraveSoftware", "Brave-Browser", "User Data"))
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

    /// <summary>The Windows rule set built for the described machine, through the seam ForThisMachine uses.</summary>
    private static IReadOnlyList<IReclaimRule> WindowsRules()
    {
        var rules = WindowsRuleSet.For(DescribedMachine);
        // A check over an empty set is a check of nothing: the set must be the whole Windows rule set.
        // (The per-volume rules are counted, not named: their identifier carries the drive letter as the running
        // platform reads it, and only Windows reads one out of a path.)
        Assert.Contains(rules, rule => rule.Id == "windows-component-store");
        Assert.Contains(rules, rule => rule.Id == "windows-crash-dumps-and-error-reports");
        Assert.Equal(DescribedMachine.FixedVolumeRoots.Count, rules.Count(rule => rule.Id.StartsWith("windows-disk-cleanup-on-", StringComparison.Ordinal)));
        Assert.Equal(DescribedMachine.FixedVolumeRoots.Count, rules.Count(rule => rule.Id.StartsWith("windows-recycle-bin-on-", StringComparison.Ordinal)));
        return rules;
    }

    [Fact]
    public void MachineRules_DeclineTheCategoriesTheMissionOnlyReports_NoRuleLooksInsideThem()
    {
        foreach (var rule in WindowsRules())
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
        foreach (var rule in WindowsRules())
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
        foreach (var rule in WindowsRules())
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
    ///
    /// Asked of the rules this machine really runs - <see cref="CcCleanupStorage.MachineRules"/> - not of
    /// the Windows rule set built on whatever machine runs the test. That set reads Windows volume roots
    /// with the running platform's path rules, so built on Linux every mount point reads as the one root
    /// "/" and its per-volume identifiers collide; production never builds it there, and a test that does
    /// was proving something about a machine nobody runs. When a macOS or Linux rule set arrives, this
    /// test holds it to the same rule without being touched.
    /// </summary>
    [Fact]
    public void MachineRules_TheRulesThisMachineRuns_HaveDistinctIdsAndPlaces()
    {
        var rules = CcCleanupStorage.MachineRules.ForThisMachine();

        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct().Count());
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.LooksIn)));

        // The rules this phase adds, named as this machine spells them, on the one platform that has a
        // rule set. Every other platform has none yet, and says so with an empty list - which the engine
        // turns into a broken report, never an answer of nothing to remove (RecommendationBuilderTests).
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
        else
        {
            Assert.Empty(rules);
        }
    }

    /// <summary>True when <paramref name="path"/> is one of the described machine's fixed volumes.</summary>
    private static bool IsAVolumeRoot(string path) =>
        DescribedMachine.FixedVolumeRoots.Any(root => string.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal));
}
