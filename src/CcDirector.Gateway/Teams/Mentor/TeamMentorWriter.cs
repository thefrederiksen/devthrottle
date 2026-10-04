using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>What one run of a team's week did: whether it had already run, how many blocks it wrote, and the outcome
/// for every member.</summary>
public sealed record MentorWeekRun(MentorWeek Week, bool AlreadyRan, int BlocksWritten, IReadOnlyList<MentorOutcome> Outcomes);

/// <summary>
/// THE MENTOR'S WEEKLY WRITER (devthrottle_internal#2305). For one team and one closed ISO week, for each member:
///
/// <list type="number">
/// <item>No sessions of theirs in the team that week: no block and NO model call (outcome <c>no-sessions</c>).</item>
/// <item>Sessions, but no prompts of theirs the Gateway holds for that week: the Mentor has nothing to read, so again no
/// block and no model call (<c>no-prompts</c>).</item>
/// <item>Otherwise their own prompts of that week - and only theirs - go to the model under labels
/// (<see cref="MentorBrief"/>). The answer is checked strictly. A refused answer writes NO block and is recorded with its
/// reason (<c>refused</c>); a model that cannot be reached likewise (<c>model-failed</c>). No retry, no looser reading,
/// no fallback text (rule 3).</item>
/// <item>An accepted answer is stored as the block. Its quotes are the person's own prompt records, copied verbatim by
/// the Gateway from the prompt log - the model only ever named a label.</item>
/// </list>
///
/// Then the week is marked run. A member whose outcome is already recorded is skipped, so a run cut short (a restart)
/// finishes the week without writing anyone twice; a week already marked run is not touched at all.
///
/// The team is passed explicitly; every store is asked for that team by name, never from an ambient scope.
/// </summary>
public sealed class TeamMentorWriter
{
    /// <summary>Resolves the model for a team's Mentor call, and its model id. Production builds the hosted inference
    /// brain the way the dictionary suggestion does; tests pass a fake. The writer disposes the brain after the call.</summary>
    public delegate Task<(IAgentBrain Brain, string Model)> MentorBrainFactory(TenantId team, CancellationToken ct);

    private readonly TeamRegistry _teams;
    private readonly TeamMentorStore _store;
    private readonly SessionHistoryStore _sessions;
    private readonly GatewayPromptLog _prompts;
    private readonly MentorBrainFactory _brains;
    private readonly Func<DateTime> _now;

    public TeamMentorWriter(TeamRegistry teams, TeamMentorStore store, SessionHistoryStore sessions, GatewayPromptLog prompts,
        MentorBrainFactory brains, Func<DateTime>? now = null)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _brains = brains ?? throw new ArgumentNullException(nameof(brains));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Write one team's week. The entry point the weekly sweep calls, and the one the tests and the proof call on
    /// demand; it is not a route.
    /// </summary>
    /// <param name="team">The team's tenant. Must be a team.</param>
    /// <param name="week">The ISO week, which must have closed in <paramref name="zone"/>.</param>
    /// <param name="zone">The team's time zone, which the week is cut in.</param>
    public async Task<MentorWeekRun> WriteWeekAsync(TenantId team, MentorWeek week, TimeZoneInfo zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!_teams.IsTeam(team))
            throw new ArgumentException("The Mentor writes only for a team's tenant.", nameof(team));
        var (fromUtc, toUtc) = week.UtcBounds(zone);
        if (_now() < toUtc)
            throw new InvalidOperationException($"Week {week} has not closed yet in {zone.Id}; the Mentor writes a week only once it is over.");

        FileLog.Write($"[TeamMentorWriter] WriteWeekAsync: team {team.ToLogString()} week={week} zone={zone.Id}");
        if (_store.HasRun(team, week))
        {
            FileLog.Write($"[TeamMentorWriter] WriteWeekAsync: team {team.ToLogString()} week={week} already ran - nothing to do");
            return new MentorWeekRun(week, AlreadyRan: true, 0, _store.Outcomes(team, week));
        }

        var members = _teams.MembersOf(team.Value);
        var ranSessions = _sessions.PersonsWithSessions(team, fromUtc, toUtc);
        var weekPrompts = PromptsOfWeek(team, fromUtc, toUtc);

