using System.Diagnostics;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Opens a PRE-FILLED "new issue" page on the cc-director GitHub repo so a user can report an install
/// problem in one click. No GitHub token is embedded (a shipped installer is a public download, so a
/// token would leak); instead the installer fills in the title, labels, and body (environment + status
/// + log tail) and the user - already signed in to GitHub - clicks Submit. The issue lands directly in
/// the repo, labeled "installation". Cross-platform open (Windows/macOS/Linux).
/// </summary>
public static class IssueReporter
{
    public const string NewIssueBase = "https://github.com/thefrederiksen/cc-director/issues/new";

    // GitHub/browsers reject very long URLs; keep the whole thing well under the practical ~8 KB limit.
    private const int MaxUrlLength = 7500;

    /// <summary>
    /// Build a GitHub new-issue URL with the title, label, and body pre-filled. If the encoded URL would
    /// exceed the length limit, the body is trimmed from the END (so the header/environment survives and
    /// only the oldest log lines are dropped - the user is told to attach the full log anyway).
    /// </summary>
    public static string BuildUrl(string title, string body, string label = "installation")
    {
        var encodedTitle = Uri.EscapeDataString(title ?? "");
        var encodedLabel = Uri.EscapeDataString(label ?? "");
        var b = body ?? "";

        string url;
        while (true)
        {
            url = $"{NewIssueBase}?labels={encodedLabel}&title={encodedTitle}&body={Uri.EscapeDataString(b)}";
            if (url.Length <= MaxUrlLength || b.Length == 0) break;
            // Drop the last 10% (oldest log lines live at the bottom of the body's "tail" block).
            b = b[..Math.Max(0, (int)(b.Length * 0.9) - 1)];
        }
        return url;
    }

    /// <summary>Open a URL in the user's default browser, cross-platform.</summary>
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        // ArgumentList avoids any shell-quoting issues with the '&' chars in the URL.
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
        psi.ArgumentList.Add(url);
        Process.Start(psi);
    }
}
