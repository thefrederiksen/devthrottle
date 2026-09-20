using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;
using Xunit;
using static CcDirector.Avalonia.Tests.SmartRestart.ShutdownProgressViewTests;
using static CcDirector.ControlApi.SmartRestart.SmartShutdownSessionState;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// One picture per moment of the smart shutdown progress screen, drawn by real Skia from the real view.
/// Each moment is a snapshot pushed through the fake run, the way the engine sends one.
///
/// The words in these pictures are the TEST's stand-ins for the engine's labels, written to read the way
/// the mission words them; the screen draws whatever it is given. The real engine's words may differ.
///
/// Every run draws every moment and checks the frame is a picture and not a blank: a frame of one flat
/// colour has a handful of distinct colours, and drawn text has hundreds. The pictures are WRITTEN only
/// when SMART_RESTART_SCREENSHOT_DIR names a folder, so an ordinary run leaves nothing on disk.
///
/// What this does NOT prove: that a picture looks right. A person or an agent opens the files for that.
/// </summary>
public class ShutdownProgressScreenshotTests
{
    private const string FolderVariable = "SMART_RESTART_SCREENSHOT_DIR";

    private const string BillingLead = "Billing - Tech Lead - invoices";
    private const string FleetLead = "Fleet - Delivery Lead - the restart";

    // Leads first, each lead followed by the sessions under it, as the engine orders them.
    private static readonly (string Name, string? Under)[] Nine =
    {
        (BillingLead, null),
        ("Billing - Developer - the export", BillingLead),
        ("Billing - Developer - the totals", BillingLead),
        (FleetLead, null),
        ("Fleet - Developer - the history list", FleetLead),
        ("Docs - Developer - the install page", null),
        ("Docs - Reviewer - the install page", null),
        ("Voice - Developer - the wake word", null),
        ("Voice - Developer - the ready cue", null),
    };

    private static readonly Dictionary<SmartShutdownSessionState, string> Words = new()
    {
        [Pending] = "not asked yet",
        [Asked] = "asked",
        [NotDelivered] = "could not be asked",
        [Writing] = "writing",
        [HandedOver] = "handed over",
        [Interrupted] = "interrupted",
        [ShutDown] = "shut down",
        [EndedAtLimit] = "ended at the limit",
        [KeptRunning] = "kept running",
        [BroughtBack] = "brought back",
    };

    private const string NotDeliveredReason =
        "The session did not take the request: its terminal is not accepting input.";

