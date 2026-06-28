using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CcDirector.Core.Backends;
using CcDirector.Core.Feedback;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Collects feedback (title, description, optional screenshot of the CC Director
/// window) and files it as a GitHub issue via <see cref="FeedbackService"/>. The
/// screenshot is captured from the owner window's visual tree with
/// <see cref="RenderTargetBitmap"/>, so this modal dialog never appears in it.
/// </summary>
public partial class FeedbackDialog : Window
{
    private readonly Window _owner;
    private byte[]? _screenshotPng;

    public FeedbackDialog(Window owner)
    {
        FileLog.Write("[FeedbackDialog] Constructor: initializing");
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        InitializeComponent();
        Opened += OnOpened;
    }

    // Land the cursor in the title field so the user can type immediately.
    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        try
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FeedbackDialog] OnOpened FAILED: {ex.Message}");
        }
    }

    private void BtnAttachScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FeedbackDialog] BtnAttachScreenshot_Click: capturing main window");
        try
        {
            var scaling = _owner.RenderScaling;
            var clientSize = _owner.ClientSize;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(clientSize.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(clientSize.Height * scaling)));
            var dpi = new Vector(96 * scaling, 96 * scaling);

            using var rtb = new RenderTargetBitmap(pixelSize, dpi);
            rtb.Render(_owner);

            using var ms = new MemoryStream();
            rtb.Save(ms);
            _screenshotPng = ms.ToArray();

            using var preview = new MemoryStream(_screenshotPng);
            ScreenshotPreview.Source = new Bitmap(preview);
            ScreenshotPreviewBorder.IsVisible = true;
            BtnRemoveScreenshot.IsVisible = true;
            BtnAttachScreenshot.Content = "Recapture screenshot";
            ShowStatus($"Screenshot attached ({_screenshotPng.Length / 1024} KB).", isError: false);
            FileLog.Write($"[FeedbackDialog] BtnAttachScreenshot_Click: captured {_screenshotPng.Length} bytes");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FeedbackDialog] BtnAttachScreenshot_Click FAILED: {ex}");
            ShowStatus($"Could not capture screenshot: {ex.Message}", isError: true);
        }
    }

    private void BtnRemoveScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FeedbackDialog] BtnRemoveScreenshot_Click");
        _screenshotPng = null;
        ScreenshotPreview.Source = null;
        ScreenshotPreviewBorder.IsVisible = false;
        BtnRemoveScreenshot.IsVisible = false;
        BtnAttachScreenshot.Content = "Attach screenshot of Director";
        StatusText.IsVisible = false;
    }

    private async void BtnSubmit_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FeedbackDialog] BtnSubmit_Click: submitting feedback");
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            ShowStatus("Please enter a title before submitting.", isError: true);
            return;
        }

        SetBusy(true);
        ShowStatus("Submitting feedback...", isError: false);
        try
        {
            var description = DescriptionBox.Text ?? string.Empty;
            var environment = BuildEnvironmentInfo();
            var screenshot = _screenshotPng;

            var issue = await Task.Run(async () =>
            {
                var token = GitHubCredentials.ReadToken();
                using var client = new GitHubRestClient(token);
                var service = new FeedbackService(client);
                return await service.SubmitAsync(title, description, screenshot, environment, CancellationToken.None);
            });

            FileLog.Write($"[FeedbackDialog] BtnSubmit_Click: submitted as issue #{issue.Number}");
            // The owner shows the confirmation toast; the dialog's job is done, so close it.
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FeedbackDialog] BtnSubmit_Click FAILED: {ex}");
            ShowStatus($"Could not submit feedback: {ex.Message}", isError: true);
            SetBusy(false);
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FeedbackDialog] BtnCancel_Click: closing");
        Close(false);
    }

    /// <summary>Diagnostic footer appended to the issue body to help triage.</summary>
    private static string BuildEnvironmentInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Environment:");
        sb.AppendLine($"- Director version: {AppVersion.Display}");
        sb.AppendLine($"- Operating system: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        return sb.ToString();
    }

    private void SetBusy(bool busy)
    {
        BtnSubmit.IsEnabled = !busy;
        BtnCancel.IsEnabled = !busy;
        BtnAttachScreenshot.IsEnabled = !busy;
        BtnRemoveScreenshot.IsEnabled = !busy;
        TitleBox.IsEnabled = !busy;
        DescriptionBox.IsEnabled = !busy;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(isError ? "#F48771" : "#AAAAAA"));
        StatusText.IsVisible = true;
    }
}
