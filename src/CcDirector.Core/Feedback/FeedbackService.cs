using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Feedback;

/// <summary>
/// Turns a user's feedback (a title, a description, and an optional screenshot)
/// into a GitHub issue on the DevThrottle product repository. When a screenshot is
/// supplied it is committed to a dedicated assets branch first, then embedded in the
/// issue body so it renders inline. The notification email is delivered by GitHub
/// itself: anyone watching the repository (for example email@devthrottle.com) is
/// emailed the moment the issue is created, so this service does not send mail.
///
/// The orchestration lives here, behind <see cref="IGitHubClient"/>, so it can be
/// unit-tested without touching the network.
/// </summary>
public sealed class FeedbackService
{
    /// <summary>Owner of the product repository feedback issues are filed against.</summary>
    public const string Owner = "thefrederiksen";

    /// <summary>Name of the product repository feedback issues are filed against.</summary>
    public const string Repository = "devthrottle";

    /// <summary>
    /// Dedicated branch screenshots are committed to, so feedback images never land
    /// on the default branch or in the code history.
    /// </summary>
    public const string ScreenshotBranch = "feedback-assets";

    private readonly IGitHubClient _client;

    public FeedbackService(IGitHubClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>
    /// Submit feedback. Uploads the screenshot (when present), then creates the issue
    /// and returns it (number + html url). <paramref name="screenshotPng"/> is PNG
    /// bytes or null when the user chose not to attach one; <paramref name="environment"/>
    /// is optional diagnostic text appended to the body to help triage.
    /// </summary>
    public async Task<GhIssue> SubmitAsync(
        string title,
        string description,
        byte[]? screenshotPng,
        string? environment,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A feedback title is required.", nameof(title));

        FileLog.Write($"[FeedbackService] SubmitAsync: title={title}, hasScreenshot={screenshotPng is { Length: > 0 }}");

        var body = new StringBuilder();
        body.Append(string.IsNullOrWhiteSpace(description) ? "(no description provided)" : description.Trim());

        if (screenshotPng is { Length: > 0 })
        {
            // The path is unique per submission so it never collides with an existing
            // file on the branch (the Contents API would otherwise need the blob sha).
            var path = $"feedback/screenshots/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png";
            var downloadUrl = await _client.UploadFileAsync(
                Owner, Repository, ScreenshotBranch, path, screenshotPng,
                $"Add feedback screenshot for: {title}", ct);
            body.Append("\n\n");
            body.Append($"![screenshot]({downloadUrl})");
            FileLog.Write($"[FeedbackService] SubmitAsync: screenshot uploaded to {path}");
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            body.Append("\n\n---\n");
            body.Append(environment.Trim());
        }

        var issue = await _client.CreateIssueAsync(Owner, Repository, $"[Feedback] {title.Trim()}", body.ToString(), ct);
        FileLog.Write($"[FeedbackService] SubmitAsync: created issue #{issue.Number} at {issue.HtmlUrl}");
        return issue;
    }
}
