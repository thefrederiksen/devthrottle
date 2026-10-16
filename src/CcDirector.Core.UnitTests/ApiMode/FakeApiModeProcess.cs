using System.Text;
using System.Threading.Channels;
using CcDirector.Core.ApiMode;

namespace CcDirector.Core.Tests.ApiMode;

/// <summary>
/// A scripted stand-in for the agent process: the test pushes standard output bytes and standard error lines,
/// and decides when it exits. Nothing is spawned and nothing waits on a clock.
/// </summary>
internal sealed class FakeApiModeProcess : IApiModeProcess
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<string> _error = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeGate = new();
    private readonly List<byte> _inputBytes = [];
    private byte[] _current = [];
    private int _currentOffset;
    private bool _inputClosed;

    /// <summary>When set, <see cref="Start"/> throws it, as a process that cannot be started does.</summary>
    public ApiModeStartException? StartFailure { get; init; }

    /// <summary>Whether closing standard input makes the process exit (with code 0), as Pi does.</summary>
    public bool ExitsWhenInputCloses { get; init; } = true;

    /// <summary>Whether exiting also ends standard output and error. False imitates a grandchild holding the pipes open.</summary>
    public bool ExitEndsStreams { get; init; } = true;

    /// <summary>Called with each line written to standard input, without its line feed, so a test can answer it.</summary>
    public Action<string>? LineWritten { get; set; }

    /// <summary>Writes each line in two halves with a yield between, so unserialised writers would interleave.</summary>
    public bool WriteInHalves { get; init; }

    /// <summary>Every write blocks until the process exits, then fails: an agent that stopped reading its full input pipe.</summary>
    public bool BlockWrites { get; init; }

    /// <summary>The write exits the process and fails with a broken pipe, as a write racing the agent's exit does.</summary>
    public bool ExitDuringWrite { get; init; }

    /// <summary>The exit code a kill reports: 137 as on Linux, or -1 as on Windows.</summary>
    public int KillExitCode { get; init; } = 137;

    /// <summary>When set, reading standard error throws it.</summary>
    public Exception? ErrorReadFailure { get; init; }

    /// <summary>When set, a cancelled output read throws this instead of a cancellation, as a read on a closed Windows handle does.</summary>
    public Exception? CancelledReadFailure { get; init; }

    public ApiModeProcessStartInfo? StartedWith { get; private set; }
    public int KillCount { get; private set; }
    public bool InputClosed => _inputClosed;
    public int ProcessId => StartedWith is null ? 0 : 4242;
    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>Every byte written to standard input, decoded and split on line feed.</summary>
    public IReadOnlyList<string> WrittenLines
    {
        get
        {
            lock (_writeGate)
                return Encoding.UTF8.GetString(_inputBytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    public void Start(ApiModeProcessStartInfo startInfo)
    {
        if (StartFailure is not null)
            throw StartFailure;
        StartedWith = startInfo;
    }

    public async Task WriteLineAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        if (_inputClosed || HasExited)
            throw new IOException("The pipe is being closed.");
        if (ExitDuringWrite)
        {
            Exit(0);
            throw new IOException("Broken pipe");
        }
        if (BlockWrites)
        {
            await _exit.Task;
            throw new IOException("The pipe is being closed.");
        }

        if (WriteInHalves)
        {
            var half = line.Length / 2;
            lock (_writeGate)
                _inputBytes.AddRange(line[..half].ToArray());
            await Task.Yield();
            lock (_writeGate)
                _inputBytes.AddRange(line[half..].ToArray());
        }
        else
        {
            lock (_writeGate)
                _inputBytes.AddRange(line.ToArray());
        }

        LineWritten?.Invoke(Encoding.UTF8.GetString(line.Span[..^1]));
    }

    public async ValueTask<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_currentOffset >= _current.Length)
        {
            if (CancelledReadFailure is not null && cancellationToken.IsCancellationRequested)
                throw CancelledReadFailure;
            bool more;
            try
            {
                more = await _output.Reader.WaitToReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (CancelledReadFailure is not null)
            {
                throw CancelledReadFailure;
            }
            if (!more)
                return 0;
            if (_output.Reader.TryRead(out var next))
            {
                _current = next;
                _currentOffset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        return count;
    }

    public async ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken)
    {
        if (ErrorReadFailure is not null)
            throw ErrorReadFailure;
        if (!await _error.Reader.WaitToReadAsync(cancellationToken))
            return null;
        return _error.Reader.TryRead(out var line) ? line : await ReadErrorLineAsync(cancellationToken);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    /// <summary>Completes when standard input is closed, so a test can act between the close and the exit.</summary>
    public Task InputClosedTask => _inputClosedSignal.Task;

    private readonly TaskCompletionSource _inputClosedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void CloseInput()
    {
        _inputClosed = true;
        _inputClosedSignal.TrySetResult();
        if (ExitsWhenInputCloses)
            Exit(0);
    }

    public void Kill()
    {
        KillCount++;
        Exit(KillExitCode);
    }

    public void Dispose()
    {
    }

    /// <summary>Push bytes to standard output, each array as one read's worth (or less, if the reader's buffer is smaller).</summary>
    public void EmitOutput(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
            _output.Writer.TryWrite(chunk);
    }

    /// <summary>Push text to standard output as UTF-8, in one read.</summary>
    public void EmitOutput(string text) => EmitOutput(Encoding.UTF8.GetBytes(text));

    public void EmitError(string line) => _error.Writer.TryWrite(line);

    /// <summary>The process exits with <paramref name="code"/>. Its output ends first, unless <see cref="ExitEndsStreams"/> is false.</summary>
    public void Exit(int code)
    {
        if (ExitEndsStreams)
        {
            _output.Writer.TryComplete();
            _error.Writer.TryComplete();
        }
        _exit.TrySetResult(code);
    }
}
