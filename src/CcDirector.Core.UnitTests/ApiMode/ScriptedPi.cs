using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Tests.ApiMode;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// A Pi that answers from a real recording (TestData/pi-rpc/&lt;scenario&gt;.transcript.jsonl, Pi 1.2.0): each command the
/// host writes is answered with the records Pi wrote after the same command in the recording, up to the next command -
/// standard output, standard error and the exit - pushed through a <see cref="FakeApiModeProcess"/>. No Pi, no process,
/// no clock.
///
/// The host writes its own command ids (<c>dt-1</c>, ...); the recording's id on the matching response is replaced with
/// the host's, by text, so every other byte of Pi's line is what Pi wrote. A command is matched to the first recorded
/// command of the same type not yet used, so the host may write <c>get_state</c> before its prompt although the
/// recorder wrote it after. A recording with no <c>get_state</c> answers it with the <c>plain</c> recording's response,
/// with that recording's conversation id replaced by this one's. A command the recording never wrote gets no answer and
/// is listed in <see cref="Unexpected"/>.
///
/// With <see cref="AutoRespond"/> false, the answers wait until the test calls <see cref="RespondToNext"/>, so a test
/// decides exactly when Pi answers.
/// </summary>
internal sealed class ScriptedPi
{
    /// <summary>
    /// How long a test waits for something the code under test must do before the wait fails, naming what never
    /// happened. A hang guard only: every wait it bounds completes in milliseconds on correct code, so it never decides
    /// a pass, and without it a regression would stall the whole gate instead of failing one test.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>Wait for <paramref name="task"/>, or fail naming <paramref name="what"/> after <see cref="HangGuard"/>.</summary>
    public static async Task<T> WithinHangGuard<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Waited {HangGuard.TotalSeconds} seconds for {what}; it never happened, so the code under test hangs here.");
        }
    }

    /// <summary>Wait for <paramref name="task"/>, or fail naming <paramref name="what"/> after <see cref="HangGuard"/>.</summary>
    public static async Task WithinHangGuard(Task task, string what) =>
        await WithinHangGuard(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), what);

    private const string PlainConversationId = "311ffc3b-7a39-45cf-bcb5-596c494cdbbf";

    private readonly object _gate = new();
    private readonly List<Block> _blocks;
    private readonly List<Block> _promptBlocks;
    private readonly HashSet<Block> _used = [];
    private readonly List<(Block Block, string Id)> _pending = [];
    private readonly List<string> _written = [];
    private readonly List<string> _unexpected = [];
    private readonly List<(Func<string, bool> Match, TaskCompletionSource<string> Done)> _writeWaiters = [];
    private int _promptsWritten;

    public ScriptedPi(string scenario, bool exitsWhenInputCloses = true)
    {
        var lines = File.ReadAllLines(PiWireLogReplay.FixturePath(scenario));
        var argv = JsonDocument.Parse(lines[0]).RootElement.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList();
        AgentSessionId = argv[argv.IndexOf("--session-id") + 1];
        _blocks = ReadBlocks(lines.Skip(1));
        _promptBlocks = _blocks.Where(b => b.Type == "prompt").ToList();
        if (_blocks.All(b => b.Type != "get_state"))
        {
            var plain = ReadBlocks(File.ReadAllLines(PiWireLogReplay.FixturePath("plain")).Skip(1)).Single(b => b.Type == "get_state");
            _blocks.Add(plain with { Items = plain.Items.Select(i => i with { Text = i.Text?.Replace(PlainConversationId, AgentSessionId) }).ToList() });
        }
        Process = new FakeApiModeProcess { ExitsWhenInputCloses = exitsWhenInputCloses };
        Process.LineWritten = OnLineWritten;
    }

    public FakeApiModeProcess Process { get; }

    /// <summary>The conversation id the recording's Pi was started with.</summary>
    public string AgentSessionId { get; }

    /// <summary>Answer each command as it is written (the default), or only on <see cref="RespondToNext"/>.</summary>
    public bool AutoRespond { get; init; } = true;

    /// <summary>For the n-th prompt the host writes, the number (from 1) of the recorded prompt whose answer it gets.
    /// Null: in order.</summary>
    public IReadOnlyList<int>? PromptAnswers { get; init; }

    /// <summary>Answer every prompt with the first recorded prompt's answer.</summary>
    public bool RepeatFirstPromptAnswer { get; init; }

    /// <summary>Answer a prompt with Pi's acknowledgement only, and nothing after it: a Pi that acknowledged and never
    /// began the turn (review finding 1 of pull request #4076).</summary>
    public bool AcknowledgeOnly { get; init; }

    /// <summary>The exit code Pi reports when the recording has it exit, instead of the recorded one (a Pi that ended with
    /// code 0 where the recording's was killed).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Changes a standard output line before it is pushed (a test that needs Pi to say something else).</summary>
    public Func<string, string>? EditStdout { get; init; }

    /// <summary>Every line the host wrote, in order.</summary>
    public IReadOnlyList<string> Written
    {
        get { lock (_gate) return _written.ToList(); }
    }

    /// <summary>The type of every command the host wrote, in order.</summary>
    public IReadOnlyList<string> WrittenTypes => Written.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("type").GetString()!).ToList();

    /// <summary>Commands the recording never wrote, so nothing answered them.</summary>
    public IReadOnlyList<string> Unexpected
    {
        get { lock (_gate) return _unexpected.ToList(); }
    }

    /// <summary>Completes when the host has written a line of the given command type (at once if it already has).</summary>
    public Task<string> WaitForWrittenAsync(string type, int occurrence = 1)
    {
        var seen = 0;
        bool Match(string line)
        {
            if (JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() != type)
                return false;
            return ++seen == occurrence;
        }

        lock (_gate)
        {
            foreach (var line in _written)
            {
                if (Match(line))
                    return Task.FromResult(line);
            }
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _writeWaiters.Add((Match, done));
            return WithinHangGuard(done.Task, $"the host to write {type} (occurrence {occurrence})");
        }
    }

    /// <summary>Push the answer to the oldest command still waiting for one.</summary>
    public void RespondToNext()
    {
        (Block Block, string Id) next;
        lock (_gate)
        {
            if (_pending.Count == 0)
                throw new InvalidOperationException("No written command is waiting for its answer.");
            next = _pending[0];
            _pending.RemoveAt(0);
        }
        Emit(next.Block, next.Id);
    }

    private void OnLineWritten(string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        var type = command.GetProperty("type").GetString()!;
        Block? block;
        lock (_gate)
        {
            _written.Add(line);
            foreach (var waiter in _writeWaiters.ToList())
            {
                if (waiter.Match(line))
                {
                    _writeWaiters.Remove(waiter);
                    waiter.Done.TrySetResult(line);
                }
            }
            if (type == "extension_ui_response")
                return;
            block = Find(type);
            if (block is null)
            {
                _unexpected.Add(line);
                return;
            }
            if (!AutoRespond)
            {
                _pending.Add((block, command.GetProperty("id").GetString()!));
                return;
            }
        }
        Emit(block, command.GetProperty("id").GetString()!);
    }

    private Block? Find(string type)
    {
        if (type == "prompt")
        {
            var number = ++_promptsWritten;
            var recorded = RepeatFirstPromptAnswer ? 1 : PromptAnswers is { } answers ? answers[number - 1] : number;
            return recorded <= _promptBlocks.Count ? _promptBlocks[recorded - 1] : null;
        }
        var block = _blocks.FirstOrDefault(b => b.Type == type && !_used.Contains(b));
        if (block is not null)
            _used.Add(block);
        return block;
    }

    private void Emit(Block block, string id)
    {
        foreach (var item in block.Items)
        {
            switch (item.Direction)
            {
                case "stdout":
                    var line = item.Text!;
                    if (block.RecordedId is { } recordedId)
                        line = line.Replace($"\"id\":\"{recordedId}\"", $"\"id\":\"{id}\"");
                    if (EditStdout is not null)
                        line = EditStdout(line);
                    Process.EmitOutput(line + "\n");
                    if (AcknowledgeOnly && block.Type == "prompt" && line.Contains("\"type\":\"response\"", StringComparison.Ordinal))
                        return;
                    break;
                case "stderr":
                    Process.EmitError(item.Text!);
                    break;
                case "exit":
                    // The recorder is Python, which reports a kill as minus the signal; .NET on Linux reports 128 plus it.
                    Process.Exit(ExitCode ?? (item.Code == -9 ? 137 : item.Code));
                    break;
            }
        }
    }

    private static List<Block> ReadBlocks(IEnumerable<string> records)
    {
        var blocks = new List<Block>();
        Block? current = null;
        foreach (var raw in records)
        {
            var record = JsonDocument.Parse(raw).RootElement;
            var direction = record.GetProperty("dir").GetString()!;
            if (direction == "stdin")
            {
                var line = record.GetProperty("line").GetString()!;
                string type;
                string? id;
                try
                {
                    var command = JsonDocument.Parse(line).RootElement;
                    type = command.GetProperty("type").GetString()!;
                    id = command.GetProperty("id").GetString();
                }
                catch (JsonException)
                {
                    // The refusal recording's deliberate malformed line: no host writes it.
                    type = "(not json)";
                    id = null;
                }
                current = new Block(type, id, []);
                blocks.Add(current);
                continue;
            }
            if (current is null || direction == "kill")
                continue;
            current.Items.Add(new Item(
                direction,
                record.TryGetProperty("line", out var text) ? text.GetString() : null,
                record.TryGetProperty("code", out var code) ? code.GetInt32() : 0));
        }
        return blocks;
    }

    private sealed record Block(string Type, string? RecordedId, List<Item> Items);

    private sealed record Item(string Direction, string? Text, int Code);
}

