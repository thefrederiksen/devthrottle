using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using SessionManager = CcDirector.Core.Sessions.SessionManager;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Memory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A FACTORY AGENT'S MEMORY, THROUGH THE ROUTES (Factory Memory mission, phase 2).
///
/// These go through a real Kestrel host and a real database, because the phase 1 review taught the lesson these
/// were written to obey: tests that call a helper directly prove the rule and prove nothing about the rule being
/// REACHED. Every case here would fail if the route stopped resolving the caller's factory, if the guard stopped
/// letting a session in, or if the store stopped being wired - which is exactly what a caller-blind test cannot
/// say.
///
/// The case that matters most is <see cref="A_SESSION_MAY_NOT_READ_ANOTHER_FACTORYS_MEMORY"/> together with
/// <see cref="A_session_in_no_factory_has_no_memory_to_read"/>: the factory is never taken from the request.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryMemoryRouteTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private const string Another = "invoice-factory";
    private static readonly string Scout = Guid.NewGuid().ToString();

    private readonly GatewayDbTestHarness _h = new();
    private WebApplication? _app;

    public void Dispose()
    {
        try { _app?.StopAsync().GetAwaiter().GetResult(); } catch (Exception) { /* best effort */ }
        try { _app?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { /* best effort */ }
        _h.Dispose();
    }

    /// <summary>
    /// Stand the real routes up over the real store. <paramref name="sessionId"/> stands in for AuthMiddleware
    /// having verified a session key; <paramref name="deviceType"/> for a person's device. The factory lookup is
    /// the REAL one, reading the history row, so a test has to write a row to be in a factory - the same way a
    /// session gets there in life.
    /// </summary>
    /// <param name="verifiedCredential">Stands in for AuthMiddleware having verified SOME credential. With no session
    /// and no person's device type, that is a Director: its workstation key, or the machine token.</param>
    private async Task<HttpClient> StartAsync(string? sessionId = null, string? deviceType = null, bool verifiedCredential = false)
    {
        var store = new FactoryMemoryStore(_h.Open());
        var history = new SessionHistoryStore(_h.Open());

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.Use(async (ctx, next) =>
        {
            if (verifiedCredential) ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-verified-credential";
            if (deviceType is not null) ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
            if (sessionId is not null)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(sessionId), TenantId.Local, "dir-1");
            await next();
        });

        FactoryMemoryEndpoints.Map(_app,
            resolveTenant: _ => TenantId.Local,
            store: store,
            sessionFactoryOf: history.FactoryOf,
            nowUtc: () => new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));

        await _app.StartAsync();
        return new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    /// <summary>Put a session in a factory the way the product does: a pushed row naming it.</summary>
    private void InFactory(string sessionId, string? factory)
        => new SessionHistoryStore(_h.Open()).UpsertLive("dir-1", new SessionDto
        {
            SessionId = sessionId,
            Name = "Scout",
            RepoPath = @"D:\repos\devthrottle",
            Agent = "ClaudeCode",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            LastActivityAt = DateTime.UtcNow,
            ActivityState = "Working",
            Status = "Running",
            Factory = factory,
        }, DateTime.UtcNow);

    private static StringContent Body(object o) =>
        new(System.Text.Json.JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    // ---------- a factory session, end to end ----------

    [Fact]
    public async Task A_FACTORY_SESSION_WRITES_A_NOTE_AND_READS_IT_BACK()
    {
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);

        var wrote = await http.PutAsync("/factory-memory/notes/domains",
            Body(new SetFactoryMemoryNoteRequest { Text = "this registry wants a telephone number", ExpectedVersion = 0 }));
        Assert.Equal(HttpStatusCode.OK, wrote.StatusCode);
        var note = await wrote.Content.ReadFromJsonAsync<FactoryMemoryNoteDto>();
        Assert.Equal(1, note!.Version);
        // THE FACTORY CAME FROM THE SESSION'S RECORD, not from the request - nothing in that call named one.
        Assert.Equal(TheFactory, note.Factory);

        var list = await http.GetFromJsonAsync<FactoryMemoryListResponse>("/factory-memory/notes");
        Assert.Equal(TheFactory, list!.Factory);
        var only = Assert.Single(list.Notes);
        Assert.Equal("domains", only.Name);
        Assert.Equal("this registry wants a telephone number", only.Text);
        Assert.Equal("session", only.AuthorKind);
        Assert.Equal(Scout, only.AuthorId);
    }

    [Fact]
    public async Task The_listing_says_how_much_room_is_left_before_a_write_is_refused()
    {
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);
        await http.PutAsync("/factory-memory/notes/domains", Body(new SetFactoryMemoryNoteRequest { Text = "12345", ExpectedVersion = 0 }));

        var list = await http.GetFromJsonAsync<FactoryMemoryListResponse>("/factory-memory/notes");

        Assert.Equal(5, list!.Bytes);
        Assert.Equal(FactoryMemoryStore.MaxFactoryBytes, list.MaxBytes);
        Assert.Equal(FactoryMemoryStore.MaxNotes, list.MaxNotes);
    }

    // ---------- whose memory it is, is never the caller's choice ----------

    [Fact]
    public async Task A_SESSION_MAY_NOT_READ_ANOTHER_FACTORYS_MEMORY()
    {
        // THE TEST THAT MATTERS. If the route read the factory from the query string, this would answer 200 with
        // the other factory's notes, and every other test here would still pass.
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);

        var resp = await http.GetAsync($"/factory-memory/notes?factory={Another}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(Another, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_session_in_no_factory_has_no_memory_to_read()
    {
        InFactory(Scout, factory: null);
        var http = await StartAsync(sessionId: Scout);

        var resp = await http.GetAsync("/factory-memory/notes");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("in no factory", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_session_the_Gateway_has_no_row_for_yet_is_told_to_try_again()
    {
        // No row written: the session is younger than its first push. A retry, not a judgement about membership.
        var http = await StartAsync(sessionId: Scout);

        var resp = await http.GetAsync("/factory-memory/notes");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("not yet known", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_caller_that_is_neither_a_person_nor_a_session_is_refused()
    {
        var http = await StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/factory-memory/notes")).StatusCode);
    }

    // ---------- a person ----------

    [Fact]
    public async Task A_PERSON_NAMES_THE_FACTORY_because_nothing_can_be_read_from_them()
    {
        var http = await StartAsync(deviceType: "browser");

        var without = await http.GetAsync("/factory-memory/notes");
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Contains("name the factory", await without.Content.ReadAsStringAsync());

        var with = await http.GetFromJsonAsync<FactoryMemoryListResponse>($"/factory-memory/notes?factory={TheFactory}");
        Assert.Equal(TheFactory, with!.Factory);
        Assert.Empty(with.Notes);
    }

    // ---------- a Director, downloading a factory session's memory before its agent starts (phase 3a) ----------

    /// <summary>A person writes the notes a Director will then download; the app is restarted as the Director.</summary>
    private async Task<HttpClient> APersonWroteTwoNotesThenStartAsADirector(string? directorDeviceType)
    {
        var person = await StartAsync(deviceType: "browser");
        await person.PutAsync($"/factory-memory/notes/domains?factory={TheFactory}",
            Body(new SetFactoryMemoryNoteRequest { Text = "this registry wants a telephone number", ExpectedVersion = 0 }));
        await person.PutAsync($"/factory-memory/notes/deliverability?factory={TheFactory}",
            Body(new SetFactoryMemoryNoteRequest { Text = "fix DMARC first", ExpectedVersion = 0 }));
        await _app!.StopAsync();
        return await StartAsync(deviceType: directorDeviceType, verifiedCredential: true);
    }

    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Theory]
    [InlineData("workstation")]   // what a Director enrols with
    [InlineData(null)]            // the machine token of a self-hosted Gateway
    public async Task A_DIRECTOR_DOWNLOADS_THE_NOTES_OF_THE_FACTORY_IT_NAMES(string? directorDeviceType)
    {
        var director = await APersonWroteTwoNotesThenStartAsADirector(directorDeviceType);

        var list = await director.GetFromJsonAsync<FactoryMemoryListResponse>($"/factory-memory/notes?factory={TheFactory}");

        Assert.Equal(TheFactory, list!.Factory);
        Assert.Equal(new[] { "deliverability", "domains" }, list.Notes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(list.Notes, n => n.Text == "this registry wants a telephone number" && n.Version == 1);
    }

    [Fact]
    public async Task A_Director_must_name_the_factory_it_is_downloading()
    {
        var director = await StartAsync(deviceType: "workstation", verifiedCredential: true);

        var resp = await director.GetAsync("/factory-memory/notes");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("name the factory whose memory you are downloading", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_DIRECTOR_READS_THE_LIST_AND_NOTHING_ELSE()
    {
        // A Director writes nobody's memory. Every route but the list is closed to it, including the single note and
        // its history: the download needs the list, and nothing wider was asked for.
        var director = await APersonWroteTwoNotesThenStartAsADirector("workstation");
        var q = $"?factory={TheFactory}";

        Assert.Equal(HttpStatusCode.Forbidden, (await director.GetAsync($"/factory-memory/notes/domains{q}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await director.GetAsync($"/factory-memory/notes/domains/history{q}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await director.PutAsync($"/factory-memory/notes/domains{q}",
            Body(new SetFactoryMemoryNoteRequest { Text = "a Director's opinion", ExpectedVersion = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await director.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/factory-memory/notes/domains{q}")
        {
            Content = Body(new DeleteFactoryMemoryNoteRequest { ExpectedVersion = 1 }),
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await director.PostAsync($"/factory-memory/notes/domains/restore{q}",
            Body(new RestoreFactoryMemoryNoteRequest { Version = 1 }))).StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_verified_credential_is_not_a_Director_even_naming_a_factory()
    {
        var nobody = await StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"/factory-memory/notes?factory={TheFactory}")).StatusCode);
    }

    [Fact]
    public async Task THE_DIRECTORS_OWN_CLIENT_AND_THE_HOSTS_WIRING_PUT_THE_NOTES_IN_PLACE_BEFORE_THE_AGENT_STARTS()
    {
        // The whole chain, with nothing hand-built in the middle: the Director's real Gateway client, through the
        // exact function ControlApiHost installs, called by the real CreateSession for a session stamped into the
        // factory the way the create verb stamps it - against the real routes over the real store.
        var director = await APersonWroteTwoNotesThenStartAsADirector("workstation");
        using var client = new GatewayClient(
            new GatewayConfig { Url = director.BaseAddress!.ToString().TrimEnd('/'), Token = "director-key" }, "dir-1", "1.0.0");

        var root = Path.Combine(Path.GetTempPath(), "ccd-fm-chain-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            reservations: new WorktreeReservationStore(Path.Combine(root, "reservations")),
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4))
        {
            FactoryMemoryRoot = Path.Combine(root, "factory-memory"),
        };
        manager.FactoryMemoryDownload = ControlApiHost.FactoryMemoryDownloadThrough(() => client);
        try
        {
            var session = manager.CreateSession(repo,
                new RawCliAgent(OnWindows ? "cmd.exe" : "/bin/sh"),
                userArgs: OnWindows ? "/c exit" : "-c true",
                SessionBackendType.ConPty, resumeSessionId: null,
                beforeLaunch: s => s.StampFactory(TheFactory));

            var dir = session.FactoryMemoryDirectory!;
            Assert.Equal("this registry wants a telephone number", File.ReadAllText(Path.Combine(dir, "domains.md")));
            Assert.Equal("fix DMARC first", File.ReadAllText(Path.Combine(dir, "deliverability.md")));
            Assert.True(File.Exists(Path.Combine(dir, "index.md")));
        }
        finally
        {
            try { await manager.KillAllSessionsAsync(); } catch (Exception) { /* best effort */ }
            manager.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_REFUSED_DOWNLOAD_REACHES_THE_CALLER_IN_THE_GATEWAYS_OWN_WORDS()
    {
        // A credential the Gateway cannot place is refused; the Director's download carries that refusal, word for
        // word, to the create that asked for it.
        var nobody = await StartAsync();
        using var client = new GatewayClient(
            new GatewayConfig { Url = nobody.BaseAddress!.ToString().TrimEnd('/'), Token = "director-key" }, "dir-1", "1.0.0");
        var download = ControlApiHost.FactoryMemoryDownloadThrough(() => client);

        var ex = Assert.Throws<InvalidOperationException>(() => download(TheFactory, Guid.NewGuid()));

        Assert.Contains("the Gateway refused the download (HTTP 403)", ex.Message);
        Assert.Contains("read and written by that factory's own sessions, or by a person", ex.Message);
    }

    [Fact]
    public void With_no_Gateway_client_the_download_says_so()
    {
        var download = ControlApiHost.FactoryMemoryDownloadThrough(() => null);

        var ex = Assert.Throws<InvalidOperationException>(() => download(TheFactory, Guid.NewGuid()));

        Assert.Contains("not connected to a Gateway", ex.Message);
    }

    // ---------- two writers, through the route ----------

    [Fact]
    public async Task A_STALE_WRITE_IS_REFUSED_AND_THE_ANSWER_CARRIES_THE_CURRENT_NOTE()
    {
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);
        await http.PutAsync("/factory-memory/notes/deliverability", Body(new SetFactoryMemoryNoteRequest { Text = "start", ExpectedVersion = 0 }));
        await http.PutAsync("/factory-memory/notes/deliverability", Body(new SetFactoryMemoryNoteRequest { Text = "start, and the Scout's lesson", ExpectedVersion = 1 }));

        // A second agent still holding version 1.
        var refused = await http.PutAsync("/factory-memory/notes/deliverability",
            Body(new SetFactoryMemoryNoteRequest { Text = "start, and the Sender's lesson", ExpectedVersion = 1 }));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Stale", text);
        // What the refused writer needs in order to merge, in the refusal itself.
        Assert.Contains("start, and the Scout's lesson", text);
    }

    // ---------- delete, deleted reads, restore ----------

    [Fact]
    public async Task A_DELETED_NOTE_ANSWERS_WITH_ITS_DELETE_RATHER_THAN_A_NOT_FOUND()
    {
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);
        await http.PutAsync("/factory-memory/notes/domains", Body(new SetFactoryMemoryNoteRequest { Text = "v1", ExpectedVersion = 0 }));

        var deleted = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/factory-memory/notes/domains")
        {
            Content = Body(new DeleteFactoryMemoryNoteRequest { ExpectedVersion = 1 }),
        });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // An agent told "no such note" would write a fresh one and lose what the history holds.
        var got = await http.GetFromJsonAsync<FactoryMemoryNoteDto>("/factory-memory/notes/domains");
        Assert.True(got!.Deleted);
        Assert.Equal(2, got.Version);
        // And it says so in words, not only in a flag: an agent reading deleted:true has to work out what to do.
        Assert.Contains("was deleted in version 2", got.DeletedNotice);
        Assert.Contains("restored by a person in the Cockpit", got.DeletedNotice);
        Assert.Contains("continues the same history", got.DeletedNotice);

        Assert.Empty((await http.GetFromJsonAsync<FactoryMemoryListResponse>("/factory-memory/notes"))!.Notes);
    }

    [Fact]
    public async Task A_note_that_was_never_written_is_a_NOT_FOUND()
    {
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/factory-memory/notes/never")).StatusCode);
    }

    [Fact]
    public async Task A_SESSION_MAY_NOT_RESTORE_and_is_told_what_to_do_instead()
    {
        // The owner let factory sessions delete because a delete is undoable by a PERSON. A session undoing its
        // own delete would add nothing, and restoring decides which version of the truth stands.
        InFactory(Scout, TheFactory);
        var http = await StartAsync(sessionId: Scout);
        await http.PutAsync("/factory-memory/notes/domains", Body(new SetFactoryMemoryNoteRequest { Text = "v1", ExpectedVersion = 0 }));

        var resp = await http.PostAsync("/factory-memory/notes/domains/restore", Body(new RestoreFactoryMemoryNoteRequest { Version = 1 }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("only a person can restore", text);
        Assert.Contains("write the text you want as a new version", text);
    }

    [Fact]
    public async Task A_PERSON_RESTORES_A_DELETED_NOTE_AS_A_NEW_VERSION_AUTHORED_BY_THEM()
    {
        InFactory(Scout, TheFactory);
        var agent = await StartAsync(sessionId: Scout);
        await agent.PutAsync("/factory-memory/notes/domains", Body(new SetFactoryMemoryNoteRequest { Text = "the good text", ExpectedVersion = 0 }));
        await agent.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/factory-memory/notes/domains")
        {
            Content = Body(new DeleteFactoryMemoryNoteRequest { ExpectedVersion = 1 }),
        });
        await _app!.StopAsync();

        var person = await StartAsync(deviceType: "browser");
        var restored = await person.PostAsync($"/factory-memory/notes/domains/restore?factory={TheFactory}",
            Body(new RestoreFactoryMemoryNoteRequest { Version = 1 }));

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var note = await restored.Content.ReadFromJsonAsync<FactoryMemoryNoteDto>();
        Assert.Equal(3, note!.Version);
        Assert.Equal("the good text", note.Text);
        Assert.Equal("person", note.AuthorKind);

        // The history still shows the delete it undid: a restore is not a rewind.
        var history = await person.GetFromJsonAsync<FactoryMemoryHistoryResponse>($"/factory-memory/notes/domains/history?factory={TheFactory}");
        Assert.Equal(3, history!.Versions.Count);
        Assert.True(history.Versions[1].Deleted);
    }

    // ---------- the guard lets a session in at all ----------

    [Fact]
    public void THE_SESSION_KEY_GUARD_ALLOWS_A_FACTORY_SESSION_TO_ITS_OWN_MEMORY()
    {
        // Without this the routes above are unreachable in production whatever they do, and no route test would
        // notice: the harness stands the endpoints up without the guard in front of them.
        Assert.True(SessionKeyGuard.Check("GET", "/factory-memory/notes").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/factory-memory/notes/domains").Allowed);
        Assert.True(SessionKeyGuard.Check("PUT", "/factory-memory/notes/domains").Allowed);
        Assert.True(SessionKeyGuard.Check("DELETE", "/factory-memory/notes/domains").Allowed);
        Assert.True(SessionKeyGuard.Check("GET", "/factory-memory/notes/domains/history").Allowed);
        Assert.True(SessionKeyGuard.Check("POST", "/factory-memory/notes/domains/restore").Allowed);
        // And nothing else under that prefix.
        Assert.False(SessionKeyGuard.Check("POST", "/factory-memory/notes").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/factory-memory").Allowed);
    }
}
