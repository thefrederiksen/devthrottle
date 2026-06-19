using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Feedback;
using Xunit;

namespace CcDirector.Core.Tests.Feedback;

public sealed class FeedbackServiceTests
{
    [Fact]
    public async Task SubmitAsync_NoScreenshot_CreatesIssueWithoutUpload()
    {
        var stub = new StubGitHubClient();
        var service = new FeedbackService(stub);

        var issue = await service.SubmitAsync(
            "Terminal flickers", "It flickers when scrolling.", screenshotPng: null,
            environment: "Environment:\n- CC Director version: 1.2.3",
            CancellationToken.None);

        Assert.Empty(stub.Uploads);
        Assert.Equal(1, stub.CreateIssueCalls);
        Assert.Equal("[Feedback] Terminal flickers", stub.CreatedIssueTitle);
        Assert.Contains("It flickers when scrolling.", stub.CreatedIssueBody);
        Assert.Contains("CC Director version: 1.2.3", stub.CreatedIssueBody);
        Assert.DoesNotContain("![screenshot]", stub.CreatedIssueBody);
        Assert.Contains("thefrederiksen/devthrottle", issue.HtmlUrl);
    }

    [Fact]
    public async Task SubmitAsync_WithScreenshot_UploadsToAssetsBranchAndEmbeds()
    {
        var stub = new StubGitHubClient
        {
            UploadDownloadUrl = "https://raw.githubusercontent.com/thefrederiksen/devthrottle/feedback-assets/feedback/screenshots/x.png",
        };
        var service = new FeedbackService(stub);
        var png = new byte[] { 1, 2, 3, 4 };

        await service.SubmitAsync("Crash on launch", "Boom.", png, environment: null, CancellationToken.None);

        var upload = Assert.Single(stub.Uploads);
        Assert.Equal(FeedbackService.ScreenshotBranch, upload.Branch);
        Assert.StartsWith("feedback/screenshots/", upload.Path);
        Assert.EndsWith(".png", upload.Path);
        Assert.Equal(png.Length, upload.Bytes);
        Assert.Contains("![screenshot](https://raw.githubusercontent.com/thefrederiksen/devthrottle/feedback-assets/feedback/screenshots/x.png)",
            stub.CreatedIssueBody);
    }

    [Fact]
    public async Task SubmitAsync_EmptyDescription_UsesPlaceholder()
    {
        var stub = new StubGitHubClient();
        var service = new FeedbackService(stub);

        await service.SubmitAsync("Title only", "   ", screenshotPng: null, environment: null, CancellationToken.None);

        Assert.Contains("(no description provided)", stub.CreatedIssueBody);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAsync_BlankTitle_Throws(string title)
    {
        var stub = new StubGitHubClient();
        var service = new FeedbackService(stub);

        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            service.SubmitAsync(title, "desc", screenshotPng: null, environment: null, CancellationToken.None));

        Assert.Equal(0, stub.CreateIssueCalls);
    }
}
