using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// Resolves a work-list item's display title and status from GitHub (issue #275). The Cockpit Lists
/// view is a CLIENT of two sources: the Gateway holds the list object (order + refs), and GitHub
/// holds the truth about each github item (its title and its <c>flow:*</c> label). This client owns
/// only the second half: given an item's source/id it returns a <see cref="WorkItemInfo"/>.
///
/// Status is DERIVED from the flow label (ASSUMPTION D1 in #275), never cached separately - so the
/// badge always follows the label. The mapping:
///   no flow label / flow:ready-dev   -> Queued
///   flow:ready-qa / flow:qa-failed    -> Running   (the loop is still on it)
///   flow:done                         -> Done
///   flow:needs-human                  -> NeedsHuman
///   flow:failed                       -> Failed
/// Non-github sources (devops/jira) have no flow label and resolve to Queued without a network call.
/// </summary>
public sealed class GitHubItemStatusClient
{
    // The canonical repo github work-items live in (same constant the rest of the app uses for
    // cc-director issues, e.g. StateVoteService). v1 lists only carry cc-director github items.
    private const string Owner = "thefrederiksen";
    private const string Repo = "cc-director";
    private const string TokenKey = "GITHUB_TOKEN";

    private readonly HttpClient _http;
    private readonly ILogger<GitHubItemStatusClient> _log;

    public GitHubItemStatusClient(HttpClient http, ILogger<GitHubItemStatusClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Resolve title + status for one item ref. A non-github source resolves to Queued (no call).
    /// A github item is fetched from GitHub and mapped from its flow label. Never throws: an
    /// unreachable GitHub or a missing token returns <see cref="WorkItemStatus.Unknown"/> with a
    /// human-readable Detail, so the list/order still renders (criterion 6) and the failure is
    /// shown explicitly rather than masked as a wrong "queued" (no-fallback rule).
    /// </summary>
    public async Task<WorkItemInfo> ResolveAsync(string source, string id, CancellationToken ct = default)
    {
        if (!string.Equals(source, "github", StringComparison.OrdinalIgnoreCase))
            return new WorkItemInfo(Title: null, Status: WorkItemStatus.Queued, Detail: $"{source} item (not runnable in v1)");

        var token = TryReadToken(out var tokenError);
        if (token is null)
        {
            _log.LogWarning("ResolveAsync: github id={Id} - no token: {Error}", id, tokenError);
            return new WorkItemInfo(Title: null, Status: WorkItemStatus.Unknown, Detail: tokenError);
        }

        try
        {
            return await FetchAsync(id, token, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "ResolveAsync: github id={Id} transport failure", id);
            return new WorkItemInfo(Title: null, Status: WorkItemStatus.Unknown, Detail: $"GitHub unreachable: {ex.Message}");
        }
    }

    private async Task<WorkItemInfo> FetchAsync(string id, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Owner}/{Repo}/issues/{Uri.EscapeDataString(id)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("cc-director-cockpit");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new WorkItemInfo(Title: null, Status: WorkItemStatus.Unknown, Detail: $"GitHub issue #{id} not found in {Owner}/{Repo}");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new WorkItemInfo(Title: null, Status: WorkItemStatus.Unknown,
                Detail: $"GitHub returned {(int)resp.StatusCode} for #{id}: {Truncate(body, 120)}");
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        var labels = new List<string>();
        if (root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labelsEl.EnumerateArray())
            {
                if (label.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name)) labels.Add(name);
                }
            }
        }

        return new WorkItemInfo(title, MapStatus(labels), Detail: null);
    }

    /// <summary>
    /// Map an issue's flow:* labels to a status badge (ASSUMPTION D1). When several flow labels are
    /// present the terminal/escalation states win over the in-flight ones, which win over queued.
    /// </summary>
    internal static WorkItemStatus MapStatus(IReadOnlyCollection<string> labels)
    {
        bool Has(string label) => labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));

        if (Has("flow:done")) return WorkItemStatus.Done;
        if (Has("flow:needs-human")) return WorkItemStatus.NeedsHuman;
        if (Has("flow:failed")) return WorkItemStatus.Failed;
        // The loop is still on the item while it is in QA (qa-failed is a transient mid-loop bounce).
        if (Has("flow:ready-qa") || Has("flow:qa-failed")) return WorkItemStatus.Running;
        // No flow label, or flow:ready-dev / flow:rejected: not yet drained.
        return WorkItemStatus.Queued;
    }

    /// <summary>
    /// Read GITHUB_TOKEN from the shared credentials file at point of use (so the secret only enters
    /// the process when a github item is actually resolved). Returns null + a fixable message when
    /// the file or key is absent - no silent fallback to an empty token.
    /// </summary>
    private static string? TryReadToken(out string error)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, "cc-director", "config", "credentials.env");
        if (!File.Exists(path))
        {
            error = $"GITHUB_TOKEN not configured (no {path}); per-item GitHub status unavailable.";
            return null;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(line[..eq].Trim(), TokenKey, StringComparison.Ordinal)) continue;
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (string.IsNullOrEmpty(value))
            {
                error = $"{TokenKey} is present in {path} but empty.";
                return null;
            }
            error = "";
            return value;
        }

        error = $"{TokenKey} not found in {path}.";
        return null;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
