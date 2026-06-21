using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// One always-on authentication-floor event - a "logged-in" recorded the first time a token pair
/// is stored, or a "logout" recorded when the store is cleared. This is the minimum, always-on
/// authentication telemetry; the richer, user-controllable telemetry is owned by the account area
/// (issue #582). The record never carries the token itself.
/// </summary>
/// <param name="Kind">The event kind: <c>logged-in</c> or <c>logout</c>.</param>
/// <param name="At">When the event occurred (UTC).</param>
public sealed record AuthEvent(string Kind, DateTime At);

/// <summary>
/// Append-only local record of authentication-floor events, written as JSON-lines under the
/// Director config directory (one event per line). It records only the event kind and timestamp -
/// never the access or refresh token - so the log can never leak a credential.
/// </summary>
public sealed class AuthEventLog
{
    /// <summary>The "logged-in on first store" event kind.</summary>
    public const string LoggedIn = "logged-in";

    /// <summary>The "logout on clear" event kind.</summary>
    public const string LoggedOut = "logout";

    private readonly string _logPath;

    /// <summary>
    /// Creates the log. The default location is the authentication-events file under the Director
    /// config directory; tests pass an explicit path to a temporary directory.
    /// </summary>
    public AuthEventLog(string? logPath = null)
    {
        _logPath = logPath ?? CcStorage.DevThrottleAuthEventsLog();
    }

    /// <summary>Records a "logged-in" event (called the first time a token pair is stored).</summary>
    public void RecordLoggedIn()
    {
        FileLog.Write("[AuthEventLog] RecordLoggedIn");
        Append(new AuthEvent(LoggedIn, DateTime.UtcNow));
    }

    /// <summary>Records a "logout" event (called when the store is cleared).</summary>
    public void RecordLoggedOut()
    {
        FileLog.Write("[AuthEventLog] RecordLoggedOut");
        Append(new AuthEvent(LoggedOut, DateTime.UtcNow));
    }

    /// <summary>Reads every recorded event in the order it was written, oldest first.</summary>
    public IReadOnlyList<AuthEvent> ReadAll()
    {
        FileLog.Write($"[AuthEventLog] ReadAll: logPath={_logPath}, exists={File.Exists(_logPath)}");
        if (!File.Exists(_logPath))
            return Array.Empty<AuthEvent>();

        var events = new List<AuthEvent>();
        foreach (var line in File.ReadAllLines(_logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parsed = JsonSerializer.Deserialize<AuthEvent>(line);
            if (parsed is not null)
                events.Add(parsed);
        }
        return events;
    }

    private void Append(AuthEvent authEvent)
    {
        var dir = Path.GetDirectoryName(_logPath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_logPath}");
        Directory.CreateDirectory(dir);
        File.AppendAllText(_logPath, JsonSerializer.Serialize(authEvent) + Environment.NewLine);
    }
}
