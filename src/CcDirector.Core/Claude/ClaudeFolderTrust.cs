using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// MARKS A FOLDER THE DIRECTOR MADE FOR A SEAT AS TRUSTED BY CLAUDE CODE, by Claude Code's own record, before the agent
/// starts (Factory Definition mission, phase 5, P7; QA finding 3).
///
/// WHY. Claude Code started in a folder it has never opened asks "Quick safety check: ... Yes, I trust this folder"
/// before anything else, with "No, exit" chosen, and the first prompt waits behind the question. Every start of a seat
/// with no repository works in a NEW kit <c>work/</c> folder and every new pooled slot is a new path, so a scheduled seat
/// that nobody watches never starts its work. The flag --dangerously-skip-permissions does not suppress the question.
///
/// WHERE CLAUDE CODE RECORDS IT, proven on Claude Code 2.1.301 on this machine
/// (docs/qa/factory-definition/phase-5/trust-proof/trust-proof-output.txt):
/// - in its configuration file (<see cref="ConfigPathForThisUser"/>), as
///   <c>projects["&lt;folder, forward slashes&gt;"].hasTrustDialogAccepted = true</c>;
/// - a trusted folder covers every folder beneath it, walking up the parents - but the walk STOPS AT A GIT ROOT. A
///   trusted factory-run root therefore covers every kit's work/ folder, while a trusted pool root does NOT cover a
///   pooled slot (each slot is a git worktree): the slot's own folder has to be recorded. Answering the question inside a
///   worktree records the MAIN CHECKOUT, which this class never does - that folder is not one the Director made.
///
/// THE WRITE IS SAFE AGAINST THE CLAUDE CODE PROCESSES THAT REWRITE THE SAME FILE WHILE THEY RUN. Claude Code takes
/// the lock <c>&lt;file&gt;.lock</c> - a directory, by the proper-lockfile protocol: made with a call that fails when it
/// exists, and renewed while held - then re-reads the file, changes
/// it, writes a temporary file beside it and renames it over the file. This does the same, in the same order: it takes
/// THAT lock, re-reads the file under it, changes only the one entry's <c>hasTrustDialogAccepted</c> (every other key,
/// and every other field of that entry, is kept as read), writes <c>&lt;file&gt;.tmp.&lt;pid&gt;.&lt;random&gt;</c> and
/// renames it over the file, then removes the lock. A Claude Code process waits for the lock, so it never writes between
/// this read and this rename, and its own next write starts from a file that already carries the entry.
///
/// IT DELETES ONLY WHAT IT MADE: the lock it made itself, and its own temporary file if the rename fails. A lock it did
/// not make is never removed, however old - not even one the protocol would call stale; it is waited for, then the start
/// is refused.
///
/// NOTHING IS WRITTEN WHEN THE FOLDER IS ALREADY TRUSTED, so most starts do not take the lock at all. A file that cannot
/// be read, a lock that stays held, or a write that fails REFUSES the seat start in words, rather than starting a seat
/// that would wait forever at the question.
/// </summary>
public sealed class ClaudeFolderTrust
{
    /// <summary>How long a start waits for a held lock before it refuses.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // Claude Code writes JSON.stringify(config, null, 2): two-space indent, "\n" line ends, characters unescaped.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object InProcess = new();

    private readonly TimeProvider _clock;
    private readonly Action<TimeSpan> _pause;

    /// <param name="configPath">Claude Code's configuration file; <see cref="ConfigPathForThisUser"/> in the Director.</param>
    /// <param name="clock">The clock that decides a lock's age and how long a start has waited; the system clock when null.</param>
    /// <param name="pause">How a start waits between looks at a held lock; a real sleep when null.</param>
    public ClaudeFolderTrust(string configPath, TimeProvider? clock = null, Action<TimeSpan>? pause = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Claude Code's configuration file must be named.", nameof(configPath));
        ConfigPath = Path.GetFullPath(configPath);
        _clock = clock ?? TimeProvider.System;
        _pause = pause ?? Thread.Sleep;
    }

    /// <summary>The configuration file this writes.</summary>
    public string ConfigPath { get; }

    /// <summary>The lock Claude Code takes on <see cref="ConfigPath"/>.</summary>
    public string LockPath => ConfigPath + ".lock";

