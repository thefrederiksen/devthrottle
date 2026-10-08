using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #3672: a Linux Director opened the Speak dialog and logged "listing microphones FAILED, selector left
/// empty: COM is not supported" and "StartAsync FAILED: COM is not supported". Capture is NAudio's winmm and
/// Core Audio, both Windows COM, so off Windows the dialog must say dictation is Windows-only and never reach
/// a device query.
///
/// The device seams here throw exactly what Linux throws, so a dialog that still queries devices shows the
/// reported error and fails these assertions.
/// </summary>
public sealed class SpeakDialogCaptureUnsupportedTests : IDisposable
{
    public void Dispose() => DesktopHostedAiGate.CheckOverrideForTests = null;

    [AvaloniaFact]
    public void Open_WhereCaptureIsUnsupported_SaysWindowsOnlyAndNeverQueriesDevices()
    {
        var preflightCalls = 0;
        DesktopHostedAiGate.CheckOverrideForTests = _ =>
        {
            Interlocked.Increment(ref preflightCalls);
            return Task.FromResult(HostedAiState.Ready);
        };
        var deviceQueries = 0;
        var recorderBuilt = false;

        var dialog = new SpeakDialog(new AgentOptions())
        {
            CaptureSupportedForTests = () => false,
            ResolveMicForTests = () =>
            {
                Interlocked.Increment(ref deviceQueries);
                throw new PlatformNotSupportedException("COM is not supported");
            },
            EnumerateMicsForTests = () =>
            {
                Interlocked.Increment(ref deviceQueries);
                throw new PlatformNotSupportedException("COM is not supported");
            },
            RecorderFactoryForTests = _ =>
            {
                recorderBuilt = true;
                throw new PlatformNotSupportedException("COM is not supported");
            },
        };
        try
        {
            dialog.Show();
            var status = dialog.FindControl<TextBlock>("StatusLabel")!;
            Pump(() => status.Text == "ERROR", TimeSpan.FromSeconds(5));
            // Let any background device query that was started get the chance to run and be counted.
            Pump(() => false, TimeSpan.FromMilliseconds(300));

            var text = dialog.FindControl<TextBox>("TranscriptText")!.Text ?? "";
            Assert.Equal("ERROR", status.Text);
            Assert.Equal("Desktop dictation is only available on Windows.", text);
            Assert.DoesNotContain("COM is not supported", text);
            Assert.Equal(0, Volatile.Read(ref deviceQueries));
            Assert.False(recorderBuilt);
            Assert.Equal(0, Volatile.Read(ref preflightCalls));
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void Pump(Func<bool> done, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (done()) return;
            Thread.Sleep(10);
        }
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
