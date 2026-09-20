using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE COMMAND LINE DOOR'S ONE TRANSLATION (mission "Smart Director Restart", section 5.3 item 12).
///
/// The engine's own answers - <see cref="SmartShutdownSnapshot"/>, <see cref="SmartShutdownResult"/>,
/// <see cref="WayUpHistory"/> - live in this project and never leave the Director's process. The command
/// line reads them over the Gateway, so they have to become the wire objects in
/// <c>CcDirector.Gateway.Contracts</c> somewhere. This is that somewhere, and it is the only one.
///
/// IT CARRIES WORDS, IT DOES NOT CHOOSE THEM. Every sentence on the wire - the phase label, each row's
/// state label, the count sentence, the outcome detail, every label in the history - is copied from the
/// engine's own answer unchanged. Nothing here decides what a state means, and nothing here invents a
/// sentence for a state the engine did not word: a state the engine cannot word throws in
/// <see cref="SmartShutdownWords"/> before it ever reaches this file. That is critical rule 7 (the client
/// is dumb) applied to a client that happens to be a terminal rather than a window.
///
/// WHY A SEPARATE SHAPE AT ALL, rather than serializing the engine's records. The Gateway relays these
/// bytes and the command line parses them, and neither references this project; a record with positional
/// construction and an enum is also not a shape to promise a parser. So the wire shape is declared once,
/// in the contracts both sides already share, and the enums travel as their own names - which a reader may
/// colour by and must never word by.
/// </summary>
public static class SmartRestartWire
{
    /// <summary>The answer for a Director on which no smart shutdown has been started since it came up.
    /// It says so rather than answering an empty run, because an absent run and a finished one are
    /// different facts and a reader that could not tell them apart would show one as the other.</summary>
    /// <param name="directorName">This Director, as a person names it.</param>
    public static SmartRestartProgressDto NothingStarted(string directorName) => new()
    {
        Running = false,
        Started = false,
        Detail =
            $"No smart shutdown has been started on {directorName} since it came up. " +
            "Start one with: cc-devthrottle director smart-restart",
    };

    /// <summary>One complete reading of a run.</summary>
    /// <param name="snapshot">The run's current snapshot - complete and immutable, as the engine raises it.</param>
    /// <param name="result">The result, once the run has finished; null while it is still going.</param>
    /// <param name="endedWithoutResult">
    /// Set only when the run is over and carried NO result - which the engine's own contract says cannot
    /// happen, because its completion never faults for an expected end. It is here rather than absent so
    /// that if it ever does happen the reader is told, in plain words, instead of being shown a run that
    /// is neither running nor finished and left to guess which.
    /// </param>
    public static SmartRestartProgressDto Progress(
        SmartShutdownSnapshot snapshot, SmartShutdownResult? result, string? endedWithoutResult = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SmartRestartProgressDto
        {
            Running = result is null && endedWithoutResult is null,
            Started = true,
            Phase = snapshot.Phase.ToString(),
            PhaseLabel = snapshot.PhaseLabel,
            CountLabel = snapshot.CountLabel,
            Total = snapshot.Total,
            Gone = snapshot.Gone,
            StartedUtc = snapshot.StartedUtc,
            InterruptAtUtc = snapshot.InterruptAtUtc,
            LimitUtc = snapshot.LimitUtc,
            WorkspaceId = result?.WorkspaceId ?? snapshot.WorkspaceId,
            Note = snapshot.Note,
            Outcome = result?.Outcome.ToString(),
            // While it runs the note IS the sentence worth showing; once it has ended the result's own
            // sentence is. Neither is composed here.
            Detail = endedWithoutResult ?? result?.Detail ?? snapshot.Note ?? snapshot.PhaseLabel,
            Sessions = snapshot.Sessions.Select(Session).ToList(),
        };
    }

    /// <summary>One session's row.</summary>
    /// <param name="row">The engine's row.</param>
    public static SmartRestartSessionDto Session(SmartShutdownSessionProgress row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SmartRestartSessionDto
        {
            SessionId = row.SessionId,
            Name = row.Name,
            Mission = row.Mission,
            Role = row.Role,
            OwnerSessionId = row.OwnerSessionId,
            State = row.State.ToString(),
            StateLabel = row.StateLabel,
            Detail = row.Detail,
        };
    }

    /// <summary>The restart history.</summary>
    /// <param name="history">The way up's own answer.</param>
    public static SmartRestartHistoryDto History(WayUpHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        FileLog.Write($"[SmartRestartWire] History: refused={history.Refused}, entries={history.Entries.Count}");
        return new SmartRestartHistoryDto
        {
            Refused = history.Refused,
            Message = history.Message,
            Entries = history.Entries.Select(Entry).ToList(),
        };
    }

    private static SmartRestartHistoryEntryDto Entry(WayUpHistoryEntry entry) => new()
    {
        WorkspaceId = entry.WorkspaceId,
        AtUtc = entry.AtUtc,
        WhenLabel = entry.WhenLabel,
        KindLabel = entry.KindLabel,
        ReasonLabel = entry.ReasonLabel,
        OutcomeLabel = entry.OutcomeLabel,
        // The offer's own sentence, or nothing at all. A record with nothing left to act on says
        // nothing rather than saying "0 sessions", which reads as a thing to act on.
        SeatsLabel = entry.Offer?.SeatsLabel,
        Seats = entry.Seats.Select(seat => new SmartRestartHistorySeatDto
        {
            SessionId = seat.SessionId,
            Name = seat.Name,
            Mission = seat.Mission,
            Role = seat.Role,
            Outcome = seat.Outcome,
        }).ToList(),
    };
}
