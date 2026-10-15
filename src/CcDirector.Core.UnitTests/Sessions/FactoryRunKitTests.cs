using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE DIRECTOR'S KIT WRITER (Factory Definition mission, phase 5, design section 4): a fresh copy every time, the
/// caller's extra files beside the kit, a restored seat refreshed in place with its work/ kept, a path from outside the
/// kit root never trusted, and the folder gone when the session ends.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryRunKitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dt-factory-run-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private static FactoryKitResponse Kit(params (string Path, string Text)[] files) => new()
    {
        Factory = "devthrottle",
        Seat = "outreach",
        Version = 3,
        Kind = FactoryKits.Seat,
        Files = files.Select(f => new FactoryKitFileDto { Path = f.Path, Text = f.Text }).ToList(),
    };

    private static FactoryKitResponse TheKit() => Kit(
        ("kit.json", "{}\n"), ("rules.json", "{\"permissions\":{}}\n"), ("brief.md", "Find prospects.\n"),
        ("files/outreach_send.py", "print('sent')\n"), ("claude-plugin/skills/mail-voice/SKILL.md", "Write plainly.\n"));

    [Fact]
    public void Write_ASeatKit_PutsEveryFileUnderTheSessionsFolderAndCreatesWork()
    {
        var id = Guid.NewGuid();

        var dir = FactoryRunKit.Write(id, TheKit(), root: _root);

        Assert.Equal(Path.Combine(_root, id.ToString()), dir);
        Assert.Equal("Find prospects.\n", File.ReadAllText(Path.Combine(dir, "brief.md")));
        Assert.Equal("print('sent')\n", File.ReadAllText(Path.Combine(dir, "files", "outreach_send.py")));
        Assert.Equal("Write plainly.\n", File.ReadAllText(Path.Combine(dir, "claude-plugin", "skills", "mail-voice", "SKILL.md")));
        Assert.True(Directory.Exists(Path.Combine(dir, "work")));
    }

    [Fact]
    public void Write_ALeftoverFolderWithTheSameId_IsReplacedByAFreshCopy()
    {
        var id = Guid.NewGuid();
        var dir = FactoryRunKit.DirectoryFor(id, _root);
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", "gone.py"), "a file the definition no longer has");

        FactoryRunKit.Write(id, TheKit(), root: _root);

        Assert.False(File.Exists(Path.Combine(dir, "files", "gone.py")));
        Assert.True(File.Exists(Path.Combine(dir, "brief.md")));
    }

    [Fact]
    public void Write_ExtraFilesFromTheCaller_AreWrittenBesideTheKit()
    {
        // Phase 5b writes check.json through this writer.
        var dir = FactoryRunKit.Write(Guid.NewGuid(), TheKit(),
            new[] { new FactoryKitFileDto { Path = "check.json", Text = "{\"count\": 2}\n" } }, root: _root);

        Assert.Equal("{\"count\": 2}\n", File.ReadAllText(Path.Combine(dir, "check.json")));
        Assert.True(File.Exists(Path.Combine(dir, "kit.json")));
    }

    [Fact]
    public void Write_AnExtraFileThatTakesAKitPath_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.Write(Guid.NewGuid(), TheKit(),
            new[] { new FactoryKitFileDto { Path = "brief.md", Text = "another brief" } }, root: _root));

        Assert.Equal("The kit names 'brief.md' twice, so it was not written.", ex.Message);
    }

    [Theory]
    [InlineData("../outside.md", "The kit file '../outside.md' has an empty, '.' or '..' part, so the kit was not written.")]
    [InlineData("files/../../outside.md", "The kit file 'files/../../outside.md' has an empty, '.' or '..' part, so the kit was not written.")]
    [InlineData("/etc/passwd", "The kit file '/etc/passwd' is not a relative path with forward slashes, so the kit was not written.")]
    [InlineData("files\\x.py", "The kit file 'files\\x.py' is not a relative path with forward slashes, so the kit was not written.")]
    [InlineData("work/notes.md", "The kit file 'work/notes.md' is inside work/, which is the seat's own folder, so the kit was not written.")]
    public void Write_AFilePathThatLeavesTheFolderOrEntersWork_IsRefusedAndNothingIsWritten(string path, string refusal)
    {
        var id = Guid.NewGuid();

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.Write(id, Kit((path, "x")), root: _root));

        Assert.Equal(refusal, ex.Message);
        Assert.False(Directory.Exists(FactoryRunKit.DirectoryFor(id, _root)));
    }

    [Fact]
    public void Refresh_ARestoredSeatsFolder_RewritesTheKitInPlaceAndKeepsWork()
    {
        var id = Guid.NewGuid();
        var dir = FactoryRunKit.Write(id, TheKit(), root: _root);
        File.WriteAllText(Path.Combine(dir, "work", "draft.md"), "what the seat was writing");
        File.WriteAllText(Path.Combine(dir, "brief.md"), "an edit an agent made to its copy");
        File.WriteAllText(Path.Combine(dir, "stray.txt"), "not part of the kit");

        var again = FactoryRunKit.Refresh(dir, TheKit(), root: _root);

        Assert.Equal(dir, again);
        Assert.Equal("what the seat was writing", File.ReadAllText(Path.Combine(dir, "work", "draft.md")));
        Assert.Equal("Find prospects.\n", File.ReadAllText(Path.Combine(dir, "brief.md")));
        Assert.False(File.Exists(Path.Combine(dir, "stray.txt")));
    }

    [Fact]
    public void Refresh_KeepsTheChecksFileBesideWork()
    {
        const string check = "{\"count\": 2}\n";
        var dir = FactoryRunKit.Write(Guid.NewGuid(), TheKit(),
            new[] { new FactoryKitFileDto { Path = FactoryRunKit.CheckFileName, Text = check } }, root: _root);

        FactoryRunKit.Refresh(dir, TheKit(), root: _root);

        Assert.Equal(check, File.ReadAllText(Path.Combine(dir, FactoryRunKit.CheckFileName)));
    }

    [Fact]
    public void Refresh_AFolderThatIsGone_IsWrittenAgainWithAnEmptyWork()
    {
        var dir = FactoryRunKit.DirectoryFor(Guid.NewGuid(), _root);

        FactoryRunKit.Refresh(dir, TheKit(), root: _root);

        Assert.True(File.Exists(Path.Combine(dir, "brief.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir, "work")));
    }

    [Fact]
    public void WhyNotAKitFolder_AFolderDirectlyUnderTheRoot_IsAccepted()
        => Assert.Null(FactoryRunKit.WhyNotAKitFolder(Path.Combine(_root, Guid.NewGuid().ToString()), _root));

    [Fact]
    public void WhyNotAKitFolder_AFolderOutsideTheRoot_IsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", Guid.NewGuid().ToString());

        var refusal = FactoryRunKit.WhyNotAKitFolder(outside, _root);

        Assert.Equal($"The kit folder '{outside}' is not directly under {Path.GetFullPath(_root)}, where this Director keeps every seat's kit.", refusal);
    }

    [Fact]
    public void WhyNotAKitFolder_AFolderThatClimbsOutWithDotDot_IsRefused()
    {
        var climbing = Path.Combine(_root, "..", "..", "Windows");

        var refusal = FactoryRunKit.WhyNotAKitFolder(climbing, _root);

        Assert.Equal($"The kit folder '{climbing}' climbs with '..' or '.', so it is not trusted; a seat's kit folder is always a folder directly under {Path.GetFullPath(_root)}.", refusal);
    }

    [Fact]
    public void WhyNotAKitFolder_TheRootItselfOrADeeperFolder_IsRefused()
    {
        Assert.NotNull(FactoryRunKit.WhyNotAKitFolder(_root, _root));
        Assert.NotNull(FactoryRunKit.WhyNotAKitFolder(Path.Combine(_root, Guid.NewGuid().ToString(), "work"), _root));
    }

    [Fact]
    public void Delete_AKitFolder_RemovesItWithItsWork()
    {
        var dir = FactoryRunKit.Write(Guid.NewGuid(), TheKit(), root: _root);
        File.WriteAllText(Path.Combine(dir, "work", "draft.md"), "x");

        FactoryRunKit.Delete(dir, _root);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_AFolderOutsideTheRoot_IsNeverTouched()
    {
        var outside = Directory.CreateTempSubdirectory("dt-not-a-kit-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.md"), "a person's file");

            FactoryRunKit.Delete(outside, _root);

            Assert.True(File.Exists(Path.Combine(outside, "keep.md")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
