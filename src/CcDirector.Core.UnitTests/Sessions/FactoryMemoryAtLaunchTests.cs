using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// The file name a factory memory note is written under (Factory Memory mission, phase 3a, section 5.4).
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryMemoryAtLaunchTests
{
    [Theory]
    [InlineData("domains", "domains.md")]
    [InlineData("a/b\\c:d", "a_b_c_d.md")]
    [InlineData("index", "note-index.md")]
    [InlineData("CON", "note-CON.md")]
    [InlineData("../../etc", "etc.md")]
    [InlineData("   ", "note.md")]
    public void A_notes_file_name_is_safe_whatever_the_note_is_called(string name, string expected)
        => Assert.Equal(expected, FactoryMemoryFiles.FileNameFor(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public void Two_notes_whose_names_clean_to_the_same_file_both_keep_their_own()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a_b.md", FactoryMemoryFiles.FileNameFor("a/b", used));
        Assert.Equal("a_b-2.md", FactoryMemoryFiles.FileNameFor("a:b", used));
    }
}
