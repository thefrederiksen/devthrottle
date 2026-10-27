using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>What the resumer needs from a session. <see cref="SessionLimitResumeTarget"/> is the product's one
/// implementation; tests supply a scripted one, so every rule is provable without a terminal or an agent.</summary>
public interface ILimitResumeTarget
{
    Guid Id { get; }

    AgentKind Agent { get; }

    /// <summary>The agent's transcript file; null when the session has not named one.</summary>
    string? TranscriptPath { get; }

    /// <summary>When input last reached the session's terminal by any route - a person's keys, a prompt, an agent's
    /// send - or null when none has. The Director's own Escape to the usage-limit dialog is not input.</summary>
    DateTime? LastInputAtUtc { get; }

    /// <summary>Type <c>continue</c> and press Enter, only when the session is waiting for a prompt with no usage-limit
    /// dialog on its screen and no unsent text of the owner's in its composer. Never waits for it to become so.</summary>
    Task<LimitResumeSend> SendContinueAsync();
}

/// <summary>How a <c>continue</c> send ended: sent, or the reason nothing was typed.</summary>
public sealed record LimitResumeSend(bool Sent, string? Reason)
{
    public static LimitResumeSend Ok { get; } = new(true, null);
    public static LimitResumeSend Refused(string reason) => new(false, reason);
}

/// <summary>The product's <see cref="ILimitResumeTarget"/>: a live <see cref="Session"/>.</summary>
public sealed class SessionLimitResumeTarget : ILimitResumeTarget
{
    private readonly Session _session;

    public SessionLimitResumeTarget(Session session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public Guid Id => _session.Id;
    public AgentKind Agent => _session.AgentKind;
    public string? TranscriptPath => _session.ClaudeTranscriptPath;
    public DateTime? LastInputAtUtc => _session.LastInputAtUtc;

    public async Task<LimitResumeSend> SendContinueAsync()
    {
        // The dialog's paid options must never take a keystroke. The Director escapes the dialog when the screen
        // settles; until it has gone, nothing is typed.
        if (_session.UsageLimitDialogMayShow())
            return LimitResumeSend.Refused("the usage-limit dialog may still be on the screen");
        var sent = await _session.SendTextOnlyWhenWaitingForInputAsync(LimitStoppedSessionResumer.ContinueText,
            SubmissionProvenance.Typed(SubmissionRoutes.Framework, SubmissionIdentityKinds.Framework),
            SendSource.Agent, origin: null, appendEnter: true).ConfigureAwait(false);
        return sent.Accepted ? LimitResumeSend.Ok : LimitResumeSend.Refused(sent.Reason ?? "the session could not take a prompt now");
    }
}

/// <summary>What the resumer did with one session after one switch.</summary>
public sealed record LimitResumeOutcome(Guid SessionId, bool Resumed, string Detail);

/// <summary>
/// The Loud When Stranded mission, step 6: after a VERIFIED license switch, the Director sends ONE plain
/// <c>continue</c> to each of its Claude Code sessions whose last turn ended on a usage-limit reply.
///
/// Why: on 26 Oct 2026 nine sessions on one machine stopped on the weekly limit within four minutes. One plain prompt
/// after the machine's active sign-in changed resumed a stopped session that had not signed in again (session 115,
/// 14:58:40 UTC). Without this they wait for the owner.
///
/// The rules, each one a test:
/// <list type="bullet">
/// <item>Verified means the switch reads back the new account AND a usage read with the new sign-in answered
/// (<see cref="IsVerifiedSwitch"/>). A replayed answer is not a new switch.</item>
/// <item>The limit is recognised from the transcript record (<see cref="UsageLimitTranscript"/>), never the screen.
/// An unreadable transcript is logged loudly and the session is skipped - nothing is guessed.</item>
/// <item>Claude Code sessions only.</item>
/// <item>Once: one limit reply is answered once (<see cref="LimitResumeStore"/>), whatever the switches and restarts.</item>
/// <item>A session given any input after the limit reply is not prompted.</item>
/// <item>The send is the guarded one: only a session waiting for a prompt, never into a dialog or a working session.</item>
/// </list>
/// </summary>
public sealed class LimitStoppedSessionResumer
{
    /// <summary>The one prompt sent.</summary>
    public const string ContinueText = "continue";

    private readonly LimitResumeStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, LastTurn> _readLastTurn;

