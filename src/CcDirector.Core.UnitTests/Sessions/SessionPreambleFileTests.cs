using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: the file that replaced
/// <c>GET /sessions/{sid}/fleet-preamble</c> and its hook-output sibling.
///
/// What is on trial here is the CONTRACT the hook depends on: the file holds the finished
/// hookSpecificOutput envelope, it is EMPTY when there is nothing to inject, and it carries everything
/// the two deleted routes carried (the session's identity, the signed-in user, the workflow seat). The
/// hook prints this file straight into the agent's context, so anything wrong in it arrives dressed as
/// instructions.
/// </summary>
public sealed class SessionPreambleFileTests : IDisposable
{
    private readonly string _dir;

    public SessionPreambleFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-preamble-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session MakeSession(string? name = "preamble-file-test")
        => new(
            Guid.NewGuid(),
            repoPath: Path.Combine(_dir, "repo"),
            workingDirectory: Path.Combine(_dir, "repo"),
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: name,
            customColor: null);

    /// <summary>The injected-text store, pinned to the DevThrottle default over a throwaway cache, so no
    /// test here becomes sensitive to whatever the machine running the suite has cached.</summary>
    private InjectedTextStore Ours() => InjectedTextStore.AlwaysOurs(_dir);

    private static string AdditionalContext(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    [Fact]
    public void The_file_is_the_finished_SessionStart_envelope_a_hook_can_print_verbatim()
    {
        var session = MakeSession();

        var path = SessionPreambleFile.WriteFor(session, "TEST-MACHINE", user: null, _dir, Ours());

        Assert.Equal(SessionHookFiles.PreamblePathFor(session.Id, _dir), path);
        var body = File.ReadAllText(path);

        // The shape the two deleted routes' hook-output sibling returned, and the shape both hook
        // scripts now print without touching it.
        using var doc = JsonDocument.Parse(body);
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        Assert.Contains("cc-devthrottle", hook.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void The_session_identifies_itself_by_id_name_and_machine()
    {
        var session = MakeSession("Remove Network Port - Manager");

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, Ours()));

        Assert.Contains(session.Id.ToString(), text);
        Assert.Contains("Remove Network Port - Manager", text);
        Assert.Contains("TEST-MACHINE", text);
    }

    /// <summary>
    /// Issue #1357's line, in the file. It is asserted here because the two routes it used to come from
    /// disagreed about it for as long as they existed: the Windows route resolved the signed-in user and
    /// the macOS/Linux route silently did not, so the same text built two ways was wrong on one platform.
    /// One file cannot have that defect - but only if the file actually carries the line.
    /// </summary>
    [Fact]
    public void The_signed_in_user_is_named_when_one_is_resolved()
    {
        var session = MakeSession();
        var user = new SignedInUser("star@example.com", "Starlord");

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user, Ours()));

