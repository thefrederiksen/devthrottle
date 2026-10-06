using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Memory;
using CcDirector.Gateway.History;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A FACTORY'S MEMORY, END TO END, ON A REAL GATEWAY AND A REAL DIRECTOR (the Factory Memory mission,
/// section 7 "the check", the live half). Not part of any suite run: it stands up a Gateway host, a Director
/// host and a tunnel between them, and starts several real sessions. It runs only when
/// <c>CC_FACTORY_MEMORY_E2E_OUT</c> names a directory, where it writes its evidence - one numbered
/// transcript per case plus a summary, exactly as <see cref="DoorbellEndToEndProof"/> does.
///
/// WHAT IS REAL. A Gateway host (stream mode, SQLite on disk, a loopback port the operating system picks)
/// with AUTHENTICATION ENFORCED - <c>authEnabled: true</c>, so every request below goes through the real
/// auth middleware, the real session-key guard, the real route and the real store. A Director host
/// (<see cref="ControlApiHost"/>) with the Director's tunnel client and its command dispatcher, so a create
/// travels the same path a create travels in the field: spawn door, tunnel, the Director's own executor,
/// the pre-launch factory stamp, the roster push, the history row. Session keys are minted and registered
/// exactly as a Director does, and each session's own key is what its calls carry. A person is a real
/// enrolled device credential of type "browser" - the Cockpit - resolved by the same middleware.
///
/// WHAT IS NOT REAL, said plainly so the evidence is not read as more than it is. The sessions run a
/// stand-in command (<c>cat</c>) rather than a coding agent: this proof is about the GATEWAY calls and who
/// may make them, and every call below is made with the session's own credential over real HTTP, which is
/// precisely what the command-line tool will do for the agent in phase 3a. No model is asked anything, so
/// nothing here shows an agent CHOOSING to write a note - that is phase 4's live run. The Director's
/// download of the notes into the session's folder before the agent starts is phase 3a and does not exist
/// yet, so "a fresh session has the note before its first turn" is shown here as the fresh session READING
/// it through the Gateway, not as a file on disk.
/// </summary>
// Carried so the mission's own check (dotnet test --filter Category=FactoryMemory) NAMES this proof rather
// than leaving it somewhere a reader has to know about. It reports as skipped there, with the variable to
// set written in the skip reason, which is the honest state: the live proof is a thing you run on purpose.
[Trait("Category", "FactoryMemory")]
[Collection("DirectorRoot")]
public sealed class FactoryMemoryEndToEndProof : IAsyncLifetime
{
    private sealed class ProofFactAttribute : FactAttribute
    {
        public ProofFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OutVariable)))
                Skip = $"Live proof with a real Gateway and a real Director; set {OutVariable} to a directory to run it.";
        }
    }

    private const string OutVariable = "CC_FACTORY_MEMORY_E2E_OUT";
    private const string Token = "factory-memory-proof-token";
    private const string DirectorId = "factory-memory-proof-director";
    private const string TheFactory = "website-factory";
    private const string AnotherFactory = "invoice-factory";

    /// <summary>The note every case works on, named as the design's own example names one.</summary>
    private const string NoteName = "domains";

    private readonly string _out = Environment.GetEnvironmentVariable(OutVariable) ?? "";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-factory-memory-proof-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly ConcurrentQueue<string> _timeline = new();

    private GatewayHost _gateway = null!;
    private SessionManager _sessions = null!;
    private ControlApiHost _director = null!;
    private GatewayStreamClient _stream = null!;
    private string _repo = "";
    private string _personKey = "";

    /// <summary>Every session's own Gateway key, by session id, minted as the Director mints them.</summary>
    private readonly ConcurrentDictionary<Guid, string> _keys = new();

    /// <summary>What each credential is called in the transcripts, so no key is ever written to a file.</summary>
    private readonly ConcurrentDictionary<string, string> _credentialNames = new();

    private readonly List<(string Number, string Name, string Verdict, string? Why)> _cases = new();
    private StringBuilder _transcript = new();
    private readonly object _transcriptGate = new();
    private string _caseNumber = "";
    private string _caseName = "";

    // ------------------------------------------------------------------------------------------------
    // The stand-in the sessions run. Not an agent: see the class comment.
    // ------------------------------------------------------------------------------------------------
    private static string StandInCommand => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_out)) return;
        Directory.CreateDirectory(_out);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_root, "gw-instances"),
            workListsPath: Path.Combine(_root, "gw-instances", "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        Note($"gateway listening at http://127.0.0.1:{_gateway.Port} with authentication ENFORCED");

        // THE PERSON. A real device credential of type "browser" - what the Cockpit enrols as - so the auth
        // middleware resolves it to a person's surface and the spawn door keeps the factory it names.
        _personKey = _gateway.Devices.Register("factory-memory-proof-cockpit", "proof-mac", "macos", "browser").DeviceKey;
        _credentialNames[_personKey] = "the person's Cockpit device key";
        Note("a person's device key enrolled (device type 'browser' - the Cockpit)");

        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);

        var url = $"http://127.0.0.1:{_gateway.Port}";

        // THE DIRECTOR IS GIVEN A GATEWAY IN ITS OWN CONFIGURATION FILE, BEFORE IT STARTS - the way an installed
        // one has one. It then builds its own Gateway client, which is what phase 3a downloads a factory session's
        // notes with, inside CreateSession, before the agent process starts.
        //
        // WITHOUT THIS LINE THIS PROOF STANDS UP 0 OF 8 and the product is right to refuse it: a factory session
        // never starts without its memory, so every create is refused with "no Gateway is configured on this
        // Director". That is what the phases 3 and 4 review found, and it found the pull request claiming 8 of 8
        // about a branch where the answer was none of them. Setting SessionManager.GatewayUrl below is enough for
        // a session's OWN key; it is not enough for the DIRECTOR's download, and the difference is the whole of
        // finding 1.
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        File.WriteAllText(CcStorage.ConfigJson(),
            System.Text.Json.JsonSerializer.Serialize(new { gateway = new { url, token = Token, streamMode = true } }));

        _sessions = new SessionManager(new AgentOptions());
        _director = new ControlApiHost(_sessions, "factory-memory-proof", () => Task.CompletedTask,
            directorId: DirectorId, instancesDirectory: Path.Combine(_root, "dir-instances"));
        await _director.StartAsync();

        _sessions.GatewayUrl = url;
        _sessions.GatewaySessionCredentialSource = id =>
        {
            var key = GatewaySessionKey.Mint();
            _gateway.SessionKeys.Register(TenantId.Local, DirectorId, id.ToString(), GatewaySessionKey.Hash(key),
                DateTime.UtcNow.AddHours(4));
            _keys[id] = key;
            return key;
        };
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId, TailnetEndpoint = "", MachineName = "proof-mac", Pid = Environment.ProcessId,
            Version = "2.16.0-factory-memory-proof", StartedAt = DateTime.UtcNow,
        });
        _stream = new GatewayStreamClient(new GatewayConfig { Url = url, Token = Token, StreamMode = true },
            DirectorId, "factory-memory-proof",
            () => _sessions.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList(),
            cmd => SessionCommandExecutor.DispatchAsync(_sessions, DirectorId, cmd),
            rePushInterval: TimeSpan.FromSeconds(2));
        _stream.Start();
        await WaitUntil(() => _gateway.PushedSessions.GetActiveConnectionId(TenantId.Local, DirectorId) is not null,
            TimeSpan.FromSeconds(30), "the Director's tunnel to connect");
        Note("director tunnel connected");
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
        await _stream.DisposeAsync();
        await _director.StopAsync();
        _sessions.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
    }

    // ================================================================================================
    // The run. One method, because the cases are one story: case 3 reads what case 2 wrote, and case 7
    // restores what case 5 left. Each case writes its own numbered transcript and its own verdict, and a
    // case that fails does not stop the ones after it - the summary says which stood up and which did not.
    // ================================================================================================

    [ProofFact]
    public async Task A_factory_agent_uses_its_factory_memory()
    {
        SessionDto scout = null!;      // the factory's first session, started by a person
        SessionDto fresh = null!;      // a later session of the same factory
        SessionDto child = null!;      // a child of that later session
        SessionDto grandchild = null!; // its child in turn - depth 2
        SessionDto outsider = null!;   // a session of another factory
        SessionDto orphan = null!;     // a session in no factory

        await RunCaseAsync("01", "a person starts a session INTO a factory, and the factory sticks to it", async () =>
        {
            Say("A person, holding a Cockpit device key, asks the Gateway for a session in the factory. The " +
                "factory rides the create request; the Gateway settles it at the spawn door and the Director " +
                "stamps it on the session before the process starts.");
            scout = await CreateSessionAsync(_personKey, "Scout", TheFactory, showWholeRecord: true);
            Assert.Equal(TheFactory, scout.Factory);

            Say("The session record the Director PUSHED to the Gateway, read back over HTTP as a person reads it.");
            var pushed = await PushedAsync(_personKey, scout.SessionId);
            Assert.Equal(TheFactory, pushed.Factory);

            Say("And the history row - the ONE record the Gateway decides membership from. It is read here " +
                "straight out of the Gateway's own database, because no route exposes it.");
            var row = await WaitForRowAsync(scout.SessionId);
            Assert.True(row.IsInAFactory);
            Assert.Equal(TheFactory, row.Factory);
        });

        await RunCaseAsync("02", "that session writes a note with its OWN key, and never names the factory", async () =>
        {
            Say("Nothing in this request names a factory. The Gateway reads it from the calling session's record.");
            var (status, body) = await CallAsync(KeyOf(scout), $"PUT {Prefix}/notes/{NoteName}", HttpMethod.Put,
                $"{Prefix}/notes/{NoteName}",
                new { text = "this registry wants a telephone number on the contact record", expectedVersion = 0 });
            Assert.Equal(HttpStatusCode.OK, status);
            var note = Read<FactoryMemoryNoteDto>(body);
            Assert.Equal(1, note.Version);
            Assert.Equal(TheFactory, note.Factory);
            Assert.Equal("session", note.AuthorKind);
            Assert.Equal(scout.SessionId, note.AuthorId);
        });

        await RunCaseAsync("03", "the session that learned it is gone; a LATER session of the factory reads it back", async () =>
        {
            Say("The Scout is killed, and the roster is watched until the Gateway agrees it is gone. What it " +
                "learned is not in it any more - it is in the factory.");
            await _sessions.KillSessionAsync(Guid.Parse(scout.SessionId));
            await WaitUntil(() => RosterHasGone(scout.SessionId), TimeSpan.FromSeconds(60),
                "the Scout to leave the Gateway's roster");
            var (rosterStatus, rosterBody) = await CallAsync(_personKey, "GET /sessions (the Scout is gone)",
                HttpMethod.Get, "/sessions", showOnly: BirthFacts);
            Say("The roster above is the whole fleet this Gateway can see; the Scout is not in it.");
            Assert.Equal(HttpStatusCode.OK, rosterStatus);
            Assert.DoesNotContain(Read<List<SessionDto>>(rosterBody), s => s.SessionId == scout.SessionId);

            fresh = await CreateSessionAsync(_personKey, "Sender", TheFactory);
            await WaitForRowAsync(fresh.SessionId);

            var (status, body) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes/{NoteName}", HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            Assert.Equal(HttpStatusCode.OK, status);
            var note = Read<FactoryMemoryNoteDto>(body);
            Assert.Equal(1, note.Version);
            Assert.Contains("telephone number", note.Text);
            Assert.Equal(scout.SessionId, note.AuthorId);
            Say("THIS IS THE MISSION IN ONE LINE: a lesson written by a session that no longer exists was read " +
                "by a session started after it died, and the note still names who learned it.");

            Say("And the whole listing, as an agent would fetch it at the start of a run.");
            var (listStatus, listBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes", HttpMethod.Get, $"{Prefix}/notes");
            Assert.Equal(HttpStatusCode.OK, listStatus);
            var list = Read<FactoryMemoryListResponse>(listBody);
            Assert.Equal(TheFactory, list.Factory);
            Assert.Single(list.Notes);
        });

        await RunCaseAsync("04", "every other session is refused, and the refusal says why", async () =>
        {
            outsider = await CreateSessionAsync(_personKey, "Another factory's session", AnotherFactory);
            await WaitForRowAsync(outsider.SessionId);
            orphan = await CreateSessionAsync(_personKey, "A session in no factory", factory: null);
            await WaitForRowAsync(orphan.SessionId);

            Say("(a) A session of ANOTHER factory names this one. Refused - not quietly answered with its own.");
            var (a, aBody) = await CallAsync(KeyOf(outsider), $"GET {Prefix}/notes/{NoteName}?factory={TheFactory}",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}?factory={TheFactory}");
            Assert.Equal(HttpStatusCode.Forbidden, a);
            Assert.Contains(AnotherFactory, aBody);

            Say("(b) The same session asks for the note WITHOUT naming a factory. It is answered about its own " +
                "factory's memory, which has no such note - so it cannot see the other factory's note at all.");
            var (b, _) = await CallAsync(KeyOf(outsider), $"GET {Prefix}/notes/{NoteName}", HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            Assert.Equal(HttpStatusCode.NotFound, b);

            Say("(c) A session in NO factory. There is nothing for it to read and nothing it may write.");
            var (c, cBody) = await CallAsync(KeyOf(orphan), $"GET {Prefix}/notes", HttpMethod.Get, $"{Prefix}/notes");
            Assert.Equal(HttpStatusCode.Forbidden, c);
            Assert.Contains("in no factory", cBody);

            Say("(d) The same session tries to WRITE. Same refusal - reading and writing are one rule.");
            var (d, _) = await CallAsync(KeyOf(orphan), $"PUT {Prefix}/notes/{NoteName}", HttpMethod.Put, $"{Prefix}/notes/{NoteName}",
                new { text = "an outsider's text", expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.Forbidden, d);

            Say("(e) And it cannot let itself in through the back door either: a session in no factory that " +
                "starts a session INTO one is refused at the spawn door.");
            var (e, eBody) = await CallAsync(KeyOf(orphan), $"POST /directors/{DirectorId}/sessions (factory: {TheFactory})",
                HttpMethod.Post, $"/directors/{DirectorId}/sessions", CreateBody("A claimed membership", TheFactory, ownerSessionId: orphan.SessionId));
            Assert.Equal(HttpStatusCode.Forbidden, e);
            Assert.Contains("is in no factory", eBody);
        });

        await RunCaseAsync("05", "two writers on one note: one wins, one is refused with the current text", async () =>
        {
            Say("A child of the factory session, started with the CHILD'S PARENT'S OWN KEY and naming no " +
                "factory at all. It inherits membership because the Gateway reads the caller's record.");
            child = await CreateSessionAsync(KeyOf(fresh), "Worker", factory: null, ownerSessionId: fresh.SessionId);
            Assert.Equal(TheFactory, child.Factory);
            await WaitForRowAsync(child.SessionId);

            Say("Both sessions read the note. Both see version 1.");
            var (_, oneBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes/{NoteName} (the first writer reads)",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            var (_, twoBody) = await CallAsync(KeyOf(child), $"GET {Prefix}/notes/{NoteName} (the second writer reads)",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            Assert.Equal(1, Read<FactoryMemoryNoteDto>(oneBody).Version);
            Assert.Equal(1, Read<FactoryMemoryNoteDto>(twoBody).Version);

            Say("Both write, at the same moment, both stating version 1.");
            var first = CallAsync(KeyOf(fresh), $"PUT {Prefix}/notes/{NoteName} (writer one, expectedVersion 1)",
                HttpMethod.Put, $"{Prefix}/notes/{NoteName}",
                new { text = "writer one: the registry also wants a postal address", expectedVersion = 1 });
            var second = CallAsync(KeyOf(child), $"PUT {Prefix}/notes/{NoteName} (writer two, expectedVersion 1)",
                HttpMethod.Put, $"{Prefix}/notes/{NoteName}",
                new { text = "writer two: the registry rejects post office boxes", expectedVersion = 1 });
            var answers = await Task.WhenAll(first, second);

            var won = answers.Where(r => r.Status == HttpStatusCode.OK).ToList();
            var lost = answers.Where(r => r.Status == HttpStatusCode.Conflict).ToList();
            Assert.Single(won);
            Assert.Single(lost);
            var winner = Read<FactoryMemoryNoteDto>(won[0].Body);
            Assert.Equal(2, winner.Version);

            Say("The refusal carries the CURRENT note, so the loser already holds what it needs to merge.");
            using var refusal = JsonDocument.Parse(lost[0].Body);
            Assert.Equal("Stale", refusal.RootElement.GetProperty("outcome").GetString());
            var current = refusal.RootElement.GetProperty("current");
            Assert.Equal(2, current.GetProperty("version").GetInt32());
            Assert.Equal(winner.Text, current.GetProperty("text").GetString());

            Say("And afterwards the winner's text is what stands - the refused write left no trace on it.");
            var (_, after) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes/{NoteName} (after both writes)",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            var stands = Read<FactoryMemoryNoteDto>(after);
            Assert.Equal(2, stands.Version);
            Assert.Equal(winner.Text, stands.Text);
        });

        await RunCaseAsync("06", "a GRANDCHILD writes: depth 2, live", async () =>
        {
            Say("The child starts a session of its own. Nothing names a factory anywhere in the chain after the " +
                "first create; every hop is the Gateway reading the caller's own record.");
            grandchild = await CreateSessionAsync(KeyOf(child), "Grandchild", factory: null, ownerSessionId: child.SessionId);
            Assert.Equal(TheFactory, grandchild.Factory);
            await WaitForRowAsync(grandchild.SessionId);
            Say($"chain: {fresh.SessionId} -> {child.SessionId} -> {grandchild.SessionId}, all in '{TheFactory}'");

            var (status, body) = await CallAsync(KeyOf(grandchild), $"PUT {Prefix}/notes/deliverability",
                HttpMethod.Put, $"{Prefix}/notes/deliverability",
                new { text = "every email to this domain failed the sender policy check", expectedVersion = 0 });
            Assert.Equal(HttpStatusCode.OK, status);
            var note = Read<FactoryMemoryNoteDto>(body);
            Assert.Equal(1, note.Version);
            Assert.Equal(TheFactory, note.Factory);
            Assert.Equal(grandchild.SessionId, note.AuthorId);
        });

        await RunCaseAsync("07", "a session deletes; the deleted note says so; a PERSON puts it back", async () =>
        {
            Say("The delete states the version it last read, exactly as a write does.");
            var (deleted, deletedBody) = await CallAsync(KeyOf(fresh), $"DELETE {Prefix}/notes/{NoteName}",
                HttpMethod.Delete, $"{Prefix}/notes/{NoteName}", new { expectedVersion = 2 });
            Assert.Equal(HttpStatusCode.OK, deleted);
            var gone = Read<FactoryMemoryNoteDto>(deletedBody);
            Assert.Equal(3, gone.Version);
            Assert.True(gone.Deleted);

            Say("A GET of the deleted note answers 200 saying it was deleted - NOT 404. An agent told 'there is " +
                "no such note' would write a fresh one and lose everything the history holds.");
            var (got, gotBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes/{NoteName} (after the delete)",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            Assert.Equal(HttpStatusCode.OK, got);
            Assert.True(Read<FactoryMemoryNoteDto>(gotBody).Deleted);

            Say("And it is no longer in the listing.");
            var (_, listBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes (after the delete)",
                HttpMethod.Get, $"{Prefix}/notes");
            Assert.DoesNotContain(Read<FactoryMemoryListResponse>(listBody).Notes, n => n.Name == NoteName);

            Say("The session tries to undo its own delete. Refused: restoring is the owner's call.");
            var (refused, refusedBody) = await CallAsync(KeyOf(fresh), $"POST {Prefix}/notes/{NoteName}/restore (as the session)",
                HttpMethod.Post, $"{Prefix}/notes/{NoteName}/restore", new { version = 2 });
            Assert.Equal(HttpStatusCode.Forbidden, refused);
            Assert.Contains("only a person can restore a note", refusedBody);

            Say("The history a person reads before deciding which version to put back.");
            var (_, historyBody) = await CallAsync(_personKey, $"GET {Prefix}/notes/{NoteName}/history?factory={TheFactory}",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}/history?factory={TheFactory}");
            var history = Read<FactoryMemoryHistoryResponse>(historyBody);
            Assert.Equal(3, history.Versions.Count);

            Say("The person restores version 2. A restore is itself a new version, with the person as its author.");
            var (restored, restoredBody) = await CallAsync(_personKey, $"POST {Prefix}/notes/{NoteName}/restore?factory={TheFactory}",
                HttpMethod.Post, $"{Prefix}/notes/{NoteName}/restore?factory={TheFactory}", new { version = 2 });
            Assert.Equal(HttpStatusCode.OK, restored);
            var back = Read<FactoryMemoryNoteDto>(restoredBody);
            Assert.Equal(4, back.Version);
            Assert.False(back.Deleted);
            Assert.Equal("person", back.AuthorKind);

            Say("The factory's own session sees it back.");
            var (_, finalBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes/{NoteName} (after the restore)",
                HttpMethod.Get, $"{Prefix}/notes/{NoteName}");
            var final = Read<FactoryMemoryNoteDto>(finalBody);
            Assert.Equal(4, final.Version);
            Assert.False(final.Deleted);
            Assert.Equal(back.Text, final.Text);
        });

        await RunCaseAsync("08", "a note over the cap is refused, and says by how much", async () =>
        {
            var tooBig = new string('x', FactoryMemoryStore.MaxNoteBytes + 1);
            Say($"One note takes at most {FactoryMemoryStore.MaxNoteBytes} bytes. This one is one byte over.");
            var (status, body) = await CallAsync(KeyOf(fresh), $"PUT {Prefix}/notes/too-large ({tooBig.Length} bytes)",
                HttpMethod.Put, $"{Prefix}/notes/too-large", new { text = tooBig, expectedVersion = 0 },
                summariseRequest: $"{{ \"text\": \"<{tooBig.Length} bytes of 'x'>\", \"expectedVersion\": 0 }}");
            Assert.Equal(HttpStatusCode.Conflict, status);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("NoteTooLarge", doc.RootElement.GetProperty("outcome").GetString());

            Say("And the note was not created: the listing is unchanged.");
            var (_, listBody) = await CallAsync(KeyOf(fresh), $"GET {Prefix}/notes (after the refused write)",
                HttpMethod.Get, $"{Prefix}/notes");
            Assert.DoesNotContain(Read<FactoryMemoryListResponse>(listBody).Notes, n => n.Name == "too-large");
        });

        WriteSummary();
        var failed = _cases.Where(c => c.Verdict != "PASS").ToList();
        Assert.True(failed.Count == 0,
            "these cases did not stand up: " + string.Join("; ", failed.Select(f => $"{f.Number} {f.Name}: {f.Why}")));
    }

    // ------------------------------------------------------------------------------------------------
    // The machinery. Everything below only writes evidence or waits.
    // ------------------------------------------------------------------------------------------------

    private const string Prefix = "/factory-memory";

    private string KeyOf(SessionDto s) => _keys[Guid.Parse(s.SessionId)];

    private async Task RunCaseAsync(string number, string name, Func<Task> body)
    {
        _caseNumber = number;
        _caseName = name;
        _transcript = new StringBuilder();
        _transcript.AppendLine($"CASE {number} - {name}");
        _transcript.AppendLine(new string('=', 100));
        _transcript.AppendLine();
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
            _transcript.AppendLine();
            _transcript.AppendLine("*** THIS CASE DID NOT STAND UP ***");
            _transcript.AppendLine(ex.ToString());
        }
        _transcript.AppendLine();
        _transcript.AppendLine(new string('=', 100));
        _transcript.AppendLine($"CASE {number}: {verdict}{(why is null ? "" : " - " + why)}");
        File.WriteAllText(Path.Combine(_out, $"{number}-{Slug(name)}.txt"), _transcript.ToString());
        _cases.Add((number, name, verdict, why));
        Note($"case {number}: {verdict}");
    }

    /// <summary>A line of plain English in the transcript, saying what the next requests are for.</summary>
    private void Say(string sentence)
    {
        Append("# " + sentence + Environment.NewLine);
    }

    /// <summary>
    /// One real request, over real HTTP, with a real credential - and its answer, both written into the
    /// case's transcript verbatim. The credential itself is never written: it is named.
    ///
    /// The whole exchange is built up locally and appended to the transcript in ONE locked step, because
    /// case 05 fires two of these at the same instant on purpose: appending line by line would interleave
    /// the two exchanges and, worse, tear the builder itself.
    ///
    /// <paramref name="showOnly"/> names the response fields to print when the answer is a large record
    /// nobody needs in full - a created session carries about 120 fields. What is left out is counted and
    /// said, so a reader can see that something was left out rather than assume the record was small.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> CallAsync(
        string credential, string label, HttpMethod method, string path, object? body = null,
        string? summariseRequest = null, string[]? showOnly = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        var exchange = new StringBuilder();
        exchange.AppendLine($"> {method.Method} {path}");
        exchange.AppendLine($"> Authorization: Bearer <{NameOf(credential)}>");
        if (body is not null)
            foreach (var line in (summariseRequest ?? JsonSerializer.Serialize(body, Json)).Split('\n'))
                exchange.AppendLine("> " + line.TrimEnd('\r'));

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        exchange.AppendLine($"< {(int)response.StatusCode} {response.StatusCode}");
        foreach (var line in Shown(text, showOnly).Split('\n'))
            exchange.AppendLine("< " + line.TrimEnd('\r'));
        Append(exchange.ToString());
        Note($"{label} -> {(int)response.StatusCode}");
        return (response.StatusCode, text);
    }

    /// <summary>The one place the transcript is written, so two requests in flight cannot tear it.</summary>
    private void Append(string block)
    {
        lock (_transcriptGate)
        {
            _transcript.Append(block);
            _transcript.AppendLine();
        }
    }

    /// <summary>The response as the transcript shows it: whole, or the named fields with the rest counted.</summary>
    private static string Shown(string json, string[]? showOnly)
    {
        if (showOnly is null || string.IsNullOrWhiteSpace(json)) return Pretty(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // A roster. One line per session, or the whole thing is hundreds of lines of fields that
                // have nothing to do with a factory's memory.
                var rows = new StringBuilder();
                rows.AppendLine($"[{doc.RootElement.GetArrayLength()} sessions, one line each]");
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var fields = item.EnumerateObject()
                        .Where(p => showOnly.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                        .Select(p => $"{p.Name}={p.Value.GetRawText()}");
                    rows.AppendLine("  " + string.Join("  ", fields));
                }
                return rows.ToString().TrimEnd();
            }
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Pretty(json);
            var all = doc.RootElement.EnumerateObject().ToList();
            var kept = all.Where(p => showOnly.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (kept.Count == 0) return Pretty(json);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            foreach (var p in kept)
                sb.AppendLine($"  \"{p.Name}\": {p.Value.GetRawText()},");
            sb.AppendLine($"  ... and {all.Count - kept.Count} more fields of the session record, left out here");
            sb.Append('}');
            return sb.ToString();
        }
        catch (JsonException)
        {
            return Pretty(json);
        }
    }

    /// <summary>
    /// The body of a create, built by hand so the transcript shows exactly what was sent.
    ///
    /// <paramref name="ownerSessionId"/> is the ownership declaration a SESSION-initiated spawn must carry
    /// (<c>controllerSessionId</c>): a session starting a session has two possible owners and the Gateway
    /// refuses to pick between them. A person's spawn carries none - it is the user's.
    /// </summary>
    private Dictionary<string, object?> CreateBody(string name, string? factory, string? ownerSessionId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["repoPath"] = _repo,
            ["agent"] = "RawCli",
            ["command"] = StandInCommand,
            ["name"] = name,
        };
        if (ownerSessionId is not null) body["controllerSessionId"] = ownerSessionId;
        // Omitted entirely rather than sent as null, so "this request named no factory" is visible in the
        // transcript rather than something a reader has to infer.
        if (factory is not null) body["factory"] = factory;
        return body;
    }

    /// <summary>The fields of a created session this proof is about. The record carries about 120.</summary>
    private static readonly string[] BirthFacts =
    {
        "sessionId", "name", "agent", "status", "factory",
        "originKind", "originSurface", "parentSessionId", "controllerSessionId",
    };

    private async Task<SessionDto> CreateSessionAsync(string credential, string name, string? factory,
        string? ownerSessionId = null, bool showWholeRecord = false)
    {
        var (status, body) = await CallAsync(credential,
            $"POST /directors/{DirectorId}/sessions ({name}, factory: {factory ?? "none named"})",
            HttpMethod.Post, $"/directors/{DirectorId}/sessions", CreateBody(name, factory, ownerSessionId),
            showOnly: showWholeRecord ? null : BirthFacts);
        Assert.Equal(HttpStatusCode.Created, status);
        var dto = Read<SessionDto>(body);
        await WaitUntil(() => _keys.ContainsKey(Guid.Parse(dto.SessionId)), TimeSpan.FromSeconds(30),
            $"the Director to mint {name}'s own Gateway key");
        _credentialNames[_keys[Guid.Parse(dto.SessionId)]] = $"{name}'s own session key";
        return dto;
    }

    /// <summary>
    /// True when the Gateway's own roster route no longer lists the session. A killed session leaves the
    /// roster, which is what the read side and every client see; the in-memory push cache still holds a row
    /// for it, so asking that instead would never come true.
    /// </summary>
    private bool RosterHasGone(string sessionId) => Roster().All(s => s.SessionId != sessionId);

    /// <summary>The session record the Director pushed, read back through the roster route as a person reads it.</summary>
    private async Task<SessionDto> PushedAsync(string credential, string sessionId)
    {
        // Polled QUIETLY - a recorded read on every attempt would fill the transcript with the same roster
        // a dozen times over. The one read that is recorded below is the one the case rests on.
        await WaitUntil(() => Roster().Any(s => s.SessionId == sessionId), TimeSpan.FromSeconds(60),
            $"session {sessionId} to reach the Gateway's roster");
        var (status, body) = await CallAsync(credential, "GET /sessions", HttpMethod.Get, "/sessions",
            showOnly: BirthFacts);
        Assert.Equal(HttpStatusCode.OK, status);
        return Read<List<SessionDto>>(body).Single(s => s.SessionId == sessionId);
    }

    /// <summary>The Gateway's own roster route, read without recording anything.</summary>
    private List<SessionDto> Roster()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _personKey);
        var response = http.GetAsync("sessions").GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) return new List<SessionDto>();
        return Read<List<SessionDto>>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Wait for the history row the Gateway decides membership from, and write what it says into the
    /// transcript. Read straight out of the Gateway's own database: no route exposes this field.
    /// </summary>
    private async Task<History.SessionFactoryLookup> WaitForRowAsync(string sessionId)
    {
        var store = new History.SessionHistoryStore(_gateway.GatewayDatabaseForTests);
        History.SessionFactoryLookup lookup = default;
        await WaitUntil(() =>
        {
            lookup = store.FactoryOf(sessionId);
            return lookup.IsKnown;
        }, TimeSpan.FromSeconds(60), $"the history row for session {sessionId}");
        Append($"[the Gateway's own record] SessionHistoryStore.FactoryOf(\"{sessionId}\") = " +
               $"known: {lookup.IsKnown}, in a factory: {lookup.IsInAFactory}, factory: {lookup.Factory ?? "(none)"}"
               + Environment.NewLine);
        return lookup;
    }

    private void WriteSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("A FACTORY'S MEMORY, END TO END - what this run showed");
        sb.AppendLine(new string('=', 100));
        sb.AppendLine();
        sb.AppendLine($"Run at {DateTime.UtcNow:o} on {Environment.MachineName} ({Environment.OSVersion}).");
        sb.AppendLine($"Gateway: a real host on 127.0.0.1:{_gateway.Port}, SQLite on disk, AUTHENTICATION ENFORCED.");
        sb.AppendLine($"Director: a real ControlApiHost '{DirectorId}', connected over the Director's own tunnel client.");
        sb.AppendLine("Every request below went through the real auth middleware, the real session-key guard, the");
        sb.AppendLine("real route and the real store. Every session's calls carry that session's OWN key.");
        sb.AppendLine();
        sb.AppendLine("The sessions ran a stand-in command, not a coding agent: this proof is about the Gateway");
        sb.AppendLine("calls and who may make them. No model was asked anything, so nothing here shows an agent");
        sb.AppendLine("CHOOSING to write a note - that is the mission's phase 4. The Director's download of the");
        sb.AppendLine("notes into a session's folder before the agent starts is now in place (phase 3a), and this");
        sb.AppendLine("proof's Director is given a Gateway in its own configuration file so that it happens: without");
        sb.AppendLine("that, every create here is refused and this proof stands up none of its eight cases, which is");
        sb.AppendLine("the product being right. Case 03 still shows a fresh session READING the note through the");
        sb.AppendLine("Gateway; the file on disk before an agent's first turn is what the phase 4 proof shows.");
        sb.AppendLine();
        foreach (var (number, name, verdict, why) in _cases)
            sb.AppendLine($"  {number}  {verdict,-4}  {name}{(why is null ? "" : "   <- " + why)}");
        sb.AppendLine();
        sb.AppendLine($"{_cases.Count(c => c.Verdict == "PASS")} of {_cases.Count} cases stood up.");
        sb.AppendLine();
        sb.AppendLine("The transcripts are the numbered files beside this one; timeline.txt is the whole run in order.");
        File.WriteAllText(Path.Combine(_out, "SUMMARY.txt"), sb.ToString());
        File.WriteAllLines(Path.Combine(_out, "timeline.txt"), _timeline);
    }

    private string NameOf(string credential) =>
        _credentialNames.TryGetValue(credential, out var n) ? n : "a credential";

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    private static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no body)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
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
        Console.WriteLine(stamped);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out after {timeout} waiting for {what}");
    }
}
