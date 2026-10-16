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
    [InlineData("prompts/input.txt", "The kit file 'prompts/input.txt' is inside prompts/, where the Director saves the prompts it sends the seat, so the kit was not written.")]
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

    // WHAT A CHECK FOUND (phase 5b, design 3.4): check.json beside the kit, listed in kit.json.

    /// <summary>A kit.json shaped as the Gateway composes it: indented, files in ordinal order of their paths.</summary>
    private const string ComposedKitJson =
        "{\n  \"factory\": \"devthrottle\",\n  \"seat\": \"outreach\",\n  \"version\": 3,\n  \"kind\": \"seat\",\n" +
        "  \"files\": [\n" +
        "    {\n      \"path\": \"brief.md\",\n      \"source\": \"definition\",\n      \"from\": [\n        \"seats/outreach/brief.md\"\n      ]\n    },\n" +
        "    {\n      \"path\": \"rules.json\",\n      \"source\": \"definition\",\n      \"from\": []\n    }\n" +
        "  ]\n}\n";

    private static FactoryKitResponse ComposedKit() => Kit(
        ("kit.json", ComposedKitJson), ("rules.json", "{\"permissions\":{}}\n"), ("brief.md", "Find prospects.\n"));

    private static FactoryCheckDto TheCheck() => new()
    {
        CheckPath = "checks/new_mail.py",
        Count = 3,
        Context = "three unread mails from customers",
        CheckedUtc = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc),
        DefinitionVersion = 3,
        Seat = "outreach",
        Start = 2,
    };

    private const string TheCheckJson =
        "{\n  \"checkPath\": \"checks/new_mail.py\",\n  \"count\": 3,\n  \"context\": \"three unread mails from customers\",\n" +
        "  \"checkedUtc\": \"2026-10-15T08:00:00Z\",\n  \"definitionVersion\": 3,\n  \"seat\": \"outreach\",\n  \"start\": 2\n}\n";

    [Fact]
    public void WithCheck_AKit_WritesCheckJsonWithExactlyWhatTheCheckFound()
    {
        var dir = FactoryRunKit.Write(Guid.NewGuid(), FactoryRunKit.WithCheck(ComposedKit(), TheCheck()), root: _root);

        Assert.Equal(TheCheckJson, File.ReadAllText(Path.Combine(dir, FactoryRunKit.CheckFileName)));
        Assert.Equal("Find prospects.\n", File.ReadAllText(Path.Combine(dir, "brief.md")));
    }

    [Fact]
    public void WithCheck_AKit_ListsCheckJsonInKitJsonInPathOrderWithTheCheckItCameFrom()
    {
        var kit = FactoryRunKit.WithCheck(ComposedKit(), TheCheck());

        var kitJson = kit.Files.Single(f => f.Path == "kit.json").Text;
        Assert.Equal(
            "{\n  \"factory\": \"devthrottle\",\n  \"seat\": \"outreach\",\n  \"version\": 3,\n  \"kind\": \"seat\",\n" +
            "  \"files\": [\n" +
            "    {\n      \"path\": \"brief.md\",\n      \"source\": \"definition\",\n      \"from\": [\n        \"seats/outreach/brief.md\"\n      ]\n    },\n" +
            "    {\n      \"path\": \"check.json\",\n      \"source\": \"check\",\n      \"from\": [\n        \"checks/new_mail.py\"\n      ]\n    },\n" +
            "    {\n      \"path\": \"rules.json\",\n      \"source\": \"definition\",\n      \"from\": []\n    }\n" +
            "  ]\n}\n", kitJson);
    }

    [Fact]
    public void WithCheck_ANullContext_IsWrittenAsNull()
    {
        var check = TheCheck();
        check.Context = null;

        var text = FactoryRunKit.WithCheck(ComposedKit(), check).Files.Single(f => f.Path == FactoryRunKit.CheckFileName).Text;

        Assert.Contains("\"context\": null,", text);
    }

    [Fact]
    public void WithCheck_AKitWithNoListOfFiles_IsRefusedAndNothingIsWritten()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.WithCheck(TheKit(), TheCheck()));

        Assert.Equal("The kit's kit.json has no list of files to name check.json in, so it was not written.", ex.Message);
    }

    [Fact]
    public void WithCheck_AnEmptyCheckPath_IsRefused()
    {
        var check = TheCheck();
        check.CheckPath = " ";

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.WithCheck(ComposedKit(), check));

        Assert.Equal("What the check found names no check path, so check.json was not written.", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithCheck_ACountOfNothingToDo_IsRefused(int count)
    {
        var check = TheCheck();
        check.Count = count;

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.WithCheck(ComposedKit(), check));

        Assert.Equal($"The check checks/new_mail.py found {count} things to do, and a check starts a seat only when it " +
                     "finds work, so check.json was not written.", ex.Message);
    }

    [Fact]
    public void WithCheck_AKitJsonThatIsNotJson_SaysWhatWasRead()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FactoryRunKit.WithCheck(Kit(("kit.json", "{ not json")), TheCheck()));

        Assert.StartsWith("The kit's kit.json as downloaded could not be read as JSON, so check.json was not listed in it: ", ex.Message);
    }

    [Fact]
    public void ListingKeptCheck_ARestoredFolder_ListsCheckJsonAndLeavesItsBytesAsTheyAre()
    {
        var dir = FactoryRunKit.Write(Guid.NewGuid(), FactoryRunKit.WithCheck(ComposedKit(), TheCheck()), root: _root);
        // What the seat's own agent may have done to the file: the restore neither reads nor rewrites it.
        var kept = new byte[] { 0x7B, 0x20, 0x6E, 0x6F, 0x74, 0x20, 0x6A, 0x73, 0x6F, 0x6E, 0x0D, 0x0A };
        File.WriteAllBytes(Path.Combine(dir, FactoryRunKit.CheckFileName), kept);

        Assert.True(FactoryRunKit.HoldsCheck(dir));
        FactoryRunKit.Refresh(dir, FactoryRunKit.ListingKeptCheck(ComposedKit()), root: _root);

        Assert.Equal(kept, File.ReadAllBytes(Path.Combine(dir, FactoryRunKit.CheckFileName)));
        Assert.Contains("    {\n      \"path\": \"check.json\",\n      \"source\": \"check\",\n      \"from\": []\n    },\n",
            File.ReadAllText(Path.Combine(dir, "kit.json")));
    }

    [Fact]
    public void HoldsCheck_AFolderWithNoCheckJson_IsFalse()
        => Assert.False(FactoryRunKit.HoldsCheck(FactoryRunKit.Write(Guid.NewGuid(), ComposedKit(), root: _root)));

    [Fact]
    public void PromptDirectoryFor_AnEnvironmentNamingAKit_IsThatKitsPromptsFolder()
    {
        var kit = FactoryRunKit.DirectoryFor(Guid.NewGuid(), _root);

        var folder = FactoryRunKit.PromptDirectoryFor(new Dictionary<string, string> { [FactoryRunKit.DirectoryEnvVar] = kit });

        Assert.Equal(Path.Combine(kit, "prompts"), folder);
    }

    [Fact]
    public void PromptDirectoryFor_UNDEFINED_FACTORY_AnEnvironmentNamingNoKit_IsEmpty()
    {
        Assert.Equal("", FactoryRunKit.PromptDirectoryFor(null));
        Assert.Equal("", FactoryRunKit.PromptDirectoryFor(new Dictionary<string, string> { ["CC_SESSION_ID"] = "x" }));
    }
}
