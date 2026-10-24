using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Pi;

/// <summary>Installs the Pi lifecycle extension carried inside the Director and returns the path passed to Pi.</summary>
internal static class PiStateExtensionInstaller
{
    internal const string ExtensionFileName = "state-reporter.ts";
    private const string ResourceName = "CcDirector.Core.Pi.state-reporter.ts";

    /// <summary>Install the extension under the Director's data root.</summary>
    internal static string? EnsureInstalled() => EnsureInstalled(CcStorage.PiExtensions());

    /// <summary>Install the extension under an explicit directory, for an isolated Director or a test.</summary>
    internal static string? EnsureInstalled(string directory)
    {
        FileLog.Write($"[PiStateExtensionInstaller] EnsureInstalled: directory={directory}");
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, ExtensionFileName);
            var content = ExtensionSource();
            if (File.Exists(destination) && File.ReadAllText(destination) == content)
            {
                FileLog.Write($"[PiStateExtensionInstaller] EnsureInstalled: current extension already installed at {destination}");
                return destination;
            }

            var temporary = destination + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            FileLog.Write($"[PiStateExtensionInstaller] EnsureInstalled: installed extension at {destination}");
            return destination;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PiStateExtensionInstaller] EnsureInstalled FAILED: directory={directory}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>Append Pi's explicit extension argument when installation succeeded.</summary>
    internal static string AppendLaunchArgument(string arguments, string? extensionPath)
    {
        FileLog.Write($"[PiStateExtensionInstaller] AppendLaunchArgument: installed={!string.IsNullOrWhiteSpace(extensionPath)}");
        var result = string.IsNullOrWhiteSpace(extensionPath)
            ? arguments
            : $"{arguments} --extension \"{extensionPath}\"".Trim();
        FileLog.Write($"[PiStateExtensionInstaller] AppendLaunchArgument: resultLength={result.Length}");
        return result;
    }

    internal static string ExtensionSource()
    {
        using var stream = typeof(PiStateExtensionInstaller).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded Pi state extension '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
