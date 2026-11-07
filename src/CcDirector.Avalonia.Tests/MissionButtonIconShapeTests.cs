using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE MISSION BUTTON'S ICON SHAPES REACH BOTH BUTTONS. The document and warning shapes live once, in
/// MainWindow.axaml's resources, and the toolbar corner and the session tab bar both point at them. A
/// resource key that is renamed or moved compiles and draws nothing, so these load the real MainWindow and
/// check that each icon has a shape with an area.
/// </summary>
public sealed class MissionButtonIconShapeTests
{
    [AvaloniaTheory]
    [InlineData("BtnMissionDocumentIcon")]
    [InlineData("BtnMissionWarningIcon")]
    [InlineData("TabBarMissionDocumentIcon")]
    [InlineData("TabBarMissionWarningIcon")]
    public void MainWindow_MissionIcon_HasAShapeWithAnArea(string iconName)
    {
        var window = new MainWindow();

        var icon = window.FindControl<Viewbox>(iconName);
        Assert.NotNull(icon);
        var path = icon!.GetLogicalDescendants().OfType<ShapePath>().Single();

        Assert.NotNull(path.Data);
        Assert.True(path.Data!.Bounds.Width > 0 && path.Data.Bounds.Height > 0,
            $"{iconName} has an empty shape, so the button would show no icon.");
    }
}
