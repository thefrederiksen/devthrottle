using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Instances;

/// <summary>
/// What a cloned instance takes from the instance it is cloned from: its SETTINGS, never its identity or its
/// history. The owner asked for this on 2026-10-17, with issue devthrottle_internal#2489: every test Director now
/// needs a data folder of its own, and redoing every setting for each one is the cost that tempts people to
/// share a folder instead - which is how a test build cleared the live Director's sessions.json.
///
/// THE LIST IS WHAT IS COPIED, NOT WHAT IS SKIPPED. A file added to an instance home later is left behind
/// until someone decides it is a setting. Copying by exclusion would carry the next identity file across
/// without anyone noticing, and two Directors with one identity is the defect this exists to prevent.
///
/// Copied (relative to the instance home):
/// <list type="bullet">
/// <item><c>config/config.json</c> - the Gateway ADDRESS, agent settings, the screenshots folder, microphone
/// timings and onboarding state. Taken out: the session rail layout (it names the source's sessions), the browser
/// default (it opens the owner's signed-in profile), and the Gateway key (see below).</item>
/// <item><c>config/director/root-directories.json</c>, <c>repositories.json</c> and <c>agent-templates.json</c> -
/// the folders and repositories the Director offers, and the owner's own agent templates.</item>
/// <item><c>dictation/dictionary.yaml</c> - the hand-listed dictation corrections.</item>
/// </list>
///
/// THE GATEWAY KEY IS NOT COPIED, and the clone signs in once. A device key belongs to the Director id it was
/// enrolled for: the hosted Gateway refuses a team key presented by any other Director id, and a clone mints its
/// own id at first start, so a copied key would be refused and the clone would never join the fleet. A personal
/// key would be accepted, but then two Directors share one device and signing one out signs out both. So the
/// key file (<c>config/director/gateway-token.txt</c>), the key in <c>config.json</c> and the team it belongs to
/// (<c>gateway-team.json</c>) all stay behind.
///
/// Never copied: the Director's id (<c>director-id-*.txt</c>), sessions and their history, the crash journal,
/// instance registrations, delivery records, logs, caches and update state. Left out on purpose: Outlook
/// sign-ins, browser profiles and the voice key (<c>keyvault.json</c>) - the owner's real accounts and secrets.
/// </summary>
public static class InstanceSettingsCopy
{
    /// <summary>The files copied, relative to the instance home.</summary>
    public static readonly IReadOnlyList<string> CopiedFiles = new[]
    {
        Path.Combine("config", "config.json"),
        Path.Combine("config", "director", "root-directories.json"),
        Path.Combine("config", "director", "repositories.json"),
        Path.Combine("config", "director", "agent-templates.json"),
        Path.Combine("dictation", "dictionary.yaml"),
    };

    /// <summary>The top-level <c>config.json</c> keys removed from the copy.</summary>
    public static readonly IReadOnlyList<string> RemovedConfigKeys = new[] { "session_rail_order", "session_rail_expanded_crews", "browser" };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Copy the settings files from <paramref name="sourceHome"/> into <paramref name="targetHome"/>, replacing
    /// any there. A file the source does not have is skipped. Returns the files copied, relative to the home.
    /// </summary>
    public static IReadOnlyList<string> Copy(string sourceHome, string targetHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHome);
        if (string.Equals(Path.GetFullPath(sourceHome), Path.GetFullPath(targetHome), StringComparison.Ordinal))
            throw new InvalidOperationException($"An instance cannot be cloned into its own folder: {sourceHome}");

        FileLog.Write($"[InstanceSettingsCopy] Copy: from={sourceHome}, to={targetHome}");
        var copied = new List<string>();
        foreach (var relative in CopiedFiles)
        {
            var source = Path.Combine(sourceHome, relative);
            if (!File.Exists(source))
                continue;

            var target = Path.Combine(targetHome, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (string.Equals(Path.GetFileName(relative), "config.json", StringComparison.Ordinal))
                WriteAtomically(target, ConfigWithoutSessionKeys(File.ReadAllText(source)));
            else
                WriteAtomically(target, File.ReadAllText(source));
            copied.Add(relative);
        }

        FileLog.Write($"[InstanceSettingsCopy] Copy: copied {copied.Count} file(s): {string.Join(", ", copied)}");
        return copied;
    }

    /// <summary><c>config.json</c> text with <see cref="RemovedConfigKeys"/> and the Gateway key taken out. Pure, for tests. A file
    /// that is not a JSON object throws: a settings file nobody can read is not silently replaced.</summary>
    public static string ConfigWithoutSessionKeys(string configJson)
    {
        var node = JsonNode.Parse(configJson) as JsonObject
                   ?? throw new InvalidOperationException("config.json is not a JSON object, so it was not cloned");
        foreach (var key in RemovedConfigKeys)
            node.Remove(key);
        // The address stays so the clone knows where to sign in; the key belongs to the source's Director id.
        if (node["gateway"] is JsonObject gateway)
            gateway.Remove("token");
        return node.ToJsonString(WriteOptions);
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
