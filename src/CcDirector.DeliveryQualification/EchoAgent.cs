using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE BYTE-ECHO AGENT (issue #4378): the rig itself, started with <c>--echo-agent &lt;log&gt;</c> inside a real Director
/// terminal in place of an agent. It records EVERY byte the Director writes to it - one JSON line per read, with the
/// bytes in hex - so the echo layer can see exactly what each input route sends: bracketed paste or not, an Enter in the
/// middle of a paste, how a character outside ASCII was encoded. No model is involved, so it costs nothing.
///
/// It behaves like the simplest composer an agent draws, because the Director's send path checks what it sees:
///  - it asks for bracketed paste (<c>ESC[?2004h</c>), as Claude Code, Codex and Pi do;
///  - it echoes printable text, so the send path's echo check finds the typed words, and repaints when a paste ends, as
///    the send path waits for an agent to do before it presses Enter;
///  - Enter outside a paste "submits": it prints a block of a few kilobytes, the output a turn starting produces, so the
///    send path's post-Enter check sees the turn begin.
/// It never exits on its own and ignores Ctrl+C; the rig kills its session.
///
/// With <c>--usage-dialog</c> it also draws Claude Code's usage-limit dialog at the bottom of its screen, and draws it again
/// after every submit, so the Director's usage-limit guard reads the dialog as showing - the words and the shape of the real
/// captured screens (<see cref="UsageDialogRows"/>). Claude Code shows that dialog only when the owner's allowance has run
/// out, which no run can bring about on demand, and the guard that drops input is the Director's own code reading the
/// screen, so this is how the cell measures it (issue #4378, the usage-limit cell).
/// </summary>
public static class EchoAgent
{
    /// <summary>The output printed for a submit: more than the 2048 bytes the send path's verifier counts as a turn.</summary>
    internal const int SubmitPadBytes = 3000;

    /// <summary>Claude Code's usage-limit dialog as the captured screens draw it (the product's UsageLimitDialogTests,
    /// "rows intact"): the question, the three numbered options, and the confirm and cancel hint.</summary>
    internal static readonly string[] UsageDialogRows =
    [
        "  What do you want to do?",
        "  \u276f 1. Stop and wait for limit to reset",
        "    2. Add funds to continue with usage credits",
        "    3. Switch to Team plan",
        "  Enter to confirm \u00b7 Esc to cancel",
    ];

    /// <summary>Enough blank lines that the dialog lands in the lower half of a 40-row screen, then the dialog.</summary>
    internal static string UsageDialog() => new string('\n', 30).Replace("\n", "\r\n") + string.Join("\r\n", UsageDialogRows) + "\r\n";

    public static int Run(string logPath, bool usageDialog = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("[echo-agent] The Windows echo agent is not built yet; it is part of the Windows baseline run.");

        // Raw mode through stty, on the terminal this process was given: no line buffering, no echo by the terminal,
        // no signal for Ctrl+C. Console is never touched - the runtime's own terminal handling would change the mode.
        RunStty("raw -echo");
        using var input = new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, 1, false);
        using var output = new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, 1, false);
        void Write(string s) { var b = Encoding.UTF8.GetBytes(s); output.Write(b); output.Flush(); }

        Write("\u001b[?2004h");
        Write("echo-agent ready\r\n> ");
        if (usageDialog) Write(UsageDialog());
        var clock = Stopwatch.StartNew();
        var screen = new EchoScreen(Write, usageDialog ? () => Write(UsageDialog()) : null);
        var buffer = new byte[65536];
        while (true)
        {
            var n = input.Read(buffer, 0, buffer.Length);
            if (n <= 0) return 0;
            var chunk = buffer.AsSpan(0, n).ToArray();
            log.WriteLine(JsonSerializer.Serialize(new EchoRead(clock.Elapsed.TotalMilliseconds, Convert.ToHexString(chunk))));
            screen.Feed(chunk);
        }
    }

    private static void RunStty(string args)
    {
        var psi = new ProcessStartInfo("stty", args) { UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("[echo-agent] stty did not start");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"[echo-agent] stty {args} failed with {p.ExitCode}");
    }

    /// <summary>The log of everything the agent read, in order, as one byte array per read.</summary>
    public static List<EchoRead> ReadLog(string logPath)
    {
        if (!File.Exists(logPath)) return [];
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var reads = new List<EchoRead>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { reads.Add(JsonSerializer.Deserialize<EchoRead>(line)!); }
            catch (JsonException) { /* a line still being written */ }
        }
        return reads;
    }
}

/// <summary>One read by the echo agent: when, in milliseconds since it started, and the bytes as hex.</summary>
public sealed record EchoRead(double Ms, string Hex)
{
    public byte[] Bytes => Convert.FromHexString(Hex);
}

/// <summary>
/// What the echo agent draws for the bytes it reads: the typed text echoed, nothing for an escape sequence, a newline for
/// Enter inside a paste, and the submit block for Enter outside one.
/// </summary>
internal sealed class EchoScreen(Action<string> write, Action? afterSubmit = null)
{
    private enum Mode { Text, Escape, Csi }

    private Mode _mode;
    private readonly StringBuilder _csi = new();
    private bool _inPaste;
    private readonly List<byte> _pending = new();

    public void Feed(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            switch (_mode)
            {
                case Mode.Escape:
                    _mode = b == (byte)'[' ? Mode.Csi : Mode.Text;
                    _csi.Clear();
                    continue;
                case Mode.Csi:
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        var seq = _csi.ToString() + (char)b;
                        if (seq == "200~") _inPaste = true;
                        else if (seq == "201~")
                        {
                            _inPaste = false;
                            // A real agent repaints its composer once a paste is in, and the send path waits for that
                            // repaint before it presses Enter; so this one does too - a quarter of a second later, as an
                            // agent takes a moment. Repainting at once races the send path: it counts the terminal's bytes
                            // only AFTER writing the paste's end, so a repaint that lands first is never seen and the send
                            // waits out its two-minute limit (found by this harness, 23 October 2026; see issue #4379).
                            Flush();
                            Thread.Sleep(250);
                            write("\r\n[paste taken in]\r\n");
                        }
                        _mode = Mode.Text;
                    }
                    else _csi.Append((char)b);
                    continue;
            }

            switch (b)
            {
                case 0x1B: Flush(); _mode = Mode.Escape; break;
                case 0x0D:
                    Flush();
                    if (_inPaste) write("\r\n");
                    else
                    {
                        write("\r\n[submitted]\r\n" + new string('.', EchoAgent.SubmitPadBytes) + "\r\n> ");
                        afterSubmit?.Invoke();
                    }
                    break;
                case 0x0A: Flush(); write("\r\n"); break;
                case 0x7F: case 0x08: Flush(); write("\b \b"); break;
                case < 0x20: Flush(); break;
                default: _pending.Add(b); break;
            }
        }
        Flush();
    }

    private void Flush()
    {
        if (_pending.Count == 0) return;
        write(Encoding.UTF8.GetString(_pending.ToArray()));
        _pending.Clear();
    }
}
