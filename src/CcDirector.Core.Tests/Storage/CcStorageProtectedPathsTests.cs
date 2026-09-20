using System.Reflection;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// The staleness guard on <see cref="CcStorage.ProtectedPaths"/> - the list of paths nothing may ever
/// remove, which the reclaim tool's first refusal reads.
///
/// The reclaim mission first planned to find those paths by reflecting over CcStorage and taking every
/// method whose name contains "vault", "credential" or "secret". That instrument was rejected on
/// evidence: it protected <c>Vault()</c> and <c>SecretsStore()</c> by luck of being named well, and was
/// blind to <c>Config()</c> - whose own documentation comment reads "Tool settings, OAuth tokens,
/// credentials, app state" - and to keyvault.json, which no method named at all. Refusal 1 would have
/// protected two paths by accident of naming and silently left the OAuth tokens and the account key
/// vault exposed. The list is now an explicit enumeration declared in CcStorage beside the paths it
/// names, and THIS test is what keeps it from going stale: it enumerates the public static path
/// members of CcStorage and fails when one is neither protected nor explicitly declared not protected.
///
/// Adding a new storage path then forces a decision instead of defaulting to unprotected, invisibly -
/// which is the whole reason a hand-typed list was forbidden in the first place.
///
/// A "path member" here is a public static method of CcStorage that takes no parameters and returns a
/// string. Every method that takes parameters composes from one of these (ToolConfig composes Config,
/// ConnectionProfile composes Connections), so the parameterless set is the complete set of
/// independently named paths, and guarding it guards everything a parameterized composer could reach.
/// </summary>
public sealed class CcStorageProtectedPathsTests
{
    // The reasons a path may be declared not protected, so each entry states one honest, checkable
    // fact rather than a shrug. "No rule offers it" is a fact about the reclaim rules, which only ever
    // match folders they can positively prove disposable - the temporary folder, the package caches,
    // the Windows installer folder. Nothing the product stores in its own root is offered by any rule.
    private const string InsideVault = "inside the vault, which is protected";
    private const string InsideConfig = "inside the config folder, which is protected";
    private const string InsideConnections = "inside the browser connections directory, which is protected";
    private const string InsideAUserFolder =
        "inside one of the user's own folders, which the reclaim gate's third refusal refuses";
    private const string DataTheMissionReportsAndNeverOffers =
        "data the mission reports and never offers for removal; no rule matches it";
    private const string TheInstalledProduct =
        "the installed product itself; no rule offers the installed tools";

    /// <summary>
    /// Every public static path member of CcStorage that is NOT in the protected list, each with its
    /// reason. This list is the explicit other half of the decision the guard forces: a member may be
    /// protected, or it may be here saying why it is not - it may not be absent from both. An entry
    /// that no longer exists must be deleted from here, which the stale-entry test below enforces.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotProtected =
        new Dictionary<string, string>
        {
            ["Root"] = "the storage root itself; the folders under it that hold credentials are protected individually, and nothing else under it is offered by any rule",
            ["MachineRoot"] = TheInstalledProduct,
            ["FleetManagerHome"] = DataTheMissionReportsAndNeverOffers,
            ["GatewayDb"] = DataTheMissionReportsAndNeverOffers,
            ["DefaultRoot"] = "the default location of the storage root; the sensitive folders beneath it are protected individually",
            ["DirectorInstances"] = InsideConfig,
            ["Output"] = InsideAUserFolder,
            ["Logs"] = DataTheMissionReportsAndNeverOffers,
            ["TurnLog"] = DataTheMissionReportsAndNeverOffers,
            ["StateChanges"] = DataTheMissionReportsAndNeverOffers,
            ["TurnDetectionShadow"] = DataTheMissionReportsAndNeverOffers,
            ["ActivityOutbox"] = DataTheMissionReportsAndNeverOffers,
            ["VoiceUtterances"] = DataTheMissionReportsAndNeverOffers,
            ["VoiceTurnLogs"] = DataTheMissionReportsAndNeverOffers,
            ["TurnReviewLogs"] = DataTheMissionReportsAndNeverOffers,
            ["VoiceTurnArchive"] = DataTheMissionReportsAndNeverOffers,
            ["VoiceTurnUploads"] = DataTheMissionReportsAndNeverOffers,
            ["DictationUploads"] = DataTheMissionReportsAndNeverOffers,
            ["BriefFeedback"] = DataTheMissionReportsAndNeverOffers,
            ["Bin"] = TheInstalledProduct,
            ["PythonRuntime"] = TheInstalledProduct,
            ["AgentPlugins"] = DataTheMissionReportsAndNeverOffers,
            ["ClaudeHooks"] = DataTheMissionReportsAndNeverOffers,
            ["CodexHooks"] = DataTheMissionReportsAndNeverOffers,
            ["InjectedTextCache"] = InsideConfig,
            ["WorkflowIndexCache"] = InsideConfig,
            ["SkillIndexCache"] = InsideConfig,
            ["Dictation"] = DataTheMissionReportsAndNeverOffers,
            ["DictationDictionary"] = DataTheMissionReportsAndNeverOffers,
            ["DictationRecordings"] = DataTheMissionReportsAndNeverOffers,
            ["DictationSessions"] = DataTheMissionReportsAndNeverOffers,
            ["DictationCorpus"] = DataTheMissionReportsAndNeverOffers,
            ["PiPreamble"] = DataTheMissionReportsAndNeverOffers,
            ["SessionPreambles"] = DataTheMissionReportsAndNeverOffers,
            ["SessionPointers"] = DataTheMissionReportsAndNeverOffers,
            ["SessionRecordings"] = DataTheMissionReportsAndNeverOffers,
            ["Transcripts"] = DataTheMissionReportsAndNeverOffers,
            ["PromptLog"] = DataTheMissionReportsAndNeverOffers,
            ["TranscriptionHistory"] = DataTheMissionReportsAndNeverOffers,
            ["TranscriptionAudio"] = DataTheMissionReportsAndNeverOffers,
            ["VoiceTestClips"] = DataTheMissionReportsAndNeverOffers,
            ["MicrophoneQuality"] = DataTheMissionReportsAndNeverOffers,
            ["TerminalCaptures"] = DataTheMissionReportsAndNeverOffers,
            ["WorktreeReservations"] = DataTheMissionReportsAndNeverOffers,
            ["WorktreeLeftovers"] = DataTheMissionReportsAndNeverOffers,
            ["WebView2Card"] = DataTheMissionReportsAndNeverOffers,
            ["VaultDb"] = InsideVault,
            ["EngineDb"] = InsideVault,
            ["QuickActionsDb"] = InsideVault,
            ["VaultDocuments"] = InsideVault,
            ["VaultTranscripts"] = InsideVault,
            ["VaultVectors"] = InsideVault,
            ["VaultMedia"] = InsideVault,
            ["VaultHealth"] = InsideVault,
            ["VaultBackups"] = InsideVault,
            ["VaultHandovers"] = InsideVault,
            ["VaultLife"] = InsideVault,
            ["ConfigJson"] = InsideConfig,
            ["Screenshots"] = InsideAUserFolder,
            ["CommQueueDb"] = InsideConfig,
            ["Workspaces"] = InsideConfig,
            ["NamedSessions"] = InsideConfig,
            ["ConnectionsRegistry"] = InsideConnections
        };

