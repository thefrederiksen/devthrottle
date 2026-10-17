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
/// <item><c>config/config.json</c> - the Gateway address and key, agent settings, the screenshots folder,
/// microphone timings and onboarding state; less the session rail layout, which names the source's sessions.</item>
/// <item><c>config/director/gateway-token.txt</c> - the device key the local tools read. A new instance made by
/// hand already takes the Gateway address and key (<see cref="NamedInstanceRegistry.Create"/>).</item>
/// <item><c>config/director/root-directories.json</c> and <c>config/director/repositories.json</c> - the folders
/// and repositories the Director offers.</item>
/// </list>
/// Never copied: the Director's id (<c>director-id-*.txt</c>), sessions and their history, the crash journal,
/// instance registrations, delivery records, logs, caches and update state - what makes a Director THIS one.
/// Left out on purpose: Outlook sign-ins and browser profiles. They are the owner's real accounts, and a test
/// Director must not send mail or browse as them by accident; sign in again where a test needs it.
/// </summary>
public static class InstanceSettingsCopy
{
    /// <summary>The files copied, relative to the instance home.</summary>
    public static readonly IReadOnlyList<string> CopiedFiles = new[]
    {
        Path.Combine("config", "config.json"),
        Path.Combine("config", "director", "gateway-token.txt"),
        Path.Combine("config", "director", "root-directories.json"),
        Path.Combine("config", "director", "repositories.json"),
    };

    /// <summary>The <c>config.json</c> keys removed from the copy: they name the source's own sessions.</summary>
    public static readonly IReadOnlyList<string> RemovedConfigKeys = new[] { "session_rail_order", "session_rail_expanded_crews" };

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

    /// <summary><c>config.json</c> text with <see cref="RemovedConfigKeys"/> taken out. Pure, for tests. A file
    /// that is not a JSON object throws: a settings file nobody can read is not silently replaced.</summary>
    public static string ConfigWithoutSessionKeys(string configJson)
    {
        var node = JsonNode.Parse(configJson) as JsonObject
                   ?? throw new InvalidOperationException("config.json is not a JSON object, so it was not cloned");
        foreach (var key in RemovedConfigKeys)
            node.Remove(key);
        return node.ToJsonString(WriteOptions);
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
