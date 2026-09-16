namespace CcDirector.Setup.Engine;

/// <summary>
/// The version of a build that has been downloaded but not yet installed, recorded beside it by
/// whoever staged it.
///
/// WHY A STAGED BUILD CANNOT SIMPLY BE ASKED. <see cref="InstalledStateReader.ReadVersionFromDisk"/>
/// reads the Windows file-version stamp, or the <c>CFBundleShortVersionString</c> of a macOS
/// <c>.app</c> bundle. The Mac and Linux launchers are neither: they are bare single-file executables,
/// which carry no version resource of any kind. So on those platforms a staged launcher read as
/// "declares no version", and <see cref="LauncherUpdateOwner.FindStagedUpdate"/> refused it - correctly,
/// because a build that will not say what it is cannot be compared with what is installed, and
/// installing it anyway would mean not knowing afterwards whether it was the right one.
///
/// THE ANSWER IS NOT TO RUN IT. A launcher binary asked for its own version is a launcher STARTED: it
/// registers its own autostart and rewrites the launch agent underneath the one already running. A
/// capability check may not have consequences, and this one would have had two.
///
/// SO THE STAGER WRITES IT DOWN, AND THAT SOURCE IS BETTER THAN THE ALTERNATIVE ANYWAY. The version
/// comes from the release manifest, next to the SHA-256 the staged file was just verified against - a
/// signed statement about a file whose bytes have been checked. A version resource read out of a
/// downloaded binary is a claim the binary makes about itself, believed before anything has been
/// verified.
///
/// ONE MECHANISM ON ALL THREE PLATFORMS. Windows does not keep the file-stamp route "because it works
/// there": two ways to answer one question agree until the day one of them is edited, and the one that
/// only runs on the platform nobody tests on is the one that rots.
/// </summary>
public static class StagedBuildVersion
{
    /// <summary>The sidecar kept beside a staged build ("&lt;staged&gt;.version").</summary>
    public static string PathFor(string stagedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        return stagedPath + ".version";
    }

    /// <summary>
    /// Record what the build at <paramref name="stagedPath"/> is. Written AFTER the build itself, so a
    /// sidecar that exists always describes a file that exists - a reader that finds the version first
    /// and the binary second would otherwise install a truncated download.
    /// </summary>
    public static void Write(string stagedPath, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var path = PathFor(stagedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, version.Trim());
        EngineLog.Write($"[StagedBuildVersion] {stagedPath} is {version}");
    }

    /// <summary>
    /// What the staged build says it is, or null when nothing staged it - which is a refusal, not a
    /// gap to be filled in by guessing. A staged file with no sidecar was not put there by this
    /// product's staging path, and the caller must decline to install it rather than reach for a
    /// second way of asking.
    /// </summary>
    public static string? Read(string stagedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        var path = PathFor(stagedPath);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[StagedBuildVersion] could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Remove the sidecar. Absent is success; there is nothing to remove.</summary>
    public static void Delete(string stagedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        var path = PathFor(stagedPath);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { EngineLog.Write($"[StagedBuildVersion] could not remove {path}: {ex.Message}"); }
    }
}
