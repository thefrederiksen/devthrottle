using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Source Control tab's container. Hosts a left page rail with two pages - "Changes"
/// (the existing per-session git file-changes view) and "Worktrees" (the repository's
/// worktree listing and, in a later phase, the reaper). New pages can be added to the rail
/// without disturbing the outer tab. Forwards the host lifecycle (Attach/Detach) to both
/// pages and re-raises the events the host cares about.
/// </summary>
public partial class SourceControlView : UserControl
{
    /// <summary>Forwarded from the Changes page so the host can open a file in the document viewer.</summary>
    public event Action<string>? ViewFileRequested;

    /// <summary>Raised when the worktree safe-to-reap count changes, so the host can badge the outer tab.</summary>
    public event Action<int>? OrphanedCountChanged;

    /// <summary>
    /// Supplies the live sessions on this machine (with working directories), so the Worktrees page
    /// can show a session-occupied worktree as "in use" and never reap it. Forwarded to that page.
    /// Set by the host, which knows the fleet.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider
    {
        get => WorktreesPage.LiveSessionsProvider;
        set => WorktreesPage.LiveSessionsProvider = value;
    }

    public SourceControlView()
    {
        InitializeComponent();
        ChangesPage.ViewFileRequested += path => ViewFileRequested?.Invoke(path);
        WorktreesPage.OrphanedCountChanged += OnWorktreesOrphanedCountChanged;
    }

    /// <summary>
    /// Point both pages at the repository. The rail resets to the default Changes page. The
    /// Worktrees page renders the background monitor's model (one brain); the Changes page keeps
    /// its own fast file-status poll (uncommitted files change second to second).
    /// </summary>
    public void Attach(RepositoryMonitor monitor, string repoPath)
    {
        FileLog.Write($"[SourceControlView] Attach: {repoPath}");
        ShowPage(worktrees: false);
        ChangesPage.Attach(repoPath);
        WorktreesPage.Attach(monitor, repoPath);
    }

    /// <summary>The host just switched to the Source Control tab. The Changes page stops polling
    /// while it cannot be seen, so it is told to refresh at once when it can be again.</summary>
    public void Shown()
    {
        if (ChangesPage.IsVisible)
            ChangesPage.Shown();
    }

    /// <summary>Tear down both pages when the session context goes away.</summary>
    public void Detach()
    {
        FileLog.Write("[SourceControlView] Detach");
        ChangesPage.Detach();
        WorktreesPage.Detach();
    }

    private void ChangesRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage(worktrees: false);

    private void WorktreesRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage(worktrees: true);

    private void ShowPage(bool worktrees)
    {
        bool changesWasHidden = !ChangesPage.IsVisible;
        ChangesPage.IsVisible = !worktrees;
        WorktreesPage.IsVisible = worktrees;
        if (!worktrees && changesWasHidden)
            ChangesPage.Shown();

        SetActive(ChangesRailButton, !worktrees);
        SetActive(WorktreesRailButton, worktrees);
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
            button.Classes.Add("active");
        else
            button.Classes.Remove("active");
    }

    private void OnWorktreesOrphanedCountChanged(int count)
    {
        WorktreesRailBadgeText.Text = count.ToString();
        WorktreesRailBadge.IsVisible = count > 0;
        OrphanedCountChanged?.Invoke(count);
    }
}
