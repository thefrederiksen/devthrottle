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
		var page = IPlatformApplication.Current!.Services.GetRequiredService<MainPage>();
		return new Window(new NavigationPage(page));
	}
}