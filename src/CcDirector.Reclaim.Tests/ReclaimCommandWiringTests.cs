using CcCleanupStorage;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The one collection for tests that change this process's own environment variables. A variable is
/// shared by every test in the process, so a test that changes one must never run beside another
/// test: every fixture tree is made under the temporary folder the variable names, and a test that
/// moved it mid-run would send a neighbour's tree somewhere it did not ask for.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChangesTheProcessEnvironment
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Tests that change this process's environment";
}

/// <summary>
/// The reclaim command hands the gate the REAL protected paths and the REAL user folders.
///
/// The ten numbered refusal tests all build the runner's request by hand, with their own protected
/// list and their own user folders, so not one of them can see what the command line passes. Proven
/// on 19 September 2026: with the command's protected list replaced by an empty one, and then with
/// its user folders replaced by an empty list, all 253 tests stayed green. A refusal the gate holds
/// perfectly is worth nothing when its caller hands it an empty list, and these two tests are the
/// ones that watch the caller.
///
/// Each drives the same two calls the tool's entry point makes - the command line reader, then the
/// runner - against a fixture tree. The storage root, the temporary folder and the OneDrive folder
/// are pointed INTO the fixture by the environment variables the product already reads, so the rule
/// set this machine really runs finds a fixture item and nothing else. The apply flag is never
/// passed: both runs are dry runs, and each test proves afterwards that nothing moved.
/// </summary>
[Collection(ChangesTheProcessEnvironment.Name)]
public sealed class ReclaimCommandWiringTests
{
    [Fact]
    public void ReclaimCommand_AnItemUnderTheRealResolversConfigFolder_IsRefusedByRefusalOne()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "This test drives the Windows rule set, which is the only rule set that exists. " +
                "It says so rather than passing on a platform where the command has no rules to run.");

        using var tree = new FixtureTree(nameof(ReclaimCommand_AnItemUnderTheRealResolversConfigFolder_IsRefusedByRefusalOne));
        using var environment = new EnvironmentChange();

        // The storage root is pointed at the fixture by the variable CcStorage already reads, and the
        // protected folder is then ASKED of the resolver, never composed here.
        environment.Set("CC_DIRECTOR_ROOT", tree.Folder("storage-root"));
        var config = CcStorage.Config();
        Assert.True(
            Rules.RuleSelection.IsInside(config, tree.Root),
            $"the config folder {config} is not inside the fixture tree, so this test will not touch it");

        var temporary = Path.Combine(config, "tmp");
        var item = AnAgedScratchFolder(temporary);
        environment.Set("TMP", temporary);
        environment.Set("TEMP", temporary);

        var answer = RunTheCommand(tree.Root);

        var json = Assert.IsType<ReclaimJson>(answer.JsonPayload);
        Assert.False(json.Apply);
        var refused = Assert.Single(json.Items);
        Assert.Equal(item, refused.Path);
        Assert.False(refused.Eligible, "an item under the config folder must never be eligible");
        Assert.Equal(1, refused.FiredCheck);
        Assert.Contains(config, refused.Reason);
        Assert.False(refused.Moved);

        // Nothing moved: the item is where it was with its bytes.
        Assert.Equal(64, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
    }

    [Fact]
    public void ReclaimCommand_AnItemUnderTheOneDriveFolder_IsRefusedByRefusalThree()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "This test drives the Windows rule set, which is the only rule set that exists. " +
                "It says so rather than passing on a platform where the command has no rules to run.");

        using var tree = new FixtureTree(nameof(ReclaimCommand_AnItemUnderTheOneDriveFolder_IsRefusedByRefusalThree));
        using var environment = new EnvironmentChange();

        // The storage root goes into the fixture as well, so refusal 1 has nothing to say about this
        // item and refusal 3 is reached.
        environment.Set("CC_DIRECTOR_ROOT", tree.Folder("storage-root"));
        var oneDrive = tree.Folder("one-drive");
        environment.Set("OneDrive", oneDrive);

        var temporary = Path.Combine(oneDrive, "tmp");
        var item = AnAgedScratchFolder(temporary);
        environment.Set("TMP", temporary);
        environment.Set("TEMP", temporary);

        var answer = RunTheCommand(tree.Root);

        var json = Assert.IsType<ReclaimJson>(answer.JsonPayload);
        Assert.False(json.Apply);
        var refused = Assert.Single(json.Items);
        Assert.Equal(item, refused.Path);
        Assert.False(refused.Eligible, "an item under the user's OneDrive folder must never be eligible");
        Assert.Equal(3, refused.FiredCheck);
        Assert.Contains(oneDrive, refused.Reason);
        Assert.False(refused.Moved);

        Assert.Equal(64, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
    }

    /// <summary>
    /// A scratch folder by one of the exact names the rule knows, written four hundred days ago, so
    /// the rule this machine really runs offers it against the real clock the command uses.
    /// </summary>
    private static string AnAgedScratchFolder(string temporaryFolder)
    {
        var item = Path.Combine(temporaryFolder, "cc-director-tests");
        Directory.CreateDirectory(item);
        var file = Path.Combine(item, "scratch.txt");
        File.WriteAllBytes(file, new byte[64]);

        var longAgo = DateTime.UtcNow.AddDays(-400);
        File.SetLastWriteTimeUtc(file, longAgo);
        Directory.SetLastWriteTimeUtc(item, longAgo);
        return item;
    }

    /// <summary>
    /// The same two calls the tool's entry point makes, in the same order. The apply flag is not
    /// among the arguments, and the request is checked to be a dry run before it is run.
    /// </summary>
    private static Answer RunTheCommand(string folder)
    {
        var holdingRootBefore = Directory.Exists(DefaultHoldingRoot(folder));

        var outcome = CommandLine.Parse(["reclaim", folder, "--json"], "unused-index-directory");
        Assert.Null(outcome.UsageError);
        var request = Assert.IsType<Request>(outcome.Request);
        Assert.False(request.Apply, "this test must never run the command with the apply flag");

        var answer = Runner.Run(request);

        // A dry run creates no holding root. The command's default sits at the root of the real
        // volume, so this is also the proof that the test wrote nothing outside its own tree.
        Assert.Equal(holdingRootBefore, Directory.Exists(DefaultHoldingRoot(folder)));
        return answer;
    }

    private static string DefaultHoldingRoot(string folder) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(folder))!, "cc-reclaim-holding");

    /// <summary>Sets environment variables for one test and puts every one back when it ends.</summary>
    private sealed class EnvironmentChange : IDisposable
    {
        private readonly Dictionary<string, string?> _before = [];

        public void Set(string name, string value)
        {
            if (!_before.ContainsKey(name))
                _before[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _before)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
