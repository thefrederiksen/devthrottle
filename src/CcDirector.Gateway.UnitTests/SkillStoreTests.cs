using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SkillStore"/> and <see cref="BuiltInSkillSeeder"/> - the central skill
/// library (devthrottle_internal issue 995).
///
/// Two properties carry the whole feature and are tested hardest here:
///
///  1. THE LISTING IS SMALL. The register listing an agent's briefing is built from must never carry
///     bodies or file contents. If it ever does, discovery costs what use should cost, and the library
///     is worse than the per-machine file copies it replaces.
///  2. A SKILL SWITCHED OFF IS UNREACHABLE, AND NOTHING IS DELETED. Off means left out of the
///     briefing and the default fetch refused - while history stays readable by explicit version.
/// </summary>
public sealed class SkillStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static SkillContentRequest Content(
        string? id = null,
        string name = "My skill",
        string summary = "Does a useful thing.",
        string body = "# My skill\n\nDo the thing.",
        List<string>? triggers = null,
        List<SkillFileDto>? files = null,
        string authoredBy = "test") => new()
    {
        Id = id,
        Name = name,
        Summary = summary,
        Triggers = triggers ?? new List<string> { "do the thing" },
        BodyMarkdown = body,
        Files = files,
        AuthoredBy = authoredBy,
    };

    // ---- seeding ----------------------------------------------------------------------------------

    [Fact]
    public void Seeds_the_built_in_skills_in_shipped_order()
    {
        var store = new SkillStore(_h.Open());

        var skills = store.ListPublished();

        Assert.Equal(new[] { "dev-throttle", "fleet-comms", "move-session", "terminology", "fleet-manager" },
            skills.Select(s => s.Id).ToArray());
        Assert.All(skills, s =>
        {
            Assert.True(s.IsBuiltIn);
            Assert.False(s.Editable);
            Assert.True(s.Enabled);
            Assert.Equal(1, s.Version);
            Assert.False(string.IsNullOrWhiteSpace(s.ContentHash));
            Assert.False(string.IsNullOrWhiteSpace(s.Summary));
            Assert.NotEmpty(s.Triggers);
        });
    }

    [Fact]
    public void Reseeding_on_restart_mints_nothing_new()
    {
        _ = new SkillStore(_h.Open());

        var store = new SkillStore(_h.Open());

        var skills = store.ListPublished();
        Assert.Equal(5, skills.Count);
        Assert.All(skills, s => Assert.Equal(1, s.Version));

        using var ctx = _h.Open().CreateContext();
        Assert.Equal(5, ctx.SkillVersions.Count());
    }

    [Fact]
    public void Every_built_in_version_row_stores_its_body()
    {
        _ = new SkillStore(_h.Open());

        using var ctx = _h.Open().CreateContext();
        foreach (var id in new[] { "dev-throttle", "fleet-comms", "move-session", "terminology", "fleet-manager" })
        {
            var version = ctx.SkillVersions.Single(v => v.SkillId == id);
            Assert.Equal(BuiltInSkills.BodyFor(id), version.BodyMarkdown);
            Assert.Equal(SkillVersionStatus.Published, version.Status);
        }
    }

    [Fact]
    public void Every_built_in_ships_a_non_empty_body()
    {
        // The comparison above cannot see an empty resource: the shipped body and the stored row would
        // both be empty, and every session's briefing would point at a skill that says nothing.
        var ids = BuiltInSkills.All().Select(s => s.Id).ToArray();
        Assert.Contains("fleet-manager", ids);

        foreach (var id in ids)
            Assert.False(string.IsNullOrWhiteSpace(BuiltInSkills.BodyFor(id)),
                $"Built-in skill '{id}' ships an empty body.");
    }

    [Fact]
    public void Fleet_manager_skill_acknowledges_events_by_id_and_never_asks_for_reports()
    {
        // The owner ruled (2026-09-16) that the sessions a Fleet Manager starts never report to it: the Gateway's
        // events carry each stop, and the Fleet Manager acknowledges them with the commands named here.
        var body = BuiltInSkills.BodyFor("fleet-manager");

        Assert.Contains("[Fleet Manager events]", body);
        Assert.Contains("cc-devthrottle fleet ack <event id>", body);
        Assert.Contains("cc-devthrottle fleet events", body);
        Assert.DoesNotContain("devthrottle session report", body);
        Assert.DoesNotContain("`session report`", body);
        Assert.DoesNotContain("emporary", body);
    }

    [Fact]
    public void Fleet_manager_skill_leaves_a_stop_waiting_for_its_reading_alone_and_follows_every_page()
    {
        // Step 4, round 3: a stop stored before its reading is listed as waiting and cannot be acknowledged, and
        // events page past 200 by cursor - the skill must say both, in the commands as built. One phrase spans a
        // wrapped line, and a Windows checkout embeds the body with CRLF.
        var body = Normalize(BuiltInSkills.BodyFor("fleet-manager"));

        Assert.Contains("verdict is `waiting`", body);
        Assert.Contains("Do not\n  act on it and do not acknowledge it.", body);
        Assert.Contains("reading_pending", body);
        Assert.Contains("after 5 minutes", body);
        Assert.Contains("eventsMoreRemain:", body);
        Assert.Contains("cc-devthrottle fleet events --cursor <nextCursor>", body);
        Assert.Contains("cc-devthrottle fleet events --every-page", body);
        Assert.Contains("One prompt carries at most 200 events", body);
    }

    [Fact]
    public void Fleet_manager_skill_table_reads_a_session_only_for_a_settled_stop_with_no_verdict()
    {
        // Steps 1 and 4 inspection, finding 2: the capability table told the Fleet Manager to treat ANY missing reading
        // as "cannot tell" and read the session - including a stop still waiting for its reading, which the rest of the
        // skill and the workflow say to leave alone. The body is split on "\n", so a Windows CRLF body is normalized first.
        var body = Normalize(BuiltInSkills.BodyFor("fleet-manager"));
        var row = body.Split('\n').Single(l => l.StartsWith("| The Wingman reading the sessions YOU own |", StringComparison.Ordinal));

        Assert.DoesNotContain("or the reading failed) - treat that as \"cannot tell\"", row);
        Assert.Contains("verdict is `waiting` is still waiting for its reading - leave it alone", row);
        Assert.Contains("Only a settled stop with no verdict to act on", row);
        Assert.Contains("`cannot-tell` or failed", row);
    }

    [Fact]
    public void Changed_shipped_content_republishes_as_the_next_version_and_supersedes_the_old()
    {
        var db = _h.Open();
        _ = new SkillStore(db);

        // Simulate the running binary shipping different content by rewriting the published row's
        // hash, which is exactly what the seeder compares against.
        using (var ctx = db.CreateContext())
        {
            var published = ctx.SkillVersions.Single(
                v => v.SkillId == "move-session" && v.Status == SkillVersionStatus.Published);
            published.ContentHash = "not-what-this-binary-ships";
            ctx.SaveChanges();
        }

        var store = new SkillStore(_h.Open());

        var skill = store.GetPublished("move-session")!;
        Assert.Equal(2, skill.Version);
        using var after = _h.Open().CreateContext();
        var rows = after.SkillVersions.Where(v => v.SkillId == "move-session").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(SkillVersionStatus.Superseded, rows.Single(v => v.Version == 1).Status);
        Assert.Equal(SkillVersionStatus.Published, rows.Single(v => v.Version == 2).Status);
    }

    // ---- the listing must stay small --------------------------------------------------------------

    [Fact]
    public void The_register_listing_carries_no_bodies_and_no_file_contents()
    {
        // This is the feature's load-bearing property: the listing is what EVERY session's launch
        // briefing is rendered from. A body reaching it would make discovery cost what use should
        // cost. SkillDto is asserted here to have no property that could carry one.
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "with-files", files: new List<SkillFileDto>
        {
            new() { FileName = "helper.py", Content = "print('x')" },
            new() { FileName = "notes.md", Content = "# notes" },
        }));
        store.Publish("with-files");

        var listed = store.ListPublished().Single(s => s.Id == "with-files");

        Assert.Equal(2, listed.FileCount);
        var propertyNames = typeof(SkillDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("BodyMarkdown", propertyNames);
        Assert.DoesNotContain("Files", propertyNames);
        Assert.DoesNotContain("Content", propertyNames);
    }

    [Fact]
    public void A_summary_that_spans_lines_is_refused()
    {
        // A multi-line summary would render as extra lines in every agent's briefing - authored text
        // turning itself into unearned preamble.
        var store = new SkillStore(_h.Open());

        var ex = Assert.Throws<SkillValidationException>(() =>
            store.CreateDraft(Content(id: "multi-line", summary: "First line.\nSecond line.")));

        Assert.Contains("single line", ex.Message);
    }

    [Fact]
    public void A_summary_longer_than_the_register_budget_is_refused()
    {
        var store = new SkillStore(_h.Open());

        Assert.Throws<SkillValidationException>(() =>
            store.CreateDraft(Content(id: "long-summary", summary: new string('x', 201))));
    }

    // ---- authoring --------------------------------------------------------------------------------

    [Fact]
    public void A_draft_is_invisible_to_the_register_until_published()
    {
        var store = new SkillStore(_h.Open());

        var draft = store.CreateDraft(Content(id: "mine"));

        Assert.Equal(1, draft.Version);
        Assert.Equal(SkillVersionStatus.Draft, draft.Status);
        Assert.DoesNotContain(store.ListPublished(), s => s.Id == "mine");
        Assert.Null(store.GetPublished("mine"));

        var published = store.Publish("mine")!;

        Assert.Equal(1, published.Version);
        Assert.True(published.Editable);
        Assert.False(published.IsBuiltIn);
        Assert.Contains(store.ListPublished(), s => s.Id == "mine");
    }

    [Fact]
    public void Publishing_without_a_body_is_refused()
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "empty", body: ""));

        var ex = Assert.Throws<SkillValidationException>(() => store.Publish("empty"));

        Assert.Contains("without a body", ex.Message);
    }

    [Fact]
    public void A_stale_If_Match_hash_is_refused_rather_than_clobbering()
    {
        var store = new SkillStore(_h.Open());
        var first = store.CreateDraft(Content(id: "mine"));
        store.UpdateDraft("mine", Content(id: "mine", body: "# changed"), ifMatchHash: first.ContentHash);

        Assert.Throws<SkillConflictException>(() =>
            store.UpdateDraft("mine", Content(id: "mine", body: "# clobber"), ifMatchHash: first.ContentHash));
    }

    [Fact]
    public void Built_ins_cannot_be_edited_published_over_or_deleted()
    {
        var store = new SkillStore(_h.Open());

        var edit = Assert.Throws<SkillValidationException>(() =>
            store.UpdateDraft("move-session", Content(), ifMatchHash: null));
        Assert.Contains("clone", edit.Message);

        Assert.Throws<SkillValidationException>(() => store.Publish("move-session"));
        Assert.Throws<SkillValidationException>(() => store.Archive("move-session"));
    }

    [Fact]
    public void A_built_in_id_cannot_be_taken_by_a_new_skill()
    {
        var store = new SkillStore(_h.Open());

        Assert.Throws<SkillConflictException>(() => store.CreateDraft(Content(id: "move-session")));
    }

    [Fact]
    public void Cloning_a_built_in_yields_an_editable_copy_with_the_same_content()
    {
        var store = new SkillStore(_h.Open());

        var clone = store.Clone("move-session", "move-session-copy", "test")!;

        Assert.True(clone.Editable);
        Assert.False(clone.IsBuiltIn);
        Assert.Equal(1, clone.Version);
        Assert.Equal(store.GetPublished("move-session")!.ContentHash, clone.ContentHash);
        Assert.Equal(store.GetBody("move-session", null), store.GetBody("move-session-copy", null));

        // And the copy really is the caller's: it edits and publishes like any other skill.
        store.UpdateDraft("move-session-copy", Content(id: "move-session-copy", body: "# ours"), null);
        Assert.Equal(2, store.Publish("move-session-copy")!.Version);
    }

    [Fact]
    public void Cloning_copies_the_supporting_files()
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "with-files", files: new List<SkillFileDto>
        {
            new() { FileName = "helper.py", Content = "print('x')" },
        }));
        store.Publish("with-files");

        var clone = store.Clone("with-files", "with-files-copy", "test")!;

        Assert.Equal(1, clone.FileCount);
        Assert.Equal("print('x')", TextOf(store.GetFile("with-files-copy", "helper.py", null)));
    }

    // ---- the owner's switch -----------------------------------------------------------------------

    [Fact]
    public void Switching_a_skill_off_refuses_the_fetch_and_deletes_nothing()
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "mine"));
        store.Publish("mine");

        Assert.True(store.SetEnabled("mine", false, "test"));

        // Still LISTED (the register must show it to switch it back on) but marked off, and the
        // fetch is refused with a message that says why - never a misleading not-found.
        var listed = store.ListPublished().Single(s => s.Id == "mine");
        Assert.False(listed.Enabled);
        var ex = Assert.Throws<SkillValidationException>(() => store.GetBody("mine", null));
        Assert.Contains("turned OFF", ex.Message);

        // Nothing is deleted: switching it back on restores it immediately.
        Assert.True(store.SetEnabled("mine", true, "test"));
        Assert.NotNull(store.GetBody("mine", null));
    }

    [Fact]
    public void A_switched_off_skill_refuses_a_PINNED_read_too_and_refuses_its_files()
    {
        // THE REGRESSION THIS FEATURE ALREADY HAD ONCE. The off-switch was checked only on an
        // unpinned read, exactly as workflows do it - but `cc-devthrottle skill get` resolves the
        // head version and then asks for it BY NUMBER, so the switch was bypassed by the one command
        // every agent uses. Found by switching a skill off on a live Gateway and watching it serve.
        //
        // A guard is only worth what its bypasses are worth: this walks the command line's ACTUAL
        // path (resolve the version, then read that version) rather than the convenient one.
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "mine", files: new List<SkillFileDto>
        {
            new() { FileName = "helper.py", Content = "print('x')" },
        }));
        var published = store.Publish("mine")!;
        store.SetEnabled("mine", false, "test");

        var pinned = Assert.Throws<SkillValidationException>(
            () => store.GetBody("mine", version: published.Version));
        Assert.Contains("turned OFF", pinned.Message);

        // And the files go with it - a switch that stopped the instructions but served the scripts
        // would be half-closed.
        Assert.Throws<SkillValidationException>(() => store.GetFile("mine", "helper.py", null));
        Assert.Throws<SkillValidationException>(
            () => store.GetFile("mine", "helper.py", published.Version));
    }

    [Fact]
    public void A_switched_off_built_in_refuses_for_this_tenant_by_the_same_rule()
    {
        var store = new SkillStore(_h.Open());
        var version = store.GetPublished("move-session")!.Version;

        store.SetEnabled("move-session", false, "test");

        Assert.Throws<SkillValidationException>(() => store.GetBody("move-session", null));
        Assert.Throws<SkillValidationException>(() => store.GetBody("move-session", version));
    }

    [Fact]
    public void A_built_ins_switch_is_the_tenants_own_choice_and_never_writes_the_library_row()
    {
        var store = new SkillStore(_h.Open());

        Assert.True(store.SetEnabled("move-session", false, "test"));

        Assert.False(store.GetPublished("move-session")!.Enabled);
        using var ctx = _h.Open().CreateContext();
        // The shared library head row is untouched; the choice lives in the tenant's override row.
        Assert.True(ctx.Skills.Single(h => h.Id == "move-session").Enabled);
        Assert.False(ctx.SkillTenantOverrides.Single(o => o.SkillId == "move-session").Enabled);
    }

    [Fact]
    public void Switching_a_skill_requires_an_actor()
    {
        var store = new SkillStore(_h.Open());

        Assert.Throws<SkillValidationException>(() => store.SetEnabled("move-session", false, ""));
    }

    // ---- version resolution -----------------------------------------------------------------------

    [Fact]
    public void A_pinned_read_serves_superseded_history_but_never_a_draft()
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "mine", body: "# v1"));
        store.Publish("mine");
        store.UpdateDraft("mine", Content(id: "mine", body: "# v2"), null);

        // v2 exists but is a DRAFT: serving it as pinned history would be a lie, because a draft can
        // still change under whoever read it.
        Assert.Null(store.GetBody("mine", version: 2));
        Assert.Equal("# v1", store.GetBody("mine", version: 1));

        store.Publish("mine");
        Assert.Equal("# v2", store.GetBody("mine", null));
        Assert.Equal("# v1", store.GetBody("mine", version: 1));
    }

    [Fact]
    public void An_archived_skill_leaves_the_register_but_keeps_its_history()
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "mine"));
        store.Publish("mine");

        Assert.True(store.Archive("mine"));

        Assert.DoesNotContain(store.ListPublished(), s => s.Id == "mine");
        Assert.Null(store.GetPublished("mine"));
        Assert.Null(store.GetBody("mine", null));
        Assert.NotNull(store.GetBody("mine", version: 1));
    }

    [Fact]
    public void Ids_are_normalized_and_unknown_ids_read_as_absent()
    {
        var store = new SkillStore(_h.Open());

        Assert.NotNull(store.GetPublished("MOVE-SESSION"));
        Assert.Equal("Move a session", store.GetPublished(" move-session ")!.Name);
        Assert.Null(store.GetPublished("does-not-exist"));
        Assert.Null(store.GetPublished(""));
    }

    [Fact]
    public void An_invalid_id_or_file_name_is_refused()
    {
        var store = new SkillStore(_h.Open());

        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(id: "Not A Slug")));
        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "bad-file",
            files: new List<SkillFileDto> { new() { FileName = "../escape.md", Content = "x" } })));
    }

    // ---- a skill is a DIRECTORY -------------------------------------------------------------------
    // The Agent Skills standard (agentskills.io) says a skill is a directory holding SKILL.md plus
    // "any additional files or directories", and every agent this product supervises reads exactly
    // that. These tests pin the store to the standard rather than to a shape of our own.

    [Fact]
    public void A_skill_holds_files_in_subdirectories()
    {
        // The rule this replaces refused a path separator outright, which made TWO skills in this very
        // repository unstorable: agent-expert keeps nine files under agents/ and playwright-cli keeps
        // seven under references/.
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "deep", files: new List<SkillFileDto>
        {
            new() { FileName = "references/tracing.md", Content = "# Tracing" },
            new() { FileName = "scripts/build.sh", Content = "#!/bin/sh\necho hi\n", Executable = true },
            new() { FileName = "assets/data/table.json", Content = "{}" },
        }));
        var published = store.Publish("deep")!;

        Assert.Equal(3, published.FileCount);
        Assert.Equal("# Tracing", TextOf(store.GetFile("deep", "references/tracing.md", null)));
        Assert.Equal("{}", TextOf(store.GetFile("deep", "assets/data/table.json", null)));

        // The executable bit survives the round trip - a bundled script a skill tells an agent to run
        // is useless without it.
        var script = store.GetFile("deep", "scripts/build.sh", null)!;
        Assert.True(script.Executable);
    }

    [Fact]
    public void A_binary_file_round_trips_byte_for_byte()
    {
        // A skill may carry an image, an archive or a compiled program. Bytes that are not valid UTF-8
        // are the whole point of this test: a text-only store silently mangles them.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0xFE, 0x80, 0x7F };
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "binary", files: new List<SkillFileDto>
        {
            new()
            {
                FileName = "assets/bundle.zip",
                Content = Convert.ToBase64String(bytes),
                Encoding = "base64",
            },
        }));
        store.Publish("binary");

        var served = store.GetFile("binary", "assets/bundle.zip", null)!;
        Assert.Equal(bytes, served.Bytes);
        // Served as bytes, not as text: serving these through a text encoding would corrupt them, and
        // the content type is what tells a caller which it is getting.
        Assert.Equal("application/octet-stream", served.ContentType);
    }

    [Fact]
    public void A_script_or_a_program_is_allowed_but_a_shortcut_is_not()
    {
        // A REVERSAL, recorded on purpose. The old rule allowed exactly four extensions and refused
        // everything else, including run.exe - but shipping command-line programs inside a skill is
        // the point of this feature, so an allow-list was the wrong instrument. What is refused now is
        // only what cannot be legitimate skill content.
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "tools", files: new List<SkillFileDto>
        {
            new() { FileName = "scripts/run.exe", Content = "AAEC", Encoding = "base64" },
            new() { FileName = "scripts/setup.ps1", Content = "Write-Output 'hi'" },
            new() { FileName = "scripts/setup.sh", Content = "echo hi" },
            new() { FileName = "config.yaml", Content = "key: value" },
        }));
        Assert.Equal(4, store.Publish("tools")!.FileCount);

        // A shortcut resolves to a target nobody reviewing the skill can see.
        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "shortcut",
            files: new List<SkillFileDto> { new() { FileName = "open.lnk", Content = "x" } })));
    }

    [Fact]
    public void A_skill_cannot_promote_itself_into_a_plugin()
    {
        // Claude Code documents that a skill folder containing .claude-plugin/plugin.json loads as a
        // PLUGIN, which may then bundle hooks and tool servers. A skill in this library is fetched by
        // every machine in a fleet, so that promotion must never be something a skill can grant itself
        // by including a file.
        var store = new SkillStore(_h.Open());

        var plugin = Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "sneaky",
            files: new List<SkillFileDto>
            {
                new() { FileName = ".claude-plugin/plugin.json", Content = "{}" },
            })));
        Assert.Contains("plugin", plugin.Message);

        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "sneaky-two",
            files: new List<SkillFileDto> { new() { FileName = ".mcp.json", Content = "{}" } })));
    }

    [Theory]
    // Traversal and absolute paths would write outside the skill's own directory.
    [InlineData("../escape.md")]
    [InlineData("references/../../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/x.dll")]
    // One bundle must mean one thing on every platform.
    [InlineData("references\\tracing.md")]
    [InlineData("references//tracing.md")]
    [InlineData("references/")]
    // Windows reserves these names in every directory and with any extension.
    [InlineData("nul")]
    [InlineData("scripts/nul.txt")]
    [InlineData("com1/notes.md")]
    // Deeper than the standard's own advice by a wide margin.
    [InlineData("a/b/c/d/e/f.md")]
    public void A_dangerous_file_path_is_refused(string path)
    {
        var store = new SkillStore(_h.Open());
        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "unsafe",
            files: new List<SkillFileDto> { new() { FileName = path, Content = "x" } })));
    }

    [Theory]
    // The other direction of the same guard: a rule that refuses everything is not a rule, so the
    // paths a real skill actually uses must be PROVED to pass.
    [InlineData("SKILL.md")]
    [InlineData("references/tracing.md")]
    [InlineData("scripts/build.sh")]
    [InlineData("assets/templates/report.html")]
    [InlineData("agents/_template.md")]
    [InlineData(".gitignore")]
    public void A_legitimate_file_path_is_accepted(string path)
    {
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(
            id: "safe",
            files: new List<SkillFileDto> { new() { FileName = path, Content = "x" } }));
        Assert.Equal(1, store.Publish("safe")!.FileCount);
    }

    [Fact]
    public void Base64_that_will_not_decode_is_refused_at_authoring_time()
    {
        // Caught HERE rather than at materialization time, where it would already be a half-written
        // bundle on somebody's machine.
        var store = new SkillStore(_h.Open());
        var ex = Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "bad-base64",
            files: new List<SkillFileDto>
            {
                new() { FileName = "assets/x.png", Content = "not base64!!", Encoding = "base64" },
            })));
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void An_unknown_encoding_is_refused_rather_than_guessed()
    {
        var store = new SkillStore(_h.Open());
        Assert.Throws<SkillValidationException>(() => store.CreateDraft(Content(
            id: "odd-encoding",
            files: new List<SkillFileDto>
            {
                new() { FileName = "notes.md", Content = "x", Encoding = "rot13" },
            })));
    }

    [Fact]
    public void A_file_sent_with_no_encoding_is_text_as_it_always_was()
    {
        // The field was added after the first release. A client that omits it is sending exactly what
        // it always sent, and must keep working.
        var store = new SkillStore(_h.Open());
        store.CreateDraft(Content(id: "legacy", files: new List<SkillFileDto>
        {
            new() { FileName = "helper.py", Content = "print('x')", Encoding = "" },
        }));
        store.Publish("legacy");

        var served = store.GetFile("legacy", "helper.py", null)!;
        Assert.Equal("print('x')", TextOf(served));
        Assert.Equal("text/plain; charset=utf-8", served.ContentType);
    }

    [Fact]
    public void The_standards_frontmatter_survives_a_round_trip()
    {
        // Held rather than invented, so a skill authored in any other tool comes back out identical.
        var store = new SkillStore(_h.Open());
        var content = Content(id: "framed");
        content.License = "Apache-2.0";
        content.Compatibility = "Requires Python 3.14 and uv";
        content.AllowedTools = "Bash(git:*) Read";
        content.Metadata = new Dictionary<string, string> { ["author"] = "example-org", ["version"] = "1.0" };
        store.CreateDraft(content);
        var published = store.Publish("framed")!;

        var detail = store.GetVersionDetail("framed", published.Version)!;
        Assert.Equal("Apache-2.0", detail.License);
        Assert.Equal("Requires Python 3.14 and uv", detail.Compatibility);
        Assert.Equal("Bash(git:*) Read", detail.AllowedTools);
        Assert.Equal("example-org", detail.Metadata["author"]);
        Assert.Equal("1.0", detail.Metadata["version"]);
    }

    [Fact]
    public void Changing_only_a_frontmatter_field_or_the_executable_bit_changes_the_bundle_hash()
    {
        // The hash is what a client compares to know whether what it holds is current. A change it
        // cannot see is a stale copy that believes it is fresh.
        var store = new SkillStore(_h.Open());
        var withFile = Content(id: "hashes", files: new List<SkillFileDto>
        {
            new() { FileName = "scripts/build.sh", Content = "echo hi" },
        });
        var first = store.CreateDraft(withFile).ContentHash;

        var licensed = Content(id: "hashes", files: new List<SkillFileDto>
        {
            new() { FileName = "scripts/build.sh", Content = "echo hi" },
        });
        licensed.License = "Apache-2.0";
        var second = store.UpdateDraft("hashes", licensed, null)!.ContentHash;
        Assert.NotEqual(first, second);

        var executable = Content(id: "hashes", files: new List<SkillFileDto>
        {
            new() { FileName = "scripts/build.sh", Content = "echo hi", Executable = true },
        });
        executable.License = "Apache-2.0";
        var third = store.UpdateDraft("hashes", executable, null)!.ContentHash;
        Assert.NotEqual(second, third);
    }

    /// <summary>The text of a served file, for the many assertions that are about text content.</summary>
    private static string? TextOf(SkillStore.SkillFilePayload? payload) =>
        payload is null ? null : System.Text.Encoding.UTF8.GetString(payload.Bytes);

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
