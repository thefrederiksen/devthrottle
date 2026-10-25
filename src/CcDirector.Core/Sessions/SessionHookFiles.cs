using CcDirector.Core.Storage;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: the ONE place that names the two files a session's
/// SessionStart hook uses, and the two environment variables the Director stamps to tell the hook
/// where they are.
///
/// The hook scripts used to call the Director's Control API for both jobs - a GET for the fleet
/// preamble and a POST to report a rotated transcript pointer. Both are now files, so the hook needs
/// no listening port, no address and no credential.
///
/// WHY THE PATHS ARE HANDED OVER RATHER THAN COMPUTED. The scripts are static and shared across every
/// session, and they run under PowerShell on Windows and POSIX shell on macOS and Linux. Working out
/// the storage root in each of those - per platform, and per NAMED INSTANCE, which moves the root -
/// would put a second copy of <see cref="CcStorage"/> into two shell dialects, where it could drift
/// from this one silently. The Director already knows the exact path, so it says so.
/// </summary>
public static class SessionHookFiles
{
    /// <summary>The environment variable naming the session's ready-to-print SessionStart hook output.
    /// A hook that does not see it is not in a Director session and prints nothing.</summary>
    public const string PreambleFileEnvVar = "CC_SESSION_PREAMBLE_FILE";

    /// <summary>The environment variable naming the file a Claude hook writes its current session id
    /// and transcript path into. A hook that does not see it reports nothing.</summary>
    public const string PointerFileEnvVar = "CC_SESSION_POINTER_FILE";

    public const string StateEventDirectoryEnvVar = "CC_STATE_EVENT_DIR";
    public const string StateEventTokenEnvVar = "CC_STATE_EVENT_TOKEN";

    public const string StateEventExtension = ".state.json";

    public static string StateEventDirectoryFor(Guid sessionId, string token, string? directory = null)
    {
        RequireMintedToken(token);
        return Path.Combine(directory ?? CcStorage.SessionPointers(), "state-events", sessionId + "." + token);
    }

