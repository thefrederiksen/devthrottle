using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The first-run consent step, shown once after the account sign-in. It explains, in plain language,
/// why DevThrottle now needs an account (so we can count how many people use the product), what we do
/// NOT collect (nothing about the person's actual work), and that future cloud features will always
/// be optional. It also carries the usage-sharing choice: the checkbox is the real usage-telemetry
/// opt-out (<see cref="TelemetrySettings"/>), pre-set to its current value (on by default), so the
/// person sets that choice in the same breath as acknowledging the explanation.
///
/// "Got it - continue" persists the usage-sharing choice and records that the consent step was
/// acknowledged (<see cref="FirstRunConsent"/>), so it is shown only once. Closing the window without
/// continuing records nothing, so the step is shown again on the next start and the usage choice keeps
/// its default until the person actively confirms it.
/// </summary>
public partial class FirstRunConsentDialog : Window
{
    public FirstRunConsentDialog()
    {
        InitializeComponent();
        FileLog.Write("[FirstRunConsentDialog] Shown: first-run consent step (account required, what we do/do not collect, optional cloud later)");

        // Pre-set the usage-sharing checkbox to the persisted opt-out value so re-running the step (or
        // an upgrade where the person already chose) reflects the real current setting, not a hardcoded
        // default. A fresh install has no persisted value, which reads as ON.
        Loaded += (_, _) => ShareUsageCheck.IsChecked = TelemetrySettings.IsEnabled();
    }

    private async void BtnContinue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var shareUsage = ShareUsageCheck.IsChecked == true;
            FileLog.Write($"[FirstRunConsentDialog] Continue clicked: persisting usage-sharing choice shareUsage={shareUsage} and acknowledging consent");

            // Immediate feedback (CodingStyle: responsive UI): disable the action while the choice is
            // written, so a double-click cannot double-persist or close mid-write.
            ContinueButton.IsEnabled = false;

            // Persist off the UI thread (both writes touch config.json). The usage-sharing choice is
            // the real telemetry opt-out; acknowledging records that this step was completed.
            await Task.Run(() =>
            {
                TelemetrySettings.SetEnabled(shareUsage);
                FirstRunConsent.MarkAcknowledged();
            });

            FileLog.Write("[FirstRunConsentDialog] Continue: choice persisted and consent acknowledged; closing the step");
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunConsentDialog] Continue FAILED: {ex.Message}");
            ContinueButton.IsEnabled = true;
        }
    }
}
