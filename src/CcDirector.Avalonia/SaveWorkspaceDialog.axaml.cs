using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.ControlApi;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>One running session, as the Save dialog needs to see it.</summary>
/// <param name="DisplayName">What the rail calls it.</param>
/// <param name="RepoPath">Its repository.</param>
/// <param name="CustomName">The name the user gave it, or null.</param>
/// <param name="CustomColor">Its rail colour, or null.</param>
/// <param name="ClaudeArgs">Extra agent arguments, passed through literally.</param>
/// <param name="Agent">Which agent command line it runs.</param>
public record SessionData(
    string DisplayName,
    string RepoPath,
    string? CustomName,
    string? CustomColor,
    string? ClaudeArgs,
    string? Agent);

/// <summary>
/// Save the running sessions as a named workspace ON THE GATEWAY (issue #2722).
///
/// The saved object is the same one a drain produces - a set of seats - so what is written here can be
/// read by anything that reads a captured workspace, and vice versa. What this dialog writes is an
/// AUTHORED workspace: it has no Director, no drain states and no restore decisions, because no fleet was
/// drained to make it.
/// </summary>
public partial class SaveWorkspaceDialog : Window
{
    private readonly IWorkspaceCatalog _catalog;
    private readonly string? _importProblem;
    private readonly List<SaveSessionItem> _items;

    /// <summary>What was saved, or null when the user cancelled.</summary>
    public WorkspaceDocument? Result { get; private set; }

