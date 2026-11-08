using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

    // Disk Space on the Meter (7 November 2026): the DISK bar is always on the toolbar beside CPU and MEM, and a drive
    // under 10 GB free turns it and the meter's frame red while CPU and MEM stay green.
    [AvaloniaFact]
    public void DiskRed_TheThirdBarSaysWhatIsLeftInRed_AndTheFrameTurnsRed()
    {
        using var rig = new Rig();

        rig.Read(Machine(28, (ulong)(7.14 * Gb)).WithDiskSpace(DiskSpaceSet.Read(
        [
            DriveSpaceReading.Read("C:", 931 * Gb, (ulong)(6.4 * Gb)),
            DriveSpaceReading.Read("D:", 3815 * Gb, 325 * Gb),
        ])).Build(), ResourceLevel.Normal, ResourceLevel.Normal);

        var texts = rig.Meter.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Equal(new[] { "CPU", "MEM", "DISK" }, texts.Where(t => t.Text is "CPU" or "MEM" or "DISK").Select(t => t.Text));
        var disk = Assert.Single(texts, t => t.Text == "C: 6 GB");
        Assert.Equal(Color.Parse("#FF8B8B"), ((ISolidColorBrush)disk.Foreground!).Color);
        Assert.Equal(Color.Parse("#7A2B2B"), ((ISolidColorBrush)rig.Meter.BorderBrush!).Color);
        Assert.Equal(ResourceLevel.Normal, rig.Meter.View!.Cpu.Level);
        Photograph(rig.Window, "08-disk-red.png");
    }

    [AvaloniaFact]
    public void DiskUnreadable_TheThirdBarIsTheDashedCannotReadBar_NeverAnEmptyTrack()
    {
        using var rig = new Rig();

        rig.Read(Machine(28, (ulong)(7.14 * Gb)).WithDiskSpace(DiskSpaceSet.Unreadable("the mount table could not be read")).Build(),
            ResourceLevel.Normal, ResourceLevel.Normal);

        var view = rig.Meter.View!;
        Assert.False(view.Disk.CouldRead);
        Assert.Equal(ResourceViewText.CannotReadText, view.Disk.PercentText);
        // Exactly one dashed outline is showing: the DISK bar's. CPU and MEM have their tracks.
        var dashed = rig.Meter.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Rectangle>().Where(r => r.IsVisible).ToList();
        Assert.Single(dashed);
        Photograph(rig.Window, "09-disk-unreadable.png");
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
    /// where the window's content actually sits catches ANY new strip, whoever adds it and whatever they call
    /// it. The reviewer of pull request 4732 confirmed it is a real measurement by adding this test to main,
    /// where the banner still existed, and watching it fail with the body at 125 instead of 71.
    ///
    /// THREE THINGS ARE MEASURED, each because a review proved the previous set insufficient by BUILDING the
    /// strip that slipped through it:
    /// <list type="bullet">
    /// <item>the layout grid's own top - a strip between the toolbar and the body, where the deleted one lived;</item>
    /// <item>the session view INSIDE the grid - a strip in the grid's header row pushes the user's work down
    /// without moving the grid's top-left at all. The reviewer showed the added version catches it, 107 against
    /// 140;</item>
    /// <item>the session view's HEIGHT - a strip docked to the BOTTOM of the window takes the user's space
    /// without moving any origin at all. Both position checks stayed green through a 56-pixel bottom strip the
    /// reviewer built and photographed; only height sees it. The rig's window is a fixed 600, so a height that
    /// does not change is a window nothing was taken from.</item>
    /// </list>
    ///
    /// WHAT IT STILL DOES NOT COVER: the vertical only. A strip docked left or right would not fail it. That is
    /// a deliberate choice - the owner's complaint was about height - and not an oversight.
    /// </summary>
    [AvaloniaFact]
    public void GoingRed_MovesNothingInTheWindow()
    {
        using var rig = new Rig();
        rig.Read(Machine(28, (ulong)(7.14 * Gb)).Build(), ResourceLevel.Normal, ResourceLevel.Normal);
        var quietGrid = rig.Window.MainLayoutGrid.TranslatePoint(new Point(0, 0), rig.Window);
        var quietBody = rig.Window.SessionViewGrid.TranslatePoint(new Point(0, 0), rig.Window);
        var quietHeight = rig.Window.SessionViewGrid.Bounds.Height;
        Assert.NotNull(quietGrid);
        Assert.NotNull(quietBody);
        Assert.True(quietHeight > 0, "the session view has no height, so this test would prove nothing");

        rig.Read(Machine(98, (ulong)(0.95 * Gb)).Build(), ResourceLevel.Critical, ResourceLevel.Critical);

        // It really did go red - otherwise this test would pass on a machine that never warned at all.
        Assert.Equal(ResourceLevel.Critical, rig.Meter.View!.FrameLevel);

        var redGrid = rig.Window.MainLayoutGrid.TranslatePoint(new Point(0, 0), rig.Window);
        var redBody = rig.Window.SessionViewGrid.TranslatePoint(new Point(0, 0), rig.Window);
        Assert.NotNull(redGrid);
        Assert.NotNull(redBody);
        Assert.Equal(quietGrid!.Value.Y, redGrid!.Value.Y);
        Assert.Equal(quietBody!.Value.Y, redBody!.Value.Y);
        Assert.Equal(quietHeight, rig.Window.SessionViewGrid.Bounds.Height);
    }

    /// <summary>
    /// SWAP KEEPS A SURFACE. Swap has no bar - the mission allows exactly two - and until the banner was deleted
    /// the banner was the only thing that read its level. Review of pull request 4732 found that removing the
    /// banner left a machine paging hard showing two green bars and saying nothing.
    ///
    /// So swap colours the meter's FRAME and speaks in the tooltip. Both halves are asserted here, with the two
    /// bars deliberately Normal so the frame's colour can only have come from swap.
    /// </summary>
    [AvaloniaFact]
    public void SwapNearlyFull_ColoursTheFrameAndSaysSo_EvenThoughBothBarsAreGreen()
    {
        using var rig = new Rig();
        rig.Verdict = new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal, "the processor is 28% busy"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "7.14 GB of memory is available"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Critical, "swap is 81% full"),
        ]);
        rig.Probe.Set(Machine(28, (ulong)(7.14 * Gb)).Build());
        rig.Sampler.Tick();
        Pump();

        var view = rig.Meter.View!;
        Assert.Equal(ResourceLevel.Normal, view.Cpu.Level);
        Assert.Equal(ResourceLevel.Normal, view.Memory.Level);
        Assert.Equal(ResourceLevel.Critical, view.FrameLevel);
        Assert.Contains(view.TooltipLines, line => line.StartsWith("Swap", StringComparison.Ordinal)
            && line.Contains("swap is 81% full", StringComparison.Ordinal));
        // The words are the banner's own, per resource: full swap is "nearly full", never "maxed out".
        Assert.Contains("swap is nearly full", view.AccessibleName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A WARNING BAR SAYS WHY. The memory verdict holds its level for sixty seconds after the load eases, so a
    /// red bar can sit beside figures that read as healthy. The deleted banner printed the reason; review of
    /// pull request 4732 found the tooltip never did, and that my claim it already carried the evidence was
    /// wrong. Without this, the first bug report is "the bar is red and the numbers say it is fine".
    /// </summary>
    [AvaloniaFact]
    public void ARedBarCarriesTheVerdictsReason_SoItIsNeverRedWithoutSayingWhy()
    {
        using var rig = new Rig();
        rig.Verdict = new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal, "the processor is 28% busy"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Critical,
                "only 600 MB was available 20 seconds ago, and a memory warning clears only after 60 seconds below the line"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap is fine"),
        ]);
        // The figures themselves look healthy - that is the whole point of the case.
        rig.Probe.Set(Machine(28, (ulong)(7.14 * Gb)).Build());
        rig.Sampler.Tick();
        Pump();

        var view = rig.Meter.View!;
        Assert.Equal(ResourceLevel.Critical, view.Memory.Level);
        Assert.Contains("7.14 GB available", view.Memory.Tooltip, StringComparison.Ordinal);
        Assert.Contains("a memory warning clears only after 60 seconds below the line", view.Memory.Tooltip,
            StringComparison.Ordinal);
        // The healthy bar says only its figures; the evidence is for a bar that is actually warning.
        Assert.DoesNotContain("the processor is 28% busy", view.Cpu.Tooltip, StringComparison.Ordinal);
        Assert.Contains("memory is nearly gone", view.AccessibleName, StringComparison.Ordinal);
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
        // The compact form keeps all three as text (Disk Space on the Meter, 7 November 2026).
        Assert.Equal("CPU 28% MEM 55% C: 500 GB", rig.Meter.View!.NarrowText);
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