    /// <summary>
    /// The configuration file Claude Code reads for this user, resolved the way Claude Code resolves it: an older
    /// <c>&lt;config folder&gt;/.config.json</c> that is not empty wins; otherwise <c>.claude.json</c> in
    /// <c>CLAUDE_CONFIG_DIR</c> when that is set, else in the home folder. The config folder is <c>CLAUDE_CONFIG_DIR</c>
    /// or <c>~/.claude</c>.
    /// </summary>
    public static string ConfigPathForThisUser(Func<string, string?>? environment = null, string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = environment("CLAUDE_CONFIG_DIR");
        var legacy = Path.Combine(string.IsNullOrWhiteSpace(configDir) ? Path.Combine(home, ".claude") : configDir, ".config.json");
        if (File.Exists(legacy) && new FileInfo(legacy).Length > 0)
            return legacy;
        return Path.Combine(string.IsNullOrWhiteSpace(configDir) ? home : configDir, ".claude.json");
    }

    /// <summary>The key Claude Code files a folder under: its full path with forward slashes and no trailing slash.</summary>
    public static string KeyFor(string folder)
    {
        var full = Path.GetFullPath(folder).Replace('\\', '/');
        return full.Length > 1 && !full.EndsWith(":/", StringComparison.Ordinal) ? full.TrimEnd('/') : full;
    }

    /// <summary>Whether Claude Code's record already holds <paramref name="folder"/> itself as trusted. Reads only.</summary>
    public bool IsRecorded(string folder)
    {
        var key = KeyFor(folder);
        return Projects(Read(folder), folder)[key] is JsonObject entry && IsTrue(entry["hasTrustDialogAccepted"]);
    }

