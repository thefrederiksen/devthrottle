using System.ComponentModel;
using CcDirector.ControlApi.SmartRestart;
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

    // ===== The engine's check (mission section 7: the Gateway unreachable when the dialog opens) =====
    //
    // Three states: checking, may, may not with the reason. A view model nobody asked to check is in
    // "may", which is what the dialog was before the check existed. The caller that opens the dialog
    // over a real engine calls BeginChecking BEFORE the window is shown, so the confirm is never live
    // ahead of the answer; and the engine asks itself the same question again when a run starts, so a
    // confirm that did get through is still refused there with the same reason.

    /// <summary>The engine has been asked and has not answered yet.</summary>
    public bool IsChecking { get; private set; }

    /// <summary>The confirm button is live: the check is over and nothing refused this door.</summary>
    public bool CanConfirm { get; private set; } = true;

    public string CheckingText => "Checking whether a smart shutdown can be done...";

    /// <summary>Why a smart shutdown cannot be done, exactly as the engine gave it. Empty when it can.</summary>
    public string SmartShutdownRefusalText { get; private set; } = "";

    public bool HasSmartShutdownRefusal => SmartShutdownRefusalText.Length > 0;

    /// <summary>Why this Director cannot be restarted, exactly as the engine gave it. Only ever set for
    /// the File menu door; the window close asks for no restart.</summary>
    public string RestartRefusalText { get; private set; } = "";

    public bool HasRestartRefusal => RestartRefusalText.Length > 0;

    /// <summary>The heading over the refusals. The other choices still work, and it says so.</summary>
    public string RefusalHeading =>
        $"{Title} cannot be used right now. \"{IgnoreButtonText}\" and \"{CancelButtonText}\" still work.";

    public bool HasRefusal => HasSmartShutdownRefusal || HasRestartRefusal;

    /// <summary>The engine is being asked: the confirm goes dead until <see cref="ApplyAvailability"/>.</summary>
    public void BeginChecking()
    {
        FileLog.Write($"[SmartShutdownViewModel] BeginChecking: door={Door}");
        IsChecking = true;
        CanConfirm = false;
        RaiseCheckChanged();
    }

    /// <summary>The engine answered. The reasons are shown as given; nothing is reworded here.</summary>
    public void ApplyAvailability(SmartShutdownAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        var restartMatters = Door == SmartShutdownDoor.FileMenu;
        IsChecking = false;
        SmartShutdownRefusalText = availability.CanSmartShutdown
            ? ""
            : RequireReason(availability.SmartShutdownRefusal, nameof(availability.SmartShutdownRefusal));
        RestartRefusalText = !restartMatters || availability.CanRestart
            ? ""
            : RequireReason(availability.RestartRefusal, nameof(availability.RestartRefusal));
        CanConfirm = availability.CanSmartShutdown && (!restartMatters || availability.CanRestart);

        FileLog.Write($"[SmartShutdownViewModel] ApplyAvailability: door={Door}, canConfirm={CanConfirm}, " +
                      $"canSmartShutdown={availability.CanSmartShutdown}, canRestart={availability.CanRestart}");
        RaiseCheckChanged();
    }

    /// <summary>The check itself failed. The confirm stays dead and the failure is what is shown.</summary>
    public void ApplyCheckFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        FileLog.Write($"[SmartShutdownViewModel] ApplyCheckFailure: {message}");
        IsChecking = false;
        CanConfirm = false;
        SmartShutdownRefusalText = message;
        RaiseCheckChanged();
    }

    // The engine promises a reason with every refusal. A refusal without one is a defect in the engine
    // and is reported as one, not papered over with words made up here.
    private static string RequireReason(string? reason, string name)
    {
        if (!string.IsNullOrWhiteSpace(reason)) return reason;

        FileLog.Write($"[SmartShutdownViewModel] ApplyAvailability FAILED: the engine refused and {name} is empty");
        throw new InvalidOperationException($"The smart shutdown engine refused without a reason: {name} is empty.");
    }

    private void RaiseCheckChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsChecking), nameof(CanConfirm), nameof(SmartShutdownRefusalText),
                     nameof(HasSmartShutdownRefusal), nameof(RestartRefusalText), nameof(HasRestartRefusal),
                     nameof(HasRefusal),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The result of the confirm button: smart shutdown, with the time now selected.</summary>
    public SmartShutdownChoice BuildSmartShutdownChoice() =>
        SmartShutdownChoice.SmartShutdown(SelectedTimeOption.TimeAllowed);
}
