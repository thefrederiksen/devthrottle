namespace CcDirectorClient.Voice;

/// <summary>
/// Client-side view of one recently-used repository on a Director, projected from the
/// Gateway's GET /directors/{id}/repos response (the server's RepositoryDto). Tapping
/// one starts a brand-new session in that repo. A plain data holder with no MAUI or
/// Android dependency so it is unit tested off-device.
/// </summary>
public sealed class RepoInfo
{
    /// <summary>Friendly repo name (folder name when the server left it blank).</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path of the repository / working directory.</summary>
    public string Path { get; set; } = "";

    /// <summary>When a session last used this repo (server sorts newest-first), or null.</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>Never-empty label for the picker: name, else the path's folder, else the path.</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
            var folder = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(folder) ? Path : folder;
        }
    }
}
