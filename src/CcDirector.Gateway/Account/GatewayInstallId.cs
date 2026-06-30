using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Loads or creates THIS Gateway's stable, per-machine install identity (issue #857).
///
/// The Gateway is one process per machine and needs a single stable identifier that survives restarts,
/// so that "sign in = register this device" (issue #857) is idempotent: the same install id is sent on
/// every registration and heartbeat, so the cloud (idempotent per member + install id) keeps exactly one
/// device record for this machine instead of creating a new one on each launch.
///
/// There was no pre-existing Gateway install id to reuse: the login-telemetry reporter accepts an
/// install id parameter (issues #57/#40) but the Gateway injects a no-op reporter, so that seam is never
/// populated on the Gateway, and the local pairing registry (issue #469) keys off client-supplied device
/// ids, not the Gateway's own. This store therefore establishes the ONE canonical Gateway identity rather
/// than minting a second competing id - and any future Gateway-side install-id need should read it here.
///
/// Persisted as a single file under the same per-user config root the local device key and the
/// Director identity already use:
///     %LOCALAPPDATA%\cc-director\config\director\gateway-install-id.txt
/// Locked to the current user by living under that per-user root.
/// </summary>
public static class GatewayInstallId
{
    /// <summary>The file name that holds the persisted Gateway install id.</summary>
    public const string FileName = "gateway-install-id.txt";

    /// <summary>The default on-disk path of the install-id file under the config root.</summary>
    public static string DefaultPath => Path.Combine(CcStorage.Config(), "director", FileName);

    /// <summary>
    /// Reads the persisted Gateway install id, minting and writing a fresh GUID once if the file is
    /// missing, empty, or malformed. Subsequent calls return the same id. Uses <see cref="DefaultPath"/>.
    /// </summary>
    public static string LoadOrCreate() => LoadOrCreate(DefaultPath);

    /// <summary>
    /// Reads the persisted Gateway install id from <paramref name="path"/>, minting and writing a fresh
    /// GUID once if the file is missing, empty, or malformed. Public so tests can drive an isolated path.
    /// </summary>
    /// <param name="path">The install-id file path. Required.</param>
    public static string LoadOrCreate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Install-id path is required", nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path).Trim();
            if (Guid.TryParse(raw, out var existing))
            {
                FileLog.Write($"[GatewayInstallId] LoadOrCreate: reusing install id={existing} path={path}");
                return existing.ToString();
            }
            FileLog.Write($"[GatewayInstallId] LoadOrCreate: file at {path} malformed, regenerating");
        }

        var fresh = Guid.NewGuid().ToString();
        File.WriteAllText(path, fresh);
        FileLog.Write($"[GatewayInstallId] LoadOrCreate: minted install id={fresh}, path={path}");
        return fresh;
    }
}
