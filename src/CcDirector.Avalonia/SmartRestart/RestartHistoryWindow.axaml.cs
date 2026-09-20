using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.ControlApi;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// FILE, RESTART HISTORY (mission "Smart Director Restart", 5.3 item 11): every record this Director
/// wrote, newest first, with what came back and what did not - and, for a record that still owes seats,
/// the SAME bring back offer the start-up window makes.
///
/// That is the whole reason the history exists, in the owner's own words: "it could be that I
/// accidentally don't restart it right away and I want to restart it later".
///
/// The window OPENS AT ONCE and says it is reading (CLAUDE.md rule 1); the engine is asked off the
/// interface thread and its answer replaces that line. InitializeComponent is the GENERATED one, for
/// the reason set out on <see cref="WayUpOfferWindow"/>.
/// </summary>
public partial class RestartHistoryWindow : Window
{
    /// <summary>
    /// Why nothing can be read when this Director's own host has not finished starting. It is fed to
    /// the ENGINE's own refusal wording, which is the same function the engine calls when the Gateway
    /// does not answer - so there is one sentence for "nothing could be read", not two.
    /// </summary>
    internal const string NoHostReason = "this Director's own host has not finished starting";

    /// <summary>Designer constructor, which the XAML compiler requires. Never used at runtime.</summary>
    public RestartHistoryWindow()
        : this(new RestartHistoryViewModel(new DesignerWayUp()))
    {
    }

    /// <param name="viewModel">The history, which asks the engine when the window is shown.</param>
    public RestartHistoryWindow(RestartHistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Title = RestartHistoryViewModel.WindowTitle;

        FileLog.Write("[RestartHistoryWindow] Created");
    }

    /// <summary>Everything the window shows.</summary>
    public RestartHistoryViewModel ViewModel { get; }

    /// <summary>
    /// Opens the restart history over <paramref name="owner"/> and returns when it is closed.
    ///
    /// A Director whose host has not started yet has no engine to ask, so the window says so in the
    /// ENGINE's own refusal sentence rather than showing an empty list - an empty list would read as
    /// "you have no records", which is a lie.
    /// </summary>
    /// <param name="owner">The window it opens over.</param>
    /// <param name="host">This Director's host, or null when it has not started.</param>
    public static async Task ShowForAsync(Window owner, ControlApiHost? host)
    {
        ArgumentNullException.ThrowIfNull(owner);
        FileLog.Write($"[RestartHistoryWindow] ShowForAsync: host={(host is null ? "none" : "present")}");

        var engine = host?.CreateDirectorWayUp() ?? new NoHostWayUp();
        var window = new RestartHistoryWindow(new RestartHistoryViewModel(engine));
        var shown = window.ShowDialog(owner);

        // The window is on screen before the engine is asked, so it never appears empty and never
        // blocks the interface thread while the Gateway is read.
        await window.ViewModel.LoadAsync();
        await shown;
        FileLog.Write("[RestartHistoryWindow] ShowForAsync: closed");
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[RestartHistoryWindow] BtnClose_Click");
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RestartHistoryWindow] BtnClose_Click FAILED: {ex}");
            throw;
        }
    }

    private async void BtnBringBack_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // The button lives inside the record's own template, so the record it belongs to is its
            // data context. Nothing is looked up by index or by name.
            if (sender is not Button { DataContext: RestartHistoryEntryViewModel entry })
            {
                FileLog.Write("[RestartHistoryWindow] BtnBringBack_Click: the button has no record, so nothing was offered");
                return;
            }

            FileLog.Write($"[RestartHistoryWindow] BtnBringBack_Click: workspace={entry.Entry.WorkspaceId}");
            var offer = new WayUpOfferWindow(entry.BuildOffer());
            await offer.ShowForAnswerAsync(this);

            // Whatever came of it, the history on screen is now older than the Gateway's. Read it
            // again so a record that has just come back says so.
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RestartHistoryWindow] BtnBringBack_Click FAILED: {ex}");
            throw;
        }
    }
}

/// <summary>
/// The engine a Director whose own host has not started yet is given. It answers the one refusal the
/// history asks for, in the ENGINE's own words, and nothing else: there is no Gateway behind it, so
/// anything that would start or change something says so instead of pretending.
/// </summary>
internal sealed class NoHostWayUp : IDirectorWayUp
{
    public Task<WayUpOffer> FindOfferAsync(CancellationToken ct) =>
        Task.FromResult(new WayUpOffer(
            WayUpOfferState.Refused, WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason), null));

    public Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct) =>
        Task.FromResult(new WayUpHistory(
            true, WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason), Array.Empty<WayUpHistoryEntry>()));

    public Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct) =>
        Task.FromResult(new WayUpBringBackResult(
            false,
            WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason),
            WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason),
            Array.Empty<WayUpSeatResult>()));

    public Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct) =>
        Task.FromResult(new WayUpReopenResult(
            false, null, WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason)));
}
