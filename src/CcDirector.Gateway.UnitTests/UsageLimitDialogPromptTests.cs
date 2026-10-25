using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The License Switching mission, trap 4: nothing automatic reaches Claude Code's usage-limit dialog - and the sender is
/// TOLD so. The fix-round review of pull request 3863 found the guard dropped the prompt and the verb still answered
/// success, so the phone showed a delivered answer that never arrived. Since the owner's decision 10 (Terminal Parity
/// mission, 23 October 2026) the owner's own prompt from the phone or the Cockpit is not automatic: it reaches the dialog,
/// as the desktop's keys do, while an agent's send is still refused. Driven through the real prompt core against a real
/// session, asserted on what the terminal RECEIVED.
/// </summary>
public sealed class UsageLimitDialogPromptTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-usage-limit-prompt-" + Guid.NewGuid().ToString("N"));
    private readonly DeliveryRecord _record;

    public UsageLimitDialogPromptTests() => _record = new DeliveryRecord(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The bottom of a real captured dialog, personal details removed.</summary>
    private static readonly string[] DialogRows =
    {
        "  ⎿  You've hit your session limit · resets 10:20am (UTC)",
        "  What do you want to do?",
        "  ❯ 1. Stop and wait for limit to reset",
        "    2. Add funds to continue with usage credits",
        "    3. Switch to Team plan",
        "  Enter to confirm · Esc to cancel",
    };

    /// <summary>An ordinary permission prompt under output that happens to say "usage limit".</summary>
    private static readonly string[] PermissionPromptRows =
    {
        "The usage limit is described in the README",
        " Bash command",
        "   git status",
        " Do you want to proceed?",
        " ❯ 1. Yes",
        "   2. Yes, and don't ask again for git status commands",
        "   3. No, and tell Claude what to do differently (esc)",
        " Esc to cancel",
    };

    private static (Session Session, ScriptedTerminal Terminal) SessionShowing(string[] rows)
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = Core.Agents.AgentKind.Gemini;
        terminal.Buffer!.Write(Encoding.UTF8.GetBytes("\u001b[2J\u001b[H" + string.Join("\r\n", rows)));
        return (session, terminal);
    }

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, Json)!;
    }

    private static void AssertRefusedAtTheDialog(PromptResponse response)
    {
        Assert.False(response.Accepted);
        Assert.Equal(DeliveryState.NotDelivered, response.DeliveryState);
        Assert.Equal(SessionCommandExecutor.UsageLimitDialogRefusal, response.Error);
        Assert.Equal(SessionCommandExecutor.UsageLimitDialogRefusal, response.DeliveryStateReason);
    }

    /// <summary>A phone answer typed as keys (no Enter) reaches the dialog - before decision 10 it was refused.</summary>
    [Fact]
    public async Task SendPromptAsync_PhoneKeysWhileTheDialogShows_AreWritten()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "1", AppendEnter = false, Surface = "phone" };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        Assert.True(response.Accepted, response.Error);
        Assert.Equal(new[] { "1" }, terminal.Writes);
    }

    /// <summary>A phone prompt submitted with Enter is not refused at the dialog: its text is typed.</summary>
    [Fact]
    public async Task SendPromptAsync_PhonePromptWithEnterWhileTheDialogShows_IsNotRefusedAtTheDialog()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "carry on", AppendEnter = true, Surface = "phone" };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        Assert.NotEqual(SessionCommandExecutor.UsageLimitDialogRefusal, response.Error);
        Assert.NotEqual(SessionCommandExecutor.UsageLimitDialogRefusal, response.DeliveryStateReason);
        Assert.Contains(terminal.Writes.Concat(terminal.Submitted), w => w.Contains("carry on", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Cockpit pane's keys: its terminal sends every key as a prompt without an Enter
    /// (<c>packages/client-core/src/terminal/interactive.ts</c>, <c>pumpInput</c>), and the Gateway stamps the surface
    /// "browser" from the Cockpit's device key. Exactly that request reaches the dialog - Down, then Enter.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_CockpitPaneKeysWhileTheDialogShows_AreWritten()
    {
        var (session, terminal) = SessionShowing(DialogRows);

        var down = Body(await SessionCommandExecutor.SendPromptAsync(session, new PromptRequest { Text = "\u001b[B", AppendEnter = false, Surface = "browser" }));
        var enter = Body(await SessionCommandExecutor.SendPromptAsync(session, new PromptRequest { Text = "\r", AppendEnter = false, Surface = "browser" }));

        Assert.True(down.Accepted, down.Error);
        Assert.True(enter.Accepted, enter.Error);
        Assert.Equal(new[] { "\u001b[B", "\r" }, terminal.Writes);
    }

    /// <summary>An agent's send - a fleet message, the Fleet Manager's events, a schedule - is not the owner: it is still
    /// refused at the dialog with the reason, whatever surface its body claims, with or without an Enter.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendPromptAsync_AgentDrivenSendWhileTheDialogShows_RefusedWithAReasonAndNothingTyped(bool appendEnter)
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "2", AppendEnter = appendEnter, Surface = "phone", AgentDriven = true };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        AssertRefusedAtTheDialog(response);
        Assert.Empty(terminal.Writes);
        Assert.Empty(terminal.Submitted);
    }

    /// <summary>A prompt the Gateway sends with no surface - its own prompts, a dev report, the Wingman - is refused too.</summary>
    [Fact]
    public async Task SendPromptAsync_PromptWithNoSurfaceWhileTheDialogShows_RefusedAndNothingTyped()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "2", AppendEnter = false };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        AssertRefusedAtTheDialog(response);
        Assert.Empty(terminal.Writes);
    }

    /// <summary>Escape still reaches the dialog by the escape verb - an agent's and the phone's way to close it - as it
    /// always has: it is the one key that only closes it.</summary>
    [Fact]
    public async Task EscapeCoreAsync_WhileTheDialogShows_PressesEscape()
    {
        var (session, terminal) = SessionShowing(DialogRows);

        var result = await SessionCommandExecutor.EscapeCoreAsync(session);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("\u001b", terminal.Writes);
    }

    /// <summary>The Cockpit pane's own Escape key reaches the dialog too.</summary>
    [Fact]
    public async Task SendPromptAsync_CockpitPaneEscapeWhileTheDialogShows_IsWritten()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "\u001b", AppendEnter = false, Surface = "browser" };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        Assert.True(response.Accepted, response.Error);
        Assert.Equal(new[] { "\u001b" }, terminal.Writes);
    }

    /// <summary>A delivery whose device did not resolve ("unknown") proves no person sent it: refused, and recorded so.</summary>
    [Fact]
    public async Task SendPromptAsync_RecordedDeliveryFromAnUnknownSurfaceWhileTheDialogShows_RecordSaysNotDeliveredWithTheReason()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest
        {
            Text = "carry on", AppendEnter = true, Surface = "unknown", DeliveryId = "usage-limit-1", DeliveryUploadId = "usage-limit-1",
        };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request, SendSource.Delivery, _record));

        AssertRefusedAtTheDialog(response);
        Assert.Empty(terminal.Writes);
        var entry = _record.Read(session.Id, "usage-limit-1");
        Assert.Equal(DeliveryState.NotDelivered, entry.State);
        Assert.Equal(SessionCommandExecutor.UsageLimitDialogRefusal, entry.Reason);
    }

    [Fact]
    public async Task SendPromptAsync_PhoneAnswerToAPermissionPromptUnderUnrelatedLimitText_ReachesIt()
    {
        var (session, terminal) = SessionShowing(PermissionPromptRows);
        var request = new PromptRequest { Text = "1", AppendEnter = false, Surface = "phone" };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        Assert.True(response.Accepted);
        Assert.Equal(new[] { "1" }, terminal.Writes);
    }
}