    /// <summary>One resume at a time. The executor starts each resume without awaiting it, so a second verified switch
    /// could otherwise check a session before the first has recorded the continue it sent (review of pull request 4534,
    /// observation O4).</summary>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="readLastTurn">Test seam: how a transcript is read; <see cref="UsageLimitTranscript.ReadLastTurn"/> when null.</param>
    public LimitStoppedSessionResumer(LimitResumeStore store, Func<DateTime> utcNow, Func<string, LastTurn>? readLastTurn = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _readLastTurn = readLastTurn ?? UsageLimitTranscript.ReadLastTurn;
    }

    /// <summary>True for a switch that ran now, switched, and was verified by a usage read with the new sign-in.</summary>
    public static bool IsVerifiedSwitch(LicenseSwitchResult result) =>
        result.Outcome == LicenseSwitchOutcomes.Switched && result.VerifiedByUsageRead && !result.Replayed;

    /// <summary>
    /// After <paramref name="result"/>, sends <c>continue</c> to each of <paramref name="sessions"/> stopped on a usage-limit
    /// reply, one at a time. A switch that is not verified does nothing. Returns what was done with each session.
    /// </summary>
    public async Task<IReadOnlyList<LimitResumeOutcome>> ResumeAfterSwitchAsync(LicenseSwitchResult result, IReadOnlyCollection<ILimitResumeTarget> sessions)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sessions);
        FileLog.Write($"[LimitStoppedSessionResumer] ResumeAfterSwitchAsync: switchId={result.SwitchId}, outcome={result.Outcome}, verified={result.VerifiedByUsageRead}, replayed={result.Replayed}, sessions={sessions.Count}");
        if (!IsVerifiedSwitch(result))
        {
            FileLog.Write($"[LimitStoppedSessionResumer] switchId={result.SwitchId}: not a verified switch, so no session is prompted");
            return [];
        }

        var outcomes = new List<LimitResumeOutcome>();
        await _oneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in sessions)
            {
                var outcome = await ResumeOneAsync(result.SwitchId, session).ConfigureAwait(false);
                FileLog.Write($"[LimitStoppedSessionResumer] switchId={result.SwitchId}, session={outcome.SessionId}: {(outcome.Resumed ? "RESUMED" : "skipped")} - {outcome.Detail}");
                outcomes.Add(outcome);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
        var resumed = outcomes.Count(o => o.Resumed);
        FileLog.Write($"[LimitStoppedSessionResumer] switchId={result.SwitchId} done: resumed={resumed}, skipped={outcomes.Count - resumed}");
        return outcomes;
    }

    private async Task<LimitResumeOutcome> ResumeOneAsync(string switchId, ILimitResumeTarget session)
    {
        if (session.Agent != AgentKind.ClaudeCode)
            return Skip(session, $"it runs {session.Agent}, not Claude Code");
        if (string.IsNullOrWhiteSpace(session.TranscriptPath))
            return Skip(session, "it has named no transcript, so its last turn is not known");

        LastTurn last;
        try
        {
            last = _readLastTurn(session.TranscriptPath);
        }
        catch (IOException ex)
        {
            last = LastTurn.Unreadable($"the transcript could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            last = LastTurn.Unreadable($"the transcript could not be read: {ex.Message}");
        }

        switch (last.Kind)
        {
            case LastTurnKind.Unreadable:
                FileLog.Write($"[LimitStoppedSessionResumer] session={session.Id} TRANSCRIPT UNREADABLE FAILED: {last.Reason}; the session is not prompted");
                return Skip(session, $"its transcript is unreadable ({last.Reason})");
            case LastTurnKind.NoConversation:
                return Skip(session, $"it has no conversation yet ({last.Reason})");
            case LastTurnKind.Other:
                return Skip(session, "its last turn did not end on a usage-limit reply");
        }

        var record = last.RecordUuid!;
        if (_store.WasResumed(record))
            return Skip(session, $"a continue was already sent for its limit reply {record}");
        if (session.LastInputAtUtc is { } input && input > last.AtUtc)
            return Skip(session, $"it was given input at {input:O}, after its limit reply at {last.AtUtc:O}");

        var sent = await session.SendContinueAsync().ConfigureAwait(false);
        if (!sent.Sent)
            return Skip(session, $"nothing was typed: {sent.Reason}");
        _store.Record(new LimitResume(record, session.Id, switchId, _utcNow()));
        return new LimitResumeOutcome(session.Id, true, $"sent '{ContinueText}' for its limit reply {record} at {last.AtUtc:O}");
    }

    private static LimitResumeOutcome Skip(ILimitResumeTarget session, string why) => new(session.Id, false, why);
}
