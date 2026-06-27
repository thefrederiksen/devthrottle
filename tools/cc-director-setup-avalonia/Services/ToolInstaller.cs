using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public class ToolInstaller
{
    /// <summary>
    /// Callback invoked when a target file is locked by a running process.
    /// Parameter: process name. Returns: true to retry, false to skip.
    /// </summary>
    public Func<string, Task<bool>>? OnProcessBlocking { get; set; }

    private readonly string _installDir;
    private readonly string _skillsBaseDir;
    private readonly GitHubReleaseService _github = new();

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static readonly string[] SkillNames =
    [
        // dev-throttle: the product's main skill (renamed from cc-director).
        "dev-throttle",
    ];

    public ToolInstaller()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _installDir = Path.Combine(localAppData, "cc-director", "bin");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _skillsBaseDir = Path.Combine(userProfile, ".claude", "skills");
    }

    public string InstallDir => _installDir;

    public async Task<(int installed, int skipped)> InstallToolsAsync(
        List<ToolDownloadItem> items,
        Dictionary<string, AssetInfo> assets)
    {
        SetupLog.Write($"[ToolInstaller] InstallToolsAsync: items={items.Count}");
        Directory.CreateDirectory(_installDir);

        int installed = 0;
        int skipped = 0;

        foreach (var item in items)
        {
            if (!assets.ContainsKey(item.AssetName))
            {
                item.Status = "Skipped";
                item.SizeText = "Not in release";
                skipped++;
                continue;
            }

            var asset = assets[item.AssetName];
            item.SizeText = FormatSize(asset.Size);

            // Check if the target executable is locked by a running process
            if (await CheckAndHandleLockedProcess(item))
            {
                skipped++;
                continue;
            }

            item.Status = "Downloading";

            try
            {
                var progress = new Progress<double>(p => item.Progress = p);

                if (item.AssetName.EndsWith(".zip"))
                {
                    await InstallZippedToolAsync(item, asset, progress);
                }
                else
                {
                    var destPath = Path.Combine(_installDir, item.AssetName);
                    await _github.DownloadFileAsync(asset.DownloadUrl, destPath, progress);

                    // Make executable on macOS
                    if (!IsWindows && !item.AssetName.EndsWith(".exe"))
                        MakeExecutable(destPath);
                }

                item.Status = "Done";
                item.Progress = 100;
                installed++;
            }
            catch (IOException ex) when (IsFileLocked(ex))
            {
                SetupLog.Write($"[ToolInstaller] InstallToolsAsync LOCKED: {item.Name} - {ex.Message}");
                item.Status = "Locked";
                item.StatusDetail = "File is in use by another process";
            }
            catch (Exception ex)
            {
                SetupLog.Write($"[ToolInstaller] InstallToolsAsync FAILED: {item.Name} - {ex.Message}");
                item.Status = "Failed";
            }
        }

        SetupLog.Write($"[ToolInstaller] InstallToolsAsync: installed={installed}, skipped={skipped}");
        return (installed, skipped);
    }

    private async Task InstallZippedToolAsync(ToolDownloadItem item, AssetInfo asset, IProgress<double> progress)
    {
        var zipPath = Path.Combine(_installDir, item.AssetName);
        var destDir = Path.Combine(_installDir, $"_{item.Name}");

        await _github.DownloadFileAsync(asset.DownloadUrl, zipPath, progress);

        // Remove old directory
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, true);

        ZipFile.ExtractToDirectory(zipPath, destDir);
        CreateLaunchers(item.Name);
        File.Delete(zipPath);
    }

    private void CreateLaunchers(string toolName)
    {
        SetupLog.Write($"[ToolInstaller] CreateLaunchers: tool={toolName}");

        string bashContent;

        if (ToolGroupRegistry.NodeTools.Contains(toolName))
        {
            bashContent = $"#!/bin/sh\nnode \"$(dirname \"$0\")/_{toolName}/src/cli.mjs\" \"$@\"\n";

            if (IsWindows)
            {
                var cmdContent = $"@node \"%~dp0_{toolName}\\src\\cli.mjs\" %*\n";
                File.WriteAllText(Path.Combine(_installDir, $"{toolName}.cmd"), cmdContent);
            }
        }
        else if (ToolGroupRegistry.DotNetTools.Contains(toolName))
        {
            var exeSuffix = IsWindows ? ".exe" : "";

            if (toolName == "cc-computer")
            {
                bashContent = $"#!/bin/sh\n\"$(dirname \"$0\")/_{toolName}/{toolName}{exeSuffix}\" --cli \"$@\"\n";

                if (IsWindows)
                {
                    File.WriteAllText(Path.Combine(_installDir, $"{toolName}.cmd"),
                        $"@\"%~dp0_{toolName}\\{toolName}.exe\" --cli %*\n");
                    File.WriteAllText(Path.Combine(_installDir, $"{toolName}-gui.cmd"),
                        $"@\"%~dp0_{toolName}\\{toolName}.exe\" %*\n");
                }

                // GUI launcher (Unix)
                var guiLauncher = Path.Combine(_installDir, $"{toolName}-gui");
                File.WriteAllText(guiLauncher,
                    $"#!/bin/sh\n\"$(dirname \"$0\")/_{toolName}/{toolName}{exeSuffix}\" \"$@\"\n");
                if (!IsWindows) MakeExecutable(guiLauncher);
            }
            else
            {
                bashContent = $"#!/bin/sh\n\"$(dirname \"$0\")/_{toolName}/{toolName}{exeSuffix}\" \"$@\"\n";

                if (IsWindows)
                {
                    File.WriteAllText(Path.Combine(_installDir, $"{toolName}.cmd"),
                        $"@\"%~dp0_{toolName}\\{toolName}.exe\" %*\n");
                }
            }
        }
        else
        {
            return;
        }

        var launcherPath = Path.Combine(_installDir, toolName);
        File.WriteAllText(launcherPath, bashContent);
        if (!IsWindows) MakeExecutable(launcherPath);
    }

    public async Task InstallSkillsAsync(List<SkillItem> skillItems)
    {
        SetupLog.Write($"[ToolInstaller] InstallSkillsAsync: count={skillItems.Count}");

        foreach (var skill in skillItems)
        {
            var skillDir = Path.Combine(_skillsBaseDir, skill.Name);
            Directory.CreateDirectory(skillDir);
            var skillPath = Path.Combine(skillDir, "SKILL.md");

            // The canonical skill tree is .claude/skills/<name>/ (the stale root skills/ duplicate was
            // removed in issue #396; fetching from skills/ here 404'd every install).
            var success = await _github.DownloadSkillFileAsync(
                skillPath, $".claude/skills/{skill.Name}/SKILL.md");
            skill.Status = success ? "Done" : "Failed";
        }

        var done = skillItems.Count(s => s.Status == "Done");
        SetupLog.Write($"[ToolInstaller] InstallSkillsAsync: {done}/{skillItems.Count} installed");
    }

    public List<ToolDownloadItem> BuildDownloadList(string[] toolNames)
    {
        var items = new List<ToolDownloadItem>();

        // Main app - platform-specific asset name
        var mainAsset = IsWindows ? "cc-director-win-x64.exe" : "cc-director-mac-arm64";
        items.Add(new ToolDownloadItem { Name = "cc-director", AssetName = mainAsset });

        foreach (var tool in toolNames)
        {
            string assetName;
            if (ToolGroupRegistry.NodeTools.Contains(tool) || ToolGroupRegistry.DotNetTools.Contains(tool))
                assetName = $"{tool}.zip";
            else if (IsWindows)
                assetName = $"{tool}-win-x64.exe";
            else
                assetName = $"{tool}-mac-arm64";

            items.Add(new ToolDownloadItem { Name = tool, AssetName = assetName });
        }

        return items;
    }

    private async Task<bool> CheckAndHandleLockedProcess(ToolDownloadItem item)
    {
        var exeName = item.Name;
        var processes = Process.GetProcessesByName(exeName);
        if (processes.Length == 0)
            return false;

        SetupLog.Write($"[ToolInstaller] Process blocking: {exeName} ({processes.Length} instance(s) running)");

        foreach (var p in processes)
            p.Dispose();

        if (OnProcessBlocking == null)
        {
            item.Status = "Locked";
            item.StatusDetail = $"{exeName} is currently running";
            return true;
        }

        // Ask user what to do - loop until resolved or skipped
        while (true)
        {
            var retry = await OnProcessBlocking(exeName);
            if (!retry)
            {
                item.Status = "Skipped";
                item.StatusDetail = $"Skipped - {exeName} was running";
                return true;
            }

            // User said they closed it, check again
            var stillRunning = Process.GetProcessesByName(exeName);
            var running = stillRunning.Length > 0;
            foreach (var p in stillRunning)
                p.Dispose();

            if (!running)
            {
                SetupLog.Write($"[ToolInstaller] Process no longer blocking: {exeName}");
                return false;
            }

            SetupLog.Write($"[ToolInstaller] Process still running after retry: {exeName}");
        }
    }

    private static bool IsFileLocked(IOException ex)
    {
        // HResult 0x80070020 = ERROR_SHARING_VIOLATION
        // HResult 0x80070021 = ERROR_LOCK_VIOLATION
        var hr = ex.HResult & 0xFFFF;
        return hr == 0x0020 || hr == 0x0021;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static void MakeExecutable(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[ToolInstaller] MakeExecutable FAILED: {path} - {ex.Message}");
        }
    }
}
