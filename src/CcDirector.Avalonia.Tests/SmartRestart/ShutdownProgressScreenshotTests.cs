using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using Xunit;
using static CcDirector.Avalonia.Tests.SmartRestart.ShutdownProgressViewTests;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// One picture per state of the shutdown progress screen, drawn by real Skia from the real view, each
/// state reached the way it is reached in life: the fake shutdown is stepped and the buttons are clicked.
///
/// Every run draws every state and checks the frame is a picture and not a blank: a frame of one flat
/// colour has a handful of distinct colours, and drawn text has hundreds. The pictures are WRITTEN only
/// when SMART_RESTART_SCREENSHOT_DIR names a folder, so an ordinary run leaves nothing on disk.
///
/// What this does NOT prove: that a picture looks right. A person or an agent opens the files for that.
/// </summary>
public class ShutdownProgressScreenshotTests
{
    private const string FolderVariable = "SMART_RESTART_SCREENSHOT_DIR";

    [AvaloniaFact]
    public void Capture_EveryStateOfTheScreen_DrawsARealPicture()
    {
        var scenes = new List<(string File, Func<Screen> Build)>
        {
            ("progress-1-just-started.png", JustStarted),
            ("progress-2-midway.png", Midway),
            ("progress-3-after-two-thirds-interrupted.png", AfterTwoThirds),
            ("progress-4-could-not-be-asked.png", CouldNotBeAsked),
            ("progress-5-after-shut-down-now.png", AfterShutDownNow),
            ("progress-6-after-cancel-and-keep-working.png", AfterCancel),
            ("progress-7-finished.png", Finished),
            ("progress-8-ignore-all.png", IgnoreAll),
        };

        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        foreach (var (file, build) in scenes)
        {
            var screen = build();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = screen.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var colours = CountDistinctColours(frame);
            Assert.True(colours > 100, $"{file} has {colours} distinct colours, so it is blank or has no drawn text");

            if (!string.IsNullOrWhiteSpace(folder))
                frame.Save(Path.Combine(folder, file));
            screen.Window.Close();
        }
    }

    private static Screen JustStarted() => Open(ShutdownProgressKind.Smart, NineSessions);

    private static Screen Midway()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        screen.Clock.Advance(TimeSpan.FromSeconds(200));
        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[1], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[2], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[3], ShutdownProgressState.EndedAtLimit);
        screen.Source.MoveTo(NineSessions[4], ShutdownProgressState.HandedOver);
        screen.Source.MoveTo(NineSessions[5], ShutdownProgressState.Writing);
        screen.Source.MoveTo(NineSessions[6], ShutdownProgressState.Interrupted);
        screen.Source.MoveTo(NineSessions[7], ShutdownProgressState.CouldNotBeAsked,
            "The session did not take the request: its terminal is not accepting input.");
        return screen;
    }

    private static Screen AfterTwoThirds()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        screen.Clock.Advance(TimeSpan.FromSeconds(415));
        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[1], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[2], ShutdownProgressState.HandedOver);
        screen.Source.MoveTo(NineSessions[3], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[4], ShutdownProgressState.Interrupted);
        screen.Source.MoveTo(NineSessions[5], ShutdownProgressState.Interrupted);
        screen.Source.MoveTo(NineSessions[6], ShutdownProgressState.Writing);
        screen.Source.MoveTo(NineSessions[7], ShutdownProgressState.Interrupted);
        screen.Source.MoveTo(NineSessions[8], ShutdownProgressState.ShutDown);
        return screen;
    }

    private static Screen CouldNotBeAsked()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions[0], NineSessions[1], NineSessions[2]);
        screen.Clock.Advance(TimeSpan.FromSeconds(30));
        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.Writing);
        screen.Source.MoveTo(NineSessions[1], ShutdownProgressState.CouldNotBeAsked,
            "The session did not take the request: its terminal is not accepting input.");
        return screen;
    }

    private static Screen AfterShutDownNow()
    {
        var screen = Midway();
        Dispatcher.UIThread.RunJobs();
        Click(screen, screen.View.BtnShutDownNow);
        return screen;
    }

    private static Screen AfterCancel()
    {
        var screen = Midway();
        Dispatcher.UIThread.RunJobs();
        Click(screen, screen.View.BtnCancelAndKeepWorking);
        return screen;
    }

    private static Screen Finished()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        screen.Clock.Advance(TimeSpan.FromSeconds(600));
        foreach (var name in NineSessions)
            screen.Source.MoveTo(name, ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[5], ShutdownProgressState.EndedAtLimit);
        screen.Source.MoveTo(NineSessions[7], ShutdownProgressState.EndedAtLimit);
        return screen;
    }

    private static Screen IgnoreAll()
    {
        var screen = Open(ShutdownProgressKind.IgnoreAll, NineSessions);
        for (var i = 0; i < 5; i++)
            screen.Source.MoveTo(NineSessions[i], ShutdownProgressState.ShutDown);
        return screen;
    }

    private static int CountDistinctColours(WriteableBitmap frame)
    {
        using var locked = frame.Lock();
        var bytes = new byte[locked.RowBytes * locked.Size.Height];
        Marshal.Copy(locked.Address, bytes, 0, bytes.Length);

        var colours = new HashSet<int>();
        for (var y = 0; y < locked.Size.Height; y++)
        {
            var row = y * locked.RowBytes;
            for (var x = 0; x < locked.Size.Width; x++)
                colours.Add(BitConverter.ToInt32(bytes, row + x * 4));
        }

        return colours.Count;
    }
}
