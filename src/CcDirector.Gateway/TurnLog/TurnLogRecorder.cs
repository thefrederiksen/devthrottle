using System.Diagnostics;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// The turn log: at the end of every turn on a machine where capture is switched on, write one
/// self-contained record of what just happened.
///
/// WHY IT IS NOT PART OF THE SUPERVISOR, WHICH ALREADY READS THE SCREEN AT THIS EXACT MOMENT. The single
/// most valuable thing this instrument can capture is a turn end the supervisor did NOT act on - the misses,
/// which today write no line anywhere and are the reason our measurement said the supervisor works while the
/// owner's experience said it works only sometimes. A log living inside the supervisor writes nothing
/// precisely when the supervisor does nothing, which reproduces the blindness it exists to cure. So it hangs
/// off the same turn-end boundary independently, and it costs a second screen read to stay honest.
///
/// IT MUST NOT CHANGE WHAT THE PRODUCT DOES. <see cref="OnTurnEnd"/> returns immediately and the whole
/// capture runs on its own task, so a turn ends identically whether capture is on or off. Nothing here can
/// gate, delay or veto anything: it holds no lock the product waits on, it types nothing, and it never
/// throws into the caller.
///
/// A FAILURE TO LOG IS NOT A FAILURE OF THE TURN - BUT IT IS RECORDED. A part that could not be collected
/// is named in the record's gaps rather than quietly left out, because a corpus that silently drops what it
/// could not read acquires holes exactly where the interesting cases are, and nobody can tell afterwards
/// whether a missing screen was a quiet session or a broken instrument.
/// </summary>
public sealed class TurnLogRecorder : IDisposable
{
    /// <summary>
    /// How many FULL turns of conversation go into a record - a full turn being the user's message and the
    /// agent's reply together. Ten, on the owner's instruction, because a screen alone often cannot say
    /// whether a session is stuck, waiting or finished and what came before it can. Erring long is
    /// deliberate: an over-long conversation costs bytes, and a short one costs a question we cannot ask.
    /// </summary>
    public const int FullTurnsCaptured = 10;

    /// <summary>How many scrollback lines are asked for. Well past what any judgement reads, for the same
    /// reason: the window is a decision we will want to re-take.</summary>
    public const int ScrollbackLinesCaptured = 2000;

    /// <summary>
    /// The ceiling on ONE record's conversation, in bytes of serialized JSON.
    ///
    /// Ten full turns is the rule, and it stays the rule - but a "turn" in an agent session is a person's
    /// prompt plus every tool call and tool result that followed it, and those run to hundreds of messages.
    /// Measured on the first live records: a median conversation of 271 KB inside a 616 KB record, which at
    /// four hundred turn ends a day is tens of gigabytes a year in a repository. The corpus is only useful
    /// if we can actually keep it.
    ///
    /// OLDEST TURNS GO FIRST, AND THEY GO WHOLE. Trimming from the front keeps the turns nearest the screen
    /// - the ones a judgement about THIS turn end would actually read - and dropping whole turns rather
    /// than messages preserves the property that made the ten-turn rule worth having: no agent reply
    /// arrives without the prompt that caused it. A record that hits the ceiling says so, and says how many
    /// turns it lost.
    /// </summary>
    public const int MaxConversationBytes = 128 * 1024;

    /// <summary>
    /// The ceiling on one capture. Not a correctness bound - nothing downstream waits on this - but an
    /// unreachable Director must not leave a capture task hanging on to a session's state for the rest of
    /// the day, and a fleet's worth of those would be a leak rather than a log.
    /// </summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many captures may be in flight at once, across the whole fleet.
    ///
    /// Every turn end used to start an unbounded task. On a busy fleet - or a wildcard switch covering many
    /// accounts - that is an unbounded number of concurrent tunnel reads, each holding a screen, two
    /// thousand scrollback lines and a conversation in memory before it compresses them. The log is not
    /// allowed to be the reason the Gateway struggles, and "it observes, it does not interfere" has to hold
    /// under load as well as at rest.
    ///
    /// Past this, a capture is DROPPED rather than queued. Queueing would trade a memory problem for a
    /// latency problem and still hold the records; dropping loses that turn and says so. A corpus that is
    /// missing a turn it names is honest; a Gateway that fell over is not.
    /// </summary>
    public const int MaxConcurrentCaptures = 8;

