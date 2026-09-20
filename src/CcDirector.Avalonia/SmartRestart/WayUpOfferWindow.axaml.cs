using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// "A RESTART IS AVAILABLE" (mission "Smart Director Restart", 5.3 item 10, ruling 10.2).
///
/// Shown by the Director at start-up when the engine answers that there is a record to offer, and again
/// from File, Restart history for any record that still owes seats. It shows what the engine computed
/// and nothing it worked out for itself, and it gives back one of two answers: bring the ticked rows
/// back, or not now - which writes nothing at all, leaving the record in the history to be offered
/// again.
///
/// InitializeComponent is the GENERATED one, on purpose. The window this pattern replaces
/// (DrainDirectorDialog) defined its own, which skipped the generated code that connects named
/// controls, and it threw the moment anybody opened it in every shipped build - and nothing caught it
/// because no test ever opened the window. Do not add one here; the headless test that opens this
/// window fails if you do.
/// </summary>
public partial class WayUpOfferWindow : Window
{
    /// <summary>Designer constructor, which the XAML compiler requires. Never used at runtime.</summary>
    public WayUpOfferWindow()
        : this(new WayUpOfferViewModel(
            new WayUpRecord(
                "sample", DateTime.UtcNow, DateTime.Now,
                WayUpWords.Headline, WayUpWords.WhenLabel(DateTime.Now), null, WayUpWords.ReasonLabel(null),
                0, WayUpWords.SeatsOwedLabel(0), Array.Empty<WayUpRow>()),
            new DesignerWayUp()))
    {
    }

    /// <param name="viewModel">The offer, already worded by the engine.</param>
    public WayUpOfferWindow(WayUpOfferViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        // Bring back is the default, so Enter takes it wherever the focus is, and the focus starts on
        // it so Space takes it too. Not now is the cancel, so Escape and the window's own X take it.
        Opened += (_, _) => BtnBringBack.Focus();

        FileLog.Write($"[WayUpOfferWindow] Created: workspace={viewModel.Record.WorkspaceId}, rows={viewModel.Rows.Count}");
    }

    /// <summary>Everything the window shows.</summary>
    public WayUpOfferViewModel ViewModel { get; }

    /// <summary>
    /// True once a bring back has been asked for from this window. False means nothing was written and
    /// nothing was started: "not now" and the window's own X are the same answer.
    /// </summary>
    public bool BringBackAsked { get; private set; }

    /// <summary>Shows the offer over <paramref name="owner"/> and returns when it is closed.</summary>
    /// <param name="owner">The window it opens over.</param>
    public async Task ShowForAnswerAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        FileLog.Write($"[WayUpOfferWindow] ShowForAnswerAsync: workspace={ViewModel.Record.WorkspaceId}");
        await ShowDialog(owner);
        FileLog.Write($"[WayUpOfferWindow] ShowForAnswerAsync: closed, bringBackAsked={BringBackAsked}");
    }

    private async void BtnBringBack_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            BringBackAsked = true;
            await ViewModel.BringBackAsync();
        }
        catch (Exception ex)
        {
            // An entry point, so this is where a failure is caught (CLAUDE.md rule 4). There is no
            // honest sentence to invent here - the engine's own answers carry every word this window
            // shows - so the failure is logged and left to the Director's own handler.
            FileLog.Write($"[WayUpOfferWindow] BtnBringBack_Click FAILED: {ex}");
            throw;
        }
    }

    private void BtnNotNow_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write($"[WayUpOfferWindow] BtnNotNow_Click: workspace={ViewModel.Record.WorkspaceId}, " +
                          $"bringBackAsked={BringBackAsked}");
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WayUpOfferWindow] BtnNotNow_Click FAILED: {ex}");
            throw;
        }
    }

    private async void BtnReopen_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // The button lives inside the row's own template, so the row it belongs to is its data
            // context. Nothing is looked up by index or by name.
            if (sender is not Button { DataContext: WayUpRowViewModel row })
            {
                FileLog.Write("[WayUpOfferWindow] BtnReopen_Click: the button has no row, so nothing was reopened");
                return;
            }

            await ViewModel.ReopenAsync(row);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WayUpOfferWindow] BtnReopen_Click FAILED: {ex}");
            throw;
        }
    }
}

/// <summary>
/// The engine the DESIGNER constructor hands the view model. It is never called: the designer record
/// has no rows, so there is no button that could reach it. It exists because the view model refuses a
/// null engine, and a window that could be built with one would be a window that fails at the moment
/// somebody presses a button rather than at the moment it is built.
/// </summary>
internal sealed class DesignerWayUp : IDirectorWayUp
{
    public Task<WayUpOffer> FindOfferAsync(CancellationToken ct) => throw new NotSupportedException(Why);

    public Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct) => throw new NotSupportedException(Why);

    public Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct) =>
        throw new NotSupportedException(Why);

    public Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct) =>
        throw new NotSupportedException(Why);

    private const string Why =
        "This is the designer's stand-in for the way up engine and it has no Gateway behind it. A window " +
        "built by the designer constructor is never driven; build it with ControlApiHost.CreateDirectorWayUp().";
}
