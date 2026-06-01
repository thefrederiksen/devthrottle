using System.Net.Http;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

/// <summary>
/// Installs Claude Code skills by downloading their SKILL.md from the repo's raw
/// content into %USERPROFILE%\.claude\skills\&lt;name&gt;\. This is per-user and needs
/// no admin. It is intentionally separate from the binary install/update engine
/// (CcDirector.Setup.Engine), which only handles released exe/zip assets.
/// </summary>
public sealed class SkillInstaller
{
    private const string RawBase = "https://raw.githubusercontent.com";
    private const string RepoOwner = "thefrederiksen";
    private const string RepoName = "cc-director";

    /// <summary>The skills the installer offers (SKILL.md is fetched per name).</summary>
    public static readonly string[] SkillNames =
    [
        "cc-director",
    ];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "cc-director-setup");
        return http;
    }

    private readonly string _skillsBaseDir;

    public SkillInstaller()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _skillsBaseDir = Path.Combine(userProfile, ".claude", "skills");
    }

    public async Task InstallSkillsAsync(List<SkillItem> skillItems)
    {
        SetupLog.Write($"[SkillInstaller] InstallSkillsAsync: count={skillItems.Count}");

        foreach (var skill in skillItems)
        {
            var skillDir = Path.Combine(_skillsBaseDir, skill.Name);
            Directory.CreateDirectory(skillDir);
            var skillPath = Path.Combine(skillDir, "SKILL.md");
            skill.Status = await DownloadSkillFileAsync(skillPath, $"skills/{skill.Name}/SKILL.md") ? "Done" : "Failed";
        }

        var done = skillItems.Count(s => s.Status == "Done");
        SetupLog.Write($"[SkillInstaller] InstallSkillsAsync: {done}/{skillItems.Count} installed");
    }

    private static async Task<bool> DownloadSkillFileAsync(string destPath, string repoPath)
    {
        var url = $"{RawBase}/{RepoOwner}/{RepoName}/main/{repoPath}";
        try
        {
            var content = await Http.GetStringAsync(url);
            await File.WriteAllTextAsync(destPath, content);
            SetupLog.Write($"[SkillInstaller] DownloadSkillFileAsync: success {repoPath}");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[SkillInstaller] DownloadSkillFileAsync FAILED {repoPath}: {ex.Message}");
            return false;
        }
    }
}
