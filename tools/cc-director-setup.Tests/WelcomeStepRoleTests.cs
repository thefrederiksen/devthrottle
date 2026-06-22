using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Steps;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the reframed installer role step (issue #645). The Welcome step's fresh-install role
/// picker is reframed around "do you already have a gateway?": the "first machine" card maps to
/// <see cref="InstallRole.Gateway"/> (provision a LOCAL gateway here) and is pre-selected by default,
/// so a solo user who does NOT choose to connect to an existing gateway ends with the Gateway role -
/// every install therefore has a gateway. The "I already have a gateway" card maps to
/// <see cref="InstallRole.Workstation"/>.
///
/// <see cref="WelcomeStep"/> is a WPF UserControl whose XAML binds App-level static resources, so all
/// cases run on ONE shared STA thread that owns a single <see cref="Application"/> with App.xaml's
/// resources loaded (resource lookup is thread-affine, so every control must be built on the thread
/// that owns the Application). The shared thread is provided by <see cref="WpfStaFixture"/>.
/// </summary>
public sealed class WelcomeStepRoleTests : IClassFixture<WpfStaFixture>
{
    private readonly WpfStaFixture _wpf;

    public WelcomeStepRoleTests(WpfStaFixture wpf) => _wpf = wpf;

    [Fact]
    public void FreshInstall_DefaultsToGatewayRole_SoSoloInstallProvisionsLocalGateway() =>
        _wpf.Run(() =>
        {
            // Arrange + Act: a fresh install (isUpdate=false). No user interaction at all.
            var step = new WelcomeStep(isUpdate: false, installedVersion: null);

            // Assert: the solo path is pre-selected to the local-gateway role, so a user who never
            // touches the cards still ends with a Gateway install (issue #645 acceptance criterion 1/2).
            Assert.Equal(InstallRole.Gateway, step.SelectedRole);
        });

    [Fact]
    public void FreshInstall_FirstMachineCard_MapsToGatewayRole() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: false, installedVersion: null);

            // Act: pick "I'm setting up my first machine".
            FindRadio(step, "FirstMachineRadio").IsChecked = true;

            // Assert: that is the local-gateway role.
            Assert.Equal(InstallRole.Gateway, step.SelectedRole);
        });

    [Fact]
    public void FreshInstall_HaveGatewayCard_MapsToWorkstationRole() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: false, installedVersion: null);

            // Act: the user already has a gateway, so this machine pairs to it (Workstation role).
            FindRadio(step, "HaveGatewayRadio").IsChecked = true;

            // Assert.
            Assert.Equal(InstallRole.Workstation, step.SelectedRole);
        });

    [Fact]
    public void FreshInstall_RoleSelected_FiresWhenUserSwitchesCards() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: false, installedVersion: null);
            var fired = false;
            step.RoleSelected += (_, _) => fired = true;

            // Act: switch from the pre-selected first-machine card to the have-a-gateway card.
            FindRadio(step, "HaveGatewayRadio").IsChecked = true;

            // Assert: the wizard is notified so it can keep Next enabled.
            Assert.True(fired);
        });

    [Fact]
    public void UpdateMode_DoesNotPreSelectAnyRole_SoTheUpdatePathUsesTheDetectedRole() =>
        _wpf.Run(() =>
        {
            // Arrange + Act: update mode hides the interactive picker; the role comes from
            // InstalledRoleDetector (passed in by MainWindow), never from this step's cards. So the
            // step itself must not assert a role - SelectedRole stays null and is never read on update.
            var step = new WelcomeStep(isUpdate: true, installedVersion: "1.2.3", installedRole: InstallRole.Gateway);

            // Assert: no card is checked in update mode, so a downgrade can never come from this step
            // (issue #645 acceptance criterion 3: an update preserves the detected role).
            Assert.Null(step.SelectedRole);
        });

    private static RadioButton FindRadio(WelcomeStep step, string name)
    {
        var radio = step.FindName(name) as RadioButton;
        Assert.NotNull(radio);
        return radio!;
    }
}

/// <summary>
/// A single, long-lived STA thread that owns one <see cref="Application"/> with App.xaml's resources
/// loaded, plus a <see cref="Dispatcher"/> to marshal work onto it. WPF resource resolution is
/// thread-affine, so every WelcomeStep must be constructed on the one thread that created the
/// Application - this fixture is that thread. Shared across the test class via IClassFixture so the
/// Application (a per-process singleton) is created exactly once.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    public WpfStaFixture()
    {
        _thread = new Thread(() =>
        {
            // One Application per process. The Welcome step's XAML binds the App-level brushes by key
            // (StaticResource AccentBrush etc.); in a unit test there is no App.xaml-driven startup, so
            // we register exactly those brushes into Application.Resources here. The values mirror
            // App.xaml; only the keys the step actually binds are needed for it to construct.
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AddBrush(app, "TextForeground", "#CCCCCC");
                AddBrush(app, "AccentBrush", "#007ACC");
                AddBrush(app, "DimText", "#888888");
                AddBrush(app, "MutedText", "#666666");
                AddBrush(app, "StepInactive", "#3C3C3C");
                AddBrush(app, "ErrorBrush", "#CC4444");
                AddBrush(app, "ButtonBackground", "#3C3C3C");
                AddBrush(app, "ButtonHover", "#505050");

                // The Uninstall button in the step references the DangerButton style by key at parse
                // time (it is only shown in update mode, but the reference is resolved when the XAML
                // loads). A minimal Button style under that key is enough for the step to construct.
                app.Resources["DangerButton"] = new Style(typeof(Button));
            }

            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait();
    }

    private static void AddBrush(Application app, string key, string hex) =>
        app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>Run <paramref name="body"/> synchronously on the STA thread, surfacing any exception.</summary>
    public void Run(Action body)
    {
        Exception? captured = null;
        _dispatcher!.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
        });
        if (captured != null)
            throw new Xunit.Sdk.XunitException($"WPF STA body failed: {captured}");
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _ready.Dispose();
    }
}
