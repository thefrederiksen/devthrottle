using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Tests.Machine;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The resource meter in the REAL Director window, rendered headless, driven by a fake probe and a virtual
/// clock (the Resource Monitor mission, issue #2811, phase 2a). Each scenario the mission asks a screenshot
/// of is asserted here and photographed: normal, amber, red, the warning clearing, a narrow window and
/// "cannot read".
///
/// THERE IS NO WARNING BANNER (the owner's ruling of 2026-11-07). The meter's own colour is the warning, and
/// <see cref="GoingRed_MovesNothingInTheWindow"/> is the guard that nothing may push the window down again.
///
/// The pictures go to %TEMP%\resource-monitor-phase2a-shots, and are copied to the directory in
/// <see cref="ProofDirectoryVariable"/> when it is set.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceMeterWindowTests
{
    private const string ProofDirectoryVariable = "CC_RESOURCE_MONITOR_PROOF_DIR";
    private const ulong Gb = FakeSampleBuilder.Gigabyte;

    /// <summary>One window with the meter attached to a sampler whose verdict the test sets.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(double width = 1400, bool attach = true)
        {
            Window = new MainWindow { SkipApplicationWiringOnLoaded = true, Width = width, Height = 600 };
            Window.Show();
            Pump();
            Directory.CreateDirectory(RecordingDirectory);
            Sampler = new ResourceSampler(Probe, _ => Verdict, Time);
            Host = new ResourceMonitorHost(
                Sampler, () => Sessions, new ResourceSummaryRecorder(new ResourceSummaryOutbox(Time), "soren-north"),
                new ResourceRecorder(RecordingDirectory, Time), Time);
            if (attach) Attach();
        }

        // As the Director does it: the monitor may have read before the window exists.
        public void Attach() => Window.AttachResourceMonitor(Host, () => 4, () => DetailOpened++);

        public MainWindow Window { get; }
        public FakeTimeProvider Time { get; } = new(FakeSampleBuilder.DefaultTakenAtUtc);
        public FakeResourceProbe Probe { get; } = new();
        public ResourceSampler Sampler { get; }
        public ResourceMonitorHost Host { get; }
        public string RecordingDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "resource-meter-window-tests", Guid.NewGuid().ToString("N"));
        public ResourcePressureVerdict Verdict { get; set; } = Levels(ResourceLevel.Normal, ResourceLevel.Normal);
        public int DetailOpened { get; set; }

        // The session whose agent started "dotnet test" in the sample (process 150, the root; 200 is its child).
        public IReadOnlyList<DirectorSessionInfo> Sessions { get; set; } =
        [
            new("s-140", 150, "140 (Resource Monitor - Dev - Linux probe)", null),
        ];

        public ResourceMeter Meter => Window.ResourceMeterControl;

        public void Read(ResourceSample sample, ResourceLevel cpu, ResourceLevel memory)
        {
            Probe.Set(sample);
            Verdict = Levels(cpu, memory);
            Sampler.Tick();
            Pump();
        }

        public void Dispose()
        {
            Host.Dispose();
            if (Directory.Exists(RecordingDirectory)) Directory.Delete(RecordingDirectory, recursive: true);
            // Hidden, not closed: the window's own closing handler saves session history through the running
            // Director application, which a headless test does not have.
            Window.Hide();
        }
    }

    private static ResourcePressureVerdict Levels(ResourceLevel cpu, ResourceLevel memory) =>
        new([
            new ResourceVerdict(ResourceKind.Cpu, cpu,
                cpu == ResourceLevel.Critical ? "the processor has been above 95% for 2 minutes" : "the processor has been above 85% for 2 minutes"),
            new ResourceVerdict(ResourceKind.Memory, memory,
                memory == ResourceLevel.Critical ? "only 600 MB of memory is available" : "only 1.6 GB of memory is available"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap is fine"),
        ]);

    // This Mac mini's own readings on 2026-11-06, from mission section 5d.
    private static FakeSampleBuilder Machine(double cpu, ulong availableBytes) =>
        new FakeSampleBuilder()
            .Cpu(cpu, 71, 8)
            .Memory(16 * Gb, availableBytes)
            .Swap(2 * Gb, (ulong)(0.94 * Gb))
            .Process(150, 1, "claude", 1, 400 * FakeSampleBuilder.Megabyte)
            .Process(200, 150, "dotnet test", cpu * 0.7, (ulong)(1.21 * Gb))
            .Process(300, 1, "chrome", 3, (ulong)(4.4 * Gb));

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Photograph(Window window, string fileName)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Path.Combine(Path.GetTempPath(), "resource-monitor-phase2a-shots");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        frame!.Save(file);
        Assert.True(new FileInfo(file).Length > 5000, "the picture is too small to be a screen");

        var proof = Environment.GetEnvironmentVariable(ProofDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(proof))
        {
            Directory.CreateDirectory(proof);
            File.Copy(file, Path.Combine(proof, fileName), overwrite: true);
        }
    }

    private static void AssertInsideTheWindow(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.Name} is not visible");
        var topLeft = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(topLeft);
        Assert.True(topLeft!.Value.X >= 0, $"{control.Name} starts left of the window");
        Assert.True(topLeft.Value.X + control.Bounds.Width <= window.ClientSize.Width + 0.5,
            $"{control.Name} ends at {topLeft.Value.X + control.Bounds.Width:F0}, past the window's {window.ClientSize.Width:F0}");
    }

    [AvaloniaFact]
    public void Normal_TwoGreenBars_TheMachineFiguresFirst()
    {
        using var rig = new Rig();

        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);

        var view = rig.Meter.View!;
        Assert.Equal("28%", view.Cpu.PercentText);
        Assert.Equal("55%", view.Memory.PercentText);
        Assert.Equal(ResourceLevel.Normal, view.FrameLevel);
        Assert.StartsWith(Environment.MachineName + " - 1 session running", view.TooltipLines[0]);
        Assert.False(rig.Meter.IsCompact);
        // A quiet machine's tooltip does NOT carry a biggest-process line: it would be a line about nothing.
        Assert.DoesNotContain(view.TooltipLines, line => line.StartsWith("Biggest:", StringComparison.Ordinal)
            || line.StartsWith("Busiest:", StringComparison.Ordinal));
        Photograph(rig.Window, "01-normal.png");
    }

    [AvaloniaFact]
    public void Amber_OnlyTheCpuBarTurns_AndTheTooltipNamesTheBusiestProcess()
    {
        using var rig = new Rig();
        rig.Read(Machine(30, 7 * Gb).Build(), ResourceLevel.Normal, ResourceLevel.Normal);

        for (var i = 0; i < 12; i++)
            rig.Read(Machine(88, (ulong)(6.2 * Gb)).Build(), ResourceLevel.Tight, ResourceLevel.Normal);

        var view = rig.Meter.View!;
        Assert.Equal(ResourceLevel.Tight, view.Cpu.Level);
        Assert.Equal(ResourceLevel.Normal, view.Memory.Level);
        Assert.Equal(ResourceLevel.Tight, view.FrameLevel);

        // The one fact the deleted banner carried and the bar cannot: WHICH program, and whose it is. It must
        // survive the banner's removal or the removal cost the user something real.
        Assert.Contains("Busiest: dotnet test in session 140 (Resource Monitor - Dev - Linux probe), 62%.",
            view.TooltipLines);
        // Memory is fine, so it contributes no line of its own.
        Assert.DoesNotContain(view.TooltipLines, line => line.StartsWith("Biggest:", StringComparison.Ordinal));
        Photograph(rig.Window, "02-amber.png");
    }

    [AvaloniaFact]
    public void AWarningAlreadyInForceAtStartUp_ColoursTheBarWhenTheWindowAttaches()
    {
        using var rig = new Rig(attach: false);

        // The first read, before the window exists: memory is already short, so its crossing goes to nobody.
        rig.Read(Machine(30, (ulong)(1.6 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Tight);
        rig.Attach();
        Pump();

        var view = rig.Meter.View!;
        Assert.Equal(ResourceLevel.Tight, view.Memory.Level);
        Assert.Equal(ResourceLevel.Normal, view.Cpu.Level);
        Assert.Contains(view.TooltipLines, line => line.StartsWith("Biggest: chrome", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Red_BothBarsRed_AndBothNameTheirBiggestUser()
    {
        using var rig = new Rig();
        rig.Read(Machine(30, 7 * Gb).Build(), ResourceLevel.Normal, ResourceLevel.Normal);

        rig.Read(Machine(97, (ulong)(0.96 * Gb)).Build(), ResourceLevel.Critical, ResourceLevel.Critical);
        rig.Read(Machine(98, (ulong)(0.95 * Gb)).Build(), ResourceLevel.Critical, ResourceLevel.Critical);

        var view = rig.Meter.View!;
        Assert.Equal(ResourceLevel.Critical, view.FrameLevel);
        Assert.Contains(view.TooltipLines, line => line.StartsWith("Busiest: dotnet test", StringComparison.Ordinal));
        Assert.Contains(view.TooltipLines, line => line.StartsWith("Biggest: chrome", StringComparison.Ordinal));
        Photograph(rig.Window, "03-red.png");
    }

    /// <summary>
    /// THE OWNER'S ACTUAL REQUIREMENT, pinned: "we don't want this massive yellow bar at the top of the screen"
    /// (2026-11-07). A machine going from quiet to red must not move one pixel of the user's own window.
    ///
    /// This is deliberately a measurement and not an absence. Asserting "no banner control exists" would pass
    /// for the wrong reason the moment someone renames the control, and an absence certifies nothing; measuring
    /// where the body actually sits catches ANY new strip, whoever adds it and whatever they call it.
    /// </summary>
    [AvaloniaFact]
    public void GoingRed_MovesNothingInTheWindow()
    {
        using var rig = new Rig();
        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        var quiet = rig.Window.MainLayoutGrid.TranslatePoint(new Point(0, 0), rig.Window);
        Assert.NotNull(quiet);

        rig.Read(Machine(98, (ulong)(0.95 * Gb)).Build(), ResourceLevel.Critical, ResourceLevel.Critical);

        // It really did go red - otherwise this test would pass on a machine that never warned at all.
        Assert.Equal(ResourceLevel.Critical, rig.Meter.View!.FrameLevel);

        var red = rig.Window.MainLayoutGrid.TranslatePoint(new Point(0, 0), rig.Window);
        Assert.NotNull(red);
        Assert.Equal(quiet!.Value.Y, red!.Value.Y);
    }

    [AvaloniaFact]
    public void Clearing_WhenTheLoadGoesAway_TheBarIsGreenAgain_AndTheBusiestLineGoes()
    {
        using var rig = new Rig();
        rig.Read(Machine(30, 7 * Gb).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        rig.Read(Machine(97, 7 * Gb).Build(), ResourceLevel.Critical, ResourceLevel.Normal);
        Assert.Equal(ResourceLevel.Critical, rig.Meter.View!.Cpu.Level);
        Assert.Contains(rig.Meter.View!.TooltipLines, line => line.StartsWith("Busiest:", StringComparison.Ordinal));
        Photograph(rig.Window, "04a-warning-up.png");

        rig.Read(Machine(22, 7 * Gb).Build(), ResourceLevel.Normal, ResourceLevel.Normal);

        Assert.Equal(ResourceLevel.Normal, rig.Meter.View!.Cpu.Level);
        Assert.DoesNotContain(rig.Meter.View!.TooltipLines, line => line.StartsWith("Busiest:", StringComparison.Ordinal));
        Photograph(rig.Window, "04b-warning-cleared.png");
    }


    [AvaloniaFact]
    public void CannotRead_TheCpuBarIsADash_WhileMemoryKeepsItsRealFigure()
    {
        using var rig = new Rig();

        rig.Read(Machine(28, (ulong)(7.14 * Gb)).CpuUnreadable("the processor counters are switched off").Build(),
            ResourceLevel.Unknown, ResourceLevel.Normal);

        var view = rig.Meter.View!;
        Assert.False(view.Cpu.CouldRead);
        Assert.Equal("-", view.Cpu.PercentText);
        Assert.Null(view.Cpu.Fraction);
        Assert.Contains("cannot read: the processor counters are switched off", view.Cpu.Tooltip);
        Assert.Equal("55%", view.Memory.PercentText);
        Assert.Equal(ResourceLevel.Normal, view.FrameLevel);
        Photograph(rig.Window, "05-cannot-read.png");
    }

    [AvaloniaFact]
    public void BeforeTheFirstRead_BothBarsAreDashes_NeverZero()
    {
        using var rig = new Rig();

        var view = rig.Meter.View!;
        Assert.Equal("-", view.Cpu.PercentText);
        Assert.Equal("-", view.Memory.PercentText);
        Assert.Equal(ResourceLevel.Unknown, view.FrameLevel);
    }

    // A Director name long enough that the bars no longer fit at the window's minimum width, but short enough
    // that the compact meter makes the whole toolbar fit. A name far longer than this overlaps New Session
    // whatever the meter does; that is the toolbar's own limit, older than the meter, and reported separately.
    private const string LongDirectorName = "devthrottle-mac-mini - Director 5";

    private static string FitText(Rig rig) =>
        $"needed {rig.Window.LastResourceMeterFit.Needed:F0}, available {rig.Window.LastResourceMeterFit.Available:F0}";

    [AvaloniaFact]
    public void MinimumWidth_WhenTheBarsFit_TheyStay_AndCockpitAndSettingsAreOnTheToolbar()
    {
        using var rig = new Rig(width: 800);
        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        Pump();

        Assert.False(rig.Meter.IsCompact, $"the meter dropped its bars although they fit: {FitText(rig)}");
        AssertInsideTheWindow(rig.Window, rig.Window.BtnCockpit);
        AssertInsideTheWindow(rig.Window, rig.Window.BtnSettings);
        Photograph(rig.Window, "06a-minimum-width-bars-fit.png");
    }

    [AvaloniaFact]
    public void Narrow_WhenTheBarsDoNotFit_TheyDrop_ThePercentagesStay_AndCockpitAndSettingsStayOnTheToolbar()
    {
        using var rig = new Rig(width: 800);
        rig.Window.DirectorInfoText.Text = LongDirectorName;
        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        Pump();
        Pump();

        Assert.True(rig.Meter.IsCompact, $"the meter kept its bars on a toolbar with no room: {FitText(rig)}");
        Assert.Equal("28%/55%", rig.Meter.View!.NarrowText);
        AssertInsideTheWindow(rig.Window, rig.Window.BtnCockpit);
        AssertInsideTheWindow(rig.Window, rig.Window.BtnSettings);
        AssertInsideTheWindow(rig.Window, rig.Meter);

        // The whole toolbar fits: the left group ends before the Director's name begins, so nothing overlaps.
        var newSessionRight = rig.Window.BtnNewSession.TranslatePoint(new Point(rig.Window.BtnNewSession.Bounds.Width, 0), rig.Window)!.Value.X;
        var nameLeft = rig.Window.DirectorInfoPanel.TranslatePoint(new Point(0, 0), rig.Window)!.Value.X;
        Assert.True(newSessionRight <= nameLeft, $"New Session ends at {newSessionRight:F0}, past the name at {nameLeft:F0}: {FitText(rig)}");
        Photograph(rig.Window, "06b-narrow-window.png");
    }

    [AvaloniaFact]
    public void Wide_TheBarsComeBack_WhenTheWindowWidensAgain()
    {
        using var rig = new Rig(width: 800);
        rig.Window.DirectorInfoText.Text = LongDirectorName;
        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        Pump();
        Pump();
        Assert.True(rig.Meter.IsCompact, $"precondition: compact at 800 with a long name: {FitText(rig)}");

        rig.Window.Width = 1600;
        Pump();
        Pump();

        Assert.False(rig.Meter.IsCompact, $"the bars did not come back: {FitText(rig)}");
    }

    [AvaloniaFact]
    public void Click_TheWholeMeterIsOneTarget_ThatOpensTheDetailScreen()
    {
        using var rig = new Rig();

        rig.Meter.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, rig.DetailOpened);
    }
}
