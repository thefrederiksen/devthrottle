using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The license warning triangle in the REAL Director window, rendered headless (issue 4728). It replaced the full-width
/// license banner the owner found too big, so the guard here is the same one the resource meter carries: an open license
/// alarm puts a triangle and a count in the toolbar, beside the meter, and moves NOTHING in the window - not the main
/// layout's origin, not the session view's origin, and not the session view's height. Each tone is photographed to
/// %TEMP%\license-triangle-shots.
/// </summary>
public sealed class LicenseAlarmTriangleWindowTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Photograph(Window window, string fileName)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Path.Combine(Path.GetTempPath(), "license-triangle-shots");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        frame!.Save(file);
        Assert.True(new FileInfo(file).Length > 5000, "the picture is too small to be a screen");
    }

    private static MainWindow OpenWindow()
    {
        var window = new MainWindow { SkipApplicationWiringOnLoaded = true, Width = 1400, Height = 600 };
        window.Show();
        Pump();
        return window;
    }

    private static LicenseAlarmsResponse Three(string tone) => new()
    {
        Count = 3, Summary = "3 license problems", LinkText = "Open Licenses", Tone = tone,
        Alarms =
        {
            new LicenseAlarmDto { Text = "Renewing the sign-in for dev@example.com on LAPTOP failed.", Tone = tone },
            new LicenseAlarmDto { Text = "The approver for a@example.com needs a fresh sign-in.", Tone = tone },
            new LicenseAlarmDto { Text = "The approver for b@example.com needs a fresh sign-in.", Tone = tone },
        },
    };

    [AvaloniaFact]
    public void AToneThisBuildDoesNotKnow_IsDrawnUncoloured()
    {
        var window = OpenWindow();

        window.ShowLicenseAlarms(new LicenseAlarmsResponse { Count = 1, Summary = "1 license problem", Tone = "purple" });
        Pump();

        Assert.True(window.LicenseAlarmTriangle.IsVisible);
        Assert.Equal("#FFCCCCCC", window.LicenseAlarmTriangleIcon.Fill?.ToString(), ignoreCase: true);
        window.Hide(); // hidden, not closed: closing saves session history through an application a headless test lacks
    }

    [AvaloniaFact]
    public void NoAlarm_TheTriangleIsHidden()
    {
        var window = OpenWindow();

        window.ShowLicenseAlarms(new LicenseAlarmsResponse { Count = 0 });
        Pump();

        Assert.False(window.LicenseAlarmTriangle.IsVisible);
        window.Hide(); // hidden, not closed: closing saves session history through an application a headless test lacks
    }

    [AvaloniaTheory]
    [InlineData("warning", "#FFEAB308")]
    [InlineData("critical", "#FFEF4444")]
    public void OpenAlarms_ShowTheTriangleAndCount_InTheGatewaysTone_WithTheGatewaysLines(string tone, string drawn)
    {
        var window = OpenWindow();

        window.ShowLicenseAlarms(Three(tone));
        Pump();

        Assert.True(window.LicenseAlarmTriangle.IsVisible);
        Assert.True(window.LicenseAlarmTriangle.Bounds.Width > 0, "the triangle is visible but takes no space");
        Assert.Equal("3", window.LicenseAlarmTriangleCount.Text);
        Assert.Contains(tone, window.LicenseAlarmTriangle.Classes);
        // The colour actually DRAWN, not only the class: a local value on the shape once outranked the tone's style.
        Assert.Equal(drawn, window.LicenseAlarmTriangleIcon.Fill?.ToString(), ignoreCase: true);
        Assert.Equal(drawn, window.LicenseAlarmTriangleCount.Foreground?.ToString(), ignoreCase: true);
        var tip = Assert.IsType<string>(ToolTip.GetTip(window.LicenseAlarmTriangle));
        Assert.Contains("The approver for b@example.com needs a fresh sign-in.", tip);
        Photograph(window, $"license-triangle-{tone}.png");
        window.Hide(); // hidden, not closed: closing saves session history through an application a headless test lacks
    }

    [AvaloniaFact]
    public void TheTriangle_SitsInTheToolbar_RightOfTheResourceMeter()
    {
        var window = OpenWindow();

        window.ShowLicenseAlarms(Three("warning"));
        Pump();

        var meter = window.ResourceMeterControl.TranslatePoint(new Point(0, 0), window);
        var triangle = window.LicenseAlarmTriangle.TranslatePoint(new Point(0, 0), window);
        var grid = window.MainLayoutGrid.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(meter);
        Assert.NotNull(triangle);
        Assert.NotNull(grid);
        Assert.True(triangle!.Value.X > meter!.Value.X, "the triangle is not to the right of the resource meter");
        Assert.True(triangle.Value.Y < grid!.Value.Y, "the triangle is not in the toolbar above the main layout");
        window.Hide(); // hidden, not closed: closing saves session history through an application a headless test lacks
    }

    /// <summary>The owner's complaint was the strip's HEIGHT. Origins and the session view's height must not change
    /// when alarms open; a strip docked top would move the origins, one docked bottom would shrink the height.</summary>
    [AvaloniaFact]
    public void OpenAlarms_MoveNothingInTheWindow()
    {
        var window = OpenWindow();
        window.ShowLicenseAlarms(null);
        Pump();
        var quietGrid = window.MainLayoutGrid.TranslatePoint(new Point(0, 0), window);
        var quietBody = window.SessionViewGrid.TranslatePoint(new Point(0, 0), window);
        var quietHeight = window.SessionViewGrid.Bounds.Height;
        Assert.NotNull(quietGrid);
        Assert.NotNull(quietBody);
        Assert.True(quietHeight > 0, "the session view has no height, so this test would prove nothing");

        window.ShowLicenseAlarms(Three("critical"));
        Pump();

        Assert.True(window.LicenseAlarmTriangle.IsVisible, "the alarm never showed, so this test would prove nothing");
        Assert.Equal(quietGrid!.Value.Y, window.MainLayoutGrid.TranslatePoint(new Point(0, 0), window)!.Value.Y);
        Assert.Equal(quietBody!.Value.Y, window.SessionViewGrid.TranslatePoint(new Point(0, 0), window)!.Value.Y);
        Assert.Equal(quietHeight, window.SessionViewGrid.Bounds.Height);
        window.Hide(); // hidden, not closed: closing saves session history through an application a headless test lacks
    }
}