    /// <summary>
    /// Whether Claude Code would start in <paramref name="folder"/> without asking, by its own record: the folder or a
    /// parent up to and including its git root is recorded trusted (a folder in no repository walks every parent), or,
    /// for a linked worktree, its main checkout is - which is what answering the question inside a worktree records.
    /// Proven against Claude Code 2.1.302: a new worktree of a trusted repository starts without the question (delivery
    /// rig, scenario trusted-worktree).
    ///
    /// Reads only. It answers false when there is no record, or when the record cannot be read (logged): a caller acts on
    /// it to carry over a trust the owner GAVE, so anything it cannot prove leaves the agent's own question in place.
    /// </summary>
    public bool Covers(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        if (!File.Exists(ConfigPath))
        {
            FileLog.Write($"[ClaudeFolderTrust] Covers: folder={full}: there is no {ConfigPath}, so nothing is trusted");
            return false;
        }
        JsonObject projects;
        try
        {
            projects = Projects(Read(full), full);
        }
        catch (ClaudeFolderTrustException ex)
        {
            // not-an-error: an unreadable record proves no trust, so the agent's own question stays - the expected answer.
            FileLog.Write($"[ClaudeFolderTrust] Covers: folder={full}: the record could not be read, so no trust is carried over: {ex.Message}");
            return false;
        }

        var candidates = new List<string>();
        if (Git.LinkedWorktree.ParentRepositoryOf(full) is { } mainCheckout)
            candidates.Add(mainCheckout);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            candidates.Add(dir.FullName);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                break;
        }
        var trustedBy = candidates.FirstOrDefault(c => projects[KeyFor(c)] is JsonObject entry && IsTrue(entry["hasTrustDialogAccepted"]));
        FileLog.Write($"[ClaudeFolderTrust] Covers: folder={full}, covered={trustedBy is not null}{(trustedBy is null ? "" : $" (by {KeyFor(trustedBy)})")}");
        return trustedBy is not null;
    }

    /// <summary>
    /// Record <paramref name="folder"/> as trusted, unless it already is. Throws <see cref="ClaudeFolderTrustException"/>,
    /// whose message is the sentence a person acts on, when the record cannot be read or written.
    /// </summary>
    public void Trust(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var key = KeyFor(folder);
        FileLog.Write($"[ClaudeFolderTrust] Trust: folder={key}, config={ConfigPath}");
        lock (InProcess)
        {
            if (IsRecorded(folder))
            {
                FileLog.Write($"[ClaudeFolderTrust] Trust: {key} is already trusted; nothing written");
                return;
            }

            TakeLock(folder);
            try
            {
                // Read again UNDER the lock: a Claude Code process may have rewritten the file since the look above.
                var root = Read(folder);
                var projects = Projects(root, folder);
                if (projects[key] is JsonObject entry)
                {
                    if (IsTrue(entry["hasTrustDialogAccepted"])) return;
                    entry["hasTrustDialogAccepted"] = true;
                }
                else
                {
                    projects[key] = new JsonObject { ["hasTrustDialogAccepted"] = true };
                }
                Replace(root, folder);
                FileLog.Write($"[ClaudeFolderTrust] Trust: recorded {key} as trusted in {ConfigPath}");
            }
            finally
            {
                ReleaseLock();
            }
        }
    }

    private JsonObject Read(string folder)
    {
        if (!File.Exists(ConfigPath))
            throw new ClaudeFolderTrustException(
                $"No seat was started: Claude Code has not been run on this machine as this user (there is no {ConfigPath}), " +
                $"so the seat would stop at Claude Code's first questions in {folder} with nobody to answer them. " +
                "Open Claude Code once on this machine, then start the seat again.");
        string text;
        try
        {
            using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unreadable(folder, ex.Message, ex);
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw Unreadable(folder, $"it is not valid JSON: {ex.Message}", ex);
        }
        return node as JsonObject ?? throw Unreadable(folder, "it does not hold a JSON object", null);
    }

    private JsonObject Projects(JsonObject root, string folder)
    {
        switch (root["projects"])
        {
            case JsonObject projects:
                return projects;
            case null:
                var created = new JsonObject();
                root["projects"] = created;
                return created;
            default:
                throw Unreadable(folder, "its \"projects\" entry is not a JSON object", null);
        }
    }

    private void Replace(JsonObject root, string folder)
    {
        var temporary = $"{ConfigPath}.tmp.{Environment.ProcessId}.{Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 6).ToLowerInvariant()}";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(WriteOptions), new System.Text.UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(ConfigPath));
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new ClaudeFolderTrustException(
                $"No seat was started: the folder {folder} could not be marked trusted, because {ConfigPath} could not be " +
                $"written ({ex.Message}), and the seat would wait at Claude Code's folder-trust question with nobody to " +
                "answer it. Free that file and start the seat again.", ex);
        }
    }

    private void TakeLock(string folder)
    {
        // A LOCK THIS DIRECTOR DID NOT MAKE IS NEVER REMOVED, however old it looks. It is waited for, and when it is still
        // there after LockWait the start is refused. A lock left by a Claude Code that died is cleared by the next
        // running Claude Code's own write, so starting the seat again is the remedy.
        var started = _clock.GetUtcNow();
        while (true)
        {
            if (MakeDirectoryExclusive(LockPath))
                return;
            if (_clock.GetUtcNow() - started >= LockWait)
                throw new ClaudeFolderTrustException(
                    $"No seat was started: the folder {folder} could not be marked trusted, because the lock on {ConfigPath} " +
                    $"that Claude Code takes to write it stayed in place for the {LockWait.TotalSeconds:F0} seconds this " +
                    "Director waited, and the seat would wait at Claude Code's folder-trust question with nobody to answer " +
                    "it. Start the seat again in a moment.");
            _pause(PollInterval);
        }
    }

    /// <summary>Remove the lock - only ever called after <see cref="TakeLock"/> made it, so it is this Director's own.</summary>
    private void ReleaseLock()
    {
        try { Directory.Delete(LockPath, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged, not thrown: the entry is written; a lock left behind is waited for by every taker.
            FileLog.Write($"[ClaudeFolderTrust] ReleaseLock FAILED: {LockPath}: {ex.Message}");
        }
    }

    private ClaudeFolderTrustException Unreadable(string folder, string reason, Exception? inner) =>
        new($"No seat was started: the folder {folder} could not be marked trusted, because {ConfigPath} could not be " +
            $"read ({reason}), and the seat would wait at Claude Code's folder-trust question with nobody to answer it. " +
            "Fix that file, then start the seat again.", inner);

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>
    /// Make a directory with a call that FAILS when it exists - the mutual exclusion the lock protocol rests on.
    /// Directory.CreateDirectory succeeds on an existing folder, so it cannot take a lock.
    /// </summary>
    private static bool MakeDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryW(path, IntPtr.Zero)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists) return false;
            throw new IOException($"The lock {path} could not be made: {new Win32Exception(error).Message}");
        }
        if (mkdir(path, 0x1FF) == 0) return true;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == UnixEexist) return false;
        throw new IOException($"The lock {path} could not be made: error {errno}");
    }

    private const int ErrorAlreadyExists = 183;
    private const int UnixEexist = 17;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string pathName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdir(string path, int mode);
}

/// <summary>A seat's folder could not be marked trusted; the message is the sentence a person acts on.</summary>
public sealed class ClaudeFolderTrustException : InvalidOperationException
{
    public ClaudeFolderTrustException(string message, Exception? inner = null) : base(message, inner) { }
}
