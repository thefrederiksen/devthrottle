using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// EVERY LINE THE PROCESS LOGS WHILE THIS IS OPEN, read back with nothing in between that can lose one. A test that
/// proves "no log line carries X" must first be sure it has the lines: a capture that came back empty would pass every
/// such check for the wrong reason.
///
/// HOW. <see cref="FileLog.Write"/> hands each line to the console on the caller's own thread when
/// <see cref="FileLog.MirrorToConsole"/> is on, before it returns. This takes the console's output into memory for its
/// life, so a line is here the moment the code under test has written it - no file, no writer thread, no flush to wait
/// for. The log is also redirected to a throwaway folder for the same time (<see cref="FileLog.RedirectForTests"/>),
/// which is what makes the log accept lines in a test process and keeps them out of the machine's own log.
///
/// ONE TEST OWNS THE LOG AT A TIME, AND THE TEST CLASS MUST SAY SO. The log's writer, its mirror switch and the console
/// are each one per process. The hosted test assembly runs one test at a time. The Gateway unit test assembly runs four
/// classes side by side, so a class there that opens one of these MUST carry
/// <c>[Collection(FileLogCaptureCollection.Name)]</c>. Without it, another class's capture closing in the middle of
/// this one turns the log off for a moment and a line is lost - measured on 13 October 2026 as about one empty capture
/// in every few hundred, in three classes that had left the attribute off.
///
/// IT PROVES ITSELF. It writes a line of its own when it opens, and <see cref="Lines"/> fails when that line is not
/// among what it captured, or when the console or the mirror is no longer this capture's - so a capture that lost
/// lines is a failed test that says why, never a passed one.
///
/// A background task left over from an earlier test may still write a line into this capture; assert on the lines of
/// the code under test, never on how many lines there are.
/// </summary>
internal sealed class CapturedLog : IDisposable
{
    private readonly string _opened = $"[CapturedLog] opened {Guid.NewGuid():N}";
    private readonly IDisposable _redirect;
    private readonly TextWriter _previousOut;
    private readonly bool _previousMirror;
    private readonly StringWriter _text = new();
    // Every write goes through this wrapper, which takes its own lock for each one; a read takes the same lock.
    private readonly TextWriter _captured;

    public CapturedLog()
    {
        _captured = TextWriter.Synchronized(_text);
        _redirect = FileLog.RedirectForTests();
        _previousOut = Console.Out;
        _previousMirror = FileLog.MirrorToConsole;
        Console.SetOut(_captured);
        FileLog.MirrorToConsole = true;
        FileLog.Write(_opened);
    }

    /// <summary>Every line captured so far, without its timestamp's line break. Fails when the capture did not even
    /// hold the line this wrote on opening.</summary>
    public IReadOnlyList<string> Lines()
    {
        string text;
        lock (_captured)
            text = _text.ToString();
        // Something else that takes the console or the log while this is open would make every line after it vanish
        // from here. Say so by name instead of letting a test fail on a line it cannot find.
        Assert.True(ReferenceEquals(Console.Out, _captured) && FileLog.MirrorToConsole,
            $"The log capture was taken away while it was open: the console is {(ReferenceEquals(Console.Out, _captured) ? "still" : "NO LONGER")} this capture's, "
            + $"and the log {(FileLog.MirrorToConsole ? "still mirrors" : "NO LONGER mirrors")} to it. {text.Length} characters were captured.");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.True(lines.Any(l => l.EndsWith(_opened, StringComparison.Ordinal)),
            "The log capture holds no line at all, not even its own opening line: nothing it says about the log can be trusted.");
        return lines.Where(l => !l.EndsWith(_opened, StringComparison.Ordinal)).ToList();
    }

    public void Dispose()
    {
        FileLog.MirrorToConsole = _previousMirror;
        Console.SetOut(_previousOut);
        _redirect.Dispose();
    }
}
