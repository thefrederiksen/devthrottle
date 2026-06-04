using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class RepositoryRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<RepositoryConfig> _repositories = new();

    public string FilePath { get; }
    public IReadOnlyList<RepositoryConfig> Repositories => _repositories.AsReadOnly();

    public RepositoryRegistry(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "repositories.json");
    }

    public void Load()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, "[]");
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<RepositoryConfig>>(json, JsonOptions);
            if (loaded != null)
            {
                _repositories.Clear();
                _repositories.AddRange(loaded);
            }
        }
        catch
        {
            // If the file is corrupt, start fresh
        }
    }

    public bool TryAdd(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var duplicate = _repositories.Any(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (duplicate)
            return false;

        var name = Path.GetFileName(normalized);
        _repositories.Add(new RepositoryConfig { Name = name, Path = normalized });
        Save();
        return true;
    }

    public bool Remove(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var index = _repositories.FindIndex(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        _repositories.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>
    /// Rename a registered repository (display name only; the path is the identity).
    /// Returns false when the path is not registered.
    /// </summary>
    public bool Rename(string folderPath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("name is required", nameof(newName));

        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var repo = _repositories.FirstOrDefault(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (repo is null)
            return false;

        repo.Name = newName.Trim();
        Save();
        FileLog.Write($"[RepositoryRegistry] Rename: {normalized} -> \"{repo.Name}\"");
        return true;
    }

    public void MarkUsed(string folderPath)
    {
        FileLog.Write($"[RepositoryRegistry] MarkUsed: {folderPath}");
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var repo = _repositories.FirstOrDefault(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (repo != null)
        {
            repo.LastUsed = DateTime.UtcNow;
            repo.NotifyLastUsedChanged();
            Save();
            FileLog.Write($"[RepositoryRegistry] MarkUsed: updated LastUsed for {repo.Name}");
        }
        else
        {
            FileLog.Write($"[RepositoryRegistry] MarkUsed: repo not found for {normalized}");
        }
    }

    public void SeedFrom(IEnumerable<RepositoryConfig> repos)
    {
        foreach (var repo in repos)
        {
            if (!string.IsNullOrWhiteSpace(repo.Path))
                TryAdd(repo.Path);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_repositories, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
