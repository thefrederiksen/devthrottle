using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// THE BUTTON (issue #2723). The drain is a long operation that closes every session on this Director, so
/// it gets a screen rather than a menu item that goes quiet for an hour.
///
/// The window owns nothing except the words. Every decision - who is messaged, what may be closed and
/// when, whether a restart may proceed - is made by <see cref="DirectorDrain"/>, and this renders what it
/// reports. That is the same rule the rest of the product follows for a reason: a screen that decided any
/// of this for itself would be a second opinion nobody could see.
/// </summary>
public partial class DrainDirectorDialog : Window
{
    private readonly ControlApiHost? _host;
    private readonly string _directorName;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>Designer constructor.</summary>
    public DrainDirectorDialog() : this(null, "this Director") { }

    /// <summary>
    /// Create the dialog.
    /// </summary>
    /// <param name="host">The Director's service host, which owns the Gateway connection and the sessions.
    /// Null when there is none, and the dialog then says so instead of offering a button that cannot work.</param>
    /// <param name="directorName">This Director's display name, used in the workspace id and the words.</param>
    public DrainDirectorDialog(ControlApiHost? host, string directorName)
    {
        InitializeComponent();
        _host = host;
        _directorName = string.IsNullOrWhiteSpace(directorName) ? "this Director" : directorName;
        Title = $"Drain {_directorName}";
        TxtReport.Text =
            "Nothing has happened yet. Press \"Start the drain\" and every session on this Director is "
            + "asked to write a handover.";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_running) return;

            var drain = _host?.CreateDrain(OnProgress);
            if (drain is null)
            {
                // Not a degraded mode. The record must live off this machine, because the moment it is
                // worth having is the moment this machine has been restarted out from under its fleet.
                TxtReport.Text =
                    "This Director is not connected to a Gateway, so a drain cannot start.\n\n"
                    + "The drain's record is a workspace stored on the Gateway, and it has to be readable "
                    + "while this machine is down and editable from another computer. Draining with "
                    + "nowhere off-machine to write would close every session here and leave the only "
                    + "account of them on a disk nobody can reach.\n\n"
                    + "Connect this Director to a Gateway in Settings, then open this again.";
                return;
            }

            _running = true;
            BtnStart.IsEnabled = false;
            _cts = new CancellationTokenSource();

            var options = new DrainOptions
            {
                WorkspaceId = MintId(_directorName, DateTime.Now),
                WorkspaceName = $"{_directorName} restart {DateTime.Now:yyyy-MM-dd HH:mm}",
                Reason = string.IsNullOrWhiteSpace(TxtReason.Text) ? null : TxtReason.Text.Trim(),
            };

