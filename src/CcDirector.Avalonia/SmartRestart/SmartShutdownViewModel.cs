using System.ComponentModel;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// Everything the Smart shutdown dialog shows, held here so the words and the counts are testable
/// without a window. The window only binds.
///
/// The review is code, no model (mission 5.3 item 3): the count working against waiting, and the
/// sessions with a question box open for the owner. The two doors (mission 10.1) differ only in
/// <see cref="Title"/> and <see cref="ConfirmButtonText"/>.
/// </summary>
public sealed class SmartShutdownViewModel : INotifyPropertyChanged
{
    /// <summary>The default time allowed, in minutes (mission 4.5).</summary>
    public const int DefaultMinutes = 10;

    private SmartShutdownTimeOption _selectedTimeOption;

    public SmartShutdownViewModel(IReadOnlyList<SmartShutdownSession> sessions, SmartShutdownDoor door)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
            throw new ArgumentException(
                "The Smart shutdown dialog is never shown with no sessions running: with nothing to shut " +
                "down the caller closes or restarts without asking.", nameof(sessions));

        Door = door;

        var working = sessions.Count(s => s.IsWorking);
        var waiting = sessions.Count - working;
        SessionCountText = sessions.Count == 1 ? "1 session is running" : $"{sessions.Count} sessions are running";
        WorkingWaitingText = $"{working} working, {waiting} waiting";
        QuestionBoxSessionNames = sessions.Where(s => s.HasQuestionBoxOpen).Select(s => s.DisplayName).ToList();

        TimeOptions =
        [
            new SmartShutdownTimeOption(5, "5 minutes"),
            new SmartShutdownTimeOption(DefaultMinutes, "10 minutes"),
            new SmartShutdownTimeOption(15, "15 minutes"),
            new SmartShutdownTimeOption(30, "30 minutes"),
            new SmartShutdownTimeOption(60, "1 hour"),
        ];
        _selectedTimeOption = TimeOptions.Single(o => o.Minutes == DefaultMinutes);

        FileLog.Write($"[SmartShutdownViewModel] Created: door={door}, sessions={sessions.Count}, " +
                      $"working={working}, waiting={waiting}, questionBoxes={QuestionBoxSessionNames.Count}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SmartShutdownDoor Door { get; }

    public string Title => Door == SmartShutdownDoor.FileMenu ? "Smart Restart" : "Smart shutdown";

    public string ConfirmButtonText => Title;

    public string SessionCountText { get; }

    public string WorkingWaitingText { get; }

    public string QuestionBoxHeading => "Answer these first?";

    public IReadOnlyList<string> QuestionBoxSessionNames { get; }

    public bool HasQuestionBoxes => QuestionBoxSessionNames.Count > 0;

    public string ExplanationText =>
        "Your sessions are shut down nicely. Each one writes a short handover of what it was doing and " +
        $"what is left. They get {SelectedTimeOption.Label}; whatever is still running after that is shut " +
        "down for them. You can start the sessions again when the Director comes back.";

    public string WhyText =>
        "Why it is worth doing: a handover compresses a session down to what is left to do.";

    public string TimeAllowedLabel => "Time allowed";

    public IReadOnlyList<SmartShutdownTimeOption> TimeOptions { get; }

    public SmartShutdownTimeOption SelectedTimeOption
    {
        get => _selectedTimeOption;
        set
        {
            // A dropdown clears its selection to null while its items change; the time allowed is
            // never "nothing", so that is not a choice the owner can make.
            if (value is null || ReferenceEquals(value, _selectedTimeOption)) return;

            _selectedTimeOption = value;
            FileLog.Write($"[SmartShutdownViewModel] SelectedTimeOption: minutes={value.Minutes}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTimeOption)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExplanationText)));
        }
    }

    public string IgnoreButtonText => "Shut down and ignore all sessions";

    public string IgnoreExplanationText => "The sessions are ended at once and no handovers are written.";

    public string CancelButtonText => "Cancel";

    /// <summary>The result of the confirm button: smart shutdown, with the time now selected.</summary>
    public SmartShutdownChoice BuildSmartShutdownChoice() =>
        SmartShutdownChoice.SmartShutdown(SelectedTimeOption.TimeAllowed);
}
