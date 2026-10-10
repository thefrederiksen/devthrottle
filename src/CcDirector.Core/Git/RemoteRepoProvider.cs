using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public static class RemoteRepoProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<(List<RemoteRepoInfo> Repos, string? Error)> ListGitHubReposAsync()
    {
        FileLog.Write("[RemoteRepoProvider] ListGitHubReposAsync: querying gh CLI");

        var (output, error) = await RunCliAsync("gh", "repo list --limit 200 --json name,url,description,isPrivate");
        if (error != null)
            return ([], error);

        try
        {
            var ghRepos = JsonSerializer.Deserialize<List<GhRepoDto>>(output, JsonOptions);
            if (ghRepos is null)
                return ([], "Failed to parse GitHub response");

            var repos = ghRepos
                .Select(r => new RemoteRepoInfo
                {
                    Name = r.Name,
                    Url = r.Url,
                    Description = r.Description,
                    IsPrivate = r.IsPrivate
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            FileLog.Write($"[RemoteRepoProvider] ListGitHubReposAsync: found {repos.Count} repos");
            return (repos, null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[RemoteRepoProvider] ListGitHubReposAsync FAILED: {ex.Message}");
            return ([], "Failed to parse GitHub CLI response");
        }
    }

    public static async Task<(List<RemoteRepoInfo> Repos, string? Error)> ListAzureDevOpsReposAsync(
        string organizationUrl, string projectName)
    {
        FileLog.Write($"[RemoteRepoProvider] ListAzureDevOpsReposAsync: org={organizationUrl}, project={projectName}");

        var args = $"repos list --organization \"{organizationUrl}\" --project \"{projectName}\" --output json";
        var (output, error) = await RunCliAsync("az", args);
        if (error != null)
            return ([], error);

        try
        {
            var azRepos = JsonSerializer.Deserialize<List<AzRepoDto>>(output, JsonOptions);
            if (azRepos is null)
                return ([], "Failed to parse Azure DevOps response");

            var repos = azRepos
                .Select(r => new RemoteRepoInfo
                {
                    Name = r.Name,
                    Url = r.RemoteUrl ?? r.WebUrl ?? string.Empty,
                    Description = null,
                    IsPrivate = true // Azure DevOps repos are always private by default
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            FileLog.Write($"[RemoteRepoProvider] ListAzureDevOpsReposAsync: found {repos.Count} repos");
            return (repos, null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[RemoteRepoProvider] ListAzureDevOpsReposAsync FAILED: {ex.Message}");
            return ([], "Failed to parse Azure DevOps CLI response");
        }
    }

    public static List<(string Name, string Path)> ScanLocalRepos(string rootPath)
    {
        FileLog.Write($"[RemoteRepoProvider] ScanLocalRepos: rootPath={rootPath}");

        var results = new List<(string Name, string Path)>();
        if (!Directory.Exists(rootPath))
        {
            FileLog.Write($"[RemoteRepoProvider] ScanLocalRepos: directory does not exist");
            return results;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                var gitDir = Path.Combine(dir, ".git");
                if (Directory.Exists(gitDir))
                {
                    var name = Path.GetFileName(dir);
                    results.Add((name, dir));
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RemoteRepoProvider] ScanLocalRepos FAILED: {ex.Message}");
        }

        FileLog.Write($"[RemoteRepoProvider] ScanLocalRepos: found {results.Count} local repos");
        return results;
    }

    private static async Task<(string Output, string? Error)> RunCliAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return ("", $"Failed to start {fileName}");

            var output = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                var errorMsg = stderr.Contains("auth login")
                    ? $"{fileName} CLI is not authenticated. Run '{fileName} auth login' first."
                    : $"{fileName} error: {stderr.Trim().Split('\n').FirstOrDefault()}";
                FileLog.Write($"[RemoteRepoProvider] RunCliAsync: {fileName} exited with code {proc.ExitCode}: {errorMsg}");
                return ("", errorMsg);
            }

            return (output, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var msg = $"{fileName} CLI not found. Please install it first.";
            // not-an-error: the command line is optional, and the person is told to install it
            FileLog.Write($"[RemoteRepoProvider] RunCliAsync: {msg}");
            return ("", msg);
        }
    }

    // DTO classes for JSON deserialization
    private sealed class GhRepoDto
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Description { get; set; }
        public bool IsPrivate { get; set; }
    }

    private sealed class AzRepoDto
    {
        public string Name { get; set; } = "";
        public string? RemoteUrl { get; set; }
        public string? WebUrl { get; set; }
    }
}
