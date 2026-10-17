using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// The desktop's one reader of the Gateway's Terminal tab wheel button (mission #4070, phase 4), the twin of
/// client-core's readWheelAction. It renders exactly what the Gateway said, returns no button when the Gateway says
/// none, and refuses to invent a button it cannot read (CLAUDE.md rule 7).
/// </summary>
public sealed class GatewayWheelActionTests
{
    [Fact]
    public void FromWire_NoVerdict_ReturnsNull()
    {
        // Act
        var action = GatewayWheelAction.FromWire(null, null, null, null);

        // Assert
        Assert.Null(action);
    }

    [Fact]
    public void FromWire_None_ReturnsNull()
    {
        // Act
        var action = GatewayWheelAction.FromWire("none", "", false, null);

        // Assert
        Assert.Null(action);
    }

    [Fact]
    public void FromWire_Take_ReadsTheGatewaysWordsVerbatim()
    {
        // Act
        var action = GatewayWheelAction.FromWire("take", "Take the wheel", true, null);

        // Assert
        Assert.Equal(new GatewayWheelAction(WheelVerb.Take, "Take the wheel", true, null), action);
    }

    [Fact]
    public void FromWire_HandBack_ReadsTheGatewaysWordsVerbatim()
    {
        // Act
        var action = GatewayWheelAction.FromWire("handBack", "Hand back", true, null);

        // Assert
        Assert.Equal(new GatewayWheelAction(WheelVerb.HandBack, "Hand back", true, null), action);
    }

    [Fact]
    public void FromWire_NotEnabled_CarriesTheGatewaysReason()
    {
        // Arrange
        const string reason = "The terminal is in the middle of a turn. Hand back when it has finished.";

        // Act
        var action = GatewayWheelAction.FromWire("handBack", "Hand back", false, reason);

        // Assert
        Assert.NotNull(action);
        Assert.False(action!.Enabled);
        Assert.Equal(reason, action.DisabledReason);
    }

    [Fact]
    public void FromWire_EnabledWithAReason_DropsTheReason()
    {
        // Act
        var action = GatewayWheelAction.FromWire("take", "Take the wheel", true, "stale words");

        // Assert
        Assert.Null(action!.DisabledReason);
    }

    [Fact]
    public void FromWire_UnknownKind_ThrowsRatherThanGuessing()
    {
        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayWheelAction.FromWire("grab", "Grab", true, null));

        // Assert
        Assert.Contains("(\"grab\")", ex.Message);
    }

    [Fact]
    public void FromWire_NoWords_ThrowsRatherThanInventingThem()
    {
        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayWheelAction.FromWire("take", "   ", true, null));

        // Assert
        Assert.Contains("no words", ex.Message);
    }
}
