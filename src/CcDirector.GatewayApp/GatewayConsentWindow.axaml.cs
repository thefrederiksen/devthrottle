using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;

namespace CcDirector.GatewayApp;

/// <summary>
/// The Gateway's first-run consent screen, shown ONCE at the Gateway's first launch alongside the
/// sign-in (Gateway Centralization Phase 3, issue #650). It explains, in plain language, why
/// DevThrottle now needs an account (so we can count how many people use the product), what we do NOT
/// collect (nothing about the person's actual work), and that future cloud features will always be
/// optional. It carries the usage-sharing choice: the checkbox is the Gateway's CENTRALIZED
/// usage-telemetry consent (<see cref="Core.Configuration.TelemetryConsentConfig"/>, issue #649) - one
/// fleet-wide setting, not a per-Director one - pre-set to its current value (on by default), so the
/// person sets that choice in the same breath as acknowledging the explanation.
///
/// "Got it - continue" writes the usage-sharing choice to the centralized consent setting and records
/// that the Gateway consent screen was acknowledged (<see cref="GatewayConsentSurface.Acknowledge"/>),
/// so it is shown only once on the Gateway. Closing the window without continuing records nothing, so
/// the screen is shown again on the next launch and the usage choice keeps its default until the
/// person actively confirms it. The decision and persistence live in the unit-tested
/// <see cref="GatewayConsentSurface"/>; this window is the thin Avalonia surface over it.
/// </summary>
public partial class GatewayConsentWindow : Window
{
    public GatewayConsentWindow()
    {
        InitializeComponent();
        FileLog.Write("[GatewayConsentWindow] Shown: gateway first-run consent screen (account required, what we do/do not collect, optional cloud later)");

        // Pre-set the usage-sharing checkbox to the persisted centralized consent value so re-showing
        // the screen (or an upgrade where the fleet already chose) reflects the real current setting,
        // not a hardcoded default. A fresh Gateway has no persisted value, which reads as ON.
        Loaded += (_, _) => ShareUsageCheck.IsChecked = GatewayConsentSurface.CurrentUsageSharingChoice();
    }

    private async void BtnContinue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var shareUsage = ShareUsageCheck.IsChecked == true;
            FileLog.Write($"[GatewayConsentWindow] Continue clicked: persisting centralized usage-sharing choice shareUsage={shareUsage} and acknowledging gateway consent");

            // Immediate feedback (CodingStyle: responsive UI): disable the action while the choice is
            // written, so a double-click cannot double-persist or close mid-write.
            ContinueButton.IsEnabled = false;

            // Persist off the UI thread (both writes touch config.json). The usage-sharing choice is the
            // centralized fleet-wide consent; acknowledging records that the gateway screen was completed.
            await Task.Run(() => GatewayConsentSurface.Acknowledge(shareUsage));

            FileLog.Write("[GatewayConsentWindow] Continue: centralized consent persisted and gateway consent acknowledged; closing the screen");
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConsentWindow] Continue FAILED: {ex.Message}");
            ContinueButton.IsEnabled = true;
        }
    }
}