    /// <summary>
    /// The ceiling on ONE finished record, in bytes of serialized JSON, whatever it is made of.
    ///
    /// The conversation already has its own ceiling. This is the backstop for everything else - a terminal
    /// that emitted an enormous line, a session snapshot that grew, a scrollback of very long rows. Terminal
    /// content is UNTRUSTED input: it is whatever a program somebody ran decided to print, and a record's
    /// size should not be settable by that program.
    ///
    /// When a record is over, the SCROLLBACK is trimmed first and the trim is recorded. The live screen is
    /// never trimmed - it is the thing every judgement reads and the reason the record exists at all.
    /// </summary>
    public const int MaxRecordBytes = 1024 * 1024;

    private readonly ITurnLogEnvironment _env;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _inFlight = new(MaxConcurrentCaptures, MaxConcurrentCaptures);
    private long _dropped;
    private bool _disposed;

    /// <summary>How many captures have been dropped because too many were already in flight. Exposed so
    /// the number is visible rather than inferred from a corpus that is quietly short.</summary>
    public long DroppedForConcurrency => Interlocked.Read(ref _dropped);

    /// <param name="enterTenantScope">Enters the owning account's storage scope for the duration of a
    /// capture. Required on the hosted Gateway and inert on a self-hosted one, which has a single partition.
    /// Without it every read the capture makes runs with no tenant in scope and is DENIED, and because a
    /// denied read is written down as a gap rather than thrown, the corpus would fill with records whose
    /// screen and conversation were always missing - a feature that looks switched on and captures nothing.</param>
    public TurnLogRecorder(
        ITurnLogEnvironment env,
        Func<TenantId, IDisposable>? enterTenantScope = null,
        Func<DateTime>? nowUtc = null)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// A turn just ended. Returns immediately, having started the capture on its own task - the product's
    /// turn-end path continues with nothing waiting on this.
    ///
    /// The switch is read here, synchronously and cheaply, so a Gateway with capture off does not even spawn
    /// a task per turn end.
    /// </summary>
    public void OnTurnEnd(TurnEndSignal signal)
    {
        if (_disposed || signal is null) return;
        if (!signal.Tenant.IsValid || string.IsNullOrEmpty(signal.SessionId)) return;

        // The machine is the Director's identity; the account is the tenant. Both are needed before the
        // switch can be asked, and neither costs a round trip.
        if (!_env.IsEnabled(signal.Tenant.Value, signal.DirectorId)) return;

        // BACKPRESSURE BEFORE THE TASK, not inside it. Taking the slot here means a saturated Gateway does
        // not even allocate the work, and the drop is counted where it can be seen.
        if (!_inFlight.Wait(0))
        {
            var n = Interlocked.Increment(ref _dropped);
            FileLog.Write($"[TurnLogRecorder] capture DROPPED (already {MaxConcurrentCaptures} in flight; {n} dropped so far) sid={TurnLogSwitchStore.Clean(signal.SessionId)}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(CaptureTimeout);
                // The scope is entered HERE, inside the task, and not by the caller. It is an async-local,
                // so a scope the turn-end callback entered would not survive into this continuation - the
                // same reason the supervisor enters its own.
                using (_enterTenantScope?.Invoke(signal.Tenant))
                    await CaptureAsync(signal, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[TurnLogRecorder] capture timed out sid={TurnLogSwitchStore.Clean(signal.SessionId)} - no record written");
            }
            catch (Exception ex)
            {
                // Loud, and it goes no further. The turn is long over.
                FileLog.Write($"[TurnLogRecorder] capture FAILED sid={TurnLogSwitchStore.Clean(signal.SessionId)}: {ex.Message}");
            }
            finally
            {
                try { _inFlight.Release(); } catch (ObjectDisposedException) { /* stopping */ }
            }
        });
    }

