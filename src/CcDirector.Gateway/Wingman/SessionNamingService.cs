using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Claude;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the session namer needs from the live Gateway. Production is <see cref="GatewayTurnVerdictEnvironment"/>;
/// a test passes a fake.
/// </summary>
public interface ISessionNamingEnvironment
{
    /// <summary>The session's facts and whether a live owning session holds it, from one fresh snapshot of the
    /// account's pushed roster (the same read the turn-verdict seat makes).</summary>
    TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId);

    /// <summary>Whether this account's Wingman judge switch is on. A voice session is read with it off, and naming
    /// must not add a paid call the account switched off.</summary>
    bool JudgeEnabled(TenantId tenant);

    /// <summary>The session's stored conversation, read inside the account's scope. Null: nothing stored yet.</summary>
    StoredConversation? ReadConversation(TenantId tenant, string sessionId);

    /// <summary>Ask the Wingman's model for a name. Throws on no answer, exactly as the judge does.</summary>
    Task<string> AskNamerAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct);

    /// <summary>Rename the session on its Director (the <c>patch</c> verb). Returns null on success, else why it
    /// failed.</summary>
    Task<string?> RenameSessionAsync(TenantId tenant, string directorId, string sessionId, string name, CancellationToken ct);
}

/// <summary>
/// THE WINGMAN NAMES A SESSION THE USER DID NOT NAME (issue #3488).
///
/// A session born without a name shows "&lt;repo&gt; / &lt;id4&gt;" for its whole life, so a fleet of them cannot be
/// told apart. After the Wingman's turn-end reading, if the session still has no name a person gave it, one small
/// model call turns the user's FIRST prompt into a short name and the Gateway applies it over the <c>patch</c> verb.
///
/// The rules:
/// <list type="bullet">
/// <item>NOT NAMED BY THE USER means an empty name (the display fallback) or one still marked
/// <see cref="SessionDto.IsAutoNamed"/>. Any other name is a person's or the session's own and is never touched.</item>
/// <item>AT MOST ONCE. The <c>patch</c> verb clears <c>IsAutoNamed</c> on the Director, so once named a session is
/// never named again; the in-memory attempt set stops a second call for the same session in this process.</item>
/// <item>ONLY WHERE THE WINGMAN ALREADY RUNS: a <see cref="TurnVerdictTrigger.TurnEnd"/> reading that was accepted.
/// A held (owned) session is never read, and an account whose Wingman is off produces no accepted reading, so
/// neither is named and neither pays for a call.</item>
/// <item>THE USER'S RENAME WINS. The facts are read again right before the rename, and a name that changed while
/// the model was answering is left alone.</item>
/// </list>
/// </summary>
public sealed class SessionNamingService
{
    /// <summary>The longest name the namer applies.</summary>
    public const int MaxNameLength = 60;

    /// <summary>How much of the first prompt the model is shown.</summary>
    public const int MaxPromptChars = 2000;

    /// <summary>How long the one naming call may take.</summary>
    public static readonly TimeSpan NamerTimeout = TimeSpan.FromSeconds(30);

    private readonly ISessionNamingEnvironment _env;

    // (tenant, session) pairs this process has already tried to name. Bounded by the sessions a Gateway sees.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _attempted = new();

    public SessionNamingService(ISessionNamingEnvironment environment)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>The event handler: starts the naming and never throws into the seat that raised the event.</summary>
    public void OnReadingCompleted(TurnVerdictReadingCompleted completed) => _ = NameIfUnnamedAsync(completed);

