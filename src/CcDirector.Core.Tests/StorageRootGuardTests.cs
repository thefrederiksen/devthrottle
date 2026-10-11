using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Architecture fitness function: production code must not re-derive the cc-director storage root by
/// hand. Composing <c>GetFolderPath(LocalApplicationData) + "cc-director"</c> (or the MyDocuments
/// equivalent) yourself produces a path that CC_DIRECTOR_ROOT cannot redirect - so any test that reaches
/// that code writes into the REAL running Director's folders, and no amount of care in the test can stop
/// it. Ask <see cref="Storage.CcStorage"/> instead; it owns the root and honors the override.
///
/// This is not hypothetical. It has now cost real time four times:
///   - Screenshots: CcStorage.Screenshots() ignored the root, so the chunked upload proof left a 50 KB
///     undrawable file in the owner's Pictures gallery on every Gateway run for over a day (#1577).
///   - StateChangeLog + VoiceUtteranceService: both baked the root into a static readonly field, so their
///     tests appended into the live Director's own data directory (#1580).
///   - Tailscale serve: test hosts clobbered the production serve table every fixture - the long-standing
///     "#179/#200 mystery clobberer" - until TestEnvironment pinned it off process-wide.
/// Each was written by someone being careful. Care is not the control; this test is.
///
/// The rule is enforced for NEW code. <see cref="KnownOffenders"/> freezes the call sites that already
/// existed when the guard landed: the guard fails the moment a new one appears, and each entry burned
/// down is deleted from the list, never added to. A file only earns a place here by predating the guard.
///
/// Deliberately scoped to src/. The installers under tools/cc-director-setup* are excluded because they
/// DEFINE the install root rather than consume it - they are the code that creates the folder CcStorage
/// later reads, and they must target the real machine to install onto it.
/// </summary>
public sealed class StorageRootGuardTests
{
    /// <summary>
    /// BURN-DOWN LIST: EMPTY, and it must stay that way. All 22 call sites that predated this guard have
    /// been moved onto CcStorage. Adding an entry here is not a way to land code that re-derives the
    /// root - it is the exception list for a debt that no longer exists. If a new call site needs a
    /// folder CcStorage has no method for, add the method; that is the whole point.
    /// </summary>
    private static readonly string[] KnownOffenders = Array.Empty<string>();

    /// <summary>CcStorage owns the root, so it is the one place allowed to resolve it from the OS.
    /// CcStorageMigration moves data between the old and new real locations, which it cannot do
    /// through the very abstraction it is migrating.
    ///
    /// BrainLog is the one genuine exception: CcDirector.AgentBrain is a standalone library with NO
    /// project references by design ("reused across many host programs, so it cannot depend on
    /// CcDirector.Core's FileLog"), so it cannot call CcStorage without breaking that boundary. It
    /// pays for the exemption by honoring CC_DIRECTOR_ROOT by hand, which is what actually matters -
    /// the rule is "be redirectable", and using CcStorage is just the normal way to achieve it.
    /// Anything added here must clear the same bar, and "it was easier" does not.</summary>
    private static readonly string[] RootOwners =
    {
        "src/CcDirector.Core/Storage/CcStorage.cs",
        "src/CcDirector.Core/Storage/CcStorageMigration.cs",
        "src/CcDirector.AgentBrain/BrainLog.cs",
    };

    [Fact]
    public void Production_code_does_not_rederive_the_storage_root()
    {
        var offenders = ScanForRootRederivation(out _);

        Assert.True(offenders.Count == 0,
            "These files compose the cc-director storage root by hand instead of asking CcStorage. "
            + "The result cannot be redirected by CC_DIRECTOR_ROOT, so any test reaching this code writes "
            + "into the user's REAL Director folders (see #1577 / #1580). Use CcStorage - add a method "
            + "there if the folder has none yet:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void BrainLog_pays_for_its_exemption_by_honoring_the_root()
    {
        // The exemption is conditional, so the condition is tested rather than trusted: AgentBrain may
        // skip CcStorage (no project references by design), but it must still be redirectable. Without
        // this, "cannot use CcStorage" would be a loophole that reintroduces the exact unredirectable
        // path the guard exists to prevent.
        var file = Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.AgentBrain", "BrainLog.cs");
        var text = File.ReadAllText(file);

        Assert.True(text.Contains("CC_DIRECTOR_ROOT", StringComparison.Ordinal),
            "BrainLog is exempt from using CcStorage because CcDirector.AgentBrain has no project "
            + "references by design - but it must still honor CC_DIRECTOR_ROOT by hand, or a test that "
            + "pins the root writes into the real Director's log folder.");
    }

    [Fact]
    public void Burn_down_list_has_no_stale_entries()
    {
        // Keeps the list honest: a file that no longer re-derives the root must be removed, so the list
        // only ever shrinks and cannot quietly become a permanent excuse.
        ScanForRootRederivation(out var seen);
        var stale = KnownOffenders.Where(k => !seen.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "These entries no longer re-derive the storage root - delete them from KnownOffenders so the "
            + "guard keeps protecting them:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>Find every production file that resolves a real user folder and pins "cc-director" under
    /// it - the shape that bypasses CcStorage. Returns non-allowlisted offenders; reports all matches via
    /// <paramref name="allMatches"/>.</summary>
    private static List<string> ScanForRootRederivation(out HashSet<string> allMatches)
    {
        var root = RepositorySourceIndex.Root;
        var offenders = new List<string>();
        allMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in RepositorySourceIndex.Under("src", ".cs"))
        {
            var rel = Relative(root, file);
            if (IsTestProject(rel)) continue;
            if (RootOwners.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;

            if (!RederivesRoot(File.ReadAllText(file))) continue;

            allMatches.Add(rel);
            if (!KnownOffenders.Contains(rel, StringComparer.OrdinalIgnoreCase))
                offenders.Add(rel);
        }

        return offenders;
    }

    /// <summary>
    /// True when the text resolves LocalApplicationData/MyDocuments and names "cc-director" within a few
    /// lines of it - i.e. builds our own root rather than reading someone else's folder. Deliberately
    /// narrow: production legitimately resolves real folders for OTHER things (~/.claude, ~/.codex,
    /// Program Files browsers, the Start Menu), and banning that outright would be wrong.
    /// </summary>
    private static bool RederivesRoot(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal)
                && !lines[i].Contains("SpecialFolder.MyDocuments", StringComparison.Ordinal))
                continue;

            // The folder name usually lands on the same line or the next few (Path.Combine wraps).
            var window = string.Join('\n', lines.Skip(i).Take(4));
            if (window.Contains("\"cc-director\"", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // One shared predicate (see TestProjectPath) - the other guard that stayed green by luck rather
    // than by being satisfied, and would have fired later on an unrelated change.
    private static bool IsTestProject(string rel) => TestProjectPath.IsTestProject(rel);

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');
}
