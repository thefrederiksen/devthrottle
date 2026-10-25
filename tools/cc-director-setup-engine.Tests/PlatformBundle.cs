using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Python tools bundle as it looks on THIS operating system, for the tests that stage a release or a venv.
///
/// WHY IT EXISTS. The installer ships a different bundle on each system - a zip with <c>python.exe</c> and a venv
/// <c>Scripts</c> folder of <c>.exe</c> console scripts on Windows, a <c>.tar.gz</c> with <c>bin/python3</c> and a venv
/// <c>bin</c> folder of bare-named scripts on macOS and Linux - and asks the platform for every one of those names.
/// The tests wrote the Windows names out by hand, so on Linux the installer looked for assets, folders and scripts that
/// were never there and about thirty tests failed on correct code. Every name here is the product's own answer, or
/// the one place the tests state the platform's shape, so a fixture is right on every system.
/// </summary>
internal static class PlatformBundle
{
    /// <summary>The <c>platform</c> word a release manifest carries for this system's assets.</summary>
    public static string ManifestPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Where the bundled interpreter sits inside the Python asset.</summary>
    public static string InterpreterInAsset =>
        OperatingSystem.IsWindows() ? "python.exe" : Path.Combine("bin", "python3");

    /// <summary>The interpreter's file name inside a venv's script folder.</summary>
    public static string VenvPythonName => OperatingSystem.IsWindows() ? "python.exe" : "python3";

    /// <summary>A venv's script folder - the product's own rule.</summary>
    public static string VenvScriptsDir(string venv) => VenvRelocation.ScriptsDirOf(venv);

    /// <summary>A tool's console script in a venv script folder - the product's own rule.</summary>
    public static string ConsoleScriptIn(string scriptsDir, string tool) => PythonToolsInstaller.ConsoleScriptIn(scriptsDir, tool);

    /// <summary>Where the product puts a tool's shim: <c>bin\&lt;name&gt;.cmd</c> on Windows, a link in the shim folder elsewhere.</summary>
    public static string ShimPath(InstallLayout layout, string name) =>
        OperatingSystem.IsWindows() ? Path.Combine(layout.BinDir, name + ".cmd") : Path.Combine(layout.ToolShimDir, name);

    /// <summary>Pack <paramref name="stageDir"/> into <paramref name="assetPath"/> in this system's archive format.</summary>
    public static void Pack(string stageDir, string assetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            ZipFile.CreateFromDirectory(stageDir, assetPath);
            return;
        }
        using var file = File.Create(assetPath);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        TarFile.CreateFromDirectory(stageDir, gzip, includeBaseDirectory: false);
    }

    /// <summary>One <c>assets</c> entry of a release manifest for a packed asset, with its real size and hash.</summary>
    public static string AssetEntry(string assetPath, string version) =>
        $"\"{Path.GetFileName(assetPath)}\":{{\"version\":\"{version}\",\"sha256\":\"{Hashing.Sha256OfFile(assetPath)}\"," +
        $"\"platform\":\"{ManifestPlatform}\",\"size\":{new FileInfo(assetPath).Length}}}";

    /// <summary>An <c>assets</c> entry with a stated hash and size, for a release whose assets are deliberately wrong.</summary>
    public static string AssetEntry(string assetName, string version, string sha256, long size) =>
        $"\"{assetName}\":{{\"version\":\"{version}\",\"sha256\":\"{sha256}\",\"platform\":\"{ManifestPlatform}\",\"size\":{size}}}";

    private static readonly Regex TarArguments = new("^-xzf \"(?<archive>[^\"]+)\" -C \"(?<dest>[^\"]+)\"$");

    /// <summary>
    /// For a test's process runner: when the installer asks to run <c>tar</c> (macOS and Linux), unpack the archive
    /// here, in this process, and answer as tar would - so no test starts a real program. False for any other run.
    /// </summary>
    public static bool TryUnpack(string exe, string arguments, out (int exit, string output) result)
    {
        result = default;
        if (exe != PythonToolsInstaller.TarPath) return false;
        var match = TarArguments.Match(arguments);
        if (!match.Success) throw new InvalidOperationException($"unexpected tar arguments: {arguments}");
        try
        {
            using var file = File.OpenRead(match.Groups["archive"].Value);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, match.Groups["dest"].Value, overwriteFiles: true);
            result = (0, "");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            result = (2, $"tar: {ex.Message}");
        }
        return true;
    }
}
