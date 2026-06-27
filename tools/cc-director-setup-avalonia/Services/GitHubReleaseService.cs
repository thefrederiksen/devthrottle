using System.Net.Http;
using System.Text.Json;

namespace CcDirectorSetup.Services;

public class GitHubReleaseService
{
    private const string ApiBase = "https://api.github.com";
    private const string RawBase = "https://raw.githubusercontent.com";
    private const string RepoOwner = "thefrederiksen";
    private const string RepoName = "devthrottle";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static GitHubReleaseService()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "cc-director-setup");
        Http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    public async Task<(string version, Dictionary<string, AssetInfo> assets)?> GetLatestReleaseAsync()
    {
        SetupLog.Write("[GitHubReleaseService] GetLatestReleaseAsync: fetching");

        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";
        var response = await Http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            SetupLog.Write($"[GitHubReleaseService] GetLatestReleaseAsync: HTTP {response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.GetProperty("tag_name").GetString() ?? "unknown";
        var assets = new Dictionary<string, AssetInfo>();

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.GetProperty("size").GetInt64();

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(downloadUrl))
            {
                assets[name] = new AssetInfo { DownloadUrl = downloadUrl, Size = size };
            }
        }

        SetupLog.Write($"[GitHubReleaseService] GetLatestReleaseAsync: version={version}, assets={assets.Count}");
        return (version, assets);
    }

    public async Task DownloadFileAsync(string url, string destPath, IProgress<double>? progress = null)
    {
        SetupLog.Write($"[GitHubReleaseService] DownloadFileAsync: url={url}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "cc-director-setup");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[8192];
        long downloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloaded += bytesRead;

            if (totalBytes > 0)
                progress?.Report((double)downloaded / totalBytes * 100);
        }

        SetupLog.Write($"[GitHubReleaseService] DownloadFileAsync: complete, bytes={downloaded}");
    }

    public async Task<bool> DownloadSkillFileAsync(string destPath, string repoPath = ".claude/skills/dev-throttle/SKILL.md")
    {
        SetupLog.Write($"[GitHubReleaseService] DownloadSkillFileAsync: {repoPath}");

        var url = $"{RawBase}/{RepoOwner}/{RepoName}/main/{repoPath}";

        try
        {
            var content = await Http.GetStringAsync(url);
            await File.WriteAllTextAsync(destPath, content);
            SetupLog.Write("[GitHubReleaseService] DownloadSkillFileAsync: success");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GitHubReleaseService] DownloadSkillFileAsync FAILED: {ex.Message}");
            return false;
        }
    }
}

public class AssetInfo
{
    public required string DownloadUrl { get; init; }
    public long Size { get; init; }
}
