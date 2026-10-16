using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// Cuts a byte stream into records of newline-delimited JSON.
///
/// A record ends at a line feed (0x0A) and ONLY there; one carriage return directly before it is removed.
/// It works on bytes, never on decoded text, for two reasons. A lone carriage return, U+2028 and U+2029 are
/// all legal inside a JSON string, and <see cref="System.IO.StreamReader.ReadLine"/> splits on a lone
/// carriage return. And a multi-byte UTF-8 character split across two reads must not be decoded half at a
/// time: a line is only decoded once all its bytes are in. 0x0A never occurs inside a multi-byte UTF-8
/// sequence, so splitting the bytes on it is always safe.
///
/// Not thread-safe: one reader loop owns it.
/// </summary>
public sealed class NewlineJsonFramer
{
    /// <summary>The default largest record: far above the few megabytes a long agent run repeats in one record.</summary>
    public const int DefaultMaximumLineBytes = 64 * 1024 * 1024;

    private readonly int _maximumLineBytes;
    private byte[] _pending = new byte[4096];
    private int _pendingLength;

    public NewlineJsonFramer(int maximumLineBytes = DefaultMaximumLineBytes)
    {
        if (maximumLineBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLineBytes), maximumLineBytes, "The largest record must be at least one byte.");
        _maximumLineBytes = maximumLineBytes;
    }

    /// <summary>Bytes received that do not yet end in a line feed.</summary>
    public int PendingByteCount => _pendingLength;

    /// <summary>
    /// Add bytes read from the stream and return every line they complete, in order, without the line ending.
    /// Throws <see cref="InvalidDataException"/> when a line grows past the largest record allowed.
    /// </summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        List<byte[]>? lines = null;
        while (!bytes.IsEmpty)
        {
            var lineFeed = bytes.IndexOf((byte)'\n');
            if (lineFeed < 0)
            {
                AppendPending(bytes);
                break;
            }

            AppendPending(bytes[..lineFeed]);
            lines ??= [];
            lines.Add(TakePendingLine());
            bytes = bytes[(lineFeed + 1)..];
        }

        return lines ?? (IReadOnlyList<byte[]>)[];
    }

    /// <summary>
    /// The stream ended. Returns the bytes after the last line feed - a record cut off mid-line - or null when
    /// the stream ended on a line feed. The framer is empty afterwards.
    /// </summary>
    public byte[]? TakeUnterminated()
    {
        if (_pendingLength == 0)
            return null;
        var tail = _pending.AsSpan(0, _pendingLength).ToArray();
        _pendingLength = 0;
        FileLog.Write($"[NewlineJsonFramer] TakeUnterminated: {tail.Length} bytes after the last line feed");
        return tail;
    }

    private void AppendPending(ReadOnlySpan<byte> bytes)
    {
        var needed = _pendingLength + bytes.Length;
        if (needed > _maximumLineBytes)
            throw new InvalidDataException(
                $"A record is longer than the largest allowed ({_maximumLineBytes} bytes) and has no line feed yet.");
        if (needed > _pending.Length)
            Array.Resize(ref _pending, Math.Min(_maximumLineBytes, Math.Max(needed, _pending.Length * 2)));
        bytes.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength = needed;
    }

    private byte[] TakePendingLine()
    {
        var length = _pendingLength;
        if (length > 0 && _pending[length - 1] == (byte)'\r')
            length--;
        var line = _pending.AsSpan(0, length).ToArray();
        _pendingLength = 0;
        return line;
    }
}
