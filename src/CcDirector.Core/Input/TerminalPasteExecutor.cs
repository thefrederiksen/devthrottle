using CcDirector.Core.Backends;

namespace CcDirector.Core.Input;

/// <summary>Builds and paces one paste identically for every terminal input surface.</summary>
public static class TerminalPasteExecutor
{
    public static async Task PasteAsync(string? text, bool bracketed, ISessionBackend backend, Action<byte[]> write, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.TrimEnd('\r', '\n');
        if (text.Length == 0) return;
        var writes = TerminalInputEncoder.Paste(text, bracketed, TerminalInputEncoder.SendsNonAsciiAsKeyEvents(backend),
            bracketed ? TerminalInputEncoder.PasteChunkBytes : 8).ToList();
        var pause = bracketed ? TerminalInputEncoder.PasteChunkPause : TimeSpan.FromMilliseconds(15);
        foreach (var chunk in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            write(chunk);
            if (!ReferenceEquals(chunk, writes[^1])) await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }
    }
}
