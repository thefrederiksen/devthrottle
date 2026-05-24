using CcDirectorClient.Recording;
using CcDirectorClient.Voice;
using Microsoft.Extensions.Logging;

namespace CcDirectorClient;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
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
		builder.Services.AddSingleton<IReplySpeaker, CcDirectorClient.Platforms.Android.AndroidTextToSpeech>();
#endif
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddTransient<TalkPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
