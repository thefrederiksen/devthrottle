using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The License Switching mission, trap 4: a prompt from the phone or the Cockpit never reaches Claude Code's usage-limit
/// dialog - and the sender is TOLD so. The fix-round review of pull request 3863 found the guard dropped the prompt and
/// the verb still answered success, so the phone showed a delivered answer that never arrived. Driven through the real
/// prompt core against a real session, asserted on what the terminal RECEIVED.
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendPromptAsync_PhoneAnswerWhileTheDialogShows_RefusedWithAReasonAndNothingTyped(bool appendEnter)
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest { Text = "2", AppendEnter = appendEnter, Surface = "phone" };

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request));

        AssertRefusedAtTheDialog(response);
        Assert.Empty(terminal.Writes);
        Assert.Empty(terminal.Submitted);
    }

    [Fact]
    public async Task SendPromptAsync_RecordedDeliveryWhileTheDialogShows_RecordSaysNotDeliveredWithTheReason()
    {
        var (session, terminal) = SessionShowing(DialogRows);
        var request = new PromptRequest
        {
            Text = "carry on", AppendEnter = true, Surface = "cockpit", DeliveryId = "usage-limit-1", DeliveryUploadId = "usage-limit-1",
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
