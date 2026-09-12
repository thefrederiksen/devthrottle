using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Tests.Drivers;

internal sealed class RecordingSessionBackend : ISessionBackend
{
    public int ProcessId => 1234;
    public string Status => "Running";
    public bool IsRunning => true;
    public bool HasExited => false;
    public CircularTerminalBuffer? Buffer { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;

    public List<byte[]> WrittenBytes { get; } = new();
    public List<string> SentTexts { get; } = new();
    public List<string> SubmittedTexts { get; } = new();
    public List<string> Starts { get; } = new();
    public int EnterCount { get; private set; }
    public int LostEnterCount { get; private set; }
    public List<(short Columns, short Rows)> Resizes { get; } = new();
    public int ShutdownCount { get; private set; }
    public bool SimulateBlindSubmitPath { get; init; }
    public TimeSpan BlindSubmitDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public RecordingEchoScript EchoScript { get; } = new();

    /// <summary>
    /// What is parked in the composer RIGHT NOW because an Enter was swallowed - empty once something
    /// finally submits. Live-state, not a tally: a prompt the watchdog nudged through was parked and
    /// then wasn't, and reporting it as still parked would hide the fix working.
    /// </summary>
    public string ParkedComposerText { get; private set; } = string.Empty;

    /// <summary>
    /// Bytes this fake streams into the terminal buffer when a submit actually lands - the agent
    /// echoing the prompt, animating its spinner and streaming a reply. This is what the submit
    /// watchdog reads as proof the turn started, so a fake that never streams looks exactly like a
    /// parked composer (which is the point: a LOST Enter streams nothing, and the watchdog must
    /// catch it). Comfortably above SubmitVerifier.SubmittedGrowthBytes.
    /// </summary>
    public int SubmitResponseBytes { get; init; } = 4096;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        Starts.Add($"{executable}|{args}|{workingDir}|{cols}|{rows}");
        StatusChanged?.Invoke(Status);
    }

    public void Write(byte[] data)
    {
        WrittenBytes.Add(data.ToArray());
        if (IsEnter(data))
        {
            EnterCount++;
            SubmitComposerIfReady();
            return;
        }

        if (IsEscape(data))
        {
            EchoScript.ClearComposer();
            return;
        }

        EchoScript.RecordTypedText(Encoding.UTF8.GetString(data));
        EchoScript.ScheduleEcho(Buffer, data);
    }

    public async Task SendTextAsync(string text)
    {
        SentTexts.Add(text);
        if (!SimulateBlindSubmitPath)
            return;

        Write(Encoding.UTF8.GetBytes(text));
        await Task.Delay(BlindSubmitDelay);
        Write([0x0D]);
    }

    public Task SendEnterAsync()
    {
        EnterCount++;
        SubmitComposerIfReady();
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        Resizes.Add((cols, rows));
    }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        ShutdownCount++;
        ProcessExited?.Invoke(0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    private void SubmitComposerIfReady()
    {
        if (EchoScript.IsComposerReady)
        {
            SubmittedTexts.Add(EchoScript.ComposerText);
            EchoScript.ClearComposer();
            ParkedComposerText = string.Empty; // whatever was parked has now gone through
            // The turn started: the real agent now floods the terminal with its reply. The submit
            // watchdog watches for exactly this and stops nudging once it appears.
            Buffer?.Write(Encoding.UTF8.GetBytes(new string('r', SubmitResponseBytes)));
            return;
        }

        // The TUI swallowed the Enter (pull request #1513): the composer keeps the text and NOTHING is
        // streamed, so the watchdog sees dead window after dead window and nudges.
        LostEnterCount++;
        ParkedComposerText = EchoScript.ComposerText;
    }

    private static bool IsEnter(byte[] data) => data.Length == 1 && data[0] == 0x0D;

    private static bool IsEscape(byte[] data) => data.Length == 1 && data[0] == 0x1B;
}

internal sealed class RecordingEchoScript
{
    private readonly Queue<RecordingEchoStep> _steps = new();
    private RecordingEchoStep _defaultStep = RecordingEchoStep.Immediate();

    public string ComposerText { get; private set; } = string.Empty;
    public bool IsComposerReady { get; private set; }

