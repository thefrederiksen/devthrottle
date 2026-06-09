using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Records which Claude Code skills CC Director installed (issue #257), so the uninstaller can
/// remove EXACTLY those and never the user's own skills that happen to live alongside them in
/// %USERPROFILE%\.claude\skills. Skills are installed per-user by the setup UI (SkillInstaller),
/// not by the binary engine, so the install side calls <see cref="RecordInstalled"/> and the
/// uninstall side reads <see cref="OwnedSkills"/>; the manifest is the single source of ownership
/// truth (Assumption 3). Persisted at <see cref="InstallLayout.SkillManifestPath"/> as a JSON
/// string array.
/// </summary>
public sealed class SkillManifest
{
    // Case-insensitive: skill directory names are matched the way the file system treats them on
    // Windows, so re-recording "CC-Director" never duplicates an existing "cc-director" entry.
    private readonly SortedSet<string> _skills;

    private SkillManifest(IEnumerable<string> skills) =>
        _skills = new SortedSet<string>(skills, StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty manifest (no skills recorded).</summary>
    public static SkillManifest Empty() => new(Array.Empty<string>());

    /// <summary>The owned skill names, sorted, case-insensitively de-duplicated.</summary>
    public IReadOnlyCollection<string> OwnedSkills => _skills;

    /// <summary>Record a skill name as cc-director-owned. Returns true if it was newly added.</summary>
    public bool Add(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            throw new ArgumentException("skillName required", nameof(skillName));
        return _skills.Add(skillName.Trim());
    }

    /// <summary>Load the manifest for a layout; an absent or unreadable file yields an empty manifest.</summary>
    public static SkillManifest Load(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = layout.SkillManifestPath;
        if (!File.Exists(path)) return Empty();
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            return names is null ? Empty() : new SkillManifest(names);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[SkillManifest] load failed ({path}): {ex.Message}; treating as empty");
            return Empty();
        }
    }

    /// <summary>Persist the manifest, creating the setup-state dir if needed.</summary>
    public void Save(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.SetupStateDir);
        var json = JsonSerializer.Serialize(_skills.ToArray(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(layout.SkillManifestPath, json);
    }

    /// <summary>
    /// Add the given skill names to the manifest at install time and persist it. Idempotent:
    /// re-recording an already-owned skill is a no-op. Returns the loaded-and-updated manifest.
    /// </summary>
    public static SkillManifest RecordInstalled(InstallLayout layout, IEnumerable<string> skillNames)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(skillNames);
        var manifest = Load(layout);
        foreach (var name in skillNames)
        {
            if (!string.IsNullOrWhiteSpace(name)) manifest.Add(name);
        }
        manifest.Save(layout);
        EngineLog.Write($"[SkillManifest] RecordInstalled: {manifest._skills.Count} owned skill(s)");
        return manifest;
    }
}
