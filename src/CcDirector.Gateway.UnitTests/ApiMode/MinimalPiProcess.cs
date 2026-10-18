using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CcDirector.Core.ApiMode;

namespace CcDirector.Gateway.UnitTests.ApiMode;

/// <summary>
/// The least of Pi a Gateway-side test needs: a process that answers <c>get_state</c> the way Pi 1.2.0 does (so the
/// session's conversation is named), exits when its input closes, and exits 137 when killed. Nothing is spawned and nothing
/// waits on a clock.
/// </summary>
internal sealed class MinimalPiProcess : IApiModeProcess
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _current = [];
    private int _offset;

    public MinimalPiProcess(string conversationId) => ConversationId = conversationId;

    public string ConversationId { get; }
    public int ProcessId => 4343;
    public bool HasExited => _exit.Task.IsCompleted;

    public void Start(ApiModeProcessStartInfo startInfo)
    {
    }

    public Task WriteLineAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        if (HasExited)
            throw new IOException("The pipe is being closed.");
        var command = JsonDocument.Parse(line[..^1]).RootElement;
        if (command.GetProperty("type").GetString() == "get_state")
        {
            var id = command.GetProperty("id").GetString();
            var answer = $$$"""{"id":"{{{id}}}","type":"response","command":"get_state","success":true,"data":{"sessionId":"{{{ConversationId}}}","model":{"id":"test-model"},"messageCount":0}}""";
            _output.Writer.TryWrite(Encoding.UTF8.GetBytes(answer + "\n"));
        }
        return Task.CompletedTask;
    }

    public async ValueTask<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_offset >= _current.Length)
        {
            if (!await _output.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            if (_output.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }
        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public async ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken)
    {
        await _exit.Task.WaitAsync(cancellationToken);
        return null;
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void CloseInput() => Exit(0);

    public void Kill() => Exit(137);

    public void Dispose()
    {
    }

    private void Exit(int code)
    {
        _output.Writer.TryComplete();
        _exit.TrySetResult(code);
    }
}
