namespace CcDirector.Core.Drivers;

/// <summary>
/// WHAT a driver asks to have typed - never the typing itself (issue #4380). A driver knows its tool's protocol: that
/// Claude Code clears with "/clear", cancels a turn with Escape, and opens its history with two Escapes 350ms apart. It
/// says so with one of these, and the HOST types it. In the Director the host is the Session alone, which types a command
/// through the same send path every prompt takes - the one-send-at-a-time gate, the look before typing and the proof
/// after the Enter - so a command can no longer reach an open menu by a route that skipped those checks. That is how the
/// 15 October failure (a send typed into Claude Code's question menu) could still be reached, through /clear and
/// /compact, until this type existed.
/// </summary>
public sealed class DriverInput
{
    private DriverInput(string? command, IReadOnlyList<DriverKey> keys)
    {
        Command = command;
        Keys = keys;
    }

    /// <summary>A line typed into the composer and submitted, exactly as a prompt is - "/clear", "/compact", "/new".
    /// Null for keys.</summary>
    public string? Command { get; }

    /// <summary>Keystrokes written as they are, each after its own pause. Empty for a command.</summary>
    public IReadOnlyList<DriverKey> Keys { get; }

    /// <summary>A command the host types into the composer and submits through its send path.</summary>
    public static DriverInput TypeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("A driver command must have text.", nameof(command));
        return new DriverInput(command, Array.Empty<DriverKey>());
    }

    /// <summary>Keystrokes the host writes in order, each after its <see cref="DriverKey.PauseBefore"/>.</summary>
    public static DriverInput PressKeys(params DriverKey[] keys)
    {
        if (keys is null || keys.Length == 0)
            throw new ArgumentException("A driver key sequence must have at least one key.", nameof(keys));
        return new DriverInput(null, keys);
    }

    /// <summary>For the log: the command, or the keys by name with their pauses.</summary>
    public override string ToString() => Command is not null
        ? $"the command '{Command}'"
        : "the keys " + string.Join(", ", Keys.Select(k => k.PauseBefore > TimeSpan.Zero
            ? $"{k.Name} after {k.PauseBefore.TotalMilliseconds:F0}ms"
            : k.Name));
}

/// <summary>One keystroke a driver asks for: its bytes, a name for the log, and how long to wait before it.</summary>
public sealed record DriverKey(string Name, byte[] Bytes, TimeSpan PauseBefore)
{
    /// <summary>Escape (0x1B).</summary>
    public static DriverKey Escape => new("Esc", [0x1B], TimeSpan.Zero);

    /// <summary>Ctrl+C (0x03).</summary>
    public static DriverKey CtrlC => new("Ctrl+C", [0x03], TimeSpan.Zero);

    /// <summary>The same key, pressed only after <paramref name="pause"/> - for a double press the tool must not
    /// coalesce.</summary>
    public DriverKey After(TimeSpan pause) => this with { PauseBefore = pause };
}