    /// <param name="catalog">Where the workspace is stored.</param>
    /// <param name="sessions">The running sessions offered for saving.</param>
    /// <param name="importProblem">Why the one-time import of this machine's older workspace files could
    /// not be done, or null. Shown, because it is the reason a name that IS already taken may not warn.</param>
    public SaveWorkspaceDialog(IWorkspaceCatalog catalog, IEnumerable<SessionData> sessions,
        string? importProblem = null)
    {
        FileLog.Write("[SaveWorkspaceDialog] Constructor");
        InitializeComponent();

        _catalog = catalog;
        _importProblem = importProblem;
        _items = sessions.Select((s, i) => new SaveSessionItem
        {
            IsSelected = true,
            DisplayName = s.DisplayName,
            RepoPath = s.RepoPath,
            CustomName = s.CustomName,
            CustomColor = s.CustomColor,
            ClaudeArgs = s.ClaudeArgs,
            Agent = s.Agent,
            SortOrder = i,
            HasColor = !string.IsNullOrWhiteSpace(s.CustomColor),
            ColorBrush = GetColorBrush(s.CustomColor)
        }).ToList();

        SessionListBox.ItemsSource = _items;

        // An entry point, so it carries the try/catch. LoadExistingIdsAsync handles its own catalog
        // failure; this guards everything else on the way out of an async void handler.
        Loaded += async (_, _) =>
        {
            try
            {
                await LoadExistingIdsAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SaveWorkspaceDialog] Loaded FAILED: {ex.Message}");
                TxtWarning.Text = ex.Message;
                TxtWarning.IsVisible = true;
            }
        };
    }

    /// <summary>Designer constructor.</summary>
    public SaveWorkspaceDialog() : this(null!, Array.Empty<SessionData>(), null) { }

    /// <summary>
    /// The ids already on the Gateway, so the overwrite warning can be shown as the user types. Fetched
    /// once when the window opens - the local-file version could ask the disk on every keystroke; this one
    /// must not put an HTTP call behind each one.
    /// </summary>
    private HashSet<string>? _existingIds;
    private string? _existingIdsProblem;

    /// <summary>
    /// The id the user has SAID they want to replace, or null.
    ///
    /// Overwriting is a decision somebody makes, not a thing that happens because a check did not run.
    /// The first Save is always create-only; if the Gateway answers that the id is taken - whether the
    /// list said so, said nothing, or could not be read at all - the dialog says so and this remembers
    /// which id was confirmed. It is cleared whenever the name changes, so a confirmation for one name
    /// can never authorise replacing a different workspace.
    /// </summary>
    private string? _overwriteConfirmedFor;

    private async System.Threading.Tasks.Task LoadExistingIdsAsync()
    {
        try
        {
            var summaries = await _catalog.ListAsync();
            _existingIds = summaries.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // SAY that the check could not be made. A null set with nothing on screen means the user
            // types a name that IS already taken, sees no warning, and overwrites a workspace - the
            // warning being absent looks exactly like the name being free.
            FileLog.Write($"[SaveWorkspaceDialog] LoadExistingIdsAsync FAILED: {ex.Message}");
            _existingIdsProblem =
                "Could not read the workspaces already on the Gateway, so you will not be warned if this " +
                $"name is already taken and would be overwritten: {ex.Message}";
        }

        OnNameChanged();
    }

    private static ISolidColorBrush GetColorBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.Transparent);

        try
        {
            var color = Color.Parse(hex);
            return new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e) => OnNameChanged();

    private void OnNameChanged()
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;

        // A NEW NAME IS A NEW DECISION. Whatever the user confirmed a moment ago was a confirmation
        // about one workspace; carrying it across a rename would replace a different one.
        if (_overwriteConfirmedFor is not null
            && !string.Equals(_overwriteConfirmedFor, WorkspaceSlug.From(name), StringComparison.OrdinalIgnoreCase))
        {
            _overwriteConfirmedFor = null;
        }

        // Save stays OFF until the overwrite check has an answer, either way. The local-file version
        // could ask the disk on every keystroke and so was never in this state; this one fetches once,
        // and between opening the window and the answer arriving a name that IS taken shows no warning -
        // which looks exactly like a name that is free.
        //
        // Enabling it when the check FAILED is safe now and was not before. It used to reach the same
        // unconditional replace as everything else, so an unread check became permission to overwrite;
        // the write below is create-only, so a name that turns out to be taken comes back as a question
        // instead of as a destroyed workspace.
        BtnSave.IsEnabled = !string.IsNullOrWhiteSpace(name)
            && (_existingIds is not null || _existingIdsProblem is not null);

        if (_importProblem is not null)
        {
            TxtWarning.Text = _importProblem;
            TxtWarning.IsVisible = true;
            return;
        }

        if (_existingIdsProblem is not null)
        {
            TxtWarning.Text = _existingIdsProblem;
            TxtWarning.IsVisible = true;
            return;
        }

        if (_existingIds is null)
        {
            TxtWarning.Text = "Reading the workspaces already on the Gateway...";
            TxtWarning.IsVisible = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(name) && _existingIds.Contains(WorkspaceSlug.From(name)))
        {
            // NOT "will be overwritten". Saving asks first, and this is an early notice so the user can
            // pick another name before they get there rather than a statement of what Save does.
            TxtWarning.Text =
                "A workspace with this name already exists. Save will ask before replacing it.";
            TxtWarning.IsVisible = true;
        }
        else
        {
            TxtWarning.IsVisible = false;
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = TxtName.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return;

            FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click: name={name}");

            var selected = _items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtWarning.Text = "Select at least one session to save.";
                TxtWarning.IsVisible = true;
                FileLog.Write("[SaveWorkspaceDialog] BtnSave_Click: no sessions selected");
                return;
            }

            var doc = new WorkspaceDocument
            {
                Id = WorkspaceSlug.From(name),
                Name = name,
                Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim(),
                Origin = WorkspaceOrigins.Authored,
                Seats = selected.Select(s => new WorkspaceSeat
                {
                    Name = !string.IsNullOrWhiteSpace(s.CustomName) ? s.CustomName! : s.DisplayName,
                    RepoPath = s.RepoPath,
                    // Null means the agent was never recorded, and every such session was created through
                    // the old default, which was Claude Code.
                    Agent = string.IsNullOrWhiteSpace(s.Agent) ? "ClaudeCode" : s.Agent!,
                    Color = s.CustomColor,
                    AgentArgs = s.ClaudeArgs,
                    SortOrder = s.SortOrder,
                }).ToList(),
            };

            BtnSave.IsEnabled = false;

            // CREATE-ONLY UNLESS THE USER SAID OTHERWISE. The check that runs when the window opens is
            // a snapshot and always was: a workspace created after it, by another Director or another
            // window, is not in it, and the dialog would show the name as free while replacing somebody
            // else's work. So the write itself refuses, and only a second click - after the sentence
            // below has been read - overwrites.
            if (string.Equals(_overwriteConfirmedFor, doc.Id, StringComparison.OrdinalIgnoreCase))
            {
                Result = await _catalog.SaveAsync(doc);
                FileLog.Write(
                    $"[SaveWorkspaceDialog] Workspace REPLACED on the user's confirmation: {name} " +
                    $"({selected.Count} seats)");
            }
            else
            {
                try
                {
                    Result = await _catalog.CreateAsync(doc);
                }
                catch (WorkspaceAlreadyExistsException)
                {
                    _overwriteConfirmedFor = doc.Id;
                    TxtWarning.Text =
                        $"A workspace named \"{name}\" is already on the Gateway. Save again to replace " +
                        "it, or change the name to keep both.";
                    TxtWarning.IsVisible = true;
                    BtnSave.IsEnabled = true;
                    FileLog.Write(
                        $"[SaveWorkspaceDialog] BtnSave_Click: '{doc.Id}' is taken; asking before replacing");
                    return;
                }

                FileLog.Write($"[SaveWorkspaceDialog] Workspace saved: {name} ({selected.Count} seats)");
            }

            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click FAILED: {ex.Message}");
            TxtWarning.Text = ex.Message;
            TxtWarning.IsVisible = true;
            BtnSave.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SaveWorkspaceDialog] BtnCancel_Click");
        Close(false);
    }

    /// <summary>One session in the list, with its tick.</summary>
    internal class SaveSessionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public string? CustomName { get; set; }
        public string? CustomColor { get; set; }
        public string? ClaudeArgs { get; set; }
        public string? Agent { get; set; }
        public int SortOrder { get; set; }
        public bool HasColor { get; set; }
        public ISolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Transparent);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