        var written = 0;
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            if (_store.OutcomeOf(team, week, member.AccountSubject) is not null)
                continue;
            if (await WritePersonAsync(team, week, member, ranSessions, weekPrompts, ct).ConfigureAwait(false))
                written++;
        }

        var total = _store.Outcomes(team, week);
        _store.RecordRun(team, week, zone.Id, total.Count(o => o.Outcome == MentorOutcomes.Written), _now());
        FileLog.Write($"[TeamMentorWriter] WriteWeekAsync: team {team.ToLogString()} week={week} done - {written} block(s) written this run, {total.Count} member outcome(s)");
        return new MentorWeekRun(week, AlreadyRan: false, written, total);
    }

    /// <summary>One member's block. True when a block was written.</summary>
    private async Task<bool> WritePersonAsync(TenantId team, MentorWeek week, TeamMember member,
        IReadOnlySet<string> ranSessions, IReadOnlyList<PromptRecord> weekPrompts, CancellationToken ct)
    {
        var subject = member.AccountSubject;
        if (!ranSessions.Contains(subject))
        {
            _store.RecordOutcome(team, week, subject, MentorOutcomes.NoSessions, null, _now());
            return false;
        }

        var own = weekPrompts.Where(p => string.Equals(p.PersonSubject, subject, StringComparison.Ordinal)).ToList();
        if (own.Count == 0)
        {
            // Sessions, but nothing they typed or said reached the prompt log this week: the Mentor has nothing to
            // read, so there is no model call and no block - never an empty or invented one.
            _store.RecordOutcome(team, week, subject, MentorOutcomes.NoPrompts, null, _now());
            return false;
        }

        var request = MentorBrief.Build(week, own);
        string answerText;
        string model;
        try
        {
            var (brain, modelId) = await _brains(team, ct).ConfigureAwait(false);
            model = modelId;
            using (brain)
                answerText = (await brain.AskAsync(request.Text, ct).ConfigureAwait(false)).Text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamMentorWriter] model call FAILED for team {team.ToLogString()} week={week}: {ex.GetType().Name}: {ex.Message}");
            _store.RecordOutcome(team, week, subject, MentorOutcomes.ModelFailed, $"The model could not be reached: {ex.Message}", _now());
            return false;
        }

        var check = MentorBrief.Check(answerText, request);
        if (check.Answer is not { } answer)
        {
            Refuse(team, week, subject, check.Refusal!);
            return false;
        }

        // The quotes: the person's own prompt records, by the labels this request handed out, text copied as the log
        // holds it. Check has already refused any label that is not one of them.
        var quotes = answer.QuoteLabels
            .Select(label => request.PromptsByLabel[label])
            .Select(p => new MentorQuote(p.PromptId!, p.TsUtc, p.Text))
            .ToList();

        _store.SaveBlock(team, new MentorBlock(
            week.ToString(), subject, answer.Tone, answer.WorkedOn, answer.HowItWent, answer.WentBadlyAndWhy,
            quotes, answer.OneThingToTry, _now(), model));
        return true;
    }

    private void Refuse(TenantId team, MentorWeek week, string subject, string reason)
    {
        FileLog.Write($"[TeamMentorWriter] answer REFUSED for team {team.ToLogString()} week={week}: {reason}");
        _store.RecordOutcome(team, week, subject, MentorOutcomes.Refused, reason, _now());
    }

    /// <summary>
    /// The prompts the Mentor may read for a week: the team's prompt-log records sent by a person (role "user") inside
    /// the week, that carry the Gateway's person stamp and prompt id. A record written before the stamp existed is
    /// nobody's and is never read.
    /// </summary>
    private IReadOnlyList<PromptRecord> PromptsOfWeek(TenantId team, DateTime fromUtc, DateTime toUtc)
    {
        var records = _prompts.Read(team, fromUtc.Date, toUtc.Date);
        return records
            .Where(r => r.TsUtc >= fromUtc && r.TsUtc < toUtc)
            .Where(r => string.Equals(r.Role, "user", StringComparison.Ordinal))
            .Where(r => !string.IsNullOrWhiteSpace(r.PersonSubject) && !string.IsNullOrWhiteSpace(r.PromptId))
            .ToList();
    }
}
