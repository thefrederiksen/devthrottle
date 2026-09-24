using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// Which directories the Director must keep its hands off, read from cc-worktrees' OWN records and
/// from the layout that tool creates. Nothing here is a second registry: every marker these tests
/// exercise is written by the pool itself.
///
/// The case that decides whether work survives is the LAST one: records that exist and cannot be read
/// must be an ERROR, never an empty answer. An empty answer reaches a destructive caller looking
/// exactly like "none of these belong to a pool".
/// </summary>
public sealed class CcWorktreesPoolSlotsTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;

    public CcWorktreesPoolSlotsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-poolslots-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "cc-worktrees-home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- the layout signal: works with no records at all --------------------------------------

    [Theory]
    [InlineData(@"D:\Repos\devthrottle.worktrees\wt01", true)]
    [InlineData(@"D:\Repos\devthrottle.worktrees\wt02", true)]
    [InlineData(@"D:\Repos\devthrottle.worktrees\wt123", true)]
    [InlineData(@"D:\Repos\devthrottle.worktrees\wt1", false)]      // the tool writes at least two digits
    [InlineData(@"D:\Repos\devthrottle.worktrees\scratch", false)]  // not a slot name
    [InlineData(@"D:\Repos\devthrottle-wt01", false)]               // a hand-made worktree beside the repo
    [InlineData(@"D:\Repos\worktrees\wt01", false)]                 // no <name>.worktrees parent
    [InlineData(@"D:\Repos\devthrottle", false)]
    public void HasSlotLayout_RecognisesOnlyTheShapeThePoolCreates(string path, bool expected)
    {
        Assert.Equal(expected, CcWorktreesPoolSlots.HasSlotLayout(ForThisPlatform(path)));
    }

    /// <summary>
    /// The table above is written in Windows spelling because that is where the pool tool runs. The SHAPE
    /// it describes - a <c>wtNN</c> leaf inside a <c>&lt;name&gt;.worktrees</c> parent - is not
    /// platform-specific, but the reader of it is: <c>HasSlotLayout</c> goes through
    /// <c>Path.GetFileName</c> and <c>Path.GetDirectoryName</c>, which only recognise the HOST's
    /// separator. On macOS a backslash is an ordinary character, so every row collapsed to a single
    /// file name with no parent and three of them failed - a test of path shape defeated by path
    /// spelling. Translating the spelling keeps one table asserting one property on both platforms.
    /// </summary>
    private static string ForThisPlatform(string windowsPath) =>
        OperatingSystem.IsWindows()
            ? windowsPath
            : "/" + windowsPath.Replace(@"D:\", string.Empty).Replace('\\', '/');

    [Fact]
    public void Owns_ASlotByItsLayout_EvenWithNoRecordsOnThisMachine()
    {
        // The records are the tool's account of itself; the layout is the thing on disk. A machine
        // whose records were lost still has the slots, and this is the moment they are most at risk.
        var slots = new CcWorktreesPoolSlots(_home);

        Assert.True(slots.Owns(Path.Combine(_root, "primary.worktrees", "wt01")));
        Assert.Empty(slots.RecordedSlotDirectories());
    }

    // ---- the records signal --------------------------------------------------------------------

    [Fact]
    public void Owns_ASlotTheRecordsName_EvenWhenItsPathHasNoPoolShape()
    {
        var slot = Path.Combine(_root, "somewhere-else", "wt-a");
        WriteRegistry(Path.Combine(_root, "primary"));
        WriteState("primary", Path.Combine(_root, "primary"), ("wt01", slot));

        var slots = new CcWorktreesPoolSlots(_home);

        Assert.False(CcWorktreesPoolSlots.HasSlotLayout(slot));
        Assert.True(slots.Owns(slot));
    }

    [Fact]
    public void Owns_SomethingInsideASlot()
    {
        var slot = Path.Combine(_root, "somewhere-else", "wt-a");
        WriteRegistry(Path.Combine(_root, "primary"));
        WriteState("primary", Path.Combine(_root, "primary"), ("wt01", slot));

        Assert.True(new CcWorktreesPoolSlots(_home).Owns(Path.Combine(slot, "src", "Program.cs")));
    }

    [Fact]
    public void Owns_APlainWorktree_IsFalse()
    {
        WriteRegistry(Path.Combine(_root, "primary"));
        WriteState("primary", Path.Combine(_root, "primary"), ("wt01", Path.Combine(_root, "primary.worktrees", "wt01")));

        Assert.False(new CcWorktreesPoolSlots(_home).Owns(Path.Combine(_root, "devthrottle-somefeature")));
    }

    [Fact]
    public void RecordedSlotDirectories_NamesEachRegisteredRepositorysSlotsDirectory()
    {
        // Registered but with no slots in its state yet: the DIRECTORY still has to be known, because a
        // slot is created on disk before the state that names it is saved.
        var repo = Path.Combine(_root, "primary");
        WriteRegistry(repo);

        var slots = new CcWorktreesPoolSlots(_home);

        Assert.True(slots.Owns(Path.Combine(_root, "primary.worktrees", "anything-at-all")));
        Assert.Contains(
            WorktreeReaperService.NormalizePath(Path.Combine(_root, "primary.worktrees")),
            slots.RecordedSlotDirectories());
    }

    [Fact]
    public void NoStateHomeAtAll_IsAPositiveNo()
    {
        // No pool was ever made on this machine. That is knowable, and it must not be an error - it is
        // the ordinary case for every machine that does not use the tool.
        var slots = new CcWorktreesPoolSlots(Path.Combine(_root, "never-created"));

        Assert.Empty(slots.RecordedSlotDirectories());
        Assert.False(slots.Owns(Path.Combine(_root, "devthrottle-somefeature")));
    }

    // ---- cannot tell is not no -----------------------------------------------------------------

    [Fact]
    public void RecordsThatCannotBeRead_Throw_RatherThanAnsweringEmpty()
    {
        File.WriteAllText(Path.Combine(_home, "registry.json"), "{ this is not json");

        var slots = new CcWorktreesPoolSlots(_home);

        var ex = Assert.Throws<CcWorktreesStateUnreadableException>(() => slots.RecordedSlotDirectories());
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void APoolStateFileThatCannotBeRead_Throws()
    {
        WriteRegistry(Path.Combine(_root, "primary"));
        Directory.CreateDirectory(Path.Combine(_home, "pools"));
        File.WriteAllText(Path.Combine(_home, "pools", "broken.json"), "]]not json[[");

        Assert.Throws<CcWorktreesStateUnreadableException>(() => new CcWorktreesPoolSlots(_home).RecordedSlotDirectories());
    }

    [Fact]
    public void ASlotByItsLayout_IsOwnedEvenWhenTheRecordsCannotBeRead()
    {
        // The layout check runs first and needs nothing on disk, so the one signal that survives a
        // broken state file is the one that protects the slot.
        File.WriteAllText(Path.Combine(_home, "registry.json"), "{ this is not json");

        Assert.True(new CcWorktreesPoolSlots(_home).Owns(Path.Combine(_root, "primary.worktrees", "wt01")));
    }

    private void WriteRegistry(params string[] repos)
    {
        var quoted = string.Join(", ", repos.Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
        File.WriteAllText(Path.Combine(_home, "registry.json"), $$"""{"version": 1, "repos": [{{quoted}}]}""");
    }

    private void WriteState(string name, string repo, params (string Slot, string Path)[] slots)
    {
        Directory.CreateDirectory(Path.Combine(_home, "pools"));
        var entries = string.Join(", ", slots.Select(s =>
            $"{System.Text.Json.JsonSerializer.Serialize(s.Slot)}: {{\"path\": {System.Text.Json.JsonSerializer.Serialize(s.Path)}, \"state\": \"free\"}}"));
        var json = $"{{\"version\": 4, \"repo\": {System.Text.Json.JsonSerializer.Serialize(repo)}, \"slots\": {{{entries}}}}}";
        File.WriteAllText(Path.Combine(_home, "pools", name + "-abcdef123456.json"), json);
    }
}
