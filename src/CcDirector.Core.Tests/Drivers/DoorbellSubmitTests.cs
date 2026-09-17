using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// The doorbell's own submit (the Message Load mission, slice 2 fix round, inspection 4 ruling 2): one Enter,
/// no nudges, no Escape, and verified only by the screen.
/// </summary>
public sealed class DoorbellSubmitTests
{
    private const string Line = "[DevThrottle doorbell] 1 fleet message is waiting for you. To read, run: cc-devthrottle message inbox";

    private static readonly Func<TimeSpan, Task> NoWait = _ => Task.CompletedTask;

    private static int Escapes(RecordingSessionBackend b) => b.WrittenBytes.Count(w => w.Length == 1 && w[0] == 0x1B);
    private static int Enters(RecordingSessionBackend b) => b.WrittenBytes.Count(w => w.Length == 1 && w[0] == 0x0D);

    private static Task<DoorbellSubmitOutcome> Submit(
        RecordingSessionBackend backend, Func<bool>? may = null, Func<bool>? shows = null, Func<bool>? started = null) =>
        TerminalSubmit.DoorbellSubmitAsync(
            backend, Line, "ClaudeCode",
            may ?? (() => true),
            shows ?? (() => false),
            started ?? (() => backend.SubmittedTexts.Count > 0),
            pause: NoWait);

    [Fact]
    public async Task A_backend_that_swallows_Enter_gets_exactly_one_Enter_and_no_nudge()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate().NotAcceptingSubmit());

        var outcome = await Submit(backend);

        Assert.Equal(DoorbellSubmitOutcome.NotVerified, outcome);
        Assert.Equal(1, Enters(backend));
        Assert.Equal(0, Escapes(backend));
        Assert.Equal(Line, backend.ParkedComposerText);
    }

    [Fact]
    public async Task A_quiet_submitted_turn_is_verified_by_the_screen_and_gets_no_second_Enter()
    {
        // The agent answers with one word: far below the shared verifier's 2,048 bytes.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer(), SubmitResponseBytes = 8 };

        var outcome = await Submit(backend);

        Assert.Equal(DoorbellSubmitOutcome.Verified, outcome);
        Assert.Equal(1, Enters(backend));
        Assert.Equal([Line], backend.SubmittedTexts);
    }

    [Fact]
    public async Task A_line_the_interface_cleared_without_a_turn_is_not_verified()
    {
        // Enter lands, the composer empties, but the screen never shows the turn.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer(), SubmitResponseBytes = 0 };

        var outcome = await Submit(backend, started: () => false);

        Assert.Equal(DoorbellSubmitOutcome.NotVerified, outcome);
        Assert.Equal(1, Enters(backend));
    }

    [Fact]
    public async Task The_last_look_saying_no_writes_nothing()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        var outcome = await Submit(backend, may: () => false);

        Assert.Equal(DoorbellSubmitOutcome.NotTyped, outcome);
        Assert.Empty(backend.WrittenBytes);
    }

    [Fact]
    public async Task A_line_that_never_echoes_is_not_submitted_and_not_cleared()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var outcome = await Submit(backend);

        Assert.Equal(DoorbellSubmitOutcome.NotVerified, outcome);
        Assert.Equal(0, Enters(backend));
        Assert.Equal(0, Escapes(backend));
    }

    [Fact]
    public async Task The_rendered_composer_is_a_second_witness_for_the_echo()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        await Submit(backend, shows: () => true, started: () => true);

        Assert.Equal(1, Enters(backend));
    }

    [Fact]
    public async Task Owner_text_typed_after_the_Enter_is_never_submitted_by_the_doorbell()
    {
        // The line parks; the owner then starts typing. The shared verifier would press Enter again on the next
        // quiet beat and submit the owner's words with ours. The doorbell does not.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate().NotAcceptingSubmit());
        var polls = 0;

        var outcome = await Submit(backend, started: () =>
        {
            if (++polls == 1) backend.EchoScript.RecordTypedText(" the owner's words");
            return false;
        });

        Assert.Equal(DoorbellSubmitOutcome.NotVerified, outcome);
        Assert.Equal(1, Enters(backend));
        Assert.Empty(backend.SubmittedTexts);
    }
}
