using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Shared terminal-submit primitives for interactive CLI drivers. The echo-verified submit was
/// proven first on ClaudeDriver and then on CodexDriver: many TUIs repaint a cycling placeholder
/// in their composer, so a blind "type text, wait, one Enter" loses the Enter when driven
/// programmatically (the Enter lands mid-repaint and is swallowed, parking the prompt unsubmitted).
/// The fix is to type the text, wait until the composer echoes it back in the terminal byte stream,
/// then press Enter as a separate keystroke. This class is the single home for that logic so every
/// driver (Codex, Pi, ...) uses one tested implementation.
/// </summary>
public static class TerminalSubmit
{
    private static readonly byte[] EnterByte = [0x0D];
    private static readonly byte[] EscapeByte = [0x1B];

    /// <summary>
    /// Type <paramref name="text"/>, wait for the composer to echo it, then press Enter. Falls back
    /// to the backend's blind submit for large/multi-line input (the @-temp-file path) and for
    /// non-buffering backends (nothing to echo-verify against). Throws if the composer never echoes
    /// the typed text after two attempts, rather than silently parking the prompt.
    /// </summary>
    public static async Task EchoVerifiedSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (LargeInputHandler.IsLargeInput(text.TrimEnd('\r', '\n')))
        {
            await backend.SendTextAsync(text);
            return;
        }

        var buffer = backend.Buffer;
        if (buffer is null)
        {
            await backend.SendTextAsync(text);
            return;
        }

        var to = echoTimeout ?? TimeSpan.FromSeconds(4);
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var settle = enterSettleDelay ?? TimeSpan.FromMilliseconds(40);
        var needle = NormalizeForEcho(text);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var cursor = buffer.TotalBytesWritten;
            backend.Write(Encoding.UTF8.GetBytes(text));

            if (needle.Length == 0 || await WaitForEchoAsync(buffer, cursor, needle, to, poll))
            {
                await Task.Delay(settle);
                backend.Write(EnterByte);
                return;
            }

            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen on attempt {attempt} " +
                          $"(len={text.Length}) - clearing the composer and retyping");
            backend.Write(EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new InvalidOperationException(
            $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts - " +
            "the TUI is not accepting input (a modal, a picker, or a composer still initializing).");
    }

    /// <summary>Poll the terminal byte stream until the typed text echoes back in the composer.</summary>
    private static async Task<bool> WaitForEchoAsync(
        CircularTerminalBuffer buffer, long cursor, string needle, TimeSpan timeout, TimeSpan poll)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (bytes, _) = buffer.GetWrittenSince(cursor);
            var hay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
            if (hay.Contains(needle, StringComparison.Ordinal))
                return true;
            await Task.Delay(poll);
        }
        return false;
    }

    /// <summary>Drop ANSI escape sequences (CSI / OSC / two-byte) from a terminal chunk.</summary>
    public static string StripAnsi(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\x1B')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= raw.Length) break;
            var kind = raw[i + 1];
            if (kind == '[')
            {
                i += 2;
                while (i < raw.Length && (raw[i] < '\x40' || raw[i] > '\x7E')) i++;
            }
            else if (kind == ']')
            {
                i += 2;
                while (i < raw.Length && raw[i] != '\a' && raw[i] != '\x1B') i++;
                if (i + 1 < raw.Length && raw[i] == '\x1B') i++;
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Letters, digits and '/' only - the comparison alphabet for composer echo checks.</summary>
    public static string NormalizeForEcho(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '/')
                sb.Append(c);
        return sb.ToString();
    }
}
