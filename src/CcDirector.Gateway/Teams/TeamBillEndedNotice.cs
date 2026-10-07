using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// Tells a team's Owner, ONCE, that the team's bill has ended (Teams v1, owner ruling of 7 October: "Refuse new
/// invitations and accepts, keep existing members, tell the Owner - nobody is cut off, and nobody joins a team that does
/// not pay").
///
/// The refusals themselves are <see cref="TeamRegistry"/>'s. This is what makes the Owner learn about them without
/// having to try an invitation: when someone else - a Manager sending or resending, or an invitee accepting - is refused
/// because the bill has ended, <see cref="TellOwnerOnce"/> asks the website to email the Owner.
///
/// ONCE PER ENDED BILL, NOT ONCE PER REFUSAL. The team is marked with its bill row's fingerprint
/// (<see cref="Tenancy.EntitlementRegistry.TeamBillFingerprint"/>), so a burst of refused accepts sends one email. The mark is taken BEFORE the call, so refusals
/// that arrive while the first email is in flight send nothing; it is released when the website did not send, so the
/// next refusal tries again rather than the Owner never being told. A new bill row (billing restarted and ended again)
/// has a new fingerprint and is told again.
///
/// Held in memory on purpose: after a restart the next refusal asks the website once
/// more, and the fingerprint travels with the request as its idempotency key so the website can refuse the repeat.
///
/// Never throws into the refusal path: the refusal has already been decided and must reach its caller unchanged.
/// </summary>
public sealed class TeamBillEndedNotice
{
    private readonly ITeamBillEndedMailer _mailer;
    private readonly object _markLock = new();
    private readonly Dictionary<string, string> _told = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    /// <param name="mailer">The website-backed mailer that emails the Owner.</param>
    public TeamBillEndedNotice(ITeamBillEndedMailer mailer)
    {
        _mailer = mailer ?? throw new ArgumentNullException(nameof(mailer));
    }

    /// <summary>
    /// Start telling the Owner of <paramref name="teamId"/> that its bill, whose row has
    /// <paramref name="billFingerprint"/>, has ended - unless they were already told about this bill. The Owner is named
    /// by their account subject, <paramref name="ownerSubject"/>, which is never logged. Not awaited by
    /// the caller: a refused request never waits on the website.
    /// </summary>
    /// <returns>True when an email was asked for now; false when the Owner was already told about this ended bill (or
    /// an email about it is already on its way).</returns>
    public bool TellOwnerOnce(string teamId, string ownerSubject, string billFingerprint)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("The team's Owner's account subject is required", nameof(ownerSubject));
        if (string.IsNullOrWhiteSpace(billFingerprint))
            throw new ArgumentException("The ended bill's fingerprint is required", nameof(billFingerprint));

        var key = teamId.Trim();
        lock (_markLock)
        {
            if (_told.TryGetValue(key, out var marked) && string.Equals(marked, billFingerprint, StringComparison.Ordinal))
            {
                FileLog.Write($"[TeamBillEndedNotice] TellOwnerOnce: team {TeamLog(key)} - the Owner was already told about this ended bill, nothing sent");
                return false;
            }
            _told[key] = billFingerprint;
        }

        FileLog.Write($"[TeamBillEndedNotice] TellOwnerOnce: team {TeamLog(key)} - someone was refused because the bill has ended; asking the website to tell the Owner");
        var send = SendAsync(key, ownerSubject, billFingerprint);
        _inFlight.TryAdd(send, 0);
        _ = send.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
        return true;
    }

    /// <summary>Completes when every email started so far has finished. For tests and an orderly shutdown; a request
    /// path never waits on it.</summary>
    internal Task Settled() => Task.WhenAll(_inFlight.Keys.ToArray());

    private async Task SendAsync(string teamId, string ownerSubject, string billFingerprint)
    {
        try
        {
            var result = await _mailer.TellOwnerBillEndedAsync(teamId, ownerSubject, billFingerprint, CancellationToken.None).ConfigureAwait(false);
            if (result.Sent)
            {
                FileLog.Write($"[TeamBillEndedNotice] SendAsync: team {TeamLog(teamId)} - the Owner was told the bill has ended");
                return;
            }
            FileLog.Write($"[TeamBillEndedNotice] SendAsync: team {TeamLog(teamId)} - the Owner was NOT told (status={result.StatusCode} code={result.ErrorCode ?? "<none>"}); the next refusal tries again");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamBillEndedNotice] SendAsync: team {TeamLog(teamId)} FAILED ({ex.GetType().Name}): {ex.Message}; the next refusal tries again");
        }
        Release(teamId, billFingerprint);
    }

    /// <summary>Forget the mark for this ended bill, so the next refusal asks again. Only this bill's mark: a newer
    /// bill's mark taken meanwhile is left alone.</summary>
    private void Release(string teamId, string billFingerprint)
    {
        lock (_markLock)
        {
            if (_told.TryGetValue(teamId, out var marked) && string.Equals(marked, billFingerprint, StringComparison.Ordinal))
                _told.Remove(teamId);
        }
    }

    private static string TeamLog(string teamId) => new Core.Tenancy.TenantId(teamId).ToLogString();
}
