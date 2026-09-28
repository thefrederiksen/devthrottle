using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A REAL AGENT USES ITS FACTORY'S MEMORY (the Factory Memory mission, phase 4, the live run the owner asked for:
/// "a report that shows the memory being used by a factory agent"). Not part of any suite run: it starts two real
/// Claude Code sessions, costs model time, and takes several minutes. It runs only when
/// <c>CC_FACTORY_MEMORY_AGENT_E2E_OUT</c> names a directory, where it writes its evidence.
///
/// <see cref="FactoryMemoryEndToEndProof"/> proved the Gateway calls with a stand-in command. This one proves the
/// part no stand-in can: a MODEL, given a factory's memory and a job, reads the memory, answers from it, decides on
/// its own that it learned something, writes it with the command line, and a later agent of the same factory
/// starts with that lesson already in its folder.
///
/// WHAT IS REAL. A Gateway host with AUTHENTICATION ENFORCED. A Director host (<see cref="ControlApiHost"/>) that
/// is given a Gateway in its own configuration file and so wires ITSELF exactly as an installed Director does: its
/// own Gateway client (which is what downloads a factory session's notes before the agent starts), its own tunnel,
/// and its own minting and registration of every session's key. The two agent sessions are started by FACTORY
/// SCHEDULES - a schedule naming <c>website-factory</c>, fired with the Gateway's own run-now route - so each agent
/// is born the way a factory agent is born, with the schedule's seed as its first prompt. The agents run the
/// branch's own <c>cc-devthrottle</c>, put first on every session's PATH through the Director's own tool folder.
/// The Website Factory's LESSONS.md is loaded as the factory's first notes by the mission's importer, run INSIDE a
/// session of the factory, because the Gateway reads the factory from the caller's key.
///
/// WHAT IS NOT REAL: the desktop window, the installed Director, and the hosted Gateway. The schedules are fired
/// with run-now rather than waiting for their clock, which is the same engine and the same session starter.
///
/// Required environment:
///   CC_FACTORY_MEMORY_AGENT_E2E_OUT       the evidence directory
///   CC_FACTORY_MEMORY_AGENT_E2E_TOOL      the branch's cc-devthrottle executable
///   CC_FACTORY_MEMORY_AGENT_E2E_CLAUDE    the Claude Code executable
///   CC_FACTORY_MEMORY_AGENT_E2E_LESSONS   the Website Factory's LESSONS.md
///   CC_FACTORY_MEMORY_AGENT_E2E_IMPORTER  the mission's import-lessons.py
///   CC_FACTORY_MEMORY_AGENT_E2E_PYTHON    a Python 3.11 or later to run the importer
///   CC_FACTORY_MEMORY_AGENT_E2E_REPO      the folder the agents work in: must not exist yet, and must be a folder
///                                         Claude Code already trusts (a child of a trusted folder), because a
///                                         folder-trust question on the first screen would swallow the seed prompt
/// </summary>
[Trait("Category", "FactoryMemory")]
[Collection("DirectorRoot")]
public sealed class FactoryMemoryAgentEndToEndProof : IAsyncLifetime
{
    private sealed class ProofFactAttribute : FactAttribute
    {
        public ProofFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OutVariable)))
                Skip = $"Live proof with real agents; set {OutVariable} and the other CC_FACTORY_MEMORY_AGENT_E2E_* variables to run it.";
        }
    }

    private const string OutVariable = "CC_FACTORY_MEMORY_AGENT_E2E_OUT";
    private const string Token = "factory-memory-agent-proof-token";
    private const string DirectorId = "factory-memory-agent-proof-director";
    private const string TheFactory = "website-factory";
    private const string Prefix = "/factory-memory";

    /// <summary>How long one agent turn may take before the case is called failed.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly string _out = Environment.GetEnvironmentVariable(OutVariable) ?? "";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-fm-agent-proof-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly ConcurrentQueue<string> _timeline = new();

    private GatewayHost _gateway = null!;
    private SessionManager _sessions = null!;
    private ControlApiHost _director = null!;
    private string _repo = "";
    private string _personKey = "";
    private string _machine = "";
    private string? _loaderId;

    private readonly List<(string Number, string Name, string Verdict, string? Why)> _cases = new();
    private StringBuilder _transcript = new();

    // The product's own log. A test process never starts the log writer, so the proof starts an isolated one (never
    // the owner's log directory) and records every line, so the Director's own account of the download and of the
    // first-prompt delivery goes into the evidence beside the agents' words.
    private FileLog.FileLogTestScope? _logScope;
    private TextWriter _console = Console.Out;
    private static readonly ConcurrentQueue<string> LogLines = new();

    /// <summary>Records the product's log lines and keeps them off the console; the proof's own notes go straight
    /// to the original console.</summary>
    private sealed class RecordingWriter(Encoding encoding) : TextWriter
    {
        public override Encoding Encoding => encoding;
        public override void WriteLine(string? value)
        {
            if (value is not null) LogLines.Enqueue(value);
        }
        public override void Write(char value) { }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string Required(string suffix)
    {
        var name = "CC_FACTORY_MEMORY_AGENT_E2E_" + suffix;
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must be set to run this proof (see the class comment)");
        return value;
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_out)) return;
        Directory.CreateDirectory(_out);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _logScope = FileLog.RedirectForTests();
        _console = Console.Out;
        Console.SetOut(new RecordingWriter(_console.Encoding));
        FileLog.MirrorToConsole = true;

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_root, "gw-instances"),
            workListsPath: Path.Combine(_root, "gw-instances", "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        // The Gateway switches the mirror off once it is listening; the proof reads the log from the mirror.
        FileLog.MirrorToConsole = true;
        var url = $"http://127.0.0.1:{_gateway.Port}";
        Note($"gateway listening at {url} with authentication ENFORCED");

        _personKey = _gateway.Devices.Register("fm-agent-proof-cockpit", "proof-pc", "windows", "browser").DeviceKey;
        Note("a person's device key enrolled (device type 'browser' - the Cockpit)");

        // THE DIRECTOR IS GIVEN A GATEWAY THE WAY AN INSTALLED ONE IS: in its own configuration file, before it
        // starts. It then builds its own Gateway client, tunnel and session-key minting - nothing below wires them.
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        File.WriteAllText(CcStorage.ConfigJson(),
            JsonSerializer.Serialize(new { gateway = new { url, token = Token, streamMode = true } }));
        Note($"the Director's configuration file points it at this Gateway: {CcStorage.ConfigJson()}");

        // The Director puts its tool folder first on every session's PATH. Put the branch's command line there.
        var bin = CcStorage.Bin();
        Directory.CreateDirectory(bin);
        var tool = Required("TOOL");
        // BOTH forms, as the installed tool folder has them. The bare-name script is the one Claude Code's Bash tool
        // runs on Windows: Git Bash does not resolve a .cmd through PATHEXT, so with only the .cmd here it walked on
        // down PATH to the machine's INSTALLED cc-devthrottle, which has no 'factory memory' - the first live run of
        // this proof lost case 04 to exactly that, with the agent reporting the command did not exist.
        var shim = Path.Combine(bin, "cc-devthrottle");
        File.WriteAllText(shim, $"#!/bin/sh\nexec \"{tool.Replace('\\', '/')}\" \"$@\"\n");
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Path.Combine(bin, "cc-devthrottle.cmd"), $"@echo off\r\n\"{tool}\" %*\r\n");
        else
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Note($"the branch's cc-devthrottle ({tool}) is first on every session's PATH through {bin}");

        _repo = Required("REPO");
        if (Directory.Exists(_repo))
            throw new InvalidOperationException($"{_repo} already exists; the proof makes it fresh so nothing in it is left over");
        Directory.CreateDirectory(_repo);
        System.Diagnostics.Process.Start("git", $"-C \"{_repo}\" init -q")!.WaitForExit();
        File.WriteAllText(Path.Combine(_repo, "scout-run-log.txt"), ScoutRunLog);

        // --dangerously-skip-permissions is how the owner's Directors run Claude Code; without it the agent's first
        // command would stop on a permission question nobody is there to answer.
        _sessions = new SessionManager(new AgentOptions
        {
            ClaudePath = Required("CLAUDE"),
            DefaultClaudeArgs = "--dangerously-skip-permissions",
        });
        _director = new ControlApiHost(_sessions, "fm-agent-proof", () => Task.CompletedTask,
            directorId: DirectorId, instancesDirectory: Path.Combine(_root, "dir-instances"));
        await _director.StartAsync();

        await WaitUntil(() => _gateway.PushedSessions.GetActiveConnectionId(TenantId.Local, DirectorId) is not null,
            TimeSpan.FromSeconds(60), "the Director's own tunnel to connect");
        await WaitUntil(() => _gateway.Registry.ListDirectors(TenantId.Local).Any(d => d.DirectorId == DirectorId),
            TimeSpan.FromSeconds(60), "the Director's own registration");
        _machine = _gateway.Registry.ListDirectors(TenantId.Local).Single(d => d.DirectorId == DirectorId).MachineName;
        Note($"director registered itself on machine '{_machine}' and its tunnel is connected");
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_out)) return;
        try
        {
            foreach (var s in _sessions.ListSessions())
                await _sessions.KillSessionAsync(s.Id);
        }
        catch (Exception ex) { Note($"killing the proof's sessions failed: {ex.Message}"); }
        await _director.StopAsync();
        _sessions.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        File.WriteAllLines(Path.Combine(_out, "timeline.txt"), _timeline);
        // The product's own lines about the things this proof is about, in order. Everything else it logged is
        // left out: it is the whole Gateway and Director, thousands of lines about unrelated machinery.
        File.WriteAllLines(Path.Combine(_out, "product-log-lines.txt"), LogLines.Where(l =>
            l.Contains("FactoryMemory", StringComparison.Ordinal) || l.Contains("PrePrompt", StringComparison.Ordinal)
            || l.Contains("FirstPrompt", StringComparison.Ordinal) || l.Contains("[DirectorCronSessionStarter]", StringComparison.Ordinal)
            || l.Contains("[CronEngine]", StringComparison.Ordinal)));
        FileLog.MirrorToConsole = false;
        Console.SetOut(_console);
        _logScope?.Dispose();
    }

    // ================================================================================================
    // What the agents are given. Written out here so the transcripts can quote them exactly.
    // ================================================================================================

    /// <summary>
    /// Today's run log, left in the agents' folder. It holds a lesson the factory's memory does NOT hold (the memory
    /// says to check that an email domain EXISTS; this says a domain that exists can still have no mail server), so
    /// an agent that writes it down is recording something it learned in this run, not copying the memory.
    /// </summary>
    private const string ScoutRunLog =
        "Scout run log - gutter installers around Knoxville, TN - 2026-09-28\n" +
        "\n" +
        "Maps scan: 118 cards, 26 with no website, 4 Facebook-only.\n" +
        "Checked the email on each of the 4 Facebook pages before drafting:\n" +
        "  - 1 on gmail.com: fine.\n" +
        "  - 3 on the business's own domain. All 3 domains resolve and show a parked page, so the\n" +
        "    'does the domain exist' check passed for all 3.\n" +
        "  - But 2 of those 3 domains have NO MX record. A test message to each bounced at once:\n" +
        "    '550 5.1.2 no mail exchanger for domain'.\n" +
        "So a domain that resolves is not enough: check for an MX record before writing to an address\n" +
        "on the business's own domain. Two of our four leads would have bounced.\n";

    private const string ScoutSeed =
        "You are the Website Factory's Scout for this run. Do each step yourself and answer in plain text. " +
        "Step 1: print the value of the environment variable CC_FACTORY_MEMORY_DIR, and list the files in that folder. " +
        "Step 2: answer this from your factory's memory only, and name the note file you found the answer in: in the " +
        "factory's second run, which three businesses were built, and what did the blind phone reviewers score each " +
        "one, out of 40? " +
        "Step 3: this folder holds today's run log, scout-run-log.txt. Read it. If it teaches the factory something " +
        "its memory does not already hold, write that lesson into the factory's memory yourself, so the next run " +
        "starts with it - one idea per note, using the command the memory's index.md describes. Decide for yourself " +
        "whether to write and what to name the note. Then say what you wrote, or why you wrote nothing.";

    private const string SenderSeed =
        "You are the Website Factory's Sender for this run. Answer in plain text. " +
        "Step 1: print the value of the environment variable CC_FACTORY_MEMORY_DIR, and list the files in that folder. " +
        "Step 2: before you draft any email you must know what the factory has learned about checking a business's " +
        "email address or its domain before writing to it. From your factory's memory only, give me every such " +
        "lesson, quoting each one word for word, and for each say which note it is in and who wrote that note " +
        "(index.md records the writer). Do not write to the memory in this run.";

    // ================================================================================================
    // The run. One story: each case rests on the one before it, and a case that fails does not stop the
    // ones after it from being tried and recorded.
    // ================================================================================================

    [ProofFact]
    public async Task A_real_factory_agent_reads_and_writes_its_factory_memory_and_a_later_agent_starts_with_it()
    {
        Guid scoutId = Guid.Empty;
        string? scoutNote = null;
        string? scoutNoteText = null;

        await RunCaseAsync("01", "the factory's first notes are LESSONS.md, loaded by a session of the factory", async () =>
        {
            Say("A person starts a stand-in session INTO the factory (the Cockpit's spawn, a person's device key). The " +
                "importer names no factory - the Gateway reads it from the calling session's key - so it has to run " +
                "inside a session of the factory. It is typed into that session's own terminal.");
            var (status, body) = await CallAsync(_personKey, HttpMethod.Post, $"/directors/{DirectorId}/sessions", new Dictionary<string, object?>
            {
                ["repoPath"] = _repo,
                ["agent"] = "RawCli",
                ["command"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                ["name"] = "Website Factory - Loader",
                ["factory"] = TheFactory,
            }, showOnly: BirthFacts);
            Assert.Equal(HttpStatusCode.Created, status);
            var loader = _sessions.GetSession(Guid.Parse(Read<SessionDto>(body).SessionId))!;
            _loaderId = loader.Id.ToString();
            Say("The loader's own notes folder, as the Director put it in place before the shell started. The factory " +
                "has no notes yet.");
            Append(Listing(loader));

            var python = Required("PYTHON");
            var importer = Required("IMPORTER");
            var lessons = Required("LESSONS");
            var dry = Path.Combine(_out, "01-importer-dry-run.txt");
            var write = Path.Combine(_out, "01-importer-write.txt");
            var line = $"\"{python}\" \"{importer}\" \"{lessons}\" > \"{dry}\" 2>&1 && " +
                       $"\"{python}\" \"{importer}\" \"{lessons}\" --write > \"{write}\" 2>&1\r";
            Say("Typed into the loader session's terminal (its output goes to the two files named in it, which are " +
                "copied below):");
            Append("  " + line.TrimEnd('\r'));
            loader.SendInput(Encoding.UTF8.GetBytes(line), null, SubmissionProvenance.FrameworkText());
            await WaitUntil(() => File.Exists(write) && ReadShared(write) is var t &&
                                  (t.Contains("notes written.") || t.Contains("STOPPED") || t.Contains("needed to write")),
                TimeSpan.FromMinutes(2), "the importer to finish inside the loader session");
            await Task.Delay(500);
            Append("--- the importer's dry run ---\n" + ReadShared(dry));
            Append("--- the importer's write ---\n" + ReadShared(write));
            Assert.Contains("notes written.", ReadShared(write));

            Say("The Gateway's own answer, read by a person: the factory's notes as they now stand.");
            var (listStatus, listBody) = await CallAsync(_personKey, HttpMethod.Get, $"{Prefix}/notes?factory={TheFactory}",
                summariseResponse: ListSummary);
            Assert.Equal(HttpStatusCode.OK, listStatus);
            var list = Read<FactoryMemoryListResponse>(listBody);
            Assert.NotEmpty(list.Notes);
            Assert.All(list.Notes, n => Assert.Equal(loader.Id.ToString(), n.AuthorId));
            Say($"{list.Notes.Count} notes, every one authored by the loader session {loader.Id}.");
        });

        await RunCaseAsync("02", "a factory SCHEDULE starts a real agent, and its memory is in place before its first turn", async () =>
        {
            var job = await CreateScheduleAsync("Website Factory - Scout", ScoutSeed);
            var scout = await RunScheduleAsync(job, "the Scout");
            scoutId = scout.Id;
            Assert.Equal(TheFactory, scout.Factory);

            Say("THE MOMENT THE CREATE RETURNED - the agent is launched but has not been given its first prompt. The " +
                "folder, as the Director put it before the agent process started:");
            var bornAt = DateTime.UtcNow;
            Append(Listing(scout));
            Assert.NotNull(scout.FactoryMemoryDirectory);
            Assert.True(File.Exists(Path.Combine(scout.FactoryMemoryDirectory!, "index.md")));

            await AnswerTrustAsync(scout, "the Scout");
            await WaitForTurnAsync(scout, "the Scout", ScoutSeed);
            var firstPrompt = FirstPromptAt(scout, ScoutSeed);
            Say($"The folder was listed at {bornAt:HH:mm:ss.fff} UTC. The agent's own conversation record shows its first " +
                $"prompt arriving at {firstPrompt:HH:mm:ss.fff} UTC - {(firstPrompt - bornAt).TotalSeconds:0.0} seconds LATER.");
            Assert.True(firstPrompt > bornAt, "the notes must be in place before the agent's first prompt");

            var record = SaveConversation(scout, "scout");
            Say("From INSIDE the session: the variable and the listing, in the agent's own tool calls and their " +
                "output (the whole conversation is in agents/scout-conversation.txt).");
            Append(Excerpt(record, "CC_FACTORY_MEMORY_DIR"));
            Assert.Contains(scout.FactoryMemoryDirectory!, record.Replace("/", "\\"), StringComparison.OrdinalIgnoreCase);
        });

        await RunCaseAsync("03", "the agent READS a note and answers from it", async () =>
        {
            Assert.NotEqual(Guid.Empty, scoutId);
            Say("The question was asked in the seed prompt (quoted in case 02's schedule). Its answer is only in the " +
                "factory's memory: the agents' folder holds nothing but an empty git repository and today's run log.");
            var answer = AgentWords(_sessions.GetSession(scoutId)!);
            Append("--- the agent's own words, verbatim, every text reply of its turn ---\n" + answer);
            Say("The facts the memory holds (the note 'second-run-concrete-contractors'): Gartman Earthworx, Concrete " +
                "Comfort & Construction, South-East Concrete Construction; blind phone reviewers 35, 34, 34 of 40.");
            Assert.Contains("Gartman", answer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("35", answer);
            Assert.Contains("34", answer);
        });

        await RunCaseAsync("04", "the agent WRITES a note itself, with the command line, and the note carries its session", async () =>
        {
            Assert.NotEqual(Guid.Empty, scoutId);
            var scout = _sessions.GetSession(scoutId)!;
            var record = File.ReadAllText(Path.Combine(_out, "agents", "scout-conversation.txt"));
            Say("The agent's own command, as it ran it (from its conversation record):");
            Append(Excerpt(record, "factory memory set"));
            Assert.Contains("factory memory set", record);

            Say("The Gateway's own rows for every note version this session wrote, read straight out of its database:");
            var rows = NoteRows().Where(r => r.AuthorId == scoutId.ToString()).ToList();
            foreach (var r in rows)
                Append($"  factory={r.Factory} name={r.Name} version={r.Version} deleted={r.Deleted} " +
                       $"author={r.AuthorKind} {r.AuthorId} at={r.WrittenAtUtc:o}\n  text:\n{Indent(r.Text)}");
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal("session", r.AuthorKind));
            var latest = rows.OrderByDescending(r => r.WrittenAtUtc).First();
            scoutNote = latest.Name;
            scoutNoteText = latest.Text;
            Say($"The note '{scoutNote}' is authored by session {scoutId} - the Scout, which nobody told what to write.");

            Say("The Scout's folder at the end of its turn (the command line keeps .read-versions.json current):");
            Append(Listing(scout));
        });

        await RunCaseAsync("05", "a LATER, separate agent of the factory starts with that note already in its folder", async () =>
        {
            Assert.NotNull(scoutNote);
            Say("The Scout is ended first. What it learned is no longer in any running session - only in the factory.");
            var ended = _sessions.GetSession(scoutId)!;
            await _sessions.KillSessionAsync(scoutId);
            // A killed session stays in the Director's list, marked exited, until it is removed; what matters here is
            // that the agent process is gone.
            await WaitUntil(() => ended.ActivityState == ActivityState.Exited, TimeSpan.FromSeconds(60),
                "the Scout's agent process to exit");
            Append($"[the Director's record] the Scout {scoutId}: ActivityState = {ended.ActivityState}");
            Note($"the Scout {scoutId} was killed and has exited");

            var job = await CreateScheduleAsync("Website Factory - Sender", SenderSeed);
            var sender = await RunScheduleAsync(job, "the Sender");
            Assert.NotEqual(scoutId, sender.Id);

            Say("THE MOMENT THE CREATE RETURNED, before the Sender's first prompt:");
            var bornAt = DateTime.UtcNow;
            Append(Listing(sender));
            var index = File.ReadAllText(Path.Combine(sender.FactoryMemoryDirectory!, "index.md"));
            Assert.Contains(scoutId.ToString(), index);
            var placed = File.ReadAllText(Path.Combine(sender.FactoryMemoryDirectory!, FileOf(index, scoutNote!)));
            Assert.Equal(scoutNoteText, placed);
            Say($"The file for '{scoutNote}' is in the Sender's folder, word for word what the Scout wrote, and index.md " +
                $"names the Scout's session {scoutId} as its writer.");

            await AnswerTrustAsync(sender, "the Sender");
            await WaitForTurnAsync(sender, "the Sender", SenderSeed);
            var firstPrompt = FirstPromptAt(sender, SenderSeed);
            Say($"Listed at {bornAt:HH:mm:ss.fff} UTC; the Sender's first prompt arrived at {firstPrompt:HH:mm:ss.fff} UTC.");
            Assert.True(firstPrompt > bornAt);

            SaveConversation(sender, "sender");
            var answer = AgentWords(sender);
            Append("--- the Sender's own words, verbatim ---\n" + answer);
            Assert.Contains("MX", answer);

            Say("And the Sender wrote nothing, as it was told:");
            Assert.DoesNotContain(NoteRows(), r => r.AuthorId == sender.Id.ToString());
            Append("  no note version is authored by the Sender's session.");
        });

        WriteGatewayRows();
        WriteSummary();
        var failed = _cases.Where(c => c.Verdict != "PASS").ToList();
        Assert.True(failed.Count == 0,
            "these cases did not stand up: " + string.Join("; ", failed.Select(f => $"{f.Number} {f.Name}: {f.Why}")));
    }

    // ------------------------------------------------------------------------------------------------
    // Schedules
    // ------------------------------------------------------------------------------------------------

    private async Task<CronJobDto> CreateScheduleAsync(string name, string seed)
    {
        Say($"A person creates the factory's schedule '{name}', naming the factory. It is set for a date far off; " +
            "it is fired below with the Gateway's run-now route, which is the same engine and the same session starter.");
        var (status, body) = await CallAsync(_personKey, HttpMethod.Post, "/cron/jobs", new
        {
            name,
            scheduleKind = "recurring",
            cronExpression = "0 5 1 1 *",
            timeZoneId = "UTC",
            factory = TheFactory,
            target = new { machine = _machine },
            action = new { repoPath = _repo, seed, autoDismiss = false },
        });
        Assert.Equal(HttpStatusCode.Created, status);
        var job = Read<CronJobDto>(body);
        Assert.Equal(TheFactory, job.Factory);
        return job;
    }

    private async Task<Session> RunScheduleAsync(CronJobDto job, string who)
    {
        var (status, body) = await CallAsync(_personKey, HttpMethod.Post, $"/cron/jobs/{job.Id}/run");
        Assert.Equal(HttpStatusCode.OK, status);
        var run = Read<CronRunRecord>(body);
        Assert.False(string.IsNullOrEmpty(run.SessionId), $"the schedule's run started no session: {body}");
        var session = _sessions.GetSession(Guid.Parse(run.SessionId!))
            ?? throw new InvalidOperationException($"the Director has no session {run.SessionId}");
        Note($"{who}: schedule {job.Id} started session {session.Id} ({session.CustomName}), factory {session.Factory}");
        return session;
    }

    /// <summary>
    /// Claude Code asks, the first time it opens a folder, whether the folder is trusted - and a trusted PARENT does
    /// not carry down to a new child, which the first run of this proof found. The Director does not pre-answer it;
    /// its first-prompt gate waits for a composer that reads input, so the seed is held, not lost, while the question
    /// is on the screen. The proof answers it the way a person does, once per folder, and records that it did.
    /// </summary>
    private async Task AnswerTrustAsync(Session s, string who)
    {
        const string question = "Yes, I trust this folder";
        const string selected = "❯ Yes, I trust this folder";
        // A folder already answered for shows no question; give the agent's first screen time to draw either way.
        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until && !Screen(s).Contains(question))
            await Task.Delay(500);
        if (!Screen(s).Contains(question))
        {
            Say($"{who} opened without a folder-trust question.");
            return;
        }
        for (var attempt = 0; attempt < 20 && Screen(s).Contains(question); attempt++)
        {
            await Task.Delay(1000);
            if (!Screen(s).Contains(selected))
            {
                s.SendInput(Encoding.UTF8.GetBytes("\x1b[B"), null, SubmissionProvenance.FrameworkText());
                continue;
            }
            s.SendInput(Encoding.UTF8.GetBytes("\r"), null, SubmissionProvenance.FrameworkText());
        }
        Assert.DoesNotContain(question, Screen(s));
        Say($"Claude Code asked whether the folder is trusted before {who}'s first prompt; the proof answered 'Yes, I " +
            "trust this folder' as a person would. The seed was held by the Director's first-prompt gate meanwhile.");
        Note($"{who}: answered the folder-trust question");
    }

    private static string Screen(Session s) => string.Join("\n", s.SnapshotScreenRows());

    // ------------------------------------------------------------------------------------------------
    // The agent's own record: Claude Code's conversation file, which the proof reads and never writes.
    // ------------------------------------------------------------------------------------------------

    private static string? ConversationFile(Session s)
    {
        if (string.IsNullOrEmpty(s.ClaudeSessionId)) return null;
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        return Directory.EnumerateFiles(projects, s.ClaudeSessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static List<JsonElement> Entries(Session s)
    {
        var file = ConversationFile(s);
        if (file is null) return new List<JsonElement>();
        var list = new List<JsonElement>();
        foreach (var line in ReadShared(file).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); }
            catch (JsonException) { /* a line still being written */ }
        }
        return list;
    }

    private static string TypeOf(JsonElement e) => e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static DateTime TimeOf(JsonElement e) =>
        e.TryGetProperty("timestamp", out var t) && DateTime.TryParse(t.GetString(), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.MinValue;

    /// <summary>The seed's entry in the record, matched on words that carry no punctuation, so no JSON escaping can
    /// hide it.</summary>
    private static bool IsTheSeed(JsonElement e, string seed) =>
        TypeOf(e) == "user" && e.GetRawText().Contains(SeedMarker(seed), StringComparison.Ordinal);

    private static string SeedMarker(string seed) => seed == ScoutSeed ? "Scout for this run" : "Sender for this run";

    private static DateTime FirstPromptAt(Session s, string seed) =>
        TimeOf(Entries(s).First(e => IsTheSeed(e, seed)));

    /// <summary>Waits until the agent's own record shows its reply to the seed finished (stop reason end_turn).</summary>
    private async Task WaitForTurnAsync(Session s, string who, string seed)
    {
        Note($"{who}: waiting for its turn to finish (at most {TurnTimeout.TotalMinutes:0} minutes)");
        try
        {
            await WaitUntil(() =>
            {
                var entries = Entries(s);
                var seedAt = entries.FindIndex(e => IsTheSeed(e, seed));
                if (seedAt < 0) return false;
                return entries.Skip(seedAt + 1).Any(e => TypeOf(e) == "assistant"
                    && e.TryGetProperty("message", out var m) && m.TryGetProperty("stop_reason", out var r)
                    && r.GetString() == "end_turn");
            }, TurnTimeout, $"{who}'s turn to finish");
        }
        finally
        {
            Directory.CreateDirectory(Path.Combine(_out, "agents"));
            File.WriteAllText(Path.Combine(_out, "agents", $"{Slug(who)}-screen.txt"),
                $"# {who} - session {s.Id} at {DateTime.UtcNow:o}, state {s.ActivityState}\n" +
                string.Join("\n", s.SnapshotScreenRows().Select(r => "|" + r)));
        }
        Note($"{who}: turn finished");
    }

    /// <summary>The whole conversation, rendered readably and saved with the raw record beside it. Returns the rendering.</summary>
    private string SaveConversation(Session s, string name)
    {
        var dir = Path.Combine(_out, "agents");
        Directory.CreateDirectory(dir);
        var file = ConversationFile(s) ?? throw new InvalidOperationException($"no conversation record for {s.Id}");
        // The raw record keeps only the conversation itself - the prompts, the agent's messages, its commands and their
        // output. The file also holds the context Claude Code loaded (the machine owner's own instruction files), which
        // is not evidence of anything here and can carry credentials, so those entries are never copied.
        File.WriteAllLines(Path.Combine(dir, $"{name}-conversation.jsonl"),
            ReadShared(file).Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && IsConversationLine(l)));
        var sb = new StringBuilder();
        sb.AppendLine($"# {name}: session {s.Id}, factory {s.Factory}, Claude Code conversation {s.ClaudeSessionId}");
        sb.AppendLine("# Rendered from the agent's own conversation record (the .jsonl beside this file), nothing paraphrased.");
        sb.AppendLine();
        foreach (var e in Entries(s))
        {
            var type = TypeOf(e);
            if (type is not ("user" or "assistant") || !e.TryGetProperty("message", out var m)) continue;
            var at = TimeOf(e).ToString("HH:mm:ss");
            if (!m.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"[{at}] {(type == "user" ? "PROMPT" : "AGENT")}:").AppendLine(Indent(content.GetString())).AppendLine();
                continue;
            }
            foreach (var block in content.EnumerateArray())
            {
                var kind = block.TryGetProperty("type", out var k) ? k.GetString() : "";
                switch (kind)
                {
                    case "text":
                        sb.AppendLine($"[{at}] {(type == "user" ? "PROMPT" : "AGENT")}:").AppendLine(Indent(block.GetProperty("text").GetString())).AppendLine();
                        break;
                    case "tool_use":
                        sb.AppendLine($"[{at}] AGENT RUNS {block.GetProperty("name").GetString()}:")
                          .AppendLine(Indent(block.GetProperty("input").GetRawText())).AppendLine();
                        break;
                    case "tool_result":
                        var result = block.TryGetProperty("content", out var c)
                            ? c.ValueKind == JsonValueKind.String ? c.GetString()
                              : string.Join("\n", c.EnumerateArray().Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : x.GetRawText()))
                            : "";
                        sb.AppendLine($"[{at}] OUTPUT:").AppendLine(Indent(result)).AppendLine();
                        break;
                }
            }
        }
        var text = sb.ToString();
        File.WriteAllText(Path.Combine(dir, $"{name}-conversation.txt"), text);
        Note($"saved {name}'s conversation ({text.Length} characters)");
        return text;
    }

    private static bool IsConversationLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return TypeOf(doc.RootElement) is "user" or "assistant";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Every text the agent wrote in its turn, joined, verbatim.</summary>
    private static string AgentWords(Session s)
    {
        var sb = new StringBuilder();
        foreach (var e in Entries(s).Where(e => TypeOf(e) == "assistant"))
        {
            if (!e.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var k) && k.GetString() == "text")
                    sb.AppendLine(block.GetProperty("text").GetString()).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The blocks of a rendered conversation that mention <paramref name="needle"/>, with the output after each.</summary>
    private static string Excerpt(string rendered, string needle)
    {
        var blocks = rendered.Split("\n\n[");
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Length; i++)
        {
            if (!blocks[i].Contains(needle, StringComparison.Ordinal) || !blocks[i].Contains("AGENT RUNS")) continue;
            sb.AppendLine("[" + blocks[i].TrimStart('['));
            if (i + 1 < blocks.Length && blocks[i + 1].Contains("OUTPUT:")) sb.AppendLine("[" + blocks[i + 1]);
            sb.AppendLine();
        }
        return sb.Length == 0 ? $"(no command in the conversation mentions '{needle}')" : sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------
    // Evidence
    // ------------------------------------------------------------------------------------------------

    private static string Listing(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[the Director's record] session {s.Id}: Factory = {s.Factory ?? "(none)"}, " +
                      $"FactoryMemoryDirectory ({FactoryMemoryFiles.DirectoryEnvVar}) = {s.FactoryMemoryDirectory ?? "(none)"}");
        if (s.FactoryMemoryDirectory is not { } dir || !Directory.Exists(dir))
        {
            sb.AppendLine("  (no folder)");
            return sb.ToString();
        }
        foreach (var f in new DirectoryInfo(dir).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.AppendLine($"  {f.LastWriteTimeUtc:HH:mm:ss.fff}  {f.Length,7}  {f.Name}");
        sb.AppendLine("--- index.md ---");
        sb.AppendLine(ReadShared(Path.Combine(dir, "index.md")));
        sb.AppendLine($"--- {FactoryMemoryFiles.ReadVersionsFileName} ---");
        sb.AppendLine(ReadShared(Path.Combine(dir, FactoryMemoryFiles.ReadVersionsFileName)));
        return sb.ToString();
    }

    /// <summary>The file index.md gives for a note, from its table row.</summary>
    private static string FileOf(string index, string note)
    {
        var row = index.Split('\n').FirstOrDefault(l => l.StartsWith($"| {note} |", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"index.md has no row for the note '{note}'");
        return row.Split('|')[2].Trim();
    }

    private List<CcDirector.Gateway.Data.Entities.FactoryMemoryNoteEntity> NoteRows()
    {
        using var ctx = _gateway.GatewayDatabaseForTests.CreateContext(TenantId.Local);
        return ctx.FactoryMemoryNotes.AsEnumerable().OrderBy(r => r.Name).ThenBy(r => r.Version).ToList();
    }

    private void WriteGatewayRows()
    {
        var sb = new StringBuilder();
        sb.AppendLine("THE GATEWAY'S OWN ROWS at the end of the run, read straight out of its database.");
        sb.AppendLine();
        sb.AppendLine("Factory memory notes, every version kept (text shown for versions not written by the loader):");
        foreach (var r in NoteRows())
        {
            sb.AppendLine($"  {r.Factory}  {r.Name}  v{r.Version}  deleted={r.Deleted}  {r.AuthorKind} {r.AuthorId}  {r.WrittenAtUtc:o}  {(r.Text ?? "").Length} chars");
            if (r.AuthorId != _loaderId)
                sb.AppendLine(Indent(r.Text));
        }
        sb.AppendLine();
        var store = new History.SessionHistoryStore(_gateway.GatewayDatabaseForTests);
        sb.AppendLine("Session history rows the Gateway decides membership from:");
        foreach (var id in NoteRows().Select(r => r.AuthorId).Where(a => a is not null).Distinct())
        {
            var lookup = store.FactoryOf(id!);
            sb.AppendLine($"  {id}: known {lookup.IsKnown}, factory {lookup.Factory ?? "(none)"}");
        }
        File.WriteAllText(Path.Combine(_out, "gateway-rows.txt"), sb.ToString());
    }

    private async Task RunCaseAsync(string number, string name, Func<Task> body)
    {
        _transcript = new StringBuilder();
        _transcript.AppendLine($"CASE {number} - {name}").AppendLine(new string('=', 100)).AppendLine();
        Note($"case {number}: {name}");
        string verdict;
        string? why = null;
        try
        {
            await body();
            verdict = "PASS";
        }
        catch (Exception ex)
        {
            verdict = "FAIL";
            why = ex.Message;
            _transcript.AppendLine().AppendLine("*** THIS CASE DID NOT STAND UP ***").AppendLine(ex.ToString());
        }
        _transcript.AppendLine().AppendLine(new string('=', 100)).AppendLine($"CASE {number}: {verdict}{(why is null ? "" : " - " + why)}");
        File.WriteAllText(Path.Combine(_out, $"{number}-{Slug(name)}.txt"), _transcript.ToString());
        _cases.Add((number, name, verdict, why));
        Note($"case {number}: {verdict}");
    }

    private void WriteSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("A REAL FACTORY AGENT USES ITS FACTORY'S MEMORY - what this run showed");
        sb.AppendLine(new string('=', 100));
        sb.AppendLine();
        sb.AppendLine($"Run at {DateTime.UtcNow:o} on {Environment.MachineName} ({Environment.OSVersion}).");
        sb.AppendLine($"Gateway: a real host on 127.0.0.1:{_gateway.Port}, SQLite on disk, AUTHENTICATION ENFORCED.");
        sb.AppendLine($"Director: a real ControlApiHost '{DirectorId}' on machine '{_machine}', wired by its OWN configuration");
        sb.AppendLine("file - its own Gateway client downloaded each factory session's notes, its own tunnel carried every");
        sb.AppendLine("create, and it minted and registered every session's key itself.");
        sb.AppendLine($"Agents: real Claude Code sessions ({Required("CLAUDE")}), each started by a FACTORY SCHEDULE naming");
        sb.AppendLine($"'{TheFactory}', fired with the Gateway's run-now route. Their command line was the branch's own cc-devthrottle.");
        sb.AppendLine("The first notes: the Website Factory's LESSONS.md, loaded by the mission's importer run inside a");
        sb.AppendLine("stand-in session of the factory (a person's hand spawn - the one session here not started by a schedule).");
        sb.AppendLine();
        foreach (var (number, name, verdict, why) in _cases)
            sb.AppendLine($"  {number}  {verdict,-4}  {name}{(why is null ? "" : "   <- " + why)}");
        sb.AppendLine();
        sb.AppendLine($"{_cases.Count(c => c.Verdict == "PASS")} of {_cases.Count} cases stood up.");
        sb.AppendLine();
        sb.AppendLine("Where to look: the numbered files are the cases; agents/ holds each agent's whole conversation, rendered");
        sb.AppendLine("and raw, and its last screen; gateway-rows.txt is the Gateway's own record; timeline.txt is the run in order.");
        File.WriteAllText(Path.Combine(_out, "SUMMARY.txt"), sb.ToString());
    }

    // ------------------------------------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------------------------------------

    private static readonly string[] BirthFacts = { "sessionId", "name", "agent", "status", "factory", "originKind", "originSurface" };

    private static string ListSummary(string json)
    {
        var list = Read<FactoryMemoryListResponse>(json);
        var sb = new StringBuilder($"{{ \"factory\": \"{list.Factory}\", \"notes\": [{list.Notes.Count} notes, one line each] }}\n");
        foreach (var n in list.Notes)
            sb.AppendLine($"  {n.Name}  v{n.Version}  {n.AuthorKind} {n.AuthorId}  {(n.Text ?? "").Length} chars");
        return sb.ToString();
    }

    private async Task<(HttpStatusCode Status, string Body)> CallAsync(string credential, HttpMethod method, string path,
        object? body = null, string[]? showOnly = null, Func<string, string>? summariseResponse = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        var sb = new StringBuilder();
        sb.AppendLine($"> {method.Method} {path}");
        sb.AppendLine("> Authorization: Bearer <the person's Cockpit device key>");
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            foreach (var line in json.Split('\n')) sb.AppendLine("> " + line.TrimEnd('\r'));
        }
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        sb.AppendLine($"< {(int)response.StatusCode} {response.StatusCode}");
        var shown = response.IsSuccessStatusCode && summariseResponse is not null ? summariseResponse(text)
            : showOnly is not null ? Only(text, showOnly) : Pretty(text);
        foreach (var line in shown.Split('\n')) sb.AppendLine("< " + line.TrimEnd('\r'));
        Append(sb.ToString());
        Note($"{method.Method} {path} -> {(int)response.StatusCode}");
        return (response.StatusCode, text);
    }

    private static string Only(string json, string[] fields)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Pretty(json);
            var all = doc.RootElement.EnumerateObject().ToList();
            var kept = all.Where(p => fields.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var sb = new StringBuilder("{\n");
            foreach (var p in kept) sb.AppendLine($"  \"{p.Name}\": {p.Value.GetRawText()},");
            sb.AppendLine($"  ... and {all.Count - kept.Count} more fields of the session record, left out here").Append('}');
            return sb.ToString();
        }
        catch (JsonException) { return Pretty(json); }
    }

    private void Say(string sentence) => Append("# " + sentence);

    private void Append(string block)
    {
        lock (_transcript) _transcript.AppendLine(block.TrimEnd()).AppendLine();
    }

    private static string Indent(string? text) =>
        string.Join("\n", (text ?? "").Replace("\r", "").Split('\n').Select(l => "    " + l));

    /// <summary>Reads a file another process may still hold open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    private static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no body)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    private static string Slug(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private void Note(string line)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
        _timeline.Enqueue(stamped);
        _console.WriteLine(stamped);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Timed out after {timeout} waiting for {what}");
    }
}
