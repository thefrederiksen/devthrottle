namespace CcDirectorSetup.Models;

/// <summary>
/// App personality profile. Standard = simplified UI for non-developers.
/// Developer = terminal-like, GitHub integration, advanced features.
/// </summary>
public enum InstallProfile
{
    Standard,
    Developer
}

public record ToolGroup(
    string Name,
    string Description,
    string[] Tools,
    bool DefaultEnabled,
    bool IsRequired);

public static class ToolGroupRegistry
{
    public static readonly ToolGroup[] AllGroups =
    [
        new("Core", "Settings and configuration",
            ["cc-settings"], DefaultEnabled: true, IsRequired: true),

        new("Documents", "PDF, HTML, Word, Excel, PowerPoint generation",
            ["cc-pdf", "cc-html", "cc-word", "cc-excel", "cc-powerpoint"],
            DefaultEnabled: true, IsRequired: false),

        new("Media", "Image, photo, video, voice, and transcription",
            ["cc-image", "cc-photos", "cc-video", "cc-voice", "cc-transcribe", "cc-whisper"],
            DefaultEnabled: true, IsRequired: false),

        new("Browser", "Browser automation with Brave and Playwright",
            ["cc-browser", "cc-playwright"],
            DefaultEnabled: true, IsRequired: false),

        new("Email", "Outlook and Gmail integration",
            ["cc-outlook", "cc-gmail"],
            DefaultEnabled: true, IsRequired: false),

        new("Data", "Personal data vault",
            ["cc-vault"],
            DefaultEnabled: true, IsRequired: false),

        new("Social", "Reddit, Twitter, and Facebook",
            ["cc-reddit", "cc-twitter", "cc-facebook"],
            DefaultEnabled: false, IsRequired: false),

        new("Research", "Web crawling and YouTube",
            ["cc-crawl4ai", "cc-youtube", "cc-youtube-info"],
            DefaultEnabled: false, IsRequired: false),

        new("Developer", "Documentation generation, hardware info, analytics",
            ["cc-docgen", "cc-hardware", "cc-posthog"],
            DefaultEnabled: false, IsRequired: false),

        new("Automation", "Computer use and click automation",
            ["cc-click", "cc-computer", "cc-trisight"],
            DefaultEnabled: false, IsRequired: false),

        new("Marketing", "Website audit and branding recommendations",
            ["cc-brandingrecommendations", "cc-websiteaudit"],
            DefaultEnabled: false, IsRequired: false),
    ];

    public static readonly string[] NodeTools =
    [
        "cc-browser",
        "cc-brandingrecommendations",
        "cc-websiteaudit",
    ];

    public static readonly string[] DotNetTools =
    [
        "cc-click",
        "cc-computer",
        "cc-trisight",
    ];

    public static List<string> GetDefaultGroupNames()
    {
        return AllGroups
            .Where(g => g.DefaultEnabled)
            .Select(g => g.Name)
            .ToList();
    }

    public static List<string> GetPresetGroupNames(string preset)
    {
        return preset switch
        {
            "Standard" => GetDefaultGroupNames(),
            "Developer" => AllGroups.Select(g => g.Name).ToList(),
            "All" => AllGroups.Select(g => g.Name).ToList(),
            _ => GetDefaultGroupNames()
        };
    }

    public static string[] GetToolsForGroups(IEnumerable<string> enabledGroupNames)
    {
        var enabled = new HashSet<string>(enabledGroupNames);

        // Always include required groups
        foreach (var g in AllGroups.Where(g => g.IsRequired))
            enabled.Add(g.Name);

        return AllGroups
            .Where(g => enabled.Contains(g.Name))
            .SelectMany(g => g.Tools)
            .Distinct()
            .ToArray();
    }

    public static int GetToolCount(IEnumerable<string> enabledGroupNames)
    {
        return GetToolsForGroups(enabledGroupNames).Length;
    }

    public static string GetToolPreview(ToolGroup group, int maxShow = 2)
    {
        var tools = group.Tools;
        if (tools.Length <= maxShow)
            return string.Join(", ", tools);

        var shown = string.Join(", ", tools.Take(maxShow));
        return $"{shown}, +{tools.Length - maxShow}";
    }
}
