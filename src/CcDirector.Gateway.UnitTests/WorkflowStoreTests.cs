using System.Runtime.CompilerServices;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowStore"/> and <see cref="BuiltInWorkflowSeeder"/> (Workflows
/// mission, phase 1). Covers: fresh seeding of the shipped built-ins, idempotent re-seeding (a
/// restart mints nothing), the ours/yours upgrade trade (newer shipped content auto-publishes ONLY
/// while the user has not customized the workflow), and the read projections' legacy-shape fields.
///
/// The FIDELITY test is the load-bearing one for the mission extraction: the embedded mission
/// instruction body must equal the body of <c>.claude/skills/mission/SKILL.md</c> modulo exactly the
/// two listed mechanical self-reference edits. It guards the "faithful extraction, not a rewrite"
/// requirement until the skill file is stubbed (phase 6), at which point the embedded copy becomes
/// canonical and this test retires.
/// </summary>
public sealed class WorkflowStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Seeds_the_four_built_ins_in_shipped_order()
    {
        var store = new WorkflowStore(_h.Open());

        var workflows = store.ListPublished();

        Assert.Equal(new[] { "mission", "standalone", "standalone-with-review", "fleet-manager" },
            workflows.Select(w => w.Id).ToArray());
        Assert.All(workflows, w =>
        {
            Assert.True(w.IsBuiltIn);
            Assert.Equal(1, w.Version);
            Assert.False(w.HasDraft);
            Assert.False(string.IsNullOrWhiteSpace(w.ContentHash));
            Assert.NotEmpty(w.Steps);
        });
    }

    [Fact]
    public void Reseeding_on_restart_mints_nothing_new()
    {
        _ = new WorkflowStore(_h.Open());

        // A "restart" is a brand-new database + store over the same file.
        var store = new WorkflowStore(_h.Open());

        var workflows = store.ListPublished();
        Assert.Equal(4, workflows.Count);
        Assert.All(workflows, w => Assert.Equal(1, w.Version));

        using var ctx = _h.Open().CreateContext();
        Assert.Equal(4, ctx.WorkflowVersions.Count());
    }

    [Fact]
    public void GetPublished_is_case_insensitive_and_null_for_unknown()
    {
        var store = new WorkflowStore(_h.Open());

        Assert.NotNull(store.GetPublished("MISSION"));
        Assert.Equal("Mission", store.GetPublished("mission")!.Name);
        Assert.Null(store.GetPublished("does-not-exist"));
        Assert.Null(store.GetPublished(""));
    }

    [Fact]
    public void Every_built_in_version_row_stores_its_instruction_body()
    {
        _ = new WorkflowStore(_h.Open());

        using var ctx = _h.Open().CreateContext();
        foreach (var id in new[] { "mission", "standalone", "standalone-with-review", "fleet-manager" })
        {
            var version = ctx.WorkflowVersions.Single(v => v.WorkflowId == id);
            Assert.Equal(BuiltInWorkflows.InstructionsFor(id), version.InstructionsMarkdown);
            Assert.Equal(WorkflowVersionStatus.Published, version.Status);
        }
    }

    [Fact]
    public void Every_built_in_ships_a_non_empty_instruction_body()
    {
        // The comparison above cannot see an empty resource: the shipped body and the stored row would
        // both be empty, and an empty conduct would be served silently to every seat.
        var ids = BuiltInWorkflows.All().Select(w => w.Id).ToArray();
        Assert.Contains("fleet-manager", ids);

        foreach (var id in ids)
            Assert.False(string.IsNullOrWhiteSpace(BuiltInWorkflows.InstructionsFor(id)),
                $"Built-in workflow '{id}' ships an empty instruction body.");
    }

    [Fact]
    public void Fleet_manager_conduct_learns_of_stops_from_events_and_never_asks_for_reports()
    {
        // The owner ruled (2026-09-16) that the sessions a Fleet Manager starts never report to it: the Gateway's
        // events carry each stop, with the Wingman's reading, and the Fleet Manager acknowledges them by id.
        var body = BuiltInWorkflows.InstructionsFor("fleet-manager");

        Assert.Contains("[Fleet Manager events]", body);
        Assert.Contains("acknowledge", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event ids you have handled", body);
        Assert.DoesNotContain("devthrottle session report", body);
        Assert.DoesNotContain("`session report`", body);
        Assert.DoesNotContain("emporary", body);
    }

    [Fact]
    public void Fleet_manager_conduct_leaves_a_stop_waiting_for_its_reading_alone_and_reads_every_event()
    {
        // Step 4, round 3: a stop still waiting for its reading is neither acted on nor acknowledged, and the digest
        // may say more events remain. The phrases span wrapped lines, and a Windows checkout embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("Leave alone any stop still waiting\n   for the Wingman's reading - do not act on it and do not acknowledge it", body);
        Assert.Contains("A stop still waiting for the Wingman's reading is not yours to act on yet.", body);
        Assert.Contains("If\n   the digest says more remain, read the rest before you act.", body);
    }

    [Fact]
    public void Fleet_manager_conduct_acts_on_an_answered_card_event_and_starts_on_a_marked_event()
    {
        // Steps 5 and 6 fixes. One phrase spans a wrapped line, and a Windows checkout embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("**An `answered` event is the owner pressing a button on one of your cards.**", body);
        Assert.Contains("do not answer the record again. Then acknowledge the event.", body);
        Assert.Contains("**A `marked` event means you have just become the Fleet Manager**", body);
        Assert.Contains("Until it arrives, a session\n  started to take over does nothing", body);
    }

    [Fact]
    public void Fleet_manager_conduct_writes_its_advice_when_it_files_and_reads_the_owners_answers_in_the_digest()
    {
        // Step 7: the walkthrough's advice is written when the record is filed, and an answer given there is recorded on
        // the record rather than typed to the Fleet Manager. Two phrases span a wrapped line, and a Windows checkout
        // embeds the body with CRLF.
        var body = Normalize(BuiltInWorkflows.InstructionsFor("fleet-manager"));

        Assert.Contains("Write it when you FILE the record (`--advice`, and `--pick` for\n    the option you would choose)", body);
        Assert.Contains("replace it with `fleet advise` if the picture changes", body);
        Assert.Contains("the Gateway refuses a line break or more than 300 characters", body);
        Assert.Contains("read what they decided in `fleet digest` (`answered`)", body);
    }

    [Fact]
    public void Uncustomized_built_in_auto_publishes_newer_shipped_content()
    {
        var db = _h.Open();
        _ = new WorkflowStore(db);

        // Simulate a database seeded by an OLDER binary: what is published matches what that binary
        // shipped, but both differ from what THIS binary ships.
        using (var ctx = db.CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            var published = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Status == WorkflowVersionStatus.Published);
            head.ShippedContentHash = "old-shipped-hash";
            published.ContentHash = "old-shipped-hash";
            ctx.SaveChanges();
        }

        var store = new WorkflowStore(_h.Open());

        var mission = store.GetPublished("mission")!;
        Assert.Equal(2, mission.Version);
        using (var ctx = _h.Open().CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            Assert.Equal(2, head.LatestVersion);
            Assert.Equal(2, head.PublishedVersion);
            var superseded = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Version == 1);
            Assert.Equal(WorkflowVersionStatus.Superseded, superseded.Status);
        }
    }

    [Fact]
    public void Previously_customized_built_in_is_republished_to_shipped_with_history_kept()
    {
        // Built-ins are READ-ONLY (Shared Workflow Library phase 3, owner ruling 2026-07-24,
        // reversing the 2026-07-17 editable-with-reset trade): a customization published under the
        // OLD ruling is superseded by shipped content on the next seed - even when the binary's own
        // content did not change - and the edit stays as forever-readable pinned history.
        var db = _h.Open();
        _ = new WorkflowStore(db);

        string customizedHash = "the-users-own-edit";
        using (var ctx = db.CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            var published = ctx.WorkflowVersions.Single(
                v => v.WorkflowId == "mission" && v.Status == WorkflowVersionStatus.Published);
            head.ShippedContentHash = "old-shipped-hash";
            published.ContentHash = customizedHash;
            published.InstructionsMarkdown = "# The user's own conduct";
            ctx.SaveChanges();
        }

        var store = new WorkflowStore(_h.Open());

        var mission = store.GetPublished("mission")!;
        Assert.Equal(2, mission.Version); // shipped content took the head as a NEW version
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), store.GetInstructions("mission", null));
        // The customization is history, not gone: the pinned read still serves it.
        Assert.Equal("# The user's own conduct", store.GetInstructions("mission", version: 1));
        using (var ctx = _h.Open().CreateContext())
        {
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            Assert.Equal(2, head.LatestVersion);
            Assert.Equal(2, head.PublishedVersion);
            Assert.Equal(head.ShippedContentHash,
                ctx.WorkflowVersions.Single(v => v.WorkflowId == "mission" && v.Version == 2).ContentHash);
        }
    }

    // ---- the content-hash invariant ----------------------------------------------------------------
    // The whole ours/yours upgrade trade rides on the bundle hash: "uncustomized" is a hash equality
    // and "this binary ships something different" is a hash inequality. A hash function that returned
    // any CONSTANT would leave every store test above green while silently breaking upgrade detection
    // forever - so the function's two invariants are pinned directly.

    [Fact]
    public void Bundle_hash_is_deterministic_and_sensitive_to_every_content_change()
    {
        var steps = new List<Contracts.WorkflowStepDto>
        {
            new() { Name = "Do", Description = "d", Doer = "Worker", Reviewer = null, Done = "merged" },
        };
        string Hash(string name = "n", string summary = "s", string instructions = "i") =>
            WorkflowContentHash.ForBundle(name, summary, "w", "h", steps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), instructions,
                Array.Empty<(string, string)>());

        // Deterministic: the same bundle always hashes the same.
        Assert.Equal(Hash(), Hash());

        // Sensitive: any changed piece - metadata, instructions, steps, files - changes the hash.
        Assert.NotEqual(Hash(), Hash(name: "other"));
        Assert.NotEqual(Hash(), Hash(summary: "other"));
        Assert.NotEqual(Hash(), Hash(instructions: "other"));
        var reviewedSteps = new List<Contracts.WorkflowStepDto>
        {
            new() { Name = "Do", Description = "d", Doer = "Worker", Reviewer = "Reviewer", Done = "merged" },
        };
        Assert.NotEqual(Hash(),
            WorkflowContentHash.ForBundle("n", "s", "w", "h", reviewedSteps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), "i", Array.Empty<(string, string)>()));
        Assert.NotEqual(Hash(),
            WorkflowContentHash.ForBundle("n", "s", "w", "h", steps,
                Array.Empty<Contracts.WorkflowOutcomeCriterionDto>(), "i",
                new[] { ("helpers.py", WorkflowContentHash.ForFile("print()")) }));
    }

    // ---- the mission-extraction fidelity test ------------------------------------------------------

    /// <summary>
    /// The two mechanical self-reference edits the extraction is allowed (each listed in the plan and
    /// shown in the pull request): the document stops calling itself "this file" where that would now
    /// be a lie, and the brief checklist points at the workflow instead of linking the file.
    /// Everything else must match byte-for-byte (modulo line endings). Each replacement ASSERTS that
    /// its source phrase was actually present - a silent no-op Replace (the phrase drifted in the
    /// skill file) would otherwise let an unedited embedded copy pass.
    /// </summary>
    private static string ApplyListedEdits(string skillBody)
    {
        var edited = ReplaceExactlyOnce(skillBody,
            "THIS FILE HOLDS THE CONDUCT OF A RUN",
            "THIS WORKFLOW HOLDS THE CONDUCT OF A RUN");
        return ReplaceExactlyOnce(edited,
            "5. **A link to this file** for the conduct of a run, and to the DevThrottle Method for the rules.",
            "5. **A pointer to the DevThrottle Method** for the rules, and to this workflow -\n" +
            "   `cc-devthrottle workflow instructions mission` - for how a run is conducted.");
    }

    private static string ReplaceExactlyOnce(string text, string from, string to)
    {
        var first = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(first >= 0,
            $"Expected the skill file to contain the listed-edit source phrase: \"{from}\". " +
            "If the skill file changed, update the listed edits deliberately.");
        Assert.Equal(first, text.LastIndexOf(from, StringComparison.Ordinal));
        return text.Replace(from, to);
    }

    [Fact]
    public void Mission_instructions_are_a_faithful_extraction_of_the_skill_file()
    {
        var skillPath = Path.Combine(RepoRoot(), ".claude", "skills", "mission", "SKILL.md");
        Assert.True(File.Exists(skillPath),
            $"The mission skill file was not found at {skillPath}. If it has been stubbed (phase 6), " +
            "this fidelity test has done its job and should be retired.");

        var skill = Normalize(File.ReadAllText(skillPath));

        // The body is everything below the YAML frontmatter (between the first two "---" lines).
        var frontmatterEnd = skill.IndexOf("\n---\n", skill.IndexOf("---\n", StringComparison.Ordinal) + 4,
            StringComparison.Ordinal);
        Assert.True(frontmatterEnd > 0, "SKILL.md has no YAML frontmatter fence.");
        var body = skill[(frontmatterEnd + "\n---\n".Length)..].TrimStart('\n');

        var expected = ApplyListedEdits(body).TrimEnd('\n');
        var embedded = Normalize(BuiltInWorkflows.InstructionsFor("mission")).TrimEnd('\n');

        Assert.Equal(expected, embedded);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>The repository root, located from this source file's own path - the tests always run
    /// from a checkout, and bin-relative paths would break under different runners.</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.Tests/WorkflowStoreTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
