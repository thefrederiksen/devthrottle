using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Session naming (issue #800): the composer guarantees a created session is named with more
/// than the bare repository folder name, so sessions in the SAME checkout can be told apart.
/// The rule: an EXPLICIT name that is blank or equals the folder name is weak (rejected by the
/// caller); when no explicit name is given the name is auto-composed from folder + purpose, or
/// folder + type + a disambiguator.
/// </summary>
public class SessionNameTests
{
    // ===== Explicit name passthrough =====

    [Fact]
    public void Compose_ExplicitName_PassesThroughVerbatim()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Implementation,
            explicitName: "Frontend review", purpose: null, disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void Compose_ExplicitName_IsTrimmed()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: "  Frontend review  ", purpose: null, disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void Compose_ExplicitName_WinsOverPurpose()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: "Frontend review", purpose: "implement #799", disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    // ===== Purpose composition =====

    [Fact]
    public void Compose_PurposeOnly_CombinesFolderAndPurpose_NotBareFolder()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: null, purpose: "implement #799", disambiguator: "1fb5");
        Assert.Equal("devthrottle: implement #799", name);
        Assert.Contains("devthrottle", name);
        Assert.Contains("implement #799", name);
        Assert.NotEqual("devthrottle", name);
    }

    [Fact]
    public void Compose_Purpose_IsTrimmedAndCappedAtMaxLength()
    {
        var longPurpose = new string('x', SessionName.MaxPurposeLength + 25);
        var name = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: null, purpose: longPurpose, disambiguator: "1fb5");
        // Folder + ": " prefix, then the purpose capped to MaxPurposeLength characters.
        var expectedPurpose = new string('x', SessionName.MaxPurposeLength);
        Assert.Equal($"devthrottle: {expectedPurpose}", name);
    }

    // ===== Type + disambiguator default when both absent =====

    [Fact]
    public void Compose_NeitherNameNorPurpose_UsesFolderTypeDisambiguator_NotBareFolder()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Implementation,
            explicitName: null, purpose: null, disambiguator: "1fb5");
        Assert.Equal("devthrottle / Implementation / 1fb5", name);
        Assert.Contains("devthrottle", name);
        Assert.Contains("Implementation", name);
        Assert.Contains("1fb5", name);
        Assert.NotEqual("devthrottle", name);
    }

    [Fact]
    public void Compose_BlankExplicitName_TreatedAsAbsent_AutoComposes()
    {
        var name = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: "   ", purpose: null, disambiguator: "abcd");
        Assert.Equal("devthrottle / Developer / abcd", name);
        Assert.NotEqual("devthrottle", name);
    }

    // ===== Two calls differing only by disambiguator produce different names =====

    [Fact]
    public void Compose_TwoDefaultsDifferingOnlyByDisambiguator_ProduceDistinctNames()
    {
        var first = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: null, purpose: null, disambiguator: "1fb5");
        var second = SessionName.Compose("devthrottle", SessionType.Developer,
            explicitName: null, purpose: null, disambiguator: "9c0a");
        Assert.NotEqual(first, second);
    }

    // ===== Rejection of a blank/weak explicit name =====

    [Fact]
    public void IsWeakExplicitName_Blank_IsWeak()
    {
        Assert.True(SessionName.IsWeakExplicitName("", "devthrottle"));
        Assert.True(SessionName.IsWeakExplicitName("   ", "devthrottle"));
        Assert.True(SessionName.IsWeakExplicitName(null, "devthrottle"));
    }

    [Fact]
    public void IsWeakExplicitName_EqualsFolderName_IsWeak_CaseInsensitive()
    {
        Assert.True(SessionName.IsWeakExplicitName("devthrottle", "devthrottle"));
        Assert.True(SessionName.IsWeakExplicitName("DevThrottle", "devthrottle"));
        Assert.True(SessionName.IsWeakExplicitName("  devthrottle  ", "devthrottle"));
    }

    [Fact]
    public void IsWeakExplicitName_MeaningfulName_IsNotWeak()
    {
        Assert.False(SessionName.IsWeakExplicitName("Frontend review", "devthrottle"));
        Assert.False(SessionName.IsWeakExplicitName("devthrottle: implement #799", "devthrottle"));
    }

    // ===== Helpers =====

    [Fact]
    public void FolderName_TrimsTrailingSeparators()
    {
        Assert.Equal("devthrottle", SessionName.FolderName(@"D:\ReposFred\devthrottle"));
        Assert.Equal("devthrottle", SessionName.FolderName(@"D:\ReposFred\devthrottle\"));
        Assert.Equal("devthrottle", SessionName.FolderName("D:/ReposFred/devthrottle/"));
    }

    [Fact]
    public void Disambiguator_IsFirstFourHexCharsOfId()
    {
        var id = Guid.Parse("1fb59c0a-1234-5678-9abc-def012345678");
        Assert.Equal("1fb5", SessionName.Disambiguator(id));
        Assert.Equal(SessionName.DisambiguatorLength, SessionName.Disambiguator(id).Length);
    }

    [Fact]
    public void DisplayName_WithCustomName_ReturnsIt()
    {
        var name = SessionName.DisplayName("Frontend review", "devthrottle",
            SessionType.Developer, "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void DisplayName_WithoutCustomName_AutoComposes_NotBareFolder()
    {
        var name = SessionName.DisplayName(null, "devthrottle", SessionType.Implementation, "1fb5");
        Assert.Equal("devthrottle / Implementation / 1fb5", name);
        Assert.NotEqual("devthrottle", name);
    }
}