/// <summary>Records every event the host publishes, and lets a test wait for one without a clock.</summary>
internal sealed class RecordingSink : ISessionEventSink
{
    private readonly object _gate = new();
    private readonly List<SessionEventBatch> _batches = [];
    private readonly List<(Func<SessionEvent, bool> Match, TaskCompletionSource<SessionEvent> Done)> _waiters = [];

    public IReadOnlyList<SessionEventBatch> Batches
    {
        get { lock (_gate) return _batches.ToList(); }
    }

    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_gate) return _batches.SelectMany(b => b.Events).ToList(); }
    }

    /// <summary>
    /// The events as one line each (<see cref="PiWireLogReplay.Describe"/>), for asserting an exact sequence. A prompt id
    /// the Director minted is a new GUID, so each is written <c>minted-1</c>, <c>minted-2</c>, ... in the order it first
    /// appears (<see cref="Minted"/>); every other id is written as it is.
    /// </summary>
    public List<string> Lines()
    {
        var minted = MintedIds();
        return Events.Select(e => minted.Select((id, i) => (id, i)).Aggregate(PiWireLogReplay.Describe(e),
            (line, m) => line.Replace(m.id, $"minted-{m.i + 1}"))).ToList();
    }

    /// <summary>The <paramref name="n"/>-th prompt id the Director minted (counting from 1), in the order the events
    /// first named it. Throws when there is no such id.</summary>
    public string Minted(int n) => MintedIds()[n - 1];

    /// <summary>Every minted prompt id (a GUID) the events named, in order of first appearance.</summary>
    public List<string> MintedIds()
    {
        var ids = new List<string>();
        foreach (var e in Events)
        {
            IEnumerable<string> named = e switch
            {
                PromptQueued q => [q.PromptId],
                PromptDelivered d => [d.PromptId],
                PromptRefused r => [r.PromptId],
                TurnStarted t => t.PromptIds,
                _ => [],
            };
            foreach (var id in named)
                if (Guid.TryParse(id, out _) && !ids.Contains(id))
                    ids.Add(id);
        }
        return ids;
    }

    public void Publish(SessionEventBatch batch)
    {
        lock (_gate)
        {
            _batches.Add(batch);
            foreach (var e in batch.Events)
            {
                foreach (var waiter in _waiters.ToList())
                {
                    if (waiter.Match(e))
                    {
                        _waiters.Remove(waiter);
                        waiter.Done.TrySetResult(e);
                    }
                }
            }
        }
    }

    /// <summary>Completes with the first event of type <typeparamref name="T"/> that matches (at once if one was
    /// already published).</summary>
    public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null) where T : SessionEvent
    {
        bool Matches(SessionEvent e) => e is T t && (match is null || match(t));
        Task<SessionEvent> done;
        lock (_gate)
        {
            var already = _batches.SelectMany(b => b.Events).FirstOrDefault(Matches);
            if (already is not null)
                return (T)already;
            var waiter = new TaskCompletionSource<SessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((Matches, waiter));
            done = waiter.Task;
        }
        return (T)await ScriptedPi.WithinHangGuard(done, $"a {typeof(T).Name} event");
    }
}
