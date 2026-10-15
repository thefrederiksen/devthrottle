using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A SEAT'S BRIEF WRITTEN INTO ITS SEAT FILE AS A TEXT EDIT (Factory Definition, phase 4, design section 9). Every test
/// asserts on the WHOLE edited file, so a byte changed anywhere but the brief's value fails it, and the edited
/// definition is held to the whole-definition check.
/// </summary>
public sealed class SeatBriefTextTests
{
    private const string Path = "seats/writer.yaml";

    /// <summary>A seat file with comments, keys before and after the brief, and a block brief with a note on its header.</summary>
    private const string Commented = """
        # The writer seat. Keep this comment.
        seat: writer
        name: Writer   # shown on the floor
        role: Writer
        brief: |  # what the writer is told
          Write the posts.
          Keep them short.

        # How it starts.
        starts:
          - cron: "0 9 * * 1"
            timeZone: Europe/Copenhagen
            prompt: Weekly post.
        rules: [base]
        """;

    [Fact]
    public void Replace_BlockBriefInACommentedFile_ChangesOnlyTheValue()
    {
        var edit = SeatBriefText.Replace(Path, Commented.ReplaceLineEndings("\n"), "Write one post.\nMake it good.\n");

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("""
            # The writer seat. Keep this comment.
            seat: writer
            name: Writer   # shown on the floor
            role: Writer
            brief: |  # what the writer is told
              Write one post.
              Make it good.

            # How it starts.
            starts:
              - cron: "0 9 * * 1"
                timeZone: Europe/Copenhagen
                prompt: Weekly post.
            rules: [base]
            """.ReplaceLineEndings("\n"), edit.Text);
    }

    [Fact]
    public void Replace_OneLineBrief_BecomesALiteralBlockAndTheRestIsKept()
    {
        var file = "seat: writer\nname: Writer\nrole: Writer\nbrief: Write the posts. # one line\nrules: [base]\n";

        var edit = SeatBriefText.Replace(Path, file, "Write the posts.\nThen tell the boss.\n");

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("seat: writer\nname: Writer\nrole: Writer\nbrief: | # one line\n  Write the posts.\n  Then tell the boss.\nrules: [base]\n",
            edit.Text);
    }

    [Fact]
    public void Replace_QuotedBriefThatIsTheLastKeyWithNoFinalLineEnd_IsReplaced()
    {
        var file = "seat: writer\nname: Writer\nrole: Writer\nbrief: \"Write the posts.\"";

        var edit = SeatBriefText.Replace(Path, file, "New text.\n");

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("seat: writer\nname: Writer\nrole: Writer\nbrief: |\n  New text.\n", edit.Text);
    }

    [Fact]
    public void Replace_ANestedBriefKey_IsNotTouched()
    {
        // A key named brief inside another mapping is not the seat's brief: only the top-level one is replaced.
        var file = "seat: writer\nnotes:\n  brief: inner\nbrief: outer\nname: Writer\n";

        var edit = SeatBriefText.Replace(Path, file, "outer, rewritten\n");

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("seat: writer\nnotes:\n  brief: inner\nbrief: |\n  outer, rewritten\nname: Writer\n", edit.Text);
    }

    [Theory]
    [InlineData("No final line end", "|-")]
    [InlineData("One final line end\n", "|")]
    [InlineData("Two final line ends\n\n", "|+")]
    [InlineData("  Starts with spaces\n", "|2")]
    public void Replace_TheTextsOwnEndsAndIndentation_ReadBackExactly(string brief, string header)
    {
        var edit = SeatBriefText.Replace(Path, "seat: writer\nbrief: old\nname: Writer\n", brief);

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Contains($"brief: {header}\n", edit.Text);
        Assert.Equal(brief, SeatBriefText.Read(Path, edit.Text!));
    }

    [Fact]
    public void Replace_AFileWrittenWithCarriageReturns_WritesItsLinesWithThem()
    {
        var file = "seat: writer\r\nbrief: |\r\n  old\r\nname: Writer\r\n";

        var edit = SeatBriefText.Replace(Path, file, "line one\r\nline two\r\n");

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("seat: writer\r\nbrief: |\r\n  line one\r\n  line two\r\nname: Writer\r\n", edit.Text);
        Assert.Equal("line one\nline two\n", SeatBriefText.Read(Path, edit.Text!));
    }

    [Fact]
    public void Replace_TheEditedDefinition_PassesTheWholeCheck()
    {
        var content = Valid();

        var boss = SeatBriefText.Replace("seats/boss.yaml", content["seats/boss.yaml"], "Run the factory.\nEmail the owner on Fridays.\n");
        var writer = SeatBriefText.Replace("seats/writer.yaml", content["seats/writer.yaml"], "Write two posts a week.");
        content["seats/boss.yaml"] = boss.Text!;
        content["seats/writer.yaml"] = writer.Text!;

        var result = Check(content);
        Assert.False(result.HasErrors, string.Join("\n", result.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
        Assert.Equal("Run the factory.\nEmail the owner on Fridays.\n", result.Definition!.Seats.Single(s => s.Seat == "boss").Brief);
        Assert.Equal("Write two posts a week.", result.Definition.Seats.Single(s => s.Seat == "writer").Brief);
    }

    [Fact]
    public void Replace_AnEmptyText_IsRefused()
    {
        var edit = SeatBriefText.Replace(Path, "seat: writer\nbrief: old\n", "  \n ");

        Assert.False(edit.Ok);
        Assert.Equal("a brief needs text: a seat told nothing has nothing to do", edit.Refusal);
    }

    [Fact]
    public void Replace_AFileWithNoBrief_IsRefusedInWords()
    {
        var edit = SeatBriefText.Replace(Path, "seat: writer\nname: Writer\n", "text\n");

        Assert.False(edit.Ok);
        Assert.Equal("seats/writer.yaml has no brief: at its top level that this edit can replace. Edit the definition itself.", edit.Refusal);
    }

    [Fact]
    public void Replace_ABriefThatIsAList_IsRefusedInWords()
    {
        var edit = SeatBriefText.Replace(Path, "seat: writer\nbrief:\n  - one\n  - two\nname: Writer\n", "text\n");

        Assert.False(edit.Ok);
        Assert.Equal("The brief in seats/writer.yaml is not one text value, so it cannot be replaced as one. Edit the definition itself.", edit.Refusal);
    }

    [Fact]
    public void Read_ABlockBrief_IsItsTextAsYamlReadsIt()
    {
        Assert.Equal("Write the posts.\nKeep them short.\n", SeatBriefText.Read(Path, Commented.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void Read_AFileWithNoBrief_IsNull()
    {
        Assert.Null(SeatBriefText.Read(Path, "seat: writer\n"));
    }
}