    [AvaloniaFact]
    public void Capture_EveryMomentOfTheScreen_DrawsARealPicture()
    {
        var scenes = new List<(string File, Func<Screen> Build)>
        {
            ("progress-1-just-started.png", JustStarted),
            ("progress-2-midway-every-state.png", Midway),
            ("progress-3-after-two-thirds-interrupted.png", AfterTwoThirds),
            ("progress-4-could-not-be-asked.png", CouldNotBeAsked),
            ("progress-5-ending-at-the-limit-cancel-dead.png", EndingAtTheLimit),
            ("progress-6-cancelling.png", CancellingScene),
            ("progress-7-finished-emptied.png", FinishedEmptied),
            ("progress-8-failed.png", FailedScene),
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

    private static SmartShutdownSnapshot Moment(
        SmartShutdownPhase phase,
        string phaseLabel,
        SmartShutdownSessionState[] states,
        bool canShutDownNow = true,
        bool canCancel = true,
        string? note = null)
    {
        Assert.Equal(Nine.Length, states.Length);
        var rows = Nine.Select((session, i) => Shutdown.Row(
            session.Name, states[i], Words[states[i]],
            states[i] == NotDelivered ? NotDeliveredReason : null,
            session.Under)).ToList();
        var gone = states.Count(state => state is ShutDown or EndedAtLimit);
        return Shutdown.Snapshot(phase, phaseLabel, $"{gone} of {Nine.Length} shut down", rows, canShutDownNow, canCancel, note);
    }

    private static Screen JustStarted() => Open(Moment(
        SmartShutdownPhase.Asking,
        "Asking each session to write a short handover of what it was doing.",
        new[] { Asked, Pending, Pending, Asked, Pending, Asked, Asked, Asked, Asked }));

    // The eight states a run can hold before a cancel. Kept running and brought back exist only after
    // one, and are in the cancelling picture.
    private static Screen Midway()
    {
        var screen = Open(Moment(
            SmartShutdownPhase.Collecting,
            "Waiting for the handovers. A session is shut down once its handover is accepted.",
            new[] { Writing, Asked, Pending, ShutDown, ShutDown, HandedOver, NotDelivered, Interrupted, EndedAtLimit },
            note: "Fleet - Developer - the history list handed over and was shut down."));
        screen.Clock.Advance(TimeSpan.FromSeconds(200));
        screen.View.ViewModel.RefreshTimeLeft();
        return screen;
    }

    private static Screen AfterTwoThirds()
    {
        var screen = Open(Moment(
            SmartShutdownPhase.Interrupting,
            "Two thirds of the time is gone. Sessions still working were interrupted and asked to hand over now.",
            new[] { HandedOver, ShutDown, Interrupted, ShutDown, ShutDown, Interrupted, Writing, Interrupted, ShutDown },
            note: "Three sessions were interrupted."));
        screen.Clock.Advance(TimeSpan.FromSeconds(415));
        screen.View.ViewModel.RefreshTimeLeft();
        return screen;
    }

    private static Screen CouldNotBeAsked()
    {
        var screen = Open(Shutdown.Snapshot(
            SmartShutdownPhase.Collecting,
            "Waiting for the handovers. A session is shut down once its handover is accepted.",
            "0 of 3 shut down",
            new[]
            {
                Shutdown.Row(Nine[5].Name, Writing, Words[Writing]),
                Shutdown.Row(Nine[6].Name, NotDelivered, Words[NotDelivered], NotDeliveredReason),
                Shutdown.Row(Nine[7].Name, Asked, Words[Asked]),
            },
            note: "One session could not be asked. It is ended at the limit if nothing changes."));
        screen.Clock.Advance(TimeSpan.FromSeconds(30));
        screen.View.ViewModel.RefreshTimeLeft();
        return screen;
    }

    private static Screen EndingAtTheLimit() => Open(Moment(
        SmartShutdownPhase.EndingAtLimit,
        "The time is up. Every session still open is being ended.",
        new[] { ShutDown, ShutDown, EndedAtLimit, ShutDown, ShutDown, ShutDown, Interrupted, EndedAtLimit, Interrupted },
        canShutDownNow: false, canCancel: false));

    private static Screen CancellingScene() => Open(Moment(
        SmartShutdownPhase.Cancelling,
        "Cancelled. Telling the open sessions the restart is off and bringing the closed ones back.",
        new[] { KeptRunning, KeptRunning, KeptRunning, BroughtBack, ShutDown, KeptRunning, BroughtBack, KeptRunning, KeptRunning },
        canShutDownNow: false, canCancel: false,
        note: "Fleet - Delivery Lead - the restart was brought back and is reading its handover."));

    private static Screen FinishedEmptied()
    {
        var screen = Open(Moment(
            SmartShutdownPhase.Finished,
            "Every session is gone.",
            new[] { ShutDown, ShutDown, ShutDown, ShutDown, ShutDown, ShutDown, EndedAtLimit, ShutDown, EndedAtLimit },
            canShutDownNow: false, canCancel: false));
        screen.Run.Complete(SmartShutdownOutcome.Emptied, "The Director is empty. 7 sessions handed over and 2 were ended at the limit.");
        return screen;
    }

    private static Screen FailedScene()
    {
        var screen = Open(Moment(
            SmartShutdownPhase.Finished,
            "The shutdown stopped.",
            new[] { ShutDown, ShutDown, HandedOver, Writing, Asked, ShutDown, Writing, Asked, Asked },
            canShutDownNow: false, canCancel: false));
        screen.Run.Complete(SmartShutdownOutcome.Failed,
            "The shutdown stopped: the Gateway stopped answering while the record was being updated. 3 sessions are shut down and 6 are still open. The record shows how far it got.");
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
