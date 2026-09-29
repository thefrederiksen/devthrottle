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

    /// <summary>Whether this account's Wingman judge switch is on. Naming must not add a paid call the account
    /// switched off.</summary>
    bool JudgeEnabled(TenantId tenant);

    /// <summary>The session's conversation read LIVE from its Director over the tunnel (the <c>turns</c> verb). The
    /// Gateway's stored copy arrives by push and can lag the moment the user presses Enter, so the namer reads the
    /// source. Null when the Director cannot be reached.</summary>
    Task<TurnsResponse?> ReadTurnsAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Ask the Wingman's model for a name. Throws on no answer, exactly as the judge does.</summary>
    Task<string> AskNamerAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct);

    /// <summary>Rename the session on its Director (the <c>patch</c> verb). Returns null on success, else why it
    /// failed.</summary>
    Task<string?> RenameSessionAsync(TenantId tenant, string directorId, string sessionId, string name, CancellationToken ct);

    /// <summary>Wait. The namer's only clock, so a test runs the wait for the first prompt instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>
/// THE WINGMAN NAMES A SESSION THE USER DID NOT NAME, FROM THE USER'S FIRST PROMPT (issue #3488).
///
/// A session born without a name shows "&lt;repo&gt; / &lt;id4&gt;" for its whole life, so a fleet of them cannot be
/// told apart. THE MOMENT THE USER SENDS THE FIRST PROMPT - the session's Working edge, not the agent's turn end -
/// the Gateway reads that prompt, one small model call turns it into a short name, and the Gateway applies it over
/// the <c>patch</c> verb. The name is there while the agent is still working on the request.
///
/// The rules:
/// <list type="bullet">
/// <item>NOT NAMED BY THE USER means an empty name (the Director shows the "&lt;repo&gt; / &lt;id4&gt;" fallback) or
/// one still marked <see cref="SessionDto.IsAutoNamed"/> (composed at birth). Any other name is a person's or the
/// session's own and is never touched.</item>
/// <item>AT MOST ONCE. The <c>patch</c> verb clears <c>IsAutoNamed</c> on the Director, so once named a session is
/// never named again; the in-memory attempt set stops a second call for the same session in this process.</item>
/// <item>WAITS FOR THE PROMPT, NOT FOR THE ANSWER. The Working edge can come a moment before the prompt reaches the
/// transcript, so the namer re-reads for up to <see cref="PromptWaitAttempts"/> x <see cref="PromptWaitInterval"/>.
/// A Working edge with no user prompt behind it (the agent starting up) names nothing and leaves the session to the
/// next edge.</item>
/// <item>NEVER A HELD SESSION, NEVER WITH THE WINGMAN OFF. An owned session is never read (owner ruling,
/// 2026-09-25), and an account whose judge switch is off pays for no naming call. Both are read again after the
/// wait, before the call.</item>
/// <item>CLAUDE CODE SESSIONS A PERSON STARTED. Only Claude Code's conversation is read from a transcript bound to the
/// session itself (<see cref="IsNameableAgent"/>), and a session a schedule or another agent started has a seed the
/// automation wrote, not a person's prompt (<see cref="IsStartedByAPerson"/>).</item>
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

    /// <summary>How many times the conversation is read waiting for the first prompt to land in the transcript.</summary>
    public const int PromptWaitAttempts = 10;

    /// <summary>The wait between those reads.</summary>
    public static readonly TimeSpan PromptWaitInterval = TimeSpan.FromSeconds(2);

    private readonly ISessionNamingEnvironment _env;

    // (tenant, session) pairs that are settled - named, named by someone else, or asked about once - plus any pair
    // whose naming is in flight, so two Working edges never both ask. Bounded by the sessions a Gateway sees.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _attempted = new();

    public SessionNamingService(ISessionNamingEnvironment environment)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>The Working-edge handler: starts the naming and never throws into the watcher that raised it.</summary>
    public void OnSessionWorking(TenantId tenant, string sessionId, string directorId)
        => _ = NameIfUnnamedAsync(tenant, sessionId, directorId);

    /// <summary>
    /// Name the session from the user's first prompt when it has no name a person gave it. Returns the name applied,
    /// or null when none was. Never throws.
    /// </summary>
    public async Task<string?> NameIfUnnamedAsync(TenantId tenant, string sessionId, string directorId,
        CancellationToken ct = default)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return null;
        var key = (tenant, sessionId);
        if (!_attempted.TryAdd(key, 0)) return null;

        var settled = false;
        try
        {
            if (!_env.JudgeEnabled(tenant)) return null;
            var state = _env.ReadSessionState(tenant, sessionId);
            if (state.Held || state.Facts is not { } facts) return null;
            if (!IsUnnamed(facts) || !IsNameableAgent(facts) || !IsStartedByAPerson(facts))
            {
                // A person's or the session's own name, an agent whose conversation cannot be read as its own, or a
                // session no person started: settled for good, never ask again.
                settled = true;
                return null;
            }

            var owner = string.IsNullOrWhiteSpace(facts.DirectorId) ? directorId : facts.DirectorId;
            var firstPrompt = await WaitForFirstPromptAsync(tenant, owner, sessionId, ct).ConfigureAwait(false);
            if (firstPrompt is null)
            {
                FileLog.Write($"[SessionNamingService] no user prompt yet sid={sessionId} tenant={tenant.ToLogString()} - left for the next Working edge");
                return null;
            }

            // The wait can be twenty seconds: the switch and the owner are read again before anything is paid for.
            if (!_env.JudgeEnabled(tenant) || _env.ReadSessionState(tenant, sessionId).Held) return null;

            // From here the session has been asked about: a failed or unusable answer is not paid for twice.
            settled = true;
            var reply = await _env.AskNamerAsync(tenant, BuildPrompt(firstPrompt), NamerTimeout, ct).ConfigureAwait(false);
            var name = CleanName(reply);
            if (name is null)
            {
                FileLog.Write($"[SessionNamingService] no usable name from the model sid={sessionId} " +
                              $"tenant={tenant.ToLogString()} replyLength={reply?.Length ?? 0}");
                return null;
            }

            // The user may have renamed it while the model was answering: their name wins.
            var nowState = _env.ReadSessionState(tenant, sessionId);
            var now = nowState.Facts;
            if (nowState.Held || now is null || !IsUnnamed(now) || !string.Equals(now.Name, facts.Name, StringComparison.Ordinal))
            {
                FileLog.Write($"[SessionNamingService] renamed by someone else while naming, left alone sid={sessionId}");
                return null;
            }

            var target = string.IsNullOrWhiteSpace(now.DirectorId) ? owner : now.DirectorId;
            var error = await _env.RenameSessionAsync(tenant, target, sessionId, name, ct).ConfigureAwait(false);
            if (error is not null)
            {
                FileLog.Write($"[SessionNamingService] rename FAILED sid={sessionId} director={target}: {error}");
                return null;
            }

            FileLog.Write($"[SessionNamingService] named sid={sessionId} tenant={tenant.ToLogString()} name=\"{name}\"");
            return name;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionNamingService] naming FAILED sid={sessionId}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            // Not settled: nothing was asked, so the next Working edge may try again.
            if (!settled) _attempted.TryRemove(key, out _);
        }
    }

    /// <summary>Read the live conversation until the user's first prompt is in it, or the wait runs out.</summary>
    private async Task<string?> WaitForFirstPromptAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= PromptWaitAttempts; attempt++)
        {
            var turns = await _env.ReadTurnsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
            // Only "ok" is a real read; anything else is a failed read that says nothing about the prompt.
            if (turns is { Status: "ok" } && FirstUserPrompt(turns.Widgets) is { } prompt) return prompt;
            if (attempt < PromptWaitAttempts) await _env.DelayAsync(PromptWaitInterval, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// True for Claude Code only: its conversation is read from the transcript file bound to its own session id. The
    /// other agents' readers locate a transcript by repository (Codex takes the newest rollout for the folder, Copilot
    /// and OpenCode the newest conversation, Gemini exposes only a terminal), so with two sessions in one repository
    /// one could be named from the other's words.
    /// </summary>
    public static bool IsNameableAgent(SessionDto facts)
        => string.Equals(facts.Agent, "ClaudeCode", StringComparison.Ordinal);

    /// <summary>
    /// True only for a session a person started (origin "human" - the Director stamps it on every session opened from
    /// the desktop, the Cockpit or the phone). A schedule's or another agent's session carries a seed the automation
    /// wrote, and an unknown origin cannot show that its first prompt came from a person, so neither is named.
    /// </summary>
    public static bool IsStartedByAPerson(SessionDto facts)
        => string.Equals(facts.OriginKind, "human", StringComparison.Ordinal);

    /// <summary>True when the session has no name a person or the session itself gave it.</summary>
    public static bool IsUnnamed(SessionDto facts)
        => string.IsNullOrWhiteSpace(facts.Name) || facts.IsAutoNamed;

    /// <summary>
    /// The first thing the user typed or said in the session, or null when none is stored. A transcript also
    /// records machine text on the user's side - a <c>&lt;system-reminder&gt;</c> block, a slash command's
    /// <c>&lt;command-name&gt;</c> or its output, the "Caveat:" line before local command output, a loaded skill's
    /// body, a task notification, the continuation summary after compaction - and none of it is what the person
    /// asked for. The widgets do not keep the transcript's meta flag, so tagged blocks are cut out and
    /// what is left is passed over when it is empty, the caveat, or what
    /// <see cref="ClaudeSessionReader.IsSystemInjectedContent"/> already knows as injected.
    /// </summary>
    public static string? FirstUserPrompt(IReadOnlyList<TurnWidgetDto>? widgets)
    {
        if (widgets is null) return null;
        foreach (var w in widgets)
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
