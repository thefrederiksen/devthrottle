using System.Runtime.InteropServices;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A FACTORY SESSION STARTS WITH ITS FACTORY'S MEMORY IN PLACE (Factory Memory mission, phase 3a, section 5.4).
///
/// These go through <see cref="SessionManager.CreateSession(string, IAgent, string?, Backends.SessionBackendType, string?, Guid?, string?, string?, Func{Guid, string}?, Guid?, Action{Session}?, PooledWorktree?)"/>
/// itself - the caller - and not through the file writer alone, because the phase 1 review's sharpest finding was
/// that every door could stop applying a rule while tests that call the helper directly stayed green. The factory
/// is stamped the way the Director's create verb stamps it, in <c>beforeLaunch</c>.
///
/// The agent is a real shell that writes its own environment variable to a file and exits, so "the variable is
/// set" is read from the process the Director actually started, not from a dictionary a test was handed.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryMemoryAtLaunchTests : IDisposable
{
    private const string TheFactory = "website-factory";

    private readonly string _root;
    private readonly string _repo;
    private readonly string _memoryRoot;
    private readonly string _slot;
    private readonly List<SessionManager> _managers = new();

    public FactoryMemoryAtLaunchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-fm-launch-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        _memoryRoot = Path.Combine(_root, "storage", "factory-memory");
        _slot = Path.Combine(_root, "repo.worktrees", "wt01");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_slot);
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            try { manager.KillAllSessionsAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
            try { manager.Dispose(); } catch { /* best effort */ }
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; } catch { Thread.Sleep(200); }
        }
    }

    // ---------- the pieces ----------

    /// <summary>An agent that is a shell: it writes the notes-folder variable to <paramref name="outFile"/> and exits.</summary>
    private sealed class EnvEchoAgent : IAgent
    {
        private readonly string _outFile;
        public EnvEchoAgent(string outFile) => _outFile = outFile;
        public AgentKind Kind => AgentKind.RawCli;
        public string ExecutablePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";
        public bool SupportsPreassignedSessionId => false;
        public bool SupportsStudioMode => false;
        public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode) =>
            new(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"/c \"echo [%{FactoryMemoryFiles.DirectoryEnvVar}%]> \"{_outFile}\"\""
                : $"-c 'printf \"[%s]\" \"${FactoryMemoryFiles.DirectoryEnvVar}\" > \"{_outFile}\"'", null);
    }

    /// <summary>A pool that hands out one slot and records what was given back.</summary>
    private sealed class RecordingPool : IWorktreePool
    {
        private readonly PooledWorktree _slot;
        public RecordingPool(PooledWorktree slot) => _slot = slot;
        public List<PooledWorktree> Returns { get; } = new();
        public PooledWorktree Get(string repoPath, string holder, int poolSize) => _slot;
        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            Returns.Add(worktree);
            return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    /// <summary>The Director's download, as a recording: what it was asked for, and what it answers.</summary>
    private sealed class RecordingDownload
    {
        private readonly Func<IReadOnlyList<FactoryMemoryNoteDto>> _answer;
        public RecordingDownload(Func<IReadOnlyList<FactoryMemoryNoteDto>> answer) => _answer = answer;
        public List<(string Factory, Guid SessionId)> Calls { get; } = new();
        public IReadOnlyList<FactoryMemoryNoteDto> Download(string factory, Guid sessionId)
        {
            Calls.Add((factory, sessionId));
            return _answer();
        }
    }

    private static FactoryMemoryNoteDto Note(string name, int version, string text) => new()
    {
        Factory = TheFactory, Name = name, Version = version, Text = text,
        AuthorKind = "session", AuthorId = "7f0c", WrittenAtUtc = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc),
    };

    private SessionManager Manager(RecordingDownload? download, IWorktreePool? pool = null, bool pooled = false)
    {
        var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(_root, "reservations")),
            worktreePool: pool,
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: pooled, PoolSize: 4))
        {
            FactoryMemoryRoot = _memoryRoot,
        };
        if (download is not null)
            manager.FactoryMemoryDownload = download.Download;
        _managers.Add(manager);
        return manager;
    }

    /// <summary>Create the way the Director's create verb does: the factory the Gateway settled, stamped pre-launch.</summary>
    private static Session Create(SessionManager manager, string repo, IAgent agent, string? factory)
        => manager.CreateSession(repo, agent, userArgs: null, Backends.SessionBackendType.ConPty, resumeSessionId: null,
            beforeLaunch: s => s.StampFactory(factory));

    private static string ReadWhatTheAgentSaw(string outFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(outFile))
            {
                try
                {
                    var text = File.ReadAllText(outFile).Trim();
                    if (text.EndsWith(']')) return text;
                }
                catch (IOException) { /* the shell is still writing it */ }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"the agent never wrote {outFile}");
    }

    // ---------- a factory session ----------

    [Fact]
    public void A_FACTORY_SESSION_STARTS_WITH_ITS_NOTES_IN_STORAGE_AND_THE_FOLDER_IN_ITS_ENVIRONMENT()
    {
        var download = new RecordingDownload(() => new[]
        {
            Note("domains", 3, "this registry wants a telephone number\nand a second line"),
            Note("deliverability", 1, "every email failed DMARC until the record was fixed"),
        });
        var manager = Manager(download);
        var outFile = Path.Combine(_root, "env.txt");

        var session = Create(manager, _repo, new EnvEchoAgent(outFile), TheFactory);

        // THE DOWNLOAD WAS ASKED FOR THIS FACTORY, FOR THIS SESSION, ONCE.
        var (factory, sessionId) = Assert.Single(download.Calls);
        Assert.Equal(TheFactory, factory);
        Assert.Equal(session.Id, sessionId);

        // THE NOTES ARE IN STORAGE, KEYED BY THE SESSION, WORD FOR WORD - and nowhere near the checkout.
        var dir = FactoryMemoryFiles.DirectoryFor(session.Id, _memoryRoot);
        Assert.Equal(dir, session.FactoryMemoryDirectory);
        Assert.Equal("this registry wants a telephone number\nand a second line", File.ReadAllText(Path.Combine(dir, "domains.md")));
        Assert.Equal("every email failed DMARC until the record was fixed", File.ReadAllText(Path.Combine(dir, "deliverability.md")));
        var index = File.ReadAllText(Path.Combine(dir, FactoryMemoryFiles.IndexFileName));
        Assert.Contains("# Factory memory: website-factory", index);
        Assert.Contains("| domains | domains.md | 3 |", index);
        Assert.Empty(Directory.GetFileSystemEntries(_repo));

        // THE VERSIONS THE COMMAND LINE WILL SEND BACK are the ones that were downloaded.
        using var versions = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, FactoryMemoryFiles.ReadVersionsFileName)));
        Assert.Equal(TheFactory, versions.RootElement.GetProperty("factory").GetString());
        Assert.Equal(3, versions.RootElement.GetProperty("versions").GetProperty("domains").GetInt32());
        Assert.Equal(1, versions.RootElement.GetProperty("versions").GetProperty("deliverability").GetInt32());

        // THE AGENT PROCESS THE DIRECTOR STARTED SAW THE FOLDER IN ITS ENVIRONMENT.
        Assert.Equal($"[{dir}]", ReadWhatTheAgentSaw(outFile));
    }

    [Fact]
    public void A_factory_with_no_notes_yet_still_gets_its_folder_and_an_index_saying_so()
    {
        var manager = Manager(new RecordingDownload(Array.Empty<FactoryMemoryNoteDto>));
        var outFile = Path.Combine(_root, "env.txt");

        var session = Create(manager, _repo, new EnvEchoAgent(outFile), TheFactory);

        var dir = FactoryMemoryFiles.DirectoryFor(session.Id, _memoryRoot);
        Assert.Contains("This factory has no notes yet.", File.ReadAllText(Path.Combine(dir, FactoryMemoryFiles.IndexFileName)));
        Assert.Equal($"[{dir}]", ReadWhatTheAgentSaw(outFile));
    }

    [Fact]
    public void Removing_a_factory_session_removes_its_copy_of_the_notes()
    {
        var manager = Manager(new RecordingDownload(() => new[] { Note("domains", 1, "x") }));
        var session = Create(manager, _repo, new EnvEchoAgent(Path.Combine(_root, "env.txt")), TheFactory);
        var dir = session.FactoryMemoryDirectory!;
        Assert.True(Directory.Exists(dir));

        Assert.True(manager.RemoveSession(session.Id));

        Assert.False(Directory.Exists(dir));
    }

    // ---------- a session in no factory is untouched ----------

    [Fact]
    public void A_SESSION_IN_NO_FACTORY_DOWNLOADS_NOTHING_WRITES_NOTHING_AND_HAS_NO_VARIABLE()
    {
        var download = new RecordingDownload(() => throw new InvalidOperationException("must not be called"));
        var manager = Manager(download);
        var outFile = Path.Combine(_root, "env.txt");

        var session = Create(manager, _repo, new EnvEchoAgent(outFile), factory: null);

        Assert.Empty(download.Calls);
        Assert.Null(session.Factory);
        Assert.Null(session.FactoryMemoryDirectory);
        Assert.False(Directory.Exists(_memoryRoot));
        Assert.Null(FactoryMemoryFiles.StartupLine(session));
        // The shell prints the variable's name back unexpanded on Windows when it is unset, and nothing elsewhere.
        var seen = ReadWhatTheAgentSaw(outFile);
        Assert.True(seen is "[]" or $"[%{FactoryMemoryFiles.DirectoryEnvVar}%]", $"the agent saw '{seen}'");
    }

    [Fact]
    public void A_session_in_no_factory_starts_even_on_a_Director_with_no_Gateway_to_download_from()
    {
        var manager = Manager(download: null);

        var session = Create(manager, _repo, new EnvEchoAgent(Path.Combine(_root, "env.txt")), factory: null);

        Assert.Contains(session, manager.ListSessions());
    }

    // ---------- the hard stop ----------

    [Fact]
    public void A_FAILED_DOWNLOAD_FAILS_THE_CREATE_WITH_ITS_REASON_AND_GIVES_THE_WORKTREE_SLOT_BACK()
    {
        var download = new RecordingDownload(() =>
            throw new InvalidOperationException("the Gateway refused the download (HTTP 403): not today"));
        var pool = new RecordingPool(new PooledWorktree(_repo, "wt01", _slot, "lease-abc"));
        var manager = Manager(download, pool, pooled: true);
        var keysMinted = 0;
        manager.GatewayUrl = "http://127.0.0.1:1";
        manager.GatewaySessionCredentialSource = _ => { keysMinted++; return "key"; };
        var outFile = Path.Combine(_root, "env.txt");

        var refused = Assert.Throws<FactoryMemoryUnavailableException>(
            () => Create(manager, _repo, new EnvEchoAgent(outFile), TheFactory));

        // IN THE TOOL'S OWN WORDS: which factory, that nothing opened, and the Gateway's reason.
        Assert.Equal(TheFactory, refused.Factory);
        Assert.StartsWith("No session was opened.", refused.Message);
        Assert.Contains("'website-factory'", refused.Message);
        Assert.Contains("the Gateway refused the download (HTTP 403): not today", refused.Message);

        // THE SLOT WENT BACK, NO SESSION EXISTS, NO AGENT RAN, NO KEY WAS MINTED, NO COPY WAS LEFT.
        var returned = Assert.Single(pool.Returns);
        Assert.Equal(_slot, returned.Path);
        Assert.Empty(manager.ListSessions());
        Thread.Sleep(500);
        Assert.False(File.Exists(outFile));
        Assert.Equal(0, keysMinted);
        Assert.False(Directory.Exists(_memoryRoot) && Directory.GetFileSystemEntries(_memoryRoot).Length > 0);
    }

    [Fact]
    public void A_FACTORY_SESSION_ON_A_DIRECTOR_WITH_NO_GATEWAY_DOES_NOT_START()
    {
        var manager = Manager(download: null);
        var outFile = Path.Combine(_root, "env.txt");

        var refused = Assert.Throws<FactoryMemoryUnavailableException>(
            () => Create(manager, _repo, new EnvEchoAgent(outFile), TheFactory));

        Assert.Contains("not connected to a Gateway", refused.Message);
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public void Notes_that_cannot_be_written_fail_the_create_and_say_where()
    {
        // A FILE where the storage folder should be: the download succeeds and the write cannot.
        Directory.CreateDirectory(Path.GetDirectoryName(_memoryRoot)!);
        File.WriteAllText(_memoryRoot, "in the way");
        var manager = Manager(new RecordingDownload(() => new[] { Note("domains", 1, "x") }));

        var refused = Assert.Throws<FactoryMemoryUnavailableException>(
            () => Create(manager, _repo, new EnvEchoAgent(Path.Combine(_root, "env.txt")), TheFactory));

        Assert.Contains("could not be written to", refused.Message);
        Assert.Empty(manager.ListSessions());
    }

    // ---------- the start-up text ----------

    [Fact]
    public void The_start_up_text_of_a_factory_session_says_where_its_notes_are()
    {
        var manager = Manager(new RecordingDownload(() => new[] { Note("domains", 1, "x") }));
        var session = Create(manager, _repo, new EnvEchoAgent(Path.Combine(_root, "env.txt")), TheFactory);

        var rendered = SessionPreambleFile.Render(session, "TESTBOX", user: null,
            store: new InjectedTextStore(Path.Combine(_root, "injected-text")));

        using var doc = JsonDocument.Parse(rendered);
        var text = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("Factory memory: this session belongs to the factory 'website-factory'", text);
        Assert.Contains(session.FactoryMemoryDirectory!, text);
        Assert.Contains(FactoryMemoryFiles.DirectoryEnvVar, text);
    }

    // ---------- file names ----------

    [Theory]
    [InlineData("domains", "domains.md")]
    [InlineData("a/b\\c:d", "a_b_c_d.md")]
    [InlineData("index", "note-index.md")]
    [InlineData("CON", "note-CON.md")]
    [InlineData("../../etc", "etc.md")]
    [InlineData("   ", "note.md")]
    public void A_notes_file_name_is_safe_whatever_the_note_is_called(string name, string expected)
        => Assert.Equal(expected, FactoryMemoryFiles.FileNameFor(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public void Two_notes_whose_names_clean_to_the_same_file_both_keep_their_own()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a_b.md", FactoryMemoryFiles.FileNameFor("a/b", used));
        Assert.Equal("a_b-2.md", FactoryMemoryFiles.FileNameFor("a:b", used));
    }
}
