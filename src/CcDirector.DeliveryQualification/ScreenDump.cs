using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE SCREEN BESIDE THE RESULTS (issue #4378, the mission's 11.5): every refused send leaves the whole screen it was
/// refused on in <c>&lt;out&gt;/screens/</c>, named after its cell, so a refusal can be read later - a menu, a dialog, a
/// composer drawn where the reader did not look - instead of inferred from a log. The results line names the file.
/// </summary>
public static class ScreenDump
{
    private static readonly Regex Unsafe = new(@"[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

    /// <summary>Write every row of <paramref name="session"/>'s screen, numbered, to <c>&lt;outDir&gt;/screens/&lt;name&gt;.txt</c>
    /// and return the path relative to <paramref name="outDir"/>.</summary>
    public static string Save(string outDir, string name, Session session, string? heading = null)
    {
        var rows = session.SnapshotScreenRows();
        return Write(outDir, name, rows, heading ?? $"session {session.Id}, state {session.ActivityState}");
    }

    /// <summary>Write <paramref name="rows"/> as <see cref="Save"/> does. Pure apart from the file, so it is tested.</summary>
    internal static string Write(string outDir, string name, IReadOnlyList<string> rows, string heading)
    {
        var dir = Path.Combine(outDir, "screens");
        Directory.CreateDirectory(dir);
        var file = FileName(name);
        var sb = new StringBuilder();
        sb.Append("# ").Append(heading).Append(", saved ").AppendLine(DateTime.UtcNow.ToString("u"));
        for (var i = 0; i < rows.Count; i++)
            sb.Append(i.ToString("D2")).Append(" | ").AppendLine(rows[i].TrimEnd());
        File.WriteAllText(Path.Combine(dir, file), sb.ToString());
        return Path.Combine("screens", file);
    }

    /// <summary>A file name made of the cell's name: anything but letters, digits, dot, dash and underscore becomes a dash.</summary>
    internal static string FileName(string name) => Unsafe.Replace(name, "-").Trim('-') + ".txt";

    /// <summary>Whether a Director verdict is a refused or failed send, whose screen is saved.</summary>
    public static bool IsRefusal(string director) => director is not ("OK" or "DELIVERING");
}