        Assert.Contains("The user of this session is Starlord (star@example.com).", text);
        Assert.Contains("do not guess identity from usage or the database", text);
    }

    [Fact]
    public void No_signed_in_user_omits_the_identity_line_rather_than_guessing()
    {
        var session = MakeSession();

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, Ours()));

        Assert.DoesNotContain("The user of this session is", text);
        Assert.Contains("cc-devthrottle", text); // the rest of the preamble is intact
    }

    [Fact]
    public void A_seated_session_carries_its_workflow_seat_paragraph()
    {
        var session = MakeSession();
        session.SetExplicitRole("Architect");
        session.SeatOnWorkflow(Guid.NewGuid(), "mission", 7);

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, Ours()));

        Assert.Contains("[Workflow seat]", text);
        Assert.Contains("seated as Architect on the 'mission' workflow", text);
        Assert.Contains("cc-devthrottle workflow instructions mission --version 7", text);
    }

    [Fact]
    public void An_unseated_session_carries_no_seat_paragraph()
    {
        var text = AdditionalContext(SessionPreambleFile.Render(MakeSession(), "TEST-MACHINE", user: null, Ours()));

        Assert.DoesNotContain("[Workflow seat]", text);
    }

    /// <summary>
    /// The user turned our text off and theirs cannot be read. The file must be EMPTY - not an error, not
    /// our text. The hook prints this file into the agent's context, so an error message here would
    /// arrive as instructions, and substituting our text would inject the policy they declined.
    /// </summary>
    [Fact]
    public void Unreadable_user_text_writes_an_empty_file_and_never_substitutes_ours()
    {
        var session = MakeSession();
        var store = new InjectedTextStore(Path.Combine(_dir, "broken-cache.json"));
        // "Yours is live" with no text at all - the state InjectedTextStore refuses to paper over.
        store.WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: null, CachedAtUtc: DateTime.UtcNow));

        var path = SessionPreambleFile.WriteFor(session, "TEST-MACHINE", user: null, _dir, store);

        Assert.True(File.Exists(path));
        Assert.Equal("", File.ReadAllText(path));
        Assert.DoesNotContain("cc-devthrottle", File.ReadAllText(path));
    }

    [Fact]
    public void Whitespace_only_user_text_writes_an_empty_file_rather_than_an_empty_envelope()
    {
        var session = MakeSession();
        var store = new InjectedTextStore(Path.Combine(_dir, "blank-cache.json"));
        store.WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: "   \r\n  ", CachedAtUtc: DateTime.UtcNow));

        var path = SessionPreambleFile.WriteFor(session, "TEST-MACHINE", user: null, _dir, store);

        // Empty, not {"hookSpecificOutput":{...,"additionalContext":""}} - a hook that printed an empty
        // envelope would be injecting a message that says nothing, which is not the same as injecting
        // nothing.
        Assert.Equal("", File.ReadAllText(path));
    }

    [Fact]
    public void The_users_own_text_is_what_gets_injected_when_theirs_is_live()
    {
        var session = MakeSession();
        var store = new InjectedTextStore(Path.Combine(_dir, "yours-cache.json"));
        store.WriteCache(new InjectedTextCacheEntry(
            UseYours: true, Yours: "MY OWN RULES AND NOTHING ELSE", CachedAtUtc: DateTime.UtcNow));

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, store));

        Assert.Contains("MY OWN RULES AND NOTHING ELSE", text);
        Assert.DoesNotContain("cc-devthrottle actions --json", text);
    }

    [Fact]
    public void Deleting_a_sessions_file_leaves_nothing_behind_and_is_safe_to_repeat()
    {
        var session = MakeSession();
        var path = SessionPreambleFile.WriteFor(session, "TEST-MACHINE", user: null, _dir, Ours());
        Assert.True(File.Exists(path));

        SessionPreambleFile.DeleteFor(session.Id, _dir);
        Assert.False(File.Exists(path));

        // A second delete of a file that is already gone is not an error - the reaper does not have to
        // know whether anything was ever written.
        SessionPreambleFile.DeleteFor(session.Id, _dir);
    }

    // ---------- the factory kit line (issue #4609) ----------

    // The injected-text store never reads the age of its cache, so a fixed time keeps these tests off the real clock.
    private static readonly DateTime FixedTime = new(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>A home seat of a factory whose kit is written to disk, with a brief and grants.</summary>
    private Session MakeFactorySeat(bool brief = true, bool grants = true, bool home = true)
    {
        var session = MakeSession("CPMC - forward-engineer");
        var kit = Directory.CreateDirectory(Path.Combine(_dir, "factory-run", session.Id.ToString())).FullName;
        Directory.CreateDirectory(Path.Combine(kit, FactoryRunKit.WorkFolderName));
        if (brief) File.WriteAllText(Path.Combine(kit, FactoryRunKit.BriefFileName), "STEP 1: answer the customer.\n");
        if (grants) File.WriteAllText(Path.Combine(kit, FactoryRunKit.GrantsFileName), "You may send email.\n");
        session.StampFactory("cpmc");
        session.StampFactorySeat("forward-engineer", 6, "cpmc/forward-engineer");
        session.FactoryKitDirectory = kit;
        session.FactoryHome = home;
        return session;
    }

    /// <summary>The user's own text, about the size of the real 10 KB start-up text, so the order is what decides
    /// whether the kit line reaches the preview.</summary>
    private InjectedTextStore LongText()
    {
        var store = new InjectedTextStore(Path.Combine(_dir, "long-cache.json"));
        store.WriteCache(new InjectedTextCacheEntry(
            UseYours: true, Yours: string.Join("\n", Enumerable.Repeat("A fleet rule the agent should know about.", 250)), CachedAtUtc: FixedTime));
        return store;
    }

    [Fact]
    public void Render_FactorySeatWithLongStartupText_KitLineSitsInTheFirst2048Bytes()
    {
        // Arrange
        var session = MakeFactorySeat();
        session.FactoryMemoryDirectory = Path.Combine(_dir, "factory-memory");
        var kit = session.FactoryKitDirectory!;

        // Act
        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, LongText()));

        // Assert: the line names the brief, the grants and the working folder by absolute path, and all of it is inside
        // the two kilobytes the agent is shown before it has to open a file.
        var line = FactoryRunKit.StartupLine(session)!;
        Assert.StartsWith(line, text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > 8192, "the start-up text should be long, as the real one is");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 2048, $"the kit line is {line.Length} characters");
        var preview = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(text), 0, 2048);
        Assert.Contains($"Your brief is {Path.Combine(kit, "brief.md")}", preview);
        Assert.Contains($"your grants are {Path.Combine(kit, "grants.md")}", preview);
        Assert.Contains($"Your working folder is {Path.Combine(kit, "work")}.", preview);
        Assert.Contains("you are seat 'forward-engineer' of factory 'cpmc'", preview);
        Assert.Contains("read the brief in full before you do anything else", preview);
        // The memory line still comes, after the fleet text.
        Assert.Contains("Factory memory:", text);
    }

    [Fact]
    public void Render_SessionWithNoKit_HasNoKitLine()
    {
        var session = MakeSession();
        session.StampFactory("cpmc");

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, Ours()));

        Assert.Null(FactoryRunKit.StartupLine(session));
        Assert.DoesNotContain("Factory seat:", text);
        Assert.DoesNotContain("brief.md", text);
    }

    [Fact]
    public void Render_UnreadableUserTextOnAFactorySeat_StillCarriesTheKitLineAlone()
    {
        var session = MakeFactorySeat();
        var store = new InjectedTextStore(Path.Combine(_dir, "broken-cache.json"));
        store.WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: null, CachedAtUtc: FixedTime));

        var text = AdditionalContext(SessionPreambleFile.Render(session, "TEST-MACHINE", user: null, store));

        Assert.Equal(FactoryRunKit.StartupLine(session), text);
    }

    [Fact]
    public void StartupLine_KitWithNoBriefOrGrants_NamesOnlyTheKit()
    {
        // A child of a seat gets a kit of rules only: the line must not send it to a brief that is not there.
        var session = MakeFactorySeat(brief: false, grants: false, home: false);

        var line = FactoryRunKit.StartupLine(session)!;

        Assert.Contains($"Your kit is {session.FactoryKitDirectory}", line);
        Assert.DoesNotContain("brief", line);
        Assert.DoesNotContain("grants", line);
        Assert.DoesNotContain("working folder", line);
    }

    [Fact]
    public void StartupLine_RepositorySeat_DoesNotNameTheWorkFolder()
    {
        var session = MakeFactorySeat(home: false);

        var line = FactoryRunKit.StartupLine(session)!;

        Assert.Contains("Your brief is", line);
        Assert.DoesNotContain("working folder", line);
    }

    /// <summary>
    /// The write must leave no temporary file behind. The pointer watcher filters on the drop extension
    /// precisely so it cannot see a half-written file, and that only holds if the temporary really is
    /// moved rather than copied.
    /// </summary>
    [Fact]
    public void The_write_is_atomic_and_leaves_no_temporary_file()
    {
        var session = MakeSession();
        SessionPreambleFile.WriteFor(session, "TEST-MACHINE", user: null, _dir, Ours());

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
