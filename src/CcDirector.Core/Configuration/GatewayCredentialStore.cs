using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Writes the credential a device received at enrollment (issue #469) to disk. After a successful
/// pairing the Director holds a unique per-device key; this store persists it to the SAME
/// credential file the Director's Control API and the local cc-* tools both read
/// (<c>%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt</c>), and records the Gateway
/// URL + the key in <c>config.json</c> so the running registration/heartbeat client presents the
/// per-device key as its Bearer.
///
/// One file, two readers (per SECURITY_FLOWS.html): the Director presents the key to the Gateway,
/// and the local agents/CLI present the same key to the loopback Control API.
/// </summary>
public static class GatewayCredentialStore
{
    /// <summary>
    /// The credential file the Director Control API and local cc-* tools read. Kept in lockstep
    /// with <c>GatewayAuth.TokenFile</c>.
    /// </summary>
    public static string CredentialFile { get; } =
        Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    /// <summary>
    /// Persist the per-device key issued at enrollment: write it to the local credential file and
    /// record the Gateway URL + key in config.json's gateway block. After this, both the Director's
    /// Gateway client and the local cc-* tools authenticate with the per-device key.
    ///
    /// Connecting to a Gateway also turns the persistent stream ON (<c>streamMode: true</c>): the
    /// stream is how a Director joins the Gateway now (issue #1176), so every enroll/connect path -
    /// the installer's account enroll, the in-app Connect-to-Gateway dialog, and the phone/Cockpit
    /// enroll - lands the same stream-enabled gateway block. <see cref="GatewayConfig.StreamMode"/>
    /// is otherwise opt-in and defaults off, so a Director that never connected is unchanged. The
    /// merge is deep (<see cref="CcDirectorConfigService.MergePatch"/>), so a hand-set
    /// <c>staleAfterSeconds</c> or other gateway keys survive.
    /// </summary>
    public static void SaveEnrolledKey(string gatewayUrl, string deviceKey)
        => Save(CredentialFile, CcStorage.ConfigJson(), gatewayUrl, deviceKey);

    /// <summary>
    /// <see cref="SaveEnrolledKey"/> for a Director whose storage home is <paramref name="storageRoot"/>
    /// rather than this process's own: the same two writes, to <c>&lt;root&gt;/config/director/gateway-token.txt</c>
    /// and <c>&lt;root&gt;/config/config.json</c>.
    ///
    /// The setup command line's <c>enroll</c> is the caller. It runs before any Director has started,
    /// from the machine root, while the Director it is connecting reads ONLY its own home
    /// (<c>instances\default</c>) - so the plain <see cref="SaveEnrolledKey"/> from the command line
    /// landed the connection where nothing reads it, and the Director started with no Gateway (issue
    /// #3506). The caller names the home; this store does not guess it.
    /// </summary>
    public static void SaveEnrolledKeyAt(string storageRoot, string gatewayUrl, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("storageRoot is required", nameof(storageRoot));
        var config = Path.Combine(storageRoot, "config");
        Save(Path.Combine(config, "director", "gateway-token.txt"), Path.Combine(config, "config.json"),
            gatewayUrl, deviceKey);
    }

    private static void Save(string credentialFile, string configJsonPath, string gatewayUrl, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("gatewayUrl is required", nameof(gatewayUrl));
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("deviceKey is required", nameof(deviceKey));

        var dir = Path.GetDirectoryName(credentialFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(credentialFile, deviceKey);
        FileLog.Write($"[GatewayCredentialStore] Wrote per-device key to {credentialFile}");

        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["url"] = gatewayUrl.Trim(),
                ["token"] = deviceKey,
                // The stream is the connection method (issue #1176): enabling it on connect makes a
                // freshly-enrolled Director join over the Gateway's director-stream instead of the
                // legacy pull/heartbeat path. Byte-identical for anyone who never connects.
                ["streamMode"] = true,
            },
        };
        CcDirectorConfigService.MergePatchAt(configJsonPath, patch);
        FileLog.Write($"[GatewayCredentialStore] Recorded gateway url + per-device key + streamMode=true in {configJsonPath} (url={gatewayUrl})");
    }

    /// <summary>
    /// Disconnect this Director from its Gateway: the inverse of <see cref="SaveEnrolledKey"/>. Deletes the
    /// local per-device credential file (the key that binds this Director to the Gateway) and clears the
    /// gateway block in config.json - the active <c>url</c>, the discovered <c>urls</c> fallback list, the
    /// <c>token</c>, the <c>tailnetEndpoint</c> override, and turns the persistent stream off. After this,
    /// <see cref="GatewayConfig.Load"/> reports local-only (<see cref="GatewayConfig.IsEnabled"/> is false),
    /// so the Director stops presenting a per-device key and the connect flow can point it at a different
    /// Gateway (local or hosted).
    ///
    /// It is the exact inverse of <see cref="SaveEnrolledKey"/>, so it deletes the SAME
    /// <see cref="CredentialFile"/> that save wrote (not a freshly-recomputed path): the credential a save
    /// persisted is precisely the one a clear removes. MergePatch cannot remove keys, so the connection
    /// fields are blanked rather than deleted; a blank <c>url</c> is exactly what local-only mode reads.
    /// </summary>
    public static void ClearConnection()
    {
        FileLog.Write("[GatewayCredentialStore] ClearConnection: disconnecting this Director from its Gateway");

        // The SAME file SaveEnrolledKey writes - so save and clear are a true inverse pair on one path.
        if (File.Exists(CredentialFile))
        {
            File.Delete(CredentialFile);
            FileLog.Write($"[GatewayCredentialStore] Deleted per-device key file {CredentialFile}");
        }

        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["url"] = "",
                ["urls"] = new JsonArray(),
                ["token"] = "",
                ["tailnetEndpoint"] = "",
                ["streamMode"] = false,
            },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[GatewayCredentialStore] Cleared gateway url + urls + token + tailnetEndpoint + streamMode in config.json (disconnected)");
    }
}
