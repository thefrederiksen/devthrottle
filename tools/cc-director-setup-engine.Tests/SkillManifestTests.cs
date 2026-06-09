using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The owned-skills manifest (issue #257) is the single source of ownership truth that makes
/// AC8 (never delete the user's own skills) safe: only names recorded here are ever removed.
/// </summary>
public class SkillManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public SkillManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-skillman-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_AbsentFile_IsEmpty()
        => Assert.Empty(SkillManifest.Load(_layout).OwnedSkills);

    [Fact]
    public void RecordInstalled_PersistsNames_AndRoundTrips()
    {
        SkillManifest.RecordInstalled(_layout, new[] { "cc-director", "bug-fixer" });

        var loaded = SkillManifest.Load(_layout);
        Assert.Equal(new[] { "bug-fixer", "cc-director" }, loaded.OwnedSkills); // sorted
        Assert.True(File.Exists(_layout.SkillManifestPath));
    }

    [Fact]
    public void RecordInstalled_IsIdempotent_CaseInsensitive()
    {
        SkillManifest.RecordInstalled(_layout, new[] { "cc-director" });
        SkillManifest.RecordInstalled(_layout, new[] { "CC-Director", "cc-director" });

        Assert.Single(SkillManifest.Load(_layout).OwnedSkills);
    }

    [Fact]
    public void Add_RejectsBlank()
        => Assert.Throws<ArgumentException>(() => SkillManifest.Empty().Add("  "));
}
