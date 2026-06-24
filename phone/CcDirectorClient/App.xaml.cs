using CcDirectorClient.Recording;
using CcDirectorClient.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirectorClient;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Reconcile and drain ALL local recordings on every app start, regardless
		// of which screen the shell shows. This recovers interrupted recordings and
		// retries anything not yet confirmed-delivered, so a previously-unsynced
		// recording always gets another chance on restart - not only when the user
		// navigates to the recorder page (issue #687).
		ReconcileOnStartup(IPlatformApplication.Current?.Services?.GetService<IAudioRecorder>());

		// Shell hosts both screens: Talk (voice client) and Recorder (cloned offline recorder).
		return new Window(new AppShell());
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
				ClientLog.Write($"[App] ReconcileOnStartup FAILED: {ex.Message}");
			}
		});
	}
}