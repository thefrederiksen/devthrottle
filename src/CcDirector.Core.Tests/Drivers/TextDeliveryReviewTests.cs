using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>Findings of the Fable review of text delivery (issue #3290, 24 September 2026) that need a recording terminal.</summary>
public sealed class TextDeliveryReviewTests : IDisposable
{
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public void Dispose() => _machine.Dispose();

    [Fact]
    public async Task An_echo_miss_on_a_silent_terminal_says_the_agent_never_reacted_so_no_retry_is_typed()
    {
        // Finding 11: a retry is only safe when the agent was reading its input.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => TerminalSubmit.SharedSubmitAsync(
            backend, "never echoed", "Test", echoTimeout: TimeSpan.FromMilliseconds(20), pollInterval: TimeSpan.FromMilliseconds(5)));

        Assert.False(error.TerminalReacted);
    }

    [Fact]
    public async Task A_long_Claude_text_goes_as_an_at_reference_not_a_paste()
    {
        // Under full CPU load Claude Code took in none of an 8 KB paste; the @-reference arrived in seconds.
        var dir = Path.Combine(Path.GetTempPath(), "cc-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer(), WorkingDirectory = dir };
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());
        var text = "Token QX. " + new string('a', TerminalSubmit.PasteLimit + 10);

        var typed = await TerminalSubmit.SharedSubmitAsync(backend, text, "ClaudeCode", bracketedPasteEnabled: true,
            echoTimeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(5));

        Assert.StartsWith("@", typed);
        Assert.True(typed.Length < 100);
        Assert.DoesNotContain(backend.WrittenBytes, b => System.Text.Encoding.UTF8.GetString(b).Contains("[200~"));
        Assert.Equal(text, File.ReadAllText(Path.Combine(dir, typed[1..])));
    }

    [Fact]
    public async Task A_long_Pi_text_goes_through_the_instruction_file_not_a_paste()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-pi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer(), WorkingDirectory = dir };
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());
        var text = "Token QP. " + new string('b', TerminalSubmit.PasteLimit + 10);

        var typed = await TerminalSubmit.SharedSubmitAsync(backend, text, "Pi", bracketedPasteEnabled: true,
            echoTimeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(5));

        Assert.StartsWith("The user's message for this turn is in the file", typed);
        Assert.DoesNotContain(backend.WrittenBytes, b => System.Text.Encoding.UTF8.GetString(b).Contains(new string('b', 50)));
    }

    [Fact]
    public async Task The_payload_instruction_is_neutral_and_short()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-instr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Every long Codex prompt since July ended "reply with the requested strings only".
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer(), WorkingDirectory = dir };
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());

        var typed = await TerminalSubmit.SharedSubmitAsync(backend, new string('a', 400), "CodexDriver",
            echoTimeout: TimeSpan.FromMilliseconds(50), pollInterval: TimeSpan.FromMilliseconds(5),
            submitVerifyBeat: TimeSpan.FromMilliseconds(1));

        Assert.DoesNotContain("requested strings", typed);
        Assert.StartsWith("The user's message for this turn is in the file ", typed);
        Assert.True(typed.Length < 200, $"instruction is {typed.Length} characters, the old one was 367");
    }
}
