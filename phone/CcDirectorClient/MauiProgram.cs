using CcDirectorClient.Recording;
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
#endif
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
