using System.Text.RegularExpressions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Parsed metadata for a single handover markdown document.
/// </summary>
public sealed class HandoverInfo
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
    public string DateDisplay { get; set; } = string.Empty;
    public List<string> RepoPaths { get; set; } = new();
    public string? SessionName { get; set; }

    public string? RepoPath => RepoPaths.Count > 0 ? RepoPaths[0] : null;
}

/// <summary>
/// Scans the Director-local handover folder (vault/handovers) and parses each document's
/// title, date, and frontmatter. This mirrors the desktop NewSessionDialog's Handovers tab
/// so the Cockpit can show the same list over REST. File access stays on the Director.
/// </summary>
public static class HandoverScanner
{
    /// <summary>Scan all handover documents, newest first. Returns an empty list if the folder is absent.</summary>
    public static List<HandoverInfo> ScanAll()
    {
        var folder = CcStorage.VaultHandovers();
        FileLog.Write($"[HandoverScanner] ScanAll: scanning {folder}");

        var result = new List<HandoverInfo>();
        if (!Directory.Exists(folder))
            return result;

        foreach (var file in Directory.GetFiles(folder, "*.md"))
        {
            try
            {
                result.Add(Parse(file));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[HandoverScanner] ScanAll: skipping {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        result.Sort((a, b) => b.DateUtc.CompareTo(a.DateUtc));
        FileLog.Write($"[HandoverScanner] ScanAll: found {result.Count} handovers");
        return result;
    }

    /// <summary>Read the full content of a handover file. The path must live inside the handover folder.</summary>
    public static string ReadContent(string filePath)
    {
        var full = ResolveInsideHandoverFolder(filePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("handover not found", full);

        return File.ReadAllText(full);
    }

    /// <summary>
    /// Write a new standalone handover document. Produces the same layout the /handover skill
    /// and HandoverArchive use (yyyyMMdd_HHmm_slug.md + YAML frontmatter) so <see cref="Parse"/>
    /// round-trips the title, date, repositories, and session name. Returns the file path.
    /// </summary>
    public static string WriteNew(string title, string content, IReadOnlyList<string>? repoPaths = null, string? sessionName = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required", nameof(title));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required", nameof(content));

        var folder = CcStorage.VaultHandovers();
        Directory.CreateDirectory(folder);

        var ts = DateTime.Now;
        var slug = Slugify(title);
        var path = Path.Combine(folder, $"{ts:yyyyMMdd_HHmm}_{slug}.md");
        // Same-minute collision: suffix -2, -3, ... rather than overwrite.
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{ts:yyyyMMdd_HHmm}_{slug}-{n}.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: {title}");
        sb.AppendLine($"date: {ts:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(sessionName))
            sb.AppendLine($"session_name: {sessionName}");
        if (repoPaths is { Count: > 0 })
        {
            sb.AppendLine("repositories:");
            foreach (var repo in repoPaths)
                sb.AppendLine($"  - path: {repo}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(content);

        File.WriteAllText(path, sb.ToString());
        FileLog.Write($"[HandoverScanner] WriteNew: wrote {path}");
        return path;
    }

    /// <summary>Delete a handover file. The path must live inside the handover folder.</summary>
    public static void Delete(string filePath)
    {
        var full = ResolveInsideHandoverFolder(filePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("handover not found", full);

        File.Delete(full);
        FileLog.Write($"[HandoverScanner] Delete: removed {full}");
    }

    /// <summary>Resolve a path and verify it lives inside the handover folder.</summary>
    private static string ResolveInsideHandoverFolder(string filePath)
    {
        var folder = CcStorage.VaultHandovers();
        var full = Path.GetFullPath(filePath);
        var rootedFolder = Path.GetFullPath(folder);

        if (!full.StartsWith(rootedFolder, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("handover path is outside the handover folder");
        return full;
    }

    /// <summary>Lowercase-kebab slug for the handover filename (ASCII only, capped at 60 chars).</summary>
    internal static string Slugify(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60)
            slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "handover" : slug;
    }

    public static HandoverInfo Parse(string filePath)
    {
        var info = new HandoverInfo { Path = filePath };
        var name = Path.GetFileNameWithoutExtension(filePath);

        // Filename convention: yyyyMMdd_HHmm_slug-words
        if (name.Length >= 13 && name[8] == '_'
            && DateTime.TryParseExact(name.Substring(0, 8) + name.Substring(9, 4),
                "yyyyMMddHHmm", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            info.DateUtc = parsed;
            info.DateDisplay = parsed.ToString("yyyy-MM-dd HH:mm");

            var slug = name.Length > 14 ? name.Substring(14) : string.Empty;
            info.Title = string.IsNullOrEmpty(slug)
                ? "Handover"
                : char.ToUpper(slug[0]) + slug.Substring(1).Replace("-", " ");
        }
        else
        {
            info.DateUtc = File.GetLastWriteTime(filePath);
            info.DateDisplay = info.DateUtc.ToString("yyyy-MM-dd HH:mm");
            info.Title = name;
        }

        var (repoPaths, sessionName) = ExtractFrontmatter(filePath);
        info.RepoPaths = repoPaths;
        info.SessionName = sessionName;
        return info;
    }

    private static (List<string> RepoPaths, string? SessionName) ExtractFrontmatter(string filePath)
    {
        var paths = new List<string>();
        string? sessionName = null;

        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();

            if (firstLine == "---")
            {
                bool inRepositories = false;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "---") break;

                    if (line.StartsWith("session_name:"))
                    {
                        inRepositories = false;
                        sessionName = line.Substring("session_name:".Length).Trim();
                        if (string.IsNullOrEmpty(sessionName))
                            sessionName = null;
                        continue;
                    }

                    if (line.StartsWith("repositories:"))
                    {
                        inRepositories = true;
                        continue;
                    }

                    if (inRepositories && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                        inRepositories = false;

                    if (inRepositories && line.TrimStart().StartsWith("- path:"))
                    {
                        var path = line.Substring(line.IndexOf("- path:") + 7).Trim();
                        if (Directory.Exists(path))
                            paths.Add(path);
                    }
                }
            }
            else
            {
                var line = firstLine;
                for (int i = 0; i < 10 && line != null; i++)
                {
                    if (line.StartsWith("**Repository:**"))
                    {
                        var raw = line.Substring("**Repository:**".Length).Trim();
                        foreach (var segment in raw.Split(','))
                        {
                            var cleaned = Regex.Replace(segment.Trim(), @"\s*\(.*?\)\s*$", "").Trim();
                            if (Directory.Exists(cleaned))
                                paths.Add(cleaned);
                        }
                        break;
                    }
                    line = reader.ReadLine();
                }
            }
        }
        catch
        {
            // Non-critical: a malformed handover just yields no repo paths.
        }

        return (paths, sessionName);
    }
}
