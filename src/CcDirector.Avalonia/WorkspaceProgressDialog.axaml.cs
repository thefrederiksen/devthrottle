using Avalonia.Controls;

namespace CcDirector.Avalonia;

public partial class WorkspaceProgressDialog : Window
{
    public WorkspaceProgressDialog(string workspaceName)
    {
        InitializeComponent();
        StatusText.Text = $"Loading workspace \"{workspaceName}\"...";
    }

    public void UpdateProgress(int current, int total, string sessionName)
    {
        StatusText.Text = $"Loading workspace - session {current}/{total}";
        ProgressBar.Value = (double)current / total * 100;
        DetailText.Text = sessionName;
    }

    public void SetClosing()
    {
        StatusText.Text = "Closing existing sessions...";
        ProgressBar.IsIndeterminate = true;
        DetailText.Text = "";
    }

    /// <summary>Every seat came up. The only path that may say this.</summary>
    public void SetComplete()
    {
        StatusText.Text = "Workspace loaded";
        ProgressBar.Value = 100;
        DetailText.Text = "";
    }

    /// <summary>
    /// The load did NOT finish, and this says which seats are missing and why.
    ///
    /// It exists because the window used to say "Workspace loaded" unconditionally, including after the
    /// running fleet had been closed and nothing came up in its place. A destructive operation that
    /// partially completes and reports success is the worst shape this product has: the user has lost
    /// what they had, has not got what they asked for, and has been told it worked.
    /// </summary>
    /// <param name="started">How many seats came up.</param>
    /// <param name="total">How many the workspace names.</param>
    /// <param name="detail">Which seats did not, and why.</param>
    public void SetIncomplete(int started, int total, string detail)
    {
        StatusText.Text = $"Workspace only partly started - {started} of {total} sessions";
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = total == 0 ? 0 : (double)started / total * 100;
        DetailText.Text = detail;
    }
}
