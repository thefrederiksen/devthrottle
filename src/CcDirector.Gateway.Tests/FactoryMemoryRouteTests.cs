using System.Net;
using System.Net.Http.Json;
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
    private async Task<HttpClient> StartAsync(string? sessionId = null, string? deviceType = null)
    {
        var store = new FactoryMemoryStore(_h.Open());
        var history = new SessionHistoryStore(_h.Open());

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.Use(async (ctx, next) =>
        {
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
