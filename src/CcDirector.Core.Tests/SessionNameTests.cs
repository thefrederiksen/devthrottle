using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Session naming (issue #800): the composer guarantees a created session is named with more
/// than the bare repository folder name, so sessions in the SAME checkout can be told apart.
/// The rule: an EXPLICIT name that is blank or equals the folder name is weak (rejected by the
/// caller); when no explicit name is given the name is auto-composed from folder + purpose, or
/// folder + a disambiguator.
/// </summary>
public class SessionNameTests
{
    // ===== Explicit name passthrough =====

    [Fact]
    public void Compose_ExplicitName_PassesThroughVerbatim()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: "Frontend review", purpose: null, disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void Compose_ExplicitName_IsTrimmed()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: "  Frontend review  ", purpose: null, disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void Compose_ExplicitName_WinsOverPurpose()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: "Frontend review", purpose: "implement #799", disambiguator: "1fb5");
        Assert.Equal("Frontend review", name);
    }

    // ===== Purpose composition =====

    [Fact]
    public void Compose_PurposeOnly_CombinesFolderAndPurpose_NotBareFolder()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: "implement #799", disambiguator: "1fb5");
        Assert.Equal("devthrottle: implement #799", name);
        Assert.Contains("devthrottle", name);
        Assert.Contains("implement #799", name);
        Assert.NotEqual("devthrottle", name);
    }

    [Fact]
    public void Compose_Worker_WithPurpose_IsTaskFlavored_NoRepoPrefix()
    {
        // Automatic session roles (chunk 3): a Worker is named by its TASK - the purpose leads, without the
        // repo prefix a Manager/Standalone gets.
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: "implement #799", disambiguator: "1fb5", isWorker: true);
        Assert.Equal("implement #799", name);
    }

    [Fact]
    public void Compose_NonWorker_WithPurpose_KeepsRepoPrefix()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: "implement #799", disambiguator: "1fb5", isWorker: false);
        Assert.Equal("devthrottle: implement #799", name);
    }

    [Fact]
    public void Compose_Worker_NoPurpose_FallsBackToRepoDefault()
    {
        // Nothing to flavor with - a worker with no purpose still gets the repo default, not a bare folder.
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: null, disambiguator: "1fb5", isWorker: true);
        Assert.Equal("devthrottle / 1fb5", name);
    }

    [Fact]
    public void Compose_Worker_ExplicitName_StillWins()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: "hand picked", purpose: "implement #799", disambiguator: "1fb5", isWorker: true);
        Assert.Equal("hand picked", name);
    }

    [Fact]
    public void Compose_Purpose_IsTrimmedAndCappedAtMaxLength()
    {
        var longPurpose = new string('x', SessionName.MaxPurposeLength + 25);
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: longPurpose, disambiguator: "1fb5");
        // Folder + ": " prefix, then the purpose capped to MaxPurposeLength characters.
        var expectedPurpose = new string('x', SessionName.MaxPurposeLength);
        Assert.Equal($"devthrottle: {expectedPurpose}", name);
    }

    // ===== Folder + disambiguator default when both name and purpose are absent =====

    [Fact]
    public void Compose_NeitherNameNorPurpose_UsesFolderDisambiguator_NotBareFolder()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: null, purpose: null, disambiguator: "1fb5");
        Assert.Equal("devthrottle / 1fb5", name);
        Assert.Contains("devthrottle", name);
        Assert.Contains("1fb5", name);
        Assert.NotEqual("devthrottle", name);
    }

    [Fact]
    public void Compose_BlankExplicitName_TreatedAsAbsent_AutoComposes()
    {
        var name = SessionName.Compose("devthrottle",
            explicitName: "   ", purpose: null, disambiguator: "abcd");
        Assert.Equal("devthrottle / abcd", name);
        Assert.NotEqual("devthrottle", name);
    }

    // ===== Two calls differing only by disambiguator produce different names =====

    [Fact]
    public void Compose_TwoDefaultsDifferingOnlyByDisambiguator_ProduceDistinctNames()
    {
        var first = SessionName.Compose("devthrottle",
            explicitName: null, purpose: null, disambiguator: "1fb5");
        var second = SessionName.Compose("devthrottle",
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
        // Each spelling must be absolute for the platform under test. A drive-letter path is relative
        // off Windows, so the folder name came back as the whole string instead of the last segment.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("devthrottle", SessionName.FolderName(@"C:\repos\devthrottle"));
            Assert.Equal("devthrottle", SessionName.FolderName(@"C:\repos\devthrottle\"));
            Assert.Equal("devthrottle", SessionName.FolderName("C:/repos/devthrottle/"));
        }
        else
        {
            Assert.Equal("devthrottle", SessionName.FolderName("/repos/devthrottle"));
            Assert.Equal("devthrottle", SessionName.FolderName("/repos/devthrottle/"));
            Assert.Equal("devthrottle", SessionName.FolderName("/repos/devthrottle//"));
        }
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
        var name = SessionName.DisplayName("Frontend review", "devthrottle", "1fb5");
        Assert.Equal("Frontend review", name);
    }

    [Fact]
    public void DisplayName_WithoutCustomName_AutoComposes_NotBareFolder()
    {
        var name = SessionName.DisplayName(null, "devthrottle", "1fb5");
        Assert.Equal("devthrottle / 1fb5", name);
        Assert.NotEqual("devthrottle", name);
    }
}
