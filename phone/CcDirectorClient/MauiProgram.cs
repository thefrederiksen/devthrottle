using CcDirectorClient.Recording;
using CcDirectorClient.Voice;
using Microsoft.Extensions.Logging;

namespace CcDirectorClient;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Global crash catchers: every unhandled .NET exception lands here BEFORE the
		// process is torn down. Android shows a generic "this app has a bug" dialog
		// with no detail, so without this hook a crash leaves no evidence in our log
		// file. With it, the stack trace ends up in client-YYYY-MM-DD.log next to the
		// rest of the diagnostics.
		AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
		{
			var ex = ev.ExceptionObject as Exception;
			ClientLog.Write($"[CRASH] AppDomain.UnhandledException terminating={ev.IsTerminating} {ex?.GetType().FullName}: {ex?.Message}");
			if (ex is not null) ClientLog.Write($"[CRASH] stack: {ex.StackTrace}");
		};
		TaskScheduler.UnobservedTaskException += (_, ev) =>
		{
			ClientLog.Write($"[CRASH] TaskScheduler.UnobservedTaskException: {ev.Exception.GetType().FullName}: {ev.Exception.Message}");
			ClientLog.Write($"[CRASH] stack: {ev.Exception.StackTrace}");
			ev.SetObserved();
		};

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if ANDROID
		builder.Services.AddSingleton<IAudioRecorder, CcDirectorClient.Platforms.Android.AndroidAudioRecorder>();
		builder.Services.AddSingleton<IUtteranceRecorder, CcDirectorClient.Platforms.Android.AndroidUtteranceRecorder>();
		builder.Services.AddSingleton<IReplySpeaker, CcDirectorClient.Platforms.Android.AndroidReplySpeaker>();
		builder.Services.AddSingleton<IVoiceForeground, CcDirectorClient.Platforms.Android.AndroidVoiceForeground>();
#endif
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddTransient<TalkPage>();
		builder.Services.AddTransient<FifoPage>();
		builder.Services.AddTransient<FifoTextPage>();
		builder.Services.AddTransient<ExesPage>();
		builder.Services.AddTransient<DictionaryPage>();
		builder.Services.AddTransient<TranscriptsPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
