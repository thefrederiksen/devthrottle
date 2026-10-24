using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia;
using CcDirector.Terminal.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>The Mac Edit menu acts on the focused text box or the focused terminal, and on nothing else (issue #4425).</summary>
public sealed class EditCommandTargetTests
{
    [AvaloniaFact]
    public void For_FocusedTextBox_IsTheTextBox()
    {
        Assert.Equal(EditCommandTarget.Kind.TextBox, EditCommandTarget.For(new TextBox(), new TerminalControl()));
    }

    [AvaloniaFact]
    public void For_TheTerminalItself_IsTheTerminal()
    {
        var terminal = new TerminalControl();
        Assert.Equal(EditCommandTarget.Kind.Terminal, EditCommandTarget.For(terminal, terminal));
    }

    [AvaloniaFact]
    public void For_NothingFocused_OrAButton_OrAnotherTerminal_IsNothing()
    {
        // A paste must never reach the agent's terminal unless the person is in it - not from a dialog, not from a button.
        var terminal = new TerminalControl();
        Assert.Equal(EditCommandTarget.Kind.Nothing, EditCommandTarget.For(null, terminal));
        Assert.Equal(EditCommandTarget.Kind.Nothing, EditCommandTarget.For(new Button(), terminal));
        Assert.Equal(EditCommandTarget.Kind.Nothing, EditCommandTarget.For(new TerminalControl(), terminal));
    }
}
