using System.Windows;
using System.Windows.Controls;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// A UI-thread snapshot of the Privacy step's choice (issue #659): the chosen telemetry value and the
/// Sign-in access token. Captured on the UI thread so the apply can run detached on a background thread
/// without touching any UI element. The access token is held in memory only and never logged.
/// </summary>
/// <param name="Enabled">The chosen telemetry value (the checkbox state).</param>
/// <param name="AccessToken">The Bearer access token captured at Sign-in, or null when unavailable.</param>
public sealed record PrivacyChoiceSnapshot(bool Enabled, string? AccessToken);

/// <summary>
/// The Privacy step of the installer (issue #659), shown right after the forced Sign-in step. It states,
/// in three plain-English one-liners, what is and is not collected, and presents ONE checkbox - "Share
/// anonymous usage information to help improve CC Director" - that is ON by default (matching the server
/// default <c>telemetry_enabled = true</c>).
///
/// The per-account server flag is the source of truth. On load the step pre-fills the checkbox from
/// <c>GET /api/v1/auth/me</c> using the access token captured at Sign-in; when the flag cannot be read
/// (no token, or an unreachable/unauthorized backend) it defaults the checkbox ON. On Next the wizard
/// calls <see cref="ApplyChoiceAsync"/>, which writes the chosen value to the server with
/// <c>PATCH /api/v1/account/telemetry</c> (best-effort, never blocking) and always mirrors it into the
/// local <c>config.json</c>. The access token is used only as the Bearer and is never logged.
/// </summary>
public partial class PrivacyStep : UserControl
{
    private readonly TelemetryChoiceApplier _applier;
    private readonly Func<string?> _accessTokenProvider;

    /// <summary>True once the pre-fill read has completed (success or default). The wizard does not gate
    /// Next on this - the choice is never a gate - it is exposed only so a test can observe completion.</summary>
    public bool PrefillCompleted { get; private set; }

    public PrivacyStep(Func<string?> accessTokenProvider)
        : this(accessTokenProvider, new TelemetryChoiceApplier())
    {
    }

    /// <summary>Constructor seam so a test can inject the applier and the access-token provider.</summary>
    public PrivacyStep(Func<string?> accessTokenProvider, TelemetryChoiceApplier applier)
    {
        InitializeComponent();
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        SetupLog.Write("[PrivacyStep] Created");

        // Pre-fill the checkbox from the server flag asynchronously so the step appears immediately
        // (responsive-UI rule) and the network read happens in the background.
        Loaded += PrivacyStep_Loaded;
    }

    /// <summary>The current checkbox state. ON by default; the source of truth until Next applies it.</summary>
    public bool IsTelemetryEnabled => TelemetryToggle.IsChecked == true;

    private async void PrivacyStep_Loaded(object sender, RoutedEventArgs e)
    {
        // Run the pre-fill once. Returning to the step via Back must not re-read and clobber a choice the
        // person just changed, so we unsubscribe after the first load.
        Loaded -= PrivacyStep_Loaded;
        SetupLog.Write("[PrivacyStep] PrivacyStep_Loaded: reading server telemetry flag to pre-fill the checkbox");
        try
        {
            var token = _accessTokenProvider();
            var enabled = await _applier.ReadPrefillAsync(token);
            TelemetryToggle.IsChecked = enabled;
            PrefillHint.Text = enabled
                ? "Currently sharing anonymous usage information. You can change this any time."
                : "Currently not sharing usage information. You can change this any time.";
            SetupLog.Write($"[PrivacyStep] PrivacyStep_Loaded: pre-filled checkbox enabled={enabled}");
        }
        catch (Exception ex)
        {
            // ReadPrefillAsync never throws, but the boundary still guards the UI thread per the coding
            // standard. Default ON on any unexpected failure.
            SetupLog.Write($"[PrivacyStep] PrivacyStep_Loaded FAILED: {ex.Message} -> defaulting checkbox ON");
            TelemetryToggle.IsChecked = true;
            PrefillHint.Text = "You can change this any time.";
        }
        finally
        {
            PrefillCompleted = true;
        }
    }

    /// <summary>
    /// Captures the current choice (the checkbox state) and the Sign-in access token on the UI thread, so
    /// the actual apply can run on a background thread without touching UI elements. Call this on the UI
    /// thread, then pass the snapshot to <see cref="ApplyChoiceAsync(PrivacyChoiceSnapshot)"/>.
    /// </summary>
    public PrivacyChoiceSnapshot SnapshotChoice() => new(IsTelemetryEnabled, _accessTokenProvider());

    /// <summary>
    /// Applies a captured choice: writes it to the per-account server flag (best-effort) and always
    /// mirrors it to the local <c>config.json</c>. Never throws and never blocks - a failed server write
    /// is logged and the wizard proceeds to Install regardless of the toggle value (the toggle is a
    /// choice, not a gate). Safe to run on a background thread because it touches no UI element.
    /// </summary>
    public async Task ApplyChoiceAsync(PrivacyChoiceSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        SetupLog.Write($"[PrivacyStep] ApplyChoiceAsync: applying choice enabled={snapshot.Enabled}");
        await _applier.ApplyChoiceAsync(snapshot.AccessToken, snapshot.Enabled);
    }
}
