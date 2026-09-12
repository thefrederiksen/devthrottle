using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class TerminalSubmitTests
{
    /// <summary>
    /// Fast beat for the post-Enter submit watchdog so the suite does not wait out real-time beats.
    /// Every test that reaches an Enter passes this; the watchdog's own semantics are covered in
    /// <see cref="SubmitVerifierTests"/>.
    /// </summary>
    private static readonly TimeSpan FastVerifyBeat = TimeSpan.FromMilliseconds(20);

    [Theory]
    [InlineData(301)]
    [InlineData(500)]
    [InlineData(750)]
    [InlineData(LargeInputHandler.LargeInputThreshold)]
    public async Task SharedSubmit_CodexSingleLineAtOrBelowLargeThreshold_SubmitsOriginalTextInline(int length)
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"TerminalSubmitTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var text = new string('x', length);
            var backend = new RecordingSessionBackend
            {
                Buffer = new CircularTerminalBuffer(),
                WorkingDirectory = repositoryPath,
            };

            await TerminalSubmit.SharedSubmitAsync(
                backend,
                text,
                "CodexDriver",
                submitVerifyBeat: FastVerifyBeat);

            Assert.Equal([text], backend.SubmittedTexts);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SharedSubmit_CodexSingleLineAboveLargeThreshold_SubmitsTruthfulFileInstruction()
    {
        await AssertCodexUsesTruthfulFileInstructionAsync(new string('x', LargeInputHandler.LargeInputThreshold + 1));
    }

    [Fact]
    public async Task SharedSubmit_CodexMultilineInput_SubmitsTruthfulFileInstruction()
    {
        await AssertCodexUsesTruthfulFileInstructionAsync("first line\nsecond line");
    }

    [Fact]
    public async Task SharedSubmit_CopilotSingleLineOverLegacyThreshold_KeepsInstructionFileRoute()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"TerminalSubmitTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var backend = new RecordingSessionBackend
            {
                Buffer = new CircularTerminalBuffer(),
                WorkingDirectory = repositoryPath,
            };

            await TerminalSubmit.SharedSubmitAsync(
                backend,
                new string('x', 301),
                "CopilotDriver",
                submitVerifyBeat: FastVerifyBeat);

            Assert.StartsWith(
                "Read and respond to the complete incoming message in .temp/input_",
                Assert.Single(backend.SubmittedTexts));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    private static async Task AssertCodexUsesTruthfulFileInstructionAsync(string text)
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"TerminalSubmitTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var backend = new RecordingSessionBackend
            {
                Buffer = new CircularTerminalBuffer(),
                WorkingDirectory = repositoryPath,
            };

            await TerminalSubmit.SharedSubmitAsync(
                backend,
                text,
                "CodexDriver",
                submitVerifyBeat: FastVerifyBeat);

            var submitted = Assert.Single(backend.SubmittedTexts);
            Assert.StartsWith("Read and respond to the complete incoming message in .temp/input_", submitted);
            Assert.EndsWith(".txt.", submitted);
            Assert.DoesNotContain("user-provided message", submitted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requested strings only", submitted, StringComparison.OrdinalIgnoreCase);

            var payloadFile = Assert.Single(Directory.GetFiles(Path.Combine(repositoryPath, ".temp"), "input_*.txt"));
            Assert.Equal(text, File.ReadAllText(payloadFile));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task EchoVerifiedSubmit_EchoingBackend_TypesTextThenSeparateEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend, "hello world", "Test", submitVerifyBeat: FastVerifyBeat);

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hello world"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_DelayedEcho_WritesEnterOnlyAfterEcho()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Delayed(TimeSpan.FromMilliseconds(120)));

        var submit = TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "hello delayed echo",
            "Test",
            echoTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat);

        await Task.Delay(40);

        Assert.Single(backend.WrittenBytes);
        Assert.Equal(Encoding.UTF8.GetBytes("hello delayed echo"), backend.WrittenBytes[0]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);

        await submit;

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Equal(["hello delayed echo"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_RepaintingPlaceholder_WaitsForRealEchoBeforeEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(
            RecordingEchoStep.RepaintingPlaceholder("thinking placeholder", TimeSpan.FromMilliseconds(80)));

        var submit = TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "hello after repaint",
            "Test",
            echoTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat);

        await Task.Delay(30);

        Assert.Single(backend.WrittenBytes);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);

        await submit;

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Equal(["hello after repaint"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_ScrolledComposerTailEcho_WritesEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        var text = "Do not modify files. Reply OKCODEXSENTDIRECT1A444B4E6BFDB40 only.";
        var visibleTail = TerminalSubmit.NormalizeForEcho(text)[^16..];
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho(visibleTail));

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            text,
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat);

        Assert.True(backend.WrittenBytes.Count > 2);
        Assert.Equal(Encoding.UTF8.GetBytes(text), backend.WrittenBytes.SkipLast(1).SelectMany(b => b).ToArray());
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[^1]);
        Assert.Equal([text], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_ShortPartialTail_DoesNotSubmit()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho("world"));

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "hello world",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.EnterCount);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_FirstEchoMissing_EscapesAndRetypesBeforeEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.Enqueue(RecordingEchoStep.Withheld());
        backend.EchoScript.Enqueue(RecordingEchoStep.Immediate());

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "retry me",
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat);

        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("retry me"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("retry me"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[3]);
        Assert.Equal(["retry me"], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_EchoNeverAppears_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "never echoed",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("never echoed"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("never echoed"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[3]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task GenericDriverSubmit_WithheldEcho_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend
        {
            Buffer = new CircularTerminalBuffer(),
        };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "generic prompt",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task ClaudeDriverSubmit_SlashCorruptedEcho_ThrowsWithoutEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.Enqueue(RecordingEchoStep.SlashCorrupted("write this"));
        backend.EchoScript.Enqueue(RecordingEchoStep.SlashCorrupted("write this"));
        var driver = new ClaudeDriver(new EmptyTranscriptReader(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => driver.SubmitAsync(backend, "write this"));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("write this"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("write this"), backend.WrittenBytes[2]);
        Assert.Equal(new byte[] { 0x1B }, backend.WrittenBytes[3]);
        Assert.Equal(0, backend.EnterCount);
        Assert.Empty(backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_NoBuffer_WritesTextAndEnter()
    {
        var backend = new RecordingSessionBackend(); // Buffer is null

        await TerminalSubmit.EchoVerifiedSubmitAsync(backend, "hi", "Test");

        Assert.Empty(backend.SentTexts);
        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hi"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
    }

    // ===== screen-grid fallback (issue #1308) ======================================================
    // A long dictation that WRAPS across composer rows is repainted interleaved with box borders and
    // footer hints, so the byte stream may never carry the typed text as one contiguous run even
    // though it is sitting in the composer. The rendered screen is consulted as a second opinion
    // before Escape/retype/throw.

    [Fact]
    public async Task EchoVerifiedSubmit_EchoTornInByteStream_ScreenSnapshotRescuesAndPressesEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        var text = "Do you understand what it is I am talking about? And can you please also rename this session";
        // The byte stream only ever shows torn fragments interleaved with the footer hint.
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho("bypasspermissionson esc again to clear"));
        // The rendered screen shows the text wrapped across two composer rows, with borders and padding.
        var screen = new[]
        {
            "| Do you understand what it is I am talking       |",
            "| about? And can you please also rename this      |",
            "| session                                         |",
            "  bypass permissions on (shift+tab to cycle)",
        };

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            text,
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            screenSnapshot: () => screen,
            submitVerifyBeat: FastVerifyBeat);

        // Rescued on attempt 1: text typed once, Enter pressed, and no Escape ever disturbed the composer.
        Assert.Equal(1, backend.EnterCount);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[^1]);
        Assert.DoesNotContain(backend.WrittenBytes, b => b.Length == 1 && b[0] == 0x1B);
        Assert.Equal([text], backend.SubmittedTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_ScreenSnapshotWithoutTheText_StillFailsLoudly()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());
        var screen = new[] { "Some unrelated conversation output", "> ", "esc to interrupt" };

        var error = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "these words never reached the composer",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                screenSnapshot: () => screen));

        Assert.Contains("never echoed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.EnterCount);
    }

    // ===== post-Enter submit verification (pull request #1513) =============================================
    // The echo check proves the text ARRIVED in the composer. These pin the other half: that it LEFT.
    // The live shape was a phone dictation whose Enter the TUI swallowed - the text sat in the
    // composer, the session was marked Working, and the NEXT dictation typed itself onto the end of
    // the orphan (both prompts visible run-together in one composer, neither ever sent).

    /// <summary>
    /// A SHORT, single-line prompt - the common route, and every phone dictation. Before this fix it
    /// was the one route with no post-Enter verification at all: only the large/multi-line
    /// @-temp-file route was watched, so exactly the everyday case went unchecked.
    /// </summary>
    [Fact]
    public async Task ShortPrompt_TuiSwallowsTheEnter_NudgedThroughInsteadOfParking()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        // The composer takes the text but is not ready to submit when the first Enter lands, so that
        // Enter is LOST and nothing streams. It starts accepting again shortly after, so the
        // watchdog's nudge is what actually sends the prompt.
        backend.EchoScript.UseDefault(
            RecordingEchoStep.CustomEcho("short dictation").SwallowsEnterUntilReady(TimeSpan.FromMilliseconds(30)));

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend,
            "short dictation",
            "Test",
            echoTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            enterSettleDelay: TimeSpan.FromMilliseconds(1),
            submitVerifyBeat: FastVerifyBeat);

        // How MANY nudges it takes is timing, not behaviour: the beat and the composer's recovery
        // window race, so pinning an exact count would be a flake. The invariants are that an Enter
        // really was swallowed, that the watchdog kept nudging, and that the prompt went through.
        Assert.True(backend.LostEnterCount >= 1, "the fake must have swallowed the submitting Enter");
        Assert.True(backend.EnterCount > backend.LostEnterCount, "the watchdog must nudge the parked composer");
        Assert.Equal(["short dictation"], backend.SubmittedTexts);
        Assert.Empty(backend.ParkedComposerText);
    }

    /// <summary>
    /// The failure the operator actually saw: the Enter is swallowed and the TUI stays dead. The
    /// submit must FAIL rather than return success, because a quiet return is what marked the session
    /// Working for a turn that never started and let the next send append to the orphan.
    /// </summary>
    [Fact]
    public async Task ShortPrompt_EnterNeverLands_ThrowsInsteadOfReportingSuccess()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        // Echoes (so the text is provably in the composer) but never accepts a submit: every Enter,
        // including every nudge, is swallowed and nothing ever streams.
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho("dictation that never sends").NotAcceptingSubmit());

        var error = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => TerminalSubmit.EchoVerifiedSubmitAsync(
                backend,
                "dictation that never sends",
                "Test",
                echoTimeout: TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                submitVerifyBeat: FastVerifyBeat));

        Assert.Contains("parked in the composer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(backend.SubmittedTexts);
        Assert.Equal("dictation that never sends", backend.ParkedComposerText);
    }

    /// <summary>
    /// A submit that lands first time must not be nudged. The nudge is an extra Enter, and an extra
    /// Enter into a composer the operator is typing into would submit their half-written text - so
    /// "only nudge what we can SEE is parked" is a safety property, not just an efficiency one.
    /// </summary>
    [Fact]
    public async Task ShortPrompt_EnterLandsFirstTime_NoNudgeEverSent()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend, "clean send", "Test", submitVerifyBeat: FastVerifyBeat);

        Assert.Equal(1, backend.EnterCount);
        Assert.Equal(0, backend.LostEnterCount);
        Assert.Equal(["clean send"], backend.SubmittedTexts);
    }

    /// <summary>
    /// Issue #1591 / #1592, from the live failure on 2026-07-15 05:31:31 that lost two phone
    /// dictations. Claude Code repaints its footer by moving the cursor away from the composer,
    /// drawing "bypass permissions on shift+tab to cycle" and "Esc again to clear", then moving
    /// back and continuing the composer text. StripAnsi discards those cursor moves - the only
    /// thing that said the footer text belongs ELSEWHERE on screen - so the hint is concatenated
    /// into the middle of the typed text, splitting "great" into "notgrea" + hint + "tforus".
    ///
    /// The echo check then runs a linear substring search over what is really two-dimensional
    /// content, never finds the needle, declares "the TUI is not accepting input", and throws.
    /// The text was in the composer the whole time.
    ///
    /// The echo text below is the REAL captured byte tail from that incident, not a synthetic
    /// approximation. Synthetic terminal output is what let this defect pass review twice.
    /// </summary>
    [Fact]
    public async Task EchoVerifiedSubmit_FooterHintRepaintedIntoTypedText_StillRecognizesTheEcho()
    {
        const string typed =
            "that is weird and not great for us We might have pushed the skills to the agents anyway " +
            "we just didnt commit them So lets figure out the skills and stop being a fuck ing scatterbrain " +
            "and deal with one thing at a time please We want the repo cleaned up but we want to do it " +
            "systematically and understand what it is were doing";

        // The real bytes: the composer echo with the footer hints spliced in mid-word at each of the
        // three points the TUI repainted, exactly as captured in the director log.
        const string hint = "\x1B[2K bypass permissions on shift+tab to cycle   Esc again to clear \x1B[1A";
        var interleavedEcho =
            "that is weird and not grea" + hint + "t for us We might have pushed the skills to the agents anyway " +
            "we just didnt commit them So lets figure out the skills and stop being a fuck" + hint + "ing scatterbrain " +
            "and deal with one thing at a time please We want the repo cleaned up but we want to do it " +
            "systematically and understand what" + hint + " it is were doing";

        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho(interleavedEcho));

        // The text IS in the composer, so this must submit. Today it throws
        // ComposerNotAcceptingInputException and the user's dictation is lost.
        await TerminalSubmit.EchoVerifiedSubmitAsync(
            backend, typed, "ClaudeCode", submitVerifyBeat: FastVerifyBeat);

        Assert.Equal(1, backend.EnterCount);
        Assert.Equal([typed], backend.SubmittedTexts);
    }

    [Fact]
    public void StripAnsi_RemovesCsiSequences()
    {
        var raw = "\x1B[31mred\x1B[0m text";
        Assert.Equal("red text", TerminalSubmit.StripAnsi(raw));
    }

    [Fact]
    public void NormalizeForEcho_KeepsLettersDigitsSlash()
    {
        Assert.Equal("abc123/clear", TerminalSubmit.NormalizeForEcho("a b c 1-2_3 /clear!"));
    }
}
