using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>Replays the Pi 1.2.0 screens captured by the Terminal Parity state rig.</summary>
public sealed class PiComposerTests
{
    [Fact]
    public void ReadComposerText_CapturedEmptyComposer_ReadsEmpty()
    {
        var frame = DoorbellCaptures.Load("pi-idle-empty", 66);

        Assert.Equal((ComposerReading.Empty, ""), DoorbellSafety.ReadComposerText(AgentKind.Pi, frame));
        Assert.True(FirstPromptGate.CanProve(AgentKind.Pi));
        Assert.True(FirstPromptGate.ComposerDrawn(AgentKind.Pi, frame));
    }

    [Fact]
    public void ReadComposerText_CapturedComposerWithDraft_PreservesTheDraft()
    {
        var empty = DoorbellCaptures.Load("pi-idle-empty", 66);
        var frame = DoorbellCaptures.WithRow(empty, empty.CursorRow, "owner draft") with { CursorCol = 11 };

        Assert.Equal((ComposerReading.HoldsText, "owner draft"), DoorbellSafety.ReadComposerText(AgentKind.Pi, frame));
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.Pi, frame, "owner draft"));
    }

    [Fact]
    public void ReadComposerText_WrappedDraftBetweenRules_ReadsTheWholeEditor()
    {
        var empty = DoorbellCaptures.Load("pi-idle-empty", 66);
        var rows = empty.Rows.ToList();
        rows.Insert(empty.CursorRow + 1, "second wrapped row");
        rows[empty.CursorRow] = "first wrapped row";
        var frame = new ScreenFrame(rows, empty.CursorRow + 1, 18, true);

        Assert.Equal((ComposerReading.HoldsText, "first wrapped row\nsecond wrapped row"),
            DoorbellSafety.ReadComposerText(AgentKind.Pi, frame));
    }

    [Fact]
    public void ReadComposerText_UnreadablePartialRepaint_FailsOpenAsNotFound()
    {
        var empty = DoorbellCaptures.Load("pi-idle-empty", 66);
        var rows = empty.Rows.ToArray();
        rows[empty.CursorRow - 1] = "";
        var frame = new ScreenFrame(rows, empty.CursorRow, 0, true);

        Assert.Equal((ComposerReading.NotFound, ""), DoorbellSafety.ReadComposerText(AgentKind.Pi, frame));
        Assert.False(FirstPromptGate.ComposerDrawn(AgentKind.Pi, frame));
    }

    [Fact]
    public void ReadComposerText_CapturedModelPickerIsNotMistakenForComposer()
    {
        var frame = DoorbellCaptures.Load("pi-menu-model-picker", 66);

        Assert.Equal((ComposerReading.NotFound, ""), DoorbellSafety.ReadComposerText(AgentKind.Pi, frame));
    }
}
