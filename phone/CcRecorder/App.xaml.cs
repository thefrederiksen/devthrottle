using CcRecorder.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace CcRecorder;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var services = IPlatformApplication.Current?.Services;
		if (services is null)
			throw new InvalidOperationException("App services are not available at window creation.");

		// Reconcile and drain ALL local recordings on every app start, regardless
		// of which page is shown. This recovers interrupted recordings and retries
		// anything not yet confirmed-delivered, so a previously-unsynced recording
		// always gets another chance on restart - not only when the user navigates
		// to MainPage (issue #687). The page below still does its own pass for
		// instant UI feedback; this one guarantees the launch reconcile is not
		// UI-dependent.
		ReconcileOnStartup(services.GetService<IAudioRecorder>());

		var page = services.GetRequiredService<MainPage>();
		return new Window(new NavigationPage(page));
	}

	// Launch-time, not UI-dependent: kick a foreground reconcile pass and hand the
	// queue to the OS background scheduler. The recorder is only registered on
	// Android, so a null recorder (non-Android) is a no-op rather than a crash.
	private static void ReconcileOnStartup(IAudioRecorder? recorder)
	{
		if (recorder is null) return;

		// Fire-and-forget off the UI thread; the pass is self-serialized and
		// idempotent. This is a lifecycle entry point, so it owns the try/catch.
		_ = Task.Run(async () =>
		{
			try
			{
				recorder.EnqueueBackgroundUpload();
				await recorder.ProcessUploadQueueAsync();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[App] ReconcileOnStartup FAILED: {ex.Message}");
			}
		});
	}
}