namespace CcDirectorClient;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Shell hosts both screens: Talk (voice client) and Recorder (cloned offline recorder).
		return new Window(new AppShell());
	}
}