    [Fact]
    public void every_public_static_path_member_is_either_protected_or_explicitly_not_protected()
    {
        var protectedSources = ProtectedPaths().Select(entry => entry.Source).ToHashSet();
        var decided = new List<string>();
        var undecided = new List<string>();

        foreach (var member in PathMembers())
        {
            if (protectedSources.Contains(member.Name) || NotProtected.ContainsKey(member.Name))
                decided.Add(member.Name);
            else
                undecided.Add(member.Name);
        }

        Assert.True(undecided.Count == 0,
            "These public static path members of CcStorage are in neither the protected list " +
            "(CcStorage.ProtectedPaths) nor the explicit not-protected list in this test. The reclaim " +
            "tool's first refusal reads that protected list, so a member that is in neither is " +
            "unprotected by default and nobody decided that. Add it to CcStorage.ProtectedPaths when " +
            "it holds credentials or anything else nothing may ever touch, or add it here with its " +
            "reason:\n  " + string.Join("\n  ", undecided));
    }

    [Fact]
    public void protected_paths_are_declared_for_methods_that_exist_and_resolve_to_their_paths()
    {
        var members = PathMembers().ToDictionary(member => member.Name, member => member);

        foreach (var entry in ProtectedPaths())
        {
            Assert.True(members.ContainsKey(entry.Source),
                $"CcStorage.ProtectedPaths names '{entry.Source}', which is not a public static path " +
                "member of CcStorage. A protected entry that names nothing protects nothing.");

            // The entry must compose from the live member rather than restate a path: this is what
            // makes the list "read from the resolver, never typed". All the protected members are
            // pure path compositions, so invoking them here touches nothing on the disk.
            var resolved = (string?)members[entry.Source].Invoke(null, null) ?? string.Empty;
            Assert.True(
                string.Equals(resolved, entry.Path, StringComparison.OrdinalIgnoreCase),
                $"CcStorage.ProtectedPaths carries '{entry.Path}' for {entry.Source}, but the member " +
                $"itself resolves to '{resolved}'. The protected list must call the members, not " +
                "restate their paths - a restated path goes stale the day the member moves.");
        }
    }

    [Fact]
    public void protected_paths_contains_the_four_paths_the_ruling_names()
    {
        var sources = ProtectedPaths().Select(entry => entry.Source).ToHashSet();

        // The Delivery Lead's ruling on the phase 3 plan names these four as the minimum, after
        // checking CcStorage on main and finding that a name test would have missed two of them.
        Assert.Contains("Vault", sources);
        Assert.Contains("SecretsStore", sources);
        Assert.Contains("Config", sources);
        Assert.Contains("KeyVaultFile", sources);
    }

    [Fact]
    public void not_protected_list_has_no_stale_entries()
    {
        var members = PathMembers().Select(member => member.Name).ToHashSet();

        var stale = NotProtected.Keys.Where(name => !members.Contains(name)).ToList();

        Assert.True(stale.Count == 0,
            "These entries of the not-protected list name members CcStorage no longer has. Delete " +
            "them so the list only ever names real decisions:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void protected_paths_has_no_duplicate_sources()
    {
        var duplicates = ProtectedPaths()
            .GroupBy(entry => entry.Source)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            "CcStorage.ProtectedPaths names these members twice:\n  " + string.Join("\n  ", duplicates));
    }

    /// <summary>The protected list, resolved once per call so a test never holds a stale copy of it.</summary>
    private static IReadOnlyList<CcStorage.ProtectedPath> ProtectedPaths() => CcStorage.ProtectedPaths();

    /// <summary>
    /// Every public static method of CcStorage that takes no parameters and returns a string - the
    /// complete set of independently named storage paths, as explained on the class.
    /// </summary>
    private static IReadOnlyList<MethodInfo> PathMembers() =>
        typeof(CcStorage)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method =>
                method.ReturnType == typeof(string) &&
                method.GetParameters().Length == 0 &&
                !method.IsGenericMethod)
            .ToList();
}