    public void UseDefault(RecordingEchoStep step)
    {
        _defaultStep = step;
    }

    public void Enqueue(RecordingEchoStep step)
    {
        _steps.Enqueue(step);
    }

    public void RecordTypedText(string text)
    {
        ComposerText += text;
        IsComposerReady = false;
    }

    public void ScheduleEcho(CircularTerminalBuffer? buffer, byte[] typedBytes)
    {
        if (buffer is null)
            return;

        var typedText = Encoding.UTF8.GetString(typedBytes);
        var step = _steps.Count > 0 ? _steps.Dequeue() : _defaultStep;
        _ = EchoAsync(buffer, typedText, step);
    }

    public void ClearComposer()
    {
        ComposerText = string.Empty;
        IsComposerReady = false;
    }

    private async Task EchoAsync(CircularTerminalBuffer buffer, string typedText, RecordingEchoStep step)
    {
        if (step.Mode == RecordingEchoMode.Withheld)
            return;

        if (!string.IsNullOrEmpty(step.PlaceholderText))
            buffer.Write(Encoding.UTF8.GetBytes(step.PlaceholderText));

        if (step.Delay > TimeSpan.Zero)
            await Task.Delay(step.Delay);

        var echoText = step.EchoText ?? typedText;
        buffer.Write(Encoding.UTF8.GetBytes(echoText));
        IsComposerReady = step.AcceptsSubmit;

        // A TUI that swallows an Enter mid-repaint is not broken forever - it comes back. Model that
        // window so a submit can be lost and then nudged through, which is the whole point of the
        // watchdog (a composer that NEVER accepts leaves ReadyAfter null and must end in a throw).
        if (step.ReadyAfter is TimeSpan readyAfter)
        {
            await Task.Delay(readyAfter);
            IsComposerReady = true;
        }
    }
}

internal sealed record RecordingEchoStep(
    RecordingEchoMode Mode,
    TimeSpan Delay,
    string? PlaceholderText,
    string? EchoText,
    bool AcceptsSubmit,
    TimeSpan? ReadyAfter = null)
{
    public static RecordingEchoStep Immediate() =>
        new(RecordingEchoMode.Delayed, TimeSpan.Zero, null, null, true);

    public static RecordingEchoStep Delayed(TimeSpan delay) =>
        new(RecordingEchoMode.Delayed, delay, null, null, true);

    public static RecordingEchoStep Withheld() =>
        new(RecordingEchoMode.Withheld, TimeSpan.Zero, null, null, false);

    public static RecordingEchoStep RepaintingPlaceholder(string placeholderText, TimeSpan delay) =>
        new(RecordingEchoMode.RepaintingPlaceholder, delay, placeholderText, null, true);

    public static RecordingEchoStep CustomEcho(string echoText) =>
        new(RecordingEchoMode.Delayed, TimeSpan.Zero, null, echoText, true);

    public static RecordingEchoStep SlashCorrupted(string text) =>
        new(RecordingEchoMode.SlashCorrupted, TimeSpan.Zero, null, "/" + text, false);

    /// <summary>
    /// The text echoes (so it is provably sitting in the composer) but the TUI SWALLOWS every Enter -
    /// the composer never reaches a submitting state. The unrecoverable form of the live failure in
    /// pull request #1513. Distinct from <see cref="Withheld"/>, where the text never arrives at all.
    /// </summary>
    public RecordingEchoStep NotAcceptingSubmit() => this with { AcceptsSubmit = false, ReadyAfter = null };

    /// <summary>
    /// The recoverable form of pull request #1513, and the common one: the text echoes, the TUI swallows the
    /// submitting Enter (an autocomplete popup, a startup window that drops Enter, a composer
    /// repaint), and then <paramref name="readyAfter"/> later it is accepting again - so the
    /// watchdog's nudge lands and the turn starts.
    /// </summary>
    public RecordingEchoStep SwallowsEnterUntilReady(TimeSpan readyAfter) =>
        this with { AcceptsSubmit = false, ReadyAfter = readyAfter };
}

internal enum RecordingEchoMode
{
    Delayed,
    Withheld,
    RepaintingPlaceholder,
    SlashCorrupted,
}
