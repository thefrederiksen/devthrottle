using System.IO;
using System.Linq;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Deployment guard (issue #723): the fleet-comms skill must be in the installer's shipped list so it
/// reaches every machine, and its SKILL.md must exist in the repo (the installer fetches it by name
/// from the default branch). Together these fail if the skill is half-wired (listed but missing, or
/// present but not shipped).
/// </summary>
public sealed class FleetCommsSkillShipTests
{
    [Fact]
    public void SkillNames_IncludesFleetComms()
    {
        Assert.Contains("fleet-comms", SkillInstaller.SkillNames);
    }

    [Fact]
    public void EverySkillName_HasASkillMdInTheRepo()
    {
        var skillsDir = FindRepoDir(Path.Combine(".claude", "skills"));
        foreach (var name in SkillInstaller.SkillNames)
        {
            var path = Path.Combine(skillsDir, name, "SKILL.md");
            Assert.True(File.Exists(path), $"{name}: SKILL.md is missing at {path}; the installer fetches it by name.");
        }
    }

    private static string FindRepoDir(string relativePath)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate {relativePath} walking up from {System.AppContext.BaseDirectory}");
    }
}
