using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.UnitTests.Skills;

/// <summary>
/// THE PLACEMENT RECORD IS WRITTEN WHOLE, AND A DAMAGED ONE HEALS (issue #4552).
///
/// The record was written with <c>File.WriteAllText</c>, which truncates and then writes. The writers are
/// PROCESSES, not just threads - two Directors on one root, or a Director and a session with a temporary
/// home - so the in-process lock did not cover them, and a second write shorter than the first left the
/// tail of the first behind it. One file then held two JSON documents, every later read failed on it, and
/// because the failing read happened inside the write path the file could never be rewritten: about 2,000
/// reports came off one machine in three days, none of which changed anything.
///
/// Every test here uses a throwaway root of its own, so none of them can see the machine's real record.
/// </summary>
public class SkillPlacementLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "placement-" + Guid.NewGuid().ToString("N"));

    private string RecordPath => Path.Combine(_root, "skills", "placement-state.json");

    private static SkillPlacement APlacement(AgentKind kind, int held, int reachable) =>
        new(kind, held, reachable, [], StoreMissing: false, AgentHasNoSkillsDirectory: false);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* a throwaway */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ARecordedPlacement_IsReadBack()
    {
        SkillPlacementLog.Record(APlacement(AgentKind.ClaudeCode, held: 7, reachable: 7), _root);

        var all = SkillPlacementLog.ReadAll(_root);

        var only = Assert.Single(all);
        Assert.Equal(nameof(AgentKind.ClaudeCode), only.AgentKind);
        Assert.Equal(7, only.Held);
        Assert.Equal(7, only.Reachable);
    }

    [Fact]
    public void TheFileHoldsExactlyOneJsonDocument_AfterALongRecordIsReplacedByAShortOne()
    {
        // The shape of the cross-process tear, reproduced in one process: a big record, then a smaller one.
        // A truncating write does this correctly on its own; what it cannot do is survive another process
        // writing at the same moment, which is why the write is now a rename of a complete file.
        SkillPlacementLog.Record(APlacement(AgentKind.ClaudeCode, held: 99, reachable: 99), _root);
        SkillPlacementLog.Record(APlacement(AgentKind.Pi, held: 1, reachable: 0), _root);

        var text = File.ReadAllText(RecordPath);

        using var document = JsonDocument.Parse(text);          // throws if a second document follows
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal(2, SkillPlacementLog.ReadAll(_root).Count);
    }

    [Fact]
    public void ARecordThatIsAlreadyTwoDocuments_IsReplacedByTheNextPlacement_NotReportedForEver()
    {
        // This is the file real machines are left holding. Before the fix the read threw inside the write
        // path, the write never happened, and the next placement found the same damage - for ever.
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        File.WriteAllText(RecordPath, """[{"AgentKind":"ClaudeCode","Held":9}][{"AgentKind":"Pi","Held":1}]""");

        Assert.Empty(SkillPlacementLog.ReadAll(_root));          // unreadable, and tolerated

        SkillPlacementLog.Record(APlacement(AgentKind.Codex, held: 4, reachable: 4), _root);

        var all = SkillPlacementLog.ReadAll(_root);
        var only = Assert.Single(all);
        Assert.Equal(nameof(AgentKind.Codex), only.AgentKind);
        Assert.Equal(4, only.Held);
    }

    [Fact]
    public void ARecordThatIsNotJsonAtAll_IsAlsoReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        File.WriteAllText(RecordPath, "half a line of something else");

        SkillPlacementLog.Record(APlacement(AgentKind.Gemini, held: 2, reachable: 1), _root);

        var only = Assert.Single(SkillPlacementLog.ReadAll(_root));
        Assert.Equal(nameof(AgentKind.Gemini), only.AgentKind);
    }

    [Fact]
    public void AWriteLeavesNoTemporaryFileBehindIt()
    {
        SkillPlacementLog.Record(APlacement(AgentKind.ClaudeCode, held: 3, reachable: 3), _root);

        var beside = Directory.GetFiles(Path.GetDirectoryName(RecordPath)!);

        Assert.Equal([RecordPath], beside);
    }
}
