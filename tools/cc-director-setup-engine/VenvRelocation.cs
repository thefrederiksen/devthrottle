using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Points a venv that was built in a staging folder at the folder it is about to be renamed to, so the tools venv
/// can be built BESIDE the live one and swapped in with a rename instead of being rebuilt in place.
///
/// A venv is not relocatable because it writes absolute paths in three places, and these are all of them:
/// <list type="bullet">
/// <item>Every console script starts with <c>#!&lt;venv&gt;/Scripts/python.exe</c> (<c>&lt;venv&gt;/bin/python3</c> off
/// Windows). On Windows the console script is a small launcher program with that line and a zip archive appended
/// to it; the launcher reads the line just before the archive, and the archive is found from its end, so the line
/// can change length without breaking it.</item>
/// <item><c>pyvenv.cfg</c> names the base Python it was made from (<c>home</c>, <c>executable</c>) and the venv
/// folder itself (<c>command</c>).</item>
/// <item>Off Windows, <c>bin/python</c> and its siblings are symbolic links to the base Python's interpreter.</item>
/// </list>
/// The activation scripts carry the venv folder too, and are rewritten with the console scripts, though nothing
/// in the product uses them.
///
/// It fails loudly rather than guessing: a console script that does not name the staged venv means the venv was
/// built some other way than this code expects, and swapping it in would leave a tool that cannot start.
/// </summary>
internal static class VenvRelocation
{
    /// <summary>A venv's script folder: <c>Scripts</c> on Windows, <c>bin</c> elsewhere.</summary>
    public static string ScriptsDirOf(string venvRoot) =>
        Path.Combine(venvRoot, OperatingSystem.IsWindows() ? "Scripts" : "bin");

    /// <summary>
    /// Rewrite every absolute path in the venv at <paramref name="stagedVenv"/> that names it or the staged base
    /// Python (<paramref name="stagedPython"/>) to the final folders (<paramref name="finalVenv"/>,
    /// <paramref name="finalPython"/>). Every file in <paramref name="requiredScripts"/> must name the staged venv
    /// before the rewrite; false, with the reason, when one does not or a file cannot be rewritten.
    /// </summary>
    public static bool Relocate(string stagedVenv, string finalVenv, string stagedPython, string finalPython,
        IReadOnlyList<string> requiredScripts, out string error)
    {
        ArgumentNullException.ThrowIfNull(requiredScripts);
        EngineLog.Write($"[VenvRelocation] Relocate: {stagedVenv} -> {finalVenv}, python {stagedPython} -> {finalPython}");
        error = "";
        var fromVenv = Encoding.UTF8.GetBytes(stagedVenv);
        var toVenv = Encoding.UTF8.GetBytes(finalVenv);

        foreach (var script in requiredScripts)
        {
            if (!File.Exists(script))
            {
                error = $"the console script {script} is missing";
                return false;
            }
            if (IndexOf(File.ReadAllBytes(script), fromVenv, 0) < 0)
            {
                error = $"the console script {script} does not name the staged environment {stagedVenv}, " +
                        "so it would not start once moved";
                return false;
            }
        }

        try
        {
            var scriptsDir = ScriptsDirOf(stagedVenv);
            foreach (var file in Directory.EnumerateFiles(scriptsDir))
            {
                if (new FileInfo(file).LinkTarget is not null) continue; // a link is re-pointed below, never written through
                var bytes = File.ReadAllBytes(file);
                var rewritten = Replace(bytes, fromVenv, toVenv);
                if (!ReferenceEquals(rewritten, bytes)) File.WriteAllBytes(file, rewritten);
            }

            var cfg = Path.Combine(stagedVenv, "pyvenv.cfg");
            if (!File.Exists(cfg))
            {
                error = $"{cfg} is missing";
                return false;
            }
            // The venv folder first: the staged Python's folder never contains it, but the reverse order would be
            // wrong if it ever did.
            var text = File.ReadAllText(cfg).Replace(stagedVenv, finalVenv, StringComparison.Ordinal)
                                            .Replace(stagedPython, finalPython, StringComparison.Ordinal);
            File.WriteAllText(cfg, text);

            if (!OperatingSystem.IsWindows()) RepointInterpreterLinks(scriptsDir, stagedPython, finalPython);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            EngineLog.Write($"[VenvRelocation] Relocate FAILED: {ex.Message}");
            return false;
        }
        EngineLog.Write("[VenvRelocation] Relocate: done");
        return true;
    }

    /// <summary>Off Windows, re-point every link in the script folder whose target is inside the staged base Python.</summary>
    private static void RepointInterpreterLinks(string scriptsDir, string stagedPython, string finalPython)
    {
        foreach (var file in Directory.EnumerateFileSystemEntries(scriptsDir))
        {
            var target = new FileInfo(file).LinkTarget;
            if (target is null || !target.StartsWith(stagedPython + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            File.Delete(file);
            File.CreateSymbolicLink(file, finalPython + target[stagedPython.Length..]);
        }
    }

    /// <summary>
    /// <paramref name="bytes"/> with every occurrence of <paramref name="from"/> replaced by <paramref name="to"/>;
    /// the same array instance when there is none, so the caller writes only files that changed.
    /// </summary>
    internal static byte[] Replace(byte[] bytes, byte[] from, byte[] to)
    {
        var at = IndexOf(bytes, from, 0);
        if (at < 0) return bytes;
        using var output = new MemoryStream(bytes.Length + Math.Max(0, to.Length - from.Length) * 4);
        var start = 0;
        while (at >= 0)
        {
            output.Write(bytes, start, at - start);
            output.Write(to, 0, to.Length);
            start = at + from.Length;
            at = IndexOf(bytes, from, start);
        }
        output.Write(bytes, start, bytes.Length - start);
        return output.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var found = haystack.AsSpan(start).IndexOf(needle);
        return found < 0 ? -1 : start + found;
    }
}
