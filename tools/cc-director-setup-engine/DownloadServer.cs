namespace CcDirector.Setup.Engine;

/// <summary>
/// Where every release is downloaded from: our own download server, not GitHub.
///
/// The release workflow (.github/workflows/release.yml) uploads every file of every release to one
/// folder per version, and writes two channel files LAST, after every file in the folder is verified:
///
///   {BaseUrl}/stable.json                      newest stable release
///   {BaseUrl}/prerelease.json                  newest release of any kind
///   {BaseUrl}/{tag}/release-manifest.json      the manifest of one release
///   {BaseUrl}/{tag}/{asset name}               every asset that manifest names
///
/// A channel file is {"version","tag","prerelease","manifest","date"}. devthrottle.com answers this
/// address with a redirect to the storage account; HttpClient follows it by default, and that must
/// stay so.
///
/// This address is the one copy for this assembly. Core has its own copy
/// (src/CcDirector.Core/Utilities/DownloadServer.cs) and the Python setup has a third
/// (tools/cc-devthrottle/src/setup_ops.py); the install scripts carry it too. Change them together.
/// </summary>
public static class DownloadServer
{
    /// <summary>The root of the download server. No trailing slash.</summary>
    public const string BaseUrl = "https://devthrottle.com/download/files";

    /// <summary>The channel file naming the newest stable release.</summary>
    public const string StableChannel = "stable.json";

    /// <summary>The channel file naming the newest release of any kind, stable or pre-release.</summary>
    public const string PreReleaseChannel = "prerelease.json";

    /// <summary>The manifest every version folder carries.</summary>
    public const string ManifestName = "release-manifest.json";

    /// <summary>The address of a file at the root of the server, such as a channel file.</summary>
    public static string FileUrl(string name) => $"{BaseUrl}/{name}";

    /// <summary>The address of a file inside one release's version folder.</summary>
    public static string ReleaseFileUrl(string tag, string name) => $"{BaseUrl}/{tag}/{name}";
}
