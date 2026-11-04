namespace CcDirector.Core.Utilities;

/// <summary>
/// Where the Director downloads its own updates from: our own download server, not GitHub.
///
/// The release workflow (.github/workflows/release.yml) uploads every file of every release to one
/// folder per version and writes the channel files LAST, after every file in the folder is verified:
///
///   {BaseUrl}/stable.json                      {"version","tag","prerelease","manifest","date"}
///   {BaseUrl}/{tag}/release-manifest.json      the manifest of one release
///   {BaseUrl}/{tag}/{asset name}               every asset that manifest names
///
/// devthrottle.com answers this address with a redirect to the storage account; HttpClient follows
/// it by default, and that must stay so.
///
/// This is Core's copy. The setup engine has its own
/// (tools/cc-director-setup-engine/DownloadServer.cs) and the Python setup a third
/// (tools/cc-devthrottle/src/setup_ops.py); the install scripts carry it too. Change them together.
/// </summary>
public static class DownloadServer
{
    /// <summary>The root of the download server. No trailing slash.</summary>
    public const string BaseUrl = "https://devthrottle.com/download/files";

    /// <summary>The channel file naming the newest stable release.</summary>
    public const string StableChannel = "stable.json";

    /// <summary>The manifest every version folder carries.</summary>
    public const string ManifestName = "release-manifest.json";

    /// <summary>The address of a file at the root of the server, such as a channel file.</summary>
    public static string FileUrl(string name) => $"{BaseUrl}/{name}";

    /// <summary>The address of a file inside one release's version folder.</summary>
    public static string ReleaseFileUrl(string tag, string name) => $"{BaseUrl}/{tag}/{name}";
}
