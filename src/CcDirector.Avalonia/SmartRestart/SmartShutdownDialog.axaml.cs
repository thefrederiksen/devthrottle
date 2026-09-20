using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// The Smart shutdown dialog (mission 5.3 items 1 to 3): one window behind two doors, the close of the
/// main window and File, Smart Restart. It shows what <see cref="SmartShutdownViewModel"/> holds and
/// gives back one <see cref="SmartShutdownChoice"/>. It shuts nothing down itself.
///
/// InitializeComponent is the GENERATED one, on purpose. The window this replaces defined its own,
/// which skipped the generated code that connects named controls, and it threw on opening in every
/// shipped build. Do not add one here; the headless test that opens this window fails if you do.
/// </summary>
public partial class SmartShutdownDialog : Window
{
    /// <summary>Designer constructor, which the XAML compiler requires. Never used at runtime.</summary>
    public SmartShutdownDialog()
        : this(new SmartShutdownViewModel(
            [new SmartShutdownSession("sample session", IsWorking: true, HasQuestionBoxOpen: false)],
            SmartShutdownDoor.WindowClose))
    {
    }

    public SmartShutdownDialog(SmartShutdownViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        // Smart shutdown is the default, always: the default button takes Enter wherever the focus
        // is, and the focus starts on it so Space takes it too.
        Opened += (_, _) => BtnSmart.Focus();

        FileLog.Write($"[SmartShutdownDialog] Created: door={viewModel.Door}");
    }

    public SmartShutdownViewModel ViewModel { get; }

    /// <summary>
    /// What the owner chose. It stays <see cref="SmartShutdownChoice.Cancelled"/> until a button says
    /// otherwise, so closing the window by its own X is cancelled.
    /// </summary>
    public SmartShutdownChoice Result { get; private set; } = SmartShutdownChoice.Cancelled;

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns what the owner chose.</summary>
    public async Task<SmartShutdownChoice> ShowForResultAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        FileLog.Write($"[SmartShutdownDialog] ShowForResultAsync: door={ViewModel.Door}");
        await ShowDialog(owner);
        FileLog.Write($"[SmartShutdownDialog] ShowForResultAsync: choice={Result.Choice}, timeAllowed={Result.TimeAllowed}");
        return Result;
    }

    private void BtnSmart_Click(object? sender, RoutedEventArgs e) =>
        CloseWith(nameof(BtnSmart_Click), () => ViewModel.BuildSmartShutdownChoice());

    private void BtnIgnore_Click(object? sender, RoutedEventArgs e) =>
        CloseWith(nameof(BtnIgnore_Click), () => SmartShutdownChoice.IgnoreAllSessions);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) =>
        CloseWith(nameof(BtnCancel_Click), () => SmartShutdownChoice.Cancelled);

    // The body of all three button handlers. A failure here is logged and then left to surface: there
    // is no honest result to give back from a dialog that could not read its own choice.
    private void CloseWith(string handler, Func<SmartShutdownChoice> choose)
    {
        try
        {
            Result = choose();
            FileLog.Write($"[SmartShutdownDialog] {handler}: choice={Result.Choice}, timeAllowed={Result.TimeAllowed}");
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownDialog] {handler} FAILED: {ex}");
            throw;
        }
    }
}
