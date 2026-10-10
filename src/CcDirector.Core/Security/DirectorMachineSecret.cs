using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Security;

/// <summary>
/// The ONE secret a machine's Director accepts, and the only place that decides which one it is.
///
/// Two sources, in this order:
///   1. the SHARED fleet token (<c>gateway.token</c> in config.json) when this machine is attached
///      to a Gateway - the Gateway presents it on every proxied call, so the Director must accept it;
///   2. otherwise this machine's own persisted token,
///      <c>config/director/gateway-token.txt</c>, generated on first use.
///
/// This lives in Core rather than beside the middleware because everything that has to PRESENT the
/// credential is outside the web layer: the launcher stopping the Director, the desktop's own
/// self-probe, the command line. Each of those previously spelled the path out for itself and read
/// only the token FILE, which is correct on a standalone machine and silently wrong on a
/// Gateway-attached one - a mismatch that could not be noticed while the Control API accepted
/// everybody.
/// </summary>
public static class DirectorMachineSecret
{
    /// <summary>
    /// This machine's own persisted secret. Computed fresh rather than cached in a static, so it
    /// honours the current storage root - a test that redirects the root must not be answered with
    /// the real machine's path.
    /// </summary>
    public static string TokenFile => Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    /// <summary>
    /// The secret to accept, given whatever fleet token this Director is configured with. Pure, so
    /// the decision can be tested without a filesystem when the fleet token is present.
    /// </summary>
    public static string Resolve(string? fleetToken)
        => string.IsNullOrWhiteSpace(fleetToken) ? LoadOrCreate() : fleetToken.Trim();

    /// <summary>
    /// The secret a SPECIFIC Director instance accepts, read from that instance's storage root - for
    /// out-of-process callers (the launcher, admin tooling) addressing one Director on a machine that
    /// may hold several, each with its whole storage under <c>instances/&lt;slug&gt;</c>. Same order
    /// as <see cref="Resolve"/>: the shared fleet token configured in that root's
    /// <c>config/config.json</c> wins, otherwise that root's own persisted token file.
    ///
    /// READ-ONLY on purpose, unlike <see cref="LoadOrCreate"/>: a client must never mint the
    /// server's secret file into existence - a token file the Director did not write verifies
    /// nothing, and creating one plants exactly the kind of stale stray that used to mask this bug.
    /// An instance with no readable secret answers null and the caller sends no credential.
    /// </summary>
    public static string? TryReadFrom(string storageRoot)
    {
        try
        {
            var configJson = Path.Combine(storageRoot, "config", "config.json");
            if (File.Exists(configJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configJson));
                    if (doc.RootElement.TryGetProperty("gateway", out var gateway)
                        && gateway.ValueKind == System.Text.Json.JsonValueKind.Object
                        && gateway.TryGetProperty("token", out var token)
                        && token.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(token.GetString()))
                    {
                        return token.GetString()!.Trim();
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    // A malformed config.json is not the answer to "what is the secret" - the token
                    // file below is where a standalone Director keeps it. Same order Resolve applies.
                    FileLog.Write($"[DirectorMachineSecret] TryReadFrom FAILED: {configJson} is not readable JSON ({ex.Message}); reading the token file instead");
                }
            }

            var tokenFile = Path.Combine(storageRoot, "config", "director", "gateway-token.txt");
            if (File.Exists(tokenFile))
            {
                var value = File.ReadAllText(tokenFile).Trim();
                if (value.Length > 0)
                    return value;
            }
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorMachineSecret] TryReadFrom({storageRoot}) FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read this machine's secret from disk; generate and persist one if absent.</summary>
    public static string LoadOrCreate()
    {
        var path = TokenFile;
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    FileLog.Write($"[DirectorMachineSecret] Loaded token from {path}");
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var token = Generate();
            File.WriteAllText(path, token);
            FileLog.Write($"[DirectorMachineSecret] Generated new token at {path}");
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorMachineSecret] LoadOrCreate FAILED: {ex.Message}");
            throw;
        }
    }

    private static string Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
