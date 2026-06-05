using System;
using Avalonia.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();

        VersionText.Text = $"Version v{AppVersion.Semver}";
        DateText.Text = DateTime.Now.ToString("MMMM d, yyyy");
    }
}