    /// <summary>
    /// Gather one record and write it. The test entry point; production reaches it through
    /// <see cref="OnTurnEnd"/>. Answers the path written, or null.
    /// </summary>
    public async Task<string?> CaptureAsync(TurnEndSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var startedAt = _nowUtc();
        var overall = Stopwatch.StartNew();
        var gaps = new List<TurnLogGap>();

        var session = _env.LocateSession(signal.Tenant, signal.SessionId);

        // FILL IN THE MACHINE NAME THE RAW PUSH LEAVES EMPTY. A Director sends its sessions without one and
        // the Gateway fills it from the Director's registration when it serves the session list; a capture
        // reads the pushed snapshot, which is one layer earlier, so it sees the blank. Left alone, every
        // record on the fleet says its computer is "unknown" and the bundles land in a directory of that
        // name - a corpus that cannot say which machine a turn happened on, while the Gateway knew all
        // along. The snapshot is the caller's to modify by contract, so nothing else is disturbed.
        if (session is not null && string.IsNullOrWhiteSpace(session.MachineName))
        {
            try
            {
                var resolved = _env.ResolveMachineName(signal.Tenant, signal.DirectorId);
                if (!string.IsNullOrWhiteSpace(resolved)) session.MachineName = resolved!;
            }
            catch (Exception ex)
            {
                gaps.Add(new TurnLogGap { Part = "machine-name", Reason = ex.Message });
            }
        }

        if (session is null)
        {
            // We keep going deliberately. A session that has already gone - deleted, or its Director dropped
            // between the boundary and this task - still produced a turn end, and a record saying so with a
            // named gap is worth more than no record at all, which would look like a turn that never happened.
            gaps.Add(new TurnLogGap
            {
                Part = "session",
                Reason = "the session was not in the Gateway's snapshot at capture time",
            });
        }

        var screenTimer = Stopwatch.StartNew();
        ScreenGridResponse? grid = null;
        try
        {
            grid = await _env.ReadScreenAsync(signal.Tenant, signal.DirectorId, signal.SessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "terminal", Reason = ex.Message });
        }
        screenTimer.Stop();
        if (grid is null)
            gaps.Add(new TurnLogGap { Part = "terminal", Reason = "the live screen could not be read - unreadable, not empty" });