            FileLog.Write($"[DrainDirectorDialog] starting drain: workspace={options.WorkspaceId}");
            var result = await drain.RunAsync(options, directory: null, ct: _cts.Token);
            ShowResult(result);
        }
        catch (DrainAlreadyRunningException ex)
        {
            TxtReport.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            TxtReport.Text =
                "The drain was cancelled. Everything it had already written is on the Gateway and on "
                + "disk - a partly drained Director is a normal, recoverable state.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DrainDirectorDialog] drain FAILED: {ex}");
            TxtReport.Text = "The drain stopped with an error and nothing was forced:\n\n" + ex.Message;
        }
        finally
        {
            _running = false;
            BtnStart.IsEnabled = true;
        }
    }

    private void OnProgress(DrainProgress p) => Dispatcher.UIThread.Post(() =>
    {
        TxtPhase.Text = p.Phase switch
        {
            "capturing" => "Capturing this Director's sessions into a workspace on the Gateway...",
            "messaging" => $"Messaging the senior and standalone seats ({p.Seats} seats in all)...",
            "collecting" => $"{p.Accounted} of {p.Seats} seats accounted for, {p.Closed} closed. Waiting.",
            "checking" => "Verifying the record and sweeping every document for secrets...",
            "finished" => "Drained. Every seat reached a clean stop.",
            "blocked" => "Stopped: the restart must not proceed. See below.",
            _ => p.Phase,
        };
        if (!string.IsNullOrWhiteSpace(p.Note)) TxtPhase.Text += "  " + p.Note;
    });

    private void ShowResult(DirectorDrainResult result)
    {
        var doc = result.Document;
        var sb = new StringBuilder();

        sb.AppendLine(result.ReadyToRestart
            ? "READY TO RESTART. Every seat reached a clean stop and is verified gone."
            : "NOT READY TO RESTART. Nothing was forced.");
        if (!result.ReadyToRestart && result.NotReadyReason is not null)
            sb.AppendLine().AppendLine(result.NotReadyReason);

        sb.AppendLine();
        sb.AppendLine($"Workspace: {doc.Id}   (on the Gateway - readable while this machine is down)");
        sb.AppendLine($"Documents: {result.Directory}");
        sb.AppendLine();

        sb.AppendLine("SEATS");
        foreach (var seat in doc.Seats.OrderBy(s => s.SortOrder))
        {
            var closed = seat.ClosedAtUtc is null ? "still present" : "closed";
            sb.AppendLine($"  {DrainPaths.ShortId(seat.SessionId),-8} {seat.DrainState ?? "not accounted for",-12} " +
                          $"{seat.Restore?.Decision ?? "-",-9} {closed,-13} {seat.Name}");
        }

        if (doc.RestoreAfterRestart.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("TO BRING BACK AFTER THE RESTART, in this order:");
            foreach (var id in doc.RestoreAfterRestart)
            {
                var seat = doc.Seats.FirstOrDefault(s => s.SessionId == id);
                sb.AppendLine($"  {DrainPaths.ShortId(id)}  {seat?.Name}");
                if (seat?.Restore?.Why is not null) sb.AppendLine($"      {seat.Restore.Why}");
            }
        }

        if (doc.OwnerQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("QUESTIONS WAITING ON YOU, word for word:");
            foreach (var q in doc.OwnerQuestions)
                sb.AppendLine($"  [{q.FromName}] {q.Question}");
        }

        var integrity = doc.Integrity;
        if (integrity is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"SECRET SWEEP: {integrity.DocumentsSwept} document(s), " +
                          $"{integrity.SweepPatternsProved} of {integrity.SweepPatternsTotal} patterns " +
                          "proved able to fire before the sweep ran, " +
                          $"{integrity.SecretFindings.Count} finding(s).");
            foreach (var f in integrity.SecretFindings)
                sb.AppendLine($"  {Path.GetFileName(f.File)}:{f.Line}  {f.Pattern}  {f.RedactedExcerpt}");

            if (integrity.Problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("PROBLEMS WITH THE RECORD:");
                foreach (var p in integrity.Problems) sb.AppendLine("  - " + p);
            }
        }

        if (result.ReadyToRestart && doc.RestartCommand is not null)
        {
            sb.AppendLine();
            sb.AppendLine("THE RESTART ITSELF is not done from here:");
            sb.AppendLine($"  {doc.RestartCommand.Method} {doc.RestartCommand.Url}");
            sb.AppendLine($"  {doc.RestartCommand.Note}");
        }

        TxtReport.Text = sb.ToString();
        BtnOpenFolder.IsVisible = Directory.Exists(result.Directory);
        BtnOpenFolder.Tag = result.Directory;
    }

    /// <summary>
    /// Mint the workspace slug: a sortable timestamp and the Director's name, so a person scanning the
    /// list months later can tell which restart is which without opening any of them.
    /// </summary>
    /// <param name="directorName">The Director's display name.</param>
    /// <param name="startedLocal">When the drain started.</param>
    internal static string MintId(string directorName, DateTime startedLocal)
    {
        var name = WorkspaceSlug.From(directorName);
        var id = $"restart-{startedLocal:yyyyMMdd-HHmm}-{name}";
        if (id.Length > 64) id = id[..64].TrimEnd('-');
        return id;
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (BtnOpenFolder.Tag is string dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DrainDirectorDialog] open folder failed: {ex.Message}");
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Closing the window CANCELS a running drain rather than orphaning it. Everything already
            // written stays written; a partly drained Director is a normal, recoverable state.
            _cts?.Cancel();
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DrainDirectorDialog] close failed: {ex.Message}");
        }
    }
}
