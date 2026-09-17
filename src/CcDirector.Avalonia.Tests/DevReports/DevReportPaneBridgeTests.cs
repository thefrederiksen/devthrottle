using System.Text.Json;
using CcDirector.Avalonia.DevReports;
using Xunit;

namespace CcDirector.Avalonia.Tests.DevReports;

/// <summary>
/// The Director reports pane's bridge decisions (issue #3019): what earns the Gateway key, what the answer
/// says, and where the pane may navigate.
///
/// These are the rules that keep an agent-written report away from the owner's credential. They are pure
/// functions on purpose, so each one is proven here directly rather than inferred from a web view that
/// happened to behave on the day somebody watched it.
/// </summary>
public sealed class DevReportPaneBridgeTests
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private const string Pane = "http://soren_north:7878/embed/reports/" + Session;
    private const string Ready = "{\"kind\":\"dev-report-host-ready\"}";

    // ---------------------------------------------------------------- what earns the key

    [Fact]
    public void EarnsTheKey_ReadyFromThePaneAddress_IsAnswered()
    {
        Assert.True(DevReportPaneBridge.EarnsTheKey(Pane, Pane, Ready, out var refusal));
        Assert.Equal("", refusal);
    }

    [Fact]
    public void EarnsTheKey_ReadyFromAnotherAddress_IsRefusedAndNamesTheAddress()
    {
        Assert.False(DevReportPaneBridge.EarnsTheKey("http://evil.example/steal", Pane, Ready, out var refusal));
        Assert.Contains("not the pane address", refusal);
    }

    [Fact]
    public void EarnsTheKey_ReadyFromTheSamePathOnAnotherHost_IsRefused()
    {
        Assert.False(DevReportPaneBridge.EarnsTheKey(
            "http://evil.example/embed/reports/" + Session, Pane, Ready, out _));
    }

    [Fact]
    public void EarnsTheKey_ReadyFromAnotherSessionsPage_IsRefused()
    {
        // The session identifier is the last path segment, so another session's page is another address.
        Assert.False(DevReportPaneBridge.EarnsTheKey(
            "http://soren_north:7878/embed/reports/99999999-9999-9999-9999-999999999999", Pane, Ready, out _));
    }

    [Fact]
    public void EarnsTheKey_AnotherMessageKind_IsRefusedAndNamesTheKind()
    {
        Assert.False(DevReportPaneBridge.EarnsTheKey(Pane, Pane, "{\"kind\":\"send\"}", out var refusal));
        Assert.Contains("send", refusal);
        Assert.Contains("dev-report-host-ready", refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("\"dev-report-host-ready\"")]
    [InlineData("{\"kind\":42}")]
    [InlineData("{\"nokind\":\"dev-report-host-ready\"}")]
    public void EarnsTheKey_AMessageThatIsNotTheOneWeAnswer_IsRefused(string body)
        => Assert.False(DevReportPaneBridge.EarnsTheKey(Pane, Pane, body, out _));

    [Fact]
    public void EarnsTheKey_ARefusalNeverRepeatsMoreThanASentenceOfWhatThePageChose()
    {
        // A report that wants to fill the Director's log picks a very long kind. A refusal is a diagnosis.
        var huge = new string('x', 5000);
        Assert.False(DevReportPaneBridge.EarnsTheKey(Pane, Pane, "{\"kind\":\"" + huge + "\"}", out var refusal));
        Assert.True(refusal.Length < 400, $"a refusal reason was {refusal.Length} characters long");
        Assert.Contains("truncated", refusal);
    }

    // ---------------------------------------------------------------- the answer

    [Fact]
    public void ComposeKeyMessage_IsTheKindTheKeyAndTheSession()
    {
        using var doc = JsonDocument.Parse(DevReportPaneBridge.ComposeKeyMessage("a-gateway-key", Session));

        Assert.Equal("dev-report-host-key", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("a-gateway-key", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(Session, doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void ComposeKeyMessage_NoKey_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneBridge.ComposeKeyMessage("  ", Session));

    [Fact]
    public void ComposeKeyMessage_NoSession_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneBridge.ComposeKeyMessage("a-gateway-key", ""));

    // ---------------------------------------------------------------- where the pane may go

    [Theory]
    [InlineData(Pane)]
    [InlineData(Pane + "/")]
    [InlineData(Pane + "?reopened=1")]
    [InlineData("about:blank")]
    public void NavigationIsAllowed_ThePaneAddressOrTheEmptyDocument_IsAllowed(string target)
        => Assert.True(DevReportPaneBridge.NavigationIsAllowed(target, Pane));

    [Theory]
    [InlineData("http://evil.example/")]
    [InlineData("https://soren_north:7878/embed/reports/" + Session)]      // another scheme
    [InlineData("http://soren_north:7879/embed/reports/" + Session)]       // another port
    [InlineData("http://soren_north:7878/embed/reports/" + Session + "/x")]
    [InlineData("http://soren_north:7878/fleet")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("data:text/html,<h1>hello</h1>")]
    [InlineData("about:srcdoc")]
    [InlineData("")]
    [InlineData(null)]
    public void NavigationIsAllowed_AnywhereElse_IsCancelled(string? target)
        => Assert.False(DevReportPaneBridge.NavigationIsAllowed(target, Pane));

    [Fact]
    public void SameAddress_NoPaneAddressResolvedYet_IsNeverTheSame()
    {
        // Before the Gateway has answered there is no address to compare against, so nothing matches and
        // nothing is answered.
        Assert.False(DevReportPaneBridge.SameAddress(Pane, ""));
        Assert.False(DevReportPaneBridge.SameAddress(Pane, null!));
    }
}