        var scrollbackTimer = Stopwatch.StartNew();
        BufferResponse? scrollback = null;
        try
        {
            scrollback = await _env.ReadScrollbackAsync(
                signal.Tenant, signal.DirectorId, signal.SessionId, ScrollbackLinesCaptured, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "scrollback", Reason = ex.Message });
        }
        scrollbackTimer.Stop();
        if (scrollback is null)
            gaps.Add(new TurnLogGap { Part = "scrollback", Reason = "the scrollback could not be read" });

        StoredConversationSnapshot? stored = null;
        try
        {
            stored = _env.ReadConversation(signal.Tenant, signal.SessionId);
        }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "conversation", Reason = ex.Message });
        }
        if (stored is null)
            gaps.Add(new TurnLogGap { Part = "conversation", Reason = "nothing has been stored for this session yet" });

        var conversation = BuildConversation(stored);

        // A CONVERSATION OLDER THAN THE TURN IS A GAP - but the comparison has to be against the TURN, not
        // against this capture, and getting that backwards was the first thing the live corpus caught.
        //
        // The Director announces the state change before it pushes the turn that caused it, so the risk is a
        // conversation that stops one turn short of the screen stored beside it. The evidence for that is a
        // last push older than the session's last activity - the turn ending. Comparing against the capture
        // instead flags the HEALTHY case, because a push that landed before we read is exactly what we want,
        // and it fired on every record in the first minutes of real capture. A gap that marks good records
        // as suspect is worse than no gap: it teaches everyone reading the corpus to ignore the field.
        if (stored is not null && stored.LastPushedUtc is { } pushed && session?.LastActivityAt is { } lastActivity)
        {
            var turnEnded = DateTime.SpecifyKind(lastActivity, DateTimeKind.Utc);
            if (pushed < turnEnded)
            {
                gaps.Add(new TurnLogGap
                {
                    Part = "conversation",
                    Reason = $"the conversation was last pushed at {pushed:O}, before this session's last activity at "
                           + $"{turnEnded:O} - it may stop one turn short of the screen stored beside it",
                });
            }
        }
        if (conversation.TurnsDroppedForSize > 0)
        {
            gaps.Add(new TurnLogGap
            {
                Part = "conversation",
                Reason = $"{conversation.TurnsDroppedForSize} older turn(s) were dropped to stay under the "
                       + $"{MaxConversationBytes / 1024} KB conversation ceiling - the turns nearest this screen were kept",
            });
        }
        overall.Stop();

        var record = new TurnLogRecord
        {
            CapturedAtUtc = startedAt,
            Glance = new TurnLogGlance
            {
                SessionId = signal.SessionId,
                SessionName = session?.Name,
                Computer = string.IsNullOrWhiteSpace(session?.MachineName) ? null : session!.MachineName,
                Agent = string.IsNullOrWhiteSpace(session?.Agent) ? null : session!.Agent,
                Repository = string.IsNullOrWhiteSpace(session?.RepoPath) ? null : session!.RepoPath,
                DirectorId = signal.DirectorId,
                Account = signal.Tenant.Value,
            },
            Moment = new TurnLogMoment
            {
                ActivityStateBefore = signal.PreviousActivityState,
                ActivityStateAfter = session?.ActivityState,
                IsNewTurn = signal.IsNewTurn,
                // COPIED FROM THE SIGNAL, never re-stamped here. This capture starts after the boundary and
                // takes as long as the machine takes; the moment the detector SAW the turn end is the one
                // thing that can pair this record with the verdict formed on the same stop.
                TurnEndObservedAtUtc = signal.ObservedAtUtc,
                IdleSeconds = session?.IdleSeconds,
                QuietThresholdSeconds = session?.QuietThresholdSeconds,
                LastActivityAtUtc = session?.LastActivityAt,
                LastOwnerTurnAtUtc = session?.LastOwnerTurnAtUtc,
                ScreenReadMs = screenTimer.ElapsedMilliseconds,
                ScrollbackReadMs = scrollbackTimer.ElapsedMilliseconds,
                GatherMs = overall.ElapsedMilliseconds,
            },
            Session = session,
            Terminal = BuildTerminal(grid, scrollback),
            Conversation = conversation,
            Observed = new TurnLogObserved
            {
                SupervisorEnabled = Safely(() => _env.SupervisorEnabled(signal.Tenant), "supervisor-enabled", gaps),
                VoiceSession = Safely(() => _env.IsVoiceSession(signal.Tenant, signal.SessionId), "voice-session", gaps),
                StateLabel = session?.StateLabel,
                TriageBucket = session?.TriageBucket,
                NeedsYouSinceUtc = session?.NeedsYouSince,
            },
            Verdict = null,
            Gaps = gaps,
        };

        record = EnforceRecordCeiling(record);

        var path = _env.Write(record);
        if (path is null)
            FileLog.Write($"[TurnLogRecorder] record NOT written sid={TurnLogSwitchStore.Clean(signal.SessionId)} - the writer refused it");
        return path;
    }

    /// <summary>
    /// Keep one record under <see cref="MaxRecordBytes"/>, trimming the SCROLLBACK and nothing else.
    ///
    /// Terminal content is untrusted input - it is whatever a program somebody ran decided to print - so a
    /// record's size must not be settable by that program. The scrollback is the shock absorber because it
    /// is the largest part and the least precious: the live screen is what every judgement reads and is
    /// never touched, and the conversation has already been cut to whole turns by its own ceiling.
    ///
    /// A trim is RECORDED. A record that is quietly shorter than it claims teaches the corpus that a
    /// session printed less than it did.
    /// </summary>
    internal static TurnLogRecord EnforceRecordCeiling(TurnLogRecord record)
    {
        var size = Measure(record);
        if (size <= MaxRecordBytes) return record;

        var scrollback = record.Terminal.Scrollback;
        var kept = scrollback.Count;
        // Halve the scrollback until it fits, keeping the NEWEST lines - the ones nearest the screen.
        while (kept > 0 && size > MaxRecordBytes)
        {
            kept /= 2;
            var trimmedTerminal = record.Terminal with
            {
                Scrollback = scrollback.Skip(scrollback.Count - kept).ToList(),
                ScrollbackLineCount = kept,
            };
            record = record with { Terminal = trimmedTerminal };
            size = Measure(record);
        }

        var dropped = scrollback.Count - kept;
        var gaps = record.Gaps.ToList();
        gaps.Add(new TurnLogGap
        {
            Part = "scrollback",
            Reason = $"{dropped} of {scrollback.Count} scrollback line(s) were dropped to keep the record under "
                   + $"{MaxRecordBytes / 1024} KB - the lines nearest the screen were kept, and the screen itself was not touched",
        });
        return record with { Gaps = gaps };
    }

    private static int Measure(TurnLogRecord record)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record).Length;

    private static TurnLogTerminal BuildTerminal(ScreenGridResponse? grid, BufferResponse? scrollback)
    {
        var lines = SplitScrollback(scrollback?.Text);
        return new TurnLogTerminal
        {
            HasGrid = grid?.HasGrid ?? false,
            Rows = grid?.Rows ?? new List<string>(),
            RowCount = grid?.Rows.Count ?? 0,
            CursorRow = grid?.CursorRow ?? -1,
            CursorCol = grid?.CursorCol ?? -1,
            CursorVisible = grid?.CursorVisible ?? false,
            IsAlternateScreen = grid?.IsAlternateScreen ?? false,
            Scrollback = lines,
            ScrollbackLineCount = lines.Count,
            ScrollbackLinesRequested = ScrollbackLinesCaptured,
        };
    }

    private static List<string> SplitScrollback(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Replace("\r\n", "\n").Split('\n').ToList();
    }

    /// <summary>
    /// Cut the stored conversation down to the last <see cref="FullTurnsCaptured"/> full turns.
    ///
    /// A FULL TURN STARTS AT A HUMAN MESSAGE, on the owner's definition that a turn is both sides together.
    /// So the cut walks back counting those and keeps everything from the tenth one onward, which keeps each
    /// kept turn WHOLE rather than slicing a fixed number of messages and handing the corpus an agent reply
    /// whose prompt was cut off.
    ///
    /// A TOOL RESULT IS NOT A HUMAN TURN, even though it arrives with the user's role. Codex records every
    /// <c>function_call_output</c> as a User message carrying a single ToolResult part
    /// (<c>CodexTranscriptReader</c>), so counting bare roles would let ten tool calls inside ONE human turn
    /// consume the whole window - and the conversation we stored would begin in the middle of a turn, with
    /// the prompt that started it cut off, which is the exact failure keeping whole turns exists to prevent.
    /// A human turn is a user message carrying something a person actually wrote.
    /// </summary>
    internal static TurnLogConversation BuildConversation(StoredConversationSnapshot? stored)
    {
        if (stored is null) return new TurnLogConversation();

        var all = stored.Messages;
        var startIndex = 0;
        var humanTurns = 0;
        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (!IsHumanTurn(all[i])) continue;
            humanTurns++;
            if (humanTurns < FullTurnsCaptured) continue;
            startIndex = i;
            break;
        }

        var cutByTurnCount = startIndex > 0;

        // THE SIZE CEILING, applied after the turn count and never instead of it. Drop the oldest WHOLE
        // turn and measure again, until it fits or one turn is left. A single turn over the ceiling is kept
        // rather than sliced: half a turn teaches the corpus something that never happened, and the record
        // says plainly that it is oversized.
        var droppedForSize = 0;
        while (Serialized(all, startIndex) > MaxConversationBytes)
        {
            var next = NextHumanTurn(all, startIndex + 1);
            if (next < 0) break;   // one turn left - keep it whole and let the record be large
            startIndex = next;
            droppedForSize++;
        }

        return new TurnLogConversation
        {
            IsSupported = stored.IsSupported,
            Generation = stored.Generation,
            TotalMessageCount = all.Count,
            FullTurnsRequested = FullTurnsCaptured,
            Truncated = cutByTurnCount || droppedForSize > 0,
            TurnsDroppedForSize = droppedForSize,
            SizeCeilingBytes = MaxConversationBytes,
            LastPushedAtUtc = stored.LastPushedUtc,
            Messages = all.Skip(startIndex).ToList(),
        };
    }

    /// <summary>The serialized size of the messages from <paramref name="from"/> onward - what the record
    /// will actually cost, measured rather than estimated from message counts, because one tool result can
    /// be larger than a hundred prompts.</summary>
    private static int Serialized(IReadOnlyList<HistoryMessageDto> all, int from)
    {
        var window = from == 0 ? all : all.Skip(from).ToList();
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(window).Length;
    }

    /// <summary>The index of the next human turn at or after <paramref name="from"/>, or -1 when there is
    /// none - which is what stops the trim from cutting into the last turn it holds.</summary>
    private static int NextHumanTurn(IReadOnlyList<HistoryMessageDto> all, int from)
    {
        for (var i = Math.Max(0, from); i < all.Count; i++)
            if (IsHumanTurn(all[i])) return i;
        return -1;
    }

    /// <summary>
    /// Whether this message is a person taking a turn, as opposed to the transcript's own bookkeeping
    /// wearing the user's role.
    ///
    /// A user message counts when it carries at least one part that is not a tool result. That admits an
    /// ordinary prompt and excludes a bare <c>function_call_output</c>; a message with no parts at all is
    /// not a turn either, because nothing was said.
    /// </summary>
    private static bool IsHumanTurn(HistoryMessageDto message)
    {
        if (!string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = message.Parts;
        if (parts is null || parts.Count == 0) return false;
        return parts.Any(p => !string.Equals(p.Kind, "ToolResult", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A read whose failure must not cost the whole record. Answers null AND writes a gap.
    ///
    /// The gap is the point. A null with no gap is indistinguishable from a value that is genuinely unknown,
    /// so a record could report "we do not know whether the supervisor was on" for a fault that was ours -
    /// and a corpus mined later would read those as sessions with no supervisor rather than as sessions we
    /// failed to ask about.
    /// </summary>
    private static bool? Safely(Func<bool?> read, string part, List<TurnLogGap> gaps)
    {
        try { return read(); }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogRecorder] an observed-state read FAILED ({part}): {ex.Message}");
            gaps.Add(new TurnLogGap { Part = part, Reason = ex.Message });
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }
        _stopping.Dispose();
    }
}
