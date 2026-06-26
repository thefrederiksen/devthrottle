using System.Text;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class TerminalSubmitTests
{
    [Fact]
    public async Task EchoVerifiedSubmit_EchoingBackend_TypesTextThenSeparateEnter()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.EchoVerifiedSubmitAsync(backend, "hello world", "Test");

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hello world"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public async Task EchoVerifiedSubmit_NoBuffer_FallsBackToBlindSendText()
    {
        var backend = new RecordingSessionBackend(); // Buffer is null

        await TerminalSubmit.EchoVerifiedSubmitAsync(backend, "hi", "Test");

        Assert.Equal("hi", Assert.Single(backend.SentTexts));
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