    /// <summary>
    /// Name the session the reading was about when it has no name a person gave it. Returns the name applied, or
    /// null when none was. Never throws.
    /// </summary>
    public async Task<string?> NameIfUnnamedAsync(TurnVerdictReadingCompleted completed, CancellationToken ct = default)
    {
        if (completed is null) return null;
        if (completed.Trigger != TurnVerdictTrigger.TurnEnd) return null;
        if (!completed.Outcome.HasAcceptedVerdict) return null;

        var key = (completed.Tenant, completed.SessionId);
        if (_attempted.ContainsKey(key)) return null;

        try
        {
            if (!_env.JudgeEnabled(completed.Tenant)) return null;
            var state = _env.ReadSessionState(completed.Tenant, completed.SessionId);
            // Owned since the reading's own held check: an owned session is never read, so never named either.
            if (state.Held) return null;
            var facts = state.Facts;
            if (facts is null) return null;
            if (!IsUnnamed(facts))
            {
                // A person's or the session's own name: settled for good, never ask again.
                _attempted.TryAdd(key, 0);
                return null;
            }

            var firstPrompt = FirstUserPrompt(_env.ReadConversation(completed.Tenant, completed.SessionId));
            // Nothing the user said is stored yet: try again at the next turn end.
            if (firstPrompt is null) return null;

            if (!_attempted.TryAdd(key, 0)) return null;

            var reply = await _env.AskNamerAsync(completed.Tenant, BuildPrompt(firstPrompt), NamerTimeout, ct)
                .ConfigureAwait(false);
            var name = CleanName(reply);
            if (name is null)
            {
                FileLog.Write($"[SessionNamingService] no usable name from the model sid={completed.SessionId} " +
                              $"tenant={completed.Tenant.ToLogString()} replyLength={reply?.Length ?? 0}");
                return null;
            }

            // The user may have renamed it while the model was answering: their name wins.
            var nowState = _env.ReadSessionState(completed.Tenant, completed.SessionId);
            var now = nowState.Facts;
            if (nowState.Held || now is null || !IsUnnamed(now) || !string.Equals(now.Name, facts.Name, StringComparison.Ordinal))
            {
                FileLog.Write($"[SessionNamingService] renamed by someone else while naming, left alone sid={completed.SessionId}");
                return null;
            }

            var directorId = string.IsNullOrWhiteSpace(now.DirectorId) ? completed.DirectorId : now.DirectorId;
            var error = await _env.RenameSessionAsync(completed.Tenant, directorId, completed.SessionId, name, ct)
                .ConfigureAwait(false);
            if (error is not null)
            {
                FileLog.Write($"[SessionNamingService] rename FAILED sid={completed.SessionId} director={directorId}: {error}");
                return null;
            }

            FileLog.Write($"[SessionNamingService] named sid={completed.SessionId} tenant={completed.Tenant.ToLogString()} name=\"{name}\"");
            return name;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionNamingService] naming FAILED sid={completed.SessionId}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the session has no name a person or the session itself gave it.</summary>
    public static bool IsUnnamed(SessionDto facts)
        => string.IsNullOrWhiteSpace(facts.Name) || facts.IsAutoNamed;

    /// <summary>
    /// The first thing the user typed or said in the session, or null when none is stored. A transcript also
    /// records machine text on the user's side - a <c>&lt;system-reminder&gt;</c> block, a slash command's
    /// <c>&lt;command-name&gt;</c> or its output, the "Caveat:" line before local command output, a loaded skill's
    /// body, a task notification, the continuation summary after compaction - and none of it is what the person
    /// asked for. The stored conversation does not keep the transcript's meta flag, so tagged blocks are cut out and
    /// what is left is passed over when it is empty, the caveat, or what
    /// <see cref="ClaudeSessionReader.IsSystemInjectedContent"/> already knows as injected.
    /// </summary>
    public static string? FirstUserPrompt(StoredConversation? conversation)
    {
        if (conversation is not { IsSupported: true } c || c.Widgets is null) return null;
        foreach (var w in c.Widgets)
        {
            if (w is null || !string.Equals(w.Kind, StoredConversationWidgets.UserTextKind, StringComparison.Ordinal)) continue;
            var text = MachineBlock.Replace(w.Content ?? "", "").Trim();
            if (text.Length == 0
                || text.StartsWith("Caveat:", StringComparison.Ordinal)
                || ClaudeSessionReader.IsSystemInjectedContent(text)) continue;
            return text.Length <= MaxPromptChars ? text : text[..MaxPromptChars];
        }
        return null;
    }

    // A tagged block the harness writes into the user's side of a transcript: <tag ...>...</tag>, across lines.
    private static readonly Regex MachineBlock = new(
        @"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-stderr|local-command-caveat)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The one question the model is asked.</summary>
    public static string BuildPrompt(string firstPrompt) =>
        "You name coding-agent sessions so a person can tell them apart in a list.\n" +
        "Below is the first request the user gave this session. Reply with a short name for the session: " +
        "3 to 6 words, title case, naming the task, not the tool. Plain ASCII only. " +
        "No quotes, no punctuation at the end, no explanation - the name and nothing else.\n\n" +
        "First request:\n<<<\n" + firstPrompt + "\n>>>";

    /// <summary>
    /// The model's reply made into a name: the first non-empty line, ASCII only, wrapping quotes and a label such as
    /// "Name:" removed, whitespace collapsed, capped at <see cref="MaxNameLength"/>. Null when nothing usable is left.
    /// </summary>
    public static string? CleanName(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var line = reply.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;

        if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) line = line[5..];

        var sb = new StringBuilder(line.Length);
        var lastSpace = false;
        foreach (var ch in line)
        {
            if (ch > 126 || ch < 32 || ch is '"' or '`' or '*' or '#') continue;
            var isSpace = ch == ' ';
            if (isSpace && (lastSpace || sb.Length == 0)) continue;
            sb.Append(ch);
            lastSpace = isSpace;
        }

        var name = sb.ToString().Trim().Trim('\'').TrimEnd('.', '!', '?', ':', ';', ',').Trim();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
            var cut = name.LastIndexOf(' ');
            if (cut > MaxNameLength / 2) name = name[..cut];
            name = name.TrimEnd();
        }
        return name.Any(char.IsLetterOrDigit) ? name : null;
    }
}