    public static string StateEventPathFor(Guid sessionId, string token, long sequence, string? directory = null)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return Path.Combine(StateEventDirectoryFor(sessionId, token, directory), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + StateEventExtension);
    }

    public static bool TryParseStateEventDropName(string path, out Guid sessionId, out string token)
    {
        sessionId = default;
        token = "";
        var name = Path.GetFileName(path);
        if (!name.EndsWith(StateEventExtension, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^StateEventExtension.Length];
        if (!long.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence <= 0) return false;
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return false;
        var folder = Path.GetFileName(parent);
        if (string.IsNullOrEmpty(folder)) return false;
        var dot = folder.LastIndexOf('.');
        if (dot <= 0 || !Guid.TryParse(folder[..dot], out sessionId)) return false;
        token = folder[(dot + 1)..];
        return token.Length == DropTokenLength && token.All(char.IsAsciiHexDigitLower);
    }

    /// <summary>The file extension both drops use, so the pointer watcher can ignore the temporary
    /// file an in-progress atomic write leaves beside the real one.</summary>
    public const string DropExtension = ".json";

    /// <summary>The maintained hook-output file for one session, under the real storage root.</summary>
    public static string PreamblePathFor(Guid sessionId) => PreamblePathFor(sessionId, directory: null);

    /// <summary>Testable overload: the maintained hook-output file under an explicit directory.</summary>
    public static string PreamblePathFor(Guid sessionId, string? directory)
        => Path.Combine(directory ?? CcStorage.SessionPreambles(), sessionId.ToString() + DropExtension);

    /// <summary>The length a drop token must be: 16 random bytes rendered as lowercase hex.</summary>
    public const int DropTokenLength = 32;

    /// <summary>
    /// Mint the unguessable half of a pointer-drop file name: 32 hex characters from a
    /// cryptographic generator. Hex only, so the value can never contain a dot or a path
    /// separator and the name parses unambiguously.
    /// </summary>
    public static string NewDropToken()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The pointer drop file for one session, under the real storage root.</summary>
    public static string PointerPathFor(Guid sessionId, string dropToken)
        => PointerPathFor(sessionId, dropToken, directory: null);

    /// <summary>
    /// Testable overload: the pointer drop file under an explicit directory.
    ///
    /// THE NAME IS ID DOT TOKEN. The id says which session the drop is for; the token proves the
    /// writer was HANDED that session's path rather than spelling its id. The drop box is a shared
    /// same-user directory, so a name anyone can derive is a name anyone can write - the token is
    /// what makes the path a capability instead of an address. It travels in the environment the
    /// Director stamps, exactly where the deleted route's session-bound credential travelled.
    /// </summary>
    public static string PointerPathFor(Guid sessionId, string dropToken, string? directory)
    {
        // Hex-only AND full length, both checked: the two-argument overloads of this method used to
        // take a DIRECTORY second, so a stale caller would otherwise compile cleanly and bake a path
        // into the file name. A token is 32 lowercase hex characters and nothing else.
        //
        // The LENGTH check is not decoration. Cross-family review of this change observed that the
        // prose above promised 32 characters while the check accepted any non-empty hex run, so a
        // one-character token would have been minted as a valid-looking capability. Nothing produces
        // one today - NewDropToken is the only mint - but a contract enforced only by the comment
        // beside it is exactly the defect this mission has spent its life finding.
        if (dropToken.Length != DropTokenLength || !dropToken.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException(
                $"A pointer drop path needs the session's {DropTokenLength}-character lowercase-hex " +
                $"drop token, got: '{dropToken}'.",
                nameof(dropToken));
        return Path.Combine(directory ?? CcStorage.SessionPointers(),
            sessionId.ToString() + "." + dropToken + DropExtension);
    }

    /// <summary>
    /// Split a drop file's name into the session it claims and the token that must prove the claim.
    /// False when the name does not have the id-dot-token shape - including the OLD bare
    /// "<c>id.json</c>" shape, which is exactly what a writer that only knows a session's id can
    /// spell, and is therefore refused rather than grandfathered.
    /// </summary>
    public static bool TryParseDropName(string path, out Guid sessionId, out string dropToken)
    {
        sessionId = default;
        dropToken = "";
        var name = Path.GetFileNameWithoutExtension(path);
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return false;
        if (!Guid.TryParse(name[..dot], out sessionId))
            return false;
        dropToken = name[(dot + 1)..];
        return true;
    }

    /// <summary>Session Rules part 2, phase 1: the environment variable naming the session's maintained
    /// rules file (wire contract W2). The before-a-command hook reads it and decides locally.</summary>
    public const string RulesFileEnvVar = "CC_SESSION_RULES_FILE";

    /// <summary>Session Rules part 2, phase 1: the environment variable naming the directory the
    /// before-a-command hook writes one file per decision into (wire contract W3).</summary>
    public const string DecisionDirEnvVar = "CC_SESSION_DECISION_DIR";

    /// <summary>The name of the folder, inside the pointer drop box, that holds every session's
    /// decision directory.</summary>
    public const string DecisionsFolderName = "decisions";

    /// <summary>The maintained rules file for one session (W2), under the real storage root.</summary>
    public static string RulesPathFor(Guid sessionId) => RulesPathFor(sessionId, directory: null);

    /// <summary>Testable overload: the maintained rules file under an explicit directory.</summary>
    public static string RulesPathFor(Guid sessionId, string? directory)
        => Path.Combine(directory ?? CcStorage.SessionRules(), sessionId.ToString() + DropExtension);

    /// <summary>
    /// The before-a-command hook's evaluation log for one session (W4): beside its rules file, by the
    /// hook's own rule (<c>x/&lt;id&gt;.json</c> logs to <c>x/&lt;id&gt;.eval.log</c>). The hook writes it; the
    /// Director deletes it with the rules file when the session is removed.
    /// </summary>
    public static string EvaluationLogPathFor(Guid sessionId, string? directory)
        => Path.ChangeExtension(RulesPathFor(sessionId, directory), ".eval.log");

    /// <summary>The name of the folder, inside the pointer drop box, where the Director records each
    /// session id and drop token it minted a decision directory for.</summary>
    public const string DecisionRecordsFolderName = "decision-records";

    /// <summary>
    /// The Director's record that it minted one session's decision directory: an empty file named
    /// id dot token, in <c>&lt;pointer drop box&gt;/decision-records</c>. Tokens live only in memory, so
    /// after a restart this record is the only thing that lets the next Director vouch for a leftover
    /// directory and forward the decisions in it. It is written before the directory is created and
    /// removed when the directory is drained.
    /// </summary>
    public static string DecisionRecordFor(Guid sessionId, string dropToken, string? pointerDirectory)
    {
        RequireMintedToken(dropToken);
        return Path.Combine(pointerDirectory ?? CcStorage.SessionPointers(), DecisionRecordsFolderName,
            sessionId.ToString() + "." + dropToken);
    }

    /// <summary>
    /// The folder holding every session's decision directory: <c>&lt;pointer drop box&gt;/decisions</c>.
    /// <paramref name="pointerDirectory"/> is the pointer drop box (tests pin it; null is the real one).
    /// </summary>
    public static string DecisionsRootFor(string? pointerDirectory)
        => Path.Combine(pointerDirectory ?? CcStorage.SessionPointers(), DecisionsFolderName);

    /// <summary>
    /// One session's decision directory (W3): <c>decisions/&lt;id&gt;.&lt;drop token&gt;</c>. The token is the
    /// session's existing pointer-drop token, so the directory name is the same capability the pointer
    /// file already is: only a writer HANDED the path can put a decision where the Director will forward
    /// it. Checked exactly as <see cref="PointerPathFor(Guid, string, string?)"/> checks it.
    /// </summary>
    public static string DecisionDirectoryFor(Guid sessionId, string dropToken, string? pointerDirectory)
    {
        RequireMintedToken(dropToken);
        return Path.Combine(DecisionsRootFor(pointerDirectory), sessionId.ToString() + "." + dropToken);
    }

    private static void RequireMintedToken(string dropToken)
    {
        if (dropToken.Length != DropTokenLength || !dropToken.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException(
                $"A decision directory needs the session's {DropTokenLength}-character lowercase-hex " +
                $"drop token, got: '{dropToken}'.",
                nameof(dropToken));
    }

    /// <summary>
    /// Stamp the two rule-hook variables into a Claude Code session's environment and create the two
    /// folders they name, BEFORE the agent starts: the hook writes into both and must never fail
    /// because a folder is missing - it swallows every error by construction (ruling P10), so a missing
    /// folder would cost the session its decisions in silence.
    ///
    /// The rules FILE is deliberately not written here. It needs the rules the Gateway answers for this
    /// session's machine and repo, and the launch path never waits on the network; until the maintainer
    /// writes it, the hook logs <c>no_rules_file</c>, which is the truth.
    /// </summary>
    public static void StampRuleHookEnvironment(
        IDictionary<string, string> environment, Guid sessionId, string dropToken,
        string? rulesDirectory, string? pointerDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var rulesPath = RulesPathFor(sessionId, rulesDirectory);
        var decisionDirectory = DecisionDirectoryFor(sessionId, dropToken, pointerDirectory);
        var record = DecisionRecordFor(sessionId, dropToken, pointerDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(rulesPath)!);
        // The record FIRST: a directory that exists without one is, by construction, not this
        // Director's, so a start-up clean-up can never mistake a session being launched for a leftover.
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, "");
        Directory.CreateDirectory(decisionDirectory);
        environment[RulesFileEnvVar] = rulesPath;
        environment[DecisionDirEnvVar] = decisionDirectory;
    }

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> so a reader never sees half of it:
    /// into a sibling temporary file first, then a single move over the destination. The temporary
    /// name deliberately does NOT end in <see cref="DropExtension"/>, so a directory watcher filtering
    /// on that extension never sees the half-written copy at all.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {path}");
        Directory.CreateDirectory(dir);
        var tmp = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
